using CodeIndex.Cli;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Theory]
    [InlineData("--visibility")]
    [InlineData("--exclude-visibility")]
    public void ParseArgs_VisibilityFiltersRejectOverlongCsv_Issue2912(string optionName)
    {
        var tooLong = new string('p', QueryCommandRunner.MaxVisibilityFilterCsvLength + 1);

        var options = QueryCommandRunner.ParseArgs(
            ["RunSearch", optionName, tooLong],
            jsonDefault: false,
            allowNamedQuery: true);

        Assert.Contains($"{optionName} value is too long", options.ParseError);
        Assert.Empty(options.VisibilityFilters);
        Assert.Empty(options.ExcludeVisibilityFilters);
    }

    [Theory]
    [InlineData("--visibility")]
    [InlineData("--exclude-visibility")]
    public void ParseArgs_VisibilityFiltersRejectTooManyCsvEntries_Issue2912(string optionName)
    {
        var tooMany = string.Join(',', Enumerable.Repeat("public", QueryCommandRunner.MaxVisibilityFilterCsvEntries + 1));

        var options = QueryCommandRunner.ParseArgs(
            ["RunSearch", optionName, tooMany],
            jsonDefault: false,
            allowNamedQuery: true);

        Assert.Contains($"{optionName} accepts at most", options.ParseError);
        Assert.Empty(options.VisibilityFilters);
        Assert.Empty(options.ExcludeVisibilityFilters);
    }

    [Fact]
    public void ParseArgs_StatusCheckScopesRejectsOverlongCsv_Issue2913()
    {
        var tooLong = new string('w', QueryCommandRunner.MaxStatusCheckScopesCsvLength + 1);

        var options = QueryCommandRunner.ParseArgs(
            [$"--check={tooLong}"],
            jsonDefault: false,
            allowStatusCheck: true);

        Assert.True(options.CheckWorkspace);
        Assert.Contains("--check value is too long", options.ParseError);
        Assert.Null(options.StatusCheckScopes);
    }

    [Fact]
    public void ParseArgs_StatusCheckScopesRejectsTooManyCsvEntries_Issue2913()
    {
        var tooMany = string.Join(',', Enumerable.Repeat("workspace", QueryCommandRunner.MaxStatusCheckScopesCsvEntries + 1));

        var options = QueryCommandRunner.ParseArgs(
            [$"--check={tooMany}"],
            jsonDefault: false,
            allowStatusCheck: true);

        Assert.True(options.CheckWorkspace);
        Assert.Contains("--check accepts at most", options.ParseError);
        Assert.Null(options.StatusCheckScopes);
    }

    [Theory]
    [InlineData("--path")]
    [InlineData("--exclude-path")]
    public void ParseArgs_PathFiltersRejectTooManyValues_Issue2911(string optionName)
    {
        var args = new List<string> { "RunSearch" };
        for (var i = 0; i <= QueryCommandRunner.MaxQueryPathFilterCount; i++)
        {
            args.Add(optionName);
            args.Add("src/**");
        }

        var options = QueryCommandRunner.ParseArgs(
            args.ToArray(),
            jsonDefault: false,
            allowNamedQuery: true);

        Assert.Contains($"{optionName} accepts at most", options.ParseError);
    }

    [Theory]
    [InlineData("--path")]
    [InlineData("--exclude-path")]
    public void ParseArgs_PathFiltersRejectOverlongPattern_Issue2911(string optionName)
    {
        var tooLong = new string('a', QueryCommandRunner.MaxQueryPathFilterLength + 1);

        var options = QueryCommandRunner.ParseArgs(
            ["RunSearch", optionName, tooLong],
            jsonDefault: false,
            allowNamedQuery: true);

        Assert.Contains($"{optionName} value is too long", options.ParseError);
    }

    [Fact]
    public void ParseArgs_MapSectionsRejectsOverlongCsv_Issue2914()
    {
        var tooLong = new string('t', QueryCommandRunner.MaxMapSectionsCsvLength + 1);

        var options = QueryCommandRunner.ParseArgs(
            ["--sections", tooLong],
            jsonDefault: false,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);

        Assert.Contains("--sections value is too long", options.ParseError);
        Assert.Empty(options.MapSections!);
    }

    [Fact]
    public void ParseArgs_MapSectionsRejectsTooManyCsvEntries_Issue2914()
    {
        var tooMany = string.Join(',', Enumerable.Repeat("tree", QueryCommandRunner.MaxMapSectionsCsvEntries + 1));

        var options = QueryCommandRunner.ParseArgs(
            ["--sections", tooMany],
            jsonDefault: false,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);

        Assert.Contains("--sections accepts at most", options.ParseError);
        Assert.Empty(options.MapSections!);
    }
}
