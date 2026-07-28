using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private sealed class SymbolExtractionState
    {
        private readonly int _initialCapacity;
        private SymbolAddState? _symbolAddState;
        private SymbolLineIdentityState? _symbolLineIdentityState;

        public SymbolExtractionState(int initialCapacity = 0)
        {
            _initialCapacity = initialCapacity;
        }

        public static SymbolExtractionState FromSymbols(List<SymbolRecord> symbols)
        {
            var state = new SymbolExtractionState(symbols.Count);
            foreach (var symbol in symbols)
                state.Record(symbol);
            return state;
        }

        public int GetExactDuplicateCount(SymbolRecord symbol) =>
            _symbolAddState?.GetExactDuplicateCount(symbol) ?? 0;

        public int? GetSameLineSignatureOccurrenceIndex(SymbolRecord symbol) =>
            _symbolAddState?.GetSameLineSignatureOccurrenceIndex(symbol)
            ?? (TryGetSameLineSignatureKey(symbol, out _) ? 0 : null);

        public bool HasSymbolLineIdentity(List<SymbolRecord> symbols, SymbolLineIdentity identity)
        {
            if (symbols.Count == 0)
                return false;

            return (_symbolLineIdentityState ??= new(_initialCapacity)).Contains(symbols, identity);
        }

        public void Record(SymbolRecord symbol) =>
            (_symbolAddState ??= new(_initialCapacity)).Record(symbol);

        public void Remove(SymbolRecord symbol) =>
            _symbolAddState?.Remove(symbol);
    }

    private sealed class SymbolExtractionList : List<SymbolRecord>
    {
        private readonly int? _maxSymbols;

        public SymbolExtractionList(int initialCapacity, int? maxSymbols = null)
            : base(maxSymbols.HasValue ? Math.Min(initialCapacity, maxSymbols.Value) : initialCapacity)
        {
            _maxSymbols = maxSymbols;
            ExtractionState = new SymbolExtractionState(
                maxSymbols.HasValue ? Math.Min(initialCapacity, maxSymbols.Value) : initialCapacity);
        }

        public SymbolExtractionState ExtractionState { get; }

        public bool IsAtCapacity => _maxSymbols.HasValue && Count >= _maxSymbols.Value;
    }

    private sealed class SymbolAddState
    {
        private readonly int _initialCapacity;
        private Dictionary<SymbolRecordIdentity, int>? _exactCounts;
        private Dictionary<SameLineSignatureKey, int>? _sameLineSignatureCounts;

        public SymbolAddState(int initialCapacity)
        {
            _initialCapacity = initialCapacity;
        }

        public int GetExactDuplicateCount(SymbolRecord symbol)
        {
            if (_exactCounts is null)
                return 0;

            var key = new SymbolRecordIdentity(symbol);
            return _exactCounts.TryGetValue(key, out var count) ? count : 0;
        }

        public int? GetSameLineSignatureOccurrenceIndex(SymbolRecord symbol)
        {
            if (!TryGetSameLineSignatureKey(symbol, out var key))
                return null;

            if (_sameLineSignatureCounts is null)
                return 0;

            return _sameLineSignatureCounts.TryGetValue(key, out var count) ? count : 0;
        }

        public void Record(SymbolRecord symbol)
        {
            var exactKey = new SymbolRecordIdentity(symbol);
            var exactCounts = _exactCounts ??= CreateSymbolRecordIdentityDictionary(_initialCapacity);
            exactCounts[exactKey] = exactCounts.TryGetValue(exactKey, out var exactCount)
                ? exactCount + 1
                : 1;

            if (TryGetSameLineSignatureKey(symbol, out var sameLineKey))
            {
                var sameLineSignatureCounts = _sameLineSignatureCounts ??= CreateSameLineSignatureDictionary(_initialCapacity);
                sameLineSignatureCounts[sameLineKey] = sameLineSignatureCounts.TryGetValue(sameLineKey, out var sameLineCount)
                    ? sameLineCount + 1
                    : 1;
            }
        }

        public void Remove(SymbolRecord symbol)
        {
            if (_exactCounts is not null)
            {
                var exactKey = new SymbolRecordIdentity(symbol);
                if (_exactCounts.TryGetValue(exactKey, out var exactCount))
                {
                    if (exactCount <= 1)
                        _exactCounts.Remove(exactKey);
                    else
                        _exactCounts[exactKey] = exactCount - 1;
                }
            }

            if (!TryGetSameLineSignatureKey(symbol, out var sameLineKey))
                return;

            if (_sameLineSignatureCounts is null)
                return;

            if (!_sameLineSignatureCounts.TryGetValue(sameLineKey, out var sameLineCount))
                return;

            if (sameLineCount <= 1)
                _sameLineSignatureCounts.Remove(sameLineKey);
            else
                _sameLineSignatureCounts[sameLineKey] = sameLineCount - 1;
        }
    }

    private static Dictionary<SymbolRecordIdentity, int> CreateSymbolRecordIdentityDictionary(int initialCapacity) =>
        initialCapacity == 0
            ? new Dictionary<SymbolRecordIdentity, int>()
            : new Dictionary<SymbolRecordIdentity, int>(initialCapacity);

    private static Dictionary<SameLineSignatureKey, int> CreateSameLineSignatureDictionary(int initialCapacity) =>
        initialCapacity == 0
            ? new Dictionary<SameLineSignatureKey, int>()
            : new Dictionary<SameLineSignatureKey, int>(initialCapacity);

    private readonly record struct SymbolRecordIdentity(
        string Kind,
        string Name,
        int Line,
        int StartLine,
        int? StartColumn,
        int EndLine,
        int? BodyStartLine,
        int? BodyEndLine,
        string? Signature,
        string? Visibility,
        string? ReturnType)
    {
        public SymbolRecordIdentity(SymbolRecord symbol)
            : this(
                symbol.Kind,
                symbol.Name,
                symbol.Line,
                symbol.StartLine,
                symbol.StartColumn,
                symbol.EndLine,
                symbol.BodyStartLine,
                symbol.BodyEndLine,
                symbol.Signature,
                symbol.Visibility,
                symbol.ReturnType)
        {
        }
    }

    private readonly record struct SymbolLineIdentity(long FileId, int Line, string Kind, string Name);
    private readonly record struct SymbolKindNameIdentity(string Kind, string Name);

    private sealed class SymbolLineIdentityState
    {
        private readonly HashSet<SymbolLineIdentity> _identities;
        private int _knownCount;

        public SymbolLineIdentityState(int initialCapacity)
        {
            _identities = initialCapacity == 0 ? [] : new HashSet<SymbolLineIdentity>(initialCapacity);
        }

        public bool Contains(List<SymbolRecord> symbols, SymbolLineIdentity identity)
        {
            Sync(symbols);
            return _identities.Contains(identity);
        }

        private void Sync(List<SymbolRecord> symbols)
        {
            if (_knownCount > symbols.Count)
            {
                _identities.Clear();
                _knownCount = 0;
            }

            for (; _knownCount < symbols.Count; _knownCount++)
                _identities.Add(GetSymbolLineIdentity(symbols[_knownCount]));
        }
    }

    private static HashSet<SymbolLineIdentity> BuildSymbolLineIdentities(IEnumerable<SymbolRecord> symbols, int expectedAdditionalLines = 0)
    {
        var identities = symbols is ICollection<SymbolRecord> collection
            ? new HashSet<SymbolLineIdentity>(collection.Count + EstimateSymbolListInitialCapacity(expectedAdditionalLines))
            : new HashSet<SymbolLineIdentity>();
        foreach (var symbol in symbols)
            identities.Add(GetSymbolLineIdentity(symbol));
        return identities;
    }

    private static SymbolLineIdentity GetSymbolLineIdentity(SymbolRecord symbol)
        => new(symbol.FileId, symbol.Line, symbol.Kind, symbol.Name);

    private static bool HasSymbolLineIdentity(
        HashSet<SymbolLineIdentity> identities,
        long fileId,
        int lineNumber,
        string kind,
        string name)
        => identities.Contains(new SymbolLineIdentity(fileId, lineNumber, kind, name));

    private static bool HasSymbolLineIdentity(
        SymbolExtractionState extractionState,
        List<SymbolRecord> symbols,
        long fileId,
        int lineNumber,
        string kind,
        string name)
        => extractionState.HasSymbolLineIdentity(symbols, new SymbolLineIdentity(fileId, lineNumber, kind, name));

    private static void RecordSymbolLineIdentity(HashSet<SymbolLineIdentity> identities, SymbolRecord symbol)
        => identities.Add(GetSymbolLineIdentity(symbol));

    private readonly record struct SameLineSignatureKey(int Line, int StartLine, string Signature);
}
