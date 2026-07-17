using CodeIndex.Database;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public partial class DbReaderTests
{
    [Fact]
    public void SymbolIdentity_LegacySchemaColumnsWithoutReadyMarkerKeepNameFallbackUntilRefresh()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_reference_identity_legacy_marker");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Target.cs", "csharp", """
                namespace LegacyIdentity;
                public static class Target
                {
                    public static void Execute() { }
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Caller.cs", "csharp", """
                namespace LegacyIdentity;
                public static class Caller
                {
                    public static void Invoke() => Target.Execute();
                }
                """);

            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            var writer = new DbWriter(db.Connection);
            writer.MarkGraphReady();
            writer.SetMeta(DbContext.ReferenceIdentityContractVersionMetaKey, null);
            using (var clearIdentity = db.Connection.CreateCommand())
            {
                clearIdentity.CommandText = """
                    DELETE FROM symbol_reference_candidates;
                    UPDATE symbol_references
                    SET source_symbol_id = NULL,
                        target_symbol_id = (SELECT id FROM symbols WHERE name = 'Execute' LIMIT 1),
                        target_symbol_key = 'stale-legacy-key',
                        resolution_state = 'resolved',
                        resolution_candidate_count = 1;
                    """;
                clearIdentity.ExecuteNonQuery();
            }

            using (var legacyReader = new DbReader(db.Connection))
            {
                var analysis = legacyReader.AnalyzeSymbol("Execute", limit: 20, lang: "csharp", exact: true);
                var bundle = Assert.Single(analysis.CandidateBundles!);
                Assert.False(bundle.IdentityScoped);
                Assert.Single(bundle.References);
                var reference = Assert.Single(legacyReader.SearchReferences("Execute", limit: 20, lang: "csharp", exact: true));
                Assert.Null(reference.TargetSymbolId);
                Assert.Null(reference.TargetSymbolKey);
                Assert.Null(reference.ResolutionState);
                Assert.Equal(0, reference.ResolutionCandidateCount);
            }

            writer.RefreshMutualRecursionFlags();
            Assert.True(writer.ReferenceIdentityContractMatchesCurrent());
            using var refreshedReader = new DbReader(db.Connection);
            Assert.True(Assert.Single(refreshedReader.AnalyzeSymbol("Execute", limit: 20, lang: "csharp", exact: true).CandidateBundles!).IdentityScoped);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void SymbolIdentity_QualifiedSameNameReferenceResolvesOnlyMatchingContainer()
    {
        InsertIndexedFile("src/identity/Alpha.cs", "csharp", """
            namespace Identity;
            public static class Alpha
            {
                public static void Execute() { }
            }
            """);
        InsertIndexedFile("src/identity/Beta.cs", "csharp", """
            namespace Identity;
            public static class Beta
            {
                public static void Execute() { }
            }
            """);
        InsertIndexedFile("src/identity/Caller.cs", "csharp", """
            namespace Identity;
            public static class Caller
            {
                public static void Invoke() => Beta.Execute();
            }
            """);

        var reference = Assert.Single(_reader.SearchReferences(
            "Execute",
            limit: 10,
            lang: "csharp",
            pathPatterns: ["src/identity/Caller.cs"],
            exact: true));
        Assert.Equal("resolved", reference.ResolutionState);
        Assert.NotNull(reference.TargetSymbolId);
        Assert.Equal(1, reference.ResolutionCandidateCount);

        var dependencies = _reader.GetFileDependencies(
            limit: 20,
            lang: "csharp",
            pathPatterns: ["src/identity/Caller.cs"]);
        Assert.Contains(dependencies, edge => edge.TargetPath == "src/identity/Beta.cs");
        Assert.DoesNotContain(dependencies, edge => edge.TargetPath == "src/identity/Alpha.cs");

        var impact = _reader.AnalyzeImpact(
            "Beta.Execute",
            maxDepth: 1,
            limit: 20,
            lang: "csharp",
            pathPatterns: ["src/identity/*"]);
        var caller = Assert.Single(impact.Callers);
        Assert.Equal("src/identity/Caller.cs", caller.Path);
        Assert.Equal("Invoke", caller.CallerName);
    }

    [Fact]
    public void SymbolIdentity_CSharpGlobalQualifierResolvesQualifiedEnumMember()
    {
        InsertIndexedFile("src/identity/GlobalStatus.cs", "csharp", """
            namespace Identity;
            public enum GlobalStatus
            {
                Ready,
            }
            """);
        var callerFileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/identity/GlobalCaller.cs",
            Lang = "csharp",
            Size = 1,
            Lines = 1,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertReferences([new ReferenceRecord
        {
            FileId = callerFileId,
            SymbolName = "Ready",
            ReferenceKind = "type_reference",
            Line = 1,
            Column = 38,
            Context = "return global::Identity.GlobalStatus.Ready;",
            TargetQualifier = "global::Identity.GlobalStatus",
        }]);

        var reference = Assert.Single(_reader.SearchReferences(
            "Ready",
            limit: 10,
            lang: "csharp",
            pathPatterns: ["src/identity/GlobalCaller.cs"],
            exact: true));

        Assert.Equal("resolved", reference.ResolutionState);
        Assert.NotNull(reference.TargetSymbolId);
        Assert.Equal(1, reference.ResolutionCandidateCount);
    }

    [Fact]
    public void SymbolIdentity_TypeQualifierMismatchDoesNotCreateNameOnlyDependency()
    {
        InsertIndexedFile("src/identity/UnrelatedMode.cs", "csharp", """
            namespace Identity;
            public enum UnrelatedMode
            {
                ReadWrite,
            }
            """);
        InsertIndexedFile("src/identity/FileUser.cs", "csharp", """
            using System.IO;
            namespace Identity;
            public static class FileUser
            {
                public static Stream Open(string path) => File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            }
            """);
        var sourceFileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/identity/FileUser.cs",
            Lang = "csharp",
            Size = 1,
            Lines = 7,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertReferences([new ReferenceRecord
        {
            FileId = sourceFileId,
            SymbolName = "ReadWrite",
            ReferenceKind = "type_reference",
            Line = 6,
            Column = 11,
            Context = "FileShare.ReadWrite",
            ContainerKind = "function",
            ContainerName = "Open",
        }]);

        var reference = Assert.Single(_reader.SearchReferences(
            query: null,
            limit: 10,
            lang: "csharp",
            pathPatterns: ["src/identity/FileUser.cs"])
            .Where(result => result.SymbolName == "ReadWrite"));
        Assert.Equal("unresolved", reference.ResolutionState);
        Assert.Null(reference.TargetSymbolId);
        Assert.Equal(0, reference.ResolutionCandidateCount);

        var dependencies = _reader.GetFileDependencies(
            limit: 20,
            lang: "csharp",
            pathPatterns: ["src/identity/FileUser.cs"]);
        Assert.DoesNotContain(dependencies, edge => edge.TargetPath == "src/identity/UnrelatedMode.cs");
    }

    [Fact]
    public void SymbolIdentity_UnqualifiedDynamicCallStaysUnresolvedWithoutGlobalCandidateAmplification()
    {
        InsertIndexedFile("src/identity/FirstService.cs", "csharp", """
            namespace Identity;
            public class FirstService
            {
                public void Process() { }
            }
            """);
        InsertIndexedFile("src/identity/SecondService.cs", "csharp", """
            namespace Identity;
            public class SecondService
            {
                public void Process() { }
            }
            """);
        InsertIndexedFile("src/identity/AmbiguousCaller.cs", "csharp", """
            namespace Identity;
            public class AmbiguousCaller
            {
                public void Invoke(dynamic service) => service.Process();
            }
            """);

        var reference = Assert.Single(_reader.SearchReferences(
            "Process",
            limit: 10,
            lang: "csharp",
            pathPatterns: ["src/identity/AmbiguousCaller.cs"],
            exact: true));
        Assert.Equal("unresolved", reference.ResolutionState);
        Assert.Null(reference.TargetSymbolId);
        Assert.Equal(0, reference.ResolutionCandidateCount);

        using (var candidateCount = _db.Connection.CreateCommand())
        {
            candidateCount.CommandText = """
                SELECT COUNT(*)
                FROM symbol_reference_candidates AS candidate
                JOIN symbol_references AS reference ON reference.id = candidate.reference_id
                WHERE reference.file_id = (SELECT id FROM files WHERE path = 'src/identity/AmbiguousCaller.cs')
                  AND reference.symbol_name = 'Process'
                """;
            Assert.Equal(0L, (long)candidateCount.ExecuteScalar()!);
        }

        var dependencies = _reader.GetFileDependencies(
            limit: 20,
            lang: "csharp",
            pathPatterns: ["src/identity/AmbiguousCaller.cs"]);
        Assert.DoesNotContain(dependencies, edge => edge.TargetPath == "src/identity/FirstService.cs");
        Assert.DoesNotContain(dependencies, edge => edge.TargetPath == "src/identity/SecondService.cs");

        var callers = _reader.GetCallers(
            "Process",
            limit: 20,
            lang: "csharp",
            pathPatterns: ["src/identity/AmbiguousCaller.cs"],
            exact: true);
        var caller = Assert.Single(callers);
        Assert.Equal("Invoke", caller.CallerName);
        Assert.False(caller.HasSelfReference);
        Assert.False(caller.HasMutualRecursion);
        Assert.Equal(1, _reader.CountCallers(
            "Process",
            limit: 20,
            lang: "csharp",
            pathPatterns: ["src/identity/AmbiguousCaller.cs"],
            exact: true));
    }

    [Fact]
    public void SymbolIdentity_ReceiverQualifiedUniqueDynamicCallStaysUnresolved()
    {
        InsertIndexedFile("src/identity/OnlyService.cs", "csharp", """
            namespace Identity;
            public class OnlyService
            {
                public void ProcessUniqueDynamic() { }
            }
            """);
        InsertIndexedFile("src/identity/DynamicCaller.cs", "csharp", """
            namespace Identity;
            public class DynamicCaller
            {
                public void Invoke(dynamic service) => service.ProcessUniqueDynamic();
            }
            """);

        var reference = Assert.Single(_reader.SearchReferences(
            "ProcessUniqueDynamic",
            limit: 10,
            lang: "csharp",
            pathPatterns: ["src/identity/DynamicCaller.cs"],
            exact: true));
        Assert.Equal("unresolved", reference.ResolutionState);
        Assert.Null(reference.TargetSymbolId);
        Assert.Equal(0, reference.ResolutionCandidateCount);

        var bundle = Assert.Single(_reader.AnalyzeSymbol(
            "ProcessUniqueDynamic",
            limit: 10,
            lang: "csharp",
            pathPatterns: ["src/identity/*"],
            exact: true).CandidateBundles!);
        Assert.Empty(bundle.References);
        Assert.Empty(bundle.Callers);

        var dependencies = _reader.GetFileDependencies(
            limit: 20,
            lang: "csharp",
            pathPatterns: ["src/identity/DynamicCaller.cs"]);
        Assert.DoesNotContain(dependencies, edge => edge.TargetPath == "src/identity/OnlyService.cs");
    }

    [Theory]
    [InlineData(
        "python",
        "py",
        "process_candidate",
        "def process_candidate():\n    return 1\n",
        "def invoke():\n    return process_candidate()\n")]
    [InlineData(
        "javascript",
        "js",
        "processCandidate",
        "function processCandidate() { return 1; }\n",
        "function invoke() { return processCandidate(); }\n")]
    public void SymbolIdentity_NonCSharpBroadAmbiguityDoesNotPersistCrossProductOrAttributeCallers(
        string lang,
        string extension,
        string symbolName,
        string definitionSource,
        string callerSource)
    {
        var firstPath = $"src/identity/{lang}/first.{extension}";
        var secondPath = $"src/identity/{lang}/second.{extension}";
        var callerPath = $"src/identity/{lang}/caller.{extension}";
        InsertIndexedFile(firstPath, lang, definitionSource);
        InsertIndexedFile(secondPath, lang, definitionSource);
        InsertIndexedFile(callerPath, lang, callerSource);

        var reference = Assert.Single(_reader.SearchReferences(
            symbolName,
            limit: 10,
            lang: lang,
            pathPatterns: [callerPath],
            exact: true));
        Assert.Equal("unresolved", reference.ResolutionState);
        Assert.Equal(0, reference.ResolutionCandidateCount);

        using (var candidateCount = _db.Connection.CreateCommand())
        {
            candidateCount.CommandText = """
                SELECT COUNT(*)
                FROM symbol_reference_candidates AS candidate
                JOIN symbol_references AS reference ON reference.id = candidate.reference_id
                JOIN files AS source_file ON source_file.id = reference.file_id
                WHERE source_file.path = @path
                  AND reference.symbol_name = @symbol
                """;
            candidateCount.Parameters.AddWithValue("@path", callerPath);
            candidateCount.Parameters.AddWithValue("@symbol", symbolName);
            Assert.Equal(0L, (long)candidateCount.ExecuteScalar()!);
        }

        var analysis = _reader.AnalyzeSymbol(
            symbolName,
            limit: 10,
            lang: lang,
            pathPatterns: [$"src/identity/{lang}/*"],
            exact: true);
        Assert.Equal(2, analysis.CandidateBundles!.Count);
        Assert.All(analysis.CandidateBundles, bundle => Assert.Empty(bundle.Callers));
    }

    [Fact]
    public void SymbolIdentity_UnqualifiedUniqueOverloadFamilyResolvesAsGroup()
    {
        InsertIndexedFile("src/identity/UniqueProcessor.cs", "csharp", """
            namespace Identity;
            public static class UniqueProcessor
            {
                public static void ProcessUnique(int value) { }
                public static void ProcessUnique(string value) { }
            }
            """);
        var callerFileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/identity/UniqueProcessorCaller.cs",
            Lang = "csharp",
            Size = 1,
            Lines = 1,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertReferences([new ReferenceRecord
        {
            FileId = callerFileId,
            SymbolName = "ProcessUnique",
            ReferenceKind = "call",
            Line = 1,
            Column = 1,
            Context = "ProcessUnique(1);",
        }]);

        var reference = Assert.Single(_reader.SearchReferences(
            "ProcessUnique",
            limit: 10,
            lang: "csharp",
            pathPatterns: ["src/identity/UniqueProcessorCaller.cs"],
            exact: true));
        Assert.Equal("resolved_group", reference.ResolutionState);
        Assert.Null(reference.TargetSymbolId);
        Assert.NotNull(reference.TargetSymbolKey);
        Assert.Equal(2, reference.ResolutionCandidateCount);

        var dependency = Assert.Single(_reader.GetFileDependencies(
            limit: 10,
            lang: "csharp",
            pathPatterns: ["src/identity/UniqueProcessorCaller.cs"]));
        Assert.Equal("src/identity/UniqueProcessor.cs", dependency.TargetPath);
        Assert.Equal(1, dependency.ReferenceCount);
    }

    [Fact]
    public void SymbolIdentity_PartialClassContainerMatchOutranksUnrelatedSameFileMember()
    {
        InsertIndexedFile("src/identity/PartialCaller.cs", "csharp", """
            namespace Identity;
            public partial class PartialService
            {
                public void Invoke() => Helper();
            }

            public sealed class Unrelated
            {
                public void Helper() { }
            }
            """);
        InsertIndexedFile("src/identity/PartialTarget.cs", "csharp", """
            namespace Identity;
            public partial class PartialService
            {
                public void Helper() { }
            }
            """);
        _writer.RefreshMutualRecursionFlags();

        var reference = Assert.Single(_reader.SearchReferences(
            "Helper",
            limit: 10,
            lang: "csharp",
            pathPatterns: ["src/identity/PartialCaller.cs"],
            exact: true));
        Assert.Equal("resolved", reference.ResolutionState);
        Assert.Equal(1, reference.ResolutionCandidateCount);

        using var resolvedTarget = _db.Connection.CreateCommand();
        resolvedTarget.CommandText = """
            SELECT target_file.path
            FROM symbol_references AS reference
            JOIN symbol_reference_candidates AS candidate ON candidate.reference_id = reference.id
            JOIN symbols AS target ON target.id = candidate.symbol_id
            JOIN files AS target_file ON target_file.id = target.file_id
            WHERE reference.file_id = (SELECT id FROM files WHERE path = 'src/identity/PartialCaller.cs')
              AND reference.symbol_name = 'Helper'
            """;
        Assert.Equal("src/identity/PartialTarget.cs", resolvedTarget.ExecuteScalar() as string);
    }

    [Fact]
    public void SymbolIdentity_DeletionOnlyRefreshRemovesStaleCandidatesAndRestampsReadiness()
    {
        InsertIndexedFile("src/identity/DeletedTarget.cs", "csharp", """
            namespace Identity;
            public static class DeletedTarget
            {
                public static void ExecuteDeleted() { }
            }
            """);
        InsertIndexedFile("src/identity/DeletedCaller.cs", "csharp", """
            namespace Identity;
            public static class DeletedCaller
            {
                public static void Invoke() => DeletedTarget.ExecuteDeleted();
            }
            """);

        Assert.True(_writer.DeleteFileByPath("src/identity/DeletedTarget.cs"));
        _writer.RefreshMutualRecursionFlags();

        var reference = Assert.Single(_reader.SearchReferences(
            "ExecuteDeleted",
            limit: 10,
            lang: "csharp",
            pathPatterns: ["src/identity/DeletedCaller.cs"],
            exact: true));
        Assert.Equal("unresolved", reference.ResolutionState);
        Assert.Equal(0, reference.ResolutionCandidateCount);
        Assert.True(_writer.ReferenceIdentityContractMatchesCurrent());
    }

    [Fact]
    public void SymbolIdentity_DeleteFileDataInvalidatesOnlyWhenIdentityRowsAreDeleted()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/identity/cleanup.py",
            Lang = "python",
            Size = 1,
            Lines = 1,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertSymbols([new SymbolRecord
        {
            FileId = fileId,
            Kind = "function",
            Name = "cleanup_target",
            Line = 1,
            StartLine = 1,
            EndLine = 1,
        }]);
        _writer.RefreshMutualRecursionFlags();
        Assert.True(_writer.ReferenceIdentityContractMatchesCurrent());

        _writer.DeleteFileData(fileId);
        Assert.False(_writer.ReferenceIdentityContractMatchesCurrent());

        _writer.RefreshMutualRecursionFlags();
        var emptyFileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/identity/empty.py",
            Lang = "python",
            Size = 0,
            Lines = 0,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        Assert.True(_writer.ReferenceIdentityContractMatchesCurrent());

        _writer.DeleteFileData(emptyFileId);
        Assert.True(_writer.ReferenceIdentityContractMatchesCurrent());
    }

    [Fact]
    public void SymbolIdentity_ReferencePurgesInvalidateOnlyWhenRowsAreDeleted()
    {
        var unsupportedFileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/identity/legacy.graph",
            Lang = "legacy_graph",
            Size = 1,
            Lines = 1,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertReferences([new ReferenceRecord
        {
            FileId = unsupportedFileId,
            SymbolName = "LegacyCall",
            ReferenceKind = "call",
            Line = 1,
            Column = 1,
            Context = "LegacyCall()",
        }]);
        Assert.True(_writer.ReferenceIdentityContractMatchesCurrent());

        Assert.True(_writer.PurgeUnsupportedReferences(["csharp"]) > 0);
        Assert.False(_writer.ReferenceIdentityContractMatchesCurrent());

        _writer.RefreshMutualRecursionFlags();
        Assert.Equal(0, _writer.PurgeUnsupportedReferences(["csharp"]));
        Assert.True(_writer.ReferenceIdentityContractMatchesCurrent());

        var csharpFileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/identity/purge.cs",
            Lang = "csharp",
            Size = 1,
            Lines = 1,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertReferences([new ReferenceRecord
        {
            FileId = csharpFileId,
            SymbolName = "CurrentCall",
            ReferenceKind = "call",
            Line = 1,
            Column = 1,
            Context = "CurrentCall()",
        }]);
        Assert.True(_writer.ReferenceIdentityContractMatchesCurrent());

        Assert.True(_writer.PurgeAllReferences() > 0);
        Assert.False(_writer.ReferenceIdentityContractMatchesCurrent());

        _writer.RefreshMutualRecursionFlags();
        Assert.Equal(0, _writer.PurgeAllReferences());
        Assert.True(_writer.ReferenceIdentityContractMatchesCurrent());
    }

    [Fact]
    public void SymbolIdentity_CrossLanguageSameNameDoesNotCreateFalseMutualRecursion()
    {
        InsertIndexedFile("src/identity/CSharpRun.cs", "csharp", """
            namespace Identity;
            public static class CSharpRun
            {
                public static void Run() { }
                public static void Invoke() => Run();
            }
            """);
        InsertIndexedFile("src/identity/python_run.py", "python", """
            def Run():
                return 1

            def invoke():
                return Run()
            """);

        var csharpReference = Assert.Single(_reader.SearchReferences(
            "Run",
            limit: 10,
            lang: "csharp",
            pathPatterns: ["src/identity/CSharpRun.cs"],
            exact: true,
            excludeSelfReferences: false));
        var pythonReference = Assert.Single(_reader.SearchReferences(
            "Run",
            limit: 10,
            lang: "python",
            pathPatterns: ["src/identity/python_run.py"],
            exact: true,
            excludeSelfReferences: false));

        Assert.Equal("resolved", csharpReference.ResolutionState);
        Assert.Equal("resolved", pythonReference.ResolutionState);
        Assert.False(csharpReference.IsMutualRecursion);
        Assert.False(pythonReference.IsMutualRecursion);

        var ambiguousImpact = _reader.AnalyzeImpact(
            "Run",
            maxDepth: 2,
            limit: 20,
            pathPatterns: ["src/identity/*"]);
        Assert.Equal(2, ambiguousImpact.DefinitionCount);
        Assert.Empty(ambiguousImpact.Callers);
        Assert.Equal("multiple_definition_files", ambiguousImpact.ZeroResultReason);
    }
}
