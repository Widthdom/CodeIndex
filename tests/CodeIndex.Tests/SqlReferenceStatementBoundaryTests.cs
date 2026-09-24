using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public sealed class SqlReferenceStatementBoundaryTests
{
    [Fact]
    public void SeedStatements_KeepCarryBoundedAndPreserveReferences()
    {
        const int count = 128;
        foreach (var separator in new[] { "\n", "\nGO\n", "\n go 2 -- batch\n", ";\n" })
        {
            var content = "SET NOCOUNT ON\n" + string.Concat(Enumerable.Range(0, count).Select(index =>
                ((index % 3) switch
                {
                    0 => "UPDATE dbo.Translation SET Value = N'updated' WHERE Id = 1",
                    1 => "INSERT INTO dbo.Translation (Id, Value) VALUES (1, N'inserted')",
                    _ => "DELETE FROM dbo.Translation WHERE Id = 1"
                }) + separator));
            var state = SqlReferenceExtractor.CreateState();
            var references = new List<ReferenceRecord>();
            var seen = new ReferenceDedupeSet();
            var lines = content.Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                SqlReferenceExtractor.Emit(lines[index], lines[index], index + 1, references, seen, 1, state,
                    _ => null, _ => false, (_, _) => false);
                Assert.True(state.StatementPrefix.Length < 256, $"Unbounded statement carry at line {index + 1}");
            }

            var actual = ReferenceExtractor.Extract(1, "sql", content, SymbolExtractor.Extract(1, "sql", content));
            var targets = actual.Where(reference => reference.SymbolName == "Translation").ToArray();
            Assert.Equal(count, targets.Length);
            Assert.All(targets, reference =>
            {
                Assert.Equal("reference", reference.ReferenceKind);
                Assert.Equal("Translation", lines[reference.Line - 1].Substring(reference.Column - 1, "Translation".Length));
            });
        }
    }

    [Fact]
    public void MultilineStatements_PreserveContinuationAndTempObjectsAcrossBatches()
    {
        foreach (var (prefix, continuation) in new[]
        {
            ("INSERT INTO dbo.Target SELECT (", "UPDATE dbo.Source SET Id = 2"),
            ("INSERT INTO dbo.Target (Id) VALUES (1) ON CONFLICT (Id) DO", "UPDATE SET Id = excluded.Id"),
            ("INSERT INTO dbo.Target (Id) VALUES (1) ON DUPLICATE KEY", "UPDATE Id = 2"),
            ("WITH changed AS (SELECT Id FROM dbo.Source)", "UPDATE dbo.Target SET Id = 2"),
            ("MERGE dbo.Target USING dbo.Source ON 1=1 WHEN MATCHED THEN", "UPDATE SET Id = 2"),
            ("SELECT * FROM", "[GO]"),
            ("SELECT * FROM", "GO_Source"),
            ("SELECT * FROM", "GO.Id"),
        })
        {
            var state = SqlReferenceExtractor.CreateState();
            state.StatementPrefix = prefix;
            SqlReferenceExtractor.Emit(continuation, continuation, 2, [], new ReferenceDedupeSet(), 1, state,
                _ => null, _ => false, (_, _) => false);
            Assert.StartsWith(prefix + "\n", state.StatementPrefix, StringComparison.Ordinal);
        }

        const string content = """
            CREATE TABLE #Scratch (Id int)
            GO
            INSERT INTO #Scratch (Id)
            SELECT Id FROM dbo.Source
            GO
            DELETE FROM dbo.Target
            WHERE Id IN (
                SELECT Id FROM #Scratch
            )
            GO
            INSERT INTO dbo.Target (Id) VALUES (1)
            ON CONFLICT (Id) DO
            UPDATE SET Id = excluded.Id
            RETURNING Id;
            MERGE dbo.Target AS t
            USING dbo.Source AS s ON t.Id = s.Id
            WHEN MATCHED THEN
            UPDATE SET t.Id = s.Id
            WHEN NOT MATCHED THEN
            INSERT (Id) VALUES (s.Id);
            SELECT 'first line
            GO
            last line', $$first line
            GO
            last line$$ FROM dbo.LiteralSource;
            """;
        var references = ReferenceExtractor.Extract(1, "sql", content, SymbolExtractor.Extract(1, "sql", content));
        Assert.Contains(references, reference => reference.SymbolName == "Source" && reference.Line == 4);
        Assert.Contains(references, reference => reference.SymbolName == "#Scratch" && reference.Line == 8);
        Assert.Contains(references, reference => reference.SymbolName == "Source" && reference.Line == 16);
        Assert.Contains(references, reference => reference.SymbolName == "Id" && reference.ReferenceKind == "join_condition_reference");
        Assert.Contains(references, reference => reference.SymbolName == "Id" && reference.Line == 18 && reference.ReferenceKind == "column_reference");
        Assert.Contains(references, reference => reference.SymbolName == "Id" && reference.Line == 20 && reference.ReferenceKind == "column_reference");
        Assert.Contains(references, reference => reference.SymbolName == "LiteralSource" && reference.Line == 25);
        Assert.DoesNotContain(references, reference => reference.SymbolName == "GO");
    }
}
