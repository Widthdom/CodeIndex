using System.Text.RegularExpressions;
using BclMatch = System.Text.RegularExpressions.Match;
using BclRegex = System.Text.RegularExpressions.Regex;

namespace CodeIndex.Indexer;

internal sealed class BoundedRegex : BclRegex
{
    internal static readonly TimeSpan DefaultMatchTimeout = TimeSpan.FromMilliseconds(250);

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
        catch (RegexMatchTimeoutException)
        {
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
        catch (RegexMatchTimeoutException)
        {
            return EmptyMatches();
        }
    }

    public static new bool IsMatch(string input, string pattern) =>
        IsMatch(input, pattern, RegexOptions.None);

    public static new bool IsMatch(string input, string pattern, RegexOptions options)
    {
        try
        {
            return BclRegex.IsMatch(input, pattern, options, DefaultMatchTimeout);
        }
        catch (RegexMatchTimeoutException)
        {
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
        catch (RegexMatchTimeoutException)
        {
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
        catch (RegexMatchTimeoutException)
        {
            return input;
        }
    }

    public new Match Match(string input)
    {
        try
        {
            return base.Match(input);
        }
        catch (RegexMatchTimeoutException)
        {
            return BclMatch.Empty;
        }
    }

    public new Match Match(string input, int startat)
    {
        try
        {
            return base.Match(input, startat);
        }
        catch (RegexMatchTimeoutException)
        {
            return BclMatch.Empty;
        }
    }

    public new Match Match(string input, int beginning, int length)
    {
        try
        {
            return base.Match(input, beginning, length);
        }
        catch (RegexMatchTimeoutException)
        {
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
        catch (RegexMatchTimeoutException)
        {
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
        catch (RegexMatchTimeoutException)
        {
            return EmptyMatches();
        }
    }

    public new bool IsMatch(string input)
    {
        try
        {
            return base.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    public new bool IsMatch(string input, int startat)
    {
        try
        {
            return base.IsMatch(input, startat);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    public new string Replace(string input, string replacement)
    {
        try
        {
            return base.Replace(input, replacement);
        }
        catch (RegexMatchTimeoutException)
        {
            return input;
        }
    }

    public new string Replace(string input, MatchEvaluator evaluator)
    {
        try
        {
            return base.Replace(input, evaluator);
        }
        catch (RegexMatchTimeoutException)
        {
            return input;
        }
    }

    private static MatchCollection EmptyMatches() =>
        BclRegex.Matches(string.Empty, @"\b\B", RegexOptions.None, DefaultMatchTimeout);
}
