using DataverseDuck.Cli;
using DataverseDuck.Plans;

namespace DataverseDuck.Tests;

/// <summary>
/// The 'query' subcommand's parsing had never been exercised without also
/// booting DuckDB and (for a Dataverse step) a live connection. These tests
/// cover the flag shapes directly, including the --plan/--plan-file deprecated
/// aliases and the --snapshot addition.
/// </summary>
public class QueryArgumentsTests
{
    [Fact]
    public void ReadsQueryAndDefaults()
    {
        Assert.True(QueryArguments.TryParse(["--query", "SELECT 1"], out var parsed, out var error));

        Assert.Null(error);
        Assert.Equal("SELECT 1", parsed!.Plan);
        Assert.Equal(":memory:", parsed.Database);
        Assert.Null(parsed.SnapshotPath);
        Assert.Equal(FoldingPolicy.Warn, parsed.Policy);
        Assert.Equal(OutputFormat.Tsv, parsed.Format);
        Assert.False(parsed.ByteOrderMark);
        Assert.Empty(parsed.Deprecations);
    }

    [Fact]
    public void PlanIsAcceptedAsADeprecatedAliasForQuery()
    {
        Assert.True(QueryArguments.TryParse(["--plan", "SELECT 1"], out var parsed, out _));

        Assert.Equal("SELECT 1", parsed!.Plan);
        Assert.Contains(parsed.Deprecations, m => m.Contains("--plan is now --query"));
    }

    [Fact]
    public void PlanFileIsAcceptedAsADeprecatedAliasForQueryFile()
    {
        var path = Path.GetTempFileName();

        try
        {
            File.WriteAllText(path, "SELECT 1");

            Assert.True(QueryArguments.TryParse(["--plan-file", path], out var parsed, out _));

            Assert.Equal("SELECT 1", parsed!.Plan);
            Assert.Contains(parsed.Deprecations, m => m.Contains("--plan-file is now --query-file"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void QueryFileReadsTheFileContents()
    {
        var path = Path.GetTempFileName();

        try
        {
            File.WriteAllText(path, "SELECT 42");

            Assert.True(QueryArguments.TryParse(["--query-file", path], out var parsed, out _));

            Assert.Equal("SELECT 42", parsed!.Plan);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void QueryFileMissingReportsAnError()
    {
        Assert.False(QueryArguments.TryParse(["--query-file", "/no/such/file.sql"], out var parsed, out var error));

        Assert.Null(parsed);
        Assert.Contains("No query file", error);
    }

    [Fact]
    public void DbSetsTheDatabasePath()
    {
        Assert.True(QueryArguments.TryParse(["--query", "SELECT 1", "--db", "cache.db"], out var parsed, out _));

        Assert.Equal("cache.db", parsed!.Database);
    }

    [Fact]
    public void SnapshotSetsTheSnapshotPath()
    {
        Assert.True(QueryArguments.TryParse(
            ["--query", "SELECT 1", "--snapshot", "metadata/snapshot.bin"], out var parsed, out _));

        Assert.Equal("metadata/snapshot.bin", parsed!.SnapshotPath);
    }

    [Fact]
    public void StrictSetsRejectCriticalPolicy()
    {
        Assert.True(QueryArguments.TryParse(["--query", "SELECT 1", "--strict"], out var parsed, out _));

        Assert.Equal(FoldingPolicy.RejectCritical, parsed!.Policy);
    }

    [Fact]
    public void BomWithJsonFormatIsRejected()
    {
        Assert.False(QueryArguments.TryParse(
            ["--query", "SELECT 1", "--format", "json", "--bom"], out var parsed, out var error));

        Assert.Null(parsed);
        Assert.Contains("--bom cannot be used with --format json", error);
    }

    [Fact]
    public void NoQueryGivenReportsAnError()
    {
        Assert.False(QueryArguments.TryParse([], out var parsed, out var error));

        Assert.Null(parsed);
        Assert.Contains("Nothing to run", error);
    }

    [Theory]
    [InlineData("--cache")]
    [InlineData("--json")]
    [InlineData("--run")]
    public void RemovedFlagsExplainTheReplacement(string flag)
    {
        Assert.False(QueryArguments.TryParse([flag], out var parsed, out var error));

        Assert.Null(parsed);
        Assert.Contains("was removed", error);
    }

    [Theory]
    [InlineData("--db")]
    [InlineData("--query")]
    [InlineData("--query-file")]
    [InlineData("--snapshot")]
    [InlineData("--format")]
    public void FlagsThatNeedAValueReportWhenMissingOne(string flag)
    {
        Assert.False(QueryArguments.TryParse([flag], out var parsed, out var error));

        Assert.Null(parsed);
        Assert.Contains($"{flag} needs a value", error);
    }

    [Fact]
    public void UnknownOptionIsRejected()
    {
        Assert.False(QueryArguments.TryParse(["--nope"], out var parsed, out var error));

        Assert.Null(parsed);
        Assert.Contains("Unknown option '--nope'", error);
    }
}
