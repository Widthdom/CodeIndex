using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using System.Runtime.CompilerServices;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static void ExtractJavaModuleDirectiveSymbols(long fileId, string[] rawLines, string[] structuralLines, List<SymbolRecord> symbols)
    {
        var moduleDeclarations = symbols
            .Where(symbol => symbol.Kind == "namespace" && symbol.BodyStartLine != null && symbol.BodyEndLine != null)
            .OrderBy(symbol => symbol.StartLine)
            .ThenByDescending(symbol => symbol.EndLine)
            .ToList();

        foreach (var moduleDeclaration in moduleDeclarations)
        {
            foreach (var statement in EnumerateJavaModuleDirectiveStatements(rawLines, structuralLines, moduleDeclaration))
            {
                if (!TryParseJavaModuleDirectiveName(statement.StructuralText, out var name))
                    continue;

                AddSymbolRecord(
                    symbols,
                    cssSeenSymbols: null,
                    statement.StartLine,
                    new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = "import",
                        Name = name,
                        Line = statement.StartLine,
                        StartLine = statement.StartLine,
                        StartColumn = statement.StartColumn,
                        EndLine = statement.EndLine,
                        Signature = statement.Signature,
                    },
                    rawLines[statement.StartLine - 1]);
            }
        }
    }

    private static IEnumerable<JavaModuleDirectiveStatement> EnumerateJavaModuleDirectiveStatements(
        string[] rawLines,
        string[] structuralLines,
        SymbolRecord moduleDeclaration)
    {
        var bodyStartLine = moduleDeclaration.BodyStartLine.GetValueOrDefault();
        var bodyEndLine = moduleDeclaration.BodyEndLine.GetValueOrDefault();
        if (bodyStartLine <= 0 || bodyEndLine < bodyStartLine)
            yield break;

        var startLineIndex = bodyStartLine - 1;
        var endLineIndex = Math.Min(bodyEndLine, rawLines.Length) - 1;
        if (startLineIndex < 0 || startLineIndex >= rawLines.Length || endLineIndex < startLineIndex)
            yield break;

        var rawBuilder = new StringBuilder();
        var statementStartLine = -1;
        var statementStartColumn = -1;

        for (var lineIndex = startLineIndex; lineIndex <= endLineIndex; lineIndex++)
        {
            var rawLine = rawLines[lineIndex];
            var structuralLine = structuralLines[lineIndex];
            var sliceStart = 0;
            var sliceEnd = rawLine.Length;

            if (lineIndex == startLineIndex)
            {
                var openingBrace = structuralLine.IndexOf('{');
                if (openingBrace >= 0)
                    sliceStart = Math.Min(openingBrace + 1, rawLine.Length);
            }

            if (lineIndex == endLineIndex && bodyEndLine == moduleDeclaration.EndLine)
            {
                var closingBrace = structuralLine.LastIndexOf('}');
                if (closingBrace >= 0)
                    sliceEnd = Math.Min(closingBrace, rawLine.Length);
            }

            if (sliceStart >= sliceEnd)
                continue;

            var rawSlice = rawLine[sliceStart..sliceEnd];
            var structuralSlice = structuralLine[sliceStart..sliceEnd];
            var offset = 0;
            while (offset < structuralSlice.Length)
            {
                if (rawBuilder.Length == 0)
                {
                    offset = SkipWhitespace(structuralSlice, offset);
                    if (offset >= structuralSlice.Length)
                        break;

                    if (!TryGetJavaModuleDirectiveKeyword(structuralSlice, offset, out _))
                        break;

                    statementStartLine = lineIndex + 1;
                    statementStartColumn = sliceStart + offset;
                }

                var semicolonIndex = structuralSlice.IndexOf(';', offset);
                var segmentEnd = semicolonIndex >= 0
                    ? semicolonIndex + 1
                    : structuralSlice.Length;
                rawBuilder.Append(rawSlice, offset, segmentEnd - offset);

                if (semicolonIndex >= 0)
                {
                    var structuralText = CollapseWhitespaceRuns(MaskJavaModuleDirectiveComments(rawBuilder.ToString()));
                    yield return new JavaModuleDirectiveStatement(
                        statementStartLine,
                        statementStartColumn,
                        lineIndex + 1,
                        NormalizeJavaModuleDirectiveSignature(rawBuilder.ToString()),
                        structuralText);
                    rawBuilder.Clear();
                    statementStartLine = -1;
                    statementStartColumn = -1;
                    offset = semicolonIndex + 1;
                    continue;
                }

                rawBuilder.Append('\n');
                break;
            }
        }
    }

    private static bool TryParseJavaModuleDirectiveName(string statement, out string name)
    {
        name = string.Empty;
        var match = JavaModuleRequiresDirectiveRegex.Match(statement);
        if (!match.Success)
            match = JavaModuleExportsOrOpensDirectiveRegex.Match(statement);
        if (!match.Success)
            match = JavaModuleUsesOrProvidesDirectiveRegex.Match(statement);
        if (!match.Success)
            return false;

        name = match.Groups["name"].Value.Trim();
        return name.Length > 0;
    }

    private static bool TryGetJavaModuleDirectiveKeyword(string line, int offset, out string keyword)
    {
        foreach (var candidate in JavaModuleDirectiveKeywords)
        {
            if (!line.AsSpan(offset).StartsWith(candidate, StringComparison.Ordinal))
                continue;

            var boundaryIndex = offset + candidate.Length;
            if (boundaryIndex < line.Length && (char.IsLetterOrDigit(line[boundaryIndex]) || line[boundaryIndex] == '_'))
                continue;

            keyword = candidate;
            return true;
        }

        keyword = string.Empty;
        return false;
    }

    private static string NormalizeJavaModuleDirectiveSignature(string statement)
    {
        return CollapseWhitespaceRuns(statement);
    }

    private static string MaskJavaModuleDirectiveComments(string text)
    {
        if (text.Length == 0)
            return string.Empty;

        var builder = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '/' && index + 1 < text.Length)
            {
                if (text[index + 1] == '/')
                {
                    if (builder.Length > 0 && !char.IsWhiteSpace(builder[^1]))
                        builder.Append(' ');

                    index += 2;
                    while (index < text.Length && text[index] != '\n')
                        index++;
                    if (index < text.Length && text[index] == '\n')
                        builder.Append('\n');
                    continue;
                }

                if (text[index + 1] == '*')
                {
                    if (builder.Length > 0 && !char.IsWhiteSpace(builder[^1]))
                        builder.Append(' ');

                    index += 2;
                    while (index < text.Length)
                    {
                        if (text[index] == '\n')
                            builder.Append('\n');

                        if (text[index] == '*' && index + 1 < text.Length && text[index + 1] == '/')
                        {
                            index++;
                            break;
                        }

                        index++;
                    }
                    continue;
                }
            }

            builder.Append(text[index]);
        }

        return builder.ToString();
    }

    private static string CollapseWhitespaceRuns(string text)
    {
        if (text.Length == 0)
            return string.Empty;

        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch);
        }

        return builder.ToString().Trim();
    }

    private readonly record struct JavaModuleDirectiveStatement(
        int StartLine,
        int StartColumn,
        int EndLine,
        string Signature,
        string StructuralText);

    private static void ExtractJavaEnumMembers(long fileId, string[] rawLines, List<SymbolRecord> symbols)
    {
        // Snapshot enum declarations first — we mutate the list during iteration.
        // 反復中に list を書き換えるため、先に enum 宣言を snapshot しておく。
        var enumDeclarations = symbols
            .Where(s => s.Kind == "enum" && s.BodyStartLine != null && s.BodyEndLine != null)
            .OrderBy(s => s.StartLine)
            .ThenByDescending(s => s.EndLine)
            .ToList();

        foreach (var enumSymbol in enumDeclarations)
        {
            if (!TryFindJavaEnumBodyBounds(rawLines, enumSymbol, out var bodyStartLineIndex, out var bodyStartColumn, out var bodyEndLineIndex, out var bodyEndColumnExclusive))
                continue;

            ExtractJavaEnumMembersFromBody(
                fileId,
                enumSymbol,
                rawLines,
                bodyStartLineIndex,
                bodyStartColumn,
                bodyEndLineIndex,
                bodyEndColumnExclusive,
                symbols);
        }
    }

    private static void ExtractJavaCompactConstructors(long fileId, string[] rawLines, List<SymbolRecord> symbols)
    {
        var recordDeclarations = symbols
            .Where(symbol =>
                symbol.FileId == fileId
                && symbol.Kind == "class"
                && symbol.BodyStartLine != null
                && symbol.BodyEndLine != null
                && IsJavaRecordSymbol(rawLines, symbol))
            .OrderBy(symbol => symbol.StartLine)
            .ThenByDescending(symbol => symbol.EndLine)
            .ToList();

        foreach (var recordSymbol in recordDeclarations)
        {
            if (!TryFindJavaSymbolBodyBounds(rawLines, recordSymbol, out var bodyStartLineIndex, out var bodyStartColumn, out var bodyEndLineIndex, out var bodyEndColumnExclusive))
                continue;

            var mode = JavaScanMode.Normal;
            var braceDepth = 0;
            for (int i = bodyStartLineIndex; i <= bodyEndLineIndex; i++)
            {
                if (mode == JavaScanMode.LineComment)
                    mode = JavaScanMode.Normal;

                var line = rawLines[i];
                var segmentStart = i == bodyStartLineIndex
                    ? Math.Min(bodyStartColumn, line.Length)
                    : 0;
                var segmentEndExclusive = i == bodyEndLineIndex
                    ? Math.Min(bodyEndColumnExclusive, line.Length)
                    : line.Length;
                var lineStartBraceDepth = braceDepth;
                var lineStartMode = mode;

                if (lineStartBraceDepth == 0
                    && lineStartMode == JavaScanMode.Normal
                    && segmentStart < segmentEndExclusive)
                {
                    var segment = line[segmentStart..segmentEndExclusive];
                    var compactConstructorOffset = 0;
                    while (compactConstructorOffset >= 0 && compactConstructorOffset < segment.Length)
                    {
                        var candidateSegment = segment[compactConstructorOffset..];
                        if (TryMatchJavaDeclarationSegment(JavaCompactConstructorRegex, candidateSegment, false, out var match, out var javaLeadingAnnotationOffset)
                            && match.Groups["name"].Value == recordSymbol.Name)
                        {
                            var absoluteStartColumn = segmentStart + compactConstructorOffset + javaLeadingAnnotationOffset + match.Index;
                            var visibility = TryGetGroup(match, "visibility");
                            var (endLine, bodyStartLine, bodyEndLine) = ResolveRange(rawLines, i, BodyStyle.Brace, "java", absoluteStartColumn);
                            var sameLineEndColumn = bodyEndLine == i + 1
                                ? FindSameLineBraceEndColumn(line, absoluteStartColumn, "java", "function")
                                : -1;
                            var existingSymbols = symbols
                                .Where(symbol =>
                                    symbol.FileId == fileId
                                    && symbol.Kind == "function"
                                    && symbol.Name == recordSymbol.Name
                                    && symbol.StartLine == i + 1
                                    && (symbol.ContainerName == null || symbol.ContainerName == recordSymbol.Name)
                                    && (symbol.ContainerKind == null || symbol.ContainerKind == "class"))
                                .ToList();
                            foreach (var existingSymbol in existingSymbols)
                            {
                                if (LooksLikeJavaCompactConstructorSymbol(existingSymbol, recordSymbol.Name))
                                    continue;
                                symbols.Remove(existingSymbol);
                            }

                            if (!symbols.Any(symbol => LooksLikeJavaCompactConstructorSymbol(symbol, recordSymbol.Name)
                                    && symbol.FileId == fileId
                                    && symbol.StartLine == i + 1))
                            {
                                symbols.Add(new SymbolRecord
                                {
                                    FileId = fileId,
                                    Kind = "function",
                                    Name = recordSymbol.Name,
                                    Line = i + 1,
                                    StartLine = i + 1,
                                    StartColumn = absoluteStartColumn,
                                    EndLine = Math.Max(i + 1, endLine),
                                    BodyStartLine = bodyStartLine,
                                    BodyEndLine = bodyEndLine,
                                    Signature = sameLineEndColumn >= absoluteStartColumn
                                        ? line[absoluteStartColumn..(sameLineEndColumn + 1)].Trim()
                                        : line[absoluteStartColumn..].Trim(),
                                    ContainerKind = "class",
                                    ContainerName = recordSymbol.Name,
                                    Visibility = visibility,
                                });
                            }

                            if (sameLineEndColumn < absoluteStartColumn)
                                break;

                            compactConstructorOffset = FindNextSameLineBraceStatementStart(segment, sameLineEndColumn - segmentStart + 1, "java");
                            continue;
                        }

                        compactConstructorOffset = FindNextSameLineBraceStatementStart(segment, compactConstructorOffset + 1, "java");
                    }
                }

                var column = segmentStart;
                while (column < segmentEndExclusive)
                {
                    if (TryConsumeJavaNonCode(line, ref column, ref mode))
                        continue;

                    var ch = line[column];
                    if (ch == '{')
                        braceDepth++;
                    else if (ch == '}' && braceDepth > 0)
                        braceDepth--;

                    column++;
                }
            }
        }
    }

    private static bool LooksLikeJavaCompactConstructorSymbol(SymbolRecord symbol, string recordName)
    {
        if (symbol.Kind != "function"
            || symbol.Name != recordName
            || symbol.ContainerKind != "class"
            || symbol.ContainerName != recordName)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(symbol.ReturnType))
            return false;

        var signature = symbol.Signature?.TrimStart();
        if (string.IsNullOrWhiteSpace(signature))
            return false;

        if (signature.Contains(" record ", StringComparison.Ordinal)
            || signature.StartsWith("record ", StringComparison.Ordinal)
            || signature.StartsWith("@", StringComparison.Ordinal))
        {
            return false;
        }

        return signature.Contains($"{recordName} {{", StringComparison.Ordinal);
    }

    private static bool IsJavaRecordSymbol(string[] rawLines, SymbolRecord symbol)
    {
        var declarationLineIndex = symbol.StartLine - 1;
        if (declarationLineIndex < 0 || declarationLineIndex >= rawLines.Length)
            return false;

        return TryMatchJavaDeclarationSegment(
            GetCurrentDeclarationRecordRegex("java", symbol.Kind, symbol.Name),
            rawLines[declarationLineIndex],
            false,
            out _,
            out _);
    }

    private static bool TryFindJavaSymbolBodyBounds(
        string[] rawLines,
        SymbolRecord containerSymbol,
        out int bodyStartLineIndex,
        out int bodyStartColumn,
        out int bodyEndLineIndex,
        out int bodyEndColumnExclusive)
    {
        bodyStartLineIndex = 0;
        bodyStartColumn = 0;
        bodyEndLineIndex = 0;
        bodyEndColumnExclusive = 0;

        var declarationLineIndex = containerSymbol.StartLine - 1;
        if (declarationLineIndex < 0 || declarationLineIndex >= rawLines.Length)
            return false;

        var scanEndLineIndex = Math.Min(containerSymbol.EndLine, rawLines.Length) - 1;
        if (scanEndLineIndex < declarationLineIndex)
            return false;

        return TryFindJavaBraceDelimitedBodyBounds(
            rawLines,
            declarationLineIndex,
            scanEndLineIndex,
            ignoreLeadingAnnotationArrayBraces: true,
            out bodyStartLineIndex,
            out bodyStartColumn,
            out bodyEndLineIndex,
            out bodyEndColumnExclusive);
    }

    private static bool TryFindJavaEnumBodyBounds(
        string[] rawLines,
        SymbolRecord enumSymbol,
        out int bodyStartLineIndex,
        out int bodyStartColumn,
        out int bodyEndLineIndex,
        out int bodyEndColumnExclusive)
    {
        return TryFindJavaSymbolBodyBounds(
            rawLines,
            enumSymbol,
            out bodyStartLineIndex,
            out bodyStartColumn,
            out bodyEndLineIndex,
            out bodyEndColumnExclusive);
    }

    // Track Java source-code scanner state (strings, char literals, comments, text blocks).
    // Java ソース scanner の state（文字列・char literal・コメント・text block）を表す。
    private enum JavaScanMode
    {
        Normal,
        LineComment,
        BlockComment,
        String,
        TextBlock,
        Char,
    }

    private static bool TryFindJavaBraceDelimitedBodyBounds(
        string[] rawLines,
        int declarationLineIndex,
        int scanEndLineIndex,
        bool ignoreLeadingAnnotationArrayBraces,
        out int bodyStartLineIndex,
        out int bodyStartColumn,
        out int bodyEndLineIndex,
        out int bodyEndColumnExclusive)
    {
        bodyStartLineIndex = 0;
        bodyStartColumn = 0;
        bodyEndLineIndex = 0;
        bodyEndColumnExclusive = 0;

        var mode = JavaScanMode.Normal;
        var depth = 0;
        var parenDepth = 0;
        var bracketDepth = 0;
        var opened = false;

        var lineIndex = declarationLineIndex;
        var column = 0;
        while (lineIndex <= scanEndLineIndex)
        {
            if (mode == JavaScanMode.LineComment)
                mode = JavaScanMode.Normal;

            var line = rawLines[lineIndex];
            while (column < line.Length)
            {
                if (TryConsumeJavaNonCode(line, ref column, ref mode))
                    continue;

                var ch = line[column];
                if (ch == '(')
                {
                    parenDepth++;
                }
                else if (ch == ')' && parenDepth > 0)
                {
                    parenDepth--;
                }
                else if (ch == '[')
                {
                    bracketDepth++;
                }
                else if (ch == ']' && bracketDepth > 0)
                {
                    bracketDepth--;
                }
                else if (ch == '{')
                {
                    if (!opened)
                    {
                        if (ignoreLeadingAnnotationArrayBraces && (parenDepth > 0 || bracketDepth > 0))
                        {
                            column++;
                            continue;
                        }

                        opened = true;
                        depth = 1;
                        bodyStartLineIndex = lineIndex;
                        bodyStartColumn = column + 1;
                    }
                    else
                    {
                        depth++;
                    }
                }
                else if (ch == '}' && opened)
                {
                    depth--;
                    if (depth == 0)
                    {
                        bodyEndLineIndex = lineIndex;
                        bodyEndColumnExclusive = column;
                        return true;
                    }
                }

                column++;
            }

            lineIndex++;
            column = 0;
        }

        if (!opened)
            return false;

        bodyEndLineIndex = scanEndLineIndex;
        bodyEndColumnExclusive = rawLines[scanEndLineIndex].Length;
        return true;
    }

    // Consume strings / chars / comments / text blocks, updating mode and advancing column.
    // Returns true if one or more characters were consumed; caller must NOT increment column again.
    // Returns false if the current character is structural code and caller should handle it.
    // 文字列・char・コメント・text block を読み飛ばして column を進める。
    // 消費したら true を返し、呼び出し元は column を再度進めないこと。
    // 構造的コードなら false を返して呼び出し元に処理を委ねる。
    private static bool TryConsumeJavaNonCode(string line, ref int column, ref JavaScanMode mode)
    {
        if (column >= line.Length)
            return false;

        var ch = line[column];
        switch (mode)
        {
            case JavaScanMode.LineComment:
                column = line.Length;
                return true;
            case JavaScanMode.BlockComment:
                if (ch == '*' && column + 1 < line.Length && line[column + 1] == '/')
                {
                    mode = JavaScanMode.Normal;
                    column += 2;
                    return true;
                }
                column++;
                return true;
            case JavaScanMode.String:
                if (ch == '\\' && column + 1 < line.Length)
                {
                    column += 2;
                    return true;
                }
                if (ch == '"')
                {
                    mode = JavaScanMode.Normal;
                    column++;
                    return true;
                }
                column++;
                return true;
            case JavaScanMode.TextBlock:
                if (ch == '"' && column + 2 < line.Length && line[column + 1] == '"' && line[column + 2] == '"')
                {
                    mode = JavaScanMode.Normal;
                    column += 3;
                    return true;
                }
                if (ch == '\\' && column + 1 < line.Length)
                {
                    column += 2;
                    return true;
                }
                column++;
                return true;
            case JavaScanMode.Char:
                if (ch == '\\' && column + 1 < line.Length)
                {
                    column += 2;
                    return true;
                }
                if (ch == '\'')
                {
                    mode = JavaScanMode.Normal;
                    column++;
                    return true;
                }
                column++;
                return true;
            default:
                if (ch == '/' && column + 1 < line.Length && line[column + 1] == '/')
                {
                    mode = JavaScanMode.LineComment;
                    column = line.Length;
                    return true;
                }
                if (ch == '/' && column + 1 < line.Length && line[column + 1] == '*')
                {
                    mode = JavaScanMode.BlockComment;
                    column += 2;
                    return true;
                }
                if (ch == '"' && column + 2 < line.Length && line[column + 1] == '"' && line[column + 2] == '"')
                {
                    mode = JavaScanMode.TextBlock;
                    column += 3;
                    return true;
                }
                if (ch == '"')
                {
                    mode = JavaScanMode.String;
                    column++;
                    return true;
                }
                if (ch == '\'')
                {
                    mode = JavaScanMode.Char;
                    column++;
                    return true;
                }
                return false;
        }
    }

    private static void ExtractJavaEnumMembersFromBody(
        long fileId,
        SymbolRecord enumSymbol,
        string[] rawLines,
        int bodyStartLineIndex,
        int bodyStartColumn,
        int bodyEndLineIndex,
        int bodyEndColumnExclusive,
        List<SymbolRecord> symbols)
    {
        var mode = JavaScanMode.Normal;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0; // depth inside the enum body (member anonymous bodies push this).
        (int LineIndex, int Column)? memberStart = null;
        var lineIndex = bodyStartLineIndex;
        var column = bodyStartColumn;

        while (lineIndex <= bodyEndLineIndex)
        {
            if (mode == JavaScanMode.LineComment)
                mode = JavaScanMode.Normal;

            var line = rawLines[lineIndex];
            var scanEndColumnExclusive = lineIndex == bodyEndLineIndex
                ? Math.Min(bodyEndColumnExclusive, line.Length)
                : line.Length;

            while (column < scanEndColumnExclusive)
            {
                if (TryConsumeJavaNonCode(line, ref column, ref mode))
                    continue;

                var ch = line[column];

                if (ch == '(')
                {
                    if (memberStart == null)
                        memberStart = (lineIndex, column);
                    parenDepth++;
                    column++;
                    continue;
                }
                if (ch == ')' && parenDepth > 0)
                {
                    parenDepth--;
                    column++;
                    continue;
                }
                if (ch == '[')
                {
                    if (memberStart == null)
                        memberStart = (lineIndex, column);
                    bracketDepth++;
                    column++;
                    continue;
                }
                if (ch == ']' && bracketDepth > 0)
                {
                    bracketDepth--;
                    column++;
                    continue;
                }
                if (ch == '{')
                {
                    if (memberStart == null)
                        memberStart = (lineIndex, column);
                    braceDepth++;
                    column++;
                    continue;
                }
                if (ch == '}' && braceDepth > 0)
                {
                    braceDepth--;
                    column++;
                    continue;
                }

                if (braceDepth == 0 && parenDepth == 0 && bracketDepth == 0)
                {
                    if (ch == ',')
                    {
                        if (memberStart != null)
                        {
                            TryAddJavaEnumMemberFromSpan(fileId, enumSymbol, rawLines, memberStart.Value, (lineIndex, column), symbols);
                            memberStart = null;
                        }
                        column++;
                        continue;
                    }
                    if (ch == ';')
                    {
                        if (memberStart != null)
                        {
                            TryAddJavaEnumMemberFromSpan(fileId, enumSymbol, rawLines, memberStart.Value, (lineIndex, column), symbols);
                            memberStart = null;
                        }
                        return;
                    }
                }

                if (!char.IsWhiteSpace(ch) && memberStart == null)
                    memberStart = (lineIndex, column);

                column++;
            }

            lineIndex++;
            column = 0;
        }

        if (memberStart != null)
            TryAddJavaEnumMemberFromSpan(fileId, enumSymbol, rawLines, memberStart.Value, (bodyEndLineIndex, bodyEndColumnExclusive), symbols);

        // Malformed-input recovery: if the scanner exited with unbalanced paren/bracket depths, the
        // body almost certainly contains an unterminated annotation. Fall back to the pre-body-scope
        // line regex so obvious enum members aren't suppressed wholesale by a single syntax error.
        // Depths > 0 mean the primary scan could not find clean boundaries — well-formed code always
        // closes back to 0 at the body end.
        // 入力不整形に対する recovery: primary scan が paren/bracket 深さを 0 に戻せずに終わった場合、未閉鎖の
        // annotation である可能性が高い。line regex を使って明白な enum member を救済する。
        if (parenDepth > 0 || bracketDepth > 0)
        {
            RecoverJavaEnumMembersByLine(
                fileId,
                enumSymbol,
                rawLines,
                bodyStartLineIndex,
                bodyStartColumn,
                bodyEndLineIndex,
                bodyEndColumnExclusive,
                symbols);
        }
    }

    private static void RecoverJavaEnumMembersByLine(
        long fileId,
        SymbolRecord enumSymbol,
        string[] rawLines,
        int bodyStartLineIndex,
        int bodyStartColumn,
        int bodyEndLineIndex,
        int bodyEndColumnExclusive,
        List<SymbolRecord> symbols)
    {
        // Dedup by member name. The primary scanner stamps StartLine at the first non-whitespace
        // (often the annotation line), while this fallback stamps the member-name line, so
        // StartLine-based dedup would miss matches. Java enum member names are unique.
        // メンバー名で重複排除する。primary scanner と recovery で StartLine 基準が揃わないため。
        var alreadyEmittedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var existing in symbols)
        {
            if (existing.FileId == enumSymbol.FileId
                && existing.ContainerKind == "enum"
                && existing.ContainerName == enumSymbol.Name)
            {
                alreadyEmittedNames.Add(existing.Name);
            }
        }

        // Track brace depth across the body so lines inside anonymous member bodies or methods
        // don't spuriously match the line regex. Depth 0 means "top of the enum body member list."
        // 匿名メンバー本体やメソッド本体内の行を誤って member として拾わないよう brace 深さを追う。
        var mode = JavaScanMode.Normal;
        var braceDepth = 0;

        for (int i = bodyStartLineIndex; i <= bodyEndLineIndex && i < rawLines.Length; i++)
        {
            if (mode == JavaScanMode.LineComment)
                mode = JavaScanMode.Normal;

            var line = rawLines[i];
            var lineStartBraceDepth = braceDepth;
            var lineStartMode = mode;

            // Only try the fallback regex when this line starts at the enum body's top level and
            // not inside a string / comment / text block carried over from the previous line.
            // 行頭が enum 本体の top-level で、かつ非コード状態でもないときだけ fallback regex を試す。
            if (lineStartBraceDepth == 0 && lineStartMode == JavaScanMode.Normal)
            {
                var match = JavaEnumMemberLineFallbackRegex.Match(line);
                if (match.Success)
                {
                    var name = match.Groups["name"].Value;
                    if (!alreadyEmittedNames.Contains(name))
                    {
                        symbols.Add(new SymbolRecord
                        {
                            FileId = fileId,
                            Kind = "function",
                            Name = name,
                            Line = i + 1,
                            StartLine = i + 1,
                            EndLine = i + 1,
                            Signature = line.Trim(),
                            ContainerKind = "enum",
                            ContainerName = enumSymbol.Name,
                        });
                        alreadyEmittedNames.Add(name);
                    }
                }
            }

            // Advance mode / brace depth across this line so subsequent lines see correct state.
            // A top-level `;` (braceDepth == 0) terminates the member list — stop recovery.
            // 次行の状態を正しく保つため行内の mode / brace 深さを更新する。top-level の `;` は終端。
            var startColumn = (i == bodyStartLineIndex) ? bodyStartColumn : 0;
            var endColumnExclusive = (i == bodyEndLineIndex)
                ? Math.Min(bodyEndColumnExclusive, line.Length)
                : line.Length;
            var column = startColumn;
            while (column < endColumnExclusive)
            {
                if (TryConsumeJavaNonCode(line, ref column, ref mode))
                    continue;

                var ch = line[column];
                if (ch == '{')
                    braceDepth++;
                else if (ch == '}' && braceDepth > 0)
                    braceDepth--;
                else if (ch == ';' && braceDepth == 0)
                    return;

                column++;
            }
        }
    }

    private static void TryAddJavaEnumMemberFromSpan(
        long fileId,
        SymbolRecord enumSymbol,
        string[] rawLines,
        (int LineIndex, int Column) start,
        (int LineIndex, int Column) endExclusive,
        List<SymbolRecord> symbols)
    {
        var rawSignature = GetSourceSpanText(rawLines, start, endExclusive).Trim();
        if (rawSignature.Length == 0)
            return;

        // Skip leading `@Annotation(...)` annotations before the member name.
        // メンバー名の前にある `@Annotation(...)` を読み飛ばす。
        var nameSearchStart = SkipLeadingJavaAnnotations(rawSignature);
        if (nameSearchStart >= rawSignature.Length)
            return;

        var match = JavaEnumMemberNameRegex.Match(rawSignature, nameSearchStart);
        if (!match.Success || match.Index != nameSearchStart)
            return;

        var name = match.Groups["name"].Value;
        if (string.IsNullOrEmpty(name))
            return;

        int? bodyStartLine = null;
        int? bodyEndLine = null;
        if (TryFindJavaEnumMemberBodyBounds(rawLines, start, endExclusive, out var anonymousBodyStartLine, out var anonymousBodyEndLine))
        {
            bodyStartLine = anonymousBodyStartLine;
            bodyEndLine = anonymousBodyEndLine;
        }

        symbols.Add(new SymbolRecord
        {
            FileId = fileId,
            Kind = "function",
            Name = name,
            Line = start.LineIndex + 1,
            StartLine = start.LineIndex + 1,
            StartColumn = start.Column,
            EndLine = endExclusive.LineIndex + 1,
            BodyStartLine = bodyStartLine,
            BodyEndLine = bodyEndLine,
            Signature = rawSignature,
            ContainerKind = "enum",
            ContainerName = enumSymbol.Name,
        });
    }

    private static bool TryFindJavaEnumMemberBodyBounds(
        string[] rawLines,
        (int LineIndex, int Column) start,
        (int LineIndex, int Column) endExclusive,
        out int bodyStartLine,
        out int bodyEndLine)
    {
        bodyStartLine = 0;
        bodyEndLine = 0;

        var mode = JavaScanMode.Normal;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var foundBody = false;

        for (int lineIndex = start.LineIndex; lineIndex <= endExclusive.LineIndex && lineIndex < rawLines.Length; lineIndex++)
        {
            if (mode == JavaScanMode.LineComment)
                mode = JavaScanMode.Normal;

            var line = rawLines[lineIndex];
            var column = lineIndex == start.LineIndex
                ? start.Column
                : 0;
            var scanEndColumnExclusive = lineIndex == endExclusive.LineIndex
                ? Math.Min(endExclusive.Column, line.Length)
                : line.Length;

            while (column < scanEndColumnExclusive)
            {
                if (TryConsumeJavaNonCode(line, ref column, ref mode))
                    continue;

                var ch = line[column];
                if (ch == '(')
                {
                    parenDepth++;
                }
                else if (ch == ')' && parenDepth > 0)
                {
                    parenDepth--;
                }
                else if (ch == '[')
                {
                    bracketDepth++;
                }
                else if (ch == ']' && bracketDepth > 0)
                {
                    bracketDepth--;
                }
                else if (ch == '{')
                {
                    if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                    {
                        foundBody = true;
                        bodyStartLine = lineIndex + 1;
                    }

                    braceDepth++;
                }
                else if (ch == '}' && braceDepth > 0)
                {
                    braceDepth--;
                    if (foundBody && braceDepth == 0)
                    {
                        bodyEndLine = lineIndex + 1;
                        return true;
                    }
                }

                column++;
            }
        }

        return false;
    }

    private static int SkipLeadingJavaAnnotations(string span, bool allowKotlinUseSiteTargets = false)
    {
        var mode = JavaScanMode.Normal;
        var index = SkipJavaWhitespaceAndComments(span, 0, ref mode);

        while (index < span.Length && mode == JavaScanMode.Normal && span[index] == '@')
        {
            index++; // consume '@'
            index = SkipJavaWhitespaceAndComments(span, index, ref mode);
            if (mode != JavaScanMode.Normal)
                return index;

            if (allowKotlinUseSiteTargets && TryConsumeKotlinAnnotationTarget(span, ref index))
            {
                index = SkipJavaWhitespaceAndComments(span, index, ref mode);
                if (mode != JavaScanMode.Normal)
                    return index;
            }

            while (index < span.Length && (char.IsLetterOrDigit(span[index]) || span[index] == '_' || span[index] == '.' || span[index] == '$'))
                index++;

            index = SkipJavaWhitespaceAndComments(span, index, ref mode);
            if (mode != JavaScanMode.Normal)
                return index;

            if (index < span.Length && span[index] == '(')
            {
                var depth = 1;
                index++;
                while (index < span.Length && depth > 0)
                {
                    if (TryConsumeJavaNonCodeAcrossLines(span, ref index, ref mode))
                        continue;

                    var ch = span[index];
                    if (ch == '(') depth++;
                    else if (ch == ')') depth--;
                    index++;
                }
            }

            index = SkipJavaWhitespaceAndComments(span, index, ref mode);
        }

        return index;
    }

    private static bool TryConsumeKotlinAnnotationTarget(string span, ref int index)
    {
        var targetStart = index;
        while (index < span.Length && (char.IsLetterOrDigit(span[index]) || span[index] == '_'))
            index++;

        if (index >= span.Length || span[index] != ':')
        {
            index = targetStart;
            return false;
        }

        if (!KotlinAnnotationTargets.Contains(span[targetStart..index]))
        {
            index = targetStart;
            return false;
        }

        index++;
        return true;
    }

    private static bool TryMatchJavaDeclarationSegment(
        Regex regex,
        string segment,
        bool allowKotlinUseSiteTargets,
        out Match match,
        out int leadingAnnotationOffset)
    {
        match = regex.Match(segment);
        leadingAnnotationOffset = 0;
        if (match.Success)
            return true;

        var skippedOffset = SkipLeadingJavaAnnotations(segment, allowKotlinUseSiteTargets);
        if (skippedOffset <= 0 || skippedOffset >= segment.Length)
            return false;

        var strippedMatch = regex.Match(segment[skippedOffset..]);
        if (!strippedMatch.Success)
            return false;

        match = strippedMatch;
        leadingAnnotationOffset = skippedOffset;
        return true;
    }

    // Walk whitespace, comments, and newlines in a multi-line span until the next non-whitespace code position.
    // 複数行 span 内の空白・コメント・改行をまとめて読み飛ばす。
    private static int SkipJavaWhitespaceAndComments(string span, int index, ref JavaScanMode mode)
    {
        while (index < span.Length)
        {
            if (mode == JavaScanMode.Normal && char.IsWhiteSpace(span[index]))
            {
                index++;
                continue;
            }

            if (TryConsumeJavaNonCodeAcrossLines(span, ref index, ref mode))
                continue;

            if (mode == JavaScanMode.Normal)
                return index;

            index++;
        }
        return index;
    }

    // Multi-line-aware variant of TryConsumeJavaNonCode. Handles `\n` explicitly so it can run on
    // a single span string that contains newlines (the line-based caller uses the per-line variant).
    // 複数行 span 対応版。`\n` を明示的に処理し、改行跨ぎの line-comment / 文字列終端も扱う。
    private static bool TryConsumeJavaNonCodeAcrossLines(string span, ref int index, ref JavaScanMode mode)
    {
        if (index >= span.Length)
            return false;

        var ch = span[index];
        switch (mode)
        {
            case JavaScanMode.LineComment:
                if (ch == '\n')
                    mode = JavaScanMode.Normal;
                index++;
                return true;
            case JavaScanMode.String:
            case JavaScanMode.Char:
                // Non-text-block Java string / char literals cannot cross raw newlines.
                // Treat a newline as an implicit terminator so the annotation skip stays sane.
                // Java の非 text-block 文字列 / char は生の改行を跨げないため、改行で暗黙終端する。
                if (ch == '\n')
                {
                    mode = JavaScanMode.Normal;
                    index++;
                    return true;
                }
                if (ch == '\\' && index + 1 < span.Length)
                {
                    index += 2;
                    return true;
                }
                if (mode == JavaScanMode.String && ch == '"')
                {
                    mode = JavaScanMode.Normal;
                    index++;
                    return true;
                }
                if (mode == JavaScanMode.Char && ch == '\'')
                {
                    mode = JavaScanMode.Normal;
                    index++;
                    return true;
                }
                index++;
                return true;
            case JavaScanMode.BlockComment:
                if (ch == '*' && index + 1 < span.Length && span[index + 1] == '/')
                {
                    mode = JavaScanMode.Normal;
                    index += 2;
                    return true;
                }
                index++;
                return true;
            case JavaScanMode.TextBlock:
                if (ch == '"' && index + 2 < span.Length && span[index + 1] == '"' && span[index + 2] == '"')
                {
                    mode = JavaScanMode.Normal;
                    index += 3;
                    return true;
                }
                if (ch == '\\' && index + 1 < span.Length)
                {
                    index += 2;
                    return true;
                }
                index++;
                return true;
            default:
                if (ch == '/' && index + 1 < span.Length && span[index + 1] == '/')
                {
                    mode = JavaScanMode.LineComment;
                    index += 2;
                    return true;
                }
                if (ch == '/' && index + 1 < span.Length && span[index + 1] == '*')
                {
                    mode = JavaScanMode.BlockComment;
                    index += 2;
                    return true;
                }
                if (ch == '"' && index + 2 < span.Length && span[index + 1] == '"' && span[index + 2] == '"')
                {
                    mode = JavaScanMode.TextBlock;
                    index += 3;
                    return true;
                }
                if (ch == '"')
                {
                    mode = JavaScanMode.String;
                    index++;
                    return true;
                }
                if (ch == '\'')
                {
                    mode = JavaScanMode.Char;
                    index++;
                    return true;
                }
                return false;
        }
    }

    internal static (int EndLine, int? BodyStartLine, int? BodyEndLine) FindJavaBraceRange(string[] lines, int startIndex, int startColumn = 0)
    {
        var depth = 0;
        var opened = false;
        int? bodyStartLine = null;
        var mode = JavaScanMode.Normal;
        var sawOpenParen = false;
        var annotationDefaultValue = false;
        // Track paren/bracket/angle nesting before the body opens so that `{` / `}` appearing
        // inside `@Ann({A.class, B.class})` type-use annotations or bounded generic arguments
        // don't open/close the outer class body prematurely. Once the body is opened, only
        // string/char/comment tracking matters for the depth counter, so the header-level
        // counters are frozen.
        // body `{` が開く前に `@Ann({...})` や `List<Map<String,Integer>>` のような annotation
        // 引数・入れ子 generic 内の `{` / `}` で誤って開閉しないよう、header 段階の `()` / `[]`
        // / `<>` 深さを追跡する。body が開いた後は深さ計測は不要（lexer の文字列・コメント
        // 追跡で十分）。
        var parenDepth = 0;
        var bracketDepth = 0;
        var angleDepth = 0;

        for (int i = startIndex; i < lines.Length; i++)
        {
            if (mode == JavaScanMode.LineComment)
                mode = JavaScanMode.Normal;

            var line = lines[i];
            var column = i == startIndex ? Math.Min(startColumn, line.Length) : 0;

            while (column < line.Length)
            {
                if (TryConsumeJavaNonCode(line, ref column, ref mode))
                    continue;

                var ch = line[column];
                if (!opened)
                {
                    if (sawOpenParen
                        && !annotationDefaultValue
                        && parenDepth == 0
                        && bracketDepth == 0
                        && angleDepth == 0
                        && StartsWithKeyword(line, column, "default"))
                    {
                        // Annotation members use `default { ... }` for array defaults, but that
                        // brace pair is part of the default value, not a real member body.
                        // `default` after a Java parameter list therefore flips the scanner into
                        // a body-less statement mode until the terminating `;`.
                        // Java の annotation member は `default { ... }` で配列デフォルト値を
                        // 持つが、この `{ ... }` は member 本体ではなく default 値の一部。
                        // Java の parameter list の後に現れた `default` は、終端 `;` まで
                        // body-less statement として扱う。
                        annotationDefaultValue = true;
                        column += "default".Length;
                        continue;
                    }

                    if (ch == '(') { parenDepth++; sawOpenParen = true; column++; continue; }
                    if (ch == ')' && parenDepth > 0) { parenDepth--; column++; continue; }
                    if (ch == '[') { bracketDepth++; column++; continue; }
                    if (ch == ']' && bracketDepth > 0) { bracketDepth--; column++; continue; }
                    if (ch == '<') { angleDepth++; column++; continue; }
                    if (ch == '>' && angleDepth > 0) { angleDepth--; column++; continue; }
                    if ((parenDepth > 0 || bracketDepth > 0 || angleDepth > 0))
                    {
                        column++;
                        continue;
                    }
                    if (annotationDefaultValue && (ch == '{' || ch == '}'))
                    {
                        column++;
                        continue;
                    }
                    if (ch == ';')
                        return (i + 1, null, null);
                }

                if (ch == '{')
                {
                    depth++;
                    if (!opened)
                    {
                        opened = true;
                        bodyStartLine = i + 1;
                    }
                }
                else if (ch == '}' && opened)
                {
                    depth--;
                    if (depth == 0)
                        return (i + 1, bodyStartLine, i + 1);
                }
                column++;
            }

            if (!opened && mode == JavaScanMode.Normal
                && parenDepth == 0 && bracketDepth == 0 && angleDepth == 0
                && line.TrimEnd().EndsWith(';'))
                return (startIndex + 1, null, null);
        }

        return opened
            ? (lines.Length, bodyStartLine, lines.Length)
            : (startIndex + 1, null, null);
    }

    private static int FindJavaSameLineBraceEndColumn(string line, int startColumn)
    {
        var mode = JavaScanMode.Normal;
        var depth = 0;
        var opened = false;
        var parenDepth = 0;
        var bracketDepth = 0;
        var angleDepth = 0;
        var column = Math.Max(0, startColumn);

        while (column < line.Length)
        {
            if (TryConsumeJavaNonCode(line, ref column, ref mode))
                continue;

            var ch = line[column];
            if (!opened)
            {
                if (ch == '(')
                {
                    parenDepth++;
                    column++;
                    continue;
                }

                if (ch == ')' && parenDepth > 0)
                {
                    parenDepth--;
                    column++;
                    continue;
                }

                if (ch == '[')
                {
                    bracketDepth++;
                    column++;
                    continue;
                }

                if (ch == ']' && bracketDepth > 0)
                {
                    bracketDepth--;
                    column++;
                    continue;
                }

                if (ch == '<')
                {
                    angleDepth++;
                    column++;
                    continue;
                }

                if (ch == '>' && angleDepth > 0)
                {
                    angleDepth--;
                    column++;
                    continue;
                }

                if (parenDepth > 0 || bracketDepth > 0 || angleDepth > 0)
                {
                    column++;
                    continue;
                }
            }

            if (ch == '{')
            {
                depth++;
                opened = true;
            }
            else if (ch == '}' && opened)
            {
                depth--;
                if (depth == 0)
                    return column;
            }

            column++;
        }

        return -1;
    }

    private static bool TryGetJavaSameLineSemicolonSiblingOffset(string matchLine, int startColumn, out int nextSameLineOffset)
    {
        nextSameLineOffset = -1;
        if (startColumn < 0 || startColumn >= matchLine.Length)
            return false;

        var statementEnd = FindJavaSameLineStatementEnd(matchLine, startColumn);
        var semicolonIndex = statementEnd - 1;
        if (semicolonIndex < startColumn
            || semicolonIndex >= matchLine.Length
            || matchLine[semicolonIndex] != ';')
        {
            return false;
        }

        var nextOffset = FindNextSameLineBraceStatementStart(matchLine, statementEnd, "java");
        while (nextOffset >= 0
            && nextOffset < matchLine.Length
            && matchLine[nextOffset] == '}')
        {
            nextOffset = FindNextSameLineBraceStatementStart(matchLine, nextOffset + 1, "java");
        }

        if (nextOffset <= statementEnd
            || nextOffset >= matchLine.Length)
        {
            return false;
        }

        nextSameLineOffset = nextOffset;
        return true;
    }

    private static readonly Regex JavaEnumMemberNameRegex = new(
        @"(?<name>[\p{L}\p{Nl}_$][\p{L}\p{Nl}\p{Nd}\p{Mn}\p{Mc}\p{Pc}_$]*)",
        RegexOptions.Compiled);

    // Line-based fallback used only when the primary body scanner exits with unbalanced delimiters,
    // which signals malformed input. Mirrors the pre-body-scope regex so mid-edit states still emit
    // obvious uppercase-identifier members.
    // malformed 入力を primary scanner が検知した場合に限って使う line-based fallback。以前の行単位正規表現と同等。
    private static readonly Regex JavaEnumMemberLineFallbackRegex = new(
        @"^\s+(?<name>[A-Z]\w*)\s*(?:\([^)]*\))?\s*(?:,|\{|;)\s*$",
        RegexOptions.Compiled);

    // Raw-text / RCDATA element names that must be masked before the symbol state
    // machine runs. `<script>` / `<style>` are raw-text, `<textarea>` / `<title>`
    // are RCDATA. Using a HashSet keeps the mask state machine branch-free per
    // opening tag.
    // state machine がシンボル抽出する前にマスクしなければならない raw-text / RCDATA
    // 要素名。`<script>` / `<style>` は raw-text、`<textarea>` / `<title>` は RCDATA。

    private static readonly Regex JavaModuleRequiresDirectiveRegex = new(
        @"^\s*requires\s+(?:transitive\s+|static\s+)*(?<name>[\w.]+)\s*;$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex JavaModuleExportsOrOpensDirectiveRegex = new(
        @"^\s*(?:exports|opens)\s+(?<name>[\w.]+)(?:\s+to\s+[\w.,\s]+)?\s*;$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex JavaModuleUsesOrProvidesDirectiveRegex = new(
        @"^\s*(?:uses|provides)\s+(?<name>[\w.]+)(?:\s+with\s+[\w.,\s]+)?\s*;$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly string[] JavaModuleDirectiveKeywords = ["requires", "exports", "opens", "uses", "provides"];
    private static readonly HashSet<string> KotlinAnnotationTargets = new(StringComparer.Ordinal)
    {
        "field", "get", "set", "param", "setparam", "property", "receiver", "file", "delegate", "all",
    };

}
