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


    private JsonNode HandlePromptsList(JsonNode? id)
    {
        var prompts = new JsonArray
        {
            CreatePromptDefinition("summarize_file", "Summarize the API surface and responsibilities of an indexed file.", "path", "Indexed file path to summarize."),
            CreatePromptDefinition("find_unused", "Find likely unused symbols in an optional language or path scope.", "scope", "Optional language, module, or path scope."),
            CreatePromptDefinition("impact_of_changing", "Plan impact analysis for changing a symbol.", "symbol", "Symbol name to analyze."),
            CreatePromptDefinition("investigate_before_edit", "Investigate relevant code before making edits.", "topic", "Optional feature, symbol, file, or behavior to investigate."),
            CreatePromptDefinition("find_existing_pattern", "Find existing implementation and test patterns before adding code.", "topic", "Optional API, behavior, module, or feature pattern to search for."),
            CreatePromptDefinition("safe_symbol_change", "Plan a safe symbol rename or behavior change using graph-aware tools.", "symbol", "Symbol or behavior being changed."),
            CreatePromptDefinition("debug_failure", "Debug a failing build, test, or runtime error using indexed evidence.", "failure", "Optional error text, test name, or failing behavior."),
        };
        return CreateSuccessResponse(true, id, new JsonObject { ["prompts"] = prompts });
    }

    private JsonNode HandlePromptsGet(JsonNode? id, JsonNode? getParams)
    {
        var name = TryReadStringValue(getParams?["name"]);
        if (string.IsNullOrWhiteSpace(name))
            return CreateErrorResponse(hasId: true, id: id, code: -32602, message: "Missing prompt name",
                category: McpErrorEnvelope.CategoryMissingParameter,
                suggestion: "prompts/get requires `params.name`; call prompts/list to enumerate available names.",
                retrySafe: false);
        name = name.Trim();
        if (name.Length > McpBoundedText.MaxPromptNameChars)
            return CreatePromptStringTooLongError(id, parameterName: "name", value: name, maxChars: McpBoundedText.MaxPromptNameChars,
                messagePrefix: "Prompt name is too long",
                suggestion: "Use one of the short prompt names returned by prompts/list.");

        var args = getParams?["arguments"] as JsonObject;
        string? ReadArg(string key, out JsonNode? error)
        {
            error = null;
            if (args == null
                || !args.TryGetPropertyValue(key, out var node)
                || node is not JsonValue value
                || !value.TryGetValue<string>(out var s))
            {
                return null;
            }
            if (s.Length > McpBoundedText.MaxPromptArgumentChars)
            {
                error = CreatePromptStringTooLongError(id, parameterName: key, value: s, maxChars: McpBoundedText.MaxPromptArgumentChars,
                    messagePrefix: $"Prompt argument '{key}' is too long",
                    suggestion: "Shorten prompt arguments before calling prompts/get; long source or path context should be fetched with tools instead.");
                return null;
            }
            return McpBoundedText.ForDisplay(s, McpBoundedText.MaxPromptArgumentChars).Text;
        }

        string text;
        switch (name)
        {
            case "summarize_file":
                {
                    var path = ReadArg("path", out var argumentError);
                    if (argumentError is not null)
                        return argumentError;
                    text = $"Use the `outline` tool for `{path ?? "<path>"}`, then use `excerpt` only for the ranges needed to summarize public API, key symbols, and responsibilities.";
                    break;
                }
            case "find_unused":
                {
                    var scope = ReadArg("scope", out var argumentError);
                    if (argumentError is not null)
                        return argumentError;
                    text = $"Use `unused_symbols` with the requested scope `{scope ?? "<scope>"}`. Cross-check surprising results with `references` or `callers` before recommending deletions.";
                    break;
                }
            case "impact_of_changing":
                {
                    var symbol = ReadArg("symbol", out var argumentError);
                    if (argumentError is not null)
                        return argumentError;
                    text = $"Use `impact_analysis` for `{symbol ?? "<symbol>"}`. Summarize direct callers, transitive callers, and files that likely need tests.";
                    break;
                }
            case "investigate_before_edit":
                {
                    var topic = ReadArg("topic", out var argumentError);
                    if (argumentError is not null)
                        return argumentError;
                    text = $"Before editing `{topic ?? "<topic>"}`, use `map` for orientation if needed, `search` for broad discovery, `symbols` or `definition` for declarations, `references` for usage and tests, and focused `excerpt` calls for only the relevant ranges.";
                    break;
                }
            case "find_existing_pattern":
                {
                    var topic = ReadArg("topic", out var argumentError);
                    if (argumentError is not null)
                        return argumentError;
                    text = $"Find existing patterns for `{topic ?? "<topic>"}` with `search` and `symbols`, inspect representative files with `outline`, then use focused `excerpt` ranges from implementation and tests before adding new code.";
                    break;
                }
            case "safe_symbol_change":
                {
                    var symbol = ReadArg("symbol", out var argumentError);
                    if (argumentError is not null)
                        return argumentError;
                    text = $"For `{symbol ?? "<symbol>"}`, confirm identity with `definition` or `symbols exactName:true`, inspect `references`, `callers`, and `callees`, then read focused `excerpt` ranges for declarations, call sites, and tests before changing behavior or names.";
                    break;
                }
            case "debug_failure":
                {
                    var failure = ReadArg("failure", out var argumentError);
                    if (argumentError is not null)
                        return argumentError;
                    text = $"Debug `{failure ?? "<failure>"}` by searching exact error text with `search` or `exactSubstring`, finding related symbols with `definition` and `references`, checking callers/callees for the failing path, and reading focused `excerpt` ranges before proposing a fix.";
                    break;
                }
            default:
                return CreateUnknownPromptError(id, name);
        }

        var messages = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = text,
                },
            },
        };
        return CreateSuccessResponse(true, id, new JsonObject
        {
            ["description"] = name,
            ["messages"] = messages,
        });
    }

    private static JsonNode CreatePromptStringTooLongError(JsonNode? id, string parameterName, string value, int maxChars, string messagePrefix, string suggestion)
    {
        var display = McpBoundedText.ForDisplay(value, maxChars);
        var data = new JsonObject
        {
            ["parameter"] = parameterName,
            ["max_length"] = maxChars,
            ["actual_length"] = value.Length,
            ["value"] = display.Text,
        };
        display.AddMetadata(data, "value");
        return CreateErrorResponse(hasId: true, id: id, code: -32602, message: $"{messagePrefix}: '{display.Text}'",
            category: McpErrorEnvelope.CategoryInvalidArgument,
            suggestion: suggestion,
            retrySafe: false,
            extraData: data);
    }

    private static JsonNode CreateUnknownPromptError(JsonNode? id, string name)
    {
        var display = McpBoundedText.ForDisplay(name, McpBoundedText.MaxPromptNameChars);
        var data = new JsonObject
        {
            ["prompt"] = display.Text,
        };
        display.AddMetadata(data, "prompt");
        return CreateErrorResponse(hasId: true, id: id, code: -32602, message: $"Unknown prompt: {display.Text}",
            category: McpErrorEnvelope.CategoryInvalidArgument,
            suggestion: "Call prompts/list and request one of the advertised prompt names.",
            retrySafe: false,
            extraData: data);
    }

    private async Task<JsonNode> HandleLoggingSetLevelAsync(JsonNode? id, JsonNode? setLevelParams)
    {
        var level = TryReadStringValue(setLevelParams?["level"]);
        if (!IsSupportedMcpLogLevel(level))
            return CreateErrorResponse(hasId: true, id: id, code: -32602, message: "Invalid logging level",
                category: McpErrorEnvelope.CategoryInvalidArgument,
                suggestion: "logging/setLevel requires params.level to be one of: debug, info, notice, warning, error, critical, alert, emergency.",
                retrySafe: false);

        var previous = Interlocked.Exchange(ref _mcpLogLevel, level!);
        await EmitLogNotificationAsync("info", $"MCP logging level changed from {previous} to {level}.").ConfigureAwait(false);
        return CreateSuccessResponse(true, id, new JsonObject());
    }

    private static JsonObject CreatePromptDefinition(string name, string description, string argumentName, string argumentDescription)
        => new()
        {
            ["name"] = name,
            ["description"] = description,
            ["arguments"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = argumentName,
                    ["description"] = argumentDescription,
                    ["required"] = false,
                },
            },
        };

    private static string BuildResourceUri(string path)
        => "cdidx://file/" + string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    private static bool TryParseResourceUri(string uri, out string path)
    {
        path = string.Empty;
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
            || !string.Equals(parsed.Scheme, "cdidx", StringComparison.OrdinalIgnoreCase)
            || !TryExtractRawResourcePath(uri, out var rawPath))
        {
            return false;
        }

        var isCanonicalFile = string.Equals(parsed.Host, "file", StringComparison.OrdinalIgnoreCase);
        var isTemplateFilePath = string.Equals(parsed.Host, "file-path", StringComparison.OrdinalIgnoreCase);
        if (!isCanonicalFile && !isTemplateFilePath)
            return false;
        if (isTemplateFilePath
            && (!string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment)))
        {
            return false;
        }

        var decodedSuccessfully = isTemplateFilePath
            ? PathUriNormalizer.TryDecodeTemplateRelativeUriPath(rawPath, out var decoded)
            : PathUriNormalizer.TryDecodeRelativeUriPath(rawPath, allowBackslash: false, out decoded);
        if (!decodedSuccessfully)
            return false;

        path = decoded;
        return true;
    }

    private static bool TryExtractRawResourcePath(string uri, out string rawPath)
    {
        rawPath = string.Empty;
        var schemeSeparator = uri.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparator < 0)
            return false;

        var hostStart = schemeSeparator + 3;
        var pathStart = uri.IndexOf('/', hostStart);
        if (pathStart < 0 || pathStart == uri.Length - 1)
            return false;

        rawPath = uri[(pathStart + 1)..];
        var terminator = rawPath.IndexOfAny(['?', '#']);
        if (terminator >= 0)
            rawPath = rawPath[..terminator];

        return !string.IsNullOrWhiteSpace(rawPath);
    }

    private static string? TryReadStringValue(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string GetResourceMimeType(string? lang)
        => lang?.ToLowerInvariant() switch
        {
            "csharp" => "text/x-csharp",
            "fsharp" => "text/x-fsharp",
            "vb" => "text/x-vb",
            "javascript" => "text/javascript",
            "typescript" => "text/typescript",
            "json" => "application/json",
            "markdown" => "text/markdown",
            "python" => "text/x-python",
            "rust" => "text/x-rust",
            "shell" => "text/x-shellscript",
            "sql" => "application/sql",
            "yaml" => "application/yaml",
            "xml" => "application/xml",
            _ => "text/plain",
        };

}
