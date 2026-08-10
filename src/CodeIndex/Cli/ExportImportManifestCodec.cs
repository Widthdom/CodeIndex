using System.IO.Compression;
using System.Text.Json;
using CodeIndex.Database;
using CodeIndex.Diagnostics;

namespace CodeIndex.Cli;

internal static class ExportImportManifestCodec
{
    internal static bool TryDeserialize(
        ReadOnlySpan<byte> utf8Json,
        JsonSerializerOptions jsonOptions,
        out ExportImportCommandRunner.ExportManifest manifest,
        out string message)
    {
        if (JsonExceedsDepthLimit(utf8Json, ExportImportCommandRunner.MaxImportManifestJsonDepth))
        {
            manifest = null!;
            message = $"manifest.json exceeds the JSON depth limit of {ExportImportCommandRunner.MaxImportManifestJsonDepth}";
            return false;
        }

        try
        {
            var parsedManifest = BoundedJson.Deserialize<ExportImportCommandRunner.ExportManifest>(
                utf8Json,
                ExportImportCommandRunner.MaxImportManifestBytes,
                CreateImportManifestJsonOptions(jsonOptions));
            if (parsedManifest == null)
            {
                manifest = null!;
                message = "manifest.json did not contain an object";
                return false;
            }

            manifest = parsedManifest;
            message = string.Empty;
            return true;
        }
        catch (JsonException)
        {
            manifest = null!;
            message = "manifest.json is not valid export manifest JSON";
            return false;
        }
        catch (NotSupportedException)
        {
            manifest = null!;
            message = "manifest.json contains unsupported export manifest JSON";
            return false;
        }
        catch (InvalidDataException ex)
        {
            manifest = null!;
            message = ExportImportCommandRunner.FormatImportManifestReadException(ex);
            return false;
        }
    }

    internal static bool TryValidateEntrySize(ZipArchiveEntry manifestEntry, out string message)
    {
        if (manifestEntry.Length < 0 || manifestEntry.CompressedLength < 0)
        {
            message = "archive manifest.json size metadata is invalid";
            return false;
        }

        if (manifestEntry.Length > ExportImportCommandRunner.MaxImportManifestBytes)
        {
            message = $"archive manifest.json is too large: {ConsoleUi.FormatBytes(manifestEntry.Length)} uncompressed exceeds the import limit of {ConsoleUi.FormatBytes(ExportImportCommandRunner.MaxImportManifestBytes)}";
            return false;
        }

        if (manifestEntry.CompressedLength > ExportImportCommandRunner.MaxImportManifestBytes)
        {
            message = $"archive manifest.json is too large: {ConsoleUi.FormatBytes(manifestEntry.CompressedLength)} compressed exceeds the import limit of {ConsoleUi.FormatBytes(ExportImportCommandRunner.MaxImportManifestBytes)}";
            return false;
        }

        message = string.Empty;
        return true;
    }

