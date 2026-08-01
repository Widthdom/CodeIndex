using System.Security.Cryptography;
using System.Text;
using CodeIndex.Indexer;

namespace CodeIndex.Database;

internal static class LogicalPartialSymbolGrouper
{
    private const char KeySeparator = '\u001f';
    private const string CSharpFileLocalFamilyPrefix = "file-local:";
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
        var persistedFamilyBodySql = $"CASE WHEN INSTR(COALESCE({persistedFamilySql}, ''), '|') > 0 THEN SUBSTR({persistedFamilySql}, INSTR({persistedFamilySql}, '|') + 1) ELSE {persistedFamilySql} END";
        var scopedPersistedFamilySql = $"CASE WHEN SUBSTR(COALESCE({persistedFamilyBodySql}, ''), 1, {CSharpFileLocalFamilyPrefix.Length}) = '{CSharpFileLocalFamilyPrefix}' THEN {persistedFamilySql} || CHAR(31) || {fileIdentitySql} ELSE {persistedFamilySql} END";
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
        var callableIdentitySql = returnTypeSql == null
            ? "NULL"
            : $"csharp_partial_callable_identity({callableSignatureSql}, {nameSql}, {returnTypeSql})";
        var callableContainerSql = $"COALESCE({scopedPersistedFamilySql}, NULLIF({fallbackContainerSql}, ''))";
        return $@"CASE
            WHEN {languageSql} = 'csharp'
             AND {kindSql} IN ('class', 'struct', 'interface', 'record')
             AND {partialDeclarationSql}
            THEN 'family:' || {languageSql} || CHAR(31) || {kindSql} || CHAR(31) ||
                 {selfFamilySql}
            WHEN {languageSql} = 'csharp'
             AND {kindSql} = 'function'
             AND {callableContainerSql} IS NOT NULL
             AND {callableIdentitySql} IS NOT NULL
            THEN 'family:' || {languageSql} || CHAR(31) || {kindSql} || CHAR(31) ||
                 {callableContainerSql} || CHAR(31) || {callableIdentitySql}
            ELSE 'symbol:' || {symbolIdSql}
        END";
    }

    internal static string BuildSqlPrimaryRankExpression(
        string kindSql,
        string bodyStartLineSql,
        string bodyEndLineSql)
        => $"CASE WHEN {kindSql} = 'function' AND ({bodyStartLineSql} IS NULL OR {bodyEndLineSql} IS NULL) THEN 1 ELSE 0 END";

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
            || !ContainsPartialModifier(symbol.Signature))
        {
            key = string.Empty;
            return false;
        }

        var containerIdentity = symbol.ContainerQualifiedName ?? symbol.ContainerName ?? string.Empty;
        if (symbol.Kind == "function")
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
                symbol.Kind,
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

    internal static string? BuildCallableIdentity(string? signature, string? name, string? returnType)
    {
        if (string.IsNullOrWhiteSpace(signature)
            || string.IsNullOrWhiteSpace(name)
            || string.IsNullOrWhiteSpace(returnType)
            || !ContainsPartialModifier(signature))
        {
            return null;
        }

        var normalizedName = name.TrimStart('@');
        var nameOffset = FindCallableNameOffset(signature, normalizedName);
        if (nameOffset < 0)
            return null;

        var cursor = nameOffset + name.Length;
        while (cursor < signature.Length && char.IsWhiteSpace(signature[cursor]))
            cursor++;

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
            cursor = genericEnd + 1;
            while (cursor < signature.Length && char.IsWhiteSpace(signature[cursor]))
                cursor++;
        }

        if (cursor >= signature.Length || signature[cursor] != '(')
            return null;

        var closeParenthesis = FindBalancedEnd(signature, cursor, '(', ')');
        if (closeParenthesis < 0)
            return null;

        var parameterIdentity = BuildCallableParameterIdentity(
            signature[(cursor + 1)..closeParenthesis],
            genericParameterNames);
        return string.Join(
            KeySeparator,
            normalizedName,
            NormalizeCallableTypeIdentity(returnType, genericParameterNames),
            genericArity.ToString(System.Globalization.CultureInfo.InvariantCulture),
            parameterIdentity);
    }

    internal static int GetSemanticScore(string? signature, string? kind)
    {
        if (string.IsNullOrWhiteSpace(signature))
            return 0;

        var declaration = RemoveCSharpComments(signature);
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
                : RemoveCSharpComments(signature));

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
        IReadOnlyList<string> genericParameterNames)
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
                .Select(parameter => BuildParameterTypeAndRefIdentity(parameter, genericParameterNames)));
    }

    private static string BuildParameterTypeAndRefIdentity(
        string parameter,
        IReadOnlyList<string> genericParameterNames)
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
        return $"{refKind}:{NormalizeCallableTypeIdentity(remaining, genericParameterNames)}";
    }

    private static bool TryReadLeadingIdentifier(
        string value,
        out string identifier,
        out int identifierLength)
    {
        identifier = string.Empty;
        identifierLength = 0;
        if (value.Length == 0 || !IsIdentifierCharacter(value[0]))
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
        IReadOnlyList<string> genericParameterNames)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var tokens = TokenizeCallableType(value);
        var builder = new StringBuilder(value.Length);
        for (var offset = 0; offset < tokens.Count;)
        {
            if (TryReadFrameworkTypeIdentity(tokens, offset, out var frameworkIdentity, out var consumedTokens))
            {
                builder.Append(frameworkIdentity);
                offset += consumedTokens;
                continue;
            }

            var token = tokens[offset];
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

            if (genericParameterIndex >= 0)
            {
                builder.Append('`');
                builder.Append(genericParameterIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(token.TrimStart('@'));
            }
            offset++;
        }
        return builder.ToString();
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
            if (!IsIdentifierCharacter(value[offset]))
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
                case '<' when defaultValueStart < 0 && bracketDepth == 0:
                    angleDepth++;
                    break;
                case '>' when defaultValueStart < 0 && bracketDepth == 0 && angleDepth > 0:
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
                                   && (defaultValueStart >= 0 || angleDepth == 0):
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
        => symbol.Kind == "function" && (!symbol.BodyStartLine.HasValue || !symbol.BodyEndLine.HasValue)
            ? 1
            : 0;

    private static bool IsGeneratedCode(SymbolResult symbol)
        => symbol.IsGeneratedCode
            ?? FileIndexer.HasGeneratedCodeFileName(symbol.Path);

    private static int GetSemanticScore(SymbolResult symbol)
        => GetSemanticScore(symbol.Signature, symbol.Kind);

    private static string GetCanonicalDeclarationIdentity(SymbolResult symbol)
        => symbol.Kind == "function"
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
                var cursor = after;
                while (cursor < signature.Length && char.IsWhiteSpace(signature[cursor]))
                    cursor++;
                if (cursor < signature.Length && signature[cursor] == '<')
                {
                    var genericEnd = FindBalancedEnd(signature, cursor, '<', '>');
                    if (genericEnd >= 0)
                    {
                        cursor = genericEnd + 1;
                        while (cursor < signature.Length && char.IsWhiteSpace(signature[cursor]))
                            cursor++;
                    }
                }
                if (cursor < signature.Length && signature[cursor] == '(')
                    return offset;
            }
            offset += name.Length;
        }
        return -1;
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
        => value == '_' || value == '@' || char.IsLetterOrDigit(value);

    private static bool ContainsPartialModifier(string signature)
    {
        var tokens = signature.Split(
            [' ', '\t', '\r', '\n', '(', ')', '[', ']', '{', '}', ':'],
            StringSplitOptions.RemoveEmptyEntries);
        return tokens.Contains("partial", StringComparer.Ordinal);
    }

    private static bool IsLogicalPartialTypeKind(string kind)
        => kind is "class" or "struct" or "interface" or "record";
}
