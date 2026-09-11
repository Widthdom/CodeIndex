using System.Collections.Concurrent;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ProjectRootResolutionUsesCapturedMetadataAndSamples_Issue5339(bool hasMetadata)
    {
        using var container = TestProjectHelper.CreateTempProjectScope("cdidx_batch_root_5339");
        using var indexedProject = TestProjectHelper.CreateTempProjectScope("cdidx_batch_indexed_5339");
        var dbPath = TestProjectHelper.CreateProjectDb(container.Root);
        var originalFile = Path.Combine(indexedProject.Root, "Example.cs");
        var changedFile = Path.Combine(container.Root, "Example.cs");
        File.WriteAllText(originalFile, "class Original {}\n");
        File.WriteAllText(changedFile, "class Changed {}\n");
        Assert.True(FileIndexer.TryComputeChecksum(originalFile, FileIndexer.DefaultMaxFileSizeBytes, out var originalChecksum));
        Assert.True(FileIndexer.TryComputeChecksum(changedFile, FileIndexer.DefaultMaxFileSizeBytes, out var changedChecksum));
        using var writer = new SqliteConnection($"Data Source={dbPath};Mode=ReadWrite;Pooling=False");
        writer.Open();
        using (var command = writer.CreateCommand())
        {
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA wal_autocheckpoint=0;
                INSERT INTO files(path, lang, size, lines, checksum, modified)
                VALUES ('Example.cs', 'csharp', 1, 1, @checksum, CURRENT_TIMESTAMP);
                DELETE FROM codeindex_meta WHERE key = @rootKey;
                """;
            command.Parameters.AddWithValue("@checksum", originalChecksum);
            command.Parameters.AddWithValue("@rootKey", DbContext.IndexedProjectRootMetaKey);
            command.ExecuteNonQuery();
        }
        var metadataWriter = new DbWriter(writer);
        if (hasMetadata)
            metadataWriter.SetMeta(DbContext.IndexedProjectRootMetaKey, indexedProject.Root);
        metadataWriter.SetMeta(DbContext.WorkspacePathCaseSensitiveMetaKey, "false");
        using var capturedDb = new DbContext(DbOpenIntent.QueryOnly, dbPath);
        using var capturedReader = new DbReader(capturedDb);

        metadataWriter.SetMeta(DbContext.IndexedProjectRootMetaKey, container.Root);
        metadataWriter.SetMeta(DbContext.WorkspacePathCaseSensitiveMetaKey, "true");
        using (var command = writer.CreateCommand())
        {
            command.CommandText = "UPDATE files SET checksum = @checksum";
            command.Parameters.AddWithValue("@checksum", changedChecksum);
            command.ExecuteNonQuery();
        }
        // The writer remains open: ReadAllBytes uses FileShare.Read and conflicts
        // with SQLite's write handle on Windows. Reuse the shared artifact reader.
        var sourceArtifacts = CaptureDatabaseArtifacts(dbPath);
        Assert.Equal(3, sourceArtifacts.Count);
        var originalDirectoryHook = DbConnectionFactory.QueryOnlySnapshotDirectoryCreatedForTesting;
        DbConnectionFactory.QueryOnlySnapshotDirectoryCreatedForTesting =
            _ => Assert.Fail("Project-root metadata must reuse the captured query snapshot.");
        try
        {
            foreach (var path in new[] { dbPath, Path.GetRelativePath(Environment.CurrentDirectory, dbPath), new Uri(dbPath).AbsoluteUri + "?mode=ro" })
            {
                Assert.Equal(hasMetadata ? indexedProject.Root : null,
                    DbPathResolver.ResolveProjectRootForQuery(path, true, capturedReader));
                Assert.Equal(container.Root,
                    DbPathResolver.ResolveProjectRootForQuery(path, false, capturedReader));
            }
            Assert.True(PathCasing.IsIgnoreCase(dbPath));
            if (hasMetadata)
                Assert.True(PathCasing.IsIgnoreCase(indexedProject.Root));
            Assert.Equal(sourceArtifacts, CaptureDatabaseArtifacts(dbPath));
        }
        finally
        {
            DbConnectionFactory.QueryOnlySnapshotDirectoryCreatedForTesting = originalDirectoryHook;
        }

        using var refreshedDb = new DbContext(DbOpenIntent.QueryOnly, dbPath);
        using var refreshedReader = new DbReader(refreshedDb);
        Assert.Equal(container.Root, DbPathResolver.ResolveProjectRootForQuery(dbPath, true, refreshedReader));
        Assert.False(PathCasing.IsIgnoreCase(dbPath));
    }

    [Fact]
    public void RunBatch_ParallelReusesBoundedSessionsAndMatchesSerialRecords_Issue4872()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_batch_session_reuse_4872");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        var input = BuildIssue4872BatchInput(9);
        var (serialExitCode, serialStdout, serialStderr) = CaptureConsoleWithInput(
            input,
            () => QueryCommandRunner.RunBatch(
                ["--db", dbPath, "--json-summary"],
                _jsonOptions));

        using var firstWaveStarted = new CountdownEvent(3);
        var openedSessions = 0;
        QueryCommandRunner.BatchParallelSessionOpenedForTesting =
            () => Interlocked.Increment(ref openedSessions);
        QueryCommandRunner.BatchParallelCommandStartedForTesting = lineNumber =>
        {
            if (lineNumber > 3)
                return;
            firstWaveStarted.Signal();
            if (!firstWaveStarted.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("The first parallel batch wave did not start.");
        };

        string parallelStdout;
        string parallelStderr;
        int parallelExitCode;
        try
        {
            (parallelExitCode, parallelStdout, parallelStderr) = CaptureConsoleWithInput(
                input,
                () => QueryCommandRunner.RunBatch(
                    ["--db", dbPath, "--json-summary", "--parallel", "3"],
                    _jsonOptions));
        }
        finally
        {
            QueryCommandRunner.BatchParallelSessionOpenedForTesting = null;
            QueryCommandRunner.BatchParallelCommandStartedForTesting = null;
        }

        Assert.Equal(CommandExitCodes.Success, serialExitCode);
        Assert.Equal(CommandExitCodes.Success, parallelExitCode);
        Assert.Equal(string.Empty, serialStderr);
        Assert.Equal(string.Empty, parallelStderr);
        Assert.Equal(3, Volatile.Read(ref openedSessions));

        var serialLines = ParseJsonLines(serialStdout);
        var parallelLines = ParseJsonLines(parallelStdout);
        try
        {
            Assert.Equal(10, serialLines.Count);
            Assert.Equal(serialLines.Count, parallelLines.Count);
            for (var index = 0; index < 9; index++)
            {
                var serialRecord = serialLines[index].RootElement;
                var parallelRecord = parallelLines[index].RootElement;
                Assert.Equal(index + 1, parallelRecord.GetProperty("line").GetInt32());
                Assert.Equal(
                    serialRecord.GetProperty("command").GetString(),
                    parallelRecord.GetProperty("command").GetString());
                Assert.Equal(
                    serialRecord.GetProperty("arguments").GetRawText(),
                    parallelRecord.GetProperty("arguments").GetRawText());
                Assert.Equal(
                    serialRecord.GetProperty("exit_code").GetInt32(),
                    parallelRecord.GetProperty("exit_code").GetInt32());
                Assert.Equal(
                    serialRecord.GetProperty("result").GetRawText(),
                    parallelRecord.GetProperty("result").GetRawText());
            }

            Assert.Equal(1, serialLines[^1].RootElement.GetProperty("parallelism").GetInt32());
            Assert.Equal(3, parallelLines[^1].RootElement.GetProperty("parallelism").GetInt32());
        }
        finally
        {
            foreach (var document in serialLines)
                document.Dispose();
            foreach (var document in parallelLines)
                document.Dispose();
        }
    }

    [Fact]
    public async Task RunBatch_ParallelRefreshesDetachedSnapshotsBetweenItems_Issue4872()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_batch_snapshot_refresh_4872");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        using var writer = new SqliteConnection(
            $"Data Source={dbPath};Mode=ReadWrite;Pooling=False");
        writer.Open();
        using (var setup = writer.CreateCommand())
        {
            setup.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA wal_autocheckpoint=0;
                INSERT INTO files(path, lang, size, lines, checksum, modified)
                VALUES ('src/Initial.cs', 'csharp', 1, 1, 'initial', CURRENT_TIMESTAMP);
                PRAGMA wal_checkpoint(TRUNCATE);
                UPDATE files SET checksum = 'initial-hot' WHERE path = 'src/Initial.cs';
                """;
            setup.ExecuteNonQuery();
        }
        Assert.True(new FileInfo(dbPath + "-wal").Length > 0);
        var initialDbLength = new FileInfo(dbPath).Length;

        using var input = new InteractiveBatchTextReader();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var firstWaveCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var statusWaveTimeout = TimeSpan.FromMinutes(2);
        Task<int>? runTask = null;
        var openedSessions = 0;
        var completedFirstWaveCommands = 0;
        QueryCommandRunner.BatchParallelSessionOpenedForTesting =
            () => Interlocked.Increment(ref openedSessions);
        QueryCommandRunner.BatchParallelCommandCompletedForTesting = lineNumber =>
        {
            if (lineNumber <= 2
                && Interlocked.Increment(ref completedFirstWaveCommands) == 2)
            {
                firstWaveCompleted.TrySetResult();
            }
        };

        try
        {
            runTask = StartIssue4872InteractiveBatch(
                dbPath,
                input,
                stdout,
                stderr,
                cancellation.Token);
            input.WriteLine("""{"command":"files","args":["--format","count","--json"]}""");
            input.WriteLine("""{"command":"files","args":["--format","count","--json"]}""");
            await WaitForIssue4872FirstWaveAsync(
                firstWaveCompleted.Task,
                runTask,
                statusWaveTimeout);

            using (var update = writer.CreateCommand())
            {
                update.CommandText = """
                    INSERT INTO files(path, lang, size, lines, checksum, modified)
                    VALUES ('src/Updated.cs', 'csharp', 1, 1, 'updated', CURRENT_TIMESTAMP);
                    PRAGMA wal_checkpoint(TRUNCATE);
                    """;
                update.ExecuteNonQuery();
            }
            Assert.Equal(initialDbLength, new FileInfo(dbPath).Length);
            Assert.Equal(0, new FileInfo(dbPath + "-wal").Length);

            input.WriteLine("""{"command":"files","args":["--format","count","--json"]}""");
            input.WriteLine("""{"command":"files","args":["--format","count","--json"]}""");
            input.Complete();

            var exitCode = await runTask.WaitAsync(statusWaveTimeout);
            Assert.True(
                exitCode == CommandExitCodes.Success,
                $"Parallel batch exited with code {exitCode}: {stdout}");
            Assert.Equal(string.Empty, stderr.ToString());

            var lines = ParseJsonLines(stdout.ToString());
            try
            {
                Assert.Equal(5, lines.Count);
                Assert.Equal(1, lines[0].RootElement.GetProperty("result").GetProperty("count").GetInt32());
                Assert.Equal(1, lines[1].RootElement.GetProperty("result").GetProperty("count").GetInt32());
                Assert.Equal(2, lines[2].RootElement.GetProperty("result").GetProperty("count").GetInt32());
                Assert.Equal(2, lines[3].RootElement.GetProperty("result").GetProperty("count").GetInt32());
                Assert.Equal(4, Volatile.Read(ref openedSessions));
            }
            finally
            {
                foreach (var document in lines)
                    document.Dispose();
            }
        }
        finally
        {
            QueryCommandRunner.BatchParallelSessionOpenedForTesting = null;
            QueryCommandRunner.BatchParallelCommandCompletedForTesting = null;
            input.Complete();
            if (runTask is { IsCompleted: false })
            {
                cancellation.Cancel();
                try
                {
                    await runTask.WaitAsync(TimeSpan.FromSeconds(15));
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                }
                catch (TimeoutException exception)
                {
                    throw new TimeoutException(
                        "The dedicated parallel batch test thread did not drain after cancellation.",
                        exception);
                }
            }
        }
    }

    [Fact]
    public async Task RunBatch_ParallelRefreshesDirectSessionsBetweenItems_Issue4872()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_batch_direct_refresh_4872");
        var sourceDbPath = TestProjectHelper.CreateProjectDb(project.Root);
        using (var source = new SqliteConnection(
                   $"Data Source={sourceDbPath};Mode=ReadWrite;Pooling=False"))
        {
            source.Open();
            using var checkpoint = source.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            checkpoint.ExecuteNonQuery();
        }

        var dbPath = Path.Combine(project.Root, "direct.db");
        File.Copy(sourceDbPath, dbPath);
        using var writer = new SqliteConnection(
            $"Data Source={dbPath};Mode=ReadWrite;Pooling=False");
        writer.Open();
        using (var journalMode = writer.CreateCommand())
        {
            journalMode.CommandText = "PRAGMA journal_mode=DELETE";
            Assert.Equal("delete", Assert.IsType<string>(journalMode.ExecuteScalar()));
        }
        using (var setup = writer.CreateCommand())
        {
            setup.CommandText = """
                INSERT INTO files(path, lang, size, lines, checksum, modified)
                VALUES ('src/Initial.cs', 'csharp', 1, 1, 'initial', CURRENT_TIMESTAMP);
                """;
            setup.ExecuteNonQuery();
        }
        Assert.Equal("delete", ReadJournalMode(writer));

        using var input = new InteractiveBatchTextReader();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var firstWaveCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        Task<int>? runTask = null;
        var openedSessions = 0;
        var completedFirstWaveCommands = 0;
        QueryCommandRunner.BatchParallelSessionOpenedForTesting =
            () => Interlocked.Increment(ref openedSessions);
        QueryCommandRunner.BatchParallelCommandCompletedForTesting = lineNumber =>
        {
            if (lineNumber <= 2
                && Interlocked.Increment(ref completedFirstWaveCommands) == 2)
            {
                firstWaveCompleted.TrySetResult();
            }
        };

        try
        {
            runTask = StartIssue4872InteractiveBatch(
                dbPath,
                input,
                stdout,
                stderr,
                cancellation.Token);
            input.WriteLine("""{"command":"files","args":["--format","count","--json"]}""");
            input.WriteLine("""{"command":"files","args":["--format","count","--json"]}""");
            await WaitForIssue4872FirstWaveAsync(
                firstWaveCompleted.Task,
                runTask,
                TimeSpan.FromSeconds(60));

            using (var update = writer.CreateCommand())
            {
                update.CommandText = """
                    INSERT INTO files(path, lang, size, lines, checksum, modified)
                    VALUES ('src/Updated.cs', 'csharp', 1, 1, 'updated', CURRENT_TIMESTAMP);
                    """;
                Assert.Equal(1, update.ExecuteNonQuery());
            }

            input.WriteLine("""{"command":"files","args":["--format","count","--json"]}""");
            input.WriteLine("""{"command":"files","args":["--format","count","--json"]}""");
            input.Complete();

            var exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.True(
                exitCode == CommandExitCodes.Success,
                $"Parallel batch exited with code {exitCode}: {stdout}");
            Assert.Equal(string.Empty, stderr.ToString());

            var lines = ParseJsonLines(stdout.ToString());
            try
            {
                Assert.Equal(5, lines.Count);
                Assert.Equal(1, lines[0].RootElement.GetProperty("result").GetProperty("count").GetInt32());
                Assert.Equal(1, lines[1].RootElement.GetProperty("result").GetProperty("count").GetInt32());
                Assert.Equal(2, lines[2].RootElement.GetProperty("result").GetProperty("count").GetInt32());
                Assert.Equal(2, lines[3].RootElement.GetProperty("result").GetProperty("count").GetInt32());
                Assert.Equal(4, Volatile.Read(ref openedSessions));
            }
            finally
            {
                foreach (var document in lines)
                    document.Dispose();
            }
        }
        finally
        {
            QueryCommandRunner.BatchParallelSessionOpenedForTesting = null;
            QueryCommandRunner.BatchParallelCommandCompletedForTesting = null;
            input.Complete();
            if (runTask is { IsCompleted: false })
            {
                cancellation.Cancel();
                try
                {
                    await runTask.WaitAsync(TimeSpan.FromSeconds(15));
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                }
                catch (TimeoutException exception)
                {
                    throw new TimeoutException(
                        "The dedicated parallel batch test thread did not drain after cancellation.",
                        exception);
                }
            }
        }
    }

    [Fact]
    public void RunBatch_InitialValidationFailuresDisposeDetachedSnapshots_Issue4872()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_batch_validation_failure_4872");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        using var writer = new SqliteConnection(
            $"Data Source={dbPath};Mode=ReadWrite;Pooling=False");
        writer.Open();
        using (var setup = writer.CreateCommand())
        {
            setup.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA wal_autocheckpoint=0;
                INSERT INTO files(path, lang, size, lines, checksum, modified)
                VALUES ('src/Initial.cs', 'csharp', 1, 1, 'initial', CURRENT_TIMESTAMP);
                """;
            setup.ExecuteNonQuery();
        }

        var snapshotDirectories = new ConcurrentBag<string>();
        var originalDirectoryHook = DbConnectionFactory.QueryOnlySnapshotDirectoryCreatedForTesting;
        var originalValidationHook = QueryCommandRunner.BatchParallelDatabaseValidatingForTesting;
        DbConnectionFactory.QueryOnlySnapshotDirectoryCreatedForTesting = snapshotDirectories.Add;
        QueryCommandRunner.BatchParallelDatabaseValidatingForTesting =
            () => throw new InvalidDataException("Injected parallel batch validation failure.");

        try
        {
            Assert.Throws<InvalidDataException>(() => CaptureConsoleWithInput(
                BuildIssue4872BatchInput(1),
                () => QueryCommandRunner.RunBatch(
                    ["--db", dbPath, "--json-summary", "--parallel", "2"],
                    _jsonOptions)));
        }
        finally
        {
            QueryCommandRunner.BatchParallelDatabaseValidatingForTesting = originalValidationHook;
            DbConnectionFactory.QueryOnlySnapshotDirectoryCreatedForTesting = originalDirectoryHook;
        }

        Assert.NotEmpty(snapshotDirectories);
        Assert.All(snapshotDirectories, path => Assert.False(Directory.Exists(path), path));
    }

    [Fact]
    public void RunBatch_ReaderConstructionFailuresDisposeDetachedSnapshots_Issue4872()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_batch_reader_failure_4872");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        using var writer = new SqliteConnection(
            $"Data Source={dbPath};Mode=ReadWrite;Pooling=False");
        writer.Open();
        using (var setup = writer.CreateCommand())
        {
            setup.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA wal_autocheckpoint=0;
                INSERT INTO files(path, lang, size, lines, checksum, modified)
                VALUES ('src/Initial.cs', 'csharp', 1, 1, 'initial', CURRENT_TIMESTAMP);
                """;
            setup.ExecuteNonQuery();
        }
        Assert.True(new FileInfo(dbPath + "-wal").Length > 0);

        var snapshotDirectories = new ConcurrentBag<string>();
        var readerConstructionAttempts = 0;
        var originalDirectoryHook = DbConnectionFactory.QueryOnlySnapshotDirectoryCreatedForTesting;
        var originalReaderFactory = QueryCommandRunner.BatchParallelReaderFactoryForTesting;
        DbConnectionFactory.QueryOnlySnapshotDirectoryCreatedForTesting = snapshotDirectories.Add;
        QueryCommandRunner.BatchParallelReaderFactoryForTesting = db =>
        {
            if (Interlocked.Increment(ref readerConstructionAttempts) == 1)
                return new DbReader(db);
            throw new InvalidDataException("Injected parallel batch reader construction failure.");
        };

        int exitCode;
        string stdout;
        string stderr;
        try
        {
            (exitCode, stdout, stderr) = CaptureConsoleWithInput(
                BuildIssue4872BatchInput(6),
                () => QueryCommandRunner.RunBatch(
                    ["--db", dbPath, "--json-summary", "--parallel", "2"],
                    _jsonOptions));
        }
        finally
        {
            QueryCommandRunner.BatchParallelReaderFactoryForTesting = originalReaderFactory;
            DbConnectionFactory.QueryOnlySnapshotDirectoryCreatedForTesting = originalDirectoryHook;
        }

        Assert.Equal(CommandExitCodes.RuntimeError, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.True(Volatile.Read(ref readerConstructionAttempts) >= 3);
        Assert.True(snapshotDirectories.Count >= 3);
        Assert.All(snapshotDirectories, path => Assert.False(Directory.Exists(path), path));

        var lines = ParseJsonLines(stdout);
        try
        {
            Assert.Equal(7, lines.Count);
            Assert.Equal(3, lines.Take(6).Count(
                line => line.RootElement.GetProperty("exit_code").GetInt32() == CommandExitCodes.RuntimeError));
        }
        finally
        {
            foreach (var document in lines)
                document.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RunBatch_ParallelSessionReuseKeepsSnapshotWorkConstant_Issue5332_Issue5339(bool filesCount)
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_batch_work_5332");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        using var writer = new SqliteConnection(
            $"Data Source={dbPath};Mode=ReadWrite;Pooling=False");
        writer.Open();
        using (var command = writer.CreateCommand())
        {
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA wal_autocheckpoint=0;
                INSERT INTO files(path, lang, size, lines, checksum, modified)
                VALUES ('src/Initial.cs', 'csharp', 1, 1, 'initial', CURRENT_TIMESTAMP);
                """;
            command.ExecuteNonQuery();
        }
        var dbBytes = new FileInfo(dbPath).Length;
        var walBytes = new FileInfo(dbPath + "-wal").Length;
        Assert.True(walBytes > 0);

        // The writer stays open without further writes, so every item sees the same
        // hot WAL generation. Count real setup/copy work instead of scheduler time.
        // Rejected commands isolate session setup; successful file counts also
        // detect per-item metadata probes that would reopen/copy the same source.
        foreach (var commandCount in new[] { 3, 12 })
            AssertIssue5332ParallelBatchWork(dbPath, commandCount, dbBytes, walBytes, filesCount);
    }

    private void AssertIssue5332ParallelBatchWork(
        string dbPath, int commandCount, long dbBytes, long walBytes, bool filesCount)
    {
        using var firstWaveStarted = new CountdownEvent(3);
        var openedSessions = 0;
        var constructedReaders = 0;
        var copiedFiles = new ConcurrentBag<(string Name, long Bytes)>();
        var snapshotDirectories = new ConcurrentBag<string>();
        var startedLines = new ConcurrentBag<int>();
        var originalSessionHook = QueryCommandRunner.BatchParallelSessionOpenedForTesting;
        var originalReaderFactory = QueryCommandRunner.BatchParallelReaderFactoryForTesting;
        var originalCommandHook = QueryCommandRunner.BatchParallelCommandStartedForTesting;
        var originalCopyHook = DbConnectionFactory.QueryOnlySnapshotFileCopyingForTesting;
        var originalDirectoryHook = DbConnectionFactory.QueryOnlySnapshotDirectoryCreatedForTesting;
        QueryCommandRunner.BatchParallelSessionOpenedForTesting =
            () => Interlocked.Increment(ref openedSessions);
        QueryCommandRunner.BatchParallelReaderFactoryForTesting = db =>
        {
            Interlocked.Increment(ref constructedReaders);
            return new DbReader(db);
        };
        DbConnectionFactory.QueryOnlySnapshotFileCopyingForTesting = (source, destination) =>
            copiedFiles.Add((Path.GetFileName(destination), new FileInfo(source).Length));
        DbConnectionFactory.QueryOnlySnapshotDirectoryCreatedForTesting = snapshotDirectories.Add;
        QueryCommandRunner.BatchParallelCommandStartedForTesting = lineNumber =>
        {
            startedLines.Add(lineNumber);
            if (lineNumber > 3)
                return;
            firstWaveStarted.Signal();
            // Synchronization watchdog only; elapsed time is not a performance assertion.
            if (!firstWaveStarted.Wait(TimeSpan.FromSeconds(30)))
                throw new TimeoutException("The parallel batch work-count wave did not start.");
        };

        try
        {
            var (exitCode, stdout, stderr) = CaptureConsoleWithInput(
                filesCount
                    ? string.Join('\n', Enumerable.Repeat("""{"command":"files","args":["--format","count","--json"]}""", commandCount)) + "\n"
                    : BuildIssue4872RejectedBatchInput(commandCount),
                () => QueryCommandRunner.RunBatch(
                    ["--db", dbPath, "--json-summary", "--parallel", "3"],
                    _jsonOptions));
            var expectedExitCode = filesCount ? CommandExitCodes.Success : CommandExitCodes.UsageError;
            const int expectedSnapshots = 3;
            Assert.Equal(expectedExitCode, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(Enumerable.Range(1, commandCount), startedLines.Order());
            Assert.Equal(3, Volatile.Read(ref openedSessions));
            Assert.Equal(3, Volatile.Read(ref constructedReaders));
            Assert.Equal(expectedSnapshots, snapshotDirectories.Count);
            Assert.All(snapshotDirectories, path => Assert.False(Directory.Exists(path), path));
            Assert.Equal(expectedSnapshots * 2, copiedFiles.Count);
            Assert.Equal(expectedSnapshots, copiedFiles.Count(file => file.Name == "snapshot.db" && file.Bytes == dbBytes));
            Assert.Equal(expectedSnapshots, copiedFiles.Count(file => file.Name == "snapshot.db-wal" && file.Bytes == walBytes));

            var lines = ParseJsonLines(stdout);
            try
            {
                Assert.Equal(commandCount + 1, lines.Count);
                for (var index = 0; index < commandCount; index++)
                {
                    var record = lines[index].RootElement;
                    Assert.Equal(index + 1, record.GetProperty("line").GetInt32());
                    Assert.Equal(filesCount ? "files" : "unknown", record.GetProperty("command").GetString());
                    Assert.Equal(expectedExitCode, record.GetProperty("exit_code").GetInt32());
                    if (filesCount)
                    {
                        var result = record.GetProperty("result");
                        Assert.Equal(1, result.GetProperty("count").GetInt32());
                        Assert.Equal(1, result.GetProperty("file_count").GetInt32());
                        Assert.Equal(1, result.GetProperty("indexed_file_count").GetInt32());
                        Assert.True(result.GetProperty("freshness_available").GetBoolean());
                        Assert.True(result.GetProperty("authoritative_count").GetBoolean());
                    }
                }
                var summary = lines[^1].RootElement;
                Assert.Equal("batch_summary", summary.GetProperty("record").GetString());
                Assert.Equal(3, summary.GetProperty("parallelism").GetInt32());
                Assert.Equal(commandCount, summary.GetProperty("commands_processed").GetInt32());
                Assert.Equal(filesCount ? 0 : commandCount, summary.GetProperty("command_failures").GetInt32());
            }
            finally
            {
                foreach (var document in lines)
                    document.Dispose();
            }
        }
        finally
        {
            QueryCommandRunner.BatchParallelSessionOpenedForTesting = originalSessionHook;
            QueryCommandRunner.BatchParallelReaderFactoryForTesting = originalReaderFactory;
            QueryCommandRunner.BatchParallelCommandStartedForTesting = originalCommandHook;
            DbConnectionFactory.QueryOnlySnapshotFileCopyingForTesting = originalCopyHook;
            DbConnectionFactory.QueryOnlySnapshotDirectoryCreatedForTesting = originalDirectoryHook;
        }
    }

    private static string BuildIssue4872BatchInput(int commandCount)
        => string.Join(
            '\n',
            Enumerable.Repeat(
                """{"command":"languages","args":["--format","count"]}""",
                commandCount)) + "\n";

    private static string BuildIssue4872RejectedBatchInput(int commandCount)
        => string.Join(
            '\n',
            Enumerable.Repeat(
                """{"command":"unknown"}""",
                commandCount)) + "\n";

    private static string ReadJournalMode(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode";
        return Assert.IsType<string>(command.ExecuteScalar());
    }

    private Task<int> StartIssue4872InteractiveBatch(
        string dbPath,
        InteractiveBatchTextReader input,
        StringWriter stdout,
        StringWriter stderr,
        CancellationToken cancellationToken)
        => Task.Factory.StartNew(
            () =>
            {
                using (var capture = ConsoleCapture.Start(stdout, stderr, input))
                {
                    return QueryCommandRunner.RunBatch(
                        ["--db", dbPath, "--json-summary", "--parallel", "2"],
                        _jsonOptions,
                        cancellationToken: cancellationToken);
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);

    private static async Task WaitForIssue4872FirstWaveAsync(
        Task firstWaveCompleted,
        Task<int> runTask,
        TimeSpan timeout)
    {
        Task boundary;
        try
        {
            boundary = await Task.WhenAny(firstWaveCompleted, runTask)
                .WaitAsync(timeout);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(
                "The first parallel batch wave did not complete before its synchronization boundary.",
                exception);
        }
        if (ReferenceEquals(boundary, runTask))
        {
            var exitCode = await runTask;
            Assert.Fail(
                $"The parallel batch exited with code {exitCode} before the first wave completed.");
        }

        await firstWaveCompleted;
    }
}
