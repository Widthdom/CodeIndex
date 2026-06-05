using System.Reflection;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

public class QueryCommandRunnerMaxLineWidthTests
{
    private static readonly MethodInfo TryParsePositiveInt = typeof(QueryCommandRunner).GetMethod(
        "TryParsePositiveInt",
        BindingFlags.NonPublic | BindingFlags.Static)!;

    [Fact]
    public void TryParsePositiveInt_AllowsZeroForMaxLineWidth()
    {
        object?[] args =
        [
            "0",
            "--max-line-width",
            123,
            "placeholder",
            null,
        ];

        var result = (bool)TryParsePositiveInt.Invoke(null, args)!;

        Assert.True(result);
        Assert.Equal(0, args[2]);
        Assert.Null(args[3]);
    }

    [Fact]
    public void TryParsePositiveInt_StillRejectsZeroForLimit()
    {
        object?[] args =
        [
            "0",
            "--limit",
            123,
            "placeholder",
            null,
        ];

        var result = (bool)TryParsePositiveInt.Invoke(null, args)!;

        Assert.False(result);
        Assert.Equal(0, args[2]);
        Assert.NotNull(args[3]);
    }
}
