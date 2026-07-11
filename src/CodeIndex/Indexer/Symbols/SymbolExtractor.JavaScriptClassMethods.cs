using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static void ExtractJavaScriptTypeScriptBareMethodsInClass(
        long fileId,
        string lang,
        string[] lines,
        List<SymbolRecord> symbols,
        JavaScriptClassScanTarget classScanTarget)
    {
        if (classScanTarget.ScanStartIndex >= classScanTarget.ScanEndExclusive)
            return;

        var scanStartIndex = classScanTarget.ScanStartIndex;
        var scanEndExclusive = classScanTarget.ScanEndExclusive;
        var nestedBraceDepth = 0;
        var inFieldInitializer = false;
        var initializerParenDepth = 0;
        var initializerBracketDepth = 0;
        var initializerBraceDepth = 0;
        var lexState = new JavaScriptLexState();
        var seenMethodStarts = new HashSet<(int Line, int Column)>();
        var pendingHeaderEndLineIndex = -1;
        var pendingHeaderEndColumn = -1;
        var pendingBodyStartLineIndex = -1;
        var pendingBodyStartColumn = -1;

        for (int i = scanStartIndex; i < scanEndExclusive; i++)
        {
            var line = lines[i];
            var lexedLine = LexJavaScriptLine(line, lexState);
            lexState = lexedLine.EndState;
            var sanitizedLine = lexedLine.SanitizedLine;

            if (pendingHeaderEndLineIndex >= 0)
            {
                if (i < pendingHeaderEndLineIndex)
                    continue;
            }

            if (pendingBodyStartLineIndex >= 0)
            {
                if (i < pendingBodyStartLineIndex)
                    continue;

                if (i == pendingBodyStartLineIndex)
                {
                    if (pendingBodyStartColumn >= 0 && pendingBodyStartColumn < sanitizedLine.Length)
                    {
                        nestedBraceDepth += CountBraces(sanitizedLine[pendingBodyStartColumn..]);
                        if (nestedBraceDepth < 0)
                            nestedBraceDepth = 0;
                    }

                    pendingBodyStartLineIndex = -1;
                    pendingBodyStartColumn = -1;
                    continue;
                }
            }

            var scanStartColumn = i == scanStartIndex
                ? Math.Min(classScanTarget.FirstLineScanOffset, sanitizedLine.Length)
                : 0;
            if (pendingHeaderEndLineIndex == i)
            {
                scanStartColumn = Math.Max(scanStartColumn, Math.Min(pendingHeaderEndColumn + 1, sanitizedLine.Length));
                pendingHeaderEndLineIndex = -1;
                pendingHeaderEndColumn = -1;
            }

            if (inFieldInitializer
                && initializerParenDepth == 0
                && initializerBracketDepth == 0
                && initializerBraceDepth == 0)
            {
                var continuationInput = scanStartColumn >= sanitizedLine.Length
                    ? ReadOnlySpan<char>.Empty
                    : sanitizedLine.AsSpan(scanStartColumn);
                if (!ShouldContinueJavaScriptTypeScriptFieldInitializer(continuationInput, lang))
                {
                    inFieldInitializer = false;
                }
            }

            var column = scanStartColumn;
            while (column < sanitizedLine.Length)
            {
                var ch = sanitizedLine[column];
                if (char.IsWhiteSpace(ch))
                {
                    column++;
                    continue;
                }

                if (classScanTarget.ContainerKind == "object"
                    && nestedBraceDepth == 0
                    && IsJavaScriptTypeScriptIdentifierStart(ch))
                {
                    var propertyStartColumn = column;
                    var propertyEndColumn = propertyStartColumn + 1;
                    while (propertyEndColumn < sanitizedLine.Length && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[propertyEndColumn]))
                        propertyEndColumn++;

                    var propertyScanColumn = propertyEndColumn;
                    while (propertyScanColumn < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[propertyScanColumn]))
                        propertyScanColumn++;

                    if (propertyScanColumn < sanitizedLine.Length && sanitizedLine[propertyScanColumn] == ':')
                    {
                        var valueStartColumn = propertyScanColumn + 1;
                        while (valueStartColumn < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[valueStartColumn]))
                            valueStartColumn++;

                        if (valueStartColumn < sanitizedLine.Length
                            && StartsJavaScriptTypeScriptFunctionAssignmentValue(sanitizedLine, valueStartColumn))
                        {
                            var propertyName = sanitizedLine[propertyStartColumn..propertyEndColumn];
                            if (seenMethodStarts.Add((i + 1, propertyStartColumn)))
                            {
                                var propertyBodyOpenBraceLineIndex = -1;
                                var propertyBodyOpenBraceColumn = -1;
                                int propertyEndLine;
                                int? propertyBodyStartLine;
                                int? propertyBodyEndLine;
                                int propertySameLineEndColumn;
                                if (TryFindJavaScriptTypeScriptAssignedFunctionBodyOpenBrace(
                                    lines,
                                    i,
                                    valueStartColumn,
                                    lang,
                                    out var foundPropertyBodyOpenBraceLineIndex,
                                    out var foundPropertyBodyOpenBraceColumn))
                                {
                                    propertyBodyOpenBraceLineIndex = foundPropertyBodyOpenBraceLineIndex;
                                    propertyBodyOpenBraceColumn = foundPropertyBodyOpenBraceColumn;
                                    (propertyEndLine, propertyBodyStartLine, propertyBodyEndLine) = ResolveRange(
                                        lines, propertyBodyOpenBraceLineIndex, BodyStyle.Brace, lang, propertyBodyOpenBraceColumn);
                                    propertySameLineEndColumn = propertyBodyEndLine == i + 1
                                        ? FindSameLineBraceEndColumn(line, valueStartColumn, lang, "function")
                                        : -1;
                                }
                                else
                                {
                                    propertyEndLine = i + 1;
                                    propertyBodyStartLine = null;
                                    propertyBodyEndLine = null;
                                    propertySameLineEndColumn = -1;
                                }

                                symbols.Add(new SymbolRecord
                                {
                                    FileId = fileId,
                                    Kind = "function",
                                    Name = propertyName,
                                    Line = i + 1,
                                    StartLine = i + 1,
                                    EndLine = Math.Max(i + 1, propertyEndLine),
                                    BodyStartLine = propertyBodyStartLine,
                                    BodyEndLine = propertyBodyEndLine,
                                    Signature = line.Trim(),
                                    ContainerKind = classScanTarget.ContainerKind,
                                    ContainerName = classScanTarget.ContainerName,
                                    Visibility = classScanTarget.IsExported ? "export" : null,
                                });

                                if (propertySameLineEndColumn >= column)
                                {
                                    column = propertySameLineEndColumn + 1;
                                    continue;
                                }

                                if (propertyBodyStartLine.HasValue
                                    && propertyBodyStartLine.Value - 1 > i)
                                {
                                    pendingBodyStartLineIndex = propertyBodyStartLine.Value - 1;
                                    pendingBodyStartColumn = propertyBodyOpenBraceColumn;
                                    break;
                                }

                                if (propertyBodyStartLine.HasValue
                                    && propertyBodyStartLine.Value - 1 == i
                                    && propertyBodyOpenBraceColumn >= 0
                                    && propertyBodyOpenBraceColumn < sanitizedLine.Length)
                                {
                                    nestedBraceDepth += CountBraces(sanitizedLine[propertyBodyOpenBraceColumn..]);
                                    if (nestedBraceDepth < 0)
                                        nestedBraceDepth = 0;
                                    break;
                                }
                            }
                        }

                        column = valueStartColumn;
                        continue;
                    }
                }

                if (inFieldInitializer)
                {
                    AdvanceJavaScriptTypeScriptFieldInitializerState(
                        ref inFieldInitializer,
                        ref initializerParenDepth,
                        ref initializerBracketDepth,
                        ref initializerBraceDepth,
                        ch);
                    column++;
                    continue;
                }

                if (nestedBraceDepth == 0
                    && classScanTarget.ContainerKind is "interface" or "class"
                    && StartsJavaScriptTypeScriptClassMemberAt(sanitizedLine, column))
                {
                    if (lang == "typescript"
                        && classScanTarget.ContainerKind == "class"
                        && TryParseJavaScriptTypeScriptAccessorFieldHeader(
                            sanitizedLine,
                            column,
                            out var accessorName,
                            out var accessorVisibility,
                            out var accessorTypeStartColumn,
                            out var accessorTypeEndColumn,
                            out var accessorHeaderEndColumn,
                            out var accessorHasInitializer))
                    {
                        var accessorStartLine = i + 1;
                        if (seenMethodStarts.Add((accessorStartLine, column)))
                        {
                            var accessorSignatureEnd = accessorHeaderEndColumn < line.Length
                                ? accessorHeaderEndColumn + 1
                                : line.Length;
                            symbols.Add(new SymbolRecord
                            {
                                FileId = fileId,
                                Kind = "property",
                                Name = accessorName,
                                Line = accessorStartLine,
                                StartLine = accessorStartLine,
                                EndLine = accessorStartLine,
                                BodyStartLine = null,
                                BodyEndLine = null,
                                Signature = line[column..accessorSignatureEnd].Trim(),
                                ContainerKind = classScanTarget.ContainerKind,
                                ContainerName = classScanTarget.ContainerName,
                                Visibility = accessorVisibility,
                                ReturnType = NormalizeMetadata(
                                    accessorTypeStartColumn >= 0 && accessorTypeEndColumn >= accessorTypeStartColumn
                                        ? line[(accessorTypeStartColumn + 1)..(accessorTypeEndColumn + 1)]
                                        : null),
                            });
                        }

                        if (accessorHasInitializer)
                        {
                            inFieldInitializer = true;
                            initializerParenDepth = 0;
                            initializerBracketDepth = 0;
                            initializerBraceDepth = 0;
                        }

                        column = accessorHeaderEndColumn + 1;
                        continue;
                    }

                    var requireAbstractModifier = classScanTarget.ContainerKind == "class";
                    if (TryParseJavaScriptTypeScriptMemberPropertyHeader(
                        sanitizedLine,
                        column,
                        lang,
                        requireAbstractModifier,
                        out var propertyName,
                        out var propertyVisibility,
                        out var propertyTypeStartColumn,
                        out var propertyTypeEndColumn,
                        out var propertyHeaderEndColumn))
                    {
                        var propertyStartLine = i + 1;
                        if (seenMethodStarts.Add((propertyStartLine, column)))
                        {
                            var propertySignatureEnd = propertyHeaderEndColumn < line.Length
                                ? propertyHeaderEndColumn + 1
                                : line.Length;
                            symbols.Add(new SymbolRecord
                            {
                                FileId = fileId,
                                Kind = "property",
                                Name = propertyName,
                                Line = propertyStartLine,
                                StartLine = propertyStartLine,
                                EndLine = propertyStartLine,
                                BodyStartLine = null,
                                BodyEndLine = null,
                                Signature = line[column..propertySignatureEnd].Trim(),
                                ContainerKind = classScanTarget.ContainerKind,
                                ContainerName = classScanTarget.ContainerName,
                                Visibility = propertyVisibility,
                                ReturnType = NormalizeMetadata(
                                    line[(propertyTypeStartColumn + 1)..(propertyTypeEndColumn + 1)]),
                            });
                        }

                        column = propertyHeaderEndColumn + 1;
                        continue;
                    }
                }

                if (nestedBraceDepth == 0
                    && IsJavaScriptTypeScriptMethodCandidateStart(sanitizedLine, column)
                    && !IsJavaScriptTypeScriptControlFlowHeader(sanitizedLine, column))
                {
                    if (TryCaptureJavaScriptTypeScriptMethodHeader(
                        lines,
                        i,
                        column,
                        scanEndExclusive,
                        sanitizedLine,
                        lexState,
                        lang,
                        out var methodCapture))
                    {
                        var methodHeader = methodCapture.HeaderInfo;
                        var startLine = i + 1;
                        if (seenMethodStarts.Add((startLine, column)))
                        {
                            var (endLine, bodyStartLine, bodyEndLine) = methodHeader.HasBody
                                ? ResolveRange(lines, i, BodyStyle.Brace, lang, column)
                                : (methodCapture.HeaderEndLineIndex + 1, null, null);
                            var sameLineMethodEndColumn = methodHeader.HasBody && bodyEndLine == startLine
                                ? FindJavaScriptSameLineBodyEndColumn(line, column, lang)
                                : methodCapture.HeaderEndLineIndex == i
                                    ? methodCapture.HeaderEndColumn
                                    : -1;
                            symbols.Add(new SymbolRecord
                            {
                                FileId = fileId,
                                Kind = ResolveJavaScriptTypeScriptFunctionKindFromHeader(methodHeader),
                                Name = GetJavaScriptTypeScriptMethodNameFromSource(methodCapture.SourceHeader, 0) ?? methodHeader.Name,
                                Line = startLine,
                                StartLine = startLine,
                                EndLine = Math.Max(startLine, endLine),
                                BodyStartLine = bodyStartLine,
                                BodyEndLine = bodyEndLine,
                                Signature = BuildJavaScriptTypeScriptBareMethodSignature(
                                    lines,
                                    i,
                                    column,
                                    bodyEndLine,
                                    sameLineMethodEndColumn,
                                    methodCapture,
                                    lang),
                                ContainerKind = classScanTarget.ContainerKind,
                                ContainerName = classScanTarget.ContainerName,
                                Visibility = methodHeader.Visibility,
                                ReturnType = GetJavaScriptTypeScriptBareMethodReturnType(methodCapture.SourceHeader, methodHeader, lang),
                            });

                            if (sameLineMethodEndColumn >= column)
                            {
                                column = sameLineMethodEndColumn + 1;
                                continue;
                            }

                            if (methodHeader.HasBody && methodCapture.BodyStartLineIndex > i)
                            {
                                pendingBodyStartLineIndex = methodCapture.BodyStartLineIndex;
                                pendingBodyStartColumn = methodCapture.BodyStartColumn;
                                break;
                            }

                            if (methodCapture.HeaderEndLineIndex > i)
                            {
                                pendingHeaderEndLineIndex = methodCapture.HeaderEndLineIndex;
                                pendingHeaderEndColumn = methodCapture.HeaderEndColumn;
                                break;
                            }
                        }

                        if (methodHeader.HasBody
                            && methodCapture.BodyStartLineIndex == i
                            && methodCapture.BodyStartColumn >= 0
                            && methodCapture.BodyStartColumn < sanitizedLine.Length)
                        {
                            nestedBraceDepth += CountBraces(sanitizedLine[methodCapture.BodyStartColumn..]);
                            if (nestedBraceDepth < 0)
                                nestedBraceDepth = 0;
                            break;
                        }

                        column++;
                        continue;
                    }

                    // Fallback: class-field arrow function (`handleClick = () => { ... }`).
                    // The method-header parser rejects these because they have no method-style
                    // parameter list before the body; handle them with a dedicated arrow parser so
                    // they still surface as function symbols instead of being consumed by the
                    // field-initializer state machine.
                    // クラスフィールドのアロー関数 (`handleClick = () => { ... }`) のフォールバック。
                    // メソッドヘッダーパーサは body 直前に method 形式の引数リストが来ないことを理由に
                    // これを弾くため、専用パーサで処理してフィールド初期化子ステートに吸われる前に
                    // function シンボルとして emit する。
                    if (TryCaptureJavaScriptTypeScriptClassFieldArrow(
                        lines,
                        i,
                        column,
                        scanEndExclusive,
                        sanitizedLine,
                        lexState,
                        lang,
                        out var arrowCapture))
                    {
                        var arrowHeader = arrowCapture.HeaderInfo;
                        var arrowStartLine = i + 1;
                        var isExpressionBody = arrowHeader.ExpressionBodyEndColumn != null
                            && arrowCapture.BodyEndLineIndex != null
                            && arrowCapture.BodyEndColumn != null;
                        if (seenMethodStarts.Add((arrowStartLine, column)))
                        {
                            int arrowEndLine;
                            int? arrowBodyStartLine;
                            int? arrowBodyEndLine;
                            int arrowSameLineEndColumn;
                            if (isExpressionBody)
                            {
                                arrowBodyStartLine = arrowCapture.BodyStartLineIndex + 1;
                                arrowBodyEndLine = arrowCapture.BodyEndLineIndex!.Value + 1;
                                arrowEndLine = arrowBodyEndLine.Value;
                                arrowSameLineEndColumn = arrowBodyEndLine == arrowStartLine
                                    ? arrowCapture.BodyEndColumn!.Value
                                    : -1;
                            }
                            else
                            {
                                (arrowEndLine, arrowBodyStartLine, arrowBodyEndLine) = ResolveRange(
                                    lines, i, BodyStyle.Brace, lang, arrowCapture.BodyStartColumn);
                                arrowSameLineEndColumn = arrowBodyEndLine == arrowStartLine
                                    ? FindJavaScriptSameLineArrowBodyEndColumn(line, arrowCapture.BodyStartColumn)
                                    : -1;
                            }
                            symbols.Add(new SymbolRecord
                            {
                                FileId = fileId,
                                Kind = "function",
                                Name = arrowHeader.Name,
                                Line = arrowStartLine,
                                StartLine = arrowStartLine,
                                EndLine = Math.Max(arrowStartLine, arrowEndLine),
                                BodyStartLine = arrowBodyStartLine,
                                BodyEndLine = arrowBodyEndLine,
                                Signature = BuildJavaScriptTypeScriptClassFieldArrowSignature(
                                    lines,
                                    i,
                                    column,
                                    arrowBodyEndLine,
                                    arrowSameLineEndColumn,
                                    arrowCapture),
                                ContainerKind = classScanTarget.ContainerKind,
                                ContainerName = classScanTarget.ContainerName,
                                Visibility = arrowHeader.Visibility,
                                ReturnType = GetJavaScriptTypeScriptBareMethodReturnType(arrowCapture.SourceHeader, arrowHeader, lang),
                            });

                            if (arrowSameLineEndColumn >= column)
                            {
                                column = arrowSameLineEndColumn + 1;
                                continue;
                            }

                            if (isExpressionBody)
                            {
                                // Expression-body spanned multiple lines; resume scanning just
                                // after the terminating `;` using the header-end pending channel
                                // (which only skips columns up to the sentinel, never entire lines)
                                // so the next field declaration on a subsequent line is still scanned.
                                // 式本体が複数行にまたがった場合、pendingHeaderEndLineIndex / Column で
                                // 終端 `;` 直後から再開する。列単位のスキップしかしないため、直後の行に
                                // ある field 宣言 (`runInline = ...`) を取りこぼさない。
                                pendingHeaderEndLineIndex = arrowCapture.BodyEndLineIndex!.Value;
                                pendingHeaderEndColumn = arrowCapture.BodyEndColumn!.Value;
                                break;
                            }

                            if (arrowCapture.BodyStartLineIndex > i)
                            {
                                pendingBodyStartLineIndex = arrowCapture.BodyStartLineIndex;
                                pendingBodyStartColumn = arrowCapture.BodyStartColumn;
                                break;
                            }
                        }

                        if (!isExpressionBody
                            && arrowCapture.BodyStartLineIndex == i
                            && arrowCapture.BodyStartColumn >= 0
                            && arrowCapture.BodyStartColumn < sanitizedLine.Length)
                        {
                            nestedBraceDepth += CountBraces(sanitizedLine[arrowCapture.BodyStartColumn..]);
                            if (nestedBraceDepth < 0)
                                nestedBraceDepth = 0;
                            break;
                        }

                        column++;
                        continue;
                    }
                }

                if (nestedBraceDepth == 0 && CanStartJavaScriptTypeScriptClassFieldInitializer(sanitizedLine, column))
                {
                    inFieldInitializer = true;
                    initializerParenDepth = 0;
                    initializerBracketDepth = 0;
                    initializerBraceDepth = 0;
                    column++;
                    continue;
                }

                if (ch == '{')
                {
                    nestedBraceDepth++;
                }
                else if (ch == '}' && nestedBraceDepth > 0)
                {
                    nestedBraceDepth--;
                }

                column++;
            }
        }
    }


}
