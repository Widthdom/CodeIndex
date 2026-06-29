using System.Text;

namespace CodeIndex.Lsp;

internal sealed class LspLiveDocumentStore
{
    private readonly Dictionary<string, string> _documents;
    private readonly Dictionary<string, int> _documentByteCounts;
    private readonly List<string> _documentOrder = [];
    private readonly StringComparison _keyComparison;
    private readonly int _maxDocuments;
    private readonly int _maxDocumentBytes;
    private readonly int _maxLiveBytes;
    private long _documentBytes;
    private long _evictionCount;
    private long _evictedBytes;

    internal LspLiveDocumentStore(StringComparer comparer, StringComparison keyComparison, int maxDocuments, int maxDocumentBytes, int maxLiveBytes)
    {
        _documents = new Dictionary<string, string>(comparer);
        _documentByteCounts = new Dictionary<string, int>(comparer);
        _keyComparison = keyComparison;
        _maxDocuments = maxDocuments;
        _maxDocumentBytes = maxDocumentBytes;
        _maxLiveBytes = maxLiveBytes;
    }

    internal long Bytes => _documentBytes;

    internal long EvictionCount => _evictionCount;

    internal long EvictedBytes => _evictedBytes;

    internal void SetText(string key, string text)
    {
        var textBytes = Encoding.UTF8.GetByteCount(text);
        if (textBytes > _maxDocumentBytes || textBytes > _maxLiveBytes)
        {
            Remove(key);
            return;
        }

        if (!_documents.ContainsKey(key))
            _documentOrder.Add(key);
        else if (_documentByteCounts.TryGetValue(key, out var previousBytes))
            _documentBytes = Math.Max(0, _documentBytes - previousBytes);

        _documents[key] = text;
        _documentByteCounts[key] = textBytes;
        _documentBytes += textBytes;
        EnsureCapacity();
    }

    internal bool TryGetText(string key, out string text) => _documents.TryGetValue(key, out text!);

    internal void Remove(string key, bool recordEviction = false)
    {
        if (_documentByteCounts.Remove(key, out var bytes))
        {
            _documentBytes = Math.Max(0, _documentBytes - bytes);
            if (recordEviction)
            {
                _evictionCount++;
                _evictedBytes += bytes;
            }
        }

        _documents.Remove(key);
        _documentOrder.RemoveAll(existing => string.Equals(existing, key, _keyComparison));
    }

    private void EnsureCapacity()
    {
        while ((_documents.Count > _maxDocuments || _documentBytes > _maxLiveBytes)
            && _documentOrder.Count > 0)
        {
            var oldestKey = _documentOrder[0];
            Remove(oldestKey, recordEviction: true);
        }

        if (_documents.Count > _maxDocuments || _documentBytes > _maxLiveBytes)
        {
            _documents.Clear();
            _documentByteCounts.Clear();
            _documentOrder.Clear();
            _documentBytes = 0;
        }
    }
}
