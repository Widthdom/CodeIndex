using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

/// <summary>
/// Connection-scoped C# source type facts used to interpret nullable annotations in
/// partial-callable identities. The lookup is intentionally conservative: ambiguous or
/// unresolved names retain their source-level <c>?</c> marker.
/// partial callable identity の nullable annotation 解釈に使う connection-scoped な
/// C# source type 情報。曖昧または未解決の名前では source 上の <c>?</c> を保持する。
/// </summary>
internal sealed class CSharpCallableTypeKindLookup
{
    internal enum TypeKind
    {
        Unknown,
        Reference,
        Value,
    }

    private const int ReferenceKindFlag = 1;
    private const int ValueKindFlag = 2;
    private const int CandidateTypeNameLimit = 512;
    private const int CandidateCallableLimit = 4_096;
    private static readonly AsyncLocal<Action?> ScanObserver = new();
    private static readonly AsyncLocal<Action<CandidateScanStats>?> CandidateScanObserver = new();
    private readonly object _gate = new();
    private Dictionary<string, int> _identityKinds = new(StringComparer.Ordinal);
    private Dictionary<ScopedTypeIdentity, int> _scopedIdentityKinds = new();
    private Dictionary<FileTypeIdentity, int> _fileIdentityKinds = new();
    private Dictionary<long, long> _callableFileIds = new();
    private Dictionary<CallableTypeParameterIdentity, TypeKind> _callableTypeParameterKinds = new();
    private long? _loadedTotalChanges;
    private long? _loadedDataVersion;
    private string? _loadedScopeKey;

    internal static Action? ScanForTesting
    {
        get => ScanObserver.Value;
        set => ScanObserver.Value = value;
    }

    internal static Action<CandidateScanStats>? CandidateScanForTesting
    {
        get => CandidateScanObserver.Value;
        set => CandidateScanObserver.Value = value;
    }

