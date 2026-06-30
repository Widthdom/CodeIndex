using System.Text;
using System.Text.Json;
using CodeIndex.Cli;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

public sealed class ExportImportManifestCodecTests
{
    [Fact]
    public void TryDeserialize_RejectsNullManifest_Issue4181()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

        Assert.False(ExportImportManifestCodec.TryDeserialize("null"u8, options, out _, out var message));
        Assert.Equal("manifest.json did not contain an object", message);
    }

    [Fact]
    public void TryDeserialize_RejectsMalformedManifest_Issue4181()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

        Assert.False(ExportImportManifestCodec.TryDeserialize("{"u8, options, out _, out var message));
        Assert.Equal("manifest.json is not valid export manifest JSON", message);
    }

    [Fact]
    public void TryDeserialize_RejectsOverDepthManifest_Issue4181()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var json =
            "{\"format_version\":\"1\",\"cdidx_version\":\"test\",\"user_version\":0,\"database_sha256\":\"" +
            new string('0', 64) +
            "\",\"nested\":" +
            string.Concat(Enumerable.Repeat("{\"x\":", ExportImportCommandRunner.MaxImportManifestJsonDepth + 1)) +
            "0" +
            new string('}', ExportImportCommandRunner.MaxImportManifestJsonDepth + 1) +
            "}";

        Assert.False(ExportImportManifestCodec.TryDeserialize(Encoding.UTF8.GetBytes(json), options, out _, out var message));
        Assert.Equal(
            $"manifest.json exceeds the JSON depth limit of {ExportImportCommandRunner.MaxImportManifestJsonDepth}",
            message);
    }

    [Fact]
    public void TryValidateHeader_RejectsNegativeMetadata_Issue4181()
    {
        var manifest = CreateValidManifest(FileCount: -1);

        Assert.False(ExportImportManifestCodec.TryValidateHeader(manifest, out var message));
        Assert.Equal("file_count must be non-negative", message);
    }

    [Fact]
    public void TryValidateHeader_RejectsUnknownExtensionSampleMismatch_Issue4181()
    {
        var manifest = CreateValidManifest(
            UnknownExtensionFiles: ["docs/notes.txt"],
            UnknownExtensionFileSampleCount: 2,
            UnknownExtensionFileSampleLimit: 10);

        Assert.False(ExportImportManifestCodec.TryValidateHeader(manifest, out var message));
        Assert.Equal("unknown_extension_file_sample_count must match unknown_extension_files length", message);
    }

    [Fact]
    public void ReadNullableString_ReturnsNullForDbNull_Issue4181()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT NULL AS optional, 'value' AS required";
        using var reader = command.ExecuteReader();

        Assert.True(reader.Read());
        Assert.Null(ExportImportSqliteRow.ReadNullableString(reader, 0));
        Assert.Equal("value", ExportImportSqliteRow.ReadNullableString(reader, 1));
    }

    private static ExportImportCommandRunner.ExportManifest CreateValidManifest(
        long? FileCount = null,
        string[]? UnknownExtensionFiles = null,
        int? UnknownExtensionFileSampleCount = null,
        int? UnknownExtensionFileSampleLimit = null)
        => new(
            "1",
            "test",
            0,
            ProjectRoot: null,
            IndexedHeadSha: null,
            DatabaseSha256: new string('0', 64),
            FileCount: FileCount,
            UnknownExtensionFiles: UnknownExtensionFiles,
            UnknownExtensionFileSampleCount: UnknownExtensionFileSampleCount,
            UnknownExtensionFileSampleLimit: UnknownExtensionFileSampleLimit);
}
