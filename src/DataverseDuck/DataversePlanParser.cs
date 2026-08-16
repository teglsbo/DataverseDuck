using System.Text;

namespace DataverseDuck;

/// <summary>
/// What a <c>WITH</c> entry brings into DuckDB before the final query runs.
/// </summary>
public enum PlanStepKind
{
    /// <summary>A local JSON file or glob, registered as a view.</summary>
    Json,

    /// <summary>A Dataverse query, fetched and materialised as a table.</summary>
    Dataverse,
}

/// <summary>
/// One thing to bring into DuckDB. <see cref="Body"/> is a path or glob for
/// <see cref="PlanStepKind.Json"/>, and Dataverse SQL otherwise.
/// </summary>
public sealed record PlanStep(PlanStepKind Kind, string Name, string Body);

/// <summary>
/// A parsed <c>WITH</c> plan: what to bring in, in the order it was written, and
/// the query to run once it is all there.
/// </summary>
public sealed record DataversePlan(IReadOnlyList<PlanStep> Steps, string FinalSql);

/// <summary>
/// Reads a <c>WITH</c> block that names its own sources:
/// <code>
/// WITH logs        AS JSON ('webchat/*.json'),
///      crm_contact AS DATAVERSE (SELECT ... WHERE contactid IN {{SELECT ... FROM logs}})
/// SELECT ...
/// </code>
/// </summary>
/// <remarks>
/// This is sugar over the ordered steps the CLI already ran as separate flags.
/// DuckDB never sees the JSON or DATAVERSE entries. Each is performed in written
/// order before the final query, so a later <c>{{ }}</c> can read anything above
/// it. Writing the order down is the point: it used to live in the order of
/// command-line flags, where nothing checked it.
///
/// Ordinary CTEs in the same <c>WITH</c> are left alone and handed back to DuckDB
/// with the final query.
///
/// The parser is deliberately shallow. It finds entry boundaries by balancing
/// parentheses while respecting string literals, quoted identifiers and comments,
/// and does not try to understand the SQL inside them.
/// </remarks>
public static class DataversePlanParser
{
    public static DataversePlan Parse(string sql) => Parse(sql, requireFinalQuery: true);

