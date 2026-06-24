using CodeIndex.Diagnostics;

namespace CodeIndex.Tests;

public class ProcessMemorySnapshotTests
{
    [Fact]
    public void Capture_ReturnsNonNegativeMetrics_Issue3988()
    {
        var snapshot = ProcessMemorySnapshot.Capture();

        Assert.True(snapshot.HeapBytes >= 0);
        Assert.True(snapshot.TotalAllocatedBytes >= 0);
        Assert.True(snapshot.GcHeapSizeBytes >= 0);
        Assert.True(snapshot.FragmentedBytes >= 0);
        Assert.True(snapshot.WorkingSetBytes > 0);
        Assert.True(snapshot.PrivateBytes >= 0);
        Assert.True(snapshot.Gen0Collections >= 0);
        Assert.True(snapshot.Gen1Collections >= 0);
        Assert.True(snapshot.Gen2Collections >= 0);
    }
}