    internal void RefreshIfChanged(
        SqliteConnection connection,
        IReadOnlySet<string> fileColumns,
        IReadOnlySet<string> symbolColumns,
        IReadOnlyList<string>? candidateQueries = null,
        bool exact = false,
        bool useFoldedNames = false)
    {
        if (!fileColumns.Contains("lang")
            || !symbolColumns.Contains("name")
            || !symbolColumns.Contains("kind")
            || !symbolColumns.Contains("signature")
            || !symbolColumns.Contains("container_qualified_name"))
        {
            return;
        }

        lock (_gate)
        {
            var totalChanges = ReadTotalChanges(connection);
            var dataVersion = ReadDataVersion(connection);
            var scopeKey = BuildScopeKey(candidateQueries, exact, useFoldedNames);
            if (_loadedTotalChanges == totalChanges
                && _loadedDataVersion == dataVersion
                && string.Equals(_loadedScopeKey, scopeKey, StringComparison.Ordinal))
            {
                return;
            }

            ScanForTesting?.Invoke();
            var identityKinds = new Dictionary<string, int>(StringComparer.Ordinal);
            var scopedIdentityKinds = new Dictionary<ScopedTypeIdentity, int>();
            var fileIdentityKinds = new Dictionary<FileTypeIdentity, int>();
            var callableFileIds = new Dictionary<long, long>();
            var callables = new List<CallableFact>();
            var candidateTypeNames = LoadCandidateCallables(
                connection,
                symbolColumns,
                candidateQueries,
                exact,
                useFoldedNames,
                callableFileIds,
                callables);
            var useFullScan = candidateTypeNames == null
                              || candidateTypeNames.Count > CandidateTypeNameLimit;
            if (useFullScan)
            {
                callableFileIds.Clear();
                callables.Clear();
                LoadAllCallableFacts(connection, symbolColumns, callableFileIds, callables);
            }
            var facts = LoadTypeFacts(
                connection,
                symbolColumns,
                useFullScan ? null : candidateTypeNames,
                LoadCSharpProjectMarkerCounts(connection));
            CandidateScanForTesting?.Invoke(new CandidateScanStats(
                useFullScan,
                callableFileIds.Count,
                facts.Count));

            var factsByIdentity = facts
                .GroupBy(BuildUnqualifiedIdentity, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            var containingFacts = facts.Distinct().ToDictionary(
                fact => fact,
                fact => ResolveContainingFact(fact, factsByIdentity));
            var resolvedIdentities = new Dictionary<TypeFact, IReadOnlyList<string>>();
            foreach (var fact in facts)
            {
                foreach (var identity in ResolveIdentities(
                             fact,
                             containingFacts,
                             resolvedIdentities,
                             new HashSet<TypeFact>()))
                {
                    Add(fileIdentityKinds, new FileTypeIdentity(fact.FileId, identity), fact.Kind);
                    if (!fact.IsFileLocal)
                    {
                        Add(identityKinds, identity, fact.Kind);
                        Add(
                            scopedIdentityKinds,
                            new ScopedTypeIdentity(fact.ProjectScope, identity),
                            fact.Kind);
                    }
                }
            }

            _identityKinds = identityKinds;
            _scopedIdentityKinds = scopedIdentityKinds;
            _fileIdentityKinds = fileIdentityKinds;
            _callableFileIds = callableFileIds;
            _callableTypeParameterKinds = BuildCallableTypeParameterKinds(callables, facts);
            _loadedTotalChanges = totalChanges;
            _loadedDataVersion = dataVersion;
            _loadedScopeKey = scopeKey;
        }
    }

    internal TypeKind Resolve(
        string sourceIdentity,
        string? containerQualifiedName,
        long? symbolId = null)
    {
        var normalizedSource = NormalizeIdentity(sourceIdentity);
        if (normalizedSource.Length == 0)
            return TypeKind.Unknown;

        lock (_gate)
        {
            var projectScope = ExtractProjectScope(containerQualifiedName);
            var fileId = symbolId.HasValue
                && _callableFileIds.TryGetValue(symbolId.Value, out var resolvedFileId)
                    ? resolvedFileId
                    : (long?)null;
            if (symbolId.HasValue
                && IsSimpleIdentifier(sourceIdentity)
                && _callableTypeParameterKinds.TryGetValue(
                    new CallableTypeParameterIdentity(symbolId.Value, normalizedSource.TrimStart('@')),
                    out var typeParameterKind))
            {
                return typeParameterKind;
            }
            if (sourceIdentity.Contains("::", StringComparison.Ordinal)
                && !sourceIdentity.StartsWith("global::", StringComparison.Ordinal))
            {
                // Using/extern aliases cannot be bound from the persisted type facts.
                // In particular, escaped @global is an ordinary alias, not the root
                // qualifier. Preserve nullable syntax instead of rebinding its leaf name.
                // using/extern alias は永続 type fact だけでは bind できない。特に escaped
                // @global は root qualifier ではなく通常 alias なので、leaf 名へ再 binding
                // せず nullable syntax を保持する。
                return TypeKind.Unknown;
            }
            if (sourceIdentity.StartsWith("global::", StringComparison.Ordinal))
                return ResolveIdentity(normalizedSource, fileId, projectScope);

            var container = NormalizeFamilyIdentity(containerQualifiedName);
            while (container.Length > 0)
            {
                var qualified = $"{container}.{normalizedSource}";
                var resolved = ResolveIdentity(qualified, fileId, projectScope);
                if (resolved != TypeKind.Unknown)
                    return resolved;

                var separator = container.LastIndexOf('.');
                container = separator < 0 ? string.Empty : container[..separator];
            }

            var direct = ResolveIdentity(normalizedSource, fileId, projectScope);
            if (direct != TypeKind.Unknown)
                return direct;
            // A same-leaf declaration elsewhere in the index does not prove C# binding.
            // Keep unqualified/import-dependent names unresolved unless a qualified container
            // identity above established the target.
            // index 内の同名 leaf だけでは C# binding の根拠にならない。上記の qualified
            // container identity で対象を確定できない名前は unresolved のまま保持する。
            return TypeKind.Unknown;
        }
    }

    private static string BuildScopeKey(
        IReadOnlyList<string>? candidateQueries,
        bool exact,
        bool useFoldedNames)
        => candidateQueries is not { Count: > 0 }
            ? "*"
            : $"{(useFoldedNames ? 'f' : 'n')}{(exact ? 'e' : 'l')}:{string.Join('\u001f', candidateQueries)}";

    private static HashSet<string>? LoadCandidateCallables(
        SqliteConnection connection,
        IReadOnlySet<string> symbolColumns,
        IReadOnlyList<string>? candidateQueries,
        bool exact,
        bool useFoldedNames,
        IDictionary<long, long> callableFileIds,
        ICollection<CallableFact> callables)
    {
        if (candidateQueries is not { Count: > 0 })
            return null;

        using var command = connection.CreateCommand();
        var clauses = new List<string>(candidateQueries.Count);
        for (var index = 0; index < candidateQueries.Count; index++)
        {
            var parameterName = $"@candidate{index}";
            var query = candidateQueries[index];
            var leaf = SqlNameResolver.GetLeafName(query).TrimStart('@');
            if (leaf.Length == 0)
                continue;

            var useFoldedColumn = useFoldedNames && symbolColumns.Contains("name_folded");
            var nameSql = useFoldedColumn ? "s.name_folded" : "s.name";
            var candidate = useFoldedColumn ? NameFold.Fold(leaf) ?? leaf : leaf;
            var collation = useFoldedColumn ? "BINARY" : "NOCASE";

            if (exact)
            {
                var explicitParameterName = $"@candidateExplicit{index}";
                clauses.Add($"({nameSql} = {parameterName} COLLATE {collation} OR {nameSql} LIKE {explicitParameterName} ESCAPE '\\')");
                SqliteCommandPolicy.Add(command, parameterName, candidate);
                SqliteCommandPolicy.Add(command, explicitParameterName, $"%.{EscapeLike(candidate)}");
            }
            else
            {
                clauses.Add($"{nameSql} LIKE {parameterName} ESCAPE '\\'");
                SqliteCommandPolicy.Add(command, parameterName, $"%{EscapeLike(candidate)}%");
            }
        }

        if (clauses.Count == 0)
            return [];

        var returnTypeSql = symbolColumns.Contains("return_type") ? "s.return_type" : "NULL";
        var startLineSql = symbolColumns.Contains("start_line")
            ? "COALESCE(s.start_line, s.line)"
            : "s.line";
        var endLineSql = symbolColumns.Contains("end_line")
            ? "COALESCE(s.end_line, s.line)"
            : "s.line";
        command.CommandText = $"""
            SELECT s.id,
                   s.file_id,
                   s.signature,
                   s.container_qualified_name,
                   {returnTypeSql},
                   {startLineSql},
                   {endLineSql}
            FROM symbols AS s
            JOIN files AS f ON f.id = s.file_id
            WHERE f.lang = 'csharp'
              AND s.kind IN ('function', 'test.method')
              AND ({string.Join(" OR ", clauses)})
            LIMIT {CandidateCallableLimit + 1}
            """;
        var typeNames = new HashSet<string>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        var count = 0;
        while (reader.Read())
        {
            if (++count > CandidateCallableLimit)
                return null;
            var symbolId = reader.GetInt64(0);
            var fileId = reader.GetInt64(1);
            callableFileIds[symbolId] = fileId;
            callables.Add(new CallableFact(
                symbolId,
                fileId,
                reader.GetInt32(5),
                reader.GetInt32(6),
                NormalizeIdentity(reader.IsDBNull(3) ? null : reader.GetString(3))));
            AddIdentifiers(typeNames, reader.IsDBNull(2) ? null : reader.GetString(2));
            AddIdentifiers(typeNames, reader.IsDBNull(3) ? null : reader.GetString(3));
            AddIdentifiers(typeNames, reader.IsDBNull(4) ? null : reader.GetString(4));
            if (typeNames.Count > CandidateTypeNameLimit)
                return null;
        }
        return typeNames;
    }

    private static List<TypeFact> LoadTypeFacts(
        SqliteConnection connection,
        IReadOnlySet<string> symbolColumns,
        IReadOnlySet<string>? typeNames,
        IReadOnlyDictionary<string, int> projectMarkerCounts)
    {
        var facts = new List<TypeFact>();
        if (typeNames is { Count: 0 })
            return facts;

        using var command = connection.CreateCommand();
        var startLineSql = symbolColumns.Contains("start_line")
            ? "COALESCE(s.start_line, s.line)"
            : "s.line";
        var endLineSql = symbolColumns.Contains("end_line")
            ? "COALESCE(s.end_line, s.line)"
            : "s.line";
        var familyKeySql = symbolColumns.Contains("family_key") ? "s.family_key" : "NULL";
        var isPartialSql = symbolColumns.Contains("is_partial_declaration")
            ? "s.is_partial_declaration"
            : "NULL";
        var isFileLocalSql = symbolColumns.Contains("is_file_local_declaration")
            ? "s.is_file_local_declaration"
            : "NULL";
        var typeNameFilter = string.Empty;
        if (typeNames is { Count: > 0 })
        {
            var orderedNames = typeNames.OrderBy(name => name, StringComparer.Ordinal).ToArray();
            var parameters = new string[orderedNames.Length];
            for (var index = 0; index < orderedNames.Length; index++)
            {
                parameters[index] = $"@typeName{index}";
                SqliteCommandPolicy.Add(command, parameters[index], orderedNames[index]);
            }
            typeNameFilter = $" AND s.name IN ({string.Join(", ", parameters)})";
        }
        command.CommandText = $"""
            SELECT s.name,
                   s.container_qualified_name,
                   s.signature,
                   s.kind,
                   {familyKeySql},
                   s.file_id,
                   {startLineSql},
                   {endLineSql},
                   {isPartialSql},
                   {isFileLocalSql},
                   f.path
            FROM symbols AS s
            JOIN files AS f ON f.id = s.file_id
            WHERE f.lang = 'csharp'
              AND s.name IS NOT NULL
              AND s.kind IN ('class', 'struct', 'interface', 'record', 'enum', 'delegate')
              {typeNameFilter}
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(0);
            var container = reader.IsDBNull(1) ? null : reader.GetString(1);
            var signature = reader.IsDBNull(2) ? null : reader.GetString(2);
            var kind = reader.GetString(3);
            var familyKey = reader.IsDBNull(4) ? null : reader.GetString(4);
            var fileId = reader.GetInt64(5);
            var startLine = reader.GetInt32(6);
            var endLine = reader.GetInt32(7);
            var arity = CSharpTypeReferenceArity.GetDefinitionArity(signature, name, kind) ?? 0;
            var ownsFamilyIdentity = OwnsFamilyIdentity(familyKey, name, arity);
            var isPartial = reader.IsDBNull(8)
                ? ownsFamilyIdentity
                : reader.GetBoolean(8);
            var isFileLocal = reader.IsDBNull(9)
                ? ContainsDeclarationModifier(signature, "file") || IsFileLocalFamily(familyKey)
                : reader.GetBoolean(9) || IsFileLocalFamily(familyKey);
            var filePath = reader.GetString(10);
            var projectScope = ExtractProjectScope(familyKey);
            if (projectScope.Length == 0)
                projectScope = ResolveProjectScope(filePath, projectMarkerCounts);
            facts.Add(new TypeFact(
                NormalizeIdentity(name),
                NormalizeIdentity(container),
                arity,
                IsValueTypeDeclaration(signature, kind) ? TypeKind.Value : TypeKind.Reference,
                isPartial && ownsFamilyIdentity ? NormalizeFamilyIdentity(familyKey) : string.Empty,
                isFileLocal,
                projectScope,
                fileId,
                startLine,
                endLine,
                signature));
        }
        return facts;
    }

    private static Dictionary<string, int> LoadCSharpProjectMarkerCounts(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path FROM files WHERE path LIKE '%.csproj' COLLATE NOCASE";
        var markerCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var directory = GetPathDirectory(NormalizeIndexedPath(reader.GetString(0)));
            markerCounts.TryGetValue(directory, out var count);
            markerCounts[directory] = count + 1;
        }
        return markerCounts;
    }

    private static string ResolveProjectScope(
        string filePath,
        IReadOnlyDictionary<string, int> projectMarkerCounts)
    {
        var normalizedPath = NormalizeIndexedPath(filePath);
        var directory = GetPathDirectory(normalizedPath);
        while (true)
        {
            if (projectMarkerCounts.TryGetValue(directory, out var markerCount))
            {
                if (markerCount == 1)
                    return directory;
                return DeriveAmbiguousProjectScope(normalizedPath, directory);
            }

            if (directory == ".")
                break;
            directory = GetPathDirectory(directory);
        }

        return FileIndexer.DeriveFallbackFamilyScopeKey(normalizedPath);
    }

    private static string DeriveAmbiguousProjectScope(string filePath, string anchorScope)
    {
        var relativeFromAnchor = anchorScope == "."
            ? filePath
            : filePath[(anchorScope.Length + 1)..];
        var firstSeparator = relativeFromAnchor.IndexOf('/');
        var childScope = firstSeparator < 0
            ? $"__file__/{relativeFromAnchor}"
            : relativeFromAnchor[..firstSeparator];
        return anchorScope == "." ? childScope : $"{anchorScope}/{childScope}";
    }

    private static string NormalizeIndexedPath(string path)
    {
        var normalized = path.Replace('\\', '/').Trim('/');
        return normalized.Length == 0 ? "." : normalized;
    }

    private static string GetPathDirectory(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator < 0 ? "." : path[..separator];
    }

    private static void LoadAllCallableFacts(
        SqliteConnection connection,
        IReadOnlySet<string> symbolColumns,
        IDictionary<long, long> callableFileIds,
        ICollection<CallableFact> callables)
    {
        using var command = connection.CreateCommand();
        var startLineSql = symbolColumns.Contains("start_line")
            ? "COALESCE(s.start_line, s.line)"
            : "s.line";
        var endLineSql = symbolColumns.Contains("end_line")
            ? "COALESCE(s.end_line, s.line)"
            : "s.line";
        command.CommandText = $"""
            SELECT s.id,
                   s.file_id,
                   {startLineSql},
                   {endLineSql},
                   s.container_qualified_name
            FROM symbols AS s
            JOIN files AS f ON f.id = s.file_id
            WHERE f.lang = 'csharp'
              AND s.kind IN ('function', 'test.method')
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var symbolId = reader.GetInt64(0);
            var fileId = reader.GetInt64(1);
            callableFileIds[symbolId] = fileId;
            callables.Add(new CallableFact(
                symbolId,
                fileId,
                reader.GetInt32(2),
                reader.GetInt32(3),
                NormalizeIdentity(reader.IsDBNull(4) ? null : reader.GetString(4))));
        }
    }

