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

        return command switch
        {
            "doctor" => await DoctorAsync(args.Skip(1).ToArray()),
            "capture" => Capture(args.Skip(1).ToArray()),
            "query" => Query(args.Skip(1).ToArray()),
            null or "help" or "--help" or "-h" => Help(),
            _ => Unknown(command),
        };
    }

    private static int Help()
    {
        Console.WriteLine("""
            dvduck - query Dataverse alongside local JSON with DuckDB

            Usage:
              dvduck doctor [table ...]     Verify the environment is set up for app-registration access.
              dvduck capture <table ...>    Save entity metadata to a snapshot for offline work.
              dvduck query [options]        Cache Dataverse tables, then query them alongside JSON.

            Configuration (environment variables):
              DATAVERSE_URL            https://yourorg.crm4.dynamics.com
              DATAVERSE_CLIENT_ID      Application (client) ID of the app registration
              DATAVERSE_CLIENT_SECRET  Client secret *value*
              DATAVERSE_TENANT_ID      Directory (tenant) ID (optional but recommended)

            Options:
              --out <path>             Snapshot path for 'capture'. Default: metadata/snapshot.bin

            Options for 'query':
              --cache <table>=<sql>    Run SQL against Dataverse into a DuckDB table. Repeatable.
              --json <view>=<path>     Expose a JSON file or glob as a view. Repeatable.
              --run <sql>              The query to print results for.
              --db <path>              DuckDB file. Default: in-memory.
              --strict                 Fail if any operation would run locally rather than
                                       inside Dataverse. Default: warn but continue.

            Example:
              dvduck query \
                --cache crm_account="SELECT accountid, name FROM account" \
                --json logs='logs/*.json' \
                --run "SELECT a.name, count(*) FROM logs l
                       JOIN crm_account a ON a.accountid = CAST(l.account_id AS UUID)
                       GROUP BY a.name"

            See docs/environment-setup.md for how to obtain these.
            """);
        return 0;
    }

    private static int Unknown(string? command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'. Run 'dvduck help'.");
        return 2;
    }

    private static bool TryLoadOptions(out DataverseOptions options)
    {
        if (DataverseOptions.TryLoadFromEnvironment(out var loaded, out var error))
        {
            options = loaded;
            return true;
        }

        Console.Error.WriteLine($"Configuration error: {error}");
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
        var outIndex = Array.FindIndex(args, a => a is "--out" or "-o");
        var path = outIndex >= 0 && outIndex + 1 < args.Length
            ? args[outIndex + 1]
            : Path.Combine("metadata", "snapshot.bin");

        var tables = args
            .Where((a, i) => i != outIndex && i != outIndex + 1 && !a.StartsWith('-'))
            .ToArray();

        if (tables.Length == 0)
        {
            Console.Error.WriteLine("Specify at least one table, for example: dvduck capture account contact");
            return 2;
        }

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
        List<(string Table, string Sql)> caches = [];
        List<(string View, string Path)> jsons = [];
        string? finalSql = null;
        var database = ":memory:";
        var policy = FoldingPolicy.Warn;

        for (var i = 0; i < args.Length; i++)
        {
            var needsValue = args[i] is "--cache" or "--json" or "--run" or "--db";

            if (needsValue && i + 1 >= args.Length)
            {
                Console.Error.WriteLine($"{args[i]} needs a value.");
                return 2;
            }

            switch (args[i])
            {
                case "--cache":
                    if (!TrySplitPair(args[++i], out var table, out var sql))
                        return 2;
                    caches.Add((table, sql));
                    break;

                case "--json":
                    if (!TrySplitPair(args[++i], out var view, out var path))
                        return 2;
                    jsons.Add((view, path));
                    break;

                case "--run":
                    finalSql = args[++i];
                    break;

                case "--db":
                    database = args[++i];
                    break;

                case "--strict":
                    policy = FoldingPolicy.RejectCritical;
                    break;

                default:
                    Console.Error.WriteLine($"Unknown option '{args[i]}'. Run 'dvduck help'.");
                    return 2;
            }
        }

        if (finalSql is null)
        {
            Console.Error.WriteLine("Nothing to run. Pass --run \"SELECT ...\".");
            return 2;
        }

        // Only connect to Dataverse if something actually needs it. Querying a
        // previously-populated --db file alongside JSON should work offline.
        ServiceClient? client = null;
        MarkMpn.Sql4Cds.Engine.Sql4CdsConnection? dataverse = null;

        try
        {
            if (caches.Count > 0)
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
            };

            foreach (var (name, statement) in caches)
            {
                Console.Error.WriteLine($"Caching {name}...");
                Console.Error.WriteLine($"  {cache.Cache(statement, name)}");
            }

            foreach (var (name, location) in jsons)
                cache.RegisterJson(name, location);

            using var reader = cache.Query(finalSql);
            WriteTable(reader);
            return 0;
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

    /// <summary>Splits <c>name=value</c>, keeping any '=' inside the value.</summary>
    private static bool TrySplitPair(string argument, out string name, out string value)
    {
        var separator = argument.IndexOf('=');

        if (separator > 0)
        {
            name = argument[..separator];
            value = argument[(separator + 1)..];
            return true;
        }

        Console.Error.WriteLine($"Expected name=value but got '{argument}'.");
        name = value = string.Empty;
        return false;
    }

    /// <summary>
    /// Prints results as tab-separated values: greppable, and pasteable into a
    /// spreadsheet. Timestamps are round-tripped in ISO 8601 so the naive-UTC
    /// convention survives being copied elsewhere.
    /// </summary>
    private static void WriteTable(System.Data.Common.DbDataReader reader)
    {
        Console.WriteLine(string.Join('\t',
            Enumerable.Range(0, reader.FieldCount).Select(reader.GetName)));

        var rows = 0L;

        while (reader.Read())
        {
            Console.WriteLine(string.Join('\t',
                Enumerable.Range(0, reader.FieldCount).Select(i => Format(reader, i))));
            rows++;
        }

        Console.Error.WriteLine($"\n({rows:N0} row(s))");
    }

    private static string Format(System.Data.Common.DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? string.Empty
            : reader.GetValue(ordinal) switch
            {
                DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss"),
                byte[] b => Convert.ToHexString(b),
                var v => v.ToString() ?? string.Empty,
            };

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
