using System.Globalization;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

// Regression lock for #1543: CLI numeric flag parsing must run under
// CultureInfo.InvariantCulture so the same `cdidx --limit X` command behaves
// identically on machines with different runtime cultures (and stays stable
// across future CLR locale-aware changes). The test installs a quirky current
// culture whose NegativeSign is U+2212 ("−") instead of ASCII "-" — under the
// old culture-leaking code that custom NegativeSign would alter how int.TryParse
// reads dashes from CLI tokens; with InvariantCulture pinned, the parser must
// stay anchored on the ASCII contract regardless of LANG.
// #1543 の回帰ロック: CLI の数値フラグ parsing は CultureInfo.InvariantCulture に
// 固定し、ロケールが異なるマシンでも `cdidx --limit X` が同一に振る舞う（将来の
// CLR ロケール対応変更にも耐える）こと。テストは NegativeSign を U+2212 ("−") に
// 差し替えた quirky culture を CurrentCulture に設定する。culture-leak していた旧
// コードでは int.TryParse のダッシュ解釈がこの custom NegativeSign に引きずられる
// が、InvariantCulture 固定後は LANG に依存せず ASCII 契約に留まる。
public class QueryCommandRunnerInvariantCultureTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("tr-TR")]
    [InlineData("fa-IR")]
    public void ParseArgs_NumericFlagsParseIdenticallyAcrossCultures(string cultureName)
    {
        using var _ = new CultureScope(new CultureInfo(cultureName));

        var options = QueryCommandRunner.ParseArgs(
            [
                "RunSearch",
                "--limit", "7",
                "--before", "2",
                "--after", "3",
                "--max-line-width", "77",
            ],
            jsonDefault: false,
            allowNamedQuery: true);

        Assert.Null(options.ParseError);
        Assert.Equal(7, options.Limit);
        Assert.Equal(2, options.ContextBefore);
        Assert.Equal(3, options.ContextAfter);
        Assert.Equal(77, options.MaxLineWidth);
    }

    [Fact]
    public void ParseArgs_NegativeLimitRejectedUnderQuirkyNegativeSignCulture()
    {
        using var _ = new CultureScope(BuildQuirkyNegativeSignCulture());

        var options = QueryCommandRunner.ParseArgs(
            ["RunSearch", "--limit", "-5"],
            jsonDefault: false,
            allowNamedQuery: true);

        Assert.NotNull(options.ParseError);
        Assert.Contains("--limit requires a positive integer", options.ParseError);
        Assert.Contains("got '-5'", options.ParseError);
    }

    [Fact]
    public void ParseArgs_AsciiHyphenInBeforeFlagRejectedUnderQuirkyNegativeSignCulture()
    {
        using var _ = new CultureScope(BuildQuirkyNegativeSignCulture());

        var options = QueryCommandRunner.ParseArgs(
            ["RunSearch", "--before", "-1"],
            jsonDefault: false,
            allowNamedQuery: true);

        Assert.NotNull(options.ParseError);
        Assert.Contains("--before requires a non-negative integer", options.ParseError);
        Assert.Contains("got '-1'", options.ParseError);
    }

    [Fact]
    public void ParseArgs_QuirkyNegativeSignCulturePreservesPositiveLimitParse()
    {
        // Positive ASCII digits must parse to the same integer regardless of the
        // current culture's NegativeSign. This guards the "no drift between users"
        // contract from #1543 for the most common positive-flag case.
        // 正の ASCII 数字は、CurrentCulture の NegativeSign が何であれ同じ整数として
        // parse されなければならない。これは #1543 の "ユーザー間で挙動が drift しない"
        // 契約の中で最も一般的な正の値ケースを担保する。
        using var _ = new CultureScope(BuildQuirkyNegativeSignCulture());

        var options = QueryCommandRunner.ParseArgs(
            ["RunSearch", "--limit", "42", "--max-line-width", "0"],
            jsonDefault: false,
            allowNamedQuery: true);

        Assert.Null(options.ParseError);
        Assert.Equal(42, options.Limit);
        Assert.Equal(0, options.MaxLineWidth);
    }

    private static CultureInfo BuildQuirkyNegativeSignCulture()
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NegativeSign = "−";
        culture.NumberFormat.PositiveSign = "➕";
        return culture;
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _originalCulture;
        private readonly CultureInfo _originalUiCulture;

        public CultureScope(CultureInfo culture)
        {
            _originalCulture = CultureInfo.CurrentCulture;
            _originalUiCulture = CultureInfo.CurrentUICulture;
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = _originalCulture;
            CultureInfo.CurrentUICulture = _originalUiCulture;
        }
    }
}