    private static Dictionary<CallableTypeParameterIdentity, TypeKind> BuildCallableTypeParameterKinds(
        IReadOnlyCollection<CallableFact> callables,
        IReadOnlyCollection<TypeFact> facts)
    {
        var result = new Dictionary<CallableTypeParameterIdentity, TypeKind>();
        var factsByFile = facts.GroupBy(fact => fact.FileId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        foreach (var callable in callables)
        {
            if (!factsByFile.TryGetValue(callable.FileId, out var fileFacts))
                continue;

            // Process outer declarations before inner declarations so a nested type
            // parameter correctly shadows a same-named parameter from its container.
            // outer 宣言から inner 宣言の順に処理し、nested type parameter が包含型の
            // 同名 parameter を正しく shadow するようにする。
            foreach (var fact in fileFacts
                         .Where(fact => fact.StartLine <= callable.StartLine
                                        && fact.EndLine >= callable.EndLine)
                         .Where(fact => IsContainingTypeIdentity(
                             callable.Container,
                             BuildUnqualifiedIdentity(fact)))
                         .OrderByDescending(fact => fact.EndLine - fact.StartLine)
                         .ThenBy(fact => fact.StartLine))
            {
                foreach (var parameter in ReadTypeParameterKinds(fact))
                {
                    result[new CallableTypeParameterIdentity(callable.SymbolId, parameter.Key)] = parameter.Value;
                }
            }
        }

        return result;
    }

    private static bool IsContainingTypeIdentity(string callableContainer, string typeIdentity)
        => callableContainer.Equals(typeIdentity, StringComparison.Ordinal)
           || callableContainer.StartsWith($"{typeIdentity}.", StringComparison.Ordinal);

    private static IReadOnlyDictionary<string, TypeKind> ReadTypeParameterKinds(TypeFact fact)
    {
        var result = new Dictionary<string, TypeKind>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(fact.Signature))
            return result;

        var declaration = SymbolExtractor.SanitizeCSharpDeclarationSignature(fact.Signature);
        if (!TryFindTypeParameterList(declaration, fact.Name, out var open, out var close))
            return result;

        foreach (var parameter in SplitTypeParameterList(declaration[(open + 1)..close]))
        {
            var name = ReadLastIdentifier(parameter);
            if (name.Length > 0)
                result[name] = TypeKind.Reference;
        }

        var constraintTokens = ReadIdentifierTokens(declaration[(close + 1)..]);
        for (var index = 0; index + 1 < constraintTokens.Count; index++)
        {
            if (!constraintTokens[index].Equals("where", StringComparison.Ordinal))
                continue;

            var parameterName = constraintTokens[index + 1].TrimStart('@');
            if (!result.ContainsKey(parameterName))
                continue;

            // T? on an unconstrained or reference-constrained type parameter is a
            // nullable annotation for callable identity. Only the two unescaped C#
            // value constraints change it to Nullable<T>; ordinary escaped identifiers
            // such as @struct are type constraints and remain reference-compatible.
            // unconstrained / reference constraint の type parameter に対する T? は
            // callable identity 上の nullable annotation。Nullable<T> へ変えるのは
            // escape なしの2つの C# value constraint だけで、@struct のような通常の
            // escaped identifier は type constraint として reference-compatible に保つ。
            var kind = TypeKind.Reference;
            for (var constraintIndex = index + 2;
                 constraintIndex < constraintTokens.Count
                 && !constraintTokens[constraintIndex].Equals("where", StringComparison.Ordinal);
                 constraintIndex++)
            {
                var constraint = constraintTokens[constraintIndex];
                if (constraint is "struct" or "unmanaged")
                {
                    kind = TypeKind.Value;
                    break;
                }
                if (constraint == "class")
                    kind = TypeKind.Reference;
            }
            result[parameterName] = kind;
        }

        return result;
    }

