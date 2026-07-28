using System.Text;
using System.Text.Json.Nodes;
using CodeIndex.Database;
using CodeIndex.Mcp;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public partial class McpServerTests
{
    [Fact]
    public void DiscoveryTools_PageEveryRowWithoutGapsOrDuplicates_Issue4853()
    {
        SeedIssue4853DiscoveryRows(5);

        AssertIssue4853Pages(
            "symbols",
            new JsonObject
            {
                ["names"] = new JsonArray("Issue4853", "Symbol"),
                ["path"] = "src/pagination4853",
                ["limit"] = 2,
                ["format"] = "compact",
            },
            "name");
        AssertIssue4853Pages(
            "files",
            new JsonObject
            {
                ["query"] = "pagination4853",
                ["limit"] = 2,
            },
            "path");
        AssertIssue4853Pages(
            "validate",
            new JsonObject
            {
                ["kind"] = "line_too_long",
                ["path"] = "src/pagination4853",
                ["limit"] = 2,
                ["format"] = "compact",
            },
            "path");

        var empty = CallIssue4853Tool(
            _server,
            "files",
            new JsonObject
            {
                ["query"] = "no-such-issue-4853-path",
                ["limit"] = 2,
            },
            id: 20);
        Assert.Equal(0, empty["total_count"]!.GetValue<int>());
        Assert.Equal(0, empty["returned_count"]!.GetValue<int>());
        Assert.False(empty["has_more"]!.GetValue<bool>());
        Assert.Null(empty["next_cursor"]);
        Assert.False(string.IsNullOrWhiteSpace(empty["result_stable_at"]!.GetValue<string>()));
    }

    [Fact]
    public void DiscoveryTools_ReturnTypedErrorsForMalformedMismatchedAndStaleCursors_Issue4853()
    {
        SeedIssue4853DiscoveryRows(3);
        var arguments = new JsonObject
        {
            ["query"] = "Issue4853Symbol",
            ["path"] = "src/pagination4853",
            ["limit"] = 1,
            ["format"] = "compact",
        };
        var first = CallIssue4853Tool(_server, "symbols", arguments, id: 1);
        var cursor = first["next_cursor"]!.GetValue<string>();

        var malformed = CallIssue4853ToolError(
            _server,
            "symbols",
            new JsonObject
            {
                ["query"] = "Issue4853Symbol",
                ["path"] = "src/pagination4853",
                ["limit"] = 1,
                ["format"] = "compact",
                ["cursor"] = "not-a-cursor",
            },
            id: 2);
        Assert.Equal("invalid_argument", malformed["category"]!.GetValue<string>());
        Assert.Equal("cursor_malformed", malformed["error_code"]!.GetValue<string>());
        Assert.True(malformed["restart_required"]!.GetValue<bool>());

        var mismatch = CallIssue4853ToolError(
            _server,
            "symbols",
            new JsonObject
            {
                ["query"] = "Issue4853Symbol",
                ["path"] = "src/pagination4853",
                ["kind"] = "class",
                ["limit"] = 1,
                ["format"] = "compact",
                ["cursor"] = cursor,
            },
            id: 3);
        Assert.Equal("invalid_argument", mismatch["category"]!.GetValue<string>());
        Assert.Equal("cursor_query_mismatch", mismatch["error_code"]!.GetValue<string>());

        var writer = new DbWriter(_db.Connection);
        writer.SetMeta(DbWriter.FoldBackfillGraphRefreshPendingMetaKey, "1");
        using (var foldRefreshedServer = new McpServer(_dbPath, "1.0.0-test", dbPathExplicit: true))
        {
            var foldStale = CallIssue4853ToolError(
                foldRefreshedServer,
                "symbols",
                new JsonObject
                {
                    ["query"] = "Issue4853Symbol",
                    ["path"] = "src/pagination4853",
                    ["limit"] = 1,
                    ["format"] = "compact",
                    ["cursor"] = cursor,
                },
                id: 4);
            Assert.Equal("index_stale", foldStale["category"]!.GetValue<string>());
            Assert.Equal("cursor_stale", foldStale["error_code"]!.GetValue<string>());
        }

        writer.SetMeta(DbWriter.FoldBackfillGraphRefreshPendingMetaKey, null);
        writer.SetMeta(DbContext.IndexedHeadTimestampMetaKey, "2026-07-28T02:00:00.0000000+00:00");
        using var refreshedServer = new McpServer(_dbPath, "1.0.0-test", dbPathExplicit: true);
        var stale = CallIssue4853ToolError(
            refreshedServer,
            "symbols",
            new JsonObject
            {
                ["query"] = "Issue4853Symbol",
                ["path"] = "src/pagination4853",
                ["limit"] = 1,
                ["format"] = "compact",
                ["cursor"] = cursor,
            },
            id: 5);
        Assert.Equal("index_stale", stale["category"]!.GetValue<string>());
        Assert.Equal("cursor_stale", stale["error_code"]!.GetValue<string>());
        Assert.True(stale["retry_safe"]!.GetValue<bool>());
    }

    [Fact]
    public void Symbols_ExactQualifiedRustQueryBindsPaginationTotalParameters_Issue4853()
    {
        const string path = "src/pagination4853/lib.rs";
        var writer = new DbWriter(_db.Connection);
        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = path,
            Lang = "rust",
            Size = 80,
            Lines = 4,
            Modified = ManualTimeProvider.FixtureUtcNow.UtcDateTime,
            Checksum = "issue-4853-rust-qualified",
        });
        writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "build",
                Line = 2,
                StartLine = 2,
                EndLine = 2,
                ContainerKind = "module",
                ContainerName = "macros",
                ContainerQualifiedName = "crate::macros",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "build",
                Line = 4,
                StartLine = 4,
                EndLine = 4,
                ContainerKind = "module",
                ContainerName = "other",
                ContainerQualifiedName = "crate::other",
            },
        ]);

        var payload = CallIssue4853Tool(
            _server,
            "symbols",
            new JsonObject
            {
                ["query"] = "crate::macros::build",
                ["lang"] = "rust",
                ["exactName"] = true,
                ["limit"] = 1,
                ["format"] = "compact",
            },
            id: 23);

        Assert.Equal(1, payload["total_count"]!.GetValue<int>());
        Assert.True(payload["total_count_authoritative"]!.GetValue<bool>());
        var result = Assert.Single(payload["results"]!.AsArray());
        Assert.Equal("build", result!["name"]!.GetValue<string>());
    }

    [Fact]
    public void DiscoveryTools_BindCursorsToEffectiveReadinessAndRuntimeFoldState_Issue4853()
    {
        SeedIssue4853DiscoveryRows(3);
        var validateArguments = new JsonObject
        {
            ["kind"] = "line_too_long",
            ["path"] = "src/pagination4853",
            ["limit"] = 1,
            ["format"] = "compact",
        };
        var validateCursor = CallIssue4853Tool(
            _server,
            "validate",
            validateArguments,
            id: 20)["next_cursor"]!.GetValue<string>();

        string paginationGeneration;
        using (var reader = new DbReader(_db))
        {
            paginationGeneration = reader.GetPaginationGeneration().Identity;
            var foldGeneration = reader.GetFoldPaginationGenerationIdentity();
            Assert.Contains(
                $"effective-fold-ready:{(reader._foldReady ? "1" : "0")}",
                foldGeneration,
                StringComparison.Ordinal);
            Assert.Contains(
                $"runtime-fold-version:{NameFold.Version}",
                foldGeneration,
                StringComparison.Ordinal);
            Assert.Contains(
                $"runtime-fold-fingerprint:{NameFold.Fingerprint()}",
                foldGeneration,
                StringComparison.Ordinal);
        }

        var writer = new DbWriter(_db.Connection);
        writer.ClearReadyFlags();
        using (var reader = new DbReader(_db))
        {
            Assert.Equal(paginationGeneration, reader.GetPaginationGeneration().Identity);
            Assert.Contains(
                "stored-issues-ready-bit:0",
                reader.GetIssuePaginationGenerationIdentity(),
                StringComparison.Ordinal);
        }

        using var readinessDemotedServer = new McpServer(
            _dbPath,
            "1.0.0-test",
            dbPathExplicit: true);
        validateArguments["cursor"] = validateCursor;
        var stale = CallIssue4853ToolError(
            readinessDemotedServer,
            "validate",
            validateArguments,
            id: 21);
        Assert.Equal("index_stale", stale["category"]!.GetValue<string>());
        Assert.Equal("cursor_stale", stale["error_code"]!.GetValue<string>());
        Assert.True(stale["retry_safe"]!.GetValue<bool>());

        validateArguments.Remove("cursor");
        var degradedResponse = CallIssue4853ToolResponse(
            readinessDemotedServer,
            "validate",
            validateArguments,
            id: 22);
        Assert.Null(degradedResponse["error"]);
        Assert.False(degradedResponse["result"]?["isError"]?.GetValue<bool>() ?? false);
        Assert.Equal(
            "Validation issue data is not current; results are non-authoritative.",
            degradedResponse["result"]!["content"]![0]!["text"]!.GetValue<string>());
        var degraded = degradedResponse["result"]!["structuredContent"]!.AsObject();
        Assert.Equal(0, degraded["total_count"]!.GetValue<int>());
        Assert.False(degraded["total_count_authoritative"]!.GetValue<bool>());
        Assert.True(degraded["issues_table_available"]!.GetValue<bool>());
        Assert.False(degraded["file_issues_data_current"]!.GetValue<bool>());
        Assert.Equal("unknown", degraded["summary"]!["actionability"]!.GetValue<string>());
        Assert.False(degraded["summary"]!["authoritative"]!.GetValue<bool>());
    }

    [Fact]
    public void Validate_RechecksIssueReadinessInsidePaginationSnapshot_Issue4853()
    {
        SeedIssue4853DiscoveryRows(1);
        using var readerOpenedWhileReady = new DbReader(_db);
        Assert.True(readerOpenedWhileReady._hasIssuesTable);

        var writer = new DbWriter(_db.Connection);
        writer.ClearReadyFlags();

        var snapshotState = readerOpenedWhileReady.RunInReadSnapshot(() => (
            Generation: readerOpenedWhileReady.GetIssuePaginationGenerationIdentity(),
            Current: readerOpenedWhileReady.IsIssueDataCurrentInSnapshot()));

        Assert.Contains("stored-issues-ready-bit:0", snapshotState.Generation, StringComparison.Ordinal);
        Assert.Contains("issues-table-available:1", snapshotState.Generation, StringComparison.Ordinal);
        Assert.Contains("effective-issues-ready:0", snapshotState.Generation, StringComparison.Ordinal);
        Assert.False(snapshotState.Current);
    }

    [Fact]
    public async Task DiscoveryCursor_IsStatelessAcrossConcurrentClientsAndFitsBoundedResponse_Issue4853()
    {
        SeedIssue4853DiscoveryRows(4);
        var first = CallIssue4853Tool(
            _server,
            "files",
            new JsonObject
            {
                ["query"] = "pagination4853",
                ["limit"] = 1,
            },
            id: 1);
        var cursor = first["next_cursor"]!.GetValue<string>();
        Assert.StartsWith("response:v2:", cursor, StringComparison.Ordinal);
        Assert.InRange(cursor.Length, 1, McpServer.MaxMcpQueryCursorCharacters);
        var status = CallIssue4853Tool(_server, "status", new JsonObject(), id: 10);
        Assert.Equal(
            McpServer.MaxMcpQueryCursorCharacters,
            status["mcp"]!["limits"]!["max_query_cursor_characters"]!.GetValue<int>());

        using var clientOne = new McpServer(_dbPath, "1.0.0-test", dbPathExplicit: true);
        using var clientTwo = new McpServer(_dbPath, "1.0.0-test", dbPathExplicit: true);
        JsonObject Arguments() => new()
        {
            ["query"] = "pagination4853",
            ["limit"] = 1,
            ["cursor"] = cursor,
        };

        var pages = await Task.WhenAll(
            Task.Run(() => CallIssue4853Tool(clientOne, "files", Arguments(), id: 2)),
            Task.Run(() => CallIssue4853Tool(clientTwo, "files", Arguments(), id: 3)));

        Assert.Equal(
            pages[0]["results"]!.ToJsonString(),
            pages[1]["results"]!.ToJsonString());
        Assert.Equal(1, pages[0]["cursor_offset"]!.GetValue<int>());
        Assert.True(
            clientOne.TrySerializeJsonNodeWithinByteLimitForTests(
                CallIssue4853ToolResponse(clientOne, "files", Arguments(), id: 4),
                maxBytes: 32 * 1024,
                out _,
                out var bytesWritten));
        Assert.InRange(bytesWritten, 1, 32 * 1024);
    }

    [Fact]
    public void DiscoveryTools_RejectCursorsAfterSameSecondCommittedIndexBatch_Issue4853()
    {
        SeedIssue4853DiscoveryRows(3);
        using (var fixTimestamp = _db.Connection.CreateCommand())
        {
            fixTimestamp.CommandText =
                "UPDATE files SET indexed_at = '2099-01-01T00:00:00.0000000Z'";
            fixTimestamp.ExecuteNonQuery();
        }

        var requests = new (string Tool, JsonObject Arguments)[]
        {
            (
                "symbols",
                new JsonObject
                {
                    ["query"] = "Issue4853Symbol",
                    ["path"] = "src/pagination4853",
                    ["limit"] = 1,
                    ["format"] = "compact",
                }),
            (
                "files",
                new JsonObject
                {
                    ["query"] = "pagination4853",
                    ["limit"] = 1,
                }),
            (
                "validate",
                new JsonObject
                {
                    ["kind"] = "line_too_long",
                    ["path"] = "src/pagination4853",
                    ["limit"] = 1,
                    ["format"] = "compact",
                }),
        };
        var cursors = requests.ToDictionary(
            request => request.Tool,
            request => CallIssue4853Tool(
                _server,
                request.Tool,
                request.Arguments,
                id: 30)["next_cursor"]!.GetValue<string>(),
            StringComparer.Ordinal);

        string[] beforeGeneration;
        using (var reader = new DbReader(_db))
            beforeGeneration = reader.GetPaginationGeneration().Identity.Split('\n');

        var writer = new DbWriter(_db.Connection);
        using (var transaction = writer.BeginTransaction())
        {
            const string path = "src/pagination4853/item-00.cs";
            const string replacement = "public sealed class Issue4853Symbol99 { }";
            var fileId = writer.UpsertFile(new FileRecord
            {
                Path = path,
                Lang = "csharp",
                Size = Encoding.UTF8.GetByteCount(replacement),
                Lines = 1,
                Modified = ManualTimeProvider.FixtureUtcNow.UtcDateTime,
                Checksum = "issue-4853-replacement",
            });
            writer.InsertSymbols(
            [
                new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "class",
                    Name = "Issue4853Symbol99",
                    Line = 1,
                    StartLine = 1,
                    EndLine = 1,
                    Signature = replacement,
                },
            ]);
            writer.InsertIssues(
                fileId,
                [
                    new FileIssue
                    {
                        Path = path,
                        Line = 1,
                        Kind = "line_too_long",
                        Severity = FileIssue.SeverityWarning,
                        Origin = FileIssue.OriginSourceLiteral,
                        Message = "Issue 4853 replacement diagnostic",
                    },
                ]);
            transaction.Commit();
        }

        string[] afterGeneration;
        using (var reader = new DbReader(_db))
            afterGeneration = reader.GetPaginationGeneration().Identity.Split('\n');
        Assert.Equal(beforeGeneration[..4], afterGeneration[..4]);
        Assert.NotEqual(beforeGeneration[4], afterGeneration[4]);

        using var refreshedServer = new McpServer(_dbPath, "1.0.0-test", dbPathExplicit: true);
        for (var index = 0; index < requests.Length; index++)
        {
            var request = requests[index];
            request.Arguments["cursor"] = cursors[request.Tool];
            var stale = CallIssue4853ToolError(
                refreshedServer,
                request.Tool,
                request.Arguments,
                id: 40 + index);
            Assert.Equal("index_stale", stale["category"]!.GetValue<string>());
            Assert.Equal("cursor_stale", stale["error_code"]!.GetValue<string>());
        }
    }

    private void AssertIssue4853Pages(string toolName, JsonObject arguments, string identityField)
    {
        var expectedTotal = 5;
        var identities = new List<string>();
        var pageSizes = new List<int>();
        string? cursor = null;
        for (var pageNumber = 0; pageNumber < 10; pageNumber++)
        {
            if (cursor is null)
                arguments.Remove("cursor");
            else
                arguments["cursor"] = cursor;

            var payload = CallIssue4853Tool(_server, toolName, arguments, id: 100 + pageNumber);
            var collectionName = toolName == "validate" ? "issues" : "results";
            var results = payload[collectionName]!.AsArray();
            pageSizes.Add(results.Count);
            identities.AddRange(results.Select(result => GetIssue4853Identity(result!, identityField)));
            Assert.Equal(expectedTotal, payload["total_count"]!.GetValue<int>());
            Assert.True(payload["total_count_authoritative"]!.GetValue<bool>());
            Assert.Equal(results.Count, payload["returned_count"]!.GetValue<int>());
            Assert.Equal(identities.Count - results.Count, payload["cursor_offset"]!.GetValue<int>());
            Assert.False(string.IsNullOrWhiteSpace(payload["result_stable_at"]!.GetValue<string>()));

            if (!payload["has_more"]!.GetValue<bool>())
            {
                Assert.Null(payload["next_cursor"]);
                break;
            }

            cursor = payload["next_cursor"]!.GetValue<string>();
            Assert.StartsWith("response:v2:", cursor, StringComparison.Ordinal);
        }

        Assert.Equal([2, 2, 1], pageSizes);
        Assert.Equal(expectedTotal, identities.Count);
        Assert.Equal(expectedTotal, identities.Distinct(StringComparer.Ordinal).Count());
    }

    private void SeedIssue4853DiscoveryRows(int count)
    {
        var writer = new DbWriter(_db.Connection);
        for (var index = 0; index < count; index++)
        {
            var path = $"src/pagination4853/item-{index:D2}.cs";
            var content = $"public sealed class Issue4853Symbol{index:D2} {{ }}";
            var fileId = writer.UpsertFile(new FileRecord
            {
                Path = path,
                Lang = "csharp",
                Size = Encoding.UTF8.GetByteCount(content),
                Lines = 1,
                Modified = ManualTimeProvider.FixtureUtcNow.AddMinutes(index).UtcDateTime,
                Checksum = $"issue-4853-{index:D2}",
            });
            writer.InsertChunks(
            [
                new ChunkRecord
                {
                    FileId = fileId,
                    ChunkIndex = 0,
                    StartLine = 1,
                    EndLine = 1,
                    Content = content,
                },
            ]);
            writer.InsertSymbols(
            [
                new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "class",
                    Name = $"Issue4853Symbol{index:D2}",
                    Line = 1,
                    StartLine = 1,
                    EndLine = 1,
                    Signature = content,
                },
            ]);
            writer.InsertIssues(
                fileId,
                [
                    new FileIssue
                    {
                        Path = path,
                        Line = 1,
                        Kind = "line_too_long",
                        Severity = FileIssue.SeverityWarning,
                        Origin = FileIssue.OriginSourceLiteral,
                        Message = $"Issue 4853 diagnostic {index:D2}",
                    },
                ]);
        }

        writer.MarkIssuesReady();
        writer.SetMeta(DbContext.IndexedHeadTimestampMetaKey, "2026-07-28T01:00:00.0000000+00:00");
    }

    private static JsonObject CallIssue4853Tool(
        McpServer server,
        string toolName,
        JsonObject arguments,
        int id)
    {
        var response = CallIssue4853ToolResponse(server, toolName, arguments, id);
        Assert.Null(response["error"]);
        Assert.False(response["result"]?["isError"]?.GetValue<bool>() ?? false);
        return response["result"]!["structuredContent"]!.AsObject();
    }

    private static JsonObject CallIssue4853ToolError(
        McpServer server,
        string toolName,
        JsonObject arguments,
        int id)
    {
        var response = CallIssue4853ToolResponse(server, toolName, arguments, id);
        Assert.True(response["result"] is JsonObject, response.ToJsonString());
        var result = response["result"]!.AsObject();
        Assert.True(result["isError"]?.GetValue<bool>() ?? false, response.ToJsonString());
        return result["structuredContent"]!.AsObject();
    }

    private static string GetIssue4853Identity(JsonNode result, string identityField)
    {
        var value = result[identityField]
            ?? result[char.ToUpperInvariant(identityField[0]) + identityField[1..]];
        Assert.NotNull(value);
        return value!.GetValue<string>();
    }

    private static JsonObject CallIssue4853ToolResponse(
        McpServer server,
        string toolName,
        JsonObject arguments,
        int id)
        => server.HandleMessage(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = toolName,
                ["arguments"] = arguments.DeepClone(),
            },
        })!.AsObject();
}
