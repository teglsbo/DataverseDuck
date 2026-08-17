using DataverseDuck.Plans;

namespace DataverseDuck.Cli;

/// <summary>
/// The parsed form of <c>dvduck repl [--db path] [--snapshot path] [--strict]</c>.
///
/// Extracted from <see cref="Program"/> so the flag grammar can be exercised
/// directly, matching the pattern used for <see cref="CaptureArguments"/> and
/// <see cref="QueryArguments"/>.
/// </summary>
internal sealed record ReplArguments
{
    /// <summary>Path to the DuckDB file to open, or <c>:memory:</c> for an in-process database.</summary>
    public string Database { get; init; } = ":memory:";
    /// <summary>
    /// Optional path to a metadata snapshot. When supplied, queries compile
    /// from the snapshot rather than a live connection.
    /// </summary>
    public string? SnapshotPath { get; init; }
    /// <summary>What to do when a query would not run entirely inside Dataverse.</summary>
    public FoldingPolicy Policy { get; init; } = FoldingPolicy.Warn;

    /// <summary>Parses <paramref name="args"/>, or returns false with a message to print.</summary>
    public static bool TryParse(string[] args, out ReplArguments? parsed, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);

        parsed = null;
        error = null;

        var database = ":memory:";
        var policy = FoldingPolicy.Warn;
        string? snapshotPath = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--db":
                    if (++i >= args.Length)
                    {
                        error = "--db needs a path.";
                        return false;
                    }

                    database = args[i];
                    break;

                case "--snapshot":
                    if (++i >= args.Length)
                    {
                        error = "--snapshot needs a path.";
                        return false;
                    }

                    snapshotPath = args[i];
                    break;

                case "--strict":
                    policy = FoldingPolicy.RejectCritical;
                    break;

                default:
                    error = $"Unknown option '{args[i]}'. Run 'dvduck help'.";
                    return false;
            }
        }

        parsed = new ReplArguments { Database = database, SnapshotPath = snapshotPath, Policy = policy };
        return true;
    }
}