    private static bool TryFindTypeParameterList(
        string declaration,
        string expectedName,
        out int open,
        out int close)
    {
        open = -1;
        close = -1;
        var cursor = 0;
        while (TryReadNextIdentifier(declaration, ref cursor, out var keyword, out _))
        {
            var normalizedKeyword = keyword;
            if (normalizedKeyword is not ("class" or "struct" or "interface" or "record"))
                continue;

            if (!TryReadNextIdentifier(declaration, ref cursor, out var declaredName, out var nameEnd))
                return false;
            if (normalizedKeyword == "record" && declaredName is "class" or "struct")
            {
                if (!TryReadNextIdentifier(declaration, ref cursor, out declaredName, out nameEnd))
                    return false;
            }
            if (!declaredName.TrimStart('@').Equals(expectedName.TrimStart('@'), StringComparison.Ordinal))
                continue;

            open = nameEnd;
            while (open < declaration.Length && char.IsWhiteSpace(declaration[open]))
                open++;
            if (open >= declaration.Length || declaration[open] != '<')
                return false;
            close = FindBalancedAngleEnd(declaration, open);
            return close >= 0;
        }

        return false;
    }

    private static int FindBalancedAngleEnd(string value, int open)
    {
        var depth = 0;
        for (var index = open; index < value.Length; index++)
        {
            if (value[index] == '<')
                depth++;
            else if (value[index] == '>' && --depth == 0)
                return index;
        }
        return -1;
    }

