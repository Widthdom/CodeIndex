using CodeIndex.Database;

namespace CodeIndex.Tests;

public class JsonStringListCodecTests
{
    [Fact]
    public void Deserialize_ValidListReturnsNonBlankStrings()
    {
        var raw = JsonStringListCodec.Serialize(["alpha", " ", "beta"]);

        var values = JsonStringListCodec.Deserialize(raw, out var diagnostic);

        Assert.Null(diagnostic);
        Assert.Equal(["alpha", "beta"], values);
    }

    [Fact]
    public void Deserialize_NonStringElementsAreIgnoredWithinBounds()
    {
        var raw = """["alpha",null,42," ","beta",{"name":"ignored"},["nested"]]""";

        var values = JsonStringListCodec.Deserialize(raw, out var diagnostic);

        Assert.Null(diagnostic);
        Assert.Equal(["alpha", "beta"], values);
    }

    [Fact]
    public void Deserialize_RejectsOverDepthJson()
    {
        var depth = JsonStringListCodec.MaxJsonDepth + 8;
        var raw = new string('[', depth) + "\"value\"" + new string(']', depth);

        var values = JsonStringListCodec.Deserialize(raw, out var diagnostic);

        Assert.Null(values);
        Assert.Equal("json_string_list_malformed", diagnostic);
    }

    [Fact]
    public void Deserialize_RejectsTooManyArrayItems()
    {
        var raw = "["
                  + string.Join(",", Enumerable.Repeat("\"value\"", JsonStringListCodec.MaxArrayItems + 1))
                  + "]";

        var values = JsonStringListCodec.Deserialize(raw, out var diagnostic);

        Assert.Null(values);
        Assert.Equal("json_string_list_too_many_items", diagnostic);
    }

    [Fact]
    public void Deserialize_RejectsTooManyDecodedCharacters()
    {
        var raw = "[\"" + new string('a', JsonStringListCodec.MaxDecodedStringCharacters + 1) + "\"]";

        var values = JsonStringListCodec.Deserialize(raw, out var diagnostic);

        Assert.Null(values);
        Assert.Equal("json_string_list_too_many_characters", diagnostic);
    }

    [Fact]
    public void Deserialize_RejectsOversizedRawJson()
    {
        var raw = "[" + new string(' ', JsonStringListCodec.MaxRawJsonCharacters) + "]";

        var values = JsonStringListCodec.Deserialize(raw, out var diagnostic);

        Assert.Null(values);
        Assert.Equal("json_string_list_raw_too_large", diagnostic);
    }

    [Fact]
    public void WriteUnknownExtensionFileMetadata_CapsSampleWithinReaderBudget()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"codeindex_json_string_list_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            db.InitializeSchema();
            var writer = new DbWriter(db.Connection);
            var longPathPrefix = new string('a', 2048);
            var paths = Enumerable.Range(0, DbContext.UnknownExtensionFilePathSampleLimit)
                .Select(i => $"{longPathPrefix}{i:D2}.mystery")
                .ToArray();
            Assert.True(paths.Sum(path => path.Length) > JsonStringListCodec.MaxDecodedStringCharacters);

            writer.WriteUnknownExtensionFileMetadata(paths);

            var status = new DbReader(db.Connection).GetStatus();
            var sample = Assert.IsType<List<string>>(status.UnknownExtensionFiles);
            Assert.NotEmpty(sample);
            Assert.True(sample.Sum(path => path.Length) <= JsonStringListCodec.MaxDecodedStringCharacters);
            Assert.True(status.UnknownExtensionFilesTruncated);
            Assert.Equal((long)DbContext.UnknownExtensionFilePathSampleLimit, status.UnknownExtensionFilePathLimit);
            Assert.Equal((long)paths.Length, status.UnknownExtensionFileCount);
        }
        finally
        {
            TestProjectHelper.DeleteFile(dbPath);
        }
    }

    [Fact]
    public void WriteUnknownExtensionFileMetadata_EmptyPathsStampsEmptyMetadata()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"codeindex_json_string_list_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            db.InitializeSchema();
            var writer = new DbWriter(db.Connection);

            writer.WriteUnknownExtensionFileMetadata([]);

            var status = new DbReader(db.Connection).GetStatus();
            Assert.Equal(0, status.UnknownExtensionFileCount);
            Assert.Empty(Assert.IsType<List<string>>(status.UnknownExtensionFiles));
            Assert.False(status.UnknownExtensionFilesTruncated);
            Assert.Equal((long)DbContext.UnknownExtensionFilePathSampleLimit, status.UnknownExtensionFilePathLimit);
            Assert.Empty(Assert.IsType<Dictionary<string, long>>(status.UnknownExtensionExtensionCounts));
            Assert.Empty(Assert.IsType<Dictionary<string, long>>(status.UnknownExtensionCategoryCounts));
            Assert.Empty(Assert.IsType<List<StatusUnknownExtensionGroup>>(status.UnknownExtensionGroups));
            Assert.Equal(0, status.UnknownExtensionGroupCount);
            Assert.False(status.UnknownExtensionGroupsTruncated);
            Assert.Equal(UnknownExtensionClassifier.MaxPersistedGroups, status.UnknownExtensionGroupLimit);
            Assert.Equal(0, status.UnknownExtensionGroupOmittedCount);
            Assert.Null(status.UnknownExtensionGuidance);
        }
        finally
        {
            TestProjectHelper.DeleteFile(dbPath);
        }
    }

    [Fact]
    public void WriteUnknownExtensionFileMetadata_ReportsOmittedGroups_Issue5100()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"codeindex_unknown_groups_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            db.InitializeSchema();
            var writer = new DbWriter(db.Connection);
            var paths = Enumerable.Range(0, UnknownExtensionClassifier.MaxPersistedGroups + 2)
                .Select(index => $"source-{index:D3}.unknown{index:D3}")
                .ToArray();

            writer.WriteUnknownExtensionFileMetadata(paths);

            var status = new DbReader(db.Connection).GetStatus();
            Assert.Equal(paths.Length, status.UnknownExtensionGroupCount);
            Assert.True(status.UnknownExtensionGroupsTruncated);
            Assert.Equal(UnknownExtensionClassifier.MaxPersistedGroups, status.UnknownExtensionGroupLimit);
            Assert.Equal(2, status.UnknownExtensionGroupOmittedCount);
            Assert.Equal(
                UnknownExtensionClassifier.MaxPersistedGroups,
                Assert.IsType<List<StatusUnknownExtensionGroup>>(status.UnknownExtensionGroups).Count);
        }
        finally
        {
            TestProjectHelper.DeleteFile(dbPath);
        }
    }
}
