using DataverseDuck.Configuration;
using DataverseDuck.Diagnostics;
using DataverseDuck.Metadata;
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

            Configuration (environment variables):
              DATAVERSE_URL            https://yourorg.crm4.dynamics.com
              DATAVERSE_CLIENT_ID      Application (client) ID of the app registration
              DATAVERSE_CLIENT_SECRET  Client secret *value*
              DATAVERSE_TENANT_ID      Directory (tenant) ID (optional but recommended)

            Options:
              --out <path>             Snapshot path for 'capture'. Default: metadata/snapshot.bin

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