    private static IReadOnlyList<string> SplitTypeParameterList(string parameters)
    {
        var result = new List<string>();
        var start = 0;
        var angleDepth = 0;
        var bracketDepth = 0;
        var parenthesisDepth = 0;
        for (var index = 0; index < parameters.Length; index++)
        {
            switch (parameters[index])
            {
                case '<': angleDepth++; break;
                case '>' when angleDepth > 0: angleDepth--; break;
                case '[': bracketDepth++; break;
                case ']' when bracketDepth > 0: bracketDepth--; break;
                case '(': parenthesisDepth++; break;
                case ')' when parenthesisDepth > 0: parenthesisDepth--; break;
                case ',' when angleDepth == 0 && bracketDepth == 0 && parenthesisDepth == 0:
                    result.Add(parameters[start..index]);
                    start = index + 1;
                    break;
            }
        }
        result.Add(parameters[start..]);
        return result;
    }

    private static string ReadLastIdentifier(string value)
    {
        var tokens = ReadIdentifierTokens(value);
        return tokens.Count == 0 ? string.Empty : tokens[^1].TrimStart('@');
    }

    private static List<string> ReadIdentifierTokens(string value)
    {
        var result = new List<string>();
        var cursor = 0;
        while (TryReadNextIdentifier(value, ref cursor, out var identifier, out _))
            result.Add(identifier);
        return result;
    }

