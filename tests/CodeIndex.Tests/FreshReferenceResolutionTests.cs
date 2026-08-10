using System.Globalization;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public sealed class FreshReferenceResolutionTests : IDisposable
{
    private readonly string _projectRoot;
    private readonly DbContext _db;
    private readonly DbWriter _writer;

    public FreshReferenceResolutionTests()
    {
        _projectRoot = TestProjectHelper.CreateTempProject("cdidx_fresh_reference_resolution");
        _db = new DbContext(
            DbOpenIntent.WriteIndex,
            Path.Combine(_projectRoot, "codeindex.db"));
        _db.InitializeSchema();
        _writer = new DbWriter(_db.Connection);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, false, true)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, false)]
    public void ShouldUseFreshReferenceResolutionDefaults_RequiresEmptyOrdinaryFullScan(
        bool startedWithNoIndexedFiles,
        bool rebuild,
        bool symbolsOnly,
        bool expected)
    {
        Assert.Equal(
            expected,
            IndexCommandRunner.ShouldUseFreshReferenceResolutionDefaults(
                startedWithNoIndexedFiles,
                rebuild,
                symbolsOnly));
    }

    [Fact]
    public void BeginReferenceGraphRefreshScope_RejectsFreshDefaultsWithoutForcedFullRefresh()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            _writer.BeginReferenceGraphRefreshScope(
                forceFullRefresh: false,
                useFreshReferenceResolutionDefaults: true));

        Assert.Equal("useFreshReferenceResolutionDefaults", exception.ParamName);
    }

    [Fact]
    public void InsertReferences_FreshDefaultsKeepParameterShapeAndUseSeparateCachedSql()
    {
        var fileId = InsertFile("src/provisional.py", "python");
        var observedWork = new List<DbWriter.ReferenceInsertBindingWork>();
        var previousHook = DbWriter.ReferenceInsertBindingWorkForTesting;
        try
        {
            DbWriter.ReferenceInsertBindingWorkForTesting = work =>
            {
                observedWork.Add(work);
                previousHook?.Invoke(work);
            };

            using (var freshScope = _writer.BeginReferenceGraphRefreshScope(
                       forceFullRefresh: true,
                       useFreshReferenceResolutionDefaults: true))
            {
                _writer.InsertReferences(
                    [CreateReference(fileId, "Fresh", line: 1, extractorFlags: true)],
                    refreshMutualRecursionFlags: false);
            }

            using (var ordinaryFullScope = _writer.BeginReferenceGraphRefreshScope(
                       forceFullRefresh: true))
            {
                _writer.InsertReferences(
                    [CreateReference(fileId, "Standard", line: 2, extractorFlags: true)],
                    refreshMutualRecursionFlags: false);
            }
        }
        finally
        {
            DbWriter.ReferenceInsertBindingWorkForTesting = previousHook;
        }

        Assert.Collection(
            observedWork,
            fresh =>
            {
                Assert.True(fresh.UsesFreshResolutionDefaults);
                Assert.Equal(1, fresh.StatementRows);
                Assert.Equal(14, fresh.BoundParameterCount);
            },
            standard =>
            {
                Assert.False(standard.UsesFreshResolutionDefaults);
                Assert.Equal(1, standard.StatementRows);
                Assert.Equal(14, standard.BoundParameterCount);
            });

        Assert.Equal(
            new ProvisionalRow("unresolved", 0, 0, 0),
            ReadProvisionalRow("Fresh"));
        Assert.Equal(
            new ProvisionalRow(null, 0, 1, 1),
            ReadProvisionalRow("Standard"));
    }

    [Fact]
    public void FreshResolutionSql_MaterializesCandidateFactsWithoutOuterReferenceScan()
    {
        var sql = DbWriter.RefreshReferenceResolutionFreshSparseSqlForTesting;

        Assert.Contains("WITH resolution_facts AS MATERIALIZED", sql, StringComparison.Ordinal);
        Assert.Contains("GROUP BY candidate.reference_id", sql, StringComparison.Ordinal);
        Assert.Contains("FROM resolution_facts AS resolution", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE r.id = resolution.reference_id", sql, StringComparison.Ordinal);
        Assert.Contains("is_self_reference = CASE", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("WHERE EXISTS", sql, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(sql, "UPDATE symbol_references AS r"));
    }

    [Fact]
    public void FreshResolutionSql_MatchesFullOracleForAllStatesAcrossCSharpAndPython()
    {
        var csharpCallerFileId = InsertFile("src/caller.cs", "csharp");
        var csharpTargetFileId = InsertFile("src/target.cs", "csharp");
        var pythonCallerFileId = InsertFile("src/caller.py", "python");
        var pythonGroupFileId = InsertFile("src/group.py", "python");
        var pythonAmbiguousAFileId = InsertFile("src/ambiguous_a.py", "python");
        var pythonAmbiguousBFileId = InsertFile("src/ambiguous_b.py", "python");
        var pythonUniqueFileId = InsertFile("src/unique.py", "python");
        _writer.InsertSymbols([
            CreateSymbol(csharpCallerFileId, "CsCaller", line: 1),
            CreateSymbol(csharpCallerFileId, "SelfTarget", line: 2),
            CreateSymbol(csharpTargetFileId, "CsTarget", line: 1),
            CreateSymbol(pythonCallerFileId, "PyCaller", line: 1),
            CreateSymbol(pythonGroupFileId, "GroupTarget", line: 1, container: "group_module"),
            CreateSymbol(pythonGroupFileId, "GroupTarget", line: 2, container: "group_module"),
            CreateSymbol(pythonAmbiguousAFileId, "AmbiguousTarget", line: 1, container: "module_a"),
            CreateSymbol(pythonAmbiguousBFileId, "AmbiguousTarget", line: 1, container: "module_b"),
            CreateSymbol(pythonUniqueFileId, "PyUnique", line: 1, container: "unique_module"),
        ]);

        using var freshScope = _writer.BeginReferenceGraphRefreshScope(
            forceFullRefresh: true,
            useFreshReferenceResolutionDefaults: true);
        _writer.InsertReferences([
            CreateReference(csharpCallerFileId, "CsTarget", line: 10),
            CreateReference(csharpCallerFileId, "MissingCs", line: 11),
            CreateReference(csharpCallerFileId, "SelfTarget", line: 12, container: "SelfTarget"),
            CreateReference(pythonCallerFileId, "GroupTarget", line: 20),
            CreateReference(pythonCallerFileId, "AmbiguousTarget", line: 21),
            CreateReference(pythonCallerFileId, "PyUnique", line: 22),
            CreateReference(pythonCallerFileId, "MissingPy", line: 23),
        ], refreshMutualRecursionFlags: false);

        Assert.Equal(
            7,
            ScalarLong("""
                SELECT COUNT(*)
                FROM symbol_references
                WHERE resolution_state = 'unresolved'
                  AND resolution_candidate_count = 0
                  AND target_symbol_id IS NULL
                  AND target_symbol_key IS NULL
                  AND is_self_reference = 0
                  AND is_mutual_recursion = 0
                """));

        Execute($"""
            UPDATE symbol_references AS reference
            SET source_symbol_id = (
                SELECT source.id
                FROM symbols AS source
                WHERE source.file_id = reference.file_id
                  AND source.name = CASE
                      WHEN reference.line = 12 THEN 'SelfTarget'
                      WHEN reference.file_id = {csharpCallerFileId} THEN 'CsCaller'
                      ELSE 'PyCaller'
                  END
                ORDER BY source.id
                LIMIT 1
            );

            INSERT INTO symbol_reference_candidates(reference_id, symbol_id, scope_rank)
            SELECT reference.id, target.id, 0
            FROM symbol_references AS reference
            JOIN files AS source_file ON source_file.id = reference.file_id
            JOIN symbols AS target ON target.name = reference.symbol_name
            JOIN files AS target_file
              ON target_file.id = target.file_id
             AND target_file.lang = source_file.lang;

            CREATE TEMP TABLE observed_fresh_resolution_updates (
                reference_id INTEGER PRIMARY KEY
            ) WITHOUT ROWID;
            CREATE TEMP TRIGGER observe_fresh_resolution_updates
            AFTER UPDATE OF target_symbol_id, target_symbol_key,
                            resolution_candidate_count, resolution_state,
                            is_self_reference
            ON symbol_references
            BEGIN
                INSERT INTO observed_fresh_resolution_updates(reference_id)
                VALUES (NEW.id);
            END;
            """);

        Execute(DbWriter.RefreshReferenceResolutionFreshSparseSqlForTesting);

        Assert.Equal(
            ScalarLong("SELECT COUNT(DISTINCT reference_id) FROM symbol_reference_candidates"),
            ScalarLong("SELECT COUNT(*) FROM observed_fresh_resolution_updates"));
        Assert.Equal(
            new ResolutionRow("resolved", 1, HasTargetId: true, HasTargetKey: true, IsSelf: false),
            ReadResolutionRow("src/caller.cs", 10));
        Assert.Equal(
            new ResolutionRow("unresolved", 0, HasTargetId: false, HasTargetKey: false, IsSelf: false),
            ReadResolutionRow("src/caller.cs", 11));
        Assert.Equal(
            new ResolutionRow("resolved", 1, HasTargetId: true, HasTargetKey: true, IsSelf: true),
            ReadResolutionRow("src/caller.cs", 12));
        Assert.Equal(
            new ResolutionRow("resolved_group", 2, HasTargetId: false, HasTargetKey: true, IsSelf: false),
            ReadResolutionRow("src/caller.py", 20));
        Assert.Equal(
            new ResolutionRow("ambiguous", 2, HasTargetId: false, HasTargetKey: false, IsSelf: false),
            ReadResolutionRow("src/caller.py", 21));
        Assert.Equal(
            new ResolutionRow("resolved", 1, HasTargetId: true, HasTargetKey: true, IsSelf: false),
            ReadResolutionRow("src/caller.py", 22));
        Assert.Equal(
            new ResolutionRow("unresolved", 0, HasTargetId: false, HasTargetKey: false, IsSelf: false),
            ReadResolutionRow("src/caller.py", 23));

        var freshSnapshot = ReadReferenceIdentitySnapshot();
        Execute("DROP TRIGGER observe_fresh_resolution_updates;");
        Execute(DbWriter.RefreshReferenceResolutionFullSqlForTesting);
        Assert.Equal(freshSnapshot, ReadReferenceIdentitySnapshot());
    }

    [Fact]
    public void FreshResolutionScope_RetainsDefaultsAfterFailureAndClearsThemAfterCompletion()
    {
        var fileId = InsertFile("src/retry.py", "python");
        _writer.InsertSymbols([
            CreateSymbol(fileId, "Caller", line: 1),
            CreateSymbol(fileId, "Target", line: 2),
        ]);

        using var freshScope = _writer.BeginReferenceGraphRefreshScope(
            forceFullRefresh: true,
            useFreshReferenceResolutionDefaults: true);
        _writer.InsertReferences(
            [CreateReference(fileId, "Target", line: 10, extractorFlags: true)],
            refreshMutualRecursionFlags: false);
        Execute("""
            CREATE TEMP TRIGGER fail_fresh_resolution
            BEFORE UPDATE OF resolution_state ON symbol_references
            WHEN OLD.symbol_name = 'Target'
            BEGIN
                SELECT RAISE(ABORT, 'fail fresh resolution');
            END;
            """);

        Assert.Throws<SqliteException>(() =>
            _writer.RefreshMutualRecursionFlags(stampReferenceIdentityContractReady: false));
        _writer.InsertReferences(
            [CreateReference(fileId, "MissingAfterFailure", line: 11, extractorFlags: true)],
            refreshMutualRecursionFlags: false);
        Assert.Equal(
            new ProvisionalRow("unresolved", 0, 0, 0),
            ReadProvisionalRow("MissingAfterFailure"));

        Execute("DROP TRIGGER fail_fresh_resolution;");
        _writer.RefreshMutualRecursionFlags(stampReferenceIdentityContractReady: false);
        Assert.Equal("resolved", ReadProvisionalRow("Target").ResolutionState);

        _writer.InsertReferences(
            [CreateReference(fileId, "MissingAfterCompletion", line: 12, extractorFlags: true)],
            refreshMutualRecursionFlags: false);
        Assert.Equal(
            new ProvisionalRow(null, 0, 1, 1),
            ReadProvisionalRow("MissingAfterCompletion"));
    }

    public void Dispose()
    {
        _db.Dispose();
        TestProjectHelper.DeleteDirectory(_projectRoot);
    }

    private long InsertFile(string path, string language)
        => _writer.UpsertFile(new FileRecord
        {
            Path = path,
            Lang = language,
            Size = 100,
            Lines = 30,
            Checksum = path,
            Modified = new DateTime(2026, 8, 11, 0, 0, 0, DateTimeKind.Utc),
        });

    private static SymbolRecord CreateSymbol(
        long fileId,
        string name,
        int line,
        string? container = null)
        => new()
        {
            FileId = fileId,
            Kind = "function",
            Name = name,
            Line = line,
            StartLine = line,
            EndLine = line,
            Signature = $"function {name}()",
            ContainerKind = container == null ? null : "module",
            ContainerName = container,
            ContainerQualifiedName = container,
        };

    private static ReferenceRecord CreateReference(
        long fileId,
        string symbolName,
        int line,
        string container = "Caller",
        bool extractorFlags = false)
        => new()
        {
            FileId = fileId,
            SymbolName = symbolName,
            ReferenceKind = "call",
            Line = line,
            Column = 1,
            Context = $"{symbolName}();",
            ContainerKind = "function",
            ContainerName = container,
            IsSelfReference = extractorFlags,
            IsMutualRecursion = extractorFlags,
        };

    private ProvisionalRow ReadProvisionalRow(string symbolName)
    {
        using var command = _db.Connection.CreateCommand();
        command.CommandText = """
            SELECT resolution_state,
                   resolution_candidate_count,
                   is_self_reference,
                   is_mutual_recursion
            FROM symbol_references
            WHERE symbol_name = @symbol_name
            """;
        command.Parameters.AddWithValue("@symbol_name", symbolName);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return new ProvisionalRow(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3));
    }

    private ResolutionRow ReadResolutionRow(string path, int line)
    {
        using var command = _db.Connection.CreateCommand();
        command.CommandText = """
            SELECT reference.resolution_state,
                   reference.resolution_candidate_count,
                   reference.target_symbol_id IS NOT NULL,
                   reference.target_symbol_key IS NOT NULL,
                   reference.is_self_reference
            FROM symbol_references AS reference
            JOIN files AS file ON file.id = reference.file_id
            WHERE file.path = @path
              AND reference.line = @line
            """;
        command.Parameters.AddWithValue("@path", path);
        command.Parameters.AddWithValue("@line", line);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return new ResolutionRow(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetBoolean(2),
            reader.GetBoolean(3),
            reader.GetBoolean(4));
    }

    private string ReadReferenceIdentitySnapshot()
    {
        using var command = _db.Connection.CreateCommand();
        command.CommandText = """
            SELECT group_concat(
                       id || ':' ||
                       COALESCE(source_symbol_id, -1) || ':' ||
                       COALESCE(target_symbol_id, -1) || ':' ||
                       COALESCE(target_symbol_key, '') || ':' ||
                       resolution_candidate_count || ':' ||
                       COALESCE(resolution_state, '') || ':' ||
                       is_self_reference || ':' ||
                       is_mutual_recursion,
                       '|')
            FROM (SELECT * FROM symbol_references ORDER BY id)
            """;
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
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

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }

    private sealed record ProvisionalRow(
        string? ResolutionState,
        int CandidateCount,
        int IsSelf,
        int IsMutual);

    private sealed record ResolutionRow(
        string ResolutionState,
        int CandidateCount,
        bool HasTargetId,
        bool HasTargetKey,
        bool IsSelf);
}
