namespace DataverseDuck.Diagnostics;

/// <summary>Outcome of a single environment check.</summary>
public enum CheckStatus
{
    /// <summary>The check passed with no concerns.</summary>
    Passed,

    /// <summary>The check found something that may cause problems, but is not blocking.</summary>
    Warning,

    /// <summary>The check found a definite misconfiguration that must be fixed.</summary>
    Failed,

    /// <summary>The check was not applicable and was not run.</summary>
    Skipped,
}

/// <summary>
/// The outcome of one environment check.
/// </summary>
/// <param name="Name">Short label.</param>
/// <param name="Status">Outcome.</param>
/// <param name="Detail">What was observed.</param>
/// <param name="Remedy">
/// What to do about it. Populated for anything other than a pass, because the
/// whole point of this command is that Dataverse setup failures are opaque:
/// a missing application user and a missing security role both surface as an
/// authentication error, but the fixes are in different portals.
/// </param>
public sealed record CheckResult(
    string Name,
    CheckStatus Status,
    string Detail,
    string? Remedy = null)
{
    /// <summary>Creates a passing result — everything looks good.</summary>
    public static CheckResult Pass(string name, string detail) => new(name, CheckStatus.Passed, detail);

    /// <summary>Creates a warning result — something is unusual but not blocking.</summary>
    public static CheckResult Warn(string name, string detail, string remedy) =>
        new(name, CheckStatus.Warning, detail, remedy);

    /// <summary>Creates a failure result — a misconfiguration that must be fixed.</summary>
    public static CheckResult Fail(string name, string detail, string remedy) =>
        new(name, CheckStatus.Failed, detail, remedy);

    /// <summary>Creates a skipped result — the check was not applicable.</summary>
    public static CheckResult Skip(string name, string detail) => new(name, CheckStatus.Skipped, detail);
}

/// <summary>
/// A full diagnostic run.
/// </summary>
public sealed record DoctorReport(IReadOnlyList<CheckResult> Checks)
{
    /// <summary>True when no check failed. Warnings and skips are treated as acceptable.</summary>
    public bool Succeeded => Checks.All(c => c.Status is CheckStatus.Passed or CheckStatus.Skipped or CheckStatus.Warning);

    /// <summary>The first failed check, or null when <see cref="Succeeded"/> is true.</summary>
    public CheckResult? FirstFailure => Checks.FirstOrDefault(c => c.Status == CheckStatus.Failed);
}
