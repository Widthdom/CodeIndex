using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Lsp;

namespace CodeIndex.Tests;

[Collection("Console sensitive")]
public sealed class LspCallHierarchyTests
{
    [Theory]
    [InlineData(51)]
    [InlineData(LspServer.ReferencePageSize * 2 + 1)]
    public void FramedReferences_DeliversEveryLocationWithIdentityAndTokenParity_Issue5392(int count)
    {
        const string call = " _ = \"😀\"; Leaf();";
        var source = "class Calls\n{\n public static void Leaf() {}\n public static void Empty() {}\n"
            + " public static void Leaf(int x) {}\n void Caller() {\n"
            + string.Concat(Enumerable.Repeat(call + "\n", count - 1))
            + " Leaf(1);\n" + string.Concat(Enumerable.Repeat(" Missing();\n", 51)) + " }\n}";
        const string other = "class Other\n{\n void Caller() { Calls.Leaf(); Missing(); }\n}\n"
            + "class Decoy\n{\n void Leaf() {}\n void Caller() { Leaf(); }\n}";
        using var fixture = new Fixture(source, "日本 #Calls.cs", additionalFiles: new Dictionary<string, string>
        {
            ["Other.cs"] = other,
        });
        Assert.True(fixture.Capabilities["referencesProvider"]!["workDoneProgress"]!.GetValue<bool>());
        var expected = Enumerable.Range(6, count - 1)
            .Select(line => (fixture.Uri, line, call.IndexOf("Leaf", StringComparison.Ordinal))).ToList();
        expected.Add((LspServer.PathToUri(Path.Combine(Path.GetDirectoryName(fixture.SourcePath)!, "Other.cs")),
            2, other.Split('\n')[2].IndexOf("Leaf", StringComparison.Ordinal)));
        foreach (var includeDeclaration in new[] { false, true })
        {
            string[]? baseline = null;
            foreach (var partial in new[] { false, true })
            {
                var result = RequestReferences(fixture, partial, includeDeclaration);
                Assert.Null(result["error"]);
                var locations = ReferenceLocations(fixture, result, partial);
                Assert.Equal(count + (includeDeclaration ? 1 : 0), locations.Length);
                var identities = locations.Select(node => node.ToJsonString()).ToArray();
                Assert.Equal(identities.Length, identities.Distinct(StringComparer.Ordinal).Count());
                if (baseline != null)
                    Assert.Equal(baseline, identities);
                baseline = identities;
                var actual = locations.Skip(includeDeclaration ? 1 : 0).Select(node =>
                    (node["uri"]!.GetValue<string>(), node["range"]!["start"]!["line"]!.GetValue<int>(),
                        node["range"]!["start"]!["character"]!.GetValue<int>())).ToArray();
                Assert.Equal(expected.OrderBy(x => x.Item1, StringComparer.Ordinal).ThenBy(x => x.Item2),
                    actual.OrderBy(x => x.Item1, StringComparer.Ordinal).ThenBy(x => x.Item2));
                Assert.All(locations, node => Assert.Equal(4,
                    node["range"]!["end"]!["character"]!.GetValue<int>()
                    - node["range"]!["start"]!["character"]!.GetValue<int>()));
                AssertReferenceProgressEnded(fixture);
                if (partial)
                {
                    Assert.True(result.ContainsKey("result"));
                    Assert.Null(result["result"]);
                    Assert.All(fixture.Messages.Where(x => IsReferenceChunk(x.Message)), entry =>
                    {
                        Assert.InRange(entry.Message["params"]!["value"]!.AsArray().Count, 1, 100);
                        Assert.InRange(entry.Bytes, 1, LspServer.MaxSymbolProgressChunkBytes);
                    });
                    Assert.Equal((locations.Length + 99) / 100,
                        fixture.Messages.Count(x => IsReferenceChunk(x.Message)));
                }
            }
        }
        foreach (var partial in new[] { false, true })
        {
            var empty = RequestReferences(fixture, partial, false, line: 3);
            Assert.Null(empty["error"]);
            Assert.Empty(ReferenceLocations(fixture, empty, partial));
            AssertReferenceProgressEnded(fixture);
            var fallback = RequestReferences(fixture, partial, false, line: count + 6, character: 2);
            Assert.Null(fallback["error"]);
            var fallbackLocations = ReferenceLocations(fixture, fallback, partial);
            Assert.Equal(51, fallbackLocations.Length);
            Assert.All(fallbackLocations, node => Assert.Equal(fixture.Uri, node["uri"]!.GetValue<string>()));
            AssertReferenceProgressEnded(fixture);
        }
        // Another evidence kind at an existing site must not duplicate a Location.
        using (var db = new DbContext(DbOpenIntent.WriteIndex, fixture.DbPath))
        using (var command = db.Connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO symbol_references(file_id, symbol_name, symbol_name_folded, reference_kind,
                    line, column_number, target_symbol_id, resolution_state, resolution_candidate_count)
                SELECT file_id, symbol_name, symbol_name_folded, 'type_reference', line, column_number,
                    target_symbol_id, resolution_state, resolution_candidate_count
                FROM symbol_references WHERE symbol_name = 'Leaf' AND line = 7 AND reference_kind = 'call'
                """;
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        foreach (var partial in new[] { false, true })
            Assert.Equal(count, ReferenceLocations(fixture, RequestReferences(fixture, partial, false), partial).Length);
        var highlights = fixture.Position("textDocument/documentHighlight", 2, 20)["result"]!.AsArray();
        Assert.InRange(highlights.Count, 1, 51);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FramedReferences_LimitsAreErrorsAndCancellationEndsProgress_Issue5392(bool pretty)
    {
        var source = "class Calls\n{\n public static void Leaf() {}\n void Caller() {\n"
            + string.Concat(Enumerable.Repeat(" Leaf();\n", LspServer.ReferencePageSize * 2 + 1)) + " }\n}";
        using var fixture = new Fixture(source, pretty: pretty);
        foreach (var partial in new[] { false, true })
        {
            fixture.Server.ReferenceRowsForTesting = LspServer.ReferencePageSize;
            var limited = RequestReferences(fixture, partial, false);
            AssertError(limited, -32803, "reference_row_limit");
            Assert.Contains("CLI inspect", limited["error"]!["data"]!["recovery"]!.GetValue<string>());
            AssertReferenceProgressEnded(fixture);
        }
        fixture.Server.ReferenceRowsForTesting = 101;
        var partlyDelivered = RequestReferences(fixture, true, false);
        AssertError(partlyDelivered, -32803, "reference_row_limit");
        Assert.Equal(100, partlyDelivered["error"]!["data"]!["deliveredLocationCount"]!.GetValue<int>());
        AssertReferenceProgressEnded(fixture);
        fixture.Server.ReferenceRowsForTesting = LspServer.MaxReferenceRows;
        fixture.Server.ReferenceResponseBytesForTesting = 512;
        var byteLimit = RequestReferences(fixture, false, false);
        AssertError(byteLimit, -32803, "response_byte_limit");
        Assert.Contains("partialResultToken", byteLimit["error"]!["data"]!["recovery"]!.GetValue<string>());
        AssertReferenceProgressEnded(fixture);
        Assert.Equal(201, ReferenceLocations(fixture, RequestReferences(fixture, true, false), true).Length);
        fixture.Server.ReferenceResponseBytesForTesting = LspServer.MaxReferenceResponseBytes;
        fixture.Server.ReferenceChunkBytesForTesting = 1024;
        Assert.Equal(201, ReferenceLocations(fixture, RequestReferences(fixture, true, false), true).Length);
        Assert.All(fixture.Messages.Where(x => IsReferenceChunk(x.Message)), entry => Assert.InRange(entry.Bytes, 1, 1024));
        fixture.Server.ReferenceChunkBytesForTesting = 1;
        AssertError(RequestReferences(fixture, true, false), -32803, "progress_byte_limit");
        AssertReferenceProgressEnded(fixture);
        fixture.Server.ReferenceChunkBytesForTesting = LspServer.MaxSymbolProgressChunkBytes;
        fixture.Server.ReferenceDeliveryBytesForTesting = 1;
        AssertError(RequestReferences(fixture, true, false), -32803, "reference_delivery_limit");
        AssertReferenceProgressEnded(fixture);
        fixture.Server.ReferenceDeliveryBytesForTesting = LspServer.MaxReferenceDeliveryBytes;
        fixture.Server.AfterReferenceChunkForTesting = () => fixture.Notify("$/cancelRequest", new { id = 5351 });
        var cancelled = RequestReferences(fixture, true, false);
        Assert.Equal(-32800, cancelled["error"]!["code"]!.GetValue<int>());
        Assert.False(cancelled.ContainsKey("result"));
        Assert.Equal(100, fixture.Messages.Single(x => IsReferenceChunk(x.Message)).Message["params"]!["value"]!.AsArray().Count);
        AssertReferenceProgressEnded(fixture);
        fixture.Server.AfterReferenceChunkForTesting = null;
        fixture.Server.BeforeReferenceReadForTesting = _ => throw new IOException("Injected failure");
        Assert.Equal(-32603, RequestReferences(fixture, true, false)["error"]!["code"]!.GetValue<int>());
        AssertReferenceProgressEnded(fixture);
        fixture.Server.BeforeReferenceReadForTesting = null;
        fixture.Server.ReferenceTimeoutForTesting = TimeSpan.FromMilliseconds(1);
        fixture.Server.BeforeReferenceReadForTesting = token => token.WaitHandle.WaitOne(TestDeterminism.DefaultTimeout);
        AssertError(RequestReferences(fixture, true, false), -32803, "query_deadline_exceeded");
        AssertReferenceProgressEnded(fixture);
        fixture.Server.ReferenceTimeoutForTesting = TimeSpan.FromSeconds(5);
        fixture.Server.BeforeReferenceReadForTesting = null;
        fixture.Server.BeforeReferenceValidationForTesting = () =>
        {
            using var db = new DbContext(DbOpenIntent.WriteIndex, fixture.DbPath);
            new DbWriter(db).SetMeta("reference_test_generation", "changed");
        };
        AssertError(RequestReferences(fixture, true, false), -32801, "index_generation_changed");
        AssertReferenceProgressEnded(fixture);
        fixture.Server.BeforeReferenceValidationForTesting = null;
        Assert.Equal(201, ReferenceLocations(fixture, RequestReferences(fixture, false, false), false).Length);
    }

    private static JsonObject RequestReferences(Fixture fixture, bool partial, bool includeDeclaration, int line = 2, int character = 20)
    {
        var parameters = new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = fixture.Uri },
            ["position"] = new JsonObject { ["line"] = line, ["character"] = character },
            ["context"] = new JsonObject { ["includeDeclaration"] = includeDeclaration },
            ["workDoneToken"] = "reference-work",
        };
        if (partial)
            parameters["partialResultToken"] = 5392;
        return fixture.Request("textDocument/references", parameters);
    }

    private static bool IsReferenceChunk(JsonObject message) => message["method"]?.GetValue<string>() == "$/progress"
        && message["params"]!["token"] is JsonValue token && token.TryGetValue<int>(out var number) && number == 5392;

    private static JsonNode[] ReferenceLocations(Fixture fixture, JsonObject result, bool partial) => partial
        ? fixture.Messages.Where(x => IsReferenceChunk(x.Message)).SelectMany(x => x.Message["params"]!["value"]!.AsArray()).Select(x => x!).ToArray()
        : result["result"]!.AsArray().Select(x => x!).ToArray();

    private static void AssertReferenceProgressEnded(Fixture fixture)
    {
        var work = fixture.Messages.Where(x => x.Message["method"]?.GetValue<string>() == "$/progress"
                && x.Message["params"]!["token"] is JsonValue token && token.TryGetValue<string>(out var value) && value == "reference-work")
            .Select(x => x.Message["params"]!["value"]!["kind"]!.GetValue<string>()).ToArray();
        Assert.Equal("begin", work[0]);
        Assert.Equal("end", work[^1]);
        Assert.Equal(1, work.Count(x => x == "end"));
        Assert.True(fixture.Messages[^1].Message.ContainsKey("id"));
    }

    private const string Source = """
        class Alpha
        {
            public void Leaf() { }
            public void Leaf(int x) { }
            public void Caller()
            {
                Leaf(); Leaf();
                Leaf(1);
            }
            public void Recursive() { Recursive(); }
            public void Missing() { Unknown(); }
        }
        class Beta
        {
            public void Leaf() { }
            public void Caller() { Leaf(); }
        }
        """;

    [Fact]
    public void FramedHierarchy_PreservesIdentityRepeatedSitesAndNavigation_Issue5351()
    {
        using var fixture = new Fixture(Source);
        Assert.True(fixture.Capabilities["callHierarchyProvider"]!.GetValue<bool>());
        var leaf = fixture.Prepare(2, "Leaf");
        var overloaded = fixture.Prepare(3, "Leaf");
        var caller = fixture.Prepare(4, "Caller");
        var other = fixture.Prepare(14, "Leaf");
        Assert.NotEqual(leaf["data"]!.GetValue<string>(), overloaded["data"]!.GetValue<string>());
        Assert.NotEqual(leaf["data"]!.GetValue<string>(), other["data"]!.GetValue<string>());
        Assert.Equal(leaf["data"]!.GetValue<string>(), fixture.Prepare(6, "Leaf")["data"]!.GetValue<string>());

        var incoming = fixture.Expand(leaf, incoming: true);
        var edge = Assert.Single(incoming);
        Assert.Equal("Caller", edge!["from"]!["name"]!.GetValue<string>());
        Assert.Equal(4, edge["from"]!["selectionRange"]!["start"]!["line"]!.GetValue<int>());
        var ranges = edge["fromRanges"]!.AsArray();
        Assert.Equal(2, ranges.Count);
        AssertRange(ranges[0]!, 6, 8, 12);
        AssertRange(ranges[1]!, 6, 16, 20);
        var outgoing = fixture.Expand(caller, incoming: false);
        Assert.Equal(2, outgoing.Count);
        Assert.Equal(new[] { 2, 3 }, outgoing.Select(node => node!["to"]!["selectionRange"]!["start"]!["line"]!.GetValue<int>()).Order().ToArray());
        Assert.Equal(3, outgoing.Sum(node => node!["fromRanges"]!.AsArray().Count));
        Assert.Single(fixture.Expand(other, incoming: true));
        Assert.Empty(fixture.Expand(leaf, incoming: false));
        var recursive = fixture.Prepare(9, "Recursive");
        Assert.Single(fixture.Expand(recursive, incoming: true));
        Assert.Single(fixture.Expand(recursive, incoming: false));

        Assert.Null(fixture.Position("textDocument/prepareCallHierarchy", 0, 6)["result"]);
        Assert.Null(fixture.Position("textDocument/prepareCallHierarchy", 1, 0)["result"]);
        Assert.NotEmpty(fixture.Position("textDocument/definition", 6, 8)["result"]!.AsArray());
        Assert.NotEmpty(fixture.Position("textDocument/references", 2, 16)["result"]!.AsArray());
        Assert.NotEmpty(fixture.Request("textDocument/documentSymbol", new { textDocument = new { uri = fixture.Uri } })["result"]!.AsArray());

        var wrongUri = leaf.DeepClone();
        wrongUri["uri"] = "file:///outside.cs";
        Assert.Equal(-32602, fixture.Request("callHierarchy/incomingCalls", new { item = wrongUri })["error"]!["code"]!.GetValue<int>());
        Assert.Equal(-32602, fixture.Request("callHierarchy/outgoingCalls", new { item = new { data = new string('x', 161) } })["error"]!["code"]!.GetValue<int>());
        Assert.Equal(-32602, fixture.Position("textDocument/prepareCallHierarchy", -1, 0)["error"]!["code"]!.GetValue<int>());
        Assert.All(fixture.Notices, notice => Assert.Contains("not compiler-complete", notice, StringComparison.Ordinal));
        Assert.NotEmpty(fixture.Notices);
    }

    [Fact]
    public void FramedNavigation_FiltersBeforeLimitsAndPreservesReferenceFallbacks_Issue5373()
    {
        const string source = "class Calls\n{\n void Leaf() {}\n void Caller(B b)\n {\n  Leaf();\n  Missing();\n  b.Remote();\n }\n}\nclass Decoy\n{\n void Remote() {}\n}";
        const string target = "class B\n{\n public void Remote() {}\n}";
        // Exactly fill the old local-candidate limit with an earlier-sorting foreign file.
        var foreign = new StringBuilder();
        for (var index = 0; index < 50; index++)
            foreign.AppendLine($"class Foreign{index}\n{{\n void Leaf() {{}}\n void Caller() {{ Leaf(); Missing(); }}\n}}");
        using var fixture = new Fixture(source, "calls.cs", additionalFiles: new Dictionary<string, string>
        {
            ["Foreign.cs"] = foreign.ToString(),
            ["Target.cs"] = target,
        });
        using (var db = new DbContext(DbOpenIntent.WriteIndex, fixture.DbPath))
        using (var command = db.Connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE files SET path = 'Calls.cs' WHERE path = 'Foreign.cs';
                DELETE FROM symbol_reference_candidates WHERE reference_id IN
                    (SELECT id FROM symbol_references WHERE symbol_name IN ('Leaf', 'Missing'));
                UPDATE symbol_references SET target_symbol_id = NULL, target_symbol_key = NULL,
                    resolution_state = 'unresolved', resolution_candidate_count = 0
                    WHERE symbol_name IN ('Leaf', 'Missing');
                """;
            command.ExecuteNonQuery();
        }
        using (var db = new DbContext(DbOpenIntent.QueryOnly, fixture.DbPath))
        {
            var reader = new DbReader(db);
            var patternCandidates = reader.GetDefinitions("Leaf", 50, exact: true, pathPatterns: ["calls.cs"]);
            Assert.Equal(50, patternCandidates.Count);
            Assert.All(patternCandidates, candidate => Assert.Equal("Calls.cs", candidate.Path));
        }
        foreach (var line in new[] { 2, 5 })
        {
            var column = source.Split('\n')[line].IndexOf("Leaf", StringComparison.Ordinal);
            var definition = Assert.Single(fixture.Position("textDocument/definition", line, column)["result"]!.AsArray())!;
            Assert.Equal(fixture.Uri, definition["uri"]!.GetValue<string>());
            AssertRange(definition["range"]!, 2, 6, 10);
        }
        // With identity evidence removed, query the invocation to exercise the legacy
        // local-name fallback; declarations deliberately require identity-bound references.
        var reference = Assert.Single(fixture.Position("textDocument/references", 5, 2)["result"]!.AsArray())!;
        Assert.Equal(fixture.Uri, reference["uri"]!.GetValue<string>());
        AssertRange(reference["range"]!, 5, 2, 6);
        var missing = Assert.Single(fixture.Position("textDocument/references", 6, 2)["result"]!.AsArray())!;
        Assert.Equal(fixture.Uri, missing["uri"]!.GetValue<string>());
        AssertRange(missing["range"]!, 6, 2, 9);

        // Local same-name candidates must not constrain an authoritative cross-file target.
        var remote = Assert.Single(fixture.Position("textDocument/definition", 7, 4)["result"]!.AsArray())!;
        Assert.Equal(LspServer.PathToUri(Path.Combine(Path.GetDirectoryName(fixture.SourcePath)!, "Target.cs")), remote["uri"]!.GetValue<string>());
        AssertRange(remote["range"]!, 2, 13, 19);
        var remoteReference = Assert.Single(fixture.Position("textDocument/references", 7, 4)["result"]!.AsArray())!;
        Assert.Equal(fixture.Uri, remoteReference["uri"]!.GetValue<string>());
        AssertRange(remoteReference["range"]!, 7, 4, 10);
    }

