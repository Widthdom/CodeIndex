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
public class DbReaderTests : IDisposable
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
    public void BuildPathLikePattern_TreatsGlobTokensAsWildcards(string input, string expected)
    {
        Assert.Equal(expected, DbReader.BuildPathLikePattern(input));
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
    public void CreateSearchReferencesCommand_RanksWithoutLoweringReferenceNames()
    {
        using var cmd = CreateSearchReferencesCommandForSql("FetchData");
        var sql = cmd.CommandText;

        Assert.DoesNotContain("lower(r.symbol_name)", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("r.symbol_name = @rankingQuery COLLATE NOCASE", sql, StringComparison.Ordinal);
        Assert.Contains("r.symbol_name COLLATE NOCASE LIKE @rankingQueryPrefix ESCAPE '\\'", sql, StringComparison.Ordinal);
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
            Path = "src/auth.py", Lang = "python", Size = 500, Lines = 30,
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
            Path = "src/api.js", Lang = "javascript", Size = 800, Lines = 50,
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
    public void Search_FindsMatchingChunks()
    {
        var results = _reader.Search("authenticate");
        Assert.Single(results);
        Assert.Equal("src/auth.py", results[0].Path);
        Assert.Equal(1, results[0].StartLine);
    }

    [Fact]
    public void GetSymbolHotspots_RanksRealCallsAboveManyLowerWeightSubscribeEdges()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/hotspot_weights.cs",
            Lang = "csharp",
            Size = 200,
            Lines = 20,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "RealCallTarget",
                Line = 1,
                StartLine = 1,
                EndLine = 3,
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "SubscribeOnlyTarget",
                Line = 5,
                StartLine = 5,
                EndLine = 7,
            },
        ]);

        var references = new List<ReferenceRecord>();
        for (var i = 0; i < 2; i++)
        {
            references.Add(new ReferenceRecord
            {
                FileId = fileId,
                SymbolName = "RealCallTarget",
                ReferenceKind = "call",
                Line = 10 + i,
                Column = 9,
                Context = "RealCallTarget();",
            });
        }
        for (var i = 0; i < 5; i++)
        {
            references.Add(new ReferenceRecord
            {
                FileId = fileId,
                SymbolName = "SubscribeOnlyTarget",
                ReferenceKind = "subscribe",
                Line = 14 + i,
                Column = 9,
                Context = "SubscribeOnlyTarget += Handler;",
            });
        }
        _writer.InsertReferences(references);

        var hotspots = _reader.GetSymbolHotspots(limit: 2, kind: "function", lang: "csharp", pathPatterns: null, excludePathPatterns: null, excludeTests: false);

        Assert.Equal("RealCallTarget", hotspots[0].Symbol.Name);
        Assert.Equal(2, hotspots[0].ReferenceCount);
        Assert.Equal(2.0, hotspots[0].ReferenceScore);
        Assert.Equal("SubscribeOnlyTarget", hotspots[1].Symbol.Name);
        Assert.Equal(5, hotspots[1].ReferenceCount);
        Assert.Equal(1.5, hotspots[1].ReferenceScore, precision: 6);
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
    public void Search_RanksMatchingPublicSymbolsBeforePrivateSymbols_Issue1868()
    {
        InsertSearchVisibilityFixture(
            "src/private-auth.cs",
            "private",
            new DateTime(2025, 6, 3, 0, 0, 0, DateTimeKind.Utc));
        InsertSearchVisibilityFixture(
            "src/public-auth.cs",
            "public",
            new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        var ranked = _reader.Search("Authenticate", lang: "csharp", exact: true, deduplicate: false);

        Assert.Equal(["src/public-auth.cs", "src/private-auth.cs"], ranked.Select(result => result.Path).ToArray());
        Assert.Equal(["public", "private"], ranked.Select(result => result.Visibility).ToArray());
    }

    [Fact]
    public void Search_CanDisableVisibilityRanking_Issue1868()
    {
        InsertSearchVisibilityFixture(
            "src/private-auth-legacy.cs",
            "private",
            new DateTime(2025, 6, 3, 0, 0, 0, DateTimeKind.Utc));
        InsertSearchVisibilityFixture(
            "src/public-auth-legacy.cs",
            "public",
            new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        var legacyRanked = _reader.Search("Authenticate", lang: "csharp", exact: true, deduplicate: false, visibilityRank: false);

        Assert.Equal(["src/private-auth-legacy.cs", "src/public-auth-legacy.cs"], legacyRanked.Select(result => result.Path).ToArray());
        Assert.Equal(["private", "public"], legacyRanked.Select(result => result.Visibility).ToArray());
    }

    [Fact]
    public void SearchSymbols_RustMacroQueriesIgnoreTrailingBang()
    {
        InsertIndexedFile(
            "src/macros.rs",
            "rust",
            """
            macro_rules! my_macro {
                () => {};
            }
            """);

        var results = _reader.SearchSymbols("my_macro!", lang: "rust", exact: true);

        var symbol = Assert.Single(results);
        Assert.Equal("my_macro", symbol.Name);
        Assert.Equal("src/macros.rs", symbol.Path);
    }

    [Fact]
    public void SearchSymbols_JavaScriptCommonJsExportQueriesResolveToLeafNames()
    {
        InsertIndexedFile(
            "src/commonjs.js",
            "javascript",
            """
            module.exports.foo = function foo() { return 1; };
            function caller() {
                foo();
            }
            exports.bar = 42;
            """);

        var foo = Assert.Single(_reader.SearchSymbols("module.exports.foo", lang: "javascript", exact: true));
        Assert.Equal("foo", foo.Name);
        Assert.Equal("src/commonjs.js", foo.Path);

        var bar = Assert.Single(_reader.SearchSymbols("exports.bar", lang: "javascript", exact: true));
        Assert.Equal("bar", bar.Name);
        Assert.Equal("src/commonjs.js", bar.Path);

        var references = _reader.SearchReferences("module.exports.foo", lang: "javascript", exact: true);
        Assert.NotEmpty(references);
        Assert.Contains(references, reference => reference.SymbolName == "foo" && reference.ContainerName == "caller" && reference.Path == "src/commonjs.js");
    }

    [Fact]
    public void SearchReferences_TerraformDottedQueriesResolveToBareNames_Issue1502()
    {
        // Issue #1502: references stored by the Terraform extractor use bare symbol names
        // (e.g. "instances", "regions", "max_size"), but users naturally query the HCL form
        // (`var.instances`, `local.regions`). Without prefix normalization at the query
        // layer, `cdidx references var.instances` returned nothing.
        // Issue #1502: Terraform extractor は bare 名（"instances" 等）で参照を格納するが、
        // 利用者は HCL 形式（`var.instances` 等）で問い合わせる。クエリ層で prefix を
        // 取り除かないと、`cdidx references var.instances` が空になる。
        InsertIndexedFile(
            "main.tf",
            "terraform",
            """
            variable "instances" {
              type = map(object({ size = string }))
            }

            variable "max_size" {
              type = number
            }

            locals {
              regions = ["us-east-1", "us-west-2"]
              suffix  = "demo"
            }

            output "ids" {
              value = var.max_size
            }

            resource "aws_instance" "fleet" {
              for_each = var.instances
              count    = length(local.regions)
              tags     = local.suffix
            }
            """);

        var varInstances = _reader.SearchReferences("var.instances", lang: "terraform", exact: true);
        Assert.Contains(varInstances, reference => reference.SymbolName == "instances" && reference.Path == "main.tf");

        var localRegions = _reader.SearchReferences("local.regions", lang: "terraform", exact: true);
        Assert.Contains(localRegions, reference => reference.SymbolName == "regions" && reference.Path == "main.tf");

        var varMaxSize = _reader.SearchReferences("var.max_size", lang: "terraform", exact: true);
        Assert.Contains(varMaxSize, reference => reference.SymbolName == "max_size" && reference.Path == "main.tf");

        // Lang inference also works when caller omits lang (extension-only path).
        // lang を省略した場合（拡張子推論のみ）も解決できることを確認する。
        var inferredLocalSuffix = _reader.SearchReferences("local.suffix", lang: null, exact: true);
        Assert.Contains(inferredLocalSuffix, reference => reference.SymbolName == "suffix" && reference.Path == "main.tf");
    }

    [Fact]
    public void SearchSymbols_JavaScriptCommonJsBracketExportQueriesResolveToLeafNames()
    {
        InsertIndexedFile(
            "src/commonjs-bracket.js",
            "javascript",
            """
            module.exports["foo"] = function foo() { return 1; };
            function caller() {
                foo();
            }
            exports['bar'] = 42;
            """);

        var foo = Assert.Single(_reader.SearchSymbols("module.exports[\"foo\"]", lang: "javascript", exact: true));
        Assert.Equal("foo", foo.Name);
        Assert.Equal("src/commonjs-bracket.js", foo.Path);

        var bar = Assert.Single(_reader.SearchSymbols("exports['bar']", lang: "javascript", exact: true));
        Assert.Equal("bar", bar.Name);
        Assert.Equal("src/commonjs-bracket.js", bar.Path);

        var references = _reader.SearchReferences("module.exports[\"foo\"]", lang: "javascript", exact: true);
        Assert.NotEmpty(references);
        Assert.Contains(references, reference => reference.SymbolName == "foo" && reference.ContainerName == "caller" && reference.Path == "src/commonjs-bracket.js");
    }

    [Fact]
    public void SearchSymbols_JavaScriptQualifiedQueriesOutsideCommonJsRemainExact()
    {
        InsertIndexedFile(
            "src/logger.js",
            "javascript",
            """
            const logger = {
                log() {}
            };
            """);

        Assert.Empty(_reader.SearchSymbols("logger.log", lang: "javascript", exact: true));
    }

    [Fact]
    public void SearchSymbols_RustRawIdentifiersIgnoreRawPrefixAndReferences()
    {
        InsertIndexedFile(
            "src/lib.rs",
            "rust",
            """
            pub fn r#type() {}
            """);

        var results = _reader.SearchSymbols("r#type", lang: "rust", exact: true);

        var symbol = Assert.Single(results);
        Assert.Equal("type", symbol.Name);
        Assert.Equal("src/lib.rs", symbol.Path);
    }

    [Fact]
    public void SearchSymbols_RustQualifiedQueriesStayPathAware()
    {
        InsertIndexedFile(
            "src/lib.rs",
            "rust",
            """
            pub mod macros {
                pub fn build() {}
            }
            """);

        InsertIndexedFile(
            "src/other.rs",
            "rust",
            """
            pub mod other {
                pub fn build() {}
            }
            """);

        var results = _reader.SearchSymbols("crate::macros::build", lang: "rust", exact: true);

        var symbol = Assert.Single(results);
        Assert.Equal("build", symbol.Name);
        Assert.Equal("src/lib.rs", symbol.Path);
        Assert.Equal(1, _reader.CountSearchSymbols("crate::macros::build", lang: "rust", exact: true));
        Assert.Equal(1, _reader.CountDefinitionsTotal("crate::macros::build", lang: "rust", exact: true).Count);
    }

    [Fact]
    public void SearchSymbols_RustRawIdentifiersIgnoreRawPrefix()
    {
        InsertIndexedFile(
            "src/raw.rs",
            "rust",
            """
            pub fn r#type() {}

            pub fn caller() {
                r#type();
            }
            """);

        var symbolResults = _reader.SearchSymbols("r#type", lang: "rust", exact: true);
        var symbol = Assert.Single(symbolResults);
        Assert.Equal("type", symbol.Name);
        Assert.Equal("src/raw.rs", symbol.Path);

        var referenceResults = _reader.SearchReferences("r#type", lang: "rust", exact: true);
        var reference = Assert.Single(referenceResults);
        Assert.Equal("type", reference.SymbolName);
        Assert.Equal("src/raw.rs", reference.Path);
    }

    [Fact]
    public void SearchSymbols_SwiftExactQueriesMatchBacktickEscapedIdentifiers()
    {
        InsertIndexedFile(
            "src/swift.swift",
            "swift",
            """
            public struct Store {
                public func `repeat`() {}
            }
            """);

        var plainResults = _reader.SearchSymbols("repeat", lang: "swift", exact: true);
        var escapedResults = _reader.SearchSymbols("`repeat`", lang: "swift", exact: true);

        var plain = Assert.Single(plainResults);
        var escaped = Assert.Single(escapedResults);

        Assert.Equal("`repeat`", plain.Name);
        Assert.Equal("src/swift.swift", plain.Path);
        Assert.Equal(plain.Name, escaped.Name);
        Assert.Equal(plain.Path, escaped.Path);
    }

    [Fact]
    public void SearchSymbols_SwiftExactQueriesMatchQualifiedBacktickEscapedIdentifiers()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/qualified.swift",
            Lang = "swift",
            Size = 64,
            Lines = 1,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks([new ChunkRecord
        {
            FileId = fileId,
            ChunkIndex = 0,
            StartLine = 1,
            EndLine = 1,
            Content = "MyType.`repeat`",
        }]);
        _writer.InsertSymbols([new SymbolRecord
        {
            FileId = fileId,
            Kind = "function",
            Name = "MyType.`repeat`",
            Line = 1,
            StartLine = 1,
            EndLine = 1,
            BodyStartLine = 1,
            BodyEndLine = 1,
            Signature = "func MyType.`repeat`() {}",
        }]);

        var plainResults = _reader.SearchSymbols("MyType.repeat", lang: "swift", exact: true);
        var escapedResults = _reader.SearchSymbols("MyType.`repeat`", lang: "swift", exact: true);

        var plain = Assert.Single(plainResults);
        var escaped = Assert.Single(escapedResults);

        Assert.Equal("MyType.`repeat`", plain.Name);
        Assert.Equal("src/qualified.swift", plain.Path);
        Assert.Equal(plain.Name, escaped.Name);
        Assert.Equal(plain.Path, escaped.Path);
    }

    [Fact]
    public void SearchReferences_RustRawMacroInvocationsIgnoreRawPrefixAndBang()
    {
        InsertIndexedFile(
            "src/raw.rs",
            "rust",
            """
            fn main() {
                r#type!();
            }
            """);

        var results = _reader.SearchReferences("r#type!", lang: "rust", exact: true);

        var reference = Assert.Single(results);
        Assert.Equal("type", reference.SymbolName);
        Assert.Equal("src/raw.rs", reference.Path);
    }

    [Fact]
    public void SearchReferences_RustQualifiedRawMacroInvocationsStayPathAware()
    {
        InsertIndexedFile(
            "src/raw.rs",
            "rust",
            """
            fn main() {
                crate::r#type!();
            }
            """);

        var results = _reader.SearchReferences("crate::r#type!", lang: "rust", exact: true);

        var reference = Assert.Single(results);
        Assert.Equal("crate::type", reference.SymbolName);
        Assert.Equal("src/raw.rs", reference.Path);
    }

    [Fact]
    public void SearchReferences_RustQualifiedMacroInvocationsStayPathAware()
    {
        InsertIndexedFile(
            "src/macros.rs",
            "rust",
            """
            fn main() {
                crate::macros::build!();
                crate::other::build!();
            }
            """);

        var results = _reader.SearchReferences("crate::macros::build!", lang: "rust", exact: true);

        var reference = Assert.Single(results);
        Assert.Equal("crate::macros::build", reference.SymbolName);
        Assert.Equal("src/macros.rs", reference.Path);
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
    public void Search_PrefersSourceFilesOverTests()
    {
        var testFileId = _writer.UpsertFile(new FileRecord
        {
            Path = "tests/auth_test.py", Lang = "python", Size = 300, Lines = 10,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks([new ChunkRecord
        {
            FileId = testFileId, ChunkIndex = 0, StartLine = 1, EndLine = 3,
            Content = "def authenticate_test_case():\n    authenticate('a', 'b')\n    return True",
        }]);

        var results = _reader.Search("authenticate", limit: 2);

        Assert.Equal(2, results.Count);
        Assert.Equal("src/auth.py", results[0].Path);
        Assert.Equal("tests/auth_test.py", results[1].Path);
    }

    [Fact]
    public void Search_DeduplicatesOverlappingChunks()
    {
        // Create two overlapping chunks in the same file that both match
        // 同じファイル内でオーバーラップし、両方マッチする2チャンクを作成
        var overlapFileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/overlap.py", Lang = "python", Size = 2000, Lines = 100,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks([
            new ChunkRecord { FileId = overlapFileId, ChunkIndex = 0, StartLine = 1, EndLine = 80, Content = "# overlap_marker\ndef func_a():\n    pass\n" + string.Concat(Enumerable.Repeat("# filler\n", 76)) },
            new ChunkRecord { FileId = overlapFileId, ChunkIndex = 1, StartLine = 71, EndLine = 150, Content = "# overlap_marker\ndef func_b():\n    pass\n" + string.Concat(Enumerable.Repeat("# filler\n", 76)) },
        ]);

        var results = _reader.Search("overlap_marker", limit: 10);

        // Should deduplicate: only 1 result from overlap.py (higher ranked chunk kept)
        // 重複排除: overlap.py からは1件のみ（上位ランクのチャンクを保持）
        var overlapResults = results.Where(r => r.Path == "src/overlap.py").ToList();
        Assert.Single(overlapResults);
    }

    [Fact]
    public void Search_PrefersDefinitionFileOverReferenceOnlySourceFile()
    {
        var refFileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/session.py", Lang = "python", Size = 300, Lines = 10,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks([new ChunkRecord
        {
            FileId = refFileId, ChunkIndex = 0, StartLine = 1, EndLine = 3,
            Content = "def login(user, password):\n    return authenticate(user, password)\n",
        }]);

        var results = _reader.Search("authenticate", limit: 2);

        Assert.Equal(2, results.Count);
        Assert.Equal("src/auth.py", results[0].Path);
        Assert.Equal("src/session.py", results[1].Path);
    }

    [Fact]
    public void Search_ReturnsEmptyForNoMatch()
    {
        var results = _reader.Search("nonexistent_term_xyz");
        Assert.Empty(results);
    }

    [Fact]
    public void Search_FiltersByLanguage()
    {
        // "fetch" appears in JS only / "fetch"はJSのみに存在
        var jsResults = _reader.Search("fetch", lang: "javascript");
        Assert.NotEmpty(jsResults);

        var pyResults = _reader.Search("fetch", lang: "python");
        Assert.Empty(pyResults);
    }

    [Theory]
    [InlineData("xaml")]
    [InlineData("axaml")]
    public void Search_FiltersByXamlLanguageAliases(string lang)
    {
        var queryToken = $"xaml_alias_{Guid.NewGuid():N}";

        InsertIndexedFile(
            "src/MainWindow.xaml",
            "xml",
            $$"""
            <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <Grid>
                    <TextBlock Text="{{queryToken}}" />
                </Grid>
            </Window>
            """);

        var results = _reader.Search(queryToken, lang: lang);

        Assert.Single(results);
        Assert.Equal("src/MainWindow.xaml", results[0].Path);
    }

    [Theory]
    [InlineData("cshtml")]
    [InlineData("razor")]
    public void Search_FiltersByCSharpRazorAliases(string lang)
    {
        var queryToken = $"razor_lang_alias_{Guid.NewGuid():N}";
        InsertIndexedFile(
            "web/Views/Home/Index.cshtml",
            "csharp",
            $@"@{{
    var marker = ""{queryToken}"";
}}");

        var results = _reader.Search(queryToken, lang: lang);

        Assert.Single(results);
        Assert.Equal("web/Views/Home/Index.cshtml", results[0].Path);
    }

    [Fact]
    public void Search_RespectsLimit()
    {
        var results = _reader.Search("return", limit: 1);
        Assert.Single(results);
    }

    [Fact]
    public void Search_RawQuerySupportsFtsPrefixSyntax()
    {
        var results = _reader.Search("auth*", rawQuery: true);

        Assert.Single(results);
        Assert.Equal("src/auth.py", results[0].Path);
    }

    [Fact]
    public void Search_CjkSubstringDoesNotMatchLongerTokenByDefault()
    {
        // Issue #1519: the default literal-safe path is strict — a bare CJK query must NOT
        // auto-widen into a prefix phrase, so `search 計算` no longer also returns content
        // containing `計算する`/`計算機`/`計算結果`. Users opt in via the `prefix` flag or by
        // appending `*` to the token; see the matching opt-in tests below. Earlier versions
        // unconditionally promoted every CJK token to an FTS5 prefix phrase because the
        // default unicode61 tokenizer indexes `計算する` as a single token, but that silently
        // widened exact CJK identifier lookups and was reported as a relevance regression.
        // Issue #1519: literal-safe 経路の既定挙動は strict — 素の CJK クエリを自動で prefix
        // phrase に昇格させないため、`search 計算` は `計算する`/`計算機`/`計算結果` を含むコードに
        // マッチしない。広げたい場合は `prefix` フラグか末尾 `*` でオプトインする（下のテストで
        // 別途固定）。以前は unicode61 が `計算する` を単一トークンとして indexing する事情から
        // 無条件に prefix 昇格させていたが、CJK 識別子の厳密検索が静かに広がっていたという
        // 不具合報告に基づく挙動変更。
        InsertIndexedFile("src/cjk_strict.py", "python",
            "def 計算する(値):\n    return 値 * 2\n");

        var results = _reader.Search("計算");

        Assert.DoesNotContain(results, r => r.Path == "src/cjk_strict.py");
    }

    [Fact]
    public void Search_CjkSubstringMatchesLongerTokenWhenPrefixFlagSet()
    {
        // Opt-in counterpart to the strict-default test above. Passing `prefix: true`
        // promotes every token in the query to an FTS5 prefix phrase, restoring the
        // "search 計算 finds 計算する" behavior on demand — the `--prefix` CLI flag and
        // MCP `prefix` argument route here. This is the documented escape hatch for
        // unicode61's CJK single-token tokenization.
        // strict 既定の opt-in 版: `prefix: true` で全トークンを FTS5 prefix phrase に昇格させ、
        // 「`search 計算` が `計算する` を見つける」挙動をオンデマンドで復元する。CLI の
        // `--prefix` と MCP の `prefix` 引数はここを通る。unicode61 の CJK 単一トークン化に
        // 対する正規のエスケープハッチ。
        InsertIndexedFile("src/cjk_prefix.py", "python",
            "def 計算する(値):\n    return 値 * 2\n");

        var results = _reader.Search("計算", prefix: true);

        Assert.Contains(results, r => r.Path == "src/cjk_prefix.py");
    }

    [Fact]
    public void Search_CjkSubstringMatchesLongerTokenWhenTokenEndsWithAsterisk()
    {
        // Per-token opt-in: appending `*` to a single CJK token in the literal-safe path
        // promotes that token (and only that token) to an FTS5 prefix phrase. This is the
        // ergonomic shorthand for users who type `cdidx search 計算*` directly without
        // adding the global `--prefix` flag. The trailing `*` is stripped from the literal
        // before quoting so the resulting FTS expression is `"計算"*`, not `"計算*"`.
        // トークン単位の opt-in: literal-safe 経路で CJK トークン末尾に `*` を付けると、
        // そのトークンのみが FTS5 prefix phrase に昇格する。`cdidx search 計算*` のような
        // 直接入力向けの shorthand。末尾 `*` は引用前に取り除かれ、最終的な FTS 式は
        // `"計算*"` ではなく `"計算"*` になる。
        InsertIndexedFile("src/cjk_asterisk.py", "python",
            "def 計算する(値):\n    return 値 * 2\n");

        var results = _reader.Search("計算*");

        Assert.Contains(results, r => r.Path == "src/cjk_asterisk.py");
    }

    [Fact]
    public void Search_CjkFullTokenQueryStillFindsExactFullToken()
    {
        // Positive regression: searching '計算する' against content '計算する' continues to
        // match under the new strict-by-default policy — unicode61 indexes both as the same
        // single token, so the literal-safe phrase `"計算する"` finds it without needing the
        // prefix opt-in. Pinning this keeps the strict-default change from accidentally
        // removing exact CJK hits along with the auto-widening.
        // 正の回帰テスト: 新しい strict 既定でも '計算する' のクエリは '計算する' を含む内容に
        // 一致する。unicode61 は両方を同じ単一トークンとして indexing するため、literal-safe の
        // phrase `"計算する"` で prefix opt-in なしに見つかる。strict 化が auto-widening と一緒に
        // exact マッチまで巻き込んで取りこぼさないことを固定する。
        InsertIndexedFile("src/cjk_exact.py", "python",
            "def 計算する(値):\n    return 値\n");

        var results = _reader.Search("計算する");

        Assert.Contains(results, r => r.Path == "src/cjk_exact.py");
    }

    [Fact]
    public void Search_CjkFullTokenQueryDoesNotWidenToLongerTokenByDefault()
    {
        // The strict-default policy must also block widening when the query is itself a
        // full token. Searching '計算する' must NOT also return content containing
        // '計算する追加', because that file's indexed token is '計算する追加' — a different
        // token. Pinning this stops a future revert that resurrects unconditional CJK
        // prefix promotion: such a regression would re-break #1519 by widening '計算する'
        // back into longer-token matches. Users who need that broad reach pass
        // `prefix: true` or append `*` and get it back explicitly.
        // クエリが完全トークンであっても strict 既定では拡張しない。`計算する` の検索は
        // `計算する追加` を含むファイル（インデックス上は別トークン）まで広げてはならない。
        // 無条件 CJK prefix 昇格を将来復活させる差分があった場合、このテストが #1519 の
        // 再発（`計算する` が `計算する追加` まで広がる）を捕える。広く拾いたい場合は
        // `prefix: true` か末尾 `*` で明示的にオプトインする。
        InsertIndexedFile("src/cjk_widen_short.py", "python",
            "def 計算する(値):\n    return 値\n");
        InsertIndexedFile("src/cjk_widen_long.py", "python",
            "def 計算する追加(値):\n    return 値 + 1\n");

        var results = _reader.Search("計算する");

        Assert.Contains(results, r => r.Path == "src/cjk_widen_short.py");
        Assert.DoesNotContain(results, r => r.Path == "src/cjk_widen_long.py");
    }

    [Fact]
    public void Search_AsciiTokenWithoutPrefixFlagDoesNotMatchLongerToken()
    {
        // The strict-default policy applies uniformly to all scripts, not just CJK. A bare
        // ASCII query 'auth' must NOT auto-widen to 'authenticate' under literal-safe
        // sanitization — the user types `cdidx search auth*` (or passes `--prefix`) to
        // opt into prefix expansion. Pinning this prevents future drift where the strict
        // default is preserved for CJK but quietly skipped for ASCII.
        // strict 既定は CJK だけでなく全スクリプトに一様に適用される。素の ASCII クエリ 'auth' は
        // literal-safe サニタイザの下で 'authenticate' へ自動拡張してはならない — ユーザーは
        // `cdidx search auth*`（または `--prefix`）で明示的にオプトインする。CJK では strict を
        // 守りつつ ASCII では静かに skip するドリフトを将来防ぐためにここを固定する。
        InsertIndexedFile("src/ascii_strict.py", "python",
            "def authenticate(user):\n    return True\n");

        var results = _reader.Search("auth");

        Assert.DoesNotContain(results, r => r.Path == "src/ascii_strict.py");
    }

    [Fact]
    public void Search_AsciiTokenMatchesLongerTokenWhenPrefixFlagSet()
    {
        // Opt-in counterpart: passing `prefix: true` widens an ASCII query to match longer
        // tokens that start with it, restoring the `auth` → `authenticate` reach.
        // ASCII クエリの opt-in 版: `prefix: true` を渡すと先頭一致するより長いトークンに
        // 広げ、`auth` → `authenticate` の到達性を復元する。
        InsertIndexedFile("src/ascii_prefix.py", "python",
            "def authenticate(user):\n    return True\n");

        var results = _reader.Search("auth", prefix: true);

        Assert.Contains(results, r => r.Path == "src/ascii_prefix.py");
    }

    [Fact]
    public void Search_AsciiTokenMatchesLongerTokenWhenTokenEndsWithAsterisk()
    {
        // Per-token opt-in for ASCII: appending `*` to the token promotes that token (and
        // only that token) to an FTS5 prefix phrase. Mirrors `Search 計算*` semantics for
        // ASCII identifiers so `cdidx search auth*` reaches `authenticate` without a flag.
        // ASCII トークン単位の opt-in: 末尾に `*` を付けるとそのトークンのみ FTS5 prefix
        // phrase に昇格する。`Search 計算*` と同じ挙動を ASCII 識別子でも提供し、`cdidx search
        // auth*` がフラグ無しで `authenticate` に到達する。
        InsertIndexedFile("src/ascii_asterisk.py", "python",
            "def authenticate(user):\n    return True\n");

        var results = _reader.Search("auth*");

        Assert.Contains(results, r => r.Path == "src/ascii_asterisk.py");
    }

    [Fact]
    public void Search_CjkPrefixOptInDoesNotMatchUnrelatedCjkTokens()
    {
        // Under `prefix: true`, the FTS5 prefix expansion must still widen only to tokens
        // that literally start with the query codepoints. An unrelated CJK word like '検索'
        // must not match '計算' even though both are CJK single-token runs under unicode61.
        // Locks the safety boundary of the opt-in widening.
        // `prefix: true` でも FTS5 prefix 拡張はクエリのコードポイントから始まるトークンにのみ
        // 限定される。'検索' のような無関係な CJK 語が、同じく unicode61 で単一トークン扱いされる
        // からといって '計算' にマッチしてはならない。opt-in 拡張の安全境界を固定する。
        InsertIndexedFile("src/cjk_match.py", "python",
            "def 計算する(値):\n    return 値\n");
        InsertIndexedFile("src/cjk_unrelated.py", "python",
            "def 検索する(値):\n    return 値\n");

        var results = _reader.Search("計算", prefix: true);

        Assert.Contains(results, r => r.Path == "src/cjk_match.py");
        Assert.DoesNotContain(results, r => r.Path == "src/cjk_unrelated.py");
    }

    [Fact]
    public void Search_EmojiMixedTokenDoesNotPrefixWidenToAsciiNeighbors()
    {
        // Regression guard for the most damaging over-widening case: if an emoji-mixed
        // token was auto-upgraded to a prefix phrase (earlier in this fix's iterations it
        // was), unicode61 would strip the emoji and the query would become a pure ASCII
        // prefix search ('"foo"*') — sweeping in unrelated neighbors like 'foobar'. The
        // sanitizer must therefore NOT add a prefix '*' to emoji-mixed tokens. Note: this
        // only protects against PREFIX widening (neighbors that merely start with the
        // ASCII fragment). It does NOT and cannot claim "exact-phrase semantics" against
        // content where unicode61 indexes an identical ASCII token — see the companion
        // `Search_EmojiMixedTokenFallsBackToAsciiToken_UseExactForStrict` pin.
        // 最大の over-widening 回帰防止: emoji 混在トークンに prefix '*' が付くと、unicode61 が
        // emoji を drop するため実質 '"foo"*' となり 'foobar' のような無関係な近傍を拾う。
        // サニタイザは emoji 混在トークンに prefix を付与してはならない。ただしこれは
        // 「prefix 拡張を防ぐ」までで、unicode61 が同じ ASCII トークンを indexing した内容に
        // 対して完全一致を保証するものではない（下記の companion pin を参照）。
        InsertIndexedFile("src/emoji_mixed.py", "python",
            "def foo🎉():\n    return 1\n");
        InsertIndexedFile("src/ascii_prefix_neighbor.py", "python",
            "def foobar():\n    return 2\n");

        var results = _reader.Search("foo🎉");

        Assert.Contains(results, r => r.Path == "src/emoji_mixed.py");
        Assert.DoesNotContain(results, r => r.Path == "src/ascii_prefix_neighbor.py");
    }

    [Fact]
    public void Search_EmojiMixedTokenFallsBackToAsciiToken_UseExactForStrict()
    {
        // Known limitation pin: unicode61 drops emoji codepoints during BOTH indexing and
        // query tokenization, so 'foo🎉' is indexed as the FTS token 'foo' and a literal
        // query 'foo🎉' is tokenized as the FTS phrase '"foo"'. The FTS path therefore
        // cannot distinguish between `def foo():` and `def foo🎉():` — both are FTS-equal.
        // Users who need strict equality over emoji must route through the exact-substring
        // path (`--exact` on the CLI, which uses SQLite `instr` against raw content and
        // bypasses unicode61 tokenization entirely). This test pins that limitation so
        // documentation and CHANGELOG cannot silently claim "exact-phrase semantics".
        // 既知の制限の固定: unicode61 は indexing とクエリの両段階で emoji を drop するため、
        // 'foo🎉' は FTS トークンとしては 'foo' と同じになる。FTS 経路では `def foo():` と
        // `def foo🎉():` を区別できず、完全一致が必要なら `--exact` 経路（SQLite `instr`）を
        // 使う必要がある。文書・CHANGELOG がこの制限を見落として「完全一致を保つ」と誤って
        // 謳わないよう、挙動を明示的に固定する。
        InsertIndexedFile("src/emoji_mixed_fallback.py", "python",
            "def foo🎉():\n    return 1\n");
        InsertIndexedFile("src/ascii_exact_twin.py", "python",
            "def foo():\n    return 3\n");

        var ftsResults = _reader.Search("foo🎉");

        // FTS path cannot distinguish — both show up because unicode61 drops '🎉' on both sides.
        // FTS 経路では区別できない — unicode61 が両側で '🎉' を drop するため。
        Assert.Contains(ftsResults, r => r.Path == "src/emoji_mixed_fallback.py");
        Assert.Contains(ftsResults, r => r.Path == "src/ascii_exact_twin.py");

        // The exact path DOES distinguish via instr() on raw content.
        // exact 経路は instr() により区別できる。
        var exactResults = _reader.Search("foo🎉", exact: true);
        Assert.Contains(exactResults, r => r.Path == "src/emoji_mixed_fallback.py");
        Assert.DoesNotContain(exactResults, r => r.Path == "src/ascii_exact_twin.py");
    }

    [Fact]
    public void Search_LatinDiacriticTokenDoesNotWidenToPrefixSearch()
    {
        // Latin-diacritic tokens (e.g. 'naïve') are tokenized normally by unicode61, and
        // under the strict-default literal-safe path no automatic prefix promotion fires —
        // not for CJK, not for Latin, not for any script. A literal 'naïve' query must
        // therefore find 'def naïve():' but not silently widen to 'def naïvety():'. This
        // test predates the strict-by-default change but still locks the same guarantee:
        // ordinary literal queries do not auto-prefix into neighboring tokens.
        // Latin 系ダイアクリティカル付きトークン（例: 'naïve'）は unicode61 で通常トークン化される。
        // strict 既定の literal-safe 経路では、CJK でも Latin でも自動 prefix 昇格は起きないため、
        // 'naïve' のリテラルクエリは 'def naïve():' を見つけても 'def naïvety():' へ静かに広がっては
        // ならない。本テストは strict 化以前から存在するが、保証する性質は同じ — 通常のリテラル
        // クエリは隣接トークンへ自動 prefix 拡張しない。
        InsertIndexedFile("src/latin_exact.py", "python",
            "def naïve():\n    return 1\n");
        InsertIndexedFile("src/latin_longer.py", "python",
            "def naïvety():\n    return 2\n");

        var results = _reader.Search("naïve");

        Assert.Contains(results, r => r.Path == "src/latin_exact.py");
        Assert.DoesNotContain(results, r => r.Path == "src/latin_longer.py");
    }

    [Fact]
    public void Search_PrefixOptInCoversNonBmpCjkExtensionH()
    {
        // Regression guard that `prefix: true` (the `--prefix` opt-in) widens correctly
        // into CJK Unified Ideographs Extension H (U+31350..U+323AF, Unicode 15.0). These
        // codepoints are non-BMP (supplementary plane) so they surface in .NET strings as
        // surrogate pairs — the sanitizer's token walk must therefore handle surrogate
        // pairs. Without the opt-in (or trailing `*`), this query returns 0 hits under
        // the strict default; with the opt-in, it must reach `𱍐abc` content.
        // `prefix: true`（`--prefix` opt-in）が CJK Extension H (U+31350..U+323AF,
        // Unicode 15.0) を正しく広げることの回帰テスト。これらは非 BMP（補助面）コードポイントで
        // .NET 文字列ではサロゲートペアとして現れるため、サニタイザのトークン走査がサロゲートを
        // 正しく扱う必要がある。opt-in（または末尾 `*`）がないと strict 既定では 0 件、opt-in を
        // 渡せば `𱍐abc` を含む内容に到達する。
        var extensionHChar = char.ConvertFromUtf32(0x31350);
        InsertIndexedFile("src/ext_h.py", "python",
            $"def {extensionHChar}abc(x):\n    return x\n");

        var results = _reader.Search(extensionHChar, prefix: true);

        Assert.Contains(results, r => r.Path == "src/ext_h.py");
    }

    [Fact]
    public void Search_PrefixOptInCoversNonBmpCjkExtensionI()
    {
        // Regression guard that `prefix: true` covers CJK Unified Ideographs Extension I
        // (U+2EBF0..U+2EE5F, Unicode 15.1, added 2023). Same non-BMP / surrogate-pair concern
        // as Extension H, pinned separately so a later cleanup dropping either range breaks
        // its own dedicated test instead of silently regressing.
        // `prefix: true` が CJK Extension I (U+2EBF0..U+2EE5F, Unicode 15.1) を網羅することの
        // 回帰テスト。Extension H と同じく非 BMP だが、どちらかの範囲を「整理」で外すと
        // それぞれ固有のテストが壊れるよう、別テストとして固定する。
        var extensionIChar = char.ConvertFromUtf32(0x2EBF0);
        InsertIndexedFile("src/ext_i.py", "python",
            $"def {extensionIChar}abc(x):\n    return x\n");

        var results = _reader.Search(extensionIChar, prefix: true);

        Assert.Contains(results, r => r.Path == "src/ext_i.py");
    }

    [Fact]
    public void Search_PrefixOptInCoversIdeographicIterationMark()
    {
        // Regression guard that `prefix: true` covers Han-script codepoints outside the CJK
        // Unified Ideographs blocks. '々' (U+3005, ideographic iteration mark) is Unicode
        // script=Han but lives in the CJK Symbols and Punctuation block. unicode61 keeps it
        // as a word character, so under the strict default `search '々'` returns 0 results
        // against `々abc`; with the opt-in it must match.
        // `prefix: true` が CJK Unified Ideographs 範囲外の Han script コードポイントを
        // 網羅することの回帰テスト。'々' (U+3005) は Unicode script=Han だが CJK Symbols and
        // Punctuation ブロックに属する。unicode61 では単語文字扱いなので、strict 既定では
        // `search '々'` が `々abc` に対し 0 件を返すが opt-in を渡せばマッチする。
        InsertIndexedFile("src/iter_mark.py", "python",
            "def 々abc(x):\n    return x\n");

        var results = _reader.Search("々", prefix: true);

        Assert.Contains(results, r => r.Path == "src/iter_mark.py");
    }

    [Fact]
    public void Search_PrefixOptInCoversIdeographicZero()
    {
        // Same concern as 々 above but for '〇' (U+3007, ideographic number zero).
        // 上の 々 と同様、'〇' (U+3007) についての回帰テスト。
        InsertIndexedFile("src/ideograph_zero.py", "python",
            "def 〇abc(x):\n    return x\n");

        var results = _reader.Search("〇", prefix: true);

        Assert.Contains(results, r => r.Path == "src/ideograph_zero.py");
    }

    [Fact]
    public void Search_PrefixOptInCoversHalfwidthHangul()
    {
        // Regression guard that `prefix: true` covers halfwidth Hangul letters
        // (U+FFA0..U+FFDC). unicode61 keeps them as word characters, so under the strict
        // default `search 'ﾱ'` returns 0 against `ﾱﾲﾳabc`. The halfwidth range extends past
        // U+FF9F (halfwidth Katakana) to U+FFDC — pinning that the sanitizer's tokenizer
        // walk hands these to FTS5 prefix expansion correctly when opted in.
        // `prefix: true` が半角ハングル (U+FFA0..U+FFDC) を網羅することの回帰テスト。
        // unicode61 は単語文字扱いなので strict 既定では `search 'ﾱ'` が `ﾱﾲﾳabc` に対し 0 件。
        // 半角範囲は U+FF9F（半角カナ）を越えて U+FFDC まで広がる — サニタイザのトークン走査が
        // opt-in 時に FTS5 prefix 拡張へ正しく渡すことを固定する。
        InsertIndexedFile("src/halfwidth_hangul.py", "python",
            "def ﾱﾲﾳabc(x):\n    return x\n");

        var results = _reader.Search("ﾱ", prefix: true);

        Assert.Contains(results, r => r.Path == "src/halfwidth_hangul.py");
    }

    [Fact]
    public void Search_PrefixOptInCoversVerticalKanaRepeatMark()
    {
        // Regression guard that `prefix: true` covers the vertical kana repeat mark block
        // (U+3031..U+3035), Unicode category Lm (Letter Modifier). Used in vertical-text
        // Japanese as iteration marks; unicode61 keeps them as word characters.
        // `prefix: true` が縦書き仮名反復記号（U+3031..U+3035、Unicode カテゴリ Lm）を
        // 網羅することの回帰テスト。unicode61 では単語文字として扱われる。
        InsertIndexedFile("src/vertical_kana.py", "python",
            "def 〱abc(x):\n    return x\n");

        var results = _reader.Search("〱", prefix: true);

        Assert.Contains(results, r => r.Path == "src/vertical_kana.py");
    }

    [Fact]
    public void Search_PrefixOptInCoversBopomofo()
    {
        // Regression guard that `prefix: true` covers Bopomofo (U+3100..U+312F), the Mandarin
        // Chinese phonetic system ("zhuyin"). Bopomofo letters are Unicode category Lo and
        // survive unicode61 tokenization as regular word characters.
        // `prefix: true` が注音符号（ボポモフォ、U+3100..U+312F、中国語発音）を網羅することの
        // 回帰テスト。Unicode カテゴリ Lo で unicode61 は単語文字として保つ。
        InsertIndexedFile("src/bopomofo.py", "python",
            "def ㄅabc(x):\n    return x\n");

        var results = _reader.Search("ㄅ", prefix: true);

        Assert.Contains(results, r => r.Path == "src/bopomofo.py");
    }

    [Fact]
    public void Search_PrefixOptInCoversBopomofoExtended()
    {
        // Regression guard that `prefix: true` covers Bopomofo Extended (U+31A0..U+31BF),
        // which extends zhuyin with additional phonetic letters used for minority Chinese
        // dialects (e.g. Min Nan, Hakka). Pinned separately from Bopomofo so a later cleanup
        // that drops either range breaks its own dedicated test.
        // `prefix: true` が拡張注音符号（U+31A0..U+31BF、閩南語や客家語等の発音）を網羅すること
        // の回帰テスト。Bopomofo と同じく単語文字扱いなので、それぞれの範囲を独立に固定する。
        InsertIndexedFile("src/bopomofo_ext.py", "python",
            "def ㆠabc(x):\n    return x\n");

        var results = _reader.Search("ㆠ", prefix: true);

        Assert.Contains(results, r => r.Path == "src/bopomofo_ext.py");
    }

    [Fact]
    public void Search_PrefixOptInCoversYiSyllable()
    {
        // Regression guard that `prefix: true` covers Yi Syllables (U+A000..U+A48F), the
        // syllabary used by the Nuosu (Yi) people in southwestern China. Yi syllables are
        // Unicode category Lo; unicode61 keeps them as word characters. Yi Radicals
        // (U+A490..U+A4CF) are intentionally excluded upstream because they are category So
        // and dropped by unicode61.
        // `prefix: true` が彝文字音節（Yi Syllables、U+A000..U+A48F、中国南西部のノス族の文字
        // 体系）を網羅することの回帰テスト。Unicode カテゴリ Lo で unicode61 は単語文字として
        // 扱う。彝文字部首（Yi Radicals、U+A490..U+A4CF）は Unicode カテゴリ So のため上流で
        // 意図的に除外。
        InsertIndexedFile("src/yi_syllables.py", "python",
            "def ꀀabc(x):\n    return x\n");

        var results = _reader.Search("ꀀ", prefix: true);

        Assert.Contains(results, r => r.Path == "src/yi_syllables.py");
    }

    [Fact]
    public void Search_PrefixOptInCoversNonBmpTangut()
    {
        // Regression guard that `prefix: true` covers Tangut (U+17000..U+187FF), a non-BMP
        // historical East Asian logographic script used by the Western Xia empire
        // (11th–13th century). Non-BMP, so the sanitizer's token walk must be
        // surrogate-pair aware to hand the right rune to FTS5 prefix expansion.
        // `prefix: true` が西夏文字（Tangut、U+17000..U+187FF、西夏帝国の非 BMP 表意文字）を
        // 網羅することの回帰テスト。非 BMP のため、サニタイザのトークン走査がサロゲートペアを
        // 正しく扱う必要がある。
        var tangutChar = char.ConvertFromUtf32(0x17000);
        InsertIndexedFile("src/tangut.py", "python",
            $"def {tangutChar}abc(x):\n    return x\n");

        var results = _reader.Search(tangutChar, prefix: true);

        Assert.Contains(results, r => r.Path == "src/tangut.py");
    }

    [Fact]
    public void Search_PrefixOptInCoversNonBmpTangutComponents()
    {
        // Regression guard that `prefix: true` covers Tangut Components (U+18800..U+18AFF),
        // the non-BMP block of radical / stroke components used to build Tangut logographs.
        // Separate Unicode block from Tangut itself, so this test exercises its own range
        // rather than aliasing to the Tangut test.
        // `prefix: true` が西夏文字部品（Tangut Components、U+18800..U+18AFF、非 BMP の西夏
        // 文字構成要素）を網羅することの回帰テスト。Tangut 本体とは別の Unicode ブロックなので
        // Tangut テストとエイリアス化せず専用範囲を検証する。
        var tangutComponentsChar = char.ConvertFromUtf32(0x18800);
        InsertIndexedFile("src/tangut_components.py", "python",
            $"def {tangutComponentsChar}abc(x):\n    return x\n");

        var results = _reader.Search(tangutComponentsChar, prefix: true);

        Assert.Contains(results, r => r.Path == "src/tangut_components.py");
    }

    [Fact]
    public void Search_PrefixOptInCoversNonBmpKhitanSmallScript()
    {
        // Regression guard that `prefix: true` covers Khitan Small Script (U+18B00..U+18CFF),
        // the non-BMP script of the Liao dynasty's Khitan people (10th–13th century).
        // Separate Unicode block from Tangut / Tangut Components / Tangut Supplement, so
        // this test exercises its own range.
        // `prefix: true` が契丹小字（Khitan Small Script、U+18B00..U+18CFF、遼朝の非 BMP
        // 表音文字）を網羅することの回帰テスト。Tangut / Tangut Components / Tangut
        // Supplement とは別の Unicode ブロック。
        var khitanChar = char.ConvertFromUtf32(0x18B00);
        InsertIndexedFile("src/khitan_small.py", "python",
            $"def {khitanChar}abc(x):\n    return x\n");

        var results = _reader.Search(khitanChar, prefix: true);

        Assert.Contains(results, r => r.Path == "src/khitan_small.py");
    }

    [Fact]
    public void Search_PrefixOptInCoversNonBmpTangutSupplement()
    {
        // Regression guard that `prefix: true` covers Tangut Supplement (U+18D00..U+18D8F),
        // the small non-BMP block added in Unicode 13.0 alongside Khitan Small Script.
        // Separate from Tangut / Tangut Components / Khitan.
        // `prefix: true` が西夏文字補助（Tangut Supplement、U+18D00..U+18D8F、Unicode 13.0 で
        // Khitan Small Script と同時追加された小規模な非 BMP ブロック）を網羅することの
        // 回帰テスト。Tangut / Tangut Components / Khitan とは別の範囲。
        var tangutSupplementChar = char.ConvertFromUtf32(0x18D00);
        InsertIndexedFile("src/tangut_supplement.py", "python",
            $"def {tangutSupplementChar}abc(x):\n    return x\n");

        var results = _reader.Search(tangutSupplementChar, prefix: true);

        Assert.Contains(results, r => r.Path == "src/tangut_supplement.py");
    }

    [Fact]
    public void Search_PrefixOptInCoversNonBmpTangutIterationMark()
    {
        // Regression guard that `prefix: true` covers the Tangut Iteration Mark (U+16FE0),
        // a non-BMP codepoint in the Ideographic Symbols and Punctuation block used to
        // annotate repeated Tangut characters. Unicode category Lm (Modifier Letter) on the
        // current runtime; unicode61 keeps Lm codepoints as word characters. The Ideographic
        // Symbols and Punctuation iteration / annotation codepoints (U+16FE0 Tangut, U+16FE1
        // Nüshu, U+16FE3 Old Chinese, U+16FE4 Khitan filler, U+16FF0 / U+16FF1 Vietnamese
        // reading marks) all need the surrogate-pair-aware walk; U+16FE2 (Po) is dropped by
        // unicode61 and must NOT ride along.
        // `prefix: true` が Tangut 反復記号（U+16FE0、非 BMP の Ideographic Symbols and
        // Punctuation ブロック）を網羅することの回帰テスト。現行ランタイムでは Unicode
        // カテゴリ Lm で unicode61 は単語文字として扱う。U+16FE0 / 16FE1 / 16FE3 / 16FE4 /
        // 16FF0 / 16FF1 はサロゲート対応の走査が必要。U+16FE2 (Po) は unicode61 が drop する
        // ため巻き込んではならない。
        var tangutIterationMark = char.ConvertFromUtf32(0x16FE0);
        InsertIndexedFile("src/tangut_iter.py", "python",
            $"def {tangutIterationMark}abc(x):\n    return x\n");

        var results = _reader.Search(tangutIterationMark, prefix: true);

        Assert.Contains(results, r => r.Path == "src/tangut_iter.py");
    }

    [Fact]
    public void Search_PrefixOptInCoversNonBmpKhitanSmallScriptFiller()
    {
        // Regression guard that `prefix: true` covers U+16FE4 (Khitan Small Script Filler),
        // a non-BMP codepoint in the Ideographic Symbols and Punctuation block. On the
        // current runtime this is Unicode category Mn (Nonspacing Mark); unicode61 still
        // keeps Mn codepoints as word characters.
        // `prefix: true` が契丹小字フィラー（U+16FE4、非 BMP の Ideographic Symbols and
        // Punctuation ブロック）を網羅することの回帰テスト。現行ランタイムでは Unicode
        // カテゴリ Mn。unicode61 は Mn も単語文字として扱う。
        var khitanFiller = char.ConvertFromUtf32(0x16FE4);
        InsertIndexedFile("src/khitan_filler.py", "python",
            $"def {khitanFiller}abc(x):\n    return x\n");

        var results = _reader.Search(khitanFiller, prefix: true);

        Assert.Contains(results, r => r.Path == "src/khitan_filler.py");
    }

    [Fact]
    public void Search_PrefixOptInCoversNonBmpVietnameseReadingMark()
    {
        // Regression guard that `prefix: true` covers U+16FF0 (Vietnamese Alternate Reading
        // Mark CA), a non-BMP codepoint in the Ideographic Symbols and Punctuation block
        // used to annotate Chu Nom (Han-based Vietnamese) text. On the current runtime this
        // is Unicode category Mc (Spacing Mark); unicode61 keeps Mc codepoints as word
        // characters.
        // `prefix: true` がベトナム語 Chu Nom 読み記号 CA（U+16FF0、非 BMP の Ideographic
        // Symbols and Punctuation ブロック）を網羅することの回帰テスト。現行ランタイムでは
        // Unicode カテゴリ Mc。unicode61 は Mc も単語文字として扱う。
        var vietnameseReadingMark = char.ConvertFromUtf32(0x16FF0);
        InsertIndexedFile("src/vietnamese_ca.py", "python",
            $"def {vietnameseReadingMark}abc(x):\n    return x\n");

        var results = _reader.Search(vietnameseReadingMark, prefix: true);

        Assert.Contains(results, r => r.Path == "src/vietnamese_ca.py");
    }

    [Fact]
    public void Search_PrefixOptInCoversNonBmpNushu()
    {
        // Regression guard that `prefix: true` covers Nüshu (U+1B170..U+1B2FF), a non-BMP
        // syllabic script historically used by women in Jiangyong County, Hunan, China.
        // Unicode category Lo; unicode61 keeps it as word characters. Non-BMP, so the
        // surrogate-pair-aware rune walk applies.
        // `prefix: true` が女書（Nüshu、U+1B170..U+1B2FF、中国湖南省江永県で女性たちが
        // 使った非 BMP 音節文字）を網羅することの回帰テスト。Unicode カテゴリ Lo で
        // unicode61 は単語文字として扱う。非 BMP のためサロゲート対応の走査が必要。
        var nushuChar = char.ConvertFromUtf32(0x1B170);
        InsertIndexedFile("src/nushu.py", "python",
            $"def {nushuChar}abc(x):\n    return x\n");

        var results = _reader.Search(nushuChar, prefix: true);

        Assert.Contains(results, r => r.Path == "src/nushu.py");
    }

    [Fact]
    public void Search_PrefixOptInCoversNonBmpKanaExtendedB()
    {
        // Regression guard that `prefix: true` covers Kana Extended-B (U+1AFF0..U+1AFFF,
        // Unicode 15.0). Non-BMP kana codepoints are represented as surrogate pairs in
        // .NET strings; the sanitizer must walk runes rather than chars.
        // `prefix: true` が Kana Extended-B (U+1AFF0..U+1AFFF, Unicode 15.0) を網羅すること
        // の回帰テスト。非 BMP の仮名は .NET 文字列ではサロゲートペアとして現れるため、
        // サニタイザは rune を走査する必要がある。
        var kanaExtendedBChar = char.ConvertFromUtf32(0x1AFF0);
        InsertIndexedFile("src/kana_ext_b.py", "python",
            $"def {kanaExtendedBChar}abc(x):\n    return x\n");

        var results = _reader.Search(kanaExtendedBChar, prefix: true);

        Assert.Contains(results, r => r.Path == "src/kana_ext_b.py");
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
    public void SearchSymbols_FindsByName()
    {
        var results = _reader.SearchSymbols("authenticate");
        Assert.Single(results);
        Assert.Equal("function", results[0].Kind);
        Assert.Equal("src/auth.py", results[0].Path);
    }

    [Fact]
    public void SearchSymbols_CSharpOperatorsConversionsAndIndexersUseNavigableNames()
    {
        InsertIndexedFile("src/csharp_special_names.cs", "csharp",
            """
            using System.Collections.Generic;

            public struct Money
            {
                public static (int whole, int cents) operator +(Money a, Money b) => (0, 0);
                public static Dictionary<string, int> operator -(Money a, Money b) => new();
                public static checked Money operator checked +(Money a, Money b) => new();
                public static implicit operator decimal(Money m) => 0m;
                public static explicit operator Money(decimal d) => new();
                public Money(decimal amount) { }
                public static explicit operator checked byte(Money m) => 0;
                public static explicit operator Dictionary<string,int>(Money m) => new();
                public static explicit operator (int whole,int cents)(Money m) => (0, 0);
                public static explicit operator (Dictionary<string, int> map, int count)?(Money m) => null;
                public static explicit operator (int[] items, int count)(Money m) => ([], 0);
                public static explicit operator ((int a, int b) pair, int count)(Money m) => ((0, 0), 0);
            }

            public class Bag
            {
                private string[] _items = new string[10];
                public string this[int i] { get => _items[i]; set => _items[i] = value; }
            }
            """);

        Assert.Single(_reader.SearchSymbols("operator +", kind: "function", lang: "csharp", exact: true, pathPatterns: ["csharp_special_names"]));
        Assert.Single(_reader.SearchSymbols("operator -", kind: "function", lang: "csharp", exact: true, pathPatterns: ["csharp_special_names"]));
        Assert.Single(_reader.SearchSymbols("operator checked +", kind: "function", lang: "csharp", exact: true, pathPatterns: ["csharp_special_names"]));
        Assert.Single(_reader.SearchSymbols("implicit operator decimal", kind: "function", lang: "csharp", exact: true, pathPatterns: ["csharp_special_names"]));
        Assert.Single(_reader.SearchSymbols("explicit operator Money", kind: "function", lang: "csharp", exact: true, pathPatterns: ["csharp_special_names"]));
        Assert.Single(_reader.SearchSymbols("explicit operator checked byte", kind: "function", lang: "csharp", exact: true, pathPatterns: ["csharp_special_names"]));
        Assert.Single(_reader.SearchSymbols("explicit operator Dictionary<string, int>", kind: "function", lang: "csharp", exact: true, pathPatterns: ["csharp_special_names"]));
        Assert.Single(_reader.SearchSymbols("explicit operator (int whole, int cents)", kind: "function", lang: "csharp", exact: true, pathPatterns: ["csharp_special_names"]));
        Assert.Single(_reader.SearchSymbols("explicit operator (Dictionary<string, int> map, int count)?", kind: "function", lang: "csharp", exact: true, pathPatterns: ["csharp_special_names"]));
        Assert.Single(_reader.SearchSymbols("explicit operator (int[] items, int count)", kind: "function", lang: "csharp", exact: true, pathPatterns: ["csharp_special_names"]));
        Assert.Single(_reader.SearchSymbols("explicit operator ((int a, int b) pair, int count)", kind: "function", lang: "csharp", exact: true, pathPatterns: ["csharp_special_names"]));
        Assert.Single(_reader.SearchSymbols("Money", kind: "function", lang: "csharp", exact: true, pathPatterns: ["csharp_special_names"]));
        Assert.Single(_reader.SearchSymbols("Item", kind: "function", lang: "csharp", exact: true, pathPatterns: ["csharp_special_names"]));
    }

    [Fact]
    public void SearchSymbols_AndDeps_DoNotTreatNamedArgumentLabelsAsLocalFunctions()
    {
        InsertIndexedFile("src/platform.cs", "csharp",
            """
            public class PlatformState
            {
                public static bool Detect() =>
                    new Options(
                        isWindows: OperatingSystem.IsWindows(),
                        isMacCatalyst: OperatingSystem.IsMacCatalyst()).Ready;
            }
            """);
        InsertIndexedFile("src/app.cs", "csharp",
            """
            public class App
            {
                public bool Read() => OperatingSystem.IsWindows() || OperatingSystem.IsMacCatalyst();
            }
            """);

        Assert.Empty(_reader.SearchSymbols("IsWindows", lang: "csharp"));
        Assert.Empty(_reader.SearchSymbols("IsMacCatalyst", lang: "csharp"));
        Assert.Empty(_reader.GetFileDependencies(lang: "csharp"));
    }

    [Fact]
    public void SearchSymbols_FindsAliasQualifiedExplicitInterfaceImplementations()
    {
        InsertIndexedFile("src/impl.cs", "csharp",
            """
            public interface IFoo
            {
                string Name();
                object Create();
            }

            public class Impl : IFoo
            {
                global::System.String IFoo.Name() => "x";
                Alias::Type IFoo.Create() => default;
            }
            """);

        var nameResults = _reader.SearchSymbols("Name", lang: "csharp");
        var createResults = _reader.SearchSymbols("Create", lang: "csharp");

        Assert.Contains(nameResults, s => s.Kind == "function" && s.Name == "Name" && s.ReturnType == "global::System.String");
        Assert.Contains(createResults, s => s.Kind == "function" && s.Name == "Create" && s.ReturnType == "Alias::Type");
    }

    [Fact]
    public void SearchSymbols_ReturnsRichMetadataWhenAvailable()
    {
        var results = _reader.SearchSymbols("fetchData");

        var symbol = Assert.Single(results);
        Assert.Equal(2, symbol.StartLine);
        Assert.Equal(3, symbol.EndLine);
        Assert.Equal(2, symbol.BodyStartLine);
        Assert.Equal(3, symbol.BodyEndLine);
        Assert.Equal("ApiClient", symbol.ContainerName);
        Assert.Equal("class", symbol.ContainerKind);
        Assert.Equal("async fetchData(url) {", symbol.Signature);
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
    public void SearchSymbols_MultipleNamesAreOrJoined()
    {
        var results = _reader.SearchSymbols(new[] { "authenticate", "fetchData" });
        var names = results.Select(r => r.Name).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "authenticate", "fetchData" }, names);
    }

    [Fact]
    public void SearchSymbols_MultiNameLimitStaysGlobalCap()
    {
        // `limit` must remain the total-result cap, not a per-name cap, so MCP payload / CLI output
        // size stays bounded. limit=1 with two requested names must return at most one row.
        // `limit` は合計の上限を維持すること。limit=1 で2名要求した場合も 1 行以下に収める。
        var capped = _reader.SearchSymbols(new[] { "authenticate", "fetchData" }, limit: 1);
        Assert.True(capped.Count <= 1, $"limit=1 must return <=1 row, got {capped.Count}");

        // Under a generous cap, round-robin merge must include every requested name at least once.
        // 十分な上限の下では、round-robin マージですべての要求名が少なくとも 1 行含まれること。
        var fair = _reader.SearchSymbols(new[] { "authenticate", "fetchData" }, limit: 10);
        var names = fair.Select(r => r.Name).Distinct().OrderBy(n => n).ToList();
        Assert.Equal(new[] { "authenticate", "fetchData" }, names);
    }

    [Fact]
    public void SearchSymbols_ExactMatchesNameEqualityAcrossMultipleNames()
    {
        // Seed a sibling symbol whose name contains `authenticate` as a substring so substring
        // mode returns both but exact mode returns only the exact-name rows per OR name.
        // exact=false は substring なので `authenticate_v2` も引き当てるが、exact=true は名前一致のみ。
        var extraFileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/auth_v2.py", Lang = "python", Size = 80, Lines = 4,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertSymbols([
            new SymbolRecord { FileId = extraFileId, Kind = "function", Name = "authenticate_v2", Line = 1, StartLine = 1, EndLine = 1 },
        ]);

        var substring = _reader.SearchSymbols(new[] { "authenticate", "fetchData" }, limit: 10, exact: false)
            .Select(r => r.Name).Distinct().OrderBy(n => n).ToList();
        Assert.Contains("authenticate", substring);
        Assert.Contains("authenticate_v2", substring);
        Assert.Contains("fetchData", substring);

        var exact = _reader.SearchSymbols(new[] { "authenticate", "fetchData" }, limit: 10, exact: true)
            .Select(r => r.Name).Distinct().OrderBy(n => n).ToList();
        Assert.Equal(new[] { "authenticate", "fetchData" }, exact);

        // Case-insensitive equality: the request's casing should not matter.
        // 大文字小文字を無視した完全一致であることを確認。
        var exactMixedCase = _reader.SearchSymbols(new[] { "AUTHENTICATE" }, limit: 10, exact: true)
            .Select(r => r.Name).Distinct().ToList();
        Assert.Equal(new[] { "authenticate" }, exactMixedCase);
    }

    [Fact]
    public void SearchSymbols_ExactFoldsNonAsciiCasing()
    {
        // #96: true Unicode CaseFold must catch accent/case pairs, sharp-S, Greek final sigma,
        // and width variants through `--exact`.
        // #96: Unicode CaseFold により accent/case、sharp-S、Greek final sigma、全角/半角を
        // `--exact` で同一視できることを確認する。
        var extraFileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/intl.py", Lang = "python", Size = 120, Lines = 6,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertSymbols([
            new SymbolRecord { FileId = extraFileId, Kind = "function", Name = "café_init", Line = 1, StartLine = 1, EndLine = 1 },
            new SymbolRecord { FileId = extraFileId, Kind = "function", Name = "Ｒｕｎ", Line = 2, StartLine = 2, EndLine = 2 }, // fullwidth
            new SymbolRecord { FileId = extraFileId, Kind = "function", Name = "Straße", Line = 3, StartLine = 3, EndLine = 3 },
            new SymbolRecord { FileId = extraFileId, Kind = "function", Name = "Σ", Line = 4, StartLine = 4, EndLine = 4 },
        ]);

        // Lowercase / uppercase Unicode should both land on the same folded row.
        // 大文字小文字違いでも folded 一致する。
        Assert.Single(_reader.SearchSymbols(new[] { "CAFÉ_INIT" }, limit: 10, exact: true));
        Assert.Single(_reader.SearchSymbols(new[] { "café_init" }, limit: 10, exact: true));

        // Sharp-S and final sigma are the classic Unicode CaseFold deltas over invariant lower.
        // sharp-S と final sigma は invariant-lower との差分が出る代表例。
        var sharpS = _reader.SearchSymbols(new[] { "STRASSE" }, limit: 10, exact: true)
            .Select(r => r.Name).ToList();
        Assert.Equal(new[] { "Straße" }, sharpS);

        var sigma = _reader.SearchSymbols(new[] { "ς" }, limit: 10, exact: true)
            .Select(r => r.Name).ToList();
        Assert.Equal(new[] { "Σ" }, sigma);

        // Fullwidth vs halfwidth: FormKC collapses them.
        // 全角/半角も FormKC 合成で同じになる。
        var halfwidth = _reader.SearchSymbols(new[] { "Run" }, limit: 10, exact: true)
            .Select(r => r.Name).OrderBy(n => n).ToList();
        Assert.Contains("Ｒｕｎ", halfwidth);
    }

    [Fact]
    public void SearchSymbols_ExactPrefersExactCaseOverFoldSibling()
    {
        InsertIndexedFile("src/a_case.py", "python",
            "def apiTwin():\n    return authenticate('a', 'b')\n");
        InsertIndexedFile("tests/z_case.py", "python",
            "def ApiTwin():\n    return authenticate('a', 'b')\n");

        var symbols = _reader.SearchSymbols(new[] { "ApiTwin" }, limit: 10, exact: true)
            .Where(r => r.Name is "ApiTwin" or "apiTwin")
            .Select(r => r.Name)
            .Distinct()
            .Take(2)
            .ToList();
        Assert.Equal(new[] { "ApiTwin", "apiTwin" }, symbols);

        var definitions = _reader.GetDefinitions("ApiTwin", limit: 10, exact: true)
            .Where(r => r.Name is "ApiTwin" or "apiTwin")
            .Select(r => r.Name)
            .Distinct()
            .Take(2)
            .ToList();
        Assert.Equal(new[] { "ApiTwin", "apiTwin" }, definitions);

        var topSymbol = Assert.Single(_reader.SearchSymbols(new[] { "ApiTwin" }, limit: 1, exact: true));
        Assert.Equal("ApiTwin", topSymbol.Name);
        Assert.Equal("tests/z_case.py", topSymbol.Path);

        var topDefinition = Assert.Single(_reader.GetDefinitions("ApiTwin", limit: 1, exact: true));
        Assert.Equal("ApiTwin", topDefinition.Name);
        Assert.Equal("tests/z_case.py", topDefinition.Path);
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
                Path = "src/a.py", Lang = "python", Size = 1, Lines = 1,
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
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(legacyPath)) File.Delete(legacyPath);
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
                    Path = "src/worker.cs", Lang = "csharp", Size = 40, Lines = 4,
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
    public void SearchSymbols_ExactFallsBackToNocaseWhenFoldKeyVersionMismatches()
    {
        // #86 codex third-pass review: when NameFold.Fold changes and bumps NameFold.Version,
        // previously stamped DBs must NOT be read through the folded equality path — their
        // stored keys were generated by the old fold function and comparing them against
        // queries folded with the new function silently misses.
        // Simulate by writing a mismatched `fold_key_version` into codeindex_meta and
        // confirming the reader falls back to NOCASE. Rebuild would restamp to current.
        // #86 3rd pass: fold_key_version 不一致時は NOCASE fallback に降格することを固定する。
        var mismatchPath = Path.Combine(Path.GetTempPath(), $"codeindex_fold_version_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new DbContext(mismatchPath);
            db.InitializeSchema();
            var writer = new DbWriter(db.Connection);
            var fileId = writer.UpsertFile(new FileRecord
            {
                Path = "src/a.py", Lang = "python", Size = 1, Lines = 1,
                Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            writer.InsertSymbols([
                new SymbolRecord { FileId = fileId, Kind = "function", Name = "authenticate", Line = 1, StartLine = 1, EndLine = 1 },
            ]);
            writer.MarkGraphReady();
            writer.MarkIssuesReady();
            writer.MarkFoldReady();

            // Now simulate a future version bump: overwrite the stored fold_key_version so the
            // reader sees a different stamped version than the current binary.
            // 未来の version bump を模擬: 記録された fold_key_version を書き換え、reader と
            // NameFold.Version を食い違わせる。
            writer.SetMeta("fold_key_version", "99");

            var reader = new DbReader(db.Connection);
            Assert.False(reader._foldReady);
            // ASCII equality still works via the NOCASE fallback path.
            Assert.Single(reader.SearchSymbols(new[] { "AUTHENTICATE" }, limit: 10, exact: true));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(mismatchPath)) File.Delete(mismatchPath);
        }
    }

    [Fact]
    public void SearchSymbols_ExactFallsBackToNocaseWhenFoldFingerprintMismatches()
    {
        // #97: runtime casing tables can drift across .NET upgrades even when
        // NameFold.Version stays constant. The persisted canary fingerprint must still
        // match the current runtime's observable fold output before folded keys are trusted.
        // #97: version が同じでも runtime drift はあり得るため、fingerprint 不一致時は
        // fold trusted を外して NOCASE fallback に降格する。
        var mismatchPath = Path.Combine(Path.GetTempPath(), $"codeindex_fold_fingerprint_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new DbContext(mismatchPath);
            db.InitializeSchema();
            var writer = new DbWriter(db.Connection);
            var fileId = writer.UpsertFile(new FileRecord
            {
                Path = "src/a.py", Lang = "python", Size = 1, Lines = 1,
                Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            writer.InsertSymbols([
                new SymbolRecord { FileId = fileId, Kind = "function", Name = "authenticate", Line = 1, StartLine = 1, EndLine = 1 },
            ]);
            writer.MarkGraphReady();
            writer.MarkIssuesReady();
            writer.MarkFoldReady();

            writer.SetMeta("fold_key_fingerprint", "DEADBEEFDEADBEEF");

            var reader = new DbReader(db.Connection);
            Assert.False(reader._foldReady);
            Assert.Single(reader.SearchSymbols(new[] { "AUTHENTICATE" }, limit: 10, exact: true));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(mismatchPath)) File.Delete(mismatchPath);
        }
    }

    [Fact]
    public void SearchSymbols_ExactFallsBackToNocaseWhenFoldNotReady()
    {
        // Legacy / partial-backfill DBs do not set FoldReadyFlag; the reader must silently
        // fall back to the ASCII `COLLATE NOCASE` path and still return correct ASCII results.
        // Non-ASCII casing is expected to miss (documented limitation until reindex).
        // Legacy DB は fold フラグ未設定なら NOCASE fallback。ASCII は動き続ける。
        var legacyPath = Path.Combine(Path.GetTempPath(), $"codeindex_fold_legacy_{Guid.NewGuid():N}.db");
        try
        {
            using var legacyDb = new DbContext(legacyPath);
            legacyDb.InitializeSchema();
            var writer = new DbWriter(legacyDb.Connection);
            var fileId = writer.UpsertFile(new FileRecord
            {
                Path = "src/a.py", Lang = "python", Size = 1, Lines = 1,
                Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            writer.InsertSymbols([
                new SymbolRecord { FileId = fileId, Kind = "function", Name = "authenticate", Line = 1, StartLine = 1, EndLine = 1 },
            ]);
            writer.MarkGraphReady();
            writer.MarkIssuesReady();
            // NOTE: intentionally do NOT stamp FoldReady.

            var legacyReader = new DbReader(legacyDb.Connection);
            Assert.False(legacyReader._foldReady);
            // ASCII case-insensitive equality still works via COLLATE NOCASE fallback.
            Assert.Single(legacyReader.SearchSymbols(new[] { "AUTHENTICATE" }, limit: 10, exact: true));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(legacyPath)) File.Delete(legacyPath);
        }
    }

    [Fact]
    public void SearchSymbols_ExactPredicateIsIndexable()
    {
        // Guard: the exact-match predicate must stay SARGable so SQLite can pick
        // idx_symbols_name_nocase instead of falling back to a full scan per query name.
        // Regression for the codex review of #81. `lower(col) = lower(@q)` is NOT SARGable;
        // `s.name = @q COLLATE NOCASE` is, given the COLLATE NOCASE index on symbols(name).
        // exact パスがインデックス（idx_symbols_name_nocase）を使える形に保つための回帰テスト。
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN SELECT s.name FROM symbols s WHERE s.name = @q COLLATE NOCASE";
        cmd.Parameters.AddWithValue("@q", "authenticate");
        using var reader = cmd.ExecuteReader();
        var plan = new System.Text.StringBuilder();
        while (reader.Read())
            plan.AppendLine(reader.GetString(3));
        var planText = plan.ToString();
        Assert.Contains("idx_symbols_name_nocase", planText);
        Assert.DoesNotContain("SCAN symbols", planText);
    }

    [Fact]
    public void SearchSymbols_LangKindPredicateUsesFileKindPlan()
    {
        // Guard #1933: keep the language + kind symbol query shaped so SQLite can
        // first resolve matching files via files(lang), then probe symbols(file_id, kind).
        // #1933: lang + kind のシンボル検索が idx_symbols_kind から全 kind を走査しないよう固定する。
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = @"
            EXPLAIN QUERY PLAN
            SELECT s.name
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE s.kind = @kind
              AND s.file_id IN (SELECT id FROM files WHERE lang = @lang)";
        cmd.Parameters.AddWithValue("@kind", "class");
        cmd.Parameters.AddWithValue("@lang", "javascript");
        using var reader = cmd.ExecuteReader();
        var plan = new System.Text.StringBuilder();
        while (reader.Read())
            plan.AppendLine(reader.GetString(3));
        var planText = plan.ToString();
        Assert.Contains("idx_symbols_file_kind", planText);
        Assert.Contains("idx_files_lang", planText);
        Assert.DoesNotContain("idx_symbols_kind", planText);
    }

    [Fact]
    public void SearchSymbols_EmptyNameListBehavesLikeNoFilter()
    {
        var all = _reader.SearchSymbols((IReadOnlyList<string>?)null);
        var empty = _reader.SearchSymbols(new string[0]);
        Assert.Equal(all.Count, empty.Count);
    }

    [Fact]
    public void SearchSymbols_FiltersByKind()
    {
        var classes = _reader.SearchSymbols(kind: "class");
        Assert.Single(classes);
        Assert.Equal("ApiClient", classes[0].Name);

        var functions = _reader.SearchSymbols(kind: "function");
        Assert.Equal(2, functions.Count);
    }

    [Fact]
    public void SearchSymbols_FiltersByLanguage()
    {
        var pySymbols = _reader.SearchSymbols(lang: "python");
        Assert.Single(pySymbols);

        var jsSymbols = _reader.SearchSymbols(lang: "javascript");
        Assert.Equal(2, jsSymbols.Count);
    }

    [Fact]
    public void SearchSymbols_AllFilters()
    {
        // Combine kind + lang filter / 種別+言語フィルタの組み合わせ
        var results = _reader.SearchSymbols(query: "fetch", kind: "function", lang: "javascript");
        Assert.Single(results);
        Assert.Equal("fetchData", results[0].Name);
    }

    [Fact]
    public void SearchSymbols_ExcludeTests_RemovesLikelyTestPaths()
    {
        var testFileId = _writer.UpsertFile(new FileRecord
        {
            Path = "tests/auth_test.py", Lang = "python", Size = 300, Lines = 10,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertSymbols([
            new SymbolRecord { FileId = testFileId, Kind = "function", Name = "authenticate", Line = 1, StartLine = 1, EndLine = 1 },
        ]);

        var results = _reader.SearchSymbols(query: "authenticate", excludeTests: true);

        Assert.Single(results);
        Assert.Equal("src/auth.py", results[0].Path);
    }

    [Fact]
    public void SearchSymbols_And_Search_ExcludeTests_KeepMidWordFilenames()
    {
        InsertIndexedFile("src/latest.py", "python", "def marker():\n    return 'latest'");
        InsertIndexedFile("src/request.py", "python", "def marker():\n    return 'request'");
        InsertIndexedFile("src/contest.py", "python", "def marker():\n    return 'contest'");
        InsertIndexedFile("src/fastest.py", "python", "def marker():\n    return 'fastest'");
        InsertIndexedFile("tests/test_foo.py", "python", "def marker():\n    return 'test_foo'");
        InsertIndexedFile("foo_test.py", "python", "def marker():\n    return 'foo_test'");
        InsertIndexedFile("test_foo.py", "python", "def marker():\n    return 'root_test_foo'");
        InsertIndexedFile("src/tests.py", "python", "def marker():\n    return 'tests'");

        var expectedPaths = new[]
        {
            "src/contest.py",
            "src/fastest.py",
            "src/latest.py",
            "src/request.py",
        };

        var symbolPaths = _reader.SearchSymbols(query: "marker", excludeTests: true)
            .Select(result => result.Path)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedPaths, symbolPaths);

        var searchPaths = _reader.Search("marker", excludeTests: true)
            .Select(result => result.Path)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedPaths, searchPaths);
    }

    [Fact]
    public void Search_ExcludeTests_RemovesLikelyTestPaths()
    {
        var testFileId = _writer.UpsertFile(new FileRecord
        {
            Path = "tests/auth_test.py", Lang = "python", Size = 300, Lines = 10,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks([new ChunkRecord
        {
            FileId = testFileId, ChunkIndex = 0, StartLine = 1, EndLine = 3,
            Content = "def authenticate_test_case():\n    authenticate('a', 'b')\n    return True",
        }]);

        var results = _reader.Search("authenticate", limit: 5, excludeTests: true);

        Assert.Single(results);
        Assert.Equal("src/auth.py", results[0].Path);
    }

    [Fact]
    public void SearchReferences_FindsIndexedCallSites()
    {
        InsertIndexedFile("src/session.py", "python", "def login(user, password):\n    return authenticate(user, password)\n");

        var results = _reader.SearchReferences("authenticate");

        var reference = Assert.Single(results);
        Assert.Equal("src/session.py", reference.Path);
        Assert.Equal("call", reference.ReferenceKind);
        Assert.Equal("login", reference.ContainerName);
    }

    [Fact]
    public void SearchReferences_UsesReferenceLinesContextInCurrentSchema()
    {
        InsertIndexedFile(
            "src/current_sql.sql",
            "sql",
            """
            CREATE PROCEDURE dbo.Caller
            AS
            BEGIN
                EXEC dbo.Target;
            END
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.Target", lang: "sql", exact: true, pathPatterns: ["current_sql"]));
        Assert.Equal("src/current_sql.sql", reference.Path);
        Assert.Contains("EXEC dbo.Target;", reference.RawContext);

        using var cmd = _db.Connection.CreateCommand();
        cmd.Parameters.AddWithValue("@path", "src/current_sql.sql");
        cmd.CommandText = "SELECT id FROM files WHERE path = @path";
        var fileId = (long)cmd.ExecuteScalar()!;

        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("@fileId", fileId);
        cmd.CommandText = "SELECT COUNT(*) FROM reference_lines WHERE file_id = @fileId";
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);

        cmd.CommandText = "SELECT COUNT(*) FROM symbol_references WHERE file_id = @fileId AND context IS NOT NULL";
        Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void SearchReferences_LegacyDatabaseWithoutReferenceLinesTableStillWorks()
    {
        var legacyPath = Path.Combine(Path.GetTempPath(), $"codeindex_legacy_reader_{Guid.NewGuid():N}.db");
        try
        {
            using var connection = CreateLegacyReferenceConnection(legacyPath);
            var legacyReader = new DbReader(connection);

            var status = legacyReader.GetStatus();
            Assert.Equal(1, status.References);

            var file = legacyReader.GetFileByPath("src/legacy_sql.sql");
            Assert.NotNull(file);
            Assert.Equal(1, file!.ReferenceCount);
        }
        finally
        {
            try { File.Delete(legacyPath); } catch { }
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
    public void GetSymbolHotspots_CountsSameNameReferencesPerSymbolFile()
    {
        InsertIndexedFile("src/hotspots_alpha.py", "python",
            "def Shared():\n    return True\n\n" +
            "def alpha_use():\n    Shared()\n    Shared()\n");
        InsertIndexedFile("src/hotspots_beta.py", "python",
            "def Shared():\n    return True\n\n" +
            "def beta_use():\n    Shared()\n");
        InsertIndexedFile("src/hotspots_gamma.py", "python",
            "def gamma_use():\n    Shared()\n");

        var results = _reader.GetSymbolHotspots(
            limit: 10,
            kind: "function",
            lang: "python",
            pathPatterns: ["src/hotspots_alpha.py", "src/hotspots_beta.py"],
            excludePathPatterns: null,
            excludeTests: false);

        var shared = results
            .Where(result => result.Symbol.Name == "Shared")
            .OrderBy(result => result.Symbol.Path, StringComparer.Ordinal)
            .ToList();

        Assert.Collection(shared,
            alpha =>
            {
                Assert.Equal("src/hotspots_alpha.py", alpha.Symbol.Path);
                Assert.Equal(2, alpha.ReferenceCount);
            },
            beta =>
            {
                Assert.Equal("src/hotspots_beta.py", beta.Symbol.Path);
                Assert.Equal(1, beta.ReferenceCount);
            });
    }

    [Fact]
    public void GetSymbolHotspots_PathFilterStillTreatsOutOfScopeDuplicateAsAmbiguous()
    {
        InsertIndexedFile("src/hotspots_alpha.py", "python",
            "def Shared():\n    return True\n\n" +
            "def alpha_use():\n    Shared()\n");
        InsertIndexedFile("src/hotspots_beta.py", "python",
            "def Shared():\n    return True\n\n" +
            "def beta_use():\n    Shared()\n    Shared()\n");
        InsertIndexedFile("src/hotspots_gamma.py", "python",
            "def gamma_use():\n    Shared()\n");

        var results = _reader.GetSymbolHotspots(
            limit: 10,
            kind: "function",
            lang: "python",
            pathPatterns: ["src/hotspots_alpha.py"],
            excludePathPatterns: null,
            excludeTests: false);

        var shared = Assert.Single(results.Where(result => result.Symbol.Name == "Shared"));
        Assert.Equal("src/hotspots_alpha.py", shared.Symbol.Path);
        Assert.Equal(1, shared.ReferenceCount);
    }

    [Fact]
    public void GetSymbolHotspots_CountsCrossFileReferencesForUniqueName()
    {
        InsertIndexedFile("src/api.py", "python", "def SharedApi():\n    return True\n");
        InsertIndexedFile("src/use1.py", "python",
            "def use_one():\n    SharedApi()\n    SharedApi()\n");
        InsertIndexedFile("src/use2.py", "python",
            "def use_two():\n    SharedApi()\n");

        var results = _reader.GetSymbolHotspots(
            limit: 10,
            kind: "function",
            lang: "python",
            pathPatterns: ["src/"],
            excludePathPatterns: null,
            excludeTests: false);

        var sharedApi = Assert.Single(results.Where(result => result.Symbol.Name == "SharedApi"));
        Assert.Equal("src/api.py", sharedApi.Symbol.Path);
        Assert.Equal(3, sharedApi.ReferenceCount);
    }

    [Fact]
    public void GetSymbolHotspots_KeepsCrossFileCountsForSameContainerOverloadFamily()
    {
        InsertIndexedFile("src/api.cs", "csharp",
            """
            public class Api
            {
                public void Run() { }
                public void Run(int value) { }
            }
            """);
        InsertIndexedFile("src/caller.cs", "csharp",
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

        var results = _reader.GetSymbolHotspots(
            limit: 10,
            kind: "function",
            lang: "csharp",
            pathPatterns: ["src/"],
            excludePathPatterns: null,
            excludeTests: false);

        var run = Assert.Single(results.Where(result => result.Symbol.Name == "Run"));
        Assert.Equal("src/api.cs", run.Symbol.Path);
        Assert.Equal("Api", run.Symbol.ContainerName);
        Assert.Equal(2, run.ReferenceCount);
    }

    [Fact]
    public void GetSymbolHotspots_KeepsCrossFileCountsForPartialClassOverloadFamily()
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

        var results = _reader.GetSymbolHotspots(
            limit: 10,
            kind: "function",
            lang: "csharp",
            pathPatterns: ["src/"],
            excludePathPatterns: null,
            excludeTests: false);

        var run = Assert.Single(results.Where(result => result.Symbol.Name == "Run"));
        Assert.Equal("Api", run.Symbol.ContainerName);
        Assert.Equal(2, run.ReferenceCount);
    }

    [Fact]
    public void GetSymbolHotspots_SeparatesSameSimpleContainerNamesAcrossNamespaces()
    {
        InsertIndexedFile("src/One.Api.cs", "csharp",
            """
            namespace One;

            public class Api
            {
                public void Run() { }

                public void LocalOne()
                {
                    Run();
                }
            }
            """);
        InsertIndexedFile("src/Two.Api.cs", "csharp",
            """
            namespace Two;

            public class Api
            {
                public void Run() { }

                public void LocalTwo()
                {
                    Run();
                }
            }
            """);

        var results = _reader.GetSymbolHotspots(
            limit: 10,
            kind: "function",
            lang: "csharp",
            pathPatterns: ["src/"],
            excludePathPatterns: null,
            excludeTests: false);

        var runs = results
            .Where(result => result.Symbol.Name == "Run")
            .OrderBy(result => result.Symbol.Path, StringComparer.Ordinal)
            .ToList();

        Assert.Collection(runs,
            first =>
            {
                Assert.Equal("src/One.Api.cs", first.Symbol.Path);
                Assert.Equal("Api", first.Symbol.ContainerName);
                Assert.Equal(1, first.ReferenceCount);
            },
            second =>
            {
                Assert.Equal("src/Two.Api.cs", second.Symbol.Path);
                Assert.Equal("Api", second.Symbol.ContainerName);
                Assert.Equal(1, second.ReferenceCount);
            });
    }

    [Fact]
    public void GetSymbolHotspots_DoesNotMergeSameContainerNameAcrossPythonModules()
    {
        InsertIndexedFile("src/alpha.py", "python",
            """
            class Api:
                def Run(self):
                    return True

                def Use(self):
                    self.Run()
                    self.Run()
            """);
        InsertIndexedFile("src/beta.py", "python",
            """
            class Api:
                def Run(self):
                    return True

                def Use(self):
                    self.Run()
                    self.Run()
            """);

        var results = _reader.GetSymbolHotspots(
            limit: 10,
            kind: "function",
            lang: "python",
            pathPatterns: ["src/"],
            excludePathPatterns: null,
            excludeTests: false);

        var runs = results
            .Where(result => result.Symbol.Name == "Run")
            .OrderBy(result => result.Symbol.Path, StringComparer.Ordinal)
            .ToList();

        Assert.Collection(runs,
            first =>
            {
                Assert.Equal("src/alpha.py", first.Symbol.Path);
                Assert.Equal("Api", first.Symbol.ContainerName);
                Assert.Equal(2, first.ReferenceCount);
            },
            second =>
            {
                Assert.Equal("src/beta.py", second.Symbol.Path);
                Assert.Equal("Api", second.Symbol.ContainerName);
                Assert.Equal(2, second.ReferenceCount);
            });
    }

    [Fact]
    public void GetSymbolHotspots_DoesNotMergeSameQualifiedTypeAcrossProjectRoots()
    {
        InsertIndexedFile("projA/src/Api.cs", "csharp",
            """
            namespace Shared;

            public class Api
            {
                public void Run() { }

                public void LocalA()
                {
                    Run();
                }
            }
            """);
        InsertIndexedFile("projB/src/Api.cs", "csharp",
            """
            namespace Shared;

            public class Api
            {
                public void Run() { }

                public void LocalB()
                {
                    Run();
                }
            }
            """);

        var results = _reader.GetSymbolHotspots(
            limit: 10,
            kind: "function",
            lang: "csharp",
            pathPatterns: ["projA/", "projB/"],
            excludePathPatterns: null,
            excludeTests: false);

        var runs = results
            .Where(result => result.Symbol.Name == "Run")
            .OrderBy(result => result.Symbol.Path, StringComparer.Ordinal)
            .ToList();

        Assert.Collection(runs,
            first =>
            {
                Assert.Equal("projA/src/Api.cs", first.Symbol.Path);
                Assert.Equal("Api", first.Symbol.ContainerName);
                Assert.Equal(1, first.ReferenceCount);
            },
            second =>
            {
                Assert.Equal("projB/src/Api.cs", second.Symbol.Path);
                Assert.Equal("Api", second.Symbol.ContainerName);
                Assert.Equal(1, second.ReferenceCount);
            });
    }

    [Fact]
    public void GetSymbolHotspots_DoesNotMergePartialFamiliesAcrossProjectRoots()
    {
        InsertIndexedFile("projA/src/Api.Part1.cs", "csharp",
            """
            namespace Shared;

            public partial class Api
            {
                public void Run()
                {
                    Run(1);
                }
            }
            """,
            familyScopeKey: "projA");
        InsertIndexedFile("projA/src/Api.Part2.cs", "csharp",
            """
            namespace Shared;

            public partial class Api
            {
                public void Run(int value) { }
            }
            """,
            familyScopeKey: "projA");
        InsertIndexedFile("projB/src/Api.Part1.cs", "csharp",
            """
            namespace Shared;

            public partial class Api
            {
                public void Run()
                {
                    Run(1);
                }
            }
            """,
            familyScopeKey: "projB");
        InsertIndexedFile("projB/src/Api.Part2.cs", "csharp",
            """
            namespace Shared;

            public partial class Api
            {
                public void Run(int value) { }
            }
            """,
            familyScopeKey: "projB");

        var results = _reader.GetSymbolHotspots(
            limit: 10,
            kind: "function",
            lang: "csharp",
            pathPatterns: ["projA/", "projB/"],
            excludePathPatterns: null,
            excludeTests: false);

        var runs = results
            .Where(result => result.Symbol.Name == "Run")
            .OrderBy(result => result.Symbol.Path, StringComparer.Ordinal)
            .ToList();

        Assert.Collection(runs,
            first =>
            {
                Assert.Equal("projA/src/Api.Part1.cs", first.Symbol.Path);
                Assert.Equal("Api", first.Symbol.ContainerName);
                Assert.Equal(1, first.ReferenceCount);
            },
            second =>
            {
                Assert.Equal("projB/src/Api.Part1.cs", second.Symbol.Path);
                Assert.Equal("Api", second.Symbol.ContainerName);
                Assert.Equal(1, second.ReferenceCount);
            });
    }

    [Fact]
    public void GetSymbolHotspots_KeepsCrossFileCountsForVbPartialClassFamily()
    {
        InsertIndexedFile("src/Api.Part1.vb", "vb",
            """
            Public Partial Class Api
                Public Sub Run()
                End Sub
            End Class
            """);
        InsertIndexedFile("src/Api.Part2.vb", "vb",
            """
            Public Partial Class Api
                Public Sub Run(value As Integer)
                End Sub
            End Class
            """);
        InsertIndexedFile("src/Caller.vb", "vb",
            """
            Public Class Caller
                Public Sub Call(api As Api)
                    api.Run()
                    api.Run(1)
                End Sub
            End Class
            """);

        var results = _reader.GetSymbolHotspots(
            limit: 10,
            kind: "function",
            lang: "vb",
            pathPatterns: ["src/"],
            excludePathPatterns: null,
            excludeTests: false);

        var run = Assert.Single(results.Where(result => result.Symbol.Name == "Run"));
        Assert.Equal("Api", run.Symbol.ContainerName);
        Assert.Equal(2, run.ReferenceCount);
    }

    [Fact]
    public void GetSymbolHotspots_GroupedFamilyReturnsRealDefinitionLocation()
    {
        InsertIndexedFile("src/APart.cs", "csharp",
            """
            public partial class Api
            {
                public void Helper()
                {
                }

                public void Run()
                {
                }
            }
            """);
        InsertIndexedFile("src/BPart.cs", "csharp",
            """
            public partial class Api
            {
                public void Run(int value)
                {
                }
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

        var results = _reader.GetSymbolHotspots(
            limit: 10,
            kind: "function",
            lang: "csharp",
            pathPatterns: ["src/"],
            excludePathPatterns: null,
            excludeTests: false);

        var run = Assert.Single(results.Where(result => result.Symbol.Name == "Run"));
        Assert.Equal(2, run.ReferenceCount);

        var definitions = _reader.SearchSymbols(
            "Run",
            limit: 10,
            kind: "function",
            lang: "csharp",
            pathPatterns: ["src/"],
            excludePathPatterns: null,
            excludeTests: false,
            exact: true);

        Assert.Contains(definitions, definition =>
            definition.Path == run.Symbol.Path &&
            definition.Line == run.Symbol.Line &&
            definition.Name == run.Symbol.Name);
    }

    [Fact]
    public void GetSymbolHotspots_CollapsesSameFileDuplicateNames()
    {
        InsertIndexedFile("src/duplicate_names.py", "python",
            "def Run():\n    return True\n\n" +
            "def Run(value=None):\n    return value\n\n" +
            "def caller():\n    Run()\n");

        var results = _reader.GetSymbolHotspots(
            limit: 10,
            kind: "function",
            lang: "python",
            pathPatterns: ["src/duplicate_names.py"],
            excludePathPatterns: null,
            excludeTests: false);

        var runResults = results
            .Where(result => result.Symbol.Name == "Run")
            .ToList();

        var run = Assert.Single(runResults);
        Assert.Equal("src/duplicate_names.py", run.Symbol.Path);
        Assert.Equal(1, run.ReferenceCount);
        Assert.Equal(1, run.Symbol.Line);
    }

    [Fact]
    public void GetSymbolHotspots_WithoutHotspotFamilyReadyFallsBackForMixedPartialFamilies()
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
                SET family_key = NULL
                WHERE file_id IN (
                    SELECT id FROM files WHERE path = 'src/Api.Part2.cs'
                )
                """;
            cmd.ExecuteNonQuery();
        }
        _writer.SetMeta(DbContext.GetHotspotFamilyVersionMetaKey("csharp"), null);

        var reader = new DbReader(_db.Connection);
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
        Assert.Contains("csharp", signal.DegradedReason);
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
    public void GetSymbolHotspots_StaleVersionOneMarkerlessFamiliesDegradeAndDoNotMerge()
    {
        InsertIndexedFile("projA/src/Api.Part1.cs", "csharp",
            """
            namespace Shared;

            public partial class Api
            {
                public void Run()
                {
                    Run(1);
                }
            }
            """,
            familyScopeKey: ".");
        InsertIndexedFile("projA/src/Api.Part2.cs", "csharp",
            """
            namespace Shared;

            public partial class Api
            {
                public void Run(int value) { }
            }
            """,
            familyScopeKey: ".");
        InsertIndexedFile("projB/src/Api.Part1.cs", "csharp",
            """
            namespace Shared;

            public partial class Api
            {
                public void Run()
                {
                    Run(1);
                }
            }
            """,
            familyScopeKey: ".");
        InsertIndexedFile("projB/src/Api.Part2.cs", "csharp",
            """
            namespace Shared;

            public partial class Api
            {
                public void Run(int value) { }
            }
            """,
            familyScopeKey: ".");

        _writer.SetMeta(DbContext.GetHotspotFamilyVersionMetaKey("csharp"), "1");
        _writer.SetMeta(DbContext.GetHotspotFamilyMarkerFingerprintMetaKey("csharp"), "stale-v1");

        var reader = new DbReader(_db.Connection);
        var signal = reader.GetHotspotFamilySignal("csharp");
        Assert.True(signal.Relevant);
        Assert.False(signal.Ready);
        Assert.Contains("csharp", signal.DegradedReason);

        var runs = reader.GetSymbolHotspots(
                limit: 10,
                kind: "function",
                lang: "csharp",
                pathPatterns: ["projA/", "projB/"],
                excludePathPatterns: null,
                excludeTests: false)
            .Where(result => result.Symbol.Name == "Run")
            .OrderBy(result => result.Symbol.Path, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(2, runs.Count);
        Assert.StartsWith("projA/src/Api.Part", runs[0].Symbol.Path, StringComparison.Ordinal);
        Assert.Equal(1, runs[0].ReferenceCount);
        Assert.StartsWith("projB/src/Api.Part", runs[1].Symbol.Path, StringComparison.Ordinal);
        Assert.Equal(1, runs[1].ReferenceCount);
    }

    [Fact]
    public void GetSymbolHotspots_DoesNotPromoteSameFileDifferentContainersToGlobalCounts()
    {
        InsertIndexedFile("src/Duplicate.cs", "csharp",
            """
            public class A
            {
                public void Run() { }
            }

            public class B
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/Caller.cs", "csharp",
            """
            public class Caller
            {
                public void Call(A api)
                {
                    api.Run();
                    api.Run();
                }
            }
            """);

        var results = _reader.GetSymbolHotspots(
            limit: 10,
            kind: "function",
            lang: "csharp",
            pathPatterns: ["src/"],
            excludePathPatterns: null,
            excludeTests: false);

        Assert.DoesNotContain(results, result => result.Symbol.Name == "Run");
    }

    [Fact]
    public void GetSymbolHotspots_DoesNotCountAmbiguousSameFileSiblingContainerReferences()
    {
        InsertIndexedFile("src/Duplicate.cs", "csharp",
            """
            public class A
            {
                public void Run() { }

                public void CallA()
                {
                    Run();
                }
            }

            public class B
            {
                public void Run() { }
            }
            """);

        var results = _reader.GetSymbolHotspots(
            limit: 10,
            kind: "function",
            lang: "csharp",
            pathPatterns: ["src/"],
            excludePathPatterns: null,
            excludeTests: false);

        Assert.DoesNotContain(results, result => result.Symbol.Name == "Run");
    }

    [Fact]
    public void GetSymbolHotspots_LangFilterIgnoresCrossLanguageReferences()
    {
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/Caller.cs", "csharp",
            """
            public class Caller
            {
                public void Call(App app)
                {
                    app.Run();
                    app.Run();
                }
            }
            """);
        InsertIndexedFile("src/tool.py", "python",
            """
            def helper():
                Run()
            """);

        var results = _reader.GetSymbolHotspots(
            limit: 10,
            kind: "function",
            lang: "csharp",
            pathPatterns: ["src/"],
            excludePathPatterns: null,
            excludeTests: false);

        var run = Assert.Single(results.Where(result => result.Symbol.Name == "Run"));
        Assert.Equal("src/App.cs", run.Symbol.Path);
        Assert.Equal(2, run.ReferenceCount);
    }

    [Fact]
    public void GetSymbolHotspots_CrossLanguageDefinitionsDoNotSuppressSameLanguageHotspots()
    {
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/Caller.cs", "csharp",
            """
            public class Caller
            {
                public void Call(App app)
                {
                    app.Run();
                    app.Run();
                }
            }
            """);
        InsertIndexedFile("src/tool.py", "python",
            """
            def Run():
                return True
            """);

        var results = _reader.GetSymbolHotspots(
            limit: 10,
            kind: "function",
            lang: null,
            pathPatterns: ["src/"],
            excludePathPatterns: null,
            excludeTests: false);

        var run = Assert.Single(results.Where(result => result.Symbol.Name == "Run" && result.Symbol.Lang == "csharp"));
        Assert.Equal("src/App.cs", run.Symbol.Path);
        Assert.Equal(2, run.ReferenceCount);
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
            Path = "src/auth_v2.py", Lang = "python", Size = 80, Lines = 4,
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
    public void SqlQualifiedNames_AlignGraphReadersHotspotsAndUnused()
    {
        InsertIndexedFile("src/sql_name_mismatch_fixture.sql", "sql",
            """
            CREATE FUNCTION dbo.fn_GetOrderItems(@orderId INT)
            RETURNS TABLE
            AS
            RETURN (SELECT * FROM dbo.OrderItems WHERE OrderId = @orderId);
            GO

            CREATE PROCEDURE dbo.usp_GetOrders
            AS
            BEGIN
                SELECT *
                FROM dbo.Orders o
                CROSS APPLY dbo.fn_GetOrderItems(o.OrderId) fi;
            END
            GO
            """);

        var bareRefs = _reader.SearchReferences("fn_GetOrderItems", lang: "sql", exact: true, pathPatterns: ["sql_name_mismatch_fixture"]);
        var qualifiedRefs = _reader.SearchReferences("dbo.fn_GetOrderItems", lang: "sql", exact: true, pathPatterns: ["sql_name_mismatch_fixture"]);
        Assert.Equal(12, Assert.Single(bareRefs).Line);
        Assert.Equal(12, Assert.Single(qualifiedRefs).Line);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.fn_GetOrderItems", lang: "sql", exact: true, pathPatterns: ["sql_name_mismatch_fixture"]));

        var bareCaller = Assert.Single(_reader.GetCallers("fn_GetOrderItems", lang: "sql", exact: true, pathPatterns: ["sql_name_mismatch_fixture"]));
        var qualifiedCaller = Assert.Single(_reader.GetCallers("dbo.fn_GetOrderItems", lang: "sql", exact: true, pathPatterns: ["sql_name_mismatch_fixture"]));
        Assert.Equal("dbo.usp_GetOrders", bareCaller.CallerName);
        Assert.Equal("dbo.usp_GetOrders", qualifiedCaller.CallerName);
        Assert.Equal(1, _reader.CountCallers("dbo.fn_GetOrderItems", lang: "sql", exact: true, pathPatterns: ["sql_name_mismatch_fixture"]));

        var bareCallee = Assert.Single(_reader.GetCallees("usp_GetOrders", lang: "sql", exact: true, pathPatterns: ["sql_name_mismatch_fixture"]));
        var qualifiedCallee = Assert.Single(_reader.GetCallees("dbo.usp_GetOrders", lang: "sql", exact: true, pathPatterns: ["sql_name_mismatch_fixture"]));
        Assert.Equal("fn_GetOrderItems", bareCallee.CalleeName);
        Assert.Equal("fn_GetOrderItems", qualifiedCallee.CalleeName);
        Assert.Equal(1, _reader.CountCallees("usp_GetOrders", lang: "sql", exact: true, pathPatterns: ["sql_name_mismatch_fixture"]));

        var (bareImpact, bareTruncated, bareTruncatedReason, _, _) = _reader.GetTransitiveCallers("fn_GetOrderItems", maxDepth: 1, limit: 10, lang: "sql", pathPatterns: ["sql_name_mismatch_fixture"]);
        var (qualifiedImpact, qualifiedTruncated, qualifiedTruncatedReason, _, _) = _reader.GetTransitiveCallers("dbo.fn_GetOrderItems", maxDepth: 1, limit: 10, lang: "sql", pathPatterns: ["sql_name_mismatch_fixture"]);
        Assert.False(bareTruncated);
        Assert.False(qualifiedTruncated);
        Assert.Null(bareTruncatedReason);
        Assert.Null(qualifiedTruncatedReason);
        Assert.Equal("dbo.usp_GetOrders", Assert.Single(bareImpact).CallerName);
        Assert.Equal("dbo.usp_GetOrders", Assert.Single(qualifiedImpact).CallerName);

        var hotspot = Assert.Single(
            _reader.GetSymbolHotspots(10, "function", "sql", ["sql_name_mismatch_fixture"], null, false),
            item => item.Symbol.Name == "dbo.fn_GetOrderItems");
        Assert.Equal(1, hotspot.ReferenceCount);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: "function", lang: "sql",
            pathPatterns: ["sql_name_mismatch_fixture"], excludePathPatterns: null, excludeTests: false);
        Assert.DoesNotContain(unused, symbol => symbol.Name == "dbo.fn_GetOrderItems");
        var unusedCount = _reader.CountUnusedSymbols(kind: "function", lang: "sql",
            pathPatterns: ["sql_name_mismatch_fixture"], excludePathPatterns: null, excludeTests: false);
        Assert.Equal(1, unusedCount.Count);
        Assert.Equal(1, unusedCount.FileCount);
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

    [Fact]
    public void SqlQualifiedNames_DownstreamReadersDoNotPromoteUnqualifiedRowsFromLaterTokens()
    {
        InsertIndexedFile("src/sql_unqualified_row_targets.sql", "sql",
            """
            CREATE FUNCTION dbo.fn_Target()
            RETURNS INT
            AS
            BEGIN
                RETURN 1;
            END
            GO

            CREATE FUNCTION sales.fn_Target()
            RETURNS INT
            AS
            BEGIN
                RETURN 2;
            END
            GO
            """);

        InsertIndexedFile("src/sql_unqualified_row_comment.sql", "sql",
            """
            CREATE PROCEDURE dbo.CommentCaller
            AS
            BEGIN
                EXEC fn_Target; -- sales.fn_Target
            END
            GO
            """);

        InsertIndexedFile("src/sql_unqualified_row_string.sql", "sql",
            """
            CREATE PROCEDURE dbo.StringCaller
            AS
            BEGIN
                EXEC fn_Target; SELECT 'sales.fn_Target';
            END
            GO
            """);

        InsertIndexedFile("src/sql_unqualified_row_mixed_calls.sql", "sql",
            """
            CREATE PROCEDURE dbo.MixedCaller
            AS
            BEGIN
                EXEC fn_Target; EXEC sales.fn_Target;
            END
            GO
            """);

        var commentDependency = Assert.Single(
            _reader.GetFileDependencies(limit: 10, lang: "sql", pathPatterns: ["sql_unqualified_row_comment.sql"], excludePathPatterns: null, excludeTests: false));
        Assert.Equal("src/sql_unqualified_row_comment.sql", commentDependency.SourcePath);
        Assert.Equal("src/sql_unqualified_row_targets.sql", commentDependency.TargetPath);
        Assert.Equal(1, commentDependency.ReferenceCount);
        Assert.Equal("dbo.fn_Target", commentDependency.Symbols);

        var stringDependency = Assert.Single(
            _reader.GetFileDependencies(limit: 10, lang: "sql", pathPatterns: ["sql_unqualified_row_string.sql"], excludePathPatterns: null, excludeTests: false));
        Assert.Equal("src/sql_unqualified_row_string.sql", stringDependency.SourcePath);
        Assert.Equal("src/sql_unqualified_row_targets.sql", stringDependency.TargetPath);
        Assert.Equal(1, stringDependency.ReferenceCount);
        Assert.Equal("dbo.fn_Target", stringDependency.Symbols);

        var mixedDependency = Assert.Single(
            _reader.GetFileDependencies(limit: 10, lang: "sql", pathPatterns: ["sql_unqualified_row_mixed_calls.sql"], excludePathPatterns: null, excludeTests: false));
        Assert.Equal("src/sql_unqualified_row_mixed_calls.sql", mixedDependency.SourcePath);
        Assert.Equal("src/sql_unqualified_row_targets.sql", mixedDependency.TargetPath);
        Assert.Equal(2, mixedDependency.ReferenceCount);
        Assert.Equal("dbo.fn_Target,sales.fn_Target", mixedDependency.Symbols);

        var hotspots = _reader.GetSymbolHotspots(10, "function", "sql", ["sql_unqualified_row"], null, false);
        var dboHotspot = Assert.Single(hotspots, item => item.Symbol.Name == "dbo.fn_Target");
        var salesHotspot = Assert.Single(hotspots, item => item.Symbol.Name == "sales.fn_Target");
        Assert.Equal(3, dboHotspot.ReferenceCount);
        Assert.Equal(1, salesHotspot.ReferenceCount);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: "function", lang: "sql",
            pathPatterns: ["sql_unqualified_row"], excludePathPatterns: null, excludeTests: false);
        Assert.DoesNotContain(unused, symbol => symbol.Name == "dbo.fn_Target");
        Assert.DoesNotContain(unused, symbol => symbol.Name == "sales.fn_Target");
    }

    [Fact]
    public void SqlQualifiedNames_AlignDepsEdges()
    {
        InsertIndexedFile("src/sql_deps_target.sql", "sql",
            """
            CREATE FUNCTION dbo.fn_GetOrderItems(@orderId INT)
            RETURNS TABLE
            AS
            RETURN (SELECT * FROM dbo.OrderItems WHERE OrderId = @orderId);
            GO
            """);

        InsertIndexedFile("src/sql_deps_caller.sql", "sql",
            """
            CREATE PROCEDURE dbo.usp_GetOrders
            AS
            BEGIN
                SELECT *
                FROM dbo.Orders o
                CROSS APPLY dbo.fn_GetOrderItems(o.OrderId) fi;
            END
            GO
            """);

        var dependency = Assert.Single(
            _reader.GetFileDependencies(limit: 10, lang: "sql", pathPatterns: ["sql_deps_caller.sql"], excludePathPatterns: null, excludeTests: false));
        Assert.Equal("src/sql_deps_caller.sql", dependency.SourcePath);
        Assert.Equal("src/sql_deps_target.sql", dependency.TargetPath);
        Assert.Equal(1, dependency.ReferenceCount);
    }

    [Fact]
    public void SqlQualifiedNames_SameLineCrossSchemaCallStillReachesReaders()
    {
        InsertIndexedFile("src/sql_same_line_cross_schema.sql", "sql",
            """
            CREATE PROCEDURE sales.fn_Target AS EXEC dbo.fn_Target;
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.fn_Target", lang: "sql", exact: true, pathPatterns: ["sql_same_line_cross_schema"]));
        Assert.Equal(1, reference.Line);
        Assert.Equal("sales.fn_Target", reference.ContainerName);

        var caller = Assert.Single(
            _reader.GetCallers("dbo.fn_Target", lang: "sql", exact: true, pathPatterns: ["sql_same_line_cross_schema"]));
        Assert.Equal("sales.fn_Target", caller.CallerName);
        Assert.Equal(1, caller.ReferenceCount);
    }

    [Fact]
    public void SqlQualifiedNames_SameLineQualifiedCallAfterStringLiteralStillReachesReaders()
    {
        InsertIndexedFile("src/sql_same_line_string_literal.sql", "sql",
            """
            CREATE PROCEDURE dbo.fn_Target
            AS
            SELECT 1;
            GO

            CREATE PROCEDURE sales.host
            AS
            BEGIN
                SELECT 'prefix'; EXEC dbo.fn_Target;
            END
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.fn_Target", lang: "sql", exact: true, pathPatterns: ["sql_same_line_string_literal"]));
        Assert.Equal(9, reference.Line);
        Assert.Equal("sales.host", reference.ContainerName);

        var caller = Assert.Single(
            _reader.GetCallers("dbo.fn_Target", lang: "sql", exact: true, pathPatterns: ["sql_same_line_string_literal"]));
        Assert.Equal("sales.host", caller.CallerName);
        Assert.Equal(1, caller.ReferenceCount);
    }

    [Fact]
    public void SqlQualifiedNames_SameLineQualifiedCallAfterInlineBlockCommentStillReachesReaders()
    {
        InsertIndexedFile("src/sql_same_line_block_comment.sql", "sql",
            """
            CREATE PROCEDURE dbo.fn_Target
            AS
            SELECT 1;
            GO

            CREATE PROCEDURE sales.host
            AS
            BEGIN
                SELECT /*note*/ 1; EXEC dbo.fn_Target;
            END
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.fn_Target", lang: "sql", exact: true, pathPatterns: ["sql_same_line_block_comment"]));
        Assert.Equal(9, reference.Line);
        Assert.Equal("sales.host", reference.ContainerName);

        var caller = Assert.Single(
            _reader.GetCallers("dbo.fn_Target", lang: "sql", exact: true, pathPatterns: ["sql_same_line_block_comment"]));
        Assert.Equal("sales.host", caller.CallerName);
        Assert.Equal(1, caller.ReferenceCount);
    }

    [Fact]
    public void SqlQualifiedNames_ResolveQuotedDefinitionsFromUnquotedQualifiedQueries()
    {
        InsertIndexedFile("src/sql_quoted_definition_target.sql", "sql",
            """
            CREATE PROCEDURE [dbo].[fn_Target]
            AS
            SELECT 1;
            GO
            """);

        InsertIndexedFile("src/sql_quoted_definition_caller.sql", "sql",
            """
            CREATE PROCEDURE [sales].[fn_Target]
            AS
            EXEC [dbo].[fn_Target];
            GO
            """);

        var definition = Assert.Single(
            _reader.GetDefinitions("dbo.fn_Target", limit: 10, lang: "sql", pathPatterns: ["sql_quoted_definition"]));
        Assert.Equal("[dbo].[fn_Target]", definition.Name);

        var exactDefinition = Assert.Single(
            _reader.GetDefinitions("dbo.fn_Target", limit: 10, lang: "sql", pathPatterns: ["sql_quoted_definition"], exact: true));
        Assert.Equal("[dbo].[fn_Target]", exactDefinition.Name);

        var analysis = _reader.AnalyzeSymbol("dbo.fn_Target", limit: 10, lang: "sql", pathPatterns: ["sql_quoted_definition"]);
        Assert.Equal("[dbo].[fn_Target]", Assert.Single(analysis.Definitions).Name);

        var exactAnalysis = _reader.AnalyzeSymbol("dbo.fn_Target", limit: 10, lang: "sql", pathPatterns: ["sql_quoted_definition"], exact: true);
        Assert.Equal("[dbo].[fn_Target]", Assert.Single(exactAnalysis.Definitions).Name);

        var impact = _reader.AnalyzeImpact("dbo.fn_Target", maxDepth: 1, limit: 10, lang: "sql", pathPatterns: ["sql_quoted_definition"]);
        Assert.Equal(1, impact.DefinitionCount);
        Assert.Equal("[dbo].[fn_Target]", Assert.Single(impact.Definitions).Name);
        Assert.Equal("[sales].[fn_Target]", Assert.Single(impact.Callers).CallerName);

        var tsqlImpact = _reader.AnalyzeImpact("dbo.fn_Target", maxDepth: 1, limit: 10, lang: "tsql", pathPatterns: ["sql_quoted_definition"]);
        Assert.Equal(1, tsqlImpact.DefinitionCount);
        Assert.Equal("[dbo].[fn_Target]", Assert.Single(tsqlImpact.Definitions).Name);
        Assert.Equal("[sales].[fn_Target]", Assert.Single(tsqlImpact.Callers).CallerName);
    }

    [Fact]
    public void AnalyzeImpact_MaxDepthZero_ReturnsResolvedDefinitionsWithoutCallers()
    {
        InsertIndexedFile("src/depth_zero.cs", "csharp",
            """
            public class App
            {
                public void Run()
                {
                    Leaf();
                }

                public void Leaf() { }
            }
            """);

        var analysis = _reader.AnalyzeImpact("Leaf", maxDepth: 0, limit: 10, lang: "csharp");

        Assert.Equal("Leaf", analysis.Query);
        Assert.Equal("Leaf", analysis.ResolvedName);
        Assert.Equal(1, analysis.DefinitionCount);
        Assert.Single(analysis.Definitions);
        Assert.Empty(analysis.Callers);
        Assert.Empty(analysis.FileImpacts);
        Assert.Equal("none", analysis.ImpactMode);
        Assert.Equal("depth_zero", analysis.ZeroResultReason);
        Assert.Contains("depth 1", analysis.Suggestion, StringComparison.Ordinal);
    }

    [Fact]
    public void SqlQualifiedNames_DoubleQuotedCallsResolveFromUnquotedQualifiedQueries()
    {
        InsertIndexedFile("src/sql_double_quoted_target.sql", "sql",
            """
            CREATE PROCEDURE "sales"."proc_name"
            AS
            SELECT 1;
            GO
            """);

        InsertIndexedFile("src/sql_double_quoted_caller.sql", "sql",
            """
            CREATE PROCEDURE sales.caller
            AS
            BEGIN
                CALL "sales"."proc_name";
            END
            GO
            """);

        var references = _reader.SearchReferences("sales.proc_name", lang: "sql", exact: true, pathPatterns: ["sql_double_quoted"]);
        var reference = Assert.Single(references);
        Assert.Equal(4, reference.Line);
        Assert.Equal("sales.caller", reference.ContainerName);

        var callers = _reader.GetCallers("sales.proc_name", lang: "sql", exact: true, pathPatterns: ["sql_double_quoted"]);
        var caller = Assert.Single(callers);
        Assert.Equal("sales.caller", caller.CallerName);
        Assert.Equal(1, caller.ReferenceCount);

        var impact = _reader.AnalyzeImpact("sales.proc_name", maxDepth: 1, limit: 10, lang: "sql", pathPatterns: ["sql_double_quoted"]);
        Assert.Equal("\"sales\".\"proc_name\"", Assert.Single(impact.Definitions).Name);
        Assert.Equal("sales.caller", Assert.Single(impact.Callers).CallerName);
    }

    [Fact]
    public void SqlQualifiedNames_NonExactQualifiedLookupsStaySchemaScoped()
    {
        InsertIndexedFile("src/sql_nonexact_scope_target.sql", "sql",
            """
            CREATE PROCEDURE archive.sales.proc_name
            AS
            BEGIN
                SELECT 1;
            END
            GO
            """);

        InsertIndexedFile("src/sql_nonexact_scope_caller.sql", "sql",
            """
            CREATE PROCEDURE sales.caller
            AS
            BEGIN
                EXEC archive.sales.proc_name;
            END
            GO
            """);

        Assert.Empty(_reader.SearchReferences("sales.proc_name", lang: "sql", pathPatterns: ["sql_nonexact_scope"]));
        Assert.Empty(_reader.GetCallers("sales.proc_name", lang: "sql", pathPatterns: ["sql_nonexact_scope"]));

        Assert.Empty(_reader.SearchReferences("sales.proc_name", lang: "sql", exact: true, pathPatterns: ["sql_nonexact_scope"]));
        Assert.Empty(_reader.GetCallers("sales.proc_name", lang: "sql", exact: true, pathPatterns: ["sql_nonexact_scope"]));

        var references = Assert.Single(_reader.SearchReferences("archive.sales.proc_name", lang: "sql", pathPatterns: ["sql_nonexact_scope"]));
        Assert.Equal(4, references.Line);
        Assert.Equal("sales.caller", references.ContainerName);

        var callers = Assert.Single(_reader.GetCallers("archive.sales.proc_name", lang: "sql", pathPatterns: ["sql_nonexact_scope"]));
        Assert.Equal("sales.caller", callers.CallerName);
        Assert.Equal(1, callers.ReferenceCount);
    }

    [Fact]
    public void SqlQualifiedNames_ExactLookups_DoNotConflateQuotedSingleIdentifierDotsWithQualifiedNames()
    {
        InsertIndexedFile("src/sql_dotted_identifier_collision.sql", "sql",
            """
            CREATE PROCEDURE sales.fn_Target
            AS
            SELECT 1;
            GO

            CREATE PROCEDURE "sales.fn_Target"
            AS
            SELECT 2;
            GO
            """);

        var qualifiedDefinition = Assert.Single(
            _reader.GetDefinitions("sales.fn_Target", limit: 10, lang: "sql", pathPatterns: ["sql_dotted_identifier_collision"], exact: true));
        Assert.Equal("sales.fn_Target", qualifiedDefinition.Name);

        var quotedDefinition = Assert.Single(
            _reader.GetDefinitions("\"sales.fn_Target\"", limit: 10, lang: "sql", pathPatterns: ["sql_dotted_identifier_collision"], exact: true));
        Assert.Equal("\"sales.fn_Target\"", quotedDefinition.Name);

        var qualifiedAnalysis = _reader.AnalyzeSymbol("sales.fn_Target", limit: 10, lang: "sql", pathPatterns: ["sql_dotted_identifier_collision"], exact: true);
        Assert.Equal("sales.fn_Target", Assert.Single(qualifiedAnalysis.Definitions).Name);

        var quotedAnalysis = _reader.AnalyzeSymbol("\"sales.fn_Target\"", limit: 10, lang: "sql", pathPatterns: ["sql_dotted_identifier_collision"], exact: true);
        Assert.Equal("\"sales.fn_Target\"", Assert.Single(quotedAnalysis.Definitions).Name);

        var qualifiedImpact = _reader.AnalyzeImpact("sales.fn_Target", maxDepth: 1, limit: 10, lang: "sql", pathPatterns: ["sql_dotted_identifier_collision"]);
        Assert.Equal(1, qualifiedImpact.DefinitionCount);
        Assert.Equal("sales.fn_Target", Assert.Single(qualifiedImpact.Definitions).Name);

        var quotedImpact = _reader.AnalyzeImpact("\"sales.fn_Target\"", maxDepth: 1, limit: 10, lang: "sql", pathPatterns: ["sql_dotted_identifier_collision"]);
        Assert.Equal(1, quotedImpact.DefinitionCount);
        Assert.Equal("\"sales.fn_Target\"", Assert.Single(quotedImpact.Definitions).Name);
    }

    [Fact]
    public void SqlQualifiedNames_ExactGraphReadersDoNotConflateQuotedSingleIdentifierDotsWithQualifiedNames()
    {
        InsertIndexedFile("src/sql_dotted_identifier_graph_targets.sql", "sql",
            """
            CREATE PROCEDURE sales.fn_Target
            AS
            SELECT 1;
            GO

            CREATE PROCEDURE "sales.fn_Target"
            AS
            SELECT 2;
            GO
            """);

        InsertIndexedFile("src/sql_dotted_identifier_graph_callers.sql", "sql",
            """
            CREATE PROCEDURE sales.caller
            AS
            BEGIN
                EXEC sales.fn_Target;
            END
            GO

            CREATE PROCEDURE quoted.caller
            AS
            BEGIN
                CALL "sales.fn_Target";
                EXEC "sales.fn_Target";
                EXECUTE "sales.fn_Target";
            END
            GO
            """);

        var references = _reader.SearchReferences("sales.fn_Target", lang: "sql", exact: true, pathPatterns: ["sql_dotted_identifier_graph"]);
        var reference = Assert.Single(references);
        Assert.Equal("sales.caller", reference.ContainerName);
        Assert.Equal(4, reference.Line);
        Assert.Equal(1, _reader.CountSearchReferences("sales.fn_Target", lang: "sql", exact: true, pathPatterns: ["sql_dotted_identifier_graph"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("sales.fn_Target", lang: "sql", exact: true, pathPatterns: ["sql_dotted_identifier_graph"]));

        var callers = _reader.GetCallers("sales.fn_Target", lang: "sql", exact: true, pathPatterns: ["sql_dotted_identifier_graph"]);
        var caller = Assert.Single(callers);
        Assert.Equal("sales.caller", caller.CallerName);
        Assert.Equal(1, caller.ReferenceCount);
        Assert.Equal(1, _reader.CountCallers("sales.fn_Target", lang: "sql", exact: true, pathPatterns: ["sql_dotted_identifier_graph"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountCallersTotal("sales.fn_Target", lang: "sql", exact: true, pathPatterns: ["sql_dotted_identifier_graph"]));

        var impact = _reader.AnalyzeImpact("sales.fn_Target", maxDepth: 1, limit: 10, lang: "sql", pathPatterns: ["sql_dotted_identifier_graph"]);
        Assert.Equal("sales.fn_Target", Assert.Single(impact.Definitions).Name);
        Assert.Equal("sales.caller", Assert.Single(impact.Callers).CallerName);

        var quotedReferences = _reader.SearchReferences("\"sales.fn_Target\"", lang: "sql", exact: true, pathPatterns: ["sql_dotted_identifier_graph"]);
        Assert.Equal(3, quotedReferences.Count);
        Assert.All(quotedReferences, reference => Assert.Equal("quoted.caller", reference.ContainerName));
        Assert.Contains(quotedReferences, reference => reference.Context == "CALL \"sales.fn_Target\";");
        Assert.Contains(quotedReferences, reference => reference.Context == "EXEC \"sales.fn_Target\";");
        Assert.Contains(quotedReferences, reference => reference.Context == "EXECUTE \"sales.fn_Target\";");
        Assert.Equal(3, _reader.CountSearchReferences("\"sales.fn_Target\"", lang: "sql", exact: true, pathPatterns: ["sql_dotted_identifier_graph"]));
        Assert.Equal(new QueryCountResult(3, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("\"sales.fn_Target\"", lang: "sql", exact: true, pathPatterns: ["sql_dotted_identifier_graph"]));

        var quotedCallers = _reader.GetCallers("\"sales.fn_Target\"", lang: "sql", exact: true, pathPatterns: ["sql_dotted_identifier_graph"]);
        var quotedCaller = Assert.Single(quotedCallers);
        Assert.Equal("quoted.caller", quotedCaller.CallerName);
        Assert.Equal(3, quotedCaller.ReferenceCount);
        Assert.Equal(1, _reader.CountCallers("\"sales.fn_Target\"", lang: "sql", exact: true, pathPatterns: ["sql_dotted_identifier_graph"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountCallersTotal("\"sales.fn_Target\"", lang: "sql", exact: true, pathPatterns: ["sql_dotted_identifier_graph"]));

        var quotedImpact = _reader.AnalyzeImpact("\"sales.fn_Target\"", maxDepth: 1, limit: 10, lang: "sql", pathPatterns: ["sql_dotted_identifier_graph"]);
        Assert.Equal("\"sales.fn_Target\"", Assert.Single(quotedImpact.Definitions).Name);
        Assert.Equal("quoted.caller", Assert.Single(quotedImpact.Callers).CallerName);
    }

    [Fact]
    public void SqlQualifiedNames_AggregatesDoNotConflateQuotedSingleIdentifierDotsWithQualifiedNames()
    {
        InsertIndexedFile("src/sql_dotted_identifier_deps_target.sql", "sql",
            """
            CREATE PROCEDURE sales.fn_Target
            AS
            SELECT 1;
            GO
            """);

        InsertIndexedFile("src/sql_dotted_identifier_deps_quoted.sql", "sql",
            """
            CREATE PROCEDURE "sales.fn_Target"
            AS
            SELECT 2;
            GO
            """);

        InsertIndexedFile("src/sql_dotted_identifier_deps_caller.sql", "sql",
            """
            CREATE PROCEDURE sales.caller
            AS
            BEGIN
                EXEC sales.fn_Target;
            END
            GO
            """);

        var dependency = Assert.Single(
            _reader.GetFileDependencies(limit: 10, lang: "sql", pathPatterns: ["sql_dotted_identifier_deps"], excludePathPatterns: null, excludeTests: false));
        Assert.Equal("src/sql_dotted_identifier_deps_caller.sql", dependency.SourcePath);
        Assert.Equal("src/sql_dotted_identifier_deps_target.sql", dependency.TargetPath);
        Assert.Equal(1, dependency.ReferenceCount);
        Assert.Equal("sales.fn_Target", dependency.Symbols);

        var hotspots = _reader.GetSymbolHotspots(10, "function", "sql", ["sql_dotted_identifier_deps"], null, false);
        Assert.Equal(1, Assert.Single(hotspots, item => item.Symbol.Name == "sales.fn_Target").ReferenceCount);
        Assert.DoesNotContain(hotspots, item => item.Symbol.Name == "\"sales.fn_Target\"");

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: "function", lang: "sql",
            pathPatterns: ["sql_dotted_identifier_deps"], excludePathPatterns: null, excludeTests: false);
        Assert.DoesNotContain(unused, symbol => symbol.Name == "sales.fn_Target");
        Assert.Contains(unused, symbol => symbol.Name == "\"sales.fn_Target\"");
    }

    [Fact]
    public void SqlQualifiedNames_QuotedSingleIdentifierContainersDoNotDonateFakeQualifiersToLeafFallback()
    {
        InsertIndexedFile("src/sql_quoted_container_leaf_fallback_schema_target.sql", "sql",
            """
            CREATE PROCEDURE sales.fn_Target
            AS
            SELECT 1;
            GO
            """);

        InsertIndexedFile("src/sql_quoted_container_leaf_fallback_quoted_target.sql", "sql",
            """
            CREATE PROCEDURE "fn_Target"
            AS
            SELECT 2;
            GO
            """);

        InsertIndexedFile("src/sql_quoted_container_leaf_fallback_caller.sql", "sql",
            """
            CREATE PROCEDURE "sales.Caller"
            AS
            BEGIN
                EXEC fn_Target;
            END
            GO
            """);

        Assert.Empty(_reader.GetCallers("sales.fn_Target", lang: "sql", exact: true, pathPatterns: ["sql_quoted_container_leaf_fallback"]));
        Assert.Equal(0, _reader.CountCallers("sales.fn_Target", lang: "sql", exact: true, pathPatterns: ["sql_quoted_container_leaf_fallback"]));
        Assert.Equal(new QueryCountResult(0, 0), _reader.CountCallersTotal("sales.fn_Target", lang: "sql", exact: true, pathPatterns: ["sql_quoted_container_leaf_fallback"]));

        var leafCaller = Assert.Single(_reader.GetCallers("fn_Target", lang: "sql", exact: true, pathPatterns: ["sql_quoted_container_leaf_fallback"]));
        Assert.Equal("\"sales.Caller\"", leafCaller.CallerName);
        Assert.Equal(1, leafCaller.ReferenceCount);

        var dependency = Assert.Single(
            _reader.GetFileDependencies(limit: 10, lang: "sql", pathPatterns: ["sql_quoted_container_leaf_fallback"], excludePathPatterns: null, excludeTests: false));
        Assert.Equal("src/sql_quoted_container_leaf_fallback_caller.sql", dependency.SourcePath);
        Assert.Equal("src/sql_quoted_container_leaf_fallback_quoted_target.sql", dependency.TargetPath);
        Assert.Equal(1, dependency.ReferenceCount);
        Assert.Equal("fn_Target", dependency.Symbols);

        var hotspots = _reader.GetSymbolHotspots(10, "function", "sql", ["sql_quoted_container_leaf_fallback"], null, false);
        Assert.Equal(1, Assert.Single(hotspots, item => item.Symbol.Name == "\"fn_Target\"").ReferenceCount);
        Assert.DoesNotContain(hotspots, item => item.Symbol.Name == "sales.fn_Target");

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: "function", lang: "sql",
            pathPatterns: ["sql_quoted_container_leaf_fallback"], excludePathPatterns: null, excludeTests: false);
        Assert.DoesNotContain(unused, symbol => symbol.Name == "\"fn_Target\"");
        Assert.Contains(unused, symbol => symbol.Name == "sales.fn_Target");
    }

    [Fact]
    public void SqlQualifiedNames_UnicodeExactGraphReadersPreserveFoldedLeafFallback()
    {
        InsertIndexedFile("src/sql_unicode_exact_leaf_fallback.sql", "sql",
            """
            CREATE PROCEDURE dbo.Äpfel
            AS
            SELECT 1;
            GO

            CREATE PROCEDURE dbo.Caller
            AS
            EXEC dbo.äpfel;
            GO

            CREATE PROCEDURE dbo.ÄCaller
            AS
            EXEC dbo.Äpfel;
            GO
            """);

        var references = _reader.SearchReferences("dbo.Äpfel", lang: "sql", exact: true, pathPatterns: ["sql_unicode_exact_leaf_fallback"]);
        Assert.Equal(2, references.Count);
        Assert.Contains(references, reference => reference.ContainerName == "dbo.Caller" && reference.Line == 8);
        Assert.Contains(references, reference => reference.ContainerName == "dbo.ÄCaller" && reference.Line == 13);
        Assert.Equal(2, _reader.CountSearchReferences("dbo.Äpfel", lang: "sql", exact: true, pathPatterns: ["sql_unicode_exact_leaf_fallback"]));

        var callers = _reader.GetCallers("dbo.Äpfel", lang: "sql", exact: true, pathPatterns: ["sql_unicode_exact_leaf_fallback"]);
        Assert.Equal(2, callers.Count);
        Assert.Contains(callers, item => item.CallerName == "dbo.Caller");
        Assert.Contains(callers, item => item.CallerName == "dbo.ÄCaller");
        Assert.Equal(2, _reader.CountCallers("dbo.Äpfel", lang: "sql", exact: true, pathPatterns: ["sql_unicode_exact_leaf_fallback"]));

        var callee = Assert.Single(_reader.GetCallees("äcaller", lang: "sql", exact: true, pathPatterns: ["sql_unicode_exact_leaf_fallback"]));
        Assert.Equal("Äpfel", callee.CalleeName);
        Assert.Equal(1, _reader.CountCallees("äcaller", lang: "sql", exact: true, pathPatterns: ["sql_unicode_exact_leaf_fallback"]));

        var impact = _reader.AnalyzeImpact("dbo.Äpfel", maxDepth: 1, limit: 10, lang: "sql", pathPatterns: ["sql_unicode_exact_leaf_fallback"]);
        Assert.Equal(2, impact.Callers.Count);
        Assert.Contains(impact.Callers, item => item.CallerName == "dbo.Caller");
        Assert.Contains(impact.Callers, item => item.CallerName == "dbo.ÄCaller");
    }

    [Fact]
    public void SqlQualifiedNames_QualifiedSqlReadersStaySchemaScoped()
    {
        InsertIndexedFile("src/sql_schema_scoped_target_dbo.sql", "sql",
            """
            CREATE PROCEDURE dbo.fn_Target
            AS
            SELECT 1;
            GO
            """);

        InsertIndexedFile("src/sql_schema_scoped_target_sales.sql", "sql",
            """
            CREATE PROCEDURE sales.fn_Target
            AS
            SELECT 2;
            GO
            """);

        InsertIndexedFile("src/sql_schema_scoped_caller.sql", "sql",
            """
            CREATE PROCEDURE dbo.Caller
            AS
            EXEC dbo.fn_Target;
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.fn_Target", lang: "sql", exact: true, pathPatterns: ["sql_schema_scoped"]));
        Assert.Equal("dbo.Caller", reference.ContainerName);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.fn_Target", lang: "sql", exact: true, pathPatterns: ["sql_schema_scoped"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.fn_Target", lang: "sql", exact: true, pathPatterns: ["sql_schema_scoped"]));

        var caller = Assert.Single(
            _reader.GetCallers("dbo.fn_Target", lang: "sql", exact: true, pathPatterns: ["sql_schema_scoped"]));
        Assert.Equal("dbo.Caller", caller.CallerName);
        Assert.Equal(1, _reader.CountCallers("dbo.fn_Target", lang: "sql", exact: true, pathPatterns: ["sql_schema_scoped"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountCallersTotal("dbo.fn_Target", lang: "sql", exact: true, pathPatterns: ["sql_schema_scoped"]));

        Assert.Equal("dbo.fn_Target", SqlNameResolver.ResolveReferenceNameAtColumn("fn_Target", "EXEC dbo.fn_Target;", "dbo.Caller", 1));
        Assert.False(SqlNameResolver.AllowLeafFallbackAtColumn("fn_Target", "EXEC dbo.fn_Target;", "dbo.Caller", 1));

        var impact = _reader.AnalyzeImpact("dbo.fn_Target", maxDepth: 1, limit: 10, lang: "sql", pathPatterns: ["sql_schema_scoped"]);
        Assert.Equal("dbo.Caller", Assert.Single(impact.Callers).CallerName);

        var dependency = Assert.Single(
            _reader.GetFileDependencies(limit: 10, lang: "sql", pathPatterns: ["sql_schema_scoped_caller.sql"], excludePathPatterns: null, excludeTests: false));
        Assert.Equal("src/sql_schema_scoped_caller.sql", dependency.SourcePath);
        Assert.Equal("src/sql_schema_scoped_target_dbo.sql", dependency.TargetPath);
        Assert.Equal(1, dependency.ReferenceCount);

        var hotspots = _reader.GetSymbolHotspots(10, "function", "sql", ["sql_schema_scoped"], null, false);
        var hotspot = Assert.Single(hotspots, item => item.Symbol.Name == "dbo.fn_Target");
        Assert.Equal(1, hotspot.ReferenceCount);
        Assert.DoesNotContain(hotspots, item => item.Symbol.Name == "sales.fn_Target");

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: "function", lang: "sql",
            pathPatterns: ["sql_schema_scoped"], excludePathPatterns: null, excludeTests: false);
        Assert.Contains(unused, symbol => symbol.Name == "sales.fn_Target");
        Assert.DoesNotContain(unused, symbol => symbol.Name == "dbo.fn_Target");
    }

    [Fact]
    public void SqlQualifiedNames_SameLineMultipleQualifiedCallsStayColumnScoped()
    {
        InsertIndexedFile("src/sql_same_line_multi_target_dbo.sql", "sql",
            """
            CREATE PROCEDURE dbo.fn_Target
            AS
            SELECT 1;
            GO
            """);

        InsertIndexedFile("src/sql_same_line_multi_target_sales.sql", "sql",
            """
            CREATE PROCEDURE sales.fn_Target
            AS
            SELECT 2;
            GO
            """);

        InsertIndexedFile("src/sql_same_line_multi_caller.sql", "sql",
            """
            CREATE PROCEDURE dbo.Caller
            AS
            BEGIN
                EXEC dbo.fn_Target; EXEC sales.fn_Target;
            END
            GO
            """);

        var dboReference = Assert.Single(
            _reader.SearchReferences("dbo.fn_Target", lang: "sql", exact: true, pathPatterns: ["sql_same_line_multi"]));
        Assert.Equal("dbo.Caller", dboReference.ContainerName);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.fn_Target", lang: "sql", exact: true, pathPatterns: ["sql_same_line_multi"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.fn_Target", lang: "sql", exact: true, pathPatterns: ["sql_same_line_multi"]));

        var salesReference = Assert.Single(
            _reader.SearchReferences("sales.fn_Target", lang: "sql", exact: true, pathPatterns: ["sql_same_line_multi"]));
        Assert.Equal("dbo.Caller", salesReference.ContainerName);
        Assert.Equal(1, _reader.CountSearchReferences("sales.fn_Target", lang: "sql", exact: true, pathPatterns: ["sql_same_line_multi"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("sales.fn_Target", lang: "sql", exact: true, pathPatterns: ["sql_same_line_multi"]));

        var dboCaller = Assert.Single(
            _reader.GetCallers("dbo.fn_Target", lang: "sql", exact: true, pathPatterns: ["sql_same_line_multi"]));
        Assert.Equal("dbo.Caller", dboCaller.CallerName);
        Assert.Equal(1, dboCaller.ReferenceCount);
        Assert.Equal(1, _reader.CountCallers("dbo.fn_Target", lang: "sql", exact: true, pathPatterns: ["sql_same_line_multi"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountCallersTotal("dbo.fn_Target", lang: "sql", exact: true, pathPatterns: ["sql_same_line_multi"]));

        var salesCaller = Assert.Single(
            _reader.GetCallers("sales.fn_Target", lang: "sql", exact: true, pathPatterns: ["sql_same_line_multi"]));
        Assert.Equal("dbo.Caller", salesCaller.CallerName);
        Assert.Equal(1, salesCaller.ReferenceCount);
        Assert.Equal(1, _reader.CountCallers("sales.fn_Target", lang: "sql", exact: true, pathPatterns: ["sql_same_line_multi"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountCallersTotal("sales.fn_Target", lang: "sql", exact: true, pathPatterns: ["sql_same_line_multi"]));

        var dependencies = _reader.GetFileDependencies(limit: 10, lang: "sql", pathPatterns: ["sql_same_line_multi"], excludePathPatterns: null, excludeTests: false)
            .OrderBy(edge => edge.TargetPath, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(2, dependencies.Count);
        Assert.Collection(dependencies,
            edge =>
            {
                Assert.Equal("src/sql_same_line_multi_caller.sql", edge.SourcePath);
                Assert.Equal("src/sql_same_line_multi_target_dbo.sql", edge.TargetPath);
                Assert.Equal(1, edge.ReferenceCount);
            },
            edge =>
            {
                Assert.Equal("src/sql_same_line_multi_caller.sql", edge.SourcePath);
                Assert.Equal("src/sql_same_line_multi_target_sales.sql", edge.TargetPath);
                Assert.Equal(1, edge.ReferenceCount);
            });

        var hotspots = _reader.GetSymbolHotspots(10, "function", "sql", ["sql_same_line_multi"], null, false);
        Assert.Equal(1, Assert.Single(hotspots, item => item.Symbol.Name == "dbo.fn_Target").ReferenceCount);
        Assert.Equal(1, Assert.Single(hotspots, item => item.Symbol.Name == "sales.fn_Target").ReferenceCount);
    }

    [Fact]
    public void SqlQualifiedNames_ExactCalleesStaySchemaScoped()
    {
        InsertIndexedFile("src/sql_callee_schema_scoped.sql", "sql",
            """
            CREATE FUNCTION dbo.fn_A()
            RETURNS INT
            AS
            BEGIN
                RETURN 1;
            END
            GO

            CREATE FUNCTION sales.fn_B()
            RETURNS INT
            AS
            BEGIN
                RETURN 2;
            END
            GO

            CREATE PROCEDURE dbo.usp_GetOrders
            AS
            BEGIN
                SELECT dbo.fn_A();
            END
            GO

            CREATE PROCEDURE sales.usp_GetOrders
            AS
            BEGIN
                SELECT sales.fn_B();
            END
            GO
            """);

        var callee = Assert.Single(_reader.GetCallees("dbo.usp_GetOrders", lang: "sql", exact: true, pathPatterns: ["sql_callee_schema_scoped"]));
        Assert.Equal("fn_A", callee.CalleeName);
        Assert.Equal("dbo.usp_GetOrders", callee.CallerName);
        Assert.Equal(1, _reader.CountCallees("dbo.usp_GetOrders", lang: "sql", exact: true, pathPatterns: ["sql_callee_schema_scoped"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountCalleesTotal("dbo.usp_GetOrders", lang: "sql", exact: true, pathPatterns: ["sql_callee_schema_scoped"]));
    }

    [Fact]
    public void SqlQualifiedNames_NonExactQualifiedReadersStaySchemaScoped()
    {
        InsertIndexedFile("src/sql_nonexact_schema_scoped_targets.sql", "sql",
            """
            CREATE FUNCTION dbo.fn_Target()
            RETURNS INT
            AS
            BEGIN
                RETURN 1;
            END
            GO

            CREATE FUNCTION sales.fn_Target()
            RETURNS INT
            AS
            BEGIN
                RETURN 2;
            END
            GO
            """);

        InsertIndexedFile("src/sql_nonexact_schema_scoped_callers.sql", "sql",
            """
            CREATE PROCEDURE dbo.Caller
            AS
            BEGIN
                EXEC dbo.fn_Target;
            END
            GO

            CREATE PROCEDURE sales.Caller
            AS
            BEGIN
                EXEC sales.fn_Target;
            END
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("sales.fn_Target", lang: "sql", pathPatterns: ["sql_nonexact_schema_scoped"]));
        Assert.Equal("sales.Caller", reference.ContainerName);
        Assert.Equal(1, _reader.CountSearchReferences("sales.fn_Target", lang: "sql", pathPatterns: ["sql_nonexact_schema_scoped"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("sales.fn_Target", lang: "sql", pathPatterns: ["sql_nonexact_schema_scoped"]));

        var caller = Assert.Single(
            _reader.GetCallers("sales.fn_Target", lang: "sql", pathPatterns: ["sql_nonexact_schema_scoped"]));
        Assert.Equal("sales.Caller", caller.CallerName);
        Assert.Equal(1, caller.ReferenceCount);
        Assert.Equal(1, _reader.CountCallers("sales.fn_Target", lang: "sql", pathPatterns: ["sql_nonexact_schema_scoped"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountCallersTotal("sales.fn_Target", lang: "sql", pathPatterns: ["sql_nonexact_schema_scoped"]));

        var callee = Assert.Single(
            _reader.GetCallees("sales.Caller", lang: "sql", pathPatterns: ["sql_nonexact_schema_scoped"]));
        Assert.Equal("fn_Target", callee.CalleeName);
        Assert.Equal("sales.Caller", callee.CallerName);
        Assert.Equal(1, _reader.CountCallees("sales.Caller", lang: "sql", pathPatterns: ["sql_nonexact_schema_scoped"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountCalleesTotal("sales.Caller", lang: "sql", pathPatterns: ["sql_nonexact_schema_scoped"]));
    }

    [Fact]
    public void SqlQualifiedNames_ExactCalleesNormalizeBracketedCallerNames()
    {
        InsertIndexedFile("src/sql_exact_bracketed_callee_targets.sql", "sql",
            """
            CREATE PROCEDURE [dbo].[fn_Target]
            AS
            BEGIN
                SELECT 1;
            END
            GO

            CREATE PROCEDURE [sales].[fn_Target]
            AS
            BEGIN
                EXEC [sales].[fn_Target];
                EXEC fn_Target;
            END
            GO
            """);

        var normalizedCallee = Assert.Single(
            _reader.GetCallees("sales.fn_Target", lang: "sql", exact: true, pathPatterns: ["sql_exact_bracketed_callee"]));
        Assert.Equal("[sales].[fn_Target]", normalizedCallee.CallerName);
        Assert.Equal("fn_Target", normalizedCallee.CalleeName);
        Assert.Equal(2, normalizedCallee.ReferenceCount);
        Assert.Equal(1, _reader.CountCallees("sales.fn_Target", lang: "sql", exact: true, pathPatterns: ["sql_exact_bracketed_callee"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountCalleesTotal("sales.fn_Target", lang: "sql", exact: true, pathPatterns: ["sql_exact_bracketed_callee"]));

        var bracketedCallee = Assert.Single(
            _reader.GetCallees("[sales].[fn_Target]", lang: "sql", exact: true, pathPatterns: ["sql_exact_bracketed_callee"]));
        Assert.Equal("[sales].[fn_Target]", bracketedCallee.CallerName);
        Assert.Equal("fn_Target", bracketedCallee.CalleeName);

        Assert.DoesNotContain(
            _reader.GetCallees("dbo.fn_Target", lang: "sql", exact: true, pathPatterns: ["sql_exact_bracketed_callee"]),
            item => item.CallerName == "[sales].[fn_Target]");
    }

    [Fact]
    public void SqlQualifiedNames_WhitespaceAroundDotsStillResolvesDefinitionsAndSameLineCalls()
    {
        InsertIndexedFile("src/sql_spaced_qualified_names.sql", "sql",
            """
            CREATE PROCEDURE [dbo].[fn_Target]
            AS
            SELECT 1;
            GO

            CREATE PROCEDURE [sales] . [fn_Target] AS EXEC [dbo] . [fn_Target];
            GO
            """);

        var definition = Assert.Single(
            _reader.GetDefinitions("sales.fn_Target", limit: 10, lang: "sql", pathPatterns: ["sql_spaced_qualified_names"], exact: true));
        Assert.Contains("fn_Target", definition.Name, StringComparison.Ordinal);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.fn_Target", lang: "sql", exact: true, pathPatterns: ["sql_spaced_qualified_names"]));
        Assert.Contains("fn_Target", reference.ContainerName ?? string.Empty, StringComparison.Ordinal);

        var caller = Assert.Single(
            _reader.GetCallers("dbo.fn_Target", lang: "sql", exact: true, pathPatterns: ["sql_spaced_qualified_names"]));
        Assert.Contains("fn_Target", caller.CallerName ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void SqlQualifiedNames_AlterTableReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_alter_table_reference_target.sql", "sql",
            """
            CREATE TABLE dbo.Orders (Id int);
            GO
            """);

        InsertIndexedFile("src/sql_alter_table_reference_migration.sql", "sql",
            """
            ALTER TABLE dbo.Orders ADD UpdatedAt datetime2 NULL;
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_alter_table_reference"]));
        Assert.Equal("src/sql_alter_table_reference_migration.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_alter_table_reference"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_alter_table_reference"]));
    }

    [Fact]
    public void SqlQualifiedNames_DropTableReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_drop_table_reference_targets.sql", "sql",
            """
            CREATE TABLE dbo.OldOrders (Id int);
            GO
            CREATE TABLE sales.OldInvoices (Id int);
            GO
            """);

        InsertIndexedFile("src/sql_drop_table_reference_migration.sql", "sql",
            """
            DROP TABLE IF EXISTS dbo.OldOrders, sales.OldInvoices;
            GO
            """);

        var references = _reader.SearchReferences("sales.OldInvoices", lang: "sql", exact: true, pathPatterns: ["sql_drop_table_reference"]);
        var reference = Assert.Single(references);
        Assert.Equal("src/sql_drop_table_reference_migration.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("sales.OldInvoices", lang: "sql", exact: true, pathPatterns: ["sql_drop_table_reference"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("sales.OldInvoices", lang: "sql", exact: true, pathPatterns: ["sql_drop_table_reference"]));
    }

    [Fact]
    public void SqlQualifiedNames_InsertWithoutIntoReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_insert_without_into_target.sql", "sql",
            """
            CREATE TABLE dbo.AuditLog (Action nvarchar(100));
            GO
            """);

        InsertIndexedFile("src/sql_insert_without_into_writer.sql", "sql",
            """
            INSERT dbo.AuditLog (Action) VALUES ('login');
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.AuditLog", lang: "sql", exact: true, pathPatterns: ["sql_insert_without_into"]));
        Assert.Equal("src/sql_insert_without_into_writer.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.AuditLog", lang: "sql", exact: true, pathPatterns: ["sql_insert_without_into"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.AuditLog", lang: "sql", exact: true, pathPatterns: ["sql_insert_without_into"]));
    }

    [Fact]
    public void SqlQualifiedNames_SelectIntoReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_select_into_target.sql", "sql",
            """
            CREATE TABLE dbo.OrderArchive (Id int);
            GO
            """);

        InsertIndexedFile("src/sql_select_into_writer.sql", "sql",
            """
            SELECT Id INTO dbo.OrderArchive FROM dbo.Orders;
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.OrderArchive", lang: "sql", exact: true, pathPatterns: ["sql_select_into"]));
        Assert.Equal("src/sql_select_into_writer.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.OrderArchive", lang: "sql", exact: true, pathPatterns: ["sql_select_into"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.OrderArchive", lang: "sql", exact: true, pathPatterns: ["sql_select_into"]));
    }

    [Fact]
    public void SqlQualifiedNames_BulkInsertReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_bulk_insert_target.sql", "sql",
            """
            CREATE TABLE dbo.ImportQueue (Payload nvarchar(max));
            GO
            """);

        InsertIndexedFile("src/sql_bulk_insert_writer.sql", "sql",
            """
            BULK INSERT dbo.ImportQueue FROM 'queue.csv';
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.ImportQueue", lang: "sql", exact: true, pathPatterns: ["sql_bulk_insert"]));
        Assert.Equal("src/sql_bulk_insert_writer.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.ImportQueue", lang: "sql", exact: true, pathPatterns: ["sql_bulk_insert"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.ImportQueue", lang: "sql", exact: true, pathPatterns: ["sql_bulk_insert"]));
    }

    [Fact]
    public void SqlQualifiedNames_CreateIndexReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_create_index_target.sql", "sql",
            """
            CREATE TABLE dbo.Orders (Id int, CreatedAt datetime2);
            GO
            """);

        InsertIndexedFile("src/sql_create_index_definition.sql", "sql",
            """
            CREATE INDEX IX_Orders_CreatedAt ON dbo.Orders (CreatedAt);
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_create_index"]));
        Assert.Equal("src/sql_create_index_definition.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_create_index"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_create_index"]));
    }

    [Fact]
    public void SqlQualifiedNames_AlterIndexReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_alter_index_target.sql", "sql",
            """
            CREATE TABLE dbo.Orders (Id int, CreatedAt datetime2);
            GO
            """);

        InsertIndexedFile("src/sql_alter_index_maintenance.sql", "sql",
            """
            ALTER INDEX IX_Orders_CreatedAt ON dbo.Orders REBUILD;
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_alter_index"]));
        Assert.Equal("src/sql_alter_index_maintenance.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_alter_index"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_alter_index"]));
    }

    [Fact]
    public void SqlQualifiedNames_DropIndexReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_drop_index_target.sql", "sql",
            """
            CREATE TABLE dbo.Orders (Id int, CreatedAt datetime2);
            GO
            """);

        InsertIndexedFile("src/sql_drop_index_cleanup.sql", "sql",
            """
            DROP INDEX IX_Orders_CreatedAt ON dbo.Orders;
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_drop_index"]));
        Assert.Equal("src/sql_drop_index_cleanup.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_drop_index"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_drop_index"]));
    }

    [Fact]
    public void SqlQualifiedNames_CreateTriggerReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_create_trigger_target.sql", "sql",
            """
            CREATE TABLE dbo.Orders (Id int);
            GO
            """);

        InsertIndexedFile("src/sql_create_trigger_definition.sql", "sql",
            """
            CREATE TRIGGER dbo.trg_Orders_Audit ON dbo.Orders AFTER INSERT AS SELECT 1;
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_create_trigger"]));
        Assert.Equal("src/sql_create_trigger_definition.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_create_trigger"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_create_trigger"]));
    }

    [Fact]
    public void SqlQualifiedNames_DisableTriggerReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_disable_trigger_target.sql", "sql",
            """
            CREATE TABLE dbo.Orders (Id int);
            GO
            """);

        InsertIndexedFile("src/sql_disable_trigger_maintenance.sql", "sql",
            """
            DISABLE TRIGGER dbo.trg_Orders_Audit ON dbo.Orders;
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_disable_trigger"]));
        Assert.Equal("src/sql_disable_trigger_maintenance.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_disable_trigger"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_disable_trigger"]));
    }

    [Fact]
    public void SqlQualifiedNames_ForeignKeyReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_foreign_key_target.sql", "sql",
            """
            CREATE TABLE dbo.Customers (Id int);
            GO
            """);

        InsertIndexedFile("src/sql_foreign_key_source.sql", "sql",
            """
            ALTER TABLE dbo.Orders ADD CONSTRAINT FK_Orders_Customers FOREIGN KEY (CustomerId) REFERENCES dbo.Customers (Id);
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.Customers", lang: "sql", exact: true, pathPatterns: ["sql_foreign_key"]));
        Assert.Equal("src/sql_foreign_key_source.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.Customers", lang: "sql", exact: true, pathPatterns: ["sql_foreign_key"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.Customers", lang: "sql", exact: true, pathPatterns: ["sql_foreign_key"]));
    }

    [Fact]
    public void SqlQualifiedNames_CreateSynonymReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_synonym_target.sql", "sql",
            """
            CREATE TABLE dbo.Customers (Id int);
            GO
            """);

        InsertIndexedFile("src/sql_synonym_definition.sql", "sql",
            """
            CREATE SYNONYM dbo.CustomerAlias FOR dbo.Customers;
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.Customers", lang: "sql", exact: true, pathPatterns: ["sql_synonym"]));
        Assert.Equal("src/sql_synonym_definition.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.Customers", lang: "sql", exact: true, pathPatterns: ["sql_synonym"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.Customers", lang: "sql", exact: true, pathPatterns: ["sql_synonym"]));
    }

    [Fact]
    public void SqlQualifiedNames_AlterSchemaTransferReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_alter_schema_transfer_target.sql", "sql",
            """
            CREATE TABLE dbo.Orders (Id int);
            GO
            """);

        InsertIndexedFile("src/sql_alter_schema_transfer_move.sql", "sql",
            """
            ALTER SCHEMA archive TRANSFER dbo.Orders;
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_alter_schema_transfer"]));
        Assert.Equal("src/sql_alter_schema_transfer_move.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_alter_schema_transfer"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_alter_schema_transfer"]));
    }

    [Fact]
    public void SqlQualifiedNames_UpdateStatisticsReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_update_statistics_target.sql", "sql",
            """
            CREATE TABLE dbo.Orders (Id int);
            GO
            """);

        InsertIndexedFile("src/sql_update_statistics_refresh.sql", "sql",
            """
            UPDATE STATISTICS dbo.Orders WITH FULLSCAN;
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_update_statistics"]));
        Assert.Equal("src/sql_update_statistics_refresh.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_update_statistics"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_update_statistics"]));
    }

    [Fact]
    public void SqlQualifiedNames_CreateStatisticsReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_create_statistics_target.sql", "sql",
            """
            CREATE TABLE dbo.Orders (Id int);
            GO
            """);

        InsertIndexedFile("src/sql_create_statistics_definition.sql", "sql",
            """
            CREATE STATISTICS st_OrderDate ON dbo.Orders (OrderDate);
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_create_statistics"]));
        Assert.Equal("src/sql_create_statistics_definition.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_create_statistics"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_create_statistics"]));
    }

    [Fact]
    public void SqlQualifiedNames_DropStatisticsReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_drop_statistics_target.sql", "sql",
            """
            CREATE TABLE dbo.Orders (Id int);
            GO
            """);

        InsertIndexedFile("src/sql_drop_statistics_cleanup.sql", "sql",
            """
            DROP STATISTICS dbo.Orders.st_OrderDate;
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_drop_statistics"]));
        Assert.Equal("src/sql_drop_statistics_cleanup.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_drop_statistics"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_drop_statistics"]));
    }

    [Fact]
    public void SqlQualifiedNames_AlterTableSwitchTargetReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_alter_table_switch_targets.sql", "sql",
            """
            CREATE TABLE dbo.Orders (Id int);
            CREATE TABLE archive.OrdersArchive (Id int);
            GO
            """);

        InsertIndexedFile("src/sql_alter_table_switch_move.sql", "sql",
            """
            ALTER TABLE dbo.Orders SWITCH TO archive.OrdersArchive;
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("archive.OrdersArchive", lang: "sql", exact: true, pathPatterns: ["sql_alter_table_switch"]));
        Assert.Equal("src/sql_alter_table_switch_move.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("archive.OrdersArchive", lang: "sql", exact: true, pathPatterns: ["sql_alter_table_switch"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("archive.OrdersArchive", lang: "sql", exact: true, pathPatterns: ["sql_alter_table_switch"]));
    }

    [Fact]
    public void SqlQualifiedNames_ObjectPermissionReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_object_permission_target.sql", "sql",
            """
            CREATE TABLE dbo.Orders (Id int);
            GO
            """);

        InsertIndexedFile("src/sql_object_permission_grant.sql", "sql",
            """
            GRANT SELECT ON OBJECT::dbo.Orders TO ReportingRole;
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_object_permission"]));
        Assert.Equal("src/sql_object_permission_grant.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_object_permission"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_object_permission"]));
    }

    [Fact]
    public void SqlQualifiedNames_BareObjectPermissionReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_bare_object_permission_target.sql", "sql",
            """
            CREATE TABLE dbo.Orders (Id int);
            GO
            """);

        InsertIndexedFile("src/sql_bare_object_permission_grant.sql", "sql",
            """
            GRANT SELECT ON dbo.Orders TO ReportingRole;
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_bare_object_permission"]));
        Assert.Equal("src/sql_bare_object_permission_grant.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_bare_object_permission"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_bare_object_permission"]));
    }

    [Fact]
    public void SqlQualifiedNames_CreateFullTextIndexReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_create_fulltext_index_target.sql", "sql",
            """
            CREATE TABLE dbo.Documents (Id int, Title nvarchar(200));
            GO
            """);

        InsertIndexedFile("src/sql_create_fulltext_index_definition.sql", "sql",
            """
            CREATE FULLTEXT INDEX ON dbo.Documents (Title) KEY INDEX PK_Documents;
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.Documents", lang: "sql", exact: true, pathPatterns: ["sql_create_fulltext_index"]));
        Assert.Equal("src/sql_create_fulltext_index_definition.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.Documents", lang: "sql", exact: true, pathPatterns: ["sql_create_fulltext_index"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.Documents", lang: "sql", exact: true, pathPatterns: ["sql_create_fulltext_index"]));
    }

    [Fact]
    public void SqlQualifiedNames_CreateSpecialXmlIndexReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_create_special_xml_index_target.sql", "sql",
            """
            CREATE TABLE dbo.Documents (Id int, Payload xml);
            GO
            """);

        InsertIndexedFile("src/sql_create_special_xml_index_definition.sql", "sql",
            """
            CREATE PRIMARY XML INDEX IX_Documents_Xml ON dbo.Documents (Payload);
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.Documents", lang: "sql", exact: true, pathPatterns: ["sql_create_special_xml_index"]));
        Assert.Equal("src/sql_create_special_xml_index_definition.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.Documents", lang: "sql", exact: true, pathPatterns: ["sql_create_special_xml_index"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.Documents", lang: "sql", exact: true, pathPatterns: ["sql_create_special_xml_index"]));
    }

    [Fact]
    public void SqlQualifiedNames_CreateClusteredColumnstoreIndexReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_create_clustered_columnstore_index_target.sql", "sql",
            """
            CREATE TABLE dbo.FactSales (Id int, Amount money);
            GO
            """);

        InsertIndexedFile("src/sql_create_clustered_columnstore_index_definition.sql", "sql",
            """
            CREATE CLUSTERED COLUMNSTORE INDEX CCI_FactSales ON dbo.FactSales;
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.FactSales", lang: "sql", exact: true, pathPatterns: ["sql_create_clustered_columnstore_index"]));
        Assert.Equal("src/sql_create_clustered_columnstore_index_definition.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.FactSales", lang: "sql", exact: true, pathPatterns: ["sql_create_clustered_columnstore_index"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.FactSales", lang: "sql", exact: true, pathPatterns: ["sql_create_clustered_columnstore_index"]));
    }

    [Fact]
    public void SqlQualifiedNames_CreateHashIndexReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_create_hash_index_target.sql", "sql",
            """
            CREATE TABLE dbo.OrderCache (Id int NOT NULL);
            GO
            """);

        InsertIndexedFile("src/sql_create_hash_index_definition.sql", "sql",
            """
            CREATE NONCLUSTERED HASH INDEX IX_OrderCache_Id
            ON dbo.OrderCache (Id)
            WITH (BUCKET_COUNT = 1024);
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.OrderCache", lang: "sql", exact: true, pathPatterns: ["sql_create_hash_index"]));
        Assert.Equal("src/sql_create_hash_index_definition.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.OrderCache", lang: "sql", exact: true, pathPatterns: ["sql_create_hash_index"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.OrderCache", lang: "sql", exact: true, pathPatterns: ["sql_create_hash_index"]));
    }

    [Fact]
    public void SqlQualifiedNames_AlterFullTextIndexReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_alter_fulltext_index_target.sql", "sql",
            """
            CREATE TABLE dbo.Documents (Id int, Title nvarchar(200));
            GO
            """);

        InsertIndexedFile("src/sql_alter_fulltext_index_maintenance.sql", "sql",
            """
            ALTER FULLTEXT INDEX ON dbo.Documents START FULL POPULATION;
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.Documents", lang: "sql", exact: true, pathPatterns: ["sql_alter_fulltext_index"]));
        Assert.Equal("src/sql_alter_fulltext_index_maintenance.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.Documents", lang: "sql", exact: true, pathPatterns: ["sql_alter_fulltext_index"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.Documents", lang: "sql", exact: true, pathPatterns: ["sql_alter_fulltext_index"]));
    }

    [Fact]
    public void SqlQualifiedNames_DropFullTextIndexReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_drop_fulltext_index_target.sql", "sql",
            """
            CREATE TABLE dbo.Documents (Id int, Title nvarchar(200));
            GO
            """);

        InsertIndexedFile("src/sql_drop_fulltext_index_cleanup.sql", "sql",
            """
            DROP FULLTEXT INDEX ON dbo.Documents;
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.Documents", lang: "sql", exact: true, pathPatterns: ["sql_drop_fulltext_index"]));
        Assert.Equal("src/sql_drop_fulltext_index_cleanup.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.Documents", lang: "sql", exact: true, pathPatterns: ["sql_drop_fulltext_index"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.Documents", lang: "sql", exact: true, pathPatterns: ["sql_drop_fulltext_index"]));
    }

    [Fact]
    public void SqlQualifiedNames_DropIndexLegacyReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_drop_index_legacy_target.sql", "sql",
            """
            CREATE TABLE dbo.Orders (Id int, CreatedAt datetime2);
            GO
            """);

        InsertIndexedFile("src/sql_drop_index_legacy_cleanup.sql", "sql",
            """
            DROP INDEX dbo.Orders.IX_Orders_Date;
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_drop_index_legacy"]));
        Assert.Equal("src/sql_drop_index_legacy_cleanup.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_drop_index_legacy"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_drop_index_legacy"]));
    }

    [Fact]
    public void SqlQualifiedNames_DeleteWithoutFromReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_delete_without_from_target.sql", "sql",
            """
            CREATE TABLE dbo.Orders (Id int);
            GO
            """);

        InsertIndexedFile("src/sql_delete_without_from_cleanup.sql", "sql",
            """
            DELETE dbo.Orders WHERE Id = 1;
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_delete_without_from"]));
        Assert.Equal("src/sql_delete_without_from_cleanup.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_delete_without_from"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_delete_without_from"]));
    }

    [Fact]
    public void SqlQualifiedNames_OutputIntoReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_output_into_target.sql", "sql",
            """
            CREATE TABLE audit.OrderAudit (OrderId int);
            GO
            """);

        InsertIndexedFile("src/sql_output_into_update.sql", "sql",
            """
            UPDATE dbo.Orders SET Status = 'Closed' OUTPUT inserted.Id INTO audit.OrderAudit (OrderId) WHERE Id = 1;
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("audit.OrderAudit", lang: "sql", exact: true, pathPatterns: ["sql_output_into"]));
        Assert.Equal("src/sql_output_into_update.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("audit.OrderAudit", lang: "sql", exact: true, pathPatterns: ["sql_output_into"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("audit.OrderAudit", lang: "sql", exact: true, pathPatterns: ["sql_output_into"]));
    }

    [Fact]
    public void SqlQualifiedNames_AlterAuthorizationObjectReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_alter_authorization_object_target.sql", "sql",
            """
            CREATE TABLE dbo.Orders (Id int);
            GO
            """);

        InsertIndexedFile("src/sql_alter_authorization_object_owner.sql", "sql",
            """
            ALTER AUTHORIZATION ON OBJECT::dbo.Orders TO app_owner;
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_alter_authorization_object"]));
        Assert.Equal("src/sql_alter_authorization_object_owner.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_alter_authorization_object"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_alter_authorization_object"]));
    }

    [Fact]
    public void SqlQualifiedNames_AlterAuthorizationBareObjectReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_alter_authorization_bare_target.sql", "sql",
            """
            CREATE TABLE dbo.Orders (Id int);
            GO
            """);

        InsertIndexedFile("src/sql_alter_authorization_bare_owner.sql", "sql",
            """
            ALTER AUTHORIZATION ON dbo.Orders TO app_owner;
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_alter_authorization_bare"]));
        Assert.Equal("src/sql_alter_authorization_bare_owner.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_alter_authorization_bare"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_alter_authorization_bare"]));
    }

    [Fact]
    public void SqlQualifiedNames_CreateSecurityPolicyReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_create_security_policy_target.sql", "sql",
            """
            CREATE TABLE dbo.Orders (Id int, TenantId int);
            GO
            """);

        InsertIndexedFile("src/sql_create_security_policy_definition.sql", "sql",
            """
            CREATE SECURITY POLICY sec.OrderPolicy
                ADD FILTER PREDICATE sec.fn_tenant(TenantId) ON dbo.Orders;
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_create_security_policy"]));
        Assert.Equal("src/sql_create_security_policy_definition.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_create_security_policy"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_create_security_policy"]));
    }

    [Fact]
    public void SqlQualifiedNames_AlterSecurityPolicyReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_alter_security_policy_target.sql", "sql",
            """
            CREATE TABLE dbo.Orders (Id int, TenantId int);
            GO
            """);

        InsertIndexedFile("src/sql_alter_security_policy_definition.sql", "sql",
            """
            ALTER SECURITY POLICY sec.OrderPolicy
                ADD FILTER PREDICATE sec.fn_tenant(TenantId) ON dbo.Orders;
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_alter_security_policy"]));
        Assert.Equal("src/sql_alter_security_policy_definition.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_alter_security_policy"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["sql_alter_security_policy"]));
    }

    [Fact]
    public void SqlQualifiedNames_AlterTableSystemVersioningReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_alter_table_system_versioning_target.sql", "sql",
            """
            CREATE TABLE history.OrdersHistory (Id int);
            GO
            """);

        InsertIndexedFile("src/sql_alter_table_system_versioning_enable.sql", "sql",
            """
            ALTER TABLE dbo.Orders
                SET (SYSTEM_VERSIONING = ON (HISTORY_TABLE = history.OrdersHistory));
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("history.OrdersHistory", lang: "sql", exact: true, pathPatterns: ["sql_alter_table_system_versioning"]));
        Assert.Equal("src/sql_alter_table_system_versioning_enable.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("history.OrdersHistory", lang: "sql", exact: true, pathPatterns: ["sql_alter_table_system_versioning"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("history.OrdersHistory", lang: "sql", exact: true, pathPatterns: ["sql_alter_table_system_versioning"]));
    }

    [Fact]
    public void SqlQualifiedNames_DropSynonymReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_drop_synonym_target.sql", "sql",
            """
            CREATE SYNONYM dbo.CustomerAlias FOR dbo.Customers;
            GO
            """);

        InsertIndexedFile("src/sql_drop_synonym_cleanup.sql", "sql",
            """
            DROP SYNONYM dbo.CustomerAlias;
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.CustomerAlias", lang: "sql", exact: true, pathPatterns: ["sql_drop_synonym"]));
        Assert.Equal("src/sql_drop_synonym_cleanup.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.CustomerAlias", lang: "sql", exact: true, pathPatterns: ["sql_drop_synonym"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.CustomerAlias", lang: "sql", exact: true, pathPatterns: ["sql_drop_synonym"]));
    }

    [Fact]
    public void SqlQualifiedNames_DropViewReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_drop_view_target.sql", "sql",
            """
            CREATE VIEW dbo.OrderSummary AS SELECT 1 AS Id;
            GO
            """);

        InsertIndexedFile("src/sql_drop_view_cleanup.sql", "sql",
            """
            DROP VIEW dbo.OrderSummary;
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.OrderSummary", lang: "sql", exact: true, pathPatterns: ["sql_drop_view"]));
        Assert.Equal("src/sql_drop_view_cleanup.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.OrderSummary", lang: "sql", exact: true, pathPatterns: ["sql_drop_view"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.OrderSummary", lang: "sql", exact: true, pathPatterns: ["sql_drop_view"]));
    }

    [Fact]
    public void SqlQualifiedNames_DropProcedureReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_drop_procedure_target.sql", "sql",
            """
            CREATE PROCEDURE dbo.RebuildOrders
            AS
            SELECT 1;
            GO
            """);

        InsertIndexedFile("src/sql_drop_procedure_cleanup.sql", "sql",
            """
            DROP PROCEDURE dbo.RebuildOrders;
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.RebuildOrders", lang: "sql", exact: true, pathPatterns: ["sql_drop_procedure"]));
        Assert.Equal("src/sql_drop_procedure_cleanup.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.RebuildOrders", lang: "sql", exact: true, pathPatterns: ["sql_drop_procedure"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.RebuildOrders", lang: "sql", exact: true, pathPatterns: ["sql_drop_procedure"]));
    }

    [Fact]
    public void SqlQualifiedNames_DropFunctionReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_drop_function_target.sql", "sql",
            """
            CREATE FUNCTION dbo.CalculateTax()
            RETURNS int
            AS
            BEGIN
                RETURN 1;
            END;
            GO
            """);

        InsertIndexedFile("src/sql_drop_function_cleanup.sql", "sql",
            """
            DROP FUNCTION dbo.CalculateTax;
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.CalculateTax", lang: "sql", exact: true, pathPatterns: ["sql_drop_function"]));
        Assert.Equal("src/sql_drop_function_cleanup.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.CalculateTax", lang: "sql", exact: true, pathPatterns: ["sql_drop_function"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.CalculateTax", lang: "sql", exact: true, pathPatterns: ["sql_drop_function"]));
    }

    [Fact]
    public void SqlQualifiedNames_DropTriggerReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_drop_trigger_target.sql", "sql",
            """
            CREATE TRIGGER audit.OrdersAudit
            ON dbo.Orders
            AFTER INSERT
            AS
            SELECT 1;
            GO
            """);

        InsertIndexedFile("src/sql_drop_trigger_cleanup.sql", "sql",
            """
            DROP TRIGGER audit.OrdersAudit;
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("audit.OrdersAudit", lang: "sql", exact: true, pathPatterns: ["sql_drop_trigger"]));
        Assert.Equal("src/sql_drop_trigger_cleanup.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("audit.OrdersAudit", lang: "sql", exact: true, pathPatterns: ["sql_drop_trigger"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("audit.OrdersAudit", lang: "sql", exact: true, pathPatterns: ["sql_drop_trigger"]));
    }

    [Fact]
    public void SqlQualifiedNames_DropSequenceReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_drop_sequence_target.sql", "sql",
            """
            CREATE SEQUENCE dbo.OrderNumbers
                START WITH 1;
            GO
            """);

        InsertIndexedFile("src/sql_drop_sequence_cleanup.sql", "sql",
            """
            DROP SEQUENCE dbo.OrderNumbers;
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.OrderNumbers", lang: "sql", exact: true, pathPatterns: ["sql_drop_sequence"]));
        Assert.Equal("src/sql_drop_sequence_cleanup.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.OrderNumbers", lang: "sql", exact: true, pathPatterns: ["sql_drop_sequence"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.OrderNumbers", lang: "sql", exact: true, pathPatterns: ["sql_drop_sequence"]));
    }

    [Fact]
    public void SqlQualifiedNames_DropTypeReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_drop_type_target.sql", "sql",
            """
            CREATE TYPE dbo.CustomerKey
                FROM int NOT NULL;
            GO
            """);

        InsertIndexedFile("src/sql_drop_type_cleanup.sql", "sql",
            """
            DROP TYPE dbo.CustomerKey;
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.CustomerKey", lang: "sql", exact: true, pathPatterns: ["sql_drop_type"]));
        Assert.Equal("src/sql_drop_type_cleanup.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.CustomerKey", lang: "sql", exact: true, pathPatterns: ["sql_drop_type"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.CustomerKey", lang: "sql", exact: true, pathPatterns: ["sql_drop_type"]));
    }

    [Fact]
    public void SqlQualifiedNames_DropRuleReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_drop_rule_target.sql", "sql",
            """
            CREATE RULE dbo.PositiveAmount
            AS
            @amount >= 0;
            GO
            """);

        InsertIndexedFile("src/sql_drop_rule_cleanup.sql", "sql",
            """
            DROP RULE dbo.PositiveAmount;
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.PositiveAmount", lang: "sql", exact: true, pathPatterns: ["sql_drop_rule"]));
        Assert.Equal("src/sql_drop_rule_cleanup.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.PositiveAmount", lang: "sql", exact: true, pathPatterns: ["sql_drop_rule"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.PositiveAmount", lang: "sql", exact: true, pathPatterns: ["sql_drop_rule"]));
    }

    [Fact]
    public void SqlQualifiedNames_DropDefaultReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_drop_default_target.sql", "sql",
            """
            CREATE DEFAULT dbo.ZeroDefault
            AS
            0;
            GO
            """);

        InsertIndexedFile("src/sql_drop_default_cleanup.sql", "sql",
            """
            DROP DEFAULT dbo.ZeroDefault;
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.ZeroDefault", lang: "sql", exact: true, pathPatterns: ["sql_drop_default"]));
        Assert.Equal("src/sql_drop_default_cleanup.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.ZeroDefault", lang: "sql", exact: true, pathPatterns: ["sql_drop_default"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.ZeroDefault", lang: "sql", exact: true, pathPatterns: ["sql_drop_default"]));
    }

    [Fact]
    public void SqlQualifiedNames_DropAggregateReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_drop_aggregate_target.sql", "sql",
            """
            CREATE AGGREGATE dbo.TotalAmount(@value int)
            RETURNS int
            EXTERNAL NAME SalesAssembly.TotalAmount;
            GO
            """);

        InsertIndexedFile("src/sql_drop_aggregate_cleanup.sql", "sql",
            """
            DROP AGGREGATE dbo.TotalAmount;
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.TotalAmount", lang: "sql", exact: true, pathPatterns: ["sql_drop_aggregate"]));
        Assert.Equal("src/sql_drop_aggregate_cleanup.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.TotalAmount", lang: "sql", exact: true, pathPatterns: ["sql_drop_aggregate"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.TotalAmount", lang: "sql", exact: true, pathPatterns: ["sql_drop_aggregate"]));
    }

    [Fact]
    public void SqlQualifiedNames_DropSecurityPolicyReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_drop_security_policy_target.sql", "sql",
            """
            CREATE SECURITY POLICY dbo.CustomerFilter
            ADD FILTER PREDICATE dbo.fn_filter(CustomerId) ON dbo.Customers;
            GO
            """);

        InsertIndexedFile("src/sql_drop_security_policy_cleanup.sql", "sql",
            """
            DROP SECURITY POLICY dbo.CustomerFilter;
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.CustomerFilter", lang: "sql", exact: true, pathPatterns: ["sql_drop_security_policy"]));
        Assert.Equal("src/sql_drop_security_policy_cleanup.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.CustomerFilter", lang: "sql", exact: true, pathPatterns: ["sql_drop_security_policy"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.CustomerFilter", lang: "sql", exact: true, pathPatterns: ["sql_drop_security_policy"]));
    }

    [Fact]
    public void SqlQualifiedNames_DropFullTextCatalogReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_drop_fulltext_catalog_target.sql", "sql",
            """
            CREATE FULLTEXT CATALOG ftOrders;
            GO
            """);

        InsertIndexedFile("src/sql_drop_fulltext_catalog_cleanup.sql", "sql",
            """
            DROP FULLTEXT CATALOG ftOrders;
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("ftOrders", lang: "sql", exact: true, pathPatterns: ["sql_drop_fulltext_catalog"]));
        Assert.Equal("src/sql_drop_fulltext_catalog_cleanup.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("ftOrders", lang: "sql", exact: true, pathPatterns: ["sql_drop_fulltext_catalog"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("ftOrders", lang: "sql", exact: true, pathPatterns: ["sql_drop_fulltext_catalog"]));
    }

    [Fact]
    public void SqlQualifiedNames_DropPartitionSchemeReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_drop_partition_scheme_target.sql", "sql",
            """
            CREATE PARTITION SCHEME psOrders
            AS PARTITION pfOrders
            ALL TO ([PRIMARY]);
            GO
            """);

        InsertIndexedFile("src/sql_drop_partition_scheme_cleanup.sql", "sql",
            """
            DROP PARTITION SCHEME psOrders;
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("psOrders", lang: "sql", exact: true, pathPatterns: ["sql_drop_partition_scheme"]));
        Assert.Equal("src/sql_drop_partition_scheme_cleanup.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("psOrders", lang: "sql", exact: true, pathPatterns: ["sql_drop_partition_scheme"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("psOrders", lang: "sql", exact: true, pathPatterns: ["sql_drop_partition_scheme"]));
    }

    [Fact]
    public void SqlQualifiedNames_DropPartitionFunctionReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_drop_partition_function_target.sql", "sql",
            """
            CREATE PARTITION FUNCTION pfOrders(int)
            AS RANGE LEFT FOR VALUES (100);
            GO
            """);

        InsertIndexedFile("src/sql_drop_partition_function_cleanup.sql", "sql",
            """
            DROP PARTITION FUNCTION pfOrders;
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("pfOrders", lang: "sql", exact: true, pathPatterns: ["sql_drop_partition_function"]));
        Assert.Equal("src/sql_drop_partition_function_cleanup.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("pfOrders", lang: "sql", exact: true, pathPatterns: ["sql_drop_partition_function"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("pfOrders", lang: "sql", exact: true, pathPatterns: ["sql_drop_partition_function"]));
    }

    [Fact]
    public void SqlQualifiedNames_DropXmlSchemaCollectionReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_drop_xml_schema_collection_target.sql", "sql",
            """
            CREATE XML SCHEMA COLLECTION dbo.InvoiceSchema AS '<schema/>';
            GO
            """);

        InsertIndexedFile("src/sql_drop_xml_schema_collection_cleanup.sql", "sql",
            """
            DROP XML SCHEMA COLLECTION dbo.InvoiceSchema;
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.InvoiceSchema", lang: "sql", exact: true, pathPatterns: ["sql_drop_xml_schema_collection"]));
        Assert.Equal("src/sql_drop_xml_schema_collection_cleanup.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.InvoiceSchema", lang: "sql", exact: true, pathPatterns: ["sql_drop_xml_schema_collection"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.InvoiceSchema", lang: "sql", exact: true, pathPatterns: ["sql_drop_xml_schema_collection"]));
    }

    [Fact]
    public void SqlQualifiedNames_DropAssemblyReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_drop_assembly_target.sql", "sql",
            """
            CREATE ASSEMBLY SalesAssembly
            FROM 0x4D5A;
            GO
            """);

        InsertIndexedFile("src/sql_drop_assembly_cleanup.sql", "sql",
            """
            DROP ASSEMBLY SalesAssembly;
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("SalesAssembly", lang: "sql", exact: true, pathPatterns: ["sql_drop_assembly"]));
        Assert.Equal("src/sql_drop_assembly_cleanup.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("SalesAssembly", lang: "sql", exact: true, pathPatterns: ["sql_drop_assembly"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("SalesAssembly", lang: "sql", exact: true, pathPatterns: ["sql_drop_assembly"]));
    }

    [Fact]
    public void SqlQualifiedNames_AlterViewReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_alter_view_target.sql", "sql",
            """
            CREATE VIEW dbo.OrderSummary AS SELECT 1 AS Id;
            GO
            """);

        InsertIndexedFile("src/sql_alter_view_update.sql", "sql",
            """
            ALTER VIEW dbo.OrderSummary AS SELECT 2 AS Id;
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.OrderSummary", lang: "sql", exact: true, pathPatterns: ["sql_alter_view"]));
        Assert.Equal("src/sql_alter_view_update.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.OrderSummary", lang: "sql", exact: true, pathPatterns: ["sql_alter_view"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.OrderSummary", lang: "sql", exact: true, pathPatterns: ["sql_alter_view"]));
    }

    [Fact]
    public void SqlQualifiedNames_AlterProcedureReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_alter_procedure_target.sql", "sql",
            """
            CREATE PROCEDURE dbo.RebuildOrders
            AS
            SELECT 1;
            GO
            """);

        InsertIndexedFile("src/sql_alter_procedure_update.sql", "sql",
            """
            ALTER PROCEDURE dbo.RebuildOrders
            AS
            SELECT 2;
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.RebuildOrders", lang: "sql", exact: true, pathPatterns: ["sql_alter_procedure"]));
        Assert.Equal("src/sql_alter_procedure_update.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.RebuildOrders", lang: "sql", exact: true, pathPatterns: ["sql_alter_procedure"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.RebuildOrders", lang: "sql", exact: true, pathPatterns: ["sql_alter_procedure"]));
    }

    [Fact]
    public void SqlQualifiedNames_AlterFunctionReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_alter_function_target.sql", "sql",
            """
            CREATE FUNCTION dbo.CalculateTax()
            RETURNS int
            AS
            BEGIN
                RETURN 1;
            END;
            GO
            """);

        InsertIndexedFile("src/sql_alter_function_update.sql", "sql",
            """
            ALTER FUNCTION dbo.CalculateTax()
            RETURNS int
            AS
            BEGIN
                RETURN 2;
            END;
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.CalculateTax", lang: "sql", exact: true, pathPatterns: ["sql_alter_function"]));
        Assert.Equal("src/sql_alter_function_update.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.CalculateTax", lang: "sql", exact: true, pathPatterns: ["sql_alter_function"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.CalculateTax", lang: "sql", exact: true, pathPatterns: ["sql_alter_function"]));
    }

    [Fact]
    public void SqlQualifiedNames_AlterTriggerReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_alter_trigger_target.sql", "sql",
            """
            CREATE TRIGGER audit.OrdersAudit
            ON dbo.Orders
            AFTER INSERT
            AS
            SELECT 1;
            GO
            """);

        InsertIndexedFile("src/sql_alter_trigger_update.sql", "sql",
            """
            ALTER TRIGGER audit.OrdersAudit
            ON dbo.Orders
            AFTER INSERT
            AS
            SELECT 2;
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("audit.OrdersAudit", lang: "sql", exact: true, pathPatterns: ["sql_alter_trigger"]));
        Assert.Equal("src/sql_alter_trigger_update.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("audit.OrdersAudit", lang: "sql", exact: true, pathPatterns: ["sql_alter_trigger"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("audit.OrdersAudit", lang: "sql", exact: true, pathPatterns: ["sql_alter_trigger"]));
    }

    [Fact]
    public void SqlQualifiedNames_AlterSequenceReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_alter_sequence_target.sql", "sql",
            """
            CREATE SEQUENCE dbo.OrderNumbers
                START WITH 1;
            GO
            """);

        InsertIndexedFile("src/sql_alter_sequence_update.sql", "sql",
            """
            ALTER SEQUENCE dbo.OrderNumbers RESTART WITH 10;
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.OrderNumbers", lang: "sql", exact: true, pathPatterns: ["sql_alter_sequence"]));
        Assert.Equal("src/sql_alter_sequence_update.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.OrderNumbers", lang: "sql", exact: true, pathPatterns: ["sql_alter_sequence"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.OrderNumbers", lang: "sql", exact: true, pathPatterns: ["sql_alter_sequence"]));
    }

    [Fact]
    public void SqlQualifiedNames_AlterSecurityPolicyNameReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_alter_security_policy_name_target.sql", "sql",
            """
            CREATE SECURITY POLICY dbo.CustomerFilter
            ADD FILTER PREDICATE dbo.fn_filter(CustomerId) ON dbo.Customers;
            GO
            """);

        InsertIndexedFile("src/sql_alter_security_policy_name_update.sql", "sql",
            """
            ALTER SECURITY POLICY dbo.CustomerFilter WITH (STATE = OFF);
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.CustomerFilter", lang: "sql", exact: true, pathPatterns: ["sql_alter_security_policy_name"]));
        Assert.Equal("src/sql_alter_security_policy_name_update.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.CustomerFilter", lang: "sql", exact: true, pathPatterns: ["sql_alter_security_policy_name"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.CustomerFilter", lang: "sql", exact: true, pathPatterns: ["sql_alter_security_policy_name"]));
    }

    [Fact]
    public void SqlQualifiedNames_AlterFullTextCatalogReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_alter_fulltext_catalog_target.sql", "sql",
            """
            CREATE FULLTEXT CATALOG ftOrders;
            GO
            """);

        InsertIndexedFile("src/sql_alter_fulltext_catalog_update.sql", "sql",
            """
            ALTER FULLTEXT CATALOG ftOrders REBUILD;
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("ftOrders", lang: "sql", exact: true, pathPatterns: ["sql_alter_fulltext_catalog"]));
        Assert.Equal("src/sql_alter_fulltext_catalog_update.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("ftOrders", lang: "sql", exact: true, pathPatterns: ["sql_alter_fulltext_catalog"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("ftOrders", lang: "sql", exact: true, pathPatterns: ["sql_alter_fulltext_catalog"]));
    }

    [Fact]
    public void SqlQualifiedNames_AlterPartitionFunctionReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_alter_partition_function_target.sql", "sql",
            """
            CREATE PARTITION FUNCTION pfOrders(int)
            AS RANGE LEFT FOR VALUES (100);
            GO
            """);

        InsertIndexedFile("src/sql_alter_partition_function_update.sql", "sql",
            """
            ALTER PARTITION FUNCTION pfOrders() SPLIT RANGE (200);
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("pfOrders", lang: "sql", exact: true, pathPatterns: ["sql_alter_partition_function"]));
        Assert.Equal("src/sql_alter_partition_function_update.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("pfOrders", lang: "sql", exact: true, pathPatterns: ["sql_alter_partition_function"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("pfOrders", lang: "sql", exact: true, pathPatterns: ["sql_alter_partition_function"]));
    }

    [Fact]
    public void SqlQualifiedNames_AlterPartitionSchemeReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_alter_partition_scheme_target.sql", "sql",
            """
            CREATE PARTITION SCHEME psOrders
            AS PARTITION pfOrders
            ALL TO ([PRIMARY]);
            GO
            """);

        InsertIndexedFile("src/sql_alter_partition_scheme_update.sql", "sql",
            """
            ALTER PARTITION SCHEME psOrders NEXT USED [PRIMARY];
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("psOrders", lang: "sql", exact: true, pathPatterns: ["sql_alter_partition_scheme"]));
        Assert.Equal("src/sql_alter_partition_scheme_update.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("psOrders", lang: "sql", exact: true, pathPatterns: ["sql_alter_partition_scheme"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("psOrders", lang: "sql", exact: true, pathPatterns: ["sql_alter_partition_scheme"]));
    }

    [Fact]
    public void SqlQualifiedNames_AlterXmlSchemaCollectionReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_alter_xml_schema_collection_target.sql", "sql",
            """
            CREATE XML SCHEMA COLLECTION dbo.InvoiceSchema AS '<schema/>';
            GO
            """);

        InsertIndexedFile("src/sql_alter_xml_schema_collection_update.sql", "sql",
            """
            ALTER XML SCHEMA COLLECTION dbo.InvoiceSchema ADD '<schema/>';
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.InvoiceSchema", lang: "sql", exact: true, pathPatterns: ["sql_alter_xml_schema_collection"]));
        Assert.Equal("src/sql_alter_xml_schema_collection_update.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.InvoiceSchema", lang: "sql", exact: true, pathPatterns: ["sql_alter_xml_schema_collection"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.InvoiceSchema", lang: "sql", exact: true, pathPatterns: ["sql_alter_xml_schema_collection"]));
    }

    [Fact]
    public void SqlQualifiedNames_AlterAssemblyReferencesResolveThroughSearch()
    {
        InsertIndexedFile("src/sql_alter_assembly_target.sql", "sql",
            """
            CREATE ASSEMBLY SalesAssembly
            FROM 0x4D5A;
            GO
            """);

        InsertIndexedFile("src/sql_alter_assembly_update.sql", "sql",
            """
            ALTER ASSEMBLY SalesAssembly
            FROM 0x4D5A;
            GO
            """);

        var reference = Assert.Single(
            _reader.SearchReferences("SalesAssembly", lang: "sql", exact: true, pathPatterns: ["sql_alter_assembly"]));
        Assert.Equal("src/sql_alter_assembly_update.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("SalesAssembly", lang: "sql", exact: true, pathPatterns: ["sql_alter_assembly"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("SalesAssembly", lang: "sql", exact: true, pathPatterns: ["sql_alter_assembly"]));
    }

    [Fact]
    public void SqlQualifiedNames_QuotedUnicodeExactDefinitionsStayAlignedWithGraphReaders()
    {
        InsertIndexedFile("src/sql_quoted_unicode_exact_definition.sql", "sql",
            """
            CREATE PROCEDURE [dbo].[Äpfel]
            AS
            SELECT 1;
            GO

            CREATE PROCEDURE [dbo].[Caller]
            AS
            EXEC [dbo].[äpfel];
            GO
            """);

        Assert.Equal(1, _reader.CountSearchSymbols(["dbo.äpfel"], lang: "sql", pathPatterns: ["sql_quoted_unicode_exact_definition"], exact: true));

        var symbol = Assert.Single(_reader.SearchSymbols(["dbo.äpfel"], limit: 10, lang: "sql", pathPatterns: ["sql_quoted_unicode_exact_definition"], exact: true));
        Assert.Equal("[dbo].[Äpfel]", symbol.Name);

        var definition = Assert.Single(_reader.GetDefinitions("dbo.äpfel", limit: 10, lang: "sql", pathPatterns: ["sql_quoted_unicode_exact_definition"], exact: true));
        Assert.Equal("[dbo].[Äpfel]", definition.Name);

        var analysis = _reader.AnalyzeSymbol("dbo.äpfel", limit: 10, lang: "sql", pathPatterns: ["sql_quoted_unicode_exact_definition"], exact: true);
        Assert.Equal("[dbo].[Äpfel]", Assert.Single(analysis.Definitions).Name);
        Assert.Equal("[dbo].[Caller]", Assert.Single(analysis.Callers).CallerName);

        var impact = _reader.AnalyzeImpact("dbo.äpfel", maxDepth: 1, limit: 10, lang: "sql", pathPatterns: ["sql_quoted_unicode_exact_definition"]);
        Assert.Equal(1, impact.DefinitionCount);
        Assert.Equal("[dbo].[Äpfel]", Assert.Single(impact.Definitions).Name);
        Assert.Equal("[dbo].[Caller]", Assert.Single(impact.Callers).CallerName);
    }

    [Fact]
    public void SqlQualifiedNames_UnqualifiedUnicodeExactDefinitionsStayAlignedWithGraphReaders()
    {
        InsertIndexedFile("src/sql_unqualified_unicode_exact_definition.sql", "sql",
            """
            CREATE PROCEDURE dbo.Äpfel
            AS
            SELECT 1;
            GO

            CREATE PROCEDURE dbo.Caller
            AS
            EXEC dbo.äpfel;
            GO
            """);

        Assert.Equal(1, _reader.CountSearchSymbols(["äpfel"], lang: "sql", pathPatterns: ["sql_unqualified_unicode_exact_definition"], exact: true));

        var symbol = Assert.Single(_reader.SearchSymbols(["äpfel"], limit: 10, lang: "sql", pathPatterns: ["sql_unqualified_unicode_exact_definition"], exact: true));
        Assert.Equal("dbo.Äpfel", symbol.Name);

        var definition = Assert.Single(_reader.GetDefinitions("äpfel", limit: 10, lang: "sql", pathPatterns: ["sql_unqualified_unicode_exact_definition"], exact: true));
        Assert.Equal("dbo.Äpfel", definition.Name);

        var analysis = _reader.AnalyzeSymbol("äpfel", limit: 10, lang: "sql", pathPatterns: ["sql_unqualified_unicode_exact_definition"], exact: true);
        Assert.Equal("dbo.Äpfel", Assert.Single(analysis.Definitions).Name);
        Assert.Equal("dbo.Caller", Assert.Single(analysis.Callers).CallerName);
    }

    [Fact]
    public void GetFileDependencies_DoesNotJoinSameNameTargetsAcrossLanguages()
    {
        InsertIndexedFile("src/Foo.cs", "csharp",
            """
            public class Foo
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/Caller.cs", "csharp",
            """
            public class Caller
            {
                public void Call(Foo foo)
                {
                    foo.Run();
                }
            }
            """);
        InsertIndexedFile("src/foo.py", "python",
            """
            def Run():
                return True
            """);

        var dependencies = _reader.GetFileDependencies(
            limit: 10,
            lang: "csharp",
            pathPatterns: ["src/Caller.cs"],
            excludePathPatterns: null,
            excludeTests: false);

        var dependency = Assert.Single(dependencies);
        Assert.Equal("src/Caller.cs", dependency.SourcePath);
        Assert.Equal("src/Foo.cs", dependency.TargetPath);
        Assert.Equal(2, dependency.ReferenceCount);
        Assert.DoesNotContain("foo.py", dependency.TargetPath, StringComparison.Ordinal);
    }

    [Fact]
    public void GetFileDependencies_IncludesMetadataReferencesAsCompileTimeDependencies()
    {
        // issue #293 follow-up: the attribute class `JsonConverter` is referenced
        // both as a runtime `new JsonConverter(...)` call AND as compile-time
        // attribute metadata `[JsonConverter(...)]`. Renaming or removing the
        // class breaks both sites, so `cdidx deps` MUST surface both edges as
        // real file-level dependencies. (`callers` / `callees` stay call-graph-
        // only and reject `--kind attribute|annotation` separately at the CLI /
        // MCP boundary — that is a different contract.)
        // issue #293 補足: attribute クラス `JsonConverter` は runtime の
        // `new JsonConverter(...)` としても、compile-time の `[JsonConverter(...)]`
        // 属性 metadata としても参照される。クラスを rename / 削除すれば両方の
        // サイトが壊れるため、`cdidx deps` は両方のエッジをファイル単位の本物の
        // 依存として出す必要がある。(`callers` / `callees` は call-graph 専用で、
        // metadata 種別は CLI / MCP boundary 側で別途拒否する)
        InsertIndexedFile("src/JsonConverterAttribute.cs", "csharp",
            """
            using System;

            [AttributeUsage(AttributeTargets.Class)]
            public class JsonConverter : Attribute
            {
                public Type ConverterType { get; }
                public JsonConverter(Type converterType) => ConverterType = converterType;
            }
            """);
        // Metadata-only usage — attribute form. Compile-time dependency: renaming
        // `JsonConverter` breaks this file at build time.
        // metadata-only の利用 (attribute 形式)。compile-time 依存:
        // `JsonConverter` を rename すればこのファイルも build-time で壊れる。
        InsertIndexedFile("src/Serializer.cs", "csharp",
            """
            [JsonConverter(typeof(int))]
            public class SerializerConfig
            {
            }
            """);
        // Runtime dependency — `new JsonConverter(...)` is a `call` / `instantiate` edge.
        // 実行時の依存 — `new JsonConverter(...)` は `call` / `instantiate` 種別の edge。
        InsertIndexedFile("src/Caller.cs", "csharp",
            """
            public class Caller
            {
                public void Do()
                {
                    var c = new JsonConverter(typeof(int));
                }
            }
            """);

        var dependencies = _reader.GetFileDependencies(limit: 10, lang: "csharp");

        // Both Caller.cs (runtime `instantiate`) and Serializer.cs (attribute
        // metadata) must appear as dependencies of JsonConverterAttribute.cs.
        // Caller.cs (runtime `instantiate`) と Serializer.cs (attribute metadata)
        // の両方が JsonConverterAttribute.cs への依存として現れる。
        Assert.Equal(2, dependencies.Count);
        Assert.Contains(dependencies, d => d.SourcePath == "src/Caller.cs" && d.TargetPath == "src/JsonConverterAttribute.cs");
        Assert.Contains(dependencies, d => d.SourcePath == "src/Serializer.cs" && d.TargetPath == "src/JsonConverterAttribute.cs");
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
    public void GetFileDependencies_MatchesCSharpAttributeSuffixConvention()
    {
        // issue #293 follow-up: C# convention — a class `FooAttribute` is used in
        // source as `[Foo]`, so the reference site is stored with symbol_name `Foo`.
        // `deps` must canonicalize these so the attribute class file is still
        // recognized as a dependency target for pure-attribute consumers.
        // issue #293 補足: C# の規約では、クラス `FooAttribute` はソース中で `[Foo]`
        // として使われるため、参照サイトは symbol_name `Foo` として保存される。
        // `deps` はこれを正規化し、attribute 専用の consumer でも attribute クラスの
        // ファイルを依存 target として認識できるようにする。
        InsertIndexedFile("src/MyAuditAttribute.cs", "csharp",
            """
            using System;

            [AttributeUsage(AttributeTargets.Class)]
            public sealed class MyAuditAttribute : Attribute
            {
            }
            """);
        // Idiomatic `[MyAudit]` usage — symbol_name recorded as `MyAudit` but target
        // class is `MyAuditAttribute`.
        // 慣用的な `[MyAudit]` 利用 — symbol_name は `MyAudit` として記録されるが
        // target クラスは `MyAuditAttribute`。
        InsertIndexedFile("src/Svc.cs", "csharp",
            """
            [MyAudit]
            public class Svc
            {
            }
            """);

        var dependencies = _reader.GetFileDependencies(limit: 10, lang: "csharp");

        Assert.Contains(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/MyAuditAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharpGenericNoArgAttribute_StillIndexedAndResolvesToAttributeClass()
    {
        // issue #293 round-15 follow-up: generic no-arg C# attributes like
        // `[MyAudit<int>]` and multi-line `[\n MyAttr<int>\n]` must still be
        // indexed as `attribute` references so `deps` can route them through
        // the suffix-alias synthesizer to the real attribute class file.
        // Before the regex was widened these forms fell through both CallRegex
        // (no `(`) and the no-arg regex (generic `<...>` after the name broke
        // the `(?=[\],]|$)` anchor), producing zero edges.
        // issue #293 round-15 補足: `[MyAudit<int>]` や複数行の `[\n MyAttr<int>\n]`
        // のようなジェネリック引数なし属性も `attribute` として取り込まれ、
        // suffix alias を経由して実属性クラスへの依存エッジに正規化される
        // こと。正規表現の拡張前は両 regex とも拾えず、エッジが 0 件だった。
        InsertIndexedFile("src/MyAuditAttribute.cs", "csharp",
            """
            using System;

            public sealed class MyAuditAttribute<T> : Attribute
            {
            }
            """);
        InsertIndexedFile("src/MyAttrAttribute.cs", "csharp",
            """
            using System;

            public sealed class MyAttrAttribute<T> : Attribute
            {
            }
            """);
        InsertIndexedFile("src/Svc.cs", "csharp",
            """
            [MyAudit<int>]
            [
                MyAttr<int>
            ]
            public class Svc
            {
            }
            """);

        var dependencies = _reader.GetFileDependencies(limit: 20, lang: "csharp");

        Assert.Contains(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/MyAuditAttribute.cs");
        Assert.Contains(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/MyAttrAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharpGenericNoArgAttribute_AssemblyTarget_IsIndexed()
    {
        // issue #293 round-15 follow-up: `[assembly: MyAttr<string>]` — assembly
        // targeted generic no-arg attribute must also reach the attribute class.
        // issue #293 round-15 補足: `[assembly: MyAttr<string>]` のような
        // assembly targeted ジェネリック引数なし属性も同様にインデックスされ、
        // attribute クラスに解決されること。
        InsertIndexedFile("src/MyAttrAttribute.cs", "csharp",
            """
            using System;

            public sealed class MyAttrAttribute<T> : Attribute
            {
            }
            """);
        InsertIndexedFile("src/AssemblyInfo.cs", "csharp",
            """
            [assembly: MyAttr<string>]
            """);

        var dependencies = _reader.GetFileDependencies(limit: 20, lang: "csharp");

        Assert.Contains(dependencies, d => d.SourcePath == "src/AssemblyInfo.cs" && d.TargetPath == "src/MyAttrAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharpAttributeRawDoesNotLeakToBareNameClass_WhenSuffixTargetExists()
    {
        // issue #293 follow-up: `[MyAudit]` in C# is stored as symbol_name='MyAudit'.
        // When both `class MyAudit` (plain class) and `class MyAuditAttribute`
        // (the real attribute target) exist, the metadata edge must resolve only
        // to `MyAuditAttribute` via the synthetic suffix alias. Keeping the raw
        // bare-name edge would over-report: `[MyAudit]` would falsely depend on
        // the unrelated plain `class MyAudit` file.
        // issue #293 補足: C# の `[MyAudit]` は symbol_name='MyAudit' で保存される。
        // `class MyAudit` (plain) と `class MyAuditAttribute` (本物の attribute)
        // が両方あるとき、metadata エッジは synthetic suffix alias 経由で
        // `MyAuditAttribute` だけに解決されるべき。raw の bare-name エッジを
        // 残すと、`[MyAudit]` が無関係な plain `class MyAudit` のファイルにも
        // 誤って依存してしまう。
        InsertIndexedFile("src/MyAuditAttribute.cs", "csharp",
            """
            using System;

            [AttributeUsage(AttributeTargets.Class)]
            public sealed class MyAuditAttribute : Attribute
            {
            }
            """);
        InsertIndexedFile("src/PlainMyAudit.cs", "csharp",
            """
            public class MyAudit
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

        var dependencies = _reader.GetFileDependencies(limit: 10, lang: "csharp");

        // Only MyAuditAttribute.cs should be a dependency target for Svc.cs;
        // PlainMyAudit.cs must not appear.
        // Svc.cs の依存先は MyAuditAttribute.cs のみで、PlainMyAudit.cs は
        // 出現してはならない。
        Assert.Contains(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/MyAuditAttribute.cs");
        Assert.DoesNotContain(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/PlainMyAudit.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharpAttributeDoesNotLeakToSameNameMethodOrProperty()
    {
        // issue #293 follow-up: `[MyAuditAttribute]` (fully qualified) must only
        // match a class-like attribute target. A method / property named
        // `MyAuditAttribute` in an unrelated file must never show up as a deps
        // edge from the metadata reference. Non-metadata call-graph edges keep
        // their previous behavior (they can still resolve to any symbol kind).
        // issue #293 補足: `[MyAuditAttribute]` (完全形) は class 系の attribute
        // target にしか一致してはならない。別ファイルの同名メソッド/プロパティ
        // `MyAuditAttribute` が metadata 参照の deps エッジに現れてはいけない。
        // 非 metadata の call-graph エッジは従来どおり任意の kind に解決できる。
        InsertIndexedFile("src/MyAuditAttribute.cs", "csharp",
            """
            using System;

            [AttributeUsage(AttributeTargets.Class)]
            public sealed class MyAuditAttribute : Attribute
            {
            }
            """);
        InsertIndexedFile("src/Helpers.cs", "csharp",
            """
            public class Helpers
            {
                public void MyAuditAttribute()
                {
                }
            }
            """);
        InsertIndexedFile("src/Svc.cs", "csharp",
            """
            [MyAuditAttribute]
            public class Svc
            {
            }
            """);

        var dependencies = _reader.GetFileDependencies(limit: 10, lang: "csharp");

        Assert.DoesNotContain(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/Helpers.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharpAttributeAmbiguityCountsSameFileDuplicateClassDefinitions()
    {
        // issue #293 follow-up: when a single source file defines TWO same-named
        // class-like attribute targets under different namespaces (idiomatic C#
        // with multiple `namespace { ... }` blocks in one .cs file), the metadata
        // edge must be dropped as ambiguous just like the multi-file case. A
        // path-level count (COUNT DISTINCT target_path) would see `count = 1`
        // because both definitions live in the same file, so the previous
        // target_ambiguity CTE falsely treated the target as unambiguous. The
        // rewritten CTE joins back through files + symbols so it counts at
        // symbol-identity level and correctly sees `count = 2`.
        // issue #293 補足: 1 つの .cs ファイルに別名前空間で同名 class-like が 2 つ
        // 定義されている場合 (C# でよくある `namespace { ... }` 複数ブロック形式)
        // でも、複数ファイルのときと同様に metadata edge は ambiguous として落とす
        // 必要がある。path 単位 (COUNT DISTINCT target_path) だと両方が同じ file に
        // あるため count=1 となり、従来の target_ambiguity では誤って一意扱いされた。
        // 書き直した CTE は files + symbols に JOIN し直すため、symbol identity 単位
        // で count=2 を正しく検出する。
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

        var dependencies = _reader.GetFileDependencies(limit: 10, lang: "csharp");

        // Even though both MyAuditAttribute definitions live in the same file, the
        // metadata reference is still ambiguous and must not produce a deps edge.
        // 同じファイル内にある 2 つの MyAuditAttribute 定義でも metadata 参照は
        // 曖昧扱いのため、deps edge を出してはならない。
        Assert.DoesNotContain(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/MyAuditAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharpAttributeDoesNotFanOutWhenMultipleSameNameAttributeClasses()
    {
        // issue #293 follow-up: if multiple same-named attribute classes exist
        // (e.g. two `MyAuditAttribute` classes in separate namespaces/files),
        // a metadata reference `[MyAudit]` must not fan out to BOTH files. We
        // cannot statically resolve which one the C# compiler picks without
        // namespace / using analysis, so we drop the ambiguous metadata edge
        // and let `impact` / `references` surface both candidates to the user.
        // issue #293 補足: 同名 attribute クラスが複数ある場合 (例: 別名前空間/別
        // ファイルに 2 つの `MyAuditAttribute` がある場合)、metadata 参照
        // `[MyAudit]` を両方に fan-out させない。cdidx は namespace / using を
        // 解析しないため正しい解決ができず、あいまいな metadata エッジは落として
        // 両候補は `impact` / `references` 経由でユーザーに示す。
        InsertIndexedFile("src/A/MyAuditAttribute.cs", "csharp",
            """
            using System;

            namespace A
            {
                [AttributeUsage(AttributeTargets.Class)]
                public sealed class MyAuditAttribute : Attribute
                {
                }
            }
            """);
        InsertIndexedFile("src/B/MyAuditAttribute.cs", "csharp",
            """
            using System;

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

        var dependencies = _reader.GetFileDependencies(limit: 10, lang: "csharp");

        // Neither fan-out edge should exist; the metadata reference is ambiguous.
        // あいまいな metadata 参照はどちらの fan-out エッジも出してはならない。
        Assert.DoesNotContain(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/A/MyAuditAttribute.cs");
        Assert.DoesNotContain(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/B/MyAuditAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharpMetadataTargetResolverExcludesNonAttributeImpostor()
    {
        // issue #435: a non-attribute class that happens to share the suffix-convention
        // name (e.g. `class FooAttribute : BaseService`) must not fake ambiguity against
        // a real `class FooAttribute : Attribute` elsewhere. Before the persisted
        // `is_metadata_target` resolver, the `signature LIKE '%: %'` heuristic counted
        // both as plausible candidates and the deps edge was silently dropped. With the
        // resolver stamped, only the real attribute target is counted, so the edge from
        // `[Foo]` consumers reaches the real attribute file.
        // issue #435: `class FooAttribute : BaseService` のような suffix 規約の名前を
        // 偶然持つ非 attribute class が、別ファイルの本物 `class FooAttribute : Attribute`
        // との ambiguity を偽装してはならない。永続化された is_metadata_target resolver
        // 以前は `signature LIKE '%: %'` ヒューリスティックで両方を候補に数えてしまい、
        // deps エッジが暗黙に落ちていた。resolver stamp 後は本物の attribute target だけが
        // 候補となり、`[Foo]` 利用側からのエッジが本物の attribute ファイルに届く。
        InsertIndexedFile("src/RealFooAttribute.cs", "csharp",
            """
            using System;

            [AttributeUsage(AttributeTargets.Class)]
            public sealed class FooAttribute : Attribute
            {
            }
            """);
        InsertIndexedFile("src/ImpostorFooAttribute.cs", "csharp",
            """
            public class FooAttribute : BaseService
            {
            }
            """);
        InsertIndexedFile("src/Svc.cs", "csharp",
            """
            [Foo]
            public class Svc
            {
            }
            """);

        _writer.ResolveCSharpMetadataTargets();
        _writer.MarkMetadataTargetReady("csharp");
        var resolverReader = new DbReader(_db.Connection);

        var dependencies = resolverReader.GetFileDependencies(limit: 10, lang: "csharp");

        Assert.Contains(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/RealFooAttribute.cs");
        Assert.DoesNotContain(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/ImpostorFooAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharpMetadataTargetResolverKeepsQualifiedAttributeEdgeWithSameNameNonAttributeSibling()
    {
        // issue #443: a fully-qualified metadata reference like `[A.Foo]` must still
        // resolve to the real `A.FooAttribute` even when a sibling namespace contains
        // a same-named non-Attribute impostor. The ambiguity guard must not let the
        // impostor suppress the legitimate deps edge.
        // issue #443: `[A.Foo]` のような fully-qualified metadata 参照は、別 namespace に
        // 同名の non-Attribute impostor があっても本物の `A.FooAttribute` に解決される必要がある。
        // impostor を理由に legitimate な deps edge を消してはならない。
        InsertIndexedFile("src/A/FooAttribute.cs", "csharp",
            """
            namespace A;

            public sealed class FooAttribute : System.Attribute
            {
            }
            """);
        InsertIndexedFile("src/B/FooAttribute.cs", "csharp",
            """
            namespace B;

            public class BaseService
            {
            }

            public sealed class FooAttribute : BaseService
            {
            }
            """);
        InsertIndexedFile("src/Svc.cs", "csharp",
            """
            namespace A;

            [A.Foo]
            public class Svc
            {
            }
            """);

        _writer.ResolveCSharpMetadataTargets();
        _writer.MarkMetadataTargetReady("csharp");
        var resolverReader = new DbReader(_db.Connection);

        var dependencies = resolverReader.GetFileDependencies(limit: 10, lang: "csharp");

        Assert.Contains(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/A/FooAttribute.cs");
        Assert.DoesNotContain(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/B/FooAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharpMetadataTargetResolverHandlesTransitiveAttributeDerivation()
    {
        // issue #435: derivation can be transitive — `class FooAttribute : BaseAttr`
        // where `class BaseAttr : Attribute`. The resolver's fixed-point iteration
        // must mark FooAttribute as a metadata target. Otherwise the same-name
        // impostor would re-introduce ambiguity and the metadata edge would drop.
        // issue #435: 派生は推移的になり得る — `class FooAttribute : BaseAttr` で
        // `class BaseAttr : Attribute` の場合、resolver の fixed-point iteration が
        // FooAttribute を metadata target として印付ける必要がある。さもなければ
        // 同名 impostor が ambiguity を再導入し、metadata エッジが落ちてしまう。
        InsertIndexedFile("src/BaseAttr.cs", "csharp",
            """
            using System;

            public abstract class BaseAttr : Attribute
            {
            }
            """);
        InsertIndexedFile("src/RealFooAttribute.cs", "csharp",
            """
            public sealed class FooAttribute : BaseAttr
            {
            }
            """);
        InsertIndexedFile("src/ImpostorFooAttribute.cs", "csharp",
            """
            public class FooAttribute : BaseService
            {
            }
            """);
        InsertIndexedFile("src/Svc.cs", "csharp",
            """
            [Foo]
            public class Svc
            {
            }
            """);

        _writer.ResolveCSharpMetadataTargets();
        _writer.MarkMetadataTargetReady("csharp");
        var resolverReader = new DbReader(_db.Connection);

        var dependencies = resolverReader.GetFileDependencies(limit: 10, lang: "csharp");

        Assert.Contains(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/RealFooAttribute.cs");
        Assert.DoesNotContain(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/ImpostorFooAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharpMetadataTargetResolverPreservesAmbiguityWhenMultipleRealAttributes()
    {
        // issue #435 invariant: the resolver fix narrows the candidate set, but it
        // must NOT mask genuine ambiguity. When two REAL attribute classes share the
        // same name (both transitively derive from Attribute), the metadata edge
        // must still be dropped — namespace/using disambiguation is out of scope.
        // issue #435 invariant: resolver は候補集合を絞るが、本物の曖昧さを隠してはならない。
        // 2 つの本物 attribute class が同名で両方 Attribute 由来なら、従来どおり
        // metadata エッジは落ちる必要がある（namespace / using 解析はスコープ外）。
        InsertIndexedFile("src/A/FooAttribute.cs", "csharp",
            """
            using System;

            namespace A
            {
                public sealed class FooAttribute : Attribute
                {
                }
            }
            """);
        InsertIndexedFile("src/B/FooAttribute.cs", "csharp",
            """
            using System;

            namespace B
            {
                public sealed class FooAttribute : Attribute
                {
                }
            }
            """);
        InsertIndexedFile("src/Svc.cs", "csharp",
            """
            [Foo]
            public class Svc
            {
            }
            """);

        _writer.ResolveCSharpMetadataTargets();
        _writer.MarkMetadataTargetReady("csharp");
        var resolverReader = new DbReader(_db.Connection);

        var dependencies = resolverReader.GetFileDependencies(limit: 10, lang: "csharp");

        Assert.DoesNotContain(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/A/FooAttribute.cs");
        Assert.DoesNotContain(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/B/FooAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharpMetadataTargetResolverDistinguishesQualifiedBases()
    {
        // issue #435 codex review iter 1: a qualified base like `: B.BaseAttr` must
        // resolve specifically against the B.BaseAttr class, not leak into an unrelated
        // A.BaseAttr that happens to be a metadata target. Before the fix, the resolver
        // collapsed the base to its simple head (`BaseAttr`) and treated "any same-name
        // class is target" as "this qualified reference is target", producing a false
        // positive metadata target and therefore a spurious deps edge for `[Impostor]`.
        // issue #435 codex review iter 1: `: B.BaseAttr` のような修飾名基底は、無関係な
        // `A.BaseAttr`（metadata target）に誤解決してはならない。修正前は simple-name
        // `BaseAttr` に潰して「どれかが target なら当該修飾参照も target」化していた。
        InsertIndexedFile("src/A/BaseAttr.cs", "csharp",
            """
            using System;

            namespace A
            {
                public abstract class BaseAttr : Attribute
                {
                }
            }
            """);
        InsertIndexedFile("src/B/BaseAttr.cs", "csharp",
            """
            namespace B
            {
                public class BaseAttr : BaseService
                {
                }
            }
            """);
        InsertIndexedFile("src/ImpostorFooAttribute.cs", "csharp",
            """
            namespace B
            {
                public class ImpostorFooAttribute : B.BaseAttr
                {
                }
            }
            """);
        InsertIndexedFile("src/Svc.cs", "csharp",
            """
            [ImpostorFoo]
            public class Svc
            {
            }
            """);

        _writer.ResolveCSharpMetadataTargets();
        _writer.MarkMetadataTargetReady("csharp");
        var resolverReader = new DbReader(_db.Connection);

        var dependencies = resolverReader.GetFileDependencies(limit: 10, lang: "csharp");

        Assert.DoesNotContain(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/ImpostorFooAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharpMetadataTargetResolverHonorsQualifiedExternalSuffixFallback()
    {
        // issue #435 codex review iter 1: when a class derives from a qualified
        // external base (`: ThirdParty.ValidationAttribute`), the resolver must still
        // apply the BCL suffix fallback even if an unrelated in-repo class happens to
        // share the same simple name (`ValidationAttribute`) and is NOT a metadata
        // target. Pre-fix, the resolver collapsed to the simple name, found the
        // in-repo non-target, and suppressed the suffix fallback — silently dropping
        // the metadata edge.
        // issue #435 codex review iter 1: `: ThirdParty.ValidationAttribute` のように
        // 外部の修飾基底を継承するとき、repo 内に同名 non-target class がいても
        // suffix 規約 fallback を殺してはならない。修正前は単純名に潰して in-repo
        // non-target にぶつかり suffix fallback を潰していた。
        InsertIndexedFile("src/InRepo/ValidationAttribute.cs", "csharp",
            """
            namespace InRepo
            {
                public class ValidationAttribute : BaseService
                {
                }
            }
            """);
        InsertIndexedFile("src/MyValidatorAttribute.cs", "csharp",
            """
            public class MyValidatorAttribute : ThirdParty.ValidationAttribute
            {
            }
            """);
        InsertIndexedFile("src/Svc.cs", "csharp",
            """
            [MyValidator]
            public class Svc
            {
            }
            """);

        _writer.ResolveCSharpMetadataTargets();
        _writer.MarkMetadataTargetReady("csharp");
        var resolverReader = new DbReader(_db.Connection);

        var dependencies = resolverReader.GetFileDependencies(limit: 10, lang: "csharp");

        Assert.Contains(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/MyValidatorAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharpMetadataTargetResolverHandlesPartialClassBase()
    {
        // issue #435 codex review iter 2: legal C# `partial class` can split a single
        // logical type across multiple declaration sites, each producing its own symbol
        // row. Only one of the partial declarations carries the real base list
        // (`: Attribute`). The qualified-base index must accumulate ALL rows sharing the
        // same FQN so the fixed-point lookup can still find the target-bearing partial,
        // regardless of which file was indexed first. Before the iter-2 fix, the index
        // used `Dictionary<string, long>` with `TryAdd`, so whichever partial row was
        // inserted first won: when the base-less partial was inserted first, the
        // qualified reference from `FooAttribute : B.BaseAttr` resolved only to that
        // base-less row, never iterating to the partial that carries `: Attribute`,
        // and the metadata edge was silently dropped in a file-order dependent way.
        // issue #435 codex review iter 2: `partial class` は 1 つの論理型が複数行に
        // 分かれる。修飾名索引が `Dictionary<string, long>` + TryAdd だった旧実装では、
        // 先に insert された partial 行しか拾われず、`: Attribute` を持つ真の target
        // partial が別ファイルにあるとファイル順で metadata edge が落ちていた。List で
        // 候補集合を保持する修正により、fixed-point 反復でどれかが target になれば
        // qualified 参照も正しく解決される。
        InsertIndexedFile("src/B/BaseAttr.Core.cs", "csharp",
            """
            namespace B
            {
                public partial class BaseAttr
                {
                }
            }
            """);
        InsertIndexedFile("src/B/BaseAttr.Marker.cs", "csharp",
            """
            using System;

            namespace B
            {
                public partial class BaseAttr : Attribute
                {
                }
            }
            """);
        InsertIndexedFile("src/FooAttribute.cs", "csharp",
            """
            public class FooAttribute : B.BaseAttr
            {
            }
            """);
        InsertIndexedFile("src/Svc.cs", "csharp",
            """
            [Foo]
            public class Svc
            {
            }
            """);

        _writer.ResolveCSharpMetadataTargets();
        _writer.MarkMetadataTargetReady("csharp");
        var resolverReader = new DbReader(_db.Connection);

        var dependencies = resolverReader.GetFileDependencies(limit: 10, lang: "csharp");

        Assert.Contains(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/FooAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharpMetadataTargetResolverPrefersSameNamespaceBaseOverGlobalImpostor()
    {
        // issue #435 codex review iter 4: unqualified base names must resolve through
        // the deriving class's own namespace / nesting chain — NOT through a global
        // simple-name bucket. Before the iter-4 fix, `namespace B { class FooAttribute
        // : BaseAttr {} }` could be falsely promoted to `is_metadata_target=1` solely
        // because an unrelated `namespace A { class BaseAttr : Attribute {} }` existed
        // elsewhere in the repo, even though B's own `BaseAttr : BaseService` was the
        // actually reachable base for the unqualified reference. The result was a false
        // `deps` / `impact` edge from `[Foo] class Svc` to `B.FooAttribute`. The fix
        // indexes classes under `(enclosing scope, simple name)` and walks the deriving
        // row's scope chain inside → outside, consulting only the first scope level
        // that has a same-name row. If no scope level matches, the resolver falls back
        // to the BCL `Attribute`-suffix heuristic for external bases — the global
        // simple-name bucket is no longer consulted.
        // issue #435 codex review iter 4: 非修飾基底は deriving の名前空間 /
        // 入れ子チェーンのみで解決する。グローバル単純名索引に落とすと、別名前空間に
        // 同名の本物 Attribute 派生が居るだけで非 Attribute 派生 class が偽の
        // `is_metadata_target=1` に昇格し、`deps` / `impact` に偽エッジが残る。
        InsertIndexedFile("src/A/BaseAttr.cs", "csharp",
            """
            using System;

            namespace A
            {
                public class BaseAttr : Attribute
                {
                }
            }
            """);
        InsertIndexedFile("src/B/BaseAttr.cs", "csharp",
            """
            namespace B
            {
                public class BaseService
                {
                }

                public class BaseAttr : BaseService
                {
                }
            }
            """);
        InsertIndexedFile("src/B/FooAttribute.cs", "csharp",
            """
            namespace B
            {
                public class FooAttribute : BaseAttr
                {
                }
            }
            """);
        InsertIndexedFile("src/Svc.cs", "csharp",
            """
            namespace B
            {
                [Foo]
                public class Svc
                {
                }
            }
            """);

        _writer.ResolveCSharpMetadataTargets();
        _writer.MarkMetadataTargetReady("csharp");
        var resolverReader = new DbReader(_db.Connection);

        var dependencies = resolverReader.GetFileDependencies(limit: 10, lang: "csharp");

        Assert.DoesNotContain(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/B/FooAttribute.cs");

        // Column-level invariant: the scope-aware resolver must classify each row
        // against its own scope chain. A.BaseAttr is a real Attribute derivative;
        // B.BaseAttr (same simple name, different namespace) derives from an unrelated
        // BaseService and must stay non-target; B.FooAttribute's unqualified `BaseAttr`
        // must resolve to B.BaseAttr (not A.BaseAttr), so it also stays non-target.
        // 列レベル不変条件: scope-aware resolver は各行を自身のスコープチェーンで判定する。
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = @"
            SELECT f.path, s.is_metadata_target
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE s.kind = 'class' AND s.name IN ('BaseAttr', 'FooAttribute')
            ORDER BY f.path, s.name";
        var rows = new List<(string Path, long Flag)>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
                rows.Add((reader.GetString(0), reader.GetInt64(1)));
        }
        Assert.Contains(rows, r => r.Path == "src/A/BaseAttr.cs" && r.Flag == 1);
        Assert.Contains(rows, r => r.Path == "src/B/BaseAttr.cs" && r.Flag == 0);
        Assert.Contains(rows, r => r.Path == "src/B/FooAttribute.cs" && r.Flag == 0);
    }

    [Fact]
    public void GetFileDependencies_CSharpMetadataTargetResolverHandlesImportedNamespaceBase()
    {
        // issue #435 codex review iter 5: the iter-4 fix made unqualified base resolution
        // strictly same-scope only. That regressed the common C# pattern
        // `using A; namespace B { class FooAttribute : BaseAttr {} }` where `A.BaseAttr :
        // Attribute` is indexed in a sibling file. The iter-5 fix threads the deriving
        // file's `using` directives into the resolver so, after a same-scope lookup miss,
        // `BaseAttr` is probed as `A.BaseAttr` via `using A;` before falling through to
        // the BCL `Attribute`-suffix convention.
        // issue #435 codex review iter 5: iter 4 の strict same-scope 限定が
        // `using A; class FooAttribute : BaseAttr` の一般的 C# パターンで false-negative を
        // 招いた。iter 5 で `using` 指令を resolver に通し、same-scope 解決失敗後に
        // `using A;` 経由で `A.BaseAttr` を qualified 索引に引き当てる。
        InsertIndexedFile("src/A/BaseAttr.cs", "csharp",
            """
            using System;

            namespace A
            {
                public class BaseAttr : Attribute
                {
                }
            }
            """);
        InsertIndexedFile("src/B/FooAttribute.cs", "csharp",
            """
            using A;

            namespace B
            {
                public class FooAttribute : BaseAttr
                {
                }
            }
            """);
        InsertIndexedFile("src/B/Svc.cs", "csharp",
            """
            namespace B
            {
                [Foo]
                public class Svc
                {
                }
            }
            """);

        _writer.ResolveCSharpMetadataTargets();
        _writer.MarkMetadataTargetReady("csharp");
        var resolverReader = new DbReader(_db.Connection);

        // Column-level invariant: FooAttribute must resolve through `using A;` to
        // `A.BaseAttr : Attribute` even though B has no same-scope `BaseAttr` of its own.
        // 列レベル不変条件: B 側に `BaseAttr` が無くても `using A;` 経由で解決されること。
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = @"
            SELECT f.path, s.is_metadata_target
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE s.kind = 'class' AND s.name = 'FooAttribute'";
        long flag;
        using (var reader = cmd.ExecuteReader())
        {
            Assert.True(reader.Read(), "FooAttribute row must exist");
            flag = reader.GetInt64(1);
        }
        Assert.Equal(1L, flag);

        var dependencies = resolverReader.GetFileDependencies(limit: 10, lang: "csharp");
        Assert.Contains(dependencies, d => d.SourcePath == "src/B/Svc.cs" && d.TargetPath == "src/B/FooAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharpMetadataTargetResolverHandlesUsingAliasBase()
    {
        // issue #435 codex review iter 5: the alias form of the same regression —
        // `using AliasAttr = A.BaseAttr; class FooAttribute : AliasAttr {}`. Before iter 5,
        // the resolver had no knowledge of alias imports and left FooAttribute at
        // `is_metadata_target=0`, dropping the `[Foo]` → FooAttribute metadata edge.
        // issue #435 codex review iter 5: alias 形式の同一 regression。
        // `using AliasAttr = A.BaseAttr;` の alias 索引を resolver に取り込み、
        // `class FooAttribute : AliasAttr` が qualified 索引上で `A.BaseAttr` に解決される。
        InsertIndexedFile("src/A/BaseAttr.cs", "csharp",
            """
            using System;

            namespace A
            {
                public class BaseAttr : Attribute
                {
                }
            }
            """);
        InsertIndexedFile("src/B/FooAttribute.cs", "csharp",
            """
            using AliasAttr = A.BaseAttr;

            namespace B
            {
                public class FooAttribute : AliasAttr
                {
                }
            }
            """);
        InsertIndexedFile("src/B/Svc.cs", "csharp",
            """
            namespace B
            {
                [Foo]
                public class Svc
                {
                }
            }
            """);

        _writer.ResolveCSharpMetadataTargets();
        _writer.MarkMetadataTargetReady("csharp");
        var resolverReader = new DbReader(_db.Connection);

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = @"
            SELECT s.is_metadata_target
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE f.path = 'src/B/FooAttribute.cs' AND s.kind = 'class' AND s.name = 'FooAttribute'";
        var flag = cmd.ExecuteScalar();
        Assert.Equal(1L, Convert.ToInt64(flag));

        var dependencies = resolverReader.GetFileDependencies(limit: 10, lang: "csharp");
        Assert.Contains(dependencies, d => d.SourcePath == "src/B/Svc.cs" && d.TargetPath == "src/B/FooAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharpMetadataTargetResolverHandlesVerbatimNamespaceImport()
    {
        // issue #435 codex review iter 6: C# verbatim identifiers (`@Foo`) are a source-level
        // escape for keywords; `using @Foo.@Bar;` is semantically identical to
        // `using Foo.Bar;`. Before iter 6 the resolver stored the raw `@Foo.@Bar` token in the
        // per-file import map and never matched the qualified index, leaving
        // `VerbatimImportAttribute : BaseAttr` as `is_metadata_target=0`.
        // issue #435 codex review iter 6: verbatim 識別子 `@Foo.@Bar` は非 verbatim 形と等価。
        // 修正前は import map に生の `@Foo.@Bar` が載り、qualified 索引に当たらず
        // `VerbatimImportAttribute : BaseAttr` が `is_metadata_target=0` のまま残っていた。
        InsertIndexedFile("src/Foo/Bar/BaseAttr.cs", "csharp",
            """
            using System;

            namespace Foo.Bar
            {
                public class BaseAttr : Attribute
                {
                }
            }
            """);
        InsertIndexedFile("src/V/VerbatimImportAttribute.cs", "csharp",
            """
            using @Foo.@Bar;

            namespace V
            {
                public class VerbatimImportAttribute : BaseAttr
                {
                }
            }
            """);
        InsertIndexedFile("src/V/Consumer.cs", "csharp",
            """
            namespace V
            {
                [VerbatimImport]
                public class Consumer
                {
                }
            }
            """);

        _writer.ResolveCSharpMetadataTargets();
        _writer.MarkMetadataTargetReady("csharp");
        var resolverReader = new DbReader(_db.Connection);

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = @"
            SELECT s.is_metadata_target
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE f.path = 'src/V/VerbatimImportAttribute.cs' AND s.kind = 'class' AND s.name = 'VerbatimImportAttribute'";
        var flag = cmd.ExecuteScalar();
        Assert.Equal(1L, Convert.ToInt64(flag));

        var dependencies = resolverReader.GetFileDependencies(limit: 10, lang: "csharp");
        Assert.Contains(dependencies, d => d.SourcePath == "src/V/Consumer.cs" && d.TargetPath == "src/V/VerbatimImportAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharpMetadataTargetResolverHandlesVerbatimAliasNameAndTarget()
    {
        // issue #435 codex review iter 6: verbatim on both sides of an alias import —
        // `using @AliasAttr = @Foo.@Bar.BaseAttr;` should parse, be captured as an import
        // row (the SymbolExtractor regex was too strict to accept the leading `@` on alias
        // names), and resolve identically to the non-verbatim spelling.
        // issue #435 codex review iter 6: alias 両辺の verbatim — alias 名にも target にも
        // `@` が付くケース。旧 SymbolExtractor regex は alias 名の `@` を受けず import 行
        // 自体が生成されなかったため、resolver に届く前に情報が欠落していた。
        InsertIndexedFile("src/Foo/Bar/BaseAttr.cs", "csharp",
            """
            using System;

            namespace Foo.Bar
            {
                public class BaseAttr : Attribute
                {
                }
            }
            """);
        InsertIndexedFile("src/V/VerbatimAliasAttribute.cs", "csharp",
            """
            using @AliasAttr = @Foo.@Bar.BaseAttr;

            namespace V
            {
                public class VerbatimAliasAttribute : AliasAttr
                {
                }
            }
            """);
        InsertIndexedFile("src/V/AliasConsumer.cs", "csharp",
            """
            namespace V
            {
                [VerbatimAlias]
                public class AliasConsumer
                {
                }
            }
            """);

        _writer.ResolveCSharpMetadataTargets();
        _writer.MarkMetadataTargetReady("csharp");
        var resolverReader = new DbReader(_db.Connection);

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = @"
            SELECT s.is_metadata_target
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE f.path = 'src/V/VerbatimAliasAttribute.cs' AND s.kind = 'class' AND s.name = 'VerbatimAliasAttribute'";
        var flag = cmd.ExecuteScalar();
        Assert.Equal(1L, Convert.ToInt64(flag));

        var dependencies = resolverReader.GetFileDependencies(limit: 10, lang: "csharp");
        Assert.Contains(dependencies, d => d.SourcePath == "src/V/AliasConsumer.cs" && d.TargetPath == "src/V/VerbatimAliasAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharpMetadataTargetResolverHandlesVerbatimBaseClassDeclaration()
    {
        // issue #435 codex review iter 7: the defining side uses a verbatim identifier in
        // the declaration itself (`public class @BaseAttr : Attribute`). Before iter 7 the
        // C# class-declaration regex only accepted `\w+` for the name capture, so this file
        // did not produce a class row at all. The deriving file's `class Verbatim : BaseAttr`
        // then had no in-repo target to resolve against and stayed `is_metadata_target=0`.
        // issue #435 codex review iter 7: 宣言側自体が verbatim（`public class @BaseAttr :
        // Attribute`）のケース。iter 7 以前の C# class 宣言 regex は name キャプチャが `\w+`
        // のみで、この file は class 行をまったく生成しなかった。その結果 `class Verbatim :
        // BaseAttr` 側も in-repo target を持てず `is_metadata_target=0` のままだった。
        InsertIndexedFile("src/V/VerbatimBase.cs", "csharp",
            """
            using System;

            namespace Foo.Bar
            {
                public class @BaseAttr : Attribute
                {
                }
            }
            """);
        InsertIndexedFile("src/V/VerbatimBaseTypeAttribute.cs", "csharp",
            """
            using Foo.Bar;

            namespace V
            {
                public class VerbatimBaseTypeAttribute : BaseAttr
                {
                }
            }
            """);
        InsertIndexedFile("src/V/VerbatimBaseConsumer.cs", "csharp",
            """
            namespace V
            {
                [VerbatimBaseType]
                public class VerbatimBaseConsumer
                {
                }
            }
            """);

        _writer.ResolveCSharpMetadataTargets();
        _writer.MarkMetadataTargetReady("csharp");
        var resolverReader = new DbReader(_db.Connection);

        using (var defnCmd = _db.Connection.CreateCommand())
        {
            // Verify the verbatim class declaration is persisted with its canonical name.
            // 宣言側 verbatim が canonical 名で永続化されていることを確認。
            defnCmd.CommandText = @"
                SELECT s.name
                FROM symbols s
                JOIN files f ON f.id = s.file_id
                WHERE f.path = 'src/V/VerbatimBase.cs' AND s.kind = 'class'";
            var defnName = defnCmd.ExecuteScalar() as string;
            Assert.Equal("BaseAttr", defnName);
        }

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = @"
            SELECT s.is_metadata_target
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE f.path = 'src/V/VerbatimBaseTypeAttribute.cs' AND s.kind = 'class' AND s.name = 'VerbatimBaseTypeAttribute'";
        var flag = cmd.ExecuteScalar();
        Assert.Equal(1L, Convert.ToInt64(flag));

        var dependencies = resolverReader.GetFileDependencies(limit: 10, lang: "csharp");
        Assert.Contains(dependencies, d => d.SourcePath == "src/V/VerbatimBaseConsumer.cs" && d.TargetPath == "src/V/VerbatimBaseTypeAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharpMetadataTargetResolverHandlesGlobalQualifiedVerbatimBase()
    {
        // issue #435 codex review iter 7: the consumer writes its base as
        // `global::@Foo.@Bar.BaseAttr`. iter 6's `StripCSharpVerbatimPrefixes` only handled
        // `.` boundaries, so after splitting into segments the first segment
        // `global::@Foo` kept its `@`, the later `global::` trim produced
        // `@Foo.Bar.BaseAttr`, and the qualified-index lookup missed the canonical key
        // `Foo.Bar.BaseAttr`. iter 7 teaches the helper about the `::` boundary.
        // issue #435 codex review iter 7: consumer が基底を `global::@Foo.@Bar.BaseAttr`
        // と書くケース。iter 6 の `StripCSharpVerbatimPrefixes` は `.` 境界しか扱わず、
        // 最初のセグメント `global::@Foo` の `@` が残り、後段の `global::` 剥がしを経て
        // `@Foo.Bar.BaseAttr` になって canonical なキー `Foo.Bar.BaseAttr` と一致しなかった。
        // iter 7 で helper が `::` 境界も処理するようになった。
        InsertIndexedFile("src/Foo/Bar/BaseAttr.cs", "csharp",
            """
            using System;

            namespace Foo.Bar
            {
                public class BaseAttr : Attribute
                {
                }
            }
            """);
        InsertIndexedFile("src/Q/QualifiedVerbatimNamespaceAttribute.cs", "csharp",
            """
            namespace Q
            {
                public class QualifiedVerbatimNamespaceAttribute : global::@Foo.@Bar.BaseAttr
                {
                }
            }
            """);
        InsertIndexedFile("src/Q/QualifiedVerbatimConsumer.cs", "csharp",
            """
            namespace Q
            {
                [QualifiedVerbatimNamespace]
                public class QualifiedVerbatimConsumer
                {
                }
            }
            """);

        _writer.ResolveCSharpMetadataTargets();
        _writer.MarkMetadataTargetReady("csharp");
        var resolverReader = new DbReader(_db.Connection);

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = @"
            SELECT s.is_metadata_target
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE f.path = 'src/Q/QualifiedVerbatimNamespaceAttribute.cs' AND s.kind = 'class' AND s.name = 'QualifiedVerbatimNamespaceAttribute'";
        var flag = cmd.ExecuteScalar();
        Assert.Equal(1L, Convert.ToInt64(flag));

        var dependencies = resolverReader.GetFileDependencies(limit: 10, lang: "csharp");
        Assert.Contains(dependencies, d => d.SourcePath == "src/Q/QualifiedVerbatimConsumer.cs" && d.TargetPath == "src/Q/QualifiedVerbatimNamespaceAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharpMetadataTargetResolverHandlesAliasQualifiedBase()
    {
        // issue #435 codex review iter 8: `using Alias = A;` followed by
        // `class FooAttribute : Alias.MetaBase` where `A.MetaBase : Attribute`.
        // Before the fix, the resolver entered the qualified branch (the base
        // contains `.`), looked up `Alias.MetaBase` in the qualified index —
        // which stores the real FQN `A.MetaBase` — found nothing, and fell
        // through to the BCL `Attribute`-suffix heuristic. `head = "MetaBase"`
        // does not end with `Attribute`, so the resolver returned false and
        // the metadata edge from `[Foo]` consumers was silently dropped even
        // though `MetaBase` is a real in-repo attribute.
        // issue #435 codex review iter 8: `using Alias = A;` の下で
        // `class FooAttribute : Alias.MetaBase` のパターン。修正前は resolver が
        // qualified 分岐に入り、qualified 索引を `Alias.MetaBase` で引いて miss し、
        // BCL サフィックス規約にフォールバックしたが `MetaBase` は `Attribute` で
        // 終わらないため false を返し、`[Foo]` consumer の metadata edge が黙って
        // 落ちていた。
        InsertIndexedFile("src/A/MetaBase.cs", "csharp",
            """
            namespace A
            {
                public class MetaBase : System.Attribute
                {
                }
            }
            """);
        InsertIndexedFile("src/B/FooAttribute.cs", "csharp",
            """
            using Alias = A;
            namespace B
            {
                public class FooAttribute : Alias.MetaBase
                {
                }
            }
            """);
        InsertIndexedFile("src/B/Svc.cs", "csharp",
            """
            namespace B
            {
                [Foo]
                public class Svc
                {
                }
            }
            """);

        _writer.ResolveCSharpMetadataTargets();
        _writer.MarkMetadataTargetReady("csharp");
        var resolverReader = new DbReader(_db.Connection);

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = @"
            SELECT s.is_metadata_target
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE f.path = 'src/B/FooAttribute.cs' AND s.kind = 'class' AND s.name = 'FooAttribute'";
        var flag = cmd.ExecuteScalar();
        Assert.Equal(1L, Convert.ToInt64(flag));

        var dependencies = resolverReader.GetFileDependencies(limit: 10, lang: "csharp");
        Assert.Contains(dependencies, d => d.SourcePath == "src/B/Svc.cs" && d.TargetPath == "src/B/FooAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharpMetadataTargetResolverHandlesAliasNamespacePointingAtSystem()
    {
        // issue #435 codex review iter 8: `using Sys = System;` followed by
        // `class Foo : Sys.Attribute`. After alias expansion the base is
        // `System.Attribute`, which must trigger the direct-attribute rule
        // rather than fall through to the qualified-index lookup (which
        // would miss `System.Attribute` since System is external to the repo).
        // issue #435 codex review iter 8: `using Sys = System;` + `class Foo :
        // Sys.Attribute` は alias 展開後に `System.Attribute` となり、修飾索引を
        // 引く前に直接 Attribute 派生ルールで拾わなければならない。
        InsertIndexedFile("src/Svc.cs", "csharp",
            """
            using Sys = System;
            namespace Svc
            {
                public class FooAttribute : Sys.Attribute
                {
                }
            }
            """);
        InsertIndexedFile("src/Usage.cs", "csharp",
            """
            namespace Svc
            {
                [Foo]
                public class Usage
                {
                }
            }
            """);

        _writer.ResolveCSharpMetadataTargets();
        _writer.MarkMetadataTargetReady("csharp");
        var resolverReader = new DbReader(_db.Connection);

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = @"
            SELECT s.is_metadata_target
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE f.path = 'src/Svc.cs' AND s.kind = 'class' AND s.name = 'FooAttribute'";
        var flag = cmd.ExecuteScalar();
        Assert.Equal(1L, Convert.ToInt64(flag));

        var dependencies = resolverReader.GetFileDependencies(limit: 10, lang: "csharp");
        Assert.Contains(dependencies, d => d.SourcePath == "src/Usage.cs" && d.TargetPath == "src/Svc.cs");
    }

    [Fact]
    public void SearchSymbols_CSharpAmbiguousUsingExposureKeepsBothCandidatesIndividuallyAddressable_Issue1521()
    {
        // issue #1521: when two `using` directives both expose a same-named type
        // (e.g. `using FooNs; using BarNs;` both exposing `Holder`), DbReader's
        // base-type resolver iterated `GetActiveCSharpTypeNamespaces` — a HashSet
        // of active namespaces — and returned the first match via FirstOrDefault,
        // routing `class Derived : Holder` to whichever namespace bucket happened
        // to enumerate first. The fix detects the ambiguity and declines to
        // resolve rather than silently picking one. Both definitions remain
        // individually addressable by their fully qualified names, and the
        // deriving site stays reachable from a bare-name reference search.
        // issue #1521: 2 つの using directive が同名型を公開する場合
        // (`using FooNs; using BarNs;` の両方が `Holder` を露出)、DbReader の基底型
        // 解決は `GetActiveCSharpTypeNamespaces` (active namespaces の HashSet) を
        // 巡回して FirstOrDefault で最初の一致を返していたため、`class Derived :
        // Holder` がどちらに routing されるかは namespace の bucket 列挙順に依存
        // していた。本修正は曖昧性を検知して 1 つを silently 選ぶのではなく解決を
        // 棄権する。両定義は完全修飾名で個別に到達可能で、bare 名の references
        // 検索からも派生位置に到達できる。
        InsertIndexedFile("src/FooNs/Holder.cs", "csharp",
            """
            namespace FooNs
            {
                public class Holder
                {
                }
            }
            """);
        InsertIndexedFile("src/BarNs/Holder.cs", "csharp",
            """
            namespace BarNs
            {
                public class Holder
                {
                }
            }
            """);
        InsertIndexedFile("src/Use/Derived.cs", "csharp",
            """
            using FooNs;
            using BarNs;

            namespace UseNs
            {
                public class Derived : Holder
                {
                }
            }
            """);

        // Both Holder definitions are indexed and individually addressable by
        // their declaring file — neither is silently dropped during indexing.
        // 両 Holder 定義は宣言ファイル単位で個別に到達可能であり、indexing 時に
        // silently 落とされることはない。
        var holderSymbols = _reader.SearchSymbols("Holder", lang: "csharp", exact: true).ToList();
        Assert.Contains(holderSymbols, symbol => symbol.Path == "src/FooNs/Holder.cs" && symbol.Kind == "class");
        Assert.Contains(holderSymbols, symbol => symbol.Path == "src/BarNs/Holder.cs" && symbol.Kind == "class");

        // The bare `Holder` reference at the deriving site is recorded and
        // surfaces in references search regardless of dictionary enumeration
        // order; the resolver no longer silently picks one specific namespace.
        // 派生位置の bare `Holder` 参照は dictionary 列挙順に依らず references
        // 検索に現れる。resolver が特定 namespace を silently 選ぶことはない。
        var holderRefs = _reader.SearchReferences("Holder", lang: "csharp", exact: true).ToList();
        Assert.Contains(holderRefs, reference => reference.Path == "src/Use/Derived.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharpMetadataTargetResolverHandlesAliasColonColonQualifiedBase()
    {
        // issue #435 codex review iter 9: C# allows both `Alias.X` (member access)
        // and `Alias::X` (qualified-alias-member, §7.8) for a using alias that
        // names a namespace. Iter 8 only taught the qualified branch to split on
        // `.`, so `class FooAttribute : Alias::MetaBase` under `using Alias = A;`
        // skipped the alias expansion entirely (the helper's IndexOf('.') returned
        // -1 and bailed), fell through to the BCL suffix heuristic, and dropped
        // the `[Foo] -> FooAttribute` metadata edge even though `A.MetaBase :
        // Attribute` lives in the repo.
        // issue #435 codex review iter 9: C# では using alias が名前空間を指す場合、
        // `Alias.X` と `Alias::X` の両方が合法。iter 8 は `.` 区切りしか扱わなかった
        // ため `using Alias = A;` 配下の `class FooAttribute : Alias::MetaBase` は
        // alias 展開に入らず（helper の IndexOf('.') が -1 で即 return）、BCL サフィ
        // ックス規約に落ちて `[Foo] -> FooAttribute` edge が落ちていた。
        InsertIndexedFile("src/A/MetaBase.cs", "csharp",
            """
            namespace A
            {
                public class MetaBase : System.Attribute
                {
                }
            }
            """);
        InsertIndexedFile("src/B/FooAttribute.cs", "csharp",
            """
            using Alias = A;
            namespace B
            {
                public class FooAttribute : Alias::MetaBase
                {
                }
            }
            """);
        InsertIndexedFile("src/B/Svc.cs", "csharp",
            """
            namespace B
            {
                [Foo]
                public class Svc
                {
                }
            }
            """);

        _writer.ResolveCSharpMetadataTargets();
        _writer.MarkMetadataTargetReady("csharp");
        var resolverReader = new DbReader(_db.Connection);

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = @"
            SELECT s.is_metadata_target
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE f.path = 'src/B/FooAttribute.cs' AND s.kind = 'class' AND s.name = 'FooAttribute'";
        var flag = cmd.ExecuteScalar();
        Assert.Equal(1L, Convert.ToInt64(flag));

        var dependencies = resolverReader.GetFileDependencies(limit: 10, lang: "csharp");
        Assert.Contains(dependencies, d => d.SourcePath == "src/B/Svc.cs" && d.TargetPath == "src/B/FooAttribute.cs");
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
    public void GetFileDependencies_CSharpMetadataTargetReaderFallsBackToNameSuffixWhenColumnMissing()
    {
        // issue #435 codex review iter 1: reader branch (3) — when the entire
        // `is_metadata_target` column is absent (truly ancient legacy DB that the
        // current binary is opening read-only), the reader must degrade to the
        // `name LIKE '%Attribute'` fallback. Pre-fix, branch (2) only required the
        // `signature` column, so a column-missing DB still ran the signature
        // heuristic — contradicting the documented 3-way branch.
        // issue #435 codex review iter 1: reader branch (3) — `is_metadata_target` 列
        // 自体が無い古い legacy DB では命名規約のみに縮退するべき。修正前は branch (2)
        // が `signature` 列の有無だけで判定され、column 欠落 DB でも signature
        // ヒューリスティックに落ちて 3 way 分岐のドキュメントと食い違っていた。
        InsertIndexedFile("src/RealFooAttribute.cs", "csharp",
            """
            using System;

            [AttributeUsage(AttributeTargets.Class)]
            public sealed class FooAttribute : Attribute
            {
            }
            """);
        InsertIndexedFile("src/ImpostorFooAttribute.cs", "csharp",
            """
            public class FooAttribute : BaseService
            {
            }
            """);
        InsertIndexedFile("src/Svc.cs", "csharp",
            """
            [Foo]
            public class Svc
            {
            }
            """);

        // Drop the `is_metadata_target` column to simulate a read-only legacy DB that
        // the current binary cannot in-place migrate. SQLite supports DROP COLUMN
        // since 3.35 (we target 3.39+ via Microsoft.Data.Sqlite).
        // `is_metadata_target` 列を落として、in-place 移行できない古い read-only 相当の
        // DB を模擬する。DROP COLUMN は SQLite 3.35 以降でサポート。
        using (var drop = _db.Connection.CreateCommand())
        {
            drop.CommandText = "ALTER TABLE symbols DROP COLUMN is_metadata_target";
            drop.ExecuteNonQuery();
        }

        var legacyReader = new DbReader(_db.Connection, isReadOnly: true);
        var dependencies = legacyReader.GetFileDependencies(limit: 10, lang: "csharp");

        // Both FooAttribute files match `name LIKE '%Attribute'`, so without
        // signature-shape disambiguation the ambiguity suppresses the deps edge.
        // 命名規約のみでは 2 つの同名 FooAttribute が候補になり、曖昧さでエッジ抑制。
        Assert.DoesNotContain(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/RealFooAttribute.cs");
        Assert.DoesNotContain(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/ImpostorFooAttribute.cs");
    }

    [Fact]
    public void SearchReferences_MatchesCSharpAttributeSuffixConvention_Substring()
    {
        // issue #293 follow-up: `references MyAuditAttribute` (substring mode) must
        // find `[MyAudit]` call sites so `references` / `inspect` / `analyze_symbol`
        // stay consistent with `deps` / `impact` canonicalization.
        // issue #293 補足: `references MyAuditAttribute`（部分一致モード）が `[MyAudit]`
        // 参照サイトを見つけられなければならず、`references` / `inspect` /
        // `analyze_symbol` が `deps` / `impact` の正規化と整合する必要がある。
        InsertIndexedFile("src/MyAuditAttribute.cs", "csharp",
            """
            using System;

            [AttributeUsage(AttributeTargets.Class)]
            public sealed class MyAuditAttribute : Attribute
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

        var results = _reader.SearchReferences("MyAuditAttribute", lang: "csharp");

        Assert.Contains(results, r => r.Path == "src/Svc.cs" && r.ReferenceKind == "attribute");
    }

    [Fact]
    public void SearchReferences_MatchesCSharpAttributeSuffixConvention_Exact()
    {
        // Same scenario under `--exact` — the suffix alias must be applied even when
        // exact-name matching is requested, otherwise `references MyAuditAttribute
        // --exact` loses the attribute call site.
        // `--exact` 指定下でも同様 — exact match の場合でも suffix alias を適用しない
        // と、`references MyAuditAttribute --exact` は attribute 参照サイトを取りこぼす。
        InsertIndexedFile("src/MyAuditAttribute.cs", "csharp",
            """
            using System;

            [AttributeUsage(AttributeTargets.Class)]
            public sealed class MyAuditAttribute : Attribute
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

        var results = _reader.SearchReferences("MyAuditAttribute", lang: "csharp", exact: true);

        Assert.Contains(results, r => r.Path == "src/Svc.cs" && r.ReferenceKind == "attribute");
    }

    [Fact]
    public void SearchReferences_CSharpAttributeSuffixAliasDoesNotBleedToOtherLanguages()
    {
        // Alias must be C# only — a Java `@MyAudit(...)` annotation using the
        // suffix convention is not part of the Java ecosystem, so querying for
        // `MyAuditAttribute` under Java scope must not spuriously match `MyAudit`.
        // alias は C# 限定 — Java の `@MyAudit(...)` annotation は suffix 規約を使わない
        // ので、Java スコープで `MyAuditAttribute` を指定したときに `MyAudit` に
        // 誤って match してはならない。
        InsertIndexedFile("src/Svc.java", "java",
            """
            @MyAudit
            public class Svc {
            }
            """);

        var results = _reader.SearchReferences("MyAuditAttribute", lang: "java");

        Assert.Empty(results);
    }

    [Fact]
    public void SearchReferences_CSharpAttributeSuffixAlias_NotAppliedToCallKind()
    {
        // Adversarial review #7 follow-up: the suffix alias must NOT bleed into
        // `--kind call` queries. `references FooAttribute --kind call --lang csharp`
        // must not match a plain `Foo()` call — that would be a false positive.
        // adversarial review #7 補足: suffix alias を `--kind call` クエリに波及させない。
        // `references FooAttribute --kind call --lang csharp` が素の `Foo()` 呼び出しに
        // 一致してはならない（誤一致になる）。
        InsertIndexedFile("src/Svc.cs", "csharp",
            """
            public class Svc
            {
                public void Call()
                {
                    MyAudit();
                }
            }
            """);

        var results = _reader.SearchReferences("MyAuditAttribute", lang: "csharp", referenceKind: "call");

        Assert.DoesNotContain(results, r => r.SymbolName == "MyAudit");
    }

    [Fact]
    public void SearchReferences_CSharpAttributeSuffixAlias_UnscopedLangStillLimitsToCSharpAttributeRows()
    {
        // When `--lang` is omitted, the alias must still only match C# attribute rows.
        // A Java `@MyAudit(...)` or a bare `MyAudit()` call must not leak through.
        // `--lang` を省略したときも、alias は C# の attribute 行にしか一致してはならない。
        // Java の `@MyAudit(...)` や素の `MyAudit()` 呼び出しが漏れてはならない。
        InsertIndexedFile("src/Svc.java", "java",
            """
            @MyAudit
            public class Svc {
            }
            """);
        InsertIndexedFile("src/Caller.cs", "csharp",
            """
            public class Caller
            {
                public void Go()
                {
                    MyAudit();
                }
            }
            """);
        InsertIndexedFile("src/Target.cs", "csharp",
            """
            [MyAudit]
            public class Target
            {
            }
            """);

        var results = _reader.SearchReferences("MyAuditAttribute");

        // Should include the C# attribute site on Target.cs …
        Assert.Contains(results, r => r.Path == "src/Target.cs" && r.ReferenceKind == "attribute");
        // … but must NOT include the Java annotation nor the C# call row via alias.
        Assert.DoesNotContain(results, r => r.Path == "src/Svc.java");
        Assert.DoesNotContain(results, r => r.Path == "src/Caller.cs" && r.ReferenceKind == "call");
    }

    [Fact]
    public void SearchReferences_CSharpAttributeSuffixAlias_CaseInsensitiveQuery()
    {
        // The surrounding exact / substring paths are case-insensitive (folded or
        // NOCASE), so the suffix-stripping step must also be case-insensitive —
        // `references myauditattribute` / `MyAuditATTRIBUTE --exact` / etc. must
        // still produce the `MyAudit` alias and reach the `[MyAudit]` site.
        // 周辺の exact / substring 経路は case-insensitive（folded or NOCASE）なので、
        // suffix 除去も case-insensitive であるべき。
        InsertIndexedFile("src/MyAuditAttribute.cs", "csharp",
            """
            using System;

            public sealed class MyAuditAttribute : Attribute
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

        var lowercaseResults = _reader.SearchReferences("myauditattribute", lang: "csharp");
        Assert.Contains(lowercaseResults, r => r.Path == "src/Svc.cs" && r.ReferenceKind == "attribute");

        var mixedCaseExactResults = _reader.SearchReferences("MyAuditATTRIBUTE", lang: "csharp", exact: true);
        Assert.Contains(mixedCaseExactResults, r => r.Path == "src/Svc.cs" && r.ReferenceKind == "attribute");
    }

    [Fact]
    public void GetFileDependencies_CSharpAttributeAliasOnlyMatchesClassLikeTargets()
    {
        // issue #293 review: the C# attribute suffix alias UNION synthesizes a
        // `FooAttribute` lookup key for `[Foo]` references. Without a kind guard the
        // subsequent name-only join would spuriously attribute the consumer to any
        // file that merely defines a function / property / variable also named
        // `FooAttribute`. Only class-like target symbols should match synthetic alias
        // rows.
        // issue #293 レビュー指摘: `[Foo]` 用の alias UNION は `FooAttribute` という
        // lookup key を合成するが、kind によるガードが無いと、偶然 `FooAttribute`
        // という名前を持つ関数 / プロパティ / 変数を含むファイルにまで依存が張られて
        // しまう。合成 alias 行は class 系の target にのみ一致すべき。
        InsertIndexedFile("src/MyAuditAttribute.cs", "csharp",
            """
            using System;

            [AttributeUsage(AttributeTargets.Class)]
            public sealed class MyAuditAttribute : Attribute
            {
            }
            """);
        // Unrelated file containing a function named `MyAuditAttribute` — not an
        // attribute class, so `[MyAudit]` must not produce a dependency edge to it.
        // 無関係なファイルに関数として `MyAuditAttribute` が居るケース。
        // `[MyAudit]` はこのファイルへの依存を作ってはいけない。
        InsertIndexedFile("src/Util.cs", "csharp",
            """
            public static class Util
            {
                public static void MyAuditAttribute()
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

        var dependencies = _reader.GetFileDependencies(limit: 20, lang: "csharp");

        Assert.Contains(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/MyAuditAttribute.cs");
        Assert.DoesNotContain(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/Util.cs");
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
    public void AnalyzeImpact_CSharpVerbatimQueryKeepsOriginalInputOnMiss()
    {
        // issue #960: verbatim C# queries should normalize for lookup when a match
        // exists, but a miss must keep the original spelling in the resolved name
        // so impact output does not claim a canonical name the user never asked for.
        // issue #960: C# の verbatim クエリは一致時のみ lookup 用に正規化し、miss
        // したときは resolved name に元の spelling を残して、ユーザーが指定していない
        // canonical 名を `impact` 出力に出さないこと。
        InsertIndexedFile("src/Verbatim.cs", "csharp",
            """
            public class @class
            {
            }
            """);

        var hit = _reader.AnalyzeImpact("@class", maxDepth: 1, limit: 10, lang: "csharp");
        Assert.Equal("class", hit.ResolvedName);
        Assert.Equal(1, hit.DefinitionCount);

        var miss = _reader.AnalyzeImpact("@missing", maxDepth: 1, limit: 10, lang: "csharp");
        Assert.Equal("@missing", miss.ResolvedName);
        Assert.Equal(0, miss.DefinitionCount);
        Assert.Equal("no_matching_definition", miss.ZeroResultReason);
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
    public void GetFileDependencies_CSharp_PlainClassWithAttributeSuffixName_DoesNotCountAsAmbiguity()
    {
        // issue #293 round-16: same metadata-eligibility filter must apply to
        // the `deps` command. target_files.has_metadata_target_kind and the
        // target_ambiguity JOIN both require C# class-like targets to inherit
        // from an Attribute-suffixed base, so a plain `MyAuditAttribute`
        // cannot ambiguate the edge from `Svc.cs` to the real attribute class.
        // issue #293 round-16: 同じ適格性フィルタを deps にも適用する。
        // target_files.has_metadata_target_kind と target_ambiguity JOIN は
        // C# では Attribute suffix 継承を要求するため、plain `MyAuditAttribute` が
        // 存在しても実 attribute クラスへのエッジは残る。
        InsertIndexedFile("src/Real/MyAuditAttribute.cs", "csharp",
            """
            namespace Real;

            public sealed class MyAuditAttribute : System.Attribute
            {
            }
            """);
        InsertIndexedFile("src/Unrelated/MyAuditAttribute.cs", "csharp",
            """
            namespace Unrelated;

            public sealed class MyAuditAttribute
            {
            }
            """);
        InsertIndexedFile("src/Real/Svc.cs", "csharp",
            """
            namespace Real;

            [MyAudit]
            public class Svc
            {
            }
            """);

        var deps = _reader.GetFileDependencies(
            limit: 50,
            lang: "csharp");

        Assert.Contains(deps, d =>
            d.SourcePath == "src/Real/Svc.cs" &&
            d.TargetPath == "src/Real/MyAuditAttribute.cs");
        Assert.DoesNotContain(deps, d =>
            d.SourcePath == "src/Real/Svc.cs" &&
            d.TargetPath == "src/Unrelated/MyAuditAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharpNestedGenericNoArgAttribute_ResolvesToAttributeClass()
    {
        // issue #293 round-16: the no-arg C# attribute regex must handle
        // nested generic type arguments (e.g. `[MyAttr<Dictionary<string, int>>]`).
        // Previously the inner `<...>` segment excluded `>`, which broke on the
        // first inner `>` and classified the reference as a call.
        // issue #293 round-16: 引数なし C# 属性 regex が
        // `[MyAttr<Dictionary<string, int>>]` のようなネスト generic を
        // 扱えること。以前は内側の `<...>` セグメントが `>` を除外していて、
        // 最初の内側 `>` で崩れて call として誤分類されていた。
        InsertIndexedFile("src/MyAttrAttribute.cs", "csharp",
            """
            using System;
            using System.Collections.Generic;

            public sealed class MyAttrAttribute : Attribute
            {
            }
            """);
        InsertIndexedFile("src/Svc.cs", "csharp",
            """
            using System.Collections.Generic;

            [MyAttr<Dictionary<string, int>>]
            public class Svc
            {
            }
            """);

        var deps = _reader.GetFileDependencies(
            limit: 50,
            lang: "csharp");

        Assert.Contains(deps, d =>
            d.SourcePath == "src/Svc.cs" &&
            d.TargetPath == "src/MyAttrAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharp_IndirectAttributeInheritance_ResolvesAsMetadataTarget()
    {
        // issue #293 round-17: the metadata-eligibility filter must not require
        // the immediate base class to end in `Attribute`. Indirect inheritance
        // like `class MyAuditAttribute : BaseAudit` where `BaseAudit : Attribute`
        // is a valid `[MyAudit]` target at compile time. The previous strict
        // pattern (`signature LIKE '%: %Attribute%'`) wrongly excluded the
        // indirectly-derived class and dropped the deps edge. The loosened
        // pattern (`signature LIKE '%: %'`) accepts any class with an
        // inheritance clause, which is the best portable approximation since
        // SQL cannot resolve base types transitively.
        // issue #293 round-17: metadata 適格性フィルタは直接基底が
        // `Attribute` で終わることを要求してはならない。
        // `class MyAuditAttribute : BaseAudit` で `BaseAudit : Attribute` の
        // ような間接継承も `[MyAudit]` の有効な target である。以前の
        // 厳格パターンは間接継承を弾いて deps エッジを落としていた。
        // 緩和パターンは「継承節を持つ class」を近似として採用する。
        InsertIndexedFile("src/BaseAudit.cs", "csharp",
            """
            namespace App;

            public abstract class BaseAudit : System.Attribute
            {
            }
            """);
        InsertIndexedFile("src/MyAuditAttribute.cs", "csharp",
            """
            namespace App;

            public sealed class MyAuditAttribute : BaseAudit
            {
            }
            """);
        InsertIndexedFile("src/Svc.cs", "csharp",
            """
            namespace App;

            [MyAudit]
            public class Svc
            {
            }
            """);

        var deps = _reader.GetFileDependencies(
            limit: 50,
            lang: "csharp");

        Assert.Contains(deps, d =>
            d.SourcePath == "src/Svc.cs" &&
            d.TargetPath == "src/MyAuditAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_JavaScriptFunctionDecorator_ResolvesAsDependency()
    {
        // issue #293 round-18: JavaScript/TypeScript decorators legitimately target
        // factory `function`s (e.g. `function sealed(target) { ... }`), not only
        // class-like definitions. The metadata-target predicate must accept
        // `function` for JS/TS or decorator edges to a function target are dropped
        // from `deps`.
        // issue #293 round-18: JS/TS decorator は `function sealed(target){...}` のような
        // factory 関数も正当な target となる。JS/TS では `function` を metadata target の
        // 対象 kind として許可しないと、function を対象とする decorator edge が deps から欠落する。
        InsertIndexedFile("src/decorators.js", "javascript",
            """
            export function sealed(target) {
                Object.freeze(target);
            }
            """);
        InsertIndexedFile("src/model.js", "javascript",
            """
            import { sealed } from './decorators.js';

            @sealed
            class Foo {
            }
            """);

        var deps = _reader.GetFileDependencies(
            limit: 50,
            lang: "javascript");

        Assert.Contains(deps, d =>
            d.SourcePath == "src/model.js" &&
            d.TargetPath == "src/decorators.js");
    }

    [Fact]
    public void GetFileDependencies_CSharp_LegacyDbWithNullSignature_StillResolvesAttributeEdge()
    {
        // issue #293 round-19: the metadata-target signature clause must degrade
        // gracefully when the `symbols.signature` column exists but individual
        // rows carry NULL values — the common shape of a DB whose schema was
        // migrated in place (`TryMigrateForRead`) without reindexing. Requiring
        // `signature LIKE '%: %'` would silently drop the real
        // `[MyAudit]` → `class MyAuditAttribute : System.Attribute` edge there,
        // so the clause must treat NULL signature as eligible (equivalent to the
        // column-missing `1 = 1` fallback).
        // issue #293 round-19: metadata-target の signature 句は、列は存在するが
        // row の値が NULL の legacy-migration DB でも degrade する必要がある。
        // LIKE を強要すると本物の `[MyAudit]` edge が silent に落ちる。
        // 列欠落時の `1 = 1` fallback と同じく NULL も eligible にする。
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

        // Simulate the partial-migration shape: signature column is present but the
        // C# class row has a NULL signature, as if the schema were upgraded in place
        // without re-running extraction.
        // partial-migration の形を再現: signature 列はあるが C# class 行の signature が
        // NULL の状態 — その場 schema 移行後に再抽出していない DB と同じ。
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "UPDATE symbols SET signature = NULL WHERE name = 'MyAuditAttribute' AND kind = 'class'";
            cmd.ExecuteNonQuery();
        }

        var deps = _reader.GetFileDependencies(
            limit: 50,
            lang: "csharp");

        Assert.Contains(deps, d =>
            d.SourcePath == "src/Svc.cs" &&
            d.TargetPath == "src/MyAuditAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharp_LegacyDbWithNullSignature_NonAttributeName_DoesNotBlockMetadataEdge()
    {
        // issue #293 round-20: the NULL-signature fallback must not treat
        // arbitrary classes as metadata targets. Before this round the clause
        // accepted `signature IS NULL` for every C# `class`, so on a legacy-migration
        // DB a non-attribute class named `HelperClient` could share a name with an
        // attribute-applied site and silently inject false ambiguity. The tightened
        // fallback requires the canonical C# attribute naming convention
        // (`name LIKE '%Attribute'`), so a NULL-signature `HelperClient` is no
        // longer counted and the real `[MyAudit]` edge to `MyAuditAttribute`
        // survives even when both rows have NULL signatures.
        // issue #293 round-20: NULL-signature フォールバックが任意の class を
        // metadata target 扱いしないこと。以前は legacy-migration DB で
        // `signature IS NULL` のすべての C# class を許容しており、attribute 名と
        // 同名の非 attribute class (`HelperClient`) が偽の曖昧さを発生させ得た。
        // 新しいフォールバックは C# の命名規約 `name LIKE '%Attribute'` を要求する
        // ため、NULL-sig かつ非 *Attribute 名の class は候補から外れ、本物の
        // `[MyAudit]` → `MyAuditAttribute` edge は両行の signature が NULL でも
        // 残る。
        InsertIndexedFile("src/MyAuditAttribute.cs", "csharp",
            """
            public sealed class MyAuditAttribute : System.Attribute
            {
            }
            """);
        InsertIndexedFile("src/HelperClient.cs", "csharp",
            """
            namespace Unrelated;

            public class HelperClient : BaseService
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

        // Simulate partial-migration: all C# class rows have NULL signature.
        // partial-migration 再現: すべての C# class 行の signature を NULL 化。
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "UPDATE symbols SET signature = NULL WHERE kind = 'class'";
            cmd.ExecuteNonQuery();
        }

        var deps = _reader.GetFileDependencies(
            limit: 50,
            lang: "csharp");

        Assert.Contains(deps, d =>
            d.SourcePath == "src/Svc.cs" &&
            d.TargetPath == "src/MyAuditAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_JavaScript_SameNameInterface_DoesNotBlockFunctionDecoratorEdge()
    {
        // issue #293 round-20: TypeScript `interface` is a compile-time type-only
        // construct and cannot be a runtime decorator target, so a same-name
        // `interface` must NOT count toward metadata-target ambiguity against a
        // real `function` provider. The metadata-target predicate for JS/TS
        // therefore restricts candidate kinds to `class` and `function` only.
        // issue #293 round-20: TS の `interface` はコンパイル時型のため runtime
        // decorator target になれない。同名 `interface` が本物の `function`
        // provider への decorator edge を潰さないよう、JS/TS の metadata-target
        // 候補 kind は `class` と `function` に限定する。
        InsertIndexedFile("src/decorators.ts", "typescript",
            """
            export function sealed(target: any): void {
                Object.freeze(target);
            }
            """);
        InsertIndexedFile("src/types.ts", "typescript",
            """
            export interface sealed {
                readonly frozen: boolean;
            }
            """);
        InsertIndexedFile("src/model.ts", "typescript",
            """
            import { sealed } from './decorators';

            @sealed
            class Foo {
            }
            """);

        var deps = _reader.GetFileDependencies(
            limit: 50,
            lang: "typescript");

        Assert.Contains(deps, d =>
            d.SourcePath == "src/model.ts" &&
            d.TargetPath == "src/decorators.ts");
    }

    [Fact]
    public void GetFileDependencies_CSharp_SameNameInterface_DoesNotBlockMetadataEdge()
    {
        // issue #293 round-18: ambiguity should only count truly attribute-eligible
        // duplicates. In C#, only `class` can inherit from `System.Attribute` —
        // a same-named `interface` or `struct` cannot be an attribute target, so it
        // must not suppress the metadata deps edge to the legitimate attribute class.
        // issue #293 round-18: ambiguity 判定は attribute 適格な重複だけを数えるべき。
        // C# では `class` のみが `System.Attribute` を継承できるため、同名の
        // `interface` や `struct` が存在しても metadata deps edge を抑止してはならない。
        InsertIndexedFile("src/MyAuditAttribute.cs", "csharp",
            """
            public sealed class MyAuditAttribute : System.Attribute
            {
            }
            """);
        InsertIndexedFile("src/IMyAudit.cs", "csharp",
            """
            public interface MyAuditAttribute
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

        var deps = _reader.GetFileDependencies(
            limit: 50,
            lang: "csharp");

        Assert.Contains(deps, d =>
            d.SourcePath == "src/Svc.cs" &&
            d.TargetPath == "src/MyAuditAttribute.cs");
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
    public void SearchReferences_ExactCSharpUsingStaticFilter_PaginatesPastSuppressedRows()
    {
        InsertIndexedFile("src/Defs.cs", "csharp",
            """
            namespace Probe;

            public enum Color
            {
                Red,
                Blue
            }
            """);
        InsertIndexedFile("src/Use.cs", "csharp",
            """
            using static Probe.Color;

            namespace Probe;

            class Demo
            {
                object? Match(object value)
                {
                    return value is Red ? value : null;
                }
            }
            """);

        // One full raw page of suppressed rows is enough to prove that the
        // exact using-static filter keeps paging until it reaches the visible
        // call site.
        const int suppressedReferenceCount = 64;
        const int callReferenceLine = suppressedReferenceCount + 10;

        using (var updateFileCmd = _db.Connection.CreateCommand())
        {
            updateFileCmd.CommandText = "UPDATE files SET lines = @lines WHERE path = 'src/Use.cs'";
            updateFileCmd.Parameters.AddWithValue("@lines", callReferenceLine + 5);
            updateFileCmd.ExecuteNonQuery();
        }

        long useFileId;
        using (var fileIdCmd = _db.Connection.CreateCommand())
        {
            fileIdCmd.CommandText = "SELECT id FROM files WHERE path = 'src/Use.cs'";
            useFileId = (long)fileIdCmd.ExecuteScalar()!;
        }

        int suppressedReferenceColumn;
        string suppressedReferenceContext;
        using (var templateCmd = _db.Connection.CreateCommand())
        {
            templateCmd.CommandText = """
                SELECT r.column_number, COALESCE(r.context, rl.context)
                FROM symbol_references r
                JOIN files f ON r.file_id = f.id
                LEFT JOIN reference_lines rl ON rl.id = r.reference_line_id
                WHERE f.path = 'src/Use.cs'
                  AND r.symbol_name = 'Red'
                  AND r.reference_kind = 'type_reference'
                LIMIT 1
                """;
            using var templateReader = templateCmd.ExecuteReader();
            Assert.True(templateReader.Read());
            suppressedReferenceColumn = templateReader.GetInt32(0);
            suppressedReferenceContext = templateReader.GetString(1);
        }

        var syntheticReferences = new List<ReferenceRecord>(suppressedReferenceCount + 1);
        for (int line = 10; line < callReferenceLine; line++)
        {
            syntheticReferences.Add(new ReferenceRecord
            {
                FileId = useFileId,
                SymbolName = "Red",
                ReferenceKind = "type_reference",
                Line = line,
                Column = suppressedReferenceColumn,
                Context = suppressedReferenceContext,
                ContainerKind = "function",
                ContainerName = "Match",
            });
        }

        syntheticReferences.Add(new ReferenceRecord
        {
            FileId = useFileId,
            SymbolName = "Red",
            ReferenceKind = "call",
            Line = callReferenceLine,
            Column = 9,
            Context = "        Red();",
            ContainerKind = "function",
            ContainerName = "Match",
        });
        _writer.InsertReferences(syntheticReferences);

        var result = Assert.Single(_reader.SearchReferences("Red", limit: 1, lang: "csharp", exact: true, pathPatterns: ["src/Use.cs"]));
        Assert.Equal("call", result.ReferenceKind);
        Assert.Equal(callReferenceLine, result.Line);
        Assert.Equal(1, _reader.CountSearchReferences("Red", limit: 1, lang: "csharp", exact: true, pathPatterns: ["src/Use.cs"]));
        Assert.Equal(new QueryCountResult(1, 1), _reader.CountSearchReferencesTotal("Red", lang: "csharp", exact: true, pathPatterns: ["src/Use.cs"]));
    }

    [Fact]
    public void SearchReferences_ExactCSharpUsingStaticTypeAliasPattern_KeepsVisibleRows()
    {
        InsertIndexedFile("src/Defs.cs", "csharp",
            """
            namespace Probe
            {
                public enum Color
                {
                    Red
                }

                namespace Real
                {
                    public class Red {}
                }
            }
            """);
        InsertIndexedFile("src/Use.cs", "csharp",
            """
            using static Probe.Color;
            using Red = Probe.Real.Red;

            namespace Probe;

            class Demo
            {
                bool Match(object value) => value is Red;
            }
            """);

        var result = Assert.Single(_reader.SearchReferences("Red", limit: 20, lang: "csharp", referenceKind: "type_reference", exact: true, pathPatterns: ["src/Use.cs"]));
        Assert.Equal("Red", result.SymbolName);
        Assert.Equal("type_reference", result.ReferenceKind);
        Assert.Equal("Match", result.ContainerName);
        Assert.Contains("value is Red", result.Context, StringComparison.Ordinal);
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
    public void SearchReferences_ExactSameLineResults_AreOrderedByColumn()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/reference_order.py",
            Lang = "python",
            Size = 32,
            Lines = 10,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks([
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 10,
                Content = "def outer():\n    pass\n",
            }
        ]);
        _writer.InsertReferences([
            new ReferenceRecord
            {
                FileId = fileId,
                SymbolName = "Target",
                ReferenceKind = "call",
                Line = 5,
                Column = 20,
                Context = "target_late() target_early()",
            },
            new ReferenceRecord
            {
                FileId = fileId,
                SymbolName = "Target",
                ReferenceKind = "call",
                Line = 5,
                Column = 5,
                Context = "target_early() target_late()",
            },
        ]);

        var results = _reader.SearchReferences("Target", limit: 2, lang: "python", exact: true, pathPatterns: ["src/reference_order.py"]);
        Assert.Collection(results,
            first => Assert.Equal(5, first.Column),
            second => Assert.Equal(20, second.Column));
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
                Content = "public class MixedOwner { public void Setup() { Changed += Handler; Changed(); } }\n",
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
        ]);

        var caller = Assert.Single(_reader.GetCallers("Changed", lang: "csharp", exact: true, pathPatterns: ["mixed_kind_caller"]));
        Assert.Equal("Setup", caller.CallerName);
        Assert.Equal("Changed", caller.CalleeName);
        Assert.Equal(2, caller.ReferenceCount);
        Assert.True(caller.HasMixedReferenceKinds);
        Assert.Equal(new[] { "event", "invoke" }, caller.ReferenceKinds);
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
        Assert.Equal("invoke", callees[1].ReferenceKind);
        Assert.False(callees[1].HasMixedReferenceKinds);
        Assert.Equal(new[] { "invoke" }, callees[1].ReferenceKinds);

        var rawCaller = Assert.Single(_reader.GetCallers("Changed", lang: "csharp", exact: true, pathPatterns: ["mixed_kind_caller"], rawKinds: true));
        Assert.Equal(new[] { "call", "subscribe" }, rawCaller.ReferenceKinds);
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
    public void AnalyzeImpact_CycleReportsTerminationReasonAndMembers()
    {
        // Issue #1883: a caller cycle must be explicit in the impact metadata so consumers
        // can distinguish a natural end from a traversal stopped by the visited guard.
        // #1883: caller cycle は impact metadata に明示し、自然終了と visited guard による停止を区別する。
        InsertIndexedFile("src/impact_cycle.cs", "csharp",
            """
            public static class ImpactCycle
            {
                public static void A() { B(); }
                public static void B() { C(); }
                public static void C() { A(); }
            }
            """);

        var analysis = _reader.AnalyzeImpact("C", maxDepth: 5, limit: 20, lang: "csharp", pathPatterns: ["impact_cycle"]);

        Assert.False(analysis.Truncated);
        Assert.Null(analysis.TruncatedReason);
        Assert.Equal(ImpactTerminationReasons.CycleDetected, analysis.TerminationReason);
        Assert.True(analysis.CycleDetected);
        var cycle = Assert.Single(analysis.Cycles!);
        Assert.Equal(new[] { "A", "B", "C" }, cycle.Members);
    }

    [Fact]
    public void AnalyzeImpact_CycleBetweenAlreadyVisitedDirectCallersIsReported()
    {
        InsertIndexedFile("src/impact_direct_cycle.cs", "csharp",
            """
            public static class ImpactDirectCycle
            {
                public static void Leaf() { }
                public static void A() { Leaf(); B(); }
                public static void B() { Leaf(); A(); }
            }
            """);

        var analysis = _reader.AnalyzeImpact("Leaf", maxDepth: 5, limit: 20, lang: "csharp", pathPatterns: ["impact_direct_cycle"]);

        Assert.False(analysis.Truncated);
        Assert.Null(analysis.TruncatedReason);
        Assert.Equal(ImpactTerminationReasons.CycleDetected, analysis.TerminationReason);
        Assert.True(analysis.CycleDetected);
        var cycle = Assert.Single(analysis.Cycles!);
        Assert.Equal(new[] { "A", "B" }, cycle.Members);
    }

    [Fact]
    public void AnalyzeImpact_BoundaryRootCycleReportsCycleNotMaxDepth()
    {
        InsertIndexedFile("src/impact_boundary_root_cycle.cs", "csharp",
            """
            public static class ImpactBoundaryRootCycle
            {
                public static void Leaf() { A(); }
                public static void A() { Leaf(); }
            }
            """);

        var analysis = _reader.AnalyzeImpact("Leaf", maxDepth: 1, limit: 20, lang: "csharp", pathPatterns: ["impact_boundary_root_cycle"]);

        Assert.False(analysis.Truncated);
        Assert.Null(analysis.TruncatedReason);
        Assert.Equal(ImpactTerminationReasons.CycleDetected, analysis.TerminationReason);
        Assert.True(analysis.CycleDetected);
        var cycle = Assert.Single(analysis.Cycles!);
        Assert.Equal(new[] { "A", "Leaf" }, cycle.Members);
    }

    [Fact]
    public void AnalyzeImpact_MaxDepthReportsTerminationReason()
    {
        InsertIndexedFile("src/impact_depth_reason.cs", "csharp",
            """
            public static class ImpactDepthReason
            {
                public static void Leaf() { }
                public static void Mid() { Leaf(); }
                public static void Top() { Mid(); }
            }
            """);

        var analysis = _reader.AnalyzeImpact("Leaf", maxDepth: 1, limit: 20, lang: "csharp", pathPatterns: ["impact_depth_reason"]);

        Assert.False(analysis.Truncated);
        Assert.Null(analysis.TruncatedReason);
        Assert.Equal(ImpactTerminationReasons.MaxDepthReached, analysis.TerminationReason);
        Assert.False(analysis.CycleDetected);
        Assert.Null(analysis.Cycles);
    }

    [Fact]
    public void AnalyzeImpact_MaxDepthBoundaryWithoutSkippedCallerReportsCompleted()
    {
        InsertIndexedFile("src/impact_depth_completed.cs", "csharp",
            """
            public static class ImpactDepthCompleted
            {
                public static void Leaf() { }
                public static void OnlyCaller() { Leaf(); }
            }
            """);

        var analysis = _reader.AnalyzeImpact("Leaf", maxDepth: 1, limit: 20, lang: "csharp", pathPatterns: ["impact_depth_completed"]);

        Assert.False(analysis.Truncated);
        Assert.Null(analysis.TruncatedReason);
        Assert.Equal(ImpactTerminationReasons.Completed, analysis.TerminationReason);
        Assert.False(analysis.CycleDetected);
        Assert.Null(analysis.Cycles);
    }

    [Fact]
    public void AnalyzeImpact_DepthZeroReportsCompletedForResolvedSymbol()
    {
        InsertIndexedFile("src/impact_depth_zero.cs", "csharp",
            """
            public static class ImpactDepthZero
            {
                public static void Leaf() { }
                public static void Caller() { Leaf(); }
            }
            """);

        var analysis = _reader.AnalyzeImpact("Leaf", maxDepth: 0, limit: 20, lang: "csharp", pathPatterns: ["impact_depth_zero"]);

        Assert.False(analysis.Truncated);
        Assert.Null(analysis.TruncatedReason);
        Assert.Equal(ImpactTerminationReasons.Completed, analysis.TerminationReason);
        Assert.Equal("depth_zero", analysis.ZeroResultReason);
        Assert.False(analysis.CycleDetected);
        Assert.Null(analysis.Cycles);
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

        // Direct callers (B, C) keep a single trivial path that ends at themselves.
        var bResult = resultsWithPaths.Single(r => r.CallerName == "B");
        Assert.NotNull(bResult.Paths);
        Assert.Equal(new[] { "Foo->B" }, bResult.Paths!.Select(p => string.Join("->", p)).ToArray());
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
    public void AnalyzeImpact_UserLimitTruncation_PropagatesTruncatedReason()
    {
        // #1533: AnalyzeImpact must surface the truncated_reason returned by
        // GetTransitiveCallers so the CLI/MCP layer can give actionable retry advice.
        // #1533: AnalyzeImpact は GetTransitiveCallers の truncated_reason を
        // そのまま伝搬して CLI/MCP 側で適切な再試行ガイダンスを出せるようにする。
        const int callerCount = 6;
        for (int i = 0; i < callerCount; i++)
        {
            var callerFileId = _writer.UpsertFile(new FileRecord
            {
                Path = $"src/impact_limit_caller_{i:D2}.py",
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
                Content = $"def impact_caller_{i:D2}():\n    return widget_op()\n",
            }]);
            _writer.InsertReferences([
                new ReferenceRecord
                {
                    FileId = callerFileId,
                    SymbolName = "widget_op",
                    ReferenceKind = "call",
                    Line = 2,
                    Column = 12,
                    Context = "return widget_op()",
                    ContainerKind = "function",
                    ContainerName = $"impact_caller_{i:D2}",
                },
            ]);
        }

        var analysis = _reader.AnalyzeImpact("widget_op", maxDepth: 1, limit: 2);

        Assert.True(analysis.Truncated);
        Assert.Equal(ImpactTruncatedReasons.UserLimit, analysis.TruncatedReason);
        Assert.Equal(2, analysis.Callers.Count);
    }

    [Fact]
    public void AnalyzeImpact_NotTruncated_LeavesTruncatedReasonNull()
    {
        // #1533: truncated_reason must be omitted (null) when truncated is false so
        // downstream consumers do not need to ignore stale reason strings.
        // #1533: truncated が false のときは truncated_reason を null にして、
        // 利用側が古い理由文字列を無視する必要がないようにする。
        InsertIndexedFile("src/impact_no_truncate.cs", "csharp",
            """
            public static class NoTruncateChain
            {
                public static void Leaf() { }
                public static void Caller() { Leaf(); }
            }
            """);

        var analysis = _reader.AnalyzeImpact("Leaf", maxDepth: 1, limit: 50, lang: "csharp", pathPatterns: ["impact_no_truncate"]);

        Assert.False(analysis.Truncated);
        Assert.Null(analysis.TruncatedReason);
        Assert.Single(analysis.Callers);
    }

    [Fact]
    public void AnalyzeImpact_ClassSymbolReturnsHeuristicFileDependencyHints()
    {
        InsertIndexedFile("src/FolderDiffService.cs", "csharp",
            """
            public class FolderDiffService
            {
                public void ExecuteFolderDiffAsync() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Run(FolderDiffService service)
                {
                    service.ExecuteFolderDiffAsync();
                }
            }
            """);

        var analysis = _reader.AnalyzeImpact("FolderDiffService", maxDepth: 3, limit: 10);

        Assert.Equal("file_dependency_hints", analysis.ImpactMode);
        Assert.True(analysis.Heuristic);
        Assert.Empty(analysis.Callers);
        Assert.True(analysis.HasClassLikeDefinitions);
        Assert.False(analysis.HasMultipleDefinitions);
        Assert.False(analysis.HasMultipleDefinitionFiles);
        Assert.Equal(1, analysis.HintCount);
        var edge = Assert.Single(analysis.FileImpacts);
        Assert.Equal("src/App.cs", edge.SourcePath);
        Assert.Equal("src/FolderDiffService.cs", edge.TargetPath);
        Assert.Contains("ExecuteFolderDiffAsync", edge.Symbols);
    }

    [Fact]
    public void AnalyzeImpact_ClassAndNamespaceWithSameName_StillReturnsHeuristicHints()
    {
        InsertIndexedFile("src/FooService.cs", "csharp",
            """
            namespace FooService;

            public class FooService
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Boot(FooService service)
                {
                    service.Run();
                }
            }
            """);

        var analysis = _reader.AnalyzeImpact("FooService", maxDepth: 3, limit: 10);

        Assert.Equal("file_dependency_hints", analysis.ImpactMode);
        Assert.True(analysis.Heuristic);
        Assert.True(analysis.HasMultipleDefinitions);
        Assert.False(analysis.HasMultipleDefinitionFiles);
        Assert.Equal(2, analysis.DefinitionCount);
        Assert.Equal(1, analysis.HintCount);
        Assert.Equal("src/App.cs", Assert.Single(analysis.FileImpacts).SourcePath);
    }

    [Fact]
    public void AnalyzeImpact_FoldEquivalentClassDefinitions_ReportAmbiguity()
    {
        InsertIndexedFile("src/FooService.cs", "csharp",
            """
            public class FooService
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/FullwidthFooService.cs", "csharp",
            """
            public class ＦｏｏＳｅｒｖｉｃｅ
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Boot(FooService service)
                {
                    service.Run();
                }
            }
            """);

        var analysis = _reader.AnalyzeImpact("FooService", maxDepth: 3, limit: 10);

        Assert.Equal("none", analysis.ImpactMode);
        Assert.False(analysis.Heuristic);
        Assert.Empty(analysis.FileImpacts);
        Assert.Equal(2, analysis.DefinitionCount);
        Assert.True(analysis.HasMultipleDefinitions);
        Assert.Equal("multiple_definition_files", analysis.ZeroResultReason);
    }

    [Fact]
    public void AnalyzeImpact_PartialClassWithoutReverseEdges_ExplainsMultipleDefinitions()
    {
        InsertIndexedFile("src/Worker.Part1.cs", "csharp",
            """
            public partial class Worker
            {
                public void Start() { }
            }
            """);
        InsertIndexedFile("src/Worker.Part2.cs", "csharp",
            """
            public partial class Worker
            {
                public void Stop() { }
            }
            """);

        var analysis = _reader.AnalyzeImpact("Worker", maxDepth: 3, limit: 10);

        Assert.Equal("none", analysis.ImpactMode);
        Assert.Empty(analysis.Callers);
        Assert.Empty(analysis.FileImpacts);
        Assert.True(analysis.HasClassLikeDefinitions);
        Assert.True(analysis.HasMultipleDefinitions);
        Assert.True(analysis.HasMultipleDefinitionFiles);
        Assert.Equal("multiple_definition_files", analysis.ZeroResultReason);
        Assert.Contains("deps --path <definition-path> --reverse", analysis.Suggestion);
    }

    [Fact]
    public void AnalyzeImpact_DuplicateDefinitionsInOneFile_ExplainsAmbiguity()
    {
        InsertIndexedFile("src/Services.cs", "csharp",
            """
            namespace A
            {
                public class FooService
                {
                    public void Run() { }
                }
            }

            namespace B
            {
                public class FooService
                {
                    public void Run() { }
                }
            }
            """);

        var analysis = _reader.AnalyzeImpact("FooService", maxDepth: 3, limit: 10);

        Assert.Equal("none", analysis.ImpactMode);
        Assert.Empty(analysis.Callers);
        Assert.Empty(analysis.FileImpacts);
        Assert.Equal(2, analysis.DefinitionCount);
        Assert.Equal(1, analysis.DefinitionFileCount);
        Assert.True(analysis.HasMultipleDefinitions);
        Assert.False(analysis.HasMultipleDefinitionFiles);
        Assert.Equal("multiple_definitions", analysis.ZeroResultReason);
        Assert.Contains("fully qualified or member symbol query", analysis.Suggestion);
    }

    [Fact]
    public void AnalyzeImpact_ClassCollisionWithoutTypeEvidenceReturnsNoHints()
    {
        InsertIndexedFile("src/FooService.cs", "csharp",
            """
            public class FooService
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/BarService.cs", "csharp",
            """
            public class BarService
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Boot(BarService service)
                {
                    service.Run();
                }
            }
            """);

        var analysis = _reader.AnalyzeImpact("FooService", maxDepth: 3, limit: 10);

        Assert.Equal("none", analysis.ImpactMode);
        Assert.False(analysis.Heuristic);
        Assert.Empty(analysis.Callers);
        Assert.Empty(analysis.FileImpacts);
        Assert.Equal(0, analysis.HintCount);
        Assert.Equal("class_symbol_no_symbol_callers", analysis.ZeroResultReason);
    }

    [Fact]
    public void AnalyzeImpact_NamespaceDoesNotFallbackToFileDependencies()
    {
        InsertIndexedFile("src/Services.cs", "csharp",
            """
            namespace Acme;

            public class FooService
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            namespace Acme;

            public class App
            {
                public void Boot(FooService service)
                {
                    service.Run();
                }
            }
            """);

        var analysis = _reader.AnalyzeImpact("Acme", maxDepth: 3, limit: 10);

        Assert.Equal("none", analysis.ImpactMode);
        Assert.Empty(analysis.Callers);
        Assert.Empty(analysis.FileImpacts);
        Assert.Equal("non_callable_symbol_kind", analysis.ZeroResultReason);
    }

    [Fact]
    public void AnalyzeImpact_ImportOnlyQueryReportsNonCallableSymbolKind()
    {
        InsertIndexedFile("src/app.py", "python",
            """
            import requests
            """);

        var analysis = _reader.AnalyzeImpact("requests", maxDepth: 3, limit: 10);

        Assert.Equal("none", analysis.ImpactMode);
        Assert.Empty(analysis.Callers);
        Assert.Empty(analysis.FileImpacts);
        Assert.Equal(1, analysis.DefinitionCount);
        Assert.Equal("import", Assert.Single(analysis.Definitions).Kind);
        Assert.Equal("non_callable_symbol_kind", analysis.ZeroResultReason);
        Assert.Contains("definition <symbol>", analysis.Suggestion);
    }

    [Fact]
    public void AnalyzeImpact_UnresolvedExternalCallOnlyWithoutTypeEvidenceReturnsNoHints()
    {
        InsertIndexedFile("src/FolderDiffService.cs", "csharp",
            """
            public class FolderDiffService
            {
                public void ExecuteFolderDiffAsync() { }
            }
            """);
        InsertIndexedFile("src/ExternalConsumer.cs", "csharp",
            """
            public class ExternalConsumer
            {
                public void Boot()
                {
                    ExecuteFolderDiffAsync();
                }
            }
            """);

        var analysis = _reader.AnalyzeImpact("FolderDiffService", maxDepth: 3, limit: 10);

        Assert.Equal("none", analysis.ImpactMode);
        Assert.False(analysis.Heuristic);
        Assert.Empty(analysis.Callers);
        Assert.Equal(0, analysis.HintCount);
        Assert.Empty(analysis.FileImpacts);
        Assert.Equal("class_symbol_no_symbol_callers", analysis.ZeroResultReason);
    }

    [Fact]
    public void AnalyzeImpact_UnicodeTypeEvidenceStillEnablesHeuristicHints()
    {
        InsertIndexedFile("src/ＦｏｏＳｅｒｖｉｃｅ.cs", "csharp",
            """
            public class ＦｏｏＳｅｒｖｉｃｅ
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Boot(ＦｏｏＳｅｒｖｉｃｅ service)
                {
                    service.Run();
                }
            }
            """);

        var analysis = _reader.AnalyzeImpact("ＦｏｏＳｅｒｖｉｃｅ", maxDepth: 3, limit: 10);

        Assert.Equal("file_dependency_hints", analysis.ImpactMode);
        Assert.True(analysis.Heuristic);
        Assert.Equal(1, analysis.HintCount);
        Assert.Equal("src/App.cs", Assert.Single(analysis.FileImpacts).SourcePath);
    }

    [Fact]
    public void AnalyzeImpact_CommentOnlyTypeMentionDoesNotCountAsTypeEvidence()
    {
        InsertIndexedFile("src/FooService.cs", "csharp",
            """
            public class FooService
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/OtherService.cs", "csharp",
            """
            public class OtherService
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Boot(OtherService service)
                {
                    service.Run(); // TODO: maybe replace with FooService later
                }
            }
            """);

        var analysis = _reader.AnalyzeImpact("FooService", maxDepth: 3, limit: 10);

        Assert.Equal("none", analysis.ImpactMode);
        Assert.False(analysis.Heuristic);
        Assert.Empty(analysis.FileImpacts);
        Assert.Equal(0, analysis.HintCount);
        Assert.Equal("class_symbol_no_symbol_callers", analysis.ZeroResultReason);
    }

    [Fact]
    public void AnalyzeImpact_StringLiteralTypeMentionDoesNotCountAsTypeEvidence()
    {
        InsertIndexedFile("src/FooService.cs", "csharp",
            """
            public class FooService
            {
                public void Execute() { }
            }
            """);
        InsertIndexedFile("src/Worker.cs", "csharp",
            """
            public class Worker
            {
                public void Execute() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Boot(Worker worker)
                {
                    var label = "FooService";
                    worker.Execute();
                }
            }
            """);

        var analysis = _reader.AnalyzeImpact("FooService", maxDepth: 3, limit: 10);

        Assert.Equal("none", analysis.ImpactMode);
        Assert.False(analysis.Heuristic);
        Assert.Empty(analysis.FileImpacts);
        Assert.Equal(0, analysis.HintCount);
        Assert.Equal("class_symbol_no_symbol_callers", analysis.ZeroResultReason);
    }

    [Fact]
    public void AnalyzeImpact_ExcludeTestsIgnoresOutOfScopeDuplicateDefinitions()
    {
        InsertIndexedFile("src/FooService.cs", "csharp",
            """
            public class FooService
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("tests/FooServiceTests.cs", "csharp",
            """
            public class FooService
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Boot(FooService service)
                {
                    service.Run();
                }
            }
            """);

        var analysis = _reader.AnalyzeImpact("FooService", maxDepth: 3, limit: 10, excludeTests: true);

        Assert.Equal("file_dependency_hints", analysis.ImpactMode);
        Assert.True(analysis.Heuristic);
        Assert.False(analysis.HasMultipleDefinitionFiles);
        Assert.Equal(1, analysis.DefinitionFileCount);
        Assert.Equal(1, analysis.HintCount);
        Assert.Equal("src/FooService.cs", Assert.Single(analysis.Definitions).Path);
        Assert.Equal("src/App.cs", Assert.Single(analysis.FileImpacts).SourcePath);
    }

    [Fact]
    public void AnalyzeImpact_IgnoresUnsupportedLanguageDuplicates()
    {
        InsertIndexedFile("src/FooService.cs", "csharp",
            """
            public class FooService
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/tools.txt", "text",
            """
            FooService() {
              :
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Boot(FooService service)
                {
                    service.Run();
                }
            }
            """);

        var analysis = _reader.AnalyzeImpact("FooService", maxDepth: 3, limit: 10);

        Assert.Equal("file_dependency_hints", analysis.ImpactMode);
        Assert.True(analysis.Heuristic);
        Assert.False(analysis.HasMultipleDefinitions);
        Assert.False(analysis.HasMultipleDefinitionFiles);
        Assert.Equal(1, analysis.DefinitionFileCount);
        Assert.Equal("src/FooService.cs", Assert.Single(analysis.Definitions).Path);
        Assert.Equal("src/App.cs", Assert.Single(analysis.FileImpacts).SourcePath);
    }

    [Fact]
    public void AnalyzeImpact_ExactDefinitionResolutionSkipsUnsupportedMatchesBeforeLimit()
    {
        for (int i = 0; i < 60; i++)
        {
            InsertIndexedFile($"scripts/Foo{i:D2}.txt", "text",
                """
                Foo() {
                  :
                }
                """);
        }

        InsertIndexedFile("src/Foo.cs", "csharp",
            """
            public class Foo
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Boot(Foo service)
                {
                    service.Run();
                }
            }
            """);

        var analysis = _reader.AnalyzeImpact("Foo", maxDepth: 3, limit: 10);

        Assert.Equal("file_dependency_hints", analysis.ImpactMode);
        Assert.True(analysis.Heuristic);
        Assert.Equal(1, analysis.DefinitionCount);
        Assert.Equal("src/Foo.cs", Assert.Single(analysis.Definitions).Path);
        Assert.Equal("src/App.cs", Assert.Single(analysis.FileImpacts).SourcePath);
    }

    [Fact]
    public void AnalyzeImpact_SubstringTypeEvidenceDoesNotCountAsStructuredEvidence()
    {
        InsertIndexedFile("src/Foo.cs", "csharp",
            """
            public class Foo
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/FooService.cs", "csharp",
            """
            public class FooService
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Handle(FooService service)
                {
                    service.Run();
                }
            }
            """);

        var analysis = _reader.AnalyzeImpact("Foo", maxDepth: 3, limit: 10);

        Assert.Equal("none", analysis.ImpactMode);
        Assert.False(analysis.Heuristic);
        Assert.Empty(analysis.FileImpacts);
        Assert.Equal(0, analysis.HintCount);
        Assert.Equal("class_symbol_no_symbol_callers", analysis.ZeroResultReason);
    }

    [Fact]
    public void AnalyzeImpact_HeuristicHintsSetTruncatedWhenLimitReached()
    {
        InsertIndexedFile("src/FolderDiffService.cs", "csharp",
            """
            public class FolderDiffService
            {
                public void ExecuteFolderDiffAsync() { }
            }
            """);
        InsertIndexedFile("src/App1.cs", "csharp",
            """
            public class App1
            {
                public void Boot(FolderDiffService service)
                {
                    service.ExecuteFolderDiffAsync();
                }
            }
            """);
        InsertIndexedFile("src/App2.cs", "csharp",
            """
            public class App2
            {
                public void Boot(FolderDiffService service)
                {
                    service.ExecuteFolderDiffAsync();
                }
            }
            """);

        var analysis = _reader.AnalyzeImpact("FolderDiffService", maxDepth: 3, limit: 1);

        Assert.Equal("file_dependency_hints", analysis.ImpactMode);
        Assert.True(analysis.Heuristic);
        Assert.True(analysis.Truncated);
        Assert.Single(analysis.FileImpacts);
        Assert.Equal(1, analysis.HintCount);
    }

    [Fact]
    public void AnalyzeImpact_HeuristicHintsKeepActualReferenceCount()
    {
        InsertIndexedFile("src/FolderDiffService.cs", "csharp",
            """
            public class FolderDiffService
            {
                public void ExecuteFolderDiffAsync() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Boot(FolderDiffService service)
                {
                    service.ExecuteFolderDiffAsync();
                    service.ExecuteFolderDiffAsync();
                    service.ExecuteFolderDiffAsync();
                }
            }
            """);

        var analysis = _reader.AnalyzeImpact("FolderDiffService", maxDepth: 3, limit: 10);

        Assert.Equal("file_dependency_hints", analysis.ImpactMode);
        var edge = Assert.Single(analysis.FileImpacts);
        Assert.Equal(4, edge.ReferenceCount);
        Assert.Equal("ExecuteFolderDiffAsync,FolderDiffService", edge.Symbols);
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
    }

    [Fact]
    public void GetRepoMap_AddsFileFallbackEntrypointForTopLevelProgram()
    {
        InsertIndexedFile("src/Program.cs", "csharp", "var client = new ApiClient();\nConsole.WriteLine(client);\n");

        var map = _reader.GetRepoMap(limit: 5, pathPatterns: new[] { "src/Program.cs" });

        Assert.Contains(map.Entrypoints, item => item.Kind == "file" && item.Name == "Program.cs" && item.Path == "src/Program.cs");
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
        var analysis = _reader.AnalyzeSymbol("Heading", limit: 5, lang: "markdown");

        Assert.Equal("markdown", analysis.GraphLanguage);
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
            Path = "src/nullcol.cs", Lang = "csharp", Size = 100, Lines = 10,
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
    public void GetUnusedSymbols_ClassifiesConfidenceBucketsAndSortsPrivateFirst()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/config/unused_fixture.cs",
            Lang = "csharp",
            Size = 200,
            Lines = 20,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "Hidden",
                Line = 3,
                StartLine = 3,
                EndLine = 3,
                Signature = "private void Hidden() { }",
                Visibility = "private",
                ContainerKind = "class",
                ContainerName = "ExportedApi",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "InternalOnly",
                Line = 5,
                StartLine = 5,
                EndLine = 5,
                Signature = "internal void InternalOnly() { }",
                Visibility = "internal",
                ContainerKind = "class",
                ContainerName = "ExportedApi",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "PathResolver",
                Line = 1,
                StartLine = 1,
                EndLine = 1,
                Signature = "public class PathResolver",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "AdoptionService",
                Line = 7,
                StartLine = 7,
                EndLine = 7,
                Signature = "public class AdoptionService",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "TokenService",
                Line = 8,
                StartLine = 8,
                EndLine = 8,
                Signature = "public class TokenService",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "AppSettings",
                Line = 9,
                StartLine = 9,
                EndLine = 11,
                Signature = "public class AppSettings",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "ApplyConfiguration",
                Line = 12,
                StartLine = 12,
                EndLine = 12,
                Signature = "public void ApplyConfiguration()",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "AppSettings",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "UseIOptions",
                Line = 13,
                StartLine = 13,
                EndLine = 13,
                Signature = "public void UseIOptions()",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "AppSettings",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "ConnectionString",
                Line = 10,
                StartLine = 10,
                EndLine = 10,
                Signature = "public string ConnectionString { get; set; }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "AppSettings",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: null, lang: "csharp",
            pathPatterns: ["unused_fixture.cs"], excludePathPatterns: null, excludeTests: false);

        Assert.Equal(["Hidden", "InternalOnly", "PathResolver", "ConnectionString", "AdoptionService", "TokenService", "AppSettings", "ApplyConfiguration", "UseIOptions"], unused.Select(symbol => symbol.Name).ToArray());
        Assert.Equal("likely_unused_private", unused[0].UnusedBucket);
        Assert.Equal("medium", unused[0].UnusedConfidence);
        Assert.Equal("maybe_unused_nonpublic", unused[1].UnusedBucket);
        Assert.Equal("low", unused[1].UnusedConfidence);
        Assert.Equal("public_or_exported_no_refs", unused[2].UnusedBucket);
        Assert.Equal("reflection_or_config_suspect", unused[3].UnusedBucket);
        Assert.Contains("config or attribute-driven reflection", unused[3].UnusedReason);
        Assert.Equal("public_or_exported_no_refs", unused[4].UnusedBucket);
        Assert.Equal("public_or_exported_no_refs", unused[5].UnusedBucket);
        Assert.Equal("public_or_exported_no_refs", unused[6].UnusedBucket);
        Assert.Equal("public_or_exported_no_refs", unused[7].UnusedBucket);
        Assert.Equal("public_or_exported_no_refs", unused[8].UnusedBucket);
        Assert.Equal("public_or_exported_no_refs", Assert.Single(unused, symbol => symbol.Name == "PathResolver").UnusedBucket);
        Assert.Equal("public_or_exported_no_refs", Assert.Single(unused, symbol => symbol.Name == "AdoptionService").UnusedBucket);
        Assert.Equal("public_or_exported_no_refs", Assert.Single(unused, symbol => symbol.Name == "TokenService").UnusedBucket);
        Assert.Equal("public_or_exported_no_refs", Assert.Single(unused, symbol => symbol.Name == "ApplyConfiguration").UnusedBucket);
        Assert.Equal("public_or_exported_no_refs", Assert.Single(unused, symbol => symbol.Name == "UseIOptions").UnusedBucket);
    }

    [Fact]
    public void GetUnusedSymbols_PlainCliOptionsProperties_StayInPublicBucket()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/cli_options_fixture.cs",
            Lang = "csharp",
            Size = 160,
            Lines = 6,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "CliOptions",
                Line = 1,
                StartLine = 1,
                EndLine = 4,
                Signature = "public sealed class CliOptions",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "ShowHelp",
                Line = 3,
                StartLine = 3,
                EndLine = 3,
                Signature = "public bool ShowHelp { get; init; }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "CliOptions",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "ProjectPath",
                Line = 4,
                StartLine = 4,
                EndLine = 4,
                Signature = "public string? ProjectPath { get; init; }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "CliOptions",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: null, lang: "csharp",
            pathPatterns: ["cli_options_fixture.cs"], excludePathPatterns: null, excludeTests: false);

        Assert.Equal("public_or_exported_no_refs", Assert.Single(unused, symbol => symbol.Name == "ShowHelp").UnusedBucket);
        Assert.Equal("public_or_exported_no_refs", Assert.Single(unused, symbol => symbol.Name == "ProjectPath").UnusedBucket);
    }

    [Fact]
    public void GetUnusedSymbols_PrivateHelperWithSameFileUse_IsNotLabeledHighConfidence()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/local_use_fixture.cs",
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
                    public void Run() { Hidden(); }
                    private void Hidden() { }
                }
                """,
            }
        ]);
        _writer.InsertSymbols(
        [
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
                Name = "Hidden",
                Line = 4,
                StartLine = 4,
                EndLine = 4,
                Signature = "private void Hidden() { }",
                Visibility = "private",
                ContainerKind = "class",
                ContainerName = "LocalUseFixture",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: null, lang: "csharp",
            pathPatterns: ["local_use_fixture.cs"], excludePathPatterns: null, excludeTests: false);

        var hidden = Assert.Single(unused, symbol => symbol.Name == "Hidden");
        Assert.Equal("likely_unused_private", hidden.UnusedBucket);
        Assert.Equal("medium", hidden.UnusedConfidence);
        Assert.Contains("same-file uses may still be missed", hidden.UnusedReason);
    }

    [Fact]
    public void GetUnusedSymbols_ReflectionAttributedProperty_IsClassifiedAsSuspect()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_unused_fixture.cs",
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

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: null, lang: "csharp",
            pathPatterns: ["reflection_unused_fixture.cs"], excludePathPatterns: null, excludeTests: false);

        Assert.Equal("public_or_exported_no_refs", Assert.Single(unused, symbol => symbol.Name == "UserDto").UnusedBucket);
        var property = Assert.Single(unused, symbol => symbol.Name == "FullName");
        Assert.Equal("reflection_or_config_suspect", property.UnusedBucket);
        Assert.Contains("attribute-driven reflection surface", property.UnusedReason);
    }

    [Fact]
    public void GetUnusedSymbols_ReflectionAttributedTypes_AreClassifiedAsSuspect()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_type_fixture.cs",
            Lang = "csharp",
            Size = 520,
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
                EndLine = 15,
                Content = """
                using System;
                using System.Text.Json.Serialization;
                using System.ComponentModel.DataAnnotations.Schema;

                [Serializable]
                public class ReflectiveModel { }

                [JsonSerializable(typeof(ApiResponse))]
                public partial class MyJsonContext : JsonSerializerContext { }

                [Table("users")]
                public class UserEntity { }

                [AttributeUsage(AttributeTargets.Class)]
                public class ReflectiveAttribute : Attribute { }
                """,
            }
        ]);
        _writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "ReflectiveModel",
                Line = 6,
                StartLine = 6,
                EndLine = 6,
                Signature = "public class ReflectiveModel { }",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "MyJsonContext",
                Line = 9,
                StartLine = 9,
                EndLine = 9,
                Signature = "public partial class MyJsonContext : JsonSerializerContext { }",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "UserEntity",
                Line = 12,
                StartLine = 12,
                EndLine = 12,
                Signature = "public class UserEntity { }",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "ReflectiveAttribute",
                Line = 15,
                StartLine = 15,
                EndLine = 15,
                Signature = "public class ReflectiveAttribute : Attribute { }",
                Visibility = "public",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: null, lang: "csharp",
            pathPatterns: ["reflection_type_fixture.cs"], excludePathPatterns: null, excludeTests: false);

        Assert.Equal("reflection_or_config_suspect", Assert.Single(unused, symbol => symbol.Name == "ReflectiveModel").UnusedBucket);
        Assert.Equal("reflection_or_config_suspect", Assert.Single(unused, symbol => symbol.Name == "MyJsonContext").UnusedBucket);
        Assert.Equal("reflection_or_config_suspect", Assert.Single(unused, symbol => symbol.Name == "UserEntity").UnusedBucket);
        Assert.Equal("reflection_or_config_suspect", Assert.Single(unused, symbol => symbol.Name == "ReflectiveAttribute").UnusedBucket);
    }

    [Fact]
    public void GetUnusedSymbols_CommonReflectionPropertyAttributes_KeyAndRequired_AreClassifiedAsSuspect()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_property_fixture.cs",
            Lang = "csharp",
            Size = 900,
            Lines = 29,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 29,
                Content = """
                using System;
                using System.ComponentModel.DataAnnotations;
                using Microsoft.AspNetCore.Components;
                using Microsoft.AspNetCore.Mvc;
                using Microsoft.AspNetCore.Mvc.ModelBinding;

                public class Target
                {
                    [Key]
                    public int Id { get; set; }

                    [Required]
                    public string Name { get; set; } = string.Empty;

                    [BindProperty]
                    public string? BoundValue { get; set; }

                    [Parameter]
                    public string? Title { get; set; }

                    [Inject]
                    public IServiceProvider? Services { get; set; }

                    public string? LegacyName { get; set; }

                    [BindNever]
                    public string? IgnoredValue { get; set; }
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
                Name = "Target",
                Line = 7,
                StartLine = 7,
                EndLine = 29,
                Signature = "public class Target",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "Id",
                Line = 10,
                StartLine = 10,
                EndLine = 10,
                Signature = "public int Id { get; set; }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "Target",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "Name",
                Line = 13,
                StartLine = 13,
                EndLine = 13,
                Signature = "public string Name { get; set; } = string.Empty;",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "Target",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "BoundValue",
                Line = 16,
                StartLine = 16,
                EndLine = 16,
                Signature = "public string? BoundValue { get; set; }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "Target",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "Title",
                Line = 19,
                StartLine = 19,
                EndLine = 19,
                Signature = "public string? Title { get; set; }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "Target",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "Services",
                Line = 22,
                StartLine = 22,
                EndLine = 22,
                Signature = "public IServiceProvider? Services { get; set; }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "Target",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "LegacyName",
                Line = 25,
                StartLine = 25,
                EndLine = 25,
                Signature = "public string? LegacyName { get; set; }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "Target",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "IgnoredValue",
                Line = 28,
                StartLine = 28,
                EndLine = 28,
                Signature = "public string? IgnoredValue { get; set; }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "Target",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: null, lang: "csharp",
            pathPatterns: ["reflection_property_fixture.cs"], excludePathPatterns: null, excludeTests: false);

        Assert.Equal("public_or_exported_no_refs", Assert.Single(unused, symbol => symbol.Name == "Target").UnusedBucket);
        Assert.Equal("reflection_or_config_suspect", Assert.Single(unused, symbol => symbol.Name == "Id").UnusedBucket);
        Assert.Equal("reflection_or_config_suspect", Assert.Single(unused, symbol => symbol.Name == "Name").UnusedBucket);
    }

    [Fact]
    public void GetUnusedSymbols_CommonReflectionPropertyAttributes_BindPropertyAndParameter_AreClassifiedAsSuspect()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_property_fixture.cs",
            Lang = "csharp",
            Size = 900,
            Lines = 29,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 29,
                Content = """
                using System;
                using System.ComponentModel.DataAnnotations;
                using Microsoft.AspNetCore.Components;
                using Microsoft.AspNetCore.Mvc;
                using Microsoft.AspNetCore.Mvc.ModelBinding;

                public class Target
                {
                    [Key]
                    public int Id { get; set; }

                    [Required]
                    public string Name { get; set; } = string.Empty;

                    [BindProperty]
                    public string? BoundValue { get; set; }

                    [Parameter]
                    public string? Title { get; set; }

                    [Inject]
                    public IServiceProvider? Services { get; set; }

                    [Obsolete]
                    public string? LegacyName { get; set; }

                    [BindNever]
                    public string? IgnoredValue { get; set; }
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
                Name = "Target",
                Line = 7,
                StartLine = 7,
                EndLine = 29,
                Signature = "public class Target",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "BoundValue",
                Line = 16,
                StartLine = 16,
                EndLine = 16,
                Signature = "public string? BoundValue { get; set; }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "Target",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "Title",
                Line = 19,
                StartLine = 19,
                EndLine = 19,
                Signature = "public string? Title { get; set; }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "Target",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: null, lang: "csharp",
            pathPatterns: ["reflection_property_fixture.cs"], excludePathPatterns: null, excludeTests: false);

        Assert.Equal("reflection_or_config_suspect", Assert.Single(unused, symbol => symbol.Name == "BoundValue").UnusedBucket);
        Assert.Equal("reflection_or_config_suspect", Assert.Single(unused, symbol => symbol.Name == "Title").UnusedBucket);
    }

    [Fact]
    public void GetUnusedSymbols_CommonReflectionPropertyAttributes_BindNever_AreClassifiedAsSuspect()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_property_fixture.cs",
            Lang = "csharp",
            Size = 900,
            Lines = 29,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 29,
                Content = """
                using System;
                using System.ComponentModel.DataAnnotations;
                using Microsoft.AspNetCore.Components;
                using Microsoft.AspNetCore.Mvc;
                using Microsoft.AspNetCore.Mvc.ModelBinding;

                public class Target
                {
                    [Key]
                    public int Id { get; set; }

                    [Required]
                    public string Name { get; set; } = string.Empty;

                    [BindProperty]
                    public string? BoundValue { get; set; }

                    [Parameter]
                    public string? Title { get; set; }

                    public IServiceProvider? Services { get; set; }

                    public string? LegacyName { get; set; }

                    [BindNever]
                    public string? IgnoredValue { get; set; }
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
                Name = "Target",
                Line = 7,
                StartLine = 7,
                EndLine = 27,
                Signature = "public class Target",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "Services",
                Line = 21,
                StartLine = 21,
                EndLine = 21,
                Signature = "public IServiceProvider? Services { get; set; }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "Target",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "LegacyName",
                Line = 23,
                StartLine = 23,
                EndLine = 23,
                Signature = "public string? LegacyName { get; set; }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "Target",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "IgnoredValue",
                Line = 26,
                StartLine = 26,
                EndLine = 26,
                Signature = "public string? IgnoredValue { get; set; }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "Target",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: null, lang: "csharp",
            pathPatterns: ["reflection_property_fixture.cs"], excludePathPatterns: null, excludeTests: false);

        Assert.Equal("public_or_exported_no_refs", Assert.Single(unused, symbol => symbol.Name == "Services").UnusedBucket);
        Assert.Equal("public_or_exported_no_refs", Assert.Single(unused, symbol => symbol.Name == "LegacyName").UnusedBucket);
        Assert.Equal("reflection_or_config_suspect", Assert.Single(unused, symbol => symbol.Name == "IgnoredValue").UnusedBucket);
    }

    [Fact]
    public void GetUnusedSymbols_MultilineReflectionAttribute_IsClassifiedAsSuspect()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_multiline_fixture.cs",
            Lang = "csharp",
            Size = 240,
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
                EndLine = 9,
                Content = """
                using System.Text.Json.Serialization;

                public class UserDto
                {
                    [JsonPropertyName(
                        "full_name")]
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
                EndLine = 7,
                Signature = "public class UserDto",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "FullName",
                Line = 7,
                StartLine = 5,
                EndLine = 7,
                Signature = "public string FullName { get; set; } = string.Empty;",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: null, lang: "csharp",
            pathPatterns: ["reflection_multiline_fixture.cs"], excludePathPatterns: null, excludeTests: false);

        var property = Assert.Single(unused, symbol => symbol.Name == "FullName");
        Assert.Equal("reflection_or_config_suspect", property.UnusedBucket);
    }

    [Fact]
    public void GetUnusedSymbols_InlineAttributeWithBracketInString_DoesNotLeakToAdjacentProperty()
    {
        // Regression for #375 — `[` or `]` inside an attribute string argument must not
        // confuse the bracket-depth scanner. Without the fix, the adjacent plain property
        // below inherited the reflection attribute and flipped into the wrong bucket.
        // #375 回帰: 属性文字列引数内の `[` / `]` が bracket-depth スキャナを乱すと、
        // 直下の属性なしプロパティが誤って reflection 属性を継承して分類が狂う。
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_string_bracket_fixture.cs",
            Lang = "csharp",
            Size = 320,
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

                public class Target
                {
                    [JsonPropertyName("a[")] public string BuggyName { get; set; } = "";

                    public string PlainName { get; set; } = "";
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
                Signature = "[JsonPropertyName(\"a[\")] public string BuggyName { get; set; } = \"\";",
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
            pathPatterns: ["reflection_string_bracket_fixture.cs"], excludePathPatterns: null, excludeTests: false);

        var buggy = Assert.Single(unused, symbol => symbol.Name == "BuggyName");
        Assert.Equal("reflection_or_config_suspect", buggy.UnusedBucket);

        var plain = Assert.Single(unused, symbol => symbol.Name == "PlainName");
        Assert.Equal("public_or_exported_no_refs", plain.UnusedBucket);
    }

    [Theory]
    [InlineData("[JsonPropertyName(\"a[\")] public string Name { get; set; } = \"\";")]
    [InlineData("[JsonPropertyName(\"a]b\")] public string Name { get; set; } = \"\";")]
    [InlineData("[JsonPropertyName(\"]\")] public string Name { get; set; } = \"\";")]
    [InlineData("[JsonPropertyName(@\"a[\")] public string Name { get; set; } = \"\";")]
    [InlineData("[JsonPropertyName(\"\"\"a[\"\"\")] public string Name { get; set; } = \"\";")]
    [InlineData("[JsonPropertyName($\"\"\"a[\"\"\")] public string Name { get; set; } = \"\";")]
    [InlineData("[JsonPropertyName($$\"\"\"a[\"\"\")] public string Name { get; set; } = \"\";")]
    public void GetUnusedSymbols_InlineReflectionAttributeWithBracketInString_IsStillSuspect(string anchor)
    {
        // The inline-declaration line itself must still be recognized as having
        // both an attribute and a declaration, regardless of `[` / `]` in string args.
        // 属性文字列中の `[` / `]` によらず、インライン宣言行自身は
        // 「属性 + 宣言が同じ行にある」と認識されねばならない。
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = $"src/reflection_string_bracket_inline_{anchor.GetHashCode():x8}.cs",
            Lang = "csharp",
            Size = 200,
            Lines = 8,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 7,
                Content = $$"""
                using System.Text.Json.Serialization;

                public class Target
                {
                    {{anchor}}
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
                Name = "Target",
                Line = 3,
                StartLine = 3,
                EndLine = 6,
                Signature = "public class Target",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "Name",
                Line = 5,
                StartLine = 5,
                EndLine = 5,
                Signature = anchor,
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "Target",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: null, lang: "csharp",
            pathPatterns: [$"reflection_string_bracket_inline_{anchor.GetHashCode():x8}.cs"],
            excludePathPatterns: null, excludeTests: false);

        var property = Assert.Single(unused, symbol => symbol.Name == "Name");
        Assert.Equal("reflection_or_config_suspect", property.UnusedBucket);
    }

    [Fact]
    public void GetUnusedSymbols_StandaloneAttributeWithBracketInString_DoesNotLeakToAdjacentDeclaration()
    {
        // Regression extension for #375 — when a standalone attribute line
        // (attribute on its own line, declaration on the next line) contains `[`
        // or `]` inside a string literal, the upward scan must not treat that as
        // an extra bracket and swallow the previous member's attribute block.
        // #375 の追加回帰: 属性単独行 (属性と宣言が別行) の文字列リテラル内の `[` / `]` が
        // 上方スキャンで bracket depth として誤算されると、直前メンバーの属性ブロックまで
        // 吸い込まれて無関係なシンボルに属性が漏れ出す。これを防ぐ。
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_standalone_bracket_fixture.cs",
            Lang = "csharp",
            Size = 400,
            Lines = 14,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 12,
                Content = "using System;\nusing System.Text.Json.Serialization;\n\npublic class Target\n{\n    [JsonPropertyName(\"name\")]\n    public string A { get; set; } = \"\";\n\n    [Obsolete(\"]\")]\n    public string B { get; set; } = \"\";\n}\n",
            }
        ]);
        _writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "Target",
                Line = 4,
                StartLine = 4,
                EndLine = 11,
                Signature = "public class Target",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "A",
                Line = 7,
                StartLine = 6,
                EndLine = 7,
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
                Line = 10,
                StartLine = 9,
                EndLine = 10,
                Signature = "public string B { get; set; } = \"\";",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "Target",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: null, lang: "csharp",
            pathPatterns: ["reflection_standalone_bracket_fixture.cs"], excludePathPatterns: null, excludeTests: false);

        // A sits under a real reflection attribute → suspect.
        // B is a plain property and must not inherit A's reflection attribute through
        // the bracket-leak path.
        // A は本物の reflection 属性の下なので suspect。
        // B は plain property なので、bracket leak で A の reflection 属性を
        // 継承してはならない。
        var a = Assert.Single(unused, symbol => symbol.Name == "A");
        Assert.Equal("reflection_or_config_suspect", a.UnusedBucket);

        var b = Assert.Single(unused, symbol => symbol.Name == "B");
        Assert.Equal("public_or_exported_no_refs", b.UnusedBucket);
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
    public void GetUnusedSymbols_CommentPrefixedInlineAttribute_KeepsReflectionContext()
    {
        // Regression for #409 follow-up (iteration 5) — a line-leading `/* ... */`
        // block comment followed by an inline attribute + declaration — e.g.
        // `/* note */ [JsonPropertyName("ok")] public string A ...` — must keep
        // the reflection context. The anchor's inline-decl check must run against
        // the sanitized line so the leading block comment (blanked by the
        // cross-line sanitizer) does not break the leading-`[` anchor.
        // #409 追加回帰 (iteration 5): 行頭の `/* ... */` ブロックコメント直後に
        // 続くインライン属性 + 宣言（例: `/* note */ [JsonPropertyName("ok")] public string A ...`）は、
        // 対象プロパティが reflection コンテキストを保たなければならない。
        // anchor のインライン宣言判定は sanitize 済み行に対して行い、
        // 行頭ブロックコメントが先頭 `[` アンカーを阻害しないようにする。
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_comment_prefixed_inline_fixture.cs",
            Lang = "csharp",
            Size = 260,
            Lines = 7,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 6,
                Content = """
                using System.Text.Json.Serialization;

                public class Target
                {
                    /* note */ [JsonPropertyName("ok")] public string A { get; set; } = "";
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
                Name = "Target",
                Line = 3,
                StartLine = 3,
                EndLine = 6,
                Signature = "public class Target",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "A",
                Line = 5,
                StartLine = 5,
                EndLine = 5,
                Signature = "public string A { get; set; } = \"\";",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "Target",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: null, lang: "csharp",
            pathPatterns: ["reflection_comment_prefixed_inline_fixture.cs"], excludePathPatterns: null, excludeTests: false);

        var a = Assert.Single(unused, symbol => symbol.Name == "A");
        Assert.Equal("reflection_or_config_suspect", a.UnusedBucket);
    }

    [Fact]
    public void GetUnusedSymbols_AttributeLineWithTrailingLineComment_KeepsReflectionContext()
    {
        // Regression for #409 follow-up — a trailing `// comment` after the closing
        // `]` of an attribute must not flip the following property out of
        // `reflection_or_config_suspect`. The guard that detects inline `[attr] decl`
        // rows must run against sanitized lines so blanked comments do not pose as
        // declaration bodies.
        // #409 追加回帰: 属性行末尾の `// コメント` が、下のプロパティを
        // `reflection_or_config_suspect` から外してはならない。インライン `[attr] decl`
        // 判定は sanitize 済み行に対して行い、消されたコメントが宣言本体と誤認されないこと。
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_trailing_comment_fixture.cs",
            Lang = "csharp",
            Size = 280,
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
                EndLine = 9,
                Content = """
                using System.Text.Json.Serialization;

                public class Target
                {
                    [JsonPropertyName("ok")] // trailing comment
                    public string C { get; set; } = "";
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
                Name = "Target",
                Line = 3,
                StartLine = 3,
                EndLine = 7,
                Signature = "public class Target",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "C",
                Line = 6,
                StartLine = 6,
                EndLine = 6,
                Signature = "public string C { get; set; } = \"\";",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "Target",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: null, lang: "csharp",
            pathPatterns: ["reflection_trailing_comment_fixture.cs"], excludePathPatterns: null, excludeTests: false);

        var c = Assert.Single(unused, symbol => symbol.Name == "C");
        Assert.Equal("reflection_or_config_suspect", c.UnusedBucket);
    }

    [Fact]
    public void GetUnusedSymbols_AttributeLineWithTrailingBlockComment_KeepsReflectionContext()
    {
        // Regression for #409 follow-up — a trailing `/* ... */` block comment
        // after the closing `]` of an attribute must not flip the following
        // property out of `reflection_or_config_suspect`. The previous
        // BuildTriviaMask heuristic flagged any line containing `*/` as trivia,
        // so the `[JsonPropertyName(...)] /* note */` row was skipped by
        // FindPreviousNonTriviaLine and the real attribute block was lost.
        // #409 追加回帰: 属性行末尾の `/* ... */` ブロックコメントが、下のプロパティを
        // `reflection_or_config_suspect` から外してはならない。以前の BuildTriviaMask は
        // `*/` を含むだけで trivia 判定していたため、`[JsonPropertyName(...)] /* note */`
        // の行が FindPreviousNonTriviaLine に飛ばされ、本来の属性ブロックが失われていた。
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_trailing_block_comment_fixture.cs",
            Lang = "csharp",
            Size = 300,
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
                EndLine = 9,
                Content = """
                using System.Text.Json.Serialization;

                public class Target
                {
                    [JsonPropertyName("ok")] /* trailing block comment */
                    public string D { get; set; } = "";
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
                Name = "Target",
                Line = 3,
                StartLine = 3,
                EndLine = 7,
                Signature = "public class Target",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "D",
                Line = 6,
                StartLine = 6,
                EndLine = 6,
                Signature = "public string D { get; set; } = \"\";",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "Target",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: null, lang: "csharp",
            pathPatterns: ["reflection_trailing_block_comment_fixture.cs"], excludePathPatterns: null, excludeTests: false);

        var d = Assert.Single(unused, symbol => symbol.Name == "D");
        Assert.Equal("reflection_or_config_suspect", d.UnusedBucket);
    }

    [Fact]
    public void GetUnusedSymbols_MultilineAttributeWithEmbeddedBlockCommentMentioningIgnoreAttribute_KeepsReflectionContext()
    {
        // Regression for #409 follow-up — a multi-line block comment embedded
        // inside an attribute list must not leak phantom attribute names from
        // its body. The closing comment line `[JsonIgnore] */` would otherwise
        // survive BuildSingleLineTrivia as real text, and the phantom
        // `JsonIgnore` would cancel the real `JsonPropertyName`, flipping the
        // property out of `reflection_or_config_suspect`.
        // #409 追加回帰: 属性リスト内に埋め込まれた複数行ブロックコメントの本体が
        // 擬似的な属性名を ExtractNormalizedAttributeNames に漏らしてはならない。
        // コメント閉じ行 `[JsonIgnore] */` がそのまま BuildSingleLineTrivia を通過すると、
        // 幻の `JsonIgnore` が本物の `JsonPropertyName` を打ち消し、プロパティが
        // `reflection_or_config_suspect` から外れてしまう。
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_embedded_block_comment_fixture.cs",
            Lang = "csharp",
            Size = 360,
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
                EndLine = 11,
                Content = """
                using System.Text.Json.Serialization;

                public class Target
                {
                    [
                        /* explanation
                           [JsonIgnore] */
                        JsonPropertyName("ok")
                    ]
                    public string E { get; set; } = "";
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
                Name = "Target",
                Line = 3,
                StartLine = 3,
                EndLine = 11,
                Signature = "public class Target",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "E",
                Line = 10,
                StartLine = 10,
                EndLine = 10,
                Signature = "public string E { get; set; } = \"\";",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "Target",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: null, lang: "csharp",
            pathPatterns: ["reflection_embedded_block_comment_fixture.cs"], excludePathPatterns: null, excludeTests: false);

        var e = Assert.Single(unused, symbol => symbol.Name == "E");
        Assert.Equal("reflection_or_config_suspect", e.UnusedBucket);
    }

    [Fact]
    public void GetUnusedSymbols_CommentBetweenAttributeAndProperty_IsClassifiedAsSuspect()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_comment_fixture.cs",
            Lang = "csharp",
            Size = 260,
            Lines = 11,
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
                    /// Bound from JSON payload.
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
                EndLine = 7,
                Signature = "public class UserDto",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "FullName",
                Line = 7,
                StartLine = 7,
                EndLine = 7,
                Signature = "public string FullName { get; set; } = string.Empty;",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: null, lang: "csharp",
            pathPatterns: ["reflection_comment_fixture.cs"], excludePathPatterns: null, excludeTests: false);

        var property = Assert.Single(unused, symbol => symbol.Name == "FullName");
        Assert.Equal("reflection_or_config_suspect", property.UnusedBucket);
    }

    [Fact]
    public void GetUnusedSymbols_QualifiedAndSuffixedAttributes_AreClassifiedCorrectly()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_qualified_fixture.cs",
            Lang = "csharp",
            Size = 420,
            Lines = 14,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 13,
                Content = """
                using System.Text.Json.Serialization;

                public class UserDto
                {
                    [global::System.Text.Json.Serialization.JsonPropertyName("full_name")]
                    public string QualifiedName { get; set; } = string.Empty;

                    [JsonPropertyNameAttribute("display_name")]
                    public string SuffixedName { get; set; } = string.Empty;

                    [System.Text.Json.Serialization.JsonIgnoreAttribute]
                    public string IgnoredName { get; set; } = string.Empty;
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
                EndLine = 12,
                Signature = "public class UserDto",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "QualifiedName",
                Line = 6,
                StartLine = 6,
                EndLine = 6,
                Signature = "public string QualifiedName { get; set; } = string.Empty;",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "SuffixedName",
                Line = 9,
                StartLine = 9,
                EndLine = 9,
                Signature = "public string SuffixedName { get; set; } = string.Empty;",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "IgnoredName",
                Line = 12,
                StartLine = 12,
                EndLine = 12,
                Signature = "public string IgnoredName { get; set; } = string.Empty;",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: null, lang: "csharp",
            pathPatterns: ["reflection_qualified_fixture.cs"], excludePathPatterns: null, excludeTests: false);

        Assert.Equal("reflection_or_config_suspect", Assert.Single(unused, symbol => symbol.Name == "QualifiedName").UnusedBucket);
        Assert.Equal("reflection_or_config_suspect", Assert.Single(unused, symbol => symbol.Name == "SuffixedName").UnusedBucket);
        Assert.Equal("public_or_exported_no_refs", Assert.Single(unused, symbol => symbol.Name == "IgnoredName").UnusedBucket);
    }

    [Fact]
    public void GetUnusedSymbols_BlockCommentBetweenAttributeAndProperty_IsClassifiedAsSuspect()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_block_comment_fixture.cs",
            Lang = "csharp",
            Size = 320,
            Lines = 11,
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
                    /* bound from payload
                       via serializer */
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
                EndLine = 8,
                Signature = "public class UserDto",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "FullName",
                Line = 8,
                StartLine = 8,
                EndLine = 8,
                Signature = "public string FullName { get; set; } = string.Empty;",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: null, lang: "csharp",
            pathPatterns: ["reflection_block_comment_fixture.cs"], excludePathPatterns: null, excludeTests: false);

        var property = Assert.Single(unused, symbol => symbol.Name == "FullName");
        Assert.Equal("reflection_or_config_suspect", property.UnusedBucket);
    }

    [Fact]
    public void GetUnusedSymbols_LargePublicLimit_IsNotCappedAtBudget()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/large_public_unused_fixture.cs",
            Lang = "csharp",
            Size = 16000,
            Lines = 2600,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 1,
                Content = "public class PublicNoise0000 { }",
            }
        ]);

        var symbols = new List<SymbolRecord>();
        for (var i = 0; i < 2500; i++)
        {
            symbols.Add(new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = $"PublicNoise{i:D4}",
                Line = i + 1,
                StartLine = i + 1,
                EndLine = i + 1,
                Signature = $"public class PublicNoise{i:D4} {{ }}",
                Visibility = "public",
            });
        }
        _writer.InsertSymbols(symbols);

        var unused = _reader.GetUnusedSymbols(limit: 3000, kind: null, lang: "csharp",
            pathPatterns: ["large_public_unused_fixture.cs"], excludePathPatterns: null, excludeTests: false);

        Assert.Equal(2500, unused.Count);
    }

    [Fact]
    public void GetUnusedSymbols_UnsupportedLanguageReturnsEmpty()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "script.txt",
            Lang = "text",
            Size = 64,
            Lines = 4,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "helper",
                Line = 1,
                StartLine = 1,
                EndLine = 3,
                Signature = "helper() {",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 20, kind: null, lang: "shell",
            pathPatterns: null, excludePathPatterns: null, excludeTests: false);
        var count = _reader.CountUnusedSymbols(kind: null, lang: "shell",
            pathPatterns: null, excludePathPatterns: null, excludeTests: false);

        Assert.Empty(unused);
        Assert.Equal(0, count.Count);
        Assert.Equal(0, count.FileCount);
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
                Name = "Hidden",
                Line = 4,
                StartLine = 4,
                EndLine = 4,
                Signature = "private void Hidden() { }",
                Visibility = "private",
                ContainerKind = "class",
                ContainerName = "LocalUseFixture",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "InternalOnly",
                Line = 5,
                StartLine = 5,
                EndLine = 5,
                Signature = "internal void InternalOnly() { }",
                Visibility = "internal",
                ContainerKind = "class",
                ContainerName = "LocalUseFixture",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(limit: 3, kind: null, lang: "csharp",
            pathPatterns: ["diversified_unused_fixture.cs"], excludePathPatterns: null, excludeTests: false);

        Assert.Equal(["Hidden", "InternalOnly", "LocalUseFixture"], unused.Select(symbol => symbol.Name).ToArray());
        Assert.Equal(["likely_unused_private", "maybe_unused_nonpublic", "public_or_exported_no_refs"], unused.Select(symbol => symbol.UnusedBucket).ToArray());
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

        Assert.Equal(["Hidden", "InternalOnly", "UserDto", "FullName"], unused.Select(symbol => symbol.Name).ToArray());
        Assert.Equal(["likely_unused_private", "maybe_unused_nonpublic", "public_or_exported_no_refs", "reflection_or_config_suspect"], unused.Select(symbol => symbol.UnusedBucket).ToArray());
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
            Path = "src/unused_null.cs", Lang = "csharp", Size = 100, Lines = 10,
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
            try { Directory.Delete(tempDir, recursive: true); } catch { }
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
