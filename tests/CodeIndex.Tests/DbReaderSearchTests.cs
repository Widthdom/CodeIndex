using System.Reflection;
using System.Text;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

public partial class DbReaderTests
{
    [Fact]
    public void Search_ExplicitPrefixMatchesLatinDiacriticToken()
    {
        InsertIndexedFile("src/cafe.md", "markdown", "menu café_au_lait\n");

        var results = _reader.Search("café*", lang: "markdown");

        Assert.Contains(results, r => r.Path == "src/cafe.md");
    }

    [Fact]
    public void Search_CredentialContextDoesNotPenalizeSeparateCancellationTokenParameter_Issue4590()
    {
        const string credentialMethod =
            "public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) => FetchCredentialAsync(cancellationToken);";
        const string separateParameterMethod =
            "public Task SendAsync(string accessToken, CancellationToken cancellationToken) => ForwardCredential(accessToken);";
        const string structuralTokenApi =
            "public Task AccessAsync(CancellationToken token) => ObserveAsync(token);";
        Assert.False(DbReader.ContainsRelevantStructuralTokenMarker(
            credentialMethod,
            ["access", "token"]));
        Assert.False(DbReader.ContainsRelevantStructuralTokenMarker(
            separateParameterMethod,
            ["access", "token"]));
        Assert.True(DbReader.ContainsRelevantStructuralTokenMarker(
            structuralTokenApi,
            ["access", "token"]));
        Assert.True(DbReader.ContainsRelevantStructuralTokenMarker(
            "private readonly SyntaxToken accessTokenSyntax;",
            ["access", "token"]));
        Assert.False(DbReader.ContainsRelevantStructuralTokenMarker(
            "private string ConnectionMultiplexerAccessToken;",
            ["access", "token"]));
        const string headerCheck =
            "if (!cancellationToken.IsCancellationRequested && request.Headers.Authorization != null) return;";
        Assert.False(DbReader.ContainsRelevantStructuralTokenMarker(
            headerCheck,
            ["Authorization"]));

        InsertIndexedFile(
            "src/request-sender.cs",
            "csharp",
            $$"""
            public sealed class RequestSender
            {
                // access
                // token
                {{credentialMethod}}
            }
            """);
        InsertIndexedFile(
            "src/loose-access-terms.cs",
            "csharp",
            "public sealed class LooseTerms { public void Observe() { var access = ReadAccess(); Console.WriteLine(token); } }\n");
        InsertIndexedFile(
            "src/structural-access-token.cs",
            "csharp",
            "private readonly SyntaxToken accessTokenSyntax;\n");
        InsertIndexedFile(
            "src/structural-access-api.cs",
            "csharp",
            $$"""
            // access
            // token
            {{structuralTokenApi}}
            """);
        InsertIndexedFile(
            "src/multiplexer-access-token.cs",
            "csharp",
            "private string ConnectionMultiplexerAccessToken = ReadCredential();\n");

        var results = _reader.Search(
            "access token",
            limit: 5,
            resultRanking: SearchResultRanking.CredentialContext);

        Assert.Equal("src/request-sender.cs", results[0].Path);
        Assert.True(
            results.FindIndex(result => result.Path == "src/request-sender.cs") <
            results.FindIndex(result => result.Path == "src/loose-access-terms.cs"));
        Assert.True(
            results.FindIndex(result => result.Path == "src/request-sender.cs") <
            results.FindIndex(result => result.Path == "src/structural-access-api.cs"));
        Assert.True(
            results.FindIndex(result => result.Path == "src/multiplexer-access-token.cs") <
            results.FindIndex(result => result.Path == "src/loose-access-terms.cs"));

        InsertIndexedFile(
            "src/request-header-check.cs",
            "csharp",
            $$"""
            public sealed class RequestHeaderCheck
            {
                public void Apply(CancellationToken cancellationToken, HttpRequestMessage request)
                {
                    {{headerCheck}}
                }
            }
            """);
        InsertIndexedFile(
            "src/header-name.cs",
            "csharp",
            "public static class HeaderNames { private const string HeaderName = \"Authorization\"; }\n");

        var authorizationResults = _reader.Search(
            "Authorization",
            limit: 2,
            resultRanking: SearchResultRanking.CredentialContext);

        Assert.Equal("src/request-header-check.cs", authorizationResults[0].Path);
    }

    [Fact]
    public void Search_CredentialContextUsesIdentifierBoundariesWhenCouplingTerms_Issue4590()
    {
        InsertIndexedFile(
            "src/a-domain-token.cs",
            "csharp",
            "public void MergeValues() { var capitalToken = Merge(api, fallback, token); }\n");
        InsertIndexedFile(
            "src/z-credential.cs",
            "csharp",
            "public sealed class CredentialStore { private string ApiToken => ReadCredential(\"api\", \"token\"); }\n");

        var results = _reader.Search(
            "api token",
            limit: 2,
            resultRanking: SearchResultRanking.CredentialContext);

        Assert.Equal("src/z-credential.cs", results[0].Path);
    }

    [Fact]
    public void Search_CredentialContextScopesRegexPenaltyAndSymbolToWinningMatch_Issue4590()
    {
        InsertIndexedFile(
            "src/mixed-authorization.cs",
            "csharp",
            """
            public sealed class AuthorizationHandler
            {
                private static Regex AuthorizationRegex => new("Authorization");

                public void Apply(HttpRequestMessage request, AuthenticationHeaderValue credential)
                {
                    request.Headers.Authorization = credential;
                }
            }
            """);
        InsertIndexedFile(
            "src/header-string.cs",
            "csharp",
            "public static class HeaderStrings { private const string HeaderName = \"Authorization\"; }\n");

        var results = _reader.Search(
            "Authorization",
            limit: 2,
            resultRanking: SearchResultRanking.CredentialContext);

        Assert.Equal("src/mixed-authorization.cs", results[0].Path);
        Assert.Equal("Apply", results[0].EnclosingSymbolName);
    }

