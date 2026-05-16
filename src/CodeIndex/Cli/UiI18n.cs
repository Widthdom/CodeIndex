using System.Globalization;

namespace CodeIndex.Cli;

/// <summary>
/// User-facing language for CLI message output. Drives <see cref="UiMessages"/> lookups.
/// CLI 出力のユーザー向け言語。<see cref="UiMessages"/> のルックアップを切り替える。
/// </summary>
public enum UiLanguage
{
    English = 0,
    Japanese = 1,
    Both = 2,
}

/// <summary>
/// Resolves which <see cref="UiLanguage"/> to use for user-facing strings.
/// Precedence: <c>CDIDX_LANG</c> env (en/ja/both) > <see cref="CultureInfo.CurrentUICulture"/>
/// (ja-* → Japanese) > English fallback.
/// 解決順: <c>CDIDX_LANG</c> 環境変数 &gt; <see cref="CultureInfo.CurrentUICulture"/>
/// (ja-* → Japanese) &gt; 英語フォールバック。
/// </summary>
public static class UiLanguageResolver
{
    public const string EnvVarName = "CDIDX_LANG";

    public static UiLanguage Resolve(string? envOverride = null, CultureInfo? cultureOverride = null)
    {
        var raw = envOverride ?? Environment.GetEnvironmentVariable(EnvVarName);
        if (TryParse(raw, out var parsed))
            return parsed;
        var culture = cultureOverride ?? CultureInfo.CurrentUICulture;
        return IsJapaneseCulture(culture) ? UiLanguage.Japanese : UiLanguage.English;
    }

    public static bool TryParse(string? raw, out UiLanguage lang)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case "en":
            case "en-us":
            case "english":
                lang = UiLanguage.English;
                return true;
            case "ja":
            case "jp":
            case "ja-jp":
            case "japanese":
                lang = UiLanguage.Japanese;
                return true;
            case "both":
            case "bilingual":
            case "en+ja":
            case "ja+en":
                lang = UiLanguage.Both;
                return true;
            default:
                lang = UiLanguage.English;
                return false;
        }
    }

    private static bool IsJapaneseCulture(CultureInfo culture)
    {
        return culture.TwoLetterISOLanguageName.Equals("ja", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Bilingual user-facing message catalog. Each entry stores English and Japanese
/// side-by-side so translations stay co-located and cannot drift independently.
/// Adding a third language only requires extending <see cref="Pair"/> and
/// <see cref="UiLanguage"/>, not editing every call site.
/// 二言語ユーザー向けメッセージカタログ。英語と日本語を同じ場所に置き、翻訳の
/// ドリフトを防ぐ。第三言語の追加もカタログ拡張だけで済む。
/// </summary>
public static class UiMessages
{
    public sealed record Pair(string English, string Japanese);

    public static readonly Pair EasterEggSushi = new(
        English: "\U0001f363 Indexing is like making sushi — patience yields perfection.",
        Japanese: "   インデックスは寿司作りのように — 忍耐が完璧を生む。");

    public static readonly Pair EasterEggCoffee = new(
        English: "☕ Leave the indexing to me and go grab a coffee!",
        Japanese: "   インデックスは任せて、コーヒーでも飲んできて！");

    public static readonly Pair EasterEggRamen = new(
        English: "\U0001f35c Indexing in progress... perfect time for a bowl of ramen!",
        Japanese: "   インデックス中…ラーメン一杯いかが？");

    public static readonly Pair EasterEggWine = new(
        English: "\U0001f377 Crushing... Aging... Pouring... Santé!",
        Japanese: "   インデックスはワインのように—熟成を待つ価値がある。");

    public static readonly Pair EasterEggBeer = new(
        English: "\U0001f37a Tapping... Pouring... Foaming... Cheers!",
        Japanese: "   インデックス完了まで、乾杯！");

    public static readonly Pair EasterEggMatcha = new(
        English: "\U0001f375 Sifting... Pouring... Whisking... どうぞ！",
        Japanese: "   一服の抹茶でもいかがですか？");

    public static readonly Pair EasterEggWhisky = new(
        English: "\U0001f943 Mashing... Distilling... Aging... Slainte!",
        Japanese: "   インデックスはウイスキーのように—熟成が大事。");

    public static IEnumerable<string> Render(Pair pair, UiLanguage lang)
    {
        switch (lang)
        {
            case UiLanguage.Japanese:
                yield return pair.Japanese;
                break;
            case UiLanguage.Both:
                yield return pair.English;
                yield return pair.Japanese;
                break;
            default:
                yield return pair.English;
                break;
        }
    }
}
