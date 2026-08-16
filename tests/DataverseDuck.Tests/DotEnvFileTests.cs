using DataverseDuck.Configuration;

namespace DataverseDuck.Tests;

public class DotEnvFileTests : IDisposable
{
    private readonly List<string> _files = [];
    private readonly List<string> _variables = [];

    public void Dispose()
    {
        foreach (var file in _files.Where(File.Exists))
        {
            File.Delete(file);
        }

        foreach (var variable in _variables)
        {
            Environment.SetEnvironmentVariable(variable, null);
        }

        GC.SuppressFinalize(this);
    }

    private string Write(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotenv-{Guid.NewGuid():N}");
        File.WriteAllText(path, content);
        _files.Add(path);
        return path;
    }

    private string Variable()
    {
        var name = $"DVDUCK_TEST_{Guid.NewGuid():N}";
        _variables.Add(name);
        return name;
    }

    [Fact]
    public void Sets_a_plain_assignment()
    {
        var name = Variable();
        DotEnvFile.Load(Write($"{name}=https://example.crm4.dynamics.com"));

        Assert.Equal("https://example.crm4.dynamics.com", Environment.GetEnvironmentVariable(name));
    }

    [Fact]
    public void An_exported_variable_wins_over_the_file()
    {
        var name = Variable();
        Environment.SetEnvironmentVariable(name, "from-the-shell");

        DotEnvFile.Load(Write($"{name}=from-the-file"));

        // In CI the secret arrives from a secret store. A stale .env quietly
        // overriding it would be the worst outcome available, so the file
        // never wins.
        Assert.Equal("from-the-shell", Environment.GetEnvironmentVariable(name));
    }

    [Theory]
    [InlineData("\"quoted value\"", "quoted value")]
    [InlineData("'quoted value'", "quoted value")]
    [InlineData("  padded  ", "padded")]
    public void Strips_one_layer_of_quotes_and_surrounding_space(string written, string expected)
    {
        var name = Variable();
        DotEnvFile.Load(Write($"{name}={written}"));

        Assert.Equal(expected, Environment.GetEnvironmentVariable(name));
    }

    [Fact]
    public void Tolerates_the_export_prefix()
    {
        var name = Variable();
        DotEnvFile.Load(Write($"export {name}=value"));

        Assert.Equal("value", Environment.GetEnvironmentVariable(name));
    }

    [Fact]
    public void Keeps_a_hash_inside_a_value()
    {
        var name = Variable();

        // Entra secrets are generated from a wide alphabet. Treating '#' as a
        // comment would truncate one and produce an authentication failure
        // that looks like a wrong secret rather than a parsing bug.
        DotEnvFile.Load(Write($"{name}=abc#def~ghi"));

        Assert.Equal("abc#def~ghi", Environment.GetEnvironmentVariable(name));
    }

    [Fact]
    public void Keeps_an_equals_sign_inside_a_value()
    {
        var name = Variable();
        DotEnvFile.Load(Write($"{name}=abc=def=="));

        Assert.Equal("abc=def==", Environment.GetEnvironmentVariable(name));
    }

    [Fact]
    public void Ignores_comments_and_blank_lines()
    {
        var name = Variable();
        DotEnvFile.Load(Write($"""
            # a comment

               # an indented comment
            {name}=value
            """));

        Assert.Equal("value", Environment.GetEnvironmentVariable(name));
    }

    [Fact]
    public void Ignores_a_line_with_no_assignment()
    {
        var name = Variable();
        DotEnvFile.Load(Write($"""
            this line is not an assignment
            =novalue
            {name}=value
            """));

        Assert.Equal("value", Environment.GetEnvironmentVariable(name));
    }

    [Fact]
    public void Finds_the_file_in_an_ancestor_directory()
    {
        var root = Directory.CreateTempSubdirectory("dotenv-walk");
        var nested = Directory.CreateDirectory(Path.Combine(root.FullName, "src", "deep"));
        var name = Variable();

        File.WriteAllText(Path.Combine(root.FullName, DotEnvFile.FileName), $"{name}=found");

        var previous = Directory.GetCurrentDirectory();

        try
        {
            Directory.SetCurrentDirectory(nested.FullName);

            var loaded = DotEnvFile.LoadFromCurrentDirectory();

            Assert.NotNull(loaded);
            Assert.Equal("found", Environment.GetEnvironmentVariable(name));
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Reports_no_file_when_there_is_none()
    {
        var directory = Directory.CreateTempSubdirectory("dotenv-none");
        var previous = Directory.GetCurrentDirectory();

        try
        {
            Directory.SetCurrentDirectory(directory.FullName);

            // Only meaningful if no ancestor of the temp directory has one.
            var loaded = DotEnvFile.LoadFromCurrentDirectory();

            Assert.Null(loaded);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
            directory.Delete(recursive: true);
        }
    }
}
