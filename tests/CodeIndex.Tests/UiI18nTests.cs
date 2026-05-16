using System.Globalization;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

/// <summary>
/// Tests for user-facing message catalog and language resolver introduced for #1568.
/// #1568 で導入したユーザー向けメッセージカタログと言語解決のテスト。
/// </summary>
[Collection("SQLite pool sensitive")]
public class UiI18nTests
{
    [Theory]
    [InlineData("en", UiLanguage.English)]
    [InlineData("EN", UiLanguage.English)]
    [InlineData("english", UiLanguage.English)]
    [InlineData("en-us", UiLanguage.English)]
    [InlineData("ja", UiLanguage.Japanese)]
    [InlineData("JA", UiLanguage.Japanese)]
    [InlineData("jp", UiLanguage.Japanese)]
    [InlineData("ja-JP", UiLanguage.Japanese)]
    [InlineData("japanese", UiLanguage.Japanese)]
    [InlineData("both", UiLanguage.Both)]
    [InlineData("bilingual", UiLanguage.Both)]
    [InlineData("en+ja", UiLanguage.Both)]
    [InlineData("ja+en", UiLanguage.Both)]
    [InlineData("  ja  ", UiLanguage.Japanese)]
    public void TryParse_KnownValue_ReturnsExpectedLanguage(string raw, UiLanguage expected)
    {
        Assert.True(UiLanguageResolver.TryParse(raw, out var lang));
        Assert.Equal(expected, lang);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("fr")]
    [InlineData("zh")]
    [InlineData("xx")]
    public void TryParse_UnknownOrEmpty_ReturnsFalseAndEnglishFallback(string? raw)
    {
        Assert.False(UiLanguageResolver.TryParse(raw, out var lang));
        Assert.Equal(UiLanguage.English, lang);
    }

    [Fact]
    public void Resolve_EnvOverrideBeatsCulture()
    {
        var lang = UiLanguageResolver.Resolve(envOverride: "ja", cultureOverride: CultureInfo.GetCultureInfo("en-US"));
        Assert.Equal(UiLanguage.Japanese, lang);
    }

    [Fact]
    public void Resolve_NoEnv_JapaneseCulture_ReturnsJapanese()
    {
        var lang = UiLanguageResolver.Resolve(envOverride: null, cultureOverride: CultureInfo.GetCultureInfo("ja-JP"));
        Assert.Equal(UiLanguage.Japanese, lang);
    }

    [Fact]
    public void Resolve_NoEnv_NonJapaneseCulture_FallsBackToEnglish()
    {
        var lang = UiLanguageResolver.Resolve(envOverride: null, cultureOverride: CultureInfo.GetCultureInfo("fr-FR"));
        Assert.Equal(UiLanguage.English, lang);
    }

    [Fact]
    public void Resolve_UnknownEnv_FallsBackToCulture()
    {
        // Garbage env values must not silently force English when the user's culture is Japanese.
        // 不正な環境変数はカルチャ判定を上書きしてはならない。
        var lang = UiLanguageResolver.Resolve(envOverride: "klingon", cultureOverride: CultureInfo.GetCultureInfo("ja-JP"));
        Assert.Equal(UiLanguage.Japanese, lang);
    }

    [Fact]
    public void Render_English_YieldsEnglishOnly()
    {
        var pair = new UiMessages.Pair("hello", "こんにちは");
        var lines = UiMessages.Render(pair, UiLanguage.English).ToList();
        Assert.Equal(new[] { "hello" }, lines);
    }

    [Fact]
    public void Render_Japanese_YieldsJapaneseOnly()
    {
        var pair = new UiMessages.Pair("hello", "こんにちは");
        var lines = UiMessages.Render(pair, UiLanguage.Japanese).ToList();
        Assert.Equal(new[] { "こんにちは" }, lines);
    }

    [Fact]
    public void Render_Both_YieldsEnglishThenJapanese()
    {
        var pair = new UiMessages.Pair("hello", "こんにちは");
        var lines = UiMessages.Render(pair, UiLanguage.Both).ToList();
        Assert.Equal(new[] { "hello", "こんにちは" }, lines);
    }

    [Fact]
    public void PrintEasterEggMessage_DefaultEnv_EmitsEnglishOnly()
    {
        var output = CaptureEasterEgg("--sushi", lang: "en");
        Assert.Contains(UiMessages.EasterEggSushi.English, output);
        Assert.DoesNotContain(UiMessages.EasterEggSushi.Japanese, output);
    }

    [Fact]
    public void PrintEasterEggMessage_JapaneseEnv_EmitsJapaneseOnly()
    {
        var output = CaptureEasterEgg("--coffee", lang: "ja");
        Assert.Contains(UiMessages.EasterEggCoffee.Japanese, output);
        Assert.DoesNotContain(UiMessages.EasterEggCoffee.English, output);
    }

    [Fact]
    public void PrintEasterEggMessage_BothEnv_EmitsBothLanguages()
    {
        var output = CaptureEasterEgg("--ramen", lang: "both");
        Assert.Contains(UiMessages.EasterEggRamen.English, output);
        Assert.Contains(UiMessages.EasterEggRamen.Japanese, output);
    }

    [Fact]
    public void PrintEasterEggMessage_UnknownFlag_PrintsBlankLinesOnly()
    {
        var output = CaptureEasterEgg("--not-a-flag", lang: "en");
        // Legacy fallback contract: print two blank lines, no catalog content.
        // 既存契約: 未知フラグは空行を2つ出すだけ。
        Assert.DoesNotContain(UiMessages.EasterEggSushi.English, output);
        Assert.DoesNotContain(UiMessages.EasterEggSushi.Japanese, output);
        var trimmed = output.Replace("\r", string.Empty);
        Assert.Equal("\n\n", trimmed);
    }

    private static string CaptureEasterEgg(string flag, string lang)
    {
        lock (TestConsoleLock.Gate)
        {
            var originalEnv = Environment.GetEnvironmentVariable(UiLanguageResolver.EnvVarName);
            var originalOut = Console.Out;
            using var writer = new StringWriter();
            try
            {
                Environment.SetEnvironmentVariable(UiLanguageResolver.EnvVarName, lang);
                Console.SetOut(writer);
                ConsoleUi.PrintEasterEggMessage(flag);
                return writer.ToString();
            }
            finally
            {
                Console.SetOut(originalOut);
                Environment.SetEnvironmentVariable(UiLanguageResolver.EnvVarName, originalEnv);
            }
        }
    }
}
