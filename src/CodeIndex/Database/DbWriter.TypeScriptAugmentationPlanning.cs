using TypeScriptDeletedAugmentationReferences = System.Collections.Generic.List<(
    long Id,
    long FileId,
    long? SourceId,
    long? TargetId,
    string? ContainerNameFolded,
    string? SymbolNameFolded)>;

namespace CodeIndex.Database;

public partial class DbWriter
{
    private readonly record struct TypeScriptAugmentationScopePlan(
        string[]? ScopedNames,
        bool IncludeIndexedInterfaceMarkers);

    private readonly record struct TypeScriptInterfaceDeclaration(
        long FileId,
        string Path,
        string Name,
        int Line,
        int Column,
        string Signature,
        string Kind,
        string ContainerName,
        string? Visibility);

    private TypeScriptAugmentationScopePlan BuildTypeScriptAugmentationScopePlan(
        IReadOnlyCollection<string>? dirtyNames,
        CancellationToken cancellationToken)
    {
        if (dirtyNames == null)
            return new TypeScriptAugmentationScopePlan(null, IncludeIndexedInterfaceMarkers: false);

        var uniqueNames = new HashSet<string>(StringComparer.Ordinal);
        var inspectedNameCount = 0;
        foreach (var name in dirtyNames)
        {
            if (!string.IsNullOrEmpty(name))
                uniqueNames.Add(name);
            if ((++inspectedNameCount & 1_023) == 0)
                cancellationToken.ThrowIfCancellationRequested();
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (uniqueNames.Count > 1_024
            && ShouldUseFullTypeScriptAugmentationRebuild(uniqueNames.Count, cancellationToken))
        {
            return new TypeScriptAugmentationScopePlan(null, IncludeIndexedInterfaceMarkers: false);
        }

        var scopedNames = new string[uniqueNames.Count];
        uniqueNames.CopyTo(scopedNames);
        Array.Sort(scopedNames, StringComparer.Ordinal);
        return new TypeScriptAugmentationScopePlan(scopedNames, IncludeIndexedInterfaceMarkers: true);
    }

    private int DeleteAndTrackTypeScriptAugmentationReferences(
        TypeScriptAugmentationScopePlan scopePlan,
        HashSet<long> affectedFileIds,
        CancellationToken cancellationToken)
    {
        var deletedReferences = new TypeScriptDeletedAugmentationReferences();
        if (scopePlan.ScopedNames == null)
        {
            DeleteAllTypeScriptAugmentationReferences(
                affectedFileIds,
                deletedReferences,
                cancellationToken);
        }
        else
        {
            DeleteScopedTypeScriptAugmentationReferences(
                scopePlan.ScopedNames,
                affectedFileIds,
                deletedReferences,
                cancellationToken);
        }

        TrackReferenceGraphDeletedReferences(deletedReferences);
        return deletedReferences.Count;
    }

    private void DeleteAllTypeScriptAugmentationReferences(
        HashSet<long> affectedFileIds,
        TypeScriptDeletedAugmentationReferences deletedReferences,
        CancellationToken cancellationToken)
    {
        var deleteCmd = RentCommand(
            """
            DELETE FROM symbol_references
            WHERE reference_kind = 'augmentation'
            RETURNING id,
                      file_id,
                      source_symbol_id,
                      target_symbol_id,
                      container_name_folded,
                      symbol_name_folded
            """,
            static _ => { });
        try
        {
            using var reader = deleteCmd.ExecuteReader();
            ReadDeletedTypeScriptAugmentationReferences(
                reader,
                affectedFileIds,
                deletedReferences,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            ReleaseCommand(deleteCmd);
        }
    }

    private void DeleteScopedTypeScriptAugmentationReferences(
        IReadOnlyList<string> scopedNames,
        HashSet<long> affectedFileIds,
        TypeScriptDeletedAugmentationReferences deletedReferences,
        CancellationToken cancellationToken)
    {
        const int nameBatchSize = 900;
        for (var offset = 0; offset < scopedNames.Count; offset += nameBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(nameBatchSize, scopedNames.Count - offset);
            using (var deleteCmd = CreateTypeScriptAugmentationNameCommand(
                scopedNames,
                offset,
                count,
                """
                DELETE FROM symbol_references
                WHERE reference_kind = 'augmentation'
                  AND symbol_name IN ({0})
                RETURNING id,
                          file_id,
                          source_symbol_id,
                          target_symbol_id,
                          container_name_folded,
                          symbol_name_folded
                """))
            using (var reader = deleteCmd.ExecuteReader())
            {
                ReadDeletedTypeScriptAugmentationReferences(
                    reader,
                    affectedFileIds,
                    deletedReferences,
                    cancellationToken);
            }
            TypeScriptAugmentationNameBatchForTesting?.Invoke((offset / nameBatchSize) + 1);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static void ReadDeletedTypeScriptAugmentationReferences(
        Microsoft.Data.Sqlite.SqliteDataReader reader,
        HashSet<long> affectedFileIds,
        TypeScriptDeletedAugmentationReferences deletedReferences,
        CancellationToken cancellationToken)
    {
        var deletedRowCount = 0;
        while (reader.Read())
        {
            var fileId = reader.GetInt64(1);
            affectedFileIds.Add(fileId);
            deletedReferences.Add((
                reader.GetInt64(0),
                fileId,
                ReadNullableInt64(reader, 2),
                ReadNullableInt64(reader, 3),
                ReadNullableString(reader, 4),
                ReadNullableString(reader, 5)));
            if ((++deletedRowCount & 255) == 0)
                cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private List<TypeScriptInterfaceDeclaration> LoadTypeScriptInterfaceDeclarations(
        TypeScriptAugmentationScopePlan scopePlan,
        CancellationToken cancellationToken)
    {
        var declarations = new List<TypeScriptInterfaceDeclaration>();
        if (scopePlan.ScopedNames == null)
        {
            var command = RentCommand(
                BuildTypeScriptInterfaceDeclarationSql(namePredicate: null),
                static _ => { });
            try
            {
                using var reader = command.ExecuteReader();
                ReadTypeScriptInterfaceDeclarations(reader, declarations, cancellationToken);
            }
            finally
            {
                ReleaseCommand(command);
            }
        }
        else
        {
            LoadScopedTypeScriptInterfaceDeclarations(
                scopePlan.ScopedNames,
                declarations,
                cancellationToken);
        }
        return declarations;
    }

    private void LoadScopedTypeScriptInterfaceDeclarations(
        IReadOnlyList<string> scopedNames,
        List<TypeScriptInterfaceDeclaration> declarations,
        CancellationToken cancellationToken)
    {
        const int nameBatchSize = 900;
        for (var offset = 0; offset < scopedNames.Count; offset += nameBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(nameBatchSize, scopedNames.Count - offset);
            using (var command = CreateTypeScriptAugmentationNameCommand(
                scopedNames,
                offset,
                count,
                BuildTypeScriptInterfaceDeclarationSql("s.name IN ({0})")))
            using (var reader = command.ExecuteReader())
            {
                ReadTypeScriptInterfaceDeclarations(reader, declarations, cancellationToken);
            }
            TypeScriptAugmentationNameBatchForTesting?.Invoke((offset / nameBatchSize) + 1);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static string BuildTypeScriptInterfaceDeclarationSql(string? namePredicate) =>
        @"
            SELECT s.file_id,
                   f.path,
                   s.name,
                   s.line,
                   s.start_column,
                   s.signature,
                   s.kind,
                   s.container_name,
                   s.visibility
            FROM symbols s"
        + (namePredicate == null ? string.Empty : " INDEXED BY idx_symbols_name")
        + @"
            JOIN files f ON f.id = s.file_id
            WHERE f.lang = 'typescript'
              AND s.name IS NOT NULL
              AND s.name <> ''
              AND s.kind = 'interface'"
        + (namePredicate == null ? string.Empty : "\n              AND " + namePredicate)
        + "\n            ORDER BY s.name, s.file_id, s.line";

    private static void ReadTypeScriptInterfaceDeclarations(
        Microsoft.Data.Sqlite.SqliteDataReader reader,
        List<TypeScriptInterfaceDeclaration> declarations,
        CancellationToken cancellationToken)
    {
        while (reader.Read())
        {
            declarations.Add(new TypeScriptInterfaceDeclaration(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? 1 : Math.Max(1, reader.GetInt32(3)),
                reader.IsDBNull(4) ? 1 : Math.Max(1, reader.GetInt32(4) + 1),
                reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
            if ((declarations.Count & 255) == 0)
                cancellationToken.ThrowIfCancellationRequested();
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private Microsoft.Data.Sqlite.SqliteCommand CreateTypeScriptAugmentationNameCommand(
        IReadOnlyList<string> names,
        int offset,
        int count,
        string sqlTemplate)
    {
        SqliteDynamicSql.EnsureParameterBudget(count, "TypeScript augmentation name batch");
        var command = _conn.CreateCommand();
        command.Transaction = _activeTransaction;
        var parameterNames = new string[count];
        for (var index = 0; index < count; index++)
        {
            var parameterName = SqliteDynamicSql.BuildParameterName("augmentation_name", index);
            parameterNames[index] = parameterName;
            command.Parameters.Add(parameterName, Microsoft.Data.Sqlite.SqliteType.Text).Value = names[offset + index];
        }
        command.CommandText = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            sqlTemplate,
            string.Join(", ", parameterNames));
        return command;
    }

    private bool ShouldUseFullTypeScriptAugmentationRebuild(
        int dirtyNameCount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var command = RentCommand(
            @"
                SELECT COUNT(*)
                FROM symbols s INDEXED BY idx_symbols_kind
                JOIN files f ON f.id = s.file_id
                WHERE s.kind = 'interface'
                  AND f.lang = 'typescript'",
            static _ => { });
        try
        {
            var declarationCount = Convert.ToInt64(
                command.ExecuteScalar(),
                System.Globalization.CultureInfo.InvariantCulture);
            cancellationToken.ThrowIfCancellationRequested();
            return dirtyNameCount >= Math.Max(1_024L, (declarationCount + 1L) / 2L);
        }
        finally
        {
            ReleaseCommand(command);
        }
    }
}
