using System.Reflection;
using System.Text.RegularExpressions;
using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public sealed class SymbolExtractorRequiredLiteralGateTests
{
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
        var baseline = Extract(language, content, applyRequiredLiteralGate: false, out var patternCount, out _);
        var gated = Extract(language, content, applyRequiredLiteralGate: true, out _, out var applicablePatternCount);

        AssertSymbolsEqual(baseline, gated, language);
        Assert.Contains(gated, symbol => symbol.Name == expectedSymbolName);
        Assert.True(
            applicablePatternCount < patternCount,
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
            var baseline = Extract(language, content, applyRequiredLiteralGate: false, out var patternCount, out _);
            var gated = Extract(language, content, applyRequiredLiteralGate: true, out _, out var applicablePatternCount);

            AssertSymbolsEqual(baseline, gated, language);
            Assert.True(
                applicablePatternCount < patternCount,
                $"{language} did not skip an absent Ordinal required literal.");
        }
    }

    [Fact]
    public void Extract_CSharp_RequiredLiteralGatePreservesAdversarialOutput()
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

        var baseline = Extract("csharp", content, applyRequiredLiteralGate: false, out var patternCount, out _);
        var gated = Extract("csharp", content, applyRequiredLiteralGate: true, out _, out var applicablePatternCount);

        AssertSymbolsEqual(baseline, gated);
        Assert.Contains(gated, symbol => symbol.Kind == "class" && symbol.Name == "Ωmega");
        Assert.True(applicablePatternCount < patternCount);
    }

    [Fact]
    public void Extract_CSharpIncompleteAttributeRecovery_UsesApplicablePatterns()
    {
        const string content = """
            [Broken(
            public class Recovered
            {
            }
            """;

        var baseline = Extract("csharp", content, applyRequiredLiteralGate: false, out _, out _);
        var gated = Extract("csharp", content, applyRequiredLiteralGate: true, out var patternCount, out var applicablePatternCount);

        AssertSymbolsEqual(baseline, gated);
        Assert.Contains(gated, symbol => symbol.Kind == "class" && symbol.Name == "Recovered");
        Assert.True(applicablePatternCount < patternCount);
    }

    [Fact]
    public void Extract_CppSameLineRecovery_UsesApplicablePatterns()
    {
        const string content = "class Box { public: int Run(); };";

        var baseline = Extract("cpp", content, applyRequiredLiteralGate: false, out _, out _);
        var gated = Extract("cpp", content, applyRequiredLiteralGate: true, out var patternCount, out var applicablePatternCount);

        AssertSymbolsEqual(baseline, gated);
        Assert.Contains(gated, symbol => symbol.Kind == "class" && symbol.Name == "Box");
        Assert.Contains(gated, symbol => symbol.Kind == "function" && symbol.Name == "Run");
        Assert.True(applicablePatternCount < patternCount);
    }

    private static List<SymbolRecord> Extract(
        string language,
        string content,
        bool applyRequiredLiteralGate,
        out int patternCount,
        out int applicablePatternCount) =>
        SymbolExtractor.ExtractForRequiredLiteralGateTesting(
            1,
            language,
            content,
            applyRequiredLiteralGate,
            out patternCount,
            out applicablePatternCount);

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
