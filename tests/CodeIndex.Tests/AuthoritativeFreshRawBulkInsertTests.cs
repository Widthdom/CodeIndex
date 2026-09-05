using System.Globalization;
using System.Text;
using CodeIndex.Database;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public sealed class AuthoritativeFreshRawBulkInsertTests : IDisposable
{
    private const long ExpectedLargeFileId = 5_000_000_001L;
    private readonly string _projectRoot;
    private readonly DbContext _db;
    private readonly DbWriter _writer;

    public AuthoritativeFreshRawBulkInsertTests()
    {
        _projectRoot = TestProjectHelper.CreateTempProject("cdidx_authoritative_fresh_raw");
        _db = new DbContext(
            DbOpenIntent.WriteIndex,
            Path.Combine(_projectRoot, "codeindex.db"));
        _db.InitializeSchema();
        _writer = new DbWriter(_db.Connection);
    }

    [Fact]
    public void Scope_RequiresFreshCallerOwnedTransactionAndDetachesAfterCompletion()
    {
        Assert.Null(_writer.BeginAuthoritativeFreshBulkInsertScope(
            enabled: false,
            CancellationToken.None));

        using (var transaction = _writer.BeginTransaction())
        {
            Assert.Throws<InvalidOperationException>(() =>
                _writer.BeginAuthoritativeFreshBulkInsertScope(
                    enabled: true,
                    CancellationToken.None));
        }

        using (var ordinaryGraph = _writer.BeginReferenceGraphRefreshScope(
                   forceFullRefresh: true))
        using (var transaction = _writer.BeginTransaction())
        {
            Assert.Throws<InvalidOperationException>(() =>
                _writer.BeginAuthoritativeFreshBulkInsertScope(
                    enabled: true,
                    CancellationToken.None));
        }

        DbWriter.AuthoritativeFreshRawInsertScopeStats? observedStats = null;
        var previousStatsHook = DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting;
        try
        {
            DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting = stats =>
            {
                observedStats = stats;
                previousStatsHook?.Invoke(stats);
            };

            using var freshGraph = _writer.BeginReferenceGraphRefreshScope(
                forceFullRefresh: true,
                useFreshReferenceResolutionDefaults: true);
            Assert.Throws<InvalidOperationException>(() =>
                _writer.BeginAuthoritativeFreshBulkInsertScope(
                    enabled: true,
                    CancellationToken.None));

            using var transaction = _writer.BeginTransaction();
            Assert.True(_writer.CanUseFreshReferenceResolutionDefaultsInCurrentTransaction());
            using var raw = _writer.BeginAuthoritativeFreshBulkInsertScope(
                enabled: true,
                CancellationToken.None)!;
            Assert.Throws<InvalidOperationException>(() =>
                _writer.BeginAuthoritativeFreshBulkInsertScope(
                    enabled: true,
                    CancellationToken.None));

            var fileId = InsertNewFile("src/scope.cs");
            Exception? foreignThreadException = null;
            using var foreignThreadFinished = new ManualResetEventSlim();
            var foreignThread = new Thread(() =>
            {
                foreignThreadException = Record.Exception(() =>
                    _writer.InsertChunks(CreateChunks(fileId, startIndex: 0, count: 1)));
                foreignThreadFinished.Set();
            });
            foreignThread.Start();
            Assert.True(foreignThreadFinished.Wait(TimeSpan.FromSeconds(5)));
            var ownershipException = Assert.IsType<InvalidOperationException>(foreignThreadException);
            Assert.Contains(
                "owned by this DbWriter",
                ownershipException.Message,
                StringComparison.Ordinal);
            raw.Complete();
            raw.Complete();

            // The completed scope is detached before graph/index work and ordinary provider
            // writes on the same transaction remain usable.
            _writer.InsertIssuesForNewFile(fileId, [CreateIssue(1)]);
            transaction.Commit();
        }
        finally
        {
            DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting = previousStatsHook;
        }

        Assert.NotNull(observedStats);
        Assert.True(observedStats.Completed);
        Assert.Equal(1, observedStats.PrepareCount);
        Assert.Equal(1, observedStats.FinalizeCount);
        Assert.Equal(1L, ScalarLong("SELECT COUNT(*) FROM file_issues"));
    }

    [Fact]
    public void Scope_CoalescesResourceGenerationAndRestoresTriggersAcrossCommitAndRollback()
    {
        var initialGeneration = ResourceListGeneration();
        Assert.Equal(3L, ResourceListGenerationTriggerCount());

        using (var freshGraph = _writer.BeginReferenceGraphRefreshScope(
                   forceFullRefresh: true,
                   useFreshReferenceResolutionDefaults: true))
        using (var transaction = _writer.BeginTransaction())
        using (var raw = _writer.BeginAuthoritativeFreshBulkInsertScope(
                   enabled: true,
                   CancellationToken.None)!)
        {
            _ = InsertNewFile("src/generation-rolled-back.cs");
        }

        Assert.Equal(initialGeneration, ResourceListGeneration());
        Assert.Equal(3L, ResourceListGenerationTriggerCount());
        Assert.Equal(
            0L,
            ScalarLong("SELECT COUNT(*) FROM files WHERE path = 'src/generation-rolled-back.cs'"));
        Assert.Equal(
            0L,
            ScalarLong($"""
                SELECT COUNT(*)
                FROM temp.sqlite_schema
                WHERE name = '{DbWriter.AuthoritativeFreshReferenceSourceSymbolsTableName}'
                """));

        using (var freshGraph = _writer.BeginReferenceGraphRefreshScope(
                   forceFullRefresh: true,
                   useFreshReferenceResolutionDefaults: true))
        using (var transaction = _writer.BeginTransaction())
        using (var raw = _writer.BeginAuthoritativeFreshBulkInsertScope(
                   enabled: true,
                   CancellationToken.None)!)
        {
            raw.Complete();
            transaction.Commit();
        }

        Assert.Equal(initialGeneration, ResourceListGeneration());
        Assert.Equal(3L, ResourceListGenerationTriggerCount());

        using (var freshGraph = _writer.BeginReferenceGraphRefreshScope(
                   forceFullRefresh: true,
                   useFreshReferenceResolutionDefaults: true))
        using (var transaction = _writer.BeginTransaction())
        using (var raw = _writer.BeginAuthoritativeFreshBulkInsertScope(
                   enabled: true,
                   CancellationToken.None)!)
        {
            _ = InsertNewFile("src/generation-a.cs");
            _ = InsertNewFile("src/generation-b.cs");
            _ = InsertNewFile("src/generation-c.cs");
            raw.Complete();
            transaction.Commit();
        }

        Assert.Equal(initialGeneration + 1, ResourceListGeneration());
        Assert.Equal(3L, ResourceListGenerationTriggerCount());

        _ = _writer.UpsertFile(new FileRecord
        {
            Path = "src/generation-provider.cs",
            Lang = "csharp",
            Size = 100,
            Lines = 10,
            Checksum = "generation-provider",
            Modified = new DateTime(2026, 8, 23, 0, 0, 0, DateTimeKind.Utc),
        });
        Assert.Equal(initialGeneration + 2, ResourceListGeneration());
    }

    [Fact]
    public void BatchStatements_PreserveShapesUnicodeNullsInt64AndProviderExclusions()
    {
        PrimeSequencesForInt64Ids();
        string[] textValues = [
            "雪😀a\0β",
            string.Empty,
            new string('a', 1024),
            new string('雪', 342),
            string.Concat(Enumerable.Repeat("😀\0雪", 1000)),
            "unpaired\ud800surrogate\udc00",
            "short after pooled buffer",
        ];
        var rawWork = new List<DbWriter.AuthoritativeFreshRawInsertWork>();
        var batchWork = new List<DbWriter.DbWriterBatchStatement>();
        DbWriter.AuthoritativeFreshRawInsertScopeStats? observedStats = null;
        var previousRawHook = DbWriter.AuthoritativeFreshRawInsertExecutingForTesting;
        var previousBatchHook = DbWriter.BatchStatementExecutingForTesting;
        var previousStatsHook = DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting;
        try
        {
            DbWriter.AuthoritativeFreshRawInsertExecutingForTesting = work =>
            {
                rawWork.Add(work);
                previousRawHook?.Invoke(work);
            };
            DbWriter.BatchStatementExecutingForTesting = work =>
            {
                batchWork.Add(work);
                previousBatchHook?.Invoke(work);
            };
            DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting = stats =>
            {
                observedStats = stats;
                previousStatsHook?.Invoke(stats);
            };

            using var graph = _writer.BeginReferenceGraphRefreshScope(
                forceFullRefresh: true,
                useFreshReferenceResolutionDefaults: true);
            using var transaction = _writer.BeginTransaction();
            Assert.True(_writer.CanUseFreshReferenceResolutionDefaultsInCurrentTransaction());
            using var raw = _writer.BeginAuthoritativeFreshBulkInsertScope(
                enabled: true,
                CancellationToken.None)!;
            var fileId = _writer.InsertNewFile(new FileRecord
            {
                Path = "src/raw-shapes.cs",
                Lang = null,
                Size = 5_000_000_123L,
                Lines = 100,
                Checksum = "雪😀a\0β",
                Modified = new DateTime(2026, 8, 23, 12, 34, 56, 789, DateTimeKind.Utc)
                    .AddTicks(1234),
            });
            Assert.Equal(ExpectedLargeFileId, fileId);

            var chunks = Enumerable.Range(0, 205)
                .Select(index => new ChunkRecord
                {
                    FileId = fileId,
                    ChunkIndex = index,
                    StartLine = index + 1,
                    EndLine = index + 1,
                    Content = index < textValues.Length ? textValues[index] : $"chunk_{index}",
                })
                .ToArray();
            var symbols = Enumerable.Range(0, 41)
                .Select(index => new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "function",
                    Name = $"target_{index}",
                    Line = index + 1,
                    StartLine = index + 1,
                    EndLine = index + 1,
                    StartColumn = index == 0 ? null : index,
                    Signature = index == 0 ? null : $"void target_{index}()",
                    IsPartialDeclaration = index == 0 ? null : false,
                    IsMetadataTarget = index == 0 ? null : false,
                })
                .ToArray();
            var issues = Enumerable.Range(0, 171)
                .Select(index => CreateIssue(index + 1))
                .ToArray();
            var references = Enumerable.Range(0, 73)
                .Select(index => new ReferenceRecord
                {
                    FileId = fileId,
                    SymbolName = $"target_{index % symbols.Length}",
                    ReferenceKind = "call",
                    Line = index + 1,
                    Column = index + 1,
                    SpanLength = index == 0 ? 0 : index + 1,
                    Context = index == 0
                        ? "雪😀a\0β"
                        : $"target_{index % symbols.Length}();",
                    ContainerKind = index == 0 ? null : "function",
                    ContainerName = index == 0 ? null : "caller",
                    IsSelfReference = true,
                    IsMutualRecursion = true,
                })
                .ToArray();

            _writer.InsertChunks(chunks, CancellationToken.None);
            _writer.InsertSymbols(symbols, CancellationToken.None);
            _writer.InsertIssuesForNewFile(fileId, issues);
            _writer.InsertReferencesForNewFilesInAtomicFileScope(
                references,
                refreshMutualRecursionFlags: false,
                CancellationToken.None);

            var rawCountBeforeProviderExclusions = rawWork.Count;
            var providerFileId = _writer.UpsertFile(new FileRecord
            {
                Path = "src/provider-exclusions.cs",
                Lang = "csharp",
                Size = 100,
                Lines = 100,
                Checksum = "provider-exclusions",
                Modified = new DateTime(2026, 8, 23, 0, 0, 0, DateTimeKind.Utc),
            });
            _writer.InsertIssues(providerFileId, [CreateIssue(1)]);
            _writer.InsertReferencesInAtomicFileScope(
                [new ReferenceRecord
                {
                    FileId = providerFileId,
                    SymbolName = "provider_target",
                    ReferenceKind = "call",
                    Line = 1,
                    Column = 1,
                    Context = "provider_target();",
                }],
                refreshMutualRecursionFlags: false,
                CancellationToken.None);
            Assert.Equal(rawCountBeforeProviderExclusions, rawWork.Count);

            raw.Complete();
            transaction.Commit();
        }
        finally
        {
            DbWriter.AuthoritativeFreshRawInsertExecutingForTesting = previousRawHook;
            DbWriter.BatchStatementExecutingForTesting = previousBatchHook;
            DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting = previousStatsHook;
        }

        Assert.Equal([(1, 7)], RowsAndParameters("insert_files"));
        Assert.Equal([(102, 510), (102, 510), (1, 5)], RowsAndParameters("insert_chunks"));
        Assert.Equal([(20, 500), (20, 500), (1, 25)], RowsAndParameters("insert_symbols"));
        Assert.Equal([(85, 510), (85, 510), (1, 6)], RowsAndParameters("insert_issues"));
        Assert.Equal([(1, 0)], RowsAndParameters("read_reference_line_id_floor"));
        Assert.Equal([(73, 220)], RowsAndParameters("insert_reference_lines"));
        Assert.Equal([(36, 504), (36, 504), (1, 14)], RowsAndParameters("insert_references"));
        Assert.Contains(
            batchWork,
            work => work.Operation == "insert_reference_lines" && work.StatementRows == 73);
        Assert.DoesNotContain(
            batchWork,
            work => work.Operation == "insert_files");

        Assert.NotNull(observedStats);
        Assert.True(observedStats.Completed);
        Assert.Equal(32, observedStats.Capacity);
        Assert.Equal(11, observedStats.PeakCachedStatementCount);
        Assert.Equal(15, observedStats.StatementExecutionCount);
        Assert.Equal(11, observedStats.PrepareCount);
        Assert.Equal(4, observedStats.CacheHitCount);
        Assert.Equal(0, observedStats.EvictionCount);
        Assert.Equal(0, observedStats.DiscardCount);
        Assert.Equal(11, observedStats.FinalizeCount);

        Assert.Equal(1L, ScalarLong("SELECT COUNT(*) FROM files WHERE lang IS NULL"));
        Assert.Equal(5_000_000_123L, ScalarLong("SELECT size FROM files WHERE path = 'src/raw-shapes.cs'"));
        Assert.Equal("323032362D30382D32332031323A33343A35362E37383931323334", ScalarString("SELECT hex(CAST(modified AS BLOB)) FROM files WHERE path = 'src/raw-shapes.cs'"));
        Assert.Equal("E99BAAF09F98806100CEB2", ScalarString("SELECT hex(CAST(checksum AS BLOB)) FROM files WHERE path = 'src/raw-shapes.cs'"));
        Assert.Equal("E99BAAF09F98806100CEB2", ScalarString("SELECT hex(CAST(content AS BLOB)) FROM chunks WHERE chunk_index = 0"));
        Assert.Equal(11L, ScalarLong("SELECT length(CAST(content AS BLOB)) FROM chunks WHERE chunk_index = 0"));
        for (var index = 0; index < textValues.Length; index++)
        {
            Assert.Equal("text", ScalarString($"SELECT typeof(content) FROM chunks WHERE chunk_index = {index}"));
            Assert.Equal(
                Convert.ToHexString(Encoding.UTF8.GetBytes(textValues[index])),
                ScalarString($"SELECT hex(CAST(content AS BLOB)) FROM chunks WHERE chunk_index = {index}"));
        }
        Assert.Equal(41L, ScalarLong($"SELECT COUNT(*) FROM symbols WHERE file_id = {ExpectedLargeFileId.ToString(CultureInfo.InvariantCulture)}"));
        Assert.Equal(1L, ScalarLong("SELECT COUNT(*) FROM symbols WHERE sub_kind IS NULL AND signature IS NULL AND start_column IS NULL"));
        Assert.Equal(171L, ScalarLong($"SELECT COUNT(*) FROM file_issues WHERE file_id = {ExpectedLargeFileId.ToString(CultureInfo.InvariantCulture)}"));
        Assert.Equal(ExpectedLargeFileId, ScalarLong("SELECT MIN(id) FROM reference_lines"));
        Assert.Equal("E99BAAF09F98806100CEB2", ScalarString("SELECT hex(CAST(context AS BLOB)) FROM reference_lines WHERE line = 1"));
        Assert.Equal(73L, ScalarLong($"SELECT COUNT(*) FROM symbol_references WHERE file_id = {ExpectedLargeFileId.ToString(CultureInfo.InvariantCulture)}"));
        Assert.Equal(73L, ScalarLong($"SELECT COUNT(*) FROM symbol_references WHERE file_id = {ExpectedLargeFileId.ToString(CultureInfo.InvariantCulture)} AND context IS NULL AND is_self_reference = 0 AND is_mutual_recursion = 0"));

        (int Rows, int Parameters)[] RowsAndParameters(string operation) =>
            rawWork
                .Where(work => work.Operation == operation)
                .Select(work => (work.StatementRows, work.BoundParameterCount))
                .ToArray();
    }

    [Fact]
    public void ChunkTextBindings_ReuseScratchSpaceForLongUtf8Values()
    {
        using var graph = _writer.BeginReferenceGraphRefreshScope(
            forceFullRefresh: true,
            useFreshReferenceResolutionDefaults: true);
        using var transaction = _writer.BeginTransaction();
        using var raw = _writer.BeginAuthoritativeFreshBulkInsertScope(true, CancellationToken.None)!;
        var fileId = InsertNewFile("src/pooled-text.py");
        var text = new string('雪', 2048);
        var warmup = CreateChunks(fileId, startIndex: 0, count: 102);
        var measured = CreateChunks(fileId, startIndex: 102, count: 102);
        foreach (var chunk in warmup.Concat(measured))
            chunk.Content = text;

        _writer.InsertChunks(warmup);
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        _writer.InsertChunks(measured);
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        // Per-value UTF-8 arrays alone would exceed 600 KiB for this one batch.
        // Allow ample bookkeeping overhead without a timing/GC-collection assertion.
        Assert.InRange(allocatedBytes, 0L, 128 * 1024L);
        Assert.Equal(204L, ScalarLong("SELECT COUNT(*) FROM chunks"));
        Assert.Equal(text, ScalarString("SELECT content FROM chunks ORDER BY id DESC LIMIT 1"));
        raw.Complete();
        transaction.Commit();
    }

    [Theory]
    [InlineData("csharp", "cs")]
    [InlineData("python", "py")]
    [InlineData("javascript", "js")]
    [InlineData("typescript", "ts")]
    [InlineData("java", "java")]
    [InlineData("go", "go")]
    [InlineData("rust", "rs")]
    [InlineData("cpp", "cpp")]
    public void ReferenceSourceLookup_PreservesMultiFileFoldFallbackAndNestedRankingAcrossBatches(
        string language,
        string extension)
    {
        long firstFileId;
        long secondFileId;
        long firstNestedSourceId;
        long aliasSourceId;
        long duplicateProbeSourceId;
        long legacyAsciiSourceId;
        long secondNestedSourceId;

        using (var graph = _writer.BeginReferenceGraphRefreshScope(
                   forceFullRefresh: true,
                   useFreshReferenceResolutionDefaults: true))
        using (var transaction = _writer.BeginTransaction())
        using (var raw = _writer.BeginAuthoritativeFreshBulkInsertScope(
                   enabled: true,
                   CancellationToken.None)!)
        {
            firstFileId = InsertNewFile($"src/source-lookup-a.{extension}", language);
            secondFileId = InsertNewFile($"src/source-lookup-b.{extension}", language);
            _writer.InsertSymbols([
                SourceSymbol(firstFileId, "Caller", line: 1, startLine: 1, endLine: 100),
                SourceSymbol(firstFileId, "Caller", line: 10, startLine: 10, endLine: 30),
                SourceSymbol(firstFileId, "Caller", line: 10, startLine: 10, endLine: 30),
                SourceSymbol(
                    firstFileId,
                    "Canonical",
                    line: 40,
                    startLine: 40,
                    endLine: 55,
                    displayNameFolded: "displayalias"),
                SourceSymbol(
                    firstFileId,
                    "Dup",
                    line: 56,
                    startLine: 56,
                    endLine: 65,
                    displayNameFolded: "dup"),
                SourceSymbol(firstFileId, "LegacyASCII", line: 66, startLine: 66, endLine: 75),
                SourceSymbol(firstFileId, "ÅLegacy", line: 76, startLine: 76, endLine: 85),
                SourceSymbol(secondFileId, "Caller", line: 1, startLine: 1, endLine: 100),
                SourceSymbol(secondFileId, "Caller", line: 50, startLine: 50, endLine: 60),
            ]);
            Execute($"""
                UPDATE symbols
                SET name_folded = NULL
                WHERE file_id = {firstFileId}
                  AND name IN ('LegacyASCII', 'ÅLegacy')
                """);

            firstNestedSourceId = ScalarLong($"""
                SELECT MIN(id)
                FROM symbols
                WHERE file_id = {firstFileId}
                  AND name = 'Caller'
                  AND start_line = 10
                """);
            aliasSourceId = ScalarLong($"""
                SELECT id FROM symbols
                WHERE file_id = {firstFileId} AND name = 'Canonical'
                """);
            duplicateProbeSourceId = ScalarLong($"""
                SELECT id FROM symbols
                WHERE file_id = {firstFileId} AND name = 'Dup'
                """);
            legacyAsciiSourceId = ScalarLong($"""
                SELECT id FROM symbols
                WHERE file_id = {firstFileId} AND name = 'LegacyASCII'
                """);
            secondNestedSourceId = ScalarLong($"""
                SELECT id FROM symbols
                WHERE file_id = {secondFileId}
                  AND name = 'Caller'
                  AND start_line = 50
                """);

            var references = Enumerable.Range(0, 40)
                .Select(index => SourceReference(
                    firstFileId,
                    $"nested_probe_{index}",
                    line: 15,
                    containerName: "Caller"))
                .ToList();
            references.Add(SourceReference(
                firstFileId,
                "display_probe",
                line: 45,
                containerName: "DisplayAlias"));
            references.Add(SourceReference(
                firstFileId,
                "duplicate_probe",
                line: 60,
                containerName: "Dup"));
            references.Add(SourceReference(
                firstFileId,
                "legacy_ascii_probe",
                line: 70,
                containerName: "legacyascii"));
            references.Add(SourceReference(
                firstFileId,
                "legacy_unicode_probe",
                line: 80,
                containerName: "ålegacy"));
            references.Add(SourceReference(
                firstFileId,
                "null_container_probe",
                line: 90,
                containerName: null));
            references.Add(SourceReference(
                firstFileId,
                "empty_container_probe",
                line: 91,
                containerName: string.Empty));
            references.Add(SourceReference(
                secondFileId,
                "second_file_probe",
                line: 55,
                containerName: "Caller"));

            _writer.InsertReferencesForNewFilesInAtomicFileScope(
                references,
                refreshMutualRecursionFlags: false,
                CancellationToken.None);

            Assert.Equal(
                2L,
                ScalarLong($"""
                    SELECT COUNT(DISTINCT file_id)
                    FROM temp.{DbWriter.AuthoritativeFreshReferenceSourceSymbolsTableName}
                    """));
            Assert.Equal(
                9L,
                ScalarLong($"""
                    SELECT COUNT(*)
                    FROM temp.{DbWriter.AuthoritativeFreshReferenceSourceSymbolsTableName}
                    """));
            Assert.Equal(
                3L,
                ScalarLong("""
                    SELECT COUNT(*)
                    FROM temp.sqlite_schema
                    WHERE type = 'index'
                      AND name LIKE 'idx_authoritative_fresh_source_%'
                    """));

            raw.Complete();
            transaction.Commit();
        }

        Assert.Equal(
            40L,
            ScalarLong($"""
                SELECT COUNT(*)
                FROM symbol_references
                WHERE symbol_name LIKE 'nested_probe_%'
                  AND source_symbol_id = {firstNestedSourceId}
                """));
        Assert.Equal(aliasSourceId, SourceSymbolId("display_probe"));
        Assert.Equal(duplicateProbeSourceId, SourceSymbolId("duplicate_probe"));
        Assert.Equal(legacyAsciiSourceId, SourceSymbolId("legacy_ascii_probe"));
        Assert.Null(SourceSymbolId("legacy_unicode_probe"));
        Assert.Null(SourceSymbolId("null_container_probe"));
        Assert.Null(SourceSymbolId("empty_container_probe"));
        Assert.Equal(secondNestedSourceId, SourceSymbolId("second_file_probe"));

        const string sourceSnapshotSql = """
            SELECT group_concat(COALESCE(source_symbol_id, 'null'), '|')
            FROM (SELECT source_symbol_id FROM symbol_references ORDER BY id)
            """;
        var freshSources = ScalarString(sourceSnapshotSql);
        _writer.RefreshMutualRecursionFlags(stampReferenceIdentityContractReady: false);
        Assert.Equal(freshSources, ScalarString(sourceSnapshotSql));

        static SymbolRecord SourceSymbol(
            long fileId,
            string name,
            int line,
            int startLine,
            int endLine,
            string? displayNameFolded = null)
            => new()
            {
                FileId = fileId,
                Kind = "function",
                Name = name,
                Line = line,
                StartLine = startLine,
                EndLine = endLine,
                DisplayNameFolded = displayNameFolded,
            };

        static ReferenceRecord SourceReference(
            long fileId,
            string symbolName,
            int line,
            string? containerName)
            => new()
            {
                FileId = fileId,
                SymbolName = symbolName,
                ReferenceKind = "call",
                Line = line,
                Column = 1,
                Context = $"{symbolName}();",
                ContainerKind = containerName == null ? null : "function",
                ContainerName = containerName,
            };
    }

    [Fact]
    public void ReferenceSourceLookup_QueryPlansUseRetainedAndPartialIndexesWithoutSourceScans()
    {
        using var graph = _writer.BeginReferenceGraphRefreshScope(
            forceFullRefresh: true,
            useFreshReferenceResolutionDefaults: true);
        using var transaction = _writer.BeginTransaction();
        using var raw = _writer.BeginAuthoritativeFreshBulkInsertScope(
            enabled: true,
            CancellationToken.None)!;

        var materializationPlan = ExplainQueryPlan(
            DbWriter.PopulateAuthoritativeFreshReferenceSourceLookupSqlForTesting,
            command => command.Parameters.AddWithValue("$file_id", 1L));
        Assert.Contains(
            materializationPlan,
            detail => detail.Contains("idx_symbols_file", StringComparison.Ordinal));
        Assert.DoesNotContain(
            materializationPlan,
            detail => detail.Contains("SCAN persisted", StringComparison.OrdinalIgnoreCase));

        var sourceValueSql =
            DbWriter.BuildMaterializedFreshReferenceSourceSymbolValueSqlForTesting("r");
        var sourceLookupPlan = ExplainQueryPlan($"""
            WITH reference_row(file_id, line, container_name, container_name_folded) AS (
                VALUES (1, 15, 'Caller', 'caller')
            )
            SELECT {sourceValueSql}
            FROM reference_row AS r
            """);
        Assert.Contains(
            sourceLookupPlan,
            detail => detail.Contains(
                "idx_authoritative_fresh_source_name_folded",
                StringComparison.Ordinal));
        Assert.Contains(
            sourceLookupPlan,
            detail => detail.Contains(
                "idx_authoritative_fresh_source_display_name_folded",
                StringComparison.Ordinal));
        Assert.Contains(
            sourceLookupPlan,
            detail => detail.Contains(
                "idx_authoritative_fresh_source_name_nocase",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            sourceLookupPlan,
            detail => detail.Contains("SCAN source", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            sourceLookupPlan,
            detail => detail.Contains("UNION USING TEMP B-TREE", StringComparison.OrdinalIgnoreCase));

        raw.Complete();
        transaction.Commit();

        IReadOnlyList<string> ExplainQueryPlan(
            string sql,
            Action<SqliteCommand>? bind = null)
        {
            using var command = _db.Connection.CreateCommand();
            command.CommandText = "EXPLAIN QUERY PLAN " + sql;
            bind?.Invoke(command);
            using var reader = command.ExecuteReader();
            var plan = new List<string>();
            while (reader.Read())
                plan.Add(reader.GetString(3));
            return plan;
        }
    }

    [Fact]
    public void ReferenceSourceLookup_FileSavepointRollbackRestoresPreviousRowsAndReprepares()
    {
        long firstFileId;
        long thirdFileId;
        using (var graph = _writer.BeginReferenceGraphRefreshScope(
                   forceFullRefresh: true,
                   useFreshReferenceResolutionDefaults: true))
        using (var outerTransaction = _writer.BeginTransaction())
        using (var raw = _writer.BeginAuthoritativeFreshBulkInsertScope(
                   enabled: true,
                   CancellationToken.None)!)
        {
            using (var firstFile = _writer.BeginTransaction())
            {
                firstFileId = InsertNewFile("src/source-savepoint-a.cs");
                _writer.InsertSymbols([
                    CreateSourceSymbol(firstFileId, "CallerA"),
                ]);
                _writer.InsertReferencesForNewFilesInAtomicFileScope(
                    [CreateSourceReference(firstFileId, "first_probe", "CallerA")],
                    refreshMutualRecursionFlags: false,
                    CancellationToken.None);
                firstFile.Commit();
            }

            Assert.Equal(
                1L,
                ScalarLong($"""
                    SELECT COUNT(*)
                    FROM temp.{DbWriter.AuthoritativeFreshReferenceSourceSymbolsTableName}
                    WHERE file_id = {firstFileId}
                    """));

            using (var failedFile = _writer.BeginTransaction())
            {
                var failedFileId = InsertNewFile("src/source-savepoint-failed.cs");
                _writer.InsertSymbols([
                    CreateSourceSymbol(failedFileId, "FailedCaller"),
                ]);
                Execute($"""
                    CREATE TEMP TRIGGER reject_materialized_source_reference
                    BEFORE INSERT ON symbol_references
                    WHEN NEW.file_id = {failedFileId}
                    BEGIN
                        SELECT RAISE(ABORT, 'reject materialized source reference');
                    END;
                    """);
                Assert.Throws<SqliteException>(() =>
                    _writer.InsertReferencesForNewFilesInAtomicFileScope(
                        [CreateSourceReference(
                            failedFileId,
                            "failed_probe",
                            "FailedCaller")],
                        refreshMutualRecursionFlags: false,
                        CancellationToken.None));
            }

            Assert.Equal(
                0L,
                ScalarLong("""
                    SELECT COUNT(*) FROM files
                    WHERE path = 'src/source-savepoint-failed.cs'
                    """));
            Assert.Equal(
                1L,
                ScalarLong($"""
                    SELECT COUNT(*)
                    FROM temp.{DbWriter.AuthoritativeFreshReferenceSourceSymbolsTableName}
                    WHERE file_id = {firstFileId}
                    """));

            using (var thirdFile = _writer.BeginTransaction())
            {
                thirdFileId = InsertNewFile("src/source-savepoint-c.cs");
                _writer.InsertSymbols([
                    CreateSourceSymbol(thirdFileId, "CallerC"),
                ]);
                _writer.InsertReferencesForNewFilesInAtomicFileScope(
                    [CreateSourceReference(thirdFileId, "third_probe", "CallerC")],
                    refreshMutualRecursionFlags: false,
                    CancellationToken.None);
                thirdFile.Commit();
            }

            raw.Complete();
            outerTransaction.Commit();
        }

        Assert.Equal(firstFileId, SourceFileId("first_probe"));
        Assert.Equal(thirdFileId, SourceFileId("third_probe"));
        Assert.Equal(
            0L,
            ScalarLong("""
                SELECT COUNT(*) FROM symbol_references
                WHERE symbol_name = 'failed_probe'
                """));

        static SymbolRecord CreateSourceSymbol(long fileId, string name)
            => new()
            {
                FileId = fileId,
                Kind = "function",
                Name = name,
                Line = 1,
                StartLine = 1,
                EndLine = 10,
            };

        static ReferenceRecord CreateSourceReference(
            long fileId,
            string symbolName,
            string containerName)
            => new()
            {
                FileId = fileId,
                SymbolName = symbolName,
                ReferenceKind = "call",
                Line = 5,
                Column = 1,
                Context = $"{symbolName}();",
                ContainerKind = "function",
                ContainerName = containerName,
            };
    }

    [Fact]
    public void ReferenceSourceLookup_InterruptRollsBackTempAndMainState()
    {
        using var cancellation = new CancellationTokenSource();
        _db.Connection.CreateFunction<long>(
            "cancel_authoritative_fresh_source_lookup",
            () =>
            {
                cancellation.Cancel();
                return 0;
            });

        OperationCanceledException exception;
        using (var graph = _writer.BeginReferenceGraphRefreshScope(
                   forceFullRefresh: true,
                   useFreshReferenceResolutionDefaults: true))
        using (var transaction = _writer.BeginTransaction())
        using (var raw = _writer.BeginAuthoritativeFreshBulkInsertScope(
                   enabled: true,
                   cancellation.Token)!)
        {
            var fileId = InsertNewFile("src/source-lookup-interrupted.cs");
            _writer.InsertSymbols([
                new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "function",
                    Name = "Caller",
                    Line = 1,
                    StartLine = 1,
                    EndLine = 10,
                },
            ]);
            Execute($"""
                CREATE TEMP TRIGGER cancel_authoritative_fresh_source_materialization
                BEFORE INSERT ON {DbWriter.AuthoritativeFreshReferenceSourceSymbolsTableName}
                BEGIN
                    SELECT cancel_authoritative_fresh_source_lookup();
                END;
                """);
            exception = Assert.Throws<OperationCanceledException>(() =>
                _writer.InsertReferencesForNewFilesInAtomicFileScope(
                    [new ReferenceRecord
                    {
                        FileId = fileId,
                        SymbolName = "interrupted_probe",
                        ReferenceKind = "call",
                        Line = 5,
                        Column = 1,
                        Context = "interrupted_probe();",
                        ContainerKind = "function",
                        ContainerName = "Caller",
                    }],
                    refreshMutualRecursionFlags: false,
                    cancellation.Token));
        }

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        var sqliteException = Assert.IsType<SqliteException>(exception.InnerException);
        Assert.Equal(9, sqliteException.SqliteErrorCode);
        Assert.Equal(0L, ScalarLong("SELECT COUNT(*) FROM files"));
        Assert.Equal(0L, ScalarLong("SELECT COUNT(*) FROM symbols"));
        Assert.Equal(0L, ScalarLong("SELECT COUNT(*) FROM symbol_references"));
        Assert.Equal(
            0L,
            ScalarLong($"""
                SELECT COUNT(*)
                FROM temp.sqlite_schema
                WHERE name = '{DbWriter.AuthoritativeFreshReferenceSourceSymbolsTableName}'
                """));
    }

    [Fact]
    public void StatementCache_EvictsLeastRecentlyUsedAndFinalizesEveryShape()
    {
        DbWriter.AuthoritativeFreshRawInsertScopeStats? observedStats = null;
        var previousCapacity = DbWriter.AuthoritativeFreshRawStatementCacheCapacityForTesting;
        var previousStatsHook = DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting;
        try
        {
            DbWriter.AuthoritativeFreshRawStatementCacheCapacityForTesting = 2;
            DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting = stats =>
            {
                observedStats = stats;
                previousStatsHook?.Invoke(stats);
            };

            using var graph = _writer.BeginReferenceGraphRefreshScope(
                forceFullRefresh: true,
                useFreshReferenceResolutionDefaults: true);
            using var transaction = _writer.BeginTransaction();
            using var raw = _writer.BeginAuthoritativeFreshBulkInsertScope(
                enabled: true,
                CancellationToken.None)!;
            var fileId = InsertNewFile("src/lru.cs");

            _writer.InsertChunks(CreateChunks(fileId, startIndex: 0, count: 6));
            _writer.InsertChunks(CreateChunks(fileId, startIndex: 6, count: 1));
            _writer.InsertIssuesForNewFile(
                fileId,
                Enumerable.Range(0, 5).Select(index => CreateIssue(index + 1)).ToArray());
            _writer.InsertChunks(CreateChunks(fileId, startIndex: 7, count: 6));
            raw.Complete();
            transaction.Commit();
        }
        finally
        {
            DbWriter.AuthoritativeFreshRawStatementCacheCapacityForTesting = previousCapacity;
            DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting = previousStatsHook;
        }

        Assert.NotNull(observedStats);
        Assert.True(observedStats.Completed);
        Assert.Equal(2, observedStats.Capacity);
        Assert.Equal(2, observedStats.PeakCachedStatementCount);
        Assert.Equal(5, observedStats.PrepareCount);
        Assert.Equal(0, observedStats.CacheHitCount);
        Assert.Equal(3, observedStats.EvictionCount);
        Assert.Equal(5, observedStats.FinalizeCount);
    }

    [Fact]
    public void BatchConstraint_ReplaysOnlyBadChunkRowAndReusesStatement()
    {
        var warnings = new List<string>();
        var previousWarningHook = DbWriter.BatchRowSkipWarningForTesting;
        try
        {
            DbWriter.BatchRowSkipWarningForTesting = warning =>
            {
                warnings.Add(warning);
                previousWarningHook?.Invoke(warning);
            };

            using var graph = _writer.BeginReferenceGraphRefreshScope(
                forceFullRefresh: true,
                useFreshReferenceResolutionDefaults: true);
            using var transaction = _writer.BeginTransaction();
            using var raw = _writer.BeginAuthoritativeFreshBulkInsertScope(
                enabled: true,
                CancellationToken.None)!;
            var fileId = InsertNewFile("src/constraint.cs");
            var chunks = CreateChunks(fileId, startIndex: 0, count: 7);
            chunks[2].FileId = -1;

            _writer.InsertChunks(chunks, CancellationToken.None);
            var (_, persistedAfterReplay, _, _) = _writer.GetCounts();
            Assert.Equal(6, persistedAfterReplay);
            _writer.InsertChunks(CreateChunks(fileId, startIndex: 100, count: 1));
            raw.Complete();
            transaction.Commit();
        }
        finally
        {
            DbWriter.BatchRowSkipWarningForTesting = previousWarningHook;
        }

        Assert.Equal(7L, ScalarLong("SELECT COUNT(*) FROM chunks"));
        Assert.Equal(1, _writer.BatchRowsSkipped);
        var warning = Assert.Single(warnings);
        Assert.Contains("file_id=-1", warning, StringComparison.Ordinal);
        Assert.Contains("chunk_index=2", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Interrupt_RollsBackFinalizesAndLeavesProviderConnectionReusable()
    {
        using var cancellation = new CancellationTokenSource();
        _db.Connection.CreateFunction<long>(
            "cancel_authoritative_fresh_raw",
            () =>
            {
                cancellation.Cancel();
                return 0;
            });
        Execute("""
            CREATE TEMP TRIGGER cancel_authoritative_fresh_raw_insert
            BEFORE INSERT ON chunks
            BEGIN
                SELECT cancel_authoritative_fresh_raw();
            END
            """);

        DbWriter.AuthoritativeFreshRawInsertScopeStats? observedStats = null;
        var previousStatsHook = DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting;
        OperationCanceledException exception;
        try
        {
            DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting = stats =>
            {
                observedStats = stats;
                previousStatsHook?.Invoke(stats);
            };

            using (var graph = _writer.BeginReferenceGraphRefreshScope(
                       forceFullRefresh: true,
                       useFreshReferenceResolutionDefaults: true))
            using (var transaction = _writer.BeginTransaction())
            using (var raw = _writer.BeginAuthoritativeFreshBulkInsertScope(
                       enabled: true,
                       cancellation.Token)!)
            {
                var fileId = InsertNewFile("src/interrupted.cs");
                exception = Assert.Throws<OperationCanceledException>(() =>
                    _writer.InsertChunks(
                        CreateChunks(fileId, startIndex: 0, count: 1),
                        cancellation.Token));
            }
        }
        finally
        {
            DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting = previousStatsHook;
        }

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        var sqliteException = Assert.IsType<SqliteException>(exception.InnerException);
        Assert.Equal(9, sqliteException.SqliteErrorCode);
        Assert.Equal(0L, ScalarLong("SELECT COUNT(*) FROM files"));
        Assert.Equal(0L, ScalarLong("SELECT COUNT(*) FROM chunks"));
        Assert.NotNull(observedStats);
        Assert.False(observedStats.Completed);
        Assert.Equal(2, observedStats.FinalizeCount);

        Execute("DROP TRIGGER cancel_authoritative_fresh_raw_insert");
        using (var transaction = _writer.BeginTransaction())
        {
            var fileId = InsertNewFile("src/reusable.cs");
            _writer.InsertChunks(CreateChunks(fileId, startIndex: 0, count: 1));
            transaction.Commit();
        }

        Assert.Equal(1L, ScalarLong("SELECT COUNT(*) FROM chunks"));
    }

    [Fact]
    public void HookFailure_CleansLeaseAndDisposeHookCannotMaskBodyException()
    {
        var previousRawHook = DbWriter.AuthoritativeFreshRawInsertExecutingForTesting;
        var previousStatsHook = DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting;
        try
        {
            DbWriter.AuthoritativeFreshRawInsertExecutingForTesting = _ =>
                throw new InvalidOperationException("raw body hook failure");
            DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting = _ =>
                throw new InvalidOperationException("raw dispose hook failure");

            var exception = Assert.Throws<InvalidOperationException>(() =>
            {
                using var graph = _writer.BeginReferenceGraphRefreshScope(
                    forceFullRefresh: true,
                    useFreshReferenceResolutionDefaults: true);
                using var transaction = _writer.BeginTransaction();
                using var raw = _writer.BeginAuthoritativeFreshBulkInsertScope(
                    enabled: true,
                    CancellationToken.None)!;
                var fileId = InsertNewFile("src/hook-failure.cs");
                _writer.InsertChunks(CreateChunks(fileId, startIndex: 0, count: 1));
            });

            Assert.Equal("raw body hook failure", exception.Message);
        }
        finally
        {
            DbWriter.AuthoritativeFreshRawInsertExecutingForTesting = previousRawHook;
            DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting = previousStatsHook;
        }

        Assert.Equal(0L, ScalarLong("SELECT COUNT(*) FROM files"));
        using var retry = _writer.BeginTransaction();
        var retryFileId = InsertNewFile("src/hook-retry.cs");
        _writer.InsertChunks(CreateChunks(retryFileId, startIndex: 0, count: 1));
        retry.Commit();
        Assert.Equal(1L, ScalarLong("SELECT COUNT(*) FROM chunks"));
    }

    [Fact]
    public void Complete_WhenReportingHookThrows_DoesNotMarkScopeCompleted()
    {
        DbWriter.AuthoritativeFreshRawInsertScopeStats? observedStats = null;
        var previousStatsHook = DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting;
        try
        {
            DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting = stats =>
            {
                observedStats = stats;
                throw new InvalidOperationException("raw completion hook failure");
            };

            using var graph = _writer.BeginReferenceGraphRefreshScope(
                forceFullRefresh: true,
                useFreshReferenceResolutionDefaults: true);
            using var transaction = _writer.BeginTransaction();
            using var raw = _writer.BeginAuthoritativeFreshBulkInsertScope(
                enabled: true,
                CancellationToken.None)!;
            var fileId = InsertNewFile("src/completion-hook-failure.cs");
            _writer.InsertChunks(CreateChunks(fileId, startIndex: 0, count: 1));

            var exception = Assert.Throws<InvalidOperationException>(raw.Complete);
            Assert.Equal("raw completion hook failure", exception.Message);
            Assert.NotNull(observedStats);
            Assert.True(observedStats.Completed);
            Assert.Throws<ObjectDisposedException>(raw.Complete);
        }
        finally
        {
            DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting = previousStatsHook;
        }

        Assert.Equal(0L, ScalarLong("SELECT COUNT(*) FROM files"));
        Assert.Equal(0L, ScalarLong("SELECT COUNT(*) FROM chunks"));
    }

    [Fact]
    public void ReferenceLineChangedRowCountFailure_DiscardsStatementAndFileSavepointAllowsNextFile()
    {
        var rawWork = new List<DbWriter.AuthoritativeFreshRawInsertWork>();
        DbWriter.AuthoritativeFreshRawInsertScopeStats? observedStats = null;
        var previousChangedRowsHook = DbWriter.AuthoritativeFreshRawChangedRowCountForTesting;
        var previousRawHook = DbWriter.AuthoritativeFreshRawInsertExecutingForTesting;
        var previousStatsHook = DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting;
        var injected = 0;
        try
        {
            DbWriter.AuthoritativeFreshRawChangedRowCountForTesting = change =>
            {
                var actual = previousChangedRowsHook?.Invoke(change) ?? change.ActualChangedRows;
                return change.Operation == "insert_reference_lines"
                    && Interlocked.Exchange(ref injected, 1) == 0
                        ? actual - 1
                        : actual;
            };
            DbWriter.AuthoritativeFreshRawInsertExecutingForTesting = work =>
            {
                rawWork.Add(work);
                previousRawHook?.Invoke(work);
            };
            DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting = stats =>
            {
                observedStats = stats;
                previousStatsHook?.Invoke(stats);
            };

            using var graph = _writer.BeginReferenceGraphRefreshScope(
                forceFullRefresh: true,
                useFreshReferenceResolutionDefaults: true);
            using var outerTransaction = _writer.BeginTransaction();
            using var raw = _writer.BeginAuthoritativeFreshBulkInsertScope(
                enabled: true,
                CancellationToken.None)!;

            using (var failedFile = _writer.BeginTransaction())
            {
                var failedFileId = InsertNewFile("src/failed-row-count.cs");
                var exception = Assert.Throws<InvalidDataException>(() =>
                    _writer.InsertReferencesForNewFilesInAtomicFileScope(
                        CreateReferences(failedFileId, 2, "failed"),
                        refreshMutualRecursionFlags: false,
                        CancellationToken.None));
                Assert.Contains("expected=2, actual=1", exception.Message, StringComparison.Ordinal);
            }

            DbWriter.AuthoritativeFreshRawChangedRowCountForTesting = previousChangedRowsHook;
            using (var succeedingFile = _writer.BeginTransaction())
            {
                var succeedingFileId = InsertNewFile("src/succeeding-row-count.cs");
                _writer.InsertReferencesForNewFilesInAtomicFileScope(
                    CreateReferences(succeedingFileId, 2, "succeeding"),
                    refreshMutualRecursionFlags: false,
                    CancellationToken.None);
                succeedingFile.Commit();
            }

            raw.Complete();
            outerTransaction.Commit();
        }
        finally
        {
            DbWriter.AuthoritativeFreshRawChangedRowCountForTesting = previousChangedRowsHook;
            DbWriter.AuthoritativeFreshRawInsertExecutingForTesting = previousRawHook;
            DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting = previousStatsHook;
        }

        Assert.Equal(0L, ScalarLong("SELECT COUNT(*) FROM files WHERE path = 'src/failed-row-count.cs'"));
        Assert.Equal(1L, ScalarLong("SELECT COUNT(*) FROM files WHERE path = 'src/succeeding-row-count.cs'"));
        Assert.Equal(2L, ScalarLong("SELECT COUNT(*) FROM reference_lines"));
        Assert.Equal(2L, ScalarLong("SELECT COUNT(*) FROM symbol_references"));
        var referenceLineWork = rawWork
            .Where(work => work.Operation == "insert_reference_lines")
            .ToArray();
        Assert.Equal(2, referenceLineWork.Length);
        Assert.All(referenceLineWork, work => Assert.False(work.CacheHit));
        var floorWork = rawWork
            .Where(work => work.Operation == "read_reference_line_id_floor")
            .ToArray();
        Assert.Equal(2, floorWork.Length);
        Assert.False(floorWork[0].CacheHit);
        Assert.True(floorWork[1].CacheHit);
        Assert.NotNull(observedStats);
        Assert.True(observedStats.Completed);
        Assert.Equal(1, observedStats.DiscardCount);
        Assert.Equal(observedStats.PrepareCount, observedStats.FinalizeCount);
    }

    [Fact]
    public void FileChangedRowCountFailure_DiscardsStatementAndRollsBackInsertedFile()
    {
        var rawWork = new List<DbWriter.AuthoritativeFreshRawInsertWork>();
        DbWriter.AuthoritativeFreshRawInsertScopeStats? observedStats = null;
        var previousChangedRowsHook = DbWriter.AuthoritativeFreshRawChangedRowCountForTesting;
        var previousRawHook = DbWriter.AuthoritativeFreshRawInsertExecutingForTesting;
        var previousStatsHook = DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting;
        var injected = 0;
        try
        {
            DbWriter.AuthoritativeFreshRawChangedRowCountForTesting = change =>
            {
                var actual = previousChangedRowsHook?.Invoke(change) ?? change.ActualChangedRows;
                return change.Operation == "insert_files"
                    && Interlocked.Exchange(ref injected, 1) == 0
                        ? 0
                        : actual;
            };
            DbWriter.AuthoritativeFreshRawInsertExecutingForTesting = work =>
            {
                rawWork.Add(work);
                previousRawHook?.Invoke(work);
            };
            DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting = stats =>
            {
                observedStats = stats;
                previousStatsHook?.Invoke(stats);
            };

            using var graph = _writer.BeginReferenceGraphRefreshScope(
                forceFullRefresh: true,
                useFreshReferenceResolutionDefaults: true);
            using var outerTransaction = _writer.BeginTransaction();
            using var raw = _writer.BeginAuthoritativeFreshBulkInsertScope(
                enabled: true,
                CancellationToken.None)!;
            using (var failedFile = _writer.BeginTransaction())
            {
                var exception = Assert.Throws<InvalidDataException>(() =>
                    InsertNewFile("src/file-change-count-failed.cs"));
                Assert.Contains("expected=1, actual=0", exception.Message, StringComparison.Ordinal);
            }

            DbWriter.AuthoritativeFreshRawChangedRowCountForTesting = previousChangedRowsHook;
            using (var succeedingFile = _writer.BeginTransaction())
            {
                _ = InsertNewFile("src/file-change-count-succeeded.cs");
                succeedingFile.Commit();
            }
            raw.Complete();
            outerTransaction.Commit();
        }
        finally
        {
            DbWriter.AuthoritativeFreshRawChangedRowCountForTesting = previousChangedRowsHook;
            DbWriter.AuthoritativeFreshRawInsertExecutingForTesting = previousRawHook;
            DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting = previousStatsHook;
        }

        Assert.Equal(0L, ScalarLong("SELECT COUNT(*) FROM files WHERE path = 'src/file-change-count-failed.cs'"));
        Assert.Equal(1L, ScalarLong("SELECT COUNT(*) FROM files WHERE path = 'src/file-change-count-succeeded.cs'"));
        var fileWork = rawWork.Where(work => work.Operation == "insert_files").ToArray();
        Assert.Equal(2, fileWork.Length);
        Assert.All(fileWork, work => Assert.False(work.CacheHit));
        Assert.NotNull(observedStats);
        Assert.Equal(1, observedStats.DiscardCount);
        Assert.Equal(observedStats.PrepareCount, observedStats.FinalizeCount);
    }

    [Fact]
    public void ReferenceLineConstraint_DiscardsStatementAndCanReprepare()
    {
        long fileId;
        using (var seedTransaction = _writer.BeginTransaction())
        {
            fileId = InsertNewFile("src/reference-line-constraint.cs");
            _writer.InsertReferencesForNewFilesInAtomicFileScope(
                CreateReferences(fileId, 1, "duplicate"),
                refreshMutualRecursionFlags: false,
                CancellationToken.None);
            seedTransaction.Commit();
        }

        DbWriter.AuthoritativeFreshRawInsertScopeStats? observedStats = null;
        var previousStatsHook = DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting;
        try
        {
            DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting = stats =>
            {
                observedStats = stats;
                previousStatsHook?.Invoke(stats);
            };
            using var graph = _writer.BeginReferenceGraphRefreshScope(
                forceFullRefresh: true,
                useFreshReferenceResolutionDefaults: true);
            using var outerTransaction = _writer.BeginTransaction();
            using var raw = _writer.BeginAuthoritativeFreshBulkInsertScope(
                enabled: true,
                CancellationToken.None)!;
            using (var failedFile = _writer.BeginTransaction())
            {
                var exception = Assert.Throws<SqliteException>(() =>
                    _writer.InsertReferencesForNewFilesInAtomicFileScope(
                        CreateReferences(fileId, 1, "duplicate"),
                        refreshMutualRecursionFlags: false,
                        CancellationToken.None));
                Assert.Equal(19, exception.SqliteErrorCode);
                Assert.Equal(2067, exception.SqliteExtendedErrorCode);
            }

            using (var succeedingFile = _writer.BeginTransaction())
            {
                _writer.InsertReferencesForNewFilesInAtomicFileScope(
                    CreateReferences(fileId, 1, "unique"),
                    refreshMutualRecursionFlags: false,
                    CancellationToken.None);
                succeedingFile.Commit();
            }
            raw.Complete();
            outerTransaction.Commit();
        }
        finally
        {
            DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting = previousStatsHook;
        }

        Assert.Equal(2L, ScalarLong("SELECT COUNT(*) FROM reference_lines"));
        Assert.NotNull(observedStats);
        Assert.Equal(1, observedStats.DiscardCount);
    }

    [Fact]
    public void ReferenceLineInterrupt_PreservesCancellationAndRollsBackOuterTransaction()
    {
        using var cancellation = new CancellationTokenSource();
        _db.Connection.CreateFunction<long>(
            "cancel_authoritative_fresh_reference_line",
            () =>
            {
                cancellation.Cancel();
                return 0;
            });
        Execute("""
            CREATE TEMP TRIGGER cancel_authoritative_fresh_reference_line_insert
            BEFORE INSERT ON reference_lines
            BEGIN
                SELECT cancel_authoritative_fresh_reference_line();
            END
            """);

        DbWriter.AuthoritativeFreshRawInsertScopeStats? observedStats = null;
        var previousStatsHook = DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting;
        OperationCanceledException exception;
        try
        {
            DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting = stats =>
            {
                observedStats = stats;
                previousStatsHook?.Invoke(stats);
            };

            exception = Assert.Throws<OperationCanceledException>(() =>
            {
                using var graph = _writer.BeginReferenceGraphRefreshScope(
                    forceFullRefresh: true,
                    useFreshReferenceResolutionDefaults: true);
                using var outerTransaction = _writer.BeginTransaction();
                using var raw = _writer.BeginAuthoritativeFreshBulkInsertScope(
                    enabled: true,
                    cancellation.Token)!;
                using var fileTransaction = _writer.BeginTransaction();
                var fileId = InsertNewFile("src/reference-line-cancel.cs");
                _writer.InsertReferencesForNewFilesInAtomicFileScope(
                    CreateReferences(fileId, 2, "cancel"),
                    refreshMutualRecursionFlags: false,
                    cancellation.Token);
            });
        }
        finally
        {
            DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting = previousStatsHook;
        }

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        var sqliteException = Assert.IsType<SqliteException>(exception.InnerException);
        Assert.Equal(9, sqliteException.SqliteErrorCode);
        Assert.Equal(0L, ScalarLong("SELECT COUNT(*) FROM files"));
        Assert.Equal(0L, ScalarLong("SELECT COUNT(*) FROM reference_lines"));
        Assert.NotNull(observedStats);
        Assert.False(observedStats.Completed);
        Assert.Equal(1, observedStats.DiscardCount);

        Execute("DROP TRIGGER cancel_authoritative_fresh_reference_line_insert");
        using var retry = _writer.BeginTransaction();
        var retryFileId = InsertNewFile("src/reference-line-cancel-retry.cs");
        _writer.InsertReferencesForNewFilesInAtomicFileScope(
            CreateReferences(retryFileId, 1, "retry"),
            refreshMutualRecursionFlags: false,
            CancellationToken.None);
        retry.Commit();
        Assert.Equal(1L, ScalarLong("SELECT COUNT(*) FROM reference_lines"));
    }

    [Fact]
    public void ReferenceLineIds_RecheckFloorAcrossBatchesAndPreferTableMaximum()
    {
        long seedFileId;
        using (var seedTransaction = _writer.BeginTransaction())
        {
            seedFileId = InsertNewFile("src/reference-line-floor-seed.cs");
            seedTransaction.Commit();
        }
        Execute($"""
            INSERT INTO reference_lines (id, file_id, line, context)
            VALUES (9000, {seedFileId.ToString(CultureInfo.InvariantCulture)}, 1, 'table-floor');
            UPDATE sqlite_sequence SET seq = 5 WHERE name = 'reference_lines';
            """);

        var previousRawHook = DbWriter.AuthoritativeFreshRawInsertExecutingForTesting;
        var rawWork = new List<DbWriter.AuthoritativeFreshRawInsertWork>();
        var floorReads = 0;
        try
        {
            DbWriter.AuthoritativeFreshRawInsertExecutingForTesting = work =>
            {
                rawWork.Add(work);
                if (work.Operation == "read_reference_line_id_floor"
                    && Interlocked.Increment(ref floorReads) == 2)
                {
                    Execute($"""
                        INSERT INTO reference_lines (id, file_id, line, context)
                        VALUES (10000, {seedFileId.ToString(CultureInfo.InvariantCulture)}, 2, 'between-batches');
                        """);
                }
                previousRawHook?.Invoke(work);
            };

            using var graph = _writer.BeginReferenceGraphRefreshScope(
                forceFullRefresh: true,
                useFreshReferenceResolutionDefaults: true);
            using var transaction = _writer.BeginTransaction();
            using var raw = _writer.BeginAuthoritativeFreshBulkInsertScope(
                enabled: true,
                CancellationToken.None)!;
            var fileId = InsertNewFile("src/reference-line-floor-batches.cs");
            _writer.InsertReferencesForNewFilesInAtomicFileScope(
                CreateReferences(fileId, 171, "floor"),
                refreshMutualRecursionFlags: false,
                CancellationToken.None);
            raw.Complete();
            transaction.Commit();

            Assert.Equal(9001L, ReferenceLineId(fileId, line: 1, "floor_0();"));
            Assert.Equal(9170L, ReferenceLineId(fileId, line: 170, "floor_169();"));
            Assert.Equal(10001L, ReferenceLineId(fileId, line: 171, "floor_170();"));
        }
        finally
        {
            DbWriter.AuthoritativeFreshRawInsertExecutingForTesting = previousRawHook;
        }

        Assert.Equal(2, floorReads);
        Assert.Equal(
            [(170, 511), (1, 4)],
            rawWork
                .Where(work => work.Operation == "insert_reference_lines")
                .Select(work => (work.StatementRows, work.BoundParameterCount))
                .ToArray());
        Assert.Equal(10001L, ScalarLong("SELECT seq FROM sqlite_sequence WHERE name = 'reference_lines'"));
    }

    [Fact]
    public void ReferenceLineIdReservation_OverflowFailsBeforeInsertAndRollsBackFileSavepoint()
    {
        long seedFileId;
        using (var seedTransaction = _writer.BeginTransaction())
        {
            seedFileId = InsertNewFile("src/reference-line-overflow-seed.cs");
            seedTransaction.Commit();
        }
        Execute($"""
            INSERT INTO reference_lines (id, file_id, line, context)
            VALUES ({long.MaxValue.ToString(CultureInfo.InvariantCulture)},
                    {seedFileId.ToString(CultureInfo.InvariantCulture)}, 1, 'overflow-primer');
            DELETE FROM reference_lines WHERE id = {long.MaxValue.ToString(CultureInfo.InvariantCulture)};
            """);

        using var graph = _writer.BeginReferenceGraphRefreshScope(
            forceFullRefresh: true,
            useFreshReferenceResolutionDefaults: true);
        using var outerTransaction = _writer.BeginTransaction();
        using var raw = _writer.BeginAuthoritativeFreshBulkInsertScope(
            enabled: true,
            CancellationToken.None)!;
        using (var failedFile = _writer.BeginTransaction())
        {
            var fileId = InsertNewFile("src/reference-line-overflow.cs");
            Assert.Throws<OverflowException>(() =>
                _writer.InsertReferencesForNewFilesInAtomicFileScope(
                    CreateReferences(fileId, 1, "overflow"),
                    refreshMutualRecursionFlags: false,
                    CancellationToken.None));
        }
        raw.Complete();
        outerTransaction.Commit();

        Assert.Equal(0L, ScalarLong("SELECT COUNT(*) FROM reference_lines"));
        Assert.Equal(0L, ScalarLong("SELECT COUNT(*) FROM files WHERE path = 'src/reference-line-overflow.cs'"));
    }

    [Fact]
    public void ReferenceLineIdFloor_DuplicateSequenceRowsFailBeforeInsertAndRollBackFileSavepoint()
    {
        Execute("""
            INSERT INTO sqlite_sequence (name, seq)
            VALUES ('reference_lines', 10), ('reference_lines', 20)
            """);

        var rawWork = new List<DbWriter.AuthoritativeFreshRawInsertWork>();
        var previousRawHook = DbWriter.AuthoritativeFreshRawInsertExecutingForTesting;
        try
        {
            DbWriter.AuthoritativeFreshRawInsertExecutingForTesting = work =>
            {
                rawWork.Add(work);
                previousRawHook?.Invoke(work);
            };

            using var graph = _writer.BeginReferenceGraphRefreshScope(
                forceFullRefresh: true,
                useFreshReferenceResolutionDefaults: true);
            using var outerTransaction = _writer.BeginTransaction();
            using var raw = _writer.BeginAuthoritativeFreshBulkInsertScope(
                enabled: true,
                CancellationToken.None)!;
            using (var failedFile = _writer.BeginTransaction())
            {
                var fileId = InsertNewFile("src/reference-line-duplicate-sequence.cs");
                var exception = Assert.Throws<InvalidDataException>(() =>
                    _writer.InsertReferencesForNewFilesInAtomicFileScope(
                        CreateReferences(fileId, 1, "duplicate_sequence"),
                        refreshMutualRecursionFlags: false,
                        CancellationToken.None));
                Assert.Contains("ID floor was negative (-1)", exception.Message, StringComparison.Ordinal);
            }
            raw.Complete();
            outerTransaction.Commit();
        }
        finally
        {
            DbWriter.AuthoritativeFreshRawInsertExecutingForTesting = previousRawHook;
        }

        Assert.Equal(
            ["insert_files", "read_reference_line_id_floor"],
            rawWork.Select(work => work.Operation).ToArray());
        Assert.Equal(0L, ScalarLong("SELECT COUNT(*) FROM reference_lines"));
        Assert.Equal(
            0L,
            ScalarLong(
                "SELECT COUNT(*) FROM files WHERE path = 'src/reference-line-duplicate-sequence.cs'"));
    }

    public void Dispose()
    {
        _db.Dispose();
        TestProjectHelper.DeleteDirectory(_projectRoot);
    }

    private long InsertNewFile(string path, string language = "csharp")
        => _writer.InsertNewFile(new FileRecord
        {
            Path = path,
            Lang = language,
            Size = 100,
            Lines = 100,
            Checksum = path,
            Modified = new DateTime(2026, 8, 23, 0, 0, 0, DateTimeKind.Utc),
        });

    private static FileIssue CreateIssue(int line)
        => new()
        {
            Path = "src/raw-shapes.cs",
            Kind = $"raw_issue_{line}",
            Line = line,
            Message = line == 1 ? "雪😀a\0β" : $"raw issue {line}",
            Origin = line == 1 ? null : "extractor",
            Severity = line == 1 ? null : "warning",
        };

    private static ChunkRecord[] CreateChunks(long fileId, int startIndex, int count)
        => Enumerable.Range(startIndex, count)
            .Select(index => new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = index,
                StartLine = index + 1,
                EndLine = index + 1,
                Content = $"chunk_{index}",
            })
            .ToArray();

    private static ReferenceRecord[] CreateReferences(
        long fileId,
        int count,
        string contextPrefix)
        => Enumerable.Range(0, count)
            .Select(index => new ReferenceRecord
            {
                FileId = fileId,
                SymbolName = $"target_{contextPrefix}_{index}",
                ReferenceKind = "call",
                Line = index + 1,
                Column = 1,
                Context = $"{contextPrefix}_{index}();",
            })
            .ToArray();

    private void PrimeSequencesForInt64Ids()
    {
        Execute("""
            INSERT INTO files (
                id, path, lang, size, lines, checksum, modified, generated, indexed_at)
            VALUES (
                5000000000, 'src/sequence-primer.cs', 'csharp', 0, 0,
                'sequence-primer', '2026-08-23T00:00:00Z', 0, CURRENT_TIMESTAMP);
            INSERT INTO reference_lines (id, file_id, line, context)
            VALUES (5000000000, 5000000000, 1, 'sequence-primer');
            DELETE FROM reference_lines WHERE id = 5000000000;
            DELETE FROM files WHERE id = 5000000000;
            """);
        Assert.False(_writer.HasAnyIndexedFiles());
    }

    private void Execute(string sql)
    {
        using var command = _db.Connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private long ScalarLong(string sql)
    {
        using var command = _db.Connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private long ReferenceLineId(long fileId, int line, string context)
    {
        using var command = _db.Connection.CreateCommand();
        command.CommandText = """
            SELECT id
            FROM reference_lines
            WHERE file_id = @file_id AND line = @line AND context = @context
            """;
        command.Parameters.AddWithValue("@file_id", fileId);
        command.Parameters.AddWithValue("@line", line);
        command.Parameters.AddWithValue("@context", context);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private long? SourceSymbolId(string symbolName)
    {
        using var command = _db.Connection.CreateCommand();
        command.CommandText = """
            SELECT source_symbol_id
            FROM symbol_references
            WHERE symbol_name = @symbol_name
            """;
        command.Parameters.AddWithValue("@symbol_name", symbolName);
        var value = command.ExecuteScalar();
        return value == null || value == DBNull.Value
            ? null
            : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private long? SourceFileId(string symbolName)
    {
        using var command = _db.Connection.CreateCommand();
        command.CommandText = """
            SELECT source.file_id
            FROM symbol_references AS reference
            LEFT JOIN symbols AS source ON source.id = reference.source_symbol_id
            WHERE reference.symbol_name = @symbol_name
            """;
        command.Parameters.AddWithValue("@symbol_name", symbolName);
        var value = command.ExecuteScalar();
        return value == null || value == DBNull.Value
            ? null
            : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private long ResourceListGeneration()
        => ScalarLong("""
            SELECT CAST(value AS INTEGER)
            FROM codeindex_meta
            WHERE key = 'resource_list_generation'
            """);

    private long ResourceListGenerationTriggerCount()
        => ScalarLong("""
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'trigger'
              AND name IN (
                  'files_resource_generation_ai',
                  'files_resource_generation_ad',
                  'files_resource_generation_au')
            """);

    private string? ScalarString(string sql)
    {
        using var command = _db.Connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value == null || value == DBNull.Value
            ? null
            : Convert.ToString(value, CultureInfo.InvariantCulture);
    }
}
