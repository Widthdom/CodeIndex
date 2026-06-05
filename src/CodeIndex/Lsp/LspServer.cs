using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Models;

namespace CodeIndex.Lsp;

internal sealed class LspServer : IDisposable
{
    private const int DefaultLimit = 50;
    internal const int MaxLspFrameBytes = 8 * 1024 * 1024;
    internal const int MaxLspHeaderLineBytes = 8 * 1024;
    internal const int MaxPositionDocumentBytes = 4 * 1024 * 1024;
    internal const int MaxJsonDepth = 32;
    private const int JsonRpcInvalidParamsCode = -32602;
    private const int JsonRpcInternalErrorCode = -32603;
    private const string JsonRpcInvalidParamsMessage = "Invalid params";
    private const string JsonRpcInternalErrorMessage = "Internal error";
    private static readonly JsonDocumentOptions LspJsonDocumentOptions = new()
    {
        MaxDepth = MaxJsonDepth,
    };

    private readonly DbReader _reader;
    private readonly string _version;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly string? _projectRoot;
    private readonly StringComparison _pathStringComparison;
    private bool _shutdownRequested;
    private bool _exitRequested;
    private bool _exitRequestedBeforeShutdown;

    private readonly record struct PositionTokenContext(string Token, string IndexedPath);

    public LspServer(DbReader reader, string version, JsonSerializerOptions jsonOptions, string? projectRoot = null)
    {
        _reader = reader;
        _version = version;
        _jsonOptions = jsonOptions;
        _projectRoot = string.IsNullOrWhiteSpace(projectRoot) ? null : projectRoot;
        _pathStringComparison = PathCasing.ComparisonFor(_projectRoot ?? Environment.CurrentDirectory);
    }

    public int Run(Stream input, Stream output)
    {
        while (TryReadMessage(input, out var payload))
        {
            var response = HandleMessage(payload);
            if (response != null)
                WriteMessage(output, response.ToJsonString(_jsonOptions));
            if (_exitRequested)
                break;
        }

        return _exitRequestedBeforeShutdown ? CommandExitCodes.UsageError : CommandExitCodes.Success;
    }

