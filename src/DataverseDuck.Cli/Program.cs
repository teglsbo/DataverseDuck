using DataverseDuck.Configuration;
using DataverseDuck.Diagnostics;
using DataverseDuck.Metadata;
using DataverseDuck.Plans;
using Microsoft.PowerPlatform.Dataverse.Client;

namespace DataverseDuck.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var command = args.FirstOrDefault()?.ToLowerInvariant();

        // Before anything reads configuration. Real environment variables still
        // win, so this cannot override a secret injected by CI.
        EnvFile = DotEnvFile.LoadFromCurrentDirectory();

        return command switch
        {
            "doctor" => await DoctorAsync(args.Skip(1).ToArray()),
            "capture" => Capture(args.Skip(1).ToArray()),
            "query" => Query(args.Skip(1).ToArray()),
            "tables" => Tables(args.Skip(1).ToArray()),
            null or "help" or "--help" or "-h" => Help(),
            _ => Unknown(command),
        };
    }

    /// <summary>The .env that was loaded, if any. Reported when configuration is incomplete.</summary>
    private static string? EnvFile;

    private static int Help()
    {
        Console.WriteLine("""
            dvduck - query Dataverse alongside local JSON with DuckDB

            Usage:
              dvduck doctor [table ...]     Verify the environment is set up for app-registration access.
              dvduck capture <table ...>    Save entity metadata to a snapshot for offline work.
              dvduck query [options]        Cache Dataverse tables, then query them alongside JSON.
              dvduck tables --db <path>     Show what a cache file holds and how old it is.

            Configuration (environment variables):
              DATAVERSE_URL            https://yourorg.crm4.dynamics.com
              DATAVERSE_CLIENT_ID      Application (client) ID of the app registration
              DATAVERSE_CLIENT_SECRET  Client secret *value*
              DATAVERSE_TENANT_ID      Directory (tenant) ID from Entra ID -- not the environment
                                       or organization ID shown in the admin centre.
                                       Optional but recommended.

            Options:
              --out <path>             Snapshot path for 'capture'. Default: metadata/snapshot.bin

            Options for 'query':
              --plan <sql>             A WITH block naming its own sources and the query, in
                                       one statement.
              --plan-file <path>       Read that plan from a file instead.
              --db <path>              DuckDB file. Default: in-memory.
              --format <name>          tsv (default), csv or json. All three escape
                                       their delimiters, so a field containing a tab,
                                       comma or newline cannot corrupt the output.
              --strict                 Fail if any operation would run locally rather than
                                       inside Dataverse. Default: warn but continue.

            Example -- how many contacts had webchat messages:
              dvduck query --plan "
                WITH logs AS JSON ('webchat/*.json'),
                     crm_contact AS DATAVERSE (
                         SELECT contactid, fullname FROM contact
                         WHERE contactid IN {{SELECT CAST(customer_id AS UUID)
                                              FROM logs WHERE channel = 'webchat'}}
                     )
                SELECT count(DISTINCT c.contactid)
                FROM logs l JOIN crm_contact c ON c.contactid = CAST(l.customer_id AS UUID)
                WHERE l.channel = 'webchat'"

              Only the contacts the logs mention are fetched; the other rows of
              the table are never transferred.

            Entries run top to bottom, before the final query. A DATAVERSE entry
            is one round trip that materialises a real table, so its {{ }} can
            read any entry above it -- including another DATAVERSE table, which
            is how you narrow across two hops:

                WITH logs        AS JSON ('webchat/*.json'),
                     crm_account AS DATAVERSE (... {{SELECT ... FROM logs}}),
                     crm_contact AS DATAVERSE (... {{SELECT accountid FROM crm_account}})
                SELECT ...

            Ordinary CTEs may appear in the same WITH; they are left to DuckDB
            and run with the final query, so a {{ }} cannot read them.

            See docs/environment-setup.md for how to obtain these.
            """);
        return 0;
    }

    private static int Unknown(string? command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'. Run 'dvduck help'.");
        return 2;
    }

    /// <summary>
    /// Prints what a cache file holds.
    ///
    /// A .duckdb file is meant to be reused, and a {{ }} table is a subset that
    /// looks exactly like a full copy. This is how you find out which you have
    /// before trusting an answer computed from it.
    /// </summary>
    private static int Tables(string[] args)
    {
        string? database = null;

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--db" && i + 1 < args.Length)
            {
                database = args[++i];
            }
        }

        if (database is null)
        {
            Console.Error.WriteLine("Pass --db <path>. An in-memory cache has nothing to show.");
            return 2;
        }

        if (!File.Exists(database))
        {
            Console.Error.WriteLine($"No such cache file: {database}");
            return 1;
        }

        using var duck = UtcTimestampPolicy.OpenConnection($"Data Source={database}");
        var entries = CacheManifest.Read(duck);

        if (entries.Count == 0)
        {
            Console.Error.WriteLine(
                CacheManifest.Exists(duck)
                    ? "The cache is empty."
                    : "This file has no manifest, so nothing records what its tables are or when they were loaded.");
            return 0;
        }

        foreach (var entry in entries)
        {
            Console.WriteLine(entry.Describe());
            Console.WriteLine($"    {Indent(entry.Source)}");

            if (entry.IsPartial)
            {
                Console.WriteLine(
                    "    Partial: only the rows those keys matched. Do not read it as the whole table.");
            }

            Console.WriteLine();
        }

        return 0;
    }

    /// <summary>Keeps a multi-line source statement inside the indented block.</summary>
    private static string Indent(string source) =>
        string.Join("\n    ", source.Split('\n').Select(line => line.TrimEnd()));

    private static bool TryLoadOptions(out DataverseOptions options)
    {
        if (DataverseOptions.TryLoadFromEnvironment(out var loaded, out var error))
        {
            options = loaded;
            return true;
        }

        Console.Error.WriteLine($"Configuration error: {error}");

        // Which file was read is the first thing you want to know when a value
        // you believe you set is not arriving -- most often the .env is in a
        // different directory, or an exported variable is shadowing it.
        Console.Error.WriteLine(
            EnvFile is null
                ? $"No {DotEnvFile.FileName} was found in this directory or above it."
                : $"Read {EnvFile}. Variables already exported take precedence over it.");

        Console.Error.WriteLine("Run 'dvduck help' for the variables, or see docs/environment-setup.md.");
        options = null!;
        return false;
    }

    private static async Task<int> DoctorAsync(string[] tables)
    {
        if (!TryLoadOptions(out var options))
            return 2;

        var doctor = new EnvironmentDoctor(options) { ExpectedTables = tables };

        Console.WriteLine($"Checking {options.EnvironmentUrl}\n");

        var report = await doctor.RunAsync();

        foreach (var check in report.Checks)
        {
            var marker = check.Status switch
            {
                CheckStatus.Passed => "PASS",
                CheckStatus.Warning => "WARN",
                CheckStatus.Failed => "FAIL",
                _ => "SKIP",
            };

            Console.WriteLine($"[{marker}] {check.Name}");
            Console.WriteLine($"       {check.Detail}");

            if (check.Remedy is not null)
            {
                Console.WriteLine();
                foreach (var line in Wrap(check.Remedy, 84))
                    Console.WriteLine($"       -> {line}");
            }

            Console.WriteLine();
        }

        if (report.FirstFailure is not null)
        {
            Console.Error.WriteLine($"Failed at: {report.FirstFailure.Name}");
            return 1;
        }

        Console.WriteLine("Environment is ready. Next: 'dvduck capture <table ...>' to snapshot metadata.");
        return 0;
    }

    private static int Capture(string[] args)
    {
        if (!CaptureArguments.TryParse(args, out var parsed, out var parseError))
        {
            Console.Error.WriteLine(parseError);
            return 2;
        }

        var tables = parsed!.Tables.ToArray();
        var path = parsed.Path;

        if (!TryLoadOptions(out var options))
            return 2;

        try
        {
            using var client = new ServiceClient(options.ToConnectionString());

            if (!client.IsReady)
            {
                Console.Error.WriteLine($"Connection failed: {client.LastError}");
                Console.Error.WriteLine("Run 'dvduck doctor' for a diagnosis.");
                return 1;
            }

            Console.WriteLine($"Capturing {tables.Length} table(s) from {options.EnvironmentUrl}...");

            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var snapshot = MetadataCapture.CaptureToFile(client, tables, path);

            Console.WriteLine($"Wrote {snapshot.Entities.Count} entities to {path} " +
                              $"({new FileInfo(path).Length / 1024.0:F1} KB).");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Capture failed: {e.Message}");
            Console.Error.WriteLine("Run 'dvduck doctor' for a diagnosis.");
            return 1;
        }
    }

    private static int Query(string[] args)
    {
        List<PlanStep> steps = [];
        string? finalSql = null;
        string? plan = null;
        var database = ":memory:";
        var policy = FoldingPolicy.Warn;
        var format = OutputFormat.Tsv;

        for (var i = 0; i < args.Length; i++)
        {
            var needsValue = args[i] is "--db" or "--plan" or "--plan-file" or "--format";

            if (needsValue && i + 1 >= args.Length)
            {
                Console.Error.WriteLine($"{args[i]} needs a value.");
                return 2;
            }

            switch (args[i])
            {
                case "--cache":
                case "--json":
                case "--run":
                    Console.Error.WriteLine(
                        $"{args[i]} was removed. A query now names its own sources in one --plan:");
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("  dvduck query --plan \"");
                    Console.Error.WriteLine("    WITH logs AS JSON ('webchat/*.json'),");
                    Console.Error.WriteLine("         crm_contact AS DATAVERSE (SELECT ... WHERE id IN {{SELECT ... FROM logs}})");
                    Console.Error.WriteLine("    SELECT ...\"");
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("Entries run top to bottom, so the order is stated rather than implied.");
                    return 2;

                case "--plan":
                    plan = args[++i];
                    break;

                case "--plan-file":
                    var planPath = args[++i];
                    if (!File.Exists(planPath))
                    {
                        Console.Error.WriteLine($"No plan file at '{planPath}'.");
                        return 2;
                    }

                    plan = File.ReadAllText(planPath);
                    break;

                case "--db":
                    database = args[++i];
                    break;

                case "--format":
                    if (!ResultWriter.TryParseFormat(args[++i], out format, out var formatError))
                    {
                        Console.Error.WriteLine(formatError);
                        return 2;
                    }

                    break;

                case "--strict":
                    policy = FoldingPolicy.RejectCritical;
                    break;

                default:
                    Console.Error.WriteLine($"Unknown option '{args[i]}'. Run 'dvduck help'.");
                    return 2;
            }
        }

        if (plan is null)
        {
            Console.Error.WriteLine("Nothing to run. Pass --plan \"WITH ...\" or --plan-file <path>.");
            return 2;
        }

        try
        {
            var parsed = DataversePlanParser.Parse(plan);
            steps.AddRange(parsed.Steps);
            finalSql = parsed.FinalSql;
        }
        catch (FormatException e)
        {
            Console.Error.WriteLine($"Could not read the plan: {e.Message}");
            return 2;
        }

        // Only connect to Dataverse if something actually needs it. Querying a
        // previously-populated --db file alongside JSON should work offline.
        ServiceClient? client = null;
        MarkMpn.Sql4Cds.Engine.Sql4CdsConnection? dataverse = null;

        try
        {
            if (steps.Any(step => step.Kind == PlanStepKind.Dataverse))
            {
                if (!TryLoadOptions(out var options))
                    return 2;

                client = new ServiceClient(options.ToConnectionString());

                if (!client.IsReady)
                {
                    Console.Error.WriteLine($"Connection failed: {client.LastError}");
                    Console.Error.WriteLine("Run 'dvduck doctor' for a diagnosis.");
                    return 1;
                }

                dataverse = Sql4CdsConnectionFactory.Create(client);
            }

            using var duck = UtcTimestampPolicy.OpenConnection($"Data Source={database}");

            IDataverseQuerySource source = dataverse is null
                ? UnavailableQuerySource.Instance
                : new Sql4CdsQuerySource(dataverse);

            var cache = new DataverseCache(duck, source)
            {
                FoldingPolicy = policy,
                Log = message => Console.Error.WriteLine($"  {message}"),

                // Metadata is what distinguishes a birthdate from an instant:
                // the reader reports both as DateTime with Kind=Unspecified.
                Mapper = new DataverseDuck.Schema.DataverseSchemaMapper
                {
                    Metadata = client is null
                        ? null
                        : new MarkMpn.Sql4Cds.Engine.AttributeMetadataCache(client),
                },
            };

            using var cancellation = new CancellationTokenSource();

            // Ctrl-C rolls the load back rather than killing the process
            // mid-append, so the database is never left holding part of a table.
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                Console.Error.WriteLine("Cancelling; the current load will be rolled back.");
                cancellation.Cancel();
            };

            // JSON first: a --cache statement may embed a {{ }} query over these
            // Everything the plan brings in, in written order: a DATAVERSE entry's
            // {{ }} can read any JSON view or table established above it.
            foreach (var step in steps)
            {
                if (step.Kind == PlanStepKind.Json)
                {
                    cache.RegisterJson(step.Name, step.Body);
                    continue;
                }

                Console.Error.WriteLine($"Caching {step.Name}...");
                Console.Error.WriteLine($"  {cache.Cache(step.Body, step.Name, cancellation.Token)}");
            }

            using var reader = cache.Query(finalSql);
            WriteResults(reader, format);
            return 0;
        }
        catch (DataverseThrottledException e)
        {
            Console.Error.WriteLine($"Throttled: {e.Message}");
            Console.Error.WriteLine("Nothing was written; any previously cached copy is unchanged.");
            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled. Nothing was written.");
            return 130;
        }
        catch (PlanNotFoldedException e)
        {
            Console.Error.WriteLine($"Refused to run this query: {e.Message}");
            Console.Error.WriteLine("Rewrite it so Dataverse can do the work, or drop --strict to run it anyway.");
            return 1;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Query failed: {e.Message}");
            return 1;
        }
        finally
        {
            dataverse?.Dispose();
            client?.Dispose();
        }
    }

    /// <summary>
    /// Writes results to stdout. The row count goes to stderr so that
    /// redirecting stdout gives a clean, parseable file.
    /// </summary>
    private static void WriteResults(System.Data.Common.DbDataReader reader, OutputFormat format)
    {
        var rows = ResultWriter.Write(reader, Console.Out, format);

        Console.Error.WriteLine($"\n({rows:N0} row(s))");
    }

    /// <summary>
    /// Used when no Dataverse connection was opened because nothing asked for
    /// one. Failing here means an internal bug, not user error.
    /// </summary>
    private sealed class UnavailableQuerySource : IDataverseQuerySource
    {
        public static readonly UnavailableQuerySource Instance = new();

        public PlanAnalysis? Analyze(string sql) => null;

        public T Query<T>(string sql, Func<System.Data.Common.DbDataReader, T> read) =>
            throw new InvalidOperationException("No Dataverse connection was opened.");
    }

    private static IEnumerable<string> Wrap(string text, int width)
    {
        var line = new System.Text.StringBuilder();

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + word.Length + 1 > width)
            {
                yield return line.ToString();
                line.Clear();
            }

            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }

        if (line.Length > 0)
            yield return line.ToString();
    }
}
