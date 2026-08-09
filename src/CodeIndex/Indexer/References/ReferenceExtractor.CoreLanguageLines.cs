using System.Text.RegularExpressions;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private readonly record struct CoreReferenceLineContext(
        long FileId,
        string Language,
        string[] Lines,
        string[] PreparedLines,
        int LineIndex,
        string PreparedLine,
        string OriginalLine,
        string Context,
        int LineNumber,
        List<ReferenceRecord> References,
        ReferenceDedupeSet Seen,
        SymbolRecord? Container,
        HashSet<string>? DefinitionNames,
        Func<int, SymbolRecord?> ResolveContainerForCall,
        Func<string, bool> IsIgnoredCallName);

    private static void EmitJavaScriptTaggedTemplateReferences(
        CoreReferenceLineContext line,
        IReadOnlyList<JsTaggedTemplateHit> tagHitsOnLine)
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
                if (IsIgnoredCallName(line.Language, name))
                    continue;
                if (JsTaggedTemplateOperatorNames.Contains(name))
                    continue;
            }
            if (line.DefinitionNames != null && line.DefinitionNames.Contains(name))
                continue;
            var tagContainer = line.ResolveContainerForCall(hit.Column - 1);
            AddChainReference(line.References, line.Seen, line.FileId, name, hit.Column, "call", line.Context, line.LineNumber, tagContainer);
        }
    }

    private static void EmitMetadataLineReferences(
        CoreReferenceLineContext line,
        List<(int start, int end)>? csharpAttrTopLevelOnLine)
    {
        if (line.Language == "csharp" && csharpAttrTopLevelOnLine != null && csharpAttrTopLevelOnLine.Count > 0)
        {
            foreach (Match match in BoundedRegex.EnumerateMatches(CSharpNoArgAttributeRegex, line.PreparedLine))
            {
                if (ReferenceLimitReached(line.References))
                    break;
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
                if (IsIgnoredCallName(line.Language, rawName))
                    continue;
                if (line.DefinitionNames != null && line.DefinitionNames.Contains(name))
                    continue;
                AddReference(line.References, line.Seen, line.FileId, name, nameIndex, "attribute", line.Context, line.LineNumber, line.Container, line.Language);
                var genericStart = nameIndex + rawName.Length;
                while (genericStart < line.PreparedLine.Length && char.IsWhiteSpace(line.PreparedLine[genericStart]))
                    genericStart++;
                if (genericStart < line.PreparedLine.Length && line.PreparedLine[genericStart] == '<')
                {
                    var genericEnd = genericStart;
                    if (TrySkipBalancedGenericArgs(line.PreparedLine, ref genericEnd, out _)
                        && genericEnd > genericStart + 2)
                    {
                        AddTypeExpressionSegments(
                            line.References,
                            line.Seen,
                            line.FileId,
                            line.PreparedLine.Substring(genericStart + 1, genericEnd - genericStart - 2),
                            genericStart + 1,
                            line.Context,
                            line.LineNumber,
                            line.Container,
                            "csharp");
                    }
                }
                if (CSharpReferenceExtractor.TryGetCallerInfoAttributeTypeName(rawName, line.PreparedLine, nameIndex) is { } callerInfoAttributeTypeName)
                {
                    AddReference(
                        line.References,
                        line.Seen,
                        line.FileId,
                        callerInfoAttributeTypeName,
                        nameIndex,
                        "type_reference",
                        line.Context,
                        line.LineNumber,
                        line.Container);
                }
            }
        }
        else if (AnnotationLanguages.Contains(line.Language)
                 && line.PreparedLine.IndexOf('@') >= 0)
        {
            if (line.Language == "kotlin")
            {
                foreach (Match match in BoundedRegex.EnumerateMatches(KotlinBacktickAnnotationRegex, line.PreparedLine))
                {
                    if (ReferenceLimitReached(line.References))
                        break;
                    var nameGroup = match.Groups["name"];
                    var name = NormalizeKotlinBacktickIdentifier(nameGroup.Value);
                    if (IsIgnoredCallName(line.Language, name))
                        continue;
                    if (line.DefinitionNames != null && line.DefinitionNames.Contains(name))
                        continue;
                    AddReference(line.References, line.Seen, line.FileId, name, nameGroup.Index, "annotation", line.Context, line.LineNumber, line.Container);
                }
            }

            foreach (Match match in BoundedRegex.EnumerateMatches(NoArgAnnotationRegex, line.PreparedLine))
            {
                if (ReferenceLimitReached(line.References))
                    break;
                var name = match.Groups["name"].Value;
                if (IsIgnoredCallName(line.Language, name))
                    continue;
                if (line.DefinitionNames != null && line.DefinitionNames.Contains(name))
                    continue;
                AddReference(line.References, line.Seen, line.FileId, match, "annotation", line.Context, line.LineNumber, line.Container);
            }
        }
    }

    private static Func<int, SymbolRecord?> CreatePythonDefinitionContainerResolver(
        CoreReferenceLineContext line,
        CoreExtractionLookups lookups,
        SymbolRecord? headerContainer,
        string definitionKind) =>
        column =>
        {
            if (headerContainer != null)
                return headerContainer;

            var container = line.ResolveContainerForCall(column);
            if (container != null)
                return container;

            var definitionContainers =
                lookups.GetPythonDefinitionContainersByLineAndKind();
            if (definitionContainers == null)
                return null;
            return definitionContainers.TryGetValue(
                (line.LineNumber, definitionKind),
                out var symbol)
                ? symbol
                : null;
        };

    private static void EmitPythonLineReferences(
        CoreReferenceLineContext line,
        CoreExtractionLookups lookups)
    {

        var pythonPreparedLine = line.PreparedLine;
        var pythonHeaderMap = default(PythonLogicalHeaderReferenceLine?);
        SymbolRecord? pythonHeaderSymbol = null;
        lookups.GetPythonHeaderSymbolsByLine()?.TryGetValue(line.LineNumber, out pythonHeaderSymbol);
        if (pythonHeaderSymbol?.Signature != null
            && TryBuildPythonLogicalHeaderReferenceLine(line.Lines, line.LineIndex, pythonHeaderSymbol.StartColumn ?? 0, out var builtPythonHeaderMap))
        {
            pythonPreparedLine = builtPythonHeaderMap.Text;
            pythonHeaderMap = builtPythonHeaderMap;
        }
        var pythonTypeFactoryLine = line.PreparedLine;
        var pythonTypeFactoryMap = default(PythonLogicalHeaderReferenceLine?);
        if (line.PreparedLine.Contains("TypeVar", StringComparison.Ordinal)
            || line.PreparedLine.Contains("ParamSpec", StringComparison.Ordinal))
        {
            var typeFactoryStartColumn = line.OriginalLine.IndexOfAny(['T', 'P']);
            if (typeFactoryStartColumn < 0)
                typeFactoryStartColumn = 0;
            if (TryBuildPythonLogicalStatementReferenceLine(line.Lines, line.LineIndex, typeFactoryStartColumn, out var builtPythonTypeFactoryMap))
            {
                pythonTypeFactoryLine = builtPythonTypeFactoryMap.Text;
                pythonTypeFactoryMap = builtPythonTypeFactoryMap;
            }
        }
        var pythonHeaderContainer = pythonHeaderSymbol ?? line.Container;
        var resolvePythonClassContainer =
            pythonPreparedLine.IndexOf("class", StringComparison.Ordinal) >= 0
                ? CreatePythonDefinitionContainerResolver(
                    line,
                    lookups,
                    pythonHeaderContainer,
                    "class")
                : line.ResolveContainerForCall;
        var resolvePythonFunctionContainer =
            pythonPreparedLine.IndexOf("def", StringComparison.Ordinal) >= 0
                ? CreatePythonDefinitionContainerResolver(
                    line,
                    lookups,
                    pythonHeaderContainer,
                    "function")
                : line.ResolveContainerForCall;

        var pythonReferenceStart = line.References.Count;
        PythonReferenceExtractor.EmitDecoratorReferences(
            line.PreparedLine,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            line.Container,
            line.DefinitionNames,
            line.IsIgnoredCallName);
        PythonReferenceExtractor.EmitRaiseReferences(
            line.PreparedLine,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            line.Container,
            line.IsIgnoredCallName);
        PythonReferenceExtractor.EmitExceptReferences(
            line.PreparedLine,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            line.Container,
            line.IsIgnoredCallName);
        PythonReferenceExtractor.EmitIsInstanceReferences(
            line.PreparedLine,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            line.Container,
            line.IsIgnoredCallName);
        PythonReferenceExtractor.EmitIsSubclassReferences(
            line.PreparedLine,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            line.Container,
            line.IsIgnoredCallName);
        PythonReferenceExtractor.EmitCastReferences(
            line.PreparedLine,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            line.Container,
            line.IsIgnoredCallName);
        PythonReferenceExtractor.EmitAssertTypeReferences(
            line.PreparedLine,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            line.Container,
            line.IsIgnoredCallName);
        PythonReferenceExtractor.EmitClassBaseReferences(
            pythonPreparedLine,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            pythonHeaderContainer,
            resolvePythonClassContainer,
            line.IsIgnoredCallName);
        PythonReferenceExtractor.EmitFunctionReturnReferences(
            pythonPreparedLine,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            pythonHeaderContainer,
            resolvePythonFunctionContainer,
            line.IsIgnoredCallName);
        PythonReferenceExtractor.EmitFunctionParameterReferences(
            pythonPreparedLine,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            pythonHeaderContainer,
            resolvePythonFunctionContainer,
            line.IsIgnoredCallName);
        PythonReferenceExtractor.EmitVariableAnnotationReferences(
            line.PreparedLine,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            line.Container,
            line.IsIgnoredCallName);
        PythonReferenceExtractor.EmitTypeAliasReferences(
            line.PreparedLine,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            line.Container,
            line.IsIgnoredCallName);
        PythonReferenceExtractor.EmitNewTypeReferences(
            line.PreparedLine,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            line.Container,
            line.IsIgnoredCallName);
        var pythonTypeFactoryReferenceStart = line.References.Count;
        PythonReferenceExtractor.EmitTypeVarBoundReferences(
            pythonTypeFactoryLine,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            line.Container,
            line.IsIgnoredCallName);
        PythonReferenceExtractor.EmitTypeVarConstraintReferences(
            pythonTypeFactoryLine,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            line.Container,
            line.IsIgnoredCallName);
        PythonReferenceExtractor.EmitGetTypeHintsReferences(
            line.PreparedLine,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            line.Container,
            line.IsIgnoredCallName);
        PythonReferenceExtractor.EmitDataclassesFieldsReferences(
            line.PreparedLine,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            line.Container,
            line.IsIgnoredCallName);
        PythonReferenceExtractor.EmitDataclassFieldReferences(
            line.PreparedLines,
            line.Lines,
            line.LineIndex,
            line.References,
            line.Seen,
            line.FileId,
            line.Container,
            line.IsIgnoredCallName);
        PythonReferenceExtractor.EmitAttrsFieldsReferences(
            line.PreparedLine,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            line.Container,
            line.IsIgnoredCallName);
        PythonReferenceExtractor.EmitPydanticTypeAdapterReferences(
            line.PreparedLine,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            line.Container,
            line.IsIgnoredCallName);
        PythonReferenceExtractor.EmitPytestRaisesReferences(
            line.PreparedLine,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            line.Container,
            line.IsIgnoredCallName);
        PythonReferenceExtractor.EmitContextlibSuppressReferences(
            line.PreparedLine,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            line.Container,
            line.IsIgnoredCallName);

        if (pythonTypeFactoryMap.HasValue)
            RemapPythonLogicalHeaderReferences(line.References, pythonTypeFactoryReferenceStart, pythonTypeFactoryMap.Value, line.Lines);
        PythonReferenceExtractor.EmitDynamicImportReferences(
            line.PreparedLine,
            line.OriginalLine,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            line.Container);
        if (pythonHeaderMap.HasValue)
            RemapPythonLogicalHeaderReferences(line.References, pythonReferenceStart, pythonHeaderMap.Value, line.Lines);
    }

    private static void EmitRLineReferences(CoreReferenceLineContext line)
    {

        RReferenceExtractor.EmitNamespaceReferences(
            line.PreparedLine,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            line.Container,
            line.DefinitionNames);
        RReferenceExtractor.EmitNamespaceDirectiveReferences(
            line.PreparedLine,
            line.OriginalLine,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            line.Container);
        RReferenceExtractor.EmitS4DispatchReferences(
            line.PreparedLine,
            line.OriginalLine,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            line.Container);
        RReferenceExtractor.EmitBacktickCallReferences(
            line.PreparedLine,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            line.Container,
            line.DefinitionNames);
        RReferenceExtractor.EmitInfixOperatorCallReferences(
            line.PreparedLine,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            line.Container,
            line.DefinitionNames);
        RReferenceExtractor.EmitSourceFileReferences(
            line.PreparedLine,
            line.OriginalLine,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            line.Container);
        RReferenceExtractor.EmitLoadAllReferences(
            line.OriginalLine,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            line.Container);
        RReferenceExtractor.EmitDataCallReferences(
            line.PreparedLine,
            line.OriginalLine,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            line.Container);
        RReferenceExtractor.EmitSystemFileReferences(
            line.PreparedLine,
            line.OriginalLine,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            line.Container);
        RReferenceExtractor.EmitVignetteReferences(
            line.PreparedLine,
            line.OriginalLine,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            line.Container);
        RReferenceExtractor.EmitHelpExampleReferences(
            line.PreparedLine,
            line.OriginalLine,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            line.Container);
        RReferenceExtractor.EmitInstallPackagesReferences(
            line.PreparedLine,
            line.OriginalLine,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            line.Container);
        RReferenceExtractor.EmitNamespacePackageInstallReferences(
            line.PreparedLine,
            line.OriginalLine,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            line.Container);
        RReferenceExtractor.EmitGitHubPackageInstallReferences(
            line.PreparedLine,
            line.OriginalLine,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            line.Container);
        RReferenceExtractor.EmitDollarMemberReferences(
            line.PreparedLine,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            line.Container,
            line.DefinitionNames);
        RReferenceExtractor.EmitBracketMemberReferences(
            line.PreparedLine,
            line.OriginalLine,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            line.Container,
            line.DefinitionNames);
        RReferenceExtractor.EmitSlotMemberReferences(
            line.PreparedLine,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            line.Container,
            line.DefinitionNames);
    }
}
