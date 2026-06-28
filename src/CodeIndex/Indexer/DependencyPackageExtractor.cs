using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using CodeIndex.Diagnostics;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal readonly record struct DependencyPackageInfo(
    string Name,
    string? Version,
    string? RequestedVersion,
    string SourceKind,
    string SubKind,
    string? Scope,
    string? Role,
    int Line,
    int Column,
    string Signature);

internal static class DependencyPackageExtractor
{
    internal const int MaxJsonLockParseBytes = 16 * 1024 * 1024;
    internal const int MaxJsonLockParseDepth = 64;

    private static readonly Regex RequirementNameRegex = new(
        @"^\s*(?<name>[A-Za-z0-9][A-Za-z0-9_.-]*)(?:\[[^\]]+\])?\s*(?<version>(?:===|==|~=|!=|<=|>=|<|>|=).+)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex TomlSectionRegex = new(
        @"^\s*\[(?<section>[^\]]+)\]\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex TomlAssignmentRegex = new(
        @"^\s*(?:""(?<quoted>[^""]+)""|'(?<single>[^']+)'|(?<bare>[A-Za-z0-9_.-]+))\s*=\s*(?<value>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex QuotedDependencyRegex = new(
        @"[""'](?<spec>[A-Za-z0-9][A-Za-z0-9_.-]*(?:\[[^\]]+\])?(?:\s*(?:===|==|~=|!=|<=|>=|<|>|=)[^""',\]]*)?)[""']",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex TomlVersionRegex = new(
        @"\bversion\s*=\s*[""'](?<version>[^""']+)[""']",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    public static List<SymbolRecord> ExtractSymbols(long fileId, string content, string[] lines, string? path, string language)
    {
        var packages = ExtractPackages(content, lines, path, language);
        var symbols = new List<SymbolRecord>(packages.Count);
        foreach (var package in packages)
        {
            symbols.Add(new SymbolRecord
            {
                FileId = fileId,
                Kind = "package",
                SubKind = package.SubKind,
                Name = package.Name,
                Line = package.Line,
                StartLine = package.Line,
                StartColumn = Math.Max(0, package.Column - 1),
                EndLine = package.Line,
                Signature = package.Signature,
                ContainerKind = package.Scope == null ? null : "project",
                ContainerName = package.Scope,
                ContainerQualifiedName = package.Scope,
                FamilyKey = "package:" + NormalizePackageName(package.Name),
            });
        }

        return symbols;
    }

    public static List<ReferenceRecord> ExtractReferences(long fileId, string content, string[] lines, string? path, string language)
    {
        var packages = ExtractPackages(content, lines, path, language);
        var references = new List<ReferenceRecord>(packages.Count);
        foreach (var package in packages)
        {
            references.Add(new ReferenceRecord
            {
                FileId = fileId,
                SymbolName = package.Name,
                ReferenceKind = "dependency",
                Line = package.Line,
                Column = package.Column,
                Context = GetContext(lines, package.Line),
                ContainerKind = package.Scope == null ? null : "project",
                ContainerName = package.Scope,
            });
        }

        return references;
    }

    internal static List<DependencyPackageInfo> ExtractPackages(string content, string[] lines, string? path, string language)
    {
        var packages = new List<DependencyPackageInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var fileName = Path.GetFileName(path ?? string.Empty);

        if (language == "dependency_lock")
        {
            ExtractJsonLock(content, lines, fileName, packages, seen);
            return packages;
        }

        if (IsXmlDependencyManifest(fileName, content))
            ExtractXmlManifest(content, lines, packages, seen);
        else if (string.Equals(fileName, "requirements.txt", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(Path.GetExtension(fileName), ".in", StringComparison.OrdinalIgnoreCase))
            ExtractRequirements(lines, packages, seen);
        else if (string.Equals(fileName, "pyproject.toml", StringComparison.OrdinalIgnoreCase))
            ExtractPyProjectToml(lines, packages, seen);

        return packages;
    }

    private static void ExtractXmlManifest(
        string content,
        string[] lines,
        List<DependencyPackageInfo> packages,
        HashSet<string> seen)
    {
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(
                new StringReader(content),
                SymbolExtractor.CreateExtractionXmlReaderSettings(DtdProcessing.Prohibit));
            document = XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (XmlException)
        {
            return;
        }

        foreach (var element in document.Descendants())
        {
            var localName = element.Name.LocalName;
            if (string.Equals(localName, "PackageVersion", StringComparison.OrdinalIgnoreCase))
            {
                AddXmlPackage(element, lines, "Include", "Version", packages, seen);
                AddXmlPackage(element, lines, "Update", "Version", packages, seen);
            }
            else if (string.Equals(localName, "package", StringComparison.OrdinalIgnoreCase))
            {
                AddXmlPackage(element, lines, "id", "version", packages, seen);
            }
        }
    }

    private static void AddXmlPackage(
        XElement element,
        string[] lines,
        string nameAttribute,
        string versionAttribute,
        List<DependencyPackageInfo> packages,
        HashSet<string> seen)
    {
        var name = element.Attribute(nameAttribute)?.Value.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return;

        var version = element.Attribute(versionAttribute)?.Value.Trim();
        var lineInfo = element.Attribute(nameAttribute) as IXmlLineInfo;
        var hasLineInfo = lineInfo?.HasLineInfo() == true;
        var line = hasLineInfo ? lineInfo!.LineNumber : FindLine(lines, name);
        var column = hasLineInfo ? lineInfo!.LinePosition : FindColumn(lines, line, name);

        AddPackage(
            packages,
            seen,
            name,
            version,
            requestedVersion: null,
            sourceKind: "manifest",
            subKind: "manifest_dependency",
            scope: null,
            role: null,
            line,
            column);
    }

    private static void ExtractJsonLock(
        string content,
        string[] lines,
        string fileName,
        List<DependencyPackageInfo> packages,
        HashSet<string> seen)
    {
        JsonDocument document;
        try
        {
            document = BoundedJson.ParseDocument(content, MaxJsonLockParseBytes, MaxJsonLockParseDepth);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return;
        }

        using (document)
        {
            if (string.Equals(fileName, "packages.lock.json", StringComparison.OrdinalIgnoreCase))
                ExtractNuGetPackagesLock(document.RootElement, lines, packages, seen);
            else if (string.Equals(fileName, "package-lock.json", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(fileName, "npm-shrinkwrap.json", StringComparison.OrdinalIgnoreCase))
                ExtractNpmPackageLock(document.RootElement, lines, packages, seen);
        }
    }

    private static void ExtractNuGetPackagesLock(
        JsonElement root,
        string[] lines,
        List<DependencyPackageInfo> packages,
        HashSet<string> seen)
    {
        if (!root.TryGetProperty("dependencies", out var dependencies) || dependencies.ValueKind != JsonValueKind.Object)
            return;

        foreach (var framework in dependencies.EnumerateObject())
        {
            if (framework.Value.ValueKind != JsonValueKind.Object)
                continue;

            foreach (var package in framework.Value.EnumerateObject())
            {
                var packageObject = package.Value;
                var role = GetStringProperty(packageObject, "type");
                var resolved = GetStringProperty(packageObject, "resolved");
                var requested = GetStringProperty(packageObject, "requested");
                var location = FindJsonProperty(lines, package.Name);
                var normalizedRole = NormalizeRole(role);

                AddPackage(
                    packages,
                    seen,
                    package.Name,
                    resolved,
                    requested,
                    sourceKind: "lock",
                    subKind: normalizedRole == "transitive" ? "lock_transitive_dependency" : "lock_direct_dependency",
                    scope: framework.Name,
                    role: normalizedRole,
                    location.Line,
                    location.Column);
            }
        }
    }

    private static void ExtractNpmPackageLock(
        JsonElement root,
        string[] lines,
        List<DependencyPackageInfo> packages,
        HashSet<string> seen)
    {
        if (root.TryGetProperty("packages", out var packageEntries) && packageEntries.ValueKind == JsonValueKind.Object)
        {
            foreach (var package in packageEntries.EnumerateObject())
            {
                if (string.IsNullOrEmpty(package.Name) || package.Value.ValueKind != JsonValueKind.Object)
                    continue;

                var name = package.Name.StartsWith("node_modules/", StringComparison.Ordinal)
                    ? package.Name["node_modules/".Length..]
                    : package.Name;
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var version = GetStringProperty(package.Value, "version");
                var location = FindJsonProperty(lines, package.Name);
                AddPackage(
                    packages,
                    seen,
                    name,
                    version,
                    requestedVersion: null,
                    sourceKind: "lock",
                    subKind: "lock_dependency",
                    scope: "npm",
                    role: null,
                    location.Line,
                    location.Column);
            }
        }

        if (!root.TryGetProperty("dependencies", out var dependencies) || dependencies.ValueKind != JsonValueKind.Object)
            return;

        foreach (var package in dependencies.EnumerateObject())
        {
            if (package.Value.ValueKind != JsonValueKind.Object)
                continue;

            var version = GetStringProperty(package.Value, "version");
            var requested = GetStringProperty(package.Value, "requires");
            var location = FindJsonProperty(lines, package.Name);
            AddPackage(
                packages,
                seen,
                package.Name,
                version,
                requested,
                sourceKind: "lock",
                subKind: "lock_dependency",
                scope: "npm",
                role: null,
                location.Line,
                location.Column);
        }
    }

    private static void ExtractRequirements(string[] lines, List<DependencyPackageInfo> packages, HashSet<string> seen)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            var rawLine = lines[i];
            var line = StripRequirementComment(rawLine).Trim();
            if (line.Length == 0
                || line.StartsWith("-", StringComparison.Ordinal)
                || line.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("git+", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith(".", StringComparison.Ordinal)
                || line.StartsWith("/", StringComparison.Ordinal))
            {
                continue;
            }

            if (!TryParseDependencySpec(line, out var name, out var version))
                continue;

            AddPackage(
                packages,
                seen,
                name,
                version,
                requestedVersion: null,
                sourceKind: "manifest",
                subKind: "manifest_dependency",
                scope: "python.requirements",
                role: null,
                line: i + 1,
                column: FindColumn(lines, i + 1, name));
        }
    }

    private static void ExtractPyProjectToml(string[] lines, List<DependencyPackageInfo> packages, HashSet<string> seen)
    {
        string? section = null;
        string? arrayScope = null;

        for (var i = 0; i < lines.Length; i++)
        {
            var rawLine = lines[i];
            var trimmed = rawLine.Trim();
            var sectionMatch = TomlSectionRegex.Match(trimmed);
            if (sectionMatch.Success)
            {
                section = sectionMatch.Groups["section"].Value.Trim();
                arrayScope = null;
                continue;
            }

            if (arrayScope != null)
            {
                AddQuotedDependencySpecs(rawLine, i + 1, arrayScope, packages, seen);
                if (trimmed.Contains(']', StringComparison.Ordinal))
                    arrayScope = null;
                continue;
            }

            var assignment = TomlAssignmentRegex.Match(rawLine);
            if (!assignment.Success)
                continue;

            var key = assignment.Groups["quoted"].Success
                ? assignment.Groups["quoted"].Value
                : assignment.Groups["single"].Success
                    ? assignment.Groups["single"].Value
                    : assignment.Groups["bare"].Value;
            var value = assignment.Groups["value"].Value.Trim();

            if (string.Equals(section, "project", StringComparison.Ordinal)
                && string.Equals(key, "dependencies", StringComparison.Ordinal)
                && value.StartsWith("[", StringComparison.Ordinal))
            {
                var scope = "python.project";
                AddQuotedDependencySpecs(rawLine, i + 1, scope, packages, seen);
                if (!value.Contains(']', StringComparison.Ordinal))
                    arrayScope = scope;
            }
            else if (string.Equals(section, "project.optional-dependencies", StringComparison.Ordinal)
                     && value.StartsWith("[", StringComparison.Ordinal))
            {
                var scope = "python.optional." + key;
                AddQuotedDependencySpecs(rawLine, i + 1, scope, packages, seen);
                if (!value.Contains(']', StringComparison.Ordinal))
                    arrayScope = scope;
            }
            else if (IsPoetryDependencySection(section) && !string.Equals(key, "python", StringComparison.OrdinalIgnoreCase))
            {
                var version = ExtractPoetryVersion(value);
                AddPackage(
                    packages,
                    seen,
                    key,
                    version,
                    requestedVersion: null,
                    sourceKind: "manifest",
                    subKind: "manifest_dependency",
                    scope: section,
                    role: null,
                    line: i + 1,
                    column: FindColumn(lines, i + 1, key));
            }
        }
    }

    private static void AddQuotedDependencySpecs(
        string rawLine,
        int line,
        string scope,
        List<DependencyPackageInfo> packages,
        HashSet<string> seen)
    {
        foreach (Match match in QuotedDependencyRegex.Matches(rawLine))
        {
            var spec = match.Groups["spec"].Value;
            if (!TryParseDependencySpec(spec, out var name, out var version))
                continue;

            AddPackage(
                packages,
                seen,
                name,
                version,
                requestedVersion: null,
                sourceKind: "manifest",
                subKind: "manifest_dependency",
                scope,
                role: null,
                line,
                column: match.Index + 2);
        }
    }

    private static bool TryParseDependencySpec(string spec, out string name, out string? version)
    {
        name = string.Empty;
        version = null;
        var markerIndex = spec.IndexOf(';');
        var normalized = (markerIndex >= 0 ? spec[..markerIndex] : spec).Trim();
        if (normalized.Length == 0)
            return false;

        var match = RequirementNameRegex.Match(normalized);
        if (!match.Success)
            return false;

        name = match.Groups["name"].Value.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return false;

        version = match.Groups["version"].Success
            ? match.Groups["version"].Value.Trim()
            : null;
        return true;
    }

    private static void AddPackage(
        List<DependencyPackageInfo> packages,
        HashSet<string> seen,
        string name,
        string? version,
        string? requestedVersion,
        string sourceKind,
        string subKind,
        string? scope,
        string? role,
        int line,
        int column)
    {
        name = name.Trim();
        if (name.Length == 0)
            return;

        line = Math.Max(1, line);
        column = Math.Max(1, column);
        version = NormalizeEmpty(version);
        requestedVersion = NormalizeEmpty(requestedVersion);
        scope = NormalizeEmpty(scope);
        role = NormalizeEmpty(role);

        var key = string.Join(
            "\u001f",
            NormalizePackageName(name),
            version,
            requestedVersion,
            sourceKind,
            subKind,
            scope,
            role,
            line.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!seen.Add(key))
            return;

        packages.Add(new DependencyPackageInfo(
            name,
            version,
            requestedVersion,
            sourceKind,
            subKind,
            scope,
            role,
            line,
            column,
            BuildSignature(name, version, requestedVersion, sourceKind, scope, role)));
    }

    private static string BuildSignature(string name, string? version, string? requestedVersion, string sourceKind, string? scope, string? role)
    {
        var parts = new List<string> { $"package {name}", $"source={sourceKind}" };
        if (!string.IsNullOrWhiteSpace(role))
            parts.Add($"role={role}");
        if (!string.IsNullOrWhiteSpace(version))
            parts.Add(sourceKind == "lock"
                ? $"resolved={version}"
                : LooksLikeVersionConstraint(version) ? $"constraint={version}" : $"version={version}");
        if (!string.IsNullOrWhiteSpace(requestedVersion))
            parts.Add($"requested={requestedVersion}");
        if (!string.IsNullOrWhiteSpace(scope))
            parts.Add($"scope={scope}");
        return string.Join(" ", parts);
    }

    private static bool IsXmlDependencyManifest(string fileName, string content)
        => string.Equals(fileName, "Directory.Packages.props", StringComparison.OrdinalIgnoreCase)
        || string.Equals(fileName, "packages.config", StringComparison.OrdinalIgnoreCase)
        || content.AsSpan().TrimStart().StartsWith("<", StringComparison.Ordinal);

    private static bool IsPoetryDependencySection(string? section)
    {
        return section != null
            && (string.Equals(section, "tool.poetry.dependencies", StringComparison.Ordinal)
                || (section.StartsWith("tool.poetry.group.", StringComparison.Ordinal)
                    && section.EndsWith(".dependencies", StringComparison.Ordinal)));
    }

    private static string? ExtractPoetryVersion(string value)
    {
        value = value.Trim();
        if ((value.StartsWith("\"", StringComparison.Ordinal) && value.EndsWith("\"", StringComparison.Ordinal))
            || (value.StartsWith("'", StringComparison.Ordinal) && value.EndsWith("'", StringComparison.Ordinal)))
        {
            return value[1..^1].Trim();
        }

        var match = TomlVersionRegex.Match(value);
        return match.Success ? match.Groups["version"].Value.Trim() : null;
    }

    private static (int Line, int Column) FindJsonProperty(string[] lines, string propertyName)
    {
        var quoted = JsonSerializer.Serialize(propertyName);
        for (var i = 0; i < lines.Length; i++)
        {
            var index = lines[i].IndexOf(quoted, StringComparison.Ordinal);
            if (index >= 0)
                return (i + 1, index + 1);
        }

        return (1, 1);
    }

    private static int FindLine(string[] lines, string text)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(text, StringComparison.OrdinalIgnoreCase))
                return i + 1;
        }

        return 1;
    }

