using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public class PostExtractionHookContractTests
{
    [Fact]
    public void CallbackProtocol_RoundTripsRequestAndResponseContracts_Issue4185()
    {
        var request = new PostExtractionHookCallbackProtocol.WorkerRequest(
            nameof(IPostExtractionHook.OnSymbolsExtracted),
            new FileContext("project", "src/App.cs", "/project/src/App.cs", "csharp"),
            [
                new SymbolRecord { FileId = 5, Kind = "class", Name = "App", Line = 3, StartLine = 3, EndLine = 3 },
            ],
            null,
            MaxSymbols: 1);

        var roundTrippedRequest = PostExtractionHookCallbackProtocol.DeserializeRequest(
            PostExtractionHookCallbackProtocol.SerializeRequest(request));

        Assert.Equal(request.Callback, roundTrippedRequest.Callback);
        Assert.Equal(request.Context, roundTrippedRequest.Context);
        var requestSymbol = Assert.Single(roundTrippedRequest.Symbols!);
        Assert.Equal("App", requestSymbol.Name);
        Assert.Equal(1, roundTrippedRequest.MaxSymbols);

        var response = new PostExtractionHookCallbackProtocol.WorkerResponse(
            [
                new SymbolRecord { FileId = 5, Kind = "class", Name = "TrimmedApp", Line = 3, StartLine = 3, EndLine = 3 },
            ],
            null,
            CallbackError: "hook_callback_failed: InvalidOperationException",
            WorkerError: null,
            SymbolsTruncated: true);

        var roundTrippedResponse = PostExtractionHookCallbackProtocol.DeserializeResponse(
            PostExtractionHookCallbackProtocol.SerializeResponse(response));

        Assert.NotNull(roundTrippedResponse);
        Assert.True(roundTrippedResponse.SymbolsTruncated);
        Assert.Null(roundTrippedResponse.WorkerError);
        Assert.Equal(response.CallbackError, roundTrippedResponse.CallbackError);
        Assert.Equal("TrimmedApp", Assert.Single(roundTrippedResponse.Symbols!).Name);
    }

    [Fact]
    public void MutationMaterializer_ClonesAndTrimsRecordsWithinContracts_Issue4185()
    {
        var symbols = new List<SymbolRecord>
        {
            new() { FileId = 7, Kind = "class", Name = "Original", Line = 1, StartLine = 1, EndLine = 1 },
            new() { FileId = 7, Kind = "method", Name = "Extra", Line = 2, StartLine = 2, EndLine = 2 },
        };

        var cloned = PostExtractionHookMutationMaterializer.CloneSymbols(symbols, maxCount: 1, out var inputTruncated);

        Assert.True(inputTruncated);
        var clonedSymbol = Assert.Single(cloned);
        clonedSymbol.Name = "ChangedByHook";
        Assert.Equal("Original", symbols[0].Name);

        var references = new List<ReferenceRecord>
        {
            new() { FileId = 7, SymbolName = "Original", ReferenceKind = "usage", Line = 1, Column = 1 },
            new() { FileId = 7, SymbolName = "Extra", ReferenceKind = "usage", Line = 2, Column = 1 },
            new() { FileId = 7, SymbolName = "Overflow", ReferenceKind = "usage", Line = 3, Column = 1 },
        };

        Assert.True(PostExtractionHookMutationMaterializer.TrimToLimit(references, maxCount: 2));
        Assert.Equal(["Original", "Extra"], references.Select(reference => reference.SymbolName));
    }
}