    internal JsonObject? HandleMessage(string payload)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload, LspJsonDocumentOptions);
        }
        catch (JsonException)
        {
            return Error(null, -32700, "Parse error");
        }

        using (document)
        {
            JsonNode? id = null;
            var hasId = false;

            try
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return Error(null, -32600, "Invalid Request");

                var method = root.TryGetProperty("method", out var methodElement) ? methodElement.GetString() : null;
                hasId = root.TryGetProperty("id", out var idElement);
                id = hasId ? JsonNode.Parse(idElement.GetRawText(), documentOptions: LspJsonDocumentOptions) : null;

                if (method == null)
                    return hasId ? Error(id, -32600, "Invalid Request") : null;

                return method switch
                {
                    "initialize" => Result(id, BuildInitializeResult()),
                    "initialized" => null,
                    "shutdown" => HandleShutdown(id),
                    "exit" => HandleExit(),
                    "workspace/symbol" => Result(id, WorkspaceSymbol(root)),
                    "textDocument/documentSymbol" => Result(id, DocumentSymbol(root)),
                    "textDocument/definition" => Result(id, Definition(root)),
                    "textDocument/references" => Result(id, References(root)),
                    _ => hasId ? Error(id, -32601, $"Method not found: {method}") : null,
                };
            }
            catch (Exception ex) when (ex is ArgumentException or JsonException)
            {
                return hasId ? Error(id, JsonRpcInvalidParamsCode, JsonRpcInvalidParamsMessage) : null;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                return hasId ? Error(id, JsonRpcInternalErrorCode, JsonRpcInternalErrorMessage) : null;
            }
        }
    }

    private JsonObject HandleShutdown(JsonNode? id)
    {
        _shutdownRequested = true;
        return Result(id, null);
    }

    private JsonObject? HandleExit()
    {
        _exitRequestedBeforeShutdown = !_shutdownRequested;
        _exitRequested = true;
        return null;
    }

    private JsonObject BuildInitializeResult() => new()
    {
        ["capabilities"] = new JsonObject
        {
            ["definitionProvider"] = true,
            ["referencesProvider"] = true,
            ["documentSymbolProvider"] = true,
            ["workspaceSymbolProvider"] = true,
            ["textDocumentSync"] = 0,
        },
        ["serverInfo"] = new JsonObject
        {
            ["name"] = "cdidx",
            ["version"] = _version,
        },
    };

    private JsonArray WorkspaceSymbol(JsonElement root)
    {
        var query = GetString(root, "params", "query");
        var symbols = _reader.SearchSymbols(query, DefaultLimit);
        var array = new JsonArray();
        foreach (var symbol in symbols)
            array.Add(ToWorkspaceSymbol(symbol));
        return array;
    }

    private JsonArray DocumentSymbol(JsonElement root)
    {
        var path = GetDocumentPath(root);
        var indexedPath = ResolveIndexedPath(path);
        if (indexedPath == null)
            return [];

        var symbols = _reader.SearchSymbols((string?)null, 1000, pathPatterns: [indexedPath]);
        var array = new JsonArray();
        foreach (var symbol in symbols.OrderBy(s => s.StartLine).ThenBy(s => s.Name, StringComparer.Ordinal))
            array.Add(ToDocumentSymbol(symbol));
        return array;
    }

    private JsonArray Definition(JsonElement root)
    {
        var context = ExtractPositionToken(root);
        if (context == null)
            return [];

        var definitions = ResolveLspDefinitions(context.Value);
        var array = new JsonArray();
        foreach (var definition in definitions)
            array.Add(ToLocation(definition.Path, definition.StartLine, 1, definition.EndLine, 1));
        return array;
    }

    private JsonArray References(JsonElement root)
    {
        var context = ExtractPositionToken(root);
        if (context == null)
            return [];

        var analysis = ResolveLspReferences(context.Value);
        var array = new JsonArray();
        foreach (var reference in analysis.References)
            array.Add(ToLocation(reference.Path, reference.Line, Math.Max(reference.Column, 1), reference.Line, Math.Max(reference.Column, 1) + Math.Max(context.Value.Token.Length, 1)));
        return array;
    }

    private List<DefinitionResult> ResolveLspDefinitions(PositionTokenContext context)
    {
        var localDefinitions = _reader.GetDefinitions(context.Token, DefaultLimit, exact: true, pathPatterns: [context.IndexedPath]);
        if (localDefinitions.Count > 0)
            return localDefinitions;

        var workspaceDefinitions = _reader.GetDefinitions(context.Token, DefaultLimit, exact: true);
        return HasSingleLspDefinitionTarget(workspaceDefinitions) ? workspaceDefinitions : [];
    }

    private SymbolAnalysisResult ResolveLspReferences(PositionTokenContext context)
    {
        var localDefinitions = _reader.GetDefinitions(context.Token, DefaultLimit, exact: true, pathPatterns: [context.IndexedPath]);
        if (localDefinitions.Count > 0)
            return _reader.AnalyzeSymbol(context.Token, DefaultLimit, pathPatterns: [context.IndexedPath], exact: true);

        var workspaceDefinitions = _reader.GetDefinitions(context.Token, DefaultLimit, exact: true);
        if (workspaceDefinitions.Count == 0 || !HasSingleLspDefinitionTarget(workspaceDefinitions))
            return _reader.AnalyzeSymbol(context.Token, DefaultLimit, pathPatterns: [context.IndexedPath], exact: true);

        return _reader.AnalyzeSymbol(context.Token, DefaultLimit, exact: true);
    }

    private static bool HasSingleLspDefinitionTarget(IReadOnlyList<DefinitionResult> definitions)
    {
        if (definitions.Count <= 1)
            return true;

        var firstKey = BuildLspDefinitionTargetKey(definitions[0]);
        return definitions.Skip(1).All(definition => string.Equals(BuildLspDefinitionTargetKey(definition), firstKey, StringComparison.Ordinal));
    }

    private static string BuildLspDefinitionTargetKey(DefinitionResult definition)
        => string.Join('\0', definition.Path, definition.Kind, definition.ContainerKind, definition.ContainerName, definition.Name);

    private PositionTokenContext? ExtractPositionToken(JsonElement root)
    {
        var path = GetDocumentPath(root);
        var line = GetInt32(root, "params", "position", "line");
        var character = GetInt32(root, "params", "position", "character");
        if (line < 0 || character < 0)
            return null;

        if (!TryResolveDocumentPath(path, out var resolvedPath, out var projectRelativePath))
            return null;

        var indexedPath = ResolveIndexedPath(path, resolvedPath, projectRelativePath);
        if (indexedPath == null || !TryResolveIndexedFilePath(indexedPath, out var indexedFullPath))
            return null;

        if (!string.Equals(resolvedPath, indexedFullPath, _pathStringComparison))
            return null;

        if (!TryReadPositionLine(indexedFullPath, line, out var sourceLine))
            return null;

        var token = ExtractTokenAtUtf16Position(sourceLine, character);
        return string.IsNullOrWhiteSpace(token) ? null : new PositionTokenContext(token, indexedPath);
    }

    private static bool TryReadPositionLine(string path, int targetLine, out string sourceLine)
    {
        sourceLine = string.Empty;
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > MaxPositionDocumentBytes)
                return false;

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            for (var currentLine = 0; currentLine <= targetLine; currentLine++)
            {
                var line = reader.ReadLine();
                if (line == null)
                    return false;
                if (currentLine == targetLine)
                {
                    sourceLine = line;
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }

        return false;
    }

    internal static string? ExtractTokenAtUtf16Position(string line, int character)
    {
        if (character < 0)
            return null;
        var index = Math.Min(character, line.Length);
        while (index > 0 && index == line.Length)
            index--;
        if (index < line.Length && !IsTokenChar(line[index]) && index > 0 && IsTokenChar(line[index - 1]))
            index--;
        if (index >= line.Length || !IsTokenChar(line[index]))
            return null;

        var start = index;
        while (start > 0 && IsTokenChar(line[start - 1]))
            start--;
        var end = index + 1;
        while (end < line.Length && IsTokenChar(line[end]))
            end++;
        return line[start..end].TrimStart('@');
    }

    private static bool IsTokenChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '@';

    private bool MatchesDocumentPath(string indexedPath, string documentPath, string? projectRelativePath)
    {
        var normalizedIndexed = indexedPath.Replace('\\', '/');
        if (_projectRoot != null)
        {
            if (Path.IsPathRooted(indexedPath)
                && TryResolveIndexedFilePath(indexedPath, out var indexedFullPath)
                && TryGetProjectRelativePath(indexedFullPath, out var indexedRelativePath)
                && indexedRelativePath != null)
            {
                normalizedIndexed = indexedRelativePath.Replace('\\', '/');
            }

            return projectRelativePath != null
                && string.Equals(normalizedIndexed, projectRelativePath.Replace('\\', '/'), _pathStringComparison);
        }

        if (string.Equals(indexedPath, documentPath, StringComparison.Ordinal))
            return true;

        var normalizedDocument = documentPath.Replace('\\', '/');
        return normalizedDocument.EndsWith("/" + normalizedIndexed, StringComparison.Ordinal);
    }

    private string? ResolveIndexedPath(string documentPath)
    {
        if (!TryResolveDocumentPath(documentPath, out var resolvedPath, out var projectRelativePath))
            return null;

        return ResolveIndexedPath(documentPath, resolvedPath, projectRelativePath);
    }

    private string? ResolveIndexedPath(string documentPath, string resolvedPath, string? projectRelativePath)
    {
        if (projectRelativePath != null)
        {
            var exactPath = projectRelativePath.Replace('\\', '/');
            var exactFile = _reader.GetFileByPath(exactPath);
            if (exactFile != null)
                return exactFile.Path;
        }

        var fileName = Path.GetFileName(documentPath);
        if (string.IsNullOrEmpty(fileName))
            fileName = Path.GetFileName(resolvedPath);
        if (string.IsNullOrEmpty(fileName))
            return null;

        var files = _reader.ListFiles(fileName, 1000);
        var matches = files
            .Where(file => MatchesDocumentPath(file.Path, documentPath, projectRelativePath))
            .Take(2)
            .ToList();
        return matches.Count == 1 ? matches[0].Path : null;
    }

    private bool TryResolveDocumentPath(string documentPath, out string resolvedPath, out string? projectRelativePath)
    {
        resolvedPath = string.Empty;
        projectRelativePath = null;
        try
        {
            resolvedPath = Path.IsPathRooted(documentPath)
                ? Path.GetFullPath(documentPath)
                : Path.GetFullPath(documentPath, _projectRoot ?? Environment.CurrentDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }

        if (_projectRoot == null)
            return true;

        return TryGetProjectRelativePath(resolvedPath, out projectRelativePath);
    }

    private bool TryResolveIndexedFilePath(string indexedPath, out string resolvedPath)
    {
        resolvedPath = string.Empty;
        try
        {
            resolvedPath = Path.IsPathRooted(indexedPath)
                ? Path.GetFullPath(indexedPath)
                : Path.GetFullPath(indexedPath, _projectRoot ?? Environment.CurrentDirectory);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool TryGetProjectRelativePath(string resolvedPath, out string? relativePath)
    {
        relativePath = null;
        if (_projectRoot == null)
            return false;

        try
        {
            var relative = Path.GetRelativePath(Path.GetFullPath(_projectRoot), resolvedPath);
            if (relative == "."
                || relative == ".."
                || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
                || Path.IsPathRooted(relative))
            {
                return false;
            }

            relativePath = relative;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private JsonObject ToWorkspaceSymbol(SymbolResult symbol) => new()
    {
        ["name"] = symbol.Name,
        ["kind"] = SymbolKind(symbol.Kind),
        ["location"] = ToLocation(symbol.Path, symbol.StartLine, 1, symbol.EndLine, 1),
        ["containerName"] = symbol.ContainerName,
    };

    private static JsonObject ToDocumentSymbol(SymbolResult symbol) => new()
    {
        ["name"] = symbol.Name,
        ["kind"] = SymbolKind(symbol.Kind),
        ["range"] = ToRange(symbol.StartLine, 1, symbol.EndLine, 1),
        ["selectionRange"] = ToRange(symbol.Line, 1, symbol.Line, 1),
        ["detail"] = symbol.Signature,
    };

    private JsonObject ToLocation(string path, int startLine, int startColumn, int endLine, int endColumn) => new()
    {
        ["uri"] = PathToUri(path, _projectRoot),
        ["range"] = ToRange(startLine, startColumn, endLine, endColumn),
    };

    private static JsonObject ToRange(int startLine, int startColumn, int endLine, int endColumn) => new()
    {
        ["start"] = new JsonObject
        {
            ["line"] = Math.Max(startLine - 1, 0),
            ["character"] = Math.Max(startColumn - 1, 0),
        },
        ["end"] = new JsonObject
        {
            ["line"] = Math.Max(endLine - 1, 0),
            ["character"] = Math.Max(endColumn - 1, 0),
        },
    };

    private static int SymbolKind(string kind) => kind switch
    {
        "class" => 5,
        "function" or "test.method" => 12,
        "property" => 7,
        "enum" => 10,
        "interface" => 11,
        "namespace" => 3,
        "struct" => 23,
        _ => 13,
    };

    private static string GetDocumentPath(JsonElement root)
    {
        var uri = GetString(root, "params", "textDocument", "uri");
        if (string.IsNullOrWhiteSpace(uri))
            throw new ArgumentException("textDocument.uri is required.");
        return UriToPath(uri);
    }

    private static string? GetString(JsonElement root, params string[] path)
    {
        if (!TryGet(root, out var value, path) || value.ValueKind != JsonValueKind.String)
            return null;
        return value.GetString();
    }

    private static int GetInt32(JsonElement root, params string[] path)
    {
        if (!TryGet(root, out var value, path) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
            return -1;
        return result;
    }

    private static bool TryGet(JsonElement root, out JsonElement value, params string[] path)
    {
        value = root;
        foreach (var segment in path)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value))
                return false;
        }
        return true;
    }

    internal static string PathToUri(string path, string? projectRoot = null)
    {
        var fullPath = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(path, projectRoot ?? Environment.CurrentDirectory);
        return new Uri(fullPath).AbsoluteUri;
    }

    internal static string UriToPath(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile)
            return uri;
        return parsed.LocalPath;
    }

    private static JsonObject Result(JsonNode? id, JsonNode? result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["result"] = result,
    };

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
        },
    };

    internal static bool TryReadMessage(Stream input, out string payload)
    {
        payload = string.Empty;
        var contentLength = -1;
        while (true)
        {
            var line = ReadAsciiLine(input);
            if (line == null)
                return false;
            if (line.Length == 0)
                break;
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                    || parsed < 0
                    || parsed > MaxLspFrameBytes)
                {
                    return false;
                }

                contentLength = parsed;
            }
        }

        if (contentLength < 0)
            return false;

        var buffer = ArrayPool<byte>.Shared.Rent(contentLength);
        try
        {
            var offset = 0;
            while (offset < contentLength)
            {
                var read = input.Read(buffer, offset, contentLength - offset);
                if (read == 0)
                    return false;
                offset += read;
            }
            payload = Encoding.UTF8.GetString(buffer, 0, contentLength);
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal static void WriteMessage(Stream output, string payload)
    {
        var body = Encoding.UTF8.GetBytes(payload);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        output.Write(header);
        output.Write(body);
        output.Flush();
    }

    private static string? ReadAsciiLine(Stream input)
    {
        var bytes = new List<byte>();
        while (true)
        {
            var value = input.ReadByte();
            if (value < 0)
                return bytes.Count == 0 ? null : Encoding.ASCII.GetString(bytes.ToArray());
            if (value == '\n')
                break;
            if (value != '\r')
            {
                if (bytes.Count >= MaxLspHeaderLineBytes)
                    return null;
                bytes.Add((byte)value);
            }
        }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    public void Dispose()
    {
        _ = _shutdownRequested;
    }
}
