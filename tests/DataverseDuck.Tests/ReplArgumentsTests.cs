using DataverseDuck.Cli;
using DataverseDuck.Plans;

namespace DataverseDuck.Tests;

/// <summary>
/// The 'repl' subcommand's parsing, mirroring <see cref="QueryArgumentsTests"/>.
/// </summary>
public class ReplArgumentsTests
{
    [Fact]
    public void DefaultsWhenNoArgumentsGiven()
    {
        Assert.True(ReplArguments.TryParse([], out var parsed, out var error));

        Assert.Null(error);
        Assert.Equal(":memory:", parsed!.Database);
        Assert.Null(parsed.SnapshotPath);
        Assert.Equal(FoldingPolicy.Warn, parsed.Policy);
    }

    [Fact]
    public void DbSetsTheDatabasePath()
    {
        Assert.True(ReplArguments.TryParse(["--db", "cache.db"], out var parsed, out _));

        Assert.Equal("cache.db", parsed!.Database);
    }

    [Fact]
    public void SnapshotSetsTheSnapshotPath()
    {
        Assert.True(ReplArguments.TryParse(["--snapshot", "metadata/snapshot.bin"], out var parsed, out _));

        Assert.Equal("metadata/snapshot.bin", parsed!.SnapshotPath);
    }

    [Fact]
    public void StrictSetsRejectCriticalPolicy()
    {
        Assert.True(ReplArguments.TryParse(["--strict"], out var parsed, out _));

        Assert.Equal(FoldingPolicy.RejectCritical, parsed!.Policy);
    }

    [Theory]
    [InlineData("--db")]
    [InlineData("--snapshot")]
    public void FlagsThatNeedAValueReportWhenMissingOne(string flag)
    {
        Assert.False(ReplArguments.TryParse([flag], out var parsed, out var error));

        Assert.Null(parsed);
        Assert.Contains($"{flag} needs a", error);
    }

    [Fact]
    public void UnknownOptionIsRejected()
    {
        Assert.False(ReplArguments.TryParse(["--nope"], out var parsed, out var error));

        Assert.Null(parsed);
        Assert.Contains("Unknown option '--nope'", error);
    }
}
