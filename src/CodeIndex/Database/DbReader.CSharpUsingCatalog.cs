using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbReader
{
    private sealed record CSharpNamespaceScope(
        string QualifiedName,
        int ScopeStartLine,
        int ScopeEndLine);

    private sealed record CSharpUsingStaticScope(
        string TargetQualifiedName,
        int Line,
        int ScopeStartLine,
        int ScopeEndLine);

    private sealed record CSharpUsingNamespaceScope(
        string TargetQualifiedName,
        int Line,
        int ScopeStartLine,
        int ScopeEndLine);

    private sealed record CSharpUsingAliasScope(
        string AliasName,
        string TargetQualifiedName,
        int Line,
        int ScopeStartLine,
        int ScopeEndLine,
        bool TargetsType);

    private sealed record CSharpRawUsingImport(int Line, string Signature);

    private sealed record CSharpRawPathUsingCatalog(
        List<CSharpNamespaceScope> NamespaceDeclarations,
        List<CSharpRawUsingImport> Imports);

    private sealed record CSharpPathUsingCatalog(
        List<CSharpNamespaceScope> NamespaceDeclarations,
        List<CSharpUsingNamespaceScope> NamespaceImports,
        List<CSharpUsingStaticScope> StaticImports,
        List<CSharpUsingAliasScope> Aliases);

    private sealed record CSharpGlobalUsingCatalog(
        HashSet<string> Namespaces,
        HashSet<string> StaticTargets,
        Dictionary<string, CSharpUsingAliasScope> AliasesByName);

    private CSharpPathUsingCatalog GetCSharpPathUsingCatalog(string path)
    {
        if (_csharpUsingCatalogsByPath.TryGetValue(path, out var cached))
            return cached;

        var rawCatalog = LoadRawCSharpPathUsingCatalog(path);
        var catalog = ProjectCSharpPathUsingCatalog(rawCatalog);
        _csharpUsingCatalogsByPath[path] = catalog;
        return catalog;
    }

    private CSharpRawPathUsingCatalog LoadRawCSharpPathUsingCatalog(string path)
    {
        const string sql = @"
            SELECT s.kind, s.line, s.body_start_line, s.body_end_line, s.end_line,
                   s.signature, f.lines, s.name
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE f.path = @path
              AND f.lang = 'csharp'
              AND (s.kind = 'import' OR s.kind = 'namespace')
            ORDER BY s.line";
        var cmd = RentCommand(sql, static c => c.Parameters.Add("@path", SqliteType.Text));
        SetParameter(cmd, "@path", path);

        var namespaceDeclarations = new List<CSharpNamespaceScope>();
        var imports = new List<CSharpRawUsingImport>();
        try
        {
            using var reader = cmd.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                var line = reader.GetInt32(1);
                if (reader.GetString(0) == "namespace")
                {
                    var (startLine, endLine) = ReadCSharpNamespaceRange(reader, line);
                    if (startLine <= 0 || endLine < startLine)
                        continue;

                    var qualifiedName = NormalizeDbCSharpQualifiedName(reader.GetString(7))
                        ?? string.Empty;
                    namespaceDeclarations.Add(new CSharpNamespaceScope(
                        qualifiedName,
                        startLine,
                        endLine));
                    continue;
                }

                if (!reader.IsDBNull(5))
                    imports.Add(new CSharpRawUsingImport(line, reader.GetString(5)));
            }
        }
        finally
        {
            ReleaseCommand(cmd);
        }

        return new CSharpRawPathUsingCatalog(namespaceDeclarations, imports);
    }

    private static (int StartLine, int EndLine) ReadCSharpNamespaceRange(
        SqliteDataReader reader,
        int declarationLine)
    {
        var startLine = reader.IsDBNull(2) ? declarationLine : reader.GetInt32(2);
        var endLine = reader.IsDBNull(3)
            ? (reader.IsDBNull(4) ? declarationLine : reader.GetInt32(4))
            : reader.GetInt32(3);
        var signature = GetNullableString(reader, 5);
        if (!string.IsNullOrWhiteSpace(signature)
            && signature.TrimEnd().EndsWith(';')
            && !reader.IsDBNull(6))
        {
            endLine = Math.Max(endLine, reader.GetInt32(6));
        }

        return (startLine, endLine);
    }

    private CSharpPathUsingCatalog ProjectCSharpPathUsingCatalog(
        CSharpRawPathUsingCatalog rawCatalog)
    {
        var namespaceImports = new List<CSharpUsingNamespaceScope>();
        var staticImports = new List<CSharpUsingStaticScope>();
        var aliases = new List<CSharpUsingAliasScope>();
        foreach (var import in rawCatalog.Imports)
        {
            var (scopeStartLine, scopeEndLine) = FindCSharpImportScope(
                import.Line,
                rawCatalog.NamespaceDeclarations);
            if (TryParseCSharpUsingNamespaceImport(import.Signature, out var namespaceTarget, out var namespaceIsGlobal)
                && !namespaceIsGlobal)
            {
                namespaceImports.Add(new CSharpUsingNamespaceScope(
                    namespaceTarget!, import.Line, scopeStartLine, scopeEndLine));
            }

            if (TryParseCSharpUsingStaticImport(import.Signature, out var staticTarget, out var staticIsGlobal)
                && !staticIsGlobal)
            {
                staticImports.Add(new CSharpUsingStaticScope(
                    staticTarget!, import.Line, scopeStartLine, scopeEndLine));
            }

            if (TryParseCSharpUsingAliasImport(
                    import.Signature,
                    out var aliasName,
                    out var aliasTarget,
                    out var aliasIsGlobal)
                && !aliasIsGlobal)
            {
                aliases.Add(new CSharpUsingAliasScope(
                    aliasName!,
                    aliasTarget!,
                    import.Line,
                    scopeStartLine,
                    scopeEndLine,
                    IsKnownCSharpTypeQualifiedName(aliasTarget!)));
            }
        }

        return new CSharpPathUsingCatalog(
            rawCatalog.NamespaceDeclarations,
            namespaceImports,
            staticImports,
            aliases);
    }

    private static (int StartLine, int EndLine) FindCSharpImportScope(
        int importLine,
        IReadOnlyList<CSharpNamespaceScope> namespaceDeclarations)
    {
        var scopeStartLine = 1;
        var scopeEndLine = int.MaxValue;
        var scopeWidth = int.MaxValue;
        foreach (var scope in namespaceDeclarations)
        {
            if (importLine < scope.ScopeStartLine || importLine > scope.ScopeEndLine)
                continue;

            var width = scope.ScopeEndLine - scope.ScopeStartLine;
            if (width > scopeWidth)
                continue;

            scopeStartLine = scope.ScopeStartLine;
            scopeEndLine = scope.ScopeEndLine;
            scopeWidth = width;
        }

        return (scopeStartLine, scopeEndLine);
    }

    private CSharpGlobalUsingCatalog GetGlobalCSharpUsingCatalog()
    {
        if (_csharpGlobalUsingCatalog != null)
            return _csharpGlobalUsingCatalog;

        var rawImports = LoadRawGlobalCSharpUsingImports();
        _csharpGlobalUsingCatalog = ProjectGlobalCSharpUsingCatalog(rawImports);
        return _csharpGlobalUsingCatalog;
    }

    private List<string> LoadRawGlobalCSharpUsingImports()
    {
        const string sql = @"
            SELECT s.signature
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE f.lang = 'csharp'
              AND s.kind = 'import'";
        var cmd = RentCommand(sql, static _ => { });
        var imports = new List<string>();
        try
        {
            using var reader = cmd.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                if (!reader.IsDBNull(0))
                    imports.Add(reader.GetString(0));
            }
        }
        finally
        {
            ReleaseCommand(cmd);
        }

        return imports;
    }

    private CSharpGlobalUsingCatalog ProjectGlobalCSharpUsingCatalog(
        IReadOnlyList<string> rawImports)
    {
        var namespaces = new HashSet<string>(StringComparer.Ordinal);
        var staticTargets = new HashSet<string>(StringComparer.Ordinal);
        var aliases = new Dictionary<string, CSharpUsingAliasScope>(StringComparer.Ordinal);
        foreach (var signature in rawImports)
        {
            if (TryParseCSharpUsingNamespaceImport(signature, out var namespaceTarget, out var namespaceIsGlobal)
                && namespaceIsGlobal)
            {
                namespaces.Add(namespaceTarget!);
            }

            if (TryParseCSharpUsingStaticImport(signature, out var staticTarget, out var staticIsGlobal)
                && staticIsGlobal)
            {
                staticTargets.Add(staticTarget!);
            }

            if (TryParseCSharpUsingAliasImport(signature, out var aliasName, out var aliasTarget, out var aliasIsGlobal)
                && aliasIsGlobal)
            {
                aliases[aliasName!] = new CSharpUsingAliasScope(
                    aliasName!,
                    aliasTarget!,
                    0,
                    1,
                    int.MaxValue,
                    IsKnownCSharpTypeQualifiedName(aliasTarget!));
            }
        }

        return new CSharpGlobalUsingCatalog(namespaces, staticTargets, aliases);
    }

    private HashSet<string> GetGlobalCSharpUsingNamespaces()
        => GetGlobalCSharpUsingCatalog().Namespaces;

    private HashSet<string> GetGlobalCSharpUsingStaticTargets()
        => GetGlobalCSharpUsingCatalog().StaticTargets;

    private Dictionary<string, CSharpUsingAliasScope> GetGlobalCSharpUsingAliasesByName()
        => GetGlobalCSharpUsingCatalog().AliasesByName;
}
