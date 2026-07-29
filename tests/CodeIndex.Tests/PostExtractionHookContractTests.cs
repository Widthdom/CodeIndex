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
    public void CallbackProtocol_RoundTripsLanguageIdentityState_Issue4738()
    {
        var request = new PostExtractionHookCallbackProtocol.WorkerRequest(
            nameof(IPostExtractionHook.OnReferencesExtracted),
            new FileContext("project", "src/style.nim", "/project/src/style.nim", "nim"),
            [
                new SymbolRecord
                {
                    FileId = 5,
                    Kind = "function",
                    Name = "my_proc",
                    IdentityNameFolded = "myproc",
                    Line = 1,
                    StartLine = 1,
                    EndLine = 1,
                },
            ],
            [
                new ReferenceRecord
                {
                    FileId = 5,
                    SymbolName = "myProc",
                    IdentitySymbolNameFolded = "myproc",
                    ReferenceKind = "call",
                    Line = 2,
                    Column = 5,
                    ContainerName = "RunGraph",
                    IdentityContainerNameFolded = "Rungraph",
                    SuppressInferredTargetQualifier = true,
                },
            ]);

        var roundTripped = PostExtractionHookCallbackProtocol.DeserializeRequest(
            PostExtractionHookCallbackProtocol.SerializeRequest(request));

        Assert.Equal("myproc", Assert.Single(roundTripped.Symbols!).IdentityNameFolded);
        var reference = Assert.Single(roundTripped.References!);
        Assert.Equal("myproc", reference.IdentitySymbolNameFolded);
        Assert.Equal("Rungraph", reference.IdentityContainerNameFolded);
        Assert.True(reference.SuppressInferredTargetQualifier);
    }

    [Fact]
    public void MutationMaterializer_ClonesAndTrimsRecordsWithinContracts_Issue4185()
    {
        var symbols = new List<SymbolRecord>
        {
            new()
            {
                FileId = 7,
                Kind = "class",
                Name = "Original",
                IdentityNameFolded = "original-key",
                Line = 1,
                StartLine = 1,
                EndLine = 1,
            },
            new() { FileId = 7, Kind = "method", Name = "Extra", Line = 2, StartLine = 2, EndLine = 2 },
        };

        var cloned = PostExtractionHookMutationMaterializer.CloneSymbols(symbols, maxCount: 1, out var inputTruncated);

        Assert.True(inputTruncated);
        var clonedSymbol = Assert.Single(cloned);
        Assert.Equal("original-key", clonedSymbol.IdentityNameFolded);
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

    [Fact]
    public void MutationMaterializer_RecomputesNimIdentityAfterHookMutation_Issue4738()
    {
        var symbols = new List<SymbolRecord>
        {
            new()
            {
                FileId = 7,
                Kind = "function",
                Name = "renamed_proc",
                IdentityNameFolded = "stale",
                DisplayNameFolded = "stale",
                Line = 1,
                StartLine = 1,
                EndLine = 1,
            },
        };
        var references = new List<ReferenceRecord>
        {
            new()
            {
                FileId = 7,
                SymbolName = "renamedProc",
                IdentitySymbolNameFolded = "stale",
                ReferenceKind = "call",
                Line = 2,
                Column = 1,
                SpanLength = 4,
                ContainerName = "Run_Graph",
                IdentityContainerNameFolded = "stale",
                TargetQualifier = "pkg",
                SuppressInferredTargetQualifier = true,
            },
        };

        var clonedReferences = PostExtractionHookMutationMaterializer.CloneReferences(
            references,
            maxCount: null,
            out var referencesTruncated);
        Assert.False(referencesTruncated);
        var clonedReference = Assert.Single(clonedReferences);
        Assert.Equal("pkg", clonedReference.TargetQualifier);
        Assert.True(clonedReference.SuppressInferredTargetQualifier);
        Assert.Equal(4, clonedReference.SpanLength);

        PostExtractionHookMutationMaterializer.RefreshLanguageIdentity("nim", symbols);
        PostExtractionHookMutationMaterializer.RefreshLanguageIdentity("nim", clonedReferences);

        Assert.Equal("renamedproc", Assert.Single(symbols).IdentityNameFolded);
        Assert.Null(Assert.Single(symbols).DisplayNameFolded);
        var reference = Assert.Single(clonedReferences);
        Assert.Equal("renamedproc", reference.IdentitySymbolNameFolded);
        Assert.Equal("Rungraph", reference.IdentityContainerNameFolded);
        Assert.Equal("pkg", reference.TargetQualifier);
        Assert.True(reference.SuppressInferredTargetQualifier);
    }

    [Fact]
    public void MutationMaterializer_RecomputesCSharpExplicitInterfaceIdentityAfterHookMutation_Issue4866()
    {
        var symbols = new List<SymbolRecord>
        {
            new()
            {
                FileId = 7,
                Kind = "function",
                Name = "Run",
                Signature = "void IFoo.@Run()",
                IdentityNameFolded = "stale",
                Line = 1,
                StartLine = 1,
                EndLine = 1,
            },
            new()
            {
                FileId = 7,
                Kind = "function",
                Name = "Plain",
                Signature = "void Plain()",
                IdentityNameFolded = "stale",
                Line = 2,
                StartLine = 2,
                EndLine = 2,
            },
            new()
            {
                FileId = 7,
                Kind = "function",
                Name = "Execute",
                Signature = "void IFoo.Run()",
                IdentityNameFolded = "ifoo.run",
                DisplayNameFolded = "run",
                Line = 3,
                StartLine = 3,
                EndLine = 3,
            },
        };

        PostExtractionHookMutationMaterializer.RefreshLanguageIdentity("csharp", symbols);

        Assert.Equal("ifoo.run", symbols[0].IdentityNameFolded);
        Assert.Equal("run", symbols[0].DisplayNameFolded);
        Assert.Null(symbols[1].IdentityNameFolded);
        Assert.Null(symbols[1].DisplayNameFolded);
        Assert.Equal("ifoo.execute", symbols[2].IdentityNameFolded);
        Assert.Equal("execute", symbols[2].DisplayNameFolded);
    }
}
