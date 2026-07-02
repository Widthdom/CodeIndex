using System.Text;
using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Diagnostics;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private const int MaxTypeScriptPathAliasConfigBytes = 256 * 1024;
    private const int MaxTypeScriptPathAliasTotalConfigBytes = 512 * 1024;
    private const int MaxTypeScriptPathAliasExtendsDepth = 8;
    private const int MaxTypeScriptPathAliasConfigJsonDepth = 32;
    private const int MaxTypeScriptPathAliasRules = 1024;
    private const int MaxTypeScriptPathAliasTargetsPerRule = 32;
    private const int MaxTypeScriptPathAliasTotalTargets = 2048;
    private const int MaxTypeScriptPathAliasExpansionCandidates = 1024;
    private const int MaxTypeScriptPathAliasPatternLength = 512;
    private const int MaxTypeScriptPathAliasTargetLength = 1024;
    private const int MaxTypeScriptPathAliasModuleSpecifierLength = 4096;
    private const int MaxTypeScriptPathAliasSubstitutedTargetLength = 4096;
    private const string TypeScriptPathAliasDiagnosticReadFailed = "tsconfig_read_failed";
    private const string TypeScriptPathAliasDiagnosticJsonInvalid = "tsconfig_json_invalid";
    private const string TypeScriptPathAliasDiagnosticSizeLimit = "tsconfig_size_limit";
    private const string TypeScriptPathAliasDiagnosticDepthLimit = "path_alias_depth_limit";
    private const string TypeScriptPathAliasDiagnosticExpansionCandidateLimit = "path_alias_expansion_candidate_limit";
    private static readonly string[] TypeScriptPathAliasConfigFileNames =
    [
        "tsconfig.json",
        "jsconfig.json",
    ];
    private static readonly string[] TypeScriptModuleCandidateExtensions =
    [
        ".ts",
        ".tsx",
        ".mts",
        ".cts",
        ".js",
        ".jsx",
        ".mjs",
        ".cjs",
        ".d.ts",
        ".json",
    ];
    private static readonly object TypeScriptPathAliasWarningLock = new();
    private static readonly HashSet<string> TypeScriptPathAliasReportedWarnings = new(StringComparer.Ordinal);
    private sealed record TypeScriptPathAliasConfig(string ConfigPath, string ProjectDirectory, string BaseDirectory, bool HasBaseUrl, IReadOnlyList<TypeScriptPathAliasRule> Rules);

    private sealed record TypeScriptPathAliasRule(string Pattern, string BaseDirectory, IReadOnlyList<string> Targets);

    private readonly record struct TypeScriptPathAliasConfigSkippedReason(string Code, string Reason);

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

        if (moduleName.Length > MaxTypeScriptPathAliasModuleSpecifierLength)
        {
            ReportTypeScriptPathAliasConfigWarningOnce(
                config.ConfigPath,
                $"Skipped TypeScript path alias resolution in config {DiagnosticSanitizer.ForPath(config.ConfigPath)} for module specifiers longer than {MaxTypeScriptPathAliasModuleSpecifierLength} characters.");
            return moduleName;
        }

        var remainingExpansionCandidates = MaxTypeScriptPathAliasExpansionCandidates;
        var expansionCandidatesTruncated = false;
        foreach (var rule in config.Rules)
        {
            if (!TryMatchTypeScriptPathAliasPattern(rule.Pattern, moduleName, out var wildcard))
                continue;

            foreach (var target in rule.Targets)
            {
                if (!TrySubstituteTypeScriptPathAliasTarget(target, wildcard, out var substituted))
                {
                    ReportTypeScriptPathAliasConfigWarningOnce(
                        config.ConfigPath,
                        $"Skipped TypeScript path alias target substitution in config {DiagnosticSanitizer.ForPath(config.ConfigPath)} longer than {MaxTypeScriptPathAliasSubstitutedTargetLength} characters.");
                    continue;
                }

                var candidate = Path.IsPathRooted(substituted)
                    ? substituted
                    : Path.Combine(rule.BaseDirectory, substituted);

                if (TryResolveTypeScriptModuleFile(
                        candidate,
                        ref remainingExpansionCandidates,
                        out var resolvedPath,
                        out var candidateBudgetExhausted))
                {
                    return NormalizeTypeScriptResolvedModulePath(projectRoot ?? config.ProjectDirectory, resolvedPath);
                }

                if (candidateBudgetExhausted)
                {
                    expansionCandidatesTruncated = true;
                    break;
                }
            }

            if (expansionCandidatesTruncated)
                break;
        }

        if (config.HasBaseUrl && !expansionCandidatesTruncated)
        {
            if (TryResolveTypeScriptModuleFile(
                    Path.Combine(config.BaseDirectory, moduleName),
                    ref remainingExpansionCandidates,
                    out var baseUrlResolvedPath,
                    out var baseUrlBudgetExhausted))
            {
                return NormalizeTypeScriptResolvedModulePath(projectRoot ?? config.ProjectDirectory, baseUrlResolvedPath);
            }

            expansionCandidatesTruncated = baseUrlBudgetExhausted;
        }

        if (expansionCandidatesTruncated)
        {
            ReportTypeScriptPathAliasWarningOnce(
                $"Truncated TypeScript path alias expansion candidates in config {config.ConfigPath} [{TypeScriptPathAliasDiagnosticExpansionCandidateLimit}] to {MaxTypeScriptPathAliasExpansionCandidates} probes.");
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
            foreach (var configFileName in TypeScriptPathAliasConfigFileNames)
            {
                var configPath = Path.Combine(directory, configFileName);
                if (File.Exists(configPath) || Directory.Exists(configPath))
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
            ReportTypeScriptPathAliasConfigSkippedWarning(
                configPath,
                TypeScriptPathAliasDiagnosticDepthLimit,
                $"the extends depth exceeds {MaxTypeScriptPathAliasExtendsDepth}");
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
                ReportTypeScriptPathAliasConfigSkippedWarning(configPath, skippedReason);
                return null;
            }

            document = BoundedJson.ParseDocument(
                configText,
                MaxTypeScriptPathAliasConfigBytes,
                MaxTypeScriptPathAliasConfigJsonDepth,
                JsonCommentHandling.Skip,
                allowTrailingCommas: true);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            ReportTypeScriptPathAliasConfigSkippedWarning(
                configPath,
                TypeScriptPathAliasDiagnosticJsonInvalid,
                $"it could not be parsed as JSON within the {MaxTypeScriptPathAliasConfigBytes}-byte size limit and {MaxTypeScriptPathAliasConfigJsonDepth}-level depth limit");
            return null;
        }
        catch
        {
            ReportTypeScriptPathAliasConfigSkippedWarning(
                configPath,
                TypeScriptPathAliasDiagnosticReadFailed,
                "it could not be read");
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
                    var totalTargets = 0;
                    var rulesTruncated = false;
                    var targetsTruncated = false;
                    var ignoredLongPattern = false;
                    var ignoredLongTarget = false;
                    foreach (var property in pathsElement.EnumerateObject())
                    {
                        if (rules.Count >= MaxTypeScriptPathAliasRules)
                        {
                            rulesTruncated = true;
                            break;
                        }

                        if (totalTargets >= MaxTypeScriptPathAliasTotalTargets)
                        {
                            targetsTruncated = true;
                            break;
                        }

                        if (property.Value.ValueKind != JsonValueKind.Array)
                            continue;

                        if (property.Name.Length > MaxTypeScriptPathAliasPatternLength)
                        {
                            ignoredLongPattern = true;
                            continue;
                        }

                        var targets = new List<string>();
                        foreach (var item in property.Value.EnumerateArray())
                        {
                            if (targets.Count >= MaxTypeScriptPathAliasTargetsPerRule
                                || totalTargets >= MaxTypeScriptPathAliasTotalTargets)
                            {
                                targetsTruncated = true;
                                break;
                            }

                            if (item.ValueKind == JsonValueKind.String
                                && !string.IsNullOrWhiteSpace(item.GetString()))
                            {
                                var target = item.GetString()!;
                                if (target.Length > MaxTypeScriptPathAliasTargetLength)
                                {
                                    ignoredLongTarget = true;
                                    continue;
                                }

                                targets.Add(target);
                                totalTargets++;
                            }
                        }

                        if (targets.Count > 0)
                            rules.Add(new TypeScriptPathAliasRule(property.Name, baseDirectory, targets));
                    }

                    if (rulesTruncated)
                    {
                        ReportTypeScriptPathAliasConfigWarningOnce(
                            configPath,
                            $"Truncated TypeScript path alias rules in config {DiagnosticSanitizer.ForPath(configPath)} to {MaxTypeScriptPathAliasRules} entries.");
                    }

                    if (targetsTruncated)
                    {
                        ReportTypeScriptPathAliasConfigWarningOnce(
                            configPath,
                            $"Truncated TypeScript path alias targets in config {DiagnosticSanitizer.ForPath(configPath)} to {MaxTypeScriptPathAliasTotalTargets} total entries and {MaxTypeScriptPathAliasTargetsPerRule} entries per rule.");
                    }

                    if (ignoredLongPattern)
                    {
                        ReportTypeScriptPathAliasConfigWarningOnce(
                            configPath,
                            $"Ignored TypeScript path alias rules in config {DiagnosticSanitizer.ForPath(configPath)} with patterns longer than {MaxTypeScriptPathAliasPatternLength} characters.");
                    }

                    if (ignoredLongTarget)
                    {
                        ReportTypeScriptPathAliasConfigWarningOnce(
                            configPath,
                            $"Ignored TypeScript path alias targets in config {DiagnosticSanitizer.ForPath(configPath)} longer than {MaxTypeScriptPathAliasTargetLength} characters.");
                    }
                }
            }

            return rules.Count == 0 && !hasBaseUrl
                ? null
                : new TypeScriptPathAliasConfig(configPath, configDirectory, baseDirectory, hasBaseUrl, SortTypeScriptPathAliasRules(rules));
        }
    }

    private static bool TryReadTypeScriptPathAliasConfigText(
        string configPath,
        ref long totalConfigBytesRead,
        out string text,
        out TypeScriptPathAliasConfigSkippedReason skippedReason)
    {
        text = string.Empty;
        skippedReason = default;

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
                skippedReason = new(TypeScriptPathAliasDiagnosticSizeLimit, $"it exceeds {MaxTypeScriptPathAliasConfigBytes} bytes");
                return false;
            }

            if (totalConfigBytesRead + stream.Length > MaxTypeScriptPathAliasTotalConfigBytes)
            {
                skippedReason = new(TypeScriptPathAliasDiagnosticSizeLimit, $"the extends chain exceeds {MaxTypeScriptPathAliasTotalConfigBytes} bytes");
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
                    skippedReason = new(TypeScriptPathAliasDiagnosticSizeLimit, $"it exceeds {MaxTypeScriptPathAliasConfigBytes} bytes");
                    return false;
                }

                totalConfigBytesRead += read;
                if (totalConfigBytesRead > MaxTypeScriptPathAliasTotalConfigBytes)
                {
                    skippedReason = new(TypeScriptPathAliasDiagnosticSizeLimit, $"the extends chain exceeds {MaxTypeScriptPathAliasTotalConfigBytes} bytes");
                    return false;
                }

                accumulator.Write(buffer, 0, read);
            }

            text = Encoding.UTF8.GetString(accumulator.ToArray());
            if (text.Length > 0 && text[0] == '\uFEFF')
                text = text[1..];
            return true;
        }
        catch (Exception ex) when (IsTypeScriptPathAliasConfigReadException(ex))
        {
            skippedReason = new(TypeScriptPathAliasDiagnosticReadFailed, "it could not be read");
            return false;
        }
    }

    private static string FormatTypeScriptPathAliasConfigSkippedMessage(
        string configPath,
        TypeScriptPathAliasConfigSkippedReason reason) =>
        FormatTypeScriptPathAliasConfigSkippedMessage(configPath, reason.Code, reason.Reason);

    private static string FormatTypeScriptPathAliasConfigSkippedMessage(string configPath, string code, string reason) =>
        $"Skipped TypeScript path alias config {DiagnosticSanitizer.ForPath(configPath)} [{DiagnosticSanitizer.ForMessage(code)}] because {DiagnosticSanitizer.ForMessage(reason)}.";

    private static void ReportTypeScriptPathAliasConfigSkippedWarning(
        string configPath,
        TypeScriptPathAliasConfigSkippedReason reason) =>
        ReportTypeScriptPathAliasConfigSkippedWarning(configPath, reason.Code, reason.Reason);

    private static void ReportTypeScriptPathAliasConfigSkippedWarning(string configPath, string code, string reason)
        => ReportTypeScriptPathAliasWarningOnce(
            FormatTypeScriptPathAliasConfigSkippedMessage(configPath, code, reason),
            $"{configPath}\n{code}\n{reason}");

    private static bool IsTypeScriptPathAliasConfigReadException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or NotSupportedException;

    private static void ReportTypeScriptPathAliasConfigWarningOnce(string configPath, string message)
        => ReportTypeScriptPathAliasWarningOnce(message, $"{configPath}\n{message}");

    private static void ReportTypeScriptPathAliasWarningOnce(string message, string? dedupeKey = null)
    {
        lock (TypeScriptPathAliasWarningLock)
        {
            if (!TypeScriptPathAliasReportedWarnings.Add(dedupeKey ?? message))
                return;
        }

        CommandErrorWriter.WriteStderr("cdidx: warning: " + message);
    }

    private static IReadOnlyList<TypeScriptPathAliasRule> SortTypeScriptPathAliasRules(IReadOnlyList<TypeScriptPathAliasRule> rules)
    {
        var indexedRules = new List<(TypeScriptPathAliasRule Rule, int Index, int WildcardRank, int LiteralLength)>(rules.Count);
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            indexedRules.Add((
                rule,
                i,
                rule.Pattern.Contains('*', StringComparison.Ordinal) ? 1 : 0,
                GetTypeScriptPathAliasLiteralLength(rule.Pattern)));
        }

        indexedRules.Sort(CompareTypeScriptPathAliasRules);

        var sortedRules = new List<TypeScriptPathAliasRule>(indexedRules.Count);
        foreach (var (rule, _, _, _) in indexedRules)
            sortedRules.Add(rule);
        return sortedRules;
    }

    private static int CompareTypeScriptPathAliasRules(
        (TypeScriptPathAliasRule Rule, int Index, int WildcardRank, int LiteralLength) left,
        (TypeScriptPathAliasRule Rule, int Index, int WildcardRank, int LiteralLength) right)
    {
        var wildcardComparison = left.WildcardRank.CompareTo(right.WildcardRank);
        if (wildcardComparison != 0)
            return wildcardComparison;

        var literalLengthComparison = right.LiteralLength.CompareTo(left.LiteralLength);
        return literalLengthComparison != 0
            ? literalLengthComparison
            : left.Index.CompareTo(right.Index);
    }

    private static int GetTypeScriptPathAliasLiteralLength(string pattern)
    {
        var count = 0;
        foreach (var ch in pattern)
        {
            if (ch != '*')
                count++;
        }

        return count;
    }

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

    private static bool TrySubstituteTypeScriptPathAliasTarget(string target, string wildcard, out string substituted)
    {
        substituted = string.Empty;
        var starCount = target.Count(static ch => ch == '*');
        if (starCount == 0)
        {
            substituted = target;
            return true;
        }

        var substitutedLength = (long)target.Length - starCount + (long)wildcard.Length * starCount;
        if (substitutedLength > MaxTypeScriptPathAliasSubstitutedTargetLength)
            return false;

        substituted = target.Replace("*", wildcard, StringComparison.Ordinal);
        return true;
    }

    private static bool TryResolveTypeScriptModuleFile(
        string candidate,
        ref int remainingCandidateBudget,
        out string resolvedPath,
        out bool budgetExhausted)
    {
        budgetExhausted = false;
        foreach (var path in EnumerateTypeScriptModuleCandidates(candidate))
        {
            if (remainingCandidateBudget <= 0)
            {
                budgetExhausted = true;
                break;
            }

            remainingCandidateBudget--;
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

        foreach (var extension in TypeScriptModuleCandidateExtensions)
            yield return candidate + extension;

        foreach (var extension in TypeScriptModuleCandidateExtensions)
            yield return Path.Combine(candidate, "index" + extension);
    }

    private static string NormalizeTypeScriptResolvedModulePath(string projectDirectory, string resolvedPath)
    {
        var relativePath = FileIndexer.GetRelativePathFromDirectory(projectDirectory, Path.GetFullPath(resolvedPath));
        return FileIndexer.NormalizePathSeparators(relativePath);
    }
}
