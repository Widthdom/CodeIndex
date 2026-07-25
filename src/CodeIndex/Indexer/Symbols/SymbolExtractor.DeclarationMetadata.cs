using System.Text.RegularExpressions;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static void ExtractCppFriendDeclarationSymbols(
        long fileId,
        string[] lines,
        List<SymbolRecord> symbols,
        SymbolExtractionState extractionState)
    {
        if (!LinesContain(lines, "friend", StringComparison.Ordinal))
            return;

        var declared = BuildSymbolKindNameIdentities(symbols);
        var inBlockComment = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!inBlockComment
                && line.IndexOf("friend", StringComparison.Ordinal) < 0
                && line.IndexOf('/') < 0)
            {
                continue;
            }

            var matchLine = MaskCppFriendDeclarationLine(line, ref inBlockComment);
            if (matchLine.IndexOf("friend", StringComparison.Ordinal) < 0)
                continue;

            var lineNumber = i + 1;

            foreach (Match match in CppFriendTypeDeclarationRegex.Matches(matchLine))
            {
                var kind = NormalizeCppFriendTypeKind(match.Groups["kind"].Value);
                var group = match.Groups["name"];
                var name = LastCppDeclarationSegment(group.Value);
                AddCppFriendDeclarationSymbol(fileId, symbols, extractionState, declared, kind, name, lineNumber, group.Index, line);
            }

            foreach (Match match in CppFriendFunctionDeclarationRegex.Matches(matchLine))
            {
                var group = match.Groups["name"];
                var name = LastCppDeclarationSegment(group.Value);
                AddCppFriendDeclarationSymbol(fileId, symbols, extractionState, declared, "function", name, lineNumber, group.Index, line);
            }
        }
    }

    private static string MaskCppFriendDeclarationLine(string line, ref bool inBlockComment)
    {
        char[]? chars = null;

        void MaskAt(int index) =>
            (chars ??= line.ToCharArray())[index] = ' ';

        void MaskToEnd(int start)
        {
            var masked = chars ??= line.ToCharArray();
            for (var index = start; index < line.Length; index++)
                masked[index] = ' ';
        }

        for (var cursor = 0; cursor < line.Length; cursor++)
        {
            if (inBlockComment)
            {
                MaskAt(cursor);
                if (cursor + 1 < line.Length && line[cursor] == '*' && line[cursor + 1] == '/')
                {
                    MaskAt(++cursor);
                    inBlockComment = false;
                }

                continue;
            }

            if (cursor + 1 < line.Length && line[cursor] == '/' && line[cursor + 1] == '/')
            {
                MaskToEnd(cursor);
                break;
            }

            if (cursor + 1 < line.Length && line[cursor] == '/' && line[cursor + 1] == '*')
            {
                MaskAt(cursor++);
                MaskAt(cursor);
                inBlockComment = true;
                continue;
            }

            if (line[cursor] is '"' or '\'')
            {
                var quote = line[cursor];
                MaskAt(cursor++);
                while (cursor < line.Length)
                {
                    if (line[cursor] == '\\' && cursor + 1 < line.Length)
                    {
                        MaskAt(cursor++);
                        MaskAt(cursor);
                        cursor++;
                        continue;
                    }

                    var closes = line[cursor] == quote;
                    MaskAt(cursor++);
                    if (closes)
                        break;
                }

                cursor--;
            }
        }

        return chars is null ? line : new string(chars);
    }

    private static bool IsCSharpTestMethod(string[] lines, int declarationLineIndex)
    {
        var scannedAttributeLine = false;
        for (var lineIndex = declarationLineIndex; lineIndex >= 0; lineIndex--)
        {
            var trimmed = lines[lineIndex].TrimStart();
            if (trimmed.Length == 0)
                return false;

            if (!trimmed.StartsWith('['))
            {
                if (lineIndex == declarationLineIndex && !scannedAttributeLine)
                    continue;

                return false;
            }

            scannedAttributeLine = true;
            if (CSharpLineHasTestMethodAttribute(trimmed))
                return true;

            var remainderIndex = trimmed.LastIndexOf(']');
            if (remainderIndex < 0)
                return false;

            var remainder = trimmed[(remainderIndex + 1)..].TrimStart();
            if (remainder.Length > 0)
                return false;
        }

        return false;
    }

    private static bool CSharpLineHasTestMethodAttribute(string trimmedLine)
    {
        var cursor = 0;
        while (cursor < trimmedLine.Length && trimmedLine[cursor] == '[')
        {
            var closeIndex = trimmedLine.IndexOf(']', cursor + 1);
            if (closeIndex < 0)
                return false;

            var content = trimmedLine[(cursor + 1)..closeIndex];
            if (CSharpTestMethodAttributeRegex.IsMatch(content))
                return true;

            cursor = closeIndex + 1;
            while (cursor < trimmedLine.Length && char.IsWhiteSpace(trimmedLine[cursor]))
                cursor++;
        }

        return false;
    }

    private static void AddCppFriendDeclarationSymbol(
        long fileId,
        List<SymbolRecord> symbols,
        SymbolExtractionState extractionState,
        HashSet<SymbolKindNameIdentity> declared,
        string kind,
        string name,
        int lineNumber,
        int startColumn,
        string line)
    {
        if (name.Length == 0 || !declared.Add(new SymbolKindNameIdentity(kind, name)))
            return;

        AddSymbolRecord(
            symbols,
            extractionState,
            cssSeenSymbols: null,
            lineNumber,
            new SymbolRecord
            {
                FileId = fileId,
                Kind = kind,
                Name = name,
                Line = lineNumber,
                StartLine = lineNumber,
                StartColumn = startColumn,
                EndLine = lineNumber,
                Signature = line.Trim(),
            },
            line);
    }

    private static string NormalizeCppFriendTypeKind(string kind)
        => kind.StartsWith("enum", StringComparison.Ordinal) ? "enum" : kind;

    private static string LastCppDeclarationSegment(string value)
    {
        var text = value.Trim();
        var qualifierIndex = text.LastIndexOf("::", StringComparison.Ordinal);
        var leaf = qualifierIndex >= 0 ? text.AsSpan(qualifierIndex + 2).Trim() : text.AsSpan();
        if (!leaf.StartsWith("operator".AsSpan(), StringComparison.Ordinal))
        {
            var genericIndex = text.IndexOf('<');
            if (genericIndex >= 0)
                text = text[..genericIndex].TrimEnd();
        }

        return qualifierIndex >= 0 ? text[(qualifierIndex + 2)..].Trim() : text;
    }
}
