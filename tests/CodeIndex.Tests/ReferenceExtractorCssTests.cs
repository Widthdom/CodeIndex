using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public partial class ReferenceExtractorTests
{
    [Fact]
    public void Extract_Css_CustomPropertiesAnimationsAndSelectors_AreReferenced()
    {
        const string content = """
            :root {
                --primary-color: #336699;
                --spacing-unit: 8px;
            }

            .card {
                color: var(--primary-color);
                padding: var(--spacing-unit);
                background: url('images/bg.png');
            }

            .btn-primary {
                background: var(--primary-color);
            }

            .container .card {
                margin: calc(var(--spacing-unit) * 2);
            }

            @media screen { .inline-media { color: red; } }

            @keyframes fade-in {
                from { opacity: 0; }
                to   { opacity: 1; }
            }

            .modal {
                animation-name: fade-in;
                animation-duration: 0.3s;
                animation: fade-in 0.3s ease-in;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "css", content);
        var references = ReferenceExtractor.Extract(1, "css", content, symbols);

        Assert.Equal(2, references.Count(reference =>
            reference.SymbolName == "primary-color"
            && reference.ReferenceKind == "reference"));
        Assert.Equal(2, references.Count(reference =>
            reference.SymbolName == "spacing-unit"
            && reference.ReferenceKind == "reference"));
        Assert.Single(references.Where(reference =>
            reference.SymbolName == ".card"
            && reference.ReferenceKind == "reference"));
        Assert.Equal(2, references.Count(reference =>
            reference.SymbolName == "fade-in"
            && reference.ReferenceKind == "reference"));
    }

    [Fact]
    public void Extract_Css_AnimationShorthand_IgnoresLeadingTimingTokens()
    {
        const string content = """
            @keyframes fade-in {
                from { opacity: 0; }
                to   { opacity: 1; }
            }

            .duration-first {
                animation: 250ms ease-in fade-in;
            }

            .keyword-only {
                animation: none;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "css", content);
        var references = ReferenceExtractor.Extract(1, "css", content, symbols);

        Assert.Single(references.Where(reference =>
            reference.SymbolName == "fade-in"
            && reference.ReferenceKind == "reference"));
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "none"
            && reference.ReferenceKind == "reference");
    }

    [Fact]
    public void Extract_Css_AnimationNameList_CapturesEachKeyframeReference()
    {
        const string content = """
            @keyframes fade-in {
                from { opacity: 0; }
                to   { opacity: 1; }
            }

            @keyframes slide-up {
                from { transform: translateY(1rem); }
                to   { transform: translateY(0); }
            }

            .modal {
                animation-name: fade-in, none, slide-up;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "css", content);
        var references = ReferenceExtractor.Extract(1, "css", content, symbols);

        Assert.Single(references.Where(reference =>
            reference.SymbolName == "fade-in"
            && reference.ReferenceKind == "reference"));
        Assert.Single(references.Where(reference =>
            reference.SymbolName == "slide-up"
            && reference.ReferenceKind == "reference"));
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "none"
            && reference.ReferenceKind == "reference");
    }

    [Fact]
    public void Extract_Css_MixedSelectorLists_KeepClassReferencesVisible()
    {
        const string content = """
            .container {
                button, .card {
                    color: red;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "css", content);
        var references = ReferenceExtractor.Extract(1, "css", content, symbols);

        Assert.Single(references.Where(reference =>
            reference.SymbolName == ".card"
            && reference.ReferenceKind == "reference"));
    }

    [Fact]
    public void Extract_Css_LongSelectorLine_UsesBoundedMatchEnumeration()
    {
        var className = new string('a', 20_000);
        var content = $".{className} {{ color: red; }}";

        var symbols = SymbolExtractor.Extract(1, "css", content);

        var exception = Record.Exception(() => ReferenceExtractor.Extract(1, "css", content, symbols));

        Assert.Null(exception);
    }

    [Fact]
    public void Extract_Css_DescendantSelectors_KeepClassReferencesVisible()
    {
        const string content = """
            .container {
                button .card {
                    color: red;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "css", content);
        var references = ReferenceExtractor.Extract(1, "css", content, symbols);

        Assert.Single(references.Where(reference =>
            reference.SymbolName == ".card"
            && reference.ReferenceKind == "reference"));
    }

    [Fact]
    public void Extract_Css_CompoundSelectors_KeepClassAndIdReferencesVisible()
    {
        const string content = """
            .btn { color: red; }
            #main { padding: 0; }

            a.btn {
                text-decoration: none;
            }

            button#main {
                background: blue;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "css", content);
        var references = ReferenceExtractor.Extract(1, "css", content, symbols);

        Assert.Single(references.Where(reference =>
            reference.SymbolName == ".btn"
            && reference.ReferenceKind == "reference"));
        Assert.Single(references.Where(reference =>
            reference.SymbolName == "#main"
            && reference.ReferenceKind == "reference"));
    }

    [Fact]
    public void Extract_Css_QuotedAttributeSelectors_DoNotEmitClassReferences()
    {
        const string content = """
            button[data-state=".card"] {
                color: red;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "css", content);
        var references = ReferenceExtractor.Extract(1, "css", content, symbols);

        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == ".card"
            && reference.ReferenceKind == "reference");
    }

    [Fact]
    public void Extract_Css_IdSelectors_AreReferenced()
    {
        const string content = """
            #header { color: blue; }
            body #header { padding: 0; }
            """;

        var symbols = SymbolExtractor.Extract(1, "css", content);
        var references = ReferenceExtractor.Extract(1, "css", content, symbols);

        Assert.Single(references.Where(reference =>
            reference.SymbolName == "#header"
            && reference.ReferenceKind == "reference"));
    }

    [Fact]
    public void Extract_Css_HexColorLiterals_DoNotEmitIdReferences()
    {
        const string content = """
            .card {
                color: #fff;
                background: #abc123;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "css", content);
        var references = ReferenceExtractor.Extract(1, "css", content, symbols);

        Assert.DoesNotContain(references, reference =>
            reference.SymbolName.StartsWith("#")
            && reference.ReferenceKind == "reference");
    }

    [Fact]
    public void Extract_CSS_ScssVariableAndExtendReferences_AreIndexed()
    {
        const string content = """
            $primary: #3366cc;
            $spacing-base: 8px;

            @mixin rounded($radius) {
              border-radius: $radius;
            }

            %button-base {
              padding: 4px;
            }

            .button {
              color: $primary;
              padding: $spacing-base * 2;
              @include rounded(4px);
            }

            .card {
              @extend %button-base;
              border: 1px solid $primary;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "css", content);
        var references = ReferenceExtractor.Extract(1, "css", content, symbols);

        Assert.Equal(2, references.Count(reference =>
            reference.SymbolName == "primary"
            && reference.ReferenceKind == "call"));
        Assert.Single(references.Where(reference =>
            reference.SymbolName == "spacing-base"
            && reference.ReferenceKind == "call"));
        Assert.Single(references.Where(reference =>
            reference.SymbolName == "%button-base"
            && reference.ReferenceKind == "call"));
        Assert.Single(references.Where(reference =>
            reference.SymbolName == "radius"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "rounded"));
    }

    [Fact]
    public void Extract_CSS_ScssIncludeReferences_AreIndexedAsCall()
    {
        // issue #1501: SCSS `@include name(args)` is a mixin invocation and must produce a
        // `call` edge to the mixin definition, otherwise mixins appear as zero-usage symbols
        // and `callers` / `impact` cannot trace mixin call graphs.
        // issue #1501: SCSS の `@include name(args)` は mixin 呼び出しであり、定義への `call`
        // エッジを出さなければ mixin が未使用シンボル扱いになり、`callers` / `impact` でも
        // mixin の呼び出し関係を辿れない。
        const string content = """
            @mixin border-radius($radius) {
              border-radius: $radius;
            }

            @mixin reset {
              margin: 0;
            }

            .card {
              @include border-radius(4px);
              @include reset;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "css", content);
        var references = ReferenceExtractor.Extract(1, "css", content, symbols);

        Assert.Single(references.Where(reference =>
            reference.SymbolName == "border-radius"
            && reference.ReferenceKind == "call"));
        Assert.Single(references.Where(reference =>
            reference.SymbolName == "reset"
            && reference.ReferenceKind == "call"));
    }

    [Fact]
    public void Extract_CSS_AtImportReferences_AreIndexedAsImport()
    {
        // issue #1501: stylesheet-level `@import "..."` / `@import url(...)` declarations
        // express cross-stylesheet dependency edges, so the extractor must emit `import`-kind
        // edges or `impact` underreports the blast radius of editing a shared theme stylesheet.
        // issue #1501: stylesheet レベルの `@import "..."` / `@import url(...)` は
        // 跨ぎ参照のエッジであり、`import` 種別のエッジを出さないと共通テーマ stylesheet を
        // 編集した際の `impact` が影響範囲を取りこぼす。
        const string content = """
            @import "theme.css";
            @import 'reset.css';
            @import url("typography.css");
            @import url('layout.css');
            @import url(utilities.css);
            @import "media.css" screen and (min-width: 600px);

            .page {
              color: black;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "css", content);
        var references = ReferenceExtractor.Extract(1, "css", content, symbols);

        Assert.Single(references.Where(reference =>
            reference.SymbolName == "theme.css"
            && reference.ReferenceKind == "import"));
        Assert.Single(references.Where(reference =>
            reference.SymbolName == "reset.css"
            && reference.ReferenceKind == "import"));
        Assert.Single(references.Where(reference =>
            reference.SymbolName == "typography.css"
            && reference.ReferenceKind == "import"));
        Assert.Single(references.Where(reference =>
            reference.SymbolName == "layout.css"
            && reference.ReferenceKind == "import"));
        Assert.Single(references.Where(reference =>
            reference.SymbolName == "utilities.css"
            && reference.ReferenceKind == "import"));
        Assert.Single(references.Where(reference =>
            reference.SymbolName == "media.css"
            && reference.ReferenceKind == "import"));
    }

    [Fact]
    public void Extract_CSS_MixedCssAndScssImportAndInclude_EmitBothEdgeKinds()
    {
        // issue #1501 mixed-extension fixture: a `.scss` entry point pulls in a `.css`
        // partial via `@import` and invokes a mixin via `@include`. Both edge kinds must
        // surface so the graph captures the cross-file dependency *and* the mixin call.
        // issue #1501 の mixed-extension fixture: `.scss` のエントリポイントが `@import` で
        // `.css` パーシャルを取り込み、`@include` で mixin を呼び出す。両エッジを同時に
        // 出力できないとファイル間依存と mixin 呼び出しの片方が欠落する。
        const string content = """
            @import "tokens.css";

            @mixin elevated($depth) {
              box-shadow: 0 $depth 0 rgba(0, 0, 0, 0.1);
            }

            .card {
              @include elevated(2px);
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "css", content);
        var references = ReferenceExtractor.Extract(1, "css", content, symbols);

        Assert.Single(references.Where(reference =>
            reference.SymbolName == "tokens.css"
            && reference.ReferenceKind == "import"));
        Assert.Single(references.Where(reference =>
            reference.SymbolName == "elevated"
            && reference.ReferenceKind == "call"));
    }
}
