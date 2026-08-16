namespace DataverseDuck.Configuration;

/// <summary>
/// Loads a <c>.env</c> file into the process environment.
///
/// Sourcing a file into the shell works, but it is one more step to forget and
/// it leaves the secret exported to every process started from that shell. A
/// file the tool reads itself is both easier and narrower.
///
/// Real environment variables always win. That ordering matters in CI, where
/// the secret arrives from a secret store and a stale committed-by-accident
/// <c>.env</c> silently overriding it would be the worst possible outcome.
/// </summary>
public static class DotEnvFile
{
    /// <summary>Conventional file name.</summary>
    public const string FileName = ".env";

    /// <summary>
    /// Loads <c>.env</c> from the current directory or the nearest ancestor,
    /// so the tool works from anywhere inside the repository.
    /// </summary>
    /// <returns>The file that was loaded, or null if none was found.</returns>
    public static string? LoadFromCurrentDirectory()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, FileName);

            if (File.Exists(candidate))
            {
                Load(candidate);
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>
    /// Applies one file. Variables already set in the environment are left
    /// alone.
    /// </summary>
    public static void Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        foreach (var raw in File.ReadLines(path))
        {
            if (!TryParse(raw, out var name, out var value))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
            {
                continue;
            }

            Environment.SetEnvironmentVariable(name, value);
        }
    }

    /// <summary>
    /// Parses one line. Skips blanks and comments; tolerates the <c>export</c>
    /// prefix, since the same file is often sourced by a shell.
    /// </summary>
    private static bool TryParse(string line, out string name, out string value)
    {
        name = string.Empty;
        value = string.Empty;

        var text = line.Trim();

        if (text.Length == 0 || text.StartsWith('#'))
        {
            return false;
        }

        if (text.StartsWith("export ", StringComparison.Ordinal))
        {
            text = text[7..].TrimStart();
        }

        var separator = text.IndexOf('=');
        if (separator <= 0)
        {
            return false;
        }

        name = text[..separator].Trim();
        value = text[(separator + 1)..].Trim();

        if (name.Length == 0)
        {
            return false;
        }

        // Strip one layer of matching quotes. A secret may legitimately contain
        // '#', so no comment stripping happens after the '=' -- taking the rest
        // of the line verbatim is the only safe reading.
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            value = value[1..^1];
        }

        return true;
    }
}