    [Fact]
    public void ExactFileScope_TreatsGlobCharactersLiterallyAndRestoresNestedScopes_Issue5373()
    {
        const string source = "class Calls\n{\n void Leaf() {}\n void Caller() { Leaf(); }\n}";
        const string indexedPath = "[Calls]*?%_#'日本.cs";
        const string otherPath = "[calls]*?%_#'日本.cs";
        using var fixture = new Fixture(source, additionalFiles: new Dictionary<string, string> { ["Other.cs"] = source });
        using var db = new DbContext(DbOpenIntent.WriteIndex, fixture.DbPath);
        using (var command = db.Connection.CreateCommand())
        {
            // These names need not be legal on the host filesystem: only indexed identity matters.
            command.CommandText = "UPDATE files SET path = CASE WHEN path = 'Calls.cs' THEN @path ELSE @other END";
            command.Parameters.AddWithValue("@path", indexedPath);
            command.Parameters.AddWithValue("@other", otherPath);
            command.ExecuteNonQuery();
        }
        var reader = new DbReader(db);
        using (DbReader.BeginExactFilePath(indexedPath))
        {
            AssertPath(indexedPath);
            Assert.Throws<InvalidOperationException>((Action)(() =>
            {
                using var nested = DbReader.BeginAuditPartitionPath(otherPath);
                AssertPath(otherPath);
                throw new InvalidOperationException("Scope restoration control");
            }));
            AssertPath(indexedPath);
        }
        Assert.Equal(2, reader.GetDefinitions("Leaf", exact: true).Count);
        Assert.Equal(2, reader.SearchReferences("Leaf", exact: true).Count);

        void AssertPath(string path)
        {
            Assert.Equal(path, Assert.Single(reader.GetDefinitions("Leaf", 1, exact: true)).Path);
            Assert.Equal(path, Assert.Single(reader.SearchReferences("Leaf", 1, exact: true)).Path);
        }
    }

