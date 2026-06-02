using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;
using CSharpContainingTypeValueReceiverNames = CodeIndex.Indexer.ReferenceExtractor.CSharpContainingTypeValueReceiverNames;
using CSharpFunctionValueReceiverNameRecord = CodeIndex.Indexer.ReferenceExtractor.CSharpFunctionValueReceiverNameRecord;
using CSharpMultiLineTypePatternState = CodeIndex.Indexer.ReferenceExtractor.CSharpMultiLineTypePatternState;
using CSharpUsingAliasRecord = CodeIndex.Indexer.ReferenceExtractor.CSharpUsingAliasRecord;
using CSharpUsingStaticRecord = CodeIndex.Indexer.ReferenceExtractor.CSharpUsingStaticRecord;
using CSharpWhereConstraintState = CodeIndex.Indexer.ReferenceExtractor.CSharpWhereConstraintState;

namespace CodeIndex.Indexer;

internal static class CSharpReferenceExtractor
{
    private const string CallerInfoAttributeNamespace = "System.Runtime.CompilerServices.";
    private static readonly Dictionary<string, string> CallerInfoAttributeTypeNames = new(StringComparer.Ordinal)
    {
        ["CallerMemberName"] = CallerInfoAttributeNamespace + "CallerMemberNameAttribute",
        ["CallerFilePath"] = CallerInfoAttributeNamespace + "CallerFilePathAttribute",
        ["CallerLineNumber"] = CallerInfoAttributeNamespace + "CallerLineNumberAttribute",
        ["CallerArgumentExpression"] = CallerInfoAttributeNamespace + "CallerArgumentExpressionAttribute",
    };

    // C# constructor chain initializer: `public A() : this(0)` / `public B() : base(42)`
    // C# コンストラクタ連鎖イニシャライザ
    private static readonly Regex CtorChainRegex = new(@":\s*(?<kind>this|base)\s*\(", RegexOptions.Compiled);
    private static readonly Regex StaticMemberQualifierRegex = new(
        @"(?<![\p{L}\p{Nd}_@.])(?<qualifier>(?:global::)?@?[A-Z_][\p{L}\p{Nd}_]*(?:\.@?[A-Z_][\p{L}\p{Nd}_]*)*)\s*\.\s*@?[\p{L}_][\p{L}\p{Nd}_]*",
        RegexOptions.Compiled);

