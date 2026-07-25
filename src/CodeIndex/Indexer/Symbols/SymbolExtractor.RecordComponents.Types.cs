using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private readonly record struct RecordPrimaryComponent(
        string Name,
        string Type,
        string Signature,
        int Line,
        string? Visibility = null);

    private readonly record struct RecordPrimaryComponentSlice(
        string Text,
        int Line);

    private readonly record struct PendingRecordPrimaryComponents(
        long FileId,
        string Kind,
        string RecordName,
        int RecordStartLine,
        List<RecordPrimaryComponent> Components);

    private readonly record struct RecordPrimaryComponentParentKey(
        long FileId,
        string Kind,
        string Name,
        int StartLine);

    private readonly record struct RecordPrimaryComponentPropertyKey(
        long FileId,
        string ContainerKind,
        string ContainerName);

    private sealed class RecordPrimaryComponentParentIndex
    {
        private readonly Dictionary<RecordPrimaryComponentParentKey, SymbolRecord> _parents = [];
        private int _indexedCount;
        private SymbolRecord? _lastIndexedSymbol;

        public SymbolRecord? FindLast(
            List<SymbolRecord> symbols,
            RecordPrimaryComponentParentKey key)
        {
            Synchronize(symbols);
            return _parents.TryGetValue(key, out var parent) ? parent : null;
        }

        private void Synchronize(List<SymbolRecord> symbols)
        {
            // AddSymbolRecord can remove declaration-only functions from the list tail.
            // Rebuild if that invalidated the indexed boundary; otherwise consume only
            // symbols appended since the previous record declaration.
            // AddSymbolRecord は末尾の declaration-only function を除くことがあるため、
            // index境界が無効なら再構築し、それ以外は追加分だけを取り込む。
            if (_indexedCount > symbols.Count
                || (_indexedCount > 0
                    && !ReferenceEquals(symbols[_indexedCount - 1], _lastIndexedSymbol)))
            {
                _parents.Clear();
                _indexedCount = 0;
            }

            for (var index = _indexedCount; index < symbols.Count; index++)
            {
                var symbol = symbols[index];
                if (symbol.Kind is not ("class" or "struct" or "enum"))
                    continue;

                _parents[new RecordPrimaryComponentParentKey(
                    symbol.FileId,
                    symbol.Kind,
                    symbol.Name,
                    symbol.StartLine)] = symbol;
            }

            _indexedCount = symbols.Count;
            _lastIndexedSymbol = symbols.Count > 0 ? symbols[^1] : null;
        }
    }

    private readonly record struct StrippedRecordComponentText(
        string Text,
        int ConsumedNewlines);
}
