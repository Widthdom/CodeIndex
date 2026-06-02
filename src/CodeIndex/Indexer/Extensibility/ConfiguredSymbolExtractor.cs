using System.Text.RegularExpressions;
using CodeIndex.Models;

namespace CodeIndex.Indexer.Extensibility;

internal sealed class ConfiguredSymbolExtractor(
    string language,
    IReadOnlyCollection<string> fileExtensions,
    IReadOnlyList<ConfiguredSymbolExtractor.PatternRule> patterns) : ISymbolExtractor
{
    private readonly object timeoutGate = new();
    private readonly HashSet<PatternRule> disabledTimeoutPatterns = [];
    private readonly HashSet<string> timeoutWarnings = new(StringComparer.Ordinal);

    internal sealed record PatternRule(string Kind, Regex Regex);

    public string Language { get; } = language;

    public IReadOnlyCollection<string> FileExtensions { get; } = fileExtensions;

    public IReadOnlyList<SymbolRecord> Extract(long fileId, string source, ExtractionContext context)
    {
        var symbols = new List<SymbolRecord>();
        var lineNumber = 0;
        foreach (var line in source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            lineNumber++;
            foreach (var pattern in patterns)
            {
                if (IsPatternDisabled(pattern))
                    continue;

                Match match;
                try
                {
                    match = pattern.Regex.Match(line);
                }
                catch (RegexMatchTimeoutException)
                {
                    DisablePatternAfterTimeout(pattern);
                    continue;
                }

                if (!match.Success)
                    continue;

                var name = match.Groups["name"].Success ? match.Groups["name"].Value : match.Value.Trim();
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                symbols.Add(new SymbolRecord
                {
                    FileId = fileId,
                    Kind = pattern.Kind,
                    Name = name,
                    Line = lineNumber,
                    StartLine = lineNumber,
                    EndLine = lineNumber,
                    Signature = line.Trim(),
                });
                break;
            }
        }

        return symbols;
    }

    private bool IsPatternDisabled(PatternRule pattern)
    {
        lock (timeoutGate)
            return disabledTimeoutPatterns.Contains(pattern);
    }

    private void DisablePatternAfterTimeout(PatternRule pattern)
    {
        var shouldReport = false;
        lock (timeoutGate)
        {
            disabledTimeoutPatterns.Add(pattern);
            shouldReport = timeoutWarnings.Add(pattern.Kind + "\0" + pattern.Regex);
        }

        if (!shouldReport)
            return;

        Console.Error.WriteLine(
            $"[cdidx] Pattern extractor for language '{Language}' kind '{pattern.Kind}' timed out after {(int)ExtractorPluginRegistry.PatternRegexTimeout.TotalMilliseconds}ms; skipped this pattern.");
    }
}
