using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeIndex.Database;

public partial class DbReader
{
    private bool HasScopedCSharpTypeCandidate(string path, int lineNumber, string symbolName)
    {
        if (HasActiveCSharpUsingTypeAlias(path, lineNumber, symbolName))
            return true;

        var activeAliasReference = ResolveActiveCSharpUsingAliasReference(path, lineNumber, symbolName);
        if (!string.Equals(activeAliasReference, symbolName, StringComparison.Ordinal)
            && IsKnownCSharpTypeQualifiedName(activeAliasReference))
        {
            return true;
        }

        var candidateNamespaces = GetCSharpTypeNamespacesByName(symbolName);
        var candidateContainingTypes = GetCSharpTypeContainingTypesByName(symbolName);
        if (candidateNamespaces.Count == 0 && candidateContainingTypes.Count == 0)
            return false;

        var activeNamespaces = GetActiveCSharpTypeNamespaces(path, lineNumber);
        foreach (var activeNamespace in activeNamespaces)
        {
            foreach (var candidateNamespace in candidateNamespaces)
            {
                if (!string.Equals(candidateNamespace.QualifiedName, activeNamespace, StringComparison.Ordinal))
                    continue;
                if (candidateNamespace.IsFileLocal && !string.Equals(candidateNamespace.Path, path, StringComparison.Ordinal))
                    continue;
                return true;
            }
        }

        var activeContainingTypeScopes = GetActiveCSharpContainingTypeScopes(path, lineNumber);
        foreach (var activeContainingTypeScope in activeContainingTypeScopes)
        {
            if (candidateContainingTypes.Any(candidate => string.Equals(candidate.QualifiedName, activeContainingTypeScope.QualifiedName, StringComparison.Ordinal)))
                return true;

            if (!candidateContainingTypes.Any(candidate => candidate.AccessibleFromDerivedType))
                continue;

            var inheritedContainingTypes = GetInheritedCSharpContainingTypes(activeContainingTypeScope);
            foreach (var candidate in candidateContainingTypes)
            {
                if (!candidate.AccessibleFromDerivedType)
                    continue;
                if (!inheritedContainingTypes.Contains(candidate.QualifiedName))
                    continue;
                return true;
            }
        }

        return false;
    }

    private HashSet<string> GetActiveCSharpTypeNamespaces(string path, int lineNumber)
    {
        if (_activeCSharpTypeNamespacesByPathLine.TryGetValue(path, out var activeNamespacesByLine)
            && activeNamespacesByLine.TryGetValue(lineNumber, out var cachedActiveNamespaces))
        {
            return cachedActiveNamespaces;
        }

        if (!_csharpNamespaceScopesByPath.TryGetValue(path, out var namespaceScopes))
        {
            namespaceScopes = LoadCSharpNamespaceScopes(path);
            _csharpNamespaceScopesByPath[path] = namespaceScopes;
        }

        if (!_csharpUsingNamespaceScopesByPath.TryGetValue(path, out var usingNamespaceScopes))
        {
            usingNamespaceScopes = LoadCSharpUsingNamespaceScopes(path);
            _csharpUsingNamespaceScopesByPath[path] = usingNamespaceScopes;
        }

        var activeNamespaces = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scope in namespaceScopes)
        {
            if (lineNumber >= scope.ScopeStartLine && lineNumber <= scope.ScopeEndLine)
                activeNamespaces.Add(scope.QualifiedName);
        }

        if (activeNamespaces.Count == 0)
            activeNamespaces.Add(string.Empty);

        foreach (var scope in usingNamespaceScopes)
        {
            if (scope.Line > lineNumber)
                continue;
            if (lineNumber < scope.ScopeStartLine || lineNumber > scope.ScopeEndLine)
                continue;
            activeNamespaces.Add(scope.TargetQualifiedName);
        }

        foreach (var globalNamespace in GetGlobalCSharpUsingNamespaces())
            activeNamespaces.Add(globalNamespace);

        if (activeNamespacesByLine == null)
        {
            activeNamespacesByLine = new Dictionary<int, HashSet<string>>();
            _activeCSharpTypeNamespacesByPathLine[path] = activeNamespacesByLine;
        }