    public static void EmitCtorChainReferences(
        string preparedLine,
        IReadOnlyList<SymbolRecord> enclosingTypeCandidates,
        IReadOnlyList<SymbolRecord> containerCandidates,
        string[] structuralLines,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        var chainMatches = CtorChainRegex.Matches(preparedLine);
        if (chainMatches.Count == 0)
            return;

        var enclosingType = ReferenceExtractor.FindInnermostClassLike(enclosingTypeCandidates, lineNumber);
        if (enclosingType == null)
            return;

        // For cross-line initializers such as:
        //   public A(int x, int y)
        //       : this(x, 0)
        //   { }
        // the chain line precedes the body, so the inner-most "body-covering" container lookup
        // returns the class rather than the constructor. Fall back to a declaration-to-body-end
        // lookup so the reference is attributed to the constructor that owns the chain.
        // クロス行イニシャライザでは body よりも前に連鎖行が現れるため、body 範囲のみで
        // 判定すると外側クラスが選ばれる。宣言〜body 終端の範囲で探し直す。
        var chainContainer = container;
        if (chainContainer == null || chainContainer.Kind != "function")
        {
            chainContainer = FindDeclarationRangeFunction(containerCandidates, lineNumber) ?? chainContainer;
        }

        foreach (Match match in chainMatches)
        {
            var kindToken = match.Groups["kind"].Value;
            string? target;
            if (kindToken == "this")
            {
                target = enclosingType.Name;
            }
            else
            {
                // `base(...)` needs the base type from the enclosing class's signature.
                // SymbolRecord.Signature only captures the first declaration line, so multi-line
                // base-lists (e.g. `class Child\n    : Parent`) lose the `: Parent` continuation.
                // Reconstruct the joined header up to the first `;` or `{` from structuralLines.
                // `base(...)` は外側クラスのシグネチャから基底型を解析する必要がある。
                // SymbolRecord.Signature は宣言 1 行目しか持たないので複数行 base-list が欠落する。
                // structuralLines から最初の `;` / `{` までを連結し直して渡す。
                var (_, _, headerText) = ReferenceExtractor.CollectCSharpRecordHeader(
                    structuralLines,
                    enclosingType.StartLine);
                target = ReferenceExtractor.ParseCSharpBaseType(headerText);
                if (string.IsNullOrWhiteSpace(target))
                    target = ReferenceExtractor.ParseCSharpBaseType(enclosingType.Signature);
                if (string.IsNullOrWhiteSpace(target))
                    continue;
            }

            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                target!,
                match.Groups["kind"].Index,
                "call",
                context,
                lineNumber,
                chainContainer);
        }
    }

    public static void AdvanceMultiLineTypePatternState(
        string preparedLine,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedConstantPatternMemberLookup,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases,
        IReadOnlyList<CSharpUsingStaticRecord> csharpUsingStatics,
        Func<string, int, bool> hasActiveSameFileCSharpTypeCandidate,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        ref CSharpMultiLineTypePatternState state)
        => ReferenceExtractor.AdvanceCSharpMultiLineTypePatternState(
            preparedLine,
            context,
            lineNumber,
            resolveContainerForColumn,
            csharpQualifiedConstantPatternMemberLookup,
            csharpUsingAliases,
            csharpUsingStatics,
            hasActiveSameFileCSharpTypeCandidate,
            references,
            seen,
            fileId,
            ref state);

    public static void EmitTypePositionReferences(
        string preparedLine,
        string originalLine,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedConstantPatternMemberLookup,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedTypePatternLookup,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases,
        IReadOnlyList<CSharpUsingStaticRecord> csharpUsingStatics,
        Func<string, int, bool> hasActiveSameFileCSharpTypeCandidate,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        SymbolRecord? container,
        CSharpWhereConstraintState pendingWhereConstraint,
        ref CSharpMultiLineTypePatternState pendingCSharpMultiLineTypePattern)
        => ReferenceExtractor.EmitCSharpTypePositionReferences(
            preparedLine,
            originalLine,
            csharpQualifiedConstantPatternMemberLookup,
            csharpQualifiedTypePatternLookup,
            csharpUsingAliases,
            csharpUsingStatics,
            hasActiveSameFileCSharpTypeCandidate,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn,
            container,
            pendingWhereConstraint,
            ref pendingCSharpMultiLineTypePattern);

    public static bool HasTrailingIsAsTypePatternIntro(string preparedLine, string originalLine)
        => ReferenceExtractor.CSharpTrailingIsAsTypePatternIntroRegex.IsMatch(preparedLine)
           && ReferenceExtractor.HasTrailingCSharpTypePatternIntro(originalLine, ReferenceExtractor.CSharpIsAsTypePatternIntroContextRegex);

    public static bool HasTrailingCaseTypePatternIntro(string preparedLine, string originalLine)
        => ReferenceExtractor.CSharpTrailingCaseTypePatternIntroRegex.IsMatch(preparedLine)
           && ReferenceExtractor.HasTrailingCSharpTypePatternIntro(originalLine, ReferenceExtractor.CSharpCaseTypePatternIntroContextRegex);

    public static void StartWaitingForMultiLineTypePatternHead(ref CSharpMultiLineTypePatternState state)
        => ReferenceExtractor.StartWaitingForCSharpMultiLineTypePatternHead(ref state);

    public static void FlushPendingMultiLineTypePatternReference(
        ref CSharpMultiLineTypePatternState state,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedConstantPatternMemberLookup,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases,
        IReadOnlyList<CSharpUsingStaticRecord> csharpUsingStatics,
        Func<string, int, bool> hasActiveSameFileCSharpTypeCandidate,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId)
        => ReferenceExtractor.FlushPendingCSharpMultiLineTypePatternReference(
            ref state,
            csharpQualifiedConstantPatternMemberLookup,
            csharpUsingAliases,
            csharpUsingStatics,
            hasActiveSameFileCSharpTypeCandidate,
            references,
            seen,
            fileId);

    public static void EmitSwitchExpressionTypePatternReferences(
        IReadOnlyList<string> lines,
        IReadOnlyList<string> preparedLines,
        IReadOnlyList<SymbolRecord> containerCandidates,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedConstantPatternMemberLookup,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedTypePatternLookup,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases,
        IReadOnlyList<CSharpUsingStaticRecord> csharpUsingStatics,
        Func<string, int, bool> hasActiveSameFileCSharpTypeCandidate,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId)
        => ReferenceExtractor.EmitCSharpSwitchExpressionTypePatternReferences(
            lines,
            preparedLines,
            containerCandidates,
            csharpQualifiedConstantPatternMemberLookup,
            csharpQualifiedTypePatternLookup,
            csharpUsingAliases,
            csharpUsingStatics,
            hasActiveSameFileCSharpTypeCandidate,
            references,
            seen,
            fileId);

    public static void EmitDocCrefReferences(
        string originalLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        int columnOffset,
        string context,
        int lineNumber,
        SymbolRecord? container)
        => ReferenceExtractor.EmitCSharpDocCrefReferences(
            originalLine,
            references,
            seen,
            fileId,
            columnOffset,
            context,
            lineNumber,
            container);

    public static bool IsPatternHeadCallSite(string[] preparedLines, int lineIndex, string preparedLine, int nameIndex)
        => ReferenceExtractor.IsCSharpPatternHeadCallSite(preparedLines, lineIndex, preparedLine, nameIndex);

    public static void EmitStaticMemberQualifierReferences(
        string preparedLine,
        IReadOnlyList<(int start, int end)>? csharpAttrRangesOnLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForCall)
    {
        var trimmed = preparedLine.TrimStart();
        if (trimmed.StartsWith("namespace ", StringComparison.Ordinal)
            || trimmed.StartsWith("global using ", StringComparison.Ordinal)
            || IsCSharpUsingDirectiveLine(trimmed))
        {
            return;
        }

        foreach (Match match in StaticMemberQualifierRegex.Matches(preparedLine))
        {
            var qualifierGroup = match.Groups["qualifier"];
            var qualifier = qualifierGroup.Value;
            var qualifierStart = qualifierGroup.Index;
            if (IsInsideRange(csharpAttrRangesOnLine, qualifierStart))
                continue;
            if (IsLikelyPatternConstantAccess(preparedLine, qualifierStart))
                continue;
            if (IsLikelyTypeQualifiedAccess(preparedLine, qualifierStart))
                continue;
            if (IsLikelyQualifiedTypeDeclaration(preparedLine, match.Index + match.Length))
                continue;
            if (qualifier.IndexOf('.') >= 0 && !IsFollowedByInvocation(preparedLine, match.Index + match.Length))
                continue;

            var dotIndex = qualifier.LastIndexOf('.');
            var simpleNameStart = dotIndex >= 0
                ? dotIndex + 1
                : qualifier.StartsWith("global::", StringComparison.Ordinal)
                    ? "global::".Length
                    : 0;
            var simpleName = NormalizeCSharpIdentifier(qualifier[simpleNameStart..]);
            if (string.IsNullOrWhiteSpace(simpleName))
                continue;

            var simpleNameIndex = qualifierStart + simpleNameStart;
            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                simpleName,
                simpleNameIndex,
                "call",
                context,
                lineNumber,
                resolveContainerForCall(simpleNameIndex));
        }
    }

    public static string? TryGetCallerInfoAttributeTypeName(string attributeName, string? preparedLine = null, int nameIndex = -1)
    {
        var normalizedName = NormalizeCSharpIdentifier(attributeName);
        var qualifier = GetCSharpAttributeQualifier(preparedLine, nameIndex) ?? GetQualifierFromAttributeName(normalizedName);
        if (qualifier != null && !IsCompilerServicesQualifier(qualifier))
            return null;

        var simpleName = normalizedName;
        var qualifierIndex = Math.Max(simpleName.LastIndexOf('.'), simpleName.LastIndexOf(':'));
        if (qualifierIndex >= 0 && qualifierIndex + 1 < simpleName.Length)
            simpleName = simpleName[(qualifierIndex + 1)..];

        if (simpleName.EndsWith("Attribute", StringComparison.Ordinal)
            && simpleName.Length > "Attribute".Length)
        {
            simpleName = simpleName[..^"Attribute".Length];
        }

        return CallerInfoAttributeTypeNames.TryGetValue(simpleName, out var typeName)
            ? typeName
            : null;
    }

    private static string? GetQualifierFromAttributeName(string attributeName)
    {
        var qualifierIndex = Math.Max(attributeName.LastIndexOf('.'), attributeName.LastIndexOf(':'));
        return qualifierIndex > 0
            ? NormalizeCSharpAttributeQualifier(attributeName[..qualifierIndex])
            : null;
    }

    private static string? GetCSharpAttributeQualifier(string? preparedLine, int nameIndex)
    {
        if (string.IsNullOrEmpty(preparedLine) || nameIndex <= 0 || nameIndex > preparedLine.Length)
            return null;

        var cursor = nameIndex - 1;
        while (cursor >= 0 && char.IsWhiteSpace(preparedLine[cursor]))
            cursor--;

        if (cursor < 0)
            return null;
        if (preparedLine[cursor] != '.'
            && !(preparedLine[cursor] == ':' && cursor > 0 && preparedLine[cursor - 1] == ':'))
        {
            return null;
        }

        var end = cursor + 1;
        while (cursor >= 0)
        {
            var ch = preparedLine[cursor];
            if (char.IsWhiteSpace(ch) || ch == '.' || ch == ':' || ch == '@' || ch == '_' || char.IsLetterOrDigit(ch))
            {
                cursor--;
                continue;
            }

            break;
        }

        return NormalizeCSharpAttributeQualifier(preparedLine[(cursor + 1)..end]);
    }

    private static string? NormalizeCSharpAttributeQualifier(string qualifier)
    {
        var compact = new string(qualifier.Where(ch => !char.IsWhiteSpace(ch)).ToArray());
        while (compact.EndsWith(".", StringComparison.Ordinal))
            compact = compact[..^1];
        while (compact.EndsWith("::", StringComparison.Ordinal))
            compact = compact[..^2];
        return compact.Length > 0 ? compact : null;
    }

    private static bool IsCompilerServicesQualifier(string qualifier)
        => string.Equals(qualifier, "System.Runtime.CompilerServices", StringComparison.Ordinal)
           || string.Equals(qualifier, "global::System.Runtime.CompilerServices", StringComparison.Ordinal);

    private static bool IsCSharpUsingDirectiveLine(string trimmedLine)
    {
        if (!trimmedLine.StartsWith("using ", StringComparison.Ordinal))
            return false;

        var afterUsing = trimmedLine["using ".Length..].TrimStart();
        if (afterUsing.StartsWith("(", StringComparison.Ordinal)
            || afterUsing.StartsWith("var ", StringComparison.Ordinal))
        {
            return false;
        }

        var equalsIndex = afterUsing.IndexOf('=');
        if (equalsIndex < 0)
            return true;

        var beforeEquals = afterUsing[..equalsIndex].Trim();
        return beforeEquals.Length > 0
            && beforeEquals.IndexOfAny([' ', '\t']) < 0;
    }

    private static bool IsInsideRange(IReadOnlyList<(int start, int end)>? ranges, int index)
    {
        if (ranges == null)
            return false;

        foreach (var (start, end) in ranges)
        {
            if (index >= start && index < end)
                return true;
        }

        return false;
    }

    private static bool IsLikelyPatternConstantAccess(string preparedLine, int qualifierStart)
    {
        var before = preparedLine[..qualifierStart];
        var trimmedBefore = before.TrimStart();
        return trimmedBefore.StartsWith("case ", StringComparison.Ordinal)
            || trimmedBefore.EndsWith(" is ", StringComparison.Ordinal)
            || trimmedBefore.EndsWith(" is not ", StringComparison.Ordinal)
            || trimmedBefore.EndsWith(" and ", StringComparison.Ordinal)
            || trimmedBefore.EndsWith(" or ", StringComparison.Ordinal);
    }

    private static bool IsLikelyTypeQualifiedAccess(string preparedLine, int qualifierStart)
    {
        var before = preparedLine[..qualifierStart].TrimEnd();
        return before.EndsWith("new", StringComparison.Ordinal)
            || before.EndsWith("typeof(", StringComparison.Ordinal)
            || before.EndsWith("nameof(", StringComparison.Ordinal)
            || before.EndsWith("sizeof(", StringComparison.Ordinal)
            || before.EndsWith("default(", StringComparison.Ordinal);
    }

    private static bool IsLikelyQualifiedTypeDeclaration(string preparedLine, int afterMemberIndex)
    {
        var index = afterMemberIndex;
        var sawWhitespace = false;
        while (index < preparedLine.Length && char.IsWhiteSpace(preparedLine[index]))
        {
            sawWhitespace = true;
            index++;
        }

        return sawWhitespace
            && index < preparedLine.Length
            && (char.IsLetter(preparedLine[index]) || preparedLine[index] == '_' || preparedLine[index] == '@');
    }

    private static bool IsFollowedByInvocation(string preparedLine, int afterMemberIndex)
    {
        var index = afterMemberIndex;
        while (index < preparedLine.Length && char.IsWhiteSpace(preparedLine[index]))
            index++;

        return index < preparedLine.Length && preparedLine[index] == '(';
    }

    private static string NormalizeCSharpIdentifier(string identifier) =>
        !string.IsNullOrEmpty(identifier) && identifier[0] == '@'
            ? identifier[1..]
            : identifier;

    public static void EmitQualifiedEnumMemberReferences(
        string preparedLine,
        IReadOnlyDictionary<string, List<(string EnumName, string? QualifiedEnumName, bool AllowShortNameFallback)>> enumMemberLookup,
        IReadOnlyList<(int start, int end)>? csharpAttrRangesOnLine,
        IReadOnlyList<CSharpUsingAliasRecord> usingAliases,
        IReadOnlyDictionary<string, CSharpContainingTypeValueReceiverNames> valueReceiverNamesByContainingType,
        IReadOnlyDictionary<int, List<CSharpFunctionValueReceiverNameRecord>> valueReceiverNamesByFunctionStartLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForCall)
        => ReferenceExtractor.EmitCSharpQualifiedEnumMemberReferences(
            preparedLine,
            enumMemberLookup,
            csharpAttrRangesOnLine,
            usingAliases,
            valueReceiverNamesByContainingType,
            valueReceiverNamesByFunctionStartLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForCall);

    private static SymbolRecord? FindDeclarationRangeFunction(
        IReadOnlyList<SymbolRecord> candidates,
        int lineNumber)
    {
        foreach (var candidate in candidates)
        {
            if (candidate.Kind is not ("function" or "operator"))
                continue;
            if (candidate.StartLine <= lineNumber && candidate.BodyEndLine!.Value >= lineNumber)
                return candidate;
        }

        return null;
    }
}
