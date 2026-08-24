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
            symbols,
            out var sameLineDeclarationsByLine,
            out var nonSameLineDeclarationCoveredLines);
        var firstExecutableLine = 0;
        var lastExecutableLine = 0;
        var inUsingDirective = false;
        var attributeBracketDepth = 0;

        for (var lineIndex = 0; lineIndex < structuralLines.Length; lineIndex++)
        {
            if (declarationCoveredLines[lineIndex]
                && (nonSameLineDeclarationCoveredLines[lineIndex]
                    || sameLineDeclarationsByLine == null
                    || !sameLineDeclarationsByLine.TryGetValue(lineIndex + 1, out var sameLineDeclarations)
                    || !HasCSharpExecutableTextOutsideSameLineDeclarations(
                        structuralLines[lineIndex],
                        sameLineDeclarations)))
            {
                continue;
            }
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
        IReadOnlyList<SymbolRecord> symbols,
        out Dictionary<int, List<SymbolRecord>>? sameLineDeclarationsByLine,
        out bool[] nonSameLineDeclarationCoveredLines)
    {
        var covered = new bool[lineCount];
        nonSameLineDeclarationCoveredLines = new bool[lineCount];
        sameLineDeclarationsByLine = null;
        foreach (var symbol in symbols)
        {
            if (!IsCSharpFileScopeDeclarationForTopLevelDetection(symbol))
                continue;

            var startLine = Math.Clamp(symbol.StartLine > 0 ? symbol.StartLine : symbol.Line, 1, lineCount);
            var endLine = Math.Clamp(symbol.EndLine >= startLine ? symbol.EndLine : startLine, startLine, lineCount);
            for (var lineNumber = startLine; lineNumber <= endLine; lineNumber++)
                covered[lineNumber - 1] = true;

            if (startLine == endLine && !string.IsNullOrWhiteSpace(symbol.Signature))
            {
                sameLineDeclarationsByLine ??= [];
                if (!sameLineDeclarationsByLine.TryGetValue(startLine, out var declarations))
                {
                    declarations = [];
                    sameLineDeclarationsByLine.Add(startLine, declarations);
                }
                declarations.Add(symbol);
            }
            else
            {
                for (var lineNumber = startLine; lineNumber <= endLine; lineNumber++)
                    nonSameLineDeclarationCoveredLines[lineNumber - 1] = true;
            }
        }

        return covered;
    }

    private static bool HasCSharpExecutableTextOutsideSameLineDeclarations(
        string structuralLine,
        IReadOnlyList<SymbolRecord> declarations)
    {
        var uncovered = structuralLine.ToCharArray();
        foreach (var symbol in declarations)
        {
            var signature = symbol.Signature!;
            var signatureIndex = symbol.StartColumn
                ?? FindCSharpSameLineSignatureStart(structuralLine, symbol.Name, signature);
            if (signatureIndex < 0 || signatureIndex + signature.Length > uncovered.Length)
                return false;

            Array.Fill(uncovered, ' ', signatureIndex, signature.Length);
        }

        return uncovered.Any(static ch => !char.IsWhiteSpace(ch));
    }

    private static int FindCSharpSameLineSignatureStart(
        string structuralLine,
        string symbolName,
        string signature)
    {
        var exactSignatureIndex = structuralLine.IndexOf(signature, StringComparison.Ordinal);
        if (exactSignatureIndex >= 0)
            return exactSignatureIndex;

        var nameOffset = signature.IndexOf(symbolName, StringComparison.Ordinal);
        if (nameOffset < 0)
            return -1;

        var searchStart = 0;
        while (searchStart < structuralLine.Length)
        {
            var nameIndex = structuralLine.IndexOf(symbolName, searchStart, StringComparison.Ordinal);
            if (nameIndex < 0)
                return -1;

            var beforeIsIdentifier = nameIndex > 0
                && IsCSharpIdentifierCharacter(structuralLine[nameIndex - 1]);
            var afterIndex = nameIndex + symbolName.Length;
            var afterIsIdentifier = afterIndex < structuralLine.Length
                && IsCSharpIdentifierCharacter(structuralLine[afterIndex]);
            if (!beforeIsIdentifier && !afterIsIdentifier && nameIndex >= nameOffset)
                return nameIndex - nameOffset;

            searchStart = nameIndex + symbolName.Length;
        }

        return -1;
    }

    private static bool IsCSharpIdentifierCharacter(char value)
        => value == '_' || value == '@' || value == '\\' || char.IsLetterOrDigit(value);

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
        var externEnd = GetCSharpLeadingKeywordEnd(line, "extern");
        if (externEnd >= 0
            && GetCSharpLeadingKeywordEnd(line[externEnd..].TrimStart(), "alias") >= 0)
        {
            return true;
        }

        var globalEnd = GetCSharpLeadingKeywordEnd(line, "global");
        if (globalEnd >= 0
            && GetCSharpLeadingKeywordEnd(line[globalEnd..].TrimStart(), "using", allowOpeningParen: true) >= 0)
        {
            startsMultilineUsingDirective = !line.Contains(';', StringComparison.Ordinal);
            return true;
        }

        var usingEnd = GetCSharpLeadingKeywordEnd(line, "using", allowOpeningParen: true);
        if (usingEnd < 0)
            return false;
        if (IsCSharpUsingDeclaration(line[usingEnd..].TrimStart()))
        {
            return false;
        }

        startsMultilineUsingDirective = !line.Contains(';', StringComparison.Ordinal);
        return true;
    }

    private static bool IsCSharpUsingDeclaration(string remainder)
    {
        if (remainder.StartsWith('(')
            || GetCSharpLeadingKeywordEnd(remainder, "var") >= 0)
        {
            return true;
        }

        var equalsIndex = remainder.IndexOf('=');
        if (equalsIndex < 0)
            return false;

        var declarationPrefix = remainder[..equalsIndex].Trim();
        var unsafeEnd = GetCSharpLeadingKeywordEnd(declarationPrefix, "unsafe");
        if (unsafeEnd >= 0)
            declarationPrefix = declarationPrefix[unsafeEnd..].TrimStart();

        // An alias directive has one identifier before '=', while an explicit using
        // declaration has a type and variable name. Structural masking has already
        // replaced comments with whitespace before this classification.
        // alias directive の '=' より前は識別子 1 個だが、明示型 using declaration
        // には型と変数名がある。comment はこの判定前に空白へ mask 済み。
        return declarationPrefix.AsSpan().IndexOfAny(' ', '\t') >= 0;
    }

    private static int GetCSharpLeadingKeywordEnd(
        string line,
        string keyword,
        bool allowOpeningParen = false)
    {
        if (!line.StartsWith(keyword, StringComparison.Ordinal))
            return -1;
        if (line.Length == keyword.Length)
            return keyword.Length;

        var boundary = line[keyword.Length];
        return char.IsWhiteSpace(boundary) || (allowOpeningParen && boundary == '(')
            ? keyword.Length
            : -1;
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
