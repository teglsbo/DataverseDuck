namespace DataverseDuck.Plans;

/// <summary>
/// What to do when part of a query will run locally instead of in Dataverse.
/// </summary>
public enum FoldingPolicy
{
    /// <summary>Analyse only. Nothing is logged or thrown.</summary>
    Allow,

    /// <summary>Log local work, but run the query anyway. The default.</summary>
    Warn,

    /// <summary>
    /// Refuse plans that do local work over a large row count. Recommended for
    /// unattended runs, where the alternative is a query that appears to hang.
    /// </summary>
    RejectCritical,

    /// <summary>
    /// Refuse any plan that is not entirely FetchXML. Strict, and appropriate
    /// when every query is expected to be a simple server-side scan.
    /// </summary>
    RequireFullFolding,
}

/// <summary>
/// Thrown when a plan does more local work than the configured policy allows.
/// </summary>
public sealed class PlanNotFoldedException : Exception
{
    public PlanNotFoldedException(PlanAnalysis analysis)
        : base(BuildMessage(analysis))
    {
        Analysis = analysis;
    }

    /// <summary>The analysis that caused the exception, for structured inspection by callers.</summary>
    public PlanAnalysis Analysis { get; }

    private static string BuildMessage(PlanAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        return "The query would not run entirely inside Dataverse.\n" +
               analysis.Describe() +
               "\n\nRewrite the query so these operations fold into FetchXML, or raise the " +
               "policy if the row counts are genuinely small.";
    }
}
