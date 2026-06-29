using System.Text.Json;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

public sealed class DiffCommandHelpersTests
{
    [Fact]
    public void Parse_RejectsUnsupportedOption_Issue4181()
    {
        var options = DiffCommandOptionsParser.Parse(["left.db", "right.db", "--bogus"], maxLimit: 25);

        Assert.Equal("left.db", options.LeftDb);
        Assert.Equal("right.db", options.RightDb);
        Assert.Equal("diff does not support option: '--bogus'", options.ParseError);
    }

    [Fact]
    public void Parse_LimitAboveBoundResetsToDefault_Issue4181()
    {
        var options = DiffCommandOptionsParser.Parse(["left.db", "right.db", "--limit", "26"], maxLimit: 25);

        Assert.Equal(DiffCommandOptionsParser.DefaultLimit, options.Limit);
        Assert.Equal("--limit must be less than or equal to 25", options.ParseError);
    }

    [Fact]
    public void WriteResult_SummaryOnlyOmitsDiffSamples_Issue4181()
    {
        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var diffOptions = new DiffCommandOptions { SummaryOnly = true, Limit = 5 };
        var result = CreateDiffResult();

        var output = CaptureStdout(() => DiffResultWriter.WriteResult(result, diffOptions, jsonOptions));

        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        Assert.Equal("different", root.GetProperty("status").GetString());
        Assert.Equal("left.db", root.GetProperty("left_db").GetString());
        Assert.Equal("right.db", root.GetProperty("right_db").GetString());
        Assert.False(root.TryGetProperty("files_only_in_left", out _));
        Assert.False(root.TryGetProperty("files_only_in_right", out _));
    }

    [Theory]
    [InlineData(0, "+0")]
    [InlineData(3, "+3")]
    [InlineData(-2, "-2")]
    public void FormatDelta_UsesDiffTextContract_Issue4181(long delta, string expected)
        => Assert.Equal(expected, DiffResultWriter.FormatDelta(delta));

    private static DiffJsonResult CreateDiffResult()
        => new(
            "different",
            false,
            "left.db",
            "right.db",
            new DiffSummaryJsonResult(
                1,
                2,
                1,
                3,
                3,
                0,
                5,
                4,
                -1,
                8,
                8,
                true),
            ["src/Left.cs"],
            ["src/Right.cs"],
            null,
            null,
            5,
            false);

    private static string CaptureStdout(Action action)
    {
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        lock (TestConsoleLock.Gate)
        {
            try
            {
                Console.SetOut(writer);
                action();
                return writer.ToString();
            }
            finally
            {
                Console.SetOut(originalOut);
            }
        }
    }
}
