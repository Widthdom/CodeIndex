using System.Text.RegularExpressions;
using CodeIndex.Cli;
using CodeIndex.Diagnostics;
using CodeIndex.Models;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Indexer.Extensibility;

internal sealed class ConfiguredSymbolExtractor(
    string language,
    IReadOnlyCollection<string> fileExtensions,
    IReadOnlyList<ConfiguredSymbolExtractor.PatternRule> patterns) : ISymbolExtractor
{
    private readonly object timeoutGate = new();
    private readonly HashSet<PatternRule> disabledTimeoutPatterns = [];
    private readonly HashSet<string> timeoutWarnings = new(StringComparer.Ordinal);

    internal sealed record PatternRule(string Kind, Regex Regex, string SourcePath = "");

    public string Language { get; } = language;

    public IReadOnlyCollection<string> FileExtensions { get; } = fileExtensions;

    internal IReadOnlyList<PatternRule> PatternsForTests => patterns;

    public IReadOnlyList<SymbolRecord> Extract(long fileId, string source, ExtractionContext context)
    {
        var symbols = new List<SymbolRecord>();
        var lineNumber = 0;
        foreach (var lineMemory in EnumerateNormalizedLines(source))
        {
            lineNumber++;
            var line = lineMemory.ToString();
            foreach (var pattern in patterns)
            {
                if (IsPatternDisabled(pattern))
                    continue;

                Match match;
                try
                {
                    using var regexTimeouts = Regex.CaptureTimeouts(Language, "configured_symbol_extraction");
                    match = pattern.Regex.Match(line);
                    if (regexTimeouts.HasTimeouts)
                    {
                        DisablePatternAfterTimeout(pattern);
                        continue;
                    }
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

    private static IEnumerable<ReadOnlyMemory<char>> EnumerateNormalizedLines(string source)
    {
        var lineStart = 0;
        for (var i = 0; i < source.Length; i++)
        {
            var ch = source[i];
            if (ch is not ('\r' or '\n'))
                continue;

            yield return source.AsMemory(lineStart, i - lineStart);
            if (ch == '\r' && i + 1 < source.Length && source[i + 1] == '\n')
                i++;
            lineStart = i + 1;
        }

        yield return source.AsMemory(lineStart);
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

        ExtractorPluginRegistry.ReportPatternExtractorTimeout(pattern.SourcePath, Language, pattern.Kind);
        CommandErrorWriter.WriteStderr(
            RegexTimeoutPolicy.FormatConfiguredPatternTimeout(Language, pattern.Kind, ExtractorPluginRegistry.PatternRegexTimeout));
    }
}
