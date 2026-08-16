using DataverseDuck.Plans;
using MarkMpn.Sql4Cds.Engine;
using MarkMpn.Sql4Cds.Engine.ExecutionPlan;

namespace DataverseDuck.Tests;

// NOTE: these are deliberately NOT 'file' types. The 'file' modifier mangles
// Type.Name into '<Owner>F4A2...__SortNode', and the analyzer matches on the
// plain name, so file-scoped doubles silently fail to be recognised.

/// <summary>
/// The engine's concrete node types are internal, so the analyzer identifies
/// them by type name and base-type chain. These doubles reproduce that shape,
/// including the names, which is the point.
/// </summary>
internal class FakeNode(int estimatedRows, params IExecutionPlanNode[] sources) : IDataExecutionPlanNode
{
    public IExecutionPlanNode Parent { get; set; } = null!;
    public int ExecutionCount => 0;
    public TimeSpan Duration => TimeSpan.Zero;
    public int EstimatedRowsOut { get; } = estimatedRows;
    public int RowsOut => 0;
    public IEnumerable<IExecutionPlanNode> GetSources() => sources;
}

internal sealed class FetchXmlScan(int rows) : FakeNode(rows), IFetchXmlExecutionPlanNode
{
    public string FetchXmlString => "<fetch><entity name='account' /></fetch>";
}

// Mirrors the real hierarchy: HashJoinNode -> FoldableJoinNode -> BaseJoinNode.
internal abstract class BaseJoinNode(int rows, params IExecutionPlanNode[] sources) : FakeNode(rows, sources);
internal abstract class FoldableJoinNode(int rows, params IExecutionPlanNode[] sources) : BaseJoinNode(rows, sources);
internal sealed class HashJoinNode(int rows, params IExecutionPlanNode[] sources) : FoldableJoinNode(rows, sources);

internal sealed class FilterNode(int rows, params IExecutionPlanNode[] sources) : FakeNode(rows, sources);
internal sealed class SortNode(int rows, params IExecutionPlanNode[] sources) : FakeNode(rows, sources);
internal sealed class IndexSpoolNode(int rows, params IExecutionPlanNode[] sources) : FakeNode(rows, sources);
internal sealed class HashMatchAggregateNode(int rows, params IExecutionPlanNode[] sources) : FakeNode(rows, sources);
internal sealed class ComputeScalarNode(int rows, params IExecutionPlanNode[] sources) : FakeNode(rows, sources);

public class ExecutionPlanAnalyzerTests
{
    private readonly ExecutionPlanAnalyzer _analyzer = new() { LargeRowThreshold = 10_000 };

    [Fact]
    public void A_pure_fetchxml_plan_is_fully_folded()
    {
        var analysis = _analyzer.AnalyzeNodes([new FetchXmlScan(500)]);

        Assert.True(analysis.FullyFolded);
        Assert.Empty(analysis.Problems);
        Assert.Equal(500, analysis.EstimatedRows);
        Assert.Single(analysis.FetchXml);
        Assert.Contains("folded entirely", analysis.Describe());
    }

    [Fact]
    public void A_join_that_did_not_fold_is_reported()
    {
        // The failure this project exists to catch: correct results, obtained by
        // dragging both tables across the network.
        var analysis = _analyzer.AnalyzeNodes([
            new HashJoinNode(200, new FetchXmlScan(100), new FetchXmlScan(100)),
        ]);

        Assert.False(analysis.FullyFolded);
        var finding = Assert.Single(analysis.Problems);
        Assert.Equal(PlanOperation.ClientSideJoin, finding.Operation);
        Assert.Equal("HashJoinNode", finding.NodeType);
    }

    [Fact]
    public void Local_work_over_a_large_table_is_critical_rather_than_a_warning()
    {
        var small = _analyzer.AnalyzeNodes([new HashJoinNode(9_999, new FetchXmlScan(10))]);
        var large = _analyzer.AnalyzeNodes([new HashJoinNode(10_000, new FetchXmlScan(10))]);

        // A client-side join over a few hundred rows is harmless; the same plan
        // over a large table is the thing that actually breaks.
        Assert.Equal(PlanSeverity.Warning, small.Problems.Single().Severity);
        Assert.Equal(PlanSeverity.Critical, large.Problems.Single().Severity);
        Assert.True(large.HasCritical);
        Assert.False(small.HasCritical);
    }

