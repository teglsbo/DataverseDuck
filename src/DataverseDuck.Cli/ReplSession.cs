using System.Data.Common;
using DataverseDuck.Configuration;
using DataverseDuck.Metadata;
using DataverseDuck.Plans;
using DuckDB.NET.Data;
using MarkMpn.Sql4Cds.Engine;
using Microsoft.PowerPlatform.Dataverse.Client;

namespace DataverseDuck.Cli;

/// <summary>How the REPL prints a result set.</summary>
internal enum ReplFormat
{
    /// <summary>Aligned columns, for reading. Not for piping anywhere.</summary>
    Table,
    Tsv,
    Csv,
    Json,
}

/// <summary>
/// An interactive session holding one DuckDB connection open.
///
/// The one-shot 'query' command re-fetches from Dataverse every time, which
/// is the expensive half of the work. Keeping the connection alive lets a
/// table be pulled once and then queried repeatedly for nothing, which is
/// what exploring actually looks like.
///
/// Dataverse is contacted only when a plan first asks for it, so a session
/// over an existing cache file needs neither network nor credentials.
/// </summary>
internal sealed class ReplSession : IDisposable
{
    private readonly DuckDBConnection _duck;
    private readonly Func<DataverseOptions?> _options;
    private readonly FoldingPolicy _policy;
    private readonly MetadataSnapshot? _snapshot;

    private ServiceClient? _client;
    private DataverseCache? _cache;
    private ReplFormat _format = ReplFormat.Table;

    /// <summary>Rows printed before output is truncated. Zero means no limit.</summary>
    private int _limit = 50;

    public ReplSession(
        DuckDBConnection duck,
        Func<DataverseOptions?> options,
        FoldingPolicy policy,
        MetadataSnapshot? snapshot)
    {
        _duck = duck;
        _options = options;
        _policy = policy;
        _snapshot = snapshot;
    }

    public void Dispose()
    {
        _client?.Dispose();
        _duck.Dispose();
    }

    /// <summary>
    /// Everything DuckDB currently holds. Also the completion source for table
    /// names, which is why it is re-read rather than cached: a plan run a
    /// moment ago should be completable now.
    ///
    /// The cache manifest is ours, not the user's, and listing it invites
    /// questions about a table nobody asked for. 'dvduck tables' is where it
    /// belongs.
    /// </summary>
    public IReadOnlyList<string> LocalTables() =>
        Names("SELECT table_name FROM information_schema.tables " +
              "WHERE table_schema = 'main' AND table_name <> '" + CacheManifest.TableName + "' " +
              "ORDER BY table_name");

    public IReadOnlyList<string> LocalColumns() =>
        Names("SELECT DISTINCT column_name FROM information_schema.columns " +
              "WHERE table_schema = 'main' ORDER BY column_name");

    private IReadOnlyList<string> Names(string sql)
    {
        var names = new List<string>();

        try
        {
            using var command = _duck.CreateCommand();
            command.CommandText = sql;

            using var reader = command.ExecuteReader();
            while (reader.Read())
                names.Add(reader.GetString(0));
        }
        catch (Exception)
        {
            // Completion must never be why a session falls over.
        }

        return names;
    }

