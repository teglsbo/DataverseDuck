using MarkMpn.Sql4Cds.Engine;
using MarkMpn.Sql4Cds.Engine.ExecutionPlan;

namespace DataverseDuck.Plans;

/// <summary>
/// Inspects a SQL 4 CDS execution plan and reports work that will happen in this
/// process rather than inside Dataverse.
///
/// This is the central guard of the project. SQL 4 CDS compiles T-SQL into
/// FetchXML where it can, and silently falls back to fetching both sides and
/// joining locally where it cannot. The fallback is not an error: the query
/// returns correct results. It just does so by pulling entire tables across the
/// network, which for the large tables we care about means a query that appears
/// to hang, exhausts memory, or trips Dataverse's service protection limits.
///
/// Because <c>GeneratePlan</c> compiles without executing, this can warn
/// <em>before</em> a bad query runs rather than after.
///
/// Detection uses two signals:
/// <list type="bullet">
/// <item><see cref="IFetchXmlExecutionPlanNode"/> is public, so server-side scans
/// are identified by interface — a stable signal.</item>
/// <item>The concrete join, filter and spool node types are <c>internal</c>, so
/// those are identified by walking the base-type name chain. That is inherently
/// more fragile, which is why <see cref="JoinBaseTypeName"/> and friends are
/// named constants covered by tests.</item>
/// </list>
/// </summary>
public sealed class ExecutionPlanAnalyzer
{
    internal const string JoinBaseTypeName = "BaseJoinNode";
    internal const string FilterTypeName = "FilterNode";
    internal const string SortTypeName = "SortNode";

    /// <summary>
    /// Row count above which local work is treated as
    /// <see cref="PlanSeverity.Critical"/> rather than a warning.
    ///
    /// A client-side join over a few hundred rows is harmless. The same plan over
    /// a million rows is the failure this project exists to avoid.
    /// </summary>
    public int LargeRowThreshold { get; init; } = 10_000;

    /// <summary>
    /// Compiles the command's plan without executing it, and analyses it.
    /// </summary>
    public PlanAnalysis Analyze(Sql4CdsCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return AnalyzeNodes(command.GeneratePlan(compileForExecution: false));
    }

    /// <summary>
    /// Analyses an already-generated plan, for example one captured from
    /// <c>StatementCompleted</c> after execution.
    /// </summary>
    public PlanAnalysis AnalyzeNodes(IEnumerable<IExecutionPlanNode> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);

        var findings = new List<PlanFinding>();
        var fetchXml = new List<string>();
        var estimatedRows = 0;

        foreach (var root in roots)
        {
            foreach (var node in Walk(root))
            {
                if (node is IFetchXmlExecutionPlanNode fetch)
                {
                    fetchXml.Add(fetch.FetchXmlString);
                    estimatedRows = Math.Max(estimatedRows, EstimatedRows(node));
                    findings.Add(new PlanFinding(
                        PlanOperation.ServerSideScan, PlanSeverity.Info,
                        TypeName(node), EstimatedRows(node),
                        "Executed inside Dataverse as FetchXML."));
                    continue;
                }

                var finding = Classify(node);
                if (finding is not null)
                    findings.Add(finding);
            }
        }