    [Fact]
    public void The_threshold_is_configurable()
    {
        var strict = new ExecutionPlanAnalyzer { LargeRowThreshold = 100 };

        Assert.True(strict.AnalyzeNodes([new HashJoinNode(150, new FetchXmlScan(1))]).HasCritical);
    }

    [Theory]
    [InlineData(typeof(FilterNode), PlanOperation.ClientSideFilter)]
    [InlineData(typeof(SortNode), PlanOperation.ClientSideSort)]
    [InlineData(typeof(IndexSpoolNode), PlanOperation.ClientSideSpool)]
    [InlineData(typeof(HashMatchAggregateNode), PlanOperation.ClientSideAggregate)]
    public void Each_kind_of_local_work_is_classified(Type nodeType, PlanOperation expected)
    {
        var node = (IExecutionPlanNode)Activator.CreateInstance(
            nodeType, 50, new IExecutionPlanNode[] { new FetchXmlScan(50) })!;

        var analysis = _analyzer.AnalyzeNodes([node]);

        Assert.Equal(expected, analysis.Problems.Single().Operation);
    }

    [Fact]
    public void Harmless_nodes_are_not_reported()
    {
        // Projection costs nothing and folding it is not meaningful.
        var analysis = _analyzer.AnalyzeNodes([new ComputeScalarNode(10, new FetchXmlScan(10))]);

        Assert.True(analysis.FullyFolded);
    }

    [Fact]
    public void The_whole_tree_is_walked_not_just_the_root()
    {
        // A fold failure is usually buried several levels down.
        var plan = new ComputeScalarNode(10,
            new FilterNode(20,
                new HashJoinNode(30, new FetchXmlScan(15), new FetchXmlScan(15))));

        var analysis = _analyzer.AnalyzeNodes([plan]);

        Assert.Equal(2, analysis.Problems.Count());
        Assert.Equal(2, analysis.FetchXml.Count);
    }

    [Fact]
    public void Estimated_rows_is_the_largest_server_side_scan()
    {
        var analysis = _analyzer.AnalyzeNodes([
            new HashJoinNode(50, new FetchXmlScan(1_000), new FetchXmlScan(250_000)),
        ]);

        Assert.Equal(250_000, analysis.EstimatedRows);
    }

    [Fact]
    public void A_node_with_no_sources_terminates_the_walk()
    {
        var analysis = _analyzer.AnalyzeNodes([new FetchXmlScan(1)]);

        Assert.Single(analysis.Findings);
    }

    [Fact]
    public void The_description_explains_why_folding_failed()
    {
        var analysis = _analyzer.AnalyzeNodes([new HashJoinNode(5, new FetchXmlScan(5))]);

        // A bare "did not fold" is not actionable; the cause has to be named.
        Assert.Contains("relationship", analysis.Describe());
    }
}

public class FoldingPolicyTests
{
    private static PlanAnalysis Folded() =>
        new ExecutionPlanAnalyzer().AnalyzeNodes([new FetchXmlScan(100)]);

    private static PlanAnalysis Warning() =>
        new ExecutionPlanAnalyzer { LargeRowThreshold = 10_000 }
            .AnalyzeNodes([new HashJoinNode(50, new FetchXmlScan(50))]);

    private static PlanAnalysis Critical() =>
        new ExecutionPlanAnalyzer { LargeRowThreshold = 10 }
            .AnalyzeNodes([new HashJoinNode(1_000, new FetchXmlScan(1_000))]);

    [Fact]
    public void Allow_and_Warn_never_reject()
    {
        foreach (var policy in new[] { FoldingPolicy.Allow, FoldingPolicy.Warn })
        {
            Assert.False(ExecutionPlanAnalyzer.IsRejectedBy(Critical(), policy));
            Assert.False(ExecutionPlanAnalyzer.IsRejectedBy(Warning(), policy));
        }
    }

    [Fact]
    public void RejectCritical_allows_small_local_work_but_stops_large()
    {
        // The useful default for unattended runs: a small in-memory join is
        // fine, a large one would appear to hang.
        Assert.False(ExecutionPlanAnalyzer.IsRejectedBy(Warning(), FoldingPolicy.RejectCritical));
        Assert.True(ExecutionPlanAnalyzer.IsRejectedBy(Critical(), FoldingPolicy.RejectCritical));
    }

