using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static bool ShouldSuppressCoreCallCandidate(
        in CoreCallReferenceContext call,
        string name,
        string normalizedName,
        int callIndex)
    {
        ref readonly var line = ref call.Line;

        // In tuple-return declarations, CallRegex can see the modifier in
        // `static (int Value, ...)` as a call. A verbatim `@static()` remains valid.
        if (line.Language == "csharp" && name == "static")
            return true;

        if (line.Language == "rust"
            && RustReferenceExtractor.IsFunctionDeclarationCallSite(
                line.PreparedLine,
                callIndex))
        {
            return true;
        }

        if (line.Language == "rust"
            && RustReferenceExtractor.IsDeriveAttributeCallSite(
                line.PreparedLine,
                normalizedName,
                callIndex))
        {
            return true;
        }

        if (line.Language == "wgsl" && name.StartsWith('@'))
            return true;

        if (line.Language == "kotlin"
            && KotlinReferenceExtractor.IsInfixFunctionDeclarationSite(
                line.PreparedLine,
                callIndex))
        {
            return true;
        }

        // A same-line Java constructor declarator is a declaration, not a call.
        if (call.JavaSameLineCtor is { } javaSameLineCtor
            && callIndex == javaSameLineCtor.NameIndex
            && string.Equals(
                normalizedName,
                javaSameLineCtor.Synthetic.Name,
                StringComparison.Ordinal))
        {
            return true;
        }

        if (line.Language == "csharp"
            && CSharpReferenceExtractor.IsPatternHeadCallSite(
                line.PreparedLines,
                line.LineIndex,
                line.PreparedLine,
                callIndex))
        {
            return true;
        }

        if (line.Language == "typescript"
            && TypeScriptReferenceExtractor.IsSatisfiesTypeOperand(
                line.PreparedLine,
                callIndex))
        {
            return true;
        }

        return call.Definitions.ShouldSuppressDefinitionCall(
            normalizedName,
            name,
            callIndex);
    }

    private static bool TryHandleCSharpValueReceiverCall(
        in CoreCallReferenceContext call,
        string name,
        string normalizedName,
        int callIndex,
        SymbolRecord? callContainer)
    {
        ref readonly var line = ref call.Line;
        var afterNameIndex = callIndex + name.Length;
        if (line.Language != "csharp"
            || afterNameIndex >= line.PreparedLine.Length
            || !line.PreparedLine.AsSpan(afterNameIndex)
                .TrimStart()
                .StartsWith(".", StringComparison.Ordinal))
        {
            return false;
        }

        var receiverLookups = call.Lookups.GetCSharpValueReceiverLookups();
        if (!HasCSharpValueReceiverConflict(
                normalizedName,
                normalizedName,
                line.LineNumber,
                callIndex,
                callContainer,
                receiverLookups.ByContainingType,
                receiverLookups.ByFunctionStartLine))
        {
            return false;
        }

        var containingType = GetContainingTypeQualifiedName(callContainer);
        if (containingType != null
            && receiverLookups.ByContainingType.TryGetValue(
                containingType,
                out var receiverNames)
            && (receiverNames.InstanceNames.Contains(normalizedName)
                || receiverNames.StaticNames.Contains(normalizedName))
            && call.Lookups.HasCSharpFieldOrPropertyMember(
                containingType,
                normalizedName))
        {
            var fileId = line.FileId;
            var lineNumber = line.LineNumber;
            line.References.RemoveAll(reference =>
                reference.FileId == fileId
                && reference.Line == lineNumber
                && reference.Column == callIndex + 1
                && reference.ReferenceKind == "type_reference"
                && string.Equals(
                    reference.SymbolName,
                    normalizedName,
                    StringComparison.Ordinal));
            AddCoreClassifiedReference(
                in call,
                $"{containingType}.{normalizedName}",
                callIndex,
                "reference",
                callContainer,
                line.Language,
                targetQualifier: null,
                name.Length);
        }

        return true;
    }
}
