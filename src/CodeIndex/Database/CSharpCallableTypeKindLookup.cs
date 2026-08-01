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
    private readonly object _gate = new();
    private Dictionary<string, int> _identityKinds = new(StringComparer.Ordinal);
    private Dictionary<string, int> _leafKinds = new(StringComparer.Ordinal);
    private long? _loadedTotalChanges;

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
            if (_loadedTotalChanges == totalChanges)
                return;

            var identityKinds = new Dictionary<string, int>(StringComparer.Ordinal);
            var leafKinds = new Dictionary<string, int>(StringComparer.Ordinal);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT s.name,
                       s.container_qualified_name,
                       s.signature,
                       s.kind
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
                var typeKind = IsValueTypeDeclaration(signature, kind)
                    ? TypeKind.Value
                    : TypeKind.Reference;
                Add(identityKinds, NormalizeIdentity(name), typeKind);
                Add(leafKinds, NormalizeIdentity(name), typeKind);
                if (!string.IsNullOrWhiteSpace(container))
                    Add(identityKinds, NormalizeIdentity($"{container}.{name}"), typeKind);
            }

            _identityKinds = identityKinds;
            _leafKinds = leafKinds;
            _loadedTotalChanges = totalChanges;
        }
    }

    internal TypeKind Resolve(string sourceIdentity, string? containerQualifiedName)
    {
        var normalizedSource = NormalizeIdentity(sourceIdentity);
        if (normalizedSource.Length == 0)
            return TypeKind.Unknown;

        lock (_gate)
        {
            if (sourceIdentity.StartsWith("global::", StringComparison.Ordinal))
                return GetUnambiguousKind(_identityKinds, normalizedSource);

            var container = NormalizeIdentity(containerQualifiedName);
            while (container.Length > 0)
            {
                var qualified = $"{container}.{normalizedSource}";
                var resolved = GetUnambiguousKind(_identityKinds, qualified);
                if (resolved != TypeKind.Unknown)
                    return resolved;

                var separator = container.LastIndexOf('.');
                container = separator < 0 ? string.Empty : container[..separator];
            }

            var direct = GetUnambiguousKind(_identityKinds, normalizedSource);
            if (direct != TypeKind.Unknown)
                return direct;

            var leafSeparator = normalizedSource.LastIndexOf('.');
            var leaf = leafSeparator < 0 ? normalizedSource : normalizedSource[(leafSeparator + 1)..];
            return GetUnambiguousKind(_leafKinds, leaf);
        }
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

    private static string NormalizeIdentity(string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity))
            return string.Empty;

        var value = identity.Trim();
        if (value.StartsWith("global::", StringComparison.Ordinal))
            value = value["global::".Length..];
        return string.Concat(value.Where(character => !char.IsWhiteSpace(character) && character != '@'));
    }
}