    [Fact]
    public void RequireFullFolding_rejects_any_local_work_at_all()
    {
        Assert.True(ExecutionPlanAnalyzer.IsRejectedBy(Warning(), FoldingPolicy.RequireFullFolding));
        Assert.False(ExecutionPlanAnalyzer.IsRejectedBy(Folded(), FoldingPolicy.RequireFullFolding));
    }

    [Fact]
    public void No_policy_rejects_a_fully_folded_plan()
    {
        foreach (var policy in Enum.GetValues<FoldingPolicy>())
            Assert.False(ExecutionPlanAnalyzer.IsRejectedBy(Folded(), policy));
    }

    [Fact]
    public void The_exception_carries_the_analysis_and_explains_the_problem()
    {
        var exception = new PlanNotFoldedException(Critical());

        Assert.True(exception.Analysis.HasCritical);
        Assert.Contains("would not run entirely inside Dataverse", exception.Message);
        Assert.Contains("HashJoinNode", exception.Message);
        Assert.Contains("Rewrite the query", exception.Message);
    }
}

/// <summary>
/// The analyzer identifies internal node types by name. If a package upgrade
/// renames them, detection would silently stop working and every query would
/// look perfectly folded — the worst possible failure for a safety check.
/// These tests fail loudly instead.
/// </summary>
public class EngineTypeNameContractTests
{
    private static readonly System.Reflection.Assembly Engine = typeof(Sql4CdsConnection).Assembly;

    private static Type Find(string name) =>
        Engine.GetTypes().SingleOrDefault(t => t.Name == name)
        ?? throw new InvalidOperationException(
            $"SQL 4 CDS no longer defines '{name}'. ExecutionPlanAnalyzer detects it by name, " +
            "so its detection is now broken and must be updated.");

    [Theory]
    [InlineData("HashJoinNode")]
    [InlineData("MergeJoinNode")]
    [InlineData("NestedLoopNode")]
    public void Every_join_node_still_derives_from_the_base_type_we_detect(string nodeName)
    {
        var joinBase = Find(ExecutionPlanAnalyzer.JoinBaseTypeName);

        Assert.True(joinBase.IsAssignableFrom(Find(nodeName)),
            $"{nodeName} no longer derives from {ExecutionPlanAnalyzer.JoinBaseTypeName}.");
    }

    [Fact]
    public void No_join_node_has_appeared_that_we_would_miss()
    {
        var joinBase = Find(ExecutionPlanAnalyzer.JoinBaseTypeName);

        var concrete = Engine.GetTypes()
            .Where(t => joinBase.IsAssignableFrom(t) && !t.IsAbstract)
            .Select(t => t.Name)
            .OrderBy(n => n)
            .ToArray();

        // Detection is by base type, so a new join node is caught automatically.
        // This pins the set so an unexpected addition is at least visible.
        Assert.Equal(["HashJoinNode", "MergeJoinNode", "NestedLoopNode"], concrete);
    }

    [Theory]
    [InlineData(ExecutionPlanAnalyzer.FilterTypeName)]
    [InlineData(ExecutionPlanAnalyzer.SortTypeName)]
    public void The_node_types_detected_by_exact_name_still_exist(string nodeName)
    {
        Assert.NotNull(Find(nodeName));
    }

    [Fact]
    public void Server_side_scans_are_still_identifiable_by_a_public_interface()
    {
        // The most important signal, and the only one that is not name-based.
        Assert.True(typeof(IFetchXmlExecutionPlanNode).IsAssignableFrom(Find("FetchXmlScan")));
    }

    [Fact]
    public void Spool_and_aggregate_nodes_still_match_their_name_substrings()
    {
        var names = Engine.GetTypes().Select(t => t.Name).ToArray();

        Assert.Contains(names, n => n.Contains("Spool", StringComparison.Ordinal));
        Assert.Contains(names, n => n.Contains("Aggregate", StringComparison.Ordinal));
    }

    [Fact]
    public void The_plan_can_still_be_compiled_without_executing_it()
    {
        // Enforce() depends on this: warning before a bad query runs, not after.
        var method = typeof(Sql4CdsCommand).GetMethod("GeneratePlan");

        Assert.NotNull(method);
        Assert.Equal("compileForExecution", method.GetParameters().Single().Name);
    }
}
