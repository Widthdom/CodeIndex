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

    public static TheoryData<string, string, string[]> RequiredAnyLiteralSuccessFixtures => new()
    {
        {
            "javascript",
            """
            const ReactMemo = React.memo(Component);
            const ReactForward = React.forwardRef(render);
            const ReactLazy = React.lazy(loader);
            const StyledMember = styled.div`color: red;`;
            const StyledCall = styled(Component)`color: blue;`;
            const Connected = connect(mapState)(Component);
            const Memoed = memo(Component);
            const Forwarded = forwardRef(render);
            const LazyLoaded = lazy(loader);
            const Observed = observer(Component);
            const WithAuth = withAuthentication(Component);
            """,
            [
                "ReactMemo",
                "ReactForward",
                "ReactLazy",
                "StyledMember",
                "StyledCall",
                "Connected",
                "Memoed",
                "Forwarded",
                "LazyLoaded",
                "Observed",
                "WithAuth",
            ]
        },
        {
            "typescript",
            """
            const ReactMemo = React.memo<Props>(Component);
            const ReactForward = React.forwardRef<HTMLDivElement, Props>(render);
            const ReactLazy = React.lazy<Props>(loader);
            const StyledMember = styled.div`color: red;`;
            const StyledCall = styled(Component)`color: blue;`;
            const Connected = connect<StateProps>(mapState)(Component);
            const Memoed = memo<Props>(Component);
            const Forwarded = forwardRef<HTMLDivElement, Props>(render);
            const LazyLoaded = lazy<Props>(loader);
            const Observed = observer<Props>(Component);
            const WithAuth = withAuthentication<Props>(Component);
            namespace IdentifierNamespace { }
            module IdentifierModule { }
            declare module 'quoted-module' { }
            declare namespace "quoted-namespace" { }
            """,
            [
                "ReactMemo",
                "ReactForward",
                "ReactLazy",
                "StyledMember",
                "StyledCall",
                "Connected",
                "Memoed",
                "Forwarded",
                "LazyLoaded",
                "Observed",
                "WithAuth",
                "IdentifierNamespace",
                "IdentifierModule",
                "quoted-module",
                "quoted-namespace",
            ]
        },
        {
            "kotlin",
            """
            class Concrete
            object Singleton
            @Serializable class AnnotatedClass
            @Serializable object AnnotatedObject
            val immutable: Int = 1
            var mutable: Int = 2
            @field:Marker val annotatedImmutable: Int = 3
            @field:Marker var annotatedMutable: Int = 4
            """,
            [
                "Concrete",
                "Singleton",
                "AnnotatedClass",
                "AnnotatedObject",
                "immutable",
                "mutable",
                "annotatedImmutable",
                "annotatedMutable",
            ]
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

    [Fact]
    public void RequiredAnyLiteralMetadata_UsesAuditedImmutableOrdinalSets()
    {
        var metadata = SymbolExtractor.GetRequiredAnyLiteralGateMetadataForTesting()
            .OrderBy(entry => entry.Language, StringComparer.Ordinal)
            .ThenBy(entry => entry.Kind, StringComparer.Ordinal)
            .ThenBy(entry => string.Join("\0", entry.Literals), StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(6, metadata.Length);
        Assert.Equal(
            [
                "javascript/function:React.,styled,connect,memo,forwardRef,lazy,observer,with",
                "kotlin/class:class,object",
                "kotlin/property:val,var",
                "typescript/function:React.,styled,connect,memo,forwardRef,lazy,observer,with",
                "typescript/namespace:namespace,module",
                "typescript/namespace:namespace,module",
            ],
            metadata.Select(entry =>
                $"{entry.Language}/{entry.Kind}:{string.Join(',', entry.Literals)}"));
        Assert.All(metadata, entry =>
        {
            Assert.NotEmpty(entry.Literals);
            Assert.Equal(RegexOptions.None, entry.Options & RegexOptions.IgnoreCase);
            Assert.All(entry.Literals, literal => Assert.True(literal.Length >= 2));
            Assert.Equal(
                entry.Literals.Count,
                entry.Literals.Distinct(StringComparer.Ordinal).Count());
        });

        var exposedSnapshot = Assert.IsType<string[]>(metadata[0].Literals);
        exposedSnapshot[0] = "mutated";
        Assert.DoesNotContain(
            SymbolExtractor.GetRequiredAnyLiteralGateMetadataForTesting(),
            entry => entry.Literals.Contains("mutated", StringComparer.Ordinal));
    }

    [Fact]
    public void RequiredAnyLiteralMetadata_RejectsMalformedOrAmbiguousSets()
    {
        Assert.Throws<InvalidOperationException>(
            () => SymbolExtractor.ValidateRequiredLiteralGateForTesting(
                RegexOptions.None,
                null,
                Array.Empty<string>()));
        Assert.Throws<InvalidOperationException>(
            () => SymbolExtractor.ValidateRequiredLiteralGateForTesting(
                RegexOptions.None,
                null,
                new string[] { null! }));
        Assert.Throws<InvalidOperationException>(
            () => SymbolExtractor.ValidateRequiredLiteralGateForTesting(
                RegexOptions.None,
                null,
                [""]));
        Assert.Throws<InvalidOperationException>(
            () => SymbolExtractor.ValidateRequiredLiteralGateForTesting(
                RegexOptions.None,
                null,
                ["x"]));
        Assert.Throws<InvalidOperationException>(
            () => SymbolExtractor.ValidateRequiredLiteralGateForTesting(
                RegexOptions.None,
                null,
                ["class", "class"]));
        Assert.Throws<InvalidOperationException>(
            () => SymbolExtractor.ValidateRequiredLiteralGateForTesting(
                RegexOptions.None,
                "class",
                ["class", "object"]));
        Assert.Throws<InvalidOperationException>(
            () => SymbolExtractor.ValidateRequiredLiteralGateForTesting(
                RegexOptions.IgnoreCase,
                null,
                ["class", "object"]));

        SymbolExtractor.ValidateRequiredLiteralGateForTesting(
            RegexOptions.None,
            null,
            ["class", "Class"]);
        SymbolExtractor.ValidateRequiredLiteralGateForTesting(RegexOptions.None, null, null);
    }

    [Theory]
    [MemberData(nameof(RequiredAnyLiteralSuccessFixtures))]
    public void Extract_RequiredAnyLiteralGatePreservesEveryAuditedAlternation(
        string language,
        string content,
        string[] expectedNames)
    {
        var baseline = Extract(
            language,
            content,
            applyRequiredLiteralFileGate: false,
            applyRequiredLiteralMatchInputGate: false,
            out _);
        var gated = Extract(
            language,
            content,
            applyRequiredLiteralFileGate: true,
            applyRequiredLiteralMatchInputGate: true,
            out _);

        AssertSymbolsEqual(baseline, gated, language);
        Assert.All(
            expectedNames,
            expectedName => Assert.Contains(gated, symbol => symbol.Name == expectedName));
        if (language == "kotlin")
        {
            Assert.Contains(
                gated,
                symbol => symbol.Name.StartsWith("Annotated", StringComparison.Ordinal)
                    && symbol.StartColumn > 0);
        }
    }

    [Theory]
    [InlineData("javascript")]
    [InlineData("typescript")]
    [InlineData("kotlin")]
    public void Extract_RequiredAnyLiteralFileGateSkipsOnlyWhenEveryAlternativeIsAbsent(
        string language)
    {
        var singularMetadata = SymbolExtractor.GetRequiredLiteralGateMetadataForTesting()
            .Where(entry => entry.Language == language)
            .ToArray();
        var anyMetadata = SymbolExtractor.GetRequiredAnyLiteralGateMetadataForTesting()
            .Where(entry => entry.Language == language)
            .ToArray();
        var anyLiterals = anyMetadata
            .SelectMany(entry => entry.Literals)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var nonOverlappingSingularLiterals = singularMetadata
            .Select(entry => entry.Literal)
            .Distinct(StringComparer.Ordinal)
            .Where(literal => !anyLiterals.Any(
                alternative => literal.Contains(alternative, StringComparison.Ordinal)))
            .OrderBy(literal => literal, StringComparer.Ordinal);
        var content = "Ω " + string.Join(" ", nonOverlappingSingularLiterals) + "\n";
        Assert.DoesNotContain(
            anyLiterals,
            literal => content.Contains(literal, StringComparison.Ordinal));

        var expectedSingularSkipCount = singularMetadata.Count(
            entry => !content.Contains(entry.Literal, StringComparison.Ordinal));
        var expectedAnySkipCount = anyMetadata.Count(
            entry => !entry.Literals.Any(
                literal => content.Contains(literal, StringComparison.Ordinal)));
        Assert.Equal(anyMetadata.Length, expectedAnySkipCount);

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
            applyRequiredLiteralMatchInputGate: false,
            out var gatedMetrics);

        AssertSymbolsEqual(baseline, gated, language);
        Assert.Equal(baselineMetrics.PatternCount, baselineMetrics.ApplicablePatternCount);
        Assert.Equal(
            baselineMetrics.PatternCount - expectedSingularSkipCount - expectedAnySkipCount,
            gatedMetrics.ApplicablePatternCount);
        Assert.Equal(0, gatedMetrics.MatchInputLiteralSkipCount);
    }

    [Theory]
    [InlineData("javascript")]
    [InlineData("typescript")]
    [InlineData("kotlin")]
    public void Extract_RequiredAnyLiteralExactInputGateStructurallyReducesRegexAttempts(
        string language)
    {
        var singularLiterals = SymbolExtractor.GetRequiredLiteralGateMetadataForTesting()
            .Where(entry => entry.Language == language)
            .Select(entry => entry.Literal)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(literal => literal, StringComparer.Ordinal)
            .ToArray();
        var anyLiterals = SymbolExtractor.GetRequiredAnyLiteralGateMetadataForTesting()
            .Where(entry => entry.Language == language)
            .SelectMany(entry => entry.Literals)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(literal => literal, StringComparer.Ordinal)
            .ToArray();
        var allFileLiterals = singularLiterals.Concat(anyLiterals);
        var unrelatedLine = "Ω " + string.Join(" ", singularLiterals);
        var content = "// " + string.Join(" ", allFileLiterals) + "\n"
            + string.Join('\n', Enumerable.Repeat(unrelatedLine, 64));

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
        Assert.Equal(baselineMetrics.PatternCount, baselineMetrics.ApplicablePatternCount);
        Assert.Equal(baselineMetrics.ApplicablePatternCount, gatedMetrics.ApplicablePatternCount);
        Assert.Equal(0, baselineMetrics.MatchInputLiteralSkipCount);
        Assert.True(gatedMetrics.MatchInputLiteralSkipCount >= 64);
        Assert.True(
            gatedMetrics.RegexAttemptCount < baselineMetrics.RegexAttemptCount,
            $"{language} any-of gate did not reduce regex attempts: "
            + $"{baselineMetrics.RegexAttemptCount} -> {gatedMetrics.RegexAttemptCount}.");
    }

    [Fact]
    public void Extract_RequiredAnyLiteralGatePreservesSameLineNonzeroRecovery()
    {
        const string typeScript = "; const Generic = memo<Props>(Component);\n";
        const string kotlin = "@Serializable class Annotated\n";

        foreach (var (language, content, expectedName) in new[]
        {
            ("typescript", typeScript, "Generic"),
            ("kotlin", kotlin, "Annotated"),
        })
        {
            var baseline = Extract(
                language,
                content,
                applyRequiredLiteralFileGate: false,
                applyRequiredLiteralMatchInputGate: false,
                out _);
            var gated = Extract(
                language,
                content,
                applyRequiredLiteralFileGate: true,
                applyRequiredLiteralMatchInputGate: true,
                out _);

            AssertSymbolsEqual(baseline, gated, language);
            Assert.Contains(
                gated,
                symbol => symbol.Name == expectedName
                    && symbol.StartColumn > 0);
        }
    }

    [Fact]
    public void Extract_RequiredAnyLiteralGatePreservesCaseSensitiveNearMissesAndHocGenericAsymmetry()
    {
        const string javaScript = """
            const Generic = memo<Props>(Component);
            const Context = React.createContext(null);
            const StyledFactory = styled.div;
            const Upper = MEMO(Component);
            """;
        const string typeScript = """
            const Generic = memo<Props>(Component);
            const Context = React.createContext(null);
            const StyledFactory = styled.div;
            const Upper = MEMO(Component);
            declare moduLe 'wrong-case' { }
            """;
        const string kotlin = """
            Class WrongClass
            Object WrongObject
            Val wrongVal: Int = 1
            Var wrongVar: Int = 2
            """;

        foreach (var (language, content) in new[]
        {
            ("javascript", javaScript),
            ("typescript", typeScript),
            ("kotlin", kotlin),
        })
        {
            var baseline = Extract(
                language,
                content,
                applyRequiredLiteralFileGate: false,
                applyRequiredLiteralMatchInputGate: false,
                out _);
            var gated = Extract(
                language,
                content,
                applyRequiredLiteralFileGate: true,
                applyRequiredLiteralMatchInputGate: true,
                out _);

            AssertSymbolsEqual(baseline, gated, language);
        }

        Assert.DoesNotContain(
            SymbolExtractor.Extract(1, "javascript", javaScript),
            symbol => symbol.Kind == "function" && symbol.Name == "Generic");
        Assert.Contains(
            SymbolExtractor.Extract(1, "typescript", typeScript),
            symbol => symbol.Kind == "function" && symbol.Name == "Generic");
        Assert.DoesNotContain(
            SymbolExtractor.Extract(1, "typescript", typeScript),
            symbol => symbol.Kind == "function"
                && symbol.Name is "Context" or "StyledFactory" or "Upper");
        Assert.DoesNotContain(
            SymbolExtractor.Extract(1, "typescript", typeScript),
            symbol => symbol.Kind == "namespace" && symbol.Name == "wrong-case");
        Assert.Empty(SymbolExtractor.Extract(1, "kotlin", kotlin));
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
