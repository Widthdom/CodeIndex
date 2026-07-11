using CodeIndex.Models;

namespace CodeIndex.Database;

public partial class DbWriter
{
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
            JOIN files f ON f.id = s.file_id
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