    /// <summary>
    /// Dataverse logical names from the snapshot. This is what makes
    /// completion useful before anything has been fetched, and it works with
    /// no connection at all. Empty when no snapshot was loaded.
    /// </summary>
    public IReadOnlyList<string> DataverseNames()
    {
        if (_snapshot is null)
            return [];

        var names = new List<string>();

        foreach (var entity in _snapshot.Entities)
        {
            if (entity.LogicalName is { Length: > 0 } logical)
                names.Add(logical);

            foreach (var attribute in entity.Attributes ?? [])
            {
                if (attribute.LogicalName is { Length: > 0 } name)
                    names.Add(name);
            }
        }

        return [.. names.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Runs one submitted statement. Returns false when the session should
    /// end. Never throws for an ordinary mistake: a bad statement should cost
    /// you the statement, not the session and everything cached in it.
    /// </summary>
    public bool Execute(string input, CancellationToken cancellationToken)
    {
        var statement = input.Trim().TrimEnd(';').Trim();

        if (statement.Length == 0)
            return true;

        if (statement.StartsWith('.'))
            return Meta(statement);

        try
        {
            if (LooksLikeAPlan(statement))
                RunPlan(statement, cancellationToken);
            else
                Show(Local().Query(statement));
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
        }
        catch (DataverseThrottledException e)
        {
            Console.Error.WriteLine($"Throttled: {e.Message}");
        }
        catch (FormatException e)
        {
            Console.Error.WriteLine($"Could not read the plan: {e.Message}");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Error: {FirstLine(e.Message)}");
        }

        return true;
    }

    /// <summary>
    /// A plan is a WITH block whose bindings say JSON or DATAVERSE. Plain SQL
    /// common table expressions have to keep working, so the marker is the
    /// binding keyword rather than the WITH itself.
    ///
    /// Getting this wrong in the safe direction costs only an error message:
    /// a plan sent to DuckDB fails to parse, and it says so.
    /// </summary>
    internal static bool LooksLikeAPlan(string statement)
    {
        if (!statement.TrimStart().StartsWith("WITH", StringComparison.OrdinalIgnoreCase))
            return false;

        return Mentions(statement, "DATAVERSE") || Mentions(statement, "JSON");
    }

    /// <summary>
    /// True when the word appears after an 'AS', which is where a binding
    /// keyword lives. Looking for the bare word would match a column called
    /// 'json' or a table called 'dataverse_log'.
    /// </summary>
    private static bool Mentions(string statement, string keyword)
    {
        var index = 0;

        while ((index = statement.IndexOf("AS", index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var before = index == 0 || !char.IsLetterOrDigit(statement[index - 1]);
            var after = index + 2 >= statement.Length || !char.IsLetterOrDigit(statement[index + 2]);
            index += 2;

            if (!before || !after)
                continue;

            var rest = statement.AsSpan(index).TrimStart();

            if (rest.StartsWith(keyword, StringComparison.OrdinalIgnoreCase)
                && (rest.Length == keyword.Length || !char.IsLetterOrDigit(rest[keyword.Length])))
                return true;
        }

        return false;
    }

    private void RunPlan(string statement, CancellationToken cancellationToken)
    {
        var plan = DataversePlanParser.Parse(statement, requireFinalQuery: false);

        var needsDataverse = plan.Steps.Any(step => step.Kind == PlanStepKind.Dataverse);
        var cache = needsDataverse ? Connect() : Local();

        foreach (var step in plan.Steps)
        {
            if (step.Kind == PlanStepKind.Json)
            {
                cache.RegisterJson(step.Name, step.Body);
                Console.Error.WriteLine($"Registered {step.Name}.");
                continue;
            }

            Console.Error.WriteLine($"Caching {step.Name}...");
            Console.Error.WriteLine($"  {cache.Cache(step.Body, step.Name, cancellationToken)}");
        }

        if (plan.FinalSql.Trim().Length > 0)
            Show(cache.Query(plan.FinalSql));
    }

    private bool Meta(string statement)
    {
        var parts = statement.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var command = parts[0].ToLowerInvariant();
        var argument = parts.Length > 1 ? parts[1] : null;

        switch (command)
        {
            case ".quit":
            case ".exit":
                return false;

            case ".help":
                PrintHelp();
                return true;

            case ".tables":
                var tables = LocalTables();

                if (tables.Count == 0)
                    Console.Error.WriteLine("Nothing cached yet. A WITH ... AS DATAVERSE block fetches something.");

                foreach (var table in tables)
                    Console.WriteLine(table);

                return true;

            case ".schema":
                Schema(argument);
                return true;

            case ".format":
                Format(argument);
                return true;

            case ".limit":
                if (argument is null)
                    Console.WriteLine(_limit == 0 ? "No row limit." : $"Showing at most {_limit} rows.");
                else if (int.TryParse(argument, out var limit) && limit >= 0)
                    _limit = limit;
                else
                    Console.Error.WriteLine("Usage: .limit <rows>, or '.limit 0' for no limit.");

                return true;

            default:
                Console.Error.WriteLine($"Unknown command '{command}'. Try .help.");
                return true;
        }
    }

    private void Format(string? argument)
    {
        if (argument is null)
        {
            Console.WriteLine($"Format is {_format.ToString().ToLowerInvariant()}.");
            return;
        }

        if (Enum.TryParse<ReplFormat>(argument, ignoreCase: true, out var format))
            _format = format;
        else
            Console.Error.WriteLine($"Unknown format '{argument}'. Use table, tsv, csv or json.");
    }

    private void Schema(string? table)
    {
        if (table is null)
        {
            Console.Error.WriteLine("Usage: .schema <table>");
            return;
        }

        try
        {
            using var command = _duck.CreateCommand();
            command.CommandText =
                "SELECT column_name, data_type, is_nullable FROM information_schema.columns " +
                $"WHERE table_schema = 'main' AND lower(table_name) = lower('{table.Replace("'", "''")}') " +
                "ORDER BY ordinal_position";

            using var reader = command.ExecuteReader();

            if (!reader.HasRows)
            {
                Console.Error.WriteLine($"No table '{table}' here. '.tables' lists what there is.");
                return;
            }

            Show(reader);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Error: {FirstLine(e.Message)}");
        }
    }

    private void Show(DbDataReader reader)
    {
        using (reader)
        {
            if (_format == ReplFormat.Table)
            {
                var (rows, truncated) = ReplTable.Write(reader, Console.Out, _limit);

                Console.Error.WriteLine(
                    truncated
                        ? $"({rows} rows shown, more remain. '.limit 0' shows everything.)"
                        : $"({Plural(rows)})");

                return;
            }

            var format = _format switch
            {
                ReplFormat.Csv => OutputFormat.Csv,
                ReplFormat.Json => OutputFormat.Json,
                _ => OutputFormat.Tsv,
            };

            Console.Error.WriteLine($"({Plural(ResultWriter.Write(reader, Console.Out, format, byteOrderMark: false))})");
        }
    }

    private static string Plural(long rows) => $"{rows} row{(rows == 1 ? string.Empty : "s")}";

    /// <summary>
    /// A cache with no Dataverse behind it. Enough for anything already
    /// fetched, plus JSON.
    /// </summary>
    private DataverseCache Local() => _cache ??= NewCache(NoDataverse.Instance, null);

    /// <summary>
    /// Connects to Dataverse, once, on first demand. Rebuilds the cache around
    /// a live source over the same DuckDB connection, so nothing fetched
    /// before the connection existed is lost.
    /// </summary>
    private DataverseCache Connect()
    {
        if (_client is not null && _cache is not null)
            return _cache;

        var options = _options()
            ?? throw new InvalidOperationException(
                "That needs Dataverse, but the configuration is incomplete. " +
                "Run 'dvduck doctor' for a diagnosis.");

        Console.Error.WriteLine($"Connecting to {options.EnvironmentUrl}...");

        var client = options.CreateServiceClient();

        if (!client.IsReady)
        {
            var reason = client.LastError ?? client.LastException?.Message ?? "unknown";
            client.Dispose();
            throw new InvalidOperationException($"Connection failed: {FirstLine(reason)}");
        }

        _client = client;

        _cache = NewCache(
            new Sql4CdsQuerySource(Sql4CdsConnectionFactory.Create(client)),
            new AttributeMetadataCache(client));

        return _cache;
    }

    private DataverseCache NewCache(IDataverseQuerySource source, IAttributeMetadataCache? metadata) =>
        new(_duck, source)
        {
            FoldingPolicy = _policy,
            Log = message => Console.Error.WriteLine($"  {message}"),

            // Metadata is what distinguishes a birthdate from an instant: the
            // reader reports both as DateTime with Kind=Unspecified.
            Mapper = new Schema.DataverseSchemaMapper { Metadata = metadata },
        };

    private static string FirstLine(string message) => message.Split('\n')[0].Trim();

    private static void PrintHelp() =>
        Console.WriteLine("""
            Statements end with ';'.

              SELECT ...                  Runs against DuckDB: whatever is cached, plus JSON.
              WITH x AS DATAVERSE (...),  Fetches from Dataverse first, then runs the query.
                   y AS JSON ('*.json')   The fetch happens once; x stays for the session.
              SELECT ...

              .tables                     What DuckDB currently holds.
              .schema <table>             Columns of one of them.
              .format [name]              table (default), tsv, csv or json.
              .limit [rows]               Rows to print. 0 for no limit. Default 50.
              .help                       This.
              .quit                       Leave. Ctrl-D does the same.

            Dataverse is contacted only when a DATAVERSE block first asks for it.
            """);

    /// <summary>
    /// Stands in until something asks for Dataverse. Reaching its Query means
    /// a plan step was routed here rather than through Connect, which would be
    /// a bug in this file and not anything the user did.
    /// </summary>
    private sealed class NoDataverse : IDataverseQuerySource
    {
        public static readonly NoDataverse Instance = new();

        public PlanAnalysis? Analyze(string sql) => null;

        public T Query<T>(string sql, Func<DbDataReader, T> read) =>
            throw new InvalidOperationException("No Dataverse connection was opened.");
    }
}
