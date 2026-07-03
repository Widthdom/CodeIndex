using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using BclMatch = System.Text.RegularExpressions.Match;
using BclRegex = System.Text.RegularExpressions.Regex;

namespace CodeIndex.Indexer;

internal sealed class BoundedRegex : BclRegex
{
    // Keep regex matches bounded, but leave enough scheduler headroom for full-suite CI contention.
    internal static readonly TimeSpan DefaultMatchTimeout = TimeSpan.FromSeconds(2);
    private const int MaxCapturedTimeoutDiagnostics = 8;
    private static readonly AsyncLocal<RegexTimeoutCaptureScope?> TimeoutCaptureScope = new();

    internal readonly record struct RegexTimeoutDiagnostic(
        string Operation,
        string PatternHash,
        int PatternLength,
        double TimeoutMs);

    internal sealed class RegexTimeoutCaptureScope : IDisposable
    {
        private readonly RegexTimeoutCaptureScope? _previous;
        private readonly List<RegexTimeoutDiagnostic> _diagnostics = [];

        internal RegexTimeoutCaptureScope(string? language, string patternFamily)
        {
            _previous = TimeoutCaptureScope.Value;
            Language = string.IsNullOrWhiteSpace(language) ? "unknown" : language;
            PatternFamily = patternFamily;
            TimeoutCaptureScope.Value = this;
        }

        public string Language { get; }
        public string PatternFamily { get; }
        public int TimeoutCount { get; private set; }
        public IReadOnlyList<RegexTimeoutDiagnostic> Diagnostics => _diagnostics;
        public bool DiagnosticsTruncated => TimeoutCount > _diagnostics.Count;
        public bool HasTimeouts => TimeoutCount > 0;

        internal void Record(string operation, string pattern, TimeSpan timeout)
        {
            TimeoutCount++;
            if (_diagnostics.Count >= MaxCapturedTimeoutDiagnostics)
                return;

            _diagnostics.Add(new RegexTimeoutDiagnostic(
                operation,
                HashPattern(pattern),
                pattern.Length,
                timeout.TotalMilliseconds));
        }

        public void Dispose()
        {
            if (ReferenceEquals(TimeoutCaptureScope.Value, this))
                TimeoutCaptureScope.Value = _previous;
        }
    }

    internal static RegexTimeoutCaptureScope CaptureTimeouts(string? language, string patternFamily) =>
        new(language, patternFamily);

    internal static BoundedRegex CreateExtractionRegex(string pattern, RegexOptions options) =>
        CreateExtractionRegex(pattern, options, DefaultMatchTimeout);

    internal static BoundedRegex CreateExtractionRegex(string pattern, RegexOptions options, TimeSpan matchTimeout) =>
        new(pattern, options | RegexOptions.CultureInvariant, matchTimeout);

    public BoundedRegex(string pattern)
        : base(pattern, RegexOptions.None, DefaultMatchTimeout)
    {
    }

    public BoundedRegex(string pattern, RegexOptions options)
        : base(pattern, options, DefaultMatchTimeout)
    {
    }

    public BoundedRegex(string pattern, RegexOptions options, TimeSpan matchTimeout)
        : base(pattern, options, matchTimeout)
    {
    }

    public static new string Escape(string str) => BclRegex.Escape(str);

    public static new string Unescape(string str) => BclRegex.Unescape(str);

    public static new Match Match(string input, string pattern) =>
        Match(input, pattern, RegexOptions.None);

    public static new Match Match(string input, string pattern, RegexOptions options)
    {
        try
        {
            return BclRegex.Match(input, pattern, options, DefaultMatchTimeout);
        }
        catch (RegexMatchTimeoutException ex)
        {
            RecordTimeout("match", pattern, ex);
            return BclMatch.Empty;
        }
    }

    public static BclMatch Match(BclRegex regex, string input)
    {
        try
        {
            return regex.Match(input);
        }
        catch (RegexMatchTimeoutException ex)
        {
            RecordTimeout("match", regex.ToString(), ex);
            return BclMatch.Empty;
        }
    }

    public static new MatchCollection Matches(string input, string pattern) =>
        Matches(input, pattern, RegexOptions.None);

    public static new MatchCollection Matches(string input, string pattern, RegexOptions options)
    {
        try
        {
            var matches = BclRegex.Matches(input, pattern, options, DefaultMatchTimeout);
            _ = matches.Count;
            return matches;
        }
        catch (RegexMatchTimeoutException ex)
        {
            RecordTimeout("matches", pattern, ex);
            return EmptyMatches();
        }
    }

    public static IEnumerable<BclMatch> EnumerateMatches(BclRegex regex, string input)
    {
        MatchCollection matches;
        try
        {
            matches = regex.Matches(input);
            _ = matches.Count;
        }
        catch (RegexMatchTimeoutException ex)
        {
            RecordTimeout("matches", regex.ToString(), ex);
            yield break;
        }

        foreach (BclMatch match in matches)
            yield return match;
    }

