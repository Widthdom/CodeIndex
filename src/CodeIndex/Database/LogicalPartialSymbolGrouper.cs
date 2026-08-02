using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using CodeIndex.Indexer;

namespace CodeIndex.Database;

internal static class LogicalPartialSymbolGrouper
{
    private const char KeySeparator = '\u001f';
    private static readonly IReadOnlyDictionary<string, string> CSharpPredefinedTypeIdentities =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["bool"] = "System.Boolean",
            ["byte"] = "System.Byte",
            ["sbyte"] = "System.SByte",
            ["char"] = "System.Char",
            ["decimal"] = "System.Decimal",
            ["double"] = "System.Double",
            ["float"] = "System.Single",
            ["int"] = "System.Int32",
            ["uint"] = "System.UInt32",
            ["nint"] = "System.IntPtr",
            ["nuint"] = "System.UIntPtr",
            ["long"] = "System.Int64",
            ["ulong"] = "System.UInt64",
            ["short"] = "System.Int16",
            ["ushort"] = "System.UInt16",
            ["object"] = "System.Object",
            ["dynamic"] = "System.Object",
            ["string"] = "System.String",
            ["void"] = "System.Void",
        };
    private static readonly IReadOnlyDictionary<string, string> CSharpFrameworkTypeIdentities =
        CSharpPredefinedTypeIdentities.Values
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(
                value => value["System.".Length..],
                value => value,
                StringComparer.Ordinal);
    private static readonly HashSet<string> CSharpFrameworkReferenceTypeIdentities =
    [
        "global::System.Object",
        "global::System.String",
    ];
    internal const int FamilyMemberLimit = 50;
    internal const string ImplementationBodyReason = "implementation_body";
    internal const string NonGeneratedSourceReason = "non_generated_source";
    internal const string SemanticDeclarationReason = "semantic_declaration";
    internal const string CanonicalDeclarationIdentityReason = "canonical_declaration_identity";
    internal const string StableLocationReason = "stable_path_and_position";

    internal static string BuildSqlKeyExpression(
        string languageSql,
        string kindSql,
        string nameSql,
        string symbolIdSql,
        string fileIdentitySql,
        string signatureSql,
        string containerNameSql,
        string containerQualifiedNameSql,
        string familyKeySql,
        string? returnTypeSql = null,
        string? isPartialDeclarationSql = null,
        bool csharpFamilyContractReady = true)
    {
        if (!csharpFamilyContractReady)
            return $"'symbol:' || {symbolIdSql}";

        var persistedFamilySql = $"NULLIF(TRIM({familyKeySql}), '')";
        var scopedPersistedFamilySql = persistedFamilySql;
        var fallbackContainerSql = $"COALESCE(NULLIF(TRIM({containerQualifiedNameSql}), ''), NULLIF(TRIM({containerNameSql}), ''), '')";
        var normalizedSignatureSql = $"REPLACE(REPLACE(REPLACE(LOWER(COALESCE({signatureSql}, '')), CHAR(9), ' '), CHAR(10), ' '), CHAR(13), ' ')";
        var signaturePartialDeclarationSql = $"INSTR(' ' || {normalizedSignatureSql} || ' ', ' partial ') > 0";
        var partialDeclarationSql = isPartialDeclarationSql == null
            ? signaturePartialDeclarationSql
            : $"COALESCE({isPartialDeclarationSql}, CASE WHEN {signaturePartialDeclarationSql} THEN 1 ELSE 0 END) <> 0";
        var projectPrefixSql = $"CASE WHEN INSTR(COALESCE({persistedFamilySql}, ''), '|') > 0 THEN SUBSTR({persistedFamilySql}, 1, INSTR({persistedFamilySql}, '|')) ELSE '' END";
        var typeAritySql = $"COALESCE(csharp_definition_type_arity({signatureSql}, {nameSql}, {kindSql}), 0)";
        var typeIdentitySql = $"{nameSql} || CASE WHEN {typeAritySql} > 0 THEN '`' || {typeAritySql} ELSE '' END";
        var reconstructedSelfFamilySql = $"{projectPrefixSql} || CASE WHEN {fallbackContainerSql} = '' THEN {typeIdentitySql} ELSE {fallbackContainerSql} || '.' || {typeIdentitySql} END";
        var selfFamilySql = $"COALESCE({scopedPersistedFamilySql}, {reconstructedSelfFamilySql})";
        var callableSignatureSql = $"CASE WHEN {signaturePartialDeclarationSql} THEN {signatureSql} WHEN {partialDeclarationSql} THEN 'partial ' || COALESCE({signatureSql}, '') ELSE {signatureSql} END";
        var callableContainerSql = $"COALESCE({scopedPersistedFamilySql}, NULLIF({fallbackContainerSql}, ''))";
        var callableIdentitySql = returnTypeSql == null
            ? "NULL"
            : $"csharp_partial_callable_identity({callableSignatureSql}, {nameSql}, {returnTypeSql}, {callableContainerSql}, {symbolIdSql})";
        return $@"CASE
            WHEN {languageSql} = 'csharp'
             AND {kindSql} IN ('class', 'struct', 'interface', 'record')
             AND {partialDeclarationSql}
            THEN 'family:' || {languageSql} || CHAR(31) || {kindSql} || CHAR(31) ||
                 {selfFamilySql}
            WHEN {languageSql} = 'csharp'
             AND {kindSql} IN ('function', 'test.method')
             AND {partialDeclarationSql}
             AND {callableContainerSql} IS NOT NULL
             AND {callableIdentitySql} IS NOT NULL
            THEN 'family:' || {languageSql} || CHAR(31) || 'function' || CHAR(31) ||
                 {callableContainerSql} || CHAR(31) || {callableIdentitySql}
            ELSE 'symbol:' || {symbolIdSql}
        END";
    }

    internal static string BuildSqlPrimaryRankExpression(
        string kindSql,
        string bodyStartLineSql,
        string bodyEndLineSql)
        => $"CASE WHEN {kindSql} IN ('function', 'test.method') AND ({bodyStartLineSql} IS NULL OR {bodyEndLineSql} IS NULL) THEN 1 ELSE 0 END";

    internal static string BuildSqlSemanticScoreExpression(
        string signatureSql,
        string kindSql,
        string? declarationSemanticScoreSql = null)
    {
        var signatureScoreSql = $"csharp_partial_semantic_score({signatureSql}, {kindSql})";
        return declarationSemanticScoreSql == null
            ? signatureScoreSql
            : $"COALESCE({declarationSemanticScoreSql}, {signatureScoreSql})";
    }

    public static List<T> Group<T>(IReadOnlyList<T> symbols)
        where T : SymbolResult
    {
        if (symbols.Count <= 1)
            return symbols.ToList();

        var groups = symbols
            .Select(symbol => (symbol, key: TryBuildKey(symbol, out var key) ? key : null))
            .Where(item => item.key != null)
            .GroupBy(item => item.key!, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .ToDictionary(group => group.Key, group => group.Select(item => item.symbol).ToList(), StringComparer.Ordinal);
        if (groups.Count == 0)
            return symbols.ToList();

        var emittedKeys = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<T>(symbols.Count);
        foreach (var symbol in symbols)
        {
            if (!TryBuildKey(symbol, out var key) || !groups.TryGetValue(key, out var group))
            {
                results.Add(symbol);
                continue;
            }
            if (!emittedKeys.Add(key))
                continue;

            var representative = group
                .OrderBy(GetPrimaryRank)
                .ThenBy(IsGeneratedCode)
                .ThenByDescending(GetSemanticScore)
                .ThenBy(GetCanonicalDeclarationIdentity, StringComparer.Ordinal)
                .ThenBy(result => result.Path, StringComparer.Ordinal)
                .ThenBy(result => result.StartLine)
                .ThenBy(result => result.StartColumn ?? int.MaxValue)
                .ThenBy(result => result.SymbolId ?? long.MaxValue)
                .First();
            PopulateFamilyMetadata(representative, group, key);
            results.Add(representative);
        }

        return results;
    }

    public static bool TryBuildKey(SymbolResult symbol, out string key)
    {
        if (!string.Equals(symbol.Lang, "csharp", StringComparison.OrdinalIgnoreCase))
        {
            key = string.Empty;
            return false;
        }

        if (!string.IsNullOrWhiteSpace(symbol.LogicalPartialKey))
        {
            if (symbol.LogicalPartialKey.StartsWith("family:", StringComparison.Ordinal))
            {
                key = symbol.LogicalPartialKey;
                return true;
            }

            // A persisted physical key is authoritative when the index contract is stale.
            // Reconstructing a family from the signature here would contradict the SQL
            // readiness gate. stale index の physical key は SQL readiness gate の結果なので、
            // signature から family を再構築してはならない。
            key = string.Empty;
            return false;
        }

        return TryBuildDeclarationKey(symbol, out key);
    }

    internal static bool TryBuildTypeFamilyKeyForReferenceResolution(
        SymbolResult symbol,
        out string key)
    {
        if (TryBuildKey(symbol, out key))
            return true;

        // A stale family contract deliberately exposes physical `symbol:*` rows to
        // ordinary grouping, but LSP position resolution still needs to distinguish
        // partial type declarations from same-name constructors. Reconstruct only a
        // partial *type* identity for that local disambiguation step; never use this
        // degraded fallback for result collapsing.
        // stale family contract は通常 query では意図的に physical `symbol:*` row を
        // 維持する。一方 LSP の位置解決では partial type 宣言と同名 constructor を
        // 区別する必要があるため、この局所的な判定に限って partial *type* identity
        // を再構築し、result grouping には流用しない。
        if (!IsLogicalPartialTypeKind(symbol.Kind))
        {
            key = string.Empty;
            return false;
        }

        return TryBuildDeclarationKey(symbol, out key);
    }

    private static bool TryBuildDeclarationKey(SymbolResult symbol, out string key)
    {

        if (string.IsNullOrWhiteSpace(symbol.Signature)
            || !ContainsPartialModifier(symbol.Signature, symbol.Kind, symbol.Name))
        {
            key = string.Empty;
            return false;
        }

        var containerIdentity = symbol.ContainerQualifiedName ?? symbol.ContainerName ?? string.Empty;
        if (IsLogicalPartialCallableKind(symbol.Kind))
        {
            var callableIdentity = BuildCallableIdentity(symbol.Signature, symbol.Name, symbol.ReturnType);
            if (callableIdentity == null || string.IsNullOrWhiteSpace(containerIdentity))
            {
                key = string.Empty;
                return false;
            }

            key = string.Join(
                KeySeparator,
                symbol.Lang?.ToLowerInvariant() ?? string.Empty,
                "function",
                containerIdentity,
                callableIdentity);
            return true;
        }

        if (!IsLogicalPartialTypeKind(symbol.Kind))
        {
            key = string.Empty;
            return false;
        }

        var genericArity = CSharpTypeReferenceArity.GetDefinitionArity(
            symbol.Signature,
            symbol.Name,
            symbol.Kind);
        var typeIdentity = genericArity > 0
            ? $"{symbol.Name}`{genericArity.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : symbol.Name;
        key = string.Join(
            KeySeparator,
            symbol.Lang?.ToLowerInvariant() ?? string.Empty,
            symbol.Kind.ToLowerInvariant(),
            typeIdentity,
            containerIdentity);
        return true;
    }

    internal static string? BuildCallableIdentity(
        string? signature,
        string? name,
        string? returnType,
        string? containerQualifiedName = null,
        CSharpCallableTypeKindLookup? typeKinds = null,
        long? symbolId = null)
    {
        if (string.IsNullOrWhiteSpace(signature)
            || string.IsNullOrWhiteSpace(name)
            || string.IsNullOrWhiteSpace(returnType)
            || !ContainsPartialModifier(signature, "function", name))
        {
            return null;
        }

        var normalizedName = name.TrimStart('@');
        var nameOffset = FindCallableNameOffset(signature, normalizedName);
        if (nameOffset < 0)
            return null;

        var cursor = SkipCSharpDeclarationTrivia(signature, nameOffset + name.Length);

        var genericArity = 0;
        var genericParameterNames = new List<string>();
        if (cursor < signature.Length && signature[cursor] == '<')
        {
            var genericEnd = FindBalancedEnd(signature, cursor, '<', '>');
            if (genericEnd < 0)
                return null;
            genericParameterNames = SplitTopLevel(signature[(cursor + 1)..genericEnd])
                .Select(ExtractGenericParameterName)
                .ToList();
            genericArity = genericParameterNames.Count;
            cursor = SkipCSharpDeclarationTrivia(signature, genericEnd + 1);
        }

        if (cursor >= signature.Length || signature[cursor] != '(')
            return null;

        var closeParenthesis = FindBalancedEnd(signature, cursor, '(', ')');
        if (closeParenthesis < 0)
            return null;

        var valueConstrainedGenericParameters = ReadValueConstrainedGenericParameters(
            signature,
            closeParenthesis,
            genericParameterNames);
        var parameterIdentity = BuildCallableParameterIdentity(
            signature[(cursor + 1)..closeParenthesis],
            genericParameterNames,
            valueConstrainedGenericParameters,
            containerQualifiedName,
            typeKinds,
            symbolId);
        return string.Join(
            KeySeparator,
            normalizedName,
            NormalizeCallableTypeIdentity(
                RemoveCallableReturnDeclarationTrivia(returnType),
                genericParameterNames,
                valueConstrainedGenericParameters,
                containerQualifiedName,
                typeKinds,
                symbolId),
            genericArity.ToString(System.Globalization.CultureInfo.InvariantCulture),
            parameterIdentity);
    }

    private static string RemoveCallableReturnDeclarationTrivia(string returnType)
    {
        var remaining = RemoveLeadingParameterAttributes(RemoveCSharpComments(returnType));
        while (TryReadLeadingIdentifier(remaining, out var modifier, out var modifierLength)
               && modifier.ToLowerInvariant() is
                   "public" or "private" or "protected" or "internal" or
                   "new" or "static" or "virtual" or "sealed" or "abstract" or
                   "override" or "extern" or "unsafe" or "async" or "partial")
        {
            remaining = RemoveLeadingParameterAttributes(remaining[modifierLength..]);
        }

        // Keep ref/readonly as part of the return identity; only declaration attributes
        // and method modifiers are discarded. This also repairs legacy rows where an
        // inline attribute caused those tokens to leak into return_type.
        // ref/readonly は return identity の一部として保持し、宣言 attribute と method
        // modifier だけを除去する。行内 attribute によりそれらが return_type へ漏れた
        // legacy row も同じ処理で補正する。
        return remaining.Trim();
    }

    internal static int GetSemanticScore(string? signature, string? kind)
    {
        if (string.IsNullOrWhiteSpace(signature))
            return 0;

        var declaration = ExtractCSharpDeclarationHeader(RemoveCSharpComments(signature));
        var score = 0;
        if (declaration.Contains('['))
            score += 2;
        if (IsLogicalPartialTypeKind(kind ?? string.Empty)
            && declaration.Contains(':', StringComparison.Ordinal))
        {
            score += 4;
        }
        if (declaration.Contains(" where ", StringComparison.Ordinal))
            score += 1;
        return score;
    }

    internal static string BuildCanonicalDeclarationIdentity(string? signature)
        => NormalizeIdentityToken(
            string.IsNullOrWhiteSpace(signature)
                ? signature
                : ExtractCSharpDeclarationHeader(RemoveCSharpComments(signature)));

    private static string ExtractCSharpDeclarationHeader(string declaration)
    {
        var parenthesisDepth = 0;
        var bracketDepth = 0;
        for (var index = 0; index < declaration.Length; index++)
        {
            switch (declaration[index])
            {
                case '(':
                    parenthesisDepth++;
                    break;
                case ')' when parenthesisDepth > 0:
                    parenthesisDepth--;
                    break;
                case '[':
                    bracketDepth++;
                    break;
                case ']' when bracketDepth > 0:
                    bracketDepth--;
                    break;
                case '{' when parenthesisDepth == 0 && bracketDepth == 0:
                    return declaration[..index];
                case '=' when parenthesisDepth == 0
                    && bracketDepth == 0
                    && index + 1 < declaration.Length
                    && declaration[index + 1] == '>':
                    return declaration[..index];
            }
        }

        return declaration;
    }

    internal static string BuildPartialFamilyId(string key)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return $"partial:{Convert.ToHexString(digest.AsSpan(0, 12)).ToLowerInvariant()}";
    }

    internal static string ResolveRepresentativeReason<T>(T representative, IReadOnlyList<T> group)
        where T : SymbolResult
    {
        if (group.Any(candidate => GetPrimaryRank(candidate) != GetPrimaryRank(representative)))
            return ImplementationBodyReason;
        if (group.Any(candidate => IsGeneratedCode(candidate) != IsGeneratedCode(representative)))
            return NonGeneratedSourceReason;
        if (group.Any(candidate => GetSemanticScore(candidate) != GetSemanticScore(representative)))
            return SemanticDeclarationReason;
        if (group.Any(candidate => !string.Equals(
                GetCanonicalDeclarationIdentity(candidate),
                GetCanonicalDeclarationIdentity(representative),
                StringComparison.Ordinal)))
        {
            return CanonicalDeclarationIdentityReason;
        }
        return StableLocationReason;
    }

    private static void PopulateFamilyMetadata<T>(T representative, IReadOnlyList<T> group, string key)
        where T : SymbolResult
    {
        representative.DefinitionSites = group.Count;
        representative.PartialFamilyId = BuildPartialFamilyId(key);
        representative.RepresentativeReason = ResolveRepresentativeReason(representative, group);
        representative.FamilyMembersTruncated = group.Count > FamilyMemberLimit;
        var orderedMembers = group
            .OrderBy(result => result.Path, StringComparer.Ordinal)
            .ThenBy(result => result.StartLine)
            .ThenBy(result => result.StartColumn ?? int.MaxValue)
            .ThenBy(result => result.SymbolId ?? long.MaxValue)
            .Take(FamilyMemberLimit)
            .ToList();
        if (!orderedMembers.Any(member => ReferenceEquals(member, representative)))
        {
            orderedMembers[^1] = representative;
            orderedMembers = orderedMembers
                .OrderBy(result => result.Path, StringComparer.Ordinal)
                .ThenBy(result => result.StartLine)
                .ThenBy(result => result.StartColumn ?? int.MaxValue)
                .ThenBy(result => result.SymbolId ?? long.MaxValue)
                .ToList();
        }
        representative.FamilyMembers = orderedMembers
            .Select(result => new PartialFamilyMember
            {
                SymbolId = result.SymbolId,
                Path = result.Path,
                Line = result.Line,
                StartLine = result.StartLine,
                StartColumn = result.StartColumn,
                EndLine = result.EndLine,
                Generated = IsGeneratedCode(result),
                Representative = ReferenceEquals(result, representative),
            })
            .ToList();
    }

    private static string BuildCallableParameterIdentity(
        string parameters,
        IReadOnlyList<string> genericParameterNames,
        IReadOnlySet<int> valueConstrainedGenericParameters,
        string? containerQualifiedName,
        CSharpCallableTypeKindLookup? typeKinds,
        long? symbolId)
    {
        if (string.IsNullOrWhiteSpace(parameters))
            return string.Empty;

        return string.Join(
            ",",
            SplitCallableParameters(parameters)
                .Select(RemoveLeadingParameterAttributes)
                .Select(RemoveCSharpComments)
                .Select(RemoveLeadingParameterAttributes)
                .Select(RemoveTrailingParameterName)
                .Select(parameter => BuildParameterTypeAndRefIdentity(
                    parameter,
                    genericParameterNames,
                    valueConstrainedGenericParameters,
                    containerQualifiedName,
                    typeKinds,
                    symbolId)));
    }

    private static string BuildParameterTypeAndRefIdentity(
        string parameter,
        IReadOnlyList<string> genericParameterNames,
        IReadOnlySet<int> valueConstrainedGenericParameters,
        string? containerQualifiedName,
        CSharpCallableTypeKindLookup? typeKinds,
        long? symbolId)
    {
        var remaining = parameter.TrimStart();
        var refKind = string.Empty;
        while (TryReadLeadingIdentifier(remaining, out var modifier, out var modifierLength))
        {
            var normalizedModifier = modifier.ToLowerInvariant();
            if (normalizedModifier is "this" or "params" or "scoped")
            {
                remaining = remaining[modifierLength..].TrimStart();
                continue;
            }
            if (normalizedModifier is "ref" or "out" or "in")
            {
                refKind = normalizedModifier;
                remaining = remaining[modifierLength..].TrimStart();
                if (normalizedModifier == "ref"
                    && TryReadLeadingIdentifier(remaining, out var readonlyModifier, out var readonlyLength)
                    && string.Equals(readonlyModifier, "readonly", StringComparison.OrdinalIgnoreCase))
                {
                    refKind = "ref_readonly";
                    remaining = remaining[readonlyLength..].TrimStart();
                }
            }
            break;
        }
        return $"{refKind}:{NormalizeCallableTypeIdentity(
            remaining,
            genericParameterNames,
            valueConstrainedGenericParameters,
            containerQualifiedName,
            typeKinds,
            symbolId)}";
    }

    private static bool TryReadLeadingIdentifier(
        string value,
        out string identifier,
        out int identifierLength)
    {
        identifier = string.Empty;
        identifierLength = 0;
        if (value.Length == 0 || !IsIdentifierStartCharacter(value[0]))
            return false;

        while (identifierLength < value.Length && IsIdentifierCharacter(value[identifierLength]))
            identifierLength++;
        identifier = value[..identifierLength];
        return true;
    }

    private static string ExtractGenericParameterName(string parameter)
    {
        var remaining = RemoveLeadingParameterAttributes(
            RemoveCSharpComments(RemoveLeadingParameterAttributes(parameter))).Trim();
        var end = remaining.Length - 1;
        while (end >= 0 && !IsIdentifierCharacter(remaining[end]))
            end--;
        if (end < 0)
            return remaining;

        var start = end;
        while (start >= 0 && IsIdentifierCharacter(remaining[start]))
            start--;
        return remaining[(start + 1)..(end + 1)];
    }

    private static string NormalizeCallableTypeIdentity(
        string? value,
        IReadOnlyList<string> genericParameterNames,
        IReadOnlySet<int>? valueConstrainedGenericParameters = null,
        string? containerQualifiedName = null,
        CSharpCallableTypeKindLookup? typeKinds = null,
        long? symbolId = null)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var tokens = TokenizeCallableType(value);
        var builder = new StringBuilder(value.Length);
        for (var offset = 0; offset < tokens.Count;)
        {
            if (TryReadTupleTypeIdentity(
                    tokens,
                    offset,
                    genericParameterNames,
                    valueConstrainedGenericParameters,
                    containerQualifiedName,
                    typeKinds,
                    symbolId,
                    out var tupleIdentity,
                    out var tupleConsumedTokens))
            {
                builder.Append(tupleIdentity);
                offset += tupleConsumedTokens;
                continue;
            }

            if (TryReadFrameworkNullableTypeIdentity(tokens, offset, out var nullableConsumedTokens))
            {
                builder.Append("global::System.Nullable");
                offset += nullableConsumedTokens;
                continue;
            }

            if (TryReadFrameworkTypeIdentity(tokens, offset, out var frameworkIdentity, out var consumedTokens))
            {
                var nullableSuffixOffset = offset + consumedTokens;
                if (nullableSuffixOffset < tokens.Count && tokens[nullableSuffixOffset] == "?")
                {
                    if (CSharpFrameworkReferenceTypeIdentities.Contains(frameworkIdentity))
                    {
                        builder.Append(frameworkIdentity);
                    }
                    else if (frameworkIdentity != "global::System.Void")
                    {
                        builder.Append("global::System.Nullable<");
                        builder.Append(frameworkIdentity);
                        builder.Append('>');
                    }
                    else
                    {
                        builder.Append(frameworkIdentity);
                        builder.Append('?');
                    }
                    offset = nullableSuffixOffset + 1;
                    continue;
                }

                builder.Append(frameworkIdentity);
                offset = nullableSuffixOffset;
                continue;
            }

            var token = tokens[offset];
            if (token == "?" && offset > 0 && tokens[offset - 1] == "]")
            {
                // Arrays are reference types; their nullable annotation does not
                // participate in CLR callable identity.
                // array は reference type なので nullable annotation を CLR callable
                // identity に含めない。
                offset++;
                continue;
            }
            if (token == "?" && offset > 0 && tokens[offset - 1] == ")")
            {
                // A nullable tuple shorthand is a known value type even without indexed
                // source facts. Normalize it to the same CLR identity as System.Nullable<T>.
                // nullable tuple shorthand は indexed source 情報がなくても value type と
                // 確定できるため、System.Nullable<T> と同じ CLR identity に正規化する。
                var sourceIdentity = ReadNullableTupleSourceIdentity(tokens, offset);
                var normalizedSourceIdentity = NormalizeCallableTypeIdentity(
                    sourceIdentity,
                    genericParameterNames,
                    valueConstrainedGenericParameters,
                    containerQualifiedName,
                    typeKinds,
                    symbolId);
                var currentIdentity = builder.ToString();
                if (normalizedSourceIdentity.Length > 0
                    && currentIdentity.EndsWith(normalizedSourceIdentity, StringComparison.Ordinal))
                {
                    builder.Length -= normalizedSourceIdentity.Length;
                    builder.Append("global::System.Nullable<");
                    builder.Append(normalizedSourceIdentity);
                    builder.Append('>');
                    offset++;
                    continue;
                }
            }
            if (token == "?")
            {
                var sourceIdentity = ReadCustomNullableSourceIdentity(tokens, offset);
                var resolvedKind = typeKinds?.Resolve(
                    sourceIdentity,
                    containerQualifiedName,
                    symbolId);
                if (resolvedKind == CSharpCallableTypeKindLookup.TypeKind.Reference)
                {
                    // Nullable reference annotations do not participate in a C# callable
                    // signature. Remove one only when indexed source facts resolve the
                    // annotated custom type as a reference type.
                    // nullable reference annotation は C# callable signature の一部ではない。
                    // indexed source 情報で reference type と確定した場合だけ除去する。
                    offset++;
                    continue;
                }
                if (resolvedKind == CSharpCallableTypeKindLookup.TypeKind.Value)
                {
                    var normalizedSourceIdentity = NormalizeCallableTypeIdentity(
                        sourceIdentity,
                        genericParameterNames,
                        valueConstrainedGenericParameters,
                        containerQualifiedName,
                        typeKinds,
                        symbolId);
                    var currentIdentity = builder.ToString();
                    if (normalizedSourceIdentity.Length > 0
                        && currentIdentity.EndsWith(normalizedSourceIdentity, StringComparison.Ordinal))
                    {
                        builder.Length -= normalizedSourceIdentity.Length;
                        builder.Append("global::System.Nullable<");
                        builder.Append(normalizedSourceIdentity);
                        builder.Append('>');
                        offset++;
                        continue;
                    }
                }
            }
            if (!IsIdentifierCharacter(token[0]))
            {
                builder.Append(token);
                offset++;
                continue;
            }

            var genericParameterIndex = -1;
            for (var index = 0; index < genericParameterNames.Count; index++)
            {
                if (string.Equals(
                        token.TrimStart('@'),
                        genericParameterNames[index].TrimStart('@'),
                        StringComparison.Ordinal))
                {
                    genericParameterIndex = index;
                    break;
                }
            }

            // A method type parameter is substituted only when it appears as an
            // unqualified type reference. In N.T / Alias::T, the leaf T names a
            // real qualified type even when the method also declares <T>.
            // method type parameter は unqualified な type reference の場合だけ置換する。
            // N.T / Alias::T の末尾 T は、method が <T> を宣言していても実型である。
            var isQualifiedTypeSegment =
                (offset > 0 && tokens[offset - 1] is "." or ":")
                || (offset + 1 < tokens.Count && tokens[offset + 1] is "." or ":");
            if (genericParameterIndex >= 0 && !isQualifiedTypeSegment)
            {
                var genericIdentity = $"`{genericParameterIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                var hasNullableSuffix = offset + 1 < tokens.Count && tokens[offset + 1] == "?";
                if (hasNullableSuffix
                    && valueConstrainedGenericParameters?.Contains(genericParameterIndex) == true)
                {
                    builder.Append("global::System.Nullable<");
                    builder.Append(genericIdentity);
                    builder.Append('>');
                    offset += 2;
                    continue;
                }

                builder.Append(genericIdentity);
                offset++;
                if (offset < tokens.Count
                    && tokens[offset] == "?"
                    && valueConstrainedGenericParameters?.Contains(genericParameterIndex) != true)
                {
                    // For unconstrained and reference-constrained method parameters, T?
                    // is a nullable annotation and not part of the CLR signature. Under a
                    // struct/unmanaged constraint it represents Nullable<T> and must remain.
                    // unconstrained / reference constraint の method type parameter では T?
                    // は CLR signature 外の annotation。struct / unmanaged constraint では
                    // Nullable<T> を表すため保持する。
                    offset++;
                }
                continue;
            }
            else
            {
                // `@global::` can name an ordinary using alias, while only unescaped
                // `global::` is the reserved root qualifier. Keep those identities distinct.
                // `@global::` は通常の using alias を指し得る。予約済み root qualifier は
                // escape なしの `global::` だけなので、両 identity を区別する。
                var isVerbatimGlobalAliasQualifier =
                    token == "@global"
                    && offset + 2 < tokens.Count
                    && tokens[offset + 1] == ":"
                    && tokens[offset + 2] == ":";
                builder.Append(isVerbatimGlobalAliasQualifier ? token : token.TrimStart('@'));
            }
            offset++;
        }
        return builder.ToString();
    }

    private static bool TryReadTupleTypeIdentity(
        IReadOnlyList<string> tokens,
        int offset,
        IReadOnlyList<string> genericParameterNames,
        IReadOnlySet<int>? valueConstrainedGenericParameters,
        string? containerQualifiedName,
        CSharpCallableTypeKindLookup? typeKinds,
        long? symbolId,
        out string identity,
        out int consumedTokens)
    {
        identity = string.Empty;
        consumedTokens = 0;
        if (offset >= tokens.Count || tokens[offset] != "(")
            return false;

        var elementRanges = new List<(int Start, int End)>();
        var elementStart = offset + 1;
        var parenthesisDepth = 0;
        var angleDepth = 0;
        var bracketDepth = 0;
        var closeOffset = -1;
        for (var current = elementStart; current < tokens.Count; current++)
        {
            switch (tokens[current])
            {
                case "(":
                    parenthesisDepth++;
                    break;
                case ")" when parenthesisDepth > 0:
                    parenthesisDepth--;
                    break;
                case ")" when angleDepth == 0 && bracketDepth == 0:
                    closeOffset = current;
                    current = tokens.Count;
                    break;
                case "<":
                    angleDepth++;
                    break;
                case ">" when angleDepth > 0:
                    angleDepth--;
                    break;
                case "[":
                    bracketDepth++;
                    break;
                case "]" when bracketDepth > 0:
                    bracketDepth--;
                    break;
                case "," when parenthesisDepth == 0 && angleDepth == 0 && bracketDepth == 0:
                    elementRanges.Add((elementStart, current));
                    elementStart = current + 1;
                    break;
            }
        }

        if (closeOffset < 0 || elementRanges.Count == 0 || elementStart >= closeOffset)
            return false;

        elementRanges.Add((elementStart, closeOffset));
        var elementIdentities = new List<string>(elementRanges.Count);
        var elementNames = new List<string?>(elementRanges.Count);
        foreach (var range in elementRanges)
        {
            if (range.Start >= range.End)
                return false;

            var nameOffset = GetTupleElementNameOffset(tokens, range.Start, range.End);
            var typeEnd = nameOffset >= 0 ? nameOffset : range.End;
            if (typeEnd <= range.Start)
                return false;

            var elementSource = string.Concat(tokens.Skip(range.Start).Take(typeEnd - range.Start));
            elementIdentities.Add(NormalizeCallableTypeIdentity(
                elementSource,
                genericParameterNames,
                valueConstrainedGenericParameters,
                containerQualifiedName,
                typeKinds,
                symbolId));
            elementNames.Add(nameOffset >= 0 ? tokens[nameOffset].TrimStart('@') : null);
        }

        var tupleIdentity = BuildValueTupleIdentity(elementIdentities);
        if (elementNames.Any(name => name != null))
        {
            // Tuple element names participate in the partial declaration contract but are
            // absent from the CLR ValueTuple type. Append a source-impossible marker so
            // matching named tuples remain stable without collapsing into an explicitly
            // unnamed ValueTuple spelling. Normalize only each element's type above; names
            // that happen to equal method type parameters must remain literal names.
            // tuple element name は partial 宣言 contract に含まれる一方、CLR ValueTuple
            // type には含まれない。source に現れない marker で保持し、method type parameter
            // と同名の element name を型 parameter として置換しない。
            tupleIdentity += $"#tuple_names({string.Join(',', elementNames.Select(name => name ?? string.Empty))})";
        }
        consumedTokens = closeOffset - offset + 1;
        if (closeOffset + 1 < tokens.Count && tokens[closeOffset + 1] == "?")
        {
            identity = $"global::System.Nullable<{tupleIdentity}>";
            consumedTokens++;
        }
        else
        {
            identity = tupleIdentity;
        }

        return true;
    }

    private static int GetTupleElementNameOffset(
        IReadOnlyList<string> tokens,
        int start,
        int end)
    {
        var last = end - 1;
        if (last <= start || !IsIdentifierCharacter(tokens[last][0]))
            return -1;

        // A final identifier preceded by member/alias qualification is the leaf type name.
        // Every other final identifier following a complete tokenized type is a tuple element
        // name (for example `int value`, `T[] values`, or `(int, int) pair`).
        // member / alias qualifier 直後の末尾 identifier は型名。それ以外で完全な型の
        // 後ろに続く末尾 identifier は tuple element name とみなす。
        return tokens[last - 1] is not "." and not ":" ? last : -1;
    }

    private static string BuildValueTupleIdentity(IReadOnlyList<string> elementIdentities)
    {
        const int directElementLimit = 7;
        if (elementIdentities.Count <= directElementLimit)
            return $"global::System.ValueTuple<{string.Join(',', elementIdentities)}>";

        var directElements = elementIdentities.Take(directElementLimit).ToList();
        directElements.Add(BuildValueTupleIdentity(elementIdentities.Skip(directElementLimit).ToList()));
        return $"global::System.ValueTuple<{string.Join(',', directElements)}>";
    }

    private static IReadOnlySet<int> ReadValueConstrainedGenericParameters(
        string signature,
        int closeParenthesis,
        IReadOnlyList<string> genericParameterNames)
    {
        var constrained = new HashSet<int>();
        if (genericParameterNames.Count == 0 || closeParenthesis + 1 >= signature.Length)
            return constrained;

        var declarationHeaderEnd = FindCallableDeclarationHeaderEnd(signature, closeParenthesis + 1);
        var tokens = TokenizeCallableType(signature[(closeParenthesis + 1)..declarationHeaderEnd]);
        for (var offset = 0; offset + 1 < tokens.Count; offset++)
        {
            if (!string.Equals(tokens[offset], "where", StringComparison.Ordinal))
                continue;

            var parameterIndex = FindGenericParameterIndex(tokens[offset + 1], genericParameterNames);
            if (parameterIndex < 0)
                continue;

            for (var constraintOffset = offset + 2; constraintOffset < tokens.Count; constraintOffset++)
            {
                var token = tokens[constraintOffset];
                if (constraintOffset > offset + 2
                    && (token == "where" || token is "{" or ";"))
                {
                    offset = constraintOffset - 1;
                    break;
                }

                if (token is "struct" or "unmanaged")
                    constrained.Add(parameterIndex);
            }
        }

        return constrained;
    }

    private static int FindCallableDeclarationHeaderEnd(string signature, int searchStart)
    {
        var sanitizedSignature = SymbolExtractor.SanitizeCSharpDeclarationSignature(signature);
        for (var offset = Math.Clamp(searchStart, 0, sanitizedSignature.Length);
             offset < sanitizedSignature.Length;
             offset++)
        {
            if (sanitizedSignature[offset] is '{' or ';'
                || (sanitizedSignature[offset] == '='
                    && offset + 1 < sanitizedSignature.Length
                    && sanitizedSignature[offset + 1] == '>'))
            {
                return offset;
            }
        }

        return signature.Length;
    }

    private static int FindGenericParameterIndex(
        string token,
        IReadOnlyList<string> genericParameterNames)
    {
        for (var index = 0; index < genericParameterNames.Count; index++)
        {
            if (string.Equals(
                    token.TrimStart('@'),
                    genericParameterNames[index].TrimStart('@'),
                    StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static string ReadCustomNullableSourceIdentity(
        IReadOnlyList<string> tokens,
        int nullableOffset)
    {
        var identityEnd = nullableOffset - 1;
        if (identityEnd < 0)
            return string.Empty;

        var nameStart = FindCustomTypeSegmentStart(tokens, identityEnd);
        if (nameStart < 0)
            return string.Empty;

        while (nameStart >= 2 && tokens[nameStart - 1] == ".")
        {
            var previousStart = FindCustomTypeSegmentStart(tokens, nameStart - 2);
            if (previousStart < 0)
                break;
            nameStart = previousStart;
        }

        if (nameStart >= 3
            && tokens[nameStart - 1] == ":"
            && tokens[nameStart - 2] == ":"
            && IsIdentifierCharacter(tokens[nameStart - 3][0]))
        {
            // Retain every alias qualifier, including escaped @global. Only literal
            // global:: has reserved root semantics; other aliases must remain visible
            // so the source-fact lookup can conservatively leave them unresolved.
            // escaped @global を含むすべての alias qualifier を保持する。予約済み root
            // semantics を持つのは literal global:: だけであり、ほかの alias は source
            // fact lookup が保守的に unresolved と判断できるよう残す。
            nameStart -= 3;
        }

        return string.Concat(tokens.Skip(nameStart).Take(identityEnd - nameStart + 1));
    }

    private static string ReadNullableTupleSourceIdentity(
        IReadOnlyList<string> tokens,
        int nullableOffset)
    {
        var depth = 0;
        for (var offset = nullableOffset - 1; offset >= 0; offset--)
        {
            if (tokens[offset] == ")")
            {
                depth++;
                continue;
            }
            if (tokens[offset] != "(")
                continue;

            depth--;
            if (depth == 0)
                return string.Concat(tokens.Skip(offset).Take(nullableOffset - offset));
        }

        return string.Empty;
    }

    private static int FindCustomTypeSegmentStart(
        IReadOnlyList<string> tokens,
        int segmentEnd)
    {
        if (segmentEnd < 0)
            return -1;
        if (tokens[segmentEnd] != ">")
            return IsIdentifierCharacter(tokens[segmentEnd][0]) ? segmentEnd : -1;

        var depth = 0;
        for (var offset = segmentEnd; offset >= 0; offset--)
        {
            if (tokens[offset] == ">")
            {
                depth++;
            }
            else if (tokens[offset] == "<" && --depth == 0)
            {
                var nameOffset = offset - 1;
                return nameOffset >= 0 && IsIdentifierCharacter(tokens[nameOffset][0])
                    ? nameOffset
                    : -1;
            }
        }

        return -1;
    }

    private static bool TryReadFrameworkNullableTypeIdentity(
        IReadOnlyList<string> tokens,
        int offset,
        out int consumedTokens)
    {
        consumedTokens = 0;
        if (offset + 5 >= tokens.Count
            || tokens[offset] != "global"
            || tokens[offset + 1] != ":"
            || tokens[offset + 2] != ":"
            || tokens[offset + 3].TrimStart('@') != "System"
            || tokens[offset + 4] != "."
            || tokens[offset + 5].TrimStart('@') != "Nullable")
        {
            return false;
        }

        consumedTokens = 6;
        return true;
    }

    private static List<string> TokenizeCallableType(string value)
    {
        var tokens = new List<string>();
        for (var offset = 0; offset < value.Length;)
        {
            if (value[offset] == '/' && offset + 1 < value.Length)
            {
                if (value[offset + 1] == '/')
                    break;
                if (value[offset + 1] == '*')
                {
                    var commentEnd = value.IndexOf("*/", offset + 2, StringComparison.Ordinal);
                    offset = commentEnd < 0 ? value.Length : commentEnd + 2;
                    continue;
                }
            }
            if (char.IsWhiteSpace(value[offset]))
            {
                offset++;
                continue;
            }
            if (!IsIdentifierStartCharacter(value[offset]))
            {
                tokens.Add(value[offset].ToString());
                offset++;
                continue;
            }

            var end = offset + 1;
            while (end < value.Length && IsIdentifierCharacter(value[end]))
                end++;
            tokens.Add(value[offset..end]);
            offset = end;
        }
        return tokens;
    }

    private static bool TryReadFrameworkTypeIdentity(
        IReadOnlyList<string> tokens,
        int offset,
        out string identity,
        out int consumedTokens)
    {
        identity = string.Empty;
        consumedTokens = 0;
        var token = tokens[offset];
        if (CSharpPredefinedTypeIdentities.TryGetValue(token, out var predefinedIdentity))
        {
            identity = $"global::{predefinedIdentity}";
            consumedTokens = 1;
            return true;
        }

        // Only an explicit global alias proves that System refers to the framework
        // namespace. An unrooted System.Int32 can bind to an enclosing namespace or
        // using alias and must retain its source identity. Verbatim escapes on the
        // namespace/type segments do not change the explicitly rooted identity.
        // explicit global alias だけが System を framework namespace と確定できる。
        // unrooted System.Int32 は外側 namespace / using alias に bind し得るため
        // source identity を保持し、rooted segment の verbatim escape だけを外す。
        if (offset + 5 >= tokens.Count
            || token != "global"
            || tokens[offset + 1] != ":"
            || tokens[offset + 2] != ":")
        {
            return false;
        }

        var systemOffset = offset + 3;
        if (systemOffset + 2 >= tokens.Count
            || tokens[systemOffset].TrimStart('@') != "System"
            || tokens[systemOffset + 1] != "."
            || !CSharpFrameworkTypeIdentities.TryGetValue(
                tokens[systemOffset + 2].TrimStart('@'),
                out var frameworkIdentity))
        {
            return false;
        }

        identity = $"global::{frameworkIdentity}";
        consumedTokens = systemOffset - offset + 3;
        return true;
    }

    private static List<string> SplitTopLevel(string value)
    {
        var items = new List<string>();
        var start = 0;
        var parenthesisDepth = 0;
        var angleDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var quote = '\0';
        var rawQuoteLength = 0;
        var escaped = false;
        var verbatim = false;
        var lineComment = false;
        var blockComment = false;
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (lineComment)
            {
                if (ch is '\r' or '\n')
                    lineComment = false;
                continue;
            }
            if (blockComment)
            {
                if (ch == '*' && i + 1 < value.Length && value[i + 1] == '/')
                {
                    blockComment = false;
                    i++;
                }
                continue;
            }
            if (rawQuoteLength > 0)
            {
                if (ch == '"' && CountRepeatedCharacter(value, i, '"') >= rawQuoteLength)
                {
                    i += rawQuoteLength - 1;
                    rawQuoteLength = 0;
                }
                continue;
            }
            if (quote != '\0')
            {
                if (verbatim && ch == '"' && i + 1 < value.Length && value[i + 1] == '"')
                {
                    i++;
                    continue;
                }
                if (escaped)
                {
                    escaped = false;
                    continue;
                }
                if (!verbatim && ch == '\\')
                {
                    escaped = true;
                    continue;
                }
                if (ch == quote)
                {
                    quote = '\0';
                    verbatim = false;
                }
                continue;
            }

            if (ch == '"')
            {
                var quoteLength = CountRepeatedCharacter(value, i, '"');
                if (quoteLength >= 3)
                {
                    rawQuoteLength = quoteLength;
                    i += quoteLength - 1;
                }
                else
                {
                    quote = ch;
                    verbatim = IsVerbatimStringStart(value, i);
                }
                continue;
            }
            if (ch == '\'')
            {
                quote = ch;
                continue;
            }
            if (ch == '/' && i + 1 < value.Length)
            {
                if (value[i + 1] == '/')
                {
                    lineComment = true;
                    i++;
                    continue;
                }
                if (value[i + 1] == '*')
                {
                    blockComment = true;
                    i++;
                    continue;
                }
            }

            switch (ch)
            {
                case '(':
                    parenthesisDepth++;
                    break;
                case ')':
                    parenthesisDepth--;
                    break;
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    angleDepth--;
                    break;
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    bracketDepth--;
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    braceDepth--;
                    break;
                case ',' when parenthesisDepth == 0 && angleDepth == 0 && bracketDepth == 0 && braceDepth == 0:
                    items.Add(value[start..i]);
                    start = i + 1;
                    break;
            }
        }
        items.Add(value[start..]);
        return items;
    }

    private static List<string> SplitCallableParameters(string value)
    {
        var items = new List<string>();
        var start = 0;
        var defaultValueStart = -1;
        var parenthesisDepth = 0;
        var angleDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var quote = '\0';
        var rawQuoteLength = 0;
        var escaped = false;
        var verbatim = false;
        var lineComment = false;
        var blockComment = false;
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (lineComment)
            {
                if (ch is '\r' or '\n')
                    lineComment = false;
                continue;
            }
            if (blockComment)
            {
                if (ch == '*' && i + 1 < value.Length && value[i + 1] == '/')
                {
                    blockComment = false;
                    i++;
                }
                continue;
            }
            if (rawQuoteLength > 0)
            {
                if (ch == '"' && CountRepeatedCharacter(value, i, '"') >= rawQuoteLength)
                {
                    i += rawQuoteLength - 1;
                    rawQuoteLength = 0;
                }
                continue;
            }
            if (quote != '\0')
            {
                if (verbatim && ch == '"' && i + 1 < value.Length && value[i + 1] == '"')
                {
                    i++;
                    continue;
                }
                if (escaped)
                {
                    escaped = false;
                    continue;
                }
                if (!verbatim && ch == '\\')
                {
                    escaped = true;
                    continue;
                }
                if (ch == quote)
                {
                    quote = '\0';
                    verbatim = false;
                }
                continue;
            }
            if (ch == '"')
            {
                var quoteLength = CountRepeatedCharacter(value, i, '"');
                if (quoteLength >= 3)
                {
                    rawQuoteLength = quoteLength;
                    i += quoteLength - 1;
                }
                else
                {
                    quote = ch;
                    verbatim = IsVerbatimStringStart(value, i);
                }
                continue;
            }
            if (ch == '\'')
            {
                quote = ch;
                continue;
            }
            if (ch == '/' && i + 1 < value.Length)
            {
                if (value[i + 1] == '/')
                {
                    lineComment = true;
                    i++;
                    continue;
                }
                if (value[i + 1] == '*')
                {
                    blockComment = true;
                    i++;
                    continue;
                }
            }

            switch (ch)
            {
                case '(':
                    parenthesisDepth++;
                    break;
                case ')':
                    parenthesisDepth--;
                    break;
                case '<' when bracketDepth == 0
                                   && (defaultValueStart < 0
                                       || angleDepth > 0
                                       || IsDefaultExpressionGenericMemberAccess(value, i)):
                    angleDepth++;
                    break;
                case '>' when bracketDepth == 0 && angleDepth > 0:
                    angleDepth--;
                    break;
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    bracketDepth--;
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    braceDepth--;
                    break;
                case '=' when defaultValueStart < 0
                                   && parenthesisDepth == 0
                                   && angleDepth == 0
                                   && bracketDepth == 0
                                   && braceDepth == 0:
                    defaultValueStart = i;
                    break;
                case ',' when parenthesisDepth == 0
                                   && bracketDepth == 0
                                   && braceDepth == 0
                                   && angleDepth == 0:
                    items.Add(value[start..(defaultValueStart >= 0 ? defaultValueStart : i)]);
                    start = i + 1;
                    defaultValueStart = -1;
                    angleDepth = 0;
                    break;
            }
        }
        items.Add(value[start..(defaultValueStart >= 0 ? defaultValueStart : value.Length)]);
        return items;
    }

    private static bool IsDefaultExpressionGenericMemberAccess(string value, int openAngleOffset)
    {
        // Optional defaults can reference a generic const as `G<int, string>.Value`.
        // Track only angle lists that close into member access: treating every `<` after
        // `=` as generic would break valid relational constants such as `1 < 2`.
        // optional default の `G<int, string>.Value` は generic comma を含む。一方、`=`
        // 以降の全 `<` を generic とみなすと `1 < 2` を壊すため、member access へ閉じる
        // angle list だけを追跡する。
        var depth = 0;
        var quote = '\0';
        var rawQuoteLength = 0;
        var escaped = false;
        var verbatim = false;
        var lineComment = false;
        var blockComment = false;
        for (var cursor = openAngleOffset; cursor < value.Length; cursor++)
        {
            var ch = value[cursor];
            if (lineComment)
            {
                if (ch is '\r' or '\n')
                    lineComment = false;
                continue;
            }
            if (blockComment)
            {
                if (ch == '*' && cursor + 1 < value.Length && value[cursor + 1] == '/')
                {
                    blockComment = false;
                    cursor++;
                }
                continue;
            }
            if (rawQuoteLength > 0)
            {
                if (ch == '"' && CountRepeatedCharacter(value, cursor, '"') >= rawQuoteLength)
                {
                    cursor += rawQuoteLength - 1;
                    rawQuoteLength = 0;
                }
                continue;
            }
            if (quote != '\0')
            {
                if (verbatim && ch == '"' && cursor + 1 < value.Length && value[cursor + 1] == '"')
                {
                    cursor++;
                    continue;
                }
                if (escaped)
                {
                    escaped = false;
                    continue;
                }
                if (!verbatim && ch == '\\')
                {
                    escaped = true;
                    continue;
                }
                if (ch == quote)
                {
                    quote = '\0';
                    verbatim = false;
                }
                continue;
            }
            if (ch == '"')
            {
                var quoteLength = CountRepeatedCharacter(value, cursor, '"');
                if (quoteLength >= 3)
                {
                    rawQuoteLength = quoteLength;
                    cursor += quoteLength - 1;
                }
                else
                {
                    quote = ch;
                    verbatim = IsVerbatimStringStart(value, cursor);
                }
                continue;
            }
            if (ch == '\'')
            {
                quote = ch;
                continue;
            }
            if (ch == '/' && cursor + 1 < value.Length)
            {
                if (value[cursor + 1] == '/')
                {
                    lineComment = true;
                    cursor++;
                    continue;
                }
                if (value[cursor + 1] == '*')
                {
                    blockComment = true;
                    cursor++;
                    continue;
                }
            }

            if (ch == '<')
            {
                depth++;
                continue;
            }
            if (ch != '>' || --depth != 0)
                continue;

            cursor = SkipCSharpDeclarationTrivia(value, cursor + 1);
            return cursor < value.Length && value[cursor] == '.';
        }

        return false;
    }

    private static string RemoveLeadingParameterAttributes(string parameter)
    {
        var remaining = parameter.TrimStart();
        while (remaining.StartsWith("[", StringComparison.Ordinal))
        {
            var attributeEnd = FindBalancedEnd(remaining, 0, '[', ']');
            if (attributeEnd < 0)
                break;
            remaining = remaining[(attributeEnd + 1)..].TrimStart();
        }
        return remaining;
    }

    private static string RemoveCSharpComments(string value)
    {
        var builder = new StringBuilder(value.Length);
        var quote = '\0';
        var rawQuoteLength = 0;
        var escaped = false;
        var verbatim = false;
        var lineComment = false;
        var blockComment = false;
        for (var offset = 0; offset < value.Length;)
        {
            var ch = value[offset];
            if (lineComment)
            {
                if (ch is '\r' or '\n')
                {
                    lineComment = false;
                    builder.Append(ch);
                }
                offset++;
                continue;
            }
            if (blockComment)
            {
                if (ch == '*' && offset + 1 < value.Length && value[offset + 1] == '/')
                {
                    blockComment = false;
                    offset += 2;
                    continue;
                }
                if (ch is '\r' or '\n')
                    builder.Append(ch);
                offset++;
                continue;
            }
            if (rawQuoteLength > 0)
            {
                var repeatedQuotes = ch == '"' ? CountRepeatedCharacter(value, offset, '"') : 0;
                if (repeatedQuotes >= rawQuoteLength)
                {
                    builder.Append(value, offset, rawQuoteLength);
                    offset += rawQuoteLength;
                    rawQuoteLength = 0;
                    continue;
                }

                builder.Append(ch);
                offset++;
                continue;
            }
            if (quote != '\0')
            {
                builder.Append(ch);
                if (verbatim && ch == '"' && offset + 1 < value.Length && value[offset + 1] == '"')
                {
                    builder.Append('"');
                    offset += 2;
                    continue;
                }
                if (escaped)
                    escaped = false;
                else if (!verbatim && ch == '\\')
                    escaped = true;
                else if (ch == quote)
                {
                    quote = '\0';
                    verbatim = false;
                }
                offset++;
                continue;
            }

            if (ch == '/' && offset + 1 < value.Length)
            {
                if (value[offset + 1] == '/')
                {
                    if (builder.Length > 0 && !char.IsWhiteSpace(builder[^1]))
                        builder.Append(' ');
                    lineComment = true;
                    offset += 2;
                    continue;
                }
                if (value[offset + 1] == '*')
                {
                    if (builder.Length > 0 && !char.IsWhiteSpace(builder[^1]))
                        builder.Append(' ');
                    blockComment = true;
                    offset += 2;
                    continue;
                }
            }
            if (ch == '"')
            {
                var repeatedQuotes = CountRepeatedCharacter(value, offset, '"');
                if (repeatedQuotes >= 3)
                {
                    builder.Append(value, offset, repeatedQuotes);
                    rawQuoteLength = repeatedQuotes;
                    offset += repeatedQuotes;
                    continue;
                }
                quote = ch;
                verbatim = IsVerbatimStringStart(value, offset);
            }
            else if (ch == '\'')
            {
                quote = ch;
            }
            builder.Append(ch);
            offset++;
        }
        return builder.ToString();
    }

    private static string RemoveTrailingParameterName(string parameter)
    {
        var end = parameter.Length - 1;
        while (end >= 0 && char.IsWhiteSpace(parameter[end]))
            end--;
        if (end < 0 || !IsIdentifierCharacter(parameter[end]))
            return parameter;

        var start = end;
        while (start >= 0 && IsIdentifierCharacter(parameter[start]))
            start--;
        var typeAndModifiers = parameter[..(start + 1)].TrimEnd();
        return typeAndModifiers.Length == 0 ? parameter : typeAndModifiers;
    }

    private static int GetPrimaryRank(SymbolResult symbol)
        => IsLogicalPartialCallableKind(symbol.Kind) && (!symbol.BodyStartLine.HasValue || !symbol.BodyEndLine.HasValue)
            ? 1
            : 0;

    private static bool IsGeneratedCode(SymbolResult symbol)
        => symbol.IsGeneratedCode
            ?? FileIndexer.HasGeneratedCodeFileName(symbol.Path);

    private static int GetSemanticScore(SymbolResult symbol)
        => GetSemanticScore(symbol.Signature, symbol.Kind);

    private static string GetCanonicalDeclarationIdentity(SymbolResult symbol)
        => IsLogicalPartialCallableKind(symbol.Kind)
            ? BuildCallableIdentity(symbol.Signature, symbol.Name, symbol.ReturnType) ?? string.Empty
            : BuildCanonicalDeclarationIdentity(symbol.Signature);

    internal static int FindCallableNameOffset(string signature, string name)
    {
        var offset = 0;
        while ((offset = signature.IndexOf(name, offset, StringComparison.Ordinal)) >= 0)
        {
            var verbatimPrefix = offset > 0
                && signature[offset - 1] == '@'
                && (offset == 1 || !IsIdentifierCharacter(signature[offset - 2]));
            var beforeIsIdentifier = offset > 0
                && IsIdentifierCharacter(signature[offset - 1])
                && !verbatimPrefix;
            var after = offset + name.Length;
            var afterIsIdentifier = after < signature.Length && IsIdentifierCharacter(signature[after]);
            if (!beforeIsIdentifier
                && !afterIsIdentifier
                && IsTopLevelCSharpDeclarationOffset(signature, offset))
            {
                var cursor = SkipCSharpDeclarationTrivia(signature, after);
                if (cursor < signature.Length && signature[cursor] == '<')
                {
                    var genericEnd = FindBalancedEnd(signature, cursor, '<', '>');
                    if (genericEnd >= 0)
                        cursor = SkipCSharpDeclarationTrivia(signature, genericEnd + 1);
                }
                if (cursor < signature.Length && signature[cursor] == '(')
                    return offset;
            }
            offset += name.Length;
        }
        return -1;
    }

    private static int SkipCSharpDeclarationTrivia(string text, int start)
    {
        var cursor = Math.Clamp(start, 0, text.Length);
        while (cursor < text.Length)
        {
            while (cursor < text.Length && char.IsWhiteSpace(text[cursor]))
                cursor++;

            if (cursor + 1 >= text.Length || text[cursor] != '/')
                break;

            if (text[cursor + 1] == '/')
            {
                cursor += 2;
                while (cursor < text.Length && text[cursor] is not ('\r' or '\n'))
                    cursor++;
                continue;
            }

            if (text[cursor + 1] != '*')
                break;

            var commentEnd = text.IndexOf("*/", cursor + 2, StringComparison.Ordinal);
            if (commentEnd < 0)
                return text.Length;
            cursor = commentEnd + 2;
        }

        return cursor;
    }

    private static bool IsTopLevelCSharpDeclarationOffset(string text, int targetOffset)
    {
        var parenthesisDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var quote = '\0';
        var rawQuoteLength = 0;
        var escaped = false;
        var verbatim = false;
        var lineComment = false;
        var blockComment = false;
        for (var i = 0; i <= targetOffset && i < text.Length; i++)
        {
            var ch = text[i];
            if (i == targetOffset)
            {
                return !lineComment
                    && !blockComment
                    && rawQuoteLength == 0
                    && quote == '\0'
                    && parenthesisDepth == 0
                    && bracketDepth == 0
                    && braceDepth == 0;
            }
            if (lineComment)
            {
                if (ch is '\r' or '\n')
                    lineComment = false;
                continue;
            }
            if (blockComment)
            {
                if (ch == '*' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    blockComment = false;
                    i++;
                }
                continue;
            }
            if (rawQuoteLength > 0)
            {
                if (ch == '"' && CountRepeatedCharacter(text, i, '"') >= rawQuoteLength)
                {
                    i += rawQuoteLength - 1;
                    rawQuoteLength = 0;
                }
                continue;
            }
            if (quote != '\0')
            {
                if (verbatim && ch == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    i++;
                    continue;
                }
                if (escaped)
                {
                    escaped = false;
                    continue;
                }
                if (!verbatim && ch == '\\')
                {
                    escaped = true;
                    continue;
                }
                if (ch == quote)
                {
                    quote = '\0';
                    verbatim = false;
                }
                continue;
            }
            if (ch == '/' && i + 1 < text.Length)
            {
                if (text[i + 1] == '/')
                {
                    lineComment = true;
                    i++;
                    continue;
                }
                if (text[i + 1] == '*')
                {
                    blockComment = true;
                    i++;
                    continue;
                }
            }
            if (ch == '"')
            {
                var quoteLength = CountRepeatedCharacter(text, i, '"');
                if (quoteLength >= 3)
                {
                    rawQuoteLength = quoteLength;
                    i += quoteLength - 1;
                }
                else
                {
                    quote = ch;
                    verbatim = IsVerbatimStringStart(text, i);
                }
                continue;
            }
            if (ch == '\'')
            {
                quote = ch;
                continue;
            }
            switch (ch)
            {
                case '(':
                    parenthesisDepth++;
                    break;
                case ')' when parenthesisDepth > 0:
                    parenthesisDepth--;
                    break;
                case '[':
                    bracketDepth++;
                    break;
                case ']' when bracketDepth > 0:
                    bracketDepth--;
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}' when braceDepth > 0:
                    braceDepth--;
                    break;
            }
        }
        return false;
    }

    private static int FindBalancedEnd(string text, int start, char open, char close)
    {
        var depth = 0;
        var quote = '\0';
        var rawQuoteLength = 0;
        var escaped = false;
        var verbatim = false;
        var lineComment = false;
        var blockComment = false;
        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];
            if (lineComment)
            {
                if (ch is '\r' or '\n')
                    lineComment = false;
                continue;
            }
            if (blockComment)
            {
                if (ch == '*' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    blockComment = false;
                    i++;
                }
                continue;
            }
            if (rawQuoteLength > 0)
            {
                if (ch == '"')
                {
                    var quoteLength = CountRepeatedCharacter(text, i, '"');
                    if (quoteLength >= rawQuoteLength)
                    {
                        i += rawQuoteLength - 1;
                        rawQuoteLength = 0;
                    }
                }
                continue;
            }
            if (quote != '\0')
            {
                if (verbatim && ch == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    i++;
                    continue;
                }
                if (escaped)
                {
                    escaped = false;
                    continue;
                }
                if (!verbatim && ch == '\\')
                {
                    escaped = true;
                    continue;
                }
                if (ch == quote)
                {
                    quote = '\0';
                    verbatim = false;
                }
                continue;
            }
            if (ch == '/' && i + 1 < text.Length)
            {
                if (text[i + 1] == '/')
                {
                    lineComment = true;
                    i++;
                    continue;
                }
                if (text[i + 1] == '*')
                {
                    blockComment = true;
                    i++;
                    continue;
                }
            }
            if (ch == '"')
            {
                var quoteLength = CountRepeatedCharacter(text, i, '"');
                if (quoteLength >= 3)
                {
                    rawQuoteLength = quoteLength;
                    i += quoteLength - 1;
                }
                else
                {
                    quote = ch;
                    verbatim = IsVerbatimStringStart(text, i);
                }
                continue;
            }
            if (ch == '\'')
            {
                quote = ch;
                continue;
            }
            if (ch == open)
                depth++;
            else if (ch == close && --depth == 0)
                return i;
        }
        return -1;
    }

    private static int CountRepeatedCharacter(string text, int start, char value)
    {
        var length = 0;
        while (start + length < text.Length && text[start + length] == value)
            length++;
        return length;
    }

    private static bool IsVerbatimStringStart(string text, int quoteOffset)
    {
        var cursor = quoteOffset - 1;
        while (cursor >= 0 && text[cursor] == '$')
            cursor--;
        return cursor >= 0 && text[cursor] == '@';
    }

    private static string NormalizeIdentityToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (!char.IsWhiteSpace(ch))
                builder.Append(char.ToLowerInvariant(ch));
        }
        return builder.ToString();
    }

    private static bool IsIdentifierCharacter(char value)
    {
        if (IsIdentifierStartCharacter(value))
            return true;

        return char.GetUnicodeCategory(value) is
            UnicodeCategory.DecimalDigitNumber or
            UnicodeCategory.ConnectorPunctuation or
            UnicodeCategory.NonSpacingMark or
            UnicodeCategory.SpacingCombiningMark or
            UnicodeCategory.Format;
    }

    private static bool IsIdentifierStartCharacter(char value)
        => value is '_' or '@'
            || char.IsLetter(value)
            || char.GetUnicodeCategory(value) == UnicodeCategory.LetterNumber;

    internal static bool ContainsPartialModifier(
        string? signature,
        string? kind,
        string? name)
        => SymbolExtractor.ContainsCSharpPartialDeclarationModifier(signature, kind, name);

    private static bool IsLogicalPartialTypeKind(string kind)
        => kind is "class" or "struct" or "interface" or "record";

    private static bool IsLogicalPartialCallableKind(string? kind)
        => kind is "function" or "test.method";
}
