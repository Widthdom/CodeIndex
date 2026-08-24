using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static void AddCSharpTopLevelScopeSymbol(
        long fileId,
        string[] lines,
        string[] structuralLines,
        SymbolExtractionList symbols)
    {
        if (structuralLines.Length == 0 || symbols.IsAtCapacity)
            return;

        // This runs after AssignContainers. Declaration ranges can therefore be excluded
        // without making source-declared local functions children of the synthetic scope.
        // AssignContainers 後に実行し、宣言 range を除外しつつ source-declared local
        // function の親を synthetic scope に変更しない。
        var declarationCoveredLines = BuildCSharpTopLevelDeclarationCoverage(
            structuralLines.Length,
            symbols);
        var firstExecutableLine = 0;
        var lastExecutableLine = 0;
        var inUsingDirective = false;
        var attributeBracketDepth = 0;

        for (var lineIndex = 0; lineIndex < structuralLines.Length; lineIndex++)
        {
            if (declarationCoveredLines[lineIndex])
                continue;
            if (IsCSharpLineCommentOnly(lines[lineIndex]))
                continue;

            var line = structuralLines[lineIndex].Trim();
            if (line.Length == 0)
                continue;

            if (attributeBracketDepth > 0)
            {
                attributeBracketDepth += CountCSharpBracketDelta(line);
                continue;
            }

            if (IsCSharpFileScopeAttributeStart(line))
            {
                attributeBracketDepth = Math.Max(0, CountCSharpBracketDelta(line));
                continue;
            }

            if (inUsingDirective)
            {
                if (line.Contains(';', StringComparison.Ordinal))
                    inUsingDirective = false;
                continue;
            }

            if (IsCSharpNonExecutableFileScopeLine(line, out var startsMultilineUsingDirective))
            {
                inUsingDirective = startsMultilineUsingDirective;
                continue;
            }

            var lineNumber = lineIndex + 1;
            if (firstExecutableLine == 0)
                firstExecutableLine = lineNumber;
            lastExecutableLine = lineNumber;
        }

        if (firstExecutableLine == 0)
            return;

        symbols.Add(new SymbolRecord
        {
            FileId = fileId,
            Kind = "function",
            SubKind = SyntheticSymbolIdentity.CSharpTopLevelScopeSubKind,
            Name = SyntheticSymbolIdentity.CSharpTopLevelScopeName,
            Line = firstExecutableLine,
            StartLine = firstExecutableLine,
            EndLine = lastExecutableLine,
            BodyStartLine = firstExecutableLine,
            BodyEndLine = lastExecutableLine,
            Signature = SyntheticSymbolIdentity.CSharpTopLevelScopeName,
        });
    }

    private static bool[] BuildCSharpTopLevelDeclarationCoverage(
        int lineCount,
        IReadOnlyList<SymbolRecord> symbols)
    {
        var covered = new bool[lineCount];
        foreach (var symbol in symbols)
        {
            if (!IsCSharpFileScopeDeclarationForTopLevelDetection(symbol))
                continue;

            var startLine = Math.Clamp(symbol.StartLine > 0 ? symbol.StartLine : symbol.Line, 1, lineCount);
            var endLine = Math.Clamp(symbol.EndLine >= startLine ? symbol.EndLine : startLine, startLine, lineCount);
            for (var lineNumber = startLine; lineNumber <= endLine; lineNumber++)
                covered[lineNumber - 1] = true;
        }

        return covered;
    }

    private static bool IsCSharpFileScopeDeclarationForTopLevelDetection(SymbolRecord symbol)
    {
        if (symbol.Kind is "namespace"
            or "class"
            or "struct"
            or "interface"
            or "enum"
            or "record"
            or "delegate")
        {
            return true;
        }

        if (symbol.ContainerName != null)
            return false;

        return symbol.Kind is "function"
            or "test.method"
            or "constructor"
            or "operator";
    }

    private static bool IsCSharpNonExecutableFileScopeLine(
        string line,
        out bool startsMultilineUsingDirective)
    {
        startsMultilineUsingDirective = false;
        if (line.StartsWith('#'))
            return true;
        if (line.StartsWith("extern alias ", StringComparison.Ordinal))
            return true;
        if (line.StartsWith("global using ", StringComparison.Ordinal))
        {
            startsMultilineUsingDirective = !line.Contains(';', StringComparison.Ordinal);
            return true;
        }
        if (!line.StartsWith("using ", StringComparison.Ordinal))
            return false;
        if (IsCSharpUsingDeclaration(line))
        {
            return false;
        }

        startsMultilineUsingDirective = !line.Contains(';', StringComparison.Ordinal);
        return true;
    }

    private static bool IsCSharpUsingDeclaration(string line)
    {
        if (line.StartsWith("using var ", StringComparison.Ordinal)
            || line.StartsWith("using (", StringComparison.Ordinal))
        {
            return true;
        }

        var equalsIndex = line.IndexOf('=');
        if (equalsIndex < 0)
            return false;

        var declarationPrefix = line["using ".Length..equalsIndex].Trim();
        if (declarationPrefix.StartsWith("unsafe ", StringComparison.Ordinal))
            declarationPrefix = declarationPrefix["unsafe ".Length..].TrimStart();

        // An alias directive has one identifier before '=', while an explicit using
        // declaration has a type and variable name. Structural masking has already
        // replaced comments with whitespace before this classification.
        // alias directive の '=' より前は識別子 1 個だが、明示型 using declaration
        // には型と変数名がある。comment はこの判定前に空白へ mask 済み。
        return declarationPrefix.AsSpan().IndexOfAny(' ', '\t') >= 0;
    }

    private static bool IsCSharpFileScopeAttributeStart(string line)
        => line.StartsWith('[');

    private static int CountCSharpBracketDelta(string line)
    {
        var delta = 0;
        foreach (var ch in line)
        {
            if (ch == '[')
                delta++;
            else if (ch == ']')
                delta--;
        }
        return delta;
    }
}
