using System.Text;
using PrettyPrompt;
using PrettyPrompt.Completion;
using PrettyPrompt.Consoles;
using PrettyPrompt.Documents;
using PrettyPrompt.Highlighting;

namespace DataverseDuck.Cli;

/// <summary>
/// Reads statements from the terminal and hands them to a
/// <see cref="ReplSession"/>.
///
/// Two readers, because PrettyPrompt needs a real terminal. When input is
/// redirected -- a piped script, a here-document, CI -- it falls back to
/// reading lines. That is not only a defensive measure: piping a script into
/// the REPL is a reasonable thing to want, and it is the only way this can be
/// tested without a terminal.
/// </summary>
internal sealed class ReplLoop(ReplSession session)
{
    /// <summary>
    /// Completed alongside table and column names. Kept short on purpose: a
    /// list of every SQL keyword buries the names you actually came for.
    /// </summary>
    private static readonly string[] Keywords =
    [
        "SELECT", "FROM", "WHERE", "GROUP BY", "ORDER BY", "HAVING", "LIMIT",
        "JOIN", "LEFT JOIN", "INNER JOIN", "ON", "AS", "WITH", "DATAVERSE", "JSON",
        "COUNT", "SUM", "AVG", "MIN", "MAX", "DISTINCT", "CAST", "NULL", "AND", "OR", "NOT",
    ];

