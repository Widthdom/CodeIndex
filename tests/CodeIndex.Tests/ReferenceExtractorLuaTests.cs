using CodeIndex.Indexer;

namespace CodeIndex.Tests;

public class ReferenceExtractorLuaTests
{
    [Fact]
    public void MaskLongCommentAndStringLines_UsesCopyOnWrite()
    {
        var lines = new[]
        {
            "local value = table[index]",
            "return module:run(value)",
        };

        var masked = LuaReferenceExtractor.MaskLongCommentAndStringLines(lines);

        Assert.Same(lines, masked);
        var longTextLines = new[]
        {
            "local value = [[",
            "Phantom.call()",
            "]]",
        };

        var maskedLongText = LuaReferenceExtractor.MaskLongCommentAndStringLines(longTextLines);

        Assert.NotSame(longTextLines, maskedLongText);
        Assert.DoesNotContain("Phantom", maskedLongText[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_Lua_EmitsColonCallsAndTableFieldReferences()
    {
        const string content = """
            local M = {}

            function M.work(self, x, ...)
                local value = self.runner.status
                return self.runner:run(x, ...)
            end
            """;

        var symbols = SymbolExtractor.Extract(1, "lua", content);
        var references = ReferenceExtractor.Extract(1, "lua", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "run"
            && reference.ReferenceKind == "call");
        Assert.Contains(references, reference =>
            reference.SymbolName == "status"
            && reference.ReferenceKind == "reference");
    }
}
