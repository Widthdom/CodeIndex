using CodeIndex.Indexer;

namespace CodeIndex.Tests;

public partial class ReferenceExtractorTests
{
    [Fact]
    public void Extract_BuiltInReferencePaths_PreserveTrimmedContextAndPhysicalColumn()
    {
        var fixtures = new[]
        {
            (
                Language: "csharp",
                Content: "class Sample\n{\n    void Run()\n    {\n        Target();    \n    }\n}",
                SymbolName: "Target",
                ReferenceKind: "call"),
            (
                Language: "clojure",
                Content: "(defn run []\n    (fetch item)    \n)",
                SymbolName: "fetch",
                ReferenceKind: "call"),
            (
                Language: "solidity",
                Content: "contract Sample {\n    event Changed();\n    function run() external {\n        emit Changed();    \n    }\n}",
                SymbolName: "Changed",
                ReferenceKind: "call"),
        };

        foreach (var fixture in fixtures)
        {
            var symbols = SymbolExtractor.Extract(
                1,
                fixture.Language,
                fixture.Content);
            var references = ReferenceExtractor.Extract(
                1,
                fixture.Language,
                fixture.Content,
                symbols);
            var reference = Assert.Single(references.Where(candidate =>
                candidate.SymbolName == fixture.SymbolName
                && candidate.ReferenceKind == fixture.ReferenceKind));
            var sourceLine = fixture.Content
                .Split('\n')[reference.Line - 1];

            Assert.Equal(sourceLine.Trim(), reference.Context);
            Assert.Equal(
                sourceLine.IndexOf(fixture.SymbolName, StringComparison.Ordinal)
                    + 1,
                reference.Column);
        }
    }

    [Fact]
    public void Extract_CSharpPendingTypePatternAtEndOfFile_NormalizesDeferredContext()
    {
        const string content = "class Point {}\nclass Sample\n{\n    bool Match(object value) => value is\n        Point    ";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var references = ReferenceExtractor.Extract(
            1,
            "csharp",
            content,
            symbols);

        var reference = Assert.Single(references.Where(candidate =>
            candidate.SymbolName == "Point"
            && candidate.ReferenceKind == "type_reference"));
        Assert.Equal("Point", reference.Context);
        Assert.Equal(9, reference.Column);
    }
}