    private static bool TryReadNextIdentifier(
        string value,
        ref int cursor,
        out string identifier,
        out int end)
    {
        identifier = string.Empty;
        end = cursor;
        while (cursor < value.Length
               && !IsIdentifierStart(value[cursor])
               && !(value[cursor] == '@'
                    && cursor + 1 < value.Length
                    && IsIdentifierStart(value[cursor + 1])))
        {
            cursor++;
        }
        if (cursor >= value.Length)
            return false;

        var start = cursor;
        if (value[cursor] == '@')
            cursor++;
        cursor++;
        while (cursor < value.Length && IsIdentifierPart(value[cursor]))
            cursor++;
        end = cursor;
        identifier = value[start..cursor];
        return true;
    }

    private static bool IsSimpleIdentifier(string value)
    {
        var remaining = value.AsSpan().Trim();
        if (!remaining.IsEmpty && remaining[0] == '@')
            remaining = remaining[1..];
        if (remaining.IsEmpty || !IsIdentifierStart(remaining[0]))
            return false;
        for (var index = 1; index < remaining.Length; index++)
        {
            if (!IsIdentifierPart(remaining[index]))
                return false;
        }
        return true;
    }

    private static void AddIdentifiers(ISet<string> names, string? text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        for (var index = 0; index < text.Length;)
        {
            if (!IsIdentifierStart(text[index]))
            {
                index++;
                continue;
            }

            var start = index++;
            while (index < text.Length && IsIdentifierPart(text[index]))
                index++;
            names.Add(text[start..index].TrimStart('@'));
        }
    }

