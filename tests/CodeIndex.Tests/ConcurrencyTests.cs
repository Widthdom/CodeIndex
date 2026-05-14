using System.Collections.Concurrent;
using CodeIndex.Database;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

/// <summary>
/// Tests for concurrent database access patterns.
/// 並行データベースアクセスパターンのテスト。
/// </summary>
[Collection("SQLite pool sensitive")]
public class ConcurrencyTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DbContext _db;

    public ConcurrencyTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"codeindex_concurrency_{Guid.NewGuid():N}.db");
        _db = new DbContext(_dbPath);
        _db.InitializeSchema();
    }

    [Fact]
    public async Task ConcurrentReads_DoNotBlock()
    {
        // Seed data / テストデータ投入
        var writer = new DbWriter(_db.Connection);
        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = "src/app.cs", Lang = "csharp", Size = 100, Lines = 10,
            Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Checksum = "abc",
        });
        writer.InsertChunks([new ChunkRecord { FileId = fileId, ChunkIndex = 0, StartLine = 1, EndLine = 10, Content = "public class App { }" }]);

        // Open multiple concurrent readers — WAL mode should allow this
        // 複数の同時読み取りを開く — WALモードなら可能
        var tasks = Enumerable.Range(0, 5).Select(_ => Task.Run(() =>
        {
            using var readDb = new DbContext(_dbPath);
            readDb.TryMigrateForRead();
            var reader = new DbReader(readDb.Connection);
            var status = reader.GetStatus();
            Assert.True(status.Files > 0);
            return status.Files;
        })).ToArray();

        var results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.True(r > 0));
    }

    [Fact]
    public async Task ConcurrentReadDuringWrite_Succeeds()
    {
        // Writer inserts while readers query — WAL allows this
        // 書き込み中に読み取り — WALモードなら可能
        var writer = new DbWriter(_db.Connection);

        // Pre-seed a file so reads have something to find
        writer.UpsertFile(new FileRecord
        {
            Path = "src/seed.cs", Lang = "csharp", Size = 50, Lines = 5,
            Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Checksum = "seed",
        });

        var writeTask = Task.Run(() =>
        {
            for (int i = 0; i < 20; i++)
            {
                using var writeDb = new DbContext(_dbPath);
                writeDb.TryMigrateForRead();
                var w = new DbWriter(writeDb.Connection);
                w.UpsertFile(new FileRecord
                {
                    Path = $"src/file{i}.cs", Lang = "csharp", Size = 100, Lines = 10,
                    Modified = DateTime.UtcNow,
                    Checksum = $"hash{i}",
                });
            }
        });

        var readTask = Task.Run(() =>
        {
            for (int i = 0; i < 20; i++)
            {
                using var readDb = new DbContext(_dbPath);
                readDb.TryMigrateForRead();
                var reader = new DbReader(readDb.Connection);
                // Should not throw even during concurrent writes
                // 同時書き込み中でも例外を投げないこと
                var status = reader.GetStatus();
                Assert.True(status.Files >= 1); // at least the seed file
            }
        });

        await Task.WhenAll(writeTask, readTask);
    }

    [Fact]
    public async Task GetStatus_ReferencesAndFilesStaySnapshotConsistent_UnderConcurrentWriter()
    {
        // Issue #180 regression: GetStatus must expose a single consistent WAL snapshot
        // across its many COUNT(*) / freshness / readiness queries. The test seeds a known
        // ratio (refs == files * refsPerFile) that every committed writer step preserves,
        // spawns a background writer that keeps committing new files with exactly that
        // many refs, and spins a reader that repeatedly calls GetStatus. Without the
        // snapshot-isolation wrap in DbReader.GetStatus, a commit landing between the
        // `files` COUNT(*) and the `references` COUNT(*) breaks the invariant; with the
        // wrap, every observation must match.
        // Issue #180 の回帰テスト: GetStatus の複数 SELECT が 1 つの WAL snapshot に揃うこと
        // を検証する。seed で `refs == files * refsPerFile` という不変条件を作り、writer が
        // 同じ比率で file+refs を commit し続ける間に reader が GetStatus を回す。snapshot
        // 隔離が無いと files と refs の COUNT(*) の間に writer が commit した結果として
        // 比率が破れる。
        const int seedFileCount = 5;
        const int refsPerFile = 20;

        var writer = new DbWriter(_db.Connection);
        for (var seedIndex = 0; seedIndex < seedFileCount; seedIndex++)
        {
            var fileId = writer.UpsertFile(new FileRecord
            {
                Path = $"src/seed{seedIndex}.cs", Lang = "csharp", Size = 100, Lines = 10,
                Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Checksum = $"seed{seedIndex}",
            });
            writer.InsertReferences(BuildReferenceBatch(fileId, $"seed{seedIndex}", refsPerFile));
        }
        // DbReader gates `_hasReferencesTable` on the GraphReadyFlag bit in user_version, so
        // without this stamp every call to GetStatus returns `references: 0` regardless of
        // table contents and the snapshot-isolation assertion becomes vacuous.
        // DbReader は `_hasReferencesTable` を user_version の GraphReadyFlag で決めるため、
        // MarkGraphReady() を呼ばないと GetStatus は常に refs=0 を返し、検証が成立しない。
        writer.MarkGraphReady();

        using var cts = new CancellationTokenSource();
        var violations = new ConcurrentBag<(long files, long references)>();
        long readerIterations = 0;
        long writerIterations = 0;

        var writeTask = Task.Run(() =>
        {
            using var writeDb = new DbContext(_dbPath);
            writeDb.TryMigrateForRead();
            var w = new DbWriter(writeDb.Connection);
            var extra = 0;
            while (!cts.IsCancellationRequested)
            {
                using var txn = w.BeginTransaction();
                var fileId = w.UpsertFile(new FileRecord
                {
                    Path = $"src/added{extra}.cs", Lang = "csharp", Size = 100, Lines = 10,
                    Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    Checksum = $"added{extra}",
                });
                w.InsertReferences(BuildReferenceBatch(fileId, $"added{extra}", refsPerFile));
                txn.Commit();
                Interlocked.Increment(ref writerIterations);
                extra++;
            }
        });

        var readTask = Task.Run(() =>
        {
            using var readDb = new DbContext(_dbPath);
            readDb.TryMigrateForRead();
            var reader = new DbReader(readDb.Connection);
            while (!cts.IsCancellationRequested)
            {
                var status = reader.GetStatus();
                if (status.References != status.Files * refsPerFile)
                    violations.Add((status.Files, status.References));
                Interlocked.Increment(ref readerIterations);
            }
        });

        // Run long enough for the two threads to interleave commits and reads many times.
        // 十分な時間動かし、コミットと読み出しの交錯機会を多数確保する。
        await Task.Delay(TimeSpan.FromSeconds(2));
        cts.Cancel();
        await Task.WhenAll(writeTask, readTask);

        Assert.True(
            violations.IsEmpty,
            $"GetStatus returned inconsistent files/refs snapshots {violations.Count} times out of " +
            $"{readerIterations} reads while the writer committed {writerIterations} times. " +
            $"Sample: {string.Join(", ", violations.Take(3).Select(v => $"files={v.files} refs={v.references}"))}");
    }

    [Fact]
    public async Task AnalyzeSymbol_RefsCallersAndFreshnessStaySnapshotConsistent_UnderConcurrentWriter()
    {
        // Issue #180 regression for the `inspect` / MCP `analyze_symbol` bundle: the
        // multi-statement read path must resolve every sub-query (definitions, file,
        // freshness, references, callers, callees, nearby symbols) against the same WAL
        // snapshot. We pin two invariants that exercise three different sub-queries:
        //
        //   1. `references.Count == callers.Count` — each toggled-in file contributes
        //      exactly one reference row and one caller row for symbol `S`, so this catches
        //      a writer commit landing between `SearchReferences` and `GetCallers`.
        //   2. `WorkspaceLatestModified` matches the `references.Count` state (1 ref => only
        //      file A is present so freshness = T0; 2 refs => file B is present too, with
        //      modified = T1 > T0, so freshness = T1). This catches a writer commit landing
        //      between `SearchReferences` / `GetCallers` and `GetWorkspaceFreshness`.
        //
        // Issue #180 回帰テスト（inspect / MCP analyze_symbol 用）: AnalyzeSymbol bundle が
        // 同じ WAL snapshot から sub-query を返すことを 2 つの不変条件で検証する。
        //   1. `references.Count == callers.Count` — 各 file が S に対して reference 1 件
        //      と caller 1 件を対称に寄与するため、`SearchReferences` と `GetCallers` の
        //      間で writer が commit すると等式が壊れる。
        //   2. `WorkspaceLatestModified` が `references.Count` と整合する — 1 ref なら
        //      file A のみで freshness = T0、2 refs なら file B も存在し modified = T1 > T0
        //      なので freshness = T1。`SearchReferences` / `GetCallers` と
        //      `GetWorkspaceFreshness` の間で writer が commit すると食い違う。
        var fileAModified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var fileBModified = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var writer = new DbWriter(_db.Connection);
        var fileAId = writer.UpsertFile(new FileRecord
        {
            Path = "src/A.cs", Lang = "csharp", Size = 100, Lines = 20,
            Modified = fileAModified,
            Checksum = "A",
        });
        writer.InsertSymbols([
            new SymbolRecord
            {
                FileId = fileAId, Kind = "function", Name = "S",
                Line = 1, StartLine = 1, EndLine = 1,
                ContainerKind = "class", ContainerName = "TA",
                Signature = "public void S()", Visibility = "public", ReturnType = "void",
            },
            new SymbolRecord
            {
                FileId = fileAId, Kind = "function", Name = "foo",
                Line = 10, StartLine = 10, EndLine = 12,
                ContainerKind = "class", ContainerName = "TA",
                Signature = "public void foo()", Visibility = "public", ReturnType = "void",
            },
        ]);
        writer.InsertReferences([
            new ReferenceRecord
            {
                FileId = fileAId, SymbolName = "S", ReferenceKind = "call",
                Line = 11, Column = 9,
                ContainerKind = "function", ContainerName = "foo",
                Context = "S();",
            },
        ]);
        writer.MarkGraphReady();
        writer.MarkCSharpSymbolNameContractReady();

        using var cts = new CancellationTokenSource();
        var violations = new ConcurrentBag<(string kind, int references, int callers, DateTime? workspaceLatestModified)>();
        long readerIterations = 0;
        long writerIterations = 0;

        var writeTask = Task.Run(() =>
        {
            using var writeDb = new DbContext(_dbPath);
            writeDb.TryMigrateForRead();
            var w = new DbWriter(writeDb.Connection);
            var toggle = 0;
            while (!cts.IsCancellationRequested)
            {
                using var txn = w.BeginTransaction();
                if ((toggle & 1) == 0)
                {
                    var fileBId = w.UpsertFile(new FileRecord
                    {
                        Path = "src/B.cs", Lang = "csharp", Size = 80, Lines = 10,
                        Modified = fileBModified,
                        Checksum = "B",
                    });
                    w.InsertSymbols([new SymbolRecord
                    {
                        FileId = fileBId, Kind = "function", Name = "bar",
                        Line = 5, StartLine = 5, EndLine = 7,
                        ContainerKind = "class", ContainerName = "TB",
                        Signature = "public void bar()", Visibility = "public", ReturnType = "void",
                    }]);
                    w.InsertReferences([new ReferenceRecord
                    {
                        FileId = fileBId, SymbolName = "S", ReferenceKind = "call",
                        Line = 6, Column = 9,
                        ContainerKind = "function", ContainerName = "bar",
                        Context = "S();",
                    }]);
                }
                else
                {
                    w.DeleteFileByPath("src/B.cs");
                }
                txn.Commit();
                Interlocked.Increment(ref writerIterations);
                toggle++;
            }
        });

        var readTask = Task.Run(() =>
        {
            using var readDb = new DbContext(_dbPath);
            readDb.TryMigrateForRead();
            var reader = new DbReader(readDb.Connection);
            while (!cts.IsCancellationRequested)
            {
                var result = reader.AnalyzeSymbol("S", limit: 10, lang: "csharp", exact: true);
                if (result.References.Count != result.Callers.Count)
                    violations.Add(("refs!=callers", result.References.Count, result.Callers.Count, result.WorkspaceLatestModified));
                // Each consistent snapshot has refs.Count ∈ {1, 2}; the matching freshness
                // is fileAModified for a file-A-only snapshot and fileBModified when file B
                // is also present. Anything else (refs == 1 with freshness == T1, or refs
                // == 2 with freshness == T0, or refs not in {1,2}) is a torn-read failure.
                // 1 ref => freshness = T0, 2 refs => freshness = T1 という対応関係を破ると
                // freshness が SearchReferences と別 snapshot から返ってきていることになる。
                var expectedFreshness = result.References.Count == 1 ? fileAModified
                    : result.References.Count == 2 ? fileBModified
                    : (DateTime?)null;
                if (expectedFreshness == null || result.WorkspaceLatestModified != expectedFreshness)
                    violations.Add(("freshness!=refsState", result.References.Count, result.Callers.Count, result.WorkspaceLatestModified));
                Interlocked.Increment(ref readerIterations);
            }
        });

        await Task.Delay(TimeSpan.FromSeconds(2));
        cts.Cancel();
        await Task.WhenAll(writeTask, readTask);

        Assert.True(
            violations.IsEmpty,
            $"AnalyzeSymbol returned torn snapshots {violations.Count} times out of " +
            $"{readerIterations} reads while the writer committed {writerIterations} times. " +
            $"Sample: {string.Join(", ", violations.Take(3).Select(v => $"{v.kind}: refs={v.references} callers={v.callers} freshness={v.workspaceLatestModified:o}"))}");
    }

    [Fact]
    public async Task GetRepoMap_FreshnessAndEntrypointsStaySnapshotConsistent_UnderConcurrentWriter()
    {
        // Issue #180 regression for `map` / MCP `repo_map`: `RepoMapBuilder.Build` runs
        // `GetFileStats`, then `getFreshness`, then `GetEntrypoints` — each a separate
        // SQL statement. We pin two invariants that exercise all three sub-queries:
        //
        //   1. `latest_modified == workspace_latest_modified` for an unfiltered map call —
        //      both aggregate across the whole workspace, so a torn read between
        //      `GetFileStats` and `getFreshness` makes them disagree.
        //   2. The presence/absence of the toggled `src/Program.cs` entrypoint must match
        //      the freshness state. When `WorkspaceLatestModified == newerModified` the
        //      writer's `Program.cs` (containing `Main()`) is committed, so `Entrypoints`
        //      must include it; when `WorkspaceLatestModified == baselineModified` it is
        //      gone, so `Entrypoints` must NOT include it. A torn read between
        //      `getFreshness` and `GetEntrypoints` breaks that correlation.
        //
        // Issue #180 回帰テスト（map / MCP repo_map 用）: `RepoMapBuilder.Build` は
        // `GetFileStats` → `getFreshness` → `GetEntrypoints` と独立 SQL を順に発行する。
        // 2 つの不変条件で 3 つの sub-query を全部踏む:
        //   1. filter 無しの map では `latest_modified == workspace_latest_modified`。
        //      `GetFileStats` と `getFreshness` の torn read で破れる。
        //   2. `src/Program.cs`（`Main()` 関数を持つ）の存在は freshness と整合する。
        //      `getFreshness` と `GetEntrypoints` の torn read で破れる。
        var writer = new DbWriter(_db.Connection);
        var baselineModified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var seedIndex = 0; seedIndex < 3; seedIndex++)
        {
            writer.UpsertFile(new FileRecord
            {
                Path = $"src/seed{seedIndex}.cs", Lang = "csharp", Size = 100, Lines = 10,
                Modified = baselineModified,
                Checksum = $"seed{seedIndex}",
            });
        }
        // Mark graph ready so the map's reference-count subquery and entrypoint scoring run
        // on the authoritative path the production reader uses.
        // map の reference count subquery / entrypoint scoring が production と同じ
        // authoritative 経路を通るよう、graph readiness を立てておく。
        writer.MarkGraphReady();

        using var cts = new CancellationTokenSource();
        var violations = new ConcurrentBag<(string kind, DateTime? scoped, DateTime? workspace, bool hasEntrypoint)>();
        long readerIterations = 0;
        long writerIterations = 0;
        var newerModified = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        const string toggledPath = "src/Program.cs";

        var writeTask = Task.Run(() =>
        {
            using var writeDb = new DbContext(_dbPath);
            writeDb.TryMigrateForRead();
            var w = new DbWriter(writeDb.Connection);
            var toggle = 0;
            while (!cts.IsCancellationRequested)
            {
                using var txn = w.BeginTransaction();
                if ((toggle & 1) == 0)
                {
                    var fileId = w.UpsertFile(new FileRecord
                    {
                        Path = toggledPath, Lang = "csharp", Size = 120, Lines = 12,
                        Modified = newerModified,
                        Checksum = "newer",
                    });
                    w.InsertSymbols([new SymbolRecord
                    {
                        FileId = fileId, Kind = "function", Name = "Main",
                        Line = 1, StartLine = 1, EndLine = 3,
                        ContainerKind = "class", ContainerName = "Program",
                        Signature = "public static void Main()", Visibility = "public", ReturnType = "void",
                    }]);
                }
                else
                {
                    w.DeleteFileByPath(toggledPath);
                }
                txn.Commit();
                Interlocked.Increment(ref writerIterations);
                toggle++;
            }
        });

        var readTask = Task.Run(() =>
        {
            using var readDb = new DbContext(_dbPath);
            readDb.TryMigrateForRead();
            var reader = new DbReader(readDb.Connection);
            while (!cts.IsCancellationRequested)
            {
                var map = reader.GetRepoMap();
                var hasEntrypoint = map.Entrypoints.Any(entrypoint => entrypoint.Path == toggledPath);
                if (map.LatestModified != map.WorkspaceLatestModified)
                    violations.Add(("scoped!=workspace", map.LatestModified, map.WorkspaceLatestModified, hasEntrypoint));
                // Freshness and entrypoint set must come from the same snapshot. When the
                // writer has committed Program.cs, freshness is newerModified and the
                // entrypoint list must contain it; when Program.cs is absent freshness is
                // baselineModified and the list must not contain it.
                // freshness と entrypoint 集合は同じ snapshot から来る必要がある。
                if (map.WorkspaceLatestModified == newerModified)
                {
                    if (!hasEntrypoint)
                        violations.Add(("entrypointMissing", map.LatestModified, map.WorkspaceLatestModified, hasEntrypoint));
                }
                else if (map.WorkspaceLatestModified == baselineModified)
                {
                    if (hasEntrypoint)
                        violations.Add(("entrypointStale", map.LatestModified, map.WorkspaceLatestModified, hasEntrypoint));
                }
                else
                {
                    violations.Add(("freshnessUnexpected", map.LatestModified, map.WorkspaceLatestModified, hasEntrypoint));
                }
                Interlocked.Increment(ref readerIterations);
            }
        });

        await Task.Delay(TimeSpan.FromSeconds(2));
        cts.Cancel();
        await Task.WhenAll(writeTask, readTask);

        Assert.True(
            violations.IsEmpty,
            $"GetRepoMap returned torn snapshots {violations.Count} times out of " +
            $"{readerIterations} reads while the writer committed {writerIterations} times. " +
            $"Sample: {string.Join(", ", violations.Take(3).Select(v => $"{v.kind}: scoped={v.scoped:o} workspace={v.workspace:o} hasEntrypoint={v.hasEntrypoint}"))}");
    }

    [Fact]
    public async Task SetReadyBit_ConcurrentWriters_DoNotLoseFlags()
    {
        // Issue #1513 regression: SetReadyBit's PRAGMA user_version read-modify-write
        // must serialise across writers. Without an immediate write lock, two parallel
        // cdidx processes can each read the same prior value, OR in their own flag,
        // and the slower writer's PRAGMA write clobbers the faster writer's flag.
        // The fix wraps the read+write in BEGIN IMMEDIATE; this test races MarkGraphReady
        // against MarkIssuesReady from two separate connections across many iterations
        // so a regression to non-atomic behaviour drops at least one flag with high probability.
        // Issue #1513 回帰テスト: SetReadyBit の PRAGMA user_version read-modify-write が
        // 書き込みプロセス間で直列化されることを検証する。BEGIN IMMEDIATE が無いと
        // 2 つの並列 writer が同じ prior 値を読み、それぞれの flag を OR して書き戻す際に
        // 後勝ちで一方の flag が消える。多くの反復で MarkGraphReady と MarkIssuesReady を
        // 競合させ、回帰時にどちらかの flag が落ちる確率を高める。
        const int iterations = 50;
        var lostFlagIterations = new List<(int iteration, int finalUserVersion)>();
        for (int i = 0; i < iterations; i++)
        {
            using (var resetCmd = _db.Connection.CreateCommand())
            {
                resetCmd.CommandText = "PRAGMA user_version = 0";
                resetCmd.ExecuteNonQuery();
            }

            using var start = new ManualResetEventSlim(false);
            var graphTask = Task.Run(() =>
            {
                using var graphDb = new DbContext(_dbPath);
                var w = new DbWriter(graphDb.Connection);
                start.Wait();
                w.MarkGraphReady();
            });
            var issuesTask = Task.Run(() =>
            {
                using var issuesDb = new DbContext(_dbPath);
                var w = new DbWriter(issuesDb.Connection);
                start.Wait();
                w.MarkIssuesReady();
            });

            start.Set();
            await Task.WhenAll(graphTask, issuesTask);

            var finalVersion = _db.GetUserVersion();
            bool graphSet = (finalVersion & DbContext.GraphReadyFlag) != 0;
            bool issuesSet = (finalVersion & DbContext.IssuesReadyFlag) != 0;
            if (!graphSet || !issuesSet)
                lostFlagIterations.Add((i, finalVersion));
        }

        Assert.True(
            lostFlagIterations.Count == 0,
            $"SetReadyBit lost a flag in {lostFlagIterations.Count}/{iterations} iterations. " +
            $"Sample: {string.Join(", ", lostFlagIterations.Take(3).Select(v => $"i={v.iteration} user_version={v.finalUserVersion}"))}");
    }

    private static List<ReferenceRecord> BuildReferenceBatch(long fileId, string label, int count)
    {
        var refs = new List<ReferenceRecord>(count);
        for (var refIndex = 0; refIndex < count; refIndex++)
        {
            refs.Add(new ReferenceRecord
            {
                FileId = fileId,
                SymbolName = $"{label}_sym{refIndex}",
                ReferenceKind = "call",
                Line = refIndex + 1,
                Column = 1,
                Context = $"// ref {refIndex} in {label}",
            });
        }
        return refs;
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
    }
}
