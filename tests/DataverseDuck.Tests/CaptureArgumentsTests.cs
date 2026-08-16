using DataverseDuck.Cli;

namespace DataverseDuck.Tests;

/// <summary>
/// The CLI had no tests at all, which is how 'dvduck capture account contact'
/// came to capture only contact. These cover the argument shapes that a first
/// run actually produces.
/// </summary>
public class CaptureArgumentsTests
{
    [Fact]
    public void KeepsEveryTableWhenNoOutFlagIsGiven()
    {
        // The regression. The old parser excluded index outIndex + 1, and with
        // no --out present outIndex was -1, so it dropped args[0] -- silently,
        // then reported the smaller count as though it were what you asked for.
        Assert.True(CaptureArguments.TryParse(["account", "contact"], out var parsed, out var error));

        Assert.Null(error);
        Assert.Equal(["account", "contact"], parsed!.Tables);
    }

    [Fact]
    public void DefaultsToTheStandardSnapshotPath()
    {
        Assert.True(CaptureArguments.TryParse(["account"], out var parsed, out _));

        Assert.Equal(Path.Combine("metadata", "snapshot.bin"), parsed!.Path);
    }

    [Theory]
    [InlineData("--out")]
    [InlineData("-o")]
    public void TakesThePathFromTheFlagAndKeepsTheTables(string flag)
    {
        Assert.True(CaptureArguments.TryParse(
            ["account", "contact", flag, "/tmp/snap.bin"], out var parsed, out _));

        Assert.Equal("/tmp/snap.bin", parsed!.Path);
        Assert.Equal(["account", "contact"], parsed.Tables);
    }

    [Fact]
    public void AcceptsTheFlagBeforeTheTables()
    {
        // Nothing forces the flag to come last, and the value must not be
        // mistaken for a table name wherever it appears.
        Assert.True(CaptureArguments.TryParse(
            ["--out", "/tmp/snap.bin", "account", "contact"], out var parsed, out _));

        Assert.Equal("/tmp/snap.bin", parsed!.Path);
        Assert.Equal(["account", "contact"], parsed.Tables);
    }

    [Fact]
    public void AcceptsTheFlagBetweenTables()
    {
        Assert.True(CaptureArguments.TryParse(
            ["account", "--out", "/tmp/snap.bin", "contact"], out var parsed, out _));

        Assert.Equal("/tmp/snap.bin", parsed!.Path);
        Assert.Equal(["account", "contact"], parsed.Tables);
    }

    [Fact]
    public void RejectsAnEmptyCommand()
    {
        Assert.False(CaptureArguments.TryParse([], out var parsed, out var error));

        Assert.Null(parsed);
        Assert.Contains("at least one table", error);
    }

    [Fact]
    public void RejectsAFlagWithNoValue()
    {
        Assert.False(CaptureArguments.TryParse(["account", "--out"], out _, out var error));

        Assert.Contains("--out needs a value", error);
    }

    [Fact]
    public void RejectsOnlyAPathAndNoTables()
    {
        Assert.False(CaptureArguments.TryParse(["--out", "/tmp/snap.bin"], out _, out var error));

        Assert.Contains("at least one table", error);
    }

    [Fact]
    public void RejectsAnUnknownOptionRatherThanCapturingIt()
    {
        // The old filter dropped anything starting with '-', so a typo became a
        // silently smaller capture instead of an error.
        Assert.False(CaptureArguments.TryParse(["account", "--outt", "x"], out _, out var error));

        Assert.Contains("--outt", error);
    }

    [Fact]
    public void RejectsATableNamedTwice()
    {
        Assert.False(CaptureArguments.TryParse(["account", "Account"], out _, out var error));

        Assert.Contains("more than once", error);
    }
}
