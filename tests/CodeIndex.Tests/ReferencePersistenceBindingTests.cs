using CodeIndex.Database;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public sealed class ReferencePersistenceBindingTests : IDisposable
{
    private readonly string _projectRoot;
    private readonly DbContext _db;
    private readonly DbWriter _writer;

    public ReferencePersistenceBindingTests()
    {
        _projectRoot = TestProjectHelper.CreateTempProject("cdidx_reference_binding");
        _db = new DbContext(
            DbOpenIntent.WriteIndex,
            Path.Combine(_projectRoot, "codeindex.db"));
        _db.InitializeSchema();
        _writer = new DbWriter(_db.Connection);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void InsertReferences_AllPersistenceModesBindNormalizedContextByOrdinal(
        bool atomicFileScope,
        bool referenceLinesAreNew)
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = $"src/reference-binding-{atomicFileScope}-{referenceLinesAreNew}.cs",
            Lang = "csharp",
            Size = 100,
            Lines = 20,
            Checksum = $"reference-binding-{atomicFileScope}-{referenceLinesAreNew}",
            Modified = new DateTime(2026, 8, 8, 0, 0, 0, DateTimeKind.Utc),
        });
        var references = new[]
        {
            CreateReference(fileId, "First", line: 10, context: "shared();"),
            CreateReference(fileId, "Second", line: 20, context: "other();"),
            CreateReference(fileId, "Third", line: 10, context: "shared();"),
        };
        var observedWork = new List<DbWriter.ReferenceInsertBindingWork>();
        var previousWorkHook = DbWriter.ReferenceInsertBindingWorkForTesting;
        try
        {
            DbWriter.ReferenceInsertBindingWorkForTesting = work =>
            {
                observedWork.Add(work);
                previousWorkHook?.Invoke(work);
            };

            if (atomicFileScope)
            {
                using var transaction = _writer.BeginTransaction();
                if (referenceLinesAreNew)
                {
                    _writer.InsertReferencesForNewFilesInAtomicFileScope(
                        references,
                        refreshMutualRecursionFlags: false,
                        CancellationToken.None);
                }
                else
                {
                    _writer.InsertReferencesInAtomicFileScope(
                        references,
                        refreshMutualRecursionFlags: false,
                        CancellationToken.None);
                }
                transaction.Commit();
            }
            else if (referenceLinesAreNew)
            {
                _writer.InsertReferencesForNewFiles(
                    references,
                    refreshMutualRecursionFlags: false,
                    CancellationToken.None);
            }
            else
            {
                _writer.InsertReferences(
                    references,
                    refreshMutualRecursionFlags: false,
                    CancellationToken.None);
            }
        }
        finally
        {
            DbWriter.ReferenceInsertBindingWorkForTesting = previousWorkHook;
        }

        var work = Assert.Single(observedWork);
        Assert.Equal(3, work.StatementRows);
        Assert.Equal(3 * 14, work.BoundParameterCount);
        Assert.Equal(3, work.MaterializedReferenceCount);
        Assert.Equal(2, work.MaterializedReferenceLineCount);

        using var command = _db.Connection.CreateCommand();
        command.Parameters.AddWithValue("@fileId", fileId);
        command.CommandText = """
            SELECT sr.symbol_name, sr.context, sr.reference_line_id, rl.context
            FROM symbol_references AS sr
            JOIN reference_lines AS rl ON rl.id = sr.reference_line_id
            WHERE sr.file_id = @fileId
            ORDER BY sr.id
            """;
        using (var reader = command.ExecuteReader())
        {
            var expected = new[]
            {
                (Symbol: "First", Context: "shared();"),
                (Symbol: "Second", Context: "other();"),
                (Symbol: "Third", Context: "shared();"),
            };
            foreach (var row in expected)
            {
                Assert.True(reader.Read());
                Assert.Equal(row.Symbol, reader.GetString(0));
                Assert.True(reader.IsDBNull(1));
                Assert.True(reader.GetInt64(2) > 0);
                Assert.Equal(row.Context, reader.GetString(3));
            }
            Assert.False(reader.Read());
        }

        command.CommandText = """
            SELECT COUNT(DISTINCT reference_line_id)
            FROM symbol_references
            WHERE file_id = @fileId
            """;
        Assert.Equal(2L, (long)command.ExecuteScalar()!);
    }

    public void Dispose()
    {
        _db.Dispose();
        TestProjectHelper.DeleteDirectory(_projectRoot);
    }

    private static ReferenceRecord CreateReference(
        long fileId,
        string symbolName,
        int line,
        string context)
        => new()
        {
            FileId = fileId,
            SymbolName = symbolName,
            ReferenceKind = "call",
            Line = line,
            Column = 1,
            Context = context,
            ContainerKind = "function",
            ContainerName = "Caller",
        };
}
