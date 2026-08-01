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
    private static readonly AsyncLocal<Action?> ScanObserver = new();
    private readonly object _gate = new();
    private Dictionary<string, int> _identityKinds = new(StringComparer.Ordinal);
    private Dictionary<string, int> _leafKinds = new(StringComparer.Ordinal);
    private Dictionary<FileTypeIdentity, int> _fileIdentityKinds = new();
    private Dictionary<FileTypeIdentity, int> _fileLeafKinds = new();
    private Dictionary<long, long> _callableFileIds = new();
    private long? _loadedTotalChanges;
    private long? _loadedDataVersion;

    internal static Action? ScanForTesting
    {
        get => ScanObserver.Value;
        set => ScanObserver.Value = value;
    }

    internal void RefreshIfChanged(
        SqliteConnection connection,
        IReadOnlySet<string> fileColumns,
        IReadOnlySet<string> symbolColumns)
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
            if (_loadedTotalChanges == totalChanges && _loadedDataVersion == dataVersion)
                return;

            ScanForTesting?.Invoke();
            var identityKinds = new Dictionary<string, int>(StringComparer.Ordinal);
            var leafKinds = new Dictionary<string, int>(StringComparer.Ordinal);
            var fileIdentityKinds = new Dictionary<FileTypeIdentity, int>();
            var fileLeafKinds = new Dictionary<FileTypeIdentity, int>();
            var callableFileIds = new Dictionary<long, long>();
            var facts = new List<TypeFact>();
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
                       {isFileLocalSql}
                FROM symbols AS s
                JOIN files AS f ON f.id = s.file_id
                WHERE f.lang = 'csharp'
                  AND s.name IS NOT NULL
                  AND s.kind IN ('class', 'struct', 'interface', 'record', 'enum', 'delegate')
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
                var typeKind = IsValueTypeDeclaration(signature, kind)
                    ? TypeKind.Value
                    : TypeKind.Reference;
                facts.Add(new TypeFact(
                    NormalizeIdentity(name),
                    NormalizeIdentity(container),
                    arity,
                    typeKind,
                    isPartial && ownsFamilyIdentity ? NormalizeFamilyIdentity(familyKey) : string.Empty,
                    isFileLocal,
                    fileId,
                    startLine,
                    endLine));
            }

            var factsByIdentity = facts
                .GroupBy(BuildUnqualifiedIdentity, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            var containingFacts = facts.Distinct().ToDictionary(
                fact => fact,
                fact => ResolveContainingFact(fact, factsByIdentity));
            var resolvedIdentities = new Dictionary<TypeFact, IReadOnlyList<string>>();
            foreach (var fact in facts)
            {
                var arityName = AppendArity(fact.Name, fact.Arity);
                Add(fileLeafKinds, new FileTypeIdentity(fact.FileId, arityName), fact.Kind);
                if (!fact.IsFileLocal)
                    Add(leafKinds, arityName, fact.Kind);
                foreach (var identity in ResolveIdentities(
                             fact,
                             containingFacts,
                             resolvedIdentities,
                             new HashSet<TypeFact>()))
                {
                    Add(fileIdentityKinds, new FileTypeIdentity(fact.FileId, identity), fact.Kind);
                    if (!fact.IsFileLocal)
                        Add(identityKinds, identity, fact.Kind);
                }
            }

            using (var callableCommand = connection.CreateCommand())
            {
                callableCommand.CommandText = """
                    SELECT s.id, s.file_id
                    FROM symbols AS s
                    JOIN files AS f ON f.id = s.file_id
                    WHERE f.lang = 'csharp'
                      AND s.kind IN ('function', 'test.method')
                    """;
                using var callableReader = callableCommand.ExecuteReader();
                while (callableReader.Read())
                    callableFileIds[callableReader.GetInt64(0)] = callableReader.GetInt64(1);
            }

            _identityKinds = identityKinds;
            _leafKinds = leafKinds;
            _fileIdentityKinds = fileIdentityKinds;
            _fileLeafKinds = fileLeafKinds;
            _callableFileIds = callableFileIds;
            _loadedTotalChanges = totalChanges;
            _loadedDataVersion = dataVersion;
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
            var fileId = symbolId.HasValue
                && _callableFileIds.TryGetValue(symbolId.Value, out var resolvedFileId)
                    ? resolvedFileId
                    : (long?)null;
            if (sourceIdentity.StartsWith("global::", StringComparison.Ordinal))
                return ResolveIdentity(normalizedSource, fileId);

            var container = NormalizeFamilyIdentity(containerQualifiedName);
            while (container.Length > 0)
            {
                var qualified = $"{container}.{normalizedSource}";
                var resolved = ResolveIdentity(qualified, fileId);
                if (resolved != TypeKind.Unknown)
                    return resolved;

                var separator = container.LastIndexOf('.');
                container = separator < 0 ? string.Empty : container[..separator];
            }

            var direct = ResolveIdentity(normalizedSource, fileId);
            if (direct != TypeKind.Unknown)
                return direct;

            var leafSeparator = normalizedSource.LastIndexOf('.');
            var leaf = leafSeparator < 0 ? normalizedSource : normalizedSource[(leafSeparator + 1)..];
            if (fileId.HasValue)
            {
                var fileKind = GetUnambiguousKind(
                    _fileLeafKinds,
                    new FileTypeIdentity(fileId.Value, leaf));
                if (fileKind != TypeKind.Unknown)
                    return fileKind;
            }
            return GetUnambiguousKind(_leafKinds, leaf);
        }
    }

    private TypeKind ResolveIdentity(string identity, long? fileId)
    {
        if (fileId.HasValue)
        {
            var fileKind = GetUnambiguousKind(
                _fileIdentityKinds,
                new FileTypeIdentity(fileId.Value, identity));
            if (fileKind != TypeKind.Unknown)
                return fileKind;
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
        long FileId,
        int StartLine,
        int EndLine);

    private readonly record struct FileTypeIdentity(long FileId, string Identity);
}
