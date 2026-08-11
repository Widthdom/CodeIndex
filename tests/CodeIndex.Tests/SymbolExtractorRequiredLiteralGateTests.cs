using System.Reflection;
using System.Text.RegularExpressions;
using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public sealed class SymbolExtractorRequiredLiteralGateTests
{
    private readonly record struct GateMetrics(
        int PatternCount,
        int ApplicablePatternCount,
        int RegexAttemptCount,
        int MatchInputLiteralSkipCount);

    private static readonly PropertyInfo[] SymbolProperties = typeof(SymbolRecord)
        .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
        .OrderBy(property => property.Name, StringComparer.Ordinal)
        .ToArray();

    public static TheoryData<string, string, string> PositiveLanguageFixtures => new()
    {
        {
            "python",
            "class Widget:\n    marker = \"ＣＬＡＳＳ DEF\"\n",
            "Widget"
        },
        {
            "javascript",
            "export function run() {}\nconst marker = \"ＦＵＮＣＴＩＯＮ CLASS\";\n",
            "run"
        },
        {
            "typescript",
            "export interface Item {}\nconst marker = \"ＩＮＴＥＲＦＡＣＥ CLASS\";\n",
            "Item"
        },
        {
            "go",
            "package sample\nfunc Run() {}\n// ＦＵＮＣ\n",
            "Run"
        },
        {
            "rust",
            "pub fn run() {}\n// ＦＮ\n",
            "run"
        },
        {
            "java",
            "public class Widget { String marker = \"ＣＬＡＳＳ\"; }\n",
            "Widget"
        },
        {
            "c",
            "struct Widget { int value; }; /* ＳＴＲＵＣＴ */\n",
            "Widget"
        },
        {
            "cpp",
            "class Widget { public: int Run(); }; // ＣＬＡＳＳ\n",
            "Widget"
        },
        {
            "swift",
            "struct Widget { let marker = \"ＳＴＲＵＣＴ\" }\n",
            "Widget"
        },
        {
            "fsharp",
            "module Sample\nlet run value = value\n// ＬＥＴ\n",
            "run"
        },
        {
            "scala",
            "object Widget { val marker = \"ＯＢＪＥＣＴ\" }\n",
            "Widget"
        },
        {
            "terraform",
            "resource \"kind\" \"main\" {}\n# ＲＥＳＯＵＲＣＥ\n",
            "main"
        },
        {
            "protobuf",
            "message Widget {}\n// ＭＥＳＳＡＧＥ\n",
            "Widget"
        },
        {
            "zig",
            "pub fn run() void {}\n// ＦＮ\n",
            "run"
        },
    };

    public static TheoryData<string, string, string, string, bool> TransformedInputFixtures => new()
    {
        {
            "csharp",
            """
            internal class Cache
            {
                static
                Cache() { }
            }
            """,
            "function",
            "Cache",
            true
        },
        {
            "csharp",
            """
            internal class Box
            {
                public int Value
                    => 42;
            }
            """,
            "property",
            "Value",
            true
        },
        {
            "fortran",
            """
            subroutine &
              & Run()
            end subroutine Run
            """,
            "function",
            "Run",
            false
        },
        {
            "java",
            "@classMarker public interface Annotated {}\n",
            "interface",
            "Annotated",
            true
        },
        {
            "kotlin",
            "@file:funMarker public interface Annotated {}\n",
            "interface",
            "Annotated",
            false
        },
        {
            "css",
            ":root, .theme { color: red; }\n",
            "class",
            ".theme",
            true
        },
    };

    [Fact]
    public void RequiredLiteralMetadata_UsesOnlyAuditedCaseSensitiveTierAValues()
    {
        var metadata = SymbolExtractor.GetRequiredLiteralGateMetadataForTesting();

        Assert.Equal(29, SymbolProperties.Length);
        Assert.Equal(400, metadata.Count);
        Assert.Equal(51, metadata.Select(entry => entry.Language).Distinct(StringComparer.Ordinal).Count());
        Assert.All(metadata, entry =>
        {
            Assert.True(
                entry.Literal.Length >= 2,
                $"{entry.Language}/{entry.Kind} uses a one-character required literal.");
            Assert.Equal(RegexOptions.None, entry.Options & RegexOptions.IgnoreCase);
        });
    }

    [Fact]
    public void RequiredLiteralMetadata_RejectsIgnoreCaseAndShortLiterals()
    {
        Assert.Throws<InvalidOperationException>(
            () => SymbolExtractor.ValidateRequiredLiteralGateForTesting(RegexOptions.IgnoreCase, "class"));
        Assert.Throws<InvalidOperationException>(
            () => SymbolExtractor.ValidateRequiredLiteralGateForTesting(RegexOptions.None, "x"));
        Assert.Throws<InvalidOperationException>(
            () => SymbolExtractor.ValidateRequiredLiteralGateForTesting(RegexOptions.None, ""));

        SymbolExtractor.ValidateRequiredLiteralGateForTesting(RegexOptions.None, null);
    }

    [Theory]
    [MemberData(nameof(PositiveLanguageFixtures))]
    public void Extract_RequiredLiteralGatePreservesRepresentativeLanguageOutput(
        string language,
        string content,
        string expectedSymbolName)
    {
        var baseline = Extract(
            language,
            content,
            applyRequiredLiteralFileGate: false,
            applyRequiredLiteralMatchInputGate: false,
            out var baselineMetrics);
        var gated = Extract(
            language,
            content,
            applyRequiredLiteralFileGate: true,
            applyRequiredLiteralMatchInputGate: true,
            out var gatedMetrics);

        AssertSymbolsEqual(baseline, gated, language);
        Assert.Contains(gated, symbol => symbol.Name == expectedSymbolName);
        Assert.True(
            gatedMetrics.ApplicablePatternCount < baselineMetrics.PatternCount,
            $"{language} fixture did not skip any impossible patterns.");
    }

    [Fact]
    public void Extract_RequiredLiteralGateSkipsAbsentLiteralsForEveryAnnotatedLanguage()
    {
        var languages = SymbolExtractor.GetRequiredLiteralGateMetadataForTesting()
            .Select(entry => entry.Language)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(language => language, StringComparer.Ordinal);

        foreach (var language in languages)
        {
            const string content = "Ω １２３ ＣＬＡＳＳ ＦＵＮＣＴＩＯＮ\n";
            var baseline = Extract(
                language,
                content,
                applyRequiredLiteralFileGate: false,
                applyRequiredLiteralMatchInputGate: false,
                out var baselineMetrics);
            var gated = Extract(
                language,
                content,
                applyRequiredLiteralFileGate: true,
                applyRequiredLiteralMatchInputGate: true,
                out var gatedMetrics);

            AssertSymbolsEqual(baseline, gated, language);
            Assert.True(
                gatedMetrics.ApplicablePatternCount < baselineMetrics.PatternCount,
                $"{language} did not skip an absent Ordinal required literal.");
        }
    }

    [Fact]
    public void Extract_CSharp_RequiredLiteralMatchInputGatePreservesAdversarialOutput()
    {
        const string content = """
            // namespace interface enum struct operator event delegate partial readonly extern using
            internal class Ωmega
            {
                private const string Words = "namespace interface enum struct operator event delegate";
                private const string FullWidth = "ｎａｍｅｓｐａｃｅ ｉｎｔｅｒｆａｃｅ";
                private static readonly string Arrow = "=>";
                public int Value => 1;
                public static implicit operator int(Ωmega value) => value.Value;
                public event Action? Changed;
                public int this[int index] => index;
            }
            """;

        var baseline = Extract(
            "csharp",
            content,
            applyRequiredLiteralFileGate: true,
            applyRequiredLiteralMatchInputGate: false,
            out var baselineMetrics);
        var gated = Extract(
            "csharp",
            content,
            applyRequiredLiteralFileGate: true,
            applyRequiredLiteralMatchInputGate: true,
            out var gatedMetrics);

        AssertSymbolsEqual(baseline, gated);
        Assert.Contains(gated, symbol => symbol.Kind == "class" && symbol.Name == "Ωmega");
        Assert.Equal(baselineMetrics.ApplicablePatternCount, gatedMetrics.ApplicablePatternCount);
        Assert.Equal(0, baselineMetrics.MatchInputLiteralSkipCount);
        Assert.True(gatedMetrics.MatchInputLiteralSkipCount > 0);
        Assert.True(gatedMetrics.RegexAttemptCount < baselineMetrics.RegexAttemptCount);
    }

    [Theory]
    [MemberData(nameof(TransformedInputFixtures))]
    public void Extract_RequiredLiteralMatchInputGatePreservesTransformedAndSupplementalInputs(
        string language,
        string content,
        string expectedKind,
        string expectedName,
        bool expectMatchInputLiteralSkip)
    {
        var baseline = Extract(
            language,
            content,
            applyRequiredLiteralFileGate: true,
            applyRequiredLiteralMatchInputGate: false,
            out var baselineMetrics);
        var gated = Extract(
            language,
            content,
            applyRequiredLiteralFileGate: true,
            applyRequiredLiteralMatchInputGate: true,
            out var gatedMetrics);

        AssertSymbolsEqual(baseline, gated, language);
        Assert.Contains(gated, symbol => symbol.Kind == expectedKind && symbol.Name == expectedName);
        Assert.Equal(baselineMetrics.ApplicablePatternCount, gatedMetrics.ApplicablePatternCount);
        Assert.Equal(0, baselineMetrics.MatchInputLiteralSkipCount);
        Assert.Equal(expectMatchInputLiteralSkip, gatedMetrics.MatchInputLiteralSkipCount > 0);
        if (expectMatchInputLiteralSkip)
        {
            Assert.True(
                gatedMetrics.RegexAttemptCount < baselineMetrics.RegexAttemptCount,
                $"{language} exact-input gate did not reduce regex attempts.");
        }
        else
        {
            Assert.Equal(baselineMetrics.RegexAttemptCount, gatedMetrics.RegexAttemptCount);
        }
    }

    [Fact]
    public void Extract_CSharpIncompleteAttributeRecovery_UsesApplicablePatterns()
    {
        const string content = """
            [operatorMarker(
            public class Recovered
            {
            }
            """;

        var baseline = Extract(
            "csharp",
            content,
            applyRequiredLiteralFileGate: true,
            applyRequiredLiteralMatchInputGate: false,
            out var baselineMetrics);
        var gated = Extract(
            "csharp",
            content,
            applyRequiredLiteralFileGate: true,
            applyRequiredLiteralMatchInputGate: true,
            out var gatedMetrics);

        AssertSymbolsEqual(baseline, gated);
        Assert.Contains(gated, symbol => symbol.Kind == "class" && symbol.Name == "Recovered");
        Assert.Equal(baselineMetrics.ApplicablePatternCount, gatedMetrics.ApplicablePatternCount);
        Assert.Equal(0, baselineMetrics.MatchInputLiteralSkipCount);
        Assert.True(gatedMetrics.MatchInputLiteralSkipCount > 0);
        Assert.True(gatedMetrics.RegexAttemptCount < baselineMetrics.RegexAttemptCount);
    }

    [Fact]
    public void Extract_CppSameLineRecovery_UsesApplicablePatterns()
    {
        const string content = "class Box { public: int Run(); };\n// operator\n";

        var baseline = Extract(
            "cpp",
            content,
            applyRequiredLiteralFileGate: true,
            applyRequiredLiteralMatchInputGate: false,
            out var baselineMetrics);
        var gated = Extract(
            "cpp",
            content,
            applyRequiredLiteralFileGate: true,
            applyRequiredLiteralMatchInputGate: true,
            out var gatedMetrics);

        AssertSymbolsEqual(baseline, gated);
        Assert.Contains(gated, symbol => symbol.Kind == "class" && symbol.Name == "Box");
        Assert.Contains(gated, symbol => symbol.Kind == "function" && symbol.Name == "Run");
        Assert.Equal(baselineMetrics.ApplicablePatternCount, gatedMetrics.ApplicablePatternCount);
        Assert.Equal(0, baselineMetrics.MatchInputLiteralSkipCount);
        Assert.True(gatedMetrics.MatchInputLiteralSkipCount > 0);
        Assert.True(gatedMetrics.RegexAttemptCount < baselineMetrics.RegexAttemptCount);
    }

    [Fact]
    public void Extract_RequiredLiteralMatchInputGateReducesRegexAttemptsAtLeastThirtyPercent()
    {
        var requiredLiterals = SymbolExtractor.GetRequiredLiteralGateMetadataForTesting()
            .Where(entry => entry.Language == "csharp")
            .Select(entry => entry.Literal)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(literal => literal, StringComparer.Ordinal);
        var content = "// " + string.Join(" ", requiredLiterals) + "\n"
            + string.Join("\n", Enumerable.Range(0, 64).Select(index => $"unrelated_token_{index};"));

        var baseline = Extract(
            "csharp",
            content,
            applyRequiredLiteralFileGate: true,
            applyRequiredLiteralMatchInputGate: false,
            out var baselineMetrics);
        var gated = Extract(
            "csharp",
            content,
            applyRequiredLiteralFileGate: true,
            applyRequiredLiteralMatchInputGate: true,
            out var gatedMetrics);

        AssertSymbolsEqual(baseline, gated);
        Assert.Equal(baselineMetrics.PatternCount, baselineMetrics.ApplicablePatternCount);
        Assert.Equal(baselineMetrics.ApplicablePatternCount, gatedMetrics.ApplicablePatternCount);
        Assert.Equal(0, baselineMetrics.MatchInputLiteralSkipCount);
        Assert.True(gatedMetrics.MatchInputLiteralSkipCount > 0);
        Assert.True(
            (long)gatedMetrics.RegexAttemptCount * 10 <= (long)baselineMetrics.RegexAttemptCount * 7,
            $"Exact-input gate reduced attempts from {baselineMetrics.RegexAttemptCount} "
            + $"to {gatedMetrics.RegexAttemptCount}, less than the required 30% reduction.");
    }

    private static List<SymbolRecord> Extract(
        string language,
        string content,
        bool applyRequiredLiteralFileGate,
        bool applyRequiredLiteralMatchInputGate,
        out GateMetrics metrics)
    {
        var symbols = SymbolExtractor.ExtractForRequiredLiteralGateTesting(
            1,
            language,
            content,
            applyRequiredLiteralFileGate,
            applyRequiredLiteralMatchInputGate,
            out var patternCount,
            out var applicablePatternCount,
            out var regexAttemptCount,
            out var matchInputLiteralSkipCount);
        metrics = new GateMetrics(
            patternCount,
            applicablePatternCount,
            regexAttemptCount,
            matchInputLiteralSkipCount);
        return symbols;
    }

    private static void AssertSymbolsEqual(
        IReadOnlyList<SymbolRecord> expected,
        IReadOnlyList<SymbolRecord> actual,
        string? context = null)
    {
        Assert.True(
            expected.Count == actual.Count,
            $"Symbol count differs for {context ?? "adversarial input"}: {expected.Count} != {actual.Count}");
        for (var symbolIndex = 0; symbolIndex < expected.Count; symbolIndex++)
        {
            foreach (var property in SymbolProperties)
            {
                Assert.True(
                    Equals(property.GetValue(expected[symbolIndex]), property.GetValue(actual[symbolIndex])),
                    $"{context ?? "adversarial input"} symbol {symbolIndex} property {property.Name} differs");
            }
        }
    }
}
