using System.Reflection;
using System.Text;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

/// <summary>
/// Tests for DbReader query operations.
/// DbReaderクエリ操作のテスト。
/// </summary>
[Collection("SQLite pool sensitive")]
public partial class DbReaderTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DbContext _db;
    private readonly DbWriter _writer;
    private readonly DbReader _reader;

    public DbReaderTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"codeindex_reader_test_{Guid.NewGuid():N}.db");
        _db = new DbContext(_dbPath);
        _db.InitializeSchema();
        _writer = new DbWriter(_db.Connection);

        // Seed test data first, then stamp the index-complete marker so DbReader sees the
        // same state a production post-indexing open would: populated tables + user_version.
        // DbReader を構築する前に seed と MarkIndexComplete を済ませ、本番の index 完了時と同じ状態にする。
        SeedData();
        _writer.MarkGraphReady();
        _writer.MarkIssuesReady();
        // #86: post-indexing production DBs also stamp FoldReady after a full scan, so the
        // reader exercises the Unicode fold path. Legacy fallback is covered by a separate
        // test that opens a DB without this flag.
        // #86: full scan 後の本番 DB は fold ready も立つため、reader は fold 経路を通す。
        _writer.MarkFoldReady();
        foreach (var lang in FileIndexer.GetHotspotFamilyMarkerLanguages())
            _writer.MarkHotspotFamilyReady(lang, $"{lang}-fixture-fingerprint");
        _reader = new DbReader(_db.Connection);
    }

    [Theory]
    [InlineData("plain", "%plain%")]
    [InlineData("src/Services", "%src/Services%")]
    [InlineData("*.py", "%.py")]
    [InlineData("src/*.py", "src/%.py")]
    [InlineData("foo?bar", "foo_bar")]
    [InlineData(@"literal\*.py", "%literal*.py%")]
    [InlineData(@"literal\?.py", "%literal?.py%")]
    [InlineData(@"literal\[name\].py", "%literal[name].py%")]
    [InlineData(@"src\Foo.cs", @"%src\\Foo.cs%")]
    public void BuildPathLikePattern_TreatsGlobTokensAsWildcards(string input, string expected)
    {
        Assert.Equal(expected, DbReader.BuildPathLikePattern(input));
    }

    [Fact]
    public void SqliteIdentifier_Quote_AllowsUnusualTableNamesForSchemaPragmas()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE \"odd \"\" table\" (\"odd col\" INTEGER)";
            cmd.ExecuteNonQuery();
        }

        var columns = DbSchemaCache.LoadColumns(connection, "odd \" table");

        Assert.Contains("odd col", columns);
        Assert.Equal("\"odd \"\" table\"", SqliteIdentifier.Quote("odd \" table"));
    }

    [Theory]
    [InlineData("page_count")]
    [InlineData("_pragma1")]
    public void SqliteIdentifier_ValidatePragmaName_AllowsBarePragmaNames(string name)
    {
        Assert.Equal(name, SqliteIdentifier.ValidatePragmaName(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("page-count")]
    [InlineData("page_count;VACUUM")]
    [InlineData("1page_count")]
    public void SqliteIdentifier_ValidatePragmaName_RejectsUnsafePragmaNames(string name)
    {
        Assert.Throws<ArgumentException>(() => SqliteIdentifier.ValidatePragmaName(name));
    }

    [Fact]
    public void DegradationReasonCodes_AllCodesHaveActionableMetadata()
    {
        foreach (var code in DegradationReasonCodes.All)
        {
            var metadata = DegradationReasonCodes.GetMetadata(code);

            Assert.Equal(code, metadata.Code);
            Assert.False(string.IsNullOrWhiteSpace(metadata.HumanText));
            Assert.Contains("cdidx", metadata.RecommendedAction, StringComparison.Ordinal);
            Assert.Contains("cdidx", metadata.AlternativeAction, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void GetStatus_ExposesOperationalMetrics()
    {
        var expectedFreshenedAt = DateTime.Parse(
            "2026-05-31T00:00:00.0000000Z",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind);
        _writer.SetMeta(DbContext.LastIndexRunModeMetaKey, "incremental");
        _writer.SetMeta(DbContext.LastIndexRunStartedAtMetaKey, "2026-05-31T00:00:00.0000000Z");
        _writer.SetMeta(DbContext.LastIndexRunDurationMsMetaKey, "1234");
        _writer.SetMeta(DbContext.LastIndexRunFilesScannedMetaKey, "3");
        _writer.SetMeta(DbContext.LastIndexRunFilesSkippedMetaKey, "1");
        _writer.SetMeta(DbContext.LastIndexRunParseErrorsMetaKey, "0");
        _writer.SetMeta(DbContext.LastIndexRunBytesReadMetaKey, "4096");
        _writer.SetMeta(DbContext.LastIndexRunBytesReadSkippedFileCountMetaKey, "2");
        _writer.SetMeta(DbContext.LastIndexRunBytesReadIncompleteMetaKey, "true");
        _writer.SetMeta(DbContext.LastIndexRunRowsUpsertedMetaKey, "2");
        _writer.SetMeta(DbContext.LastIndexRunRowsDeletedMetaKey, "1");
        _writer.SetMeta(DbContext.LastIndexRunPeakMemoryMbMetaKey, "64");
        _writer.SetMeta(DbContext.LastIndexRunDiagnosticsMetaKey, JsonStringListCodec.Serialize(["indexed_head_metadata_write_failed: IOException: denied"]));
        _writer.SetMeta(DbContext.LastIndexRunDiagnosticCountMetaKey, "1");
        _writer.SetMeta(DbContext.LastIndexRunDiagnosticsTruncatedMetaKey, "false");

        var status = _reader.GetStatus();

        Assert.True(status.DbSizeBytes > 0);
        Assert.True(status.WalSizeBytes >= 0);
        Assert.NotNull(status.SymbolsByLanguage);
        Assert.True(status.SymbolsByLanguage.Values.Sum(kinds => kinds.Values.Sum()) > 0);
        Assert.True(status.Process.HeapBytes > 0);
        Assert.True(status.Process.WorkingSetBytes > 0);
        Assert.NotNull(status.LastIndexRun);
        Assert.Equal("incremental", status.LastIndexRun.Mode);
        Assert.Equal(expectedFreshenedAt, status.LastWorkspaceFreshenedAt);
        Assert.Equal(1234, status.LastIndexRun.DurationMs);
        Assert.Equal(3, status.LastIndexRun.FilesScanned);
        Assert.Equal(2, status.LastIndexRun.BytesReadSkippedFileCount);
        Assert.True(status.LastIndexRun.BytesReadIncomplete);
        Assert.Equal(64, status.LastIndexRun.PeakMemoryMb);
        Assert.Equal(["indexed_head_metadata_write_failed: IOException: denied"], status.LastIndexRun.Diagnostics);
        Assert.Equal(1, status.LastIndexRun.DiagnosticCount);
        Assert.False(status.LastIndexRun.DiagnosticsTruncated);
    }

    [Theory]
    [InlineData(DegradationReasonCodes.MissingFoldBackfill, "--exact falls back")]
    [InlineData(DegradationReasonCodes.StaleFoldKeyVersion, "older fold-key version")]
    [InlineData(DegradationReasonCodes.StaleFoldKeyFingerprint, "older runtime fingerprint")]
    [InlineData(DegradationReasonCodes.FoldRowsNotRestamped, "not restamped")]
    public void DegradationReasonCodes_BuildsFoldExplanationFromCode(string code, string expectedText)
    {
        var explanation = DegradationReasonCodes.BuildFoldNotReadyExplanation(code);

        Assert.Contains(expectedText, explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void CountSearchResults_NormalizesJavascriptLangSpelling()
    {
        const string query = "JavaScriptAliasToken";

        InsertIndexedFile(
            "src/javascript-alias.js",
            "javascript",
            $@"const marker = ""{query}"";");

        var counts = _reader.CountSearchResults(query, lang: "Javascript");

        Assert.Equal(1, counts.Count);
        Assert.Equal(1, counts.FileCount);
    }

    [Fact]
    public void AnalyzeFtsQuery_AllTokensTooLong_ReturnsDegradedReason()
    {
        var query = new string('x', DbReader.FtsUnicode61MaxTokenLength + 1);

        var diagnostics = DbReader.AnalyzeFtsQuery(query);

        Assert.Equal(DbReader.AllTokensFilteredByLengthReason, diagnostics.QueryDegradedReason);
        Assert.Equal([query], diagnostics.TokensDropped);
    }







    [Fact]
    public void CountSearchResults_RawFtsRejectsUnknownColumnQualifiersBeforeSqlite()
    {
        var ex = Assert.Throws<FtsQuerySyntaxException>(() => _reader.CountSearchResults("rowid:authenticate", rawQuery: true));

        Assert.Equal(FtsQuerySyntaxErrorKind.ColumnQualifier, ex.Kind);
        Assert.Contains("rowid:", ex.Message);
        Assert.Contains("'content' column", ex.Message);
    }

    [Fact]
    public void AnalyzeSymbol_KotlinValueClassIncludesSubKind()
    {
        InsertIndexedFile("src/UserId.kt", "kotlin", "value class UserId(val id: Long)\n");

        var analysis = _reader.AnalyzeSymbol("UserId", limit: 5, lang: "kotlin", exact: true);

        var definition = Assert.Single(analysis.Definitions);
        Assert.Equal("kotlin_value_class", definition.SubKind);
    }

    [Fact]
    public void GetCallers_CSharpGenericInvocationTypeArgument_ParticipatesInGraph()
    {
        InsertIndexedFile(
            "src/generic_type_argument_fixture.cs",
            "csharp",
            """
            interface IFoo {}
            class Runner
            {
                void Process<T>(T item) {}
                void Run(IFoo value) { Process<IFoo>(value); }
            }
            """);

        var defaultCaller = Assert.Single(_reader.GetCallers(
            "IFoo",
            lang: "csharp",
            exact: true,
            pathPatterns: ["generic_type_argument_fixture"]));

        Assert.Equal("Run", defaultCaller.CallerName);
        Assert.Equal("generic_type_argument", defaultCaller.ReferenceKind);

        var caller = Assert.Single(_reader.GetCallers(
            "IFoo",
            lang: "csharp",
            referenceKind: "generic_type_argument",
            exact: true,
            pathPatterns: ["generic_type_argument_fixture"]));

        Assert.Equal("Run", caller.CallerName);
        Assert.Equal("generic_type_argument", caller.ReferenceKind);
    }

    [Fact]
    public void GetCallers_SolutionProjectReference_ParticipatesInGraph_Issue3662()
    {
        InsertManualReference(
            "CodeIndex.sln",
            "solution",
            "project",
            "App",
            "src/App/App.csproj",
            "project_reference");

        var caller = Assert.Single(_reader.GetCallers("src/App/App.csproj", lang: "solution", exact: true));

        Assert.Equal("CodeIndex.sln", caller.Path);
        Assert.Equal("App", caller.CallerName);
        Assert.Equal("src/App/App.csproj", caller.CalleeName);
        Assert.Equal("project_reference", caller.ReferenceKind);
    }

    [Fact]
    public void CreateSearchReferencesCommand_RanksWithoutLoweringReferenceNames()
    {
        using var cmd = CreateSearchReferencesCommandForSql("FetchData");
        var sql = cmd.CommandText;

        Assert.DoesNotContain("lower(r.symbol_name)", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("r.symbol_name = @rankingQuery COLLATE NOCASE", sql, StringComparison.Ordinal);
        Assert.Contains("r.symbol_name COLLATE NOCASE LIKE @rankingQueryPrefix ESCAPE '\\'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void SymbolReferenceKindAggregationPlan_UsesNameKindIndexBeforeAndAfterAnalyze()
    {
        var sql = """
            SELECT r.symbol_name,
                   GROUP_CONCAT(DISTINCT r.reference_kind) AS reference_kinds,
                   MIN(r.line) AS first_line,
                   COUNT(*) AS reference_count
            FROM symbol_references r
            WHERE r.symbol_name = @query
              AND r.reference_kind IN ('call', 'instantiate', 'subscribe', 'unsubscribe', 'razor_event_binding')
            GROUP BY r.symbol_name
            """;

        var planBeforeAnalyze = ExplainQueryPlan(sql);
        Assert.Contains("idx_symbol_refs_name_kind", planBeforeAnalyze);

        using (var analyze = _db.Connection.CreateCommand())
        {
            analyze.CommandText = "ANALYZE";
            analyze.ExecuteNonQuery();
        }

        var planAfterAnalyze = ExplainQueryPlan(sql);
        Assert.Contains("idx_symbol_refs_name_kind", planAfterAnalyze);
    }

    [Fact]
    public void FileCountHelpers_UseGroupedReferenceCounts()
    {
        var joinSql = InvokePrivateStringMethod(_reader, "BuildFileReferenceCountJoinSql", "file_page");
        var countSql = GetPrivateStringProperty(_reader, "FileReferenceCountSql");

        Assert.Contains("GROUP BY r.file_id", joinSql, StringComparison.Ordinal);
        Assert.Contains("JOIN file_page file_set ON file_set.id = r.file_id", joinSql, StringComparison.Ordinal);
        Assert.DoesNotContain("WHERE file_id = f.id", joinSql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("COALESCE(reference_counts.reference_count, 0)", countSql);
    }

    [Fact]
    public void NormalizeSymbolSearchQueries_SkipsAlreadyNormalizedInput()
    {
        var method = typeof(DbReader).GetMethod(
            "NormalizeSymbolSearchQueries",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var normalized = Assert.IsAssignableFrom<IReadOnlyList<string>>(method!.Invoke(null, [new[] { "module.exports.fetchData", "module.exports.fetchData" }, "javascript", false]));
        var secondPass = Assert.IsAssignableFrom<IReadOnlyList<string>>(method.Invoke(null, [normalized, "javascript", false]));

        Assert.Same(normalized, secondPass);
        Assert.Equal(["fetchData"], normalized);
    }

    [Theory]
    [InlineData("js")]
    [InlineData("JS")]
    [InlineData("jsx")]
    [InlineData("JSX")]
    [InlineData("cjs")]
    [InlineData("MJS")]
    public void CountSearchResults_NormalizesJavascriptShorthandLangSpellings(string lang)
    {
        const string query = "JavaScriptShorthandToken";

        InsertIndexedFile(
            "src/javascript-shorthand.js",
            "javascript",
            $@"const marker = ""{query}"";");

        var counts = _reader.CountSearchResults(query, lang: lang);

        Assert.Equal(1, counts.Count);
        Assert.Equal(1, counts.FileCount);
    }

    [Theory]
    [InlineData("TypeScript")]
    [InlineData("typescript")]
    public void CountSearchResults_NormalizesTypeScriptSpelling(string lang)
    {
        const string query = "TypeScriptToken";

        InsertIndexedFile(
            "src/typescript-alias.ts",
            "typescript",
            $@"const marker = ""{query}"";");

        var counts = _reader.CountSearchResults(query, lang: lang);

        Assert.Equal(1, counts.Count);
        Assert.Equal(1, counts.FileCount);
    }

    [Theory]
    [InlineData("src/csharp-alias.cs", "csharp", "c#")]
    [InlineData("src/cpp-alias.cpp", "cpp", "c++")]
    [InlineData("src/fsharp-alias.fs", "fsharp", "f#")]
    [InlineData("src/vb-alias.vb", "vb", "vb.net")]
    [InlineData("src/visual-basic-alias.vb", "vb", "visual-basic")]
    [InlineData("src/visual_basic-alias.vb", "vb", "visual_basic")]
    [InlineData("src/vbs-alias.vbs", "vb", "vbs")]
    [InlineData("src/vbscript-alias.vbs", "vb", "vbscript")]
    [InlineData("src/java-alias.java", "java", "jav")]
    [InlineData("src/python-alias.py", "python", "py3")]
    [InlineData("src/python3-alias.py", "python", "python3")]
    [InlineData("src/sql-alias.sql", "sql", "sqlserver")]
    public void CountSearchResults_NormalizesCommonLanguageAliases(string path, string fileLang, string queryLang)
    {
        const string query = "CommonAliasToken";

        InsertIndexedFile(
            path,
            fileLang,
            $@"const marker = ""{query}"";");

        var counts = _reader.CountSearchResults(query, lang: queryLang);

        Assert.Equal(1, counts.Count);
        Assert.Equal(1, counts.FileCount);
    }

    private void SeedData()
    {
        const string authContent = "def authenticate(user, password):\n    if user == 'admin':\n        return True\n    return False";
        var pyId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/auth.py",
            Lang = "python",
            Size = 500,
            Lines = 30,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks([new ChunkRecord
        {
            FileId = pyId, ChunkIndex = 0, StartLine = 1, EndLine = 30,
            Content = authContent,
        }]);
        var authSymbols = new List<SymbolRecord>
        {
            new SymbolRecord
            {
                FileId = pyId, Kind = "function", Name = "authenticate", Line = 1,
                StartLine = 1, EndLine = 4, BodyStartLine = 2, BodyEndLine = 4,
                Signature = "def authenticate(user, password):"
            },
        };
        _writer.InsertSymbols(authSymbols);
        _writer.InsertReferences(ReferenceExtractor.Extract(pyId, "python", authContent, authSymbols));

        const string apiContent = "export class ApiClient {\n  async fetchData(url) {\n    return fetch(url)\n  }\n}";
        var jsId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/api.js",
            Lang = "javascript",
            Size = 800,
            Lines = 50,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks([new ChunkRecord
        {
            FileId = jsId, ChunkIndex = 0, StartLine = 1, EndLine = 50,
            Content = apiContent,
        }]);
        var apiSymbols = new List<SymbolRecord>
        {
            new SymbolRecord
            {
                FileId = jsId, Kind = "class", Name = "ApiClient", Line = 1,
                StartLine = 1, EndLine = 4, BodyStartLine = 1, BodyEndLine = 4,
                Signature = "export class ApiClient {", Visibility = "export"
            },
            new SymbolRecord
            {
                FileId = jsId, Kind = "function", Name = "fetchData", Line = 2,
                StartLine = 2, EndLine = 3, BodyStartLine = 2, BodyEndLine = 3,
                Signature = "async fetchData(url) {", ContainerKind = "class", ContainerName = "ApiClient"
            },
        };
        _writer.InsertSymbols(apiSymbols);
        _writer.InsertReferences(ReferenceExtractor.Extract(jsId, "javascript", apiContent, apiSymbols));

        // Plain text file with no symbols for outline edge case
        // アウトラインのエッジケース用のシンボルなしプレーンテキストファイル
        InsertIndexedFile("docs/notes.md", "markdown", "Some documentation text.");
    }

    private void InsertIndexedFile(string path, string lang, string content, DateTime? modified = null, string? familyScopeKey = null)
    {
        var normalized = content.Replace("\r\n", "\n");
        var lines = normalized.Split('\n');
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = path,
            Lang = lang,
            Size = normalized.Length,
            Lines = lines.Length,
            Modified = modified ?? new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        _writer.InsertChunks([new ChunkRecord
        {
            FileId = fileId,
            ChunkIndex = 0,
            StartLine = 1,
            EndLine = lines.Length,
            Content = normalized,
        }]);

        var symbols = SymbolExtractor.Extract(fileId, lang, normalized);
        SymbolExtractor.ApplyFamilyScope(symbols, familyScopeKey ?? FileIndexer.DeriveFallbackFamilyScopeKey(path));
        _writer.InsertSymbols(symbols);
        _writer.InsertReferences(ReferenceExtractor.Extract(fileId, lang, normalized, symbols));
    }

    private string ExplainQueryPlan(string sql)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
        cmd.Parameters.AddWithValue("@query", "authenticate");

        var plan = new StringBuilder();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            plan.AppendLine(reader.GetString(3));
        return plan.ToString();
    }

    private static string GetPrivateStringProperty(DbReader reader, string name)
    {
        var property = typeof(DbReader).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(property);
        return Assert.IsType<string>(property!.GetValue(reader));
    }

    private static string InvokePrivateStringMethod(DbReader reader, string name, params object[] args)
    {
        var method = typeof(DbReader).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<string>(method!.Invoke(reader, args));
    }

    private SqliteCommand CreateSearchReferencesCommandForSql(string query)
    {
        var method = typeof(DbReader).GetMethod(
            "CreateSearchReferencesCommand",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        return Assert.IsType<SqliteCommand>(method!.Invoke(
            _reader,
            [
                query,
                20,
                null,
                null,
                null,
                null,
                false,
                false,
                0,
                true,
                false,
            ]));
    }

    private void InsertManualReferences(string path, string containerName, string target, string kind, int count)
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = path,
            Lang = "csharp",
            Size = 100,
            Lines = count + 1,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        var references = Enumerable.Range(1, count)
            .Select(line => new ReferenceRecord
            {
                FileId = fileId,
                SymbolName = target,
                ReferenceKind = kind,
                Line = line,
                Column = 9,
                Context = $"{kind} {target}",
                ContainerKind = "class",
                ContainerName = containerName,
            })
            .ToList();
        _writer.InsertReferences(references);
    }

    private void InsertManualReferences(string path, IReadOnlyList<ReferenceRecord> references)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT id FROM files WHERE path = @path";
        cmd.Parameters.AddWithValue("@path", path);
        var fileId = (long)cmd.ExecuteScalar()!;
        foreach (var reference in references)
            reference.FileId = fileId;

        _writer.InsertReferences(references);
    }

    private void InsertManualReference(string path, string lang, string? containerKind, string? containerName, string target, string kind)
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = path,
            Lang = lang,
            Size = 100,
            Lines = 1,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        _writer.InsertReferences([
            new ReferenceRecord
            {
                FileId = fileId,
                SymbolName = target,
                ReferenceKind = kind,
                Line = 1,
                Column = 1,
                Context = $"{target}()",
                ContainerKind = containerKind,
                ContainerName = containerName,
            }
        ]);
    }

    private void InsertSearchVisibilityFixture(string path, string visibility, DateTime modified)
    {
        const string content = "public class AuthFixture { void Marker() { Authenticate(); } }";
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = path,
            Lang = "csharp",
            Size = content.Length,
            Lines = 1,
            Modified = modified,
        });

        _writer.InsertChunks([new ChunkRecord
        {
            FileId = fileId,
            ChunkIndex = 0,
            StartLine = 1,
            EndLine = 1,
            Content = content,
        }]);

        _writer.InsertSymbols([
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "Authenticate",
                Line = 1,
                StartLine = 1,
                EndLine = 1,
                Signature = $"{visibility} void Authenticate()",
                Visibility = visibility,
            }
        ]);
    }







    [Fact]
    public void GetCallers_DefaultWeightedRankingPrioritizesInstantiateOverNoisySubscriptions()
    {
        const string target = "TargetService";
        InsertManualReferences("src/Factory.cs", "Factory", target, "instantiate", 3);
        InsertManualReferences("src/EventBus.cs", "EventBus", target, "subscribe", 50);

        var weighted = _reader.GetCallers(target, lang: "csharp", exact: true);
        var countRanked = _reader.GetCallers(target, lang: "csharp", exact: true, rankMode: ReferenceRankMode.Count);

        Assert.Equal("Factory", weighted[0].CallerName);
        Assert.Equal(3, weighted[0].ReferenceCount);
        Assert.Equal(0, weighted[0].ReferenceKindCounts["call"]);
        Assert.Equal(3, weighted[0].ReferenceKindCounts["instantiate"]);
        Assert.Equal(0, weighted[0].ReferenceKindCounts["subscribe"]);
        Assert.Equal(9.0, weighted[0].ReferenceWeightScore, precision: 3);

        Assert.Equal("EventBus", countRanked[0].CallerName);
        Assert.Equal(50, countRanked[0].ReferenceCount);
        Assert.Equal(0, countRanked[0].ReferenceKindCounts["call"]);
        Assert.Equal(0, countRanked[0].ReferenceKindCounts["instantiate"]);
        Assert.Equal(50, countRanked[0].ReferenceKindCounts["subscribe"]);
    }

    [Theory]
    [InlineData("src/top-level.js", "javascript")]
    [InlineData("src/top-level.ts", "typescript")]
    [InlineData("src/top_level.py", "python")]
    public void GetTransitiveCallers_TreatsScriptNullContainerReferencesAsTopLevelCallers(string path, string lang)
    {
        const string target = "TargetService";
        InsertManualReference(path, lang, containerKind: null, containerName: null, target, "call");

        var (results, truncated, truncatedReason, _, _) = _reader.GetTransitiveCallers(target, maxDepth: 1, limit: 10, lang: lang);

        var result = Assert.Single(results);
        Assert.Equal(path, result.Path);
        Assert.Equal(lang, result.Lang);
        Assert.Equal("<top-level>", result.CallerName);
        Assert.Equal("function", result.CallerKind);
        Assert.Equal(target, result.CalleeName);
        Assert.Equal(1, result.Depth);
        Assert.False(truncated);
        Assert.Null(truncatedReason);
    }

    [Fact]
    public void GetTransitiveCallers_DoesNotTreatJavaNullContainerReferencesAsTopLevelCallers()
    {
        InsertManualReference(
            "src/TopLevel.java",
            "java",
            containerKind: null,
            containerName: null,
            target: "TargetService",
            kind: "call");

        var (results, truncated, truncatedReason, _, _) = _reader.GetTransitiveCallers("TargetService", maxDepth: 1, limit: 10, lang: "java");

        Assert.Empty(results);
        Assert.False(truncated);
        Assert.Null(truncatedReason);
    }





    [Fact]
    public void CountSymbolsAndDefinitions_VisibilityFiltersStayInCountQueries()
    {
        InsertIndexedFile(
            "src/count_visibility.rs",
            "rust",
            """
            pub fn counted_public() {}
            fn counted_private() {}
            """);

        Assert.Equal(1, _reader.CountSearchSymbols("counted_public", lang: "rust", exact: true, visibilityFilters: ["public"]));
        Assert.Equal(0, _reader.CountSearchSymbols("counted_public", lang: "rust", exact: true, excludeVisibilityFilters: ["public"]));

        var symbolTotal = _reader.CountSearchSymbolsTotal("counted_public", lang: "rust", exact: true, visibilityFilters: ["public"]);
        Assert.Equal(1, symbolTotal.Count);
        Assert.Equal(1, symbolTotal.FileCount);

        var excludedSymbolTotal = _reader.CountSearchSymbolsTotal("counted_public", lang: "rust", exact: true, excludeVisibilityFilters: ["public"]);
        Assert.Equal(0, excludedSymbolTotal.Count);
        Assert.Equal(0, excludedSymbolTotal.FileCount);

        var definitionTotal = _reader.CountDefinitionsTotal("counted_public", lang: "rust", exact: true, visibilityFilters: ["public"]);
        Assert.Equal(1, definitionTotal.Count);
        Assert.Equal(1, definitionTotal.FileCount);

        var excludedDefinitionTotal = _reader.CountDefinitionsTotal("counted_public", lang: "rust", exact: true, excludeVisibilityFilters: ["public"]);
        Assert.Equal(0, excludedDefinitionTotal.Count);
        Assert.Equal(0, excludedDefinitionTotal.FileCount);
    }













    [Fact]
    public void RustQualifiedQueriesResolveAcrossGraphCommands()
    {
        InsertIndexedFile(
            "src/lib.rs",
            "rust",
            """
            pub mod macros {
                pub fn build() {}

                pub fn invoke() {
                    build();
                }
            }
            """);

        var references = _reader.SearchReferences("crate::macros::build", lang: "rust", exact: true);
        var reference = Assert.Single(references);
        Assert.Equal("build", reference.SymbolName);
        Assert.Equal("src/lib.rs", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("crate::macros::build", lang: "rust", exact: true));

        var callers = _reader.GetCallers("crate::macros::build", lang: "rust", exact: true);
        var caller = Assert.Single(callers);
        Assert.Equal("invoke", caller.CallerName);
        Assert.Equal("build", caller.CalleeName);
        Assert.Equal("src/lib.rs", caller.Path);
        Assert.Equal(1, _reader.CountCallers("crate::macros::build", lang: "rust", exact: true));

        var callees = _reader.GetCallees("crate::macros::invoke", lang: "rust", exact: true);
        var callee = Assert.Single(callees);
        Assert.Equal("invoke", callee.CallerName);
        Assert.Equal("build", callee.CalleeName);
        Assert.Equal("src/lib.rs", callee.Path);
        Assert.Equal(1, _reader.CountCallees("crate::macros::invoke", lang: "rust", exact: true));
    }

    [Fact]
    public void GetOutline_PreservesNestedSymbolDepths()
    {
        InsertIndexedFile(
            "src/deep.cs",
            "csharp",
            """
            namespace OuterNs
            {
                namespace InnerNs
                {
                    public class OuterClass
                    {
                        public class NestedClass
                        {
                            public class DeeplyNested
                            {
                                public void Method() { }
                            }
                        }
                    }
                }
            }
            """);

        var outline = _reader.GetOutline("src/deep.cs");

        Assert.NotNull(outline);
        var outer = Assert.Single(outline!.Symbols.Where(symbol => symbol.Name == "OuterClass"));
        var nested = Assert.Single(outline.Symbols.Where(symbol => symbol.Name == "NestedClass"));
        var deep = Assert.Single(outline.Symbols.Where(symbol => symbol.Name == "DeeplyNested"));
        var method = Assert.Single(outline.Symbols.Where(symbol => symbol.Name == "Method"));

        Assert.True(nested.Depth > outer.Depth);
        Assert.True(deep.Depth > nested.Depth);
        Assert.True(method.Depth > deep.Depth);
    }













    [Fact]
    public void CountSearchResults_RawQueryRejectsOutOfRangeNearDistance_Issue2089()
    {
        var ex = Assert.Throws<FtsQuerySyntaxException>(() => _reader.CountSearchResults("NEAR(auth login, 1000000)", rawQuery: true));

        Assert.Contains("NEAR distance must be between 0 and 100", ex.Message);
    }
































    [Fact]
    public void CountSearchResults_CjkSubstringYieldsZeroByDefault()
    {
        // Count path shares the sanitizer with Search, so the strict-default policy must
        // apply there too: a bare CJK query returns 0/0 against content where the indexed
        // token only contains the query as a prefix. Without this pin, a future change that
        // re-enables auto-prefix promotion in the count path (but not Search, or vice versa)
        // would silently desynchronize count vs. result-list relevance.
        // カウント経路も Search と同じサニタイザを共有するため、strict 既定が同様に適用される。
        // 素の CJK クエリは、インデックス上のトークンがクエリを接頭辞として含むだけの内容に
        // 対しては 0/0 を返す。Search と count のどちらかにだけ自動 prefix を復活させるような
        // 差分が入ると count と result list の relevance が静かに乖離するため、これを固定する。
        InsertIndexedFile("src/cjk_count_hit.py", "python",
            "def 計算する(値):\n    return 値\n");
        InsertIndexedFile("src/cjk_count_miss.py", "python",
            "def 検索する(値):\n    return 値\n");

        var counts = _reader.CountSearchResults("計算");

        Assert.Equal(0, counts.Count);
        Assert.Equal(0, counts.FileCount);
    }

    [Fact]
    public void CountSearchResults_CjkSubstringMatchesWhenPrefixFlagSet()
    {
        // Opt-in counterpart to the strict-default count test above. Passing `prefix: true`
        // through the count path must yield the matching count/fileCount, mirroring how
        // Search behaves under the same opt-in.
        // strict 既定の count テストに対する opt-in 版。`prefix: true` を count 経路にも渡すと、
        // 同じ opt-in を渡した Search と一致する count/fileCount を返す。
        InsertIndexedFile("src/cjk_count_hit.py", "python",
            "def 計算する(値):\n    return 値\n");
        InsertIndexedFile("src/cjk_count_miss.py", "python",
            "def 検索する(値):\n    return 値\n");

        var counts = _reader.CountSearchResults("計算", prefix: true);

        Assert.Equal(1, counts.Count);
        Assert.Equal(1, counts.FileCount);
    }







    [Fact]
    public void GetExcerpt_ReconstructsRequestedLineRange()
    {
        var excerpt = _reader.GetExcerpt("src/auth.py", 1, 2);

        Assert.NotNull(excerpt);
        Assert.Equal(1, excerpt!.StartLine);
        Assert.Equal(2, excerpt.EndLine);
        Assert.Contains("def authenticate(user, password):", excerpt.Content);
        Assert.Contains("if user == 'admin':", excerpt.Content);
    }

    [Fact]
    public void FindInFiles_ReturnsPathScopedLiteralMatchesWithContext()
    {
        InsertIndexedFile("src/Auth.cs", "csharp",
            """
            class Auth
            {
                void Guard() {}
                void Next() {}
            }
            """);

        var results = _reader.FindInFiles("guard", limit: 10, pathPatterns: ["src/Auth.cs"], before: 1, after: 1);

        var match = Assert.Single(results);
        Assert.Equal("src/Auth.cs", match.Path);
        Assert.Equal(3, match.Line);
        Assert.Equal(10, match.Column);
        Assert.Equal(2, match.StartLine);
        Assert.Equal(4, match.EndLine);
        Assert.Contains("void Guard()", match.Snippet);
        Assert.Contains("void Next()", match.Snippet);
    }

    [Fact]
    public void FindInFiles_ExactModeIsCaseSensitive()
    {
        InsertIndexedFile("src/Auth.cs", "csharp",
            """
            class Auth
            {
                void Guard() {}
            }
            """);

        var insensitive = _reader.FindInFiles("guard", limit: 10, pathPatterns: ["src/Auth.cs"]);
        var exact = _reader.FindInFiles("guard", limit: 10, pathPatterns: ["src/Auth.cs"], exact: true);

        Assert.Single(insensitive);
        Assert.Empty(exact);
    }

    [Fact]
    public void FindInFiles_ReturnsEverySameLineOccurrence()
    {
        InsertIndexedFile("src/Sample.cs", "csharp", "alpha alpha alpha\n");

        var results = _reader.FindInFiles("alpha", limit: 10, pathPatterns: ["src/Sample.cs"]);

        Assert.Equal(3, results.Count);
        Assert.Equal([1, 7, 13], results.Select(r => r.Column).ToArray());
        Assert.All(results, result => Assert.Equal(1, result.Line));
    }

    [Fact]
    public void FindInFiles_CountsOverlappingOccurrences()
    {
        InsertIndexedFile("src/Sample.cs", "csharp", "// banana\n");

        var results = _reader.FindInFiles("ana", limit: 10, pathPatterns: ["src/Sample.cs"]);

        Assert.Equal(2, results.Count);
        Assert.Equal([5, 7], results.Select(r => r.Column).ToArray());
    }

    [Fact]
    public void GetDefinitions_ReturnsDefinitionContentAndOptionalBody()
    {
        var results = _reader.GetDefinitions("authenticate", includeBody: true);

        var definition = Assert.Single(results);
        Assert.Contains("def authenticate(user, password):", definition.Content);
        Assert.NotNull(definition.BodyContent);
        Assert.Contains("return True", definition.BodyContent);
    }

    [Fact]
    public void GetDefinitions_IncludeBodyCapsBodyLinesAndMarksTruncated_Issue3131()
    {
        var bodyLines = Enumerable.Range(1, DbReader.DefinitionBodyMaxLines + 5)
            .Select(i => $"    value_{i:D2} = {i}");
        InsertIndexedFile(
            "src/long_body.py",
            "python",
            "def long_body():\n" + string.Join('\n', bodyLines) + "\n    return value_01\n");

        var definition = Assert.Single(_reader.GetDefinitions("long_body", lang: "python", includeBody: true, exact: true));

        Assert.NotNull(definition.BodyContent);
        Assert.True(definition.BodyContentTruncated);
        Assert.Null(definition.Complexity);
        Assert.True(definition.BodyContent!.Split('\n').Length <= DbReader.DefinitionBodyMaxLines);
        Assert.Contains("value_01", definition.BodyContent);
        Assert.DoesNotContain("value_25", definition.BodyContent);
    }

    [Fact]
    public void GetDefinitions_IncludeBodyCapsBodyBytesAndMarksTruncated_Issue3131()
    {
        var longLiteral = new string('a', DbReader.DefinitionBodyMaxBytes + 1024);
        InsertIndexedFile(
            "src/huge_body.py",
            "python",
            $"def huge_body():\n    value = \"{longLiteral}\"\n    return value\n");

        var definition = Assert.Single(_reader.GetDefinitions("huge_body", lang: "python", includeBody: true, exact: true));

        Assert.NotNull(definition.BodyContent);
        Assert.True(definition.BodyContentTruncated);
        Assert.Null(definition.Complexity);
        Assert.True(Encoding.UTF8.GetByteCount(definition.BodyContent!) <= DbReader.DefinitionBodyMaxBytes);
    }

    [Fact]
    public void GetDefinitions_CSharpAddsDefinitionDisambiguators()
    {
        InsertIndexedFile("src/disambiguators.cs", "csharp",
            """
            public partial class Widget
            {
                public void Convert(int value) { }
                public void Convert(string value) { }
                public static void Touch(this string value) { }
            }

            public partial class Widget
            {
            }
            """);

        var overloads = _reader.GetDefinitions("Convert", limit: 10, lang: "csharp", exact: true)
            .OrderBy(result => result.Line)
            .ToList();
        Assert.Equal(["overload(int)", "overload(string)"], overloads.Select(result => result.Disambiguator).ToArray());

        var partials = _reader.GetDefinitions("Widget", limit: 10, lang: "csharp", exact: true);
        Assert.All(partials, result => Assert.Equal("partial-class", result.Disambiguator));

        var extension = Assert.Single(_reader.GetDefinitions("Touch", limit: 10, lang: "csharp", exact: true));
        Assert.Equal("extension-method-on(string)", extension.Disambiguator);
    }






    [Fact]
    public void AllFoldedColumnsBackfilled_DetectsLegacyRowsWithNullFoldedValues()
    {
        // Regression for codex #86 review: the upgrade path must not stamp FoldReady when
        // legacy rows (pre-#86) still have NULL folded columns. Simulate by inserting a row
        // via raw SQL that bypasses the writer's folded-column population, then confirm the
        // backfill check reports missing data.
        // Codex 指摘の回帰: legacy 行が残っていれば AllFoldedColumnsBackfilled() は false を返す。
        var legacyPath = Path.Combine(Path.GetTempPath(), $"codeindex_fold_verify_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new DbContext(legacyPath);
            db.InitializeSchema();
            var writer = new DbWriter(db.Connection);

            // Happy path: fresh DB with writer-inserted rows — all folded columns populated.
            // writer 経由で入れた行は folded 付き。
            var fileId = writer.UpsertFile(new FileRecord
            {
                Path = "src/a.py",
                Lang = "python",
                Size = 1,
                Lines = 1,
                Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            writer.InsertSymbols([
                new SymbolRecord { FileId = fileId, Kind = "function", Name = "authenticate", Line = 1, StartLine = 1, EndLine = 1 },
            ]);
            Assert.True(writer.AllFoldedColumnsBackfilled());

            // Simulate a legacy row by manually nulling name_folded (as a pre-#86 row would be).
            // pre-#86 の legacy 行を模擬して name_folded を NULL に戻す。
            using (var cmd = db.Connection.CreateCommand())
            {
                cmd.CommandText = "UPDATE symbols SET name_folded = NULL WHERE name = 'authenticate'";
                cmd.ExecuteNonQuery();
            }
            Assert.False(writer.AllFoldedColumnsBackfilled());

            // Issue #1758: the readiness predicate must also reject rows that are non-NULL
            // but were folded with an older or otherwise incorrect fold key, and it must do
            // that in the same read snapshot as the NULL check.
            using (var cmd = db.Connection.CreateCommand())
            {
                cmd.CommandText = "UPDATE symbols SET name_folded = 'stale-fold-key' WHERE name = 'authenticate'";
                cmd.ExecuteNonQuery();
            }
            Assert.True(writer.AllFoldedColumnsBackfilled());
            Assert.False(writer.AllFoldedColumnsBackfilled(requireCurrentFoldKeys: true));
            Assert.False(writer.MarkFoldReady());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(legacyPath)) File.Delete(legacyPath);
        }
    }

    [Fact]
    public void GetStatus_WithFoldRowVerification_DegradesWhenReadyBitRowsAreIncomplete()
    {
        using var env = EnvironmentVariableScope.Capture(DbReader.VerifyFoldReadyRowsEnvironmentVariable);
        env.Set(DbReader.VerifyFoldReadyRowsEnvironmentVariable, "1");
        var dbPath = Path.Combine(Path.GetTempPath(), $"codeindex_fold_status_verify_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new DbContext(dbPath);
            db.InitializeSchema();
            var writer = new DbWriter(db.Connection);
            var fileId = writer.UpsertFile(new FileRecord
            {
                Path = "src/a.py",
                Lang = "python",
                Size = 1,
                Lines = 1,
                Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            writer.InsertSymbols([
                new SymbolRecord { FileId = fileId, Kind = "function", Name = "authenticate", Line = 1, StartLine = 1, EndLine = 1 },
            ]);
            Assert.True(writer.MarkFoldReady());

            using (var cmd = db.Connection.CreateCommand())
            {
                cmd.CommandText = "UPDATE symbols SET name_folded = NULL WHERE name = 'authenticate'";
                cmd.ExecuteNonQuery();
            }

            var status = new DbReader(db.Connection).GetStatus();

            Assert.False(status.FoldReady);
            Assert.Equal(DegradationReasonCodes.FoldReadyBitSetButRowsIncomplete, status.FoldReadyReason);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void GetStatus_WithFoldRowVerification_IgnoresMissingReferenceTable()
    {
        using var env = EnvironmentVariableScope.Capture(DbReader.VerifyFoldReadyRowsEnvironmentVariable);
        env.Set(DbReader.VerifyFoldReadyRowsEnvironmentVariable, "1");
        var dbPath = Path.Combine(Path.GetTempPath(), $"codeindex_fold_status_legacy_refs_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new DbContext(dbPath);
            db.InitializeSchema();
            var writer = new DbWriter(db.Connection);
            var fileId = writer.UpsertFile(new FileRecord
            {
                Path = "src/a.py",
                Lang = "python",
                Size = 1,
                Lines = 1,
                Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            writer.InsertSymbols([
                new SymbolRecord { FileId = fileId, Kind = "function", Name = "authenticate", Line = 1, StartLine = 1, EndLine = 1 },
            ]);
            Assert.True(writer.MarkFoldReady());

            using (var cmd = db.Connection.CreateCommand())
            {
                cmd.CommandText = "DROP TABLE symbol_references";
                cmd.ExecuteNonQuery();
            }
            db.RefreshSchemaCache();

            var status = new DbReader(db.Connection).GetStatus();

            Assert.True(status.FoldReady);
            Assert.Null(status.FoldReadyReason);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void AllFoldedColumnsBackfilled_DetectsEveryPartialFoldColumnState(
        bool nullSymbolName,
        bool nullReferenceSymbolName,
        bool nullReferenceContainerName)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"codeindex_fold_partial_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new DbContext(dbPath);
            db.InitializeSchema();
            var writer = new DbWriter(db.Connection);
            var fileId = writer.UpsertFile(new FileRecord
            {
                Path = "src/a.py",
                Lang = "python",
                Size = 1,
                Lines = 1,
                Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            writer.InsertSymbols([
                new SymbolRecord { FileId = fileId, Kind = "function", Name = "authenticate", Line = 1, StartLine = 1, EndLine = 1 },
            ]);
            writer.InsertReferences([
                new ReferenceRecord
                {
                    FileId = fileId,
                    SymbolName = "authenticate",
                    ReferenceKind = "call",
                    Line = 1,
                    Column = 1,
                    ContainerName = "login",
                },
            ]);

            Assert.True(writer.AllFoldedColumnsBackfilled());

            using (var cmd = db.Connection.CreateCommand())
            {
                cmd.CommandText = $"""
                    UPDATE symbols
                    SET name_folded = CASE WHEN @nullSymbolName THEN NULL ELSE name_folded END;
                    UPDATE symbol_references
                    SET
                        symbol_name_folded = CASE WHEN @nullReferenceSymbolName THEN NULL ELSE symbol_name_folded END,
                        container_name_folded = CASE WHEN @nullReferenceContainerName THEN NULL ELSE container_name_folded END;
                    """;
                cmd.Parameters.AddWithValue("@nullSymbolName", nullSymbolName);
                cmd.Parameters.AddWithValue("@nullReferenceSymbolName", nullReferenceSymbolName);
                cmd.Parameters.AddWithValue("@nullReferenceContainerName", nullReferenceContainerName);
                cmd.ExecuteNonQuery();
            }

            for (var i = 0; i < 5; i++)
                Assert.False(writer.AllFoldedColumnsBackfilled());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void GetExactGraphSupportedDefinitionLanguage_DegradesOnLegacyDbMissingContainerKind()
    {
        // Regression for #493: the exact graph-support probe hardcoded `s.container_kind`
        // instead of going through `GetSymbolColumnSql("container_kind", "''")`, so exact
        // inspect/references/callers/callees crashed with "no such column" on legacy or
        // read-only DBs where `container_kind` did not exist and `TryMigrateForRead` could
        // not add it in place. The probe must degrade gracefully (the preferNonEnumMember
        // filter becomes a no-op) rather than throw.
        // #493 回帰: legacy/read-only DB で container_kind 列が欠けていても、exact graph 経路が
        // クラッシュせず probe が成立する契約を固定する。
        var legacyPath = Path.Combine(Path.GetTempPath(), $"codeindex_issue493_{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new DbContext(legacyPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                var fileId = writer.UpsertFile(new FileRecord
                {
                    Path = "src/worker.cs",
                    Lang = "csharp",
                    Size = 40,
                    Lines = 4,
                    Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                });
                writer.InsertSymbols([
                    new SymbolRecord
                    {
                        FileId = fileId, Kind = "function", Name = "Run", Line = 3,
                        StartLine = 3, EndLine = 3, Signature = "public void Run()",
                        Visibility = "public", ContainerKind = "class", ContainerName = "Worker",
                    },
                    new SymbolRecord
                    {
                        FileId = fileId, Kind = "class", Name = "Worker", Line = 1,
                        StartLine = 1, EndLine = 4, Signature = "public class Worker",
                        Visibility = "public",
                    },
                ]);
                writer.MarkGraphReady();

                // Simulate a DB from before container_kind existed (#62-style legacy schema).
                // container_kind 列追加前の legacy schema を模擬する。
                using var cmd = db.Connection.CreateCommand();
                cmd.CommandText = "ALTER TABLE symbols DROP COLUMN container_kind";
                cmd.ExecuteNonQuery();
            }

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            using var legacyDb = new DbContext(legacyPath);
            // Deliberately skip TryMigrateForRead: on a truly read-only mount it cannot add
            // the column back, which is the scenario the issue reproduces.
            // 読み取り専用 FS 上で列を再追加できない状況を模擬するため TryMigrateForRead は呼ばない。
            var reader = new DbReader(legacyDb.Connection);

            // Both preferNonEnumMember=true (first try) and preferNonEnumMember=false (second
            // try) must execute against the column-missing schema without throwing.
            // preferNonEnumMember の両分岐が legacy schema で例外を出さずに走りきることを確認する。
            var lang = reader.GetExactGraphSupportedDefinitionLanguage("Run", null, null, null, false);
            Assert.Equal("csharp", lang);
            Assert.True(reader.HasExactGraphSupportedDefinition("Run", null, null, null, false));
            Assert.Null(reader.GetExactGraphSupportedDefinitionLanguage("DoesNotExist", null, null, null, false));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(legacyPath)) File.Delete(legacyPath);
        }
    }
















    [Fact]
    public void GetCallers_ReturnsCallingFunctions()
    {
        InsertIndexedFile("src/session.py", "python", "def login(user, password):\n    return authenticate(user, password)\n");

        var results = _reader.GetCallers("authenticate");

        var caller = Assert.Single(results);
        Assert.Equal("src/session.py", caller.Path);
        Assert.Equal("login", caller.CallerName);
        Assert.Equal("authenticate", caller.CalleeName);
        Assert.Equal(1, caller.ReferenceCount);
    }

    [Fact]
    public void GetCallers_CSharpTopLevelStatementCallSurfacesSyntheticTopLevelCaller()
    {
        InsertIndexedFile("src/Program.cs", "csharp",
            """
            using System;

            Console.WriteLine("boot");

            int Add(int a, int b) => a + b;
            void Run()
            {
                Console.WriteLine(Add(1, 2));
            }

            Run();
            """);

        var callers = _reader.GetCallers("Run", lang: "csharp", exact: true, pathPatterns: ["Program.cs"]);

        var caller = Assert.Single(callers);
        Assert.Equal("src/Program.cs", caller.Path);
        Assert.Equal("function", caller.CallerKind);
        Assert.Equal("<top-level>", caller.CallerName);
        Assert.Equal("Run", caller.CalleeName);
        Assert.Equal(1, caller.ReferenceCount);
        Assert.Equal(1, _reader.CountCallers("Run", lang: "csharp", exact: true, pathPatterns: ["Program.cs"]));
        Assert.Equal(new QueryCountResult(1, 1), _reader.CountCallersTotal("Run", lang: "csharp", exact: true, pathPatterns: ["Program.cs"]));
    }

    [Fact]
    public void GetCallers_CSharpTopLevelStatementCallWithExplicitKindSurfacesSyntheticTopLevelCaller()
    {
        InsertIndexedFile("src/Program.cs", "csharp",
            """
            using System;

            Console.WriteLine("boot");

            void Run()
            {
                Console.WriteLine("inside");
            }

            Run();
            """);

        var callers = _reader.GetCallers("Run", lang: "csharp", referenceKind: "call", exact: true, pathPatterns: ["Program.cs"]);

        var caller = Assert.Single(callers);
        Assert.Equal("src/Program.cs", caller.Path);
        Assert.Equal("function", caller.CallerKind);
        Assert.Equal("<top-level>", caller.CallerName);
        Assert.Equal("Run", caller.CalleeName);
        Assert.Equal("call", caller.ReferenceKind);
        Assert.Equal(1, caller.ReferenceCount);
    }

    [Fact]
    public void GetTransitiveCallers_CSharpExact_MapsInterfaceDispatchToConcreteImplementation()
    {
        InsertIndexedFile("src/PolymorphicDispatch.cs", "csharp",
            """
            namespace Demo;

            public interface IWorker
            {
                void Execute();
            }

            public sealed class Worker : IWorker
            {
                public void Execute() { }
            }

            public sealed class Coordinator
            {
                public void Run(IWorker worker)
                {
                    worker.Execute();
                }
            }
            """);
        InsertManualReferences("src/PolymorphicDispatch.cs",
        [
            new ReferenceRecord
            {
                SymbolName = "Demo.IWorker.Execute",
                ReferenceKind = "call",
                Line = 16,
                Column = 16,
                Context = "worker.Execute();",
                ContainerKind = "function",
                ContainerName = "Run",
            },
        ]);

        var impact = _reader.GetTransitiveCallers("Demo.Worker.Execute", maxDepth: 2, lang: "csharp", pathPatterns: ["PolymorphicDispatch.cs"]);

        var caller = Assert.Single(impact.Results);
        Assert.Equal("Run", caller.CallerName);
        Assert.Equal(1, caller.Depth);
    }

    [Fact]
    public void GetTransitiveCallers_CSharpExact_FollowsAbstractBaseDispatchToConcreteOverride()
    {
        InsertIndexedFile("src/AbstractDispatch.cs", "csharp",
            """
            namespace Demo;

            public abstract class BaseJob
            {
                public abstract void Execute();
            }

            public sealed class Job : BaseJob
            {
                public override void Execute() { }
            }

            public sealed class Scheduler
            {
                public void Schedule(BaseJob job)
                {
                    job.Execute();
                }
            }
            """);
        InsertManualReferences("src/AbstractDispatch.cs",
        [
            new ReferenceRecord
            {
                SymbolName = "Demo.BaseJob.Execute",
                ReferenceKind = "call",
                Line = 16,
                Column = 13,
                Context = "job.Execute();",
                ContainerKind = "function",
                ContainerName = "Schedule",
            },
        ]);

        var impact = _reader.GetTransitiveCallers("Demo.Job.Execute", maxDepth: 2, lang: "csharp", pathPatterns: ["AbstractDispatch.cs"]);

        var caller = Assert.Single(impact.Results);
        Assert.Equal("Schedule", caller.CallerName);
        Assert.Equal(1, caller.Depth);
    }

    [Fact]
    public void GetTransitiveCallers_CSharpExact_DoesNotMixUnrelatedSameNameHierarchies()
    {
        InsertIndexedFile("src/UnrelatedDispatch.cs", "csharp",
            """
            namespace Demo;

            public interface IWorker
            {
                void Execute();
            }

            public sealed class Worker : IWorker
            {
                public void Execute() { }
            }

            public interface IOtherWorker
            {
                void Execute();
            }

            public sealed class OtherWorker : IOtherWorker
            {
                public void Execute() { }
            }

            public sealed class Coordinator
            {
                public void RunOther(IOtherWorker worker)
                {
                    worker.Execute();
                }
            }
            """);
        InsertManualReferences("src/UnrelatedDispatch.cs",
        [
            new ReferenceRecord
            {
                SymbolName = "Demo.IOtherWorker.Execute",
                ReferenceKind = "call",
                Line = 24,
                Column = 16,
                Context = "worker.Execute();",
                ContainerKind = "function",
                ContainerName = "RunOther",
            },
        ]);

        var impact = _reader.GetTransitiveCallers("Demo.Worker.Execute", maxDepth: 2, lang: "csharp", pathPatterns: ["UnrelatedDispatch.cs"]);

        Assert.Empty(impact.Results);
    }

    [Fact]
    public void GetTransitiveCallers_CSharpExact_DoesNotUseBaseListFromDuplicateShortTypeName()
    {
        InsertIndexedFile("src/DuplicateShortTypeDispatch.cs", "csharp",
            """
            namespace Other;

            public interface IWorker
            {
                void Execute();
            }

            public sealed class Worker : IWorker
            {
                public void Execute() { }
            }

            public sealed class Coordinator
            {
                public void Run(IWorker worker)
                {
                    worker.Execute();
                }
            }

            namespace Demo;

            public sealed class Worker
            {
                public void Execute() { }
            }
            """);
        InsertManualReferences("src/DuplicateShortTypeDispatch.cs",
        [
            new ReferenceRecord
            {
                SymbolName = "Other.IWorker.Execute",
                ReferenceKind = "call",
                Line = 16,
                Column = 16,
                Context = "worker.Execute();",
                ContainerKind = "function",
                ContainerName = "Run",
            },
        ]);

        var impact = _reader.GetTransitiveCallers("Demo.Worker.Execute", maxDepth: 2, lang: "csharp", pathPatterns: ["DuplicateShortTypeDispatch.cs"]);

        Assert.Empty(impact.Results);
    }

    [Fact]
    public void GetCallees_ReturnsReferencedSymbolsForCaller()
    {
        InsertIndexedFile("src/session.py", "python", "def login(user, password):\n    return authenticate(user, password)\n");

        var results = _reader.GetCallees("login");

        var callee = Assert.Single(results);
        Assert.Equal("src/session.py", callee.Path);
        Assert.Equal("login", callee.CallerName);
        Assert.Equal("authenticate", callee.CalleeName);
        Assert.Equal("invoke", callee.ReferenceKind);
    }

    [Fact]
    public void GetCallers_CppFriendReferenceParticipatesInGraphQueries()
    {
        InsertIndexedFile("src/widget.cpp", "cpp",
            """
            class Inspector {};

            class Widget
            {
                friend class Inspector;
            };
            """);

        var callers = _reader.GetCallers("Inspector", lang: "cpp", exact: true, pathPatterns: ["widget.cpp"]);

        var caller = Assert.Single(callers);
        Assert.Equal("src/widget.cpp", caller.Path);
        Assert.Equal("class", caller.CallerKind);
        Assert.Equal("Widget", caller.CallerName);
        Assert.Equal("Inspector", caller.CalleeName);
        Assert.Equal("friend", caller.ReferenceKind);
    }




    [Fact]
    public void GetFileSymbolHotspots_GroupsByPathAndAppliesLimit()
    {
        InsertIndexedFile("src/file_hotspot_one.py", "python",
            "def AlphaFileHotspot():\n    return True\n\n" +
            "def BetaFileHotspot():\n    return True\n\n" +
            "def use_one():\n    AlphaFileHotspot()\n    AlphaFileHotspot()\n    BetaFileHotspot()\n");
        InsertIndexedFile("src/file_hotspot_two.py", "python",
            "def GammaFileHotspot():\n    return True\n\n" +
            "def use_two():\n    GammaFileHotspot()\n    GammaFileHotspot()\n");
        InsertIndexedFile("src/file_hotspot_three.py", "python",
            "def DeltaFileHotspot():\n    return True\n\n" +
            "def use_three():\n    DeltaFileHotspot()\n");

        var results = _reader.GetFileSymbolHotspots(
            limit: 2,
            kind: "function",
            lang: "python",
            pathPatterns: ["src/file_hotspot_"],
            excludePathPatterns: null,
            excludeTests: false);

        Assert.Collection(results,
            first =>
            {
                Assert.Equal("src/file_hotspot_one.py", first.Path);
                Assert.Equal("python", first.Lang);
                Assert.Equal(3, first.ReferenceCount);
                Assert.Equal(2, first.SymbolCount);
            },
            second =>
            {
                Assert.Equal("src/file_hotspot_two.py", second.Path);
                Assert.Equal("python", second.Lang);
                Assert.Equal(2, second.ReferenceCount);
                Assert.Equal(1, second.SymbolCount);
            });
    }













    [Fact]
    public void GetHotspotFamilySignal_LegacyPartialFamiliesWithoutPersistedKeysAreStillDegraded()
    {
        InsertIndexedFile("src/Api.Part1.cs", "csharp",
            """
            public partial class Api
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/Api.Part2.cs", "csharp",
            """
            public partial class Api
            {
                public void Run(int value) { }
            }
            """);
        InsertIndexedFile("src/Caller.cs", "csharp",
            """
            public class Caller
            {
                public void Call(Api api)
                {
                    api.Run();
                    api.Run(1);
                }
            }
            """);

        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE symbols
                SET family_key = NULL,
                    container_qualified_name = NULL
                WHERE file_id IN (
                    SELECT id FROM files WHERE lang = 'csharp'
                )
                """;
            cmd.ExecuteNonQuery();
        }
        _writer.SetMeta(DbContext.GetHotspotFamilyVersionMetaKey("csharp"), null);
        _writer.SetMeta(DbContext.GetHotspotFamilyMarkerFingerprintMetaKey("csharp"), null);

        var reader = new DbReader(_db.Connection);
        var signal = reader.GetHotspotFamilySignal("csharp");

        Assert.True(signal.Relevant);
        Assert.False(signal.Ready);
        Assert.Contains("hotspot_family_support_not_indexed=csharp", signal.DegradedReason);
    }

    [Fact]
    public void GetHotspotFamilySignal_CurrentStampWithPartialFamilyRowsIsDegraded()
    {
        InsertIndexedFile("src/Api.Part1.cs", "csharp",
            """
            public partial class Api
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/Api.Part2.cs", "csharp",
            """
            public partial class Api
            {
                public void Run(int value) { }
            }
            """);

        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE symbols
                SET family_key = NULL
                WHERE file_id IN (
                    SELECT id FROM files WHERE path = 'src/Api.Part2.cs'
                )
                """;
            cmd.ExecuteNonQuery();
        }

        var reader = new DbReader(_db.Connection);
        var signal = reader.GetHotspotFamilySignal("csharp");

        Assert.True(signal.Relevant);
        Assert.False(signal.Ready);
        Assert.Contains("partial_family_key_population=csharp", signal.DegradedReason);
    }

    [Fact]
    public void GetStatus_ExposesPerLanguageReadinessMap()
    {
        InsertIndexedFile("src/Api.Part1.cs", "csharp",
            """
            public partial class Api
            {
                public void Run() { }
            }
            """);

        var reader = new DbReader(_db.Connection);
        var status = reader.GetStatus();

        Assert.NotNull(status.LanguageReadiness);
        Assert.True(status.LanguageReadiness!.ContainsKey("csharp"));
        Assert.True(status.LanguageReadiness["csharp"]["hotspot_family"].Ready);
        Assert.True(status.LanguageReadiness["csharp"].ContainsKey("symbol_name"));
        Assert.True(status.LanguageReadiness["csharp"].ContainsKey("metadata_target"));
    }

    [Fact]
    public void GetHotspotFamilySignal_MissingMarkerFingerprintIsStillDegraded()
    {
        InsertIndexedFile("src/Api.Part1.cs", "csharp",
            """
            public partial class Api
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/Api.Part2.cs", "csharp",
            """
            public partial class Api
            {
                public void Run(int value) { }
            }
            """);
        InsertIndexedFile("src/Caller.cs", "csharp",
            """
            public class Caller
            {
                public void Call(Api api)
                {
                    api.Run();
                    api.Run(1);
                }
            }
            """);

        _writer.SetMeta(DbContext.GetHotspotFamilyMarkerFingerprintMetaKey("csharp"), null);

        var reader = new DbReader(_db.Connection);
        var signal = reader.GetHotspotFamilySignal("csharp");

        Assert.True(signal.Relevant);
        Assert.False(signal.Ready);
        Assert.Contains("csharp", signal.DegradedReason);

        var results = reader.GetSymbolHotspots(
            limit: 10,
            kind: "function",
            lang: "csharp",
            pathPatterns: ["src/"],
            excludePathPatterns: null,
            excludeTests: false);

        Assert.DoesNotContain(results, result => result.Symbol.Name == "Run");
    }

    [Fact]
    public void GetHotspotFamilySignal_IncompleteMarkerFingerprintReportsSpecificReason()
    {
        InsertIndexedFile("src/Api.Part1.cs", "csharp",
            """
            public partial class Api
            {
                public void Run() { }
            }
            """);

        _writer.MarkHotspotFamilyMarkerFingerprintIncomplete("csharp", "truncated-fixture");

        var reader = new DbReader(_db.Connection);
        var signal = reader.GetHotspotFamilySignal("csharp");
        var status = reader.GetStatus();

        Assert.True(signal.Relevant);
        Assert.False(signal.Ready);
        Assert.Contains($"{DegradationReasonCodes.HotspotFamilyMarkerFingerprintIncomplete}=csharp", signal.DegradedReason);
        Assert.Contains(".gitignore", signal.DegradedReason);
        Assert.False(status.HotspotFamilyReady);
        Assert.Contains($"{DegradationReasonCodes.HotspotFamilyMarkerFingerprintIncomplete}=csharp", status.HotspotFamilyDegradedReason);
        Assert.False(status.LanguageReadiness!["csharp"]["hotspot_family"].Ready);
        Assert.Contains(
            DegradationReasonCodes.HotspotFamilyMarkerFingerprintIncomplete,
            status.LanguageReadiness["csharp"]["hotspot_family"].DegradedReason);
    }






    [Fact]
    public void GraphReaders_ExactMatchesNameEquality()
    {
        // Seed content where `authenticate_v2` is both CALLED (so it appears as a reference
        // `symbol_name`) and calls `authenticate` (so it appears as a `container_name`). Substring
        // mode for `authenticate` matches both rows; exact mode returns only `authenticate`.
        // Mirrors the semantics codex nailed in #81 — case-insensitive equality, no substring expansion.
        // authenticate_v2 を呼び出しもし、中から authenticate も呼び出す内容を仕込む。
        InsertIndexedFile("src/auth_v2.py", "python",
            "def authenticate_v2(user, password):\n    authenticate(user, password)\n    return True\n\n" +
            "def wrapper(u, p):\n    return authenticate_v2(u, p)\n");

        // references
        var refsSub = _reader.SearchReferences("authenticate", exact: false)
            .Select(r => r.SymbolName).Distinct().OrderBy(n => n).ToList();
        Assert.Contains("authenticate", refsSub);
        Assert.Contains("authenticate_v2", refsSub);

        var refsExact = _reader.SearchReferences("authenticate", exact: true)
            .Select(r => r.SymbolName).Distinct().ToList();
        Assert.Equal(new[] { "authenticate" }, refsExact);

        // callers (filter on callee / symbol_name)
        var callersSub = _reader.GetCallers("authenticate", exact: false)
            .Select(r => r.CalleeName).Distinct().OrderBy(n => n).ToList();
        Assert.Contains("authenticate", callersSub);
        Assert.Contains("authenticate_v2", callersSub);

        var callersExact = _reader.GetCallers("authenticate", exact: true)
            .Select(r => r.CalleeName).Distinct().ToList();
        Assert.Equal(new[] { "authenticate" }, callersExact);

        // callees (filter on container_name)
        var calleesSub = _reader.GetCallees("authenticate", exact: false)
            .Select(r => r.CallerName).Distinct().OrderBy(n => n ?? "").ToList();
        Assert.Contains("authenticate_v2", calleesSub);

        var calleesExact = _reader.GetCallees("authenticate", exact: true)
            .Select(r => r.CallerName).Distinct().ToList();
        Assert.DoesNotContain("authenticate_v2", calleesExact);

        // Case-insensitive equality across all three.
        Assert.Single(_reader.SearchReferences("AUTHENTICATE", exact: true));
        Assert.Single(_reader.GetCallers("AUTHENTICATE", exact: true));
    }

    [Fact]
    public void GraphReaders_QualifiedMemberQueriesUseContextAndDefinitionFallback_Issue2819()
    {
        InsertIndexedFile("src/issue2819/HttpMcpTransport.cs", "csharp",
            """
            namespace Issue2819;

            public sealed class HttpMcpTransport
            {
                public void HandleContext()
                {
                    RunEventStreamAsync();
                    System.Guid.NewGuid();
                }

                private void RunEventStreamAsync()
                {
                    Guid.NewGuid();
                }
            }
            """);

        var definition = Assert.Single(_reader.GetDefinitions(
            "HttpMcpTransport.RunEventStreamAsync",
            lang: "csharp",
            exact: true,
            pathPatterns: ["issue2819"]));
        Assert.Equal("RunEventStreamAsync", definition.Name);
        Assert.Equal("HttpMcpTransport", definition.ContainerName);

        var reference = Assert.Single(_reader.SearchReferences(
            "HttpMcpTransport.RunEventStreamAsync",
            lang: "csharp",
            exact: true,
            pathPatterns: ["issue2819"]));
        Assert.Equal("RunEventStreamAsync", reference.SymbolName);
        Assert.Equal("HandleContext", reference.ContainerName);
        Assert.Equal(1, _reader.CountSearchReferences(
            "HttpMcpTransport.RunEventStreamAsync",
            lang: "csharp",
            exact: true,
            pathPatterns: ["issue2819"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: false), _reader.CountSearchReferencesTotal(
            "HttpMcpTransport.RunEventStreamAsync",
            lang: "csharp",
            exact: true,
            pathPatterns: ["issue2819"]));

        var caller = Assert.Single(_reader.GetCallers(
            "HttpMcpTransport.RunEventStreamAsync",
            lang: "csharp",
            exact: true,
            pathPatterns: ["issue2819"]));
        Assert.Equal("HandleContext", caller.CallerName);
        Assert.Equal(1, _reader.CountCallers(
            "HttpMcpTransport.RunEventStreamAsync",
            lang: "csharp",
            exact: true,
            pathPatterns: ["issue2819"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: false), _reader.CountCallersTotal(
            "HttpMcpTransport.RunEventStreamAsync",
            lang: "csharp",
            exact: true,
            pathPatterns: ["issue2819"]));

        var callees = _reader.GetCallees(
            "HttpMcpTransport.RunEventStreamAsync",
            lang: "csharp",
            exact: true,
            pathPatterns: ["issue2819"]);
        Assert.Contains(callees, result => result.CallerName == "RunEventStreamAsync" && result.CalleeName == "NewGuid");
        Assert.Equal(callees.Count, _reader.CountCallees(
            "HttpMcpTransport.RunEventStreamAsync",
            lang: "csharp",
            exact: true,
            pathPatterns: ["issue2819"]));
        Assert.Equal(new QueryCountResult(callees.Count, 1, IncludesSql: false), _reader.CountCalleesTotal(
            "HttpMcpTransport.RunEventStreamAsync",
            lang: "csharp",
            exact: true,
            pathPatterns: ["issue2819"]));

        var frameworkCallers = _reader.GetCallers(
            "System.Guid.NewGuid",
            lang: "csharp",
            exact: true,
            pathPatterns: ["issue2819"]);
        Assert.Contains(frameworkCallers, result => result.CallerName == "HandleContext");
        Assert.Contains(frameworkCallers, result => result.CallerName == "RunEventStreamAsync");
    }

    [Fact]
    public void GraphReaders_QualifiedMemberLeafFallbackRequiresUniqueLeaf_Issue2819()
    {
        InsertIndexedFile("src/issue2819/AmbiguousLeaf.cs", "csharp",
            """
            namespace Issue2819;

            public sealed class TargetTransport
            {
                public void RunEventStreamAsync() { }
            }

            public sealed class OtherTransport
            {
                public void RunEventStreamAsync() { }

                public void HandleOther()
                {
                    RunEventStreamAsync();
                }
            }
            """);

        Assert.Empty(_reader.SearchReferences(
            "TargetTransport.RunEventStreamAsync",
            lang: "csharp",
            exact: true,
            pathPatterns: ["issue2819/AmbiguousLeaf"]));

        Assert.Empty(_reader.GetCallers(
            "TargetTransport.RunEventStreamAsync",
            lang: "csharp",
            exact: true,
            pathPatterns: ["issue2819/AmbiguousLeaf"]));
    }

    [Fact]
    public void GraphReaders_ExactPrefersExactCaseOverFoldSibling()
    {
        InsertIndexedFile("src/a_case.py", "python",
            "def apiTwin():\n    authenticate('a', 'b')\n    return True\n\n" +
            "def lower_wrapper():\n    return apiTwin()\n");
        InsertIndexedFile("tests/z_case.py", "python",
            "def ApiTwin():\n    authenticate('a', 'b')\n    return True\n\n" +
            "def upper_wrapper():\n    return ApiTwin()\n");

        var references = _reader.SearchReferences("ApiTwin", exact: true)
            .Where(r => r.SymbolName is "ApiTwin" or "apiTwin")
            .Select(r => r.SymbolName)
            .Distinct()
            .Take(2)
            .ToList();
        Assert.Equal(new[] { "ApiTwin", "apiTwin" }, references);

        var callers = _reader.GetCallers("ApiTwin", exact: true)
            .Where(r => r.CalleeName is "ApiTwin" or "apiTwin")
            .Select(r => r.CalleeName)
            .Distinct()
            .Take(2)
            .ToList();
        Assert.Equal(new[] { "ApiTwin", "apiTwin" }, callers);

        var callees = _reader.GetCallees("ApiTwin", exact: true)
            .Where(r => r.CallerName is "ApiTwin" or "apiTwin")
            .Select(r => r.CallerName)
            .Distinct()
            .Take(2)
            .ToList();
        Assert.Equal(new[] { "ApiTwin", "apiTwin" }, callees);

        var topReference = Assert.Single(_reader.SearchReferences("ApiTwin", limit: 1, exact: true));
        Assert.Equal("ApiTwin", topReference.SymbolName);
        Assert.Equal("tests/z_case.py", topReference.Path);

        var topCaller = Assert.Single(_reader.GetCallers("ApiTwin", limit: 1, exact: true));
        Assert.Equal("ApiTwin", topCaller.CalleeName);
        Assert.Equal("tests/z_case.py", topCaller.Path);

        var topCallee = Assert.Single(_reader.GetCallees("ApiTwin", limit: 1, exact: true));
        Assert.Equal("ApiTwin", topCallee.CallerName);
        Assert.Equal("tests/z_case.py", topCallee.Path);
    }

    [Fact]
    public void GetTransitiveCallers_ExactUsesUnicodeFoldForResolutionAndCallerMatch()
    {
        // Regression for #93: impact BFS used ASCII-only equality in both ResolveSymbolName()
        // and GetCallersExact(), so a mixed fullwidth/non-ASCII query could miss even when
        // the definition and caller rows were both present and fold-equivalent.
        // #93 回帰: impact BFS の 2 箇所が ASCII-only 比較だったため、fullwidth と
        // 非 ASCII 大文字を含むクエリで definition / caller が両方揃っていても取りこぼした。
        var symbolFileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/intl.py",
            Lang = "python",
            Size = 48,
            Lines = 2,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks([new ChunkRecord
        {
            FileId = symbolFileId,
            ChunkIndex = 0,
            StartLine = 1,
            EndLine = 2,
            Content = "def café_init():\n    return True\n",
        }]);
        _writer.InsertSymbols([
            new SymbolRecord
            {
                FileId = symbolFileId,
                Kind = "function",
                Name = "café_init",
                Line = 1,
                StartLine = 1,
                EndLine = 2,
                BodyStartLine = 2,
                BodyEndLine = 2,
                Signature = "def café_init():",
            },
        ]);

        var callerFileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/bootstrap.py",
            Lang = "python",
            Size = 58,
            Lines = 2,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks([new ChunkRecord
        {
            FileId = callerFileId,
            ChunkIndex = 0,
            StartLine = 1,
            EndLine = 2,
            Content = "def bootstrap():\n    return CAFÉ_INIT()\n",
        }]);
        _writer.InsertSymbols([
            new SymbolRecord
            {
                FileId = callerFileId,
                Kind = "function",
                Name = "bootstrap",
                Line = 1,
                StartLine = 1,
                EndLine = 2,
                BodyStartLine = 2,
                BodyEndLine = 2,
                Signature = "def bootstrap():",
            },
        ]);
        _writer.InsertReferences([
            new ReferenceRecord
            {
                FileId = callerFileId,
                SymbolName = "CAFÉ_INIT",
                ReferenceKind = "call",
                Line = 2,
                Column = 12,
                Context = "return CAFÉ_INIT()",
                ContainerKind = "function",
                ContainerName = "bootstrap",
            },
        ]);

        var (results, truncated, truncatedReason, _, _) = _reader.GetTransitiveCallers("ＣＡＦÉ_ＩＮＩＴ", maxDepth: 1, limit: 10);

        Assert.False(truncated);
        Assert.Null(truncatedReason);
        var caller = Assert.Single(results);
        Assert.Equal("src/bootstrap.py", caller.Path);
        Assert.Equal("bootstrap", caller.CallerName);
        Assert.Equal("CAFÉ_INIT", caller.CalleeName);
        Assert.Equal(1, caller.Depth);
    }

    [Fact]
    public void GetTransitiveCallers_CSharpTopLevelStatementCallSurfacesSyntheticTopLevelCallerWithoutRecursing()
    {
        InsertIndexedFile("src/Program.cs", "csharp",
            """
            using System;

            Console.WriteLine("boot");

            void Run()
            {
                Console.WriteLine("inside");
            }

            Run();
            """);

        var (results, truncated, truncatedReason, _, _) = _reader.GetTransitiveCallers("Run", maxDepth: 3, limit: 10, lang: "csharp", pathPatterns: ["Program.cs"]);

        Assert.False(truncated);
        Assert.Null(truncatedReason);
        var caller = Assert.Single(results);
        Assert.Equal("src/Program.cs", caller.Path);
        Assert.Equal("function", caller.CallerKind);
        Assert.Equal("<top-level>", caller.CallerName);
        Assert.Equal("Run", caller.CalleeName);
        Assert.Equal(1, caller.Depth);
        Assert.Equal(1, caller.ReferenceCount);
    }

    [Fact]
    public void GetDefinitions_ExactMatchesNameEquality()
    {
        var extraFileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/auth_v2.py",
            Lang = "python",
            Size = 80,
            Lines = 4,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks([new ChunkRecord
        {
            FileId = extraFileId, ChunkIndex = 0, StartLine = 1, EndLine = 1,
            Content = "def authenticate_v2(user, password): pass",
        }]);
        _writer.InsertSymbols([
            new SymbolRecord { FileId = extraFileId, Kind = "function", Name = "authenticate_v2", Line = 1, StartLine = 1, EndLine = 1 },
        ]);

        var substring = _reader.GetDefinitions("authenticate", exact: false)
            .Select(r => r.Name).Distinct().OrderBy(n => n).ToList();
        Assert.Contains("authenticate", substring);
        Assert.Contains("authenticate_v2", substring);

        var exact = _reader.GetDefinitions("authenticate", exact: true)
            .Select(r => r.Name).Distinct().ToList();
        Assert.Equal(new[] { "authenticate" }, exact);
    }

    [Fact]
    public void AnalyzeSymbol_ExactPropagatesToBundledSubQueries()
    {
        // The bundled one-round-trip path (`inspect` / MCP `analyze_symbol`) must propagate
        // `exact` into every sub-query — otherwise the bundle keeps returning RunAsync/RunImpact
        // spillover even when the caller asked for precision. Codex adversarial review of #83.
        // bundle 側も `exact` を尊重すること（definitions / references / callers / callees）。
        InsertIndexedFile("src/auth_v2.py", "python",
            "def authenticate_v2(user, password):\n    authenticate(user, password)\n    return True\n\n" +
            "def wrapper(u, p):\n    return authenticate_v2(u, p)\n");

        var exactBundle = _reader.AnalyzeSymbol("authenticate", exact: true);
        Assert.All(exactBundle.Definitions, d => Assert.Equal("authenticate", d.Name));
        Assert.All(exactBundle.References, r => Assert.Equal("authenticate", r.SymbolName));
        Assert.All(exactBundle.Callers, c => Assert.Equal("authenticate", c.CalleeName));
        // Callees are filtered on container_name, so exact must reject `authenticate_v2` as a container.
        // callees は container_name で絞るため、authenticate_v2 を含んではいけない。
        Assert.DoesNotContain(exactBundle.Callees, c => c.CallerName == "authenticate_v2");

        var substringBundle = _reader.AnalyzeSymbol("authenticate", exact: false);
        Assert.Contains(substringBundle.Definitions, d => d.Name == "authenticate_v2");
    }

    [Fact]
    public void AnalyzeSymbol_ExactZeroHint_OnlyWhenWholeBundleIsEmpty()
    {
        InsertIndexedFile("src/handlers.cs", "csharp",
            """
            public class Handler
            {
                public void HandleRequest() { }
                public void HandleRequestAsync() { HandleRequest(); }
            }
            """);

        var exactMiss = _reader.AnalyzeSymbol("HandleRe", exact: true);
        Assert.NotNull(exactMiss.ExactZeroHint);
        Assert.Equal(2, exactMiss.ExactZeroHint!.RelaxedCount);
        Assert.Contains("HandleRequest", exactMiss.ExactZeroHint.SampleNames);
        Assert.Contains("HandleRequestAsync", exactMiss.ExactZeroHint.SampleNames);

        var exactHit = _reader.AnalyzeSymbol("HandleRequest", exact: true);
        Assert.Null(exactHit.ExactZeroHint);
    }

    [Fact]
    public void AnalyzeSymbol_BareVerbatimTokenFailsClosed()
    {
        InsertIndexedFile("src/app.cs", "csharp", "public class Foo { public int Bar() => 0; }\n");

        var analysis = _reader.AnalyzeSymbol("@", lang: "csharp", exact: true);
        var callers = _reader.GetCallers("@", lang: "csharp", exact: true);
        var callees = _reader.GetCallees("@", lang: "csharp", exact: true);

        Assert.Equal("@", analysis.Query);
        Assert.Empty(analysis.Definitions);
        Assert.Empty(analysis.References);
        Assert.Empty(analysis.Callers);
        Assert.Empty(analysis.Callees);
        Assert.Empty(analysis.NearbySymbols);
        Assert.Null(analysis.File);
        Assert.Empty(callers);
        Assert.Empty(callees);
        Assert.Equal(0, _reader.CountCallers("@", lang: "csharp", exact: true));
        Assert.Equal(0, _reader.CountCallees("@", lang: "csharp", exact: true));
    }

    [Fact]
    public void GraphReaders_ExactPredicatesAreIndexable()
    {
        // Guard: `references / callers / callees --exact` must stay SARGable so SQLite can
        // pick the new NOCASE covering indexes on symbol_references(symbol_name / container_name).
        // Mirrors SearchSymbols_ExactPredicateIsIndexable from #81.
        // references / callers / callees --exact 用の NOCASE index 使用を固定する回帰テスト。
        using var cmdRef = _db.Connection.CreateCommand();
        cmdRef.CommandText = "EXPLAIN QUERY PLAN SELECT r.line FROM symbol_references r WHERE r.symbol_name = @q COLLATE NOCASE";
        cmdRef.Parameters.AddWithValue("@q", "authenticate");
        var refPlan = new System.Text.StringBuilder();
        using (var rr = cmdRef.ExecuteReader())
            while (rr.Read()) refPlan.AppendLine(rr.GetString(3));
        Assert.Contains("idx_symbol_refs_name_nocase", refPlan.ToString());

        using var cmdCon = _db.Connection.CreateCommand();
        cmdCon.CommandText = "EXPLAIN QUERY PLAN SELECT r.line FROM symbol_references r WHERE r.container_name = @q COLLATE NOCASE";
        cmdCon.Parameters.AddWithValue("@q", "login");
        var conPlan = new System.Text.StringBuilder();
        using (var cr = cmdCon.ExecuteReader())
            while (cr.Read()) conPlan.AppendLine(cr.GetString(3));
        Assert.Contains("idx_symbol_refs_container_nocase", conPlan.ToString());
    }

    [Fact]
    public void GraphReaders_IgnoreLegacyReferencesFromUnsupportedLanguages()
    {
        var pythonFileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/session.py",
            Lang = "python",
            Size = 80,
            Lines = 2,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks([new ChunkRecord
        {
            FileId = pythonFileId,
            ChunkIndex = 0,
            StartLine = 1,
            EndLine = 2,
            Content = "def login(user, password):\n    return authenticate(user, password)\n",
        }]);
        _writer.InsertReferences([
            new ReferenceRecord
            {
                FileId = pythonFileId,
                SymbolName = "authenticate",
                ReferenceKind = "call",
                Line = 2,
                Column = 12,
                Context = "return authenticate(user, password)",
                ContainerKind = "function",
                ContainerName = "login",
            },
        ]);

        var shellFileId = _writer.UpsertFile(new FileRecord
        {
            Path = "scripts/legacy.txt",
            Lang = "text",
            Size = 48,
            Lines = 2,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks([new ChunkRecord
        {
            FileId = shellFileId,
            ChunkIndex = 0,
            StartLine = 1,
            EndLine = 2,
            Content = "login() {\n  authenticate \"$1\"\n}\n",
        }]);
        _writer.InsertReferences([
            new ReferenceRecord
            {
                FileId = shellFileId,
                SymbolName = "authenticate",
                ReferenceKind = "call",
                Line = 2,
                Column = 3,
                Context = "authenticate \"$1\"",
                ContainerKind = "function",
                ContainerName = "login",
            },
        ]);

        var references = _reader.SearchReferences("authenticate");
        var callers = _reader.GetCallers("authenticate");
        var callees = _reader.GetCallees("login");

        var reference = Assert.Single(references);
        Assert.Equal("src/session.py", reference.Path);

        var caller = Assert.Single(callers);
        Assert.Equal("src/session.py", caller.Path);

        var callee = Assert.Single(callees);
        Assert.Equal("src/session.py", callee.Path);
    }

    [Fact]
    public void GraphQueries_DefaultCountsDeduplicateConstructorCallAndInstantiateSites()
    {
        InsertIndexedFile("src/constructor_fixture_target.cs", "csharp",
            """
            public class Target
            {
                public Target() { }
            }
            """);
        InsertIndexedFile("src/constructor_fixture_caller.cs", "csharp",
            """
            public class Caller
            {
                public void Run()
                {
                    var target = new Target();
                }
            }
            """);

        var refs = _reader.SearchReferences("Target", lang: "csharp", exact: true, pathPatterns: ["constructor_fixture"]);
        var reference = Assert.Single(refs);
        Assert.Equal("instantiate", reference.ReferenceKind);
        Assert.Equal(1, _reader.CountSearchReferences("Target", lang: "csharp", exact: true, pathPatterns: ["constructor_fixture"]));

        var caller = Assert.Single(_reader.GetCallers("Target", lang: "csharp", exact: true, pathPatterns: ["constructor_fixture"]));
        Assert.Equal("Run", caller.CallerName);
        Assert.Equal(1, caller.ReferenceCount);

        var callee = Assert.Single(_reader.GetCallees("Run", lang: "csharp", exact: true, pathPatterns: ["constructor_fixture"]));
        Assert.Equal("Target", callee.CalleeName);
        Assert.Equal("invoke", callee.ReferenceKind);
        Assert.Equal(1, callee.ReferenceCount);

        var (impact, truncated, truncatedReason, _, _) = _reader.GetTransitiveCallers("Target", maxDepth: 1, limit: 10, lang: "csharp", pathPatterns: ["constructor_fixture"]);
        Assert.False(truncated);
        Assert.Null(truncatedReason);
        var impactCaller = Assert.Single(impact);
        Assert.Equal("Run", impactCaller.CallerName);
        Assert.Equal(1, impactCaller.ReferenceCount);

        var hotspot = Assert.Single(_reader.GetSymbolHotspots(10, "class", "csharp", ["constructor_fixture"], null, false), item => item.Symbol.Name == "Target");
        Assert.Equal(1, hotspot.ReferenceCount);

        var dependency = Assert.Single(_reader.GetFileDependencies(limit: 10, lang: "csharp", pathPatterns: ["constructor_fixture_caller.cs"], excludePathPatterns: null, excludeTests: false));
        Assert.Equal("src/constructor_fixture_caller.cs", dependency.SourcePath);
        Assert.Equal("src/constructor_fixture_target.cs", dependency.TargetPath);
        Assert.Equal(1, dependency.ReferenceCount);
    }

    [Fact]
    public void GraphQueries_CsharpBareMemberCallersSkipReceiverQualifiedCalls()
    {
        InsertIndexedFile("src/common_member_graph_fixture.cs", "csharp",
            """
            using System.Text.Json;

            public class Caller
            {
                private string GetString() => "";

                public void Run(LocalApi api, JsonElement json)
                {
                    api.GetString();
                    json.GetString();
                    GetString();
                }
            }

            public class LocalApi
            {
                public string GetString() => "";
            }
            """);

        var callers = _reader.GetCallers("GetString", lang: "csharp", exact: true, pathPatterns: ["common_member_graph_fixture"]);

        var caller = Assert.Single(callers);
        Assert.Equal("Run", caller.CallerName);
        Assert.Equal(1, caller.ReferenceCount);
        Assert.Equal(1, _reader.CountCallers("GetString", lang: "csharp", exact: true, pathPatterns: ["common_member_graph_fixture"]));
        var total = _reader.CountCallersTotal("GetString", lang: "csharp", exact: true, pathPatterns: ["common_member_graph_fixture"]);
        Assert.Equal(1, total.Count);
        Assert.Equal(1, total.FileCount);
    }


    [Fact]
    public void SqlBareCalls_AlignAggregateReadersWithLeafFallback()
    {
        InsertIndexedFile("src/sql_bare_call_caller.sql", "sql",
            """
            CREATE PROCEDURE sales.host
            AS
            BEGIN
                EXEC fn_Target;
            END
            GO
            """);
        InsertIndexedFile("src/sql_bare_call_target.sql", "sql",
            """
            CREATE PROCEDURE dbo.fn_Target
            AS
            BEGIN
                SELECT 1;
            END
            GO
            """);

        var caller = Assert.Single(_reader.GetCallers("fn_Target", lang: "sql", exact: true, pathPatterns: ["sql_bare_call_"]));
        Assert.Equal("sales.host", caller.CallerName);
        Assert.Equal(1, caller.ReferenceCount);

        var dependencies = _reader.GetFileDependencies(limit: 10, lang: "sql", pathPatterns: ["sql_bare_call_"], excludePathPatterns: null, excludeTests: false);
        var dependency = Assert.Single(dependencies);
        Assert.Equal("src/sql_bare_call_caller.sql", dependency.SourcePath);
        Assert.Equal("src/sql_bare_call_target.sql", dependency.TargetPath);
        Assert.Equal(1, dependency.ReferenceCount);

        var tsqlDependencies = _reader.GetFileDependencies(limit: 10, lang: "tsql", pathPatterns: ["sql_bare_call_"], excludePathPatterns: null, excludeTests: false);
        var tsqlDependency = Assert.Single(tsqlDependencies);
        Assert.Equal("src/sql_bare_call_caller.sql", tsqlDependency.SourcePath);
        Assert.Equal("src/sql_bare_call_target.sql", tsqlDependency.TargetPath);
        Assert.Equal(1, tsqlDependency.ReferenceCount);

        var hotspot = Assert.Single(
            _reader.GetSymbolHotspots(10, "function", "sql", ["sql_bare_call_"], null, false),
            item => item.Symbol.Name == "dbo.fn_Target");
        Assert.Equal(1, hotspot.ReferenceCount);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: "function", lang: "sql",
            pathPatterns: ["sql_bare_call_"], excludePathPatterns: null, excludeTests: false);
        Assert.DoesNotContain(unused, symbol => symbol.Name == "dbo.fn_Target");
        Assert.Contains(unused, symbol => symbol.Name == "sales.host");
        var unusedCount = _reader.CountUnusedSymbols(kind: "function", lang: "sql",
            pathPatterns: ["sql_bare_call_"], excludePathPatterns: null, excludeTests: false);
        Assert.Equal(1, unusedCount.Count);
        Assert.Equal(1, unusedCount.FileCount);
    }




    private long InsertSyntheticDependencyFile(string path)
    {
        return _writer.UpsertFile(new FileRecord
        {
            Path = path,
            Lang = "csharp",
            Size = 1,
            Lines = 1,
            Modified = new DateTime(2026, 6, 6, 0, 0, 0, DateTimeKind.Utc),
            Checksum = Guid.NewGuid().ToString("N"),
        });
    }



















































































    [Fact]
    public void ReferenceKindMatrix_DepsKeepsMetadataInBothDirections_CallersExcludesMetadata()
    {
        // Regression for issue #1882 — pins the intentional reference_kind filter
        // split between the call graph and the dependency graph:
        //   * `deps` (forward AND reverse, single `GetFileDependencies` SQL)
        //     keeps `attribute` / `annotation` rows as compile-time edges.
        //   * `callers` (and the transitive impact BFS that reuses
        //     `CallGraphReferenceKindsSql`) drops metadata kinds.
        // The reconciliation path documented in DEVELOPER_GUIDE.md's
        // reference_kind filtering matrix is `references --kind attribute`
        // (i.e. `SearchReferences(referenceKind: "attribute")`), which still
        // surfaces the metadata-only edge that the call-graph view drops.
        // issue #1882 リグレッション — 呼び出しグラフと依存グラフで
        // reference_kind フィルタが意図的に異なる契約を固定する:
        //   * `deps` は前進 / 逆方向で同じ `GetFileDependencies` SQL を共有し、
        //     `attribute` / `annotation` も compile-time エッジとして残す。
        //   * `callers` (および `CallGraphReferenceKindsSql` を再利用する
        //     `impact` BFS) は metadata 種別を除外する。
        // 差分を埋める導線は DEVELOPER_GUIDE.md の対応表に記載した
        // `references --kind attribute` (`SearchReferences(referenceKind:
        // "attribute")`) で、call-graph 側が落とした metadata エッジを救う。
        InsertIndexedFile("src/MatrixTarget.cs", "csharp",
            """
            using System;

            [AttributeUsage(AttributeTargets.Class)]
            public class MatrixTarget : Attribute
            {
                public MatrixTarget(Type t) { }
            }
            """);
        InsertIndexedFile("src/MatrixAnnotated.cs", "csharp",
            """
            [MatrixTarget(typeof(int))]
            public class MatrixAnnotated
            {
            }
            """);
        InsertIndexedFile("src/MatrixRuntimeCaller.cs", "csharp",
            """
            public class MatrixRuntimeCaller
            {
                public void Do()
                {
                    var x = new MatrixTarget(typeof(int));
                }
            }
            """);

        // Forward deps: both the runtime `new MatrixTarget(...)` edge and the
        // metadata `[MatrixTarget(...)]` edge surface as compile-time
        // dependencies of MatrixTarget.cs.
        // 前進 deps: runtime の `new MatrixTarget(...)` と metadata の
        // `[MatrixTarget(...)]` の両方が MatrixTarget.cs への依存として現れる。
        var forward = _reader.GetFileDependencies(limit: 10, lang: "csharp", pathPatterns: ["Matrix"]);
        Assert.Contains(forward, d => d.SourcePath == "src/MatrixAnnotated.cs" && d.TargetPath == "src/MatrixTarget.cs");
        Assert.Contains(forward, d => d.SourcePath == "src/MatrixRuntimeCaller.cs" && d.TargetPath == "src/MatrixTarget.cs");

        // Reverse deps share the same SQL function. `reverse: true` only flips
        // which side path filters apply to, so the reference_kind set must be
        // identical between directions. This assertion pins that the two
        // directions cannot drift apart and start filtering metadata
        // asymmetrically.
        // 逆方向 deps は同じ SQL 関数を共有する。`reverse: true` は path filter の
        // 当て先を source / target で入れ替えるだけのため、reference_kind 集合は
        // 前進と同一でなければならない。前進 / 逆方向の filter が乖離して
        // metadata の扱いが非対称になる事態を防ぐ assertion。
        var reverse = _reader.GetFileDependencies(limit: 10, lang: "csharp", pathPatterns: ["MatrixTarget"], reverse: true);
        Assert.Contains(reverse, d => d.SourcePath == "src/MatrixAnnotated.cs" && d.TargetPath == "src/MatrixTarget.cs");
        Assert.Contains(reverse, d => d.SourcePath == "src/MatrixRuntimeCaller.cs" && d.TargetPath == "src/MatrixTarget.cs");

        // Callers: call-graph contract excludes metadata kinds via
        // `CallGraphReferenceKindsSql`, so only the runtime instantiate site
        // is reported. The `[MatrixTarget(...)]` row on MatrixAnnotated must
        // NOT appear as a caller.
        // callers: call-graph 契約は `CallGraphReferenceKindsSql` で metadata を
        // 除外するため、runtime の instantiate サイトのみが返る。
        // MatrixAnnotated の `[MatrixTarget(...)]` は caller に出てはならない。
        var callers = _reader.GetCallers("MatrixTarget", lang: "csharp", exact: true, pathPatterns: ["Matrix"]);
        Assert.Contains(callers, c => c.CallerName == "Do");
        Assert.DoesNotContain(callers, c => c.CallerName == "MatrixAnnotated");

        // `references --kind attribute` is the documented reconciliation path:
        // it surfaces the `[MatrixTarget(...)]` metadata-only edge that the
        // call-graph view intentionally drops.
        // 差分を埋める `references --kind attribute` は、call-graph 側が落とした
        // `[MatrixTarget(...)]` metadata エッジを返す。
        var attrRefs = _reader.SearchReferences("MatrixTarget", limit: 10, lang: "csharp", referenceKind: "attribute", exact: true, pathPatterns: ["Matrix"]);
        Assert.Contains(attrRefs, r => r.Path == "src/MatrixAnnotated.cs" && r.ReferenceKind == "attribute");
        Assert.DoesNotContain(attrRefs, r => r.Path == "src/MatrixRuntimeCaller.cs");
    }

    [Fact]
    public void ReferenceKindMatrix_CallersIncludesReactHookConsumption()
    {
        InsertIndexedFile("src/hooks.tsx", "typescript",
            """
            export const useSharedValue = () => {
              return 1;
            };
            """);
        InsertIndexedFile("src/Widget.tsx", "typescript",
            """
            import { useSharedValue } from "./hooks";

            export function Widget() {
              return useSharedValue();
            }
            """);

        var callers = _reader.GetCallers("useSharedValue", lang: "typescript", exact: true);

        Assert.Contains(callers, caller =>
            caller.CallerName == "Widget"
            && caller.ReferenceKind == "consumes_hook");
    }

    [Fact]
    public void ReferenceKindMatrix_CallersIncludesCSharpLambdaCaptures()
    {
        InsertIndexedFile("src/CaptureDemo.cs", "csharp",
            """
            public class CaptureDemo
            {
                public void Run()
                {
                    var seed = 1;
                    System.Func<int> next = () => seed + 1;
                }
            }
            """);

        var callers = _reader.GetCallers("seed", lang: "csharp", exact: true);

        Assert.Contains(callers, caller =>
            caller.CallerName == "Run"
            && caller.ReferenceKind == "capture");
    }


























    [Fact]
    public void ResolveCSharpMetadataTargets_SeedsExtractorOwnedMetadataTargets_Issue3524()
    {
        InsertIndexedFile("src/A/DirectAttribute.cs", "csharp",
            """
            namespace A
            {
                public class DirectAttribute : System.Attribute
                {
                }
            }
            """);
        InsertIndexedFile("src/A/ChildAttribute.cs", "csharp",
            """
            namespace A
            {
                public class ChildAttribute : DirectAttribute
                {
                }
            }
            """);
        InsertIndexedFile("src/A/Svc.cs", "csharp",
            """
            namespace A
            {
                [Child]
                public class Svc
                {
                }
            }
            """);

        _writer.ResolveCSharpMetadataTargets();
        _writer.MarkMetadataTargetReady("csharp");
        var resolverReader = new DbReader(_db.Connection);

        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT s.is_metadata_target, s.metadata_target_source
                FROM symbols s
                JOIN files f ON f.id = s.file_id
                WHERE f.path = 'src/A/DirectAttribute.cs' AND s.kind = 'class' AND s.name = 'DirectAttribute'";
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal(SymbolRecord.MetadataTargetSourceExtractor, reader.GetString(1));
        }

        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT s.is_metadata_target, s.metadata_target_source
                FROM symbols s
                JOIN files f ON f.id = s.file_id
                WHERE f.path = 'src/A/ChildAttribute.cs' AND s.kind = 'class' AND s.name = 'ChildAttribute'";
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal(SymbolRecord.MetadataTargetSourceResolver, reader.GetString(1));
        }

        var dependencies = resolverReader.GetFileDependencies(limit: 10, lang: "csharp");
        Assert.Contains(dependencies, d => d.SourcePath == "src/A/Svc.cs" && d.TargetPath == "src/A/ChildAttribute.cs");
    }

    [Fact]
    public void ResolveCSharpMetadataTargets_DoesNotMistakeGenericConstraintForBaseList()
    {
        // issue #435 codex review iter 1: `class Foo<T> where T : Attribute {}` has no
        // base list — only a generic constraint. Before the fix, FindBaseListColon
        // returned the first top-level `:` even when it was the `where` clause's
        // `T : Attribute`, causing ParseCSharpBaseIdentifiers to read `Attribute` as a
        // base and wrongly promote the class to `is_metadata_target = 1`.
        // issue #435 codex review iter 1: `class Foo<T> where T : Attribute {}` は base
        // list を持たず、generic constraint だけ。修正前は FindBaseListColon が
        // `where T :` の `:` を採用し、`Attribute` を基底と解釈して target 化していた。
        InsertIndexedFile("src/NotAnAttributeClass.cs", "csharp",
            """
            using System;

            public class NotAnAttributeClass<T> where T : Attribute
            {
            }
            """);

        _writer.ResolveCSharpMetadataTargets();

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = @"
            SELECT s.is_metadata_target
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE f.path = 'src/NotAnAttributeClass.cs' AND s.kind = 'class' AND s.name = 'NotAnAttributeClass'";
        var flag = cmd.ExecuteScalar();
        Assert.Equal(0L, Convert.ToInt64(flag));
    }

    [Fact]
    public void ResolveCSharpMetadataTargets_RespectsBaseListBeforeGenericConstraint()
    {
        // Companion to the `where`-only test: a class with both a base list and a
        // generic constraint (`: BaseAttr where T : IDisposable`) must still pick up
        // the base list and propagate metadata-target status through the fixed-point
        // iteration, not stop at the `where` clause before reading the actual base.
        // `where` only テストの対、base list と generic constraint を両方持つ宣言では
        // base list を正しく拾って transitive 伝播させる必要がある。
        InsertIndexedFile("src/BaseAttr.cs", "csharp",
            """
            using System;

            public abstract class BaseAttr : Attribute
            {
            }
            """);
        InsertIndexedFile("src/GenericAttr.cs", "csharp",
            """
            using System;

            public sealed class GenericAttr<T> : BaseAttr where T : IDisposable
            {
            }
            """);

        _writer.ResolveCSharpMetadataTargets();

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = @"
            SELECT s.is_metadata_target
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE f.path = 'src/GenericAttr.cs' AND s.kind = 'class' AND s.name = 'GenericAttr'";
        var flag = cmd.ExecuteScalar();
        Assert.Equal(1L, Convert.ToInt64(flag));
    }









    [Fact]
    public void GetFileDependencyHints_SuppressesCSharpAttributeMetadataBypassOnAmbiguousTarget()
    {
        // issue #293 review: when two classes share the `MyAuditAttribute` name
        // *within the active impact scope*, a `[MyAudit]` reference row only
        // carries the short name and cannot be uniquely attributed to either
        // target. In that ambiguous case the `impact` metadata evidence bypass
        // must be skipped so rename / removal blast radius is not over-reported.
        // issue #293 レビュー指摘: impact スコープ内で同名の `MyAuditAttribute`
        // クラスが複数存在するとき、`[MyAudit]` 参照行は短縮名しか持たず、
        // どちらの target にも一意に紐付けられない。この曖昧なケースでは
        // `impact` の metadata evidence bypass を行わず、rename / 削除の影響
        //範囲を過大報告しないようにする。
        InsertIndexedFile("src/A/Inner1/MyAuditAttribute.cs", "csharp",
            """
            namespace A.Inner1;

            public sealed class MyAuditAttribute : System.Attribute
            {
            }
            """);
        InsertIndexedFile("src/A/Inner2/MyAuditAttribute.cs", "csharp",
            """
            namespace A.Inner2;

            public sealed class MyAuditAttribute : System.Attribute
            {
            }
            """);
        // Pure attribute consumer in src/A/ — no structured type evidence exists for
        // `MyAuditAttribute` other than the `[MyAudit]` use site itself.
        // src/A/ に純粋な attribute consumer — `MyAuditAttribute` に対する構造化された
        // 型証拠は `[MyAudit]` use site 以外には無い。
        InsertIndexedFile("src/A/Svc.cs", "csharp",
            """
            namespace A;

            [MyAudit]
            public class Svc
            {
            }
            """);

        // Both ambiguous definitions are within the `src/A/` scope; without the
        // ambiguity guard, the metadata bypass would fabricate a heuristic edge
        // even though the `[MyAudit]` target is qualifier-ambiguous.
        // src/A/ スコープ内に曖昧な定義が 2 件ある。ambiguity guard が無ければ、
        // `[MyAudit]` の target が qualifier 曖昧でも metadata bypass が heuristic
        // エッジを作ってしまう。
        var result = _reader.AnalyzeImpact(
            "MyAuditAttribute",
            maxDepth: 3,
            limit: 20,
            lang: "csharp",
            pathPatterns: new[] { "src/A/" });

        Assert.DoesNotContain(result.FileImpacts, f => f.SourcePath == "src/A/Svc.cs");
    }

    [Fact]
    public void GetFileDependencyHints_CSharpAttributeMetadataBypassAppliesWhenTargetUnambiguous()
    {
        // issue #293 review: the ambiguity guard must only fire when genuinely
        // ambiguous. With a single class-like `MyAuditAttribute` definition the
        // metadata bypass should still surface the `[MyAudit]` consumer as a
        // file-level hint, preserving the legitimate pure-attribute consumer case.
        // issue #293 レビュー指摘: ambiguity guard は本当に曖昧なときだけ発動すべき。
        // `MyAuditAttribute` の class 定義が 1 件しかない場合は従来通り metadata
        // bypass で `[MyAudit]` consumer を file-level hint として出し、純粋な
        // attribute consumer の正当な検出を保つ。
        InsertIndexedFile("src/MyAuditAttribute.cs", "csharp",
            """
            public sealed class MyAuditAttribute : System.Attribute
            {
            }
            """);
        InsertIndexedFile("src/Svc.cs", "csharp",
            """
            [MyAudit]
            public class Svc
            {
            }
            """);

        var result = _reader.AnalyzeImpact("MyAuditAttribute", maxDepth: 3, limit: 20, lang: "csharp");

        Assert.Contains(result.FileImpacts, f => f.SourcePath == "src/Svc.cs" && f.TargetPath == "src/MyAuditAttribute.cs");
    }

    [Fact]
    public void GetFileDependencyHints_InvokeReferenceAnchorsFileImpactWithoutStructuredTypeEvidence()
    {
        // issue #1881: a `call` / `instantiate` reference to the resolved target name in
        // the source file is a strictly stronger anchor than the metadata-bypass
        // widening for the file-level `impact` heuristic, and was previously ignored
        // when no structured type evidence (signature / return-type token) existed in
        // the same file. The reordered candidate loop now consults call/instantiate
        // evidence before falling through to the metadata bypass, so a file that
        // genuinely instantiates `MyAuditAttribute` surfaces in `impact MyAuditAttribute`
        // even when the call site's container_name is missing — without depending on
        // the looser ambiguity-guarded attribute / annotation widening.
        // issue #1881: ソースファイル内の `call` / `instantiate` 参照は signature /
        // return 型トークンに比べてより強い anchor だが、従来は同ファイルに structured
        // 型エビデンスが無いと file-level `impact` heuristic で無視されていた。
        // 並び替えた candidate loop は metadata bypass にフォールスルーする前に
        // call/instantiate エビデンスを評価するため、container_name が欠落した
        // call site でも `MyAuditAttribute` を実際に instantiate しているファイルが
        // `impact MyAuditAttribute` の結果に現れる — 曖昧性ガード付きの attribute /
        // annotation 広げに依存せずに済む。
        InsertIndexedFile("src/MyAuditAttribute.java", "java",
            """
            package src;

            public class MyAuditAttribute {
            }
            """);
        // Pure consumer with no structured type evidence (no method signature mentioning
        // `MyAuditAttribute`, no return-type token). The synthetic `instantiate`
        // reference below carries a NULL container so the BFS caller predicate
        // (`r.container_name IS NOT NULL OR (f.lang = 'csharp' AND r.container_name IS NULL)`)
        // misses it for Java — forcing the impact heuristic path to evaluate the
        // candidate. Without the issue #1881 fix, the heuristic would drop the edge
        // for lack of structured-type evidence; with the fix, the call-graph
        // reference itself anchors the file as a dependent.
        // structured 型エビデンスが無い純粋な consumer（`MyAuditAttribute` を含む
        // method signature も return 型も無い）。下で挿入する `instantiate` 参照は
        // container を NULL にしてあり、Java では BFS の caller 述語
        // (`r.container_name IS NOT NULL OR (f.lang = 'csharp' AND r.container_name IS NULL)`)
        // に拾われない。そのため impact heuristic 経路で candidate が評価される。
        // issue #1881 修正前は structured 型エビデンスが無く edge が落ちていたが、
        // 修正後は call-graph 参照自体が file を依存元として anchor する。
        var svcFileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/Svc.java",
            Lang = "java",
            Size = 32,
            Lines = 5,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertReferences(new[]
        {
            new ReferenceRecord
            {
                FileId = svcFileId,
                SymbolName = "MyAuditAttribute",
                ReferenceKind = "instantiate",
                Line = 4,
                Column = 9,
                Context = "        new MyAuditAttribute();",
                ContainerKind = null,
                ContainerName = null,
            },
        });

        var result = _reader.AnalyzeImpact("MyAuditAttribute", maxDepth: 3, limit: 20, lang: "java");

        Assert.Contains(result.FileImpacts, f =>
            f.SourcePath == "src/Svc.java" && f.TargetPath == "src/MyAuditAttribute.java");
    }

    [Fact]
    public void SourceFileHasAnchorReference_SubscribeAnchorsFileImpactWithoutStructuredTypeEvidence()
    {
        // issue #2132: C# event subscriptions are compile-time dependencies just like
        // calls/instantiations. The file-level impact evidence guard must treat the
        // `subscribe` row itself as an anchor, even when no method signature or
        // return-type token mentions the event name and no metadata bypass applies.
        // issue #2132: C# の event subscription は call / instantiate と同じく
        // compile-time dependency なので、method signature / return 型に event 名が
        // 出ず metadata bypass も使えない場合でも、`subscribe` 行そのものを
        // file-level impact の anchor として扱う。
        var svcFileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/Svc.cs",
            Lang = "csharp",
            Size = 64,
            Lines = 6,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertReferences(new[]
        {
            new ReferenceRecord
            {
                FileId = svcFileId,
                SymbolName = "Changed",
                ReferenceKind = "subscribe",
                Line = 4,
                Column = 16,
                Context = "source.Changed += OnChanged;",
                ContainerKind = null,
                ContainerName = null,
            },
        });

        var method = typeof(DbReader).GetMethod("SourceFileHasAnchorReferenceTo", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var anchored = (bool)method.Invoke(_reader, new object[] { svcFileId, "Changed" })!;

        Assert.True(anchored);
    }


    [Fact]
    public void GetFileDependencyHints_MetadataBypassAmbiguityGuard_CountsSameFileDuplicateDefinitions()
    {
        // issue #293 follow-up: the `impact` metadata bypass ambiguity guard must
        // count class-like definitions at symbol-identity level rather than at path
        // level. A single .cs file with two same-named `MyAuditAttribute` class
        // declarations under different namespaces is still ambiguous — metadata
        // reference rows only keep the short name `MyAudit` and cannot resolve
        // between `A.MyAuditAttribute` and `B.MyAuditAttribute`. Previously the
        // guard counted `SELECT DISTINCT f.path` so both definitions collapsed to
        // 1 and the bypass falsely fired, mis-attributing `[MyAudit]` consumers to
        // the impact of a specific target when the true resolution is unknown.
        // issue #293 補足: `impact` の metadata bypass 曖昧性ガードは、path 単位
        // ではなく symbol identity 単位で class-like 定義を数える必要がある。1 つの
        // .cs ファイル内に別名前空間で `MyAuditAttribute` が 2 つ定義されていても、
        // metadata 参照は短縮名 `MyAudit` しか持たず `A.MyAuditAttribute` と
        // `B.MyAuditAttribute` を区別できないため依然として曖昧。従来は
        // `SELECT DISTINCT f.path` で数えていたため両定義が 1 に潰れ、bypass が
        // 誤って発動し `[MyAudit]` consumer を特定 target の影響範囲へ誤帰属させていた。
        InsertIndexedFile("src/MyAuditAttribute.cs", "csharp",
            """
            using System;

            namespace A
            {
                [AttributeUsage(AttributeTargets.Class)]
                public sealed class MyAuditAttribute : Attribute
                {
                }
            }

            namespace B
            {
                [AttributeUsage(AttributeTargets.Class)]
                public sealed class MyAuditAttribute : Attribute
                {
                }
            }
            """);
        InsertIndexedFile("src/Svc.cs", "csharp",
            """
            [MyAudit]
            public class Svc
            {
            }
            """);

        var result = _reader.AnalyzeImpact("MyAuditAttribute", maxDepth: 3, limit: 20, lang: "csharp");

        // Two same-named class-like definitions in one file still make the target
        // ambiguous, so the `[MyAudit]` consumer must not surface as a file-level
        // impact hint — the metadata evidence bypass should fall through to the
        // normal structured-evidence check, which `[MyAudit]`-only consumers fail.
        // 同じファイル内の 2 つの同名 class-like 定義でも target は曖昧なので、
        // `[MyAudit]` consumer は file-level impact hint に現れてはいけない。
        // metadata evidence bypass は通常の structured-evidence 判定へフォール
        // スルーし、pure `[MyAudit]` consumer はそこで落ちる。
        Assert.DoesNotContain(result.FileImpacts, f => f.SourcePath == "src/Svc.cs" && f.TargetPath == "src/MyAuditAttribute.cs");
    }

    [Fact]
    public void GetFileDependencyHints_CSharpAttributeSuffixAlias_DoesNotLeakToSameFileSiblings()
    {
        // issue #293 round-12 follow-up: the C# `Attribute` suffix alias used by
        // ResolveImpactFallbackNames must only be applied to the resolved
        // definition's own name. If it were applied to every same-file fallback
        // name (e.g. a nested `BarAttribute` inside the file that defines
        // `FooAttribute`), `impact FooAttribute` would falsely claim `[Bar]` use
        // sites as its own blast radius.
        // issue #293 round-12 追加: ResolveImpactFallbackNames の C# `Attribute`
        // suffix 別名は、解決済み定義自身の名前にだけ適用すべき。same-file
        // fallback 名全体（例: `FooAttribute` と同一ファイルに nested で存在する
        // `BarAttribute`）にまで strip を適用すると、`impact FooAttribute` が
        // `[Bar]` 利用を自身の影響範囲として誤報告してしまう。
        InsertIndexedFile("src/FooAttribute.cs", "csharp",
            """
            public sealed class FooAttribute : System.Attribute
            {
                public sealed class BarAttribute : System.Attribute
                {
                }
            }
            """);
        // A separate file uses `[Bar]` — that must NOT show up in
        // `impact FooAttribute` because it references `BarAttribute`, not
        // `FooAttribute`.
        // 別ファイルで `[Bar]` を使う — これは `BarAttribute` の参照であり、
        // `FooAttribute` の `impact` には出てはならない。
        InsertIndexedFile("src/UseBar.cs", "csharp",
            """
            [Bar]
            public class UseBar
            {
            }
            """);

        var result = _reader.AnalyzeImpact("FooAttribute", maxDepth: 3, limit: 20, lang: "csharp");

        Assert.DoesNotContain(result.FileImpacts, f => f.SourcePath == "src/UseBar.cs");
    }

    [Fact]
    public void GetFileDependencyHints_MetadataBypassAmbiguityGuard_RespectsLangScope()
    {
        // issue #293 round-11 follow-up: the ambiguity guard must honor the active
        // `--lang` scope. A same-named class in an unrelated language must not
        // suppress the C# metadata bypass because attribute reference rows are
        // already language-qualified through the graph-supported `f.lang = 'csharp'`
        // join on the reference side.
        // issue #293 round-11 追加: ambiguity guard は active な `--lang` スコープを
        // 尊重すべき。別言語に同名クラスが存在しても C# の metadata bypass を
        // 潰してはならない — 参照側の join で既に言語修飾されているため、曖昧性は
        // 言語スコープ内でのみ判定する。
        InsertIndexedFile("src/MyAuditAttribute.cs", "csharp",
            """
            public sealed class MyAuditAttribute : System.Attribute
            {
            }
            """);
        InsertIndexedFile("src/Svc.cs", "csharp",
            """
            [MyAudit]
            public class Svc
            {
            }
            """);
        // Unrelated Java class / annotation sharing the unqualified name — must not
        // affect the C#-only impact query.
        // 無関係な Java 側の同名クラス / アノテーション — C# 限定の impact クエリに
        // 影響してはならない。
        InsertIndexedFile("src/java/MyAuditAttribute.java", "java",
            """
            package pkg;

            public @interface MyAuditAttribute {
            }
            """);

        var result = _reader.AnalyzeImpact("MyAuditAttribute", maxDepth: 3, limit: 20, lang: "csharp");

        Assert.Contains(result.FileImpacts, f => f.SourcePath == "src/Svc.cs" && f.TargetPath == "src/MyAuditAttribute.cs");
    }

    [Fact]
    public void GetFileDependencyHints_MetadataBypassAmbiguityGuard_RespectsPathScope()
    {
        // issue #293 round-11 follow-up: ambiguity guard must honor `--path`
        // scoping. A same-named class outside the requested path subtree should
        // not suppress the bypass inside that subtree.
        // issue #293 round-11 追加: ambiguity guard は `--path` スコープを尊重すべき。
        // 要求した path サブツリー外にある同名クラスが、サブツリー内の bypass を
        // 潰してはならない。
        InsertIndexedFile("src/A/MyAuditAttribute.cs", "csharp",
            """
            namespace A;

            public sealed class MyAuditAttribute : System.Attribute
            {
            }
            """);
        InsertIndexedFile("src/A/Svc.cs", "csharp",
            """
            namespace A;

            [MyAudit]
            public class Svc
            {
            }
            """);
        // Out-of-scope same-named definition in src/B/ — must not affect the
        // src/A/-scoped impact query.
        // スコープ外 src/B/ の同名定義 — src/A/ 限定の impact クエリに影響してはならない。
        InsertIndexedFile("src/B/MyAuditAttribute.cs", "csharp",
            """
            namespace B;

            public sealed class MyAuditAttribute : System.Attribute
            {
            }
            """);

        var result = _reader.AnalyzeImpact(
            "MyAuditAttribute",
            maxDepth: 3,
            limit: 20,
            lang: "csharp",
            pathPatterns: new[] { "src/A/" });

        Assert.Contains(result.FileImpacts, f => f.SourcePath == "src/A/Svc.cs" && f.TargetPath == "src/A/MyAuditAttribute.cs");
    }

    [Fact]
    public void GetFileDependencyHints_MetadataBypassAmbiguityGuard_CliPathPatternEscaping_SuppressesWhenInScopeIsAmbiguous()
    {
        // issue #293 round-15 follow-up: path / exclude-path parameters must be
        // wrapped with `%...%` and routed through EscapeLikeQuery so the LIKE
        // predicate accepts CLI-style prefixes like `src/A/`. Without the wrap
        // the ambiguity count would underflow to 1 (unambiguous), and the
        // metadata bypass would falsely fire even though two MyAuditAttribute
        // classes exist side-by-side in the requested subtree.
        // issue #293 round-15 補足: path / exclude-path のバインドは他の reader
        // 経路と同じ `%...%` + EscapeLikeQuery に揃える必要がある。生値で渡すと
        // `src/A/` のような CLI 形では LIKE が一致せず、要求したサブツリーに
        // 同名 MyAuditAttribute が 2 件存在しても曖昧性カウントが 1 に落ち、
        // 本来抑止すべき metadata bypass が誤発火してしまう。
        InsertIndexedFile("src/A/Inner1/MyAuditAttribute.cs", "csharp",
            """
            namespace A.Inner1;

            public sealed class MyAuditAttribute : System.Attribute
            {
            }
            """);
        InsertIndexedFile("src/A/Inner2/MyAuditAttribute.cs", "csharp",
            """
            namespace A.Inner2;

            public sealed class MyAuditAttribute : System.Attribute
            {
            }
            """);
        InsertIndexedFile("src/A/Svc.cs", "csharp",
            """
            namespace A;

            [MyAudit]
            public class Svc
            {
            }
            """);

        var result = _reader.AnalyzeImpact(
            "MyAuditAttribute",
            maxDepth: 3,
            limit: 20,
            lang: "csharp",
            pathPatterns: new[] { "src/A/" });

        Assert.DoesNotContain(result.FileImpacts, f =>
            f.SourcePath == "src/A/Svc.cs" &&
            (f.TargetPath == "src/A/Inner1/MyAuditAttribute.cs" || f.TargetPath == "src/A/Inner2/MyAuditAttribute.cs"));
    }









    [Fact]
    public void GetFileDependencyHints_MetadataBypassAmbiguityGuard_RespectsExcludeTests()
    {
        // issue #293 round-11 follow-up: ambiguity guard must honor
        // `--exclude-tests`. A same-named class only present in tests should not
        // suppress the bypass when the caller has already excluded tests from the
        // impact scope.
        // issue #293 round-11 追加: ambiguity guard は `--exclude-tests` を尊重すべき。
        // test 配下にしか存在しない同名定義が、test を除外した impact クエリの
        // bypass を潰してはならない。
        InsertIndexedFile("src/MyAuditAttribute.cs", "csharp",
            """
            public sealed class MyAuditAttribute : System.Attribute
            {
            }
            """);
        InsertIndexedFile("src/Svc.cs", "csharp",
            """
            [MyAudit]
            public class Svc
            {
            }
            """);
        // Test-only same-named definition — must be filtered out when the caller
        // passes excludeTests=true so the bypass stays active in the source scope.
        // test 配下にしかない同名定義 — excludeTests=true のときはスコープ外になり、
        // source 側の bypass を維持すべき。
        InsertIndexedFile("tests/CodeIndex.Tests/MyAuditAttributeTests.cs", "csharp",
            """
            public sealed class MyAuditAttribute : System.Attribute
            {
            }
            """);

        var result = _reader.AnalyzeImpact(
            "MyAuditAttribute",
            maxDepth: 3,
            limit: 20,
            lang: "csharp",
            excludeTests: true);

        Assert.Contains(result.FileImpacts, f => f.SourcePath == "src/Svc.cs" && f.TargetPath == "src/MyAuditAttribute.cs");
    }

    [Fact]
    public void GetGroupedSymbolHotspots_CollapsesDuplicateNamesWithoutBareJoinInflation()
    {
        InsertIndexedFile("src/Alpha.cs", "csharp",
            """
            public class Alpha
            {
                private void SharedHelper() { }

                public void Use()
                {
                    SharedHelper();
                    SharedHelper();
                }
            }
            """);
        InsertIndexedFile("src/Beta.cs", "csharp",
            """
            public class Beta
            {
                private void SharedHelper() { }

                public void Use()
                {
                    SharedHelper();
                }
            }
            """);

        var grouped = _reader.GetGroupedSymbolHotspots(
            limit: 10,
            kind: "function",
            lang: "csharp",
            pathPatterns: ["src/"],
            excludePathPatterns: null,
            excludeTests: false);

        var shared = Assert.Single(grouped.Where(result => result.Symbol.Name == "SharedHelper"));
        Assert.Equal(3, shared.ReferenceCount);
        Assert.Equal(2, shared.DefinitionSites);
        Assert.Equal(2, shared.Paths.Count);
        Assert.Contains("src/Alpha.cs", shared.Paths);
        Assert.Contains("src/Beta.cs", shared.Paths);
    }

    [Fact]
    public void GraphQueries_ConstructorReferencesAreInstantiateOnly()
    {
        InsertIndexedFile("src/constructor_kind_target.cs", "csharp",
            """
            namespace N
            {
                public class Target
                {
                    public Target() { }
                }
            }
            """);
        InsertIndexedFile("src/constructor_kind_caller.cs", "csharp",
            """
            public class Caller
            {
                public void Run()
                {
                    var target = new N.Target();
                    var other = new global::N.Target();
                }
            }
            """);

        var instantiateRefs = _reader.SearchReferences("Target", lang: "csharp", referenceKind: "instantiate", exact: true, pathPatterns: ["constructor_kind"]).ToList();
        Assert.Equal(2, instantiateRefs.Count);
        Assert.All(instantiateRefs, reference => Assert.Equal("instantiate", reference.ReferenceKind));

        Assert.Empty(_reader.SearchReferences("Target", lang: "csharp", referenceKind: "call", exact: true, pathPatterns: ["constructor_kind"]));
        Assert.Equal(0, _reader.CountSearchReferences("Target", lang: "csharp", referenceKind: "call", exact: true, pathPatterns: ["constructor_kind"]));
        Assert.Equal(2, _reader.CountSearchReferences("Target", lang: "csharp", referenceKind: "instantiate", exact: true, pathPatterns: ["constructor_kind"]));

        Assert.Empty(_reader.GetCallees("Run", lang: "csharp", referenceKind: "call", exact: true, pathPatterns: ["constructor_kind"]));

        var instantiateCallee = Assert.Single(_reader.GetCallees("Run", lang: "csharp", referenceKind: "instantiate", exact: true, pathPatterns: ["constructor_kind"]));
        Assert.Equal("instantiate", instantiateCallee.ReferenceKind);
        Assert.Equal(2, instantiateCallee.ReferenceCount);

        Assert.Empty(_reader.GetCallers("Target", lang: "csharp", referenceKind: "call", exact: true, pathPatterns: ["constructor_kind"]));
        var instantiateCaller = Assert.Single(_reader.GetCallers("Target", lang: "csharp", referenceKind: "instantiate", exact: true, pathPatterns: ["constructor_kind"]));
        Assert.Equal(2, instantiateCaller.ReferenceCount);
    }



    [Fact]
    public void CSharpActiveScopeResolvers_CacheRepeatedFileLineResults_Issue2074()
    {
        InsertIndexedFile("src/ScopeCache.cs", "csharp",
            """
            using System;
            using static Probe.Color;

            namespace Probe;

            public enum Color
            {
                Red,
                Blue
            }

            public class Demo
            {
                object? Match(object value)
                {
                    return value is Red ? value : null;
                }
            }
            """);

        const string path = "src/ScopeCache.cs";
        const int lineNumber = 15;

        var namespacesFirst = InvokePrivateCSharpScopeResolver("GetActiveCSharpTypeNamespaces", path, lineNumber);
        var namespacesSecond = InvokePrivateCSharpScopeResolver("GetActiveCSharpTypeNamespaces", path, lineNumber);
        var containingTypesFirst = InvokePrivateCSharpScopeResolver("GetActiveCSharpContainingTypeScopes", path, lineNumber);
        var containingTypesSecond = InvokePrivateCSharpScopeResolver("GetActiveCSharpContainingTypeScopes", path, lineNumber);
        var staticTargetsFirst = InvokePrivateCSharpScopeResolver("GetActiveCSharpUsingStaticTargets", path, lineNumber);
        var staticTargetsSecond = InvokePrivateCSharpScopeResolver("GetActiveCSharpUsingStaticTargets", path, lineNumber);

        Assert.Same(namespacesFirst, namespacesSecond);
        Assert.Same(containingTypesFirst, containingTypesSecond);
        Assert.Same(staticTargetsFirst, staticTargetsSecond);
    }

    private object InvokePrivateCSharpScopeResolver(string methodName, string path, int lineNumber)
    {
        var method = typeof(DbReader).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method.Invoke(_reader, new object[] { path, lineNumber })!;
    }


    [Fact]
    public void GraphQueries_DefaultGraphQueriesKeepSubscribeRowsVisible()
    {
        InsertIndexedFile("src/event_publisher.cs", "csharp",
            """
            using System;

            public class Publisher
            {
                public event EventHandler? Changed;
            }
            """);
        InsertIndexedFile("src/event_subscriber.cs", "csharp",
            """
            using System;

            public class Subscriber
            {
                public void Hook(Publisher publisher)
                {
                    publisher.Changed += OnChanged;
                }

                private void OnChanged(object? sender, EventArgs e) { }
            }
            """);

        var reference = Assert.Single(_reader.SearchReferences("Changed", lang: "csharp", exact: true, pathPatterns: ["event_"]));
        Assert.Equal("subscribe", reference.ReferenceKind);
        Assert.Equal("Hook", reference.ContainerName);
        Assert.Equal(1, _reader.CountSearchReferences("Changed", lang: "csharp", exact: true, pathPatterns: ["event_"]));

        var caller = Assert.Single(_reader.GetCallers("Changed", lang: "csharp", exact: true, pathPatterns: ["event_"]));
        Assert.Equal("Hook", caller.CallerName);
        Assert.Equal("Changed", caller.CalleeName);
        Assert.Equal(1, caller.ReferenceCount);
        Assert.Equal(1, _reader.CountCallers("Changed", lang: "csharp", exact: true, pathPatterns: ["event_"]));

        var callee = Assert.Single(_reader.GetCallees("Hook", lang: "csharp", exact: true, pathPatterns: ["event_"]));
        Assert.Equal("Hook", callee.CallerName);
        Assert.Equal("Changed", callee.CalleeName);
        Assert.Equal("event", callee.ReferenceKind);
        Assert.Equal(1, callee.ReferenceCount);
        Assert.Equal(1, _reader.CountCallees("Hook", lang: "csharp", exact: true, pathPatterns: ["event_"]));

        var analysis = _reader.AnalyzeSymbol("Changed", limit: 5, lang: "csharp", pathPatterns: ["event_"], exact: true);
        var bundledReference = Assert.Single(analysis.References);
        Assert.Equal("subscribe", bundledReference.ReferenceKind);
        Assert.Equal("Hook", bundledReference.ContainerName);
        var bundledCaller = Assert.Single(analysis.Callers);
        Assert.Equal("Hook", bundledCaller.CallerName);
        Assert.Empty(analysis.Callees);

        var callerAnalysis = _reader.AnalyzeSymbol("Hook", limit: 5, lang: "csharp", pathPatterns: ["event_"], exact: true);
        var bundledCallee = Assert.Single(callerAnalysis.Callees);
        Assert.Equal("Hook", bundledCallee.CallerName);
        Assert.Equal("Changed", bundledCallee.CalleeName);
        Assert.Equal("event", bundledCallee.ReferenceKind);
    }

    [Fact]
    public void GraphQueries_CountTotalsPreserveScssAliasScope()
    {
        var cssFileId = _writer.UpsertFile(new FileRecord
        {
            Path = "styles/theme.scss",
            Lang = "css",
            Size = 128,
            Lines = 8,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertReferences([
            new ReferenceRecord
            {
                FileId = cssFileId,
                SymbolName = "primary",
                ReferenceKind = "call",
                Line = 4,
                Column = 10,
                Context = "color: $primary;",
                ContainerKind = "rule",
                ContainerName = ".button",
            },
            new ReferenceRecord
            {
                FileId = cssFileId,
                SymbolName = "radius",
                ReferenceKind = "call",
                Line = 6,
                Column = 12,
                Context = "@include rounded(4px);",
                ContainerKind = "function",
                ContainerName = "rounded",
            },
        ]);

        var jsFileId = _writer.UpsertFile(new FileRecord
        {
            Path = "scripts/theme.js",
            Lang = "javascript",
            Size = 128,
            Lines = 8,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertReferences([
            new ReferenceRecord
            {
                FileId = jsFileId,
                SymbolName = "primary",
                ReferenceKind = "call",
                Line = 4,
                Column = 10,
                Context = "color: $primary;",
                ContainerKind = "rule",
                ContainerName = ".button",
            },
            new ReferenceRecord
            {
                FileId = jsFileId,
                SymbolName = "radius",
                ReferenceKind = "call",
                Line = 6,
                Column = 12,
                Context = "@include rounded(4px);",
                ContainerKind = "function",
                ContainerName = "rounded",
            },
        ]);

        Assert.Equal(new QueryCountResult(1, 1), _reader.CountCallersTotal("$primary", exact: true));
        Assert.Equal(new QueryCountResult(1, 1), _reader.CountCalleesTotal("$rounded", exact: true));
    }

    [Fact]
    public void GetCallers_ExposesDistinctReferenceKindsForMixedGroups()
    {
        // Regression for #501: when a single container reaches the same callee via
        // multiple reference kinds (e.g. `call` + `subscribe`), the grouped caller row
        // must still surface the distinct kinds via `reference_kinds` /
        // `has_mixed_reference_kinds` so AI clients do not trust a misleading single
        // summary label. `callees` rows split by kind, so their metadata stays
        // single-kind even when the underlying container is mixed.
        // #501 リグレッション: 同じコンテナが同一 callee に対して複数の reference kind
        // (`call` + `subscribe` など) を持つとき、グループ化された caller 行でも
        // `reference_kinds` / `has_mixed_reference_kinds` で distinct kind を返し、
        // 要約ラベル 1 つに騙されないようにすること。`callees` 側は元々 kind ごとに
        // 行が分かれるため、基盤が混在でも各行は単一 kind のまま。
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/mixed_kind_caller.cs",
            Lang = "csharp",
            Size = 256,
            Lines = 12,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks([
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 12,
                Content = "public class MixedOwner { public void Setup() { Changed += Handler; Changed(); Changed(); Changed(); Changed(); Changed(); } }\n",
            }
        ]);
        _writer.InsertReferences([
            new ReferenceRecord
            {
                FileId = fileId,
                SymbolName = "Changed",
                ReferenceKind = "subscribe",
                Line = 1,
                Column = 41,
                Context = "Changed += Handler;",
                ContainerKind = "function",
                ContainerName = "Setup",
            },
            new ReferenceRecord
            {
                FileId = fileId,
                SymbolName = "Changed",
                ReferenceKind = "call",
                Line = 1,
                Column = 62,
                Context = "Changed();",
                ContainerKind = "function",
                ContainerName = "Setup",
            },
            new ReferenceRecord
            {
                FileId = fileId,
                SymbolName = "Changed",
                ReferenceKind = "call",
                Line = 1,
                Column = 73,
                Context = "Changed();",
                ContainerKind = "function",
                ContainerName = "Setup",
            },
            new ReferenceRecord
            {
                FileId = fileId,
                SymbolName = "Changed",
                ReferenceKind = "call",
                Line = 1,
                Column = 84,
                Context = "Changed();",
                ContainerKind = "function",
                ContainerName = "Setup",
            },
            new ReferenceRecord
            {
                FileId = fileId,
                SymbolName = "Changed",
                ReferenceKind = "call",
                Line = 1,
                Column = 95,
                Context = "Changed();",
                ContainerKind = "function",
                ContainerName = "Setup",
            },
            new ReferenceRecord
            {
                FileId = fileId,
                SymbolName = "Changed",
                ReferenceKind = "call",
                Line = 1,
                Column = 106,
                Context = "Changed();",
                ContainerKind = "function",
                ContainerName = "Setup",
            },
        ]);

        var caller = Assert.Single(_reader.GetCallers("Changed", lang: "csharp", exact: true, pathPatterns: ["mixed_kind_caller"]));
        Assert.Equal("Setup", caller.CallerName);
        Assert.Equal("Changed", caller.CalleeName);
        Assert.Equal(6, caller.ReferenceCount);
        Assert.True(caller.HasMixedReferenceKinds);
        Assert.Equal(new[] { "event", "invoke" }, caller.ReferenceKinds);
        Assert.Equal(5, caller.ReferenceKindCounts["call"]);
        Assert.Equal(1, caller.ReferenceKindCounts["subscribe"]);
        Assert.Equal("event", caller.ReferenceKind);

        // `callees` rows are already split per kind, so each grouped row stays
        // single-kind with `has_mixed_reference_kinds = false`.
        // `callees` 行は元から kind ごとに分かれるため、各行は single-kind のまま。
        var callees = _reader.GetCallees("Setup", lang: "csharp", exact: true, pathPatterns: ["mixed_kind_caller"])
            .OrderBy(c => c.ReferenceKind, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(2, callees.Count);
        Assert.Equal("event", callees[0].ReferenceKind);
        Assert.False(callees[0].HasMixedReferenceKinds);
        Assert.Equal(new[] { "event" }, callees[0].ReferenceKinds);
        Assert.Equal(1, callees[0].ReferenceKindCounts["subscribe"]);
        Assert.Equal("invoke", callees[1].ReferenceKind);
        Assert.False(callees[1].HasMixedReferenceKinds);
        Assert.Equal(new[] { "invoke" }, callees[1].ReferenceKinds);
        Assert.Equal(5, callees[1].ReferenceKindCounts["call"]);

        var rawCaller = Assert.Single(_reader.GetCallers("Changed", lang: "csharp", exact: true, pathPatterns: ["mixed_kind_caller"], rawKinds: true));
        Assert.Equal(new[] { "call", "subscribe" }, rawCaller.ReferenceKinds);
        Assert.Equal(5, rawCaller.ReferenceKindCounts["call"]);
        Assert.Equal(1, rawCaller.ReferenceKindCounts["subscribe"]);
        Assert.Equal("subscribe", rawCaller.ReferenceKind);
    }

    [Fact]
    public void GetCallers_RawKindsKeepsUnsubscribeVisible()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/unsubscribe_kind_caller.cs",
            Lang = "csharp",
            Size = 256,
            Lines = 12,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertReferences([
            new ReferenceRecord
            {
                FileId = fileId,
                SymbolName = "Changed",
                ReferenceKind = "unsubscribe",
                Line = 1,
                Column = 41,
                Context = "Changed -= Handler;",
                ContainerKind = "function",
                ContainerName = "Cleanup",
            },
            new ReferenceRecord
            {
                FileId = fileId,
                SymbolName = "Changed",
                ReferenceKind = "call",
                Line = 1,
                Column = 62,
                Context = "Changed();",
                ContainerKind = "function",
                ContainerName = "Cleanup",
            },
        ]);

        var logicalCaller = Assert.Single(_reader.GetCallers("Changed", lang: "csharp", exact: true, pathPatterns: ["unsubscribe_kind_caller"]));
        Assert.Equal(new[] { "event", "invoke" }, logicalCaller.ReferenceKinds);
        Assert.Equal("event", logicalCaller.ReferenceKind);

        var rawCaller = Assert.Single(_reader.GetCallers("Changed", lang: "csharp", exact: true, pathPatterns: ["unsubscribe_kind_caller"], rawKinds: true));
        Assert.Equal(new[] { "call", "unsubscribe" }, rawCaller.ReferenceKinds);
        Assert.Equal("unsubscribe", rawCaller.ReferenceKind);
    }

    [Fact]
    public void GetTransitiveCallers_FollowsSubscribeEdges()
    {
        // Regression: impact BFS must share the call-graph contract with callers/callees,
        // so event subscriptions (`Changed += OnChanged`) also participate in the transitive
        // caller chain rather than being stripped like metadata edges.
        // リグレッション: impact BFS も callers/callees と同じ call-graph 契約を共有し、
        // イベント購読 (`Changed += OnChanged`) が transitive caller chain に含まれること。
        InsertIndexedFile("src/impact_subscribe_publisher.cs", "csharp",
            """
            using System;

            public class SubPublisher
            {
                public event EventHandler? Changed;
            }
            """);
        InsertIndexedFile("src/impact_subscribe_subscriber.cs", "csharp",
            """
            using System;

            public class SubSubscriber
            {
                public void Hook(SubPublisher publisher)
                {
                    publisher.Changed += OnChanged;
                }

                private void OnChanged(object? sender, EventArgs e) { }
            }
            """);

        var (impact, truncated, truncatedReason, _, _) = _reader.GetTransitiveCallers(
            "Changed", maxDepth: 2, limit: 10, lang: "csharp", pathPatterns: ["impact_subscribe_"]);

        Assert.False(truncated);
        Assert.Null(truncatedReason);
        var caller = Assert.Single(impact);
        Assert.Equal("Hook", caller.CallerName);
    }

    [Fact]
    public void GetTransitiveCallers_CallCycleDoesNotReAddResolvedRoot()
    {
        // Issue #1864: cycles must not inflate impact by reporting the resolved root as one of
        // its own transitive callers. Mutual recursion is still a valid call graph, but impact
        // should stop when traversal returns to the original query symbol.
        // issue #1864: サイクルで解決済み root 自身が transitive caller として再登場し、
        // impact 件数を膨らませてはいけない。相互再帰は有効な call graph だが、元の
        // query symbol に戻った時点で traversal を止める。
        InsertIndexedFile("src/impact_call_cycle.cs", "csharp",
            """
            public static class ImpactCallCycle
            {
                public static void ImpactCycleA() { ImpactCycleB(); }
                public static void ImpactCycleB() { ImpactCycleA(); }
            }
            """);

        var (impact, truncated, truncatedReason, _, _) = _reader.GetTransitiveCallers(
            "ImpactCycleA", maxDepth: 5, limit: 10, lang: "csharp", pathPatterns: ["impact_call_cycle"]);

        Assert.False(truncated);
        Assert.Null(truncatedReason);
        var caller = Assert.Single(impact);
        Assert.Equal("ImpactCycleB", caller.CallerName);
        Assert.Equal(1, caller.Depth);
    }

    [Fact]
    public void GetCallers_ReportsAndCanExcludeSelfReferences()
    {
        InsertIndexedFile("src/self_reference_query.cs", "csharp",
            """
            public static class SelfReferenceQuery
            {
                public static void SelfReferenceTarget() { SelfReferenceTarget(); }
            }
            """);

        var callers = _reader.GetCallers(
            "SelfReferenceTarget", lang: "csharp", exact: true, pathPatterns: ["self_reference_query"]);
        var caller = Assert.Single(callers);
        Assert.Equal("SelfReferenceTarget", caller.CallerName);
        Assert.True(caller.HasSelfReference);
        Assert.False(caller.HasMutualRecursion);

        Assert.Empty(_reader.GetCallers(
            "SelfReferenceTarget",
            lang: "csharp",
            exact: true,
            pathPatterns: ["self_reference_query"],
            excludeSelfReferences: true));
    }



    [Fact]
    public void GetTransitiveCallers_MetadataCycleDoesNotParticipateInBfs()
    {
        // Issue #1864: metadata-only edges are compile-time dependency edges, not runtime
        // caller edges. Even if metadata rows form a cycle, impact's symbol-level BFS must
        // ignore them so they cannot inflate caller counts or rankings.
        // issue #1864: metadata-only edge は compile-time dependency であり runtime caller
        // ではない。metadata 行がサイクルを形成しても、impact の symbol-level BFS は
        // それらを辿らず、caller 件数や ranking を膨らませない。
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/impact_metadata_cycle.cs",
            Lang = "csharp",
            Size = 128,
            Lines = 6,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks([
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 6,
                Content = "[ImpactMetadataConsumer]\nclass ImpactMetadataTarget {}\nclass ImpactMetadataConsumer {}\n",
            }
        ]);
        _writer.InsertReferences([
            new ReferenceRecord
            {
                FileId = fileId,
                SymbolName = "ImpactMetadataTarget",
                ReferenceKind = "attribute",
                Line = 1,
                Column = 2,
                Context = "[ImpactMetadataTarget]",
                ContainerKind = "class",
                ContainerName = "ImpactMetadataConsumer",
            },
            new ReferenceRecord
            {
                FileId = fileId,
                SymbolName = "ImpactMetadataConsumer",
                ReferenceKind = "type_reference",
                Line = 2,
                Column = 28,
                Context = "class ImpactMetadataTarget : ImpactMetadataConsumer {}",
                ContainerKind = "class",
                ContainerName = "ImpactMetadataTarget",
            },
        ]);

        var (impact, truncated, truncatedReason, _, _) = _reader.GetTransitiveCallers(
            "ImpactMetadataTarget", maxDepth: 5, limit: 10, lang: "csharp", pathPatterns: ["impact_metadata_cycle"]);

        Assert.False(truncated);
        Assert.Null(truncatedReason);
        Assert.Empty(impact);
    }

    [Fact]
    public void GetTransitiveCallers_ReturnsAllDirectCallersAcrossPages()
    {
        const int callerCount = 205;
        for (int i = 0; i < callerCount; i++)
        {
            var callerFileId = _writer.UpsertFile(new FileRecord
            {
                Path = $"src/caller_{i:D3}.py",
                Lang = "python",
                Size = 96,
                Lines = 2,
                Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            _writer.InsertChunks([new ChunkRecord
            {
                FileId = callerFileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 2,
                Content = $"def caller_{i:D3}():\n    return authenticate('user', 'pw')\n",
            }]);
            _writer.InsertReferences([
                new ReferenceRecord
                {
                    FileId = callerFileId,
                    SymbolName = "authenticate",
                    ReferenceKind = "call",
                    Line = 2,
                    Column = 12,
                    Context = "return authenticate('user', 'pw')",
                    ContainerKind = "function",
                    ContainerName = $"caller_{i:D3}",
                },
            ]);
        }

        var (results, truncated, truncatedReason, _, _) = _reader.GetTransitiveCallers("authenticate", maxDepth: 1, limit: 300);

        Assert.False(truncated);
        Assert.Null(truncatedReason);
        Assert.Equal(callerCount, results.Count);
        Assert.Equal(callerCount, results.Select(result => $"{result.Path}:{result.CallerName}").Distinct(StringComparer.Ordinal).Count());
        Assert.All(results, result => Assert.Equal(1, result.Depth));
    }

    [Fact]
    public void GetTransitiveCallers_MaxDepthIsInclusiveAcrossChain()
    {
        // Regression for #1879: an audit suspected an off-by-one in the depth bound
        // (i.e. that --depth=2 would only reach depth 1). Verify with a 3-hop chain
        // ImpactNodeA → ImpactNodeB → ImpactNodeC → ImpactLeaf that maxDepth is inclusive:
        //  - maxDepth=1 returns only the direct caller (ImpactNodeC at depth 1);
        //  - maxDepth=2 also returns ImpactNodeB at depth 2;
        //  - maxDepth=3 also returns ImpactNodeA at depth 3.
        // #1879 回帰: maxDepth が inclusive であること (--depth=2 が depth 2 まで到達する) を
        // 3-hop チェーン ImpactNodeA → ImpactNodeB → ImpactNodeC → ImpactLeaf で確認する。
        InsertIndexedFile("src/impact_depth_chain.cs", "csharp",
            """
            public static class ImpactDepthChain
            {
                public static void ImpactLeaf() { }
                public static void ImpactNodeC() { ImpactLeaf(); }
                public static void ImpactNodeB() { ImpactNodeC(); }
                public static void ImpactNodeA() { ImpactNodeB(); }
            }
            """);

        var (depth1, truncated1, truncatedReason1, _, _) = _reader.GetTransitiveCallers(
            "ImpactLeaf", maxDepth: 1, limit: 20, lang: "csharp", pathPatterns: ["impact_depth_chain"]);
        var (depth2, truncated2, truncatedReason2, _, _) = _reader.GetTransitiveCallers(
            "ImpactLeaf", maxDepth: 2, limit: 20, lang: "csharp", pathPatterns: ["impact_depth_chain"]);
        var (depth3, truncated3, truncatedReason3, _, _) = _reader.GetTransitiveCallers(
            "ImpactLeaf", maxDepth: 3, limit: 20, lang: "csharp", pathPatterns: ["impact_depth_chain"]);

        Assert.False(truncated1);
        Assert.False(truncated2);
        Assert.False(truncated3);
        Assert.Null(truncatedReason1);
        Assert.Null(truncatedReason2);
        Assert.Null(truncatedReason3);

        var depth1Pairs = depth1.Select(r => (r.CallerName, r.Depth)).ToArray();
        Assert.Equal(new (string?, int)[] { ("ImpactNodeC", 1) }, depth1Pairs);

        var depth2Pairs = depth2
            .Select(r => (r.CallerName, r.Depth))
            .OrderBy(p => p.Depth)
            .ToArray();
        Assert.Equal(new (string?, int)[] { ("ImpactNodeC", 1), ("ImpactNodeB", 2) }, depth2Pairs);

        var depth3Pairs = depth3
            .Select(r => (r.CallerName, r.Depth))
            .OrderBy(p => p.Depth)
            .ToArray();
        Assert.Equal(
            new (string?, int)[] { ("ImpactNodeC", 1), ("ImpactNodeB", 2), ("ImpactNodeA", 3) },
            depth3Pairs);
    }








    [Fact]
    public void GetTransitiveCallers_WithPathsDefaultIsOff()
    {
        // Default (no opt-in) keeps the legacy contract: Paths is null and PathsTruncated is false.
        // 既定では Paths は null、PathsTruncated は false で旧来の契約を維持する。
        InsertIndexedFile("src/impact_paths_off.cs", "csharp",
            """
            public static class ImpactPathsOff
            {
                public static void Leaf() { }
                public static void Caller() { Leaf(); }
            }
            """);

        var (results, _, _, _, _) = _reader.GetTransitiveCallers(
            "Leaf", maxDepth: 2, limit: 10, lang: "csharp", pathPatterns: ["impact_paths_off"]);

        var caller = Assert.Single(results);
        Assert.Equal("Caller", caller.CallerName);
        Assert.Null(caller.Paths);
        Assert.False(caller.PathsTruncated);
    }

    [Fact]
    public void GetTransitiveCallers_WithPathsSurfacesDiamondConvergence()
    {
        // Issue #1536: when BFS converges on the same caller via distinct intermediates at the
        // same depth (A → B → Foo and A → C → Foo), `--with-paths` must surface both routes so
        // that callers can tell "via what" the dependency flows. The non-opt-in result keeps the
        // historical dedup (single A row at depth 2).
        // issue #1536: 同 depth で同名 caller に複数経路が収束する (A → B → Foo と A → C → Foo)
        // 場合、--with-paths で双方の経路を返すこと。opt-in しない既存出力は depth 2 の A 1 行に
        // 集約される従来動作を維持する。
        InsertIndexedFile("src/impact_paths_diamond.cs", "csharp",
            """
            public static class ImpactPathsDiamond
            {
                public static void Foo() { }
                public static void B() { Foo(); }
                public static void C() { Foo(); }
                public static void A() { B(); C(); }
            }
            """);

        var (resultsDefault, _, _, _, _) = _reader.GetTransitiveCallers(
            "Foo", maxDepth: 5, limit: 20, lang: "csharp", pathPatterns: ["impact_paths_diamond"]);

        var defaultByName = resultsDefault
            .GroupBy(r => r.CallerName)
            .ToDictionary(g => g.Key!, g => g.OrderBy(r => r.Depth).First());
        // Diamond dedup collapses A to a single row at depth 2 — the legacy behavior the issue
        // calls out as lossy (no "via what" hint without --with-paths).
        Assert.True(defaultByName.ContainsKey("A"));
        Assert.Equal(2, defaultByName["A"].Depth);
        Assert.Null(defaultByName["A"].Paths);

        var (resultsWithPaths, _, _, _, _) = _reader.GetTransitiveCallers(
            "Foo", maxDepth: 5, limit: 20, lang: "csharp", pathPatterns: ["impact_paths_diamond"],
            withPaths: true);

        var aResult = resultsWithPaths.Single(r => r.CallerName == "A");
        Assert.NotNull(aResult.Paths);
        Assert.False(aResult.PathsTruncated);

        var pathSet = aResult.Paths!
            .Select(p => string.Join("->", p))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "Foo->B->A", "Foo->C->A" }, pathSet);
        Assert.NotNull(aResult.PathDetails);
        var detailPathSet = aResult.PathDetails!
            .Select(p => string.Join("->", p.Select(node => $"{node.Name}@{node.DefinitionPath}")))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            new[]
            {
                "Foo@src/impact_paths_diamond.cs->B@src/impact_paths_diamond.cs->A@src/impact_paths_diamond.cs",
                "Foo@src/impact_paths_diamond.cs->C@src/impact_paths_diamond.cs->A@src/impact_paths_diamond.cs",
            },
            detailPathSet);
        var firstDetailPath = aResult.PathDetails![0];
        Assert.All(firstDetailPath, node =>
        {
            Assert.Equal("csharp", node.Lang);
            Assert.Equal("function", node.Kind);
            Assert.Equal("src/impact_paths_diamond.cs", node.DefinitionPath);
            Assert.True(node.DefinitionLine > 0);
            Assert.Matches("^(family|container|file)\\|", node.LogicalTargetKey);
        });
        Assert.Equal("src/impact_paths_diamond.cs", firstDetailPath[^1].ReferencePath);
        Assert.True(firstDetailPath[^1].ReferenceLine > 0);

        // Direct callers (B, C) keep a single trivial path that ends at themselves.
        var bResult = resultsWithPaths.Single(r => r.CallerName == "B");
        Assert.NotNull(bResult.Paths);
        Assert.Equal(new[] { "Foo->B" }, bResult.Paths!.Select(p => string.Join("->", p)).ToArray());
        Assert.NotNull(bResult.PathDetails);
        Assert.Equal("src/impact_paths_diamond.cs", bResult.PathDetails![0][^1].DefinitionPath);
        var cResult = resultsWithPaths.Single(r => r.CallerName == "C");
        Assert.NotNull(cResult.Paths);
        Assert.Equal(new[] { "Foo->C" }, cResult.Paths!.Select(p => string.Join("->", p)).ToArray());
    }

    [Fact]
    public void GetTransitiveCallers_WithPathsRespectsPerRowCap()
    {
        // When more shortest paths converge on a single caller than the per-row cap allows,
        // PathsTruncated must be set so consumers know there are more routes than emitted.
        // 同一 caller に保持上限を超える経路がある場合は PathsTruncated を立てて知らせること。
        InsertIndexedFile("src/impact_paths_cap.cs", "csharp",
            """
            public static class ImpactPathsCap
            {
                public static void Sink() { }
                public static void M1() { Sink(); }
                public static void M2() { Sink(); }
                public static void M3() { Sink(); }
                public static void Top() { M1(); M2(); M3(); }
            }
            """);

        var (results, _, _, _, _) = _reader.GetTransitiveCallers(
            "Sink", maxDepth: 5, limit: 20, lang: "csharp", pathPatterns: ["impact_paths_cap"],
            withPaths: true, maxPathsPerResult: 2);

        var top = results.Single(r => r.CallerName == "Top");
        Assert.NotNull(top.Paths);
        Assert.Equal(2, top.Paths!.Count);
        Assert.True(top.PathsTruncated);

        // Exact-fit: cap equals the natural number of paths. Truncated must stay false because
        // no unexplored parent was skipped — the DFS just drained naturally as it hit the cap.
        // ちょうど cap と等しい経路数の場合、未探索 parent はないので truncated は false のまま。
        var (exactResults, _, _, _, _) = _reader.GetTransitiveCallers(
            "Sink", maxDepth: 5, limit: 20, lang: "csharp", pathPatterns: ["impact_paths_cap"],
            withPaths: true, maxPathsPerResult: 3);
        var exactTop = exactResults.Single(r => r.CallerName == "Top");
        Assert.NotNull(exactTop.Paths);
        Assert.Equal(3, exactTop.Paths!.Count);
        Assert.False(exactTop.PathsTruncated);
    }

    [Fact]
    public void GetTransitiveCallers_LimitSmallerThanCallerCount_ReportsUserLimitReason()
    {
        // #1533: when truncation is caused by --limit, the reason must be "user_limit"
        // so callers know that raising --limit is the right remediation.
        // #1533: --limit による打ち切り時は理由 "user_limit" を返し、--limit を上げれば
        // 解消することを伝える。
        const int callerCount = 8;
        for (int i = 0; i < callerCount; i++)
        {
            var callerFileId = _writer.UpsertFile(new FileRecord
            {
                Path = $"src/limit_caller_{i:D2}.py",
                Lang = "python",
                Size = 96,
                Lines = 2,
                Modified = new DateTime(2026, 5, 15, 0, 0, 0, DateTimeKind.Utc),
            });
            _writer.InsertChunks([new ChunkRecord
            {
                FileId = callerFileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 2,
                Content = $"def caller_{i:D2}():\n    return target()\n",
            }]);
            _writer.InsertReferences([
                new ReferenceRecord
                {
                    FileId = callerFileId,
                    SymbolName = "target",
                    ReferenceKind = "call",
                    Line = 2,
                    Column = 12,
                    Context = "return target()",
                    ContainerKind = "function",
                    ContainerName = $"caller_{i:D2}",
                },
            ]);
        }

        var (results, truncated, truncatedReason, _, _) = _reader.GetTransitiveCallers("target", maxDepth: 1, limit: 3);

        Assert.True(truncated);
        Assert.Equal(ImpactTruncatedReasons.UserLimit, truncatedReason);
        Assert.Equal(3, results.Count);
    }





















    [Fact]
    public void ListFiles_ReturnsAllFiles()
    {
        var results = _reader.ListFiles();
        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void ListFiles_FiltersByLanguage()
    {
        var results = _reader.ListFiles(lang: "python");
        Assert.Single(results);
        Assert.Equal("src/auth.py", results[0].Path);
    }

    [Fact]
    public void ListFiles_FiltersByNamePattern()
    {
        var results = _reader.ListFiles(query: "api");
        Assert.Single(results);
        Assert.Equal("src/api.js", results[0].Path);
    }

    [Fact]
    public void ListFiles_MultiplePathPatterns_AreOred()
    {
        // Two --path values should match any file whose path matches either pattern.
        // 2つの --path 値は、どちらかのパターンにマッチするファイルを返す。
        var results = _reader.ListFiles(pathPatterns: new[] { "auth", "docs/" });

        Assert.Equal(2, results.Count);
        var paths = results.Select(r => r.Path).ToHashSet();
        Assert.Contains("src/auth.py", paths);
        Assert.Contains("docs/notes.md", paths);
    }

    [Fact]
    public void ListFiles_PathFiltersAndExcludePaths_WorkTogether()
    {
        var results = _reader.ListFiles(pathPatterns: new[] { "src/" }, excludePathPatterns: ["api"]);

        Assert.Single(results);
        Assert.Equal("src/auth.py", results[0].Path);
    }

    [Fact]
    public void ListFiles_IncludesSymbolCount()
    {
        var results = _reader.ListFiles(query: "api");
        Assert.Equal(2, results[0].SymbolCount); // ApiClient + fetchData
    }

    [Fact]
    public void ListFiles_ReturnsFreshnessMetadata()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/fresh.cs",
            Lang = "csharp",
            Size = 120,
            Lines = 6,
            Checksum = "fresh-checksum",
            Modified = new DateTime(2025, 6, 2, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks([new ChunkRecord
        {
            FileId = fileId,
            ChunkIndex = 0,
            StartLine = 1,
            EndLine = 6,
            Content = "public class Fresh { public void Run() { } }",
        }]);

        var file = Assert.Single(_reader.ListFiles(query: "fresh.cs"));
        Assert.Equal("fresh-checksum", file.Checksum);
        Assert.Equal(new DateTime(2025, 6, 2, 0, 0, 0, DateTimeKind.Utc), file.Modified);
        Assert.NotNull(file.IndexedAt);
    }

    [Fact]
    public void GetStatus_ReturnsCorrectCounts()
    {
        var status = _reader.GetStatus();
        Assert.Equal(3, status.Files);
        Assert.Equal(3, status.Chunks);
        Assert.Equal(3, status.Symbols);
        Assert.Equal(1, status.References);
        Assert.NotNull(status.IndexedAt);
    }

    [Fact]
    public void GetStatus_IncludesLanguageBreakdown()
    {
        var status = _reader.GetStatus();
        Assert.Equal(3, status.Languages.Count);
        Assert.Equal(1, status.Languages["python"]);
        Assert.Equal(1, status.Languages["javascript"]);
        Assert.Equal(1, status.Languages["markdown"]);
    }

    [Fact]
    public void GetStatus_ExposesDbPragmaSettings()
    {
        var status = _reader.GetStatus();

        Assert.Equal("wal", status.DbPragmaSettings.JournalMode);
        Assert.Equal(DbContext.DefaultSynchronousMode, status.DbPragmaSettings.Synchronous);
        Assert.Equal(DbContext.DefaultWalAutocheckpointPages, status.DbPragmaSettings.WalAutocheckpoint);
    }

    [Fact]
    public void GetStatus_ExposesCSharpMetadataTargetReadyForWorkspaceWithoutCSharpFiles()
    {
        // #435 codex review iter 3: README / CLAUDE.md advertise `csharp_metadata_target_ready`
        // on `status --json`. Before iter 3, `StatusResult` had no such property, so the JSON
        // silently returned `null` and the contract was violated. A workspace with NO C# files
        // must still report the flag as `true` because no edge is exposed to degraded fallback.
        // #435 codex review iter 3: C# ファイルが 0 の workspace では契約上 ready=true を返す。
        var status = _reader.GetStatus();

        Assert.True(status.CSharpMetadataTargetReady);
    }

    [Fact]
    public void GetStatus_ExposesCSharpMetadataTargetReadyFalseWhenContractStampMissing()
    {
        // #435 codex review iter 3: a workspace with C# files whose DB is missing the
        // `metadata_target_version_csharp` stamp must surface as `csharp_metadata_target_ready=false`
        // so `status --json` and the human `WARN` line can tell AI clients that `deps` / `impact`
        // metadata-attribute edges are running on the legacy `signature LIKE '%: %'` heuristic
        // instead of the authoritative persisted column. Before the iter-3 fix the flag never
        // flowed into `StatusResult` at all, so a degraded DB looked healthy in both paths.
        // #435 codex review iter 3: C# ファイルがあり、かつ stamp 欠落 / ズレで authoritative
        // column が信頼できない状態では false を返して AI クライアントに縮退を伝える。
        InsertIndexedFile("src/Foo.cs", "csharp", "public class Foo { }\n");
        ClearMetaStamp(DbContext.GetMetadataTargetVersionMetaKey("csharp"));
        var freshReader = new DbReader(_db.Connection);

        var status = freshReader.GetStatus();

        Assert.False(status.CSharpMetadataTargetReady);
        Assert.Equal(DegradationReasonCodes.CSharpMetadataTargetStampOutdated, status.CSharpMetadataTargetDegradedReason);
    }

    [Fact]
    public void GetStatus_DistinguishesCSharpMetadataTargetMissingColumn()
    {
        InsertIndexedFile("src/Legacy.cs", "csharp", "public class Legacy { }\n");
        RecreateSymbolsTableWithoutMetadataTargetColumn();
        var freshReader = new DbReader(_db.Connection);

        var status = freshReader.GetStatus();

        Assert.False(status.CSharpMetadataTargetReady);
        Assert.Equal(DegradationReasonCodes.CSharpMetadataTargetMissingColumn, status.CSharpMetadataTargetDegradedReason);
    }

    [Fact]
    public void GetStatus_DistinguishesCSharpMetadataTargetSourceMissingColumn_Issue3524()
    {
        InsertIndexedFile("src/LegacySource.cs", "csharp", "public class LegacySource { }\n");
        _writer.MarkMetadataTargetReady("csharp");
        RecreateSymbolsTableWithoutMetadataTargetSourceColumn();
        var freshReader = new DbReader(_db.Connection);

        var status = freshReader.GetStatus();

        Assert.False(status.CSharpMetadataTargetReady);
        Assert.Equal(DegradationReasonCodes.CSharpMetadataTargetMissingColumn, status.CSharpMetadataTargetDegradedReason);
    }

    [Fact]
    public void GetStatus_ExposesCSharpMetadataTargetReadyTrueWhenContractStampCurrent()
    {
        // Happy path: C# files are indexed and the current-version stamp is present, so the
        // reader should report the authoritative column is trustworthy. Pins the positive side
        // of the flag to prevent future regressions that would keep the JSON always false.
        // C# ファイル + 現行契約 stamp が揃っているときは true を返すという正常系の pin。
        InsertIndexedFile("src/Bar.cs", "csharp", "public class Bar { }\n");
        _writer.MarkMetadataTargetReady("csharp");
        var freshReader = new DbReader(_db.Connection);

        var status = freshReader.GetStatus();

        Assert.True(status.CSharpMetadataTargetReady);
    }

    [Fact]
    public void GetStatus_ExposesIndexWriterVersionStampedByWriter()
    {
        // Issue #1515: WriteCdidxWriterVersion stores the cdidx version that wrote the most
        // recent successful index pass. Pinned so `status --json` can surface "indexed by
        // v1.22.0, you are on v1.21.0" against the reader's own version.
        // Issue #1515: writer.WriteCdidxWriterVersion で stamp した version を status に出す。
        _writer.WriteCdidxWriterVersion("1.22.0");
        var freshReader = new DbReader(_db.Connection);

        var status = freshReader.GetStatus();

        Assert.Equal("1.22.0", status.IndexWriterVersion);
    }

    [Fact]
    public void GetStatus_ReportsLegacyDbWithoutWriterVersionStamp()
    {
        // Issue #1515: a DB that was never end-of-index-stamped (legacy or pre-1515 binary)
        // must surface `index_writer_version` as null so AI clients can tell "we don't know
        // who wrote this" apart from "this version wrote it". The forward-compat sentinel
        // should also stay false because no numeric contract stored exceeds the reader's max.
        // Issue #1515: stamp 無し DB では writer_version=null + newer_than_reader=false。
        ClearMetaStamp(DbContext.CdidxWriterVersionMetaKey);
        var freshReader = new DbReader(_db.Connection);

        var status = freshReader.GetStatus();

        Assert.Null(status.IndexWriterVersion);
        Assert.False(status.IndexNewerThanReader);
        Assert.Null(status.IndexNewerThanReaderReason);
    }

    [Fact]
    public void GetStatus_FlagsIndexNewerThanReaderWhenCSharpMetadataVersionExceedsCurrent()
    {
        // Issue #1515: the existing string.Equals readiness gate silently degraded when a
        // newer cdidx wrote `metadata_target_version_csharp` = current+1 and an older cdidx
        // re-opened the DB. The new forward-compat sentinel must flip to true with a reason
        // that names the offending contract so `status` can WARN loudly instead of pretending
        // the DB is merely "degraded due to stale stamp".
        // Issue #1515: stored > current の数値 contract を「未来 DB」として明示する。
        _writer.SetMeta(
            DbContext.GetMetadataTargetVersionMetaKey("csharp"),
            (DbContext.MetadataTargetVersion + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        _writer.WriteCdidxWriterVersion("9.99.0");
        var freshReader = new DbReader(_db.Connection);

        var status = freshReader.GetStatus();

        Assert.True(status.IndexNewerThanReader);
        Assert.NotNull(status.IndexNewerThanReaderReason);
        Assert.Contains("metadata_target_version_csharp", status.IndexNewerThanReaderReason);
        Assert.Equal("9.99.0", status.IndexWriterVersion);
    }

    [Fact]
    public void GetStatus_FlagsIndexNewerThanReaderWhenUserVersionCarriesUnknownReadyBit()
    {
        // Issue #1515: a future cdidx may introduce a new readiness bit beyond
        // `DbContext.CurrentSchemaVersion`. PRAGMA user_version values with bits outside that
        // mask therefore indicate the DB was written by a newer binary, even if every numeric
        // meta contract still equals the older binary's compiled max.
        // Issue #1515: CurrentSchemaVersion マスク外の bit も「未来 DB」シグナルにする。
        var unknownBit = (DbContext.CurrentSchemaVersion + 1) | DbContext.CurrentSchemaVersion;
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA user_version = {unknownBit}";
            cmd.ExecuteNonQuery();
        }
        var freshReader = new DbReader(_db.Connection);

        var status = freshReader.GetStatus();

        Assert.True(status.IndexNewerThanReader);
        Assert.NotNull(status.IndexNewerThanReaderReason);
        Assert.Contains("user_version_bits", status.IndexNewerThanReaderReason);
    }

    [Fact]
    public void GetStatus_DoesNotFlagIndexNewerThanReaderWhenAllStoredVersionsMatchCurrent()
    {
        // Negative pin: a DB whose stamps all equal this binary's compiled constants must
        // never trip the forward-compat sentinel. Keeps the existing "stored == current"
        // happy path observably distinct from the new "stored > current" warning, so AI
        // clients can rely on the flag instead of false-positive degraded reasons.
        // Issue #1515: stored == current の通常 DB では新フラグは false のまま。
        var freshReader = new DbReader(_db.Connection);

        var status = freshReader.GetStatus();

        Assert.False(status.IndexNewerThanReader);
        Assert.Null(status.IndexNewerThanReaderReason);
    }

    private void ClearMetaStamp(string key)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM codeindex_meta WHERE key = @key";
        cmd.Parameters.AddWithValue("@key", key);
        cmd.ExecuteNonQuery();
    }

    private void RecreateSymbolsTableWithoutMetadataTargetColumn()
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA foreign_keys = OFF;
            ALTER TABLE symbols RENAME TO symbols_old;
            CREATE TABLE symbols (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                file_id         INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                kind            TEXT,
                sub_kind        TEXT,
                name            TEXT,
                name_folded     TEXT,
                line            INTEGER,
                start_line      INTEGER,
                start_column    INTEGER,
                end_line        INTEGER,
                body_start_line INTEGER,
                body_end_line   INTEGER,
                signature       TEXT,
                container_kind  TEXT,
                container_name  TEXT,
                container_qualified_name TEXT,
                family_key      TEXT,
                visibility      TEXT,
                return_type     TEXT
            );
            INSERT INTO symbols (
                id, file_id, kind, sub_kind, name, name_folded, line, start_line,
                start_column, end_line, body_start_line,
                body_end_line, signature, container_kind, container_name,
                container_qualified_name, family_key, visibility, return_type
            )
            SELECT
                id, file_id, kind, sub_kind, name, name_folded, line, start_line,
                start_column, end_line, body_start_line,
                body_end_line, signature, container_kind, container_name,
                container_qualified_name, family_key, visibility, return_type
            FROM symbols_old;
            DROP TABLE symbols_old;
            PRAGMA foreign_keys = ON;
            """;
        cmd.ExecuteNonQuery();
    }

    private void RecreateSymbolsTableWithoutMetadataTargetSourceColumn()
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA foreign_keys = OFF;
            ALTER TABLE symbols RENAME TO symbols_old;
            CREATE TABLE symbols (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                file_id         INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                kind            TEXT,
                sub_kind        TEXT,
                name            TEXT,
                name_folded     TEXT,
                line            INTEGER,
                start_line      INTEGER,
                start_column    INTEGER,
                end_line        INTEGER,
                body_start_line INTEGER,
                body_end_line   INTEGER,
                signature       TEXT,
                container_kind  TEXT,
                container_name  TEXT,
                container_qualified_name TEXT,
                family_key      TEXT,
                visibility      TEXT,
                return_type     TEXT,
                is_metadata_target INTEGER
            );
            INSERT INTO symbols (
                id, file_id, kind, sub_kind, name, name_folded, line, start_line,
                start_column, end_line, body_start_line,
                body_end_line, signature, container_kind, container_name,
                container_qualified_name, family_key, visibility, return_type, is_metadata_target
            )
            SELECT
                id, file_id, kind, sub_kind, name, name_folded, line, start_line,
                start_column, end_line, body_start_line,
                body_end_line, signature, container_kind, container_name,
                container_qualified_name, family_key, visibility, return_type, is_metadata_target
            FROM symbols_old;
            DROP TABLE symbols_old;
            PRAGMA foreign_keys = ON;
            """;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void GetRepoMap_ReturnsOverviewSectionsAndEntrypoints()
    {
        InsertIndexedFile("src/Program.cs", "csharp", "public class Program\n{\n    public static void Main(string[] args)\n    {\n        var client = new ApiClient();\n    }\n}\n");

        var map = _reader.GetRepoMap(limit: 5, excludeTests: true);

        Assert.True(map.FileCount >= 3);
        Assert.Contains(map.Languages, item => item.Lang == "csharp");
        Assert.Contains(map.Modules, item => item.Module == "src");
        Assert.NotEmpty(map.TopFiles);
        Assert.NotEmpty(map.LargestFiles);
        Assert.NotEmpty(map.SymbolRichFiles);
        Assert.NotEmpty(map.ReferenceRichFiles);
        Assert.Contains(map.Entrypoints, item => item.Name == "Main" && item.Path == "src/Program.cs");
        var entrypoint = Assert.Single(map.Entrypoints, item => item.Name == "Main" && item.Path == "src/Program.cs");
        Assert.Equal("path+name", entrypoint.MatchType);
        Assert.True(entrypoint.Confidence >= 0.8);
        Assert.Equal(1, entrypoint.HintRank);
    }

    [Fact]
    public void GetRepoMap_KeepsSectionOrderingAndCountsAfterAggregateRefactor()
    {
        InsertIndexedFile("perfmap/api/large.md", "markdown", "one\ntwo\nthree\nfour");
        InsertIndexedFile("perfmap/api/small.md", "markdown", "one");
        InsertIndexedFile("perfmap/cli/medium.py", "python", "# note\n# note");

        var map = _reader.GetRepoMap(limit: 3, pathPatterns: new[] { "perfmap/" });

        Assert.Equal(3, map.FileCount);
        Assert.Equal(7, map.TotalLines);
        Assert.Collection(map.Languages,
            language =>
            {
                Assert.Equal("markdown", language.Lang);
                Assert.Equal(2, language.Files);
                Assert.Equal(5, language.Lines);
            },
            language =>
            {
                Assert.Equal("python", language.Lang);
                Assert.Equal(1, language.Files);
                Assert.Equal(2, language.Lines);
            });
        Assert.Collection(map.Modules,
            module =>
            {
                Assert.Equal("perfmap", module.Module);
                Assert.Equal(3, module.Files);
                Assert.Equal(7, module.Lines);
            });
        Assert.Equal(new[] { "perfmap/api/large.md", "perfmap/cli/medium.py", "perfmap/api/small.md" },
            map.TopFiles.Select(file => file.Path).ToArray());
        Assert.Equal(new[] { "perfmap/api/large.md", "perfmap/cli/medium.py", "perfmap/api/small.md" },
            map.LargestFiles.Select(file => file.Path).ToArray());
        Assert.All(map.LargestFiles, file => Assert.Null(file.Score));
    }

    [Fact]
    public void GetRepoMap_AddsFileFallbackEntrypointForTopLevelProgram()
    {
        InsertIndexedFile("src/Program.cs", "csharp", "var client = new ApiClient();\nConsole.WriteLine(client);\n");

        var map = _reader.GetRepoMap(limit: 5, pathPatterns: new[] { "src/Program.cs" });

        var entrypoint = Assert.Single(map.Entrypoints, item => item.Kind == "file" && item.Name == "Program.cs" && item.Path == "src/Program.cs");
        Assert.Equal("path", entrypoint.MatchType);
        Assert.True(entrypoint.Confidence >= 0.4);
        Assert.Equal(1, entrypoint.HintRank);
    }

    [Fact]
    public void GetRepoMap_RanksProductionCliEntrypointAheadOfToolingPrograms_Issue4115()
    {
        InsertIndexedFile("src/CodeIndex/Program.cs", "csharp",
            """
            return await CodeIndex.Cli.ProgramRunner.RunAsync(args);

            internal static class BuildHost
            {
                public static object Create() => new();
            }
            """);
        InsertIndexedFile("tools/CodeIndex.Changelog/Program.cs", "csharp",
            """
            namespace CodeIndex.Changelog;

            public static class Program
            {
                public static int Main(string[] args) => 0;
            }
            """);
        InsertIndexedFile("src/Tools/BuildHelper/Program.cs", "csharp",
            """
            namespace BuildHelper;

            public static class Program
            {
                public static int Main(string[] args) => 0;
            }
            """);
        InsertIndexedFile("src/CodeIndex.Tools/Program.cs", "csharp",
            """
            namespace CodeIndex.Tools;

            public static class Program
            {
                public static int Main(string[] args) => 0;
            }
            """);
        InsertIndexedFile("tools/TopLevel/Program.cs", "csharp", "Console.WriteLine(\"tool\");\n");
        InsertIndexedFile("src/CodeIndex.Tests/Program.cs", "csharp",
            """
            namespace CodeIndex.Tests;

            public static class Program
            {
                public static int Main(string[] args) => 0;
            }
            """);
        InsertIndexedFile(".codex/hooks/bash_guard.py", "python", "def main():\n    return 0\n");

        var map = _reader.GetRepoMap(limit: 20, excludeTests: true);
        var production = Assert.Single(map.Entrypoints, item => item.Path == "src/CodeIndex/Program.cs" && item.Kind == "file");
        var productionHelpers = map.Entrypoints
            .Where(item => item.Path == "src/CodeIndex/Program.cs" && item.Kind != "file")
            .ToList();
        var toolScores = map.Entrypoints
            .Where(item => item.Path.StartsWith("tools/", StringComparison.OrdinalIgnoreCase) ||
                           item.Path.Contains("/tools/", StringComparison.OrdinalIgnoreCase) ||
                           item.Path.Contains(".Tool/", StringComparison.OrdinalIgnoreCase) ||
                           item.Path.Contains(".Tools/", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Score)
            .ToList();
        var toolConfidences = map.Entrypoints
            .Where(item => item.Path.StartsWith("tools/", StringComparison.OrdinalIgnoreCase) ||
                           item.Path.Contains("/tools/", StringComparison.OrdinalIgnoreCase) ||
                           item.Path.Contains(".Tool/", StringComparison.OrdinalIgnoreCase) ||
                           item.Path.Contains(".Tools/", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Confidence)
            .ToList();
        var unfilteredMap = _reader.GetRepoMap(limit: 20, excludeTests: false);
        var unfilteredProduction = Assert.Single(unfilteredMap.Entrypoints, item => item.Path == "src/CodeIndex/Program.cs" && item.Kind == "file");
        var dottedTestScores = unfilteredMap.Entrypoints
            .Where(item => item.Path == "src/CodeIndex.Tests/Program.cs")
            .Select(item => item.Score)
            .ToList();

        Assert.NotEmpty(toolScores);
        Assert.NotEmpty(productionHelpers);
        Assert.NotEmpty(dottedTestScores);
        Assert.Contains(map.Entrypoints, item => item.Path == "tools/TopLevel/Program.cs" && item.Kind == "file");
        Assert.Equal("src/CodeIndex/Program.cs", map.Entrypoints[0].Path);
        Assert.Equal("file", production.Kind);
        Assert.All(productionHelpers, helper => Assert.True(production.Score > helper.Score));
        Assert.All(toolScores, score => Assert.True(production.Score > score));
        Assert.All(toolConfidences, confidence => Assert.True(production.Confidence >= confidence));
        Assert.All(dottedTestScores, score => Assert.True(unfilteredProduction.Score > score));
    }

    [Fact]
    public void GetRepoMap_KeepsTestFileFallbackEntrypointWhenTestsIncluded_Issue4115()
    {
        InsertIndexedFile("tests/Program.cs", "csharp", "Console.WriteLine(\"test\");\n");

        var map = _reader.GetRepoMap(limit: 5, pathPatterns: new[] { "tests/Program.cs" }, excludeTests: false);

        var entrypoint = Assert.Single(map.Entrypoints, item => item.Path == "tests/Program.cs");
        Assert.Equal("file", entrypoint.Kind);
        Assert.Equal("path", entrypoint.MatchType);
        Assert.True(entrypoint.Score > 0);
    }

    [Fact]
    public void GetRepoMap_KeepsSupportFileFallbackEntrypointVisible_Issue4115()
    {
        InsertIndexedFile("scripts/main.py", "python", "# support script\n");

        var map = _reader.GetRepoMap(limit: 5, pathPatterns: new[] { "scripts/main.py" }, excludeTests: false);

        var entrypoint = Assert.Single(map.Entrypoints, item => item.Path == "scripts/main.py");
        Assert.Equal("file", entrypoint.Kind);
        Assert.Equal("path", entrypoint.MatchType);
        Assert.True(entrypoint.Score > 0);
    }

    [Fact]
    public void GetRepoMap_MinEntrypointConfidenceFiltersWeakNameOnlyMatches()
    {
        InsertIndexedFile("src/services/service.py", "python", "def app():\n    return True\n");
        InsertIndexedFile("src/main.py", "python", "def main():\n    return True\n");

        var map = _reader.GetRepoMap(limit: 10, lang: "python", minEntrypointConfidence: 0.7);

        Assert.Contains(map.Entrypoints, item => item.Path == "src/main.py" && item.Name == "main");
        Assert.DoesNotContain(map.Entrypoints, item => item.Path == "src/services/service.py" && item.Name == "app");
    }

    [Fact]
    public void GetRepoMap_RepeatedWeakEntrypointNamesReduceConfidence()
    {
        InsertIndexedFile("src/plugins/first.py", "python", "def app():\n    return True\n");
        InsertIndexedFile("src/plugins/second.py", "python", "def app():\n    return True\n");

        var map = _reader.GetRepoMap(limit: 10, lang: "python", pathPatterns: new[] { "plugins/" });

        var entries = map.Entrypoints.Where(item => item.Name == "app").ToList();
        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry =>
        {
            Assert.Equal("name", entry.MatchType);
            Assert.True(entry.Confidence < 0.5);
        });
    }

    [Theory]
    [InlineData("src/MainWindow.xaml.cs", "MainWindow")]
    [InlineData("src/MainPage.xaml.cs", "MainPage")]
    [InlineData("src/AppShell.xaml.cs", "AppShell")]
    [InlineData("src/Shell.xaml.cs", "Shell")]
    [InlineData("src/ContentPage.xaml.cs", "ContentPage")]
    public void GetRepoMap_AddsFileFallbackEntrypointForCommonCSharpXamlCodeBehind(string path, string className)
    {
        InsertIndexedFile(path, "csharp", "public partial class " + className + "\n{\n}\n");

        var map = _reader.GetRepoMap(limit: 5, pathPatterns: new[] { path });

        Assert.Contains(map.Entrypoints, item => item.Kind == "class" && item.Name == className && item.Path == path);
    }

    [Theory]
    [InlineData("src/Main.vb")]
    [InlineData("src/Module.vb")]
    [InlineData("src/Form1.vb")]
    [InlineData("src/App.xaml.vb")]
    public void GetRepoMap_AddsFileFallbackEntrypointForCommonVbStartupFiles(string path)
    {
        InsertIndexedFile(path, "vb",
            """
            Public Class Launcher
                Public Sub Execute()
                End Sub
            End Class
            """);

        var map = _reader.GetRepoMap(limit: 5, pathPatterns: new[] { path });

        Assert.Contains(map.Entrypoints, item => item.Kind == "function" && item.Name == "Execute" && item.Path == path);
    }

    [Fact]
    public void GetRepoMap_KeepsScopedFreshnessAndAddsWorkspaceFreshness()
    {
        InsertIndexedFile("src/Program.cs", "csharp", "public class Program\n{\n    public static void Main(string[] args)\n    {\n    }\n}\n",
            modified: new DateTime(2025, 6, 2, 0, 0, 0, DateTimeKind.Utc));
        InsertIndexedFile("docs/guide.md", "markdown", "# Guide\n",
            modified: new DateTime(2025, 6, 3, 0, 0, 0, DateTimeKind.Utc));

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            UPDATE files
            SET indexed_at = CASE path
                WHEN 'src/auth.py' THEN '2025-06-01 00:00:00'
                WHEN 'src/api.js' THEN '2025-06-01 00:00:00'
                WHEN 'src/Program.cs' THEN '2025-06-02 00:00:00'
                WHEN 'docs/guide.md' THEN '2025-06-04 00:00:00'
                WHEN 'docs/notes.md' THEN '2025-06-04 00:00:00'
                ELSE indexed_at
            END
            WHERE path IN ('src/auth.py', 'src/api.js', 'src/Program.cs', 'docs/guide.md', 'docs/notes.md')
            """;
        cmd.ExecuteNonQuery();

        var map = _reader.GetRepoMap(limit: 5, pathPatterns: new[] { "src/Program.cs" });

        Assert.Equal(new DateTime(2025, 6, 2, 0, 0, 0, DateTimeKind.Utc), map.IndexedAt);
        Assert.Equal(new DateTime(2025, 6, 2, 0, 0, 0, DateTimeKind.Utc), map.LatestModified);
        Assert.Equal(new DateTime(2025, 6, 4, 0, 0, 0, DateTimeKind.Utc), map.WorkspaceIndexedAt);
        Assert.Equal(new DateTime(2025, 6, 3, 0, 0, 0, DateTimeKind.Utc), map.WorkspaceLatestModified);
    }

    [Fact]
    public void GetRepoMap_TreatsStoredTimestampsAsUtc_NotLocalRelabelled()
    {
        // Issue #1545: timestamps stored in SQLite (whether offsetless or with an explicit
        // offset) must round-trip to a single canonical UTC instant. Previously the offset-
        // bearing string was first converted to local time by DateTime.TryParse and then
        // re-stamped as UTC, drifting freshness by the caller's local TZ offset.
        // Issue #1545: SQLite に保存された日時（オフセット有無問わず）は同一の UTC 時点へ
        // ラウンドトリップする必要がある。旧実装は DateTime.TryParse が一旦ローカルへ変換し、
        // SpecifyKind(Utc) で再ラベルしていたため、呼び出し側のローカル TZ ぶんずれていた。
        InsertIndexedFile("src/Program.cs", "csharp", "public class Program {}\n",
            modified: new DateTime(2025, 6, 2, 0, 0, 0, DateTimeKind.Utc));

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE files SET indexed_at = @ts WHERE path = 'src/Program.cs'";
        // Offset-bearing literal: 2025-06-04T15:00:00+09:00 == 2025-06-04T06:00:00Z /
        // オフセット付き値: 2025-06-04T15:00:00+09:00 == 2025-06-04T06:00:00Z
        cmd.Parameters.AddWithValue("@ts", "2025-06-04T15:00:00+09:00");
        cmd.ExecuteNonQuery();

        var file = _reader.GetFileByPath("src/Program.cs");
        Assert.NotNull(file);
        Assert.NotNull(file!.IndexedAt);
        Assert.Equal(new DateTime(2025, 6, 4, 6, 0, 0, DateTimeKind.Utc), file.IndexedAt!.Value);
        Assert.Equal(DateTimeKind.Utc, file.IndexedAt!.Value.Kind);
    }

    [Fact]
    public void GetFileByPath_ReturnsExactMatchWithFullMetadata()
    {
        // Seed data: src/api.js — Size=800, Lines=50, Modified=2025-06-01, 2 symbols (ApiClient, fetchData)
        // シードデータ: src/api.js — Size=800, Lines=50, Modified=2025-06-01, シンボル2個
        var file = _reader.GetFileByPath("src/api.js");
        Assert.NotNull(file);
        Assert.Equal("src/api.js", file!.Path);
        Assert.Equal("javascript", file.Lang);
        Assert.Equal(800, file.Size);
        Assert.Equal(50, file.Lines);
        Assert.Equal(2, file.SymbolCount);
        Assert.Equal(new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc), file.Modified);
        Assert.NotNull(file.IndexedAt);

        // Substring or partial path must return null / 部分一致は null を返す
        Assert.Null(_reader.GetFileByPath("api.js"));
        Assert.Null(_reader.GetFileByPath("api"));
        Assert.Null(_reader.GetFileByPath("src/api"));
        Assert.Null(_reader.GetFileByPath("nonexistent.py"));
    }

    [Fact]
    public void AnalyzeSymbol_BundlesDefinitionGraphAndNearbyContext()
    {
        var analysis = _reader.AnalyzeSymbol("fetchData", limit: 5, lang: "javascript", includeBody: true);

        var definition = Assert.Single(analysis.Definitions);
        Assert.Equal("fetchData", definition.Name);
        Assert.NotNull(analysis.File);
        Assert.Equal("src/api.js", analysis.File!.Path);
        Assert.NotNull(analysis.WorkspaceIndexedAt);
        Assert.Equal(new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc), analysis.WorkspaceLatestModified);
        Assert.Equal("javascript", analysis.GraphLanguage);
        Assert.True(analysis.GraphSupported);
        Assert.Contains("indexed", analysis.GraphSupportReason);
        Assert.Contains(analysis.NearbySymbols, item => item.Name == "ApiClient");
        Assert.Contains(analysis.Callees, item => item.CalleeName == "fetch");
    }

    [Fact]
    public void AnalyzeSymbol_PrefersExactDefinitionAsPrimaryAnchorWhenSubstringMatchesOverlap()
    {
        InsertIndexedFile("src/Services/ILoggerService.cs", "csharp",
            """
            public interface ILoggerService
            {
                void Log(string message);
            }
            """);
        InsertIndexedFile("src/Services/LoggerService.cs", "csharp",
            """
            public class LoggerService : ILoggerService
            {
                public void Log(string message) { }
                public void Execute() { }
            }
            """);

        var analysis = _reader.AnalyzeSymbol("loggerservice", limit: 1, lang: "csharp");

        Assert.NotNull(analysis.File);
        Assert.Equal("src/Services/LoggerService.cs", analysis.File!.Path);
        var definition = Assert.Single(analysis.Definitions);
        Assert.Equal("LoggerService", definition.Name);
        Assert.Equal("src/Services/LoggerService.cs", definition.Path);
        Assert.All(analysis.NearbySymbols, item => Assert.Equal("src/Services/LoggerService.cs", item.Path));
    }

    [Fact]
    public void AnalyzeSymbol_NonExactDoesNotUseFoldOnlyExactAnchor()
    {
        InsertIndexedFile("src/Intl/FullwidthRun.cs", "csharp",
            """
            public class Holder
            {
                public void Ｒｕｎ() { }
            }
            """);

        var analysis = _reader.AnalyzeSymbol("Run", limit: 1, lang: "csharp", exact: false);

        Assert.Null(analysis.File);
        Assert.Empty(analysis.Definitions);
        Assert.Empty(analysis.NearbySymbols);
        Assert.Null(analysis.ExactIndexAvailable);
        Assert.Null(analysis.DegradedReason);
    }

    [Fact]
    public void AnalyzeSymbol_UnsupportedLanguage_ReportsGraphSupportMetadata()
    {
        var analysis = _reader.AnalyzeSymbol("Heading", limit: 5, lang: "toml");

        Assert.Equal("toml", analysis.GraphLanguage);
        Assert.False(analysis.GraphSupported);
        Assert.Contains("not indexed", analysis.GraphSupportReason);
        Assert.Empty(analysis.Definitions);
        Assert.Empty(analysis.References);
        Assert.Empty(analysis.Callers);
        Assert.Empty(analysis.Callees);
    }

    // --- Cancellation plumbing (#1567) / キャンセル伝搬テスト ---

    [Fact]
    public void Constructor_DefaultOverload_LeavesCancellationNone()
    {
        // The two-argument constructor is the historical surface kept for callers that don't
        // need request cancellation. It must continue to expose a no-op token so existing
        // sites (CLI runners, tests) keep working unchanged (#1567).
        // 既存の 2 引数コンストラクタは cancellation 不要な呼び出し元向けに残してあり、
        // 互換のため CancellationToken.None を保持する (#1567)。
        var reader = new DbReader(_db.Connection);
        Assert.False(reader.Cancellation.CanBeCanceled);
    }

    [Fact]
    public void Constructor_ExplicitToken_PropagatedThroughHelpers()
    {
        using var cts = new CancellationTokenSource();
        var reader = new DbReader(_db.Connection, isReadOnly: false, cts.Token);
        Assert.True(reader.Cancellation.CanBeCanceled);

        cts.Cancel();
        Assert.True(reader.Cancellation.IsCancellationRequested);
        Assert.Throws<OperationCanceledException>(() => reader.ThrowIfCancellationRequested());
    }

    public void Dispose()
    {
        _db.Dispose();
        DeleteDbPath();
    }

    private void DeleteDbPath()
    {
        if (!File.Exists(_dbPath))
            return;

        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }
        catch (UnauthorizedAccessException)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }
    }

    // --- Outline tests / アウトラインテスト ---

    [Fact]
    public void GetOutline_ReturnsSymbolsOrderedByLine()
    {
        var outline = _reader.GetOutline("src/auth.py");

        Assert.NotNull(outline);
        Assert.Equal("src/auth.py", outline!.Path);
        Assert.Equal("python", outline.Lang);
        Assert.True(outline.SymbolCount > 0);
        Assert.True(outline.TotalLines > 0);

        // Symbols should be ordered by line / シンボルは行順であるべき
        for (int i = 1; i < outline.Symbols.Count; i++)
            Assert.True(outline.Symbols[i].Line >= outline.Symbols[i - 1].Line,
                $"Symbol at index {i} (line {outline.Symbols[i].Line}) should be >= previous (line {outline.Symbols[i - 1].Line})");
    }

    [Fact]
    public void GetOutline_SameLineSymbols_UsesStableTieBreakers()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/same-line.cs",
            Lang = "csharp",
            Size = 45,
            Lines = 1,
            Modified = new DateTime(2025, 6, 2, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks([new ChunkRecord
        {
            FileId = fileId,
            ChunkIndex = 0,
            StartLine = 1,
            EndLine = 1,
            Content = "public class First { } public class Second { }",
        }]);
        _writer.InsertSymbols([
            new SymbolRecord { FileId = fileId, Kind = "property", Name = "Zoo", Line = 1, StartLine = 1, EndLine = 1 },
            new SymbolRecord { FileId = fileId, Kind = "class", Name = "Second", Line = 1, StartLine = 1, StartColumn = 23, EndLine = 1 },
            new SymbolRecord { FileId = fileId, Kind = "property", Name = "Alpha", Line = 1, StartLine = 1, EndLine = 1 },
            new SymbolRecord { FileId = fileId, Kind = "class", Name = "First", Line = 1, StartLine = 1, StartColumn = 7, EndLine = 1 },
        ]);

        var outline = _reader.GetOutline("src/same-line.cs");

        Assert.NotNull(outline);
        Assert.Equal(["First", "Second", "Alpha", "Zoo"], outline!.Symbols.Select(symbol => symbol.Name));
    }

    [Fact]
    public void GetOutline_ComputesContainerDepthFromSymbolChain()
    {
        InsertIndexedFile(
            "src/deep.cs",
            "csharp",
            """
            namespace OuterNs
            {
                namespace InnerNs
                {
                    public class OuterClass
                    {
                        public class NestedClass
                        {
                            public class DeeplyNested
                            {
                                public void Method() { }
                            }
                        }
                    }
                }
            }
            """);

        var outline = _reader.GetOutline("src/deep.cs");

        Assert.NotNull(outline);
        Assert.Equal(6, outline!.Symbols.Count);
        Assert.Collection(outline.Symbols,
            symbol =>
            {
                Assert.Equal("OuterNs", symbol.Name);
                Assert.Equal(0, symbol.Depth);
            },
            symbol =>
            {
                Assert.Equal("InnerNs", symbol.Name);
                Assert.Equal(1, symbol.Depth);
            },
            symbol =>
            {
                Assert.Equal("OuterClass", symbol.Name);
                Assert.Equal(2, symbol.Depth);
            },
            symbol =>
            {
                Assert.Equal("NestedClass", symbol.Name);
                Assert.Equal(3, symbol.Depth);
            },
            symbol =>
            {
                Assert.Equal("DeeplyNested", symbol.Name);
                Assert.Equal(4, symbol.Depth);
            },
            symbol =>
            {
                Assert.Equal("Method", symbol.Name);
                Assert.Equal(5, symbol.Depth);
            });
    }

    [Fact]
    public void GetOutline_UsesQualifiedContainerPathForAmbiguousNames()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/ambiguous.cs",
            Lang = "csharp",
            Size = 300,
            Lines = 20,
            Modified = new DateTime(2025, 6, 2, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks([new ChunkRecord
        {
            FileId = fileId,
            ChunkIndex = 0,
            StartLine = 1,
            EndLine = 20,
            Content = """
            class A { class Wrapper { } }
            class B { class Wrapper { void Target() { } } }
            """,
        }]);
        _writer.InsertSymbols([
            new SymbolRecord { FileId = fileId, Kind = "class", Name = "A", Line = 1, StartLine = 1, EndLine = 5, BodyStartLine = 1, BodyEndLine = 5, ContainerQualifiedName = null },
            new SymbolRecord { FileId = fileId, Kind = "class", Name = "Wrapper", Line = 2, StartLine = 2, EndLine = 4, BodyStartLine = 2, BodyEndLine = 4, ContainerKind = "class", ContainerName = "A", ContainerQualifiedName = "A" },
            new SymbolRecord { FileId = fileId, Kind = "class", Name = "B", Line = 6, StartLine = 6, EndLine = 15, BodyStartLine = 6, BodyEndLine = 15, ContainerQualifiedName = null },
            new SymbolRecord { FileId = fileId, Kind = "class", Name = "Wrapper", Line = 7, StartLine = 7, EndLine = 14, BodyStartLine = 7, BodyEndLine = 14, ContainerKind = "class", ContainerName = "B", ContainerQualifiedName = "B" },
            new SymbolRecord { FileId = fileId, Kind = "function", Name = "Target", Line = 8, StartLine = 8, EndLine = 8, ContainerKind = "class", ContainerName = "Wrapper", ContainerQualifiedName = "B.Wrapper" },
        ]);

        var outline = _reader.GetOutline("src/ambiguous.cs");

        Assert.NotNull(outline);
        var target = Assert.Single(outline!.Symbols.Where(symbol => symbol.Name == "Target"));
        Assert.Equal("B.Wrapper.Target", target.Path);
        Assert.Equal(2, target.Depth);
    }

    [Fact]
    public void GetOutline_ComputesDepthForFileScopedNamespace()
    {
        InsertIndexedFile(
            "src/file_scoped.cs",
            "csharp",
            """
            namespace FileScoped;

            public class OuterClass
            {
                public class NestedClass
                {
                    public void Method() { }
                }
            }
            """);

        var outline = _reader.GetOutline("src/file_scoped.cs");

        Assert.NotNull(outline);
        Assert.Equal(4, outline!.Symbols.Count);
        Assert.Collection(outline.Symbols,
            symbol =>
            {
                Assert.Equal("FileScoped", symbol.Name);
                Assert.Equal(0, symbol.Depth);
            },
            symbol =>
            {
                Assert.Equal("OuterClass", symbol.Name);
                Assert.Equal(1, symbol.Depth);
            },
            symbol =>
            {
                Assert.Equal("NestedClass", symbol.Name);
                Assert.Equal(2, symbol.Depth);
            },
            symbol =>
            {
                Assert.Equal("Method", symbol.Name);
                Assert.Equal(3, symbol.Depth);
            });
    }

    [Fact]
    public void GetOutline_AddsDisplayNamesForCSharpOverloads()
    {
        InsertIndexedFile(
            "src/worker.cs",
            "csharp",
            """
            using System.Threading;

            public class Worker
            {
                public void Process(string input) { }
                public void Process(int count, CancellationToken cancellationToken = default) { }
            }
            """);

        var outline = _reader.GetOutline("src/worker.cs");

        Assert.NotNull(outline);
        var overloads = outline!.Symbols
            .Where(symbol => symbol.Name == "Process")
            .OrderBy(symbol => symbol.Line)
            .ToList();
        Assert.Equal(2, overloads.Count);
        Assert.Equal("Process(string)", overloads[0].DisplayName);
        Assert.Equal("Worker.Process", overloads[0].Path);
        Assert.Equal("Process(int, CancellationToken)", overloads[1].DisplayName);
        Assert.Equal("Worker.Process", overloads[1].Path);
    }

    [Fact]
    public void GetOutline_PathFallsBackToContainerNameWhenQualifiedContainerIsUnavailable()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/legacy-container.cs",
            Lang = "csharp",
            Size = 64,
            Lines = 3,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks([new ChunkRecord
        {
            FileId = fileId,
            ChunkIndex = 0,
            StartLine = 1,
            EndLine = 3,
            Content = "class Worker { void Process(int count) { } }",
        }]);
        _writer.InsertSymbols([
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "Worker",
                Line = 1,
                StartLine = 1,
                EndLine = 3,
                Signature = "class Worker",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "Process",
                Line = 2,
                StartLine = 2,
                EndLine = 2,
                Signature = "void Process(int count)",
                ContainerKind = "class",
                ContainerName = "Worker",
            }
        ]);

        var outline = _reader.GetOutline("src/legacy-container.cs");

        Assert.NotNull(outline);
        var method = Assert.Single(outline!.Symbols.Where(symbol => symbol.Name == "Process"));
        Assert.Equal("Worker.Process", method.Path);
        Assert.Equal("Process(int)", method.DisplayName);
    }

    [Fact]
    public void GetOutline_AddsPathsForPythonShadowedMethods()
    {
        InsertIndexedFile(
            "src/shadow.py",
            "python",
            """
            class Alpha:
                def run(self, value: int):
                    return value

            class Beta:
                def run(self, value: str):
                    return value
            """);

        var outline = _reader.GetOutline("src/shadow.py");

        Assert.NotNull(outline);
        var methods = outline!.Symbols
            .Where(symbol => symbol.Name == "run")
            .OrderBy(symbol => symbol.Line)
            .ToList();
        Assert.Equal(2, methods.Count);
        Assert.Equal("run(int)", methods[0].DisplayName);
        Assert.Equal("Alpha.run", methods[0].Path);
        Assert.Equal("run(str)", methods[1].DisplayName);
        Assert.Equal("Beta.run", methods[1].Path);
    }

    [Fact]
    public void GetOutline_AddsDisplayNameForGoReceiverMethod()
    {
        InsertIndexedFile(
            "cmd/app/main.go",
            "go",
            """
            package main

            import "context"

            type Service struct{}

            func (s *Service) Process(ctx context.Context, id int) error {
                return nil
            }
            """);

        var outline = _reader.GetOutline("cmd/app/main.go");

        Assert.NotNull(outline);
        var method = Assert.Single(outline!.Symbols.Where(symbol => symbol.Name == "Process"));
        Assert.Equal("Process(context.Context, int)", method.DisplayName);
    }

    [Fact]
    public void GetOutline_AddsPathsForTypeScriptNamespaceShadowedFunctions()
    {
        InsertIndexedFile(
            "src/shadow.ts",
            "typescript",
            """
            namespace First {
              export function make(value: string) {
                return value;
              }
            }

            namespace Second {
              export function make(value: number) {
                return value;
              }
            }
            """);

        var outline = _reader.GetOutline("src/shadow.ts");

        Assert.NotNull(outline);
        var functions = outline!.Symbols
            .Where(symbol => symbol.Name == "make")
            .OrderBy(symbol => symbol.Line)
            .ToList();
        Assert.Equal(2, functions.Count);
        Assert.Equal("make(string)", functions[0].DisplayName);
        Assert.Equal("First.make", functions[0].Path);
        Assert.Equal("make(number)", functions[1].DisplayName);
        Assert.Equal("Second.make", functions[1].Path);
    }

    [Fact]
    public void GetOutline_FileWithNoSymbols_ReturnsEmptyList()
    {
        var outline = _reader.GetOutline("docs/notes.md");

        Assert.NotNull(outline);
        Assert.Equal("docs/notes.md", outline!.Path);
        Assert.Equal(0, outline.SymbolCount);
        Assert.Empty(outline.Symbols);
    }

    [Fact]
    public void GetOutline_MarkdownHeadings_ReturnNestedHeadingSymbols()
    {
        InsertIndexedFile(
            "docs/guide.md",
            "markdown",
            """
            # Guide

            Intro text.

            ## Details

            ```markdown
            # Not a heading
            ```

            ### Deep Dive

            # Appendix
            """);

        var outline = _reader.GetOutline("docs/guide.md");

        Assert.NotNull(outline);
        Assert.Equal("docs/guide.md", outline!.Path);
        Assert.Equal(5, outline.SymbolCount);
        Assert.Collection(outline.Symbols,
            symbol =>
            {
                Assert.Equal("Guide", symbol.Name);
                Assert.Equal(0, symbol.Depth);
            },
            symbol =>
            {
                Assert.Equal("Details", symbol.Name);
                Assert.Equal(1, symbol.Depth);
            },
            symbol =>
            {
                Assert.Equal("markdown", symbol.Name);
                Assert.Equal("code", symbol.Kind);
                Assert.Equal(2, symbol.Depth);
            },
            symbol =>
            {
                Assert.Equal("Deep Dive", symbol.Name);
                Assert.Equal(2, symbol.Depth);
            },
            symbol =>
            {
                Assert.Equal("Appendix", symbol.Name);
                Assert.Equal(0, symbol.Depth);
            });
    }

    [Fact]
    public void GetOutline_MarkdownSetextHeadings_ReturnNestedHeadingSymbols()
    {
        InsertIndexedFile(
            "docs/setext.md",
            "markdown",
            """
            Guide
            =====

            Details
            -------

            ### Deep Dive

            Appendix
            ========
            """);

        var outline = _reader.GetOutline("docs/setext.md");

        Assert.NotNull(outline);
        Assert.Equal("docs/setext.md", outline!.Path);
        Assert.Equal(4, outline.SymbolCount);
        Assert.Collection(outline.Symbols,
            symbol =>
            {
                Assert.Equal("Guide", symbol.Name);
                Assert.Equal(0, symbol.Depth);
            },
            symbol =>
            {
                Assert.Equal("Details", symbol.Name);
                Assert.Equal(1, symbol.Depth);
            },
            symbol =>
            {
                Assert.Equal("Deep Dive", symbol.Name);
                Assert.Equal(2, symbol.Depth);
            },
            symbol =>
            {
                Assert.Equal("Appendix", symbol.Name);
                Assert.Equal(0, symbol.Depth);
            });
    }

    [Fact]
    public void GetOutline_NonexistentFile_ReturnsNull()
    {
        var outline = _reader.GetOutline("nonexistent/file.cs");

        Assert.Null(outline);
    }

    [Fact]
    public void GetExcerptAndOutline_RoundTripPathContainingBackslash()
    {
        // #191: POSIX filenames containing '\' must not be silently rewritten to '/'.
        // The index should store the literal path, and excerpt/outline must find it
        // when the user supplies the same literal path.
        // #191: POSIX の '\' を含むファイル名は '/' に書き換えてはいけない。
        // 保存と検索の両方でリテラルなパスをそのまま使い、excerpt/outline で見つかることを確認する。
        InsertIndexedFile("back\\slash.py", "python", "def hu(): pass\n");

        var excerpt = _reader.GetExcerpt("back\\slash.py", 1, 1);
        Assert.NotNull(excerpt);
        Assert.Equal("back\\slash.py", excerpt!.Path);
        Assert.Contains("def hu(): pass", excerpt.Content);

        var outline = _reader.GetOutline("back\\slash.py");
        Assert.NotNull(outline);
        Assert.Equal("back\\slash.py", outline!.Path);

        // The mangled form must NOT match — otherwise the fix would be a no-op.
        // 誤った書き換え形では見つからないことを確認する（no-op 化の検出）。
        Assert.Null(_reader.GetExcerpt("back/slash.py", 1, 1));
        Assert.Null(_reader.GetOutline("back/slash.py"));
    }

    [Fact]
    public void GetOutline_NullStartEndLine_FallsBackToLine()
    {
        // Insert a file with a symbol that has NULL start_line/end_line (#46)
        // start_line/end_lineがNULLのシンボルを持つファイルを挿入（#46）
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/nullcol.cs",
            Lang = "csharp",
            Size = 100,
            Lines = 10,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks([new ChunkRecord
        {
            FileId = fileId, ChunkIndex = 0, StartLine = 1, EndLine = 10,
            Content = "class Foo { void Bar() {} }",
        }]);
        // Insert symbol with NULL start_line and end_line via raw SQL /
        // start_lineとend_lineがNULLのシンボルを生SQLで挿入
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = @"INSERT INTO symbols (file_id, kind, name, line, start_line, end_line)
                            VALUES (@fid, 'function', 'Bar', 5, NULL, NULL)";
        cmd.Parameters.AddWithValue("@fid", fileId);
        cmd.ExecuteNonQuery();

        var outline = _reader.GetOutline("src/nullcol.cs");

        Assert.NotNull(outline);
        var sym = Assert.Single(outline!.Symbols);
        Assert.Equal("Bar", sym.Name);
        Assert.Equal(5, sym.Line);
        // Falls back to line value when start_line/end_line are NULL / NULLの場合lineにフォールバック
        Assert.Equal(5, sym.StartLine);
        Assert.Equal(5, sym.EndLine);
    }


















    [Fact]
    public void GetUnusedSymbols_InlineRawInterpolatedAttributeWithBracketInString_DoesNotLeakToAdjacentProperty()
    {
        // Regression extension for #375 — raw-interpolated string literals
        // (`$"""..."""`) inside an inline attribute must not escape bracket-depth
        // sanitization either, or the adjacent plain property re-inherits the
        // reflection attribute context.
        // #375 の追加回帰: raw 補間文字列 (`$"""..."""`) を含むインライン属性でも、
        // 直下の属性なしプロパティに reflection 属性コンテキストが漏れ出さないこと。
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_raw_interpolated_fixture.cs",
            Lang = "csharp",
            Size = 340,
            Lines = 12,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 10,
                Content = "using System.Text.Json.Serialization;\n\npublic class Target\n{\n    [JsonPropertyName($\"\"\"a[\"\"\")] public string BuggyName { get; set; } = \"\";\n\n    public string PlainName { get; set; } = \"\";\n}\n",
            }
        ]);
        _writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "Target",
                Line = 3,
                StartLine = 3,
                EndLine = 8,
                Signature = "public class Target",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "BuggyName",
                Line = 5,
                StartLine = 5,
                EndLine = 5,
                Signature = "[JsonPropertyName($\"\"\"a[\"\"\")] public string BuggyName { get; set; } = \"\";",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "Target",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "PlainName",
                Line = 7,
                StartLine = 7,
                EndLine = 7,
                Signature = "public string PlainName { get; set; } = \"\";",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "Target",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: null, lang: "csharp",
            pathPatterns: ["reflection_raw_interpolated_fixture.cs"], excludePathPatterns: null, excludeTests: false);

        var buggy = Assert.Single(unused, symbol => symbol.Name == "BuggyName");
        Assert.Equal("reflection_or_config_suspect", buggy.UnusedBucket);

        var plain = Assert.Single(unused, symbol => symbol.Name == "PlainName");
        Assert.Equal("public_or_exported_no_refs", plain.UnusedBucket);
    }

    [Theory]
    [InlineData("verbatim_standalone", "[JsonPropertyName(@\"a[\n]\")]\n    public string A { get; set; } = \"\";", 7, 9)]
    [InlineData("raw_standalone", "[JsonPropertyName(\"\"\"a[\n]\"\"\")]\n    public string A { get; set; } = \"\";", 7, 9)]
    [InlineData("raw_interp_standalone", "[JsonPropertyName($\"\"\"a[\n]\"\"\")]\n    public string A { get; set; } = \"\";", 7, 9)]
    [InlineData("raw_interp_double_dollar_standalone", "[JsonPropertyName($$\"\"\"a[\n]\"\"\")]\n    public string A { get; set; } = \"\";", 7, 9)]
    [InlineData("verbatim_inline_close", "[JsonPropertyName(@\"a[\n]\")] public string A { get; set; } = \"\";", 6, 8)]
    [InlineData("raw_inline_close", "[JsonPropertyName(\"\"\"a[\n]\"\"\")] public string A { get; set; } = \"\";", 6, 8)]
    [InlineData("raw_interp_inline_close", "[JsonPropertyName($\"\"\"a[\n]\"\"\")] public string A { get; set; } = \"\";", 6, 8)]
    // Interpolation-hole cases (#409 follow-up, iteration 4): the sanitizer must
    // not let quotes / triple-quote runs inside an interpolation hole prematurely
    // close the outer interpolated string, which would leak the hole's inner
    // string content as phantom attribute text (e.g. a fake `[JsonIgnore]`).
    // 補間ホール内の `"` / `"""` 連続が外側の補間文字列を早期終了させて、
    // ホール内の文字列内容が擬似 attribute（例: 擬似 `[JsonIgnore]`）として
    // 漏れないことを検証する (#409 iteration 4 回帰)。
    [InlineData("verbatim_interp_hole_with_dollar_at", "[JsonPropertyName($@\"{\n\"[JsonIgnore]\"}\")]\n    public string A { get; set; } = \"\";", 7, 9)]
    [InlineData("verbatim_interp_hole_with_at_dollar", "[JsonPropertyName(@$\"{\n\"[JsonIgnore]\"}\")]\n    public string A { get; set; } = \"\";", 7, 9)]
    [InlineData("raw_interp_hole_with_triple_quote_run", "[JsonPropertyName($\"\"\"{\n\"\"\"[JsonIgnore]\"\"\"}\"\"\")]\n    public string A { get; set; } = \"\";", 7, 9)]
    public void GetUnusedSymbols_MultilineAttributeLiteralWithBracketInString_KeepsReflectionContext(string label, string attributeAndDeclaration, int aLine, int bLine)
    {
        // Regression for #409 — multi-line verbatim / raw / raw-interpolated string
        // literals in C# attributes with `[` or `]` inside must not cause the property
        // carrying the reflection attribute to fall out of the
        // `reflection_or_config_suspect` bucket. At the same time, the adjacent plain
        // property must not inherit reflection context.
        // #409 回帰: C# 属性内の複数行 verbatim / raw / raw 補間文字列リテラルに `[` / `]` が
        // 含まれても、その属性を持つプロパティが `reflection_or_config_suspect` から
        // 外れてはならない。同時に、直下の属性なしプロパティに reflection コンテキストが
        // 漏れてはならない。
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = $"src/reflection_multiline_attr_fixture_{label}.cs",
            Lang = "csharp",
            Size = 400,
            Lines = 12,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        var content = "using System.Text.Json.Serialization;\n\npublic class Target\n{\n    " + attributeAndDeclaration + "\n\n    public string B { get; set; } = \"\";\n}\n";
        _writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = bLine + 2,
                Content = content,
            }
        ]);
        _writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "Target",
                Line = 3,
                StartLine = 3,
                EndLine = bLine + 1,
                Signature = "public class Target",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "A",
                Line = aLine,
                StartLine = aLine,
                EndLine = aLine,
                Signature = "public string A { get; set; } = \"\";",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "Target",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "B",
                Line = bLine,
                StartLine = bLine,
                EndLine = bLine,
                Signature = "public string B { get; set; } = \"\";",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "Target",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: null, lang: "csharp",
            pathPatterns: [$"reflection_multiline_attr_fixture_{label}.cs"], excludePathPatterns: null, excludeTests: false);

        var a = Assert.Single(unused, symbol => symbol.Name == "A");
        Assert.Equal("reflection_or_config_suspect", a.UnusedBucket);

        var b = Assert.Single(unused, symbol => symbol.Name == "B");
        Assert.Equal("public_or_exported_no_refs", b.UnusedBucket);
    }










    [Fact]
    public void GetUnusedSymbols_CSharpEnumMembersAreIncludedWhenUnreferenced()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/unused_enum_members_fixture.cs",
            Lang = "csharp",
            Size = 180,
            Lines = 8,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "enum",
                Name = "Color",
                Line = 1,
                StartLine = 1,
                EndLine = 4,
                Signature = "public enum Color",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "enum",
                Name = "Red",
                Line = 3,
                StartLine = 3,
                EndLine = 3,
                Signature = "Red,",
                ContainerKind = "enum",
                ContainerName = "Color",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "enum",
                Name = "Blue",
                Line = 4,
                StartLine = 4,
                EndLine = 4,
                Signature = "Blue",
                ContainerKind = "enum",
                ContainerName = "Color",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "enum",
                Name = "TrulyUnused",
                Line = 6,
                StartLine = 6,
                EndLine = 8,
                Signature = "public enum TrulyUnused",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "enum",
                Name = "Green",
                Line = 8,
                StartLine = 8,
                EndLine = 8,
                Signature = "Green",
                ContainerKind = "enum",
                ContainerName = "TrulyUnused",
            },
        ]);
        _writer.InsertReferences(
        [
            new ReferenceRecord
            {
                FileId = fileId,
                SymbolName = "Color",
                ReferenceKind = "type_reference",
                Line = 10,
                Column = 12,
                Context = "public Color Shade => Color.Red;",
            },
            new ReferenceRecord
            {
                FileId = fileId,
                SymbolName = "Red",
                ReferenceKind = "call",
                Line = 10,
                Column = 30,
                Context = "public Color Shade => Color.Red;",
            },
            new ReferenceRecord
            {
                FileId = fileId,
                SymbolName = "Blue",
                ReferenceKind = "call",
                Line = 11,
                Column = 30,
                Context = "public Color Next => Color.Blue;",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: "enum", lang: "csharp",
            pathPatterns: ["unused_enum_members_fixture.cs"], excludePathPatterns: null, excludeTests: false);
        var count = _reader.CountUnusedSymbols(kind: "enum", lang: "csharp",
            pathPatterns: ["unused_enum_members_fixture.cs"], excludePathPatterns: null, excludeTests: false);

        Assert.Contains(unused, symbol => symbol.Name == "TrulyUnused");
        Assert.Contains(unused, symbol => symbol.Name == "Green");
        Assert.DoesNotContain(unused, symbol => symbol.Name == "Color");
        Assert.DoesNotContain(unused, symbol => symbol.Name == "Red");
        Assert.DoesNotContain(unused, symbol => symbol.Name == "Blue");
        Assert.Equal(2, count.Count);
        Assert.Equal(1, count.FileCount);
    }

    [Fact]
    public void GetUnusedSymbols_CSharpEnumMemberNameCollisionsStayConservative()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/unused_enum_collision_fixture.cs",
            Lang = "csharp",
            Size = 240,
            Lines = 18,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "enum",
                Name = "Color",
                Line = 1,
                StartLine = 1,
                EndLine = 4,
                Signature = "public enum Color",
                Visibility = "public",
                ContainerKind = "namespace",
                ContainerName = "Demo",
                ContainerQualifiedName = "Demo.Color",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "enum",
                Name = "None",
                Line = 3,
                StartLine = 3,
                EndLine = 3,
                Signature = "None,",
                ContainerKind = "enum",
                ContainerName = "Color",
                ContainerQualifiedName = "Demo.Color",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "enum",
                Name = "Red",
                Line = 4,
                StartLine = 4,
                EndLine = 4,
                Signature = "Red",
                ContainerKind = "enum",
                ContainerName = "Color",
                ContainerQualifiedName = "Demo.Color",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "enum",
                Name = "Status",
                Line = 6,
                StartLine = 6,
                EndLine = 9,
                Signature = "public enum Status",
                Visibility = "public",
                ContainerKind = "namespace",
                ContainerName = "Demo",
                ContainerQualifiedName = "Demo.Status",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "enum",
                Name = "None",
                Line = 8,
                StartLine = 8,
                EndLine = 8,
                Signature = "None,",
                ContainerKind = "enum",
                ContainerName = "Status",
                ContainerQualifiedName = "Demo.Status",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "enum",
                Name = "Started",
                Line = 9,
                StartLine = 9,
                EndLine = 9,
                Signature = "Started",
                ContainerKind = "enum",
                ContainerName = "Status",
                ContainerQualifiedName = "Demo.Status",
            },
        ]);
        var unused = _reader.GetUnusedSymbols(limit: 10, kind: "enum", lang: "csharp",
            pathPatterns: ["unused_enum_collision_fixture.cs"], excludePathPatterns: null, excludeTests: false);
        var count = _reader.CountUnusedSymbols(kind: "enum", lang: "csharp",
            pathPatterns: ["unused_enum_collision_fixture.cs"], excludePathPatterns: null, excludeTests: false);

        Assert.DoesNotContain(unused, symbol => symbol.Name == "None");
        Assert.Contains(unused, symbol => symbol.Name == "Red");
        Assert.Contains(unused, symbol => symbol.Name == "Status");
        Assert.Contains(unused, symbol => symbol.Name == "Started");
        Assert.Equal(4, count.Count);
        Assert.Equal(1, count.FileCount);
    }

    [Fact]
    public void GetUnusedSymbols_CSharpEnumMemberCollisionsRespectPathScope()
    {
        var srcFileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/active.cs",
            Lang = "csharp",
            Size = 140,
            Lines = 8,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        var testFileId = _writer.UpsertFile(new FileRecord
        {
            Path = "tests/peer.cs",
            Lang = "csharp",
            Size = 140,
            Lines = 8,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = srcFileId,
                Kind = "enum",
                Name = "Color",
                Line = 1,
                StartLine = 1,
                EndLine = 4,
                Signature = "public enum Color",
                Visibility = "public",
                ContainerKind = "namespace",
                ContainerName = "Demo",
                ContainerQualifiedName = "Demo.Color",
            },
            new SymbolRecord
            {
                FileId = srcFileId,
                Kind = "enum",
                Name = "None",
                Line = 3,
                StartLine = 3,
                EndLine = 3,
                Signature = "None,",
                ContainerKind = "enum",
                ContainerName = "Color",
                ContainerQualifiedName = "Demo.Color",
            },
            new SymbolRecord
            {
                FileId = srcFileId,
                Kind = "enum",
                Name = "Red",
                Line = 4,
                StartLine = 4,
                EndLine = 4,
                Signature = "Red",
                ContainerKind = "enum",
                ContainerName = "Color",
                ContainerQualifiedName = "Demo.Color",
            },
            new SymbolRecord
            {
                FileId = testFileId,
                Kind = "enum",
                Name = "Status",
                Line = 1,
                StartLine = 1,
                EndLine = 4,
                Signature = "public enum Status",
                Visibility = "public",
                ContainerKind = "namespace",
                ContainerName = "Demo",
                ContainerQualifiedName = "Demo.Status",
            },
            new SymbolRecord
            {
                FileId = testFileId,
                Kind = "enum",
                Name = "None",
                Line = 3,
                StartLine = 3,
                EndLine = 3,
                Signature = "None,",
                ContainerKind = "enum",
                ContainerName = "Status",
                ContainerQualifiedName = "Demo.Status",
            },
            new SymbolRecord
            {
                FileId = testFileId,
                Kind = "enum",
                Name = "Stopped",
                Line = 4,
                StartLine = 4,
                EndLine = 4,
                Signature = "Stopped",
                ContainerKind = "enum",
                ContainerName = "Status",
                ContainerQualifiedName = "Demo.Status",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: "enum", lang: "csharp",
            pathPatterns: ["src/"], excludePathPatterns: null, excludeTests: false);
        var count = _reader.CountUnusedSymbols(kind: "enum", lang: "csharp",
            pathPatterns: ["src/"], excludePathPatterns: null, excludeTests: false);

        Assert.Contains(unused, symbol => symbol.Name == "None");
        Assert.Contains(unused, symbol => symbol.Name == "Red");
        Assert.Contains(unused, symbol => symbol.Name == "Color");
        Assert.DoesNotContain(unused, symbol => symbol.Path.StartsWith("tests/", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, count.Count);
        Assert.Equal(1, count.FileCount);
    }

    [Fact]
    public void GetUnusedSymbols_CSharpEnumMemberCollisionsRespectExcludeTestsScope()
    {
        var srcFileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/active.cs",
            Lang = "csharp",
            Size = 140,
            Lines = 8,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        var testFileId = _writer.UpsertFile(new FileRecord
        {
            Path = "tests/peer.cs",
            Lang = "csharp",
            Size = 140,
            Lines = 8,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = srcFileId,
                Kind = "enum",
                Name = "Color",
                Line = 1,
                StartLine = 1,
                EndLine = 4,
                Signature = "public enum Color",
                Visibility = "public",
                ContainerKind = "namespace",
                ContainerName = "Demo",
                ContainerQualifiedName = "Demo.Color",
            },
            new SymbolRecord
            {
                FileId = srcFileId,
                Kind = "enum",
                Name = "None",
                Line = 3,
                StartLine = 3,
                EndLine = 3,
                Signature = "None,",
                ContainerKind = "enum",
                ContainerName = "Color",
                ContainerQualifiedName = "Demo.Color",
            },
            new SymbolRecord
            {
                FileId = srcFileId,
                Kind = "enum",
                Name = "Red",
                Line = 4,
                StartLine = 4,
                EndLine = 4,
                Signature = "Red",
                ContainerKind = "enum",
                ContainerName = "Color",
                ContainerQualifiedName = "Demo.Color",
            },
            new SymbolRecord
            {
                FileId = testFileId,
                Kind = "enum",
                Name = "Status",
                Line = 1,
                StartLine = 1,
                EndLine = 4,
                Signature = "public enum Status",
                Visibility = "public",
                ContainerKind = "namespace",
                ContainerName = "Demo",
                ContainerQualifiedName = "Demo.Status",
            },
            new SymbolRecord
            {
                FileId = testFileId,
                Kind = "enum",
                Name = "None",
                Line = 3,
                StartLine = 3,
                EndLine = 3,
                Signature = "None,",
                ContainerKind = "enum",
                ContainerName = "Status",
                ContainerQualifiedName = "Demo.Status",
            },
            new SymbolRecord
            {
                FileId = testFileId,
                Kind = "enum",
                Name = "Stopped",
                Line = 4,
                StartLine = 4,
                EndLine = 4,
                Signature = "Stopped",
                ContainerKind = "enum",
                ContainerName = "Status",
                ContainerQualifiedName = "Demo.Status",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: "enum", lang: "csharp",
            pathPatterns: null, excludePathPatterns: null, excludeTests: true);
        var count = _reader.CountUnusedSymbols(kind: "enum", lang: "csharp",
            pathPatterns: null, excludePathPatterns: null, excludeTests: true);

        Assert.Contains(unused, symbol => symbol.Name == "None");
        Assert.Contains(unused, symbol => symbol.Name == "Red");
        Assert.Contains(unused, symbol => symbol.Name == "Color");
        Assert.DoesNotContain(unused, symbol => symbol.Path.StartsWith("tests/", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, count.Count);
        Assert.Equal(1, count.FileCount);
    }

    [Fact]
    public void GetUnusedSymbols_IgnoreAttributes_DoNotClassifyAsSuspect()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_ignore_fixture.cs",
            Lang = "csharp",
            Size = 200,
            Lines = 12,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 10,
                Content = """
                using System.Runtime.Serialization;
                using System.Text.Json.Serialization;

                public class LegacyDto
                {
                    [JsonIgnore]
                    public string LegacyField { get; set; } = string.Empty;
                    [IgnoreDataMember]
                    public string LegacyAlias { get; set; } = string.Empty;
                }
                """,
            }
        ]);
        _writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "LegacyDto",
                Line = 4,
                StartLine = 4,
                EndLine = 9,
                Signature = "public class LegacyDto",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "LegacyField",
                Line = 6,
                StartLine = 6,
                EndLine = 6,
                Signature = "public string LegacyField { get; set; } = string.Empty;",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "LegacyDto",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "LegacyAlias",
                Line = 8,
                StartLine = 8,
                EndLine = 8,
                Signature = "public string LegacyAlias { get; set; } = string.Empty;",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "LegacyDto",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: null, lang: "csharp",
            pathPatterns: ["reflection_ignore_fixture.cs"], excludePathPatterns: null, excludeTests: false);

        Assert.Equal("public_or_exported_no_refs", Assert.Single(unused, symbol => symbol.Name == "LegacyField").UnusedBucket);
        Assert.Equal("public_or_exported_no_refs", Assert.Single(unused, symbol => symbol.Name == "LegacyAlias").UnusedBucket);
    }

    [Fact]
    public void GetUnusedSymbols_MissingChunks_DegradesReflectionClassificationWithoutCrashing()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_missing_chunks_fixture.cs",
            Lang = "csharp",
            Size = 200,
            Lines = 10,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 8,
                Content = """
                using System.Text.Json.Serialization;

                public class UserDto
                {
                    [JsonPropertyName("full_name")]
                    public string FullName { get; set; } = string.Empty;
                }
                """,
            }
        ]);
        _writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "UserDto",
                Line = 3,
                StartLine = 3,
                EndLine = 6,
                Signature = "public class UserDto",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "FullName",
                Line = 5,
                StartLine = 5,
                EndLine = 5,
                Signature = "public string FullName { get; set; } = string.Empty;",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
        ]);
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "DROP TABLE chunks;";
            cmd.ExecuteNonQuery();
        }

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: null, lang: "csharp",
            pathPatterns: ["reflection_missing_chunks_fixture.cs"], excludePathPatterns: null, excludeTests: false);

        Assert.Equal("public_or_exported_no_refs", Assert.Single(unused, symbol => symbol.Name == "FullName").UnusedBucket);
    }

    [Fact]
    public void GetUnusedSymbols_AdjacentProperties_DoNotLeakAttributeContext()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_adjacent_fixture.cs",
            Lang = "csharp",
            Size = 400,
            Lines = 16,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 14,
                Content = """
                using System.Runtime.Serialization;
                using System.Text.Json.Serialization;

                public class MixedDto
                {
                    [JsonPropertyName("decorated")]
                    public string Decorated { get; set; } = string.Empty;
                    public string Plain { get; set; } = string.Empty;
                    [JsonIgnore]
                    public string Ignored { get; set; } = string.Empty;
                    [JsonPropertyName("tagged")]
                    public string Tagged { get; set; } = string.Empty;
                }
                """,
            }
        ]);
        _writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "MixedDto",
                Line = 4,
                StartLine = 4,
                EndLine = 11,
                Signature = "public class MixedDto",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "Decorated",
                Line = 7,
                StartLine = 7,
                EndLine = 7,
                Signature = "public string Decorated { get; set; } = string.Empty;",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "MixedDto",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "Plain",
                Line = 8,
                StartLine = 8,
                EndLine = 8,
                Signature = "public string Plain { get; set; } = string.Empty;",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "MixedDto",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "Ignored",
                Line = 10,
                StartLine = 10,
                EndLine = 10,
                Signature = "public string Ignored { get; set; } = string.Empty;",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "MixedDto",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "Tagged",
                Line = 12,
                StartLine = 12,
                EndLine = 12,
                Signature = "public string Tagged { get; set; } = string.Empty;",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "MixedDto",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: null, lang: "csharp",
            pathPatterns: ["reflection_adjacent_fixture.cs"], excludePathPatterns: null, excludeTests: false);

        Assert.Equal("reflection_or_config_suspect", Assert.Single(unused, symbol => symbol.Name == "Decorated").UnusedBucket);
        Assert.Equal("public_or_exported_no_refs", Assert.Single(unused, symbol => symbol.Name == "Plain").UnusedBucket);
        Assert.Equal("public_or_exported_no_refs", Assert.Single(unused, symbol => symbol.Name == "Ignored").UnusedBucket);
        Assert.Equal("reflection_or_config_suspect", Assert.Single(unused, symbol => symbol.Name == "Tagged").UnusedBucket);
    }

    [Fact]
    public void GetUnusedSymbols_SmallLimitDiversifiesAcrossBuckets()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/diversified_unused_fixture.cs",
            Lang = "csharp",
            Size = 200,
            Lines = 20,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 8,
                Content = """
                public class LocalUseFixture
                {
                    public void Run() { HiddenUsed(); }
                    private void HiddenUsed() { }
                    private void HiddenUnused() { }
                    internal void InternalOnly() { }
                }
                """,
            }
        ]);
        _writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "LocalUseFixture",
                Line = 1,
                StartLine = 1,
                EndLine = 5,
                Signature = "public class LocalUseFixture",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "Run",
                Line = 3,
                StartLine = 3,
                EndLine = 3,
                Signature = "public void Run() { Hidden(); }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "LocalUseFixture",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "HiddenUsed",
                Line = 4,
                StartLine = 4,
                EndLine = 4,
                Signature = "private void HiddenUsed() { }",
                Visibility = "private",
                ContainerKind = "class",
                ContainerName = "LocalUseFixture",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "HiddenUnused",
                Line = 5,
                StartLine = 5,
                EndLine = 5,
                Signature = "private void HiddenUnused() { }",
                Visibility = "private",
                ContainerKind = "class",
                ContainerName = "LocalUseFixture",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "InternalOnly",
                Line = 6,
                StartLine = 6,
                EndLine = 6,
                Signature = "internal void InternalOnly() { }",
                Visibility = "internal",
                ContainerKind = "class",
                ContainerName = "LocalUseFixture",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 3, kind: null, lang: "csharp",
            pathPatterns: ["diversified_unused_fixture.cs"], excludePathPatterns: null, excludeTests: false);
        var count = _reader.CountUnusedSymbols(kind: null, lang: "csharp",
            pathPatterns: ["diversified_unused_fixture.cs"], excludePathPatterns: null, excludeTests: false);

        Assert.DoesNotContain(unused, symbol => symbol.Name == "HiddenUsed");
        Assert.Equal(["HiddenUnused", "InternalOnly", "LocalUseFixture"], unused.Select(symbol => symbol.Name).ToArray());
        Assert.Equal(["likely_unused_private", "maybe_unused_nonpublic", "public_or_exported_no_refs"], unused.Select(symbol => symbol.UnusedBucket).ToArray());
        Assert.Equal(4, count.Count);
    }

    [Fact]
    public void GetUnusedSymbols_SmallLimitIncludesReflectionAttributedSuspect()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_diversified_unused_fixture.cs",
            Lang = "csharp",
            Size = 200,
            Lines = 12,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 10,
                Content = """
                using System.Text.Json.Serialization;

                public class UserDto
                {
                    [JsonPropertyName("full_name")]
                    public string FullName { get; set; } = string.Empty;
                    public void Run() { Hidden(); }
                    private void Hidden() { }
                    internal void InternalOnly() { }
                }
                """,
            }
        ]);
        _writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "UserDto",
                Line = 3,
                StartLine = 3,
                EndLine = 8,
                Signature = "public class UserDto",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "FullName",
                Line = 5,
                StartLine = 5,
                EndLine = 5,
                Signature = "public string FullName { get; set; } = string.Empty;",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "Run",
                Line = 6,
                StartLine = 6,
                EndLine = 6,
                Signature = "public void Run() { Hidden(); }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "Hidden",
                Line = 7,
                StartLine = 7,
                EndLine = 7,
                Signature = "private void Hidden() { }",
                Visibility = "private",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "InternalOnly",
                Line = 8,
                StartLine = 8,
                EndLine = 8,
                Signature = "internal void InternalOnly() { }",
                Visibility = "internal",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 4, kind: null, lang: "csharp",
            pathPatterns: ["reflection_diversified_unused_fixture.cs"], excludePathPatterns: null, excludeTests: false);

        Assert.Equal(["InternalOnly", "UserDto", "FullName", "Run"], unused.Select(symbol => symbol.Name).ToArray());
        Assert.Equal(["maybe_unused_nonpublic", "public_or_exported_no_refs", "reflection_or_config_suspect", "public_or_exported_no_refs"], unused.Select(symbol => symbol.UnusedBucket).ToArray());
    }

    [Fact]
    public void GetUnusedSymbols_LargePublicNoiseStillFindsReflectionSuspect()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_noise_fixture.cs",
            Lang = "csharp",
            Size = 4000,
            Lines = 1200,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 8,
                Content = """
                using System.Text.Json.Serialization;

                public class UserDto
                {
                    [JsonPropertyName("full_name")]
                    public string FullName { get; set; } = string.Empty;
                }
                """,
            }
        ]);

        var symbols = new List<SymbolRecord>();
        for (var i = 0; i < 1100; i++)
        {
            symbols.Add(new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = $"PublicNoise{i:D4}",
                Line = 20 + i,
                StartLine = 20 + i,
                EndLine = 20 + i,
                Signature = $"public class PublicNoise{i:D4}",
                Visibility = "public",
            });
        }
        symbols.Add(new SymbolRecord
        {
            FileId = fileId,
            Kind = "class",
            Name = "UserDto",
            Line = 3,
            StartLine = 3,
            EndLine = 6,
            Signature = "public class UserDto",
            Visibility = "public",
        });
        symbols.Add(new SymbolRecord
        {
            FileId = fileId,
            Kind = "property",
            Name = "FullName",
            Line = 5,
            StartLine = 5,
            EndLine = 5,
            Signature = "public string FullName { get; set; } = string.Empty;",
            Visibility = "public",
            ContainerKind = "class",
            ContainerName = "UserDto",
        });
        _writer.InsertSymbols(symbols);

        var unused = _reader.GetUnusedSymbols(limit: 4, kind: null, lang: "csharp",
            pathPatterns: ["reflection_noise_fixture.cs"], excludePathPatterns: null, excludeTests: false);

        Assert.Contains(unused, symbol => symbol.Name == "FullName" && symbol.UnusedBucket == "reflection_or_config_suspect");
    }

    [Fact]
    public void GetUnusedSymbols_BoundedPublicOverfetch_DoesNotScanLateReflectionSuspect()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_budget_fixture.cs",
            Lang = "csharp",
            Size = 12000,
            Lines = 2600,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 2405,
                EndLine = 2412,
                Content = """
                using System.Text.Json.Serialization;

                public class LateDto
                {
                    [JsonPropertyName("late_name")]
                    public string LateName { get; set; } = string.Empty;
                }
                """,
            }
        ]);

        var symbols = new List<SymbolRecord>();
        for (var i = 0; i < 2200; i++)
        {
            symbols.Add(new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = $"PublicNoise{i:D4}",
                Line = 20 + i,
                StartLine = 20 + i,
                EndLine = 20 + i,
                Signature = $"public class PublicNoise{i:D4}",
                Visibility = "public",
            });
        }
        symbols.Add(new SymbolRecord
        {
            FileId = fileId,
            Kind = "class",
            Name = "LateDto",
            Line = 2407,
            StartLine = 2407,
            EndLine = 2410,
            Signature = "public class LateDto",
            Visibility = "public",
        });
        symbols.Add(new SymbolRecord
        {
            FileId = fileId,
            Kind = "property",
            Name = "LateName",
            Line = 2409,
            StartLine = 2409,
            EndLine = 2409,
            Signature = "public string LateName { get; set; } = string.Empty;",
            Visibility = "public",
            ContainerKind = "class",
            ContainerName = "LateDto",
        });
        _writer.InsertSymbols(symbols);

        var unused = _reader.GetUnusedSymbols(limit: 4, kind: null, lang: "csharp",
            pathPatterns: ["reflection_budget_fixture.cs"], excludePathPatterns: null, excludeTests: false);

        Assert.DoesNotContain(unused, symbol => symbol.Name == "LateName");
        Assert.Equal(["public_or_exported_no_refs", "public_or_exported_no_refs", "public_or_exported_no_refs", "public_or_exported_no_refs"],
            unused.Select(symbol => symbol.UnusedBucket).ToArray());
    }

    [Fact]
    public void GetUnusedSymbols_NullStartEndLine_DoesNotCrash()
    {
        // Regression: #49 — legacy indexes can have NULL start_line/end_line on symbol rows.
        // cdidx unused crashed with "The data is NULL at ordinal 5" before the COALESCE fix.
        // リグレッション: #49 — 古いインデックスは symbols 行の start_line/end_line が NULL になりうる。
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/unused_null.cs",
            Lang = "csharp",
            Size = 100,
            Lines = 10,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = @"INSERT INTO symbols (file_id, kind, name, line, start_line, end_line)
                            VALUES (@fid, 'function', 'Orphan', 7, NULL, NULL)";
        cmd.Parameters.AddWithValue("@fid", fileId);
        cmd.ExecuteNonQuery();

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: null, lang: null,
            pathPatterns: null, excludePathPatterns: null, excludeTests: false);

        var sym = Assert.Single(unused, s => s.Name == "Orphan");
        Assert.Equal(7, sym.Line);
        Assert.Equal(7, sym.StartLine);
        Assert.Equal(7, sym.EndLine);
    }

    [Fact]
    public void SearchReferences_ClampsLongSingleLineContextAroundMatch()
    {
        var longLine = "const x = 0; " + new string('a', 320) + " target(); " + new string('b', 320);
        InsertIndexedFile("dist/app.js", "javascript", longLine);

        var result = Assert.Single(_reader.SearchReferences("target", limit: 1, maxLineWidth: 96));

        Assert.True(result.ContextTruncated);
        Assert.Contains("target()", result.Context);
        Assert.True(result.Context.Length <= 96);
    }

    [Fact]
    public void GetExcerpt_ClampsLongSingleLineContentAroundFocus()
    {
        var longLine = new string('a', 320) + "TARGET" + new string('b', 320);
        InsertIndexedFile("dist/data.txt", "text", longLine);

        var excerpt = _reader.GetExcerpt(
            "dist/data.txt",
            1,
            1,
            maxLineWidth: 96,
            focusLine: 1,
            focusColumn: longLine.IndexOf("TARGET", StringComparison.Ordinal) + 1,
            focusLength: "TARGET".Length);

        Assert.NotNull(excerpt);
        Assert.True(excerpt!.ContentTruncated);
        Assert.DoesNotContain(longLine, excerpt.Content);
        Assert.Contains("TARGET", excerpt.Content);
        Assert.True(excerpt.Content.Length <= 96);
    }

    [Fact]
    public void GetExcerpt_WithoutFocusStillClampsLongSingleLineContent()
    {
        var longLine = new string('a', 320) + "TARGET" + new string('b', 320);
        InsertIndexedFile("dist/no-focus.txt", "text", longLine);

        var excerpt = _reader.GetExcerpt("dist/no-focus.txt", 1, 1, maxLineWidth: 96);

        Assert.NotNull(excerpt);
        Assert.True(excerpt!.ContentTruncated);
        Assert.DoesNotContain(longLine, excerpt.Content);
        Assert.True(excerpt.Content.Length <= 96);
    }

    [Fact]
    public void GetExcerpt_FocusColumnOutsideFocusedLineReturnsNull()
    {
        var longLine = new string('a', 320) + "TARGET" + new string('b', 320);
        InsertIndexedFile("dist/focus-column-range.txt", "text", longLine);

        var excerpt = _reader.GetExcerpt(
            "dist/focus-column-range.txt",
            1,
            1,
            maxLineWidth: 40,
            focusLine: 1,
            focusColumn: 9999,
            focusLength: 6);

        Assert.Null(excerpt);
    }

    [Fact]
    public void FindInFiles_ClampsLongSingleLineSnippetAroundMatch()
    {
        var longLine = new string('a', 320) + "target" + new string('b', 320);
        InsertIndexedFile("dist/search.txt", "text", longLine);

        var result = Assert.Single(_reader.FindInFiles("target", 1, pathPatterns: ["dist/search.txt"], exact: true, maxLineWidth: 96));

        Assert.True(result.SnippetTruncated);
        Assert.Contains("target", result.Snippet);
        Assert.True(result.Snippet.Length <= 96);
    }

    // Issue #203 regression: --since thresholds with a time-of-day component used to silently
    // return zero rows because `@since` was bound via ToString("O") (yyyy-MM-ddTHH:mm:ss.fffffffZ)
    // while files.modified is stored by Microsoft.Data.Sqlite as "yyyy-MM-dd HH:mm:ss.FFFFFFF"
    // (space separator, no T, no Z). SQLite compares TEXT lexicographically, and "T" (0x54) is
    // greater than " " (0x20) at position 10, so `f.modified >= @since` was always false for
    // T-formatted thresholds regardless of actual temporal ordering. These tests bind DateTimes
    // straight through AddWithValue so both sides share the same serialization.
    // Issue #203 回帰: --since に時刻成分を渡すと無条件に0件だったバグの再発防止。
    // `@since` は ToString("O") で T 区切り + Z 付きに整形されていた一方、`files.modified` は
    // Microsoft.Data.Sqlite の既定 "yyyy-MM-dd HH:mm:ss.FFFFFFF"（空白区切り、T や Z なし）で
    // 保存されており、位置10の文字比較（スペース 0x20 vs T 0x54）で必ず保存値 < @since に
    // なっていた。DateTime をそのままバインドすれば書き込み側と完全に同じ文字列になる。

    [Fact]
    public void ListFiles_WithTimeOfDaySince_IncludesNewerFiles()
    {
        InsertIndexedFile(
            "src/since203_new.py",
            "python",
            "def new_func():\n    return 1\n",
            modified: new DateTime(2025, 6, 20, 22, 0, 0, DateTimeKind.Utc));
        InsertIndexedFile(
            "src/since203_old.py",
            "python",
            "def old_func():\n    return 0\n",
            modified: new DateTime(2025, 6, 20, 10, 0, 0, DateTimeKind.Utc));

        // Threshold 1h before the newer file; the newer file must be included.
        // より新しいファイルの1時間前を閾値にした場合、その新しいファイルが含まれるはず。
        var since = new DateTime(2025, 6, 20, 21, 0, 0, DateTimeKind.Utc);
        var results = _reader.ListFiles(
            pathPatterns: new[] { "src/since203_" },
            since: since);

        Assert.Contains(results, r => r.Path == "src/since203_new.py");
        Assert.DoesNotContain(results, r => r.Path == "src/since203_old.py");
    }

    [Fact]
    public void CountListFiles_WithTimeOfDaySince_CountsOnlyNewerFiles()
    {
        InsertIndexedFile(
            "src/count203_new.py",
            "python",
            "def new_func():\n    return 1\n",
            modified: new DateTime(2025, 6, 20, 22, 0, 0, DateTimeKind.Utc));
        InsertIndexedFile(
            "src/count203_old.py",
            "python",
            "def old_func():\n    return 0\n",
            modified: new DateTime(2025, 6, 20, 10, 0, 0, DateTimeKind.Utc));

        var since = new DateTime(2025, 6, 20, 21, 0, 0, DateTimeKind.Utc);
        var summary = _reader.CountListFiles(
            pathPatterns: new[] { "src/count203_" },
            since: since);

        Assert.Equal(1, summary.Count);
    }

    [Fact]
    public void SearchSymbols_WithTimeOfDaySince_IncludesNewerFiles()
    {
        InsertIndexedFile(
            "src/sym_new.py",
            "python",
            "def sym_only_new():\n    return 1\n",
            modified: new DateTime(2025, 6, 20, 22, 0, 0, DateTimeKind.Utc));
        InsertIndexedFile(
            "src/sym_old.py",
            "python",
            "def sym_only_old():\n    return 0\n",
            modified: new DateTime(2025, 6, 20, 10, 0, 0, DateTimeKind.Utc));

        var since = new DateTime(2025, 6, 20, 21, 0, 0, DateTimeKind.Utc);

        var newHits = _reader.SearchSymbols("sym_only_new", since: since);
        Assert.Single(newHits, s => s.Path == "src/sym_new.py");

        var oldHits = _reader.SearchSymbols("sym_only_old", since: since);
        Assert.Empty(oldHits);
    }

    [Fact]
    public void Search_WithTimeOfDaySince_IncludesNewerFiles()
    {
        InsertIndexedFile(
            "src/search_new.py",
            "python",
            "def search_only_new():\n    return 'needle_203'\n",
            modified: new DateTime(2025, 6, 20, 22, 0, 0, DateTimeKind.Utc));
        InsertIndexedFile(
            "src/search_old.py",
            "python",
            "def search_only_old():\n    return 'needle_203'\n",
            modified: new DateTime(2025, 6, 20, 10, 0, 0, DateTimeKind.Utc));

        var since = new DateTime(2025, 6, 20, 21, 0, 0, DateTimeKind.Utc);
        var results = _reader.Search("needle_203", since: since);

        Assert.Contains(results, r => r.Path == "src/search_new.py");
        Assert.DoesNotContain(results, r => r.Path == "src/search_old.py");
    }

    [Fact]
    public void ListFiles_WithTimeOfDaySince_ExcludesFilesBeforeThreshold()
    {
        InsertIndexedFile(
            "src/excl_only.py",
            "python",
            "def excl():\n    return 1\n",
            modified: new DateTime(2025, 6, 20, 22, 0, 0, DateTimeKind.Utc));

        // Threshold 1h after the file; must exclude everything.
        // ファイルより1時間後を閾値にした場合は除外されるはず。
        var since = new DateTime(2025, 6, 20, 23, 0, 0, DateTimeKind.Utc);
        var results = _reader.ListFiles(pathPatterns: new[] { "src/excl_only.py" }, since: since);

        Assert.Empty(results);
    }

    // Count-only SQL paths (search --count / symbols --count / definition --count) are compiled
    // independently from the list paths above, so they need their own regressions against the
    // ToString("O") vs DateTimeSqliteDefaultFormat mismatch. Without these, a future refactor that
    // reintroduces ToString("O") on any single count binding would pass the list-path tests.
    // `--count` 経路の SQL は一覧経路とは別に組み立てられているため、`ToString("O")` と
    // DateTimeSqliteDefaultFormat の非対称が再発しても一覧側テストだけでは検出できない。
    // カウント経路専用の回帰テストで各 bind を独立に守る。

    [Fact]
    public void CountSearchResults_WithTimeOfDaySince_CountsOnlyNewerChunks()
    {
        InsertIndexedFile(
            "src/countsearch_new.py",
            "python",
            "def countsearch_only_new():\n    return 'needle_203_count'\n",
            modified: new DateTime(2025, 6, 20, 22, 0, 0, DateTimeKind.Utc));
        InsertIndexedFile(
            "src/countsearch_old.py",
            "python",
            "def countsearch_only_old():\n    return 'needle_203_count'\n",
            modified: new DateTime(2025, 6, 20, 10, 0, 0, DateTimeKind.Utc));

        var since = new DateTime(2025, 6, 20, 21, 0, 0, DateTimeKind.Utc);
        var summary = _reader.CountSearchResults("needle_203_count", since: since);

        Assert.Equal(1, summary.FileCount);
        Assert.True(summary.Count >= 1);
    }

    [Fact]
    public void CountSearchSymbolsTotal_WithTimeOfDaySince_CountsOnlyNewerSymbols()
    {
        InsertIndexedFile(
            "src/countsym_new.py",
            "python",
            "def countsym_only_new():\n    return 1\n",
            modified: new DateTime(2025, 6, 20, 22, 0, 0, DateTimeKind.Utc));
        InsertIndexedFile(
            "src/countsym_old.py",
            "python",
            "def countsym_only_old():\n    return 0\n",
            modified: new DateTime(2025, 6, 20, 10, 0, 0, DateTimeKind.Utc));

        var since = new DateTime(2025, 6, 20, 21, 0, 0, DateTimeKind.Utc);

        var newSummary = _reader.CountSearchSymbolsTotal("countsym_only_new", since: since);
        Assert.Equal(1, newSummary.Count);

        var oldSummary = _reader.CountSearchSymbolsTotal("countsym_only_old", since: since);
        Assert.Equal(0, oldSummary.Count);
    }

    [Fact]
    public void CountDefinitionsTotal_WithTimeOfDaySince_CountsOnlyNewerDefinitions()
    {
        InsertIndexedFile(
            "src/countdef_new.py",
            "python",
            "def countdef_only_new():\n    return 1\n",
            modified: new DateTime(2025, 6, 20, 22, 0, 0, DateTimeKind.Utc));
        InsertIndexedFile(
            "src/countdef_old.py",
            "python",
            "def countdef_only_old():\n    return 0\n",
            modified: new DateTime(2025, 6, 20, 10, 0, 0, DateTimeKind.Utc));

        var since = new DateTime(2025, 6, 20, 21, 0, 0, DateTimeKind.Utc);

        var newSummary = _reader.CountDefinitionsTotal("countdef_only_new", since: since);
        Assert.Equal(1, newSummary.Count);

        var oldSummary = _reader.CountDefinitionsTotal("countdef_only_old", since: since);
        Assert.Equal(0, oldSummary.Count);
    }

    [Fact]
    public void EndToEnd_BomBearingFile_StripLineLeadingBomPreserveMidLineZwnbsp()
    {
        // End-to-end #183 vertical: real bytes on disk → FileIndexer.BuildRecord →
        // ChunkSplitter.Split → SymbolExtractor.Extract + ReferenceExtractor.Extract
        // → DbWriter → DbReader.Search + GetExcerpt + GetDefinitions +
        // SearchReferences. Pins five invariants at once so the CHANGELOG claim
        // of covering `search` / `excerpt` / `definition` / `references` surfaces
        // is actually tested:
        //   1. Leading BOM at offset 0 is stripped: search + definition find the
        //      line-1 symbol (`^\s*`-anchored indexing succeeds).
        //   2. A BOM that immediately follows `\n` is stripped: definition of the
        //      mid-file symbol is found, and excerpt of the affected line does
        //      not emit a phantom U+FEFF.
        //   3. Excerpt of lines never starts with a phantom U+FEFF.
        //   4. Non-line-leading U+FEFF (intentional ZWNBSP inside a string literal)
        //      is preserved verbatim — the narrowing iteration of the fix must not
        //      silently corrupt intentional mid-line ZWNBSP use.
        //   5. A call-site reference on a BOM-bearing source is captured end-to-end,
        //      pinning the `references` / `callers` surface through the same
        //      pipeline rather than claiming coverage via CHANGELOG alone.
        // Closes #183.
        // #183 のエンドツーエンド縦串テスト: 実バイトから FileIndexer.BuildRecord →
        // ChunkSplitter.Split → SymbolExtractor.Extract + ReferenceExtractor.Extract
        // → DbWriter → DbReader.Search + GetExcerpt + GetDefinitions +
        // SearchReferences まで通す。CHANGELOG が主張する search / excerpt /
        // definition / references の全サーフェスが実際にテストされていることを
        // 保証する 5 つの不変条件を同時に pin する:
        //   1. オフセット 0 の先頭 BOM は剥がす。1 行目のシンボルが search /
        //      definition で見つかる (`^\s*` 固定パターンが成立する)。
        //   2. `\n` の直後の BOM は剥がす。該当 mid-file シンボルが definition で
        //      見つかり、excerpt に幽霊 U+FEFF を含めない。
        //   3. excerpt の各行は幽霊 U+FEFF で始まらない。
        //   4. 行頭以外の U+FEFF (文字列リテラル内の意図的 ZWNBSP) はそのまま保持する。
        //   5. BOM 付きソース中の call-site 参照がエンドツーエンドで抽出され、
        //      references / callers 経路を同じパイプラインで pin する。
        // Closes #183.
        var tempDir = Path.Combine(Path.GetTempPath(), $"cdidx_bom_e2e_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var source =
                "\uFEFFnamespace BomE2E;\n" +
                "\n" +
                "\uFEFFpublic class PhraseHolder\n" +
                "{\n" +
                "    public const string Phrase = \"A\uFEFFB\";\n" +
                "    public void Greet() { System.Console.WriteLine(Phrase); }\n" +
                "}\n";
            var bytes = Encoding.UTF8.GetBytes(source);
            var filePath = Path.Combine(tempDir, "bom_e2e.cs");
            File.WriteAllBytes(filePath, bytes);

            var indexer = new FileIndexer(tempDir);
            var (record, content, _, _) = indexer.BuildRecordWithRawBytes(filePath);

            // Line-leading BOMs are stripped; mid-line ZWNBSP inside the string literal is preserved.
            // 行頭 BOM は剥がし、文字列リテラル内の mid-line ZWNBSP は保持されている。
            Assert.DoesNotContain('\uFEFF', new string(content.Split('\n')[0].ToCharArray()));
            Assert.Contains("\"A\uFEFFB\"", content);

            var fileId = _writer.UpsertFile(record);
            _writer.InsertChunks(ChunkSplitter.Split(fileId, content));
            var symbols = SymbolExtractor.Extract(fileId, "csharp", content);
            _writer.InsertSymbols(symbols);
            _writer.InsertReferences(ReferenceExtractor.Extract(fileId, "csharp", content, symbols));

            // 1. search finds the line-1 namespace declaration.
            // 1. search が 1 行目の namespace 宣言を発見する。
            var searchResults = _reader.Search("BomE2E");
            Assert.Contains(searchResults, r => r.Path == record.Path);

            // 2. GetDefinitions resolves both the line-1 namespace and the mid-file class / method.
            // 2. GetDefinitions が 1 行目の namespace と mid-file の class / method を解決する。
            var nsDefs = _reader.GetDefinitions("BomE2E");
            Assert.Contains(nsDefs, d => d.Path == record.Path && d.Name == "BomE2E" && d.Line == 1);
            var classDefs = _reader.GetDefinitions("PhraseHolder");
            Assert.Contains(classDefs, d => d.Path == record.Path && d.Name == "PhraseHolder" && d.Line == 3);
            var methodDefs = _reader.GetDefinitions("Greet");
            Assert.Contains(methodDefs, d => d.Path == record.Path && d.Name == "Greet");

            // 3. Excerpt of lines 1-3 never has a phantom U+FEFF at line start.
            // 3. 1〜3 行目の excerpt には、行頭の幽霊 U+FEFF が含まれない。
            var headExcerpt = _reader.GetExcerpt(record.Path, startLine: 1, endLine: 3);
            Assert.NotNull(headExcerpt);
            foreach (var line in headExcerpt!.Content.Split('\n'))
            {
                if (line.Length == 0) continue;
                Assert.NotEqual('\uFEFF', line[0]);
            }
            Assert.Contains("namespace BomE2E;", headExcerpt.Content);
            Assert.Contains("public class PhraseHolder", headExcerpt.Content);

            // 4. Excerpt of the const-string line still carries the intentional mid-line ZWNBSP.
            // 4. const 文字列行の excerpt には、意図的な mid-line ZWNBSP がそのまま残る。
            var literalExcerpt = _reader.GetExcerpt(record.Path, startLine: 5, endLine: 5);
            Assert.NotNull(literalExcerpt);
            Assert.Contains("\"A\uFEFFB\"", literalExcerpt!.Content);

            // 5. SearchReferences finds the call-site reference on the BOM-bearing file,
            //    pinning the references / callers surface end-to-end.
            // 5. SearchReferences が BOM 付きファイルの call-site 参照を発見し、
            //    references / callers 経路をエンドツーエンドで pin する。
            var refs = _reader.SearchReferences("WriteLine", lang: "csharp");
            Assert.Contains(refs, r => r.Path == record.Path);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Search_RanksFilesWithExactSymbolMatchBeforeFilesWithout_Issue1520()
    {
        // Issue #1520: the search ORDER BY uses a per-file "exact symbol match" bucket so that
        // FTS hits inside files where a symbol named exactly like the query exists float above
        // files where the query only appears textually. Pin the observable ordering after
        // materializing the EXISTS predicate into a derived-table LEFT JOIN.
        // Issue #1520: ORDER BY のシンボル一致バケットをサブクエリ→LEFT JOIN 化したため、
        // ランキングが従来通りに維持されることを観測ベースで pin する。
        const string token = "rank_match_token_1520";
        InsertIndexedFile(
            "src/rank_text_only.py",
            "python",
            $"# bare mention only\nresult = {token}\n");
        InsertIndexedFile(
            "src/rank_symbol_hit.py",
            "python",
            $"def {token}():\n    return None\n");

        var results = _reader.Search(token);

        Assert.True(results.Count >= 2);
        var symbolHitIndex = results.FindIndex(r => r.Path == "src/rank_symbol_hit.py");
        var textOnlyIndex = results.FindIndex(r => r.Path == "src/rank_text_only.py");
        Assert.True(symbolHitIndex >= 0, "file with the exact-symbol match should appear in results");
        Assert.True(textOnlyIndex >= 0, "file with the textual-only match should appear in results");
        Assert.True(symbolHitIndex < textOnlyIndex,
            $"file with the exact-symbol match ranked at {symbolHitIndex} should precede textual-only at {textOnlyIndex}");
    }

    [Fact]
    public void Search_RanksFilesWithPrefixSymbolMatchBeforeFilesWithout_Issue1520()
    {
        // Issue #1520: prefix bucket must still favor files that own a symbol whose name starts
        // with the query (e.g. `auth*` matches an `authenticate` function declaration) over
        // files that only contain the literal substring in chunk text.
        // Issue #1520: prefix バケットも、シンボル名が query で始まるファイルを優先する挙動を維持する。
        const string prefix = "prefix1520";
        InsertIndexedFile(
            "src/prefix_text_only.py",
            "python",
            $"# textual mention: {prefix}_lookup is just a string here.\n");
        InsertIndexedFile(
            "src/prefix_symbol_hit.py",
            "python",
            $"def {prefix}_handler():\n    return None\n");

        var results = _reader.Search(prefix);

        Assert.True(results.Count >= 2);
        var symbolHitIndex = results.FindIndex(r => r.Path == "src/prefix_symbol_hit.py");
        var textOnlyIndex = results.FindIndex(r => r.Path == "src/prefix_text_only.py");
        Assert.True(symbolHitIndex >= 0);
        Assert.True(textOnlyIndex >= 0);
        Assert.True(symbolHitIndex < textOnlyIndex,
            $"file with the prefix-symbol match ranked at {symbolHitIndex} should precede textual-only at {textOnlyIndex}");
    }

    [Fact]
    public void Search_RawLongQueryDemotesChunksBelowHalfTokenCoverage_Issue1970()
    {
        var tokens = new[]
        {
            "coverage1970alpha",
            "coverage1970bravo",
            "coverage1970charlie",
            "coverage1970delta",
            "coverage1970echo",
            "coverage1970foxtrot",
            "coverage1970golf",
            "coverage1970hotel",
            "coverage1970india",
            "coverage1970juliet",
        };
        InsertIndexedFile(
            "src/coverage1970_partial.py",
            "python",
            $"{tokens[0]} {tokens[1]}\n");
        InsertIndexedFile(
            "src/coverage1970_fuller.py",
            "python",
            string.Join(' ', tokens.Take(6)) + "\n");

        var rawQuery = string.Join(" OR ", tokens.Select(token => $"\"{token}\""));
        var results = _reader.Search(rawQuery, rawQuery: true, limit: 10);

        var fullerIndex = results.FindIndex(r => r.Path == "src/coverage1970_fuller.py");
        var partialIndex = results.FindIndex(r => r.Path == "src/coverage1970_partial.py");
        Assert.True(fullerIndex >= 0, "file covering at least 60% of the long query should appear in results");
        Assert.True(partialIndex >= 0, "file covering fewer than half of the long query should appear in results");
        Assert.True(fullerIndex < partialIndex,
            $"higher-coverage file ranked at {fullerIndex} should precede partial match at {partialIndex}");

        var qualifiedResults = _reader.Search($"content:({rawQuery})", rawQuery: true, limit: 10);
        var columnListResults = _reader.Search($"{{content}}:({rawQuery})", rawQuery: true, limit: 10);

        Assert.Equal(results.Select(r => r.Path), qualifiedResults.Select(r => r.Path));
        Assert.Equal(results.Select(r => r.Path), columnListResults.Select(r => r.Path));
    }

    [Fact]
    public void SearchRankingBuckets_DoNotEmbedCorrelatedExistsInOrderBy_Issue1520()
    {
        // Issue #1520: the ranking constants must not embed a correlated EXISTS subquery
        // against `symbols` that references the outer `f.id`. Such a subquery is re-evaluated
        // per FTS hit before the LIMIT, turning a fast search into an O(N x M) sort.
        // Issue #1520: ranking 定数に外側 f.id を参照する EXISTS を埋め戻していないことを固定する。
        Assert.DoesNotContain("EXISTS", DbReader.ExactSymbolMatchOrder, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EXISTS", DbReader.PrefixSymbolMatchOrder, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FROM symbols", DbReader.ExactSymbolMatchOrder, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FROM symbols", DbReader.PrefixSymbolMatchOrder, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("exact_symbol_match", DbReader.ExactSymbolMatchOrder, StringComparison.Ordinal);
        Assert.Contains("prefix_symbol_match", DbReader.PrefixSymbolMatchOrder, StringComparison.Ordinal);
        Assert.Contains("LEFT JOIN", _reader.SearchSymbolMatchJoinsSql, StringComparison.Ordinal);
        Assert.Contains("GROUP BY file_id", _reader.SearchSymbolMatchJoinsSql, StringComparison.Ordinal);
        // The materialized lookup must stay SARGable (no `lower(name)` wrapping).
        Assert.DoesNotContain("lower(name", _reader.SearchSymbolMatchJoinsSql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Search_OrderByPlanDoesNotReScanSymbolsPerRow_Issue1520()
    {
        // Issue #1520: EXPLAIN QUERY PLAN of the full search SQL must show the ranking
        // subqueries materialized once instead of re-scanning `symbols` correlated by `f.id`.
        // Modern SQLite reports a single "MATERIALIZE" or "CO-ROUTINE" step for SELECT DISTINCT
        // subqueries in FROM; the regression would surface a "CORRELATED SCALAR SUBQUERY"
        // (or repeated "SEARCH symbols ... USING INDEX idx_symbols_file") instead.
        // Issue #1520: EXPLAIN QUERY PLAN に CORRELATED SCALAR SUBQUERY が現れないことを固定する。
        const string sql = @"
            SELECT f.path, f.lang, c.start_line, c.end_line, c.content, rank
            FROM fts_chunks
            JOIN chunks c ON fts_chunks.rowid = c.id
            JOIN files f ON c.file_id = f.id
            LEFT JOIN (
                SELECT DISTINCT file_id FROM symbols
                WHERE name = @rankingQuery COLLATE NOCASE
            ) AS exact_symbol_match ON exact_symbol_match.file_id = f.id
            LEFT JOIN (
                SELECT DISTINCT file_id FROM symbols
                WHERE name LIKE @rankingQueryPrefix ESCAPE '\' COLLATE NOCASE
            ) AS prefix_symbol_match ON prefix_symbol_match.file_id = f.id
            WHERE fts_chunks MATCH @query
            ORDER BY
                CASE WHEN exact_symbol_match.file_id IS NULL THEN 1 ELSE 0 END,
                CASE WHEN prefix_symbol_match.file_id IS NULL THEN 1 ELSE 0 END,
                rank
            LIMIT 10";

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
        cmd.Parameters.AddWithValue("@query", "authenticate");
        cmd.Parameters.AddWithValue("@rankingQuery", "authenticate");
        cmd.Parameters.AddWithValue("@rankingQueryPrefix", "authenticate%");

        var plan = new StringBuilder();
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                plan.AppendLine(reader.GetString(3));

        var planText = plan.ToString();
        Assert.DoesNotContain("CORRELATED", planText);
    }

    [Fact]
    public void AnalyzeFileLine_WithKindAndLanguageFilters_ReturnsSymbolAtLine_Issue4057()
    {
        InsertIndexedFile(
            "src/issue4057/LineLookup.cs",
            "csharp",
            """
            public class LineLookup
            {
                public void Outside() { }
                public void Target()
                {
                }
            }
            """);

        var analysis = _reader.AnalyzeFileLine(
            "src/issue4057/LineLookup.cs",
            line: 5,
            limit: 5,
            lang: "csharp",
            kind: "function");

        var definition = Assert.Single(analysis.Definitions);
        Assert.Equal("Target", definition.Name);
        Assert.Equal("function", definition.Kind);
        Assert.Equal("csharp", definition.Lang);
        Assert.Equal("src/issue4057/LineLookup.cs", definition.Path);
    }

    [Fact]
    public void GetRepoMap_CaseSensitiveWorkspaceKeepsCaseVariantEntrypointFallbackPath()
    {
        StampWorkspacePathCaseSensitive(true);
        InsertIndexedFile("src/Program.cs", "csharp",
            """
            public class Program
            {
                public static void Main() { }
            }
            """);
        InsertIndexedFile("src/program.cs", "csharp", "Console.WriteLine(\"fallback\");\n");

        var map = _reader.GetRepoMap(limit: 10, lang: "csharp", pathPatterns: new[] { "src/" });

        Assert.Contains(map.Entrypoints, item => item.Name == "Main" && item.Path == "src/Program.cs");
        Assert.Contains(map.Entrypoints, item => item.Kind == "file" && item.Name == "program.cs" && item.Path == "src/program.cs");
    }

    [Fact]
    public void GetRepoMap_CaseInsensitiveWorkspaceCollapsesCaseVariantEntrypointFallbackPath()
    {
        StampWorkspacePathCaseSensitive(false);
        InsertIndexedFile("src/Program.cs", "csharp",
            """
            public class Program
            {
                public static void Main() { }
            }
            """);
        InsertIndexedFile("src/program.cs", "csharp", "Console.WriteLine(\"fallback\");\n");

        var map = _reader.GetRepoMap(limit: 10, lang: "csharp", pathPatterns: new[] { "src/" });

        Assert.Contains(map.Entrypoints, item => item.Name == "Main" && item.Path == "src/Program.cs");
        Assert.DoesNotContain(map.Entrypoints, item => item.Kind == "file" && item.Path == "src/program.cs");
    }

    private void StampWorkspacePathCaseSensitive(bool pathCaseSensitive)
        => _writer.SetMeta(DbContext.WorkspacePathCaseSensitiveMetaKey, pathCaseSensitive.ToString());

    private static SqliteConnection CreateLegacyReferenceConnection(string legacyPath)
    {
        var db = new DbContext(legacyPath);
        db.InitializeSchema();
        var writer = new DbWriter(db.Connection);

        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = "src/legacy_sql.sql",
            Lang = "sql",
            Size = 64,
            Lines = 4,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        writer.InsertReferences([
            new ReferenceRecord
            {
                FileId = fileId,
                SymbolName = "dbo.Target",
                ReferenceKind = "call",
                Line = 3,
                Column = 9,
                Context = "EXEC dbo.Target;",
                ContainerKind = "procedure",
                ContainerName = "dbo.Caller",
            },
        ]);
        writer.MarkGraphReady();

        using (var cmd = db.Connection.CreateCommand())
        {
            cmd.CommandText = "UPDATE symbol_references SET context = @context WHERE file_id = @fileId";
            cmd.Parameters.AddWithValue("@context", "EXEC dbo.Target;");
            cmd.Parameters.AddWithValue("@fileId", fileId);
            cmd.ExecuteNonQuery();
        }

        using (var cmd = db.Connection.CreateCommand())
        {
            cmd.CommandText = @"
                PRAGMA foreign_keys = OFF;
                DROP TABLE reference_lines;
                PRAGMA foreign_keys = ON;";
            cmd.ExecuteNonQuery();
        }

        return db.Connection;
    }
}