    private static bool IsIdentifierStart(char value)
        => value == '_'
           || char.GetUnicodeCategory(value) is
               System.Globalization.UnicodeCategory.UppercaseLetter or
               System.Globalization.UnicodeCategory.LowercaseLetter or
               System.Globalization.UnicodeCategory.TitlecaseLetter or
               System.Globalization.UnicodeCategory.ModifierLetter or
               System.Globalization.UnicodeCategory.OtherLetter or
               System.Globalization.UnicodeCategory.LetterNumber;

    private static bool IsIdentifierPart(char value)
        => IsIdentifierStart(value)
           || char.GetUnicodeCategory(value) is
               System.Globalization.UnicodeCategory.NonSpacingMark or
               System.Globalization.UnicodeCategory.SpacingCombiningMark or
               System.Globalization.UnicodeCategory.DecimalDigitNumber or
               System.Globalization.UnicodeCategory.ConnectorPunctuation or
               System.Globalization.UnicodeCategory.Format;

    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private TypeKind ResolveIdentity(string identity, long? fileId, string projectScope)
    {
        if (fileId.HasValue)
        {
            var fileKind = GetUnambiguousKind(
                _fileIdentityKinds,
                new FileTypeIdentity(fileId.Value, identity));
            if (fileKind != TypeKind.Unknown)
                return fileKind;
        }

        if (projectScope.Length > 0)
        {
            var scopedKind = GetUnambiguousKind(
                _scopedIdentityKinds,
                new ScopedTypeIdentity(projectScope, identity));
            // A scoped miss or ambiguity must not borrow a declaration from another
            // project through the repository-global map.
            // scoped lookup の miss/ambiguity を別 project の宣言で補完しない。
            return scopedKind;
        }

        return GetUnambiguousKind(_identityKinds, identity);
    }

    private static bool IsValueTypeDeclaration(string? signature, string kind)
        => kind is "struct" or "enum"
            || CSharpTypeReferenceArity.IsValueTypeDeclaration(signature, kind);

