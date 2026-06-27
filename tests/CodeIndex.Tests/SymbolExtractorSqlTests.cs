using System.Diagnostics;
using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Xml;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public partial class SymbolExtractorTests
{
    [Fact]
    public void Extract_SQL_DetectsCreateStatements()
    {
        var content = "CREATE TEMP TABLE users (\n  id INT PRIMARY KEY\n);\n\nCREATE OR REPLACE FUNCTION get_user(id INT) RETURNS void;\n\nCREATE MATERIALIZED VIEW active_users AS SELECT * FROM users;\n\nCREATE TYPE color AS ENUM ('red', 'green');\nCREATE TYPE inventory_item AS (name text);\nCREATE SCHEMA analytics;\nCREATE SCHEMA AUTHORIZATION analytics_owner;\nCREATE SEQUENCE order_seq;\nCREATE EXTENSION IF NOT EXISTS pgcrypto;\nCREATE DOMAIN positive_int AS integer CHECK (VALUE > 0);\nCREATE UNIQUE INDEX users_email_idx ON users (id);\n\nALTER TABLE users ADD COLUMN email TEXT;";
        var symbols = SymbolExtractor.Extract(1, "sql", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "users");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "get_user");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "active_users");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "color");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "inventory_item");
        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "analytics");
        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "analytics_owner");
        Assert.DoesNotContain(symbols, s => s.Kind == "namespace" && s.Name == "AUTHORIZATION");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "order_seq");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "pgcrypto");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "positive_int");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "users_email_idx");
    }

    [Fact]
    public void Extract_SQL_DoesNotCaptureOnAsAnonymousIndexName()
    {
        var content = "CREATE INDEX ON users (email);\nCREATE INDEX IF NOT EXISTS users_name_idx ON users (name);";
        var symbols = SymbolExtractor.Extract(1, "sql", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "ON");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "users_name_idx");
    }

    [Fact]
    public void Extract_SQL_MySqlDefinerCreatesDefinerSymbols()
    {
        const string content = """
            CREATE DEFINER='admin'@'%' PROCEDURE schema.proc()
            BEGIN
              SELECT 1;
            END;
            CREATE DEFINER=`app_user`@`localhost` VIEW `schema`.`v_orders` AS SELECT 1;
            """;

        var symbols = SymbolExtractor.Extract(1, "sql", content);

        Assert.Contains(symbols, s => s.Kind == "definer" && s.Name == "admin@%");
        Assert.Contains(symbols, s => s.Kind == "definer" && s.Name == "app_user@localhost");
    }

    [Fact]
    public void Extract_SQL_PostgresReturnsTableAndOutParametersCreateFieldSymbols()
    {
        const string content = """
            CREATE FUNCTION public.search_orders()
            RETURNS TABLE(id bigint, customer_name text, total numeric(12, 2))
            AS $$
            BEGIN
              RETURN QUERY SELECT id, customer_name, total FROM orders;
            END;
            $$ LANGUAGE plpgsql;

            CREATE FUNCTION public.load_order(OUT order_id int, OUT order_name text) RETURNS RECORD
            AS $$ SELECT 1, 'a' $$ LANGUAGE sql;
            """;

        var symbols = SymbolExtractor.Extract(1, "sql", content);

        Assert.Contains(symbols, s => s.Kind == "field" && s.Name == "id" && s.ContainerName == "public.search_orders" && s.ReturnType == "bigint");
        Assert.Contains(symbols, s => s.Kind == "field" && s.Name == "customer_name" && s.ContainerName == "public.search_orders" && s.ReturnType == "text");
        Assert.Contains(symbols, s => s.Kind == "field" && s.Name == "total" && s.ContainerName == "public.search_orders" && s.ReturnType == "numeric(12, 2)");
        Assert.Contains(symbols, s => s.Kind == "field" && s.Name == "order_id" && s.ContainerName == "public.load_order");
        Assert.Contains(symbols, s => s.Kind == "field" && s.Name == "order_name" && s.ContainerName == "public.load_order");
    }

    [Fact]
    public void Extract_SQL_DoesNotEmitDefinerOrReturnFieldsFromCommentsAndStrings()
    {
        const string content = """
            -- CREATE DEFINER='ghost'@'%' PROCEDURE hidden()
            SELECT 'RETURNS TABLE(fake int)';
            CREATE FUNCTION public.real()
            RETURNS TABLE(real_id int)
            AS $$ SELECT 1 $$ LANGUAGE sql;
            """;

        var symbols = SymbolExtractor.Extract(1, "sql", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "definer" && s.Name == "ghost@%");
        Assert.DoesNotContain(symbols, s => s.Kind == "field" && s.Name == "fake");
        Assert.Contains(symbols, s => s.Kind == "field" && s.Name == "real_id");
    }

    [Fact]
    public void Extract_SQL_DetectsTSqlDdlKinds()
    {
        var content =
            "CREATE SCHEMA sales AUTHORIZATION dbo;\n" +
            "CREATE TYPE sales.Money FROM DECIMAL(18, 4) NOT NULL;\n" +
            "CREATE SEQUENCE sales.OrderSeq START WITH 1 INCREMENT BY 1;\n" +
            "CREATE AGGREGATE dbo.SumInt (@value INT) RETURNS INT EXTERNAL NAME dbo.SumInt;\n" +
            "CREATE ASSEMBLY dbo.MyAssembly FROM 0x010203;\n" +
            "CREATE XML SCHEMA COLLECTION dbo.XmlCollection AS N'<schema />';\n" +
            "CREATE RULE sales.discount_rule AS @price > 0;\n" +
            "CREATE DEFAULT dbo.zero_default AS 0;\n" +
            "CREATE SYNONYM dbo.Customers FOR external.Customers;\n" +
            "CREATE LOGIN [app_service] WITH PASSWORD = 'x';\n" +
            "CREATE USER app_service FOR LOGIN app_service;\n" +
            "CREATE ROLE sales_writer AUTHORIZATION dbo;\n" +
            "CREATE DATABASE sales_db;\n" +
            "CREATE CERTIFICATE svc_cert WITH SUBJECT = 'svc';\n" +
            "CREATE SECURITY POLICY SalesFilter ADD FILTER PREDICATE Security.fn_salesPredicate(SalesRep) ON dbo.Orders WITH (STATE = ON);\n" +
            "CREATE PARTITION FUNCTION pf_OrdersByYear (datetime2) AS RANGE RIGHT FOR VALUES ('2024-01-01');\n" +
            "CREATE PARTITION SCHEME ps_OrdersByYear AS PARTITION pf_OrdersByYear TO ([primary]);\n" +
            "CREATE FULLTEXT CATALOG ftc_sales WITH ACCENT_SENSITIVITY=OFF;\n" +
            "CREATE PROC dbo.sp_DailyReport @Date DATE AS BEGIN SELECT 1; END\n" +
            "CREATE PROCEDURE [dbo].[sp_GetOrder] @Id INT AS SELECT 1;\n" +
            "CREATE OR ALTER PROCEDURE dbo.sp_UpsertUser AS SELECT 1;\n" +
            "CREATE OR ALTER VIEW dbo.v_ActiveOrders AS SELECT 1;\n" +
            "ALTER PROCEDURE dbo.sp_DailyReport AS SELECT 2;\n" +
            "ALTER FUNCTION dbo.fn_Total() RETURNS INT AS BEGIN RETURN 1 END;\n" +
            "ALTER PARTITION FUNCTION pf_OrdersByYear() SPLIT RANGE ('2025-01-01');\n" +
            "ALTER AGGREGATE dbo.SumInt (@value INT) RETURNS INT EXTERNAL NAME dbo.SumIntV2;\n" +
            "ALTER ASSEMBLY dbo.MyAssembly FROM 0x040506;\n" +
            "ALTER XML SCHEMA COLLECTION dbo.XmlCollection ADD N'<schema />';\n" +
            "ALTER SCHEMA sales TRANSFER dbo.Customers;\n" +
            "ALTER EXTENSION hstore UPDATE TO '1.5';\n" +
            "ALTER CERTIFICATE svc_cert WITH PRIVATE KEY (DECRYPTION BY PASSWORD = 'x');\n" +
            "ALTER SECURITY POLICY SalesFilter WITH (STATE = OFF);\n" +
            "ALTER DOMAIN us_postal_code DROP NOT NULL;\n";
        var symbols = SymbolExtractor.Extract(1, "sql", content);

        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "sales");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "sales.Money");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "sales.OrderSeq");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "dbo.SumInt");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "dbo.MyAssembly");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "dbo.XmlCollection");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "sales.discount_rule");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "dbo.zero_default");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "dbo.Customers");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "[app_service]");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "app_service");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "sales_writer");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "sales_db");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "svc_cert");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "SalesFilter");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "pf_OrdersByYear");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "ps_OrdersByYear");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "ftc_sales");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "dbo.sp_DailyReport");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "[dbo].[sp_GetOrder]");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "dbo.sp_UpsertUser");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "dbo.v_ActiveOrders");
        // ALTER on an object kind other than TABLE must now be captured with the same kind
        // contract as the matching CREATE row (function for procedure-like, namespace for schema,
        // import for extension, class for everything else).
        // TABLE 以外のオブジェクトに対する ALTER も、対応する CREATE 行と同じ kind 契約で
        // 捕捉されること（プロシージャ類は function、SCHEMA は namespace、EXTENSION は import、
        // その他は class）。
        Assert.Equal(2, symbols.Count(s => s.Name == "dbo.sp_DailyReport"));
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "dbo.sp_DailyReport" && s.Signature != null && s.Signature.StartsWith("ALTER PROCEDURE", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "dbo.fn_Total");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "pf_OrdersByYear" && s.Signature != null && s.Signature.StartsWith("ALTER PARTITION FUNCTION", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "dbo.SumInt" && s.Signature != null && s.Signature.StartsWith("CREATE AGGREGATE", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "dbo.SumInt" && s.Signature != null && s.Signature.StartsWith("ALTER AGGREGATE", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "dbo.MyAssembly" && s.Signature != null && s.Signature.StartsWith("CREATE ASSEMBLY", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "dbo.MyAssembly" && s.Signature != null && s.Signature.StartsWith("ALTER ASSEMBLY", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "dbo.XmlCollection" && s.Signature != null && s.Signature.StartsWith("CREATE XML SCHEMA COLLECTION", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "dbo.XmlCollection" && s.Signature != null && s.Signature.StartsWith("ALTER XML SCHEMA COLLECTION", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "sales" && s.Signature != null && s.Signature.StartsWith("ALTER SCHEMA", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "hstore");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "svc_cert" && s.Signature != null && s.Signature.StartsWith("ALTER CERTIFICATE", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "SalesFilter" && s.Signature != null && s.Signature.StartsWith("ALTER SECURITY POLICY", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "us_postal_code");

        // SQL `CREATE SCHEMA sales;` and `ALTER SCHEMA sales TRANSFER ...;` are body-less
        // namespace-kind symbols. They must NOT be treated as C# file-scoped namespaces and
        // must not wrap every subsequent top-level SQL symbol as their container — SQL
        // schema is expressed through qualified names (`sales.Money`) rather than containment.
        // SQL の `CREATE SCHEMA sales;` と `ALTER SCHEMA sales TRANSFER ...;` は body 無しの
        // namespace kind だが、C# の file-scoped namespace 扱いにしてはならない。SQL の schema
        // は包含ではなく `sales.Money` のような修飾名で表すので、後続の top-level シンボルを
        // schema 配下に吸い込んではいけない。
        var containerPollutionVictims = new[]
        {
            "dbo.fn_Total",
            "pf_OrdersByYear",
            "hstore",
            "us_postal_code",
            "[app_service]",
            "app_service",
            "sales_writer",
            "sales_db",
            "svc_cert",
            "ps_OrdersByYear",
            "ftc_sales",
            "dbo.sp_DailyReport",
            "[dbo].[sp_GetOrder]",
            "dbo.sp_UpsertUser",
            "dbo.v_ActiveOrders",
        };
        foreach (var name in containerPollutionVictims)
        {
            Assert.All(
                symbols.Where(s => s.Name == name),
                s => Assert.True(
                    s.ContainerKind != "namespace" || s.ContainerName != "sales",
                    $"{s.Kind} {s.Name} was wrapped under namespace=sales — ALTER/CREATE SCHEMA must not act as a C# file-scoped namespace container."));
        }
    }

    [Fact]
    public void Extract_SQL_QualifiedNamesAllowWhitespaceAroundDots()
    {
        var content =
            "CREATE PROCEDURE [sales] . [sp_Report] AS SELECT 1;\n" +
            "CREATE VIEW dbo . v_Orders AS SELECT 1;\n" +
            "CREATE TYPE sales . Money AS ENUM ('usd');\n";

        var symbols = SymbolExtractor.Extract(1, "sql", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name.Contains("sp_Report", StringComparison.Ordinal));
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name.Contains("v_Orders", StringComparison.Ordinal));
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name.Contains("Money", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_SQL_ProcedureBodyRange_TSqlBeginEnd()
    {
        // Multi-line T-SQL CREATE PROCEDURE with explicit BEGIN/END body terminated by GO.
        // ReferenceExtractor.ResolveContainerForCall depends on BodyStartLine/BodyEndLine
        // covering the lines that hold EXEC / CALL calls inside the procedure (issue #429).
        // GO で終わる複数行 T-SQL CREATE PROCEDURE（BEGIN/END 本体）。
        // ReferenceExtractor.ResolveContainerForCall は本体内の EXEC / CALL を含む行を
        // カバーする BodyStartLine / BodyEndLine に依存する（issue #429）。
        var content =
            "CREATE PROCEDURE dbo.sp_Outer\n" +  // line 1
            "AS\n" +                              // line 2
            "BEGIN\n" +                           // line 3
            "  EXEC dbo.sp_Inner;\n" +            // line 4
            "  SELECT 1;\n" +                     // line 5
            "END\n" +                             // line 6
            "GO\n" +                              // line 7
            "CREATE PROCEDURE dbo.sp_Inner\n" +   // line 8
            "AS\n" +                              // line 9
            "BEGIN\n" +                           // line 10
            "  SELECT 2;\n" +                     // line 11
            "END\n" +                             // line 12
            "GO\n";                               // line 13

        var symbols = SymbolExtractor.Extract(1, "sql", content);

        var outer = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "dbo.sp_Outer");
        Assert.Equal(1, outer.StartLine);
        Assert.NotNull(outer.BodyStartLine);
        Assert.NotNull(outer.BodyEndLine);
        // Body must cover the EXEC call on line 4 so callers/impact can attribute it.
        // EXEC 行（4 行目）を本体が覆う必要がある（callers / impact が帰属させられるように）。
        Assert.True(outer.BodyStartLine!.Value <= 4, $"BodyStartLine={outer.BodyStartLine} must be <= 4");
        Assert.True(outer.BodyEndLine!.Value >= 6, $"BodyEndLine={outer.BodyEndLine} must be >= 6 (body END)");
        // Body must not leak into the next procedure on line 8.
        // 8 行目の次のプロシージャまで本体が伸びてはいけない。
        Assert.True(outer.BodyEndLine!.Value < 8, $"BodyEndLine={outer.BodyEndLine} must not leak into the next CREATE at line 8");

        var inner = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "dbo.sp_Inner");
        Assert.Equal(8, inner.StartLine);
        Assert.NotNull(inner.BodyStartLine);
        Assert.NotNull(inner.BodyEndLine);
        Assert.True(inner.BodyStartLine!.Value <= 11, $"BodyStartLine={inner.BodyStartLine} must cover SELECT on line 11");
        Assert.True(inner.BodyEndLine!.Value >= 12, $"BodyEndLine={inner.BodyEndLine} must cover END on line 12");
    }

    [Fact]
    public void Extract_SQL_ProcedureBodyRange_PostgresDollarQuoted()
    {
        // PostgreSQL CREATE FUNCTION ... AS $$ ... $$ must resolve BodyEndLine to the line
        // containing the closing `$$`, regardless of BEGIN / END / GO / ; inside the body.
        // PostgreSQL の `CREATE FUNCTION ... AS $$ ... $$` は、本体内の BEGIN / END / GO / ;
        // に関係なく、閉じ `$$` の行で BodyEndLine を解決できる必要がある。
        var content =
            "CREATE OR REPLACE FUNCTION public.notify_user(uid INT) RETURNS void AS $$\n" +  // line 1
            "DECLARE msg TEXT;\n" +                                                            // line 2
            "BEGIN\n" +                                                                        // line 3
            "  msg := 'hi; GO -- fake terminator';\n" +                                        // line 4
            "  PERFORM public.enqueue(uid, msg);\n" +                                          // line 5
            "END;\n" +                                                                         // line 6
            "$$ LANGUAGE plpgsql;\n" +                                                         // line 7
            "\n" +                                                                             // line 8
            "CREATE FUNCTION public.enqueue(uid INT, msg TEXT) RETURNS void AS $$\n" +         // line 9
            "BEGIN\n" +                                                                        // line 10
            "  INSERT INTO outbox VALUES (uid, msg);\n" +                                      // line 11
            "END;\n" +                                                                         // line 12
            "$$ LANGUAGE plpgsql;\n";                                                          // line 13

        var symbols = SymbolExtractor.Extract(1, "sql", content);

        var notify = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "public.notify_user");
        Assert.Equal(1, notify.StartLine);
        Assert.NotNull(notify.BodyStartLine);
        Assert.NotNull(notify.BodyEndLine);
        Assert.True(notify.BodyStartLine!.Value <= 5, $"BodyStartLine={notify.BodyStartLine} must cover PERFORM on line 5");
        Assert.Equal(7, notify.BodyEndLine);

        var enqueue = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "public.enqueue");
        Assert.Equal(9, enqueue.StartLine);
        Assert.NotNull(enqueue.BodyStartLine);
        Assert.NotNull(enqueue.BodyEndLine);
        Assert.True(enqueue.BodyStartLine!.Value <= 11, $"BodyStartLine={enqueue.BodyStartLine} must cover INSERT on line 11");
        Assert.Equal(13, enqueue.BodyEndLine);
    }

    [Fact]
    public void Extract_SQL_ProcedureBodyRange_ClosesAtNextCreateWithoutGo()
    {
        // No `GO` between two procedures — the new-DDL-start guard must still close the
        // previous body so the second CREATE's header is not swallowed into sp_First's body.
        // プロシージャ間に `GO` が無い場合でも、次の DDL 行で前のボディを閉じる必要がある
        // （そうしないと次の CREATE 行が sp_First のボディに吸い込まれる）。
        var content =
            "CREATE PROCEDURE dbo.sp_First AS\n" +  // line 1
            "BEGIN\n" +                              // line 2
            "  EXEC dbo.sp_Helper;\n" +              // line 3
            "END\n" +                                // line 4
            "CREATE PROCEDURE dbo.sp_Second AS\n" +  // line 5
            "BEGIN\n" +                              // line 6
            "  SELECT 2;\n" +                        // line 7
            "END\n";                                 // line 8

        var symbols = SymbolExtractor.Extract(1, "sql", content);

        var first = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "dbo.sp_First");
        Assert.NotNull(first.BodyEndLine);
        Assert.True(first.BodyEndLine!.Value <= 4, $"BodyEndLine={first.BodyEndLine} must close before the next CREATE on line 5");

        var second = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "dbo.sp_Second");
        Assert.Equal(5, second.StartLine);
    }

    [Fact]
    public void Extract_SQL_ProcedureBodyRange_SingleLineAsBeginEnd()
    {
        // Single-line `CREATE PROC ... AS BEGIN ... END` must still expose a body range so
        // that any call on that same line (e.g. an EXEC inside a single-line body) can be
        // attributed back to the procedure.
        // 1 行で書かれた `CREATE PROC ... AS BEGIN ... END` でも、同一行の呼び出しを
        // プロシージャに帰属させるため、必ず body range を返す必要がある。
        var content =
            "CREATE PROC dbo.sp_A AS BEGIN EXEC dbo.sp_B; END\n" +
            "CREATE PROC dbo.sp_B AS BEGIN SELECT 1; END\n";

        var symbols = SymbolExtractor.Extract(1, "sql", content);

        var a = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "dbo.sp_A");
        Assert.NotNull(a.BodyStartLine);
        Assert.NotNull(a.BodyEndLine);
        Assert.True(a.BodyStartLine!.Value <= 1 && a.BodyEndLine!.Value >= 1,
            $"single-line sp_A body range must cover line 1 (got [{a.BodyStartLine}, {a.BodyEndLine}])");
        Assert.True(a.BodyEndLine!.Value < 2, $"sp_A body must not leak into sp_B on line 2 (got {a.BodyEndLine})");
    }

    [Fact]
    public void Extract_SQL_ProcedureBodyRange_IgnoresTerminatorsInStringsAndComments()
    {
        // A `GO` inside a string literal or a block comment must not close the body
        // prematurely — MaskSqlLineForBodyScan strips strings/comments before the scan.
        // 文字列や `/* ... */` 内の `GO` で本体を閉じないこと
        // （MaskSqlLineForBodyScan が文字列・コメントを除去するため）。
        var content =
            "CREATE PROCEDURE dbo.sp_NoisyBody AS\n" +       // line 1
            "BEGIN\n" +                                       // line 2
            "  DECLARE @msg NVARCHAR(100) = 'GO ahead';\n" +  // line 3 — 'GO' in string
            "  /* GO */\n" +                                  // line 4 — GO in block comment
            "  -- GO line-comment\n" +                        // line 5 — GO in line comment
            "  EXEC dbo.sp_Target;\n" +                       // line 6
            "END\n" +                                         // line 7
            "GO\n" +                                          // line 8 — real terminator
            "CREATE PROCEDURE dbo.sp_Target AS\n" +           // line 9
            "BEGIN SELECT 1; END\n" +                         // line 10
            "GO\n";                                           // line 11

        var symbols = SymbolExtractor.Extract(1, "sql", content);

        var noisy = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "dbo.sp_NoisyBody");
        Assert.NotNull(noisy.BodyStartLine);
        Assert.NotNull(noisy.BodyEndLine);
        // Body must cover the real EXEC on line 6 and must not stop at the fake GO on line 3/4/5.
        // 本体は line 6 の本物の EXEC を覆い、line 3/4/5 の偽 GO で止まってはいけない。
        Assert.True(noisy.BodyEndLine!.Value >= 6, $"BodyEndLine={noisy.BodyEndLine} must cover the real EXEC on line 6");
        Assert.True(noisy.BodyEndLine!.Value < 9, $"BodyEndLine={noisy.BodyEndLine} must not leak into sp_Target on line 9");

        var target = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "dbo.sp_Target");
        Assert.Equal(9, target.StartLine);
    }

    [Fact]
    public void Extract_SQL_ProcedureBodyRange_AlterProcedureHasBody()
    {
        // ALTER PROCEDURE / ALTER FUNCTION / ALTER TRIGGER share the body shape with CREATE
        // and must get a body range so replacement implementations' inner calls resolve too.
        // ALTER PROCEDURE / ALTER FUNCTION / ALTER TRIGGER は CREATE と本体形状を共有するので、
        // 置換実装内の呼び出しも解決できるよう body range を持つ必要がある。
        var content =
            "ALTER PROCEDURE dbo.sp_Reset AS\n" +   // line 1
            "BEGIN\n" +                              // line 2
            "  EXEC dbo.sp_Clear;\n" +               // line 3
            "END\n" +                                // line 4
            "GO\n";                                  // line 5

        var symbols = SymbolExtractor.Extract(1, "sql", content);

        var reset = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "dbo.sp_Reset");
        Assert.NotNull(reset.BodyStartLine);
        Assert.NotNull(reset.BodyEndLine);
        Assert.True(reset.BodyEndLine!.Value >= 3, $"BodyEndLine={reset.BodyEndLine} must cover the EXEC on line 3");
        Assert.True(reset.BodyEndLine!.Value < 5, $"BodyEndLine={reset.BodyEndLine} must not include the GO batch terminator on line 5");
    }

    [Fact]
    public void Extract_SQL_AlterPartitionFunctionHasNoBody()
    {
        // ALTER PARTITION FUNCTION only changes partition boundaries (no code body), so it
        // must keep BodyStartLine / BodyEndLine unset even though ALTER PROCEDURE / FUNCTION
        // / TRIGGER now resolve a body via SqlProcBody.
        // ALTER PARTITION FUNCTION は境界変更のみ（コード本体なし）のため、
        // ALTER PROCEDURE / FUNCTION / TRIGGER が SqlProcBody で本体を取るようになっても、
        // BodyStartLine / BodyEndLine は null のまま維持する必要がある。
        var content =
            "ALTER PARTITION FUNCTION pf_OrdersByYear() SPLIT RANGE ('2025-01-01');\n" +
            "CREATE PROCEDURE dbo.sp_After AS BEGIN SELECT 1; END\n";

        var symbols = SymbolExtractor.Extract(1, "sql", content);

        var partition = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "pf_OrdersByYear");
        Assert.Null(partition.BodyStartLine);
        Assert.Null(partition.BodyEndLine);
    }

    [Fact]
    public void Extract_SQL_ProcedureBodyRange_DoesNotPolluteContainer()
    {
        // Regression guard for the schema-pollution invariant (existing
        // Extract_SQL_DetectsTSqlDdlKinds) extended to proc-body ranges: a CREATE PROCEDURE
        // with a real body must not wrap the *next* proc as its container.
        // スキーマ汚染防止の不変量（既存の Extract_SQL_DetectsTSqlDdlKinds）を、今回追加した
        // プロシージャ本体にも拡張する。本体を持つ CREATE PROCEDURE が「次の」プロシージャを
        // コンテナとして囲ってはならない。
        var content =
            "CREATE PROCEDURE dbo.sp_First AS\n" +
            "BEGIN\n" +
            "  SELECT 1;\n" +
            "END\n" +
            "GO\n" +
            "CREATE PROCEDURE dbo.sp_Second AS\n" +
            "BEGIN\n" +
            "  SELECT 2;\n" +
            "END\n" +
            "GO\n";

        var symbols = SymbolExtractor.Extract(1, "sql", content);

        var second = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "dbo.sp_Second");
        Assert.True(
            second.ContainerKind != "function" || second.ContainerName != "dbo.sp_First",
            $"dbo.sp_Second was wrapped under container=dbo.sp_First — CREATE PROCEDURE body must not wrap sibling procedures.");
    }

    [Fact]
    public void Extract_SQL_ProcedureBodyRange_BodyInternalCreateTableDoesNotCloseBody()
    {
        // Regression for codex review iteration 1 finding #1: the body terminator heuristic must
        // only react to *another* proc-like DDL header (`CREATE|ALTER|DROP PROCEDURE|PROC|FUNCTION|
        // TRIGGER`), not to ordinary body-internal DDL like `CREATE TABLE #tmp` / `ALTER TABLE`.
        // Otherwise issue #429 re-appears whenever a T-SQL procedure stages a temp table before its
        // real work.
        // codex レビュー iteration 1 指摘 #1 の回帰テスト: 本体終端の判定は別の proc 系ヘッダ
        // （`CREATE|ALTER|DROP PROCEDURE|PROC|FUNCTION|TRIGGER`）のときだけ発火し、本体内の普通の
        // DDL（`CREATE TABLE #tmp` / `ALTER TABLE` など）では閉じてはならない。そうしないと、T-SQL
        // プロシージャが一時テーブルを用意してから実処理する典型パターンで issue #429 が再発する。
        var content =
            "CREATE PROCEDURE dbo.sp_Stage AS\n" +  // line 1
            "BEGIN\n" +                              // line 2
            "  CREATE TABLE #tmp(id INT);\n" +      // line 3 — body-internal DDL, must NOT close
            "  ALTER TABLE #tmp ADD name NVARCHAR(100);\n" + // line 4 — same
            "  EXEC dbo.sp_Inner;\n" +               // line 5 — real EXEC must be inside body
            "END\n" +                                // line 6
            "GO\n" +                                 // line 7
            "CREATE PROCEDURE dbo.sp_Inner AS BEGIN SELECT 1; END\n" + // line 8
            "GO\n";                                  // line 9

        var symbols = SymbolExtractor.Extract(1, "sql", content);

        var stage = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "dbo.sp_Stage");
        Assert.NotNull(stage.BodyStartLine);
        Assert.NotNull(stage.BodyEndLine);
        Assert.True(stage.BodyEndLine!.Value >= 5,
            $"BodyEndLine={stage.BodyEndLine} must cover the EXEC on line 5; body-internal CREATE TABLE / ALTER TABLE must not close the body.");
        Assert.True(stage.BodyEndLine!.Value < 8,
            $"BodyEndLine={stage.BodyEndLine} must not leak into sp_Inner starting on line 8.");
    }

    [Fact]
    public void Extract_SQL_ProcedureBodyRange_MultiLineBlockCommentDoesNotCloseBody()
    {
        // Regression for codex review iteration 1 finding #2: MaskSqlLineForBodyScan threads an
        // `inBlockComment` state across lines, so a bare `GO` or `CREATE` appearing inside a
        // multi-line `/* ... */` block must not close the enclosing procedure body.
        // codex レビュー iteration 1 指摘 #2 の回帰テスト: MaskSqlLineForBodyScan は `inBlockComment`
        // を行間に持ち越し、複数行の `/* ... */` ブロック内に現れる単独 `GO` / `CREATE` では
        // 外側プロシージャ本体を閉じてはならない。
        var content =
            "CREATE PROCEDURE dbo.sp_CommentHeavy AS\n" + // line 1
            "BEGIN\n" +                                     // line 2
            "  /*\n" +                                      // line 3 — block comment opens
            "   GO\n" +                                     // line 4 — bare GO inside comment
            "   CREATE PROCEDURE dbo.sp_FakeInner AS SELECT 0;\n" + // line 5 — fake header inside comment
            "  */\n" +                                      // line 6 — block comment closes
            "  EXEC dbo.sp_Real;\n" +                       // line 7 — real EXEC must be inside body
            "END\n" +                                       // line 8
            "GO\n" +                                        // line 9 — real terminator
            "CREATE PROCEDURE dbo.sp_Real AS BEGIN SELECT 1; END\n" + // line 10
            "GO\n";                                         // line 11

        var symbols = SymbolExtractor.Extract(1, "sql", content);

        var heavy = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "dbo.sp_CommentHeavy");
        Assert.NotNull(heavy.BodyStartLine);
        Assert.NotNull(heavy.BodyEndLine);
        Assert.True(heavy.BodyEndLine!.Value >= 7,
            $"BodyEndLine={heavy.BodyEndLine} must cover the real EXEC on line 7; multi-line block comment must not close the body.");
        Assert.True(heavy.BodyEndLine!.Value < 10,
            $"BodyEndLine={heavy.BodyEndLine} must not leak into sp_Real starting on line 10.");
    }

    [Fact]
    public void Extract_SQL_ProcedureBodyRange_CreateOrAlterClosesPriorBody()
    {
        // Regression for codex review iteration 2 finding #1: SqlTopLevelDdlStartRegex must accept
        // both PostgreSQL `CREATE OR REPLACE PROCEDURE` and T-SQL `CREATE OR ALTER PROCEDURE`
        // (SQL Server 2016+) so a sibling `CREATE OR ALTER PROCEDURE` declaration without an
        // intervening `GO` actually terminates the previous procedure's body range.
        // codex レビュー iteration 2 指摘 #1 の回帰テスト: SqlTopLevelDdlStartRegex は PostgreSQL の
        // `CREATE OR REPLACE PROCEDURE` と T-SQL（SQL Server 2016+）の `CREATE OR ALTER PROCEDURE`
        // の両方を受理し、`GO` 区切りなしの隣接 `CREATE OR ALTER PROCEDURE` 宣言で前 proc の
        // body 範囲を確実に閉じなければならない。
        var content =
            "CREATE OR ALTER PROCEDURE dbo.sp_First AS\n" + // line 1
            "BEGIN\n" +                                     // line 2
            "  EXEC dbo.sp_Inner;\n" +                      // line 3 — must be inside sp_First body
            "END\n" +                                       // line 4
            "CREATE OR ALTER PROCEDURE dbo.sp_Second AS\n" +// line 5 — sibling must close sp_First
            "BEGIN\n" +                                     // line 6
            "  EXEC dbo.sp_Other;\n" +                      // line 7 — must be inside sp_Second body
            "END\n";                                        // line 8

        var symbols = SymbolExtractor.Extract(1, "sql", content);

        var first = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "dbo.sp_First");
        Assert.NotNull(first.BodyStartLine);
        Assert.NotNull(first.BodyEndLine);
        Assert.True(first.BodyEndLine!.Value >= 3,
            $"BodyEndLine={first.BodyEndLine} must cover sp_First's EXEC on line 3.");
        Assert.True(first.BodyEndLine!.Value < 5,
            $"BodyEndLine={first.BodyEndLine} must close before sp_Second begins on line 5; CREATE OR ALTER PROCEDURE sibling must terminate the prior body.");

        var second = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "dbo.sp_Second");
        Assert.NotNull(second.BodyStartLine);
        Assert.NotNull(second.BodyEndLine);
        Assert.True(second.BodyStartLine!.Value >= 5,
            $"BodyStartLine={second.BodyStartLine} for sp_Second must start at or after line 5.");
        Assert.True(second.BodyEndLine!.Value >= 7,
            $"BodyEndLine={second.BodyEndLine} must cover sp_Second's EXEC on line 7.");
    }

    [Fact]
    public void Extract_SQL_ProcedureBodyRange_NestedBlockCommentDoesNotCloseBody()
    {
        // Regression for codex review iteration 2 finding #2: MaskSqlLineForBodyScan must track
        // block-comment depth instead of a plain bool so PostgreSQL-style nested
        // `/* /* ... */ ... */` block comments do not exit on the inner `*/` and re-expose comment-
        // interior `GO` / `CREATE PROCEDURE` tokens that would prematurely close the body.
        // codex レビュー iteration 2 指摘 #2 の回帰テスト: MaskSqlLineForBodyScan は plain bool ではなく
        // ブロックコメント depth を追う必要があり、PostgreSQL 風のネスト `/* /* ... */ ... */` を
        // 内側の `*/` で誤って抜けてはならない。抜けるとコメント内部の `GO` / `CREATE PROCEDURE` が
        // 露出し、本体を早期に閉じてしまう。
        var content =
            "CREATE PROCEDURE dbo.sp_NestedComment AS\n" +    // line 1
            "BEGIN\n" +                                         // line 2
            "  /*\n" +                                          // line 3 — outer block opens
            "   /*\n" +                                         // line 4 — inner block opens
            "     GO\n" +                                       // line 5 — bare GO in nested comment
            "     CREATE PROCEDURE dbo.sp_FakeNested AS SELECT 0;\n" + // line 6 — fake header
            "   */\n" +                                         // line 7 — inner closes; outer still open
            "   GO\n" +                                         // line 8 — still inside outer comment
            "   CREATE PROCEDURE dbo.sp_FakeOuter AS SELECT 0;\n" + // line 9 — still inside outer
            "  */\n" +                                          // line 10 — outer closes
            "  EXEC dbo.sp_RealCall;\n" +                       // line 11 — real EXEC must be inside body
            "END\n" +                                           // line 12
            "GO\n" +                                            // line 13 — real terminator
            "CREATE PROCEDURE dbo.sp_AfterNested AS BEGIN SELECT 1; END\n" + // line 14
            "GO\n";                                             // line 15

        var symbols = SymbolExtractor.Extract(1, "sql", content);

        var nested = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "dbo.sp_NestedComment");
        Assert.NotNull(nested.BodyStartLine);
        Assert.NotNull(nested.BodyEndLine);
        Assert.True(nested.BodyEndLine!.Value >= 11,
            $"BodyEndLine={nested.BodyEndLine} must cover the real EXEC on line 11; nested block comment must not close the body via the inner `*/`.");
        Assert.True(nested.BodyEndLine!.Value < 14,
            $"BodyEndLine={nested.BodyEndLine} must not leak into sp_AfterNested starting on line 14.");
    }

    [Fact]
    public void Extract_SQL_DetectsOraclePlSqlDdlKinds()
    {
        // Oracle PL/SQL — PACKAGE / PACKAGE BODY / TYPE / TYPE BODY / DATABASE LINK / DIRECTORY /
        // CONTEXT / PROFILE must all be captured, and object names may contain `$` / `#`.
        // Oracle PL/SQL — PACKAGE / PACKAGE BODY / TYPE / TYPE BODY / DATABASE LINK / DIRECTORY /
        // CONTEXT / PROFILE を全て捕捉し、オブジェクト名に `$` / `#` を含められる。
        var content =
            "CREATE OR REPLACE PACKAGE orders_pkg IS\n" +
            "  PROCEDURE insert_order(p_id IN NUMBER);\n" +
            "END orders_pkg;\n" +
            "/\n" +
            "CREATE OR REPLACE PACKAGE BODY orders_pkg IS\n" +
            "  PROCEDURE insert_order(p_id IN NUMBER) IS BEGIN NULL; END;\n" +
            "END orders_pkg;\n" +
            "/\n" +
            "CREATE OR REPLACE TYPE address_t AS OBJECT (street VARCHAR2(100));\n" +
            "/\n" +
            "CREATE OR REPLACE TYPE BODY address_t AS\n" +
            "END;\n" +
            "/\n" +
            "CREATE SEQUENCE hr.order_seq START WITH 1 INCREMENT BY 1;\n" +
            "CREATE PUBLIC SYNONYM customer_v FOR schema1.customers;\n" +
            "CREATE PUBLIC DATABASE LINK remote_db CONNECT TO app IDENTIFIED BY \"x\" USING 'REMOTE';\n" +
            "CREATE SHARED PUBLIC DATABASE LINK remote_shared_pub_db CONNECT TO app IDENTIFIED BY \"x\" USING 'REMOTE';\n" +
            "CREATE SHARED DATABASE LINK remote_shared_db CONNECT TO app IDENTIFIED BY \"x\" USING 'REMOTE';\n" +
            "CREATE DIRECTORY data_dir AS '/var/oracle/data';\n" +
            "CREATE CONTEXT app_ctx USING app_pkg;\n" +
            "CREATE PROFILE app_profile LIMIT SESSIONS_PER_USER 5;\n" +
            "CREATE TABLE SYS$ITEMS#1 (id NUMBER);\n" +
            "ALTER PACKAGE orders_pkg COMPILE;\n" +
            "ALTER PACKAGE orders_pkg COMPILE BODY;\n" +
            "ALTER TYPE address_t COMPILE BODY;\n" +
            "ALTER DATABASE LINK remote_db;\n" +
            "ALTER DIRECTORY data_dir AS '/var/oracle/data2';\n" +
            "ALTER PROFILE app_profile LIMIT SESSIONS_PER_USER 10;\n";
        var symbols = SymbolExtractor.Extract(1, "sql", content);

        // PACKAGE spec/body both captured (BODY is not absorbed as the package name)
        // PACKAGE spec / body の両方が取れ、`BODY` が package name に吸い込まれない
        Assert.Equal(2, symbols.Count(s => s.Kind == "class" && s.Name == "orders_pkg" && s.Signature != null && s.Signature.StartsWith("CREATE", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "orders_pkg" && s.Signature != null && s.Signature.Contains("PACKAGE BODY", StringComparison.OrdinalIgnoreCase));

        // TYPE + TYPE BODY
        Assert.Equal(2, symbols.Count(s => s.Kind == "class" && s.Name == "address_t" && s.Signature != null && s.Signature.StartsWith("CREATE", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "address_t" && s.Signature != null && s.Signature.Contains("TYPE BODY", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "hr.order_seq");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "customer_v");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "remote_db" && s.Signature != null && s.Signature.Contains("DATABASE LINK", StringComparison.OrdinalIgnoreCase));
        // Oracle allows `CREATE [SHARED] [PUBLIC] DATABASE LINK` — both modifiers may appear together.
        // Oracle は `CREATE [SHARED] [PUBLIC] DATABASE LINK` で両修飾子の 2 語並びも取る。
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "remote_shared_pub_db" && s.Signature != null && s.Signature.Contains("SHARED PUBLIC DATABASE LINK", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "remote_shared_db" && s.Signature != null && s.Signature.Contains("SHARED DATABASE LINK", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "data_dir" && s.Signature != null && s.Signature.StartsWith("CREATE DIRECTORY", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "app_ctx" && s.Signature != null && s.Signature.StartsWith("CREATE CONTEXT", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "app_profile" && s.Signature != null && s.Signature.StartsWith("CREATE PROFILE", StringComparison.OrdinalIgnoreCase));

        // Oracle identifiers may contain `$` / `#`
        // Oracle 識別子は `$` / `#` を含められる
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "SYS$ITEMS#1");

        // `BODY` keyword is NOT treated as the object name — these assertions would fail if the
        // generic PACKAGE / TYPE rows absorbed the `BODY` token.
        // `BODY` キーワードは name として取られない — generic な PACKAGE / TYPE 行が `BODY` を
        // 飲み込んでしまうと以下の Assert が失敗する
        Assert.DoesNotContain(symbols, s => s.Name == "BODY");

        // `LINK` keyword must not be eaten by the generic CREATE DATABASE row.
        // `LINK` が generic な CREATE DATABASE 行に食われないこと
        Assert.DoesNotContain(symbols, s => s.Name == "LINK");

        // ALTER counterparts — Oracle body compilation uses `ALTER PACKAGE <name> COMPILE BODY` /
        // `ALTER TYPE <name> COMPILE BODY`, not a `BODY <name>` keyword position.
        // ALTER 側 — Oracle の body コンパイルは `ALTER PACKAGE <name> COMPILE BODY` /
        // `ALTER TYPE <name> COMPILE BODY` であり、`BODY <name>` という位置取りではない。
        Assert.Equal(2, symbols.Count(s => s.Kind == "class" && s.Name == "orders_pkg" && s.Signature != null && s.Signature.StartsWith("ALTER PACKAGE ", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "orders_pkg" && s.Signature != null && s.Signature.Contains("COMPILE BODY", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "address_t" && s.Signature != null && s.Signature.StartsWith("ALTER TYPE ", StringComparison.OrdinalIgnoreCase) && s.Signature.Contains("COMPILE BODY", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "remote_db" && s.Signature != null && s.Signature.StartsWith("ALTER DATABASE LINK", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "data_dir" && s.Signature != null && s.Signature.StartsWith("ALTER DIRECTORY", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "app_profile" && s.Signature != null && s.Signature.StartsWith("ALTER PROFILE", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Extract_SQL_KeepsQualifiedNamesWhenDotsHaveSurroundingWhitespace()
    {
        var content =
            "CREATE SCHEMA sales . reporting;\n" +
            "CREATE SCHEMA AUTHORIZATION sales . auth_owner;\n" +
            "CREATE SEQUENCE sales . seq_orders START WITH 1;\n" +
            "CREATE EXTENSION \"sales\" . \"ext_demo\";\n" +
            "CREATE SYNONYM [sales] . [syn_demo] FOR dbo.target;\n" +
            "CREATE DATABASE LINK sales . remote_db CONNECT TO app IDENTIFIED BY 'x' USING 'REMOTE';\n" +
            "CREATE LOGIN sales . app_login WITH PASSWORD = 'x';\n" +
            "CREATE PARTITION FUNCTION sales . pf_orders (int) AS RANGE LEFT FOR VALUES (1);\n" +
            "CREATE PARTITION SCHEME sales . ps_orders AS PARTITION sales . pf_orders ALL TO ([PRIMARY]);\n" +
            "CREATE FULLTEXT CATALOG sales . ft_catalog;\n" +
            "CREATE INDEX sales . idx_users_email ON dbo.Users (Email);\n" +
            "ALTER PARTITION FUNCTION sales . pf_orders() SPLIT RANGE (2);\n" +
            "ALTER SCHEMA sales . reporting TRANSFER dbo.Users;\n" +
            "ALTER EXTENSION \"sales\" . \"ext_demo\" UPDATE TO '2.0';\n" +
            "ALTER DATABASE LINK sales . remote_db;\n" +
            "ALTER SEQUENCE sales . seq_orders RESTART WITH 10;\n" +
            "ALTER SYNONYM [sales] . [syn_demo] FOR dbo.target;\n" +
            "ALTER LOGIN sales . app_login WITH DEFAULT_DATABASE = master;\n" +
            "ALTER INDEX sales . idx_users_email REBUILD;\n" +
            "ALTER PARTITION SCHEME sales . ps_orders NEXT USED [PRIMARY];\n" +
            "ALTER FULLTEXT CATALOG sales . ft_catalog REORGANIZE;\n";
        var symbols = SymbolExtractor.Extract(1, "sql", content);

        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "sales.reporting");
        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "sales.auth_owner");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "sales.seq_orders");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "\"sales\".\"ext_demo\"");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "[sales].[syn_demo]");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "sales.remote_db");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "sales.app_login");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "sales.pf_orders");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "sales.ps_orders");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "sales.ft_catalog");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "sales.idx_users_email");
    }
}
