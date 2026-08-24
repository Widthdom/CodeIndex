using CodeIndex.Database;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public partial class DbReaderTests
{
    [Fact]
    public void UnusedCandidateQueries_ExcludeCSharpTopLevelSyntheticEntryPoint_Issue5164()
    {
        var csharpFileId = CreateMixedCandidateFile("src/issue5164-unused.cs", "csharp", 1);
        var sqlFileId = CreateMixedCandidateFile("src/issue5164-unused.sql", "sql", 1);
        _writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = csharpFileId,
                Kind = "function",
                SubKind = SyntheticSymbolIdentity.CSharpTopLevelScopeSubKind,
                Name = SyntheticSymbolIdentity.CSharpTopLevelScopeName,
                Line = 1,
                StartLine = 1,
                EndLine = 1,
            },
            CreateMixedCandidate(sqlFileId, "dbo.ActuallyUnused", 1, 1),
        ]);

        var csharpOnly = _reader.GetUnusedSymbols(
            10, "function", "csharp", ["src/issue5164-unused.*"], null, excludeTests: false);
        var csharpOnlyCount = _reader.CountUnusedSymbols(
            "function", "csharp", ["src/issue5164-unused.*"], null, excludeTests: false);
        var mixed = _reader.GetUnusedSymbols(
            10, "function", null, ["src/issue5164-unused.*"], null, excludeTests: false);
        var mixedCount = _reader.CountUnusedSymbols(
            "function", null, ["src/issue5164-unused.*"], null, excludeTests: false);
        var detailedCount = _reader.CountUnusedSymbolsDetailed(
            "function", null, ["src/issue5164-unused.*"], null, excludeTests: false);

        Assert.Empty(csharpOnly);
        Assert.Equal(new QueryCountResult(0, 0), csharpOnlyCount);
        Assert.Equal("dbo.ActuallyUnused", Assert.Single(mixed).Name);
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), mixedCount);
        Assert.Equal(1, detailedCount.Count);
        Assert.Equal(1, detailedCount.FileCount);
        Assert.True(detailedCount.IncludesSql);
    }

    [Fact]
    public void UnusedCandidateQueries_MixedSqlScopePreservesProjectionAndCounts()
    {
        var paths = SeedMixedUnusedCandidates();

        var unused = _reader.GetUnusedSymbols(
            10, "function", null, paths, null, excludeTests: false);
        var count = _reader.CountUnusedSymbols(
            "function", null, paths, null, excludeTests: false);
        var detailedCount = _reader.CountUnusedSymbolsDetailed(
            "function", null, paths, null, excludeTests: false);

        Assert.Collection(unused, AssertMixedCSharpCandidate, AssertMixedSqlCandidate);
        Assert.Equal(new QueryCountResult(2, 2, IncludesSql: true), count);
        Assert.Equal(2, detailedCount.Count);
        Assert.Equal(2, detailedCount.FileCount);
        Assert.True(detailedCount.IncludesSql);
        Assert.Equal(1, detailedCount.BucketCounts["likely_unused_private"]);
        Assert.Equal(1, detailedCount.BucketCounts["maybe_unused_nonpublic"]);
    }

    [Fact]
    public void UnusedCandidateQueries_CSharpPartialMemberUseMatchesSqlResolverModes()
    {
        var paths = SeedMixedPartialFamilyCandidates();

        // The mixed scope contains a public SQL function, so SQL scope probing selects
        // the resolver-aware route before the private visibility filter removes that row.
        Assert.True(_reader.ScopeMayIncludeSqlSymbols(
            "function", null, paths, null, excludeTests: false));
        Assert.False(_reader.ScopeMayIncludeSqlSymbols(
            "function", "csharp", paths, null, excludeTests: false));
        var mixedResults = _reader.GetUnusedSymbols(
            1, "function", null, paths, null, excludeTests: false,
            visibilityFilters: ["private"]);
        var csharpResults = _reader.GetUnusedSymbols(
            1, "function", "csharp", paths, null, excludeTests: false,
            visibilityFilters: ["private"]);
        var mixedCount = _reader.CountUnusedSymbols(
            "function", null, paths, null, excludeTests: false,
            visibilityFilters: ["private"]);
        var csharpCount = _reader.CountUnusedSymbols(
            "function", "csharp", paths, null, excludeTests: false,
            visibilityFilters: ["private"]);
        var mixedDetailedCount = _reader.CountUnusedSymbolsDetailed(
            "function", null, paths, null, excludeTests: false,
            visibilityFilters: ["private"]);
        var csharpDetailedCount = _reader.CountUnusedSymbolsDetailed(
            "function", "csharp", paths, null, excludeTests: false,
            visibilityFilters: ["private"]);

        AssertUnusedResultParity(csharpResults, mixedResults);
        var candidate = Assert.Single(csharpResults);
        Assert.Equal("VisibleUnusedHelper", candidate.Name);
        Assert.DoesNotContain(mixedResults, result => result.Name == "HiddenPartialHandler");
        Assert.DoesNotContain(mixedResults, result => result.Lang == "sql");
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: false), csharpCount);
        Assert.Equal(csharpCount, mixedCount);
        AssertUnusedDetailedCountParity(csharpDetailedCount, mixedDetailedCount);
        Assert.Equal(
            (csharpCount.Count, csharpCount.FileCount, csharpCount.IncludesSql),
            (csharpDetailedCount.Count, csharpDetailedCount.FileCount, csharpDetailedCount.IncludesSql));
        Assert.False(mixedDetailedCount.IncludesSql);
    }

    private string[] SeedMixedUnusedCandidates()
    {
        var csharpFileId = CreateMixedCandidateFile("src/mixed_unused_scope.cs", "csharp", 12);
        var sqlFileId = CreateMixedCandidateFile("src/mixed_unused_scope.sql", "sql", 20);
        InsertMixedCandidateChunks(csharpFileId, sqlFileId);
        _writer.InsertSymbols(
        [
            CreateMixedCandidate(
                csharpFileId, "HiddenMixedCandidate", 7, 8,
                "private string HiddenMixedCandidate()", "private", "string",
                "class", "MixedCandidateHost", "Fixtures.MixedCandidateHost"),
            CreateMixedCandidate(
                sqlFileId, "dbo.UsedMixedCandidate", 3, 5,
                "CREATE PROCEDURE dbo.UsedMixedCandidate"),
            CreateMixedCandidate(sqlFileId, "dbo.UnusedMixedCandidate", 11, 14),
        ]);
        _writer.InsertReferences(
        [
            new ReferenceRecord
            {
                FileId = sqlFileId,
                SymbolName = "dbo.UsedMixedCandidate",
                ReferenceKind = "call",
                Line = 18,
                Column = 6,
                Context = "EXEC dbo.UsedMixedCandidate;",
            },
        ]);
        return ["src/mixed_unused_scope.*"];
    }

    private string[] SeedMixedPartialFamilyCandidates()
    {
        var ownerFileId = CreateMixedCandidateFile("src/mixed_partial_scope.owner.cs", "csharp", 6);
        var peerFileId = CreateMixedCandidateFile("src/mixed_partial_scope.peer.cs", "csharp", 5);
        var sqlFileId = CreateMixedCandidateFile("src/mixed_partial_scope.sql", "sql", 1);
        _writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = ownerFileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 6,
                Content = """
                    namespace Fixtures;
                    internal partial class MixedPartialHost
                    {
                        private void HiddenPartialHandler() { }
                        private void VisibleUnusedHelper() { }
                    }
                    """,
            },
            new ChunkRecord
            {
                FileId = peerFileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 5,
                Content = """
                    namespace Fixtures;
                    internal partial class MixedPartialHost
                    {
                        private void Wire() => HiddenPartialHandler();
                    }
                    """,
            },
            new ChunkRecord
            {
                FileId = sqlFileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 1,
                Content = "CREATE PROCEDURE dbo.PublicPartialScope AS SELECT 1;",
            },
        ]);
        _writer.InsertSymbols(
        [
            CreateMixedPartialType(ownerFileId, endLine: 6),
            CreateMixedPartialType(peerFileId, endLine: 5),
            CreateMixedCandidate(
                ownerFileId, "HiddenPartialHandler", 4, 4,
                "private void HiddenPartialHandler()", "private", "void",
                "class", "MixedPartialHost", "Fixtures.MixedPartialHost"),
            CreateMixedCandidate(
                ownerFileId, "VisibleUnusedHelper", 5, 5,
                "private void VisibleUnusedHelper()", "private", "void",
                "class", "MixedPartialHost", "Fixtures.MixedPartialHost"),
            CreateMixedCandidate(
                sqlFileId, "dbo.PublicPartialScope", 1, 1,
                "CREATE PROCEDURE dbo.PublicPartialScope", "public"),
        ]);
        return ["src/mixed_partial_scope.*"];
    }

    private static SymbolRecord CreateMixedPartialType(long fileId, int endLine) => new()
    {
        FileId = fileId,
        Kind = "class",
        Name = "MixedPartialHost",
        Line = 2,
        StartLine = 2,
        EndLine = endLine,
        Signature = "internal partial class MixedPartialHost",
        Visibility = "internal",
        ContainerKind = "namespace",
        ContainerName = "Fixtures",
        ContainerQualifiedName = "Fixtures",
    };

    private void InsertMixedCandidateChunks(long csharpFileId, long sqlFileId) =>
        _writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = csharpFileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 12,
                Content = "private string HiddenMixedCandidate() => string.Empty;",
            },
            new ChunkRecord
            {
                FileId = sqlFileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 20,
                Content = "CREATE PROCEDURE dbo.UsedMixedCandidate AS SELECT 1;",
            },
        ]);

    private long CreateMixedCandidateFile(string path, string lang, int lines) =>
        _writer.UpsertFile(new FileRecord
        {
            Path = path,
            Lang = lang,
            Size = 200,
            Lines = lines,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });

    private static SymbolRecord CreateMixedCandidate(
        long fileId,
        string name,
        int startLine,
        int endLine,
        string? signature = null,
        string? visibility = null,
        string? returnType = null,
        string? containerKind = null,
        string? containerName = null,
        string? containerQualifiedName = null) => new()
        {
            FileId = fileId,
            Kind = "function",
            Name = name,
            Line = startLine,
            StartLine = startLine,
            EndLine = endLine,
            Signature = signature,
            Visibility = visibility,
            ReturnType = returnType,
            ContainerKind = containerKind,
            ContainerName = containerName,
            ContainerQualifiedName = containerQualifiedName,
        };

    private static void AssertMixedCSharpCandidate(UnusedSymbolResult candidate)
    {
        Assert.Equal("src/mixed_unused_scope.cs", candidate.Path);
        Assert.Equal("csharp", candidate.Lang);
        Assert.Equal("function", candidate.Kind);
        Assert.Equal("HiddenMixedCandidate", candidate.Name);
        Assert.Equal((7, 7, 8), (candidate.Line, candidate.StartLine, candidate.EndLine));
        Assert.Equal("private string HiddenMixedCandidate()", candidate.Signature);
        Assert.Equal("private", candidate.Visibility);
        Assert.Equal("string", candidate.ReturnType);
        Assert.Equal("class", candidate.ContainerKind);
        Assert.Equal("MixedCandidateHost", candidate.ContainerName);
        Assert.Equal("likely_unused_private", candidate.UnusedBucket);
    }

    private static void AssertMixedSqlCandidate(UnusedSymbolResult candidate)
    {
        Assert.Equal("src/mixed_unused_scope.sql", candidate.Path);
        Assert.Equal("sql", candidate.Lang);
        Assert.Equal("function", candidate.Kind);
        Assert.Equal("dbo.UnusedMixedCandidate", candidate.Name);
        Assert.Equal((11, 11, 14), (candidate.Line, candidate.StartLine, candidate.EndLine));
        Assert.Null(candidate.Signature);
        Assert.Null(candidate.Visibility);
        Assert.Null(candidate.ReturnType);
        Assert.Null(candidate.ContainerKind);
        Assert.Null(candidate.ContainerName);
        Assert.Equal("maybe_unused_nonpublic", candidate.UnusedBucket);
    }

    private static void AssertUnusedResultParity(
        IReadOnlyList<UnusedSymbolResult> expected,
        IReadOnlyList<UnusedSymbolResult> actual)
    {
        static object Project(UnusedSymbolResult result) => new
        {
            result.Path,
            result.Lang,
            result.Kind,
            result.Name,
            result.Line,
            result.StartLine,
            result.EndLine,
            result.Signature,
            result.Visibility,
            result.ReturnType,
            result.ContainerKind,
            result.ContainerName,
            result.UnusedBucket,
            result.UnusedConfidence,
            result.UnusedReason,
            ReasonTags = string.Join('\n', result.UnusedReasonTags ?? []),
            result.UnusedContractDomain,
            ContractDomainTags = string.Join('\n', result.UnusedContractDomainTags ?? []),
        };

        Assert.Equal(expected.Select(Project), actual.Select(Project));
    }

    private static void AssertUnusedDetailedCountParity(
        UnusedCountResult expected,
        UnusedCountResult actual)
    {
        Assert.Equal(
            (expected.Count, expected.FileCount, expected.IncludesSql),
            (actual.Count, actual.FileCount, actual.IncludesSql));
        Assert.Equal(
            expected.BucketCounts.OrderBy(pair => pair.Key),
            actual.BucketCounts.OrderBy(pair => pair.Key));
        Assert.Equal(
            expected.ConfidenceCounts.OrderBy(pair => pair.Key),
            actual.ConfidenceCounts.OrderBy(pair => pair.Key));
        Assert.Equal(
            expected.ContractDomainCounts.OrderBy(pair => pair.Key),
            actual.ContractDomainCounts.OrderBy(pair => pair.Key));
    }
}