    /// <summary>
    /// Reads a plan. <paramref name="requireFinalQuery"/> is false only for the
    /// REPL, where a WITH block on its own means 'fetch this and let me look at
    /// it', and the query comes as the next statement. For the one-shot command
    /// a plan with no query does nothing observable, so it stays an error there.
    /// </summary>
    public static DataversePlan Parse(string sql, bool requireFinalQuery)
    {
        ArgumentNullException.ThrowIfNull(sql);

        var position = 0;
        SkipTrivia(sql, ref position);

        if (!ReadKeyword(sql, ref position, "WITH"))
        {
            // No WITH at all: there is nothing to desugar.
            return new DataversePlan([], sql);
        }

        var afterWith = position;
        SkipTrivia(sql, ref afterWith);
        if (ReadKeyword(sql, ref afterWith, "RECURSIVE"))
        {
            throw new FormatException(
                "WITH RECURSIVE is not supported here. A recursive CTE cannot describe a " +
                "sequence of fetches; write them as separate entries.");
        }

        var steps = new List<PlanStep>();
        var plainCtes = new List<string>();

        while (true)
        {
            SkipTrivia(sql, ref position);

            var nameAt = position;
            var name = ReadName(sql, ref position);
            SkipTrivia(sql, ref position);

            if (!ReadKeyword(sql, ref position, "AS"))
            {
                throw PlanSource.Error(sql, position, $"Expected 'AS' after '{name}' in the WITH list");
            }

            SkipTrivia(sql, ref position);

            var kind =
                ReadKeyword(sql, ref position, "DATAVERSE") ? PlanStepKind.Dataverse :
                ReadKeyword(sql, ref position, "JSON") ? PlanStepKind.Json :
                (PlanStepKind?)null;

            SkipTrivia(sql, ref position);

            // DuckDB allows MATERIALIZED / NOT MATERIALIZED here; keep it with the
            // CTE text so DuckDB still sees it.
            var hintStart = position;
            ReadKeyword(sql, ref position, "NOT");
            SkipTrivia(sql, ref position);
            ReadKeyword(sql, ref position, "MATERIALIZED");
            var hint = sql[hintStart..position].Trim();
            SkipTrivia(sql, ref position);

            if (position >= sql.Length || sql[position] != '(')
            {
                throw PlanSource.Error(sql, position, $"Expected '(' after '{name} AS'");
            }

            var body = ReadBalanced(sql, ref position);

            if (kind is { } stepKind)
            {
                if (hint.Length > 0)
                {
                    throw PlanSource.Error(sql, nameAt,
                        $"'{name}' is a {stepKind.ToString().ToUpperInvariant()} entry, so MATERIALIZED " +
                        "does not apply -- it is always materialised before the query runs");
                }

                var content = stepKind == PlanStepKind.Json
                    ? ReadSinglePath(name, body)
                    : body.Trim();

                steps.Add(new PlanStep(stepKind, name, content));
            }
            else
            {
                plainCtes.Add($"{name} AS {hint}{(hint.Length > 0 ? " " : "")}({body})");
            }

            SkipTrivia(sql, ref position);
            if (position < sql.Length && sql[position] == ',')
            {
                position++;
                continue;
            }

            break;
        }

        if (steps.Count == 0)
        {
            throw new FormatException(
                "This WITH block has no JSON or DATAVERSE entries, so there is nothing to bring in. " +
                "Run it as an ordinary query instead.");
        }

        RejectDuplicates(steps);
        RejectForwardReferences(steps, plainCtes);

        var tail = sql[position..].Trim();
        if (tail.Length == 0)
        {
            if (requireFinalQuery)
                throw new FormatException("The WITH block is not followed by a query.");

            // Plain CTEs cannot outlive the statement that declared them, so
            // there is nowhere to put them. Saying so beats caching the
            // Dataverse entries and quietly dropping the rest.
            if (plainCtes.Count > 0)
            {
                throw new FormatException(
                    "A WITH block with no query can only bring in JSON and DATAVERSE entries. " +
                    "An ordinary CTE would have nothing to be part of.");
            }

            return new DataversePlan(steps, string.Empty);
        }

        var finalSql = plainCtes.Count > 0
            ? $"WITH {string.Join(",\n     ", plainCtes)}\n{tail}"
            : tail;

        return new DataversePlan(steps, finalSql);
    }

    /// <summary>
    /// A JSON entry names one file or glob, as a single quoted string.
    /// </summary>
    private static string ReadSinglePath(string name, string body)
    {
        var trimmed = body.Trim();

        if (trimmed.Length < 2 || trimmed[0] != '\'' || trimmed[^1] != '\'')
        {
            throw new FormatException(
                $"'{name}' is a JSON entry, so it takes one quoted path or glob, " +
                $"as in {name} AS JSON ('logs/*.json').");
        }

        var inner = trimmed[1..^1];

        // A doubled quote inside is an escape; anything else means more than one literal.
        var unescaped = new StringBuilder();
        for (var i = 0; i < inner.Length; i++)
        {
            if (inner[i] != '\'')
            {
                unescaped.Append(inner[i]);
                continue;
            }

            if (i + 1 < inner.Length && inner[i + 1] == '\'')
            {
                unescaped.Append('\'');
                i++;
                continue;
            }

            throw new FormatException(
                $"'{name}' is a JSON entry, so it takes exactly one quoted path or glob.");
        }

        if (unescaped.Length == 0)
        {
            throw new FormatException($"'{name}' is a JSON entry with an empty path.");
        }

        return unescaped.ToString();
    }

