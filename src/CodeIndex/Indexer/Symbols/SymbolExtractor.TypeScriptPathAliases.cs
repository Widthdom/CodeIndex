using System.Text;
using System.Text.Json;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private const int MaxTypeScriptPathAliasConfigBytes = 256 * 1024;
    private const int MaxTypeScriptPathAliasTotalConfigBytes = 512 * 1024;
    private const int MaxTypeScriptPathAliasExtendsDepth = 8;
    private const int MaxTypeScriptPathAliasConfigJsonDepth = 32;
    private static readonly object TypeScriptPathAliasWarningLock = new();
    private static readonly HashSet<string> TypeScriptPathAliasReportedWarnings = new(StringComparer.Ordinal);
    private static readonly JsonDocumentOptions TypeScriptPathAliasConfigJsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = MaxTypeScriptPathAliasConfigJsonDepth,
    };

    private sealed record TypeScriptPathAliasConfig(string ProjectDirectory, string BaseDirectory, bool HasBaseUrl, IReadOnlyList<TypeScriptPathAliasRule> Rules);

    private sealed record TypeScriptPathAliasRule(string Pattern, string BaseDirectory, IReadOnlyList<string> Targets);

    private static string ResolveJavaScriptTypeScriptModuleSpecifier(string lang, string? filePath, string? projectRoot, string moduleName)
    {
        if (lang is not ("typescript" or "javascript") || string.IsNullOrWhiteSpace(filePath))
            return moduleName;

        if (moduleName.StartsWith(".", StringComparison.Ordinal)
            || moduleName.StartsWith("/", StringComparison.Ordinal)
            || moduleName.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || moduleName.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || moduleName.StartsWith("node:", StringComparison.Ordinal))
        {
            return moduleName;
        }

        var config = FindTypeScriptPathAliasConfig(filePath);
        if (config == null)
            return moduleName;

        foreach (var rule in config.Rules)
        {
            if (!TryMatchTypeScriptPathAliasPattern(rule.Pattern, moduleName, out var wildcard))
                continue;

            foreach (var target in rule.Targets)
            {
                var substituted = target.Contains('*', StringComparison.Ordinal)
                    ? target.Replace("*", wildcard, StringComparison.Ordinal)
                    : target;
                var candidate = Path.IsPathRooted(substituted)
                    ? substituted
                    : Path.Combine(rule.BaseDirectory, substituted);

                if (TryResolveTypeScriptModuleFile(candidate, out var resolvedPath))
                    return NormalizeTypeScriptResolvedModulePath(projectRoot ?? config.ProjectDirectory, resolvedPath);
            }
        }

        if (config.HasBaseUrl
            && TryResolveTypeScriptModuleFile(Path.Combine(config.BaseDirectory, moduleName), out var baseUrlResolvedPath))
        {
            return NormalizeTypeScriptResolvedModulePath(projectRoot ?? config.ProjectDirectory, baseUrlResolvedPath);
        }

        return moduleName;
    }

    private static TypeScriptPathAliasConfig? FindTypeScriptPathAliasConfig(string filePath)
    {
        var fullFilePath = Path.GetFullPath(filePath);
        var directory = Directory.Exists(fullFilePath)
            ? fullFilePath
            : Path.GetDirectoryName(fullFilePath);
        while (!string.IsNullOrEmpty(directory))
        {
            foreach (var configFileName in new[] { "tsconfig.json", "jsconfig.json" })
            {
                var configPath = Path.Combine(directory, configFileName);
                if (File.Exists(configPath))
                {
                    var totalConfigBytesRead = 0L;
                    return ParseTypeScriptPathAliasConfig(
                        configPath,
                        new HashSet<string>(StringComparer.Ordinal),
                        depth: 0,
                        ref totalConfigBytesRead);
                }
            }

            var parent = Directory.GetParent(directory)?.FullName;
            if (string.Equals(parent, directory, StringComparison.Ordinal))
                break;
            directory = parent;
        }

        return null;
    }

    private static TypeScriptPathAliasConfig? ParseTypeScriptPathAliasConfig(
        string configPath,
        HashSet<string> seen,
        int depth,
        ref long totalConfigBytesRead)
    {
        configPath = Path.GetFullPath(configPath);
        if (!seen.Add(configPath))
            return null;

        if (depth > MaxTypeScriptPathAliasExtendsDepth)
        {
            ReportTypeScriptPathAliasWarningOnce(
                $"Skipped TypeScript path alias config {configPath} because the extends depth exceeds {MaxTypeScriptPathAliasExtendsDepth}.");
            return null;
        }

        JsonDocument document;
        try
        {
            if (!TryReadTypeScriptPathAliasConfigText(
                    configPath,
                    ref totalConfigBytesRead,
                    out var configText,
                    out var skippedReason))
            {
                ReportTypeScriptPathAliasWarningOnce(
                    $"Skipped TypeScript path alias config {configPath} because {skippedReason}.");
                return null;
            }

            document = JsonDocument.Parse(
                configText,
                TypeScriptPathAliasConfigJsonOptions);
        }
        catch (JsonException)
        {
            ReportTypeScriptPathAliasWarningOnce(
                $"Skipped TypeScript path alias config {configPath} because it could not be parsed as JSON within the {MaxTypeScriptPathAliasConfigJsonDepth}-level depth limit.");
            return null;
        }
        catch
        {
            return null;
        }

        using (document)
        {
            var configDirectory = Path.GetDirectoryName(configPath) ?? Directory.GetCurrentDirectory();
            var inherited = TryGetTypeScriptExtendsPath(document.RootElement, configDirectory, out var extendsPath)
                ? ParseTypeScriptPathAliasConfig(extendsPath, seen, depth + 1, ref totalConfigBytesRead)
                : null;

            var baseDirectory = inherited?.BaseDirectory ?? configDirectory;
            var hasBaseUrl = inherited?.HasBaseUrl ?? false;
            var rules = inherited?.Rules.ToList() ?? [];
            if (document.RootElement.TryGetProperty("compilerOptions", out var compilerOptions)
                && compilerOptions.ValueKind == JsonValueKind.Object)
            {
                if (compilerOptions.TryGetProperty("baseUrl", out var baseUrlElement)
                    && baseUrlElement.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(baseUrlElement.GetString()))
                {
                    var baseUrl = baseUrlElement.GetString()!;
                    baseDirectory = Path.IsPathRooted(baseUrl)
                        ? baseUrl
                        : Path.GetFullPath(Path.Combine(configDirectory, baseUrl));
                    hasBaseUrl = true;
                }

                if (compilerOptions.TryGetProperty("paths", out var pathsElement)
                    && pathsElement.ValueKind == JsonValueKind.Object)
                {
                    rules.Clear();
                    foreach (var property in pathsElement.EnumerateObject())
                    {
                        if (property.Value.ValueKind != JsonValueKind.Array)
                            continue;

                        var targets = new List<string>();
                        foreach (var item in property.Value.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.String
                                && !string.IsNullOrWhiteSpace(item.GetString()))
                            {
                                targets.Add(item.GetString()!);
                            }
                        }

                        if (targets.Count > 0)
                            rules.Add(new TypeScriptPathAliasRule(property.Name, baseDirectory, targets));
                    }
                }
            }

            return rules.Count == 0 && !hasBaseUrl
                ? null
                : new TypeScriptPathAliasConfig(configDirectory, baseDirectory, hasBaseUrl, SortTypeScriptPathAliasRules(rules));
        }
    }

    private static bool TryReadTypeScriptPathAliasConfigText(
        string configPath,
        ref long totalConfigBytesRead,
        out string text,
        out string skippedReason)
    {
        text = string.Empty;
        skippedReason = string.Empty;

        try
        {
            using var stream = new FileStream(
                configPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 8192,
                useAsync: false);

            if (stream.Length > MaxTypeScriptPathAliasConfigBytes)
            {
                skippedReason = $"it exceeds {MaxTypeScriptPathAliasConfigBytes} bytes";
                return false;
            }

            if (totalConfigBytesRead + stream.Length > MaxTypeScriptPathAliasTotalConfigBytes)
            {
                skippedReason = $"the extends chain exceeds {MaxTypeScriptPathAliasTotalConfigBytes} bytes";
                return false;
            }

            using var accumulator = new MemoryStream((int)Math.Min(stream.Length, MaxTypeScriptPathAliasConfigBytes));
            var buffer = new byte[8192];
            long fileBytesRead = 0;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                fileBytesRead += read;
                if (fileBytesRead > MaxTypeScriptPathAliasConfigBytes)
                {
                    skippedReason = $"it exceeds {MaxTypeScriptPathAliasConfigBytes} bytes";
                    return false;
                }

                totalConfigBytesRead += read;
                if (totalConfigBytesRead > MaxTypeScriptPathAliasTotalConfigBytes)
                {
                    skippedReason = $"the extends chain exceeds {MaxTypeScriptPathAliasTotalConfigBytes} bytes";
                    return false;
                }

                accumulator.Write(buffer, 0, read);
            }

            text = Encoding.UTF8.GetString(accumulator.ToArray());
            if (text.Length > 0 && text[0] == '\uFEFF')
                text = text[1..];
            return true;
        }
        catch (IOException)
        {
            skippedReason = "it could not be read";
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            skippedReason = "it could not be read";
            return false;
        }
    }

    private static void ReportTypeScriptPathAliasWarningOnce(string message)
    {
        lock (TypeScriptPathAliasWarningLock)
        {
            if (!TypeScriptPathAliasReportedWarnings.Add(message))
                return;
        }

        Console.Error.WriteLine("cdidx: warning: " + message);
    }

    private static IReadOnlyList<TypeScriptPathAliasRule> SortTypeScriptPathAliasRules(IReadOnlyList<TypeScriptPathAliasRule> rules) =>
        rules
            .OrderBy(static rule => rule.Pattern.Contains('*', StringComparison.Ordinal) ? 1 : 0)
            .ThenByDescending(static rule => GetTypeScriptPathAliasLiteralLength(rule.Pattern))
            .ToList();

    private static int GetTypeScriptPathAliasLiteralLength(string pattern) =>
        pattern.Count(static ch => ch != '*');

    private static bool TryGetTypeScriptExtendsPath(JsonElement root, string configDirectory, out string extendsPath)
    {
        extendsPath = string.Empty;
        if (!root.TryGetProperty("extends", out var extendsElement)
            || extendsElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(extendsElement.GetString()))
        {
            return false;
        }

        var value = extendsElement.GetString()!;
        if (!value.StartsWith(".", StringComparison.Ordinal) && !Path.IsPathRooted(value))
            return false;

        var candidate = Path.IsPathRooted(value) ? value : Path.Combine(configDirectory, value);
        if (!Path.HasExtension(candidate))
            candidate += ".json";

        if (!File.Exists(candidate))
            return false;

        extendsPath = candidate;
        return true;
    }

    private static bool TryMatchTypeScriptPathAliasPattern(string pattern, string moduleName, out string wildcard)
    {
        wildcard = string.Empty;
        var starIndex = pattern.IndexOf('*', StringComparison.Ordinal);
        if (starIndex < 0)
            return string.Equals(pattern, moduleName, StringComparison.Ordinal);

        var prefix = pattern[..starIndex];
        var suffix = pattern[(starIndex + 1)..];
        if (!moduleName.StartsWith(prefix, StringComparison.Ordinal)
            || !moduleName.EndsWith(suffix, StringComparison.Ordinal)
            || moduleName.Length < prefix.Length + suffix.Length)
        {
            return false;
        }

        wildcard = moduleName.Substring(prefix.Length, moduleName.Length - prefix.Length - suffix.Length);
        return true;
    }

    private static bool TryResolveTypeScriptModuleFile(string candidate, out string resolvedPath)
    {
        foreach (var path in EnumerateTypeScriptModuleCandidates(candidate))
        {
            if (File.Exists(path))
            {
                resolvedPath = Path.GetFullPath(path);
                return true;
            }
        }

        resolvedPath = string.Empty;
        return false;
    }

    private static IEnumerable<string> EnumerateTypeScriptModuleCandidates(string candidate)
    {
        yield return candidate;

        foreach (var extension in new[] { ".ts", ".tsx", ".mts", ".cts", ".js", ".jsx", ".mjs", ".cjs", ".d.ts", ".json" })
            yield return candidate + extension;

        foreach (var extension in new[] { ".ts", ".tsx", ".mts", ".cts", ".js", ".jsx", ".mjs", ".cjs", ".d.ts", ".json" })
            yield return Path.Combine(candidate, "index" + extension);
    }

    private static string NormalizeTypeScriptResolvedModulePath(string projectDirectory, string resolvedPath)
    {
        var relativePath = Path.GetRelativePath(projectDirectory, Path.GetFullPath(resolvedPath));
        return relativePath.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }
}
