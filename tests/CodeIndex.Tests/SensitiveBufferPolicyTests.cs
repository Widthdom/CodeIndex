using CodeIndex.Security;

namespace CodeIndex.Tests;

public class SensitiveBufferPolicyTests
{
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1024, 1024)]
    [InlineData(
        SensitiveBufferPolicy.GeneratedJsonCaptureInitialCapacityBytes + 1,
        SensitiveBufferPolicy.GeneratedJsonCaptureInitialCapacityBytes)]
    public void GetBoundedGeneratedJsonInitialCapacity_ClampsToPolicyBudget_Issue4000(
        int maxBytes,
        int expected)
    {
        Assert.Equal(expected, SensitiveBufferPolicy.GetBoundedGeneratedJsonInitialCapacity(maxBytes));
    }

    [Fact]
    public void ClearUsedSensitiveBytes_ClearsOnlyUsedRange_Issue4000()
    {
        var buffer = new byte[] { 1, 2, 3, 4 };

        SensitiveBufferPolicy.ClearUsedSensitiveBytes(buffer, usedBytes: 2);

        Assert.Equal(new byte[] { 0, 0, 3, 4 }, buffer);
    }

    [Fact]
    public void ReturnSensitivePayloadBuffer_ClearsDirectBufferWithoutRequiringPoolReturn_Issue4000()
    {
        var buffer = new byte[] { 1, 2, 3, 4 };

        SensitiveBufferPolicy.ReturnSensitivePayloadBuffer(buffer, usedBytes: 99, rented: false);

        Assert.Equal(new byte[] { 0, 0, 0, 0 }, buffer);
    }
}
