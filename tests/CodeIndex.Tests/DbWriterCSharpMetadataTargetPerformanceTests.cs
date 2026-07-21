using CodeIndex.Database;
using CodeIndex.Models;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public sealed class DbWriterCSharpMetadataTargetPerformanceTests : IDisposable
{
    private readonly string _projectDir;
    private readonly DbContext _db;
    private readonly DbWriter _writer;

    public DbWriterCSharpMetadataTargetPerformanceTests()
    {
        _projectDir = TestProjectHelper.CreateTempProject("csharp_metadata_target_performance");
        _db = new DbContext(DbOpenIntent.WriteIndex, Path.Combine(_projectDir, "codeindex.db"));
        _db.InitializeSchema();
        _writer = new DbWriter(_db.Connection);
    }

    [Fact]
    public void ResolveCSharpMetadataTargets_LongReverseOrderedChain_PropagatesToLeaf()
    {
        const int chainLength = 8_000;
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/ReverseChain.cs",
            Lang = "csharp",
            Size = chainLength * 40,
            Lines = chainLength,
            Modified = new DateTime(2026, 7, 21, 0, 0, 0, DateTimeKind.Utc),
        });
        var symbols = new List<SymbolRecord>(chainLength);
        for (int i = chainLength - 1; i >= 0; i--)
        {
            symbols.Add(new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = $"Node{i}",
                Line = chainLength - i,
                StartLine = chainLength - i,
                EndLine = chainLength - i,
                Signature = i == 0
                    ? "public class Node0 : System.Attribute"
                    : $"public class Node{i} : Node{i - 1}",
                ContainerQualifiedName = $"Bench.Node{i}",
                IsMetadataTarget = i == 0,
                MetadataTargetSource = i == 0 ? SymbolRecord.MetadataTargetSourceExtractor : null,
            });
        }
        _writer.InsertSymbols(symbols);

        var stats = _writer.ResolveCSharpMetadataTargetsCore(CancellationToken.None);

        Assert.Equal(chainLength, stats.RowCount);
        Assert.Equal(chainLength - 1, stats.DependencyEdgeCount);
        Assert.Equal(chainLength, stats.QueueVisitCount);
        Assert.Equal(chainLength - 1, stats.RowsUpdated);
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT is_metadata_target FROM symbols WHERE name = 'Node7999'";
        Assert.Equal(1L, Convert.ToInt64(cmd.ExecuteScalar()));
    }

    [Fact]
    public void ResolveCSharpMetadataTargets_CrossFilePartialFanInAndUnseededCycle_AreDeterministic()
    {
        InsertClass("src/Root.cs", "Root", "public class Root : System.Attribute", "Graph.Root", extractorTarget: true);
        InsertClass("src/Shared.Part1.cs", "Shared", "public partial class Shared", "Graph.Shared");
        InsertClass("src/Shared.Part2.cs", "Shared", "public partial class Shared : Root", "Graph.Shared");
        InsertClass("src/Leaf.cs", "Leaf", "public class Leaf : Shared", "Graph.Leaf");
        InsertClass("src/CycleA.cs", "CycleA", "public class CycleA : CycleB", "Graph.CycleA");
        InsertClass("src/CycleB.cs", "CycleB", "public class CycleB : CycleA", "Graph.CycleB");

        var stats = _writer.ResolveCSharpMetadataTargetsCore(CancellationToken.None);

        Assert.Equal(6, stats.RowCount);
        Assert.Equal(5, stats.DependencyEdgeCount);
        Assert.Equal(3, stats.QueueVisitCount);
        Assert.Equal(2, stats.RowsUpdated);
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = @"
            SELECT f.path, s.is_metadata_target
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE s.kind = 'class'";
        using var reader = cmd.ExecuteReader();
        var flags = new Dictionary<string, long>(StringComparer.Ordinal);
        while (reader.Read())
            flags[reader.GetString(0)] = reader.GetInt64(1);
        Assert.Equal(1, flags["src/Root.cs"]);
        Assert.Equal(0, flags["src/Shared.Part1.cs"]);
        Assert.Equal(1, flags["src/Shared.Part2.cs"]);
        Assert.Equal(1, flags["src/Leaf.cs"]);
        Assert.Equal(0, flags["src/CycleA.cs"]);
        Assert.Equal(0, flags["src/CycleB.cs"]);
    }

    [Fact]
    public void ResolveCSharpMetadataTargets_StableRerunSkipsPhysicalWritesAndRepairsDrift()
    {
        InsertClass("src/Base.cs", "Base", "public class Base : System.Attribute", "Audit.Base", extractorTarget: true);
        InsertClass("src/Child.cs", "Child", "public class Child : Base", "Audit.Child");
        ExecuteSql(@"
            CREATE TABLE metadata_target_write_audit(symbol_id INTEGER NOT NULL);
            CREATE TRIGGER audit_metadata_target_write
            AFTER UPDATE OF is_metadata_target, metadata_target_source ON symbols
            BEGIN
                INSERT INTO metadata_target_write_audit(symbol_id) VALUES (NEW.id);
            END;");

        var first = _writer.ResolveCSharpMetadataTargetsCore(CancellationToken.None);
        Assert.Equal(1, first.RowsUpdated);
        Assert.Equal(1L, ExecuteScalarInt64("SELECT COUNT(*) FROM metadata_target_write_audit"));

        ExecuteSql("DELETE FROM metadata_target_write_audit");
        var stable = _writer.ResolveCSharpMetadataTargetsCore(CancellationToken.None);
        Assert.Equal(0, stable.RowsUpdated);
        Assert.Equal(0L, ExecuteScalarInt64("SELECT COUNT(*) FROM metadata_target_write_audit"));

        ExecuteSql(@"
            UPDATE symbols
            SET is_metadata_target = 0, metadata_target_source = NULL
            WHERE name = 'Child';
            DELETE FROM metadata_target_write_audit;");
        var repaired = _writer.ResolveCSharpMetadataTargetsCore(CancellationToken.None);
        Assert.Equal(1, repaired.RowsUpdated);
        Assert.Equal(1L, ExecuteScalarInt64("SELECT COUNT(*) FROM metadata_target_write_audit"));
        Assert.Equal(1L, ExecuteScalarInt64("SELECT is_metadata_target FROM symbols WHERE name = 'Child'"));
    }

    [Fact]
    public void ResolveCSharpMetadataTargets_PreCanceledTokenLeavesRowsUntouched()
    {
        InsertClass("src/Base.cs", "Base", "public class Base : System.Attribute", "Cancel.Base", extractorTarget: true);
        InsertClass("src/Child.cs", "Child", "public class Child : Base", "Cancel.Child");
        ExecuteSql(@"
            CREATE TABLE metadata_target_write_audit(symbol_id INTEGER NOT NULL);
            CREATE TRIGGER audit_metadata_target_write
            AFTER UPDATE OF is_metadata_target, metadata_target_source ON symbols
            BEGIN
                INSERT INTO metadata_target_write_audit(symbol_id) VALUES (NEW.id);
            END;");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => _writer.ResolveCSharpMetadataTargets(cts.Token));

        Assert.Equal(0L, ExecuteScalarInt64("SELECT COUNT(*) FROM metadata_target_write_audit"));
        Assert.Equal(0L, ExecuteScalarInt64("SELECT is_metadata_target FROM symbols WHERE name = 'Child'"));
    }

    [Fact]
    public void ResolveCSharpMetadataTargets_UpdateFailureRollsBackEarlierRows()
    {
        InsertClass("src/Base.cs", "Base", "public class Base : System.Attribute", "Rollback.Base", extractorTarget: true);
        InsertClass("src/Child1.cs", "Child1", "public class Child1 : Base", "Rollback.Child1");
        InsertClass("src/Child2.cs", "Child2", "public class Child2 : Child1", "Rollback.Child2");
        ExecuteSql(@"
            CREATE TRIGGER fail_second_metadata_target_write
            BEFORE UPDATE OF is_metadata_target, metadata_target_source ON symbols
            WHEN NEW.name = 'Child2'
            BEGIN
                SELECT RAISE(ABORT, 'injected metadata-target failure');
            END;");

        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(
            () => _writer.ResolveCSharpMetadataTargets());

        Assert.Equal(0L, ExecuteScalarInt64("SELECT is_metadata_target FROM symbols WHERE name = 'Child1'"));
        Assert.Equal(0L, ExecuteScalarInt64("SELECT is_metadata_target FROM symbols WHERE name = 'Child2'"));
    }

    private void InsertClass(
        string path,
        string name,
        string signature,
        string qualifiedName,
        bool extractorTarget = false)
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = path,
            Lang = "csharp",
            Size = signature.Length,
            Lines = 1,
            Modified = new DateTime(2026, 7, 21, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertSymbols([
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = name,
                Line = 1,
                StartLine = 1,
                EndLine = 1,
                Signature = signature,
                ContainerQualifiedName = qualifiedName,
                IsMetadataTarget = extractorTarget,
                MetadataTargetSource = extractorTarget ? SymbolRecord.MetadataTargetSourceExtractor : null,
            },
        ]);
    }

    private void ExecuteSql(string sql)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private long ExecuteScalarInt64(string sql)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public void Dispose()
    {
        _db.Dispose();
        TestProjectHelper.DeleteDirectory(_projectDir);
    }
}
