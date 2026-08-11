using CodeIndex.Database;

namespace CodeIndex.Tests;

public sealed class CSharpBaseListParserTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("public class Plain {")]
    [InlineData("public class Generic<T> where T : System.Attribute {")]
    [InlineData("public record Generic<T>(T Value) where T : class;")]
    [InlineData("public record Generic(string Value = \"where: value\");")]
    public void Parse_DoesNotTreatNonBaseColonsAsBaseLists(string? signature)
    {
        var typeReferences = CSharpBaseListParser.Parse(
            signature,
            CSharpBaseListProjection.TypeReference);
        var headIdentifiers = CSharpBaseListParser.Parse(
            signature,
            CSharpBaseListProjection.HeadIdentifier);

        Assert.Empty(typeReferences);
        Assert.Empty(headIdentifiers);
    }

    [Fact]
    public void Parse_PreservesNestedCSharpTypeReferences()
    {
        const string Signature =
            "public sealed class Derived<T>(string value = \"where:,{}\") "
            + ": global::Demo.Base<(int Left, string Right)>(value), "
            + "IFoo<T[]>, Alias::IBar where T : class {";

        var result = CSharpBaseListParser.Parse(
            Signature,
            CSharpBaseListProjection.TypeReference);

        Assert.Equal(
        [
            "global::Demo.Base<(int Left, string Right)>(value)",
            "IFoo<T[]>",
            "Alias::IBar",
        ], result);
    }

    [Fact]
    public void Parse_ProjectsMetadataHeadIdentifiersWithoutChangingReaderEntries()
    {
        const string Signature =
            "public sealed class Derived<T>(T value) "
            + ": global::Demo.Base<(int Left, string Right)>(value), "
            + "IFoo<T[]>, Alias::IBar where T : class {";

        var result = CSharpBaseListParser.Parse(
            Signature,
            CSharpBaseListProjection.HeadIdentifier);

        Assert.Equal(["global::Demo.Base", "IFoo", "Alias::IBar"], result);
    }

    [Theory]
    [InlineData("public class Somewhere : Base", "Base")]
    [InlineData("public class Where : Base", "Base")]
    [InlineData("public class @where : Base", "Base")]
    [InlineData("public record Derived(int Value) : Base(Value);", "Base(Value)")]
    [InlineData("public class Derived(int Value = 1 < 2 ? 1 : 0) : Base(Value)", "Base(Value)")]
    [InlineData("[global::Demo.Marker(Name = \"where: value\")] public class Derived : global::Demo.Base", "global::Demo.Base")]
    public void Parse_RecognizesWhereOnlyAsTheConstraintKeyword(
        string signature,
        string expectedBase)
    {
        var result = CSharpBaseListParser.Parse(
            signature,
            CSharpBaseListProjection.TypeReference);

        Assert.Equal([expectedBase], result);
    }

    [Fact]
    public void Parse_SplitsOnlyTopLevelCommasAcrossMultilineDeclarations()
    {
        const string Signature = """
            public class Derived<T> :
                Base<Dictionary<string, (int Left, int Right)>>,
                IFoo<Action<int[], string>>
                where T : class
            {
            """;

        var result = CSharpBaseListParser.Parse(
            Signature,
            CSharpBaseListProjection.TypeReference);

        Assert.Equal(
        [
            "Base<Dictionary<string, (int Left, int Right)>>",
            "IFoo<Action<int[], string>>",
        ], result);
    }

    [Fact]
    public void Parse_IgnoresComparisonOperatorsInsideBaseConstructorArguments()
    {
        const string Signature =
            "public class Derived(int value, int limit) "
            + ": Base(value < limit ? value : limit), IFoo {";

        var result = CSharpBaseListParser.Parse(
            Signature,
            CSharpBaseListProjection.HeadIdentifier);

        Assert.Equal(["Base", "IFoo"], result);
    }
}
