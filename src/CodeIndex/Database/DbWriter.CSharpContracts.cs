using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbWriter
{
    // The workspace prepass needs both interface declarations and their static contract members.
    // workspace prepassではinterface宣言とstatic contract memberの両方が必要。
    private const string CSharpStaticInterfaceContractWhereSql = @"
            WHERE f.lang = 'csharp'
              AND (
                    s.kind = 'interface'
                    OR (
                        s.container_kind = 'interface'
                        AND s.kind IN ('function', 'operator', 'property')
                        AND s.signature LIKE '%static%'
                        AND (s.signature LIKE '%abstract%' OR s.signature LIKE '%virtual%')
                    )
              )";

    public List<SymbolRecord> LoadCSharpStaticInterfaceContractSymbols(IReadOnlySet<string>? excludedPaths = null)
    {
        var symbols = new List<SymbolRecord>();
        const string sql = @"
            SELECT
                f.path,
                s.file_id, s.kind, s.name, s.line,
                COALESCE(s.start_line, s.line) AS start_line,
                s.start_column,
                COALESCE(s.end_line, COALESCE(s.start_line, s.line)) AS end_line,
                s.body_start_line, s.body_end_line,
                s.signature,
                s.container_kind, s.container_name, s.container_qualified_name,
                s.family_key, s.visibility, s.return_type,
                s.is_metadata_target
            FROM symbols s
            JOIN files f ON f.id = s.file_id" + CSharpStaticInterfaceContractWhereSql;

        var cmd = RentCommand(sql, static _ => { });
        try
        {
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var path = reader.GetString(0);
                if (excludedPaths?.Contains(path) == true)
                    continue;

                symbols.Add(new SymbolRecord
                {
                    FileId = reader.GetInt64(1),
                    Kind = reader.GetString(2),
                    Name = reader.GetString(3),
                    Line = reader.GetInt32(4),
                    StartLine = reader.GetInt32(5),
                    StartColumn = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    EndLine = reader.GetInt32(7),
                    BodyStartLine = reader.IsDBNull(8) ? null : reader.GetInt32(8),
                    BodyEndLine = reader.IsDBNull(9) ? null : reader.GetInt32(9),
                    Signature = reader.IsDBNull(10) ? null : reader.GetString(10),
                    ContainerKind = reader.IsDBNull(11) ? null : reader.GetString(11),
                    ContainerName = reader.IsDBNull(12) ? null : reader.GetString(12),
                    ContainerQualifiedName = reader.IsDBNull(13) ? null : reader.GetString(13),
                    FamilyKey = reader.IsDBNull(14) ? null : reader.GetString(14),
                    Visibility = reader.IsDBNull(15) ? null : reader.GetString(15),
                    ReturnType = reader.IsDBNull(16) ? null : reader.GetString(16),
                    IsMetadataTarget = reader.IsDBNull(17) ? null : reader.GetInt32(17) != 0,
                });
            }
        }
        finally
        {
            ReleaseCommand(cmd);
        }

        return symbols;
    }

    public bool HasCSharpStaticInterfaceContractSymbols(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CSharpContractPreflightForTesting?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();

        // The MCP purge preflight needs only a presence bit, so let SQLite stop at the first row.
        // MCP purge preflightは存在判定だけでよいため、SQLite側で最初の1行で打ち切る。
        const string sql = @"
            SELECT EXISTS(
                SELECT 1
                FROM symbols s
                JOIN files f ON f.id = s.file_id" + CSharpStaticInterfaceContractWhereSql + @"
                LIMIT 1)";

        var cmd = RentCommand(sql, static _ => { });
        try
        {
            using var cancellationRegistration = RegisterSqliteInterrupt(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var raw = cmd.ExecuteScalar();
            cancellationToken.ThrowIfCancellationRequested();
            return raw is long l ? l != 0 : raw is int i && i != 0;
        }
        catch (SqliteException ex) when (IsSqliteInterruptCancellation(ex, cancellationToken))
        {
            throw new OperationCanceledException("C# static-interface contract preflight was interrupted.", ex, cancellationToken);
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    public bool HasCSharpStaticInterfaceContractSymbolsInPaths(IReadOnlySet<string> paths)
    {
        if (paths.Count == 0)
            return false;

        const string sql = @"
            SELECT f.path
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE f.lang = 'csharp'
              AND s.container_kind = 'interface'
              AND s.kind IN ('function', 'operator', 'property')
              AND s.signature LIKE '%static%'
              AND (s.signature LIKE '%abstract%' OR s.signature LIKE '%virtual%')";

        var cmd = RentCommand(sql, static _ => { });
        try
        {
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (paths.Contains(reader.GetString(0)))
                    return true;
            }
        }
        finally
        {
            ReleaseCommand(cmd);
        }

        return false;
    }
}
