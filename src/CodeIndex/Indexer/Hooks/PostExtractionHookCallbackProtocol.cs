using System.Text.Json;
using System.Text.Json.Serialization;
using CodeIndex.Diagnostics;
using CodeIndex.Models;

namespace CodeIndex.Indexer.Hooks;

internal static class PostExtractionHookCallbackProtocol
{
    internal static readonly JsonSerializerOptions JsonOptions =
        WorkerProtocolJsonValidator.CreateSerializerOptions(PostExtractionHookCallbackProtocolJsonContext.Default.Options);

    internal static string SerializeRequest(WorkerRequest request)
        => JsonSerializer.Serialize(request, JsonOptions);

    internal static string SerializeResponse(WorkerResponse response)
        => JsonSerializer.Serialize(response, JsonOptions);

    internal static WorkerRequest DeserializeRequest(string requestJson, int maxUtf8Bytes = WorkerProtocolLineLimits.MaxLineUtf8Bytes)
        => BoundedJson.Deserialize<WorkerRequest>(requestJson, maxUtf8Bytes, JsonOptions)
           ?? throw new InvalidOperationException("worker request was empty.");

    internal static WorkerResponse? DeserializeResponse(string responseJson, int maxUtf8Bytes = WorkerProtocolLineLimits.MaxLineUtf8Bytes)
        => BoundedJson.Deserialize<WorkerResponse>(responseJson, maxUtf8Bytes, JsonOptions);

    internal static WorkerResponse WorkerError(string category, Exception exception)
        => new(null, null, null, SafeDiagnosticFormatter.FormatExceptionCategory(category, exception));

    internal static WorkerResponse WorkerError(string message)
        => new(null, null, null, message);

    internal sealed record WorkerRequest(
        string Callback,
        FileContext Context,
        List<SymbolRecord>? Symbols,
        List<ReferenceRecord>? References,
        int? MaxSymbols = null,
        int? MaxReferences = null);

    internal sealed record WorkerResponse(
        List<SymbolRecord>? Symbols,
        List<ReferenceRecord>? References,
        string? CallbackError,
        string? WorkerError,
        bool SymbolsTruncated = false,
        bool ReferencesTruncated = false);
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PostExtractionHookCallbackProtocol.WorkerRequest), TypeInfoPropertyName = "ProtocolWorkerRequest")]
[JsonSerializable(typeof(PostExtractionHookCallbackProtocol.WorkerResponse), TypeInfoPropertyName = "ProtocolWorkerResponse")]
[JsonSerializable(typeof(PostExtractionHookCallbackWorker.WorkerRequest), TypeInfoPropertyName = "CompatibilityWorkerRequest")]
[JsonSerializable(typeof(PostExtractionHookCallbackWorker.WorkerResponse), TypeInfoPropertyName = "CompatibilityWorkerResponse")]
internal partial class PostExtractionHookCallbackProtocolJsonContext : JsonSerializerContext;