    [Fact]
    public void Search_CredentialContextKeepsCandidateUniverseStableAcrossCursorPages_Issue4590()
    {
        const string decoy = "// github token github token github token github token github token\n";
        for (var i = 0; i < 210; i++)
            InsertIndexedFile($"src/page-noise-{i:D3}.cs", "csharp", decoy);

        var filler = string.Join(' ', Enumerable.Repeat("neutral", 300));
        InsertIndexedFile(
            "src/page-credential.cs",
            "csharp",
            $$"""
            public sealed class PageCredential
            {
                private string GitHubToken => ReadCredential("github", "token");
            }
            // {{filler}}
            """);

        var databaseOrder = _reader.Search("github token", limit: 250, deduplicate: false);
        var credentialDatabaseIndex = databaseOrder.FindIndex(result => result.Path == "src/page-credential.cs");
        Assert.InRange(credentialDatabaseIndex, 200, 249);

        var firstPage = _reader.Search(
            "github token",
            limit: 4,
            resultRanking: SearchResultRanking.CredentialContext);
        var secondPage = _reader.Search(
            "github token",
            limit: 4,
            cursor: new SearchCursor(0, 0, 4),
            resultRanking: SearchResultRanking.CredentialContext);

        Assert.Equal("src/page-credential.cs", firstPage[0].Path);
        Assert.Equal(4, firstPage.Count);
        Assert.Equal(4, secondPage.Count);
        Assert.DoesNotContain(secondPage, second =>
            firstPage.Any(first => first.Path == second.Path && first.ChunkId == second.ChunkId));
    }

    [Fact]
    public void Search_CredentialContextHonorsSupportedLimitAboveLegacyCandidateCap_Issue4590()
    {
        Assert.Equal(QueryCommandRunner.MaxQueryResultLimit, DbReader.MaxContextRankingCandidates);
        const int candidateCount = 1001;
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/large-auth-audit.cs",
            Lang = "csharp",
            Size = candidateCount * 40,
            Lines = candidateCount,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks(Enumerable.Range(0, candidateCount)
            .Select(index => new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = index,
                StartLine = index + 1,
                EndLine = index + 1,
                Content = $"// github token candidate {index}\n",
            })
            .ToList());

        var results = _reader.Search(
            "github token",
            limit: candidateCount,
            deduplicate: false,
            resultRanking: SearchResultRanking.CredentialContext);