        return new PlanAnalysis(findings, fetchXml, estimatedRows);
    }

    private PlanFinding? Classify(IExecutionPlanNode node)
    {
        var rows = LargestEstimate(node);
        var name = TypeName(node);

        if (DerivesFrom(node, JoinBaseTypeName))
            return new PlanFinding(
                PlanOperation.ClientSideJoin, Escalate(rows), name, rows,
                "This join did not fold into FetchXML, so both sides are fetched and joined " +
                "in memory. Dataverse link-entity joins require a relationship between the " +
                "tables; a join on a non-relationship column, across data sources, or with a " +
                "non-equality predicate cannot fold.");

        if (name == FilterTypeName)
            return new PlanFinding(
                PlanOperation.ClientSideFilter, Escalate(rows), name, rows,
                "This predicate did not fold into the FetchXML filter, so rows are fetched " +
                "and then discarded locally. Functions applied to a column, and comparisons " +
                "between two columns, commonly prevent folding.");

        if (name.Contains("Spool", StringComparison.Ordinal))
            return new PlanFinding(
                PlanOperation.ClientSideSpool, Escalate(rows), name, rows,
                "Rows are buffered in memory to support a join or repeated read.");

        if (name == SortTypeName)
            return new PlanFinding(
                PlanOperation.ClientSideSort, Escalate(rows), name, rows,
                "Ordering is applied locally rather than by Dataverse, so all rows must be " +
                "fetched before the first is returned.");

        if (name.Contains("Aggregate", StringComparison.Ordinal))
            return new PlanFinding(
                PlanOperation.ClientSideAggregate, Escalate(rows), name, rows,
                "Grouping is applied locally, so detail rows are fetched to compute a summary. " +
                "Dataverse can aggregate server-side, but caps results at 50,000 rows.");

        return null;
    }

    private PlanSeverity Escalate(int rows) =>
        rows >= LargeRowThreshold ? PlanSeverity.Critical : PlanSeverity.Warning;

    /// <summary>
    /// The offending node's own <see cref="EstimatedRows"/> is often small or
    /// unavailable (-1) even when its subtree processes a large number of rows
    /// -- a client-side join's own row count, for instance, describes its
    /// output, not the (potentially huge) input it joined against. Walking the
    /// subtree for the largest estimate avoids under-classifying severity for
    /// non-folding operations that sit above a large data source.
    /// </summary>
    private static int LargestEstimate(IExecutionPlanNode node) =>
        Walk(node)
            .Select(EstimatedRows)
            .Where(rows => rows >= 0)
            .DefaultIfEmpty(-1)
            .Max();

    /// <summary>
    /// Analyses a command and applies a policy to the result.
    ///
    /// Intended to sit in front of execution: with <see cref="FoldingPolicy.RejectCritical"/>
    /// a query that would drag a large table into memory fails immediately with an
    /// explanation, instead of appearing to hang.
    /// </summary>
    /// <param name="command">Command to inspect. Not executed.</param>
    /// <param name="policy">What to do about local work.</param>
    /// <param name="log">Receives a description when there is anything to report.</param>
    /// <exception cref="PlanNotFoldedException">
    /// Thrown when the policy rejects the plan.
    /// </exception>
    public PlanAnalysis Enforce(
        Sql4CdsCommand command,
        FoldingPolicy policy = FoldingPolicy.Warn,
        Action<string>? log = null)
    {
        var analysis = Analyze(command);

        if (!analysis.FullyFolded)
            log?.Invoke(analysis.Describe());

        if (IsRejectedBy(analysis, policy))
            throw new PlanNotFoldedException(analysis);

        return analysis;
    }

    /// <summary>
    /// Whether a policy rejects an analysis. Separate from <see cref="Enforce"/>
    /// so the decision can be exercised without a live connection.
    /// </summary>
    public static bool IsRejectedBy(PlanAnalysis analysis, FoldingPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        return policy switch
        {
            FoldingPolicy.RejectCritical => analysis.HasCritical,
            FoldingPolicy.RequireFullFolding => !analysis.FullyFolded,
            _ => false,
        };
    }

    /// <summary>
    /// Depth-first walk of the plan tree.
    /// </summary>
    private static IEnumerable<IExecutionPlanNode> Walk(IExecutionPlanNode node)
    {
        yield return node;

        // A node with no sources is a leaf; GetSources never returns null in
        // practice, but a plan built by a test double might.
        foreach (var source in node.GetSources() ?? [])
            foreach (var descendant in Walk(source))
                yield return descendant;
    }

    private static int EstimatedRows(IExecutionPlanNode node)
    {
        // Only meaningful on data nodes, and only once the plan is costed.
        if (node is not IDataExecutionPlanNode data)
            return -1;

        try
        {
            return data.EstimatedRowsOut;
        }
        catch
        {
            return -1;
        }
    }

    private static string TypeName(IExecutionPlanNode node) => node.GetType().Name;

    private static bool DerivesFrom(object node, string baseTypeName)
    {
        for (var type = node.GetType(); type is not null; type = type.BaseType)
        {
            if (type.Name == baseTypeName)
                return true;
        }

        return false;
    }
}