    [Theory]
    [InlineData("cs", "class Café\n{\n    void 終了() { }\n    void 開始() { var s = \"😀\"; 終了(); }\n}\n", 2, "終了", 3)]
    [InlineData("py", "def 終了():\n    pass\ndef 開始():\n    終了()\n", 0, "終了", 3)]
    public void FramedHierarchy_UsesUtf16AndFileUris_Issue5351(string extension, string source, int declarationLine, string name, int callLine)
    {
        using var fixture = new Fixture(source, "日本 語 #." + extension);
        var item = fixture.Prepare(declarationLine, name);
        Assert.Equal(fixture.Uri, item["uri"]!.GetValue<string>());
        var declarationColumn = source.Split('\n')[declarationLine].IndexOf(name, StringComparison.Ordinal);
        AssertRange(item["selectionRange"]!, declarationLine, declarationColumn, declarationColumn + name.Length);
        var edge = Assert.Single(fixture.Expand(item, incoming: true));
        var column = source.Split('\n')[callLine].IndexOf(name, StringComparison.Ordinal);
        AssertRange(Assert.Single(edge!["fromRanges"]!.AsArray())!, callLine, column, column + name.Length);
    }

    [Fact]
    public void FramedHierarchy_RejectsUnresolvedIncompleteStaleAndUnsavedEvidence_Issue5351()
    {
        using var fixture = new Fixture(Source);
        var leaf = fixture.Prepare(2, "Leaf");
        var missing = fixture.Prepare(10, "Missing");
        AssertError(fixture.Request("callHierarchy/outgoingCalls", new { item = missing }), -32803, "unresolved_or_ambiguous_calls");
        fixture.Notify("textDocument/didOpen", new { textDocument = new { uri = fixture.Uri, version = 1, text = Source } });
        Assert.Single(fixture.Expand(leaf, incoming: true));
        fixture.Notify("textDocument/didChange", new { textDocument = new { uri = fixture.Uri, version = 2 }, contentChanges = new[] { new { text = Source + "\n// unsaved" } } });
        AssertError(fixture.Request("callHierarchy/incomingCalls", new { item = leaf }), -32801, "unsaved_document");
        fixture.Notify("textDocument/didChange", new { textDocument = new { uri = fixture.Uri, version = 1 }, contentChanges = new[] { new { text = Source } } });
        AssertError(fixture.Request("callHierarchy/incomingCalls", new { item = leaf }), -32801, "unsaved_document");
        fixture.Notify("textDocument/didClose", new { textDocument = new { uri = fixture.Uri } });
        Assert.Single(fixture.Expand(leaf, incoming: true));
        File.AppendAllText(fixture.SourcePath, "\n// saved after indexing");
        AssertError(fixture.Request("callHierarchy/incomingCalls", new { item = leaf }), -32801, "document_not_indexed");
        fixture.Reindex();
        AssertError(fixture.Request("callHierarchy/incomingCalls", new { item = leaf }), -32801, "stale_item");
        leaf = fixture.Prepare(2, "Leaf");
        Assert.Single(fixture.Expand(leaf, incoming: true));
        using (var writerDb = new DbContext(DbOpenIntent.WriteIndex, fixture.DbPath))
            new DbWriter(writerDb).SetMeta(DbContext.IndexCompletenessMetaKey, "incomplete");
        AssertError(fixture.Request("callHierarchy/incomingCalls", new { item = leaf }), -32803, "graph_unavailable_or_incomplete");
    }