        Assert.Equal(candidateCount, results.Count);
    }

    [Fact]
    public void Search_CredentialContextScoresRankingRuleSymbolsBeyondFirstTwoHundredCandidates_Issue4590()
    {
        for (var i = 0; i < 200; i++)
        {
            InsertIndexedFile(
                $"src/symbol-decoy-{i:D3}.cs",
                "csharp",
                $$"""
                public sealed class CancellationTokenRegexContainer{{i}}
                {
                    public string Authorization => credential;
                }
                """);
        }

        var filler = string.Join(' ', Enumerable.Repeat("neutral", 300));
        InsertIndexedFile(
            "src/late-ranking-rules.cs",
            "csharp",
            $$"""
            public static class RankingRules
            {
                private static bool ContainsCredentialUseSyntax(string text)
                    => text.Contains("Authorization");
            }
            // {{filler}}
            """);

        var databaseOrder = _reader.Search("Authorization", limit: 250, deduplicate: false);
        Assert.InRange(
            databaseOrder.FindIndex(result => result.Path == "src/late-ranking-rules.cs"),
            200,
            249);

        var ranked = _reader.Search(
            "Authorization",
            limit: 250,
            deduplicate: false,
            resultRanking: SearchResultRanking.CredentialContext);

        Assert.NotEqual("src/late-ranking-rules.cs", ranked[0].Path);
        Assert.True(ranked.FindIndex(result => result.Path == "src/late-ranking-rules.cs") > 0);
    }

    [Fact]
    public void Search_GuardFiltersReadAcrossChunkBoundaries_Issue2852()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/chunked.cs",
            Lang = "csharp",
            Size = 128,
            Lines = 5,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 3,
                Content = "public void Guarded(string path)\n{\n    var length = new FileInfo(path).Length;",
            },
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 1,
                StartLine = 4,
                EndLine = 5,
                Content = "    var text = File.ReadAllText(path);\n}",
            },
        ]);

        var requireResults = _reader.Search(
            "File.ReadAllText",
            exact: true,
            pathPatterns: ["src/chunked.cs"],
            guardFilters: [new SearchGuardFilter(SearchGuardRole.Require, SearchGuardDirection.Before, "Length")],
            guardWindow: 2);
        var rejectResults = _reader.Search(
            "File.ReadAllText",
            exact: true,
            pathPatterns: ["src/chunked.cs"],
            guardFilters: [new SearchGuardFilter(SearchGuardRole.Reject, SearchGuardDirection.Before, "Length")],
            guardWindow: 2);
        var cursorResults = _reader.Search(
            "File.ReadAllText",
            exact: true,
            pathPatterns: ["src/chunked.cs"],
            cursor: new SearchCursor(0, 0, 0),
            guardFilters: [new SearchGuardFilter(SearchGuardRole.Require, SearchGuardDirection.Before, "Length")],
            guardWindow: 2);

        var result = Assert.Single(requireResults);
        Assert.Equal(4, result.StartLine);
        var evidence = Assert.Single(result.GuardEvidence!);
        Assert.Equal(3, evidence.Line);
        Assert.Empty(rejectResults);
        Assert.Single(cursorResults);
    }

    [Fact]
    public void Search_EnclosingSymbolUsesPreparedMatchLineContext_Issue3086()
    {
        var filler = string.Join('\n', Enumerable.Range(1, 2_000).Select(i => $"        // filler {i}"));
        InsertIndexedFile(
            "src/enclosing-large.cs",
            "csharp",
            $$"""
            namespace Demo;
            public class Worker
            {
                public void Run()
                {
            {{filler}}
                    EnclosingNeedle();
                }
            }
            """);

        var results = _reader.Search(
            "EnclosingNeedle",
            exact: true,
            pathPatterns: ["src/enclosing-large.cs"],
            limit: 1);

        var result = Assert.Single(results);
        Assert.Equal("Run", result.EnclosingSymbolName);
        Assert.Equal("function", result.EnclosingSymbolKind);
    }

    [Theory]
    [InlineData("rowid:authenticate", "rowid:")]
    [InlineData("title:authenticate", "title:")]
    [InlineData("{title}:authenticate", "title:")]
    [InlineData("{rowid title}:authenticate", "rowid:")]
    public void Search_RawFtsRejectsUnknownColumnQualifiers(string query, string expectedQualifier)
    {
        var ex = Assert.Throws<FtsQuerySyntaxException>(() => _reader.Search(query, rawQuery: true));

        Assert.Equal(FtsQuerySyntaxErrorKind.ColumnQualifier, ex.Kind);
        Assert.Contains(expectedQualifier, ex.Message);
        Assert.Contains("'content' column", ex.Message);
    }

    [Fact]
    public void Search_RawFtsAllowsContentColumnQualifier()
    {
        var results = _reader.Search("content:authenticate", rawQuery: true);

        Assert.Contains(results, r => r.Path == "src/auth.py");
    }

    [Fact]
    public void Search_RawFtsAllowsContentColumnListQualifier()
    {
        var results = _reader.Search("{content}:authenticate", rawQuery: true);

        Assert.Contains(results, r => r.Path == "src/auth.py");
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
    public void Search_ReturnsEnclosingSymbolMetadata_Issue2838()
    {
        const string token = "issue2838_unique_needle";
        InsertIndexedFile("src/issue2838/SearchContainer.cs", "csharp",
            $$"""
            namespace Issue2838;

            public sealed class SearchContainer
            {
                public void Run()
                {
                    var message = "{{token}}";
                }
            }
            """);

        var result = Assert.Single(_reader.Search(token, lang: "csharp")
            .Where(result => result.Path == "src/issue2838/SearchContainer.cs"));

        Assert.Equal("Run", result.EnclosingSymbolName);
        Assert.Equal("function", result.EnclosingSymbolKind);
        Assert.Equal("SearchContainer", result.EnclosingContainerName);
        Assert.True(result.EnclosingSymbolStartLine > 0);
        Assert.True(result.EnclosingSymbolEndLine >= result.EnclosingSymbolStartLine);
    }

    [Fact]
    public void Search_ReturnsEnclosingSymbolForActualMatchLine_Issue2838()
    {
        const string token = "issue2838_multifunction_needle";
        InsertIndexedFile("src/issue2838/MultiFunctionSearch.cs", "csharp",
            $$"""
            namespace Issue2838;

            public sealed class MultiFunctionSearch
            {
                public void Tiny() { }

                public void Larger()
                {
                    var message = "{{token}}";
                    Console.WriteLine(message);
                }
            }
            """);

        var result = Assert.Single(_reader.Search(token, lang: "csharp")
            .Where(result => result.Path == "src/issue2838/MultiFunctionSearch.cs"));

        Assert.Equal("Larger", result.EnclosingSymbolName);
        Assert.Equal("function", result.EnclosingSymbolKind);
        Assert.Equal("MultiFunctionSearch", result.EnclosingContainerName);
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
    public void SearchSymbols_PublicVisibilityFilterMatchesLanguageAliases()
    {
        InsertIndexedFile(
            "src/visibility.rs",
            "rust",
            """
            pub fn exported_fn() {}
            fn private_fn() {}
            """);

        var publicResults = _reader.SearchSymbols(
            lang: "rust",
            visibilityFilters: ["public"]);
        var nonPublicResults = _reader.SearchSymbols(
            lang: "rust",
            excludeVisibilityFilters: ["public"]);

        Assert.Contains(publicResults, result => result.Name == "exported_fn" && result.Visibility == "pub");
        Assert.DoesNotContain(publicResults, result => result.Name == "private_fn");
        Assert.Contains(nonPublicResults, result => result.Name == "private_fn");
        Assert.DoesNotContain(nonPublicResults, result => result.Name == "exported_fn");
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
    public void Search_PrefersSourceFilesOverTests()
    {
        var testFileId = _writer.UpsertFile(new FileRecord
        {
            Path = "tests/auth_test.py",
            Lang = "python",
            Size = 300,
            Lines = 10,
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
    public void Search_DeduplicatesFullyCoveredChunk()
    {
        // Create two chunks in the same file where the lower-ranked match is fully covered.
        // 同じファイル内で低順位のマッチが完全包含される2チャンクを作成。
        var overlapFileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/overlap.py",
            Lang = "python",
            Size = 2000,
            Lines = 100,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        var duplicateContent = "# overlap_marker\ndef func_a():\n    pass\n" + string.Concat(Enumerable.Repeat("# filler\n", 76));
        _writer.InsertChunks([
            new ChunkRecord { FileId = overlapFileId, ChunkIndex = 0, StartLine = 1, EndLine = 80, Content = duplicateContent },
            new ChunkRecord { FileId = overlapFileId, ChunkIndex = 1, StartLine = 71, EndLine = 80, Content = duplicateContent },
        ]);

        var results = _reader.Search("overlap_marker", limit: 10);

        // Should deduplicate: only 1 result from overlap.py because the second range is covered.
        // 重複排除: 2件目の範囲は包含済みなので overlap.py からは1件のみ。
        var overlapResults = results.Where(r => r.Path == "src/overlap.py").ToList();
        Assert.Single(overlapResults);
    }

    [Fact]
    public void Search_TiedChunksUseStableChunkIdOrder()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/tied_chunks.py",
            Lang = "python",
            Size = 3000,
            Lines = 260,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks([
            new ChunkRecord { FileId = fileId, ChunkIndex = 0, StartLine = 1, EndLine = 20, Content = "stable_tie_marker\n" },
            new ChunkRecord { FileId = fileId, ChunkIndex = 1, StartLine = 101, EndLine = 120, Content = "stable_tie_marker\n" },
            new ChunkRecord { FileId = fileId, ChunkIndex = 2, StartLine = 201, EndLine = 220, Content = "stable_tie_marker\n" },
        ]);

        var first = _reader.Search("stable_tie_marker", limit: 10)
            .Where(r => r.Path == "src/tied_chunks.py")
            .Select(r => (r.Path, r.StartLine, r.EndLine, r.Content))
            .ToArray();

        Assert.Equal([1, 101, 201], first.Select(r => r.StartLine).ToArray());

        for (var i = 0; i < 10; i++)
        {
            var next = _reader.Search("stable_tie_marker", limit: 10)
                .Where(r => r.Path == "src/tied_chunks.py")
                .Select(r => (r.Path, r.StartLine, r.EndLine, r.Content))
                .ToArray();

            Assert.Equal(first, next);
        }
    }

    [Fact]
    public void Search_PrefersDefinitionFileOverReferenceOnlySourceFile()
    {
        var refFileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/session.py",
            Lang = "python",
            Size = 300,
            Lines = 10,
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
    public void Search_DeduplicationKeepsOverlappingChunkWithNewCoverage()
    {
        var content = string.Join('\n', Enumerable.Range(1, 30).Select(i => i switch
        {
            5 => "needle first hit",
            25 => "needle second hit",
            _ => $"line {i}",
        }));
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/overlapping_chunks.py",
            Lang = "python",
            Size = content.Length,
            Lines = 30,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        var lines = content.Split('\n');
        _writer.InsertChunks([
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 20,
                Content = string.Join('\n', lines.Take(20)),
            },
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 1,
                StartLine = 11,
                EndLine = 30,
                Content = string.Join('\n', lines.Skip(10)),
            },
        ]);

        var results = _reader.Search("needle")
            .Where(r => r.Path == "src/overlapping_chunks.py")
            .ToList();

        Assert.Equal([1, 11], results.Select(r => r.StartLine).OrderBy(line => line));

        var count = _reader.CountSearchResults("needle");
        Assert.Equal(2, count.Count);
        Assert.Equal(1, count.FileCount);
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

    [Theory]
    [InlineData("NEAR(auth login, 101)")]
    [InlineData("NEAR(1000000)")]
    [InlineData("near(auth login, -1)")]
    [InlineData("NEAR(auth login, 999999999999)")]
    [InlineData("NEAR(auth login, 999999999999999999999999999999999)")]
    public void Search_RawQueryRejectsOutOfRangeNearDistance_Issue2089(string query)
    {
        var ex = Assert.Throws<FtsQuerySyntaxException>(() => _reader.Search(query, rawQuery: true));

        Assert.Contains("NEAR distance must be between 0 and 100", ex.Message);
    }

    [Theory]
    [InlineData("NEAR(auth login, 100)")]
    [InlineData("auth NEAR login")]
    [InlineData("\"NEAR(auth login, 1000000)\"")]
    public void Search_RawQueryAllowsBoundedNearSyntax_Issue2089(string query)
    {
        var ex = Record.Exception(() => _reader.Search(query, rawQuery: true));

        Assert.False(ex is FtsQuerySyntaxException);
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
    public void SearchSymbols_FindsByName()
    {
        var results = _reader.SearchSymbols("authenticate");
        Assert.Single(results);
        Assert.Equal("function", results[0].Kind);
        Assert.Equal("src/auth.py", results[0].Path);
    }

    [Fact]
    public void SearchSymbols_BreaksSameLineTiesByStartColumn()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/same_line_symbols.cs",
            Lang = "csharp",
            Size = 100,
            Lines = 1,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertSymbols(
        [
            new SymbolRecord { FileId = fileId, Kind = "function", Name = "SameLine", Line = 1, StartLine = 1, StartColumn = 30, EndLine = 1, Signature = "late SameLine()" },
            new SymbolRecord { FileId = fileId, Kind = "function", Name = "SameLine", Line = 1, StartLine = 1, StartColumn = 10, EndLine = 1, Signature = "early SameLine()" },
        ]);

        var results = _reader.SearchSymbols("SameLine", kind: "function", lang: "csharp", exact: true, pathPatterns: ["src/same_line_symbols.cs"]);

        Assert.Equal(["early SameLine()", "late SameLine()"], results.Select(result => result.Signature).ToArray());
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

        Assert.Single(_reader.SearchSymbols("operator +", kind: "operator", lang: "csharp", exact: true, pathPatterns: ["src/*csharp_special_names*"]));
        Assert.Single(_reader.SearchSymbols("operator -", kind: "operator", lang: "csharp", exact: true, pathPatterns: ["src/*csharp_special_names*"]));
        Assert.Single(_reader.SearchSymbols("operator checked +", kind: "operator", lang: "csharp", exact: true, pathPatterns: ["src/*csharp_special_names*"]));
        Assert.Single(_reader.SearchSymbols("implicit operator decimal", kind: "operator", lang: "csharp", exact: true, pathPatterns: ["src/*csharp_special_names*"]));
        Assert.Single(_reader.SearchSymbols("explicit operator Money", kind: "operator", lang: "csharp", exact: true, pathPatterns: ["src/*csharp_special_names*"]));
        Assert.Single(_reader.SearchSymbols("explicit operator checked byte", kind: "operator", lang: "csharp", exact: true, pathPatterns: ["src/*csharp_special_names*"]));
        Assert.Single(_reader.SearchSymbols("explicit operator Dictionary<string,int>", kind: "operator", lang: "csharp", exact: true, pathPatterns: ["src/*csharp_special_names*"]));
        Assert.Single(_reader.SearchSymbols("explicit operator (int whole,int cents)", kind: "operator", lang: "csharp", exact: true, pathPatterns: ["src/*csharp_special_names*"]));
        Assert.Single(_reader.SearchSymbols("explicit operator (int[] items, int count)", kind: "operator", lang: "csharp", exact: true, pathPatterns: ["src/*csharp_special_names*"]));
        Assert.Single(_reader.SearchSymbols("Money", kind: "function", lang: "csharp", exact: true, pathPatterns: ["src/*csharp_special_names*"]));
        Assert.Single(_reader.SearchSymbols("Item", kind: "function", lang: "csharp", exact: true, pathPatterns: ["src/*csharp_special_names*"]));
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
    public void SearchSymbols_ExplicitInterfaceExactIdentityAndShortAliasStayDistinct_Issue4866()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_explicit_interface_identity_4866");
        var dbPath = Path.Combine(project.Root, "codeindex.db");
        using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
        db.InitializeSchema();
        var writer = new DbWriter(db.Connection);
        const string path = "src/ExplicitMembers.cs";
        const string content = """
            namespace Demo;

            public interface IFoo
            {
                void Run<T>(T value);
                int Value { get; }
                event System.EventHandler Changed;
                string this[int index] { get; }
                void Ä();
                void @this();
                void Verify();
            }

            public interface IBar
            {
                void Run<TLeft, TRight>(TLeft left, TRight right);
            }

            public interface IItemContract
            {
                int Item { get; }
            }

            public sealed class Service : IFoo, IBar, IItemContract
            {
                void IFoo.Run<TValue>(TValue value) { ExplicitHelper(); }
                void IBar.Run<TLeft, TRight>(TLeft left, TRight right) { }
                int IFoo.Value => 1;
                event System.EventHandler IFoo . Changed { add { } remove { } }
                string IFoo . this[int index] => index.ToString();
                int IItemContract . Item => 2;
                void IFoo.Ä() { }
                void IFoo.@this() { }
                [Fact]
                void IFoo.Verify() { }
                public void Run<T>(T value) { }
                public void ExplicitHelper() { }
                public void CallPublicRun() { Run(1); }
            }

            public sealed class ArrayFactory
            {
                public Demo.Service[] Service() => [];
            }
            """;
        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = path,
            Lang = "csharp",
            Size = content.Length,
            Lines = content.Count(ch => ch == '\n') + 1,
            Modified = new DateTime(2026, 7, 29, 0, 0, 0, DateTimeKind.Utc),
        });
        writer.InsertChunks([
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = content.Count(ch => ch == '\n') + 1,
                Content = content,
            },
        ]);
        var symbols = SymbolExtractor.Extract(fileId, "csharp", content, filePath: path);
        SymbolExtractor.ApplyFamilyScope(symbols, FileIndexer.DeriveFallbackFamilyScopeKey(path));
        writer.InsertSymbols(symbols);
        writer.InsertReferences(ReferenceExtractor.Extract(fileId, "csharp", content, symbols, path: path));
        var rewritten = writer.BackfillFoldedColumns(rewriteAll: true);
        Assert.True(rewritten.Symbols > 0);
        Assert.True(writer.AllFoldedColumnValuesMatchCurrentFold());
        Assert.True(writer.MarkFoldReady());
        writer.MarkCSharpSymbolNameContractReady();
        writer.MarkGraphReady();

        using var reader = new DbReader(db.Connection);
        using (var planCommand = db.Connection.CreateCommand())
        {
            planCommand.CommandText = """
                EXPLAIN QUERY PLAN
                SELECT s.id
                FROM symbols s
                JOIN files f ON f.id = s.file_id
                WHERE s.name_folded = @identity
                   OR (f.lang = 'csharp' AND s.display_name_folded = @display)
                """;
            planCommand.Parameters.AddWithValue("@identity", "run");
            planCommand.Parameters.AddWithValue("@display", "run");
            using var planReader = planCommand.ExecuteReader();
            var plan = new System.Text.StringBuilder();
            while (planReader.Read())
                plan.AppendLine(planReader.GetString(3));
            var planText = plan.ToString();
            Assert.Contains("idx_symbols_name_folded", planText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("idx_symbols_display_name_folded", planText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SCAN s", planText, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(
            DbContext.CSharpSymbolNameContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            reader.GetMetaString(DbContext.CSharpSymbolNameContractVersionMetaKey));
        Assert.Equal(
            "ifoo.run`1",
            CSharpSymbolNameNormalizer.NormalizeExplicitInterfaceQueryIdentityNameFolded("IFoo.Run<T>"));
        Assert.Null(CSharpSymbolNameNormalizer.BuildExplicitInterfaceIdentityNameFolded(
            "Run",
            "Models.Run Run()",
            "function"));
        Assert.Null(CSharpSymbolNameNormalizer.BuildExplicitInterfaceIdentityNameFolded(
            "Run",
            "Models.Run<T> Run()",
            "function"));
        Assert.Null(CSharpSymbolNameNormalizer.BuildExplicitInterfaceIdentityNameFolded(
            "Run",
            "Models.Run[] Run()",
            "function"));
        Assert.Null(CSharpSymbolNameNormalizer.BuildExplicitInterfaceIdentityNameFolded(
            "Count",
            "public int Count => inner.Count;",
            "property"));
        Assert.Null(CSharpSymbolNameNormalizer.BuildExplicitInterfaceIdentityNameFolded(
            "Add",
            "public void Add(Item item) => inner.Add(item);",
            "function"));
        Assert.Null(CSharpSymbolNameNormalizer.BuildExplicitInterfaceIdentityNameFolded(
            "MaxSize",
            "internal const int MaxSize = Limits.MaxSize;",
            "field"));
        Assert.Null(CSharpSymbolNameNormalizer.BuildExplicitInterfaceIdentityNameFolded(
            "Registry",
            "using Registry = CodeIndex.Indexer.Registry;",
            "import"));
        Assert.Null(CSharpSymbolNameNormalizer.BuildExplicitInterfaceIdentityNameFolded(
            "Runner",
            "public class Runner : IFoo.Runner { }",
            "class"));
        Assert.Null(CSharpSymbolNameNormalizer.BuildExplicitInterfaceIdentityNameFolded(
            "Run",
            "void Run<T>() where T : IFoo.Run { }",
            "function"));
        Assert.Null(CSharpSymbolNameNormalizer.BuildExplicitInterfaceIdentityNameFolded(
            "Run",
            "public void Run([Foo.Run()] int value) { }",
            "function"));
        Assert.Equal(
            "ifoo.run",
            CSharpSymbolNameNormalizer.BuildExplicitInterfaceIdentityNameFolded(
                "Run",
                "[Run()] void IFoo.Run()",
                "function"));
        Assert.Equal(
            "ifoo.run`1",
            CSharpSymbolNameNormalizer.BuildExplicitInterfaceIdentityNameFolded(
                "Run",
                "Models.Run<T> IFoo.Run<TValue>(TValue value)",
                "function"));
        Assert.Equal(
            "ifoo.changed",
            CSharpSymbolNameNormalizer.BuildExplicitInterfaceIdentityNameFolded(
                "Changed",
                "event System.EventHandler IFoo . Changed { add { } remove { } }",
                "event"));
        Assert.Equal(
            "ifoo.run",
            CSharpSymbolNameNormalizer.BuildExplicitInterfaceIdentityNameFolded(
                "Run",
                "void IFoo.@Run()",
                "function"));
        Assert.Equal(
            "ifoo.this",
            CSharpSymbolNameNormalizer.BuildExplicitInterfaceIdentityNameFolded(
                "this",
                "void IFoo.@this()",
                "function"));
        Assert.Equal(
            "ifoo.item",
            CSharpSymbolNameNormalizer.NormalizeExplicitInterfaceQueryIdentityNameFolded(
                "IFoo.this"));
        Assert.Equal(
            "iitemcontract.item",
            CSharpSymbolNameNormalizer.BuildExplicitInterfaceIdentityNameFolded(
                "Item",
                "int IItemContract . Item => 2;",
                "property"));
        Assert.Equal(
            "ifoo.this",
            CSharpSymbolNameNormalizer.NormalizeExplicitInterfaceQueryIdentityNameFolded(
                "IFoo.@this"));
        Assert.True(SqlNameResolver.HasQualifier("IFoo.Run<T>"));
        using (var identityCommand = db.Connection.CreateCommand())
        {
            identityCommand.CommandText = """
                SELECT name_folded
                FROM symbols
                WHERE signature LIKE 'void IFoo.Run%'
                """;
            Assert.Equal("ifoo.run`1", identityCommand.ExecuteScalar());
        }

        var fooRun = Assert.Single(reader.SearchSymbols(
            "IFoo.Run<T>",
            lang: "csharp",
            exact: true));
        Assert.Equal("Run", fooRun.Name);
        Assert.StartsWith("void IFoo.Run<TValue>", fooRun.Signature, StringComparison.Ordinal);

        var sameArityAlias = Assert.Single(reader.SearchSymbols(
            "IFoo.Run<TRenamed>",
            lang: "csharp",
            exact: true));
        Assert.Equal(fooRun.SymbolId, sameArityAlias.SymbolId);
        using (var sourceIdentity = db.Connection.CreateCommand())
        {
            sourceIdentity.CommandText = """
                SELECT source_symbol_id
                FROM symbol_references
                WHERE symbol_name = 'ExplicitHelper'
                """;
            Assert.Equal(fooRun.SymbolId, sourceIdentity.ExecuteScalar());
        }

        var barRun = Assert.Single(reader.SearchSymbols(
            "IBar.Run<TLeft, TRight>",
            lang: "csharp",
            exact: true));
        Assert.StartsWith("void IBar.Run", barRun.Signature, StringComparison.Ordinal);
        Assert.NotEqual(fooRun.SymbolId, barRun.SymbolId);

        var interfaceDeclaration = Assert.Single(reader.SearchSymbols(
            "IFoo.Run",
            lang: "csharp",
            exact: true));
        Assert.Equal("interface", interfaceDeclaration.ContainerKind);
        Assert.NotEqual(fooRun.SymbolId, interfaceDeclaration.SymbolId);
        Assert.Contains(
            reader.SearchSymbols("Run", limit: 20, lang: "csharp", exact: true),
            result => result.SymbolId == fooRun.SymbolId);

        var valueResults = reader.SearchSymbols("IFoo.Value", lang: "csharp", exact: true);
        Assert.Equal(2, valueResults.Count);
        Assert.Equal(2, valueResults.Select(result => result.SymbolId).Distinct().Count());
        Assert.Contains(valueResults, result => result.ContainerKind == "interface");
        Assert.Contains(valueResults, result => result.ContainerKind == "class");

        var eventResults = reader.SearchSymbols("IFoo.Changed", lang: "csharp", exact: true);
        Assert.Equal(2, eventResults.Count);
        Assert.Equal(2, eventResults.Select(result => result.SymbolId).Distinct().Count());

        var itemResults = reader.SearchSymbols("IFoo.Item", lang: "csharp", exact: true);
        Assert.Equal(2, itemResults.Count);
        Assert.Equal(2, itemResults.Select(result => result.SymbolId).Distinct().Count());
        var sourceSpelledItemResults = reader.SearchSymbols(
            "IFoo.this",
            lang: "csharp",
            exact: true);
        Assert.Equal(
            itemResults.Select(result => result.SymbolId).Order().ToArray(),
            sourceSpelledItemResults.Select(result => result.SymbolId).Order().ToArray());
        var sourceSpelledItemResultsWithoutLanguage = reader.SearchSymbols(
            "IFoo.this",
            exact: true);
        Assert.Equal(
            itemResults.Select(result => result.SymbolId).Order().ToArray(),
            sourceSpelledItemResultsWithoutLanguage.Select(result => result.SymbolId).Order().ToArray());
        Assert.Equal(
            itemResults.Count,
            reader.CountSearchSymbols("IFoo.this", exact: true));
        Assert.Equal(
            itemResults.Count,
            reader.CountSearchSymbolsTotal("IFoo.this", exact: true).Count);
        Assert.Equal(
            itemResults.Select(result => result.SymbolId).Order().ToArray(),
            reader.GetDefinitions("IFoo.this", exact: true)
                .Select(result => result.SymbolId)
                .Order()
                .ToArray());

        var namedItemPropertyResults = reader.SearchSymbols(
            "IItemContract.Item",
            lang: "csharp",
            exact: true);
        Assert.Equal(2, namedItemPropertyResults.Count);
        Assert.All(namedItemPropertyResults, result => Assert.Equal("property", result.Kind));

        var unicodeShortAliasResults = reader.SearchSymbols(
            "ä",
            lang: "csharp",
            exact: true);
        Assert.Equal(2, unicodeShortAliasResults.Count);
        Assert.Contains(
            unicodeShortAliasResults,
            result => result.Signature?.Contains("IFoo.Ä", StringComparison.Ordinal) == true);
        Assert.Equal(
            2,
            reader.SearchSymbols("IFoo.Ä", lang: "csharp", exact: true).Count);

        var verbatimThisResults = reader.SearchSymbols(
            "IFoo.@this",
            lang: "csharp",
            exact: true);
        Assert.Equal(2, verbatimThisResults.Count);
        Assert.All(
            verbatimThisResults,
            result => Assert.DoesNotContain("IFoo.this[", result.Signature, StringComparison.Ordinal));
        Assert.Equal(
            2,
            reader.CountSearchSymbols("IFoo.@this", lang: "csharp", exact: true));
        Assert.Equal(
            2,
            reader.CountSearchSymbolsTotal("IFoo.@this", lang: "csharp", exact: true).Count);
        var verbatimThisDefinitions = reader.GetDefinitions(
            "IFoo.@this",
            lang: "csharp",
            exact: true);
        Assert.Equal(2, verbatimThisDefinitions.Count);
        Assert.All(
            verbatimThisDefinitions,
            result => Assert.DoesNotContain("IFoo.this[", result.Signature, StringComparison.Ordinal));
        var verbatimThisResultsWithoutLanguage = reader.SearchSymbols(
            "IFoo.@this",
            exact: true);
        Assert.Equal(
            verbatimThisResults.Select(result => result.SymbolId).Order().ToArray(),
            verbatimThisResultsWithoutLanguage.Select(result => result.SymbolId).Order().ToArray());
        var attributedExplicitMethod = Assert.Single(
            reader.SearchSymbols("IFoo.Verify", lang: "csharp", exact: true),
            result => result.Kind == "test.method");
        Assert.Contains("IFoo.Verify", attributedExplicitMethod.Signature, StringComparison.Ordinal);

        const string sqlPath = "src/qualified-function.sql";
        var sqlFileId = writer.UpsertFile(new FileRecord
        {
            Path = sqlPath,
            Lang = "sql",
            Size = 32,
            Lines = 1,
            Modified = new DateTime(2026, 7, 29, 0, 0, 0, DateTimeKind.Utc),
        });
        writer.InsertChunks([
            new ChunkRecord
            {
                FileId = sqlFileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 1,
                Content = "CREATE FUNCTION foo.this();",
            },
        ]);
        writer.InsertSymbols([
            new SymbolRecord
            {
                FileId = sqlFileId,
                Kind = "function",
                Name = "foo.this",
                Line = 1,
                StartLine = 1,
                EndLine = 1,
                Signature = "CREATE FUNCTION foo.this();",
            },
        ]);
        writer.BackfillFoldedColumns(rewriteAll: true);

        var sqlQualified = Assert.Single(reader.SearchSymbols("foo.this", exact: true));
        Assert.Equal(sqlPath, sqlQualified.Path);
        Assert.Equal(1, reader.CountSearchSymbolsTotal("foo.this", exact: true).Count);
        Assert.Equal(sqlPath, Assert.Single(reader.GetDefinitions("foo.this", exact: true)).Path);

        var qualifiedService = Assert.Single(reader.SearchSymbols(
            "Demo.Service",
            lang: "csharp",
            exact: true));
        Assert.Equal("class", qualifiedService.Kind);
        Assert.Single(reader.SearchSymbols("IFoo.Run<T>", lang: "csharp", exact: false));

        var definitions = reader.GetDefinitions(
            "IFoo.Run<T>",
            lang: "csharp",
            exact: true);
        Assert.Single(definitions);
        Assert.Equal(fooRun.SymbolId, definitions[0].SymbolId);
        Assert.Equal(1, reader.CountDefinitionsTotal(
            "IFoo.Run<T>",
            lang: "csharp",
            exact: true).Count);

        var analysis = reader.AnalyzeSymbol(
            "IFoo.Run<T>",
            lang: "csharp",
            exact: true);
        Assert.Single(analysis.Definitions);
        Assert.Equal(fooRun.SymbolId, analysis.Definitions[0].SymbolId);
        Assert.Empty(analysis.References);
        var explicitCallee = Assert.Single(analysis.Callees);
        Assert.Equal("ExplicitHelper", explicitCallee.CalleeName);
        Assert.Empty(reader.SearchReferences(
            "IFoo.Run<T>",
            lang: "csharp",
            exact: true));
        Assert.NotEmpty(reader.SearchReferences(
            "Run",
            lang: "csharp",
            exact: true));

        var outline = reader.GetOutline(path);
        Assert.NotNull(outline);
        Assert.Contains(
            outline!.Symbols,
            symbol => symbol.Name == "Run"
                && symbol.Signature?.StartsWith("void IFoo.Run<TValue>", StringComparison.Ordinal) == true);
        Assert.Contains(
            outline.Symbols,
            symbol => symbol.Name == "Item"
                && symbol.Signature?.Contains("IFoo . this", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void SearchSymbols_QualifiedExactWithoutLanguagePreservesLegacyCSharpAndTerraform_Issue4866Review()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_explicit_interface_cross_language_4866");
        var dbPath = Path.Combine(project.Root, "codeindex.db");
        using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
        db.InitializeSchema();
        var writer = new DbWriter(db.Connection);
        var csharpFileId = writer.UpsertFile(new FileRecord
        {
            Path = "src/Using.cs",
            Lang = "csharp",
            Size = 25,
            Lines = 1,
            Modified = new DateTime(2026, 7, 29, 0, 0, 0, DateTimeKind.Utc),
        });
        writer.InsertChunks([
            new ChunkRecord
            {
                FileId = csharpFileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 1,
                Content = "using CodeIndex.Database; public sealed class Service { }",
            },
        ]);
        var terraformFileId = writer.UpsertFile(new FileRecord
        {
            Path = "infra/main.tf",
            Lang = "terraform",
            Size = 20,
            Lines = 1,
            Modified = new DateTime(2026, 7, 29, 0, 0, 0, DateTimeKind.Utc),
        });
        writer.InsertSymbols([
            new SymbolRecord
            {
                FileId = csharpFileId,
                Kind = "import",
                Name = "CodeIndex.Database",
                Signature = "using CodeIndex.Database;",
                Line = 1,
                StartLine = 1,
                EndLine = 1,
            },
            new SymbolRecord
            {
                FileId = csharpFileId,
                Kind = "class",
                Name = "Service",
                Signature = "public sealed class Service",
                ContainerKind = "namespace",
                ContainerName = "Demo",
                ContainerQualifiedName = "Demo",
                Line = 1,
                StartLine = 1,
                EndLine = 1,
            },
            new SymbolRecord
            {
                FileId = terraformFileId,
                Kind = "function",
                Name = "region",
                Signature = """variable "region" {}""",
                Line = 1,
                StartLine = 1,
                EndLine = 1,
            },
        ]);
        writer.BackfillFoldedColumns(rewriteAll: true);
        Assert.True(writer.MarkFoldReady());
        writer.SetMeta(DbContext.CSharpSymbolNameContractVersionMetaKey, "2");

        using var reader = new DbReader(db.Connection);
        Assert.Equal(
            "src/Using.cs",
            Assert.Single(reader.SearchSymbols("CodeIndex.Database", exact: true)).Path);
        Assert.Equal(
            "src/Using.cs",
            Assert.Single(reader.SearchSymbols("global::CodeIndex.Database", exact: true)).Path);
        Assert.Equal(
            "src/Using.cs",
            Assert.Single(reader.SearchSymbols("@CodeIndex.@Database", exact: true)).Path);
        Assert.Equal(
            "src/Using.cs",
            Assert.Single(reader.SearchSymbols("global::Demo.Service", exact: true)).Path);
        Assert.Equal(
            "src/Using.cs",
            Assert.Single(reader.SearchSymbols("@Demo.@Service", exact: true)).Path);
        Assert.Equal(
            "src/Using.cs",
            Assert.Single(reader.GetDefinitions("global::Demo.Service", exact: true)).Path);
        Assert.Equal(
            "infra/main.tf",
            Assert.Single(reader.SearchSymbols("var.region", exact: true)).Path);
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
            Path = "src/auth_v2.py",
            Lang = "python",
            Size = 80,
            Lines = 4,
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
            Path = "src/intl.py",
            Lang = "python",
            Size = 120,
            Lines = 6,
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
    public void SearchSymbols_ExactFallsBackToNocaseWhenFoldKeyVersionMismatches()
    {
        // #86 codex third-pass review: when NameFold.Fold changes and bumps NameFold.Version,
        // previously stamped DBs must NOT be read through the folded equality path — their
        // stored keys were generated by the old fold function and comparing them against
        // queries folded with the new function silently misses.
        // Simulate by writing a mismatched `fold_key_version` into codeindex_meta and
        // confirming the reader falls back to NOCASE. Rebuild would restamp to current.
        // #86 3rd pass: fold_key_version 不一致時は NOCASE fallback に降格することを固定する。
        var mismatchDir = TestProjectHelper.CreateTempProject("codeindex_fold_version");
        var mismatchPath = Path.Combine(mismatchDir, "codeindex.db");
        try
        {
            using var db = new DbContext(DbOpenIntent.WriteIndex, mismatchPath);
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
            TestProjectHelper.DeleteDirectory(mismatchDir);
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
        var mismatchDir = TestProjectHelper.CreateTempProject("codeindex_fold_fingerprint");
        var mismatchPath = Path.Combine(mismatchDir, "codeindex.db");
        try
        {
            using var db = new DbContext(DbOpenIntent.WriteIndex, mismatchPath);
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
            TestProjectHelper.DeleteDirectory(mismatchDir);
        }
    }

    [Fact]
    public void SearchSymbols_ExactFallsBackToNocaseWhenFoldNotReady()
    {
        // Legacy / partial-backfill DBs do not set FoldReadyFlag; the reader must silently
        // fall back to the ASCII `COLLATE NOCASE` path and still return correct ASCII results.
        // Non-ASCII casing is expected to miss (documented limitation until reindex).
        // Legacy DB は fold フラグ未設定なら NOCASE fallback。ASCII は動き続ける。
        var legacyDir = TestProjectHelper.CreateTempProject("codeindex_fold_legacy");
        var legacyPath = Path.Combine(legacyDir, "codeindex.db");
        try
        {
            using var legacyDb = new DbContext(DbOpenIntent.WriteIndex, legacyPath);
            legacyDb.InitializeSchema();
            var writer = new DbWriter(legacyDb.Connection);
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
            TestProjectHelper.DeleteDirectory(legacyDir);
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
            Path = "tests/auth_test.py",
            Lang = "python",
            Size = 300,
            Lines = 10,
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
            Path = "tests/auth_test.py",
            Lang = "python",
            Size = 300,
            Lines = 10,
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
            _reader.SearchReferences("dbo.Target", lang: "sql", exact: true, pathPatterns: ["src/*current_sql*"]));
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
        var legacyDir = TestProjectHelper.CreateTempProject("codeindex_legacy_reader");
        var legacyPath = Path.Combine(legacyDir, "codeindex.db");
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
            TestProjectHelper.DeleteDirectory(legacyDir);
        }
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
        const int secondCallReferenceLine = callReferenceLine + 1;

        using (var updateFileCmd = _db.Connection.CreateCommand())
        {
            updateFileCmd.CommandText = "UPDATE files SET lines = @lines WHERE path = 'src/Use.cs'";
            updateFileCmd.Parameters.AddWithValue("@lines", secondCallReferenceLine + 5);
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
        syntheticReferences.Add(new ReferenceRecord
        {
            FileId = useFileId,
            SymbolName = "Red",
            ReferenceKind = "call",
            Line = secondCallReferenceLine,
            Column = 9,
            Context = "        Red();",
            ContainerKind = "function",
            ContainerName = "Match",
        });
        _writer.InsertReferences(syntheticReferences);

        var result = Assert.Single(_reader.SearchReferences("Red", limit: 1, lang: "csharp", exact: true, pathPatterns: ["src/Use.cs"]));
        Assert.Equal("call", result.ReferenceKind);
        Assert.Equal(callReferenceLine, result.Line);

        var nextPage = Assert.Single(_reader.SearchReferences("Red", limit: 1, lang: "csharp", exact: true, pathPatterns: ["src/Use.cs"], offset: 1));
        Assert.Equal("call", nextPage.ReferenceKind);
        Assert.Equal(secondCallReferenceLine, nextPage.Line);

        Assert.Equal(2, _reader.CountSearchReferences("Red", limit: 2, lang: "csharp", exact: true, pathPatterns: ["src/Use.cs"]));
        Assert.Equal(new QueryCountResult(2, 1), _reader.CountSearchReferencesTotal("Red", lang: "csharp", exact: true, pathPatterns: ["src/Use.cs"]));
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
    public void SearchReferences_ReportsAndCanExcludeSelfReferences()
    {
        InsertIndexedFile("src/self_reference_search.cs", "csharp",
            """
            public static class SelfReferenceSearch
            {
                public static void SearchSelfTarget() { SearchSelfTarget(); }
            }
            """);

        var reference = Assert.Single(_reader.SearchReferences(
            "SearchSelfTarget", lang: "csharp", referenceKind: "call", exact: true, pathPatterns: ["src/*self_reference_search*"]));
        Assert.True(reference.IsSelfReference);
        Assert.False(reference.IsMutualRecursion);

        Assert.Empty(_reader.SearchReferences(
            "SearchSelfTarget",
            lang: "csharp",
            referenceKind: "call",
            exact: true,
            pathPatterns: ["src/*self_reference_search*"],
            excludeSelfReferences: true));
    }

    [Fact]
    public void SearchReferences_StampsMutualRecursionAcrossFiles()
    {
        InsertIndexedFile("src/mutual_recursion_a.cs", "csharp",
            """
            public static class MutualRecursionA
            {
                public static void CrossCycleA() { CrossCycleB(); }
            }
            """);
        InsertIndexedFile("src/mutual_recursion_b.cs", "csharp",
            """
            public static class MutualRecursionB
            {
                public static void CrossCycleB() { CrossCycleA(); }
            }
            """);

        var aToB = Assert.Single(_reader.SearchReferences(
            "CrossCycleB", lang: "csharp", referenceKind: "call", exact: true, pathPatterns: ["src/*mutual_recursion_a*"]));
        var bToA = Assert.Single(_reader.SearchReferences(
            "CrossCycleA", lang: "csharp", referenceKind: "call", exact: true, pathPatterns: ["src/*mutual_recursion_b*"]));

        Assert.True(aToB.IsMutualRecursion);
        Assert.True(bToA.IsMutualRecursion);
        Assert.False(aToB.IsSelfReference);
        Assert.False(bToA.IsSelfReference);
    }
}
