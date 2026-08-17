namespace DataverseDuck.Cli;

/// <summary>
/// The parsed form of <c>dvduck capture &lt;table ...&gt; [--out path]</c>.
///
/// This lives apart from <see cref="Program"/> so it can be tested. It was
/// written after the original inline version silently dropped the first table
/// whenever <c>--out</c> was absent: the filter excluded both the flag's index
/// and the one after it, but the "not found" index is -1, so -1 + 1 selected
/// the first real argument. <c>dvduck capture account contact</c> captured only
/// contact, and reported "1 table(s)" without complaint.
/// </summary>
internal sealed record CaptureArguments
{
    public const string DefaultPath = "metadata/snapshot.bin";

    /// <summary>Entity logical names to capture metadata for.</summary>
    public required IReadOnlyList<string> Tables { get; init; }
    /// <summary>Path to write the snapshot file to.</summary>
    public required string Path { get; init; }

    /// <summary>Parses <paramref name="args"/>, or returns false with a message to print.</summary>
    public static bool TryParse(string[] args, out CaptureArguments? parsed, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);

        parsed = null;
        error = null;

        var path = System.IO.Path.Combine("metadata", "snapshot.bin");
        List<string> tables = [];

        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];

            if (argument is "--out" or "-o")
            {
                if (i + 1 >= args.Length)
                {
                    error = $"{argument} needs a value, for example: --out metadata/snapshot.bin";
                    return false;
                }

                path = args[i + 1];
                i++;
                continue;
            }

            if (argument.StartsWith('-'))
            {
                error = $"Unknown option '{argument}' for 'capture'. Run 'dvduck help'.";
                return false;
            }

            tables.Add(argument);
        }

        if (tables.Count == 0)
        {
            error = "Specify at least one table, for example: dvduck capture account contact";
            return false;
        }

        // Asking for the same table twice would fetch it twice and prove nothing.
        var duplicates = tables
            .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();

        if (duplicates.Length > 0)
        {
            error = $"Table(s) named more than once: {string.Join(", ", duplicates)}.";
            return false;
        }

        parsed = new CaptureArguments { Tables = tables, Path = path };
        return true;
    }
}