    public static IEnumerable<BclMatch> EnumerateMatches(string input, string pattern) =>
        EnumerateMatches(input, pattern, RegexOptions.None);

    public static IEnumerable<BclMatch> EnumerateMatches(string input, string pattern, RegexOptions options)
    {
        MatchCollection matches;
        try
        {
            matches = BclRegex.Matches(input, pattern, options, DefaultMatchTimeout);
            _ = matches.Count;
        }
        catch (RegexMatchTimeoutException ex)
        {
            RecordTimeout("matches", pattern, ex);
            yield break;
        }

        foreach (BclMatch match in matches)
            yield return match;
    }

    public static new bool IsMatch(string input, string pattern) =>
        IsMatch(input, pattern, RegexOptions.None);

    public static new bool IsMatch(string input, string pattern, RegexOptions options)
    {
        try
        {
            return BclRegex.IsMatch(input, pattern, options, DefaultMatchTimeout);
        }
        catch (RegexMatchTimeoutException ex)
        {
            RecordTimeout("is_match", pattern, ex);
            return false;
        }
    }

    public static new string Replace(string input, string pattern, string replacement) =>
        Replace(input, pattern, replacement, RegexOptions.None);

    public static new string Replace(string input, string pattern, string replacement, RegexOptions options)
    {
        try
        {
            return BclRegex.Replace(input, pattern, replacement, options, DefaultMatchTimeout);
        }
        catch (RegexMatchTimeoutException ex)
        {
            RecordTimeout("replace", pattern, ex);
            return input;
        }
    }

    public static new string Replace(string input, string pattern, MatchEvaluator evaluator) =>
        Replace(input, pattern, evaluator, RegexOptions.None);

    public static new string Replace(string input, string pattern, MatchEvaluator evaluator, RegexOptions options)
    {
        try
        {
            return BclRegex.Replace(input, pattern, evaluator, options, DefaultMatchTimeout);
        }
        catch (RegexMatchTimeoutException ex)
        {
            RecordTimeout("replace", pattern, ex);
            return input;
        }
    }

    public new Match Match(string input)
    {
        try
        {
            return base.Match(input);
        }
        catch (RegexMatchTimeoutException ex)
        {
            RecordTimeout("match", ToString(), ex);
            return BclMatch.Empty;
        }
    }

    public new Match Match(string input, int startat)
    {
        try
        {
            return base.Match(input, startat);
        }
        catch (RegexMatchTimeoutException ex)
        {
            RecordTimeout("match", ToString(), ex);
            return BclMatch.Empty;
        }
    }

    public new Match Match(string input, int beginning, int length)
    {
        try
        {
            return base.Match(input, beginning, length);
        }
        catch (RegexMatchTimeoutException ex)
        {
            RecordTimeout("match", ToString(), ex);
            return BclMatch.Empty;
        }
    }

    public new MatchCollection Matches(string input)
    {
        try
        {
            var matches = base.Matches(input);
            _ = matches.Count;
            return matches;
        }
        catch (RegexMatchTimeoutException ex)
        {
            RecordTimeout("matches", ToString(), ex);
            return EmptyMatches();
        }
    }

    public new MatchCollection Matches(string input, int startat)
    {
        try
        {
            var matches = base.Matches(input, startat);
            _ = matches.Count;
            return matches;
        }
        catch (RegexMatchTimeoutException ex)
        {
            RecordTimeout("matches", ToString(), ex);
            return EmptyMatches();
        }
    }

    public new bool IsMatch(string input)
    {
        try
        {
            return base.IsMatch(input);
        }
        catch (RegexMatchTimeoutException ex)
        {
            RecordTimeout("is_match", ToString(), ex);
            return false;
        }
    }

    public new bool IsMatch(string input, int startat)
    {
        try
        {
            return base.IsMatch(input, startat);
        }
        catch (RegexMatchTimeoutException ex)
        {
            RecordTimeout("is_match", ToString(), ex);
            return false;
        }
    }

    public new string Replace(string input, string replacement)
    {
        try
        {
            return base.Replace(input, replacement);
        }
        catch (RegexMatchTimeoutException ex)
        {
            RecordTimeout("replace", ToString(), ex);
            return input;
        }
    }

    public new string Replace(string input, MatchEvaluator evaluator)
    {
        try
        {
            return base.Replace(input, evaluator);
        }
        catch (RegexMatchTimeoutException ex)
        {
            RecordTimeout("replace", ToString(), ex);
            return input;
        }
    }

    private static void RecordTimeout(string operation, string pattern, RegexMatchTimeoutException ex) =>
        TimeoutCaptureScope.Value?.Record(operation, pattern, ex.MatchTimeout);

    private static string HashPattern(string pattern)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(pattern));
        return HexEncoding.ToLowerHexString(hash, 0, 8);
    }

    private static MatchCollection EmptyMatches() =>
        BclRegex.Matches(string.Empty, @"\b\B", RegexOptions.None, DefaultMatchTimeout);
}
