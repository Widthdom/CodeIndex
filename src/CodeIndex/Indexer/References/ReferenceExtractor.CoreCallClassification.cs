using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static bool TryAddCoreCallLikeReference(
        CoreCallReferenceContext call,
        string name,
        int callIndex,
        string? targetQualifier = null)
    {
        var line = call.Line;
        var normalizedName =
            line.Language == "fsharp"
            && FSharpReferenceExtractor.IsOperatorCallName(name)
                ? $"operator {name}"
                : line.Language == "rust"
                    ? RustReferenceExtractor.NormalizeIdentifier(name)
                    : NormalizeAtPrefixedIdentifier(name);

        // In tuple-return declarations such as `private static (int Value, string Error)
        // Resolve(...)`, CallRegex sees the modifier token as `static(`. It is a C# keyword,
        // never a callable identifier, so suppress the phantom edge before graph ingestion.
        // `private static (int Value, string Error) Resolve(...)` のような tuple return 宣言では
        // CallRegex が modifier を `static(` と誤認する。C# keyword は呼び出し対象にならないため、
        // graph に入る前に phantom edge を除外する。
        if (line.Language == "csharp" && name == "static")
            return false;

        if (line.Language == "rust"
            && RustReferenceExtractor.IsFunctionDeclarationCallSite(
                line.PreparedLine,
                callIndex))
        {
            return false;
        }
        if (line.Language == "rust"
            && RustReferenceExtractor.IsDeriveAttributeCallSite(
                line.PreparedLine,
                normalizedName,
                callIndex))
        {
            return false;
        }
        if (line.Language == "wgsl" && name.StartsWith('@'))
            return false;
        if (line.Language == "kotlin"
            && KotlinReferenceExtractor.IsInfixFunctionDeclarationSite(
                line.PreparedLine,
                callIndex))
        {
            return false;
        }

        // Suppress the same-line Java ctor declarator's self-call. CallRegex matches
        // `CtorName(` at the declarator once per same-line ctor, but it is a declaration
        // site — not a call — so attributing it to `class:CtorName` produces a phantom
        // `CtorName|call|class|CtorName` edge. `line.DefinitionNames` does not cover this
        // because same-line ctors do not appear in the symbol table.
        // 同一行 ctor の宣言子 `CtorName(` は呼び出しではないため CallRegex の対象から除外する。
        if (call.JavaSameLineCtor != null
            && callIndex == call.JavaSameLineCtor.Value.NameIndex
            && string.Equals(
                normalizedName,
                call.JavaSameLineCtor.Value.Synthetic.Name,
                StringComparison.Ordinal))
        {
            return false;
        }

        // C# positional patterns such as `case Point(var x, var y):` are type-pattern
        // heads, not calls. `CallRegex` still sees `Point(` and would otherwise emit a
        // phantom `call` edge alongside the real `type_reference`.
        // C# の positional pattern (`case Point(var x, var y):`) は型パターンの先頭であり、
        // 呼び出しではない。`CallRegex` が `Point(` を拾ってしまうため、そのままだと
        // 本物の `type_reference` に加えて phantom な `call` エッジが出る。
        if (line.Language == "csharp"
            && CSharpReferenceExtractor.IsPatternHeadCallSite(
                line.PreparedLines,
                line.LineIndex,
                line.PreparedLine,
                callIndex))
        {
            return false;
        }
        if (line.Language == "typescript"
            && TypeScriptReferenceExtractor.IsSatisfiesTypeOperand(
                line.PreparedLine,
                callIndex))
        {
            return false;
        }
        if (call.Definitions.ShouldSuppressDefinitionCall(
                normalizedName,
                name,
                callIndex))
        {
            return false;
        }

        var callContainer = line.ResolveContainerForCall(callIndex);
        if (line.Language == "csharp"
            && callIndex + name.Length < line.PreparedLine.Length
            && line.PreparedLine.AsSpan(callIndex + name.Length)
                .TrimStart()
                .StartsWith(".", StringComparison.Ordinal))
        {
            var receiverLookups = call.Lookups.GetCSharpValueReceiverLookups();
            if (HasCSharpValueReceiverConflict(
                    normalizedName,
                    normalizedName,
                    line.LineNumber,
                    callIndex,
                    callContainer,
                    receiverLookups.ByContainingType,
                    receiverLookups.ByFunctionStartLine))
            {
                var containingType =
                    GetContainingTypeQualifiedName(callContainer);
                if (containingType != null
                    && receiverLookups.ByContainingType.TryGetValue(
                        containingType,
                        out var receiverNames)
                    && (receiverNames.InstanceNames.Contains(normalizedName)
                        || receiverNames.StaticNames.Contains(normalizedName))
                    && call.Lookups.HasCSharpProperty(
                        containingType,
                        normalizedName))
                {
                    line.References.RemoveAll(reference =>
                        reference.FileId == line.FileId
                        && reference.Line == line.LineNumber
                        && reference.Column == callIndex + 1
                        && reference.ReferenceKind == "type_reference"
                        && string.Equals(
                            reference.SymbolName,
                            normalizedName,
                            StringComparison.Ordinal));
                    AddReference(
                        line.References,
                        line.Seen,
                        line.FileId,
                        $"{containingType}.{normalizedName}",
                        callIndex,
                        "reference",
                        line.Context,
                        line.LineNumber,
                        callContainer,
                        line.Language,
                        sourceLength: name.Length);
                }

                return false;
            }
        }
        if (IsConstructorCallName(
                line.Language,
                line.PreparedLine,
                callIndex))
        {
            AddReference(
                line.References,
                line.Seen,
                line.FileId,
                normalizedName,
                callIndex,
                "instantiate",
                line.Context,
                line.LineNumber,
                callContainer,
                line.Language,
                targetQualifier,
                name.Length);
            return true;
        }
        if (line.Language == "rust"
            && RustReferenceExtractor.IsLikelyInstantiationCallName(
                name,
                normalizedName,
                line.PreparedLine,
                callIndex))
        {
            AddReference(
                line.References,
                line.Seen,
                line.FileId,
                normalizedName,
                callIndex,
                "instantiate",
                line.Context,
                line.LineNumber,
                callContainer,
                line.Language,
                sourceLength: name.Length);
            return true;
        }
        if (line.Language == "python"
            && TryGetKnownPythonTypeCall(
                call,
                normalizedName,
                callIndex,
                out var pythonTypeName))
        {
            AddReference(
                line.References,
                line.Seen,
                line.FileId,
                pythonTypeName,
                callIndex,
                "instantiate",
                line.Context,
                line.LineNumber,
                callContainer,
                line.Language,
                sourceLength: name.Length);
            return true;
        }
        if (line.Language == "csharp"
            && CSharpReferenceExtractor.ShouldSuppressQualifiedCommonMemberCall(
                line.PreparedLine,
                normalizedName,
                callIndex))
        {
            return false;
        }
        if (IsIgnoredCallName(line.Language, name)
            && !(line.Language == "scala"
                && string.Equals(name, "foreach", StringComparison.Ordinal)))
        {
            return false;
        }

        var insideCSharpAttributeRange = call.CSharpAttributeRanges != null
            && IsInsideCSharpAttributeRange(
                call.CSharpAttributeRanges,
                callIndex);
        var metadataKind = TryClassifyMetadataReference(
            line.Language,
            line.PreparedLine,
            callIndex,
            insideCSharpAttributeRange);
        if (metadataKind != null)
        {
            AddReference(
                line.References,
                line.Seen,
                line.FileId,
                normalizedName,
                callIndex,
                metadataKind,
                line.Context,
                line.LineNumber,
                callContainer,
                line.Language,
                sourceLength: name.Length);
            if (line.Language == "csharp"
                && metadataKind == "attribute"
                && CSharpReferenceExtractor.TryGetCallerInfoAttributeTypeName(
                    name,
                    line.PreparedLine,
                    callIndex) is { } callerInfoAttributeTypeName)
            {
                AddReference(
                    line.References,
                    line.Seen,
                    line.FileId,
                    callerInfoAttributeTypeName,
                    callIndex,
                    "type_reference",
                    line.Context,
                    line.LineNumber,
                    callContainer,
                    line.Language,
                    sourceLength: name.Length);
            }
            return true;
        }

        if (line.Language == "kotlin"
            && KotlinReferenceExtractor.IsConstructorCallName(
                normalizedName,
                call.KotlinConstructorTypeNames!))
        {
            AddReference(
                line.References,
                line.Seen,
                line.FileId,
                normalizedName,
                callIndex,
                "instantiate",
                line.Context,
                line.LineNumber,
                callContainer,
                sourceLength: name.Length);
            return true;
        }

        if (line.Language is "javascript" or "typescript"
            && SymbolExtractor.IsJavaScriptTypeScriptReactHookName(
                normalizedName))
        {
            AddReference(
                line.References,
                line.Seen,
                line.FileId,
                normalizedName,
                callIndex,
                "consumes_hook",
                line.Context,
                line.LineNumber,
                callContainer,
                sourceLength: name.Length);
            return true;
        }

        AddReference(
            line.References,
            line.Seen,
            line.FileId,
            normalizedName,
            callIndex,
            "call",
            line.Context,
            line.LineNumber,
            callContainer,
            ScientificNativeReferenceExtractor.Supports(line.Language)
                ? line.Language
                : null,
            targetQualifier: targetQualifier,
            sourceLength: name.Length);
        return true;
    }

    private static bool TryGetKnownPythonTypeCall(
        CoreCallReferenceContext call,
        string candidate,
        int callIndex,
        out string canonicalName)
    {
        canonicalName = candidate;
        var separator = candidate.LastIndexOf('.');
        var leaf = separator >= 0 ? candidate[(separator + 1)..] : candidate;
        if (leaf.Length == 0 || !char.IsUpper(leaf, 0))
            return false;

        if (call.Lookups.HasSameFilePythonClass(candidate, leaf))
            return true;

        return PythonImportBindingResolver.TryResolveImportedTypeCall(
            candidate,
            call.Line.PreparedLine,
            callIndex,
            call.Lookups.GetPythonImportedTypeCallLookup(),
            out canonicalName);
    }
}