        activeNamespacesByLine[lineNumber] = activeNamespaces;
        return activeNamespaces;
    }

    private List<CSharpContainingTypeScope> GetActiveCSharpContainingTypeScopes(string path, int lineNumber)
    {
        if (_activeCSharpContainingTypeScopesByPathLine.TryGetValue(path, out var activeContainingTypesByLine)
            && activeContainingTypesByLine.TryGetValue(lineNumber, out var cachedActiveContainingTypes))
        {
            return cachedActiveContainingTypes;
        }

        if (!_csharpContainingTypeScopesByPath.TryGetValue(path, out var containingTypeScopes))
        {
            containingTypeScopes = LoadCSharpContainingTypeScopes(path);
            _csharpContainingTypeScopesByPath[path] = containingTypeScopes;
        }

        var activeContainingTypes = new List<CSharpContainingTypeScope>();
        foreach (var scope in containingTypeScopes)
        {
            if (lineNumber >= scope.ScopeStartLine && lineNumber <= scope.ScopeEndLine)
                activeContainingTypes.Add(scope);
        }

        if (activeContainingTypesByLine == null)
        {
            activeContainingTypesByLine = new Dictionary<int, List<CSharpContainingTypeScope>>();
            _activeCSharpContainingTypeScopesByPathLine[path] = activeContainingTypesByLine;
        }

        activeContainingTypesByLine[lineNumber] = activeContainingTypes;
        return activeContainingTypes;
    }

    private HashSet<string> GetInheritedCSharpContainingTypes(CSharpContainingTypeScope containingTypeScope)
    {
        if (_csharpInheritedContainingTypesByQualifiedName.TryGetValue(containingTypeScope.QualifiedName, out var cached))
            return cached;

        var inheritedContainingTypes = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal)
        {
            containingTypeScope.QualifiedName,
        };
        CollectInheritedCSharpContainingTypes(containingTypeScope, inheritedContainingTypes, visited);
        _csharpInheritedContainingTypesByQualifiedName[containingTypeScope.QualifiedName] = inheritedContainingTypes;
        return inheritedContainingTypes;
    }

    private void CollectInheritedCSharpContainingTypes(CSharpContainingTypeScope containingTypeScope, HashSet<string> inheritedContainingTypes, HashSet<string> visited)
    {
        var directBaseScope = ResolveDirectCSharpBaseContainingTypeScope(containingTypeScope);
        if (directBaseScope == null || !visited.Add(directBaseScope.QualifiedName))
            return;

        inheritedContainingTypes.Add(directBaseScope.QualifiedName);
        CollectInheritedCSharpContainingTypes(directBaseScope, inheritedContainingTypes, visited);
    }

    private CSharpContainingTypeScope? GetCSharpContainingTypeScope(string qualifiedName)
    {
        if (_csharpContainingTypeScopeByQualifiedName.TryGetValue(qualifiedName, out var cached))
            return cached;

        var lastDot = qualifiedName.LastIndexOf('.');
        var shortName = lastDot >= 0 ? qualifiedName[(lastDot + 1)..] : qualifiedName;
        var sql = @"
            SELECT f.path, s.kind, s.name, s.container_name, s.container_qualified_name, s.visibility, s.signature, s.body_start_line, s.body_end_line, s.start_line, s.end_line
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE f.lang = 'csharp'
              AND s.name = @symbolName COLLATE NOCASE
              AND s.kind IN ('class', 'struct', 'interface')";
        var cmd = RentCommand(sql, static c => c.Parameters.Add("@symbolName", SqliteType.Text));
        SetParameter(cmd, "@symbolName", shortName);

        CSharpContainingTypeScope? resolved = null;
        try
        {
            using var reader = cmd.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                var scope = CreateCSharpContainingTypeScope(
                    reader.GetString(0),
                    GetNullableString(reader, 1),
                    GetNullableString(reader, 2),
                    GetNullableString(reader, 3),
                    GetNullableString(reader, 4),
                    GetNullableString(reader, 5),
                    GetNullableString(reader, 6),
                    reader.IsDBNull(7) ? null : reader.GetInt32(7),
                    reader.IsDBNull(8) ? null : reader.GetInt32(8),
                    reader.IsDBNull(9) ? null : reader.GetInt32(9),
                    reader.IsDBNull(10) ? null : reader.GetInt32(10));
                if (scope == null)
                    continue;
                if (!string.Equals(scope.QualifiedName, qualifiedName, StringComparison.Ordinal))
                    continue;
                resolved = scope;
                break;
            }
        }
        finally
        {
            ReleaseCommand(cmd);
        }

        _csharpContainingTypeScopeByQualifiedName[qualifiedName] = resolved;
        return resolved;
    }

    private bool IsKnownCSharpTypeQualifiedName(string qualifiedName)
    {
        var normalizedQualifiedName = NormalizeCSharpAliasTargetForTypeLookup(qualifiedName);
        if (string.IsNullOrWhiteSpace(normalizedQualifiedName))
            return false;

        var lastDot = normalizedQualifiedName.LastIndexOf('.');
        var shortName = lastDot >= 0
            ? normalizedQualifiedName[(lastDot + 1)..]
            : normalizedQualifiedName;
        var containerQualifiedName = lastDot >= 0
            ? normalizedQualifiedName[..lastDot]
            : string.Empty;

        var namespaceCandidates = GetCSharpTypeNamespacesByName(shortName);
        foreach (var candidate in namespaceCandidates)
        {
            if (string.Equals(candidate.QualifiedName, containerQualifiedName, StringComparison.Ordinal))
                return true;
        }

        var containingTypes = GetCSharpTypeContainingTypesByName(shortName);
        if (containingTypes.Any(candidate => string.Equals(candidate.QualifiedName, containerQualifiedName, StringComparison.Ordinal)))
            return true;

        return false;
    }

    private static string NormalizeCSharpAliasTargetForTypeLookup(string qualifiedName)
    {
        if (string.IsNullOrWhiteSpace(qualifiedName))
            return string.Empty;

        var trimmed = qualifiedName.Trim();
        var builder = new System.Text.StringBuilder(trimmed.Length);
        var genericDepth = 0;
        for (var i = 0; i < trimmed.Length; i++)
        {
            var ch = trimmed[i];
            if (ch == '<')
            {
                genericDepth++;
                continue;
            }

            if (ch == '>')
            {
                if (genericDepth > 0)
                    genericDepth--;
                continue;
            }

            if (genericDepth == 0)
                builder.Append(ch);
        }

        var normalized = builder.ToString().Trim();
        while (normalized.EndsWith("?", StringComparison.Ordinal))
            normalized = normalized[..^1].TrimEnd();
        while (normalized.EndsWith("[]", StringComparison.Ordinal))
            normalized = normalized[..^2].TrimEnd();

        return normalized;
    }

    private bool HasActiveCSharpUsingTypeAlias(string path, int lineNumber, string symbolName)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(symbolName))
            return false;

        if (TryResolveActiveCSharpUsingAliasScope(path, lineNumber, symbolName, requireTypeAlias: true, out _))
            return true;

        if (!_csharpUsingAliasScopesByPath.TryGetValue(path, out var scopes))
        {
            scopes = LoadCSharpUsingAliasScopes(path);
            _csharpUsingAliasScopesByPath[path] = scopes;
        }

        for (var i = scopes.Count - 1; i >= 0; i--)
        {
            var scope = scopes[i];
            if (!string.Equals(scope.AliasName, symbolName, StringComparison.Ordinal))
                continue;
            if (scope.Line > lineNumber)
                continue;
            if (lineNumber < scope.ScopeStartLine || lineNumber > scope.ScopeEndLine)
                continue;
            if (IsKnownCSharpTypeQualifiedName(scope.TargetQualifiedName))
                return true;

            var resolvedContainer = ResolveScopedCSharpContainingTypeQualifiedName(path, lineNumber, scope.TargetQualifiedName);
            if (string.IsNullOrWhiteSpace(resolvedContainer))
                continue;

            if (IsKnownCSharpTypeQualifiedName(resolvedContainer))
                return true;

            foreach (var activeNamespace in GetActiveCSharpTypeNamespaces(path, lineNumber))
            {
                if (string.IsNullOrWhiteSpace(activeNamespace))
                    continue;

                var namespacedTarget = CombineDbQualifiedName(activeNamespace, resolvedContainer);
                if (!string.IsNullOrWhiteSpace(namespacedTarget)
                    && IsKnownCSharpTypeQualifiedName(namespacedTarget))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private HashSet<string> GetActiveCSharpUsingStaticTargets(string path, int lineNumber)
    {
        if (_activeCSharpUsingStaticTargetsByPathLine.TryGetValue(path, out var activeTargetsByLine)
            && activeTargetsByLine.TryGetValue(lineNumber, out var cachedActiveTargets))
        {
            return cachedActiveTargets;
        }

        if (!_csharpUsingStaticScopesByPath.TryGetValue(path, out var scopes))
        {
            scopes = LoadCSharpUsingStaticScopes(path);
            _csharpUsingStaticScopesByPath[path] = scopes;
        }

        var activeTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scope in scopes)
        {
            if (scope.Line > lineNumber)
                continue;
            if (lineNumber < scope.ScopeStartLine || lineNumber > scope.ScopeEndLine)
                continue;
            activeTargets.Add(scope.TargetQualifiedName);
        }

        foreach (var globalTarget in GetGlobalCSharpUsingStaticTargets())
            activeTargets.Add(globalTarget);

        if (activeTargetsByLine == null)
        {
            activeTargetsByLine = new Dictionary<int, HashSet<string>>();
            _activeCSharpUsingStaticTargetsByPathLine[path] = activeTargetsByLine;
        }

        activeTargetsByLine[lineNumber] = activeTargets;
        return activeTargets;
    }

    private List<CSharpNamespaceScope> LoadCSharpNamespaceScopes(string path)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT s.line, s.body_start_line, s.body_end_line, s.end_line, s.name, s.signature, f.lines
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE f.path = @path
              AND f.lang = 'csharp'
              AND s.kind = 'namespace'
            ORDER BY s.line";
        cmd.Parameters.AddWithValue("@path", path);

        var scopes = new List<CSharpNamespaceScope>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            var line = reader.GetInt32(0);
            var startLine = reader.IsDBNull(1) ? line : reader.GetInt32(1);
            var endLine = reader.IsDBNull(2)
                ? (reader.IsDBNull(3) ? line : reader.GetInt32(3))
                : reader.GetInt32(2);
            var signature = GetNullableString(reader, 5);
            if (!string.IsNullOrWhiteSpace(signature)
                && signature.TrimEnd().EndsWith(';')
                && !reader.IsDBNull(6))
            {
                endLine = Math.Max(endLine, reader.GetInt32(6));
            }

            if (startLine <= 0 || endLine < startLine)
                continue;

            var qualifiedName = NormalizeDbCSharpQualifiedName(reader.GetString(4)) ?? string.Empty;
            scopes.Add(new CSharpNamespaceScope(qualifiedName, startLine, endLine));
        }

        return scopes;
    }

    private List<CSharpUsingNamespaceScope> LoadCSharpUsingNamespaceScopes(string path)
    {
        var sql = @"
            SELECT s.kind, s.line, s.body_start_line, s.body_end_line, s.end_line, s.signature, f.lines
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE f.path = @path
              AND f.lang = 'csharp'
              AND (s.kind = 'import' OR s.kind = 'namespace')
            ORDER BY s.line";
        var cmd = RentCommand(sql, static c => c.Parameters.Add("@path", SqliteType.Text));
        SetParameter(cmd, "@path", path);

        var namespaceScopes = new List<(int StartLine, int EndLine)>();
        var imports = new List<(int Line, string Signature)>();
        try
        {
            using var reader = cmd.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                var kind = reader.GetString(0);
                var line = reader.GetInt32(1);
                if (kind == "namespace")
                {
                    var startLine = reader.IsDBNull(2) ? line : reader.GetInt32(2);
                    var endLine = reader.IsDBNull(3)
                        ? (reader.IsDBNull(4) ? line : reader.GetInt32(4))
                        : reader.GetInt32(3);
                    var signature = GetNullableString(reader, 5);
                    if (!string.IsNullOrWhiteSpace(signature)
                        && signature.TrimEnd().EndsWith(';')
                        && !reader.IsDBNull(6))
                    {
                        endLine = Math.Max(endLine, reader.GetInt32(6));
                    }

                    if (startLine > 0 && endLine >= startLine)
                        namespaceScopes.Add((startLine, endLine));
                    continue;
                }

                if (!reader.IsDBNull(5))
                    imports.Add((line, reader.GetString(5)));
            }
        }
        finally
        {
            ReleaseCommand(cmd);
        }

        var scopes = new List<CSharpUsingNamespaceScope>();
        foreach (var import in imports)
        {
            if (!TryParseCSharpUsingNamespaceImport(import.Signature, out var target, out var isGlobal)
                || isGlobal)
            {
                continue;
            }

            var scopeStartLine = 1;
            var scopeEndLine = int.MaxValue;
            var scopeWidth = int.MaxValue;
            foreach (var (startLine, endLine) in namespaceScopes)
            {
                if (import.Line < startLine || import.Line > endLine)
                    continue;

                var width = endLine - startLine;
                if (width > scopeWidth)
                    continue;

                scopeStartLine = startLine;
                scopeEndLine = endLine;
                scopeWidth = width;
            }

            scopes.Add(new CSharpUsingNamespaceScope(target!, import.Line, scopeStartLine, scopeEndLine));
        }

        return scopes;
    }

    private List<CSharpContainingTypeScope> LoadCSharpContainingTypeScopes(string path)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT s.kind, s.name, s.container_name, s.container_qualified_name, s.visibility, s.signature, s.body_start_line, s.body_end_line, s.start_line, s.end_line
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE f.path = @path
              AND f.lang = 'csharp'
              AND s.kind IN ('class', 'struct', 'interface')
            ORDER BY s.start_line";
        cmd.Parameters.AddWithValue("@path", path);

        var scopes = new List<CSharpContainingTypeScope>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            var scope = CreateCSharpContainingTypeScope(
                path,
                GetNullableString(reader, 0),
                GetNullableString(reader, 1),
                GetNullableString(reader, 2),
                GetNullableString(reader, 3),
                GetNullableString(reader, 4),
                GetNullableString(reader, 5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetInt32(8),
                reader.IsDBNull(9) ? null : reader.GetInt32(9));
            if (scope == null)
                continue;

            scopes.Add(scope);
            _csharpContainingTypeScopeByQualifiedName.TryAdd(scope.QualifiedName, scope);
        }

        return scopes;
    }

    private static CSharpContainingTypeScope? CreateCSharpContainingTypeScope(
        string path,
        string? kind,
        string? name,
        string? containerName,
        string? containerQualifiedName,
        string? visibility,
        string? signature,
        int? bodyStartLine,
        int? bodyEndLine,
        int? startLine,
        int? endLine)
    {
        if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(name))
            return null;

        var qualifiedName = CombineDbQualifiedName(
            NormalizeDbCSharpQualifiedName(containerQualifiedName ?? containerName ?? string.Empty),
            NormalizeDbCSharpQualifiedName(name));
        if (string.IsNullOrWhiteSpace(qualifiedName))
            return null;

        var resolvedStartLine = bodyStartLine ?? startLine ?? 0;
        var resolvedEndLine = bodyEndLine ?? endLine ?? resolvedStartLine;
        var declarationLine = startLine ?? resolvedStartLine;
        if (resolvedStartLine <= 0 || resolvedEndLine < resolvedStartLine || declarationLine <= 0)
            return null;

        return new CSharpContainingTypeScope(path, kind, qualifiedName, visibility, signature, declarationLine, resolvedStartLine, resolvedEndLine);
    }

    private CSharpContainingTypeScope? ResolveDirectCSharpBaseContainingTypeScope(CSharpContainingTypeScope containingTypeScope)
    {
        if (!string.Equals(containingTypeScope.Kind, "class", StringComparison.Ordinal))
            return null;

        var baseTypeReference = ParseCSharpBaseTypeReference(containingTypeScope.Signature);
        if (string.IsNullOrWhiteSpace(baseTypeReference))
            return null;

        var directBaseQualifiedName = ResolveScopedCSharpContainingTypeQualifiedName(
            containingTypeScope.Path,
            containingTypeScope.DeclarationLine,
            baseTypeReference);
        if (string.IsNullOrWhiteSpace(directBaseQualifiedName))
            return null;

        var directBaseScope = GetCSharpContainingTypeScope(directBaseQualifiedName);
        if (directBaseScope == null || !string.Equals(directBaseScope.Kind, "class", StringComparison.Ordinal))
            return null;

        return directBaseScope;
    }

    private string? ResolveScopedCSharpContainingTypeQualifiedName(string path, int lineNumber, string typeReference)
    {
        var normalizedReference = NormalizeCSharpBaseTypeReference(typeReference);
        if (string.IsNullOrWhiteSpace(normalizedReference))
            return null;

        normalizedReference = NormalizeCSharpBaseTypeReference(ResolveActiveCSharpUsingAliasReference(path, lineNumber, normalizedReference));
        if (string.IsNullOrWhiteSpace(normalizedReference))
            return null;

        var shortName = GetLastQualifiedSegment(normalizedReference);
        var candidateContainingTypes = GetCSharpTypeContainingTypesByName(shortName);
        var candidateNamespaces = GetCSharpTypeNamespacesByName(shortName);
        if (candidateContainingTypes.Count == 0 && candidateNamespaces.Count == 0)
            return normalizedReference;

        var lastDot = normalizedReference.LastIndexOf('.');
        if (lastDot >= 0)
        {
            var qualifiedPrefix = NormalizeDbCSharpQualifiedName(normalizedReference[..lastDot]);
            if (!string.IsNullOrWhiteSpace(qualifiedPrefix))
            {
                var exactContainingType = candidateContainingTypes.FirstOrDefault(candidate =>
                    string.Equals(candidate.QualifiedName, qualifiedPrefix, StringComparison.Ordinal));
                if (exactContainingType != null)
                    return CombineDbQualifiedName(qualifiedPrefix, shortName);

                var exactNamespace = candidateNamespaces.FirstOrDefault(candidate =>
                    string.Equals(candidate.QualifiedName, qualifiedPrefix, StringComparison.Ordinal));
                if (exactNamespace != null)
                    return CombineDbQualifiedName(qualifiedPrefix, shortName);
            }
        }

        foreach (var activeContainingTypeScope in GetActiveCSharpContainingTypeScopes(path, lineNumber))
        {
            var exactContainingType = candidateContainingTypes.FirstOrDefault(candidate =>
                string.Equals(candidate.QualifiedName, activeContainingTypeScope.QualifiedName, StringComparison.Ordinal));
            if (exactContainingType != null)
                return CombineDbQualifiedName(activeContainingTypeScope.QualifiedName, shortName);
        }

        string? resolvedActiveNamespace = null;
        foreach (var activeNamespace in GetActiveCSharpTypeNamespaces(path, lineNumber))
        {
            var exactNamespace = candidateNamespaces.FirstOrDefault(candidate =>
                string.Equals(candidate.QualifiedName, activeNamespace, StringComparison.Ordinal));
            if (exactNamespace == null)
                continue;
            if (resolvedActiveNamespace != null
                && !string.Equals(resolvedActiveNamespace, activeNamespace, StringComparison.Ordinal))
            {
                return null;
            }
            resolvedActiveNamespace = activeNamespace;
        }

        if (resolvedActiveNamespace != null)
            return CombineDbQualifiedName(resolvedActiveNamespace, shortName);

        return normalizedReference;
    }

    private static string? ParseCSharpBaseTypeReference(string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
            return null;

        var text = signature.TrimEnd();
        if (text.EndsWith("{", StringComparison.Ordinal))
            text = text[..^1].TrimEnd();

        var colonIndex = FindCSharpBaseListColonIndex(text);
        if (colonIndex < 0)
            return null;

        var baseList = text[(colonIndex + 1)..];
        var whereIndex = baseList.IndexOf(" where ", StringComparison.Ordinal);
        if (whereIndex >= 0)
            baseList = baseList[..whereIndex];

        var firstEntry = TakeFirstCSharpBaseListEntry(baseList).Trim();
        return firstEntry.Length == 0 ? null : firstEntry;
    }

    private static int FindCSharpBaseListColonIndex(string signature)
    {
        var angleDepth = 0;
        var parenDepth = 0;
        var squareDepth = 0;
        for (var i = 0; i < signature.Length; i++)
        {
            switch (signature[i])
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    if (angleDepth > 0)
                        angleDepth--;
                    break;
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0)
                        parenDepth--;
                    break;
                case '[':
                    squareDepth++;
                    break;
                case ']':
                    if (squareDepth > 0)
                        squareDepth--;
                    break;
                case ':':
                    if (angleDepth == 0 && parenDepth == 0 && squareDepth == 0)
                        return i;
                    break;
            }
        }

        return -1;
    }

    private static string TakeFirstCSharpBaseListEntry(string baseList)
    {
        var angleDepth = 0;
        var parenDepth = 0;
        var squareDepth = 0;
        for (var i = 0; i < baseList.Length; i++)
        {
            switch (baseList[i])
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    if (angleDepth > 0)
                        angleDepth--;
                    break;
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0)
                        parenDepth--;
                    break;
                case '[':
                    squareDepth++;
                    break;
                case ']':
                    if (squareDepth > 0)
                        squareDepth--;
                    break;
                case ',':
                    if (angleDepth == 0 && parenDepth == 0 && squareDepth == 0)
                        return baseList[..i];
                    break;
            }
        }

        return baseList;
    }

    private static string NormalizeCSharpBaseTypeReference(string typeReference)
    {
        if (string.IsNullOrWhiteSpace(typeReference))
            return string.Empty;

        var builder = new System.Text.StringBuilder(typeReference.Length);
        var angleDepth = 0;
        var parenDepth = 0;
        var squareDepth = 0;
        for (var i = 0; i < typeReference.Length; i++)
        {
            var ch = typeReference[i];
            switch (ch)
            {
                case '<':
                    angleDepth++;
                    continue;
                case '>':
                    if (angleDepth > 0)
                        angleDepth--;
                    continue;
                case '(':
                    if (angleDepth == 0 && squareDepth == 0)
                        return NormalizeDbCSharpQualifiedName(builder.ToString()) ?? string.Empty;
                    parenDepth++;
                    continue;
                case ')':
                    if (parenDepth > 0)
                        parenDepth--;
                    continue;
                case '[':
                    if (angleDepth == 0 && parenDepth == 0)
                        squareDepth++;
                    continue;
                case ']':
                    if (squareDepth > 0)
                        squareDepth--;
                    continue;
            }

            if (angleDepth == 0 && parenDepth == 0 && squareDepth == 0)
                builder.Append(ch);
        }

        return NormalizeDbCSharpQualifiedName(builder.ToString()) ?? string.Empty;
    }

    private static bool IsNestedCSharpTypeAccessibleFromDerivedType(string? visibility, string? signature)
    {
        if (!string.IsNullOrWhiteSpace(visibility))
            return !string.Equals(visibility, "private", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(signature))
            return false;

        var normalizedSignature = signature.TrimStart();
        return normalizedSignature.StartsWith("public ", StringComparison.Ordinal)
            || normalizedSignature.StartsWith("protected ", StringComparison.Ordinal)
            || normalizedSignature.StartsWith("internal ", StringComparison.Ordinal)
            || normalizedSignature.StartsWith("private protected ", StringComparison.Ordinal)
            || normalizedSignature.StartsWith("protected internal ", StringComparison.Ordinal);
    }

    private static string GetLastQualifiedSegment(string qualifiedName)
    {
        if (string.IsNullOrWhiteSpace(qualifiedName))
            return string.Empty;

        var lastDot = qualifiedName.LastIndexOf('.');
        var lastColon = qualifiedName.LastIndexOf("::", StringComparison.Ordinal);
        var split = Math.Max(lastDot, lastColon);
        return split < 0 ? qualifiedName : qualifiedName[(split + (split == lastColon ? 2 : 1))..];
    }

    private List<CSharpUsingStaticScope> LoadCSharpUsingStaticScopes(string path)
    {
        var sql = @"
            SELECT s.kind, s.line, s.body_start_line, s.body_end_line, s.end_line, s.signature, f.lines
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE f.path = @path
              AND f.lang = 'csharp'
              AND (s.kind = 'import' OR s.kind = 'namespace')
            ORDER BY s.line";
        var cmd = RentCommand(sql, static c => c.Parameters.Add("@path", SqliteType.Text));
        SetParameter(cmd, "@path", path);

        var namespaceScopes = new List<(int StartLine, int EndLine)>();
        var imports = new List<(int Line, string Signature)>();
        try
        {
            using var reader = cmd.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                var kind = reader.GetString(0);
                var line = reader.GetInt32(1);
                if (kind == "namespace")
                {
                    var startLine = reader.IsDBNull(2) ? line : reader.GetInt32(2);
                    var endLine = reader.IsDBNull(3)
                        ? (reader.IsDBNull(4) ? line : reader.GetInt32(4))
                        : reader.GetInt32(3);
                    var signature = GetNullableString(reader, 5);
                    if (!string.IsNullOrWhiteSpace(signature)
                        && signature.TrimEnd().EndsWith(';')
                        && !reader.IsDBNull(6))
                    {
                        endLine = Math.Max(endLine, reader.GetInt32(6));
                    }

                    if (startLine > 0 && endLine >= startLine)
                        namespaceScopes.Add((startLine, endLine));
                    continue;
                }

                if (!reader.IsDBNull(5))
                    imports.Add((line, reader.GetString(5)));
            }
        }
        finally
        {
            ReleaseCommand(cmd);
        }

        var scopes = new List<CSharpUsingStaticScope>();
        foreach (var import in imports)
        {
            if (!TryParseCSharpUsingStaticImport(import.Signature, out var target, out var isGlobal)
                || isGlobal)
            {
                continue;
            }

            var scopeStartLine = 1;
            var scopeEndLine = int.MaxValue;
            var scopeWidth = int.MaxValue;
            foreach (var (startLine, endLine) in namespaceScopes)
            {
                if (import.Line < startLine || import.Line > endLine)
                    continue;

                var width = endLine - startLine;
                if (width > scopeWidth)
                    continue;

                scopeStartLine = startLine;
                scopeEndLine = endLine;
                scopeWidth = width;
            }

            scopes.Add(new CSharpUsingStaticScope(target!, import.Line, scopeStartLine, scopeEndLine));
        }

        return scopes;
    }

    private List<CSharpUsingAliasScope> LoadCSharpUsingAliasScopes(string path)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT s.kind, s.line, s.body_start_line, s.body_end_line, s.end_line, s.signature, f.lines
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE f.path = @path
              AND f.lang = 'csharp'
              AND (s.kind = 'import' OR s.kind = 'namespace')
            ORDER BY s.line";
        cmd.Parameters.AddWithValue("@path", path);

        var namespaceScopes = new List<(int StartLine, int EndLine)>();
        var imports = new List<(int Line, string Signature)>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            var kind = reader.GetString(0);
            var line = reader.GetInt32(1);
            if (kind == "namespace")
            {
                var startLine = reader.IsDBNull(2) ? line : reader.GetInt32(2);
                var endLine = reader.IsDBNull(3)
                    ? (reader.IsDBNull(4) ? line : reader.GetInt32(4))
                    : reader.GetInt32(3);
                var signature = GetNullableString(reader, 5);
                if (!string.IsNullOrWhiteSpace(signature)
                    && signature.TrimEnd().EndsWith(';')
                    && !reader.IsDBNull(6))
                {
                    endLine = Math.Max(endLine, reader.GetInt32(6));
                }

                if (startLine > 0 && endLine >= startLine)
                    namespaceScopes.Add((startLine, endLine));
                continue;
            }

            if (!reader.IsDBNull(5))
                imports.Add((line, reader.GetString(5)));
        }

        var scopes = new List<CSharpUsingAliasScope>();
        foreach (var import in imports)
        {
            if (!TryParseCSharpUsingAliasImport(import.Signature, out var aliasName, out var targetQualifiedName, out var isGlobal)
                || isGlobal)
            {
                continue;
            }

            var scopeStartLine = 1;
            var scopeEndLine = int.MaxValue;
            var scopeWidth = int.MaxValue;
            foreach (var (startLine, endLine) in namespaceScopes)
            {
                if (import.Line < startLine || import.Line > endLine)
                    continue;

                var width = endLine - startLine;
                if (width > scopeWidth)
                    continue;

                scopeStartLine = startLine;
                scopeEndLine = endLine;
                scopeWidth = width;
            }

            scopes.Add(new CSharpUsingAliasScope(
                aliasName!,
                targetQualifiedName!,
                import.Line,
                scopeStartLine,
                scopeEndLine,
                IsKnownCSharpTypeQualifiedName(targetQualifiedName!)));
        }

        return scopes;
    }

    private HashSet<string> GetGlobalCSharpUsingStaticTargets()
    {
        if (_csharpGlobalUsingStaticTargets != null)
            return _csharpGlobalUsingStaticTargets;

        var sql = @"
            SELECT s.signature
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE f.lang = 'csharp'
              AND s.kind = 'import'";

        var targets = new HashSet<string>(StringComparer.Ordinal);
        var cmd = RentCommand(sql, static _ => { });
        try
        {
            using var reader = cmd.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                if (reader.IsDBNull(0))
                    continue;
                if (TryParseCSharpUsingStaticImport(reader.GetString(0), out var target, out var isGlobal)
                    && isGlobal)
                {
                    targets.Add(target!);
                }
            }
        }
        finally
        {
            ReleaseCommand(cmd);
        }

        _csharpGlobalUsingStaticTargets = targets;
        return _csharpGlobalUsingStaticTargets;
    }

    private Dictionary<string, CSharpUsingAliasScope> GetGlobalCSharpUsingAliasesByName()
    {
        if (_csharpGlobalUsingAliasesByName != null)
            return _csharpGlobalUsingAliasesByName;

        var sql = @"
            SELECT s.signature
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE f.lang = 'csharp'
              AND s.kind = 'import'";

        var aliases = new Dictionary<string, CSharpUsingAliasScope>(StringComparer.Ordinal);
        var cmd = RentCommand(sql, static _ => { });
        try
        {
            using var reader = cmd.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                if (reader.IsDBNull(0))
                    continue;
                if (TryParseCSharpUsingAliasImport(reader.GetString(0), out var aliasName, out var targetQualifiedName, out var isGlobal)
                    && isGlobal)
                {
                    aliases[aliasName!] = new CSharpUsingAliasScope(
                        aliasName!,
                        targetQualifiedName!,
                        0,
                        1,
                        int.MaxValue,
                        IsKnownCSharpTypeQualifiedName(targetQualifiedName!));
                }
            }
        }
        finally
        {
            ReleaseCommand(cmd);
        }

        _csharpGlobalUsingAliasesByName = aliases;
        return _csharpGlobalUsingAliasesByName;
    }

    private HashSet<string> GetGlobalCSharpUsingNamespaces()
    {
        if (_csharpGlobalUsingNamespaces != null)
            return _csharpGlobalUsingNamespaces;

        var sql = @"
            SELECT s.signature
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE f.lang = 'csharp'
              AND s.kind = 'import'";

        var namespaces = new HashSet<string>(StringComparer.Ordinal);
        var cmd = RentCommand(sql, static _ => { });
        try
        {
            using var reader = cmd.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                if (reader.IsDBNull(0))
                    continue;
                if (TryParseCSharpUsingNamespaceImport(reader.GetString(0), out var target, out var isGlobal)
                    && isGlobal)
                {
                    namespaces.Add(target!);
                }
            }
        }
        finally
        {
            ReleaseCommand(cmd);
        }

        _csharpGlobalUsingNamespaces = namespaces;
        return _csharpGlobalUsingNamespaces;
    }

    private static bool TryParseCSharpUsingStaticImport(string signature, out string? target, out bool isGlobal)
    {
        target = null;
        isGlobal = false;
        if (string.IsNullOrWhiteSpace(signature))
            return false;

        var match = CSharpUsingStaticImportRegex.Match(signature);
        if (!match.Success)
            return false;

        target = NormalizeDbCSharpQualifiedName(match.Groups["target"].Value);
        if (string.IsNullOrWhiteSpace(target))
            return false;

        isGlobal = signature.TrimStart().StartsWith("global using static ", StringComparison.Ordinal);
        return true;
    }

    private static bool TryParseCSharpUsingAliasImport(string signature, out string? aliasName, out string? target, out bool isGlobal)
    {
        aliasName = null;
        target = null;
        isGlobal = false;
        if (string.IsNullOrWhiteSpace(signature))
            return false;

        var match = CSharpUsingAliasImportRegex.Match(signature);
        if (!match.Success)
            return false;

        aliasName = match.Groups["alias"].Value.Trim();
        target = NormalizeDbCSharpQualifiedName(match.Groups["target"].Value);
        if (string.IsNullOrWhiteSpace(aliasName) || string.IsNullOrWhiteSpace(target))
            return false;

        isGlobal = signature.TrimStart().StartsWith("global using ", StringComparison.Ordinal);
        return true;
    }

    private bool TryResolveActiveCSharpUsingAliasScope(string path, int lineNumber, string aliasReference, bool requireTypeAlias, out CSharpUsingAliasScope? resolvedScope)
    {
        resolvedScope = null;
        if (string.IsNullOrWhiteSpace(aliasReference))
            return false;

        var normalizedReference = NormalizeDbCSharpQualifiedName(aliasReference);
        if (string.IsNullOrWhiteSpace(normalizedReference))
            return false;

        var firstDot = normalizedReference.IndexOf('.');
        var aliasName = firstDot >= 0
            ? normalizedReference[..firstDot]
            : normalizedReference;
        if (string.IsNullOrWhiteSpace(aliasName))
            return false;

        if (!_csharpUsingAliasScopesByPath.TryGetValue(path, out var scopes))
        {
            scopes = LoadCSharpUsingAliasScopes(path);
            _csharpUsingAliasScopesByPath[path] = scopes;
        }

        for (var i = scopes.Count - 1; i >= 0; i--)
        {
            var scope = scopes[i];
            if (!string.Equals(scope.AliasName, aliasName, StringComparison.Ordinal))
                continue;
            if (scope.Line > lineNumber)
                continue;
            if (lineNumber < scope.ScopeStartLine || lineNumber > scope.ScopeEndLine)
                continue;
            if (requireTypeAlias && !scope.TargetsType)
                return false;
            resolvedScope = scope;
            return true;
        }

        var globalAliases = GetGlobalCSharpUsingAliasesByName();
        if (!globalAliases.TryGetValue(aliasName, out var globalScope))
            return false;
        if (requireTypeAlias && !globalScope.TargetsType)
            return false;

        resolvedScope = globalScope;
        return true;
    }

    private string ResolveActiveCSharpUsingAliasReference(string path, int lineNumber, string typeReference)
    {
        var resolvedReference = typeReference;
        if (string.IsNullOrWhiteSpace(resolvedReference))
            return string.Empty;

        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (visited.Add(resolvedReference)
               && TryResolveActiveCSharpUsingAliasScope(path, lineNumber, resolvedReference, requireTypeAlias: false, out var resolvedScope)
               && resolvedScope != null)
        {
            var normalizedReference = NormalizeDbCSharpQualifiedName(resolvedReference);
            if (string.IsNullOrWhiteSpace(normalizedReference))
                break;

            var firstDot = normalizedReference.IndexOf('.');
            var suffix = firstDot >= 0
                ? NormalizeDbCSharpQualifiedName(normalizedReference[(firstDot + 1)..])
                : string.Empty;
            var nextReference = string.IsNullOrWhiteSpace(suffix)
                ? resolvedScope.TargetQualifiedName
                : CombineDbQualifiedName(resolvedScope.TargetQualifiedName, suffix);
            if (string.IsNullOrWhiteSpace(nextReference)
                || string.Equals(nextReference, resolvedReference, StringComparison.Ordinal))
            {
                break;
            }

            resolvedReference = nextReference;
        }

        return resolvedReference;
    }

    private static bool TryParseCSharpUsingNamespaceImport(string signature, out string? target, out bool isGlobal)
    {
        target = null;
        isGlobal = false;
        if (string.IsNullOrWhiteSpace(signature)
            || signature.IndexOf('=') >= 0)
        {
            return false;
        }

        var match = CSharpUsingNamespaceImportRegex.Match(signature);
        if (!match.Success)
            return false;

        target = NormalizeDbCSharpQualifiedName(match.Groups["target"].Value);
        if (string.IsNullOrWhiteSpace(target))
            return false;

        isGlobal = signature.TrimStart().StartsWith("global using ", StringComparison.Ordinal);
        return true;
    }

    private static bool IsCSharpUsingStaticConstantPatternContext(string context, string symbolName, int columnNumber)
    {
        if (string.IsNullOrWhiteSpace(context))
            return false;

        if (!TryFindCSharpReferenceTokenStart(context, symbolName, columnNumber, out var symbolColumn))
            return false;

        var cursor = symbolColumn;
        cursor = SkipCSharpTriviaBackward(context, cursor);
        return IsCSharpUsingStaticConstantPatternAnchor(context, ref cursor, out _)
            || IsCSharpUsingStaticConstantTypeKeywordAnchor(context, ref cursor);
    }

    private static bool TryExtractQualifiedCSharpPatternQualifier(string context, string symbolName, int columnNumber, out string qualifier, out string anchorKind)
    {
        qualifier = string.Empty;
        anchorKind = string.Empty;
        if (string.IsNullOrWhiteSpace(context)
            || string.IsNullOrWhiteSpace(symbolName)
            || !TryFindCSharpReferenceTokenStart(context, symbolName, columnNumber, out var symbolColumn))
        {
            return false;
        }

        var headCursor = symbolColumn + symbolName.Length;
        if (!SkipCSharpPatternHeadBackward(context, ref headCursor))
            return false;

        var fullHead = NormalizeDbCSharpQualifiedName(context[headCursor..(symbolColumn + symbolName.Length)]);
        if (string.IsNullOrWhiteSpace(fullHead))
            return false;

        var lastDot = fullHead.LastIndexOf('.');
        if (lastDot < 0)
            return false;

        var anchorCursor = headCursor;
        if (!IsCSharpUsingStaticConstantPatternAnchor(context, ref anchorCursor, out anchorKind))
            return false;

        qualifier = fullHead[..lastDot];
        return !string.IsNullOrWhiteSpace(qualifier);
    }

    private static bool IsCSharpUsingStaticConstantPatternAnchor(string text, ref int cursor, out string anchorKind)
    {
        anchorKind = string.Empty;
        cursor = SkipCSharpTriviaBackward(text, cursor);
        if (TryConsumeTrailingCSharpToken(text, ref cursor, "not"))
            cursor = SkipCSharpTriviaBackward(text, cursor);

        while (true)
        {
            if (TryConsumeTrailingCSharpToken(text, ref cursor, "case"))
            {
                anchorKind = "case";
                return true;
            }

            if (TryConsumeTrailingCSharpToken(text, ref cursor, "is"))
            {
                anchorKind = "is";
                return true;
            }

            if (!TryConsumeTrailingCSharpToken(text, ref cursor, "or")
                && !TryConsumeTrailingCSharpToken(text, ref cursor, "and"))
            {
                return false;
            }

            cursor = SkipCSharpTriviaBackward(text, cursor);
            if (!SkipCSharpPatternHeadBackward(text, ref cursor))
                return false;
            cursor = SkipCSharpTriviaBackward(text, cursor);
            if (TryConsumeTrailingCSharpToken(text, ref cursor, "not"))
                cursor = SkipCSharpTriviaBackward(text, cursor);
        }
    }

    private static bool IsCSharpUsingStaticConstantTypeKeywordAnchor(string text, ref int cursor)
    {
        cursor = SkipCSharpTriviaBackward(text, cursor);
        if (cursor <= 0 || text[cursor - 1] != '(')
            return false;

        cursor--;
        cursor = SkipCSharpTriviaBackward(text, cursor);
        return TryConsumeTrailingCSharpToken(text, ref cursor, "typeof")
            || TryConsumeTrailingCSharpToken(text, ref cursor, "sizeof")
            || TryConsumeTrailingCSharpToken(text, ref cursor, "default");
    }

    private bool TryBuildCSharpUsingStaticPatternContextWindow(
        string path,
        int lineNumber,
        string contextForFilter,
        int columnNumber,
        string symbolName,
        out string patternContext,
        out int patternColumn)
    {
        patternContext = contextForFilter;
        patternColumn = columnNumber;
        if (!_hasChunksTable
            || string.IsNullOrWhiteSpace(path)
            || string.IsNullOrWhiteSpace(symbolName)
            || string.IsNullOrWhiteSpace(contextForFilter)
            || lineNumber <= 1
            || columnNumber <= 0)
        {
            return IsCSharpUsingStaticConstantPatternContext(patternContext, symbolName, patternColumn)
                || TryExtractQualifiedCSharpPatternQualifier(patternContext, symbolName, patternColumn, out _, out _);
        }

        if (IsCSharpUsingStaticConstantPatternContext(patternContext, symbolName, patternColumn)
            || TryExtractQualifiedCSharpPatternQualifier(patternContext, symbolName, patternColumn, out _, out _))
        {
            return true;
        }

        var maxLookback = lineNumber - 1;
        var lookback = Math.Min(2, maxLookback);
        while (true)
        {
            var startLine = Math.Max(1, lineNumber - lookback);
            if (!TryLoadIndexedFileLines(path, out _, out _, out var lineMap, startLine, lineNumber)
                || !lineMap.TryGetValue(lineNumber, out var currentLine))
            {
                return IsCSharpUsingStaticConstantPatternContext(patternContext, symbolName, patternColumn)
                    || TryExtractQualifiedCSharpPatternQualifier(patternContext, symbolName, patternColumn, out _, out _);
            }

            var lines = new List<string>();
            var prefixLength = 0;
            for (var absoluteLine = startLine; absoluteLine <= lineNumber; absoluteLine++)
            {
                if (!lineMap.TryGetValue(absoluteLine, out var lineText))
                    continue;

                if (absoluteLine < lineNumber)
                    prefixLength += lineText.Length + 1;
                lines.Add(lineText);
            }

            patternContext = lines.Count <= 1 ? currentLine : string.Join('\n', lines);
            patternColumn = lines.Count <= 1 ? columnNumber : prefixLength + columnNumber;
            if (IsCSharpUsingStaticConstantPatternContext(patternContext, symbolName, patternColumn)
                || TryExtractQualifiedCSharpPatternQualifier(patternContext, symbolName, patternColumn, out _, out _))
            {
                return true;
            }

            if (startLine == 1 || lookback >= maxLookback)
                return false;

            lookback = Math.Min(maxLookback, Math.Max(lookback + 1, lookback * 2));
        }
    }

    private HashSet<string> GetScopedCSharpQualifiedPatternQualifierCandidates(string path, int lineNumber, string qualifier)
    {
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        var normalizedQualifier = NormalizeDbCSharpQualifiedName(ResolveActiveCSharpUsingAliasReference(path, lineNumber, qualifier));
        if (string.IsNullOrWhiteSpace(normalizedQualifier))
            return candidates;

        candidates.Add(normalizedQualifier);

        foreach (var activeNamespace in GetActiveCSharpTypeNamespaces(path, lineNumber))
        {
            if (string.IsNullOrWhiteSpace(activeNamespace))
                continue;
            candidates.Add(activeNamespace + "." + normalizedQualifier);
        }

        foreach (var activeContainingTypeScope in GetActiveCSharpContainingTypeScopes(path, lineNumber))
        {
            candidates.Add(activeContainingTypeScope.QualifiedName + "." + normalizedQualifier);

            var inheritedContainingTypes = GetInheritedCSharpContainingTypes(activeContainingTypeScope);
            foreach (var inheritedContainingType in inheritedContainingTypes)
            {
                candidates.Add(inheritedContainingType + "." + normalizedQualifier);
            }
        }

        return candidates;
    }

    private static int SkipCSharpTriviaBackward(string text, int cursor)
    {
        while (cursor > 0)
        {
            if (char.IsWhiteSpace(text[cursor - 1]))
            {
                cursor--;
                continue;
            }

            if (cursor >= 2
                && text[cursor - 1] == '/'
                && text[cursor - 2] == '*')
            {
                var commentStart = text.LastIndexOf("/*", cursor - 2, StringComparison.Ordinal);
                if (commentStart >= 0)
                {
                    cursor = commentStart;
                    continue;
                }
            }

            if (TryGetCSharpSingleLineCommentLineStart(text, cursor, out var commentLineStart))
            {
                cursor = commentLineStart;
                continue;
            }

            break;
        }

        return cursor;
    }

    private static bool TryGetCSharpSingleLineCommentLineStart(string text, int cursor, out int commentLineStart)
    {
        commentLineStart = -1;
        if (cursor <= 0)
            return false;

        var lineStart = text.LastIndexOf('\n', Math.Min(cursor - 1, text.Length - 1));
        lineStart = lineStart < 0 ? 0 : lineStart + 1;

        var firstNonWhitespace = lineStart;
        while (firstNonWhitespace < cursor && char.IsWhiteSpace(text[firstNonWhitespace]))
            firstNonWhitespace++;

        if (firstNonWhitespace + 1 >= cursor
            || text[firstNonWhitespace] != '/'
            || text[firstNonWhitespace + 1] != '/')
        {
            return false;
        }

        commentLineStart = lineStart;
        return true;
    }

    private static bool SkipCSharpPatternHeadBackward(string text, ref int cursor)
    {
        if (!TryConsumeTrailingCSharpIdentifier(text, ref cursor))
            return false;

        while (true)
        {
            cursor = SkipCSharpTriviaBackward(text, cursor);
            if (cursor >= 2
                && text[cursor - 2] == ':'
                && text[cursor - 1] == ':')
            {
                cursor -= 2;
            }
            else if (cursor > 0 && text[cursor - 1] == '.')
            {
                cursor--;
            }
            else
            {
                break;
            }

            cursor = SkipCSharpTriviaBackward(text, cursor);
            if (!TryConsumeTrailingCSharpIdentifier(text, ref cursor))
                return false;
        }

        return true;
    }

    private static bool TryConsumeTrailingCSharpIdentifier(string text, ref int cursor)
    {
        var end = cursor;
        while (cursor > 0
               && (char.IsLetterOrDigit(text[cursor - 1])
                   || text[cursor - 1] == '_'))
        {
            cursor--;
        }

        if (cursor == end)
            return false;

        if (cursor > 0 && text[cursor - 1] == '@')
            cursor--;

        return true;
    }

    private static bool TryFindCSharpReferenceTokenStart(string text, string token, int preferredColumn, out int matchIndex)
    {
        matchIndex = -1;
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(token))
            return false;

        var preferredIndex = Math.Max(0, preferredColumn - 1);
        var searchStart = 0;
        while (searchStart < text.Length)
        {
            var candidate = text.IndexOf(token, searchStart, StringComparison.Ordinal);
            if (candidate < 0)
                break;

            searchStart = candidate + token.Length;
            if (!IsCSharpTokenBoundary(text, candidate - 1) || !IsCSharpTokenBoundary(text, candidate + token.Length))
                continue;

            if (candidate <= preferredIndex)
            {
                matchIndex = candidate;
                continue;
            }

            if (matchIndex < 0)
                matchIndex = candidate;
            break;
        }

        return matchIndex >= 0;
    }

    private static bool IsCSharpTokenBoundary(string text, int index)
    {
        if (index < 0 || index >= text.Length)
            return true;

        return !char.IsLetterOrDigit(text[index]) && text[index] != '_';
    }

    private static bool TryConsumeTrailingCSharpToken(string text, ref int cursor, string token)
    {
        var tokenStart = cursor - token.Length;
        if (tokenStart < 0
            || !text.AsSpan(tokenStart, token.Length).SequenceEqual(token))
        {
            return false;
        }

        if (tokenStart > 0 && (char.IsLetterOrDigit(text[tokenStart - 1]) || text[tokenStart - 1] == '_'))
            return false;
        if (cursor < text.Length && (char.IsLetterOrDigit(text[cursor]) || text[cursor] == '_'))
            return false;

        cursor = tokenStart;
        return true;
    }

    private List<CSharpTypeNamespaceCandidate> GetCSharpTypeNamespacesByName(string symbolName)
    {
        if (_csharpTypeNamespacesByName.TryGetValue(symbolName, out var cached))
            return cached;

        var sql = @"
            SELECT DISTINCT s.container_kind, s.container_name, s.container_qualified_name, f.path, s.visibility, s.signature
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE f.lang = 'csharp'
              AND s.name = @symbolName COLLATE NOCASE
              AND s.kind IN ('class', 'struct', 'interface', 'enum', 'delegate')";
        var cmd = RentCommand(sql, static c => c.Parameters.Add("@symbolName", SqliteType.Text));
        SetParameter(cmd, "@symbolName", symbolName);

        var namespaces = new List<CSharpTypeNamespaceCandidate>();
        try
        {
            using var reader = cmd.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                var containerKind = GetNullableString(reader, 0);
                var path = reader.GetString(3);
                var visibility = GetNullableString(reader, 4);
                var signature = GetNullableString(reader, 5);
                var isFileLocal = string.Equals(visibility, "file", StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrWhiteSpace(signature) && signature.Contains("file ", StringComparison.Ordinal));
                if (string.Equals(containerKind, "namespace", StringComparison.Ordinal))
                {
                    var qualifiedNamespace = GetNullableString(reader, 2);
                    var fallbackNamespace = GetNullableString(reader, 1);
                    var namespaceName = NormalizeDbCSharpQualifiedName(qualifiedNamespace ?? fallbackNamespace ?? string.Empty)
                        ?? string.Empty;
                    namespaces.Add(new CSharpTypeNamespaceCandidate(namespaceName, path, isFileLocal));
                    continue;
                }

                if (containerKind == null)
                    namespaces.Add(new CSharpTypeNamespaceCandidate(string.Empty, path, isFileLocal));
            }
        }
        finally
        {
            ReleaseCommand(cmd);
        }

        _csharpTypeNamespacesByName[symbolName] = namespaces;
        return namespaces;
    }

    private List<CSharpContainingTypeCandidate> GetCSharpTypeContainingTypesByName(string symbolName)
    {
        if (_csharpTypeContainingTypesByName.TryGetValue(symbolName, out var cached))
            return cached;

        var sql = @"
            SELECT DISTINCT s.container_kind, s.container_name, s.container_qualified_name, s.visibility, s.signature
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE f.lang = 'csharp'
              AND s.name = @symbolName COLLATE NOCASE
              AND s.kind IN ('class', 'struct', 'interface', 'enum', 'delegate')";
        var cmd = RentCommand(sql, static c => c.Parameters.Add("@symbolName", SqliteType.Text));
        SetParameter(cmd, "@symbolName", symbolName);

        var containingTypes = new List<CSharpContainingTypeCandidate>();
        try
        {
            using var reader = cmd.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                var containerKind = GetNullableString(reader, 0);
                if (containerKind is not ("class" or "struct" or "interface"))
                    continue;

                var containerQualifiedName = GetNullableString(reader, 2);
                var containerName = GetNullableString(reader, 1);
                var qualifiedContainer = NormalizeDbCSharpQualifiedName(containerQualifiedName ?? containerName ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(qualifiedContainer))
                {
                    containingTypes.Add(new CSharpContainingTypeCandidate(
                        qualifiedContainer,
                        IsNestedCSharpTypeAccessibleFromDerivedType(GetNullableString(reader, 3), GetNullableString(reader, 4))));
                }
            }
        }
        finally
        {
            ReleaseCommand(cmd);
        }

        _csharpTypeContainingTypesByName[symbolName] = containingTypes;
        return containingTypes;
    }

    private HashSet<string> GetCSharpConstantPatternContainersByMemberName(string symbolName)
    {
        if (_csharpConstantPatternContainersByMemberName.TryGetValue(symbolName, out var cached))
            return cached;

        var sql = @"
            SELECT s.kind, s.container_kind, s.container_name, s.container_qualified_name, s.signature
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE f.lang = 'csharp'
              AND s.name = @symbolName COLLATE NOCASE
              AND s.container_name IS NOT NULL";
        var cmd = RentCommand(sql, static c => c.Parameters.Add("@symbolName", SqliteType.Text));
        SetParameter(cmd, "@symbolName", symbolName);

        var containers = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var reader = cmd.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                var kind = reader.GetString(0);
                var containerKind = GetNullableString(reader, 1);
                var containerName = GetNullableString(reader, 2);
                if (string.IsNullOrWhiteSpace(containerName))
                    continue;

                var isConstantPatternMember = (kind == "enum" && containerKind == "enum")
                    || (containerKind is "class" or "struct" && !reader.IsDBNull(4) && IsCSharpConstSignature(reader.GetString(4)));
                if (!isConstantPatternMember)
                    continue;

                var qualifiedContainer = GetNullableString(reader, 3);
                containers.Add(string.IsNullOrWhiteSpace(qualifiedContainer) ? containerName! : qualifiedContainer!);
            }
        }
        finally
        {
            ReleaseCommand(cmd);
        }

        _csharpConstantPatternContainersByMemberName[symbolName] = containers;
        return containers;
    }

    private static bool IsCSharpConstSignature(string signature) =>
        signature.Contains(" const ", StringComparison.Ordinal)
        || signature.StartsWith("const ", StringComparison.Ordinal);

    private static string? NormalizeDbCSharpQualifiedName(string candidate)
    {
        var trimmed = candidate.Trim();
        if (trimmed.StartsWith("global::", StringComparison.Ordinal))
            trimmed = trimmed["global::".Length..];
        if (string.IsNullOrWhiteSpace(trimmed))
            return null;

        var segments = trimmed
            .Split(["::", "."], StringSplitOptions.None)
            .Select(segment => segment.Trim())
            .Where(segment => segment.Length > 0)
            .Select(segment => segment[0] == '@' ? segment[1..] : segment)
            .ToList();
        return segments.Count == 0 ? null : string.Join(".", segments);
    }
}
