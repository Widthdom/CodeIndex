using CodeIndex.Cli;
using CodeIndex.Models;

namespace CodeIndex.Database;

public partial class DbWriter
{
    public int RebuildTypeScriptAugmentationReferences(string? projectRoot = null)
    {
        using var transaction = BeginTransaction();

        var affectedFileIds = new HashSet<long>();
        var deleteCmd = RentCommand(
            "DELETE FROM symbol_references WHERE reference_kind = 'augmentation' RETURNING file_id",
            static _ => { });
        try
        {
            using var reader = deleteCmd.ExecuteReader();
            while (reader.Read())
                affectedFileIds.Add(reader.GetInt64(0));
        }
        finally
        {
            ReleaseCommand(deleteCmd);
        }

        var references = new List<ReferenceRecord>();
        var cmd = RentCommand(
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
                FROM symbols s
                JOIN files f ON f.id = s.file_id
                WHERE f.lang = 'typescript'
                  AND s.name IS NOT NULL
                  AND s.name <> ''
                  AND s.kind = 'interface'
                ORDER BY s.name, s.file_id, s.line",
            static _ => { });
        try
        {
            using var reader = cmd.ExecuteReader();
            var declarations = new List<(long FileId, string Path, string Name, int Line, int Column, string Signature, string Kind, string ContainerName, string? Visibility)>();
            while (reader.Read())
            {
                declarations.Add((
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? 1 : Math.Max(1, reader.GetInt32(3)),
                    reader.IsDBNull(4) ? 1 : Math.Max(1, reader.GetInt32(4) + 1),
                    reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                    reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                    reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8)));
            }

            var moduleFileIds = FindTypeScriptModuleFileIds(projectRoot, declarations);
            foreach (var group in declarations.GroupBy(d => (d.Name, ScopeKey: BuildTypeScriptScopeKey(d.FileId, d.Path, d.Signature, d.ContainerName, moduleFileIds))))
            {
                if (group.Count() < 2)
                    continue;

                foreach (var declaration in group)
                {
                    references.Add(new ReferenceRecord
                    {
                        FileId = declaration.FileId,
                        SymbolName = declaration.Name,
                        ReferenceKind = "augmentation",
                        Line = declaration.Line,
                        Column = declaration.Column,
                        Context = declaration.Signature,
                        ContainerKind = declaration.Kind == "interface" ? "interface" : "type",
                        ContainerName = declaration.Name,
                    });
                }
            }
        }
        finally
        {
            ReleaseCommand(cmd);
        }

        InsertReferencesInAtomicFileScope(
            references,
            refreshMutualRecursionFlags: true,
            CancellationToken.None);
        foreach (var reference in references)
            affectedFileIds.Remove(reference.FileId);
        RefreshHotspotReferenceCounts(affectedFileIds, CancellationToken.None);
        MarkTypeScriptAugmentationReady();
        transaction.Commit();
        return references.Count;
    }

    public void MarkTypeScriptAugmentationReady()
    {
        SetMeta(
            DbContext.TypeScriptAugmentationVersionMetaKey,
            DbContext.TypeScriptAugmentationVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static HashSet<long> FindTypeScriptModuleFileIds(
        string? projectRoot,
        IReadOnlyList<(long FileId, string Path, string Name, int Line, int Column, string Signature, string Kind, string ContainerName, string? Visibility)> declarations)
    {
        var moduleFileIds = new HashSet<long>();
        foreach (var declaration in declarations)
        {
            if (declaration.Visibility == "export"
                || declaration.Signature.StartsWith("export ", StringComparison.Ordinal)
                || declaration.Signature.StartsWith("import ", StringComparison.Ordinal))
            {
                moduleFileIds.Add(declaration.FileId);
            }
        }

        if (string.IsNullOrWhiteSpace(projectRoot))
            return moduleFileIds;

        foreach (var file in declarations.GroupBy(static d => (d.FileId, d.Path)))
        {
            if (moduleFileIds.Contains(file.Key.FileId))
                continue;

            var absolutePath = Path.Combine(projectRoot, file.Key.Path.Replace('/', Path.DirectorySeparatorChar));
            if (TypeScriptFileHasModuleSyntax(absolutePath))
                moduleFileIds.Add(file.Key.FileId);
        }

        return moduleFileIds;
    }

    private static string BuildTypeScriptScopeKey(long fileId, string path, string signature, string containerName, HashSet<long> moduleFileIds)
    {
        if (!moduleFileIds.Contains(fileId))
            return "global:" + containerName;
        if (containerName == "global" || signature.StartsWith("declare global ", StringComparison.Ordinal))
            return "global:";
        if (IsAmbientModuleContainer(containerName))
            return "ambient-module:" + containerName;
        return "module:" + path + ":" + containerName;
    }

    private static bool IsAmbientModuleContainer(string containerName)
    {
        if (string.IsNullOrWhiteSpace(containerName))
            return false;
        return containerName[0] is '"' or '\'' || containerName.Contains('/', StringComparison.Ordinal);
    }

    private static bool TypeScriptFileHasModuleSyntax(string absolutePath)
    {
        try
        {
            var content = DataDirectorySecurity.ReadTextWithinLimit(
                absolutePath,
                TypeScriptModuleSyntaxFallbackMaxBytes,
                FileShare.ReadWrite | FileShare.Delete);
            if (content is null)
                return false;

            var lineCount = 0;
            foreach (var line in EnumerateLines(content))
            {
                lineCount++;
                if (lineCount > TypeScriptModuleSyntaxFallbackMaxLines)
                    return false;

                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("import ", StringComparison.Ordinal)
                    || trimmed.StartsWith("export ", StringComparison.Ordinal)
                    || trimmed.StartsWith("export{", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (IsExpectedTypeScriptFallbackReadException(ex)) { }

        return false;
    }

    internal static bool TypeScriptFileHasModuleSyntaxForTests(string absolutePath) =>
        TypeScriptFileHasModuleSyntax(absolutePath);

    private static IEnumerable<string> EnumerateLines(string text)
    {
        var start = 0;
        while (start <= text.Length)
        {
            var newline = text.IndexOf('\n', start);
            if (newline < 0)
            {
                yield return text[start..].TrimEnd('\r');
                yield break;
            }

            yield return text[start..newline].TrimEnd('\r');
            start = newline + 1;
        }
    }

    private static bool IsExpectedTypeScriptFallbackReadException(Exception ex) =>
        ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException;
}
