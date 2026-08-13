using System.Text.RegularExpressions;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static void EmitJsxElementReferences(in CoreReferenceLineContext line)
    {
        if (line.PreparedLine.IndexOf('<') < 0)
            return;

        var jsxTypeArgumentSkipUntil = -1;
        foreach (Match match in BoundedRegex.EnumerateMatches(JsxElementOpenRegex, line.PreparedLine))
        {
            if (ReferenceLimitReached(line.References))
                break;
            if (match.Index < jsxTypeArgumentSkipUntil)
                continue;

            var fullName = match.Groups["name"].Value;
            var nameIndex = match.Groups["name"].Index;
            var jsxContainer = line.ResolveContainerForCall(nameIndex);
            var firstDotIndex = fullName.IndexOf('.');
            var tagEndIndex = nameIndex + fullName.Length;

            AddReference(
                line.References,
                line.Seen,
                line.FileId,
                firstDotIndex < 0 ? fullName : fullName[..firstDotIndex],
                nameIndex,
                "call",
                line.Context,
                line.LineNumber,
                jsxContainer);

            var dotIndex = fullName.LastIndexOf('.');
            if (dotIndex > 0 && dotIndex + 1 < fullName.Length)
            {
                AddReference(
                    line.References,
                    line.Seen,
                    line.FileId,
                    fullName[(dotIndex + 1)..],
                    nameIndex + dotIndex + 1,
                    "call",
                    line.Context,
                    line.LineNumber,
                    jsxContainer);
            }

            if (line.Language == "typescript")
            {
                var genericStart = SkipWhitespace(line.PreparedLine, tagEndIndex);
                if (genericStart < line.PreparedLine.Length && line.PreparedLine[genericStart] == '<')
                {
                    var genericEnd = genericStart;
                    if (TrySkipTypeScriptJsxTypeArguments(line.PreparedLine, ref genericEnd)
                        && genericEnd > genericStart + 2)
                    {
                        jsxTypeArgumentSkipUntil = Math.Max(jsxTypeArgumentSkipUntil, genericEnd);
                        AddTypeExpressionSegments(
                            line.References,
                            line.Seen,
                            line.FileId,
                            line.PreparedLine.Substring(genericStart + 1, genericEnd - genericStart - 2),
                            genericStart + 1,
                            line.Context,
                            line.LineNumber,
                            jsxContainer,
                            "typescript");
                    }
                }
            }
        }
    }

    private static void EmitInfrastructureLineReferences(
        in CoreReferenceLineContext line,
        HashSet<string>? dockerfileStageNames,
        HashSet<string>? dockerfileVariableNames,
        IReadOnlyList<SymbolRecord>? cobolCallableSymbols)
    {
        if (line.Language == "terraform")
        {
            TerraformReferenceExtractor.Emit(
                line.PreparedLine,
                line.Context,
                line.LineNumber,
                line.References,
                line.Seen,
                line.FileId,
                line.DefinitionNames,
                line.Container);
        }

        if (line.Language == "dockerfile")
        {
            DockerfileReferenceExtractor.EmitStageReferences(
                line.PreparedLine,
                line.OriginalLine,
                line.Context,
                line.LineNumber,
                line.References,
                line.Seen,
                line.FileId,
                dockerfileStageNames,
                line.Container);
            DockerfileReferenceExtractor.EmitVariableReferences(
                line.PreparedLine,
                line.Context,
                line.LineNumber,
                line.References,
                line.Seen,
                line.FileId,
                dockerfileVariableNames,
                line.Container);
        }

        if (line.Language == "cobol")
        {
            CobolReferenceExtractor.Emit(
                line.Lines[line.LineIndex],
                line.Context,
                line.LineNumber,
                line.References,
                line.Seen,
                line.FileId,
                line.Container,
                cobolCallableSymbols);
        }
    }

    private static HashSet<int>? EmitSqlLineReferences(
        in CoreReferenceLineContext line,
        string structuralLine,
        SqlReferenceExtractor.State? sqlState,
        CoreLineDefinitionState definitionState)
    {
        if (line.Language != "sql")
            return null;

        return SqlReferenceExtractor.Emit(
            structuralLine,
            line.Context,
            line.LineNumber,
            line.References,
            line.Seen,
            line.FileId,
            sqlState!,
            line.ResolveContainerForCall,
            line.IsIgnoredCallName,
            (resolvedName, callIndex) =>
                definitionState.ShouldSuppressDefinitionCall(
                    resolvedName,
                    resolvedName,
                    callIndex));
    }

    private static void EmitParenlessInitializerReferences(
        in CoreReferenceLineContext line)
    {
        if (line.PreparedLine.IndexOf("new", StringComparison.Ordinal) < 0)
            return;

        HashSet<int>? matchedInitializerIndices = null;
        var mayContainNestedGenericInitializer = line.Language == "csharp" && MayContainNestedGenericSyntax(line.PreparedLine);
        foreach (Match match in BoundedRegex.EnumerateMatches(CSharpJavaInitializerRegex, line.PreparedLine))
        {
            if (ReferenceLimitReached(line.References))
                break;
            var rawName = match.Groups["name"].Value;
            var nameIndex = match.Groups["name"].Index;
            (matchedInitializerIndices ??= []).Add(nameIndex);
            if (ShouldSkipInitializerName(line.Language, rawName))
                continue;
            // Do NOT skip when the type is defined in the same file — the CallRegex
            // `IsConstructorCallName` path emits `instantiate` without a line.DefinitionNames
            // filter, so `new Foo { ... }` and `new Foo()` should behave the same way.
            // 同一ファイル内定義でもスキップしない。`IsConstructorCallName` 経路の
            // `instantiate` が同様の扱いをしているため、括弧あり/なしで挙動を揃える。
            var initContainer = line.ResolveContainerForCall(nameIndex);
            var name = line.Language == "csharp" ? NormalizeCSharpIdentifier(rawName) : rawName;
            AddReference(line.References, line.Seen, line.FileId, name, nameIndex, "instantiate", line.Context, line.LineNumber, initContainer, line.Language);
        }

        // The initializer regex has the same one-level generic ceiling as CallRegex,
        // so nested generic targets like `new Dictionary<string, List<int>> { ... }`
        // need a depth-aware fallback to keep the outer `instantiate` edge.
        // initializer regex も CallRegex と同じく generic を 1 段までしか見ないため、
        // `new Dictionary<string, List<int>> { ... }` の外側型は depth-aware fallback
        // で補って `instantiate` を落とさないようにする。
        if (mayContainNestedGenericInitializer)
        {
            foreach (var candidate in EnumerateNestedGenericInitializerCandidates(
                         line.PreparedLine,
                         matchedInitializerIndices ?? EmptyMatchedIndices,
                         requireOpeningBrace: true))
            {
                if (ShouldSkipInitializerName(line.Language, candidate.Name))
                    continue;

                var initContainer = line.ResolveContainerForCall(candidate.NameIndex);
                AddReference(
                    line.References,
                    line.Seen,
                    line.FileId,
                    candidate.Name,
                    candidate.NameIndex,
                    "instantiate",
                    line.Context,
                    line.LineNumber,
                    initContainer,
                    line.Language);
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
        var trailingMatch = CSharpJavaInitializerTrailingRegex.Match(line.PreparedLine);
        var peek = line.LineIndex + 1;
        while (peek < line.PreparedLines.Length && string.IsNullOrWhiteSpace(line.PreparedLines[peek]))
            peek++;
        if (peek < line.PreparedLines.Length)
        {
            var nextContent = line.PreparedLines[peek].TrimStart();
            if (nextContent.Length > 0 && nextContent[0] == '{')
            {
                if (trailingMatch.Success)
                {
                    var rawName = trailingMatch.Groups["name"].Value;
                    var nameIndex = trailingMatch.Groups["name"].Index;
                    (matchedInitializerIndices ??= []).Add(nameIndex);
                    if (!ShouldSkipInitializerName(line.Language, rawName))
                    {
                        var initContainer = line.ResolveContainerForCall(nameIndex);
                        var name = line.Language == "csharp" ? NormalizeCSharpIdentifier(rawName) : rawName;
                        AddReference(line.References, line.Seen, line.FileId, name, nameIndex, "instantiate", line.Context, line.LineNumber, initContainer);
                    }

                }

                if (mayContainNestedGenericInitializer)
                {
                    foreach (var candidate in EnumerateNestedGenericInitializerCandidates(
                                 line.PreparedLine,
                                 matchedInitializerIndices ?? EmptyMatchedIndices,
                                 requireOpeningBrace: false))
                    {
                        if (ShouldSkipInitializerName(line.Language, candidate.Name))
                            continue;

                        var initContainer = line.ResolveContainerForCall(candidate.NameIndex);
                        var name = line.Language == "csharp" ? NormalizeCSharpIdentifier(candidate.Name) : candidate.Name;
                        AddReference(
                            line.References,
                            line.Seen,
                            line.FileId,
                            name,
                            candidate.NameIndex,
                            "instantiate",
                            line.Context,
                            line.LineNumber,
                            initContainer);
                    }
                }
            }
        }
    }

    private static void EmitScssLineReferences(
        in CoreReferenceLineContext line)
    {
        if (line.Language != "css")
            return;

        CssReferenceExtractor.EmitScss(
            line.PreparedLine,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            line.Container);
    }
}
