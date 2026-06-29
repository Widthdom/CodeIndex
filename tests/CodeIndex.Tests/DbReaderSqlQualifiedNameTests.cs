using System.Reflection;
using System.Text;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

public partial class DbReaderTests
{
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

        var bareRefs = _reader.SearchReferences("fn_GetOrderItems", lang: "sql", exact: true, pathPatterns: ["src/*sql_name_mismatch_fixture*.sql"]);
        var qualifiedRefs = _reader.SearchReferences("dbo.fn_GetOrderItems", lang: "sql", exact: true, pathPatterns: ["src/*sql_name_mismatch_fixture*.sql"]);
        Assert.Equal(12, Assert.Single(bareRefs).Line);
        Assert.Equal(12, Assert.Single(qualifiedRefs).Line);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.fn_GetOrderItems", lang: "sql", exact: true, pathPatterns: ["src/*sql_name_mismatch_fixture*.sql"]));

        var bareCaller = Assert.Single(_reader.GetCallers("fn_GetOrderItems", lang: "sql", exact: true, pathPatterns: ["src/*sql_name_mismatch_fixture*.sql"]));
        var qualifiedCaller = Assert.Single(_reader.GetCallers("dbo.fn_GetOrderItems", lang: "sql", exact: true, pathPatterns: ["src/*sql_name_mismatch_fixture*.sql"]));
        Assert.Equal("dbo.usp_GetOrders", bareCaller.CallerName);
        Assert.Equal("dbo.usp_GetOrders", qualifiedCaller.CallerName);
        Assert.Equal(1, _reader.CountCallers("dbo.fn_GetOrderItems", lang: "sql", exact: true, pathPatterns: ["src/*sql_name_mismatch_fixture*.sql"]));

        var bareCallee = Assert.Single(_reader.GetCallees("usp_GetOrders", lang: "sql", exact: true, pathPatterns: ["src/*sql_name_mismatch_fixture*.sql"]));
        var qualifiedCallee = Assert.Single(_reader.GetCallees("dbo.usp_GetOrders", lang: "sql", exact: true, pathPatterns: ["src/*sql_name_mismatch_fixture*.sql"]));
        Assert.Equal("fn_GetOrderItems", bareCallee.CalleeName);
        Assert.Equal("fn_GetOrderItems", qualifiedCallee.CalleeName);
        Assert.Equal(1, _reader.CountCallees("usp_GetOrders", lang: "sql", exact: true, pathPatterns: ["src/*sql_name_mismatch_fixture*.sql"]));

        var (bareImpact, bareTruncated, bareTruncatedReason, _, _) = _reader.GetTransitiveCallers("fn_GetOrderItems", maxDepth: 1, limit: 10, lang: "sql", pathPatterns: ["src/*sql_name_mismatch_fixture*.sql"]);
        var (qualifiedImpact, qualifiedTruncated, qualifiedTruncatedReason, _, _) = _reader.GetTransitiveCallers("dbo.fn_GetOrderItems", maxDepth: 1, limit: 10, lang: "sql", pathPatterns: ["src/*sql_name_mismatch_fixture*.sql"]);
        Assert.False(bareTruncated);
        Assert.False(qualifiedTruncated);
        Assert.Null(bareTruncatedReason);
        Assert.Null(qualifiedTruncatedReason);
        Assert.Equal("dbo.usp_GetOrders", Assert.Single(bareImpact).CallerName);
        Assert.Equal("dbo.usp_GetOrders", Assert.Single(qualifiedImpact).CallerName);

        var hotspot = Assert.Single(
            _reader.GetSymbolHotspots(10, "function", "sql", ["src/*sql_name_mismatch_fixture*.sql"], null, false),
            item => item.Symbol.Name == "dbo.fn_GetOrderItems");
        Assert.Equal(1, hotspot.ReferenceCount);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: "function", lang: "sql",
            pathPatterns: ["src/*sql_name_mismatch_fixture*.sql"], excludePathPatterns: null, excludeTests: false);
        Assert.DoesNotContain(unused, symbol => symbol.Name == "dbo.fn_GetOrderItems");
        var unusedCount = _reader.CountUnusedSymbols(kind: "function", lang: "sql",
            pathPatterns: ["src/*sql_name_mismatch_fixture*.sql"], excludePathPatterns: null, excludeTests: false);
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
            _reader.GetFileDependencies(limit: 10, lang: "sql", pathPatterns: ["src/sql_unqualified_row_comment.sql"], excludePathPatterns: null, excludeTests: false));
        Assert.Equal("src/sql_unqualified_row_comment.sql", commentDependency.SourcePath);
        Assert.Equal("src/sql_unqualified_row_targets.sql", commentDependency.TargetPath);
        Assert.Equal(1, commentDependency.ReferenceCount);
        Assert.Equal("dbo.fn_Target", commentDependency.Symbols);

        var stringDependency = Assert.Single(
            _reader.GetFileDependencies(limit: 10, lang: "sql", pathPatterns: ["src/sql_unqualified_row_string.sql"], excludePathPatterns: null, excludeTests: false));
        Assert.Equal("src/sql_unqualified_row_string.sql", stringDependency.SourcePath);
        Assert.Equal("src/sql_unqualified_row_targets.sql", stringDependency.TargetPath);
        Assert.Equal(1, stringDependency.ReferenceCount);
        Assert.Equal("dbo.fn_Target", stringDependency.Symbols);

        var mixedDependency = Assert.Single(
            _reader.GetFileDependencies(limit: 10, lang: "sql", pathPatterns: ["src/sql_unqualified_row_mixed_calls.sql"], excludePathPatterns: null, excludeTests: false));
        Assert.Equal("src/sql_unqualified_row_mixed_calls.sql", mixedDependency.SourcePath);
        Assert.Equal("src/sql_unqualified_row_targets.sql", mixedDependency.TargetPath);
        Assert.Equal(2, mixedDependency.ReferenceCount);
        Assert.Equal("dbo.fn_Target,sales.fn_Target", mixedDependency.Symbols);

        var hotspots = _reader.GetSymbolHotspots(10, "function", "sql", ["src/*sql_unqualified_row*.sql"], null, false);
        var dboHotspot = Assert.Single(hotspots, item => item.Symbol.Name == "dbo.fn_Target");
        var salesHotspot = Assert.Single(hotspots, item => item.Symbol.Name == "sales.fn_Target");
        Assert.Equal(3, dboHotspot.ReferenceCount);
        Assert.Equal(1, salesHotspot.ReferenceCount);

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: "function", lang: "sql",
            pathPatterns: ["src/*sql_unqualified_row*.sql"], excludePathPatterns: null, excludeTests: false);
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
            _reader.GetFileDependencies(limit: 10, lang: "sql", pathPatterns: ["src/sql_deps_caller.sql"], excludePathPatterns: null, excludeTests: false));
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
            _reader.SearchReferences("dbo.fn_Target", lang: "sql", exact: true, pathPatterns: ["src/*sql_same_line_cross_schema*.sql"]));
        Assert.Equal(1, reference.Line);
        Assert.Equal("sales.fn_Target", reference.ContainerName);

        var caller = Assert.Single(
            _reader.GetCallers("dbo.fn_Target", lang: "sql", exact: true, pathPatterns: ["src/*sql_same_line_cross_schema*.sql"]));
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
            _reader.SearchReferences("dbo.fn_Target", lang: "sql", exact: true, pathPatterns: ["src/*sql_same_line_string_literal*.sql"]));
        Assert.Equal(9, reference.Line);
        Assert.Equal("sales.host", reference.ContainerName);

        var caller = Assert.Single(
            _reader.GetCallers("dbo.fn_Target", lang: "sql", exact: true, pathPatterns: ["src/*sql_same_line_string_literal*.sql"]));
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
            _reader.SearchReferences("dbo.fn_Target", lang: "sql", exact: true, pathPatterns: ["src/*sql_same_line_block_comment*.sql"]));
        Assert.Equal(9, reference.Line);
        Assert.Equal("sales.host", reference.ContainerName);

        var caller = Assert.Single(
            _reader.GetCallers("dbo.fn_Target", lang: "sql", exact: true, pathPatterns: ["src/*sql_same_line_block_comment*.sql"]));
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
            _reader.GetDefinitions("dbo.fn_Target", limit: 10, lang: "sql", pathPatterns: ["src/*sql_quoted_definition*.sql"]));
        Assert.Equal("[dbo].[fn_Target]", definition.Name);

        var exactDefinition = Assert.Single(
            _reader.GetDefinitions("dbo.fn_Target", limit: 10, lang: "sql", pathPatterns: ["src/*sql_quoted_definition*.sql"], exact: true));
        Assert.Equal("[dbo].[fn_Target]", exactDefinition.Name);

        var analysis = _reader.AnalyzeSymbol("dbo.fn_Target", limit: 10, lang: "sql", pathPatterns: ["src/*sql_quoted_definition*.sql"]);
        Assert.Equal("[dbo].[fn_Target]", Assert.Single(analysis.Definitions).Name);

        var exactAnalysis = _reader.AnalyzeSymbol("dbo.fn_Target", limit: 10, lang: "sql", pathPatterns: ["src/*sql_quoted_definition*.sql"], exact: true);
        Assert.Equal("[dbo].[fn_Target]", Assert.Single(exactAnalysis.Definitions).Name);

        var impact = _reader.AnalyzeImpact("dbo.fn_Target", maxDepth: 1, limit: 10, lang: "sql", pathPatterns: ["src/*sql_quoted_definition*.sql"]);
        Assert.Equal(1, impact.DefinitionCount);
        Assert.Equal("[dbo].[fn_Target]", Assert.Single(impact.Definitions).Name);
        Assert.Equal("[sales].[fn_Target]", Assert.Single(impact.Callers).CallerName);

        var tsqlImpact = _reader.AnalyzeImpact("dbo.fn_Target", maxDepth: 1, limit: 10, lang: "tsql", pathPatterns: ["src/*sql_quoted_definition*.sql"]);
        Assert.Equal(1, tsqlImpact.DefinitionCount);
        Assert.Equal("[dbo].[fn_Target]", Assert.Single(tsqlImpact.Definitions).Name);
        Assert.Equal("[sales].[fn_Target]", Assert.Single(tsqlImpact.Callers).CallerName);
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

        var references = _reader.SearchReferences("sales.proc_name", lang: "sql", exact: true, pathPatterns: ["src/*sql_double_quoted*.sql"]);
        var reference = Assert.Single(references);
        Assert.Equal(4, reference.Line);
        Assert.Equal("sales.caller", reference.ContainerName);

        var callers = _reader.GetCallers("sales.proc_name", lang: "sql", exact: true, pathPatterns: ["src/*sql_double_quoted*.sql"]);
        var caller = Assert.Single(callers);
        Assert.Equal("sales.caller", caller.CallerName);
        Assert.Equal(1, caller.ReferenceCount);

        var impact = _reader.AnalyzeImpact("sales.proc_name", maxDepth: 1, limit: 10, lang: "sql", pathPatterns: ["src/*sql_double_quoted*.sql"]);
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

        Assert.Empty(_reader.SearchReferences("sales.proc_name", lang: "sql", pathPatterns: ["src/*sql_nonexact_scope*.sql"]));
        Assert.Empty(_reader.GetCallers("sales.proc_name", lang: "sql", pathPatterns: ["src/*sql_nonexact_scope*.sql"]));

        Assert.Empty(_reader.SearchReferences("sales.proc_name", lang: "sql", exact: true, pathPatterns: ["src/*sql_nonexact_scope*.sql"]));
        Assert.Empty(_reader.GetCallers("sales.proc_name", lang: "sql", exact: true, pathPatterns: ["src/*sql_nonexact_scope*.sql"]));

        var references = Assert.Single(_reader.SearchReferences("archive.sales.proc_name", lang: "sql", pathPatterns: ["src/*sql_nonexact_scope*.sql"]));
        Assert.Equal(4, references.Line);
        Assert.Equal("sales.caller", references.ContainerName);

        var callers = Assert.Single(_reader.GetCallers("archive.sales.proc_name", lang: "sql", pathPatterns: ["src/*sql_nonexact_scope*.sql"]));
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
            _reader.GetDefinitions("sales.fn_Target", limit: 10, lang: "sql", pathPatterns: ["src/*sql_dotted_identifier_collision*.sql"], exact: true));
        Assert.Equal("sales.fn_Target", qualifiedDefinition.Name);

        var quotedDefinition = Assert.Single(
            _reader.GetDefinitions("\"sales.fn_Target\"", limit: 10, lang: "sql", pathPatterns: ["src/*sql_dotted_identifier_collision*.sql"], exact: true));
        Assert.Equal("\"sales.fn_Target\"", quotedDefinition.Name);

        var qualifiedAnalysis = _reader.AnalyzeSymbol("sales.fn_Target", limit: 10, lang: "sql", pathPatterns: ["src/*sql_dotted_identifier_collision*.sql"], exact: true);
        Assert.Equal("sales.fn_Target", Assert.Single(qualifiedAnalysis.Definitions).Name);

        var quotedAnalysis = _reader.AnalyzeSymbol("\"sales.fn_Target\"", limit: 10, lang: "sql", pathPatterns: ["src/*sql_dotted_identifier_collision*.sql"], exact: true);
        Assert.Equal("\"sales.fn_Target\"", Assert.Single(quotedAnalysis.Definitions).Name);

        var qualifiedImpact = _reader.AnalyzeImpact("sales.fn_Target", maxDepth: 1, limit: 10, lang: "sql", pathPatterns: ["src/*sql_dotted_identifier_collision*.sql"]);
        Assert.Equal(1, qualifiedImpact.DefinitionCount);
        Assert.Equal("sales.fn_Target", Assert.Single(qualifiedImpact.Definitions).Name);

        var quotedImpact = _reader.AnalyzeImpact("\"sales.fn_Target\"", maxDepth: 1, limit: 10, lang: "sql", pathPatterns: ["src/*sql_dotted_identifier_collision*.sql"]);
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

        var references = _reader.SearchReferences("sales.fn_Target", lang: "sql", exact: true, pathPatterns: ["src/*sql_dotted_identifier_graph*.sql"]);
        var reference = Assert.Single(references);
        Assert.Equal("sales.caller", reference.ContainerName);
        Assert.Equal(4, reference.Line);
        Assert.Equal(1, _reader.CountSearchReferences("sales.fn_Target", lang: "sql", exact: true, pathPatterns: ["src/*sql_dotted_identifier_graph*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("sales.fn_Target", lang: "sql", exact: true, pathPatterns: ["src/*sql_dotted_identifier_graph*.sql"]));

        var callers = _reader.GetCallers("sales.fn_Target", lang: "sql", exact: true, pathPatterns: ["src/*sql_dotted_identifier_graph*.sql"]);
        var caller = Assert.Single(callers);
        Assert.Equal("sales.caller", caller.CallerName);
        Assert.Equal(1, caller.ReferenceCount);
        Assert.Equal(1, _reader.CountCallers("sales.fn_Target", lang: "sql", exact: true, pathPatterns: ["src/*sql_dotted_identifier_graph*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountCallersTotal("sales.fn_Target", lang: "sql", exact: true, pathPatterns: ["src/*sql_dotted_identifier_graph*.sql"]));

        var impact = _reader.AnalyzeImpact("sales.fn_Target", maxDepth: 1, limit: 10, lang: "sql", pathPatterns: ["src/*sql_dotted_identifier_graph*.sql"]);
        Assert.Equal("sales.fn_Target", Assert.Single(impact.Definitions).Name);
        Assert.Equal("sales.caller", Assert.Single(impact.Callers).CallerName);

        var quotedReferences = _reader.SearchReferences("\"sales.fn_Target\"", lang: "sql", exact: true, pathPatterns: ["src/*sql_dotted_identifier_graph*.sql"]);
        Assert.Equal(3, quotedReferences.Count);
        Assert.All(quotedReferences, reference => Assert.Equal("quoted.caller", reference.ContainerName));
        Assert.Contains(quotedReferences, reference => reference.Context == "CALL \"sales.fn_Target\";");
        Assert.Contains(quotedReferences, reference => reference.Context == "EXEC \"sales.fn_Target\";");
        Assert.Contains(quotedReferences, reference => reference.Context == "EXECUTE \"sales.fn_Target\";");
        Assert.Equal(3, _reader.CountSearchReferences("\"sales.fn_Target\"", lang: "sql", exact: true, pathPatterns: ["src/*sql_dotted_identifier_graph*.sql"]));
        Assert.Equal(new QueryCountResult(3, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("\"sales.fn_Target\"", lang: "sql", exact: true, pathPatterns: ["src/*sql_dotted_identifier_graph*.sql"]));

        var quotedCallers = _reader.GetCallers("\"sales.fn_Target\"", lang: "sql", exact: true, pathPatterns: ["src/*sql_dotted_identifier_graph*.sql"]);
        var quotedCaller = Assert.Single(quotedCallers);
        Assert.Equal("quoted.caller", quotedCaller.CallerName);
        Assert.Equal(3, quotedCaller.ReferenceCount);
        Assert.Equal(1, _reader.CountCallers("\"sales.fn_Target\"", lang: "sql", exact: true, pathPatterns: ["src/*sql_dotted_identifier_graph*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountCallersTotal("\"sales.fn_Target\"", lang: "sql", exact: true, pathPatterns: ["src/*sql_dotted_identifier_graph*.sql"]));

        var quotedImpact = _reader.AnalyzeImpact("\"sales.fn_Target\"", maxDepth: 1, limit: 10, lang: "sql", pathPatterns: ["src/*sql_dotted_identifier_graph*.sql"]);
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
            _reader.GetFileDependencies(limit: 10, lang: "sql", pathPatterns: ["src/*sql_dotted_identifier_deps*.sql"], excludePathPatterns: null, excludeTests: false));
        Assert.Equal("src/sql_dotted_identifier_deps_caller.sql", dependency.SourcePath);
        Assert.Equal("src/sql_dotted_identifier_deps_target.sql", dependency.TargetPath);
        Assert.Equal(1, dependency.ReferenceCount);
        Assert.Equal("sales.fn_Target", dependency.Symbols);

        var hotspots = _reader.GetSymbolHotspots(10, "function", "sql", ["src/*sql_dotted_identifier_deps*.sql"], null, false);
        Assert.Equal(1, Assert.Single(hotspots, item => item.Symbol.Name == "sales.fn_Target").ReferenceCount);
        Assert.DoesNotContain(hotspots, item => item.Symbol.Name == "\"sales.fn_Target\"");

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: "function", lang: "sql",
            pathPatterns: ["src/*sql_dotted_identifier_deps*.sql"], excludePathPatterns: null, excludeTests: false);
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

        Assert.Empty(_reader.GetCallers("sales.fn_Target", lang: "sql", exact: true, pathPatterns: ["src/*sql_quoted_container_leaf_fallback*.sql"]));
        Assert.Equal(0, _reader.CountCallers("sales.fn_Target", lang: "sql", exact: true, pathPatterns: ["src/*sql_quoted_container_leaf_fallback*.sql"]));
        Assert.Equal(new QueryCountResult(0, 0), _reader.CountCallersTotal("sales.fn_Target", lang: "sql", exact: true, pathPatterns: ["src/*sql_quoted_container_leaf_fallback*.sql"]));

        var leafCaller = Assert.Single(_reader.GetCallers("fn_Target", lang: "sql", exact: true, pathPatterns: ["src/*sql_quoted_container_leaf_fallback*.sql"]));
        Assert.Equal("\"sales.Caller\"", leafCaller.CallerName);
        Assert.Equal(1, leafCaller.ReferenceCount);

        var dependency = Assert.Single(
            _reader.GetFileDependencies(limit: 10, lang: "sql", pathPatterns: ["src/*sql_quoted_container_leaf_fallback*.sql"], excludePathPatterns: null, excludeTests: false));
        Assert.Equal("src/sql_quoted_container_leaf_fallback_caller.sql", dependency.SourcePath);
        Assert.Equal("src/sql_quoted_container_leaf_fallback_quoted_target.sql", dependency.TargetPath);
        Assert.Equal(1, dependency.ReferenceCount);
        Assert.Equal("fn_Target", dependency.Symbols);

        var hotspots = _reader.GetSymbolHotspots(10, "function", "sql", ["src/*sql_quoted_container_leaf_fallback*.sql"], null, false);
        Assert.Equal(1, Assert.Single(hotspots, item => item.Symbol.Name == "\"fn_Target\"").ReferenceCount);
        Assert.DoesNotContain(hotspots, item => item.Symbol.Name == "sales.fn_Target");

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: "function", lang: "sql",
            pathPatterns: ["src/*sql_quoted_container_leaf_fallback*.sql"], excludePathPatterns: null, excludeTests: false);
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

        var references = _reader.SearchReferences("dbo.Äpfel", lang: "sql", exact: true, pathPatterns: ["src/*sql_unicode_exact_leaf_fallback*.sql"]);
        Assert.Equal(2, references.Count);
        Assert.Contains(references, reference => reference.ContainerName == "dbo.Caller" && reference.Line == 8);
        Assert.Contains(references, reference => reference.ContainerName == "dbo.ÄCaller" && reference.Line == 13);
        Assert.Equal(2, _reader.CountSearchReferences("dbo.Äpfel", lang: "sql", exact: true, pathPatterns: ["src/*sql_unicode_exact_leaf_fallback*.sql"]));

        var callers = _reader.GetCallers("dbo.Äpfel", lang: "sql", exact: true, pathPatterns: ["src/*sql_unicode_exact_leaf_fallback*.sql"]);
        Assert.Equal(2, callers.Count);
        Assert.Contains(callers, item => item.CallerName == "dbo.Caller");
        Assert.Contains(callers, item => item.CallerName == "dbo.ÄCaller");
        Assert.Equal(2, _reader.CountCallers("dbo.Äpfel", lang: "sql", exact: true, pathPatterns: ["src/*sql_unicode_exact_leaf_fallback*.sql"]));

        var callee = Assert.Single(_reader.GetCallees("äcaller", lang: "sql", exact: true, pathPatterns: ["src/*sql_unicode_exact_leaf_fallback*.sql"]));
        Assert.Equal("Äpfel", callee.CalleeName);
        Assert.Equal(1, _reader.CountCallees("äcaller", lang: "sql", exact: true, pathPatterns: ["src/*sql_unicode_exact_leaf_fallback*.sql"]));

        var impact = _reader.AnalyzeImpact("dbo.Äpfel", maxDepth: 1, limit: 10, lang: "sql", pathPatterns: ["src/*sql_unicode_exact_leaf_fallback*.sql"]);
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
            _reader.SearchReferences("dbo.fn_Target", lang: "sql", exact: true, pathPatterns: ["src/*sql_schema_scoped*.sql"]));
        Assert.Equal("dbo.Caller", reference.ContainerName);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.fn_Target", lang: "sql", exact: true, pathPatterns: ["src/*sql_schema_scoped*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.fn_Target", lang: "sql", exact: true, pathPatterns: ["src/*sql_schema_scoped*.sql"]));

        var caller = Assert.Single(
            _reader.GetCallers("dbo.fn_Target", lang: "sql", exact: true, pathPatterns: ["src/*sql_schema_scoped*.sql"]));
        Assert.Equal("dbo.Caller", caller.CallerName);
        Assert.Equal(1, _reader.CountCallers("dbo.fn_Target", lang: "sql", exact: true, pathPatterns: ["src/*sql_schema_scoped*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountCallersTotal("dbo.fn_Target", lang: "sql", exact: true, pathPatterns: ["src/*sql_schema_scoped*.sql"]));

        Assert.Equal("dbo.fn_Target", SqlNameResolver.ResolveReferenceNameAtColumn("fn_Target", "EXEC dbo.fn_Target;", "dbo.Caller", 1));
        Assert.False(SqlNameResolver.AllowLeafFallbackAtColumn("fn_Target", "EXEC dbo.fn_Target;", "dbo.Caller", 1));

        var impact = _reader.AnalyzeImpact("dbo.fn_Target", maxDepth: 1, limit: 10, lang: "sql", pathPatterns: ["src/*sql_schema_scoped*.sql"]);
        Assert.Equal("dbo.Caller", Assert.Single(impact.Callers).CallerName);

        var dependency = Assert.Single(
            _reader.GetFileDependencies(limit: 10, lang: "sql", pathPatterns: ["src/sql_schema_scoped_caller.sql"], excludePathPatterns: null, excludeTests: false));
        Assert.Equal("src/sql_schema_scoped_caller.sql", dependency.SourcePath);
        Assert.Equal("src/sql_schema_scoped_target_dbo.sql", dependency.TargetPath);
        Assert.Equal(1, dependency.ReferenceCount);

        var hotspots = _reader.GetSymbolHotspots(10, "function", "sql", ["src/*sql_schema_scoped*.sql"], null, false);
        var hotspot = Assert.Single(hotspots, item => item.Symbol.Name == "dbo.fn_Target");
        Assert.Equal(1, hotspot.ReferenceCount);
        Assert.DoesNotContain(hotspots, item => item.Symbol.Name == "sales.fn_Target");

        var unused = _reader.GetUnusedSymbols(limit: 10, kind: "function", lang: "sql",
            pathPatterns: ["src/*sql_schema_scoped*.sql"], excludePathPatterns: null, excludeTests: false);
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
            _reader.SearchReferences("dbo.fn_Target", lang: "sql", exact: true, pathPatterns: ["src/*sql_same_line_multi*.sql"]));
        Assert.Equal("dbo.Caller", dboReference.ContainerName);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.fn_Target", lang: "sql", exact: true, pathPatterns: ["src/*sql_same_line_multi*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.fn_Target", lang: "sql", exact: true, pathPatterns: ["src/*sql_same_line_multi*.sql"]));

        var salesReference = Assert.Single(
            _reader.SearchReferences("sales.fn_Target", lang: "sql", exact: true, pathPatterns: ["src/*sql_same_line_multi*.sql"]));
        Assert.Equal("dbo.Caller", salesReference.ContainerName);
        Assert.Equal(1, _reader.CountSearchReferences("sales.fn_Target", lang: "sql", exact: true, pathPatterns: ["src/*sql_same_line_multi*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("sales.fn_Target", lang: "sql", exact: true, pathPatterns: ["src/*sql_same_line_multi*.sql"]));

        var dboCaller = Assert.Single(
            _reader.GetCallers("dbo.fn_Target", lang: "sql", exact: true, pathPatterns: ["src/*sql_same_line_multi*.sql"]));
        Assert.Equal("dbo.Caller", dboCaller.CallerName);
        Assert.Equal(1, dboCaller.ReferenceCount);
        Assert.Equal(1, _reader.CountCallers("dbo.fn_Target", lang: "sql", exact: true, pathPatterns: ["src/*sql_same_line_multi*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountCallersTotal("dbo.fn_Target", lang: "sql", exact: true, pathPatterns: ["src/*sql_same_line_multi*.sql"]));

        var salesCaller = Assert.Single(
            _reader.GetCallers("sales.fn_Target", lang: "sql", exact: true, pathPatterns: ["src/*sql_same_line_multi*.sql"]));
        Assert.Equal("dbo.Caller", salesCaller.CallerName);
        Assert.Equal(1, salesCaller.ReferenceCount);
        Assert.Equal(1, _reader.CountCallers("sales.fn_Target", lang: "sql", exact: true, pathPatterns: ["src/*sql_same_line_multi*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountCallersTotal("sales.fn_Target", lang: "sql", exact: true, pathPatterns: ["src/*sql_same_line_multi*.sql"]));

        var dependencies = _reader.GetFileDependencies(limit: 10, lang: "sql", pathPatterns: ["src/*sql_same_line_multi*.sql"], excludePathPatterns: null, excludeTests: false)
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

        var hotspots = _reader.GetSymbolHotspots(10, "function", "sql", ["src/*sql_same_line_multi*.sql"], null, false);
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

        var callee = Assert.Single(_reader.GetCallees("dbo.usp_GetOrders", lang: "sql", exact: true, pathPatterns: ["src/*sql_callee_schema_scoped*.sql"]));
        Assert.Equal("fn_A", callee.CalleeName);
        Assert.Equal("dbo.usp_GetOrders", callee.CallerName);
        Assert.Equal(1, _reader.CountCallees("dbo.usp_GetOrders", lang: "sql", exact: true, pathPatterns: ["src/*sql_callee_schema_scoped*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountCalleesTotal("dbo.usp_GetOrders", lang: "sql", exact: true, pathPatterns: ["src/*sql_callee_schema_scoped*.sql"]));
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
            _reader.SearchReferences("sales.fn_Target", lang: "sql", pathPatterns: ["src/*sql_nonexact_schema_scoped*.sql"]));
        Assert.Equal("sales.Caller", reference.ContainerName);
        Assert.Equal(1, _reader.CountSearchReferences("sales.fn_Target", lang: "sql", pathPatterns: ["src/*sql_nonexact_schema_scoped*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("sales.fn_Target", lang: "sql", pathPatterns: ["src/*sql_nonexact_schema_scoped*.sql"]));

        var caller = Assert.Single(
            _reader.GetCallers("sales.fn_Target", lang: "sql", pathPatterns: ["src/*sql_nonexact_schema_scoped*.sql"]));
        Assert.Equal("sales.Caller", caller.CallerName);
        Assert.Equal(1, caller.ReferenceCount);
        Assert.Equal(1, _reader.CountCallers("sales.fn_Target", lang: "sql", pathPatterns: ["src/*sql_nonexact_schema_scoped*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountCallersTotal("sales.fn_Target", lang: "sql", pathPatterns: ["src/*sql_nonexact_schema_scoped*.sql"]));

        var callee = Assert.Single(
            _reader.GetCallees("sales.Caller", lang: "sql", pathPatterns: ["src/*sql_nonexact_schema_scoped*.sql"]));
        Assert.Equal("fn_Target", callee.CalleeName);
        Assert.Equal("sales.Caller", callee.CallerName);
        Assert.Equal(1, _reader.CountCallees("sales.Caller", lang: "sql", pathPatterns: ["src/*sql_nonexact_schema_scoped*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountCalleesTotal("sales.Caller", lang: "sql", pathPatterns: ["src/*sql_nonexact_schema_scoped*.sql"]));
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
            _reader.GetCallees("sales.fn_Target", lang: "sql", exact: true, pathPatterns: ["src/*sql_exact_bracketed_callee*.sql"]));
        Assert.Equal("[sales].[fn_Target]", normalizedCallee.CallerName);
        Assert.Equal("fn_Target", normalizedCallee.CalleeName);
        Assert.Equal(2, normalizedCallee.ReferenceCount);
        Assert.Equal(1, _reader.CountCallees("sales.fn_Target", lang: "sql", exact: true, pathPatterns: ["src/*sql_exact_bracketed_callee*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountCalleesTotal("sales.fn_Target", lang: "sql", exact: true, pathPatterns: ["src/*sql_exact_bracketed_callee*.sql"]));

        var bracketedCallee = Assert.Single(
            _reader.GetCallees("[sales].[fn_Target]", lang: "sql", exact: true, pathPatterns: ["src/*sql_exact_bracketed_callee*.sql"]));
        Assert.Equal("[sales].[fn_Target]", bracketedCallee.CallerName);
        Assert.Equal("fn_Target", bracketedCallee.CalleeName);

        Assert.DoesNotContain(
            _reader.GetCallees("dbo.fn_Target", lang: "sql", exact: true, pathPatterns: ["src/*sql_exact_bracketed_callee*.sql"]),
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
            _reader.GetDefinitions("sales.fn_Target", limit: 10, lang: "sql", pathPatterns: ["src/*sql_spaced_qualified_names*.sql"], exact: true));
        Assert.Contains("fn_Target", definition.Name, StringComparison.Ordinal);

        var reference = Assert.Single(
            _reader.SearchReferences("dbo.fn_Target", lang: "sql", exact: true, pathPatterns: ["src/*sql_spaced_qualified_names*.sql"]));
        Assert.Contains("fn_Target", reference.ContainerName ?? string.Empty, StringComparison.Ordinal);

        var caller = Assert.Single(
            _reader.GetCallers("dbo.fn_Target", lang: "sql", exact: true, pathPatterns: ["src/*sql_spaced_qualified_names*.sql"]));
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
            _reader.SearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_table_reference*.sql"]));
        Assert.Equal("src/sql_alter_table_reference_migration.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_table_reference*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_table_reference*.sql"]));
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

        var references = _reader.SearchReferences("sales.OldInvoices", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_table_reference*.sql"]);
        var reference = Assert.Single(references);
        Assert.Equal("src/sql_drop_table_reference_migration.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("sales.OldInvoices", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_table_reference*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("sales.OldInvoices", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_table_reference*.sql"]));
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
            _reader.SearchReferences("dbo.AuditLog", lang: "sql", exact: true, pathPatterns: ["src/*sql_insert_without_into*.sql"]));
        Assert.Equal("src/sql_insert_without_into_writer.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.AuditLog", lang: "sql", exact: true, pathPatterns: ["src/*sql_insert_without_into*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.AuditLog", lang: "sql", exact: true, pathPatterns: ["src/*sql_insert_without_into*.sql"]));
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
            _reader.SearchReferences("dbo.OrderArchive", lang: "sql", exact: true, pathPatterns: ["src/*sql_select_into*.sql"]));
        Assert.Equal("src/sql_select_into_writer.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.OrderArchive", lang: "sql", exact: true, pathPatterns: ["src/*sql_select_into*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.OrderArchive", lang: "sql", exact: true, pathPatterns: ["src/*sql_select_into*.sql"]));
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
            _reader.SearchReferences("dbo.ImportQueue", lang: "sql", exact: true, pathPatterns: ["src/*sql_bulk_insert*.sql"]));
        Assert.Equal("src/sql_bulk_insert_writer.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.ImportQueue", lang: "sql", exact: true, pathPatterns: ["src/*sql_bulk_insert*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.ImportQueue", lang: "sql", exact: true, pathPatterns: ["src/*sql_bulk_insert*.sql"]));
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
            _reader.SearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_create_index*.sql"]));
        Assert.Equal("src/sql_create_index_definition.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_create_index*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_create_index*.sql"]));
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
            _reader.SearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_index*.sql"]));
        Assert.Equal("src/sql_alter_index_maintenance.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_index*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_index*.sql"]));
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
            _reader.SearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_index*.sql"]));
        Assert.Equal("src/sql_drop_index_cleanup.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_index*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_index*.sql"]));
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
            _reader.SearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_create_trigger*.sql"]));
        Assert.Equal("src/sql_create_trigger_definition.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_create_trigger*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_create_trigger*.sql"]));
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
            _reader.SearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_disable_trigger*.sql"]));
        Assert.Equal("src/sql_disable_trigger_maintenance.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_disable_trigger*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_disable_trigger*.sql"]));
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
            _reader.SearchReferences("dbo.Customers", lang: "sql", exact: true, pathPatterns: ["src/*sql_foreign_key*.sql"]));
        Assert.Equal("src/sql_foreign_key_source.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.Customers", lang: "sql", exact: true, pathPatterns: ["src/*sql_foreign_key*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.Customers", lang: "sql", exact: true, pathPatterns: ["src/*sql_foreign_key*.sql"]));
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
            _reader.SearchReferences("dbo.Customers", lang: "sql", exact: true, pathPatterns: ["src/*sql_synonym*.sql"]));
        Assert.Equal("src/sql_synonym_definition.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.Customers", lang: "sql", exact: true, pathPatterns: ["src/*sql_synonym*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.Customers", lang: "sql", exact: true, pathPatterns: ["src/*sql_synonym*.sql"]));
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
            _reader.SearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_schema_transfer*.sql"]));
        Assert.Equal("src/sql_alter_schema_transfer_move.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_schema_transfer*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_schema_transfer*.sql"]));
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
            _reader.SearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_update_statistics*.sql"]));
        Assert.Equal("src/sql_update_statistics_refresh.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_update_statistics*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_update_statistics*.sql"]));
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
            _reader.SearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_create_statistics*.sql"]));
        Assert.Equal("src/sql_create_statistics_definition.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_create_statistics*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_create_statistics*.sql"]));
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
            _reader.SearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_statistics*.sql"]));
        Assert.Equal("src/sql_drop_statistics_cleanup.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_statistics*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_statistics*.sql"]));
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
            _reader.SearchReferences("archive.OrdersArchive", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_table_switch*.sql"]));
        Assert.Equal("src/sql_alter_table_switch_move.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("archive.OrdersArchive", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_table_switch*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("archive.OrdersArchive", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_table_switch*.sql"]));
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
            _reader.SearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_object_permission*.sql"]));
        Assert.Equal("src/sql_object_permission_grant.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_object_permission*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_object_permission*.sql"]));
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
            _reader.SearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_bare_object_permission*.sql"]));
        Assert.Equal("src/sql_bare_object_permission_grant.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_bare_object_permission*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_bare_object_permission*.sql"]));
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
            _reader.SearchReferences("dbo.Documents", lang: "sql", exact: true, pathPatterns: ["src/*sql_create_fulltext_index*.sql"]));
        Assert.Equal("src/sql_create_fulltext_index_definition.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.Documents", lang: "sql", exact: true, pathPatterns: ["src/*sql_create_fulltext_index*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.Documents", lang: "sql", exact: true, pathPatterns: ["src/*sql_create_fulltext_index*.sql"]));
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
            _reader.SearchReferences("dbo.Documents", lang: "sql", exact: true, pathPatterns: ["src/*sql_create_special_xml_index*.sql"]));
        Assert.Equal("src/sql_create_special_xml_index_definition.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.Documents", lang: "sql", exact: true, pathPatterns: ["src/*sql_create_special_xml_index*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.Documents", lang: "sql", exact: true, pathPatterns: ["src/*sql_create_special_xml_index*.sql"]));
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
            _reader.SearchReferences("dbo.FactSales", lang: "sql", exact: true, pathPatterns: ["src/*sql_create_clustered_columnstore_index*.sql"]));
        Assert.Equal("src/sql_create_clustered_columnstore_index_definition.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.FactSales", lang: "sql", exact: true, pathPatterns: ["src/*sql_create_clustered_columnstore_index*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.FactSales", lang: "sql", exact: true, pathPatterns: ["src/*sql_create_clustered_columnstore_index*.sql"]));
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
            _reader.SearchReferences("dbo.OrderCache", lang: "sql", exact: true, pathPatterns: ["src/*sql_create_hash_index*.sql"]));
        Assert.Equal("src/sql_create_hash_index_definition.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.OrderCache", lang: "sql", exact: true, pathPatterns: ["src/*sql_create_hash_index*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.OrderCache", lang: "sql", exact: true, pathPatterns: ["src/*sql_create_hash_index*.sql"]));
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
            _reader.SearchReferences("dbo.Documents", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_fulltext_index*.sql"]));
        Assert.Equal("src/sql_alter_fulltext_index_maintenance.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.Documents", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_fulltext_index*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.Documents", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_fulltext_index*.sql"]));
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
            _reader.SearchReferences("dbo.Documents", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_fulltext_index*.sql"]));
        Assert.Equal("src/sql_drop_fulltext_index_cleanup.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.Documents", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_fulltext_index*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.Documents", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_fulltext_index*.sql"]));
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
            _reader.SearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_index_legacy*.sql"]));
        Assert.Equal("src/sql_drop_index_legacy_cleanup.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_index_legacy*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_index_legacy*.sql"]));
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
            _reader.SearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_delete_without_from*.sql"]));
        Assert.Equal("src/sql_delete_without_from_cleanup.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_delete_without_from*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_delete_without_from*.sql"]));
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
            _reader.SearchReferences("audit.OrderAudit", lang: "sql", exact: true, pathPatterns: ["src/*sql_output_into*.sql"]));
        Assert.Equal("src/sql_output_into_update.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("audit.OrderAudit", lang: "sql", exact: true, pathPatterns: ["src/*sql_output_into*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("audit.OrderAudit", lang: "sql", exact: true, pathPatterns: ["src/*sql_output_into*.sql"]));
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
            _reader.SearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_authorization_object*.sql"]));
        Assert.Equal("src/sql_alter_authorization_object_owner.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_authorization_object*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_authorization_object*.sql"]));
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
            _reader.SearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_authorization_bare*.sql"]));
        Assert.Equal("src/sql_alter_authorization_bare_owner.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_authorization_bare*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_authorization_bare*.sql"]));
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
            _reader.SearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_create_security_policy*.sql"]));
        Assert.Equal("src/sql_create_security_policy_definition.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_create_security_policy*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_create_security_policy*.sql"]));
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
            _reader.SearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_security_policy*.sql"]));
        Assert.Equal("src/sql_alter_security_policy_definition.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_security_policy*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.Orders", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_security_policy*.sql"]));
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
            _reader.SearchReferences("history.OrdersHistory", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_table_system_versioning*.sql"]));
        Assert.Equal("src/sql_alter_table_system_versioning_enable.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("history.OrdersHistory", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_table_system_versioning*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("history.OrdersHistory", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_table_system_versioning*.sql"]));
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
            _reader.SearchReferences("dbo.CustomerAlias", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_synonym*.sql"]));
        Assert.Equal("src/sql_drop_synonym_cleanup.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.CustomerAlias", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_synonym*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.CustomerAlias", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_synonym*.sql"]));
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
            _reader.SearchReferences("dbo.OrderSummary", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_view*.sql"]));
        Assert.Equal("src/sql_drop_view_cleanup.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.OrderSummary", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_view*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.OrderSummary", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_view*.sql"]));
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
            _reader.SearchReferences("dbo.RebuildOrders", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_procedure*.sql"]));
        Assert.Equal("src/sql_drop_procedure_cleanup.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.RebuildOrders", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_procedure*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.RebuildOrders", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_procedure*.sql"]));
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
            _reader.SearchReferences("dbo.CalculateTax", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_function*.sql"]));
        Assert.Equal("src/sql_drop_function_cleanup.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.CalculateTax", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_function*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.CalculateTax", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_function*.sql"]));
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
            _reader.SearchReferences("audit.OrdersAudit", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_trigger*.sql"]));
        Assert.Equal("src/sql_drop_trigger_cleanup.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("audit.OrdersAudit", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_trigger*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("audit.OrdersAudit", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_trigger*.sql"]));
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
            _reader.SearchReferences("dbo.OrderNumbers", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_sequence*.sql"]));
        Assert.Equal("src/sql_drop_sequence_cleanup.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.OrderNumbers", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_sequence*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.OrderNumbers", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_sequence*.sql"]));
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
            _reader.SearchReferences("dbo.CustomerKey", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_type*.sql"]));
        Assert.Equal("src/sql_drop_type_cleanup.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.CustomerKey", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_type*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.CustomerKey", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_type*.sql"]));
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
            _reader.SearchReferences("dbo.PositiveAmount", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_rule*.sql"]));
        Assert.Equal("src/sql_drop_rule_cleanup.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.PositiveAmount", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_rule*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.PositiveAmount", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_rule*.sql"]));
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
            _reader.SearchReferences("dbo.ZeroDefault", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_default*.sql"]));
        Assert.Equal("src/sql_drop_default_cleanup.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.ZeroDefault", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_default*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.ZeroDefault", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_default*.sql"]));
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
            _reader.SearchReferences("dbo.TotalAmount", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_aggregate*.sql"]));
        Assert.Equal("src/sql_drop_aggregate_cleanup.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.TotalAmount", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_aggregate*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.TotalAmount", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_aggregate*.sql"]));
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
            _reader.SearchReferences("dbo.CustomerFilter", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_security_policy*.sql"]));
        Assert.Equal("src/sql_drop_security_policy_cleanup.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.CustomerFilter", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_security_policy*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.CustomerFilter", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_security_policy*.sql"]));
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
            _reader.SearchReferences("ftOrders", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_fulltext_catalog*.sql"]));
        Assert.Equal("src/sql_drop_fulltext_catalog_cleanup.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("ftOrders", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_fulltext_catalog*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("ftOrders", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_fulltext_catalog*.sql"]));
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
            _reader.SearchReferences("psOrders", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_partition_scheme*.sql"]));
        Assert.Equal("src/sql_drop_partition_scheme_cleanup.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("psOrders", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_partition_scheme*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("psOrders", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_partition_scheme*.sql"]));
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
            _reader.SearchReferences("pfOrders", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_partition_function*.sql"]));
        Assert.Equal("src/sql_drop_partition_function_cleanup.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("pfOrders", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_partition_function*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("pfOrders", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_partition_function*.sql"]));
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
            _reader.SearchReferences("dbo.InvoiceSchema", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_xml_schema_collection*.sql"]));
        Assert.Equal("src/sql_drop_xml_schema_collection_cleanup.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.InvoiceSchema", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_xml_schema_collection*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.InvoiceSchema", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_xml_schema_collection*.sql"]));
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
            _reader.SearchReferences("SalesAssembly", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_assembly*.sql"]));
        Assert.Equal("src/sql_drop_assembly_cleanup.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("SalesAssembly", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_assembly*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("SalesAssembly", lang: "sql", exact: true, pathPatterns: ["src/*sql_drop_assembly*.sql"]));
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
            _reader.SearchReferences("dbo.OrderSummary", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_view*.sql"]));
        Assert.Equal("src/sql_alter_view_update.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.OrderSummary", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_view*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.OrderSummary", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_view*.sql"]));
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
            _reader.SearchReferences("dbo.RebuildOrders", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_procedure*.sql"]));
        Assert.Equal("src/sql_alter_procedure_update.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.RebuildOrders", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_procedure*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.RebuildOrders", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_procedure*.sql"]));
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
            _reader.SearchReferences("dbo.CalculateTax", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_function*.sql"]));
        Assert.Equal("src/sql_alter_function_update.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.CalculateTax", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_function*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.CalculateTax", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_function*.sql"]));
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
            _reader.SearchReferences("audit.OrdersAudit", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_trigger*.sql"]));
        Assert.Equal("src/sql_alter_trigger_update.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("audit.OrdersAudit", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_trigger*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("audit.OrdersAudit", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_trigger*.sql"]));
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
            _reader.SearchReferences("dbo.OrderNumbers", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_sequence*.sql"]));
        Assert.Equal("src/sql_alter_sequence_update.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.OrderNumbers", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_sequence*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.OrderNumbers", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_sequence*.sql"]));
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
            _reader.SearchReferences("dbo.CustomerFilter", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_security_policy_name*.sql"]));
        Assert.Equal("src/sql_alter_security_policy_name_update.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.CustomerFilter", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_security_policy_name*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.CustomerFilter", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_security_policy_name*.sql"]));
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
            _reader.SearchReferences("ftOrders", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_fulltext_catalog*.sql"]));
        Assert.Equal("src/sql_alter_fulltext_catalog_update.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("ftOrders", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_fulltext_catalog*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("ftOrders", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_fulltext_catalog*.sql"]));
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
            _reader.SearchReferences("pfOrders", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_partition_function*.sql"]));
        Assert.Equal("src/sql_alter_partition_function_update.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("pfOrders", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_partition_function*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("pfOrders", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_partition_function*.sql"]));
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
            _reader.SearchReferences("psOrders", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_partition_scheme*.sql"]));
        Assert.Equal("src/sql_alter_partition_scheme_update.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("psOrders", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_partition_scheme*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("psOrders", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_partition_scheme*.sql"]));
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
            _reader.SearchReferences("dbo.InvoiceSchema", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_xml_schema_collection*.sql"]));
        Assert.Equal("src/sql_alter_xml_schema_collection_update.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("dbo.InvoiceSchema", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_xml_schema_collection*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("dbo.InvoiceSchema", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_xml_schema_collection*.sql"]));
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
            _reader.SearchReferences("SalesAssembly", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_assembly*.sql"]));
        Assert.Equal("src/sql_alter_assembly_update.sql", reference.Path);
        Assert.Equal(1, _reader.CountSearchReferences("SalesAssembly", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_assembly*.sql"]));
        Assert.Equal(new QueryCountResult(1, 1, IncludesSql: true), _reader.CountSearchReferencesTotal("SalesAssembly", lang: "sql", exact: true, pathPatterns: ["src/*sql_alter_assembly*.sql"]));
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

        Assert.Equal(1, _reader.CountSearchSymbols(["dbo.äpfel"], lang: "sql", pathPatterns: ["src/*sql_quoted_unicode_exact_definition*.sql"], exact: true));

        var symbol = Assert.Single(_reader.SearchSymbols(["dbo.äpfel"], limit: 10, lang: "sql", pathPatterns: ["src/*sql_quoted_unicode_exact_definition*.sql"], exact: true));
        Assert.Equal("[dbo].[Äpfel]", symbol.Name);

        var definition = Assert.Single(_reader.GetDefinitions("dbo.äpfel", limit: 10, lang: "sql", pathPatterns: ["src/*sql_quoted_unicode_exact_definition*.sql"], exact: true));
        Assert.Equal("[dbo].[Äpfel]", definition.Name);

        var analysis = _reader.AnalyzeSymbol("dbo.äpfel", limit: 10, lang: "sql", pathPatterns: ["src/*sql_quoted_unicode_exact_definition*.sql"], exact: true);
        Assert.Equal("[dbo].[Äpfel]", Assert.Single(analysis.Definitions).Name);
        Assert.Equal("[dbo].[Caller]", Assert.Single(analysis.Callers).CallerName);

        var impact = _reader.AnalyzeImpact("dbo.äpfel", maxDepth: 1, limit: 10, lang: "sql", pathPatterns: ["src/*sql_quoted_unicode_exact_definition*.sql"]);
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

        Assert.Equal(1, _reader.CountSearchSymbols(["äpfel"], lang: "sql", pathPatterns: ["src/*sql_unqualified_unicode_exact_definition*.sql"], exact: true));

        var symbol = Assert.Single(_reader.SearchSymbols(["äpfel"], limit: 10, lang: "sql", pathPatterns: ["src/*sql_unqualified_unicode_exact_definition*.sql"], exact: true));
        Assert.Equal("dbo.Äpfel", symbol.Name);

        var definition = Assert.Single(_reader.GetDefinitions("äpfel", limit: 10, lang: "sql", pathPatterns: ["src/*sql_unqualified_unicode_exact_definition*.sql"], exact: true));
        Assert.Equal("dbo.Äpfel", definition.Name);

        var analysis = _reader.AnalyzeSymbol("äpfel", limit: 10, lang: "sql", pathPatterns: ["src/*sql_unqualified_unicode_exact_definition*.sql"], exact: true);
        Assert.Equal("dbo.Äpfel", Assert.Single(analysis.Definitions).Name);
        Assert.Equal("dbo.Caller", Assert.Single(analysis.Callers).CallerName);
    }
}
