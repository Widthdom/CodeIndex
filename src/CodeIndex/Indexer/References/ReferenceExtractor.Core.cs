using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    internal static List<ReferenceRecord> ExtractCore(ReferenceExtractionContext request)
    {
        request.CancellationToken.ThrowIfCancellationRequested();
        var fileId = request.FileId;
        var language = request.Language;
        var content = request.Content;
        var symbols = request.Symbols;
        var path = request.Path;
        var workspaceSymbols = request.WorkspaceSymbols;
        var requestedLanguage = request.RequestedLanguage;
        var isJsxFile = IsJsxFilePath(path);
        var isRazorFile = IsRazorFilePath(path) || requestedLanguage is "razor" or "blazor" or "cshtml";

        if (!TryPrepareReferenceLines(language, content, isRazorFile, out var preparedInput))
            return [];
        request.CancellationToken.ThrowIfCancellationRequested();

        content = preparedInput.Content;
        var lines = preparedInput.Lines;
        var structuralLines = preparedInput.StructuralLines;
        var csharpLinesInsideMultilineStringContent = preparedInput.CSharpLinesInsideMultilineStringContent;
        var csharpLinesInsideBlockComment = preparedInput.CSharpLinesInsideBlockComment;
        var referenceStructuralLines = preparedInput.ReferenceStructuralLines;
        var preparedLines = preparedInput.PreparedLines;
        var goImportBlockLines = preparedInput.GoImportBlockLines;
        var luaReferenceLines = preparedInput.LuaReferenceLines;
        var luaPreparedLines = preparedInput.LuaPreparedLines;
        var lispReferenceLines = preparedInput.LispReferenceLines;
        var razorReferenceLines = preparedInput.RazorReferenceLines;
        var razorImplementedTypeNames = preparedInput.RazorImplementedTypeNames;
        var typeScriptNamespaceAliases = preparedInput.TypeScriptNamespaceAliases;
        var typeScriptTypeAliases = language == "typescript"
            ? TypeScriptReferenceExtractor.BuildTypeAliasTargets(preparedLines)
            : null;
        var swiftTypeAliases = language == "swift"
            ? SwiftReferenceExtractor.BuildTypeAliasTargets(preparedLines)
            : null;
        var jsTaggedTemplatesByLine = preparedInput.JsTaggedTemplatesByLine;
        // Pre-pass C# attribute analysis so cross-line `[\n Foo("x")\n]` and parameter
        // attributes `void M([Attr] T x)` are classified consistently with same-line `[Foo]`.
        // 行を跨いだ `[\n Foo("x")\n]` やパラメータ属性 `void M([Attr] T x)` も、同一行の `[Foo]` と
        // 同じ判定で属性として扱えるように、事前パスで C# 属性セクションの範囲を構築する。
        var csharpAttrTables = language == "csharp"
            ? BuildCSharpAttributeRanges(preparedLines)
            : (null, null);
        var csharpAttrRanges = csharpAttrTables.Item1;
        // Top-level (paren-depth 0) zones inside attribute sections. Used by the no-arg
        // attribute regex so that enum / qualified-constant identifiers appearing inside
        // attribute argument lists (e.g. `AllowNumbers` in `[JsonConverter(ConverterStrategy.AllowNumbers)]`)
        // are not misclassified as no-arg attribute references.
        // 属性セクション内で paren 深さ 0 の top-level ゾーンだけを別テーブルで持つ。複数行
        // `[...]` の引数中に現れる enum / 修飾定数（`ConverterStrategy.AllowNumbers` など）が
        // no-arg attribute として誤分類されないよう、no-arg 属性用ゲートに使う。
        var csharpAttrTopLevelRanges = csharpAttrTables.Item2;
        var definitionNamesComparer = GetDefinitionNamesComparer(language);
        var definitionNamesByLine = BuildDefinitionNamesByLine(language, symbols);
        var fileDefinitionNames = isRazorFile
            ? new HashSet<string>(symbols.Select(symbol => symbol.Name), StringComparer.Ordinal)
            : null;
        var sqlDefinitionLeafSpansByLine = language == "sql"
            ? SqlReferenceExtractor.BuildDefinitionLeafSpansByLine(lines, symbols)
            : null;
        var sqlWindowFunctionCallSiteSuppressions = language == "sql"
            ? SqlReferenceExtractor.BuildWindowFunctionCallSiteSuppressions(structuralLines)
            : null;
        var cobolCallableSymbols = language == "cobol"
            ? symbols
                .Where(symbol => symbol.Kind == "function")
                .OrderBy(symbol => symbol.Line)
                .ThenBy(symbol => symbol.StartLine)
                .ThenBy(symbol => symbol.Name, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : null;
        // Include 'property' so expression-bodied and block-bodied property accessors
        // attribute their calls to the property rather than falling through to the
        // enclosing class (see issue #233).
        // 式本体・ブロック本体のプロパティアクセサ内の呼び出しを、外側のクラスではなく
        // プロパティ自身に帰属させる (issue #233 参照)。
        var containerCandidates = BuildReferenceContainerCandidates(symbols);
        var containerResolver = new InnermostContainerResolver(containerCandidates);
        var csharpXmlDocAttachmentScopeCandidates = BuildCSharpXmlDocAttachmentScopeCandidates(language, symbols);
        // Enclosing-type candidates for constructor-chain rewrites (class/struct/record; namespace excluded).
        // Ordered innermost-first via ascending body range. Java enums can declare constructors and
        // chain via `this(...)` so `enum` is included; C# enums cannot declare constructors, and
        // `CSharpCtorChainRegex` will not match inside them, so including `enum` is a no-op there.
        // コンストラクタ連鎖の呼び先解決で使う外側の型候補（class/struct/record/enum。namespace は含めない）。
        // 内側優先で昇順にソート。Java の enum は `this(...)` 連鎖を持てるため `enum` も含める。
        // C# の enum はコンストラクタ自体を持てず `CSharpCtorChainRegex` が一致しないので副作用は無い。
        var enclosingTypeCandidates = BuildEnclosingTypeCandidates(symbols);
        var rustEnumCandidates = language == "rust"
            ? symbols
                .Where(symbol => symbol.Kind == "enum" && symbol.BodyStartLine != null && symbol.BodyEndLine != null)
                .OrderBy(symbol => (symbol.BodyEndLine ?? symbol.EndLine) - (symbol.BodyStartLine ?? symbol.StartLine))
                .ToList()
            : null;
        var pythonDefinitionContainersByLineAndKind = language == "python"
            ? BuildPythonDefinitionContainersByLineAndKind(symbols)
            : null;
        var swiftPropertyDefinitionsByLine = BuildSwiftPropertyDefinitionsByLine(language, symbols);

        // Synthetic function-kind container for C# primary-ctor declarations with a base
        // primary-ctor call such as `record Child(int x) : Parent(x)` or C# 12 `class Child(int x) : Parent(x)`.
        // The range spans the entire declaration header so multi-line forms where `: Parent(x)` sits on a
        // later line are covered. Later lines inside the body keep their real innermost containers.
        // C# のプライマリコンストラクタ宣言（record / class / struct）で base primary-ctor を呼んでいる場合、
        // 宣言ヘッダー全体を合成コンテナで上書きする。`{` / `;` 以降の本体行は通常の container に戻す。
        var recordPrimaryCtorRanges = BuildCSharpPrimaryCtorContainers(language, symbols, structuralLines);
        var csharpQualifiedEnumMemberLookup = BuildCSharpQualifiedEnumMemberLookup(language, symbols);
        var csharpQualifiedConstantPatternMemberLookup = BuildCSharpQualifiedConstantPatternMemberLookup(language, symbols);
        var csharpQualifiedTypePatternLookup = BuildCSharpQualifiedTypePatternLookup(language, symbols);
        var csharpKnownTypeNames = BuildCSharpKnownTypeNames(language, symbols);
        var kotlinConstructorTypeNames = KotlinReferenceExtractor.BuildConstructorTypeNames(language, symbols);
        var kotlinInfixFunctionNames = KotlinReferenceExtractor.BuildInfixFunctionNames(language, symbols);
        KotlinReferenceExtractor.AddDeclaredInfixFunctionNames(language, lines, kotlinInfixFunctionNames);
        var callableDefinitionNames = BuildCallableDefinitionNames(language, symbols);
        var dockerfileStageNames = DockerfileReferenceExtractor.BuildStageNames(language, symbols);
        var dockerfileVariableNames = DockerfileReferenceExtractor.BuildVariableNames(language, symbols);
        var shellCallableNames = ShellReferenceExtractor.BuildCallableNames(language, symbols);
        var shellGlobalAliasNames = ShellReferenceExtractor.BuildGlobalAliasNames(language, symbols);
        var csharpUsingAliases = BuildCSharpUsingAliases(language, symbols, csharpKnownTypeNames);
        var csharpUsingStatics = BuildCSharpUsingStatics(language, symbols);
        var csharpValueReceiverNames = BuildCSharpValueReceiverNamesByContainingType(language, symbols);
        var csharpFunctionValueReceiverNames = BuildCSharpValueReceiverNamesByFunctionStartLine(
            language,
            symbols,
            structuralLines,
            csharpKnownTypeNames,
            csharpUsingAliases);
        var powershellSplatAssignments = language == "powershell"
            ? PowerShellReferenceExtractor.BuildSplatAssignments(preparedLines)
            : null;
        // Workspace-wide same-name type rescue needs cross-file visibility, so the
        // extractor leaves ambiguous unqualified using-static pattern heads for the
        // read path to disambiguate.
        // ワークスペース全体の同名型 rescue には cross-file 可視性が必要なため、
        // extractor は曖昧な unqualified using-static pattern head を残し、
        // read path 側で判定させる。
        bool HasActiveSameFileCSharpTypeCandidate(string typeExpression, int lineNumber)
        {
            _ = lineNumber;
            var normalized = NormalizeCSharpAliasTargetForTypeLookup(typeExpression);
            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            normalized = TrimLeadingCSharpGlobalQualifier(normalized);
            if (csharpKnownTypeNames.Contains(normalized))
                return true;

            var shortName = GetLastQualifiedSegment(normalized);
            return csharpUsingAliases.Any(alias =>
                alias.TargetsType
                && alias.Line <= lineNumber
                && lineNumber >= alias.ScopeStartLine
                && lineNumber <= alias.ScopeEndLine
                && string.Equals(alias.AliasName, shortName, StringComparison.Ordinal));
        }

        var references = new List<ReferenceRecord>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (language == "csharp")
        {
            EmitCSharpAsyncIteratorReferences(fileId, lines, structuralLines, symbols, references, seen);
            EmitCSharpStaticInterfaceMemberImplementationReferences(
                fileId,
                lines,
                structuralLines,
                symbols,
                workspaceSymbols ?? symbols,
                references,
                seen);
        }
        else if (language == "rust")
        {
            RustReferenceExtractor.EmitMultilineAttributeReferences(
                preparedLines,
                references,
                seen,
                fileId,
                (lineNumber, _) => FindInnermostContainer(containerCandidates, lineNumber));
        }
        var pendingCSharpMultiLineTypePattern = default(CSharpMultiLineTypePatternState);
        var pendingCSharpWhereConstraint = language == "csharp" ? new CSharpWhereConstraintState() : null;
        var csharpLocalNamesByFunction = language == "csharp"
            ? new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
            : null;
        var sqlState = language == "sql" ? SqlReferenceExtractor.CreateState() : null;
        var csharpInDelimitedDocComment = false;
        var jvmInDelimitedDocComment = false;
        var phpInDocblock = false;
        SymbolRecord? phpDocblockContainer = null;
        HashSet<string>? phpDocblockPropertyNames = null;

        for (int i = 0; i < lines.Length; i++)
        {
            if ((i & 0x3f) == 0)
                request.CancellationToken.ThrowIfCancellationRequested();

            var lineNumber = i + 1;
            var originalLine = lines[i];
            var preparedLine = luaPreparedLines?[i] ?? lispReferenceLines?[i] ?? preparedLines[i];
            var csharpAttrRangesOnLine = csharpAttrRanges?[i];
            var csharpAttrTopLevelOnLine = csharpAttrTopLevelRanges?[i];
            SymbolRecord? phpLineContainer = null;
            var phpLineContainerResolved = false;

            SymbolRecord? GetPhpLineContainer()
            {
                if (!phpLineContainerResolved)
                {
                    phpLineContainer = containerResolver.Find(lineNumber);
                    phpLineContainerResolved = true;
                }

                return phpLineContainer;
            }

            if (language == "csharp"
                && !(csharpLinesInsideMultilineStringContent?[i] ?? false)
                && TryGetCSharpXmlDocCommentSpan(
                    originalLine,
                    csharpInDelimitedDocComment,
                    csharpLinesInsideBlockComment?[i] ?? false,
                    out var csharpDocCommentStartIndex,
                    out var csharpDocCommentEndExclusive,
                    out var nextCsharpDelimitedDocComment))
            {
                var csharpDocCommentText = originalLine[csharpDocCommentStartIndex..csharpDocCommentEndExclusive];
                if (csharpDocCommentText.IndexOf("cref=\"", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var innermostContainer = containerResolver.Find(lineNumber);
                    var sameLineDeclarationStartColumn = GetCSharpSameLineDocumentedDeclarationStartColumn(
                        originalLine,
                        csharpDocCommentEndExclusive,
                        nextCsharpDelimitedDocComment);
                    var docContainer = FindDocumentedContainer(
                        containerCandidates,
                        structuralLines[i],
                        preparedLine,
                        csharpAttrRangesOnLine,
                        lineNumber,
                        sameLineDeclarationStartColumn);
                    if (docContainer != null
                        && (docContainer.StartLine == lineNumber
                            || CanAttachCSharpXmlDocCommentToNextDeclaration(
                                innermostContainer,
                                csharpXmlDocAttachmentScopeCandidates,
                                csharpAttrRanges,
                                preparedLines,
                                lineNumber,
                                docContainer)))
                    {
                        CSharpReferenceExtractor.EmitDocCrefReferences(
                            csharpDocCommentText,
                            references,
                            seen,
                            fileId,
                            csharpDocCommentStartIndex,
                            csharpDocCommentText.Trim(),
                            lineNumber,
                            docContainer);
                    }
                }
                csharpInDelimitedDocComment = nextCsharpDelimitedDocComment;
            }
            else if (language is "java" or "kotlin"
                     && TryGetJvmDocCommentSpan(
                         originalLine,
                         jvmInDelimitedDocComment,
                         out var jvmDocCommentStartIndex,
                         out var jvmDocCommentEndExclusive,
                         out var jvmSameLineDeclarationStartColumn,
                         out var nextJvmDelimitedDocComment))
            {
                if (jvmDocCommentEndExclusive > jvmDocCommentStartIndex)
                {
                    var docContainer = FindJvmDocumentedContainer(
                        containerCandidates,
                        lines,
                        structuralLines[i],
                        lineNumber,
                        jvmSameLineDeclarationStartColumn);
                    if (docContainer != null)
                    {
                        var docText = originalLine[jvmDocCommentStartIndex..jvmDocCommentEndExclusive];
                        EmitJvmDocLinkReferences(
                            language,
                            docText,
                            references,
                            seen,
                            fileId,
                            jvmDocCommentStartIndex,
                            docText.Trim(),
                            lineNumber,
                            docContainer);
                    }
                }

                jvmInDelimitedDocComment = nextJvmDelimitedDocComment;
            }

            if (language == "r")
            {
                var roxygenContext = originalLine.Trim();
                if (roxygenContext.Length > 0)
                {
                    RReferenceExtractor.EmitRoxygenImportFromReferences(
                        originalLine,
                        references,
                        seen,
                        fileId,
                        roxygenContext,
                        lineNumber,
                        container: null);
                    RReferenceExtractor.EmitRoxygenImportReferences(
                        originalLine,
                        references,
                        seen,
                        fileId,
                        roxygenContext,
                        lineNumber,
                        container: null);
                    RReferenceExtractor.EmitRoxygenMethodReferences(
                        originalLine,
                        references,
                        seen,
                        fileId,
                        roxygenContext,
                        lineNumber,
                        container: null);
                }
            }

            if (language == "php")
            {
                EmitPhpLinePreambleReferences(
                    originalLine,
                    references,
                    seen,
                    fileId,
                    lineNumber,
                    GetPhpLineContainer,
                    ref phpInDocblock,
                    ref phpDocblockContainer,
                    ref phpDocblockPropertyNames);
            }

            if (string.IsNullOrWhiteSpace(preparedLine))
            {
                if (language == "csharp"
                    && (pendingCSharpMultiLineTypePattern.WaitingForHead
                        || pendingCSharpMultiLineTypePattern.PendingTypeExpression != null))
                {
                    continue;
                }

                if (language == "csharp")
                    CSharpReferenceExtractor.FlushPendingMultiLineTypePatternReference(
                        ref pendingCSharpMultiLineTypePattern,
                        csharpQualifiedConstantPatternMemberLookup,
                        csharpUsingAliases,
                        csharpUsingStatics,
                        HasActiveSameFileCSharpTypeCandidate,
                        references,
                        seen,
                        fileId);
                continue;
            }

            var context = originalLine.Trim();
            if (context.Length == 0)
                continue;

            var definitionNames = definitionNamesByLine.TryGetValue(lineNumber, out var namesOnLine)
                ? namesOnLine
                : null;
            Dictionary<string, int>? definitionNameIndices = null;
            if (definitionNames != null && language != "sql")
            {
                definitionNameIndices = new Dictionary<string, int>(definitionNamesComparer);
                foreach (var definitionName in definitionNames)
                {
                    var definitionIndex = preparedLine.IndexOf(definitionName, StringComparison.Ordinal);
                    if (definitionIndex >= 0)
                        definitionNameIndices[definitionName] = definitionIndex;
                }
            }
            List<SqlReferenceExtractor.DefinitionLeafSpan>? sqlDefinitionLeafSpans = null;
            if (language == "sql")
                sqlDefinitionLeafSpansByLine?.TryGetValue(lineNumber, out sqlDefinitionLeafSpans);
            var container = containerResolver.Find(lineNumber);

            // Per-line Java same-line ctor synthesis. When `public Leaf(){super(0); doWork();}`
            // is entirely on one line, SymbolExtractor does not emit a function symbol for the
            // ctor (its method regex requires the line to end with `{`), so `FindInnermostContainer`
            // returns the enclosing `class:Leaf`. Body-level calls such as `doWork()` would then
            // attach to the class rather than the ctor. We pre-compute a synthetic function-kind
            // container covering the body `{ ... }` region on the current line, so those calls
            // land on `function:Leaf` and `callers Leaf` reflects what the ctor actually does.
            // 同一行 ctor は function symbol が作られないため、body `{ ... }` 内の通常 call が
            // 外側クラスに吸われてしまう。合成 function コンテナを per-line で構築して差し替える。
            (SymbolRecord Synthetic, int NameIndex, int OpenBraceIndex, int CloseBraceIndex)? javaSameLineCtor = null;
            if (language == "java")
            {
                javaSameLineCtor = JavaReferenceExtractor.TryBuildSameLineCtorSpan(
                    preparedLine,
                    lineNumber,
                    enclosingTypeCandidates);
            }

            // Per-call-site record primary-ctor override: only calls whose column sits inside the
            // record header (not in a braced body on the same line) should land on the synthetic
            // ctor. Overriding `container` for the whole line would steal body-level calls such as
            // `record Child(int V) : Parent(V) { public int Sum() => Add(V, 1); }` where `Add(...)`
            // lives past the header-terminating `{` and must stay with its real innermost container.
            // 同一行 record で `{` より後ろの本体呼び出しまで合成 ctor に奪われないよう、コール単位で
            // ヘッダ範囲（end line の end column より前）に入っているかを判定して差し替える。
            SymbolRecord? ResolveContainerForCall(int column)
            {
                foreach (var (rangeStart, rangeStartColumn, rangeEnd, rangeEndColumn, syntheticRecordCtor) in recordPrimaryCtorRanges)
                {
                    if (lineNumber < rangeStart || lineNumber > rangeEnd)
                        continue;
                    if (lineNumber == rangeStart && column < rangeStartColumn)
                        continue;
                    if (lineNumber == rangeEnd && column >= rangeEndColumn)
                        continue;
                    return syntheticRecordCtor;
                }

                // Java same-line ctor body override: calls whose column sits strictly inside the
                // `{ ... }` block on the ctor declaration line attach to the synthetic function-kind
                // ctor instead of the enclosing class container. When no matching `}` is found on
                // the same line (CloseBraceIndex < 0), the body extends beyond the current line —
                // in that case SymbolExtractor emits a real ctor function symbol (its regex matches
                // because the line ends with `{`), so this override is only needed for the fully
                // same-line shape where the matching `}` exists on the same line.
                // Java の same-line ctor では `{ ... }` 内の call を合成 function コンテナに振り向ける。
                if (javaSameLineCtor != null)
                {
                    var info = javaSameLineCtor.Value;
                    if (info.CloseBraceIndex >= 0
                        && column > info.OpenBraceIndex
                        && column < info.CloseBraceIndex)
                    {
                        return info.Synthetic;
                    }
                }

                if (language == "csharp")
                {
                    if (CSharpWhereClauseRegex.IsMatch(preparedLine))
                    {
                        var declarationRangeContainer = FindInnermostCSharpDeclarationRangeContainer(
                            containerCandidates,
                            structuralLines[i],
                            lineNumber,
                            column);
                        if (declarationRangeContainer != null)
                            return declarationRangeContainer;
                    }

                    var sameLineContainer = FindInnermostSameLineCSharpContainer(
                        containerCandidates,
                        structuralLines[i],
                        lineNumber,
                        column);
                    if (sameLineContainer != null)
                        return sameLineContainer;

                    if (CSharpWhereClauseRegex.IsMatch(preparedLine)
                        && container?.Kind == "function"
                        && container.StartLine == lineNumber
                        && (!TryFindCSharpFunctionNameColumn(structuralLines[i], container.Name, out var containerNameColumn)
                            || column < containerNameColumn))
                    {
                        return null;
                    }
                }

                return container;
            }

            SymbolRecord? ResolvePythonDefinitionContainer(int line, string kind)
            {
                if (pythonDefinitionContainersByLineAndKind == null)
                    return null;
                return pythonDefinitionContainersByLineAndKind.TryGetValue((line, kind), out var symbol)
                    ? symbol
                    : null;
            }

            SymbolRecord? ResolveSwiftPropertyContainerForCall(int column)
            {
                if (swiftPropertyDefinitionsByLine != null
                    && swiftPropertyDefinitionsByLine.TryGetValue(lineNumber, out var sameLineProperties))
                {
                    foreach (var property in sameLineProperties)
                    {
                        if ((property.StartColumn ?? 0) <= column)
                            return property;
                    }
                }

                return ResolveContainerForCall(column);
            }

            if (isJsxFile && (language is "javascript" or "typescript"))
            {
                var jsxTypeArgumentSkipUntil = -1;
                foreach (Match match in JsxElementOpenRegex.Matches(preparedLine))
                {
                    if (match.Index < jsxTypeArgumentSkipUntil)
                        continue;

                    var fullName = match.Groups["name"].Value;
                    var nameIndex = match.Groups["name"].Index;
                    var jsxContainer = ResolveContainerForCall(nameIndex);
                    var firstDotIndex = fullName.IndexOf('.');
                    var tagEndIndex = nameIndex + fullName.Length;

                    AddReference(
                        references,
                        seen,
                        fileId,
                        firstDotIndex < 0 ? fullName : fullName[..firstDotIndex],
                        nameIndex,
                        "call",
                        context,
                        lineNumber,
                        jsxContainer);

                    var dotIndex = fullName.LastIndexOf('.');
                    if (dotIndex > 0 && dotIndex + 1 < fullName.Length)
                    {
                        AddReference(
                            references,
                            seen,
                            fileId,
                            fullName[(dotIndex + 1)..],
                            nameIndex + dotIndex + 1,
                            "call",
                            context,
                            lineNumber,
                            jsxContainer);
                    }

                    if (language == "typescript")
                    {
                        var genericStart = SkipWhitespace(preparedLine, tagEndIndex);
                        if (genericStart < preparedLine.Length && preparedLine[genericStart] == '<')
                        {
                            var genericEnd = genericStart;
                            if (TrySkipTypeScriptJsxTypeArguments(preparedLine, ref genericEnd)
                                && genericEnd > genericStart + 2)
                            {
                                jsxTypeArgumentSkipUntil = Math.Max(jsxTypeArgumentSkipUntil, genericEnd);
                                AddTypeExpressionSegments(
                                    references,
                                    seen,
                                    fileId,
                                    preparedLine.Substring(genericStart + 1, genericEnd - genericStart - 2),
                                    genericStart + 1,
                                    context,
                                    lineNumber,
                                    jsxContainer,
                                    "typescript");
                            }
                        }
                    }
                }
            }

            if (language == "csharp")
            {
                CSharpReferenceExtractor.AdvanceMultiLineTypePatternState(
                    preparedLine,
                    context,
                    lineNumber,
                    ResolveContainerForCall,
                    csharpQualifiedConstantPatternMemberLookup,
                    csharpUsingAliases,
                    csharpUsingStatics,
                    HasActiveSameFileCSharpTypeCandidate,
                    references,
                    seen,
                    fileId,
                    ref pendingCSharpMultiLineTypePattern);
            }

            bool ShouldSuppressDefinitionCall(string resolvedName, int callIndex)
            {
                if (definitionNames == null)
                    return false;

                if (language == "csharp")
                {
                    if (context.Contains("when", StringComparison.Ordinal))
                        return false;
                }

                if (language != "sql")
                    return definitionNameIndices != null
                        && definitionNameIndices.TryGetValue(resolvedName, out var definitionIndex)
                        && callIndex == definitionIndex;

                return SqlReferenceExtractor.ShouldSuppressDefinitionCall(sqlDefinitionLeafSpans, resolvedName, callIndex);
            }

            // Event subscription/unsubscription (C#) / イベント購読・解除 (C#)
            if (language is "csharp")
            {
                foreach (Match match in EventSubscriptionRegex.Matches(preparedLine))
                {
                    var eventContainer = ResolveContainerForCall(match.Groups["name"].Index);
                    AddReference(references, seen, fileId, match, "subscribe", context, lineNumber, eventContainer);
                }
            }

            // Constructor chain-call rewrites: C# `: this(...)` / `: base(...)`, Java `this(...)` / `super(...)`,
            // and Kotlin `constructor(...) : this(...)` / `: super(...)`.
            // コンストラクタ連鎖呼び出しの書き換え
            if (language is "csharp")
            {
                CSharpReferenceExtractor.EmitCtorChainReferences(
                    preparedLine, enclosingTypeCandidates, containerCandidates,
                    structuralLines, references, seen, fileId, context, lineNumber, container);
            }
            else if (language is "java")
            {
                JavaReferenceExtractor.EmitCtorChainReferences(
                    preparedLine, enclosingTypeCandidates, symbols, structuralLines,
                    references, seen, fileId, context, lineNumber, container);
            }
            else if (language is "kotlin")
            {
                KotlinReferenceExtractor.EmitCtorDelegationReferences(
                    preparedLine, enclosingTypeCandidates, symbols, structuralLines,
                    references, seen, fileId, context, lineNumber, container);
            }

            // Compile-time type/member references that CallRegex cannot see because the
            // argument has no trailing `(` of its own. See issue #253.
            // 末尾の `(` を持たず CallRegex では取れないコンパイル時の型/メンバ参照。issue #253 参照。
            if (language is "csharp")
            {
                var csharpGenericParameterNames = CollectCSharpGenericParameterNamesForDeclaration(preparedLine);
                foreach (Match match in CSharpTypeKeywordIntroRegex.Matches(preparedLine))
                {
                    int parenIndex = match.Index + match.Length - 1; // position of '(' / '(' の位置
                    ExtractCSharpTypeKeywordSegments(
                        references, seen, fileId, preparedLine, parenIndex + 1,
                        context, lineNumber, container, language, csharpGenericParameterNames);
                }
                ExtractCSharpReflectionNameLiteralReferences(
                    references, seen, fileId, preparedLine, originalLine, context, lineNumber, container);
            }
            else if (language is "java")
            {
                JavaReferenceExtractor.EmitDotClassTypeLiteralReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container);
            }
            else if (language is "kotlin")
            {
                KotlinReferenceExtractor.EmitClassLiteralReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container);
                KotlinReferenceExtractor.EmitBacktickConstructorReferences(
                    preparedLine,
                    kotlinConstructorTypeNames,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    ResolveContainerForCall);
            }

            // Type-position references without an introducing keyword-call: base lists,
            // declaration types, generic constraints, throws clauses, type tests, and
            // XML-doc crefs. These are dependency edges for `references` / `impact`, but
            // not invocation edges for default `callers` / `callees`. See issue #256.
            // キーワード呼び出しの外にある型位置参照（継承リスト、宣言型、generic 制約、
            // throws、型テスト、XML doc cref）。`references` / `impact` では依存として扱うが、
            // 既定の `callers` / `callees` では呼び出しエッジではない。issue #256 参照。
            if (language is "csharp" or "java" or "kotlin")
            {
                EmitCatchTypeReferences(
                    language,
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    ResolveContainerForCall);
            }

            if (language == "csharp")
            {
                EmitCSharpLambdaCaptureReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container,
                    csharpLocalNamesByFunction);

                CSharpReferenceExtractor.EmitTypePositionReferences(
                    preparedLine,
                    originalLine,
                    csharpQualifiedConstantPatternMemberLookup,
                    csharpQualifiedTypePatternLookup,
                    csharpUsingAliases,
                    csharpUsingStatics,
                    HasActiveSameFileCSharpTypeCandidate,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    ResolveContainerForCall,
                    container,
                    pendingCSharpWhereConstraint!,
                    ref pendingCSharpMultiLineTypePattern);

                if (CSharpReferenceExtractor.HasTrailingIsAsTypePatternIntro(preparedLine, originalLine))
                {
                    CSharpReferenceExtractor.StartWaitingForMultiLineTypePatternHead(ref pendingCSharpMultiLineTypePattern);
                }

                if (CSharpReferenceExtractor.HasTrailingCaseTypePatternIntro(preparedLine, originalLine))
                {
                    CSharpReferenceExtractor.StartWaitingForMultiLineTypePatternHead(ref pendingCSharpMultiLineTypePattern);
                }

                TrackCSharpLocalDeclarations(preparedLine, container, csharpLocalNamesByFunction);
            }
            else if (language == "java")
            {
                JavaReferenceExtractor.EmitModuleDirectiveReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    ResolveContainerForCall);

                JavaReferenceExtractor.EmitTypePositionReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    ResolveContainerForCall,
                    container);
            }
            else if (language == "typescript")
            {
                TypeScriptReferenceExtractor.EmitTypePositionReferences(
                    preparedLines,
                    lines,
                    i,
                    preparedLine,
                    lines[i],
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    ResolveContainerForCall,
                    typeScriptNamespaceAliases);

                TypeScriptReferenceExtractor.EmitDeclarationTypeReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    ResolveContainerForCall);

                TypeScriptReferenceExtractor.EmitAliasTargetReferences(
                    preparedLine,
                    typeScriptTypeAliases!,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    ResolveContainerForCall);
            }
            else if (language == "kotlin")
            {
                KotlinReferenceExtractor.EmitTypePositionReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    ResolveContainerForCall);
            }
            else if (language == "swift")
            {
                SwiftReferenceExtractor.EmitTypePositionReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    ResolveContainerForCall,
                    ResolveSwiftPropertyContainerForCall);
                SwiftReferenceExtractor.EmitAliasTargetReferences(
                    preparedLine,
                    swiftTypeAliases!,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    ResolveContainerForCall);
            }
            else if (language == "rust")
            {
                var rustEnumContainer = rustEnumCandidates != null
                    ? FindInnermostContainer(rustEnumCandidates, lineNumber)
                    : null;
                var rustTypePositionLine = RustReferenceExtractor.MaskAttributeBodies(preparedLine);
                RustReferenceExtractor.EmitTypePositionReferences(
                    rustTypePositionLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    ResolveContainerForCall,
                    container,
                    rustEnumContainer);
            }
            else if (language == "c")
                CReferenceExtractor.EmitTypePositionReferences(preparedLine, originalLine, references, seen, fileId, context, lineNumber, ResolveContainerForCall);
            else if (language == "cpp")
                CppReferenceExtractor.EmitTypePositionReferences(preparedLine, originalLine, references, seen, fileId, context, lineNumber, ResolveContainerForCall);
            else if (language == "go")
            {
                GoReferenceExtractor.EmitConcurrencyReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    ResolveContainerForCall);
                GoReferenceExtractor.EmitTypePositionReferences(preparedLine, originalLine, references, seen, fileId, context, lineNumber, ResolveContainerForCall, goImportBlockLines?[i] == true);
            }
            else if (language == "dart")
                DartReferenceExtractor.EmitTypePositionReferences(preparedLine, references, seen, fileId, context, lineNumber, ResolveContainerForCall);
            else if (language == "vb")
                VisualBasicReferenceExtractor.EmitTypePositionReferences(preparedLine, references, seen, fileId, context, lineNumber, ResolveContainerForCall);
            else if (language == "fortran")
                FortranReferenceExtractor.EmitTypePositionReferences(preparedLine, originalLine, references, seen, fileId, context, lineNumber, ResolveContainerForCall, container);
            else if (language == "pascal")
                PascalReferenceExtractor.EmitTypePositionReferences(preparedLine, references, seen, fileId, context, lineNumber, ResolveContainerForCall, container);
            else if (language == "objc")
                ObjectiveCReferenceExtractor.EmitTypePositionReferences(preparedLine, references, seen, fileId, context, lineNumber, ResolveContainerForCall, container);
            else if (language == "haskell")
                HaskellReferenceExtractor.EmitTypePositionReferences(preparedLine, references, seen, fileId, context, lineNumber, container);
            else if (language == "elixir")
                ElixirReferenceExtractor.EmitTypePositionReferences(preparedLine, references, seen, fileId, context, lineNumber, container);
            else if (language == "lua")
                LuaReferenceExtractor.EmitTypePositionReferences(luaReferenceLines?[i] ?? originalLine, references, seen, fileId, context, lineNumber, container);
            else if (language == "css")
            {
                CssReferenceExtractor.EmitCss(
                    preparedLine,
                    originalLine,
                    context,
                    lineNumber,
                    references,
                    seen,
                    fileId,
                    definitionNames,
                    container);
            }

            if (language == "terraform")
            {
                TerraformReferenceExtractor.Emit(
                    preparedLine,
                    context,
                    lineNumber,
                    references,
                    seen,
                    fileId,
                    definitionNames,
                    container);
            }

            if (language == "dockerfile")
            {
                DockerfileReferenceExtractor.EmitStageReferences(
                    preparedLine,
                    originalLine,
                    context,
                    lineNumber,
                    references,
                    seen,
                    fileId,
                    dockerfileStageNames,
                    container);
                DockerfileReferenceExtractor.EmitVariableReferences(
                    preparedLine,
                    context,
                    lineNumber,
                    references,
                    seen,
                    fileId,
                    dockerfileVariableNames,
                    container);
            }

            if (language == "cobol")
            {
                CobolReferenceExtractor.Emit(
                    lines[i],
                    context,
                    lineNumber,
                    references,
                    seen,
                    fileId,
                    container,
                    cobolCallableSymbols);
            }

            var sqlSuppressedCallIndices = language is "sql"
                ? SqlReferenceExtractor.Emit(
                    structuralLines[i],
                    context,
                    lineNumber,
                    references,
                    seen,
                    fileId,
                    sqlState!,
                    ResolveContainerForCall,
                    name => IsIgnoredCallName(language, name),
                    ShouldSuppressDefinitionCall)
                : null;

            if (language == "css")
            {
                CssReferenceExtractor.EmitScss(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container);
            }

            // C# / Java parenless initializers: `new T { ... }` / `new T<U> { ... }` /
            // `new T[] { ... }` etc. CallRegex requires a trailing `(`, so these forms slip
            // through and the type is otherwise never recorded as instantiated. Emit an
            // `instantiate` row here so `references` / `callers` / `impact` see the edge.
            // See issue #286.
            // 括弧省略の C# / Java インスタンス化 (`new T { ... }` 等) は CallRegex で拾えないため、
            // 専用パスで `instantiate` を発行する。issue #286 参照。
            if (language is "csharp" or "java")
            {
                var matchedInitializerIndices = new HashSet<int>();
                foreach (Match match in CSharpJavaInitializerRegex.Matches(preparedLine))
                {
                    var rawName = match.Groups["name"].Value;
                    var nameIndex = match.Groups["name"].Index;
                    matchedInitializerIndices.Add(nameIndex);
                    if (ShouldSkipInitializerName(language, rawName))
                        continue;
                    // Do NOT skip when the type is defined in the same file — the CallRegex
                    // `IsConstructorCallName` path emits `instantiate` without a definitionNames
                    // filter, so `new Foo { ... }` and `new Foo()` should behave the same way.
                    // 同一ファイル内定義でもスキップしない。`IsConstructorCallName` 経路の
                    // `instantiate` が同様の扱いをしているため、括弧あり/なしで挙動を揃える。
                    var initContainer = ResolveContainerForCall(nameIndex);
                    var name = language == "csharp" ? NormalizeCSharpIdentifier(rawName) : rawName;
                    AddReference(references, seen, fileId, name, nameIndex, "instantiate", context, lineNumber, initContainer, language);
                }

                // The initializer regex has the same one-level generic ceiling as CallRegex,
                // so nested generic targets like `new Dictionary<string, List<int>> { ... }`
                // need a depth-aware fallback to keep the outer `instantiate` edge.
                // initializer regex も CallRegex と同じく generic を 1 段までしか見ないため、
                // `new Dictionary<string, List<int>> { ... }` の外側型は depth-aware fallback
                // で補って `instantiate` を落とさないようにする。
                if (language == "csharp")
                {
                    foreach (var candidate in EnumerateNestedGenericInitializerCandidates(
                                 preparedLine,
                                 matchedInitializerIndices,
                                 requireOpeningBrace: true))
                    {
                        if (ShouldSkipInitializerName(language, candidate.Name))
                            continue;

                        var initContainer = ResolveContainerForCall(candidate.NameIndex);
                        AddReference(
                            references,
                            seen,
                            fileId,
                            candidate.Name,
                            candidate.NameIndex,
                            "instantiate",
                            context,
                            lineNumber,
                            initContainer,
                            language);
                    }
                }

                // Allman-style multi-line form: `new T` at end of current line with the
                // opening `{` on the next non-blank prepared line. Peek forward to confirm
                // before emitting, so trailing `new T` patterns that are not followed by `{`
                // (e.g. `var a = new Foo\n;` or `var a = new Foo\n(1, 2);`) do not produce
                // phantom `instantiate` rows.
                // Allman スタイルの多行形式: 現在行末の `new T` と次の非空 prepared line 冒頭の
                // `{` を合わせて 1 つの instantiate として扱う。`{` が続かない場合（`;` や `(` が
                // 後続する等）には幻行を出さないため、peek で確認してから発行する。
                var trailingMatch = CSharpJavaInitializerTrailingRegex.Match(preparedLine);
                var peek = i + 1;
                while (peek < preparedLines.Length && string.IsNullOrWhiteSpace(preparedLines[peek]))
                    peek++;
                if (peek < preparedLines.Length)
                {
                    var nextContent = preparedLines[peek].TrimStart();
                    if (nextContent.Length > 0 && nextContent[0] == '{')
                    {
                        if (trailingMatch.Success)
                        {
                            var rawName = trailingMatch.Groups["name"].Value;
                            var nameIndex = trailingMatch.Groups["name"].Index;
                            matchedInitializerIndices.Add(nameIndex);
                            if (!ShouldSkipInitializerName(language, rawName))
                            {
                                var initContainer = ResolveContainerForCall(nameIndex);
                                var name = language == "csharp" ? NormalizeCSharpIdentifier(rawName) : rawName;
                                AddReference(references, seen, fileId, name, nameIndex, "instantiate", context, lineNumber, initContainer);
                            }

                        }

                        if (language == "csharp")
                        {
                            foreach (var candidate in EnumerateNestedGenericInitializerCandidates(
                                         preparedLine,
                                         matchedInitializerIndices,
                                         requireOpeningBrace: false))
                            {
                                if (ShouldSkipInitializerName(language, candidate.Name))
                                    continue;

                                var initContainer = ResolveContainerForCall(candidate.NameIndex);
                                var name = language == "csharp" ? NormalizeCSharpIdentifier(candidate.Name) : candidate.Name;
                                AddReference(
                                    references,
                                    seen,
                                    fileId,
                                    name,
                                    candidate.NameIndex,
                                    "instantiate",
                                    context,
                                    lineNumber,
                                    initContainer);
                            }
                        }
                    }
                }
            }

            if (language == "css")
            {
                CssReferenceExtractor.EmitScss(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container);
            }

            if (language == "php")
            {
                PhpReferenceExtractor.EmitStaticAccessReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container);

                PhpReferenceExtractor.EmitInstanceofReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container);

                PhpReferenceExtractor.EmitCatchTypeReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container);

                PhpReferenceExtractor.EmitReturnTypeReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container);

                PhpReferenceExtractor.EmitParameterTypeReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container);

                PhpReferenceExtractor.EmitPropertyTypeReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container);

                PhpReferenceExtractor.EmitInheritanceTypeReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container);

                PhpReferenceExtractor.EmitUseTypeReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container);

                PhpReferenceExtractor.EmitUseFunctionReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container);

                PhpReferenceExtractor.EmitUseConstReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container);

                PhpReferenceExtractor.EmitObjectMemberAccessReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container);
            }

            if (language is "javascript" or "typescript")
            {
                JavaScriptReferenceExtractor.EmitOptionalMemberChainReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    ResolveContainerForCall);

                JavaScriptReferenceExtractor.EmitDiscriminantStringGuardReferences(
                    referenceStructuralLines[i],
                    originalLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    ResolveContainerForCall);

                JavaScriptReferenceExtractor.EmitParenlessConstructorReferences(
                    preparedLine,
                    preparedLines,
                    i,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    ResolveContainerForCall);
            }

            void AddCallLikeReference(string name, int callIndex) =>
                _ = TryAddCallLikeReference(name, callIndex);

            void AddPowerShellParameterReference(string name, int callIndex)
            {
                var callContainer = ResolveContainerForCall(callIndex);
                AddReference(references, seen, fileId, name, callIndex, "parameter", context, lineNumber, callContainer, language);
            }

            bool TryAddCallLikeReference(string name, int callIndex)
            {
                var normalizedName = language == "fsharp" && FSharpReferenceExtractor.IsOperatorCallName(name)
                    ? $"operator {name}"
                    : language == "rust"
                        ? RustReferenceExtractor.NormalizeIdentifier(name)
                        : NormalizeAtPrefixedIdentifier(name);

                if (language == "rust" && RustReferenceExtractor.IsFunctionDeclarationCallSite(preparedLine, callIndex))
                    return false;
                if (language == "rust" && RustReferenceExtractor.IsDeriveAttributeCallSite(preparedLine, normalizedName, callIndex))
                    return false;
                if (language == "kotlin" && KotlinReferenceExtractor.IsInfixFunctionDeclarationSite(preparedLine, callIndex))
                    return false;

                // Suppress the same-line Java ctor declarator's self-call. CallRegex matches
                // `CtorName(` at the declarator once per same-line ctor, but it is a declaration
                // site — not a call — so attributing it to `class:CtorName` produces a phantom
                // `CtorName|call|class|CtorName` edge. `definitionNames` does not cover this
                // because same-line ctors do not appear in the symbol table.
                // 同一行 ctor の宣言子 `CtorName(` は呼び出しではないため CallRegex の対象から除外する。
                if (javaSameLineCtor != null
                    && callIndex == javaSameLineCtor.Value.NameIndex
                    && string.Equals(normalizedName, javaSameLineCtor.Value.Synthetic.Name, StringComparison.Ordinal))
                {
                    return false;
                }

                // C# positional patterns such as `case Point(var x, var y):` are type-pattern
                // heads, not calls. `CallRegex` still sees `Point(` and would otherwise emit a
                // phantom `call` edge alongside the real `type_reference`.
                // C# の positional pattern (`case Point(var x, var y):`) は型パターンの先頭であり、
                // 呼び出しではない。`CallRegex` が `Point(` を拾ってしまうため、そのままだと
                // 本物の `type_reference` に加えて phantom な `call` エッジが出る。
                var isCSharpPatternHeadCallSite = language == "csharp"
                    && CSharpReferenceExtractor.IsPatternHeadCallSite(preparedLines, i, preparedLine, callIndex);
                if (isCSharpPatternHeadCallSite)
                    return false;
                if (language == "typescript" && TypeScriptReferenceExtractor.IsSatisfiesTypeOperand(preparedLine, callIndex))
                    return false;

                var callContainer = ResolveContainerForCall(callIndex);
                if (IsConstructorCallName(language, preparedLine, callIndex))
                {
                    AddReference(references, seen, fileId, normalizedName, callIndex, "instantiate", context, lineNumber, callContainer, language);
                    return true;
                }
                if (language == "rust"
                    && RustReferenceExtractor.IsLikelyInstantiationCallName(name, normalizedName, preparedLine, callIndex))
                {
                    AddReference(references, seen, fileId, normalizedName, callIndex, "instantiate", context, lineNumber, callContainer, language);
                    return true;
                }
                if (IsIgnoredCallName(language, name))
                {
                    if (!(language == "scala" && string.Equals(name, "foreach", StringComparison.Ordinal)))
                        return false;
                }
                if (ShouldSuppressDefinitionCall(normalizedName, callIndex))
                    return false;

                // issue #293: reclassify C# attribute / Java/Kotlin/Scala/TypeScript annotation
                // usages with arguments so they do not pollute the call-graph as phantom `call` rows.
                // issue #293: 引数付きの C# attribute と Java/Kotlin/Scala/TypeScript annotation 使用を
                // `call` ではなく専用の種別に分類し、call-graph の phantom エッジを防ぐ。
                var insideCSharpAttributeRange = csharpAttrRangesOnLine != null
                    && IsInsideCSharpAttributeRange(csharpAttrRangesOnLine, callIndex);
                var metadataKind = TryClassifyMetadataReference(language, preparedLine, callIndex, insideCSharpAttributeRange);
                if (metadataKind != null)
                {
                    AddReference(references, seen, fileId, normalizedName, callIndex, metadataKind, context, lineNumber, callContainer, language);
                    if (language == "csharp"
                        && metadataKind == "attribute"
                        && CSharpReferenceExtractor.TryGetCallerInfoAttributeTypeName(name, preparedLine, callIndex) is { } callerInfoAttributeTypeName)
                    {
                        AddReference(
                            references,
                            seen,
                            fileId,
                            callerInfoAttributeTypeName,
                            callIndex,
                            "type_reference",
                            context,
                            lineNumber,
                            callContainer,
                            language);
                    }
                    return true;
                }

                if (language == "kotlin" && KotlinReferenceExtractor.IsConstructorCallName(normalizedName, kotlinConstructorTypeNames))
                {
                    AddReference(references, seen, fileId, normalizedName, callIndex, "instantiate", context, lineNumber, callContainer);
                    return true;
                }

                if (language is "javascript" or "typescript"
                    && SymbolExtractor.IsJavaScriptTypeScriptReactHookName(normalizedName))
                {
                    AddReference(references, seen, fileId, normalizedName, callIndex, "consumes_hook", context, lineNumber, callContainer);
                    return true;
                }

                AddReference(references, seen, fileId, normalizedName, callIndex, "call", context, lineNumber, callContainer);
                return true;
            }

            if (language is "batch")
                BatchReferenceExtractor.EmitJumpTargetReferences(
                    originalLine,
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    ResolveContainerForCall);

            if (language is "assembly")
                AssemblyReferenceExtractor.EmitInstructionTargetReferences(
                    originalLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    ResolveContainerForCall);

            var matchedCallIndices = new HashSet<int>();
            if (language is "commonlisp" or "racket")
            {
                LispReferenceExtractor.EmitReferences(
                    language,
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    ResolveContainerForCall,
                    definitionNames);
            }
            else if (language is "powershell")
            {
                PowerShellReferenceExtractor.EmitCallReferences(preparedLine, AddCallLikeReference);
                PowerShellReferenceExtractor.EmitSplatParameterReferences(
                    preparedLine,
                    powershellSplatAssignments!,
                    lineNumber,
                    AddPowerShellParameterReference);
            }
            else if (language is "shell")
            {
                ShellReferenceExtractor.EmitReferences(
                    preparedLine,
                    originalLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    shellCallableNames,
                    shellGlobalAliasNames,
                    ResolveContainerForCall,
                    AddCallLikeReference);
            }
            else if (language is "assembly")
            {
                // Assembly references are operand-driven, not `name(...)` call syntax. Running the
                // shared CallRegex would misread addressing forms such as `foo(%rip)` as calls.
            }
            else
            {
                foreach (Match match in CallRegex.Matches(preparedLine))
                {
                    var name = match.Groups["name"].Value;
                    var callIndex = match.Groups["name"].Index;
                    if (language == "rust" && RustReferenceExtractor.IsRawIdentifierPrefix(preparedLine, callIndex))
                        continue;
                    if (language == "objc" && IsObjCSelectorLiteralCall(preparedLine, name, callIndex))
                        continue;
                    if (sqlSuppressedCallIndices != null && sqlSuppressedCallIndices.Contains(callIndex))
                        continue;
                    if (sqlWindowFunctionCallSiteSuppressions != null
                        && sqlWindowFunctionCallSiteSuppressions.Contains((lineNumber, callIndex)))
                        continue;
                    matchedCallIndices.Add(callIndex);
                    if (TryAddCallLikeReference(name, callIndex))
                    {
                        EmitGenericInvocationTypeArgumentReferences(
                            language,
                            preparedLine,
                            callIndex,
                            references,
                            seen,
                            fileId,
                            context,
                            lineNumber,
                            ResolveContainerForCall(callIndex));
                    }
                    if (language == "ruby")
                        RubyReferenceExtractor.EmitCommandTargetReferences(
                            name,
                            callIndex,
                            originalLine,
                            references,
                            seen,
                            fileId,
                            context,
                            lineNumber,
                            ResolveContainerForCall);
                }

                if (language == "ruby")
                {
                    RubyReferenceExtractor.EmitAdditionalCallReferences(
                        preparedLine,
                        originalLine,
                        references,
                        seen,
                        fileId,
                        context,
                        lineNumber,
                        ResolveContainerForCall,
                        matchedCallIndices,
                        AddCallLikeReference);
                }
                else if (language == "perl")
                {
                    PerlReferenceExtractor.EmitAdditionalReferences(
                        preparedLine,
                        originalLine,
                        references,
                        seen,
                        fileId,
                        context,
                        lineNumber,
                        ResolveContainerForCall,
                        AddCallLikeReference);
                }

                if (language == "go")
                    LanguageReferenceExtractionSupport.EmitGoBranchLabelReferences(preparedLine, AddCallLikeReference);

                if (language == "swift")
                    SwiftReferenceExtractor.EmitTrailingClosureReferences(preparedLine, AddCallLikeReference);
                else if (language == "kotlin")
                {
                    KotlinReferenceExtractor.EmitInfixCallReferences(
                        preparedLine,
                        originalLine,
                        kotlinInfixFunctionNames,
                        AddCallLikeReference);
                    KotlinReferenceExtractor.EmitTrailingLambdaReferences(preparedLine, AddCallLikeReference);
                }

                if (language == "fsharp")
                {
                    FSharpReferenceExtractor.EmitAdditionalCallReferences(
                        preparedLine,
                        AddCallLikeReference);
                }

                if (language == "scala")
                {
                    ScalaReferenceExtractor.EmitTrailingBlockCallReferences(
                        preparedLine,
                        AddCallLikeReference);
                    ScalaReferenceExtractor.EmitAdditionalReferences(
                        preparedLine,
                        references,
                        seen,
                        fileId,
                        context,
                        lineNumber,
                        ResolveContainerForCall,
                        AddCallLikeReference);
                }
                else if (language == "gradle")
                {
                    void AddGradleDslReference(string name, int callIndex)
                    {
                        var normalizedName = NormalizeAtPrefixedIdentifier(name);
                        var callContainer = ResolveContainerForCall(callIndex);
                        AddReference(references, seen, fileId, normalizedName, callIndex, "call", context, lineNumber, callContainer, language);
                    }

                    GradleReferenceExtractor.EmitDslCallReferences(
                        preparedLine,
                        AddGradleDslReference);
                }

                if (language == "fortran")
                    FortranReferenceExtractor.EmitAdditionalCallReferences(preparedLine, AddCallLikeReference);
                else if (language == "pascal")
                    PascalReferenceExtractor.EmitAdditionalCallReferences(preparedLine, AddCallLikeReference, definitionNames);
                else if (language == "objc")
                    ObjectiveCReferenceExtractor.EmitAdditionalCallReferences(preparedLine, AddCallLikeReference, references, seen, fileId, context, lineNumber, ResolveContainerForCall);
                else if (language == "haskell")
                    HaskellReferenceExtractor.EmitAdditionalCallReferences(preparedLine, AddCallLikeReference, definitionNames);
                else if (language == "elixir")
                    ElixirReferenceExtractor.EmitAdditionalCallReferences(preparedLine, AddCallLikeReference, definitionNames);
                else if (language == "lua")
                    LuaReferenceExtractor.EmitAdditionalCallReferences(preparedLine, AddCallLikeReference, references, seen, fileId, context, lineNumber, ResolveContainerForCall, definitionNames);
                else if (language == "smalltalk")
                    SmalltalkReferenceExtractor.EmitAdditionalCallReferences(preparedLine, AddCallLikeReference, definitionNames);
                else if (language == "vb")
                    LanguageReferenceExtractionSupport.EmitAdditionalCallReferences(
                        "vb",
                        preparedLine,
                        originalLine,
                        AddCallLikeReference,
                        references,
                        seen,
                        fileId,
                        context,
                        lineNumber,
                        ResolveContainerForCall,
                        definitionNames);

                // The flat CallRegex misses nested generic tails like `>>(` because `<[^>\n]+>`
                // stops at the first `>`. Add a depth-aware fallback so `Foo<Bar<int>>()` and
                // `new Dict<K, List<V>>()` still emit call/instantiate rows. See issue #263.
                // 平坦な CallRegex は `<[^>\n]+>` が最初の `>` で止まるため `>>(` 形を取りこぼす。
                // depth-aware な fallback を足し、`Foo<Bar<int>>()` や `new Dict<K, List<V>>()` でも
                // `call` / `instantiate` を発行する。issue #263 参照。
                foreach (var candidate in EnumerateNestedGenericCallCandidates(preparedLine, matchedCallIndices))
                {
                    if (TryAddCallLikeReference(candidate.Name, candidate.NameIndex))
                    {
                        EmitGenericInvocationTypeArgumentReferences(
                            language,
                            preparedLine,
                            candidate.NameIndex,
                            references,
                            seen,
                            fileId,
                            context,
                            lineNumber,
                            ResolveContainerForCall(candidate.NameIndex));
                    }
                }
            }

            if (language == "rust")
            {
                RustReferenceExtractor.EmitAdditionalCallReferences(preparedLine, AddCallLikeReference);
                RustReferenceExtractor.EmitAttributeReferences(preparedLine, references, seen, fileId, context, lineNumber, container);
            }

            if (language == "csharp")
            {
                EmitMethodGroupReferences(
                    language,
                    preparedLine,
                    callableDefinitionNames,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    ResolveContainerForCall);
            }
            else if (language is "java")
            {
                JavaReferenceExtractor.EmitMethodReferenceReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    ResolveContainerForCall);
            }
            else if (language is "kotlin")
            {
                KotlinReferenceExtractor.EmitMethodReferenceReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    ResolveContainerForCall);
            }
            else if (language is "scala")
            {
                ScalaReferenceExtractor.EmitMethodReferenceReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    ResolveContainerForCall);
            }

            if (language == "csharp")
            {
                CSharpReferenceExtractor.EmitStaticMemberQualifierReferences(
                    preparedLine,
                    csharpAttrRangesOnLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    ResolveContainerForCall);
            }

            // Qualified C# enum-member access such as `Nested.A` or `Outer.First.None` is not
            // a method call, but downstream symbol workflows (`references`, `callers`,
            // `callees`, `inspect`, `impact`) still need an edge anchored to the narrowest
            // real owner symbol. Ordinary code paths stay `call` so existing graph readers
            // keep working, while C# attribute metadata sites are downgraded to `attribute`
            // to stay out of runtime call-graph traversals (issue #293 / #492).
            // `Nested.A` や `Outer.First.None` のような C# enum member の修飾アクセスは
            // メソッド呼び出しではないが、下流の symbol workflow では実 owner に紐づく edge が必要。
            // 通常コードでは既存 reader / SQL 契約を守るため kind は `call` を維持し、C# 属性メタデータ内だけ
            // `attribute` に落として runtime call-graph への混入を防ぐ (issue #293 / #492)。
            if (language == "csharp" && csharpQualifiedEnumMemberLookup.Count > 0)
            {
                CSharpReferenceExtractor.EmitQualifiedEnumMemberReferences(
                    preparedLine,
                    csharpQualifiedEnumMemberLookup,
                    csharpAttrRangesOnLine,
                    csharpUsingAliases,
                    csharpValueReceiverNames,
                    csharpFunctionValueReceiverNames,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    ResolveContainerForCall);
            }

            // issue #268: JS/TS tagged template literal call sites. The structural masker
            // already located each template opener and captured its preceding tag identifier;
            // emit one `call` row per hit so `gql\`...\`` / `styled.div\`...\`` / `sql\`...${x}...\``
            // surface in references / callers / callees / impact just like `fn()` call sites.
            // issue #268: JS/TS タグ付きテンプレートリテラルの呼び出し位置。構造マスカーが
            // テンプレート opener を検出済みで先行する tag 識別子を記録しているため、そのまま
            // `call` として発行し、`gql\`...\``・`styled.div\`...\``・`sql\`...${x}...\`` を
            // references / callers / callees / impact に反映する。
            if (jsTaggedTemplatesByLine != null
                && jsTaggedTemplatesByLine.TryGetValue(lineNumber, out var tagHitsOnLine))
            {
                foreach (var hit in tagHitsOnLine)
                {
                    var name = hit.Name;
                    // Bare-name suppression (shared ignore list + tagged-template
                    // operator denylist) is bypassed for member-access tags because
                    // any reserved / keyword-ish identifier is a legal property name
                    // in JS/TS — `obj.return\`x\``, `obj.await\`y\``, `obj.yield\`z\``,
                    // `obj.default\`w\``, `obj.finally\`v\`` all evaluate to real
                    // tagged-template calls. Only bare-keyword forms such as
                    // `yield \`x\``, `await \`x\``, `export default \`x\``,
                    // `try {} finally \`x\`` should remain suppressed.
                    // bare-name による抑止（共有 ignore list と tagged-template 演算子
                    // denylist）は member-access のタグでは迂回する。JS/TS ではすべての
                    // 予約語相当 identifier が property 名になれるため
                    // `obj.return\`x\``・`obj.await\`y\``・`obj.yield\`z\``・
                    // `obj.default\`w\``・`obj.finally\`v\`` はすべて正当なタグ呼び出し。
                    // `yield \`x\``・`await \`x\``・`export default \`x\``・
                    // `try {} finally \`x\`` のような bare-keyword 形のみ抑止する。
                    if (!hit.IsMemberAccess)
                    {
                        if (IsIgnoredCallName(language, name))
                            continue;
                        if (JsTaggedTemplateOperatorNames.Contains(name))
                            continue;
                    }
                    if (definitionNames != null && definitionNames.Contains(name))
                        continue;
                    var tagContainer = ResolveContainerForCall(hit.Column - 1);
                    AddChainReference(references, seen, fileId, name, hit.Column, "call", context, lineNumber, tagContainer);
                }
            }

            // issue #293: bare no-arg attributes / annotations are invisible to CallRegex because
            // it requires `(`. Emit them from dedicated regexes so `[Serializable]` / `@Deprecated`
            // and their siblings still populate the reference table.
            // issue #293: 引数なしの属性・アノテーションは `(` が必須な CallRegex では拾えないため、
            // 専用 regex から `[Serializable]` / `@Deprecated` などの素形を reference テーブルへ反映する。
            if (language == "csharp" && csharpAttrTopLevelOnLine != null && csharpAttrTopLevelOnLine.Count > 0)
            {
                foreach (Match match in CSharpNoArgAttributeRegex.Matches(preparedLine))
                {
                    var rawName = match.Groups["name"].Value;
                    var name = NormalizeCSharpIdentifier(rawName);
                    var nameIndex = match.Groups["name"].Index;
                    // Gate on the attribute-section top-level (paren-depth 0) zones only, so
                    // identifiers that sit inside an attribute's argument list (e.g.
                    // `ConverterStrategy.AllowNumbers` in `[JsonConverter(...)]`) are not
                    // misclassified as no-arg attributes.
                    // 属性セクションの top-level（paren 深さ 0）ゾーンでのみ採用する。属性の
                    // 引数リスト内にある識別子（`[JsonConverter(ConverterStrategy.AllowNumbers)]`
                    // の `AllowNumbers` など）を no-arg 属性として誤分類しないため。
                    if (!IsInsideCSharpAttributeRange(csharpAttrTopLevelOnLine, nameIndex))
                        continue;
                    if (IsIgnoredCallName(language, rawName))
                        continue;
                    if (definitionNames != null && definitionNames.Contains(name))
                        continue;
                    AddReference(references, seen, fileId, name, nameIndex, "attribute", context, lineNumber, container, language);
                    var genericStart = nameIndex + rawName.Length;
                    while (genericStart < preparedLine.Length && char.IsWhiteSpace(preparedLine[genericStart]))
                        genericStart++;
                    if (genericStart < preparedLine.Length && preparedLine[genericStart] == '<')
                    {
                        var genericEnd = genericStart;
                        if (TrySkipBalancedGenericArgs(preparedLine, ref genericEnd, out _)
                            && genericEnd > genericStart + 2)
                        {
                            AddTypeExpressionSegments(
                                references,
                                seen,
                                fileId,
                                preparedLine.Substring(genericStart + 1, genericEnd - genericStart - 2),
                                genericStart + 1,
                                context,
                                lineNumber,
                                container,
                                "csharp");
                        }
                    }
                    if (CSharpReferenceExtractor.TryGetCallerInfoAttributeTypeName(rawName, preparedLine, nameIndex) is { } callerInfoAttributeTypeName)
                    {
                        AddReference(
                            references,
                            seen,
                            fileId,
                            callerInfoAttributeTypeName,
                            nameIndex,
                            "type_reference",
                            context,
                            lineNumber,
                            container);
                    }
                }
            }
            else if (AnnotationLanguages.Contains(language))
            {
                if (language == "kotlin")
                {
                    foreach (Match match in KotlinBacktickAnnotationRegex.Matches(preparedLine))
                    {
                        var nameGroup = match.Groups["name"];
                        var name = NormalizeKotlinBacktickIdentifier(nameGroup.Value);
                        if (IsIgnoredCallName(language, name))
                            continue;
                        if (definitionNames != null && definitionNames.Contains(name))
                            continue;
                        AddReference(references, seen, fileId, name, nameGroup.Index, "annotation", context, lineNumber, container);
                    }
                }

                foreach (Match match in NoArgAnnotationRegex.Matches(preparedLine))
                {
                    var name = match.Groups["name"].Value;
                    if (IsIgnoredCallName(language, name))
                        continue;
                    if (definitionNames != null && definitionNames.Contains(name))
                        continue;
                    AddReference(references, seen, fileId, match, "annotation", context, lineNumber, container);
                }
            }

            if (isRazorFile && language == "csharp")
            {
                RazorReferenceExtractor.EmitReferences(
                    razorReferenceLines?[i] ?? originalLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    ResolveContainerForCall,
                    definitionNames,
                    fileDefinitionNames,
                    razorImplementedTypeNames);
            }

            if (language == "python")
            {
                var pythonPreparedLine = preparedLine;
                var pythonHeaderMap = default(PythonLogicalHeaderReferenceLine?);
                var pythonHeaderSymbol = symbols.FirstOrDefault(symbol =>
                    symbol.Line == lineNumber
                    && symbol.Signature != null
                    && symbol.Kind is "function" or "class" or "property" or "class_hook");
                if (pythonHeaderSymbol?.Signature != null
                    && TryBuildPythonLogicalHeaderReferenceLine(lines, i, pythonHeaderSymbol.StartColumn ?? 0, out var builtPythonHeaderMap))
                {
                    pythonPreparedLine = builtPythonHeaderMap.Text;
                    pythonHeaderMap = builtPythonHeaderMap;
                }
                var pythonTypeFactoryLine = preparedLine;
                var pythonTypeFactoryMap = default(PythonLogicalHeaderReferenceLine?);
                if (preparedLine.Contains("TypeVar", StringComparison.Ordinal)
                    || preparedLine.Contains("ParamSpec", StringComparison.Ordinal))
                {
                    var typeFactoryStartColumn = originalLine.IndexOfAny(['T', 'P']);
                    if (typeFactoryStartColumn < 0)
                        typeFactoryStartColumn = 0;
                    if (TryBuildPythonLogicalStatementReferenceLine(lines, i, typeFactoryStartColumn, out var builtPythonTypeFactoryMap))
                    {
                        pythonTypeFactoryLine = builtPythonTypeFactoryMap.Text;
                        pythonTypeFactoryMap = builtPythonTypeFactoryMap;
                    }
                }
                var pythonHeaderContainer = pythonHeaderSymbol ?? container;

                var pythonReferenceStart = references.Count;
                PythonReferenceExtractor.EmitDecoratorReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container,
                    definitionNames,
                    name => IsIgnoredCallName(language, name));
                PythonReferenceExtractor.EmitRaiseReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container,
                    name => IsIgnoredCallName(language, name));
                PythonReferenceExtractor.EmitExceptReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container,
                    name => IsIgnoredCallName(language, name));
                PythonReferenceExtractor.EmitIsInstanceReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container,
                    name => IsIgnoredCallName(language, name));
                PythonReferenceExtractor.EmitIsSubclassReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container,
                    name => IsIgnoredCallName(language, name));
                PythonReferenceExtractor.EmitCastReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container,
                    name => IsIgnoredCallName(language, name));
                PythonReferenceExtractor.EmitAssertTypeReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container,
                    name => IsIgnoredCallName(language, name));
                PythonReferenceExtractor.EmitClassBaseReferences(
                    pythonPreparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    pythonHeaderContainer,
                    index => pythonHeaderContainer ?? ResolveContainerForCall(index) ?? ResolvePythonDefinitionContainer(lineNumber, "class"),
                    name => IsIgnoredCallName(language, name));
                PythonReferenceExtractor.EmitFunctionReturnReferences(
                    pythonPreparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    pythonHeaderContainer,
                    index => pythonHeaderContainer ?? ResolveContainerForCall(index) ?? ResolvePythonDefinitionContainer(lineNumber, "function"),
                    name => IsIgnoredCallName(language, name));
                PythonReferenceExtractor.EmitFunctionParameterReferences(
                    pythonPreparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    pythonHeaderContainer,
                    index => pythonHeaderContainer ?? ResolveContainerForCall(index) ?? ResolvePythonDefinitionContainer(lineNumber, "function"),
                    name => IsIgnoredCallName(language, name));
                PythonReferenceExtractor.EmitVariableAnnotationReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container,
                    name => IsIgnoredCallName(language, name));
                PythonReferenceExtractor.EmitTypeAliasReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container,
                    name => IsIgnoredCallName(language, name));
                PythonReferenceExtractor.EmitNewTypeReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container,
                    name => IsIgnoredCallName(language, name));
                var pythonTypeFactoryReferenceStart = references.Count;
                PythonReferenceExtractor.EmitTypeVarBoundReferences(
                    pythonTypeFactoryLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container,
                    name => IsIgnoredCallName(language, name));
                PythonReferenceExtractor.EmitTypeVarConstraintReferences(
                    pythonTypeFactoryLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container,
                    name => IsIgnoredCallName(language, name));
                PythonReferenceExtractor.EmitGetTypeHintsReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container,
                    name => IsIgnoredCallName(language, name));
                PythonReferenceExtractor.EmitDataclassesFieldsReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container,
                    name => IsIgnoredCallName(language, name));
                PythonReferenceExtractor.EmitDataclassFieldReferences(
                    preparedLines,
                    lines,
                    i,
                    references,
                    seen,
                    fileId,
                    container,
                    name => IsIgnoredCallName(language, name));
                PythonReferenceExtractor.EmitAttrsFieldsReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container,
                    name => IsIgnoredCallName(language, name));
                PythonReferenceExtractor.EmitPydanticTypeAdapterReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container,
                    name => IsIgnoredCallName(language, name));
                PythonReferenceExtractor.EmitPytestRaisesReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container,
                    name => IsIgnoredCallName(language, name));
                PythonReferenceExtractor.EmitContextlibSuppressReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container,
                    name => IsIgnoredCallName(language, name));

                if (pythonTypeFactoryMap.HasValue)
                    RemapPythonLogicalHeaderReferences(references, pythonTypeFactoryReferenceStart, pythonTypeFactoryMap.Value, lines);
                PythonReferenceExtractor.EmitDynamicImportReferences(
                    preparedLine,
                    originalLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container);
                if (pythonHeaderMap.HasValue)
                    RemapPythonLogicalHeaderReferences(references, pythonReferenceStart, pythonHeaderMap.Value, lines);
            }

            if (language == "r")
            {
                RReferenceExtractor.EmitNamespaceReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container,
                    definitionNames);
                RReferenceExtractor.EmitNamespaceDirectiveReferences(
                    preparedLine,
                    originalLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container);
                RReferenceExtractor.EmitS4DispatchReferences(
                    preparedLine,
                    originalLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container);
                RReferenceExtractor.EmitBacktickCallReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container,
                    definitionNames);
                RReferenceExtractor.EmitInfixOperatorCallReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container,
                    definitionNames);
                RReferenceExtractor.EmitSourceFileReferences(
                    preparedLine,
                    originalLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container);
                RReferenceExtractor.EmitLoadAllReferences(
                    originalLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container);
                RReferenceExtractor.EmitDataCallReferences(
                    preparedLine,
                    originalLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container);
                RReferenceExtractor.EmitSystemFileReferences(
                    preparedLine,
                    originalLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container);
                RReferenceExtractor.EmitVignetteReferences(
                    preparedLine,
                    originalLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container);
                RReferenceExtractor.EmitHelpExampleReferences(
                    preparedLine,
                    originalLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container);
                RReferenceExtractor.EmitInstallPackagesReferences(
                    preparedLine,
                    originalLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container);
                RReferenceExtractor.EmitNamespacePackageInstallReferences(
                    preparedLine,
                    originalLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container);
                RReferenceExtractor.EmitGitHubPackageInstallReferences(
                    preparedLine,
                    originalLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container);
                RReferenceExtractor.EmitDollarMemberReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container,
                    definitionNames);
                RReferenceExtractor.EmitBracketMemberReferences(
                    preparedLine,
                    originalLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container,
                    definitionNames);
                RReferenceExtractor.EmitSlotMemberReferences(
                    preparedLine,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container,
                    definitionNames);
            }
        }

        if (language == "csharp")
        {
            CSharpReferenceExtractor.EmitSwitchExpressionTypePatternReferences(
                lines,
                preparedLines,
                containerCandidates,
                csharpQualifiedConstantPatternMemberLookup,
                csharpQualifiedTypePatternLookup,
                csharpUsingAliases,
                csharpUsingStatics,
                HasActiveSameFileCSharpTypeCandidate,
                references,
                seen,
                fileId);

            CSharpReferenceExtractor.FlushPendingMultiLineTypePatternReference(
                ref pendingCSharpMultiLineTypePattern,
                csharpQualifiedConstantPatternMemberLookup,
                csharpUsingAliases,
                csharpUsingStatics,
                HasActiveSameFileCSharpTypeCandidate,
                references,
                seen,
                fileId);
        }

        MarkMutualRecursionReferences(references);
        return references;
    }

}
