using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;

namespace CodeIndex.Mcp;

public partial class McpServer : IDisposable
{


    private static JsonNode HandleResourceTemplatesList(JsonNode? id, JsonNode? templateParams)
    {
        if (templateParams is not null && templateParams is not JsonObject)
        {
            return CreateErrorResponse(hasId: true, id: id, code: -32602,
                message: "resources/templates/list params must be an object.",
                category: McpErrorEnvelope.CategoryInvalidArgument,
                suggestion: "Pass an empty params object or omit params.",
                retrySafe: false);
        }

        if (templateParams?["cursor"] is not null)
        {
            return CreateErrorResponse(hasId: true, id: id, code: -32602,
                message: "resources/templates/list does not have another page.",
                category: McpErrorEnvelope.CategoryInvalidArgument,
                suggestion: "Omit params.cursor; the complete resource template catalog fits in one response.",
                retrySafe: false);
        }

        return CreateSuccessResponse(true, id, new JsonObject
        {
            ["resourceTemplates"] = new JsonArray
            {
                new JsonObject
                {
                    ["uriTemplate"] = "cdidx://file-path/{path}",
                    ["name"] = "indexed-file",
                    ["title"] = "Indexed repository file",
                    ["description"] = "Read one indexed, non-generated file by its exact repository-relative path. The template-only file-path resolver decodes the URI-template value, validates it as a relative path, and returns the canonical cdidx://file resource identity.",
                },
            },
        });
    }

    private JsonNode HandleResourcesList(JsonNode? id, JsonNode? listParams)
    {
        var filterError = ValidateResourceListFilters(id, listParams, out var filters);
        if (filterError is not null)
            return filterError;
        var filterFingerprint = ComputeResourceListFilterFingerprint(filters);

        var requestedMaxBytes = DefaultResourceListMaxBytes;
        if (listParams?["maxBytes"] is JsonNode maxBytesNode)
        {
            if (maxBytesNode is not JsonValue maxBytesValue
                || !maxBytesValue.TryGetValue<int>(out requestedMaxBytes)
                || requestedMaxBytes < MinResourceListMaxBytes
                || requestedMaxBytes > MaxResourceListMaxBytes)
            {
                return CreateResourcesListMaxBytesError(id);
            }
        }
        var effectiveMaxBytes = Math.Min(requestedMaxBytes, GetMaxResponseBytes());
        var activeTransportMaxResponseBytes = Volatile.Read(ref _activeTransportMaxResponseBytes);
        if (activeTransportMaxResponseBytes > 0)
            effectiveMaxBytes = Math.Min(effectiveMaxBytes, activeTransportMaxResponseBytes);
        if (_currentBatchResponseItemMaxBytes.Value is { } batchResponseItemMaxBytes)
            effectiveMaxBytes = Math.Min(effectiveMaxBytes, batchResponseItemMaxBytes);

        long? afterFileId = null;
        long? expectedGeneration = null;
        var legacyOffset = 0;
        if (listParams?["cursor"] is JsonNode cursorNode)
        {
            if (cursorNode is not JsonValue cursorValue
                || !cursorValue.TryGetValue<string>(out var cursor))
            {
                return CreateResourcesListCursorError(id);
            }

            if (cursor.Length > MaxResourceListCursorChars)
                return CreateResourcesListCursorError(id);

            if (int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedLegacyOffset))
            {
                if (parsedLegacyOffset < 0 || parsedLegacyOffset > MaxMcpPaginationOffset)
                    return CreateResourcesListCursorError(id);
                if (parsedLegacyOffset != 0)
                    return CreateResourcesListRestartError(id);
            }
            else if (TryDecodeResourceListCursor(cursor, out var decodedCursor))
            {
                if ((decodedCursor.HasFilterFingerprint
                        && decodedCursor.FilterFingerprint != filterFingerprint)
                    || (!decodedCursor.HasFilterFingerprint && !filters.IsDefault))
                {
                    return CreateResourcesListFilterMismatchError(id);
                }
                afterFileId = decodedCursor.AfterFileId;
                expectedGeneration = decodedCursor.Generation;
            }
            else
            {
                return CreateResourcesListCursorError(id);
            }
        }

