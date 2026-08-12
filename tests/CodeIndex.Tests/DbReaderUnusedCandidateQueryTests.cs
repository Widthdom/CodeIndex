using CodeIndex.Database;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public partial class DbReaderTests
{
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
}
