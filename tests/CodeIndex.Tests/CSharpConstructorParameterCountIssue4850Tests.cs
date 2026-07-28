using CodeIndex.Indexer;

namespace CodeIndex.Tests;

public sealed class CSharpConstructorParameterCountIssue4850Tests
{
    [Theory]
    [InlineData("public Widget()", "Widget", "function", 0)]
    [InlineData("public Widget(Dictionary<string, int> value, string text = \"x,y\")", "Widget", "function", 2)]
    [InlineData("public class Widget(int value)", "Widget", "class", 1)]
    [InlineData("public record Packet<T>(T Value, string Name)", "Packet", "record", 2)]
    public void GetConstructorParameterCount_ReturnsInstanceAndPrimaryArities_Issue4850(
        string signature,
        string name,
        string kind,
        int expected)
    {
        Assert.Equal(
            expected,
            CSharpTypeReferenceArity.GetConstructorParameterCount(signature, name, kind));
    }

    [Theory]
    [InlineData("static Widget()")]
    [InlineData("~Widget()")]
    [InlineData("public class Widget")]
    public void GetConstructorParameterCount_RejectsNonInstanceConstructorTargets_Issue4850(
        string signature)
    {
        Assert.Null(
            CSharpTypeReferenceArity.GetConstructorParameterCount(
                signature,
                "Widget",
                signature.Contains("class", StringComparison.Ordinal) ? "class" : "function"));
    }
}
