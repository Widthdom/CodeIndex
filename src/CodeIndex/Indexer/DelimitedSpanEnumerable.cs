namespace CodeIndex.Indexer;

internal readonly ref struct DelimitedSpanEnumerable
{
    private readonly ReadOnlySpan<char> _value;
    private readonly char _delimiter;
    private readonly bool _trimEntries;
    private readonly bool _removeEmptyEntries;

    internal DelimitedSpanEnumerable(
        ReadOnlySpan<char> value,
        char delimiter,
        bool trimEntries = false,
        bool removeEmptyEntries = false)
    {
        _value = value;
        _delimiter = delimiter;
        _trimEntries = trimEntries;
        _removeEmptyEntries = removeEmptyEntries;
    }

    public Enumerator GetEnumerator() =>
        new(_value, _delimiter, _trimEntries, _removeEmptyEntries);

    internal ref struct Enumerator
    {
        private readonly ReadOnlySpan<char> _value;
        private readonly char _delimiter;
        private readonly bool _trimEntries;
        private readonly bool _removeEmptyEntries;
        private int _nextStart;
        private bool _finished;

        internal Enumerator(
            ReadOnlySpan<char> value,
            char delimiter,
            bool trimEntries,
            bool removeEmptyEntries)
        {
            _value = value;
            _delimiter = delimiter;
            _trimEntries = trimEntries;
            _removeEmptyEntries = removeEmptyEntries;
            _nextStart = 0;
            _finished = false;
            Current = default;
            CurrentStart = 0;
        }

        public ReadOnlySpan<char> Current { get; private set; }
        public int CurrentStart { get; private set; }

        public bool MoveNext()
        {
            while (!_finished)
            {
                var start = _nextStart;
                var relativeEnd = _value[start..].IndexOf(_delimiter);
                var end = relativeEnd < 0
                    ? _value.Length
                    : start + relativeEnd;
                _finished = relativeEnd < 0;
                _nextStart = end + 1;

                var entry = _value[start..end];
                if (_trimEntries)
                {
                    var trimmed = entry.Trim();
                    start += entry.Length - entry.TrimStart().Length;
                    entry = trimmed;
                }

                if (_removeEmptyEntries && entry.IsEmpty)
                    continue;

                Current = entry;
                CurrentStart = start;
                return true;
            }

            return false;
        }
    }
}
