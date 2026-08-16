namespace DataverseDuck.Diagnostics;

public enum CheckStatus
{
    Passed,
    Warning,
    Failed,
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
    public static CheckResult Pass(string name, string detail) => new(name, CheckStatus.Passed, detail);

    public static CheckResult Warn(string name, string detail, string remedy) =>
        new(name, CheckStatus.Warning, detail, remedy);

    public static CheckResult Fail(string name, string detail, string remedy) =>
        new(name, CheckStatus.Failed, detail, remedy);

    public static CheckResult Skip(string name, string detail) => new(name, CheckStatus.Skipped, detail);
}

/// <summary>
/// A full diagnostic run.
/// </summary>
public sealed record DoctorReport(IReadOnlyList<CheckResult> Checks)
{
    public bool Succeeded => Checks.All(c => c.Status is CheckStatus.Passed or CheckStatus.Skipped or CheckStatus.Warning);

    public CheckResult? FirstFailure => Checks.FirstOrDefault(c => c.Status == CheckStatus.Failed);
}