    private static readonly string[] MetaCommands =
        [".tables", ".schema", ".format", ".limit", ".help", ".quit", ".exit"];

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        return HasUsableTerminal()
            ? await InteractiveAsync(cancellationToken)
            : Piped(cancellationToken);
    }

    /// <summary>
    /// PrettyPrompt draws into the window, so it needs one with a size. A
    /// terminal that reports zero (some CI runners, some embedded shells, a
    /// pty opened without a size) makes it throw an OverflowException deep in
    /// its renderer, which reads as a dvduck crash. Reading plain lines is a
    /// duller experience but it works everywhere, so prefer it when in doubt.
    /// </summary>
    private static bool HasUsableTerminal()
    {
        if (Console.IsInputRedirected)
            return false;

        try
        {
            return Console.WindowWidth > 0 && Console.WindowHeight > 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Read lines, accumulate until a ';' or a meta command, and run them.
    /// Behaves like a script interpreter when input is piped. A person can end
    /// up here too, if their terminal cannot report a size, so print a prompt
    /// when someone is actually typing — otherwise the session looks hung.
    /// </summary>
    private int Piped(CancellationToken cancellationToken)
    {
        var buffer = new StringBuilder();
        var typing = !Console.IsInputRedirected;

        while (Prompt(typing, buffer.Length > 0) is { } line)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            if (!Accumulate(buffer, line, out var statement))
                continue;

            if (!session.Execute(statement, cancellationToken))
                return 0;
        }

        // A trailing statement with no final semicolon is still a statement.
        if (buffer.ToString().Trim() is { Length: > 0 } last)
            session.Execute(last, cancellationToken);

        return 0;
    }

    /// <summary>
    /// Writes the prompt only when a person is typing, then reads a line.
    /// </summary>
    private static string? Prompt(bool typing, bool continuing)
    {
        if (typing)
            Console.Write(continuing ? "    ...> " : "dvduck> ");

        return Console.ReadLine();
    }

    /// <summary>
    /// Adds a line to the buffer and reports whether that completes a
    /// statement. Meta commands complete on their own line, since requiring
    /// '.tables;' would be a needless surprise.
    /// </summary>
    internal static bool Accumulate(StringBuilder buffer, string line, out string statement)
    {
        statement = string.Empty;

        if (buffer.Length == 0 && line.TrimStart().StartsWith('.'))
        {
            statement = line.Trim();
            return statement.Length > 0;
        }

        buffer.AppendLine(line);

        if (!line.TrimEnd().EndsWith(';'))
            return false;

        statement = buffer.ToString();
        buffer.Clear();
        return true;
    }

    private async Task<int> InteractiveAsync(CancellationToken cancellationToken)
    {
        await using var prompt = new Prompt(
            persistentHistoryFilepath: HistoryFile(),
            callbacks: new Completions(session),
            configuration: new PromptConfiguration(
                prompt: new FormattedString("dvduck> ", new FormatSpan(0, 7, AnsiColor.Green))));

        var buffer = new StringBuilder();

        while (!cancellationToken.IsCancellationRequested)
        {
            var response = await prompt.ReadLineAsync();

            // Ctrl-C on an empty line, or Ctrl-D.
            if (!response.IsSuccess)
                break;

            if (!Accumulate(buffer, response.Text, out var statement))
                continue;

            if (!session.Execute(statement, cancellationToken))
                break;
        }

        return 0;
    }

    /// <summary>
    /// History outlives the session, which is most of the value of having a
    /// REPL at all. Under the user's config directory rather than the working
    /// directory, so it is not accidentally committed.
    /// </summary>
    private static string HistoryFile()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dvduck");

        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "history.txt");
    }

    internal sealed class Completions(ReplSession session) : PromptCallbacks
    {
        /// <summary>
        /// The default span stops at the '.', so typing '.ta' asks us to
        /// complete 'ta' and the dot is invisible to us. Meta commands would
        /// then never be offered, and the table names that do match 'ta' would
        /// be committed straight after the dot -- '.contact'. Take the dot into
        /// the span when it opens the statement.
        ///
        /// Only when it opens the statement: in 'a.b' it is a qualifier, and in
        /// 'SELECT .5' it is a number.
        /// </summary>
        protected override async Task<TextSpan> GetSpanToReplaceByCompletionAsync(
            string text,
            int caret,
            CancellationToken cancellationToken)
        {
            var span = await base.GetSpanToReplaceByCompletionAsync(text, caret, cancellationToken);

            if (span.Start > 0 && text[span.Start - 1] == '.' && IsBlank(text, span.Start - 1))
                return new TextSpan(span.Start - 1, span.Length + 1);

            return span;
        }

        private static bool IsBlank(string text, int end)
        {
            for (var i = 0; i < end; i++)
            {
                if (!char.IsWhiteSpace(text[i]))
                    return false;
            }

            return true;
        }

        protected override Task<IReadOnlyList<CompletionItem>> GetCompletionItemsAsync(
            string text,
            int caret,
            TextSpan spanToBeReplaced,
            CancellationToken cancellationToken)
        {
            var typed = text.AsSpan(spanToBeReplaced.Start, spanToBeReplaced.Length).ToString();

            IEnumerable<(string Text, string Kind)> candidates =
                typed.StartsWith('.')
                    ? MetaCommands.Select(name => (name, "command"))
                    : [
                        .. session.LocalTables().Select(name => (name, "cached table")),
                        .. session.LocalColumns().Select(name => (name, "column")),
                        .. session.DataverseNames().Select(name => (name, "Dataverse")),
                        .. Keywords.Select(name => (name, "keyword")),
                    ];

            var items = candidates
                .Where(candidate => candidate.Text.Contains(typed, StringComparison.OrdinalIgnoreCase))
                // A substring match is worth offering -- 'name' should find
                // 'fullname' -- but it must not outrank the thing whose name
                // you were actually typing, which Take() would otherwise cut.
                .OrderBy(candidate =>
                    candidate.Text.StartsWith(typed, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .Take(100)
                .Select(candidate => new CompletionItem(
                    replacementText: candidate.Text,
                    displayText: candidate.Text,
                    filterText: candidate.Text,
                    getExtendedDescription: _ =>
                        Task.FromResult(new FormattedString(candidate.Kind))))
                .ToList();

            return Task.FromResult<IReadOnlyList<CompletionItem>>(items);
        }

        /// <summary>
        /// Opening on every keystroke makes typing feel like wading. Only open
        /// once there is something to filter on.
        /// </summary>
        protected override Task<bool> ShouldOpenCompletionWindowAsync(
            string text,
            int caret,
            KeyPress keyPress,
            CancellationToken cancellationToken)
        {
            if (caret == 0)
                return Task.FromResult(false);

            var previous = text[caret - 1];
            return Task.FromResult(char.IsLetter(previous) || previous is '.' or '_');
        }
    }
}
