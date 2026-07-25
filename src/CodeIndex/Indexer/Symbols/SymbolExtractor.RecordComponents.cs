using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static void CollectRecordPrimaryComponentSymbols(
        long fileId,
        string lang,
        string[] lines,
        int declarationLineIndex,
        int declarationStartColumn,
        string kind,
        string recordName,
        ref List<PendingRecordPrimaryComponents>? pendingRecordPrimaryComponents,
        ref RecordPrimaryComponentParentIndex? parentIndex,
        List<SymbolRecord> symbols)
    {
        if (lang == "kotlin")
        {
            if (kind is not "class" and not "enum")
                return;
        }
        else if (kind is not "class" and not "struct")
        {
            return;
        }

        if (!TryGetRecordPrimaryComponents(
            lang,
            lines,
            declarationLineIndex,
            declarationStartColumn,
            kind,
            recordName,
            out var components,
            out var declarationEndLine))
            return;

        var parentKey = new RecordPrimaryComponentParentKey(
            fileId,
            kind,
            recordName,
            declarationLineIndex + 1);
        var parentSymbol = (parentIndex ??= new RecordPrimaryComponentParentIndex())
            .FindLast(symbols, parentKey);
        if (parentSymbol != null)
            parentSymbol.EndLine = Math.Max(parentSymbol.EndLine, declarationEndLine);

        if (components.Count > 0)
        {
            (pendingRecordPrimaryComponents ??= []).Add(new PendingRecordPrimaryComponents(
                fileId,
                kind,
                recordName,
                declarationLineIndex + 1,
                components));
        }
    }

    private static void MaterializeRecordPrimaryComponentSymbols(
        List<SymbolRecord> symbols,
        List<PendingRecordPrimaryComponents>? pendingRecordPrimaryComponents)
    {
        if (pendingRecordPrimaryComponents is not { Count: > 0 })
            return;

        var parents = new Dictionary<RecordPrimaryComponentParentKey, SymbolRecord?>();
        var propertyBuckets = new Dictionary<RecordPrimaryComponentPropertyKey, List<SymbolRecord>>();
        foreach (var pending in pendingRecordPrimaryComponents)
        {
            parents.TryAdd(
                new RecordPrimaryComponentParentKey(
                    pending.FileId,
                    pending.Kind,
                    pending.RecordName,
                    pending.RecordStartLine),
                null);
            propertyBuckets.TryAdd(
                new RecordPrimaryComponentPropertyKey(
                    pending.FileId,
                    pending.Kind,
                    pending.RecordName),
                []);
        }

        // Build both final-state indexes after AssignContainers. Forward overwrite preserves
        // the previous reverse-search "last parent wins" rule, and property buckets retain
        // source-list order for overlapping/nested parent ranges.
        // AssignContainers 後の最終状態から両indexを構築する。forward上書きで従来の
        // 「最後のparent優先」を保ち、property bucketは重複range向けにlist順を保つ。
        foreach (var symbol in symbols)
        {
            var parentKey = new RecordPrimaryComponentParentKey(
                symbol.FileId,
                symbol.Kind,
                symbol.Name,
                symbol.StartLine);
            if (parents.ContainsKey(parentKey))
                parents[parentKey] = symbol;

            if (symbol.Kind == "property"
                && symbol.ContainerKind != null
                && symbol.ContainerName != null
                && propertyBuckets.TryGetValue(
                    new RecordPrimaryComponentPropertyKey(
                        symbol.FileId,
                        symbol.ContainerKind,
                        symbol.ContainerName),
                    out var propertyBucket))
            {
                propertyBucket.Add(symbol);
            }
        }

        foreach (var pending in pendingRecordPrimaryComponents)
        {
            var parentKey = new RecordPrimaryComponentParentKey(
                pending.FileId,
                pending.Kind,
                pending.RecordName,
                pending.RecordStartLine);
            var parentSymbol = parents[parentKey];
            if (parentSymbol == null)
                continue;

            var propertyKey = new RecordPrimaryComponentPropertyKey(
                pending.FileId,
                pending.Kind,
                pending.RecordName);
            var propertyBucket = propertyBuckets[propertyKey];
            var existingComponentNames = BuildExistingRecordPrimaryComponentNameSet(propertyBucket, parentSymbol);

            foreach (var component in pending.Components)
            {
                if (!existingComponentNames.Add(component.Name))
                    continue;

                var componentSymbol = new SymbolRecord
                {
                    FileId = pending.FileId,
                    Kind = "property",
                    Name = component.Name,
                    Line = component.Line,
                    StartLine = component.Line,
                    EndLine = component.Line,
                    Signature = component.Signature,
                    ContainerKind = pending.Kind,
                    ContainerName = pending.RecordName,
                    Visibility = component.Visibility ?? "public",
                    ReturnType = component.Type,
                };
                symbols.Add(componentSymbol);
                propertyBucket.Add(componentSymbol);
            }
        }
    }

    private static HashSet<string> BuildExistingRecordPrimaryComponentNameSet(
        IReadOnlyList<SymbolRecord> propertyCandidates,
        SymbolRecord parentSymbol)
    {
        var existingComponentNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbol in propertyCandidates)
        {
            if (symbol.StartLine >= parentSymbol.StartLine
                && symbol.EndLine <= parentSymbol.EndLine)
            {
                existingComponentNames.Add(symbol.Name);
            }
        }

        return existingComponentNames;
    }

    private static bool TryGetRecordPrimaryComponents(
        string lang,
        string[] lines,
        int declarationLineIndex,
        int declarationStartColumn,
        string kind,
        string recordName,
        out List<RecordPrimaryComponent> components,
        out int declarationEndLine)
    {
        components = null!;
        declarationEndLine = declarationLineIndex + 1;

        if (lang is not "csharp" and not "java" and not "kotlin")
            return false;

        var declaration = lang == "kotlin"
            ? CollectKotlinPrimaryConstructorDeclarationText(lines, declarationLineIndex, declarationStartColumn)
            : CollectRecordDeclarationText(lines, declarationLineIndex, declarationStartColumn);
        if (string.IsNullOrWhiteSpace(declaration))
            return false;

        var recordRegex = GetCurrentDeclarationRecordRegex(lang, kind, recordName);
        var javaLeadingAnnotationOffset = 0;
        var recordMatch = lang is "java" or "kotlin"
            ? (TryMatchJavaDeclarationSegment(recordRegex, declaration, lang == "kotlin", out var javaRecordMatch, out javaLeadingAnnotationOffset)
                ? javaRecordMatch
                : recordRegex.Match(declaration))
            : recordRegex.Match(declaration);
        if (!recordMatch.Success)
            return false;

        var parameterOpenIndex = lang == "csharp"
            ? FindCSharpPrimaryConstructorParameterListStart(declaration, recordMatch.Index + recordMatch.Length)
            : FindRecordPrimaryComponentListStart(
                declaration,
                recordMatch.Index + recordMatch.Length + javaLeadingAnnotationOffset);
        if (parameterOpenIndex < 0)
            return false;

        var parameterCloseIndex = FindMatchingRecordPrimaryComponentListEnd(declaration, parameterOpenIndex);
        if (parameterCloseIndex <= parameterOpenIndex)
            return false;
        var declarationTerminatorIndex = FindRecordDeclarationTerminatorIndex(declaration, parameterCloseIndex + 1);
        var declarationLineSpanEnd = declarationTerminatorIndex >= 0 ? declarationTerminatorIndex + 1 : parameterCloseIndex + 1;
        declarationEndLine = declarationLineIndex + 1 + declaration[..declarationLineSpanEnd].Count(ch => ch == '\n');

        var rawParameterList = StripRecordComponentComments(declaration[(parameterOpenIndex + 1)..parameterCloseIndex]);
        components = [];
        foreach (var rawComponent in SplitTopLevelRecordPrimaryComponents(rawParameterList, declarationLineIndex + 1))
        {
            if (TryParseRecordPrimaryComponent(lang, rawComponent, out var component))
                components.Add(component);
        }

        return true;
    }

    private static string CollectRecordDeclarationText(string[] lines, int declarationLineIndex, int declarationStartColumn)
    {
        var builder = new StringBuilder();
        var parameterOpenIndex = -1;
        var parameterCloseIndex = -1;
        for (int i = declarationLineIndex; i < lines.Length; i++)
        {
            if (builder.Length > 0)
                builder.Append('\n');

            // Content was split on '\n', so CRLF lines carry a trailing '\r'. Strip it before
            // appending so intermediate separators stay '\n' and the collected declaration
            // text is stable across OS line endings (#405 follow-up to #382).
            // content は '\n' で分割しているため、CRLF 行は末尾に '\r' が残る。行間の区切りを
            // '\n' に揃え、OS 差分で collected text が変わらないよう '\r' を落として追加する
            // （#382 に続く #405 対応）。
            var line = lines[i];
            var lineText = i == declarationLineIndex
                ? line[Math.Min(declarationStartColumn, line.Length)..]
                : line;
            builder.Append(StripTrailingCr(lineText));

            var declaration = builder.ToString();
            if (parameterOpenIndex < 0)
            {
                parameterOpenIndex = FindRecordPrimaryComponentListStart(declaration, 0);
                if (parameterOpenIndex < 0)
                {
                    if (FindRecordDeclarationTerminatorIndex(declaration, 0) >= 0)
                        return declaration;
                    continue;
                }
            }

            if (parameterCloseIndex < 0)
            {
                parameterCloseIndex = FindMatchingRecordPrimaryComponentListEnd(declaration, parameterOpenIndex);
                if (parameterCloseIndex <= parameterOpenIndex)
                    continue;
            }

            if (FindRecordDeclarationTerminatorIndex(declaration, parameterCloseIndex + 1) >= 0)
                return declaration;
        }

        return builder.ToString();
    }

    private static string CollectKotlinPrimaryConstructorDeclarationText(string[] lines, int declarationLineIndex, int declarationStartColumn)
    {
        const int KotlinPrimaryConstructorDeclarationLookaheadLineLimit = 32;

        var builder = new StringBuilder();
        var parameterOpenIndex = -1;
        var parameterCloseIndex = -1;
        for (int i = declarationLineIndex; i < lines.Length && i < declarationLineIndex + KotlinPrimaryConstructorDeclarationLookaheadLineLimit; i++)
        {
            if (builder.Length > 0)
                builder.Append('\n');

            // Kotlin class headers may split across a few physical lines. Keep the collected
            // declaration stable across line endings while bounding the scan so a class without
            // a primary constructor does not cause a whole-file read.
            // Kotlin の class ヘッダは数行に分割されうる。改行差を吸収しつつ、primary
            // constructor を持たない class でファイル全体を走査しないよう、収集範囲を
            // ほどよい行数に制限する。
            var line = lines[i];
            var lineText = i == declarationLineIndex
                ? line[Math.Min(declarationStartColumn, line.Length)..]
                : line;
            builder.Append(StripTrailingCr(lineText));

            var declaration = builder.ToString();
            if (parameterOpenIndex < 0)
            {
                parameterOpenIndex = FindRecordPrimaryComponentListStart(declaration, 0);
                if (parameterOpenIndex < 0)
                {
                    if (FindRecordDeclarationTerminatorIndex(declaration, 0) >= 0)
                        return declaration;

                    continue;
                }
            }

            if (parameterCloseIndex < 0)
            {
                parameterCloseIndex = FindMatchingRecordPrimaryComponentListEnd(declaration, parameterOpenIndex);
                if (parameterCloseIndex <= parameterOpenIndex)
                    continue;
            }

            return declaration[..(parameterCloseIndex + 1)];
        }

        return builder.ToString();
    }

    private static Regex GetCurrentDeclarationRecordRegex(string lang, string kind, string recordName)
    {
        if (lang == "csharp")
        {
            return kind == "struct"
                ? new Regex(@"^\s*(?:(?:public|private|protected\s+internal|private\s+protected|protected|internal)\s+)?(?:(?:static|partial|readonly|file|new|ref|unsafe)\s+)*(?:record\s+)?struct\s+" + Regex.Escape(recordName) + @"\b", RegexOptions.CultureInvariant)
                : new Regex(@"^\s*(?:(?:public|private|protected\s+internal|private\s+protected|protected|internal)\s+)?(?:(?:static|partial|abstract|sealed|readonly|file|new|unsafe)\s+)*(?:record(?:\s+class)?\s+|class\s+)" + Regex.Escape(recordName) + @"\b", RegexOptions.CultureInvariant);
        }

        if (lang == "kotlin")
        {
            return kind == "enum"
                ? new Regex(@"^\s*(?<visibility>public|private|protected|internal)?\s*(?:(?:abstract|data|sealed|open|inner|value|annotation|expect|actual)\s+)*enum\s+class\s+" + Regex.Escape(recordName) + @"\b", RegexOptions.CultureInvariant)
                : new Regex(@"^\s*(?<visibility>public|private|protected|internal)?\s*(?:(?:abstract|data|sealed|open|inner|value|annotation|expect|actual)\s+)*(?:class|object)\s+" + Regex.Escape(recordName) + @"\b", RegexOptions.CultureInvariant);
        }

        return new Regex(@"^\s*(?:public|private|protected)?\s*(?:(?:static|final|abstract|sealed|non-sealed|strictfp)\s+)*record\s+" + Regex.Escape(recordName) + @"\b", RegexOptions.CultureInvariant);
    }

    private static int FindCSharpPrimaryConstructorParameterListStart(string declaration, int startIndex)
    {
        var index = Math.Max(0, startIndex);
        if (!SkipCSharpGenericTypeParameterList(declaration, ref index))
            return -1;

        while (index < declaration.Length && char.IsWhiteSpace(declaration[index]))
            index++;

        return index < declaration.Length && declaration[index] == '('
            ? index
            : -1;
    }

    private static bool SkipCSharpGenericTypeParameterList(string declaration, ref int index)
    {
        if (index >= declaration.Length || declaration[index] != '<')
            return true;

        var depth = 0;
        for (; index < declaration.Length; index++)
        {
            var ch = declaration[index];
            if (ch == '<')
                depth++;
            else if (ch == '>')
            {
                depth--;
                if (depth == 0)
                {
                    index++;
                    return true;
                }
            }
        }

        return false;
    }

    private static int FindRecordPrimaryComponentListStart(string declaration, int startIndex)
    {
        var angleDepth = 0;
        for (int i = Math.Max(0, startIndex); i < declaration.Length; i++)
        {
            var ch = declaration[i];
            if (ch == '<')
            {
                angleDepth++;
                continue;
            }

            if (ch == '>')
            {
                if (angleDepth > 0)
                    angleDepth--;
                continue;
            }

            if (ch == '(' && angleDepth == 0)
                return i;
        }

        return -1;
    }

    private static int FindMatchingRecordPrimaryComponentListEnd(string declaration, int openIndex)
    {
        var parenDepth = 0;
        var angleDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var inLineComment = false;
        var inBlockComment = false;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var escapeNext = false;

        for (int i = openIndex; i < declaration.Length; i++)
        {
            var ch = declaration[i];
            var next = i + 1 < declaration.Length ? declaration[i + 1] : '\0';

            if (inLineComment)
            {
                if (ch == '\n')
                    inLineComment = false;
                continue;
            }

            if (inBlockComment)
            {
                if (ch == '*' && next == '/')
                {
                    inBlockComment = false;
                    i++;
                }

                continue;
            }

            if (escapeNext)
            {
                escapeNext = false;
                continue;
            }

            if (inSingleQuote)
            {
                if (ch == '\\')
                    escapeNext = true;
                else if (ch == '\'')
                    inSingleQuote = false;
                continue;
            }

            if (inDoubleQuote)
            {
                if (ch == '\\')
                    escapeNext = true;
                else if (ch == '"')
                    inDoubleQuote = false;
                continue;
            }

            switch (ch)
            {
                case '\'':
                    inSingleQuote = true;
                    continue;
                case '"':
                    inDoubleQuote = true;
                    continue;
                case '/' when next == '/':
                    inLineComment = true;
                    i++;
                    continue;
                case '/' when next == '*':
                    inBlockComment = true;
                    i++;
                    continue;
                case '(':
                    parenDepth++;
                    continue;
                case ')':
                    parenDepth--;
                    if (parenDepth == 0)
                        return i;
                    continue;
                case '<' when LooksLikeRecordGenericAngleStart(declaration, i):
                    angleDepth++;
                    continue;
                case '>':
                    if (angleDepth > 0)
                        angleDepth--;
                    continue;
                case '[':
                    bracketDepth++;
                    continue;
                case ']':
                    if (bracketDepth > 0)
                        bracketDepth--;
                    continue;
                case '{':
                    braceDepth++;
                    continue;
                case '}':
                    if (braceDepth > 0)
                        braceDepth--;
                    continue;
            }
        }

        return -1;
    }

    private static int FindRecordDeclarationTerminatorIndex(string declaration, int startIndex)
    {
        var parenDepth = 0;
        var angleDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var inLineComment = false;
        var inBlockComment = false;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var escapeNext = false;

        for (int i = Math.Max(0, startIndex); i < declaration.Length; i++)
        {
            var ch = declaration[i];
            var next = i + 1 < declaration.Length ? declaration[i + 1] : '\0';

            if (inLineComment)
            {
                if (ch == '\n')
                    inLineComment = false;
                continue;
            }

            if (inBlockComment)
            {
                if (ch == '*' && next == '/')
                {
                    inBlockComment = false;
                    i++;
                }

                continue;
            }

            if (escapeNext)
            {
                escapeNext = false;
                continue;
            }

            if (inSingleQuote)
            {
                if (ch == '\\')
                    escapeNext = true;
                else if (ch == '\'')
                    inSingleQuote = false;
                continue;
            }

            if (inDoubleQuote)
            {
                if (ch == '\\')
                    escapeNext = true;
                else if (ch == '"')
                    inDoubleQuote = false;
                continue;
            }

            switch (ch)
            {
                case '\'':
                    inSingleQuote = true;
                    continue;
                case '"':
                    inDoubleQuote = true;
                    continue;
                case '/' when next == '/':
                    inLineComment = true;
                    i++;
                    continue;
                case '/' when next == '*':
                    inBlockComment = true;
                    i++;
                    continue;
                case '(':
                    parenDepth++;
                    continue;
                case ')':
                    if (parenDepth > 0)
                        parenDepth--;
                    continue;
                case '<' when LooksLikeRecordGenericAngleStart(declaration, i):
                    angleDepth++;
                    continue;
                case '>':
                    if (angleDepth > 0)
                        angleDepth--;
                    continue;
                case '[':
                    bracketDepth++;
                    continue;
                case ']':
                    if (bracketDepth > 0)
                        bracketDepth--;
                    continue;
                case '{' when parenDepth == 0 && angleDepth == 0 && bracketDepth == 0 && braceDepth == 0:
                    return i;
                case '{':
                    braceDepth++;
                    continue;
                case '}':
                    if (braceDepth > 0)
                        braceDepth--;
                    continue;
                case ';' when parenDepth == 0 && angleDepth == 0 && bracketDepth == 0 && braceDepth == 0:
                    return i;
            }
        }

        return -1;
    }

    private static IEnumerable<RecordPrimaryComponentSlice> SplitTopLevelRecordPrimaryComponents(string parameterList, int firstLineNumber)
    {
        var builder = new StringBuilder();
        var parenDepth = 0;
        var angleDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var escapeNext = false;
        var currentLineNumber = firstLineNumber;
        var componentLineNumber = firstLineNumber;
        var componentHasToken = false;

        for (int index = 0; index < parameterList.Length; index++)
        {
            var ch = parameterList[index];
            if (!componentHasToken && !char.IsWhiteSpace(ch))
            {
                componentHasToken = true;
                componentLineNumber = currentLineNumber;
            }

            if (escapeNext)
            {
                builder.Append(ch);
                escapeNext = false;
                if (ch == '\n')
                    currentLineNumber++;
                continue;
            }

            if (inSingleQuote)
            {
                builder.Append(ch);
                if (ch == '\\')
                    escapeNext = true;
                else if (ch == '\'')
                    inSingleQuote = false;
                else if (ch == '\n')
                    currentLineNumber++;
                continue;
            }

            if (inDoubleQuote)
            {
                builder.Append(ch);
                if (ch == '\\')
                    escapeNext = true;
                else if (ch == '"')
                    inDoubleQuote = false;
                else if (ch == '\n')
                    currentLineNumber++;
                continue;
            }

            switch (ch)
            {
                case '\'':
                    inSingleQuote = true;
                    builder.Append(ch);
                    continue;
                case '"':
                    inDoubleQuote = true;
                    builder.Append(ch);
                    continue;
                case '(':
                    parenDepth++;
                    builder.Append(ch);
                    continue;
                case ')':
                    if (parenDepth > 0)
                        parenDepth--;
                    builder.Append(ch);
                    continue;
                case '<' when LooksLikeRecordGenericAngleStart(parameterList, index):
                    angleDepth++;
                    builder.Append(ch);
                    continue;
                case '>':
                    if (angleDepth > 0)
                        angleDepth--;
                    builder.Append(ch);
                    continue;
                case '[':
                    bracketDepth++;
                    builder.Append(ch);
                    continue;
                case ']':
                    if (bracketDepth > 0)
                        bracketDepth--;
                    builder.Append(ch);
                    continue;
                case '{':
                    braceDepth++;
                    builder.Append(ch);
                    continue;
                case '}':
                    if (braceDepth > 0)
                        braceDepth--;
                    builder.Append(ch);
                    continue;
                case ',' when parenDepth == 0 && angleDepth == 0 && bracketDepth == 0 && braceDepth == 0:
                    var component = builder.ToString().Trim();
                    if (component.Length > 0)
                        yield return new RecordPrimaryComponentSlice(component, componentLineNumber);
                    builder.Clear();
                    componentHasToken = false;
                    componentLineNumber = currentLineNumber;
                    continue;
                default:
                    builder.Append(ch);
                    if (ch == '\n')
                        currentLineNumber++;
                    continue;
            }
        }

        var trailingComponent = builder.ToString().Trim();
        if (trailingComponent.Length > 0)
            yield return new RecordPrimaryComponentSlice(trailingComponent, componentLineNumber);
    }

    private static string StripRecordComponentComments(string text)
    {
        var builder = new StringBuilder(text.Length);
        var inLineComment = false;
        var inBlockComment = false;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var escapeNext = false;

        for (int i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';

            if (inLineComment)
            {
                if (ch == '\n')
                {
                    inLineComment = false;
                    builder.Append(ch);
                }

                continue;
            }

            if (inBlockComment)
            {
                if (ch == '*' && next == '/')
                {
                    inBlockComment = false;
                    i++;
                    builder.Append(' ');
                }
                else if (ch == '\n')
                {
                    builder.Append(ch);
                }

                continue;
            }

            if (escapeNext)
            {
                builder.Append(ch);
                escapeNext = false;
                continue;
            }

            if (inSingleQuote)
            {
                builder.Append(ch);
                if (ch == '\\')
                    escapeNext = true;
                else if (ch == '\'')
                    inSingleQuote = false;
                continue;
            }

            if (inDoubleQuote)
            {
                builder.Append(ch);
                if (ch == '\\')
                    escapeNext = true;
                else if (ch == '"')
                    inDoubleQuote = false;
                continue;
            }

            if (ch == '\'')
            {
                inSingleQuote = true;
                builder.Append(ch);
                continue;
            }

            if (ch == '"')
            {
                inDoubleQuote = true;
                builder.Append(ch);
                continue;
            }

            if (ch == '/' && next == '/')
            {
                inLineComment = true;
                i++;
                continue;
            }

            if (ch == '/' && next == '*')
            {
                inBlockComment = true;
                i++;
                continue;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    private static bool TryParseRecordPrimaryComponent(string lang, RecordPrimaryComponentSlice rawComponent, out RecordPrimaryComponent component)
    {
        component = default;
        if (string.IsNullOrWhiteSpace(rawComponent.Text))
            return false;

        var normalized = TrimAfterTopLevelEquals(rawComponent.Text).Trim();
        if (normalized.Length == 0)
            return false;

        var componentLine = rawComponent.Line;
        string? visibility = null;
        if (lang == "kotlin")
        {
            var stripped = StripLeadingJavaRecordComponentAnnotations(normalized, allowKotlinUseSiteTargets: true);
            normalized = stripped.Text;
            componentLine += stripped.ConsumedNewlines;

            stripped = StripLeadingKotlinConstructorPropertyModifiers(normalized, out visibility);
            normalized = stripped.Text;
            componentLine += stripped.ConsumedNewlines;

            if (!StartsWithKotlinPropertyKeyword(normalized, out var propertyKeywordLength))
                return false;

            normalized = normalized[propertyKeywordLength..];
            var keywordWhitespaceConsumed = 0;
            normalized = TrimLeadingWhitespaceAndCountNewlines(normalized, ref keywordWhitespaceConsumed);
            componentLine += keywordWhitespaceConsumed;

            var separatorIndex = FindKotlinPropertyTypeSeparatorIndex(normalized);
            if (separatorIndex <= 0)
                return false;

            var kotlinComponentName = normalized[..separatorIndex].Trim();
            var kotlinComponentType = normalized[(separatorIndex + 1)..].Trim();
            if (kotlinComponentName.Length == 0 || kotlinComponentType.Length == 0)
                return false;

            component = new RecordPrimaryComponent(kotlinComponentName, kotlinComponentType, normalized, componentLine, visibility);
            return true;
        }
        else
        {
            var stripped = lang == "csharp"
                ? StripLeadingCSharpRecordComponentAttributes(normalized)
                : StripLeadingJavaRecordComponentAnnotations(normalized, allowKotlinUseSiteTargets: lang == "kotlin");
            normalized = stripped.Text;
            componentLine += stripped.ConsumedNewlines;

            stripped = StripLeadingRecordComponentModifiers(lang, normalized);
            normalized = stripped.Text;
            componentLine += stripped.ConsumedNewlines;
        }
        if (normalized.Length == 0)
            return false;

        var nameMatch = Regex.Match(normalized, @"(?<name>@?[\p{L}_$][\p{L}\p{Nd}_$]*)\s*$", RegexOptions.CultureInvariant);
        if (!nameMatch.Success)
            return false;

        var componentName = nameMatch.Groups["name"].Value.TrimStart('@');
        var componentType = normalized[..nameMatch.Index].Trim();
        if (componentName.Length == 0 || componentType.Length == 0)
            return false;

        component = new RecordPrimaryComponent(componentName, componentType, normalized, componentLine, visibility);
        return true;
    }

    private static string TrimAfterTopLevelEquals(string text)
    {
        var parenDepth = 0;
        var angleDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var escapeNext = false;

        for (int i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (escapeNext)
            {
                escapeNext = false;
                continue;
            }

            if (inSingleQuote)
            {
                if (ch == '\\')
                    escapeNext = true;
                else if (ch == '\'')
                    inSingleQuote = false;
                continue;
            }

            if (inDoubleQuote)
            {
                if (ch == '\\')
                    escapeNext = true;
                else if (ch == '"')
                    inDoubleQuote = false;
                continue;
            }

            switch (ch)
            {
                case '\'':
                    inSingleQuote = true;
                    continue;
                case '"':
                    inDoubleQuote = true;
                    continue;
                case '(':
                    parenDepth++;
                    continue;
                case ')':
                    if (parenDepth > 0)
                        parenDepth--;
                    continue;
                case '<' when LooksLikeRecordGenericAngleStart(text, i):
                    angleDepth++;
                    continue;
                case '>':
                    if (angleDepth > 0)
                        angleDepth--;
                    continue;
                case '[':
                    bracketDepth++;
                    continue;
                case ']':
                    if (bracketDepth > 0)
                        bracketDepth--;
                    continue;
                case '{':
                    braceDepth++;
                    continue;
                case '}':
                    if (braceDepth > 0)
                        braceDepth--;
                    continue;
                case '=' when parenDepth == 0 && angleDepth == 0 && bracketDepth == 0 && braceDepth == 0:
                    return SliceCSharpTrimEnd(text, i);
            }
        }

        return text;
    }

    private static StrippedRecordComponentText StripLeadingCSharpRecordComponentAttributes(string component)
    {
        var consumedNewlines = 0;
        var trimmed = TrimLeadingWhitespaceAndCountNewlines(component, ref consumedNewlines);
        while (trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            var endIndex = FindMatchingBracket(trimmed, 0, '[', ']');
            if (endIndex < 0)
                return new(component.Trim(), 0);

            consumedNewlines += CountNewlines(trimmed.AsSpan(0, endIndex + 1));
            trimmed = TrimLeadingWhitespaceAndCountNewlines(trimmed[(endIndex + 1)..], ref consumedNewlines);
        }

        return new(trimmed, consumedNewlines);
    }

    private static bool LooksLikeRecordGenericAngleStart(string text, int index)
    {
        if (index < 0 || index >= text.Length || text[index] != '<')
            return false;

        var previousIndex = FindPreviousNonWhitespaceIndex(text, index - 1);
        if (previousIndex < 0)
            return false;

        var nextIndex = FindNextNonWhitespaceIndex(text, index + 1);
        if (nextIndex < 0)
            return false;

        var previous = text[previousIndex];
        var next = text[nextIndex];
        if (!IsRecordGenericAnglePredecessor(previous)
            || !IsRecordGenericAngleSuccessor(next))
        {
            return false;
        }

        return TryFindRecordGenericAngleEnd(text, index, out _);
    }

    private static bool IsRecordGenericAnglePredecessor(char ch) =>
        char.IsLetterOrDigit(ch)
        || ch is '_' or '$' or '@' or '.' or '>' or ')' or ']' or '?';

    private static bool IsRecordGenericAngleSuccessor(char ch) =>
        char.IsLetter(ch)
        || ch is '_' or '$' or '@' or '?' or '(';

    private static int FindPreviousNonWhitespaceIndex(string text, int index)
    {
        for (int i = Math.Min(index, text.Length - 1); i >= 0; i--)
        {
            if (!char.IsWhiteSpace(text[i]))
                return i;
        }

        return -1;
    }

    private static int FindNextNonWhitespaceIndex(string text, int index)
    {
        for (int i = Math.Max(0, index); i < text.Length; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
                return i;
        }

        return -1;
    }

    private static bool TryFindRecordGenericAngleEnd(string text, int openIndex, out int closeIndex)
    {
        closeIndex = -1;

        var angleDepth = 0;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var inLineComment = false;
        var inBlockComment = false;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var escapeNext = false;

        for (int i = openIndex; i < text.Length; i++)
        {
            var ch = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';

            if (inLineComment)
            {
                if (ch == '\n')
                    inLineComment = false;
                continue;
            }

            if (inBlockComment)
            {
                if (ch == '*' && next == '/')
                {
                    inBlockComment = false;
                    i++;
                }

                continue;
            }

            if (escapeNext)
            {
                escapeNext = false;
                continue;
            }

            if (inSingleQuote)
            {
                if (ch == '\\')
                    escapeNext = true;
                else if (ch == '\'')
                    inSingleQuote = false;
                continue;
            }

            if (inDoubleQuote)
            {
                if (ch == '\\')
                    escapeNext = true;
                else if (ch == '"')
                    inDoubleQuote = false;
                continue;
            }

            switch (ch)
            {
                case '\'':
                    inSingleQuote = true;
                    continue;
                case '"':
                    inDoubleQuote = true;
                    continue;
                case '/' when next == '/':
                    inLineComment = true;
                    i++;
                    continue;
                case '/' when next == '*':
                    inBlockComment = true;
                    i++;
                    continue;
                case '(':
                    parenDepth++;
                    continue;
                case ')':
                    if (parenDepth > 0)
                        parenDepth--;
                    continue;
                case '[':
                    bracketDepth++;
                    continue;
                case ']':
                    if (bracketDepth > 0)
                        bracketDepth--;
                    continue;
                case '{':
                    braceDepth++;
                    continue;
                case '}':
                    if (braceDepth > 0)
                        braceDepth--;
                    continue;
                case '<' when i == openIndex || LooksLikeRecordGenericAngleCandidate(text, i):
                    angleDepth++;
                    continue;
                case '>':
                    if (angleDepth == 0)
                        continue;

                    angleDepth--;
                    if (angleDepth == 0)
                    {
                        if (!IsRecordGenericAnglePayloadTypeLike(text.AsSpan(openIndex + 1, i - openIndex - 1)))
                            return false;

                        closeIndex = i;
                        return true;
                    }

                    continue;
            }
        }

        return false;
    }

    private static bool LooksLikeRecordGenericAngleCandidate(string text, int index)
    {
        if (index < 0 || index >= text.Length || text[index] != '<')
            return false;

        var previousIndex = FindPreviousNonWhitespaceIndex(text, index - 1);
        if (previousIndex < 0)
            return false;

        var nextIndex = FindNextNonWhitespaceIndex(text, index + 1);
        if (nextIndex < 0)
            return false;

        return IsRecordGenericAnglePredecessor(text[previousIndex])
            && IsRecordGenericAngleSuccessor(text[nextIndex]);
    }

    private static bool IsRecordGenericAnglePayloadTypeLike(ReadOnlySpan<char> text)
    {
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var inLineComment = false;
        var inBlockComment = false;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var escapeNext = false;

        for (int i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';

            if (inLineComment)
            {
                if (ch == '\n')
                    inLineComment = false;
                continue;
            }

            if (inBlockComment)
            {
                if (ch == '*' && next == '/')
                {
                    inBlockComment = false;
                    i++;
                }

                continue;
            }

            if (escapeNext)
            {
                escapeNext = false;
                continue;
            }

            if (inSingleQuote)
            {
                if (ch == '\\')
                    escapeNext = true;
                else if (ch == '\'')
                    inSingleQuote = false;
                continue;
            }

            if (inDoubleQuote)
            {
                if (ch == '\\')
                    escapeNext = true;
                else if (ch == '"')
                    inDoubleQuote = false;
                continue;
            }

            switch (ch)
            {
                case '\'':
                    inSingleQuote = true;
                    continue;
                case '"':
                    inDoubleQuote = true;
                    continue;
                case '/' when next == '/':
                    inLineComment = true;
                    i++;
                    continue;
                case '/' when next == '*':
                    inBlockComment = true;
                    i++;
                    continue;
                case '(':
                    parenDepth++;
                    continue;
                case ')':
                    if (parenDepth > 0)
                        parenDepth--;
                    continue;
                case '[':
                    bracketDepth++;
                    continue;
                case ']':
                    if (bracketDepth > 0)
                        bracketDepth--;
                    continue;
                case '{':
                    braceDepth++;
                    continue;
                case '}':
                    if (braceDepth > 0)
                        braceDepth--;
                    continue;
            }

            if (parenDepth == 0
                && bracketDepth == 0
                && braceDepth == 0
                && ch is '=' or '+' or '-' or '*' or '/' or '%' or '&' or '|' or '!' or '^' or '~' or ';')
            {
                return false;
            }
        }

        return true;
    }

    private static StrippedRecordComponentText StripLeadingJavaRecordComponentAnnotations(string component, bool allowKotlinUseSiteTargets)
    {
        var consumedNewlines = 0;
        var trimmed = TrimLeadingWhitespaceAndCountNewlines(component, ref consumedNewlines);
        while (trimmed.StartsWith("@", StringComparison.Ordinal))
        {
            var index = 1;
            while (index < trimmed.Length && (char.IsLetterOrDigit(trimmed[index]) || trimmed[index] is '_' or '$' or '.'))
                index++;

            if (allowKotlinUseSiteTargets && index < trimmed.Length && trimmed[index] == ':' && KotlinAnnotationTargets.Contains(trimmed[1..index]))
            {
                index++;
                while (index < trimmed.Length && char.IsWhiteSpace(trimmed[index]))
                    index++;
                while (index < trimmed.Length && (char.IsLetterOrDigit(trimmed[index]) || trimmed[index] is '_' or '$' or '.'))
                    index++;
            }

            if (index < trimmed.Length && trimmed[index] == ':')
            {
                index++;
                while (index < trimmed.Length && char.IsWhiteSpace(trimmed[index]))
                    index++;
                while (index < trimmed.Length && (char.IsLetterOrDigit(trimmed[index]) || trimmed[index] is '_' or '$' or '.'))
                    index++;
            }

            while (index < trimmed.Length && char.IsWhiteSpace(trimmed[index]))
                index++;

            if (index < trimmed.Length && trimmed[index] == '(')
            {
                var endIndex = FindMatchingBracket(trimmed, index, '(', ')');
                if (endIndex < 0)
                    return new(component.Trim(), 0);

                index = endIndex + 1;
            }

            consumedNewlines += CountNewlines(trimmed.AsSpan(0, index));
            trimmed = TrimLeadingWhitespaceAndCountNewlines(trimmed[index..], ref consumedNewlines);
        }

        return new(trimmed, consumedNewlines);
    }

    private static StrippedRecordComponentText StripLeadingKotlinConstructorPropertyModifiers(
        string component,
        out string? visibility)
    {
        visibility = null;
        var consumedNewlines = 0;
        var trimmed = TrimLeadingWhitespaceAndCountNewlines(component, ref consumedNewlines);
        string[] modifiers = ["public", "private", "protected", "internal", "vararg"];
        var removedModifier = true;
        while (removedModifier)
        {
            removedModifier = false;
            foreach (var modifier in modifiers)
            {
                if (trimmed.StartsWith(modifier, StringComparison.Ordinal)
                    && trimmed.Length > modifier.Length
                    && char.IsWhiteSpace(trimmed[modifier.Length]))
                {
                    if (modifier is "public" or "private" or "protected" or "internal")
                        visibility ??= modifier;

                    trimmed = TrimLeadingWhitespaceAndCountNewlines(trimmed[(modifier.Length + 1)..], ref consumedNewlines);
                    removedModifier = true;
                    break;
                }
            }
        }

        return new(trimmed, consumedNewlines);
    }

    private static int FindKotlinPropertyTypeSeparatorIndex(string text)
    {
        var parenDepth = 0;
        var angleDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var escapeNext = false;

        for (int i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (escapeNext)
            {
                escapeNext = false;
                continue;
            }

            if (inSingleQuote)
            {
                if (ch == '\\')
                    escapeNext = true;
                else if (ch == '\'')
                    inSingleQuote = false;
                continue;
            }

            if (inDoubleQuote)
            {
                if (ch == '\\')
                    escapeNext = true;
                else if (ch == '"')
                    inDoubleQuote = false;
                continue;
            }

            switch (ch)
            {
                case '\'':
                    inSingleQuote = true;
                    continue;
                case '"':
                    inDoubleQuote = true;
                    continue;
                case '(':
                    parenDepth++;
                    continue;
                case ')':
                    if (parenDepth > 0)
                        parenDepth--;
                    continue;
                case '<':
                    angleDepth++;
                    continue;
                case '>':
                    if (angleDepth > 0)
                        angleDepth--;
                    continue;
                case '[':
                    bracketDepth++;
                    continue;
                case ']':
                    if (bracketDepth > 0)
                        bracketDepth--;
                    continue;
                case '{':
                    braceDepth++;
                    continue;
                case '}':
                    if (braceDepth > 0)
                        braceDepth--;
                    continue;
                case ':' when parenDepth == 0 && angleDepth == 0 && bracketDepth == 0 && braceDepth == 0:
                    return i;
            }
        }

        return -1;
    }

    private static bool StartsWithKotlinPropertyKeyword(string text, out int keywordLength)
    {
        if (text.StartsWith("val", StringComparison.Ordinal)
            && (text.Length == 3 || !IsKotlinPropertyKeywordPart(text[3])))
        {
            keywordLength = 3;
            return true;
        }

        if (text.StartsWith("var", StringComparison.Ordinal)
            && (text.Length == 3 || !IsKotlinPropertyKeywordPart(text[3])))
        {
            keywordLength = 3;
            return true;
        }

        keywordLength = 0;
        return false;
    }

    private static bool IsKotlinPropertyKeywordPart(char ch) =>
        char.IsLetterOrDigit(ch) || ch == '_';

    private static StrippedRecordComponentText StripLeadingRecordComponentModifiers(string lang, string component)
    {
        ReadOnlySpan<string> modifiers = lang == "csharp"
            ? ["params", "this", "ref", "out", "in", "scoped", "readonly"]
            : ["final"];

        var consumedNewlines = 0;
        var trimmed = TrimLeadingWhitespaceAndCountNewlines(component, ref consumedNewlines);
        var removedModifier = true;
        while (removedModifier)
        {
            removedModifier = false;
            foreach (var modifier in modifiers)
            {
                if (trimmed.StartsWith(modifier, StringComparison.Ordinal)
                    && trimmed.Length > modifier.Length
                    && char.IsWhiteSpace(trimmed[modifier.Length]))
                {
                    trimmed = TrimLeadingWhitespaceAndCountNewlines(trimmed[(modifier.Length + 1)..], ref consumedNewlines);
                    removedModifier = true;
                    break;
                }
            }
        }

        return new(trimmed, consumedNewlines);
    }

    private static string TrimLeadingWhitespaceAndCountNewlines(string text, ref int consumedNewlines)
    {
        var index = 0;
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            if (text[index] == '\n')
                consumedNewlines++;
            index++;
        }

        return text[index..];
    }

    private static int CountNewlines(ReadOnlySpan<char> text)
    {
        var count = 0;
        foreach (var ch in text)
        {
            if (ch == '\n')
                count++;
        }

        return count;
    }

    private static int FindMatchingBracket(string text, int openIndex, char openBracket, char closeBracket)
    {
        var depth = 0;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var escapeNext = false;

        for (int i = openIndex; i < text.Length; i++)
        {
            var ch = text[i];
            if (escapeNext)
            {
                escapeNext = false;
                continue;
            }

            if (inSingleQuote)
            {
                if (ch == '\\')
                    escapeNext = true;
                else if (ch == '\'')
                    inSingleQuote = false;
                continue;
            }

            if (inDoubleQuote)
            {
                if (ch == '\\')
                    escapeNext = true;
                else if (ch == '"')
                    inDoubleQuote = false;
                continue;
            }

            if (ch == '\'')
            {
                inSingleQuote = true;
                continue;
            }

            if (ch == '"')
            {
                inDoubleQuote = true;
                continue;
            }

            if (ch == openBracket)
            {
                depth++;
                continue;
            }

            if (ch == closeBracket)
            {
                depth--;
                if (depth == 0)
                    return i;
            }
        }

        return -1;
    }
}