    private static long ReadTotalChanges(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT total_changes()";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static long ReadDataVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA data_version";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<string> ResolveIdentities(
        TypeFact fact,
        IReadOnlyDictionary<TypeFact, TypeFact?> containingFacts,
        IDictionary<TypeFact, IReadOnlyList<string>> cache,
        ISet<TypeFact> visiting)
    {
        if (cache.TryGetValue(fact, out var cached))
            return cached;

        var arityName = AppendArity(fact.Name, fact.Arity);
        if (fact.FamilyIdentity.Length > 0)
        {
            var familyIdentity = CSharpTypeReferenceArity.NormalizeTypeIdentityArity(fact.FamilyIdentity);
            var precise = familyIdentity.Length > 0 ? new[] { familyIdentity } : Array.Empty<string>();
            cache[fact] = precise;
            return precise;
        }

        if (fact.Container.Length == 0 || !visiting.Add(fact))
        {
            var root = new[] { CombineIdentity(fact.Container, arityName) };
            cache[fact] = root;
            return root;
        }

        IReadOnlyList<string> resolved;
        if (!containingFacts.TryGetValue(fact, out var parent) || parent == null)
        {
            resolved = new[] { CombineIdentity(fact.Container, arityName) };
        }
        else
        {
            resolved = ResolveIdentities(parent, containingFacts, cache, visiting)
                .Select(parentIdentity => CombineIdentity(parentIdentity, arityName))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        visiting.Remove(fact);
        cache[fact] = resolved;
        return resolved;
    }

    private static TypeFact? ResolveContainingFact(
        TypeFact fact,
        IReadOnlyDictionary<string, TypeFact[]> factsByIdentity)
    {
        if (fact.Container.Length == 0
            || !factsByIdentity.TryGetValue(fact.Container, out var candidates))
        {
            return null;
        }

        return candidates
            .Where(candidate => candidate.FileId == fact.FileId && candidate != fact)
            .Where(candidate => candidate.StartLine <= fact.StartLine && candidate.EndLine >= fact.EndLine)
            .OrderBy(candidate => candidate.EndLine - candidate.StartLine)
            .ThenByDescending(candidate => candidate.StartLine)
            .FirstOrDefault();
    }

    private static string BuildUnqualifiedIdentity(TypeFact fact)
        => CombineIdentity(fact.Container, fact.Name);

    private static string CombineIdentity(string container, string name)
        => container.Length == 0 ? name : $"{container}.{name}";

    private static string AppendArity(string name, int arity)
        => arity > 0 ? $"{name}`{arity}" : name;

    private static string NormalizeFamilyIdentity(string? familyKey)
    {
        var normalized = NormalizeIdentity(familyKey);
        if (normalized.Length == 0)
            return string.Empty;

        var scopeSeparator = normalized.LastIndexOf('|');
        if (scopeSeparator >= 0)
            normalized = normalized[(scopeSeparator + 1)..];
        var fileLocalSeparator = normalized.IndexOf('\u001f');
        if (fileLocalSeparator >= 0)
            normalized = normalized[(fileLocalSeparator + 1)..];
        return normalized.StartsWith("file-local:", StringComparison.Ordinal)
            ? string.Empty
            : normalized;
    }

    private static string ExtractProjectScope(string? familyKey)
    {
        if (string.IsNullOrWhiteSpace(familyKey))
            return string.Empty;

        var normalized = familyKey.Trim();
        var scopeSeparator = normalized.IndexOf('|');
        return scopeSeparator > 0 ? normalized[..scopeSeparator] : string.Empty;
    }

    private static bool IsFileLocalFamily(string? familyKey)
    {
        if (string.IsNullOrWhiteSpace(familyKey))
            return false;

        var normalized = familyKey.Trim();
        var scopeSeparator = normalized.LastIndexOf('|');
        if (scopeSeparator >= 0)
            normalized = normalized[(scopeSeparator + 1)..];
        return normalized.StartsWith("file-local:", StringComparison.Ordinal);
    }

    private static bool OwnsFamilyIdentity(string? familyKey, string name, int arity)
    {
        var familyIdentity = NormalizeFamilyIdentity(familyKey);
        if (familyIdentity.Length == 0)
            return false;

        var separator = familyIdentity.LastIndexOf('.');
        var leaf = separator < 0 ? familyIdentity : familyIdentity[(separator + 1)..];
        return string.Equals(
            leaf,
            AppendArity(NormalizeIdentity(name), arity),
            StringComparison.Ordinal);
    }

    private static bool ContainsDeclarationModifier(string? signature, string modifier)
    {
        if (string.IsNullOrWhiteSpace(signature))
            return false;

        return signature.Split(
                [' ', '\t', '\r', '\n', '(', ')', '[', ']', '{', '}', ':'],
                StringSplitOptions.RemoveEmptyEntries)
            .Contains(modifier, StringComparer.Ordinal);
    }

    private static void Add(Dictionary<string, int> kinds, string identity, TypeKind kind)
    {
        if (identity.Length == 0)
            return;

        var flag = kind == TypeKind.Value ? ValueKindFlag : ReferenceKindFlag;
        kinds.TryGetValue(identity, out var existing);
        kinds[identity] = existing | flag;
    }

    private static TypeKind GetUnambiguousKind(IReadOnlyDictionary<string, int> kinds, string identity)
    {
        if (!kinds.TryGetValue(identity, out var flags))
            return TypeKind.Unknown;
        return flags switch
        {
            ReferenceKindFlag => TypeKind.Reference,
            ValueKindFlag => TypeKind.Value,
            _ => TypeKind.Unknown,
        };
    }

    private static void Add<TKey>(Dictionary<TKey, int> kinds, TKey identity, TypeKind kind)
        where TKey : notnull
    {
        var flag = kind == TypeKind.Value ? ValueKindFlag : ReferenceKindFlag;
        kinds.TryGetValue(identity, out var existing);
        kinds[identity] = existing | flag;
    }

    private static TypeKind GetUnambiguousKind<TKey>(IReadOnlyDictionary<TKey, int> kinds, TKey identity)
        where TKey : notnull
    {
        if (!kinds.TryGetValue(identity, out var flags))
            return TypeKind.Unknown;
        return flags switch
        {
            ReferenceKindFlag => TypeKind.Reference,
            ValueKindFlag => TypeKind.Value,
            _ => TypeKind.Unknown,
        };
    }

    private static string NormalizeIdentity(string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity))
            return string.Empty;

        return CSharpTypeReferenceArity.NormalizeTypeIdentityArity(identity);
    }

    private sealed record TypeFact(
        string Name,
        string Container,
        int Arity,
        TypeKind Kind,
        string FamilyIdentity,
        bool IsFileLocal,
        string ProjectScope,
        long FileId,
        int StartLine,
        int EndLine,
        string? Signature);

    private sealed record CallableFact(
        long SymbolId,
        long FileId,
        int StartLine,
        int EndLine,
        string Container);

    internal sealed record CandidateScanStats(
        bool UsedFullScan,
        int CallableCount,
        int TypeFactCount);

    private readonly record struct FileTypeIdentity(long FileId, string Identity);
    private readonly record struct ScopedTypeIdentity(string ProjectScope, string Identity);
    private readonly record struct CallableTypeParameterIdentity(long SymbolId, string ParameterName);
}
