namespace CodeIndex.Tests;

public class HexEncodingTests
{
    [Fact]
    public void ToLowerHexString_FormatsFullByteArrayWithoutUppercase()
    {
        var value = HexEncoding.ToLowerHexString([0x00, 0x0F, 0x10, 0xAB, 0xCD, 0xEF]);

        Assert.Equal("000f10abcdef", value);
    }

    [Fact]
    public void ToLowerHexString_FormatsByteArraySlice()
    {
        var value = HexEncoding.ToLowerHexString([0xFF, 0x01, 0x23, 0x45, 0xEE], 1, 3);

        Assert.Equal("012345", value);
    }

    [Fact]
    public void ToLowerHexString_NullBytes_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => HexEncoding.ToLowerHexString(null!));
    }
}
