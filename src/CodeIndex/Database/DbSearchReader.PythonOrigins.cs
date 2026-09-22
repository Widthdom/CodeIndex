namespace CodeIndex.Database;

public partial class DbReader
{
    private void AttachPythonOriginLines(List<SearchResult> results, CancellationToken cancellationToken = default)
    {
        // Share bounded indexed evidence across occurrences; never retain source across queries.
        foreach (var group in results.Where(r => string.Equals(r.Lang, "python", StringComparison.OrdinalIgnoreCase))
                     .GroupBy(r => r.Path, StringComparer.Ordinal))
        {
            var generation = ReadOriginGeneration();
            var token = cancellationToken.CanBeCanceled ? cancellationToken : _cancellation;
            var retainedLines = new Dictionary<int, string>();
            var conflictingContext = false;
            var origins = new SearchMatchClassifier.PythonOriginContext(start =>
            {
                var window = ReadCSharpOriginWindow(group.Key, start, generation, retainedLines, token);
                conflictingContext |= window.StopReason == "indexed_text_mismatch";
                return window;
            }, OriginPasses, token);
            var invalidReason = ReadOriginGeneration() != generation ? "indexed_generation_changed"
                : conflictingContext ? "indexed_text_mismatch" : null;
            if (invalidReason is not null)
                origins = new SearchMatchClassifier.PythonOriginContext(
                    _ => new(new Dictionary<int, string>(), invalidReason), 1, token);
            foreach (var result in group)
                result.PythonOrigins = origins;
        }
    }
}
