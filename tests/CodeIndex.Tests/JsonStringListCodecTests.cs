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
}
