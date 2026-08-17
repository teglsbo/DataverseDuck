using DataverseDuck;
using DataverseDuck.Plans;

namespace DataverseDuck.Cli;

/// <summary>
/// The parsed form of <c>dvduck query [--query|--query-file] ... [--db path]
/// [--snapshot path] [--format fmt] [--bom] [--strict]</c>.
///
/// Extracted from <see cref="Program"/> so the flag grammar can be exercised
/// directly, without booting DuckDB or a Dataverse connection just to check
/// that a flag was parsed correctly. <c>--plan</c>/<c>--plan-file</c> are kept
/// as deprecated aliases for <c>--query</c>/<c>--query-file</c>.
/// </summary>
internal sealed record QueryArguments
{
    public string? Plan { get; init; }
    public string Database { get; init; } = ":memory:";
    public string? SnapshotPath { get; init; }
    public OutputFormat Format { get; init; } = OutputFormat.Tsv;
    public bool ByteOrderMark { get; init; }
    public FoldingPolicy Policy { get; init; } = FoldingPolicy.Warn;

    /// <summary>Deprecation notices to print to stderr, in the order they were triggered.</summary>
    public IReadOnlyList<string> Deprecations { get; init; } = [];

    /// <summary>Parses <paramref name="args"/>, or returns false with a message to print.</summary>
    public static bool TryParse(string[] args, out QueryArguments? parsed, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);

        parsed = null;
        error = null;

        string? plan = null;
        var database = ":memory:";
        string? snapshotPath = null;
        var policy = FoldingPolicy.Warn;
        var format = OutputFormat.Tsv;
        var byteOrderMark = false;
        List<string> deprecations = [];

        for (var i = 0; i < args.Length; i++)
        {
            var needsValue = args[i] is
                "--db" or "--query" or "--query-file" or "--plan" or "--plan-file" or
                "--format" or "--snapshot";

            if (needsValue && i + 1 >= args.Length)
            {
                error = $"{args[i]} needs a value.";
                return false;
            }

            switch (args[i])
            {
                case "--cache":
                case "--json":
                case "--run":
                    error =
                        $"{args[i]} was removed. A query now names its own sources in one --query:\n\n" +
                        "  dvduck query --query \"\n" +
                        "    WITH logs AS JSON ('webchat/*.json'),\n" +
                        "         crm_contact AS DATAVERSE (SELECT ... WHERE id IN {{SELECT ... FROM logs}})\n" +
                        "    SELECT ...\"\n\n" +
                        "Entries run top to bottom, so the order is stated rather than implied.";
                    return false;

                case "--plan":
                    deprecations.Add("--plan is now --query -- it runs the statement, it does not just plan it. Still accepted.");
                    plan = args[++i];
                    break;

                case "--query":
                    plan = args[++i];
                    break;

                case "--plan-file":
                case "--query-file":
                {
                    if (args[i] == "--plan-file")
                        deprecations.Add("--plan-file is now --query-file. Still accepted.");

                    var planPath = args[++i];
                    if (!File.Exists(planPath))
                    {
                        error = $"No query file at '{planPath}'.";
                        return false;
                    }

                    plan = File.ReadAllText(planPath);
                    break;
                }

                case "--db":
                    database = args[++i];
                    break;

                case "--snapshot":
                    snapshotPath = args[++i];
                    break;

                case "--format":
                    if (!ResultWriter.TryParseFormat(args[++i], out format, out var formatError))
                    {
                        error = formatError;
                        return false;
                    }

                    break;

                case "--bom":
                    byteOrderMark = true;
                    break;

                case "--strict":
                    policy = FoldingPolicy.RejectCritical;
                    break;

                default:
                    error = $"Unknown option '{args[i]}'. Run 'dvduck help'.";
                    return false;
            }
        }

        if (byteOrderMark && format == OutputFormat.Json)
        {
            error =
                "--bom cannot be used with --format json: RFC 8259 section 8.1 says implementations " +
                "must not add a byte order mark to JSON, and many parsers reject it.";
            return false;
        }

        if (plan is null)
        {
            error = "Nothing to run. Pass --query \"WITH ...\" or --query-file <path>.";
            return false;
        }

        parsed = new QueryArguments
        {
            Plan = plan,
            Database = database,
            SnapshotPath = snapshotPath,
            Format = format,
            ByteOrderMark = byteOrderMark,
            Policy = policy,
            Deprecations = deprecations,
        };

        return true;
    }
}
