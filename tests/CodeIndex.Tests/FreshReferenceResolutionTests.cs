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
    public void FreshDefaultsRevalidation_RequiresTransactionAndRejectsPersistedRows()
    {
        Assert.Throws<InvalidOperationException>(() =>
            _writer.CanUseFreshReferenceResolutionDefaultsInCurrentTransaction());

        using var transaction = _writer.BeginTransaction();
        Assert.True(_writer.CanUseFreshReferenceResolutionDefaultsInCurrentTransaction());

        InsertFile("src/concurrent.py", "python");

        Assert.False(_writer.CanUseFreshReferenceResolutionDefaultsInCurrentTransaction());
    }

    [Fact]
    public void InsertReferences_FreshDefaultsKeepParameterShapeAndUseSeparateCachedSql()
    {
        var fileId = InsertFile("src/provisional.py", "python");
        _writer.InsertSymbols([CreateSymbol(fileId, "Caller", line: 1)]);
        Assert.Equal(
            0L,
            ScalarLong($"""
                SELECT COUNT(*)
                FROM temp.sqlite_schema
                WHERE name = '{DbWriter.AuthoritativeFreshReferenceSourceSymbolsTableName}'
                """));
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
        Assert.Equal(
            1,
            ScalarLong("""
                SELECT COUNT(*)
                FROM symbol_references
                WHERE symbol_name = 'Fresh'
                  AND source_symbol_id IS NOT NULL
                """));
        Assert.Equal(
            0L,
            ScalarLong($"""
                SELECT COUNT(*)
                FROM temp.sqlite_schema
                WHERE name = '{DbWriter.AuthoritativeFreshReferenceSourceSymbolsTableName}'
                """));
        Assert.Equal(
            1,
            ScalarLong("""
                SELECT COUNT(*)
                FROM symbol_references
                WHERE symbol_name = 'Standard'
                  AND source_symbol_id IS NULL
                """));

        var freshSql = DbWriter.BuildReferenceInsertSqlForTesting(
            rowCount: 2,
            useFreshReferenceResolutionDefaults: true);
        var materializedFreshSql = DbWriter.BuildReferenceInsertSqlForTesting(
            rowCount: 2,
            useFreshReferenceResolutionDefaults: true,
            useMaterializedFreshSourceLookup: true);
        var standardSql = DbWriter.BuildReferenceInsertSqlForTesting(
            rowCount: 2,
            useFreshReferenceResolutionDefaults: false);
        Assert.Contains("WITH fresh_reference(", freshSql, StringComparison.Ordinal);
        Assert.Contains("input_ordinal", freshSql, StringComparison.Ordinal);
        Assert.Contains("source_symbol_id", freshSql, StringComparison.Ordinal);
        Assert.Contains("FROM fresh_reference AS r", freshSql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY r.input_ordinal", freshSql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY (COALESCE(s.end_line", freshSql, StringComparison.Ordinal);
        Assert.Equal(28, CountOccurrences(freshSql, "?"));
        Assert.DoesNotContain("?0", freshSql, StringComparison.Ordinal);
        Assert.Contains("FROM symbols AS s", freshSql, StringComparison.Ordinal);
        Assert.DoesNotContain(
            DbWriter.AuthoritativeFreshReferenceSourceSymbolsTableName,
            freshSql,
            StringComparison.Ordinal);
        Assert.Contains(
            $"FROM temp.{DbWriter.AuthoritativeFreshReferenceSourceSymbolsTableName} AS source",
            materializedFreshSql,
            StringComparison.Ordinal);
        Assert.DoesNotContain("FROM symbols AS s", materializedFreshSql, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(materializedFreshSql, "UNION"));
        Assert.DoesNotContain("UNION ALL", materializedFreshSql, StringComparison.Ordinal);
        Assert.Contains(
            "COALESCE(candidate.start_line, candidate.line) DESC",
            materializedFreshSql,
            StringComparison.Ordinal);
        Assert.Equal(28, CountOccurrences(materializedFreshSql, "?"));
        Assert.DoesNotContain("?0", materializedFreshSql, StringComparison.Ordinal);
        Assert.DoesNotContain("WITH fresh_reference(", standardSql, StringComparison.Ordinal);
        Assert.DoesNotContain("source_symbol_id", standardSql, StringComparison.Ordinal);
        Assert.Equal(28, CountOccurrences(standardSql, "?"));
        Assert.DoesNotContain("?0", standardSql, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() =>
            DbWriter.BuildReferenceInsertSqlForTesting(
                rowCount: 1,
                useFreshReferenceResolutionDefaults: false,
                useMaterializedFreshSourceLookup: true));

        var freshRefresh = DbWriter.SelectReferenceSourceRefreshSqlForTesting(
            useFreshReferenceResolutionDefaults: true,
            hasPersistedReferenceResolutionState: false);
        var ordinaryFull = DbWriter.SelectReferenceSourceRefreshSqlForTesting(
            useFreshReferenceResolutionDefaults: false,
            hasPersistedReferenceResolutionState: false);
        var differential = DbWriter.SelectReferenceSourceRefreshSqlForTesting(
            useFreshReferenceResolutionDefaults: false,
            hasPersistedReferenceResolutionState: true);
        Assert.Null(freshRefresh);
        Assert.DoesNotContain("r.source_symbol_id IS NOT", ordinaryFull, StringComparison.Ordinal);
        Assert.Contains("r.source_symbol_id IS NOT", differential, StringComparison.Ordinal);
    }

    [Fact]
    public void FreshReferenceInsert_AssignsCrossLanguageNestedSourcesWithoutFinalUpdate()
    {
        var csharpFileId = InsertFile("src/nested-source.cs", "csharp");
        var pythonFileId = InsertFile("src/nested_source.py", "python");
        _writer.InsertSymbols([
            CreateRangedSymbol(csharpFileId, "Caller", startLine: 1, endLine: 30),
            CreateRangedSymbol(csharpFileId, "Caller", startLine: 10, endLine: 20),
            CreateRangedSymbol(pythonFileId, "Caller", startLine: 1, endLine: 30),
            CreateRangedSymbol(pythonFileId, "Caller", startLine: 10, endLine: 20),
        ]);

        using var freshScope = _writer.BeginReferenceGraphRefreshScope(
            forceFullRefresh: true,
            useFreshReferenceResolutionDefaults: true);
        _writer.InsertReferences([
            CreateReference(csharpFileId, "CsOuter", line: 5),
            CreateReference(csharpFileId, "CsNested", line: 15),
            CreateReference(csharpFileId, "CsOutside", line: 31),
            CreateReference(pythonFileId, "PyOuter", line: 5),
            CreateReference(pythonFileId, "PyNested", line: 15),
            CreateReference(pythonFileId, "PyOutside", line: 31),
        ], refreshMutualRecursionFlags: false);

        Assert.Equal(1, ReadSourceLine("CsOuter"));
        Assert.Equal(10, ReadSourceLine("CsNested"));
        Assert.Null(ReadSourceLine("CsOutside"));
        Assert.Equal(1, ReadSourceLine("PyOuter"));
        Assert.Equal(10, ReadSourceLine("PyNested"));
        Assert.Null(ReadSourceLine("PyOutside"));
        Execute("""
            CREATE TEMP TRIGGER reject_fresh_source_rewrite
            BEFORE UPDATE OF source_symbol_id ON symbol_references
            BEGIN
                SELECT RAISE(ABORT, 'fresh source identity must be insert-complete');
            END;
            """);

        _writer.RefreshMutualRecursionFlags(stampReferenceIdentityContractReady: false);

        Execute("DROP TRIGGER reject_fresh_source_rewrite;");
        Assert.Equal(10, ReadSourceLine("CsNested"));
        Assert.Equal(10, ReadSourceLine("PyNested"));
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
        Assert.Contains(
            "FROM symbol_reference_candidates AS candidate",
            sql,
            StringComparison.Ordinal);
        Assert.Contains("INDEXED BY idx_symbol_ref_candidates_symbol", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE EXISTS", sql, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(sql, "UPDATE symbol_references AS r"));
    }

    [Fact]
    public void ReferenceResolutionFacts_ConstructTargetKeysOnceAcrossEveryRefreshScope()
    {
        foreach (var (scope, materializationSql, resolutionSql) in
                 DbWriter.ReferenceResolutionFactSqlForTesting)
        {
            Assert.True(
                materializationSql.Contains("target_file.path", StringComparison.Ordinal),
                $"{scope} target-key materialization omitted the physical fallback: {materializationSql}");
            Assert.Contains(
                "INSERT INTO temp.reference_resolution_symbol_facts",
                materializationSql,
                StringComparison.Ordinal);
            Assert.Contains("THEN 'family:'", materializationSql, StringComparison.Ordinal);
            Assert.Contains("target.family_key", materializationSql, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "target_file.path",
                resolutionSql,
                StringComparison.Ordinal);
            Assert.Contains(
                "JOIN temp.reference_resolution_symbol_facts AS target_fact",
                resolutionSql,
                StringComparison.Ordinal);
            Assert.DoesNotContain("COUNT(DISTINCT", resolutionSql, StringComparison.Ordinal);
            Assert.Contains("MIN(target_fact.target_key COLLATE BINARY)", resolutionSql, StringComparison.Ordinal);
            Assert.Contains("IS MAX(target_fact.target_key COLLATE BINARY)", resolutionSql, StringComparison.Ordinal);
            Assert.Equal(
                scope is "differential" or "scoped" ? 2 : 1,
                CountOccurrences(resolutionSql, "JOIN temp.reference_resolution_symbol_facts AS target_fact"));
        }

        var scoped = Assert.Single(
            DbWriter.ReferenceResolutionFactSqlForTesting,
            static entry => entry.Scope == "scoped");
        Assert.Contains(
            "WITH dirty_target_symbols(symbol_id) AS MATERIALIZED",
            scoped.MaterializationSql,
            StringComparison.Ordinal);
        Assert.Contains(
            "FROM temp.reference_graph_dirty_references AS dirty_target_reference",
            scoped.MaterializationSql,
            StringComparison.Ordinal);
        Assert.Contains("GROUP BY candidate.symbol_id", scoped.MaterializationSql, StringComparison.Ordinal);

        foreach (var candidateBoundScope in new[] { "fresh", "full", "differential", "retained" })
        {
            var materialization = Assert.Single(
                DbWriter.ReferenceResolutionFactSqlForTesting,
                entry => entry.Scope == candidateBoundScope).MaterializationSql;
            Assert.Contains("FROM symbols AS target", materialization, StringComparison.Ordinal);
            Assert.Contains("WHERE EXISTS", materialization, StringComparison.Ordinal);
            Assert.Contains(
                "INDEXED BY idx_symbol_ref_candidates_symbol",
                materialization,
                StringComparison.Ordinal);
            Assert.Contains(
                "candidate.symbol_id = target.id",
                materialization,
                StringComparison.Ordinal);
            Assert.DoesNotContain("dirty_target_symbols", materialization, StringComparison.Ordinal);
            Assert.DoesNotContain("GROUP BY candidate.symbol_id", materialization, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ReferenceResolutionFacts_MaterializeOnlyCandidateBearingSymbolsWithReverseSeeks()
    {
        var callerFileId = InsertFile("src/bounded-caller.py", "python");
        var targetFileId = InsertFile("src/bounded-target.py", "python");
        _writer.InsertSymbols([
            CreateSymbol(targetFileId, "CandidateTarget", line: 1),
            CreateSymbol(targetFileId, "UnusedTarget", line: 2),
        ]);
        _writer.InsertReferences(
            [CreateReference(callerFileId, "CandidateTarget", line: 10)],
            refreshMutualRecursionFlags: false);
        Execute("""
            INSERT INTO symbol_reference_candidates(reference_id, symbol_id, scope_rank)
            SELECT reference.id, target.id, 0
            FROM symbol_references AS reference
            JOIN symbols AS target ON target.name = reference.symbol_name;
            """);

        Execute(DbWriter.RefreshReferenceResolutionFullSqlForTesting);

        Assert.Equal(
            "CandidateTarget",
            ScalarString("""
                SELECT target.name
                FROM temp.reference_resolution_symbol_facts AS fact
                JOIN symbols AS target ON target.id = fact.symbol_id
                """));
        Assert.Equal(1, ScalarLong("SELECT COUNT(*) FROM temp.reference_resolution_symbol_facts"));

        var fullFacts = Assert.Single(
            DbWriter.ReferenceResolutionFactSqlForTesting,
            static entry => entry.Scope == "full").MaterializationSql;
        var insert = Assert.Single(
            fullFacts.Split(
                    ';',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static statement => statement.StartsWith(
                    "INSERT INTO temp.reference_resolution_symbol_facts",
                    StringComparison.Ordinal)));
        var plan = ReadQueryPlanDetails(insert);
        Assert.Contains(plan, static detail => detail.Contains(
            "SEARCH candidate USING COVERING INDEX idx_symbol_ref_candidates_symbol",
            StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan, static detail =>
            detail.Equals("SCAN candidate", StringComparison.OrdinalIgnoreCase)
            || detail.StartsWith("SCAN candidate ", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan, static detail => detail.Contains(
            "USE TEMP B-TREE",
            StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("DISTINCT", insert, StringComparison.Ordinal);
        Assert.DoesNotContain("GROUP BY candidate.symbol_id", insert, StringComparison.Ordinal);
    }

    [Fact]
    public void SingletonAggregate_MinMaxMatchesCountDistinctNullAndBinarySemantics()
    {
        Execute("""
            CREATE TEMP TABLE singleton_oracle_values (
                value TEXT COLLATE BINARY
            );
            """);
        var cases = new (string Name, string?[] Values)[]
        {
            ("empty", []),
            ("all-null", [null, null]),
            ("null-and-a", [null, "A"]),
            ("duplicate-a", ["A", "A"]),
            ("binary-case-variants", ["A", "a"]),
            ("a-and-b", ["A", "B"]),
        };

        foreach (var testCase in cases)
        {
            Execute("DELETE FROM temp.singleton_oracle_values;");
            using (var insert = _db.Connection.CreateCommand())
            {
                insert.CommandText = "INSERT INTO temp.singleton_oracle_values(value) VALUES (@value)";
                var value = insert.Parameters.Add("@value", SqliteType.Text);
                foreach (var item in testCase.Values)
                {
                    value.Value = item == null ? DBNull.Value : item;
                    insert.ExecuteNonQuery();
                }
            }

            var distinctSingleton = ScalarLong("""
                SELECT COUNT(DISTINCT value COLLATE BINARY) = 1
                FROM temp.singleton_oracle_values
                """);
            var minMaxSingleton = ScalarLong("""
                SELECT COUNT(value) > 0
                   AND MIN(value COLLATE BINARY) IS MAX(value COLLATE BINARY)
                FROM temp.singleton_oracle_values
                """);
            Assert.True(
                distinctSingleton == minMaxSingleton,
                $"Singleton aggregate mismatch for {testCase.Name}.");
        }
    }

    [Fact]
    public void ReferenceResolutionFacts_CollapseOnlyOneLogicalCSharpPartialFamily_Issue5158()
    {
        var callerFileId = InsertFile("src/Caller.cs", "csharp");
        var demoAFileId = InsertFile("src/Demo.Widget.A.cs", "csharp");
        var demoBFileId = InsertFile("src/Demo.Widget.B.cs", "csharp");
        var otherAFileId = InsertFile("src/Other.Widget.A.cs", "csharp");
        var otherBFileId = InsertFile("src/Other.Widget.B.cs", "csharp");
        var structAFileId = InsertFile("src/Demo.Packet.A.cs", "csharp");
        var structBFileId = InsertFile("src/Demo.Packet.B.cs", "csharp");
        var recordAFileId = InsertFile("src/Demo.Receipt.A.cs", "csharp");
        var recordBFileId = InsertFile("src/Demo.Receipt.B.cs", "csharp");
        var genericAFileId = InsertFile("src/Demo.Box.Generic.A.cs", "csharp");
        var genericBFileId = InsertFile("src/Demo.Box.Generic.B.cs", "csharp");
        var plainAFileId = InsertFile("src/Demo.Box.Plain.A.cs", "csharp");
        var plainBFileId = InsertFile("src/Demo.Box.Plain.B.cs", "csharp");
        var classShapeFileId = InsertFile("src/Demo.Shape.Class.cs", "csharp");
        var structShapeFileId = InsertFile("src/Demo.Shape.Struct.cs", "csharp");
        var csharpCrossLangAFileId = InsertFile("src/CrossLang.A.cs", "csharp");
        var csharpCrossLangBFileId = InsertFile("src/CrossLang.B.cs", "csharp");
        var javaCrossLangFileId = InsertFile("src/CrossLang.java", "java");
        _writer.InsertSymbols([
            CreatePartialTypeSymbol(demoAFileId, "Widget", "class", "Demo", "fixture|Demo.Widget", "public partial class Widget"),
            CreatePartialTypeSymbol(demoBFileId, "Widget", "class", "Demo", "fixture|Demo.Widget", "public partial class Widget"),
            CreatePartialTypeSymbol(otherAFileId, "Widget", "class", "Other", "fixture|Other.Widget", "public partial class Widget"),
            CreatePartialTypeSymbol(otherBFileId, "Widget", "class", "Other", "fixture|Other.Widget", "public partial class Widget"),
            CreatePartialTypeSymbol(structAFileId, "Packet", "struct", "Demo", "fixture|Demo.Packet", "public partial struct Packet"),
            CreatePartialTypeSymbol(structBFileId, "Packet", "struct", "Demo", "fixture|Demo.Packet", "public partial struct Packet"),
            CreatePartialTypeSymbol(recordAFileId, "Receipt", "record", "Demo", "fixture|Demo.Receipt", "public partial record Receipt"),
            CreatePartialTypeSymbol(recordBFileId, "Receipt", "record", "Demo", "fixture|Demo.Receipt", "public partial record Receipt"),
            CreatePartialTypeSymbol(genericAFileId, "Box", "class", "Demo", "fixture|Demo.Box`1", "public partial class Box<T>"),
            CreatePartialTypeSymbol(genericBFileId, "Box", "class", "Demo", "fixture|Demo.Box`1", "public partial class Box<T>"),
            CreatePartialTypeSymbol(plainAFileId, "Box", "class", "Demo", "fixture|Demo.Box", "public partial class Box"),
            CreatePartialTypeSymbol(plainBFileId, "Box", "class", "Demo", "fixture|Demo.Box", "public partial class Box"),
            CreatePartialTypeSymbol(classShapeFileId, "Shape", "class", "Demo", "fixture|Demo.Shape", "public partial class Shape"),
            CreatePartialTypeSymbol(structShapeFileId, "Shape", "struct", "Demo", "fixture|Demo.Shape", "public partial struct Shape"),
            CreatePartialTypeSymbol(csharpCrossLangAFileId, "CrossLang", "class", "Demo", "fixture|Demo.CrossLang", "public partial class CrossLang"),
            CreatePartialTypeSymbol(csharpCrossLangBFileId, "CrossLang", "class", "Demo", "fixture|Demo.CrossLang", "public partial class CrossLang"),
            CreatePartialTypeSymbol(javaCrossLangFileId, "CrossLang", "class", "Demo", "fixture|Demo.CrossLang", "public class CrossLang"),
        ]);
        _writer.InsertReferences([
            CreateTypeReference(callerFileId, "Widget", line: 10),
            CreateTypeReference(callerFileId, "Packet", line: 11),
            CreateTypeReference(callerFileId, "Receipt", line: 12),
            CreateTypeReference(callerFileId, "Box", line: 13),
            CreateTypeReference(callerFileId, "Widget", line: 20),
            CreateTypeReference(callerFileId, "Box", line: 21),
            CreateTypeReference(callerFileId, "Shape", line: 22),
            CreateTypeReference(callerFileId, "CrossLang", line: 23),
        ], refreshMutualRecursionFlags: false);

        Execute("""
            DELETE FROM symbol_reference_candidates;

            INSERT INTO symbol_reference_candidates(reference_id, symbol_id, scope_rank)
            SELECT reference.id, target.id, 0
            FROM symbol_references AS reference
            JOIN symbols AS target ON target.name = reference.symbol_name
            WHERE (reference.line = 10 AND target.container_qualified_name = 'Demo')
               OR reference.line IN (11, 12)
               OR (reference.line = 13 AND target.family_key = 'fixture|Demo.Box`1')
               OR reference.line IN (20, 21, 22, 23);
            """);

        Execute(DbWriter.RefreshReferenceResolutionFullSqlForTesting);

        foreach (var line in new[] { 10, 11, 12, 13 })
        {
            var row = ReadResolutionRow("src/Caller.cs", line);
            Assert.Equal("resolved_group", row.ResolutionState);
            Assert.Equal(2, row.CandidateCount);
            Assert.False(row.HasTargetId);
            Assert.True(row.HasTargetKey);
        }
        Assert.Equal(
            "family:csharp\u001fclass\u001ffixture|Demo.Widget",
            ScalarString("SELECT target_symbol_key FROM symbol_references WHERE line = 10"));
        Assert.Equal(
            "family:csharp\u001fstruct\u001ffixture|Demo.Packet",
            ScalarString("SELECT target_symbol_key FROM symbol_references WHERE line = 11"));
        Assert.Equal(
            "family:csharp\u001frecord\u001ffixture|Demo.Receipt",
            ScalarString("SELECT target_symbol_key FROM symbol_references WHERE line = 12"));
        Assert.Equal(
            "family:csharp\u001fclass\u001ffixture|Demo.Box`1",
            ScalarString("SELECT target_symbol_key FROM symbol_references WHERE line = 13"));

        var namespaceAmbiguity = ReadResolutionRow("src/Caller.cs", 20);
        Assert.Equal("ambiguous", namespaceAmbiguity.ResolutionState);
        Assert.Equal(4, namespaceAmbiguity.CandidateCount);
        Assert.False(namespaceAmbiguity.HasTargetKey);
        var arityAmbiguity = ReadResolutionRow("src/Caller.cs", 21);
        Assert.Equal("ambiguous", arityAmbiguity.ResolutionState);
        Assert.Equal(4, arityAmbiguity.CandidateCount);
        Assert.False(arityAmbiguity.HasTargetKey);
        var kindAmbiguity = ReadResolutionRow("src/Caller.cs", 22);
        Assert.Equal("ambiguous", kindAmbiguity.ResolutionState);
        Assert.Equal(2, kindAmbiguity.CandidateCount);
        Assert.False(kindAmbiguity.HasTargetKey);
        var languageAmbiguity = ReadResolutionRow("src/Caller.cs", 23);
        Assert.Equal("ambiguous", languageAmbiguity.ResolutionState);
        Assert.Equal(3, languageAmbiguity.CandidateCount);
        Assert.False(languageAmbiguity.HasTargetKey);
    }

    [Fact]
    public void ReferenceResolutionFacts_PreserveResolvedLegacyCandidateWithNullTargetKey()
    {
        var callerFileId = InsertFile("src/legacy-caller.py", "python");
        var targetFileId = InsertFile("src/legacy-target.py", "python");
        _writer.InsertSymbols([CreateSymbol(targetFileId, "LegacyTarget", line: 1)]);
        _writer.InsertReferences(
            [CreateReference(callerFileId, "LegacyTarget", line: 10)],
            refreshMutualRecursionFlags: false);

        Execute($"""
            INSERT INTO symbol_reference_candidates(reference_id, symbol_id, scope_rank)
            SELECT reference.id, target.id, 0
            FROM symbol_references AS reference
            CROSS JOIN symbols AS target
            WHERE reference.symbol_name = 'LegacyTarget'
              AND target.name = 'LegacyTarget';

            UPDATE files SET lang = NULL WHERE id = {targetFileId};
            """);

        Execute(DbWriter.RefreshReferenceResolutionFullSqlForTesting);

        Assert.Equal(
            1,
            ScalarLong("""
                SELECT COUNT(*)
                FROM symbol_references
                WHERE symbol_name = 'LegacyTarget'
                  AND resolution_state = 'resolved'
                  AND resolution_candidate_count = 1
                  AND target_symbol_id IS NOT NULL
                  AND target_symbol_key IS NULL
                """));
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
            "csharp\u001fsrc/target.cs\u001f\u001fCsTarget",
            ScalarString("""
                SELECT reference.target_symbol_key
                FROM symbol_references AS reference
                JOIN files AS file ON file.id = reference.file_id
                WHERE file.path = 'src/caller.cs'
                  AND reference.line = 10
                """));
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

    private static SymbolRecord CreateRangedSymbol(
        long fileId,
        string name,
        int startLine,
        int endLine)
        => new()
        {
            FileId = fileId,
            Kind = "function",
            Name = name,
            Line = startLine,
            StartLine = startLine,
            EndLine = endLine,
            Signature = $"function {name}()",
        };

    private static SymbolRecord CreatePartialTypeSymbol(
        long fileId,
        string name,
        string kind,
        string container,
        string familyKey,
        string signature)
        => new()
        {
            FileId = fileId,
            Kind = kind,
            Name = name,
            Line = 1,
            StartLine = 1,
            EndLine = 3,
            Signature = signature,
            ContainerKind = "namespace",
            ContainerName = container,
            ContainerQualifiedName = container,
            FamilyKey = familyKey,
            IsPartialDeclaration = true,
        };

    private static ReferenceRecord CreateTypeReference(
        long fileId,
        string symbolName,
        int line)
        => new()
        {
            FileId = fileId,
            SymbolName = symbolName,
            ReferenceKind = "type_reference",
            Line = line,
            Column = 1,
            Context = $"{symbolName} value;",
            ContainerKind = "function",
            ContainerName = "Caller",
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

    private int? ReadSourceLine(string symbolName)
    {
        using var command = _db.Connection.CreateCommand();
        command.CommandText = """
            SELECT source.line
            FROM symbol_references AS reference
            LEFT JOIN symbols AS source ON source.id = reference.source_symbol_id
            WHERE reference.symbol_name = @symbol_name
            """;
        command.Parameters.AddWithValue("@symbol_name", symbolName);
        var value = command.ExecuteScalar();
        return value == null || value == DBNull.Value
            ? null
            : Convert.ToInt32(value, CultureInfo.InvariantCulture);
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

    private string? ScalarString(string sql)
    {
        using var command = _db.Connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value == null || value == DBNull.Value
            ? null
            : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private IReadOnlyList<string> ReadQueryPlanDetails(string sql)
    {
        using var command = _db.Connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        using var reader = command.ExecuteReader();
        var details = new List<string>();
        while (reader.Read())
            details.Add(reader.GetString(3));
        return details;
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