    [Fact]
    public void FramedHierarchy_RejectsAmbiguityAndForeignItems_Issue5351()
    {
        const string ambiguous = "class A\n{\n void Leaf(int x) { }\n void Leaf(string x) { }\n void Caller() { Leaf(value); }\n}\n";
        using var fixture = new Fixture(ambiguous);
        var first = fixture.Prepare(2, "Leaf");
        var second = fixture.Prepare(3, "Leaf");
        Assert.NotEqual(first["data"]!.GetValue<string>(), second["data"]!.GetValue<string>());
        AssertError(fixture.Position("textDocument/prepareCallHierarchy", 4, 17), -32803, "ambiguous_position");
        AssertError(fixture.Request("callHierarchy/incomingCalls", new { item = first }), -32803, "unresolved_or_ambiguous_calls");
        using var other = new Fixture(ambiguous);
        AssertError(other.Request("callHierarchy/incomingCalls", new { item = first }), -32801, "stale_item");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FramedHierarchy_RejectsHighDegreeWithoutPartialSuccess_Issue5351(bool incoming)
    {
        var source = new StringBuilder("class Calls\n{\n void Leaf() { }\n");
        for (var i = 0; i <= LspServer.MaxCallHierarchyItems; i++)
            source.AppendLine($" void Node{i}() {{ Leaf(); }}");
        source.AppendLine(" void Root() {");
        for (var i = 0; i <= LspServer.MaxCallHierarchyItems; i++)
            source.AppendLine($" Node{i}();");
        source.AppendLine(" }\n}");
        using var fixture = new Fixture(source.ToString());
        var item = incoming ? fixture.Prepare(2, "Leaf") : fixture.Prepare(LspServer.MaxCallHierarchyItems + 4, "Root");
        AssertError(fixture.Request(incoming ? "callHierarchy/incomingCalls" : "callHierarchy/outgoingCalls", new { item }), -32803, "call_item_budget_exceeded");
    }

    [Fact]
    public void FramedHierarchy_CancelsAndKeepsSessionUsable_Issue5351()
    {
        using var fixture = new Fixture(Source);
        var leaf = fixture.Prepare(2, "Leaf");
        fixture.Server.BeforeCallHierarchyForTesting = token =>
        {
            fixture.Notify("$/cancelRequest", new { id = 5351 });
            token.ThrowIfCancellationRequested();
        };
        var cancelled = fixture.Request("callHierarchy/incomingCalls", new { item = leaf });
        Assert.Equal(-32800, cancelled["error"]!["code"]!.GetValue<int>());
        fixture.Server.BeforeCallHierarchyForTesting = null;
        Assert.Single(fixture.Expand(leaf, incoming: true));
        fixture.Notify("textDocument/didOpen", new { textDocument = new { uri = fixture.Uri, version = 1, text = new string('x', LspServer.MaxPositionDocumentBytes + 1) } });
        AssertError(fixture.Request("callHierarchy/incomingCalls", new { item = leaf }), -32801, "live_document_evicted_reconnect_required");
    }

    [Fact]
    public void FramedHierarchy_RejectsOversizedResponseAndKeepsSessionUsable_Issue5351()
    {
        var source = new StringBuilder("class Calls\n{\n void Leaf() {}\n");
        for (var i = 0; i < LspServer.MaxCallHierarchyItems; i++)
            source.AppendLine($" void {new string('節', 2048)}{i}() {{ Leaf(); }}");
        source.AppendLine("}");
        using var fixture = new Fixture(source.ToString());
        var leaf = fixture.Prepare(2, "Leaf");
        AssertError(fixture.Request("callHierarchy/incomingCalls", new { item = leaf }), -32803, "response_budget_exceeded");
        Assert.Empty(fixture.Expand(leaf, incoming: false));
        Assert.Equal(leaf["data"]!.GetValue<string>(), fixture.Prepare(2, "Leaf")["data"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("Calls.cs", "calls.cs")]
    [InlineData("Calls[0].cs", "Calls0.cs")]
    [InlineData("Calls%.cs", "CallsX.cs")]
    [InlineData("Calls_.cs", "CallsX.cs")]
    [InlineData("Calls.cs", "Calls.cs/Foreign.cs")]
    public void FramedNavigation_UsesExactFileIdentity_Issue5351_Issue5373(string fileName, string foreignPath)
    {
        const string source = "class B\n{\n public void Leaf() {}\n}\nclass Calls\n{\n void Caller(B b)\n {\n        b.Leaf();\n }\n}";
        const string foreign = "class Foreign\n{\n\n\n\n\n\n\n     void Leaf() {}\n}";
        using var fixture = new Fixture(source, fileName, additionalFiles: new Dictionary<string, string> { ["Foreign.cs"] = foreign });
        // Model case-colliding indexed files even on a case-insensitive test filesystem.
        // The foreign file must never be selected or read for this document's position.
        using (var db = new DbContext(DbOpenIntent.WriteIndex, fixture.DbPath))
        using (var command = db.Connection.CreateCommand())
        {
            command.CommandText = "UPDATE files SET path = @path WHERE path = 'Foreign.cs'";
            command.Parameters.AddWithValue("@path", foreignPath);
            command.ExecuteNonQuery();
        }
        var declared = fixture.Prepare(2, "Leaf");
        var atCall = fixture.Prepare(8, "Leaf");
        Assert.Equal(fixture.Uri, atCall["uri"]!.GetValue<string>());
        Assert.Equal(declared["data"]!.GetValue<string>(), atCall["data"]!.GetValue<string>());
        foreach (var method in new[] { "textDocument/definition", "textDocument/declaration" })
        {
            var location = Assert.Single(fixture.Position(method, 8, 10)["result"]!.AsArray())!;
            Assert.Equal(fixture.Uri, location["uri"]!.GetValue<string>());
            AssertRange(location["range"]!, 2, 13, 17);
        }
        foreach (var position in new[] { (Line: 2, Column: 13), (Line: 8, Column: 10) })
        {
            var reference = Assert.Single(fixture.Position("textDocument/references", position.Line, position.Column)["result"]!.AsArray())!;
            Assert.Equal(fixture.Uri, reference["uri"]!.GetValue<string>());
            AssertRange(reference["range"]!, 8, 10, 14);
        }
    }

    [Fact]
    public void FramedHierarchy_BoundsMetadataForOverlappingCallables_Issue5351()
    {
        const int nestedCount = 40;
        var source = new StringBuilder("class Calls\n{\n void Leaf() {}\n void Root() {\n Leaf();\n");
        for (var i = 0; i < nestedCount; i++)
            source.AppendLine($" void Node{i}() {{\n Leaf();");
        for (var i = 0; i < 2000; i++)
            source.AppendLine("// " + new string('x', 1000));
        source.AppendLine(new string('}', nestedCount + 2));
        using var fixture = new Fixture(source.ToString());
        using (var db = new DbContext(DbOpenIntent.QueryOnly, fixture.DbPath))
        {
            var reader = new DbReader(db);
            var node = Assert.Single(reader.GetCallHierarchyDeclarations("Calls.cs", 6, 256).Where(symbol => symbol.Name == "Node0"));
            Assert.NotNull(reader.GetCallHierarchySymbol(node.SymbolId!.Value));
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 100; i++)
                Assert.NotNull(reader.GetCallHierarchySymbol(node.SymbolId.Value));
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(allocated < 4 * 1024 * 1024, $"Metadata queries allocated {allocated} bytes for overlapping 2 MiB ranges.");
        }
        var leaf = fixture.Prepare(2, "Leaf");
        Assert.Equal(nestedCount + 1, fixture.Expand(leaf, incoming: true).Count);
    }

    [Theory]
    [InlineData("java", "class Calls {\n void leaf() {}\n void caller() { leaf(); }\n}", 1)]
    [InlineData("js", "function leaf() {}\nfunction caller() { leaf(); }", 0)]
    [InlineData("ts", "function leaf() {}\nfunction caller() { leaf(); }", 0)]
    [InlineData("go", "package main\nfunc leaf() {}\nfunc caller() { leaf() }", 1)]
    [InlineData("rs", "fn leaf() {}\nfn caller() { leaf(); }", 0)]
    [InlineData("c", "void leaf() {}\nvoid caller() { leaf(); }", 0)]
    [InlineData("cpp", "void leaf() {}\nvoid caller() { leaf(); }", 0)]
    [InlineData("kt", "fun leaf() {}\nfun caller() { leaf() }", 0)]
    [InlineData("rb", "def leaf()\nend\ndef caller()\n leaf()\nend", 0)]
    [InlineData("php", "<?php\nfunction leaf() {}\nfunction caller() { leaf(); }", 1)]
    [InlineData("swift", "func leaf() {}\nfunc caller() { leaf() }", 0)]
    public void FramedHierarchy_UsesExistingLanguageGraph_Issue5351(string extension, string source, int declarationLine)
    {
        using var fixture = new Fixture(source, "Calls." + extension);
        var leaf = fixture.Prepare(declarationLine, "leaf");
        var edge = Assert.Single(fixture.Expand(leaf, incoming: true));
        Assert.Equal("caller", edge!["from"]!["name"]!.GetValue<string>());
        Assert.Single(fixture.Expand(edge["from"]!, incoming: false));
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(1001)]
    public void FramedHierarchy_BoundsRepeatedCallSites_Issue5351(int sites)
    {
        var source = "class Calls\n{\n void Leaf() {}\n void Caller() {\n"
            + string.Concat(Enumerable.Repeat(" Leaf();\n", sites)) + " }\n}";
        using var fixture = new Fixture(source);
        var leaf = fixture.Prepare(2, "Leaf");
        var response = fixture.Request("callHierarchy/incomingCalls", new { item = leaf });
        if (sites > LspServer.MaxCallHierarchySites)
            AssertError(response, -32803, "call_site_budget_exceeded");
        else
            Assert.Equal(sites, Assert.Single(response["result"]!.AsArray())!["fromRanges"]!.AsArray().Count);
    }

    [Fact]
    public void FramedHierarchy_RejectsConcurrentGenerationAndBadRanges_Issue5351()
    {
        using var fixture = new Fixture(Source);
        var leaf = fixture.Prepare(2, "Leaf");
        fixture.Server.BeforeCallHierarchyValidationForTesting = () =>
        {
            using var writerDb = new DbContext(DbOpenIntent.WriteIndex, fixture.DbPath);
            new DbWriter(writerDb).SetMeta("hierarchy_test_generation", "changed");
        };
        AssertError(fixture.Request("callHierarchy/incomingCalls", new { item = leaf }), -32801, "index_generation_changed");
        fixture.Server.BeforeCallHierarchyValidationForTesting = null;
        using (var writerDb = new DbContext(DbOpenIntent.WriteIndex, fixture.DbPath))
        using (var command = writerDb.Connection.CreateCommand())
        {
            command.CommandText = "UPDATE symbol_references SET column_number = 1 WHERE reference_kind = 'call'";
            command.ExecuteNonQuery();
        }
        leaf = fixture.Prepare(2, "Leaf");
        AssertError(fixture.Request("callHierarchy/incomingCalls", new { item = leaf }), -32803, "call_site_range_unavailable");
        fixture.Notify("textDocument/didOpen", new { textDocument = new { uri = LspServer.PathToUri(Path.Combine(Path.GetDirectoryName(fixture.SourcePath)!, "New.cs")), version = 1, text = "class New {}" } });
        AssertError(fixture.Request("callHierarchy/incomingCalls", new { item = leaf }), -32801, "open_document_not_indexed");
    }

    [Fact]
    public void FramedHierarchy_UnsupportedLanguageReturnsNull_Issue5351()
    {
        using var fixture = new Fixture("# Heading\n", "Notes.md");
        var response = fixture.Position("textDocument/prepareCallHierarchy", 0, 2);
        Assert.Null(response["error"]);
        Assert.Null(response["result"]);
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf8-bom")]
    [InlineData("utf16-le")]
    [InlineData("utf16-be")]
    public void FramedHierarchy_UsesIndexerEncodingAndNewlinePolicy_Issue5351(string encodingName)
    {
        var encoding = encodingName switch
        {
            "utf8-bom" => new UTF8Encoding(true),
            "utf16-le" => Encoding.Unicode,
            "utf16-be" => Encoding.BigEndianUnicode,
            _ => new UTF8Encoding(false),
        };
        using var fixture = new Fixture(Source.ReplaceLineEndings("\r\n"), encoding: encoding);
        var leaf = fixture.Prepare(2, "Leaf");
        var edge = Assert.Single(fixture.Expand(leaf, incoming: true));
        Assert.Equal(2, edge!["fromRanges"]!.AsArray().Count);
        fixture.Notify("textDocument/didOpen", new { textDocument = new { uri = fixture.Uri, version = 1, text = Source } });
        Assert.Single(fixture.Expand(leaf, incoming: true));
    }

    private static void AssertError(JsonObject response, int code, string reason)
    {
        Assert.Equal(code, response["error"]?["code"]?.GetValue<int>());
        Assert.Equal(reason, response["error"]?["data"]?["reason"]?.GetValue<string>());
        Assert.False(response.ContainsKey("result"));
    }

    private static void AssertRange(JsonNode range, int line, int start, int end)
    {
        Assert.Equal(line, range["start"]!["line"]!.GetValue<int>());
        Assert.Equal(line, range["end"]!["line"]!.GetValue<int>());
        Assert.Equal(start, range["start"]!["character"]!.GetValue<int>());
        Assert.Equal(end, range["end"]!["character"]!.GetValue<int>());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = TestProjectHelper.CreateTempProject("cdidx_lsp_hierarchy");
        internal string SourcePath { get; }
        internal string DbPath { get; }
        internal string Uri => LspServer.PathToUri(SourcePath);
        internal LspServer Server { get; }
        internal JsonNode Capabilities { get; }
        internal List<string> Notices { get; } = [];
        internal List<(JsonObject Message, int Bytes)> Messages { get; } = [];

        internal Fixture(string source, string fileName = "Calls.cs", Encoding? encoding = null,
            IReadOnlyDictionary<string, string>? additionalFiles = null, bool pretty = false)
        {
            SourcePath = Path.Combine(_root, fileName);
            DbPath = Path.Combine(_root, ".cdidx", "codeindex.db");
            File.WriteAllText(SourcePath, source, encoding ?? new UTF8Encoding(false));
            if (additionalFiles != null)
            {
                foreach (var file in additionalFiles)
                    File.WriteAllText(Path.Combine(_root, file.Key), file.Value);
            }
            Reindex();
            var db = new DbContext(DbOpenIntent.QueryOnly, DbPath);
            var jsonOptions = ProgramRunner.CreateDefaultJsonOptions();
            jsonOptions.WriteIndented = pretty;
            Server = new LspServer(db, DbPath, "test", jsonOptions, _root);
            Capabilities = Request("initialize", new { })["result"]!["capabilities"]!;
        }

        internal void Reindex()
        {
            var (exitCode, stdout, stderr) = QueryCommandTestSupport.CaptureConsole(() =>
                ProgramRunner.Run(["index", _root, "--db", DbPath, "--json"], ProgramRunner.CreateDefaultJsonOptions(), "test"));
            Assert.True(exitCode == 0, stdout + stderr);
        }

        internal JsonNode Prepare(int line, string name)
        {
            var column = File.ReadAllText(SourcePath).Split('\n')[line].IndexOf(name, StringComparison.Ordinal);
            var response = Position("textDocument/prepareCallHierarchy", line, column);
            Assert.True(response["error"] == null, response.ToJsonString());
            return Assert.Single(response["result"]!.AsArray())!;
        }

        internal JsonArray Expand(JsonNode item, bool incoming)
        {
            var response = Request(incoming ? "callHierarchy/incomingCalls" : "callHierarchy/outgoingCalls", new { item });
            Assert.True(response["error"] == null, response.ToJsonString());
            return response["result"]!.AsArray();
        }

        internal JsonObject Position(string method, int line, int character) =>
            Request(method, new { textDocument = new { uri = Uri }, position = new { line, character } });

        internal void Notify(string method, object parameters) =>
            Assert.Null(Server.HandleMessage(JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params = parameters })));

        internal JsonObject Request(string method, object parameters)
        {
            Messages.Clear();
            var payload = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 5351, method, @params = parameters });
            using var input = new MemoryStream(Encoding.UTF8.GetBytes($"Content-Length: {Encoding.UTF8.GetByteCount(payload)}\r\n\r\n{payload}"));
            using var output = new MemoryStream();
            Assert.Equal(0, Server.Run(input, output));
            output.Position = 0;
            JsonObject? response = null;
            while (LspServer.TryReadMessage(output, out var message))
            {
                var node = JsonNode.Parse(message)!.AsObject();
                Messages.Add((node, Encoding.UTF8.GetByteCount(message)));
                if (node.ContainsKey("id"))
                    response = node;
                else if (node["method"]?.GetValue<string>() == "window/logMessage")
                    Notices.Add(node["params"]!["message"]!.GetValue<string>());
            }
            return response ?? throw new InvalidDataException("Missing framed LSP response.");
        }

        public void Dispose()
        {
            Server.Dispose();
            TestProjectHelper.DeleteDirectory(_root);
        }
    }
}