    internal static bool TryValidateHeader(ExportImportCommandRunner.ExportManifest manifest, out string message)
    {
        if (!string.Equals(manifest.FormatVersion, "1", StringComparison.Ordinal))
        {
            message = $"unsupported format_version `{manifest.FormatVersion}`";
            return false;
        }

        if (manifest.UserVersion < 0 || (manifest.UserVersion & ~DbContext.CurrentSchemaVersion) != 0)
        {
            message = $"unsupported user_version `{manifest.UserVersion}`";
            return false;
        }

        if (!IsSha256Hex(manifest.DatabaseSha256))
        {
            message = "database_sha256 is missing or invalid";
            return false;
        }

        if (!ValidateNonNegativeManifestLong(manifest.FileCount, "file_count", out message)
            || !ValidateNonNegativeManifestLong(manifest.ChunkCount, "chunk_count", out message)
            || !ValidateNonNegativeManifestLong(manifest.SymbolCount, "symbol_count", out message)
            || !ValidateNonNegativeManifestLong(manifest.ReferenceCount, "reference_count", out message)
            || !ValidateNonNegativeManifestLong(manifest.UnknownExtensionFileCount, "unknown_extension_file_count", out message))
        {
            return false;
        }

        if (!ValidateNonNegativeManifestInt(manifest.CodeIndexMetaSchemaVersion, "codeindex_meta_schema_version", out message)
            || !ValidateNonNegativeManifestInt(manifest.CSharpSymbolNameContractVersion, "csharp_symbol_name_contract_version", out message)
            || !ValidateNonNegativeManifestInt(manifest.SqlGraphContractVersion, "sql_graph_contract_version", out message)
            || !ValidateNonNegativeManifestInt(manifest.HotspotFamilyVersion, "hotspot_family_version", out message)
            || !ValidateNonNegativeManifestInt(manifest.UnknownExtensionFilePathLimit, "unknown_extension_file_path_limit", out message)
            || !ValidateNonNegativeManifestInt(manifest.UnknownExtensionFileSampleCount, "unknown_extension_file_sample_count", out message)
            || !ValidateNonNegativeManifestInt(manifest.UnknownExtensionFileSampleLimit, "unknown_extension_file_sample_limit", out message))
        {
            return false;
        }

        if (manifest.UnknownExtensionFileSampleCount.HasValue)
        {
            var sampleLength = manifest.UnknownExtensionFiles?.Length ?? 0;
            if (manifest.UnknownExtensionFileSampleCount.Value != sampleLength)
            {
                message = "unknown_extension_file_sample_count must match unknown_extension_files length";
                return false;
            }
        }

        if (manifest.UnknownExtensionFileSampleCount.HasValue
            && manifest.UnknownExtensionFileSampleLimit.HasValue
            && manifest.UnknownExtensionFileSampleCount.Value > manifest.UnknownExtensionFileSampleLimit.Value)
        {
            message = "unknown_extension_file_sample_count exceeds unknown_extension_file_sample_limit";
            return false;
        }

        if (manifest.UnknownExtensionFiles is { Length: > ExportImportCommandRunner.ManifestUnknownExtensionFileLimit })
        {
            message = $"unknown_extension_files exceeds the manifest limit of {ExportImportCommandRunner.ManifestUnknownExtensionFileLimit}";
            return false;
        }

        if (manifest.UnknownExtensionFiles != null)
        {
            var totalPathChars = 0;
            foreach (var path in manifest.UnknownExtensionFiles)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    message = "unknown_extension_files contains an empty path";
                    return false;
                }

                if (path.Length > ExportImportCommandRunner.ManifestUnknownExtensionPathCharLimit)
                {
                    message = $"unknown_extension_files contains a path longer than {ExportImportCommandRunner.ManifestUnknownExtensionPathCharLimit} characters";
                    return false;
                }

                totalPathChars += path.Length;
                if (totalPathChars > ExportImportCommandRunner.ManifestUnknownExtensionFilesTotalCharLimit)
                {
                    message = $"unknown_extension_files total path text exceeds the manifest limit of {ExportImportCommandRunner.ManifestUnknownExtensionFilesTotalCharLimit} characters";
                    return false;
                }
            }
        }

        if (!TryValidateIncompleteReasons(manifest, out message))
            return false;

        if (manifest.Scope != null)
        {
            if (!TryValidateScope(manifest.Scope, out message))
                return false;
        }

        message = string.Empty;
        return true;
    }

    private static bool TryValidateScope(
        ExportImportCommandRunner.ArchiveExportScopeResult scope,
        out string message)
    {
        if (scope.PathPatterns == null
            || scope.ExcludePathPatterns == null
            || scope.Projects == null
            || scope.ResolvedProjectPathPatterns == null)
        {
            message = "scope arrays must not be null";
            return false;
        }
        if (scope.SourceFileCount < 0
            || scope.ExportedFileCount < 0
            || scope.ExportedFileCount > scope.SourceFileCount)
        {
            message = "scope file counts are invalid";
            return false;
        }

        var hasSelection = !string.IsNullOrWhiteSpace(scope.Lang)
            || scope.PathPatterns.Count > 0
            || scope.ExcludePathPatterns.Count > 0
            || scope.Projects.Count > 0
            || !string.IsNullOrWhiteSpace(scope.Solution)
            || scope.ExcludeTests;
        if (scope.Scoped != hasSelection)
        {
            message = "scope.scoped does not match the recorded selection metadata";
            return false;
        }
        if (scope.RepresentsEntireSourceDatabase == true
            && (scope.Scoped || scope.SourceFileCount != scope.ExportedFileCount))
        {
            message = "scope cannot represent the entire source database when filters or omitted files are recorded";
            return false;
        }

        var values = scope.PathPatterns
            .Concat(scope.ExcludePathPatterns)
            .Concat(scope.Projects)
            .Concat(scope.ResolvedProjectPathPatterns)
            .ToList();
        if (scope.Solution != null)
            values.Add(scope.Solution);
        if (values.Count > ExportImportCommandRunner.MaxArchiveScopeValues * 2)
        {
            message = $"scope contains more than {ExportImportCommandRunner.MaxArchiveScopeValues * 2} values";
            return false;
        }

        var totalChars = 0;
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                message = "scope contains an empty value";
                return false;
            }
            if (value.Length > ExportImportCommandRunner.MaxArchiveScopeValueChars)
            {
                message = $"scope contains a value longer than {ExportImportCommandRunner.MaxArchiveScopeValueChars} characters";
                return false;
            }
            totalChars += value.Length;
            if (totalChars > ExportImportCommandRunner.MaxArchiveScopeTotalChars * 2)
            {
                message = $"scope value text exceeds {ExportImportCommandRunner.MaxArchiveScopeTotalChars * 2} characters";
                return false;
            }
        }

        message = string.Empty;
        return true;
    }

    private static bool TryValidateIncompleteReasons(
        ExportImportCommandRunner.ExportManifest manifest,
        out string message)
    {
        if (manifest.IndexIncompleteReasons is not { } reasons)
        {
            message = string.Empty;
            return true;
        }
        if (reasons.Length > ExportImportCommandRunner.MaxArchiveIncompleteReasons)
        {
            message = $"index_incomplete_reasons exceeds the manifest limit of {ExportImportCommandRunner.MaxArchiveIncompleteReasons}";
            return false;
        }

        var totalChars = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reason in reasons)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                message = "index_incomplete_reasons contains an empty reason";
                return false;
            }
            if (reason.Length > ExportImportCommandRunner.MaxArchiveIncompleteReasonChars)
            {
                message = $"index_incomplete_reasons contains a reason longer than {ExportImportCommandRunner.MaxArchiveIncompleteReasonChars} characters";
                return false;
            }
            totalChars += reason.Length;
            if (totalChars > ExportImportCommandRunner.MaxArchiveIncompleteReasonsTotalChars)
            {
                message = $"index_incomplete_reasons exceeds {ExportImportCommandRunner.MaxArchiveIncompleteReasonsTotalChars} total characters";
                return false;
            }
            if (!seen.Add(reason))
            {
                message = "index_incomplete_reasons contains a duplicate reason";
                return false;
            }
        }

        if (manifest.IndexComplete == true && reasons.Length > 0)
        {
            message = "index_complete cannot be true when index_incomplete_reasons are present";
            return false;
        }

        message = string.Empty;
        return true;
    }

    internal static JsonSerializerOptions CreateImportManifestJsonOptions(JsonSerializerOptions jsonOptions)
        => new(jsonOptions) { MaxDepth = ExportImportCommandRunner.MaxImportManifestJsonDepth };

    internal static bool JsonExceedsDepthLimit(ReadOnlySpan<byte> json, int maxDepth)
    {
        var depth = 0;
        var inString = false;

        for (var i = 0; i < json.Length; i++)
        {
            var value = json[i];
            if (inString)
            {
                if (value == (byte)'\\')
                {
                    i++;
                    continue;
                }

                if (value == (byte)'"')
                    inString = false;
                continue;
            }

            if (value == (byte)'"')
            {
                inString = true;
                continue;
            }

            if (value is (byte)'{' or (byte)'[')
            {
                depth++;
                if (depth > maxDepth)
                    return true;
                continue;
            }

            if (value is (byte)'}' or (byte)']')
                depth = Math.Max(0, depth - 1);
        }

        return false;
    }

    private static bool ValidateNonNegativeManifestLong(long? value, string fieldName, out string message)
    {
        if (value is < 0)
        {
            message = $"{fieldName} must be non-negative";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private static bool ValidateNonNegativeManifestInt(int? value, string fieldName, out string message)
    {
        if (value is < 0)
        {
            message = $"{fieldName} must be non-negative";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private static bool IsSha256Hex(string? value)
    {
        if (value == null || value.Length != 64)
            return false;

        foreach (var ch in value)
        {
            if (!char.IsAsciiHexDigit(ch))
                return false;
        }

        return true;
    }
}