        return WithDbReader(id, args: listParams, reader =>
        {
            var resourcePage = reader.ListResourceFiles(
                limit: ResourceListPageSize + 1,
                afterFileId: afterFileId,
                expectedGeneration: expectedGeneration,
                legacyOffset: legacyOffset,
                pathPatterns: filters.PathPatterns,
                lang: filters.Language,
                includeGenerated: filters.IncludeGenerated);
            if (resourcePage.GenerationTrackingUnavailable)
                return CreateResourcesListGenerationUnavailableError(id);
            if (resourcePage.CursorRestartRequired)
                return CreateResourcesListRestartError(id);

            var page = resourcePage.Files.Take(ResourceListPageSize).ToArray();
            var resources = new JsonArray();
            var reservedResponse = CreateResourceListResponse(
                id,
                resources: [],
                generation: long.MaxValue,
                lastConsumedFileId: long.MaxValue,
                filterFingerprint: ulong.MaxValue,
                hasContinuation: true,
                requestedMaxBytes: MaxResourceListMaxBytes,
                effectiveMaxBytes: MaxResourceListMaxBytes,
                candidatesConsumed: ResourceListPageSize,
                uriTooLongCount: ResourceListPageSize,
                resourceExceedsMaxBytesCount: ResourceListPageSize,
                byteBudgetReached: true);
            _ = TryMeasureJsonUtf8BytesWithinLimit(
                reservedResponse,
                _jsonOptions,
                int.MaxValue,
                out var reservedResponseBytes);
            if (reservedResponseBytes > effectiveMaxBytes)
                return CreateResourcesListEffectiveMaxBytesError(id, requestedMaxBytes, effectiveMaxBytes);

            var acceptedResourceBytes = 0L;
            var candidatesConsumed = 0;
            var uriTooLongCount = 0;
            var resourceExceedsMaxBytesCount = 0;
            var byteBudgetReached = false;
            var stoppedForByteBudget = false;
            long? lastConsumedFileId = null;
            foreach (var file in page)
            {
                var uri = BuildResourceUri(file.Path);
                if (uri.Length > McpBoundedText.MaxResourceUriChars)
                {
                    uriTooLongCount++;
                    candidatesConsumed++;
                    lastConsumedFileId = file.Id;
                    continue;
                }

                var resource = new JsonObject
                {
                    ["uri"] = uri,
                    ["name"] = file.Path,
                    ["description"] = $"{file.Path} ({file.Lang ?? "unknown"}, {file.Lines} lines)",
                    ["mimeType"] = GetResourceMimeType(file.Lang),
                };
                var resourceFitsAlone = TryMeasureJsonUtf8BytesWithinLimit(
                    resource,
                    _jsonOptions,
                    effectiveMaxBytes,
                    out var resourceBytes);
                var commaBytes = resources.Count == 0 ? 0 : 1;
                var resourceFitsEmptyPage = resourceFitsAlone
                    && reservedResponseBytes + resourceBytes <= effectiveMaxBytes;
                var resourceFitsPage = resourceFitsEmptyPage
                    && reservedResponseBytes + acceptedResourceBytes + commaBytes + resourceBytes <= effectiveMaxBytes;
                if (!resourceFitsPage)
                {
                    byteBudgetReached = true;
                    if (resourceFitsEmptyPage || resources.Count > 0)
                    {
                        stoppedForByteBudget = true;
                        break;
                    }

                    // Consume resources that cannot fit even on an empty page so the cursor cannot livelock.
                    // 空ページにも収まらない resource は消費・報告し、cursor の livelock を防ぐ。
                    resourceExceedsMaxBytesCount++;
                    candidatesConsumed++;
                    lastConsumedFileId = file.Id;
                    continue;
                }

                resources.Add(resource);
                acceptedResourceBytes += commaBytes + resourceBytes;
                candidatesConsumed++;
                lastConsumedFileId = file.Id;
            }

            var hasContinuation = stoppedForByteBudget || resourcePage.Files.Count > ResourceListPageSize;
            var response = CreateResourceListResponse(
                id,
                resources,
                resourcePage.Generation,
                lastConsumedFileId,
                filterFingerprint,
                hasContinuation,
                requestedMaxBytes,
                effectiveMaxBytes,
                candidatesConsumed,
                uriTooLongCount,
                resourceExceedsMaxBytesCount,
                byteBudgetReached);

            if (!TryMeasureJsonUtf8BytesWithinLimit(response, _jsonOptions, effectiveMaxBytes, out _))
                return CreateResourcesListEffectiveMaxBytesError(id, requestedMaxBytes, effectiveMaxBytes);
            return response;
        });
    }

    private static JsonObject CreateResourcesListMaxBytesError(JsonNode? id)
        => CreateErrorResponse(hasId: true, id: id, code: -32602,
            message: $"resources/list maxBytes must be between {MinResourceListMaxBytes} and {MaxResourceListMaxBytes}.",
            category: McpErrorEnvelope.CategoryInvalidArgument,
            suggestion: "Use an integer params.maxBytes within the documented range, or omit it to use the default.",
            retrySafe: false,
            extraData: new JsonObject
            {
                ["min_max_bytes"] = MinResourceListMaxBytes,
                ["max_max_bytes"] = MaxResourceListMaxBytes,
                ["default_max_bytes"] = DefaultResourceListMaxBytes,
            });

    private static JsonObject CreateResourcesListEffectiveMaxBytesError(
        JsonNode? id,
        int requestedMaxBytes,
        int effectiveMaxBytes)
        => CreateErrorResponse(hasId: true, id: id, code: -32602,
            message: "resources/list response metadata does not fit within the effective byte limit.",
            category: McpErrorEnvelope.CategoryInvalidArgument,
            suggestion: "Raise the MCP response byte limit or request a larger params.maxBytes value.",
            retrySafe: false,
            extraData: new JsonObject
            {
                ["requested_max_bytes"] = requestedMaxBytes,
                ["effective_max_bytes"] = effectiveMaxBytes,
            });

    private static JsonObject CreateResourceListResponse(
        JsonNode? id,
        JsonArray resources,
        long generation,
        long? lastConsumedFileId,
        ulong filterFingerprint,
        bool hasContinuation,
        int requestedMaxBytes,
        int effectiveMaxBytes,
        int candidatesConsumed,
        int uriTooLongCount,
        int resourceExceedsMaxBytesCount,
        bool byteBudgetReached)
    {
        var result = new JsonObject
        {
            ["resources"] = resources,
            ["_meta"] = new JsonObject
            {
                ["discovery_contract"] = CreateResourceListDiscoveryContract(),
                ["response_controls"] = CreateResourceListResponseControls(
                    requestedMaxBytes,
                    effectiveMaxBytes,
                    candidatesConsumed,
                    resources.Count,
                    uriTooLongCount,
                    resourceExceedsMaxBytesCount,
                    byteBudgetReached,
                    hasContinuation),
            },
        };
        if (hasContinuation && lastConsumedFileId is not null)
            result["nextCursor"] = EncodeResourceListCursor(generation, lastConsumedFileId.Value, filterFingerprint);
        return CreateSuccessResponse(true, id, result);
    }

    private static JsonObject CreateResourceListDiscoveryContract()
        => new()
        {
            ["accepted_params"] = new JsonArray
            {
                "cursor",
                "path",
                "lang",
                "includeGenerated",
                "maxBytes",
            },
            ["filter_params"] = new JsonArray
            {
                "path",
                "lang",
                "includeGenerated",
            },
            ["path_filter"] = new JsonObject
            {
                ["type"] = "string_or_array",
                ["max_items"] = MaxResourceListPathFilterCount,
                ["max_characters_per_item"] = MaxResourceListPathFilterChars,
                ["max_wildcards_per_item"] = MaxResourceListPathFilterWildcards,
            },
            ["language_filter"] = new JsonObject
            {
                ["type"] = "normalized_language_name_or_alias",
                ["max_characters"] = MaxResourceListLanguageFilterChars,
            },
            ["generated_files_excluded_by_default"] = true,
            ["max_bytes"] = new JsonObject
            {
                ["scope"] = "json_rpc_envelope",
                ["minimum"] = MinResourceListMaxBytes,
                ["default"] = DefaultResourceListMaxBytes,
                ["maximum"] = MaxResourceListMaxBytes,
            },
            ["pagination"] = new JsonObject
            {
                ["cursor_param"] = "params.cursor",
                ["next_cursor_field"] = "result.nextCursor",
                ["cursor_is_opaque"] = true,
                ["cursor_binds_index_generation"] = true,
                ["cursor_binds_filters"] = true,
            },
        };

    private static JsonObject CreateResourceListResponseControls(
        int requestedMaxBytes,
        int effectiveMaxBytes,
        int candidatesConsumed,
        int resourcesReturned,
        int uriTooLongCount,
        int resourceExceedsMaxBytesCount,
        bool byteBudgetReached,
        bool hasContinuation)
        => new()
        {
            ["requested_max_bytes"] = requestedMaxBytes,
            ["effective_max_bytes"] = effectiveMaxBytes,
            ["page_item_limit"] = ResourceListPageSize,
            ["resource_candidates_consumed"] = candidatesConsumed,
            ["resources_returned"] = resourcesReturned,
            ["omitted_resource_count"] = uriTooLongCount + resourceExceedsMaxBytesCount,
            ["omitted_resource_reason_counts"] = new JsonObject
            {
                ["resource_uri_too_long"] = uriTooLongCount,
                ["resource_exceeds_max_bytes"] = resourceExceedsMaxBytesCount,
            },
            ["byte_budget_reached"] = byteBudgetReached,
            ["continuation_reason"] = hasContinuation
                ? byteBudgetReached ? "byte_budget" : "item_limit"
                : "completed",
        };

    private static JsonObject CreateResourcesListCursorError(JsonNode? id)
        => CreateErrorResponse(hasId: true, id: id, code: -32602,
            message: "resources/list cursor is invalid or unsupported.",
            category: McpErrorEnvelope.CategoryInvalidArgument,
            suggestion: "Use the `nextCursor` value returned by the previous resources/list response, or omit params.cursor to start from the first page.",
            retrySafe: false,
            extraData: new JsonObject
            {
                ["max_cursor_length"] = MaxResourceListCursorChars,
                ["max_legacy_pagination_offset"] = MaxMcpPaginationOffset,
            });

    private static JsonObject CreateResourcesListFilterMismatchError(JsonNode? id)
        => CreateErrorResponse(hasId: true, id: id, code: -32602,
            message: "The resources/list filters do not match the supplied cursor.",
            category: McpErrorEnvelope.CategoryInvalidArgument,
            suggestion: "Continue with the same path, lang, and includeGenerated filters used to create the cursor, or omit params.cursor to start a new filtered listing.",
            retrySafe: false,
            extraData: new JsonObject
            {
                ["reason"] = "resources_list_filters_changed",
                ["restart_required"] = true,
            });

    private static JsonObject CreateResourcesListRestartError(JsonNode? id)
        => CreateErrorResponse(hasId: true, id: id, code: McpErrorEnvelope.CodeIndexStale,
            message: "The indexed file set changed after this resources/list cursor was issued.",
            category: McpErrorEnvelope.CategoryIndexStale,
            suggestion: "Omit params.cursor and restart resources/list from the first page.",
            retrySafe: false,
            extraData: new JsonObject
            {
                ["reason"] = "resources_list_generation_changed",
                ["restart_required"] = true,
            });

    private static JsonObject CreateResourcesListGenerationUnavailableError(JsonNode? id)
        => CreateErrorResponse(hasId: true, id: id, code: McpErrorEnvelope.CodeIndexStale,
            message: "This database cannot prove a stable resources/list generation.",
            category: McpErrorEnvelope.CategoryIndexStale,
            suggestion: "Open the database on writable storage and run `cdidx index <projectPath>` with the current cdidx to install generation tracking. Use an `immutable=1` URI only for a snapshot guaranteed not to change.",
            retrySafe: false,
            extraData: new JsonObject
            {
                ["reason"] = "resources_list_generation_unavailable",
                ["migration_required"] = true,
                ["restart_required"] = false,
            });

    private static JsonObject? ValidateResourceListFilters(
        JsonNode? id,
        JsonNode? listParams,
        out ResourceListFilters filters)
    {
        filters = ResourceListFilters.Default;
        if (listParams is null)
            return null;
        if (listParams is not JsonObject obj)
        {
            return CreateResourcesListFilterError(
                id,
                parameter: "params",
                message: "resources/list params must be an object.",
                suggestion: "Pass an object containing optional cursor, maxBytes, path, lang, and includeGenerated members.");
        }

        var pathPatterns = new List<string>();
        if (obj.TryGetPropertyValue("path", out var pathNode) && pathNode is not null)
        {
            if (pathNode is JsonValue pathValue
                && pathValue.TryGetValue<string>(out var scalarPath))
            {
                pathPatterns.Add(scalarPath);
            }
            else if (pathNode is JsonArray pathArray)
            {
                if (pathArray.Count > MaxResourceListPathFilterCount)
                {
                    return CreateResourcesListFilterError(
                        id,
                        parameter: "path",
                        message: $"resources/list params.path accepts at most {MaxResourceListPathFilterCount} values.",
                        suggestion: "Reduce the path filter array and retry.",
                        extraData: new JsonObject
                        {
                            ["max_item_count"] = MaxResourceListPathFilterCount,
                            ["actual_item_count"] = pathArray.Count,
                        });
                }

                foreach (var item in pathArray)
                {
                    if (item is not JsonValue itemValue
                        || !itemValue.TryGetValue<string>(out var pathText))
                    {
                        return CreateResourcesListFilterError(
                            id,
                            parameter: "path",
                            message: "resources/list params.path array items must be strings.",
                            suggestion: "Use a single path string or an array containing only non-empty path strings.");
                    }
                    pathPatterns.Add(pathText);
                }
            }
            else
            {
                return CreateResourcesListFilterError(
                    id,
                    parameter: "path",
                    message: "resources/list params.path must be a string or an array of strings.",
                    suggestion: "Use repository-relative path text or bounded glob-style path patterns.");
            }
        }

        for (var i = 0; i < pathPatterns.Count; i++)
        {
            var pathPattern = pathPatterns[i];
            if (string.IsNullOrWhiteSpace(pathPattern)
                || pathPattern.Length > MaxResourceListPathFilterChars)
            {
                return CreateResourcesListFilterError(
                    id,
                    parameter: "path",
                    message: $"resources/list params.path values must contain text and be at most {MaxResourceListPathFilterChars} characters.",
                    suggestion: "Use a shorter non-empty repository-relative path filter.",
                    extraData: new JsonObject
                    {
                        ["max_value_length"] = MaxResourceListPathFilterChars,
                        ["item_index"] = i,
                        ["actual_value_length"] = pathPattern?.Length ?? 0,
                    });
            }

            if (CountUnescapedResourcePathWildcards(pathPattern) > MaxResourceListPathFilterWildcards)
            {
                return CreateResourcesListFilterError(
                    id,
                    parameter: "path",
                    message: $"resources/list params.path values may contain at most {MaxResourceListPathFilterWildcards} wildcard operators.",
                    suggestion: "Split the path filters or use a narrower directory prefix.",
                    extraData: new JsonObject
                    {
                        ["max_wildcard_count"] = MaxResourceListPathFilterWildcards,
                        ["item_index"] = i,
                    });
            }
        }

        string? language = null;
        if (obj.TryGetPropertyValue("lang", out var languageNode) && languageNode is not null)
        {
            if (languageNode is not JsonValue languageValue
                || !languageValue.TryGetValue<string>(out var parsedLanguage)
                || string.IsNullOrWhiteSpace(parsedLanguage)
                || parsedLanguage.Length > MaxResourceListLanguageFilterChars)
            {
                return CreateResourcesListFilterError(
                    id,
                    parameter: "lang",
                    message: $"resources/list params.lang must be a non-empty string of at most {MaxResourceListLanguageFilterChars} characters.",
                    suggestion: "Use an indexed language name or alias such as `csharp`, `cs`, `typescript`, or `python`.",
                    extraData: new JsonObject
                    {
                        ["max_value_length"] = MaxResourceListLanguageFilterChars,
                    });
            }
            language = DbReader.NormalizeQueryLanguage(parsedLanguage);
            if (string.IsNullOrEmpty(language))
            {
                return CreateResourcesListFilterError(
                    id,
                    parameter: "lang",
                    message: "resources/list params.lang must contain at least one letter or digit after normalization.",
                    suggestion: "Use an indexed language name or alias such as `csharp`, `cs`, `typescript`, or `python`.");
            }
        }

        var includeGenerated = false;
        if (obj.TryGetPropertyValue("includeGenerated", out var generatedNode) && generatedNode is not null)
        {
            if (generatedNode is not JsonValue generatedValue
                || !generatedValue.TryGetValue<bool>(out includeGenerated))
            {
                return CreateResourcesListFilterError(
                    id,
                    parameter: "includeGenerated",
                    message: "resources/list params.includeGenerated must be a boolean.",
                    suggestion: "Use true to include generated files or false to preserve the default exclusion.");
            }
        }

        filters = new ResourceListFilters(
            pathPatterns
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            language,
            includeGenerated);
        return null;
    }

    private static JsonObject CreateResourcesListFilterError(
        JsonNode? id,
        string parameter,
        string message,
        string suggestion,
        JsonObject? extraData = null)
    {
        extraData ??= new JsonObject();
        extraData["reason"] = "resource_filter_invalid";
        extraData["parameter"] = parameter;
        return CreateErrorResponse(
            hasId: true,
            id: id,
            code: -32602,
            message: message,
            category: McpErrorEnvelope.CategoryInvalidArgument,
            suggestion: suggestion,
            retrySafe: false,
            extraData: extraData);
    }

    private static ulong ComputeResourceListFilterFingerprint(ResourceListFilters filters)
    {
        var canonical = new StringBuilder("resources-list-filters-v1\n");
        canonical.Append(filters.IncludeGenerated ? "1\n" : "0\n");
        AppendFingerprintValue(canonical, filters.Language ?? string.Empty);
        var canonicalPathPatterns = filters.PathPatterns
            .Select(static pathPattern =>
                (DbReader.PathLikePatternHasWildcard(pathPattern) ? "W:" : "P:")
                + DbReader.BuildPathLikePattern(pathPattern))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        canonical.Append(canonicalPathPatterns.Length).Append('\n');
        foreach (var pathPattern in canonicalPathPatterns)
            AppendFingerprintValue(canonical, pathPattern);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return BinaryPrimitives.ReadUInt64BigEndian(hash);
    }

    private static void AppendFingerprintValue(StringBuilder builder, string value)
        => builder.Append(value.Length).Append(':').Append(value).Append('\n');

    private static int CountUnescapedResourcePathWildcards(string pathPattern)
    {
        var count = 0;
        var escaped = false;
        foreach (var ch in pathPattern)
        {
            if (escaped)
            {
                escaped = false;
                continue;
            }
            if (ch == '\\')
            {
                escaped = true;
                continue;
            }
            if (ch is '*' or '?')
                count++;
        }
        return count;
    }

    private readonly record struct ResourceListFilters(
        string[] PathPatterns,
        string? Language,
        bool IncludeGenerated)
    {
        internal static ResourceListFilters Default { get; } = new([], null, false);

        internal bool IsDefault
            => PathPatterns.Length == 0
               && Language is null
               && !IncludeGenerated;
    }

    private static string EncodeResourceListCursor(
        long generation,
        long afterFileId,
        ulong filterFingerprint)
    {
        Span<byte> payload = stackalloc byte[ResourceListCursorPayloadBytes];
        payload[0] = ResourceListCursorVersion;
        BinaryPrimitives.WriteInt64BigEndian(payload[1..9], generation);
        BinaryPrimitives.WriteInt64BigEndian(payload[9..17], afterFileId);
        BinaryPrimitives.WriteUInt64BigEndian(payload[17..25], filterFingerprint);
        return Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static bool TryDecodeResourceListCursor(string cursor, out ResourceListCursor decoded)
    {
        decoded = default;
        if (cursor.Length is not LegacyResourceListCursorChars and not MaxResourceListCursorChars
            || cursor.Any(static ch => !char.IsAsciiLetterOrDigit(ch) && ch is not '-' and not '_'))
        {
            return false;
        }

        var paddedLength = ((cursor.Length + 3) / 4) * 4;
        Span<char> base64 = stackalloc char[paddedLength];
        for (var i = 0; i < cursor.Length; i++)
        {
            base64[i] = cursor[i] switch
            {
                '-' => '+',
                '_' => '/',
                _ => cursor[i],
            };
        }
        base64[cursor.Length..].Fill('=');

        Span<byte> payload = stackalloc byte[ResourceListCursorPayloadBytes];
        if (!Convert.TryFromBase64Chars(base64, payload, out var bytesWritten)
            || (bytesWritten != LegacyResourceListCursorPayloadBytes
                && bytesWritten != ResourceListCursorPayloadBytes))
        {
            return false;
        }

        var version = payload[0];
        if ((version == LegacyResourceListCursorVersion && bytesWritten != LegacyResourceListCursorPayloadBytes)
            || (version == ResourceListCursorVersion && bytesWritten != ResourceListCursorPayloadBytes)
            || version is not LegacyResourceListCursorVersion and not ResourceListCursorVersion)
        {
            return false;
        }

        var generation = BinaryPrimitives.ReadInt64BigEndian(payload[1..9]);
        var afterFileId = BinaryPrimitives.ReadInt64BigEndian(payload[9..17]);
        if (generation < 0 || afterFileId <= 0)
            return false;

        decoded = version == ResourceListCursorVersion
            ? new ResourceListCursor(
                generation,
                afterFileId,
                BinaryPrimitives.ReadUInt64BigEndian(payload[17..25]),
                HasFilterFingerprint: true)
            : new ResourceListCursor(
                generation,
                afterFileId,
                FilterFingerprint: 0,
                HasFilterFingerprint: false);
        return true;
    }

    private readonly record struct ResourceListCursor(
        long Generation,
        long AfterFileId,
        ulong FilterFingerprint,
        bool HasFilterFingerprint);

    private JsonNode HandleResourcesRead(
        JsonNode? id,
        JsonNode? readParams,
        bool adaptForToolResult = false)
    {
        if (readParams is not null && readParams is not JsonObject)
        {
            return CreateResourceReadArgumentError(
                id,
                "params",
                "resources/read params must be an object.",
                "Pass an object containing uri and optional startLine, endLine, maxBytes, cursor, and includeGenerated members.");
        }

        var uri = TryReadStringValue(readParams?["uri"]);
        if (string.IsNullOrWhiteSpace(uri))
            return CreateErrorResponse(hasId: true, id: id, code: -32602, message: "Missing resource uri",
                category: McpErrorEnvelope.CategoryMissingParameter,
                suggestion: "resources/read requires `params.uri` from resources/list or resources/templates/list, such as `cdidx://file/src/app.cs`.",
                retrySafe: false);
        if (uri.Length > McpBoundedText.MaxResourceUriChars)
            return CreateResourceUriError(id, uri, messagePrefix: "Resource uri is too long",
                suggestion: "Use a resource URI returned by resources/list or expanded from resources/templates/list, and keep it within the documented MCP resource URI length limit.",
                retrySafe: false,
                includeLengthLimit: true);

        if (!TryParseResourceUri(uri, out var path))
            return CreateResourceUriError(id, uri, messagePrefix: "Invalid resource uri",
                suggestion: "Use a cdidx file resource URI returned by resources/list or expanded from resources/templates/list (`cdidx://file/<indexed-path>`).",
                retrySafe: false);

        if (readParams?["includeGenerated"] is JsonNode includeGeneratedNode
            && (includeGeneratedNode is not JsonValue includeGeneratedValue
                || !includeGeneratedValue.TryGetValue<bool>(out _)))
        {
            return CreateResourceReadArgumentError(
                id,
                "includeGenerated",
                "resources/read params.includeGenerated must be a boolean.",
                "Use true only when reading a generated URI returned by resources/list with includeGenerated enabled.");
        }

        if (!TryReadOptionalResourceReadInteger(readParams, "startLine", out var requestedStartLine))
            return CreateResourceReadArgumentError(id, "startLine",
                "resources/read params.startLine must be a positive integer.",
                "Pass a 1-based line number, or omit startLine to begin at line 1.");
        if (!TryReadOptionalResourceReadInteger(readParams, "endLine", out var requestedEndLine))
            return CreateResourceReadArgumentError(id, "endLine",
                "resources/read params.endLine must be a positive integer.",
                "Pass an inclusive 1-based line number greater than or equal to startLine, or omit endLine to read through the resource.");
        if (!TryReadOptionalResourceReadInteger(readParams, "maxBytes", out var requestedMaxBytes))
            return CreateResourceReadArgumentError(id, "maxBytes",
                "resources/read params.maxBytes must be an integer.",
                $"Pass a UTF-8 text budget between {MinResourceReadMaxBytes} and {MaxResourceReadMaxBytes} bytes.");
        if (!TryReadOptionalResourceReadString(readParams, "cursor", out var cursorText))
            return CreateResourceReadArgumentError(id, "cursor",
                "resources/read params.cursor must be a non-empty string.",
                "Use the nextCursor returned in result._meta, or omit cursor to start a new range.");

        if (requestedStartLine is <= 0)
            return CreateResourceReadIntegerRangeError(id, "startLine", 1, int.MaxValue, requestedStartLine.Value);
        if (requestedEndLine is <= 0)
            return CreateResourceReadIntegerRangeError(id, "endLine", 1, int.MaxValue, requestedEndLine.Value);
        if (requestedStartLine.HasValue && requestedEndLine.HasValue && requestedEndLine.Value < requestedStartLine.Value)
            return CreateResourceReadArgumentError(id, "endLine",
                "resources/read params.endLine must be greater than or equal to params.startLine.",
                "Increase endLine or start a new range with matching 1-based boundaries.");

        var maxBytes = requestedMaxBytes ?? DefaultResourceReadMaxBytes;
        if (maxBytes < MinResourceReadMaxBytes || maxBytes > MaxResourceReadMaxBytes)
            return CreateResourceReadIntegerRangeError(id, "maxBytes", MinResourceReadMaxBytes, MaxResourceReadMaxBytes, maxBytes);

        ResourceReadCursor? cursor = null;
        if (cursorText is not null)
        {
            if (requestedStartLine.HasValue || requestedEndLine.HasValue)
                return CreateResourceReadArgumentError(id, "cursor",
                    "resources/read params.cursor cannot be combined with startLine or endLine.",
                    "Continue with cursor and an optional maxBytes value, or omit cursor to start a new line range.");
            if (cursorText.Length > MaxResourceReadCursorCharacters || !TryParseResourceReadCursor(cursorText, out var parsedCursor))
                return CreateResourceReadArgumentError(id, "cursor",
                    "resources/read params.cursor is invalid or expired.",
                    "Use the exact nextCursor returned by the previous resources/read response, or omit cursor to restart the range.",
                    new JsonObject
                    {
                        ["maxCursorCharacters"] = MaxResourceReadCursorCharacters,
                    });
            cursor = parsedCursor;
        }

        return WithDbReader(id, args: readParams, reader => reader.RunInReadSnapshot(() =>
        {
            var file = reader.GetResourceFileMetadata(path);
            if (file == null)
                return CreateResourceUriError(id, uri, messagePrefix: "Resource not found",
                    suggestion: "Verify the exact indexed path through resources/templates/list or call resources/list again, then retry with a matching resource URI.",
                    retrySafe: true);

            var fingerprint = BuildResourceReadFingerprint(file.Path, file.Checksum, file.Size, file.Lines, file.Modified);
            if (cursor is { } suppliedCursor && !string.Equals(suppliedCursor.Fingerprint, fingerprint, StringComparison.Ordinal))
                return CreateResourceReadArgumentError(id, "cursor",
                    "resources/read params.cursor no longer matches the indexed resource.",
                    "The resource changed after the previous page. Omit cursor and restart the range to avoid skipped or duplicated text.",
                    new JsonObject
                    {
                        ["cursorStale"] = true,
                    });

            ResourceReadMetadataLoadedForTests?.Invoke();

            var isEmpty = file.Size >= 0
                          && DbReader.IsAffirmativelyEmptyIndexedFile(file.Lines, file.Checksum);
            var totalLines = Math.Max(0, file.Lines);
            var hasReadableLines = !isEmpty && file.Lines > 0;
            if (isEmpty && cursor.HasValue)
                return CreateResourceReadArgumentError(id, "cursor",
                    "resources/read params.cursor does not identify a readable position in this empty resource.",
                    "Omit cursor and restart the resource read without line boundaries.");

            var startLine = isEmpty ? 0 : hasReadableLines ? cursor?.Line ?? requestedStartLine ?? 1 : 1;
            var endLine = isEmpty ? 0 : hasReadableLines ? cursor?.EndLine ?? requestedEndLine ?? totalLines : 1;
            if (hasReadableLines && startLine > totalLines)
                return CreateResourceReadArgumentError(id, "startLine",
                    $"resources/read params.startLine exceeds the resource line count ({file.Lines}).",
                    "Use a startLine from resources/read result._meta or restart at line 1.",
                    new JsonObject
                    {
                        ["totalLines"] = file.Lines,
                    });
            if (hasReadableLines)
                endLine = Math.Min(endLine, totalLines);
            if (hasReadableLines && endLine < startLine)
                return CreateResourceReadArgumentError(id, "endLine",
                    "resources/read effective endLine is before startLine.",
                    "Restart the range with an endLine greater than or equal to startLine.");

            var resourceUri = BuildResourceUri(file.Path);
            var mimeType = GetResourceMimeType(file.Lang);
            var effectiveMaxBytes = GetEffectiveResourceReadMaxBytes(
                id,
                resourceUri,
                mimeType,
                maxBytes,
                adaptForToolResult);
            if (effectiveMaxBytes < MinResourceReadMaxBytes)
                return CreateErrorResponse(hasId: true, id: id, code: -32603,
                    message: "The configured MCP response limit is too small for a resources/read page.",
                    category: McpErrorEnvelope.CategoryInternalError,
                    suggestion: "Use a smaller JSON-RPC batch, or increase CDIDX_MCP_RESPONSE_MAX_BYTES or CDIDX_MCP_HTTP_MAX_RESPONSE_BYTES, then retry.",
                    retrySafe: false,
                    extraData: new JsonObject
                    {
                        ["reason"] = "resource_response_budget_too_small",
                        ["minimumContentBytes"] = MinResourceReadMaxBytes,
                        ["responseLimitBytes"] = GetEffectiveResourceReadResponseLimit(),
                    });

            var page = reader.GetBoundedFileContent(
                file,
                isEmpty ? 1 : startLine,
                isEmpty ? 1 : endLine,
                effectiveMaxBytes,
                MaxResourceReadLinesPerPage,
                hasReadableLines ? cursor?.Line : null,
                hasReadableLines ? cursor?.ByteOffset ?? 0 : 0);
            switch (page.Status)
            {
                case BoundedFileReadStatus.FileNotFound:
                    return CreateResourceUriError(id, uri, messagePrefix: "Resource not found",
                        suggestion: "Verify the exact indexed path through resources/templates/list or call resources/list again, then retry with a matching resource URI.",
                        retrySafe: true);
                case BoundedFileReadStatus.InvalidContinuation:
                    return CreateResourceReadArgumentError(id, "cursor",
                        "resources/read params.cursor does not identify a readable UTF-8 position in this resource.",
                        "Omit cursor and restart the range to obtain a fresh continuation token.");
                case BoundedFileReadStatus.IncompleteCoverage:
                case BoundedFileReadStatus.ContentUnavailable:
                case BoundedFileReadStatus.InvalidTopology:
                    return CreateResourceReadStorageError(id, page.Status, page.FailureReason);
            }

            var text = page.Content;
            var returnedBytes = page.Utf8Bytes;
            var truncated = page.Truncated && page.NextLine.HasValue;
            var metadata = new JsonObject
            {
                ["startLine"] = startLine,
                ["startLineByteOffset"] = cursor?.ByteOffset ?? 0,
                ["endLine"] = endLine,
                ["totalLines"] = totalLines,
                ["maxBytes"] = maxBytes,
                ["maxLines"] = MaxResourceReadLinesPerPage,
                ["returnedStartLine"] = isEmpty ? 0 : page.StartLine,
                ["returnedEndLine"] = isEmpty ? 0 : page.EndLine,
                ["returnedBytes"] = returnedBytes,
                ["truncated"] = truncated,
            };
            if (effectiveMaxBytes != maxBytes)
                metadata["effectiveMaxBytes"] = effectiveMaxBytes;
            if (truncated)
            {
                metadata["truncationReason"] = page.TruncationReason switch
                {
                    "max_lines" => "maxLines",
                    "max_bytes" when effectiveMaxBytes < maxBytes => "maxResponseBytes",
                    _ => "maxBytes",
                };
                metadata["nextLine"] = page.NextLine!.Value;
                metadata["nextLineByteOffset"] = page.NextByteOffset ?? 0;
                metadata["nextCursor"] = BuildResourceReadCursor(
                    page.NextLine.Value,
                    page.NextByteOffset ?? 0,
                    endLine,
                    fingerprint);
            }

            var contents = new JsonArray
            {
                new JsonObject
                {
                    ["uri"] = resourceUri,
                    ["mimeType"] = mimeType,
                    ["text"] = text,
                }
            };
            return CreateSuccessResponse(true, id, new JsonObject
            {
                ["contents"] = contents,
                ["_meta"] = metadata,
            });
        }));
    }

    /// <summary>
    /// Adapt the backward-compatible resources/read implementation to a typed tools/call
    /// result without duplicating file text in structuredContent. Validation, index access,
    /// UTF-8 paging, and continuation state stay owned by the single resource reader.
    /// 後方互換の resources/read 実装を型付き tools/call result へ変換し、
    /// structuredContent には file text を重複させない。validation、index access、
    /// UTF-8 paging、continuation state は単一の resource reader が引き続き担当する。
    /// </summary>
    private JsonNode ExecuteReadResource(JsonNode? id, JsonNode? args)
    {
        var resourceResponse = HandleResourcesRead(id, args, adaptForToolResult: true);
        if (resourceResponse["result"] is JsonObject existingToolResult
            && existingToolResult["isError"] is JsonValue existingErrorValue
            && existingErrorValue.TryGetValue<bool>(out var existingIsError)
            && existingIsError)
        {
            return resourceResponse;
        }

        if (resourceResponse["error"] is JsonObject error)
        {
            var errorData = error["data"] as JsonObject;
            var category = TryReadStringValue(errorData?["category"])
                ?? McpErrorEnvelope.CategoryInvalidArgument;
            var suggestion = TryReadStringValue(errorData?["suggestion"])
                ?? "Inspect the read_resource inputSchema via tools/list and correct the URI, range, budget, or cursor.";
            var retrySafe = errorData?["retry_safe"] is JsonValue retrySafeValue
                && retrySafeValue.TryGetValue<bool>(out var parsedRetrySafe)
                && parsedRetrySafe;
            var extraData = errorData?.DeepClone().AsObject() ?? new JsonObject();
            extraData.Remove("category");
            extraData.Remove("suggestion");
            extraData.Remove("retry_safe");
            if (error["code"] is JsonNode errorCode)
                extraData["jsonrpc_code"] = errorCode.DeepClone();

            return CreateToolErrorResponse(
                id,
                TryReadStringValue(error["message"]) ?? "read_resource failed.",
                category,
                suggestion,
                retrySafe,
                extraData);
        }

        if (resourceResponse["result"] is not JsonObject resourceResult
            || resourceResult["contents"] is not JsonArray contents
            || contents.Count != 1
            || contents[0] is not JsonObject content)
        {
            return CreateToolErrorResponse(
                id,
                "read_resource received an invalid internal resource response.",
                McpErrorEnvelope.CategoryInternalError,
                "Retry once. If the problem persists, rebuild the index and report the server diagnostics.",
                retrySafe: true);
        }

        var text = TryReadStringValue(content["text"]) ?? string.Empty;
        var mimeType = TryReadStringValue(content["mimeType"]) ?? "text/plain";
        var structuredContent = new JsonObject
        {
            ["api_version"] = JsonOutputContract.ApiVersion,
            ["tool"] = "read_resource",
            ["resource"] = new JsonObject
            {
                ["uri"] = content["uri"]?.DeepClone(),
                ["mimeType"] = mimeType,
            },
            ["_meta"] = resourceResult["_meta"]?.DeepClone(),
        };
        return CreateToolResult(
            id,
            text,
            structuredContent,
            mimeType,
            enrichStructuredContent: false);
    }

    private JsonObject CreateResourceReadStorageError(
        JsonNode? id,
        BoundedFileReadStatus status,
        string? reason)
    {
        var normalizedReason = reason ?? status switch
        {
            BoundedFileReadStatus.IncompleteCoverage => "resource_chunk_coverage_incomplete",
            BoundedFileReadStatus.ContentUnavailable => "resource_content_unavailable",
            _ => "resource_chunk_topology_invalid",
        };
        var extraData = new JsonObject
        {
            ["reason"] = normalizedReason,
        };
        if (status == BoundedFileReadStatus.InvalidTopology)
        {
            extraData["maxChunks"] = DbReader.MaxBoundedFileReadChunks;
            extraData["maxScannedBytes"] = DbReader.MaxBoundedFileReadScannedUtf8Bytes;
        }

        return status switch
        {
            BoundedFileReadStatus.IncompleteCoverage => CreateErrorResponse(hasId: true, id: id,
                code: McpErrorEnvelope.CodeIndexStale,
                message: "Indexed resource chunks do not cover the requested range.",
                category: McpErrorEnvelope.CategoryIndexStale,
                suggestion: "Refresh or rebuild the index, then call resources/list and retry the read.",
                retrySafe: true,
                extraData: extraData),
            BoundedFileReadStatus.ContentUnavailable => CreateErrorResponse(hasId: true, id: id,
                code: McpErrorEnvelope.CodeIndexMissing,
                message: "Indexed content is unavailable for this non-empty resource.",
                category: McpErrorEnvelope.CategoryIndexMissing,
                suggestion: "Inspect file issues, resolve skipped-content diagnostics, and rebuild the index before retrying.",
                retrySafe: true,
                extraData: extraData),
            _ => CreateErrorResponse(hasId: true, id: id,
                code: McpErrorEnvelope.CodeIndexCorrupted,
                message: "Indexed resource storage metadata is inconsistent or exceeds safe read limits.",
                category: McpErrorEnvelope.CategoryIndexCorrupted,
                suggestion: "Delete the index database, rebuild it, and retry with a resource URI from resources/list.",
                retrySafe: false,
                extraData: extraData),
        };
    }

    private int GetEffectiveResourceReadMaxBytes(
        JsonNode? id,
        string resourceUri,
        string mimeType,
        int requestedMaxBytes,
        bool adaptForToolResult)
    {
        var worstCaseMetadata = new JsonObject
        {
            ["startLine"] = int.MaxValue,
            ["startLineByteOffset"] = int.MaxValue,
            ["endLine"] = int.MaxValue,
            ["totalLines"] = int.MaxValue,
            ["maxBytes"] = requestedMaxBytes,
            ["effectiveMaxBytes"] = int.MaxValue,
            ["maxLines"] = MaxResourceReadLinesPerPage,
            ["returnedStartLine"] = int.MaxValue,
            ["returnedEndLine"] = int.MaxValue,
            ["returnedBytes"] = int.MaxValue,
            ["truncated"] = true,
            ["truncationReason"] = "maxResponseBytes",
            ["nextLine"] = int.MaxValue,
            ["nextLineByteOffset"] = int.MaxValue,
            ["nextCursor"] = new string('x', MaxResourceReadCursorCharacters),
        };
        var worstCaseResponse = adaptForToolResult
            ? CreateSuccessResponse(true, id, new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["mimeType"] = mimeType,
                        ["text"] = string.Empty,
                    },
                },
                ["structuredContent"] = new JsonObject
                {
                    ["api_version"] = JsonOutputContract.ApiVersion,
                    ["tool"] = "read_resource",
                    ["resource"] = new JsonObject
                    {
                        ["uri"] = resourceUri,
                        ["mimeType"] = mimeType,
                    },
                    ["_meta"] = worstCaseMetadata,
                },
            })
            : CreateSuccessResponse(true, id, new JsonObject
            {
                ["contents"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["uri"] = resourceUri,
                        ["mimeType"] = mimeType,
                        ["text"] = string.Empty,
                    },
                },
                ["_meta"] = worstCaseMetadata,
            });
        var envelopeBytes = Encoding.UTF8.GetByteCount(worstCaseResponse.ToJsonString(_jsonOptions));
        var availableEncodedTextBytes = GetEffectiveResourceReadResponseLimit() - envelopeBytes;
        if (availableEncodedTextBytes <= 0)
            return 0;

        // System.Text.Json's default encoder expands any valid source UTF-8 byte by at most
        // six bytes (`\uXXXX` for an ASCII control or HTML-sensitive character).
        // System.Text.Json既定encoderで有効なsource UTF-8 1 byteが展開される最大は6 byte
        // （ASCII control/HTML-sensitive文字の`\uXXXX`）。
        const int worstCaseJsonExpansion = 6;
        return Math.Min(requestedMaxBytes, availableEncodedTextBytes / worstCaseJsonExpansion);
    }

    private int GetEffectiveResourceReadResponseLimit()
    {
        var responseLimit = GetMaxResponseBytes();
        var transportLimit = Volatile.Read(ref _activeTransportMaxResponseBytes);
        if (transportLimit > 0)
            responseLimit = Math.Min(responseLimit, transportLimit);
        if (_currentBatchResponseItemMaxBytes.Value is { } batchLimit)
            responseLimit = Math.Min(responseLimit, Math.Max(0, batchLimit));
        return responseLimit;
    }

    private readonly record struct ResourceReadCursor(int Line, int ByteOffset, int EndLine, string Fingerprint);

    private static bool TryReadOptionalResourceReadInteger(JsonNode? readParams, string name, out int? result)
    {
        result = null;
        if (readParams is not JsonObject obj || !obj.TryGetPropertyValue(name, out var node) || node is null)
            return true;
        if (node is not JsonValue value || !value.TryGetValue<int>(out var parsed))
            return false;
        result = parsed;
        return true;
    }

    private static bool TryReadOptionalResourceReadString(JsonNode? readParams, string name, out string? result)
    {
        result = null;
        if (readParams is not JsonObject obj || !obj.TryGetPropertyValue(name, out var node) || node is null)
            return true;
        if (node is not JsonValue value || !value.TryGetValue<string>(out var parsed) || string.IsNullOrWhiteSpace(parsed))
            return false;
        result = parsed;
        return true;
    }

    private static JsonObject CreateResourceReadIntegerRangeError(JsonNode? id, string argument, int minimum, int maximum, int actual)
        => CreateResourceReadArgumentError(id, argument,
            $"resources/read params.{argument} must be between {minimum} and {maximum}.",
            $"Choose a {argument} value inside the documented resources/read range.",
            new JsonObject
            {
                ["minimum"] = minimum,
                ["maximum"] = maximum,
                ["actual"] = actual,
            });

    private static JsonObject CreateResourceReadArgumentError(
        JsonNode? id,
        string argument,
        string message,
        string suggestion,
        JsonObject? extraData = null)
    {
        var data = extraData ?? new JsonObject();
        data["argument"] = argument;
        return CreateErrorResponse(hasId: true, id: id, code: -32602, message: message,
            category: McpErrorEnvelope.CategoryInvalidArgument,
            suggestion: suggestion,
            retrySafe: false,
            extraData: data);
    }

    private static bool TryParseResourceReadCursor(string value, out ResourceReadCursor cursor)
    {
        cursor = default;
        var parts = value.Split(':');
        if (parts.Length != 5
            || !string.Equals(parts[0], "v1", StringComparison.Ordinal)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var line)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var byteOffset)
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var endLine)
            || line <= 0
            || byteOffset < 0
            || byteOffset > DbReader.MaxBoundedFileReadScannedUtf8Bytes
            || endLine < line
            || parts[4].Length != 16)
        {
            return false;
        }

        cursor = new ResourceReadCursor(line, byteOffset, endLine, parts[4]);
        return true;
    }

    private static string BuildResourceReadCursor(int line, int byteOffset, int endLine, string fingerprint)
        => string.Create(CultureInfo.InvariantCulture, $"v1:{line}:{byteOffset}:{endLine}:{fingerprint}");

    private static string BuildResourceReadFingerprint(string path, string? checksum, long size, int lines, DateTime? modified)
    {
        var descriptor = string.Create(
            CultureInfo.InvariantCulture,
            $"{path}\n{checksum ?? string.Empty}\n{size}\n{lines}\n{modified?.ToUniversalTime().Ticks ?? 0}");
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(descriptor), digest);
        return Convert.ToHexString(digest[..8]);
    }

    private static JsonNode CreateResourceUriError(JsonNode? id, string uri, string messagePrefix, string suggestion, bool retrySafe, bool includeLengthLimit = false)
    {
        var display = McpBoundedText.ForDisplay(uri, McpBoundedText.MaxResourceUriChars);
        var data = new JsonObject
        {
            ["uri"] = display.Text,
        };
        display.AddMetadata(data, "uri");
        if (includeLengthLimit)
        {
            data["max_length"] = McpBoundedText.MaxResourceUriChars;
            data["actual_length"] = uri.Length;
        }
        return CreateErrorResponse(hasId: true, id: id, code: -32602, message: $"{messagePrefix}: {display.Text}",
            category: McpErrorEnvelope.CategoryInvalidArgument,
            suggestion: suggestion,
            retrySafe: retrySafe,
            extraData: data);
    }

}
