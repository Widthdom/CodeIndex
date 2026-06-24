using System.Diagnostics;

namespace CodeIndex.Diagnostics;

internal readonly struct ProcessMemorySnapshot
{
    private ProcessMemorySnapshot(
        long heapBytes,
        long totalAllocatedBytes,
        long gcHeapSizeBytes,
        long fragmentedBytes,
        long workingSetBytes,
        long privateBytes,
        int gen0Collections,
        int gen1Collections,
        int gen2Collections)
    {
        HeapBytes = heapBytes;
        TotalAllocatedBytes = totalAllocatedBytes;
        GcHeapSizeBytes = gcHeapSizeBytes;
        FragmentedBytes = fragmentedBytes;
        WorkingSetBytes = workingSetBytes;
        PrivateBytes = privateBytes;
        Gen0Collections = gen0Collections;
        Gen1Collections = gen1Collections;
        Gen2Collections = gen2Collections;
    }

    public long HeapBytes { get; }
    public long TotalAllocatedBytes { get; }
    public long GcHeapSizeBytes { get; }
    public long FragmentedBytes { get; }
    public long WorkingSetBytes { get; }
    public long PrivateBytes { get; }
    public int Gen0Collections { get; }
    public int Gen1Collections { get; }
    public int Gen2Collections { get; }

    public static ProcessMemorySnapshot Capture()
    {
        var gcInfo = GC.GetGCMemoryInfo();
        using var process = Process.GetCurrentProcess();
        return new ProcessMemorySnapshot(
            GC.GetTotalMemory(forceFullCollection: false),
            GC.GetTotalAllocatedBytes(),
            gcInfo.HeapSizeBytes,
            gcInfo.FragmentedBytes,
            process.WorkingSet64,
            process.PrivateMemorySize64,
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2));
    }
}