    private static int FindColumn(string[] lines, int line, string text)
    {
        if (line <= 0 || line > lines.Length)
            return 1;

        var index = lines[line - 1].IndexOf(text, StringComparison.OrdinalIgnoreCase);
        return index >= 0 ? index + 1 : 1;
    }

    private static string GetContext(string[] lines, int line)
        => line > 0 && line <= lines.Length ? lines[line - 1].Trim() : string.Empty;

    private static string NormalizePackageName(string name)
        => name.Trim().ToLowerInvariant();

    private static string? NormalizeEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool LooksLikeVersionConstraint(string version)
        => version.StartsWith("=", StringComparison.Ordinal)
        || version.StartsWith("<", StringComparison.Ordinal)
        || version.StartsWith(">", StringComparison.Ordinal)
        || version.StartsWith("~", StringComparison.Ordinal)
        || version.StartsWith("!", StringComparison.Ordinal)
        || version.StartsWith("^", StringComparison.Ordinal);

    private static string? GetStringProperty(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static string? NormalizeRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
            return null;
        return role.Equals("Transitive", StringComparison.OrdinalIgnoreCase) ? "transitive" : "direct";
    }

    private static string StripRequirementComment(string line)
    {
        var commentIndex = line.IndexOf('#', StringComparison.Ordinal);
        return commentIndex >= 0 ? line[..commentIndex] : line;
    }
}
