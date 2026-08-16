namespace DataverseDuck.Plans;

/// <summary>
/// How much a plan finding matters.
/// </summary>
public enum PlanSeverity
{
    /// <summary>Worth knowing, not a problem.</summary>
    Info,

    /// <summary>Work is happening locally that could have run in Dataverse.</summary>
    Warning,

    /// <summary>
    /// Local work over a row count large enough to be a real problem: slow,
    /// memory-hungry, and liable to hit service protection limits.
    /// </summary>
    Critical,
}

/// <summary>
/// What a plan node is doing, in terms that matter to us.
/// </summary>
public enum PlanOperation
{
    /// <summary>Executed inside Dataverse as FetchXML. What we want.</summary>
    ServerSideScan,

    /// <summary>
    /// A join the optimiser could not fold into FetchXML, so both sides are
    /// fetched and joined in this process.
    /// </summary>
    ClientSideJoin,

    /// <summary>A predicate not folded into the FetchXML filter.</summary>
    ClientSideFilter,

    /// <summary>Rows buffered in memory to support a join or repeated read.</summary>
    ClientSideSpool,

    /// <summary>Ordering applied locally.</summary>
    ClientSideSort,

    /// <summary>Grouping applied locally.</summary>
    ClientSideAggregate,
}

/// <param name="Operation">What the node does.</param>
/// <param name="Severity">How much it matters.</param>
/// <param name="NodeType">Engine node type name, for diagnosis.</param>
/// <param name="EstimatedRows">Rows the optimiser expects, or -1 if unavailable.</param>
/// <param name="Message">Explanation, including what to do about it.</param>
public sealed record PlanFinding(
    PlanOperation Operation,
    PlanSeverity Severity,
    string NodeType,
    int EstimatedRows,
    string Message)
{
    public override string ToString() =>
        $"[{Severity}] {NodeType}: {Message}" +
        (EstimatedRows >= 0 ? $" (estimated {EstimatedRows:N0} rows)" : string.Empty);
}

/// <summary>
/// The result of inspecting a compiled execution plan.
/// </summary>
public sealed record PlanAnalysis(
    IReadOnlyList<PlanFinding> Findings,
    IReadOnlyList<string> FetchXml,
    int EstimatedRows)
{
    /// <summary>
    /// True when every join, filter and aggregate was pushed into Dataverse.
    /// </summary>
    public bool FullyFolded => Findings.All(f => f.Severity == PlanSeverity.Info);

    /// <summary>Findings that indicate real local work.</summary>
    public IEnumerable<PlanFinding> Problems =>
        Findings.Where(f => f.Severity != PlanSeverity.Info);

    public bool HasCritical => Findings.Any(f => f.Severity == PlanSeverity.Critical);

    /// <summary>A readable summary, suitable for logging.</summary>
    public string Describe()
    {
        if (FullyFolded)
            return $"Plan folded entirely into FetchXML (estimated {EstimatedRows:N0} rows).";

        var lines = Problems.Select(f => "  " + f);
        return $"Plan does {Problems.Count()} operation(s) locally:\n{string.Join("\n", lines)}";
    }
}
