using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Models;

namespace CodeIndex.Tests;

[Collection("Console sensitive")]
public sealed class JsonEnvelopeWrapperIssue4882Tests
{
    private readonly JsonSerializerOptions _jsonOptions = ProgramRunner.CreateDefaultJsonOptions();

    [Fact]
    public void GraphBodySnippetProjection_PreservesCountAndPagination_Issue4882()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("bounded_graph_body_snippet_4882");
        try
        {
            var dbPath = CreateGraphFixture(projectRoot);

            foreach (var (command, query) in new[]
            {
                ("references", "TargetA"),
                ("refs", "TargetA"),
                ("callers", "TargetA"),
                ("callees", "Caller"),
            })
            {
                var firstArgs = new[]
                {
                    command, query, "--db", dbPath, "--json", "--body", "--snippet-lines", "3",
                    "--fields", "path,line,body_content,body_start_line,body_end_line,body_content_truncated,body_requested_start_line,body_requested_end_line,body_effective_start_line,body_effective_end_line,body_content_truncation_reasons,body_content_recovery,callsite_content,callsite_start_line,callsite_end_line,callsite_content_truncated,callsite_requested_start_line,callsite_requested_end_line,callsite_effective_start_line,callsite_effective_end_line,callsite_content_truncation_reasons,callsite_content_recovery,callsite_line,callsite_column,callsite_length,callsite_selection,callsite_reference_count,callsite_omitted_reference_count,callsite_content_unavailable_reason",
                    "--limit", "1", "--max-json-bytes", "16384",
                    "--exact",
                };
                var (firstExitCode, firstStdout, firstStderr) = CaptureConsole(
                    () => ProgramRunner.Run(firstArgs, _jsonOptions, "1.0.0-test"));

                Assert.Equal(CommandExitCodes.Success, firstExitCode);
                Assert.Equal(string.Empty, firstStderr);
                using var firstDocument = JsonDocument.Parse(firstStdout);
                var firstMetadata = firstDocument.RootElement.GetProperty("metadata");
                Assert.True(firstMetadata.TryGetProperty("total_count_authoritative", out _));
                Assert.Equal(2, firstMetadata.GetProperty("total_count").GetInt32());
                Assert.True(firstMetadata.GetProperty("has_more").GetBoolean());
                var cursor = Assert.IsType<string>(firstMetadata.GetProperty("next_cursor").GetString());
                var firstResult = firstDocument.RootElement.GetProperty("results")[0];
                Assert.False(string.IsNullOrWhiteSpace(firstResult.GetProperty("body_content").GetString()));
                foreach (var field in new[]
                {
                    "body_start_line",
                    "body_end_line",
                    "body_requested_start_line",
                    "body_requested_end_line",
                    "body_effective_start_line",
                    "body_effective_end_line",
                })
                {
                    Assert.True(firstResult.TryGetProperty(field, out _), field);
                }
                Assert.False(string.IsNullOrWhiteSpace(firstResult.GetProperty("callsite_content").GetString()));
                Assert.Equal("first_reference", firstResult.GetProperty("callsite_selection").GetString());
                Assert.True(firstResult.GetProperty("callsite_line").GetInt32() > 0);
                Assert.True(firstResult.GetProperty("callsite_column").GetInt32() > 0);
                Assert.True(firstResult.GetProperty("callsite_reference_count").GetInt32() > 0);
                Assert.True(firstResult.GetProperty("callsite_omitted_reference_count").GetInt32() >= 0);
                foreach (var field in new[]
                {
                    "callsite_start_line",
                    "callsite_end_line",
                    "callsite_requested_start_line",
                    "callsite_requested_end_line",
                    "callsite_effective_start_line",
                    "callsite_effective_end_line",
                })
                {
                    Assert.True(firstResult.TryGetProperty(field, out _), field);
                }
                if (firstResult.TryGetProperty("body_content_truncated", out var bodyContentTruncated))
                {
                    Assert.True(bodyContentTruncated.GetBoolean());
                    Assert.True(firstResult.TryGetProperty("body_content_truncation_reasons", out _));
                    Assert.True(firstResult.TryGetProperty("body_content_recovery", out _));
                }
                if (firstResult.TryGetProperty("callsite_content_truncated", out var callsiteContentTruncated))
                {
                    Assert.True(callsiteContentTruncated.GetBoolean());
                    Assert.True(firstResult.TryGetProperty("callsite_content_truncation_reasons", out _));
                    Assert.True(firstResult.TryGetProperty("callsite_content_recovery", out _));
                }

                var secondArgs = firstArgs.Concat(["--cursor", cursor]).ToArray();
                var (secondExitCode, secondStdout, secondStderr) = CaptureConsole(
                    () => ProgramRunner.Run(secondArgs, _jsonOptions, "1.0.0-test"));

                Assert.Equal(CommandExitCodes.Success, secondExitCode);
                Assert.Equal(string.Empty, secondStderr);
                using var secondDocument = JsonDocument.Parse(secondStdout);
                var secondMetadata = secondDocument.RootElement.GetProperty("metadata");
                Assert.Equal(1, secondMetadata.GetProperty("cursor_offset").GetInt32());
                Assert.Equal(2, secondMetadata.GetProperty("total_count").GetInt32());
                Assert.False(secondMetadata.GetProperty("has_more").GetBoolean());
                Assert.Equal(JsonValueKind.Null, secondMetadata.GetProperty("next_cursor").ValueKind);
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void GraphBodyIntent_IsIndependentFromBoundedProjection_Issue5094()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("bounded_graph_body_intent_5094");
        try
        {
            var dbPath = CreateGraphFixture(projectRoot);
            var projections = new (string? Fields, bool ExpectBody, bool ExpectCallsite)[]
            {
                ("path,line", false, false),
                ("file,line", false, false),
                ("body_content,body_content_truncated,body_content_recovery", true, false),
                ("callsite_content,callsite_line,callsite_column,callsite_selection", false, true),
                ("all", true, true),
            };

            foreach (var (command, query) in new[]
            {
                ("references", "TargetA"),
                ("refs", "TargetA"),
                ("callers", "TargetA"),
                ("callees", "Caller"),
            })
            {
                var unprojectedArgs = new[]
                {
                    command, query, "--db", dbPath, "--json", "--body", "--snippet-lines", "3",
                    "--limit", "1", "--exact-name",
                };
                var (unprojectedExitCode, unprojectedStdout, unprojectedStderr) = CaptureConsole(
                    () => ProgramRunner.Run(unprojectedArgs, _jsonOptions, "1.0.0-test"));

                Assert.Equal(CommandExitCodes.Success, unprojectedExitCode);
                Assert.Equal(string.Empty, unprojectedStderr);
                using (var unprojectedDocument = JsonDocument.Parse(unprojectedStdout))
                {
                    Assert.False(string.IsNullOrWhiteSpace(
                        unprojectedDocument.RootElement.GetProperty("body_content").GetString()));
                    Assert.False(string.IsNullOrWhiteSpace(
                        unprojectedDocument.RootElement.GetProperty("callsite_content").GetString()));
                }

                foreach (var projection in projections)
                {
                    foreach (var compact in new[] { false, true })
                    {
                        var args = new List<string>
                        {
                            command, query, "--db", dbPath, "--json", "--body", "--snippet-lines", "3",
                            "--limit", "1", "--max-json-bytes", "32768", "--exact-name",
                        };
                        if (compact)
                            args.Add("--compact");
                        if (projection.Fields is not null)
                        {
                            args.Add("--fields");
                            args.Add(projection.Fields);
                        }

                        var (exitCode, stdout, stderr) = CaptureConsole(
                            () => ProgramRunner.Run([.. args], _jsonOptions, "1.0.0-test"));

                        Assert.Equal(CommandExitCodes.Success, exitCode);
                        Assert.Equal(string.Empty, stderr);
                        using var document = JsonDocument.Parse(stdout);
                        var result = document.RootElement.GetProperty("results")[0];
                        Assert.Equal(projection.ExpectBody, result.TryGetProperty("body_content", out _));
                        if (!projection.ExpectBody)
                        {
                            Assert.DoesNotContain(
                                result.EnumerateObject().Select(property => property.Name),
                                propertyName => propertyName.StartsWith("body_", StringComparison.Ordinal));
                        }
                        Assert.Equal(projection.ExpectCallsite, result.TryGetProperty("callsite_content", out _));
                        if (!projection.ExpectCallsite)
                        {
                            Assert.DoesNotContain(
                                result.EnumerateObject().Select(property => property.Name),
                                propertyName => propertyName.StartsWith("callsite_", StringComparison.Ordinal));
                        }
                    }
                }
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void GraphExplicitProjection_PreservesCompactEnvelopeAndEmptyResults_Issue5094()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("bounded_graph_compact_projection_5094");
        try
        {
            var dbPath = CreateGraphFixture(projectRoot);
            foreach (var (command, query) in new[]
            {
                ("references", "TargetA"),
                ("refs", "TargetA"),
                ("callers", "TargetA"),
                ("callees", "Caller"),
            })
            {
                foreach (var compactArgs in new[]
                {
                    new[] { "--compact" },
                    new[] { "--format", "compact" },
                })
                {
                    var commonArgs = new[]
                        {
                            "--db", dbPath, "--json", "--fields", "path,line", "--limit", "1",
                            "--max-json-bytes", "8192", "--exact-name",
                        }
                        .Concat(compactArgs)
                        .ToArray();
                    var (matchExitCode, matchStdout, matchStderr) = CaptureConsole(
                        () => ProgramRunner.Run(
                            [command, query, .. commonArgs],
                            _jsonOptions,
                            "1.0.0-test"));

                    Assert.Equal(CommandExitCodes.Success, matchExitCode);
                    Assert.Equal(string.Empty, matchStderr);
                    using (var matchDocument = JsonDocument.Parse(matchStdout))
                    {
                        Assert.Equal("compact", matchDocument.RootElement.GetProperty("format").GetString());
                        Assert.Single(matchDocument.RootElement.GetProperty("results").EnumerateArray());
                    }

                    // The pre-existing zero-match callees contract is tracked separately by #5128.
                    if (command == "callees")
                        continue;

                    var (emptyExitCode, emptyStdout, emptyStderr) = CaptureConsole(
                        () => ProgramRunner.Run(
                            [command, "DefinitelyNoSuchSymbol_5094", .. commonArgs],
                            _jsonOptions,
                            "1.0.0-test"));

                    Assert.Equal(CommandExitCodes.Success, emptyExitCode);
                    Assert.Equal(string.Empty, emptyStderr);
                    using var emptyDocument = JsonDocument.Parse(emptyStdout);
                    Assert.True(emptyDocument.RootElement.TryGetProperty("format", out var emptyFormat), emptyStdout);
                    Assert.Equal("compact", emptyFormat.GetString());
                    Assert.True(emptyDocument.RootElement.TryGetProperty("results", out var emptyResults), emptyStdout);
                    Assert.Empty(emptyResults.EnumerateArray());
                }
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void GraphBodyIntentValidation_PrecedesProjectionAndDatabaseAccess_Issue5094()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("bounded_graph_body_validation_5094");
        try
        {
            var missingDbPath = Path.Combine(projectRoot, "missing.db");
            foreach (var command in new[] { "references", "refs", "callers", "callees" })
            {
                foreach (var (bodyArgs, expectedMessage, unexpectedMessage) in new[]
                {
                    (new[] { "--snippet-lines", "3" }, "--snippet-lines requires --body", "database"),
                    (new[] { "--body", "--snippet-lines", "21" }, "--snippet-lines must be less than or equal to 20", "--snippet-lines requires --body"),
                })
                {
                    var args = new[]
                        {
                            command, "Target", "--db", missingDbPath, "--json", "--fields", "path,line",
                            "--max-json-bytes", "8192",
                        }
                        .Concat(bodyArgs)
                        .ToArray();
                    var (exitCode, stdout, stderr) = CaptureConsole(
                        () => ProgramRunner.Run(args, _jsonOptions, "1.0.0-test"));
                    var diagnostic = stdout + stderr;

                    Assert.Equal(CommandExitCodes.UsageError, exitCode);
                    Assert.Contains(CommandErrorCodes.UsageError, diagnostic);
                    Assert.Contains(expectedMessage, diagnostic);
                    Assert.DoesNotContain(unexpectedMessage, diagnostic, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void GraphFieldDiscovery_RemainsIndependentFromBodyIntent_Issue5094()
    {
        foreach (var command in new[] { "references", "refs", "callers", "callees" })
        {
            var args = new[]
            {
                command, "--json", "--body", "--snippet-lines", "3", "--fields", "list",
            };
            var (exitCode, stdout, stderr) = CaptureConsole(
                () => ProgramRunner.Run(args, _jsonOptions, "1.0.0-test"));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = JsonDocument.Parse(stdout);
            var validFields = document.RootElement.GetProperty("valid_fields")
                .EnumerateArray()
                .Select(field => field.GetString())
                .ToArray();
            Assert.Contains("body_content", validFields);
            Assert.Contains("body_content_truncated", validFields);
            Assert.Contains("body_content_recovery", validFields);
            Assert.Contains("callsite_content", validFields);
            Assert.Contains("callsite_content_truncated", validFields);
            Assert.Contains("callsite_content_recovery", validFields);
            Assert.Contains("callsite_selection", validFields);
        }
    }

    [Fact]
    public void BoundedGraphCountReplay_PreservesVerbatimSnippetLikeQueries_Issue4882()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("bounded_graph_verbatim_snippet_query_4882");
        try
        {
            var dbPath = CreateGraphFixture(projectRoot);
            foreach (var command in new[] { "references", "callers", "callees" })
            {
                var queryForms = new[]
                {
                    new[] { "--query", "--snippet-lines" },
                    new[] { "--", "--snippet-lines" },
                    new[] { "--query=--snippet-lines" },
                };
                foreach (var queryForm in queryForms)
                {
                    var args = new[] { command, "--db", dbPath, "--json", "--fields", "path,line", "--limit", "1", "--max-json-bytes", "8192" }
                        .Concat(queryForm)
                        .ToArray();
                    var (exitCode, stdout, stderr) = CaptureConsole(
                        () => ProgramRunner.Run(args, _jsonOptions, "1.0.0-test"));

                    Assert.Equal(CommandExitCodes.Success, exitCode);
                    Assert.Equal(string.Empty, stderr);
                    using var document = JsonDocument.Parse(stdout);
                    var metadata = document.RootElement.GetProperty("metadata");
                    Assert.Equal("--snippet-lines", metadata.GetProperty("query_normalized").GetString());
                    Assert.Equal(2, metadata.GetProperty("total_count").GetInt32());
                    Assert.True(metadata.GetProperty("has_more").GetBoolean());
                    Assert.False(string.IsNullOrWhiteSpace(metadata.GetProperty("next_cursor").GetString()));
                }
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void CompactGraphSnippetValidation_UsesOriginalArgsBeforeDatabase_Issue4882()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("bounded_graph_compact_preflight_4882");
        try
        {
            var missingDbPath = Path.Combine(projectRoot, "missing.db");
            foreach (var command in new[] { "references", "callers", "callees" })
            {
                foreach (var compactArgs in new[]
                {
                    new[] { "--compact" },
                    new[] { "--format", "compact" },
                })
                {
                    var args = new[]
                        {
                            command, "Target", "--db", missingDbPath, "--body", "--snippet-lines", "3",
                        }
                        .Concat(compactArgs)
                        .ToArray();
                    var (exitCode, stdout, stderr) = CaptureConsole(
                        () => ProgramRunner.Run(args, _jsonOptions, "1.0.0-test"));

                    Assert.Equal(CommandExitCodes.UsageError, exitCode);
                    Assert.Equal(string.Empty, stdout);
                    Assert.Contains("--snippet-lines with --body requires text or JSON result output", stderr);
                    Assert.DoesNotContain("DB_NOT_FOUND", stderr, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("database", stderr, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    private static string CreateGraphFixture(string projectRoot)
    {
        var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
        TestProjectHelper.InsertIndexedFile(
            dbPath,
            "src/Session.cs",
            "csharp",
            """
            class Session
            {
                int TargetA() => 1;
                int TargetB() => 2;
                int Caller()
                {
                    return TargetA() + TargetB();
                }
                int Other()
                {
                    return TargetA();
                }
                int LiteralCallerOne() => 1;
                int LiteralCallerTwo() => 2;
            }
            """);
        using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
        using var select = db.Connection.CreateCommand();
        select.CommandText = "SELECT id FROM files WHERE path = 'src/Session.cs'";
        var fileId = Convert.ToInt32(select.ExecuteScalar());
        var writer = new DbWriter(db.Connection);
        writer.InsertReferences([
            CreateReference(fileId, "TargetA", 7, 16, "Caller"),
            CreateReference(fileId, "TargetB", 7, 28, "Caller"),
            CreateReference(fileId, "TargetA", 11, 16, "Other"),
            CreateReference(fileId, "--snippet-lines", 13, 35, "LiteralCallerOne"),
            CreateReference(fileId, "--snippet-lines", 14, 35, "LiteralCallerTwo"),
            CreateReference(fileId, "LiteralTargetOne", 7, 16, "--snippet-lines"),
            CreateReference(fileId, "LiteralTargetTwo", 7, 28, "--snippet-lines"),
        ]);
        writer.MarkGraphReady();
        writer.MarkFoldReady();
        return dbPath;
    }

    private static ReferenceRecord CreateReference(
        int fileId,
        string symbolName,
        int line,
        int column,
        string containerName)
        => new()
        {
            FileId = fileId,
            SymbolName = symbolName,
            ReferenceKind = "call",
            Line = line,
            Column = column,
            Context = $"        return {symbolName}();",
            ContainerKind = "function",
            ContainerName = containerName,
        };

    private static (int ExitCode, string Stdout, string Stderr) CaptureConsole(Func<int> action)
    {
        using var capture = ConsoleCapture.Start(captureOut: true, captureError: true);
        var exitCode = action();
        return (exitCode, capture.Out!.ToString()!, capture.Error!.ToString()!);
    }
}
