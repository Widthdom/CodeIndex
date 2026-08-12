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
        in CoreReferenceLineContext line,
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
        in CoreReferenceLineContext line,
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
}
