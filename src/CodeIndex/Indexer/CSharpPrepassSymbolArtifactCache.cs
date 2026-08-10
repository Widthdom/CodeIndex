using System.Collections.Concurrent;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal sealed class CSharpPrepassSymbolArtifactCache
{
    internal const int DefaultMaxFiles = 4_096;
    internal const int DefaultMaxSymbols = 131_072;
    internal const long DefaultMaxEstimatedBytes = 32L * 1024 * 1024;

    private const long EstimatedArtifactBytes = 128;
    private const long EstimatedSymbolBytes = 256;
    private static readonly AsyncLocal<Action<CSharpPrepassSymbolArtifactCacheEvent>?>
        ScopedEventForTesting = new();

    private readonly ConcurrentDictionary<string, CSharpPrepassSymbolArtifact> _artifacts =
        new(StringComparer.Ordinal);
    private readonly object _admissionLock = new();
    private readonly int _maxFiles;
    private readonly int _maxSymbols;
    private readonly long _maxEstimatedBytes;
    private int _admittedFiles;
    private int _admittedSymbols;
    private long _admittedEstimatedBytes;

    internal CSharpPrepassSymbolArtifactCache(
        int maxFiles = DefaultMaxFiles,
        int maxSymbols = DefaultMaxSymbols,
        long maxEstimatedBytes = DefaultMaxEstimatedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFiles, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSymbols, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEstimatedBytes, 1);
        _maxFiles = maxFiles;
        _maxSymbols = maxSymbols;
        _maxEstimatedBytes = maxEstimatedBytes;
    }

    internal static Action<CSharpPrepassSymbolArtifactCacheEvent>? EventForTesting
    {
        get => ScopedEventForTesting.Value;
        set => ScopedEventForTesting.Value = value;
    }

    internal int Count => _artifacts.Count;
    internal int AdmittedFileCount => Volatile.Read(ref _admittedFiles);
    internal int AdmittedSymbolCount => Volatile.Read(ref _admittedSymbols);
    internal long AdmittedEstimatedBytes => Interlocked.Read(ref _admittedEstimatedBytes);

    internal static CSharpPrepassSymbolArtifactCache? CreateForFreshBuiltInExtraction(
        bool enabled)
        => enabled
            ? new CSharpPrepassSymbolArtifactCache()
            : null;

    internal bool TryAdmit(
        string path,
        string checksum,
        IReadOnlyList<SymbolRecord> symbols,
        bool hadRegexTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(checksum);
        ArgumentNullException.ThrowIfNull(symbols);
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedPath = FileIndexer.NormalizeIndexPath(path);
        if (hadRegexTimeout)
        {
            Report("regex_timeout_skipped", normalizedPath);
            return false;
        }
        var estimatedBytes = EstimateBytes(normalizedPath, checksum, symbols.Count);

        lock (_admissionLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_artifacts.ContainsKey(normalizedPath))
                return false;

            if (_admittedFiles >= _maxFiles
                || symbols.Count > _maxSymbols - _admittedSymbols
                || estimatedBytes > _maxEstimatedBytes - _admittedEstimatedBytes)
            {
                Report("capacity_skipped", normalizedPath);
                return false;
            }

            var clonedSymbols = new List<SymbolRecord>(symbols.Count);
            foreach (var symbol in symbols)
            {
                cancellationToken.ThrowIfCancellationRequested();
                clonedSymbols.Add(PostExtractionHookMutationMaterializer.CloneSymbol(symbol));
            }

            var artifact = new CSharpPrepassSymbolArtifact(
                normalizedPath,
                checksum,
                clonedSymbols);
            if (!_artifacts.TryAdd(normalizedPath, artifact))
                return false;

            _admittedFiles++;
            _admittedSymbols += symbols.Count;
            _admittedEstimatedBytes += estimatedBytes;
            Report("admitted", normalizedPath);
            return true;
        }
    }

    internal bool TryTake(
        string path,
        string checksum,
        out CSharpPrepassSymbolArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(checksum);
        var normalizedPath = FileIndexer.NormalizeIndexPath(path);
        if (!_artifacts.TryRemove(normalizedPath, out artifact!))
            return false;

        if (!string.Equals(artifact.Checksum, checksum, StringComparison.Ordinal))
        {
            artifact = null!;
            Report("checksum_mismatch", normalizedPath);
            return false;
        }

        Report("taken", normalizedPath);
        return true;
    }

    internal void Clear()
    {
        lock (_admissionLock)
        {
            _artifacts.Clear();
            _admittedFiles = 0;
            _admittedSymbols = 0;
            _admittedEstimatedBytes = 0;
        }
        Report("cleared", string.Empty);
    }

    private static long EstimateBytes(
        string path,
        string checksum,
        int symbolCount)
    {
        return EstimatedArtifactBytes
            + (path.Length * sizeof(char))
            + (checksum.Length * sizeof(char))
            + (symbolCount * EstimatedSymbolBytes);
    }

    private static void Report(string phase, string path)
        => ScopedEventForTesting.Value?.Invoke(
            new CSharpPrepassSymbolArtifactCacheEvent(phase, path));
}

internal sealed record CSharpPrepassSymbolArtifact(
    string Path,
    string Checksum,
    List<SymbolRecord> Symbols);

internal readonly record struct CSharpPrepassSymbolArtifactCacheEvent(
    string Phase,
    string Path);