    private static void RejectDuplicates(List<PlanStep> steps)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in steps)
        {
            if (!seen.Add(step.Name))
            {
                throw new FormatException(
                    $"'{step.Name}' is defined twice. The second would replace the first, " +
                    "which is unlikely to be what was meant.");
            }
        }
    }

    /// <summary>
    /// Entries run before DuckDB sees the final query, so a <c>{{ }}</c> cannot
    /// read an ordinary CTE, nor an entry below it. Both would otherwise surface
    /// as a confusing "table not found".
    /// </summary>
    private static void RejectForwardReferences(List<PlanStep> steps, List<string> plainCtes)
    {
        var plainNames = plainCtes
            .Select(cte => cte[..cte.IndexOf(" AS ", StringComparison.OrdinalIgnoreCase)].Trim())
            .ToList();

        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].Kind != PlanStepKind.Dataverse)
            {
                continue;
            }

            var keyQuery = KeySetPushdown.FindKeyQuery(steps[i].Body);
            if (keyQuery is null)
            {
                continue;
            }

            foreach (var plain in plainNames)
            {
                if (MentionsWord(keyQuery.Sql, plain))
                {
                    throw new FormatException(
                        $"'{steps[i].Name}' reads '{plain}' inside {{{{ }}}}, but '{plain}' is an " +
                        "ordinary CTE that only exists once the final query runs. Make it a " +
                        "JSON or DATAVERSE entry instead.");
                }
            }

            for (var later = i; later < steps.Count; later++)
            {
                if (MentionsWord(keyQuery.Sql, steps[later].Name))
                {
                    var problem = later == i
                        ? $"'{steps[i].Name}' reads itself inside {{{{ }}}}."
                        : $"'{steps[i].Name}' reads '{steps[later].Name}' inside {{{{ }}}}, " +
                          $"but '{steps[later].Name}' comes later.";

                    throw new FormatException(
                        problem + " Entries run top to bottom, so one can only read entries above it.");
                }
            }
        }
    }

    private static bool MentionsWord(string text, string word)
    {
        var index = text.IndexOf(word, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var before = index == 0 || !IsNameChar(text[index - 1]);
            var afterIndex = index + word.Length;
            var after = afterIndex >= text.Length || !IsNameChar(text[afterIndex]);
            if (before && after)
            {
                return true;
            }

            index = text.IndexOf(word, index + 1, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static string ReadName(string sql, ref int position)
    {
        if (position < sql.Length && sql[position] == '"')
        {
            var closing = sql.IndexOf('"', position + 1);
            if (closing < 0)
            {
                throw PlanSource.Error(sql, position, "Unterminated quoted name in the WITH list");
            }

            var quoted = sql[(position + 1)..closing];
            position = closing + 1;
            return quoted;
        }

        var start = position;
        while (position < sql.Length && IsNameChar(sql[position]))
        {
            position++;
        }

        if (position == start)
        {
            throw PlanSource.Error(sql, start, "Expected a name in the WITH list");
        }

        return sql[start..position];
    }

    private static bool ReadKeyword(string sql, ref int position, string keyword)
    {
        if (position + keyword.Length > sql.Length)
        {
            return false;
        }

        if (string.Compare(sql, position, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) != 0)
        {
            return false;
        }

        var after = position + keyword.Length;
        if (after < sql.Length && IsNameChar(sql[after]))
        {
            return false;
        }

        position = after;
        return true;
    }

    /// <summary>
    /// Consumes a parenthesised body and returns its contents without the outer
    /// parentheses. String literals, quoted identifiers and comments are skipped
    /// so that a stray bracket inside them cannot unbalance the scan.
    /// </summary>
    private static string ReadBalanced(string sql, ref int position)
    {
        var depth = 0;
        var open = position;
        var body = new StringBuilder();

        while (position < sql.Length)
        {
            var c = sql[position];

            if (c == '\'' || c == '"')
            {
                body.Append(ReadQuoted(sql, ref position));
                continue;
            }

            if (IsCommentStart(sql, position))
            {
                var before = position;
                SkipTrivia(sql, ref position);
                body.Append(sql[before..position]);
                continue;
            }

            position++;

            if (c == '(')
            {
                depth++;
                if (depth == 1)
                {
                    continue;
                }
            }
            else if (c == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return body.ToString();
                }
            }

            body.Append(c);
        }

        throw PlanSource.Error(sql, open, "Unbalanced parentheses in the WITH list");
    }

    private static string ReadQuoted(string sql, ref int position)
    {
        var quote = sql[position];
        var start = position;
        position++;

        while (position < sql.Length)
        {
            if (sql[position] == quote)
            {
                // A doubled quote is an escape, not the end.
                if (position + 1 < sql.Length && sql[position + 1] == quote)
                {
                    position += 2;
                    continue;
                }

                position++;
                return sql[start..position];
            }

            position++;
        }

        // Quotes pair left to right, so an earlier stray quote shifts every
        // pairing after it and the one that fails to close is rarely the one
        // that was mistyped. Say so, rather than pointing confidently at a
        // literal the reader can see is fine.
        var spansLines = sql.AsSpan(start).Contains('\n');

        throw PlanSource.Error(sql, start,
            "Unterminated string literal in the WITH list" +
            (spansLines ? "; it runs to the end of the plan, so an earlier quote is probably unclosed" : string.Empty));
    }

    private static bool IsCommentStart(string sql, int position) =>
        position + 1 < sql.Length &&
        ((sql[position] == '-' && sql[position + 1] == '-') ||
         (sql[position] == '/' && sql[position + 1] == '*'));

    private static void SkipTrivia(string sql, ref int position)
    {
        while (position < sql.Length)
        {
            if (char.IsWhiteSpace(sql[position]))
            {
                position++;
                continue;
            }

            if (position + 1 < sql.Length && sql[position] == '-' && sql[position + 1] == '-')
            {
                var newline = sql.IndexOf('\n', position);
                position = newline < 0 ? sql.Length : newline + 1;
                continue;
            }

            if (position + 1 < sql.Length && sql[position] == '/' && sql[position + 1] == '*')
            {
                var close = sql.IndexOf("*/", position + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    throw PlanSource.Error(sql, position, "Unterminated block comment in the WITH list");
                }

                position = close + 2;
                continue;
            }

            return;
        }
    }
}

/// <summary>
/// Points a parse error at the text that caused it.
///
/// The structural failures -- an unclosed paren, an unterminated literal --
/// are only detected at end of input, which is nowhere near the mistake. On a
/// one-line plan the message alone is enough; on a thirty-line one it is not,
/// and the offending character is exactly what the reader needs to see. So the
/// offset reported is where the broken construct <em>opened</em>, not where
/// the parser gave up.
/// </summary>
internal static class PlanSource
{
    /// <summary>Builds an exception carrying the position and an excerpt with a caret.</summary>
    public static FormatException Error(string sql, int offset, string message)
    {
        var (line, column) = LineAndColumn(sql, offset);
        return new FormatException($"{message} (line {line}, column {column})\n\n{Excerpt(sql, offset)}");
    }

    private static (int Line, int Column) LineAndColumn(string sql, int offset)
    {
        offset = Math.Clamp(offset, 0, Math.Max(sql.Length - 1, 0));

        var line = 1;
        var lineStart = 0;

        for (var i = 0; i < offset && i < sql.Length; i++)
        {
            if (sql[i] != '\n')
            {
                continue;
            }

            line++;
            lineStart = i + 1;
        }

        return (line, offset - lineStart + 1);
    }

    /// <summary>The offending line, with a caret under the offending character.</summary>
    private static string Excerpt(string sql, int offset)
    {
        if (sql.Length == 0)
        {
            return string.Empty;
        }

        offset = Math.Clamp(offset, 0, sql.Length - 1);

        var start = sql.LastIndexOf('\n', Math.Max(offset - 1, 0)) + 1;
        if (offset == 0)
        {
            start = 0;
        }

        var end = sql.IndexOf('\n', offset);
        if (end < 0)
        {
            end = sql.Length;
        }

        var (line, column) = LineAndColumn(sql, offset);
        var gutter = $"  {line} | ";
        var text = sql[start..end].TrimEnd('\r');

        // Tabs would put the caret in the wrong place, so render them as one space.
        text = text.Replace('\t', ' ');

        return $"{gutter}{text}\n{new string(' ', gutter.Length + column - 1)}^";
    }
}
