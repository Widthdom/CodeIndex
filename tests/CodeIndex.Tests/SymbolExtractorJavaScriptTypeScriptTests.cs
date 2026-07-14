using System.Diagnostics;
using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Xml;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public partial class SymbolExtractorTests
{
    [Fact]
    public void TypeScriptBareMethodMatchInput_ReusesPlainBodyHeaders()
    {
        const string input = "run() {";

        var normalized = SymbolExtractor.NormalizeTypeScriptBareMethodMatchInput(input);

        Assert.Same(input, normalized);
    }

    [Fact]
    public void TypeScriptBareMethodMatchInput_BlanksGenericSpan()
    {
        const string input = "run<T>(value: T) {";

        var normalized = SymbolExtractor.NormalizeTypeScriptBareMethodMatchInput(input);

        Assert.NotSame(input, normalized);
        Assert.Equal("run   (value: T) {", normalized);
    }

    [Fact]
    public void TypeScriptBareMethodMatchInput_RewritesReturnTypeBraces()
    {
        const string input = "make(): { value: string } {";

        var normalized = SymbolExtractor.NormalizeTypeScriptBareMethodMatchInput(input);

        Assert.NotSame(input, normalized);
        Assert.Equal("make(): ( value: string ) {", normalized);
    }

    [Theory]
    [InlineData("javascript")]
    [InlineData("typescript")]
    public void Extract_JavaScriptTypeScript_DetectsStringLiteralExportNames(string language)
    {
        var content = """
            const handler = () => {};
            const other = 1;
            export { handler as "x-api", other as otherName /* keep */ };
            export { remote as "remote-key", another as anotherName } from "./remote";
            """;
        var symbols = SymbolExtractor.Extract(1, language, content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "x-api" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "otherName" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "remote-key" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "anotherName" && s.Visibility == "export");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "\"x-api\"");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./remote");
    }

    [Theory]
    [InlineData("javascript")]
    [InlineData("typescript")]
    public void Extract_JavaScriptTypeScript_DetectsCommonJsRequireModuleSymbols(string language)
    {
        var content = """
            const fs = require("node:fs");
            const helper = require(
              "./helper"
            );
            const method = loader.require("./method");
            const resolved = require.resolve("./resolved");
            const resolvedWithPaths = require.resolve("./with-paths", { paths: [__dirname] });
            const text = "require('./string')";
            """;
        var symbols = SymbolExtractor.Extract(1, language, content);

        var fsImport = Assert.Single(symbols.Where(s => s.Kind == "import" && s.Name == "node:fs"));
        Assert.Equal(1, fsImport.Line);
        var helperImport = Assert.Single(symbols.Where(s => s.Kind == "import" && s.Name == "./helper"));
        Assert.Equal(3, helperImport.Line);
        Assert.Contains("require(", helperImport.Signature);
        var resolvedImport = Assert.Single(symbols.Where(s => s.Kind == "import" && s.Name == "./resolved"));
        Assert.Equal(6, resolvedImport.Line);
        Assert.Contains("require.resolve", resolvedImport.Signature);
        var resolvedWithPathsImport = Assert.Single(symbols.Where(s => s.Kind == "import" && s.Name == "./with-paths"));
        Assert.Equal(7, resolvedWithPathsImport.Line);
        Assert.Contains("paths", resolvedWithPathsImport.Signature);
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "./method");
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "./string");
    }

    [Theory]
    [InlineData("javascript")]
    [InlineData("typescript")]
    public void Extract_JavaScriptTypeScript_DetectsImportMetaModuleSymbols(string language)
    {
        var content = """
            const resolved = import.meta.resolve("./feature.js");
            const scoped = import.meta.resolve(
              "./scoped.js",
              import.meta.url
            );
            client.import.meta.resolve("./method.js");
            const dynamic = import.meta.resolve(path);
            const text = "import.meta.resolve('./string.js')";

            const workerUrl = new URL("./worker.js", import.meta.url);
            const imageUrl = new URL(
              "./image.png",
              import.meta.url
            );
            const templated = new URL(`./view.js`, import.meta.url);
            const computed = new URL(`./${name}.js`, import.meta.url);
            const plain = URL("./plain.js", import.meta.url);
            const otherBase = new URL("./other.js", baseUrl);
            const hrefBase = new URL("./href.js", import.meta.url.href);
            """;
        var symbols = SymbolExtractor.Extract(1, language, content);

        var resolvedImport = Assert.Single(symbols.Where(s => s.Kind == "import" && s.Name == "./feature.js"));
        Assert.Equal(1, resolvedImport.Line);
        Assert.Contains("import.meta.resolve", resolvedImport.Signature);
        var scopedImport = Assert.Single(symbols.Where(s => s.Kind == "import" && s.Name == "./scoped.js"));
        Assert.Equal(3, scopedImport.Line);
        Assert.Contains("import.meta.url", scopedImport.Signature);
        var workerImport = Assert.Single(symbols.Where(s => s.Kind == "import" && s.Name == "./worker.js"));
        Assert.Equal(10, workerImport.Line);
        Assert.Contains("new URL", workerImport.Signature);
        var imageImport = Assert.Single(symbols.Where(s => s.Kind == "import" && s.Name == "./image.png"));
        Assert.Equal(12, imageImport.Line);
        Assert.Contains("import.meta.url", imageImport.Signature);
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./view.js");
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name.Contains("${", StringComparison.Ordinal));
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "./plain.js");
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "./other.js");
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "./href.js");
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name is "./method.js" or "path" or "./string.js");
    }

    [Theory]
    [InlineData("javascript")]
    [InlineData("typescript")]
    public void Extract_JavaScriptTypeScript_DetectsWorkerLoadingModuleSymbols(string language)
    {
        var content = """
            importScripts("./worker-a.js", "/worker-b.js");
            importScripts(
              "./legacy.js",
              `./template-script.js`,
              `./${name}.js`
            );
            loader.importScripts("./method.js");
            const text = "importScripts('./string.js')";

            const worker = new Worker("./worker.js");
            const shared = new SharedWorker(
              "./shared-worker.js",
              { type: "module" }
            );
            const templatedWorker = new Worker(`./template-worker.js`, { type: "module" });
            const computedWorker = new Worker(`./${name}.js`);
            const windowWorker = new window.Worker("./window-worker.js");
            const globalShared = new globalThis.SharedWorker("./global-shared-worker.js");
            const plainWorker = Worker("./plain-worker.js");
            const service = new ServiceWorker("./service-worker.js");
            """;
        var symbols = SymbolExtractor.Extract(1, language, content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./worker-a.js" && s.Line == 1);
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "/worker-b.js" && s.Line == 1);
        var legacyImport = Assert.Single(symbols.Where(s => s.Kind == "import" && s.Name == "./legacy.js"));
        Assert.Equal(3, legacyImport.Line);
        Assert.Contains("importScripts", legacyImport.Signature);
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./template-script.js");
        var workerImport = Assert.Single(symbols.Where(s => s.Kind == "import" && s.Name == "./worker.js"));
        Assert.Equal(10, workerImport.Line);
        Assert.Contains("new Worker", workerImport.Signature);
        var sharedImport = Assert.Single(symbols.Where(s => s.Kind == "import" && s.Name == "./shared-worker.js"));
        Assert.Equal(12, sharedImport.Line);
        Assert.Contains("type", sharedImport.Signature);
        foreach (var name in new[] { "./template-worker.js", "./window-worker.js", "./global-shared-worker.js" })
            Assert.Contains(symbols, s => s.Kind == "import" && s.Name == name);
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name.Contains("${", StringComparison.Ordinal));
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name is
            "./method.js" or "./string.js" or "./plain-worker.js" or "./service-worker.js");
    }

    [Theory]
    [InlineData("javascript")]
    [InlineData("typescript")]
    public void Extract_JavaScriptTypeScript_DetectsBrowserModuleRegistrationSymbols(string language)
    {
        var content = """
            navigator.serviceWorker.register("./sw.js");
            navigator.serviceWorker.register(
              "./scoped-sw.js",
              { scope: "./" }
            );
            window.navigator.serviceWorker.register("./window-sw.js");
            globalThis.navigator.serviceWorker.register("./global-sw.js");
            navigator.serviceWorker.register(dynamicPath);
            const text = "navigator.serviceWorker.register('./string-sw.js')";

            audioWorklet.addModule("./audio-processor.js");
            CSS.paintWorklet.addModule(
              "./paint-worklet.js",
              { credentials: "same-origin" }
            );
            layoutWorklet.addModule(`./layout-worklet.js`);
            this.audioWorklet.addModule("./method-audio.js");
            worklet.addModule("./generic-worklet.js");
            audioWorklet.addModule(dynamicPath);
            const text = "audioWorklet.addModule('./string-audio.js')";
            """;
        var symbols = SymbolExtractor.Extract(1, language, content);

        var serviceWorkerImport = Assert.Single(symbols.Where(s => s.Kind == "import" && s.Name == "./sw.js"));
        Assert.Equal(1, serviceWorkerImport.Line);
        Assert.Contains("navigator.serviceWorker.register", serviceWorkerImport.Signature);
        var scopedImport = Assert.Single(symbols.Where(s => s.Kind == "import" && s.Name == "./scoped-sw.js"));
        Assert.Equal(3, scopedImport.Line);
        Assert.Contains("scope", scopedImport.Signature);
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./window-sw.js");
        var audioImport = Assert.Single(symbols.Where(s => s.Kind == "import" && s.Name == "./audio-processor.js"));
        Assert.Equal(11, audioImport.Line);
        Assert.Contains("audioWorklet.addModule", audioImport.Signature);
        var paintImport = Assert.Single(symbols.Where(s => s.Kind == "import" && s.Name == "./paint-worklet.js"));
        Assert.Equal(13, paintImport.Line);
        Assert.Contains("credentials", paintImport.Signature);
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./layout-worklet.js");
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "dynamicPath");
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name is
            "./global-sw.js" or "./string-sw.js" or "./method-audio.js" or "./generic-worklet.js" or "./string-audio.js");
    }

    [Fact]
    public void Extract_JavaScript_StringBraceDoesNotBreakFollowingContainerAssignment()
    {
        var content = """"
            export class Example {
              foo() {
                const value = "}";
                return value;
              }

              bar() {
                return 1;
              }
            }
            """";
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        var example = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Example"));
        var foo = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "foo"));
        var bar = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "bar"));

        Assert.Equal(10, example.EndLine);
        Assert.Equal(5, foo.EndLine);
        Assert.Equal("class", bar.ContainerKind);
        Assert.Equal("Example", bar.ContainerName);
    }

    [Fact]
    public void Extract_JavaScript_TemplateLiteralBraceDoesNotBreakFollowingContainerAssignment()
    {
        var content = """"
            export class Example {
              foo() {
                const value = `}`;
                return value;
              }

              bar() {
                return 1;
              }
            }
            """";
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        var example = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Example"));
        var foo = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "foo"));
        var bar = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "bar"));

        Assert.Equal(10, example.EndLine);
        Assert.Equal(5, foo.EndLine);
        Assert.Equal("class", bar.ContainerKind);
        Assert.Equal("Example", bar.ContainerName);
    }

    [Fact]
    public void Extract_JavaScript_DetectsExportDefaultClassMembers()
    {
        var content = """""
            export default class DefaultJs {
                run() {}
            }
            """"";
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "DefaultJs");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run");
    }

    [Fact]
    public void Extract_JavaScript_DoesNotInventExtendsAsAnonymousDefaultClassName()
    {
        var content = """
            export default class extends Base {
                run() {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "extends");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run");
    }

    [Fact]
    public void Extract_JavaScript_DoesNotInventExtendsAsAnonymousDefaultDerivedClassName()
    {
        var content = """
            export default class extends mixin(Base) {
                run() {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "extends");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run");
    }

    [Fact]
    public void Extract_JavaScript_DetectsClassExpressionMethods()
    {
        var content = """
            const Service = class NamedService {
                run() {}
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Service");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "NamedService");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run");
        var run = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "run"));
        Assert.Equal("class", run.ContainerKind);
        Assert.Equal("Service", run.ContainerName);
    }

    [Fact]
    public void Extract_JavaScript_DetectsMultilineExportedClassExpressionMethods()
    {
        var content = """
            export const Service =
                class {
                    run() {}
                };
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Service");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "Service");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Service" && s.Signature == "export const Service = class {");
    }

    [Fact]
    public void Extract_JavaScript_DetectsParenthesizedClassExpressionMethods()
    {
        var content = "const Service = (class { run() {} });";
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Service");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "Service");
    }

    [Fact]
    public void Extract_JavaScript_DetectsInlineClassMethods()
    {
        var content = "export class Inline { run() {} }";
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Inline");
        var run = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "run"));
        Assert.Equal("class", run.ContainerKind);
        Assert.Equal("Inline", run.ContainerName);
    }

    [Fact]
    public void Extract_JavaScript_DetectsInlineMultipleMethods()
    {
        var content = "class Inline { first() {} second() {} }";
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Inline");
        var first = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "first"));
        var second = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "second"));
        Assert.Equal("class", first.ContainerKind);
        Assert.Equal("Inline", first.ContainerName);
        Assert.Equal("first() {}", first.Signature);
        Assert.Equal("class", second.ContainerKind);
        Assert.Equal("Inline", second.ContainerName);
        Assert.Equal("second() {}", second.Signature);
    }

    [Fact]
    public void Extract_JavaScript_DetectsSameLineSiblingClassesWithDistinctMethodNames()
    {
        var content = "class A { first() {} } class B { second() {} }";
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "A");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "B");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "first" && s.ContainerName == "A");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "second" && s.ContainerName == "B");
    }

    [Fact]
    public void Extract_JavaScript_DetectsSameLineSiblingClassesWithIdenticalMethodNames()
    {
        var content = "class A { run() {} } class B { run() {} }";
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "A");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "B");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "A");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "B");
    }

    [Fact]
    public void Extract_JavaScript_DetectsSameLinePublicClassAfterStatementPrefix()
    {
        var content = "foo(); class Visible { keep() {} }";
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Visible");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep" && s.ContainerName == "Visible");
    }

    [Fact]
    public void Extract_JavaScript_DetectsSameLinePublicClassAfterFunctionLocalHiddenClass()
    {
        var content = "function outer(){ class Hidden { run() {} } } class Visible { keep() {} }";
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "outer");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Hidden");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "run");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Visible");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep" && s.ContainerName == "Visible");
    }

    [Fact]
    public void Extract_JavaScript_DetectsSameLineClassExpressionAfterStatementPrefix()
    {
        var content = "foo(); const Service = class Visible { keep() {} };";
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Service");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Visible");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep" && s.ContainerName == "Service");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Service" && s.Signature == "const Service = class Visible { keep() {} }");
    }

    [Fact]
    public void Extract_JavaScript_DetectsStatementPrefixedDefaultExportClassSignatureFromExport()
    {
        var content = "const before = 1; export default (class { run() {} })";
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "default" && s.Signature == "export default (class { run() {} }");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "default");
    }

    [Fact]
    public void Extract_JavaScript_DetectsInlineDefaultExportClassMethods()
    {
        var content = "export default class Inline { run() {} }";
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Inline");
        var run = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "run"));
        Assert.Equal("class", run.ContainerKind);
        Assert.Equal("Inline", run.ContainerName);
    }

    [Fact]
    public void Extract_JavaScript_DetectsInlineDefaultExportMultipleMethods()
    {
        var content = "export default class { first() {} second() {} }";
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "default");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "first" && s.ContainerName == "default");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "second" && s.ContainerName == "default");
    }

    [Fact]
    public void Extract_JavaScript_DetectsParenthesizedDefaultExportClassMembers()
    {
        var content = """
            export default (class {
                run() {}
            });
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "default");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "default");
    }

    [Fact]
    public void Extract_JavaScript_DetectsMultilineParenthesizedDefaultExportClassSignature()
    {
        var content = """
            export default
            (
                class {
                    run() {}
                }
            );
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "default" && s.Signature == "export default ( class {");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "default");
    }

    [Fact]
    public void Extract_JavaScript_DetectsInlineModifierNamedMethods()
    {
        var content = "export default class { async() {} static() {} keep() {} }";
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "default");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "async" && s.ContainerName == "default");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "static" && s.ContainerName == "default");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep" && s.ContainerName == "default");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "async" && s.Signature == "async() {}");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "static" && s.Signature == "static() {}");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep" && s.Signature == "keep() {}");
    }

    [Fact]
    public void Extract_JavaScript_DetectsInlineMethodsWithDefaultArguments()
    {
        var content = "class Example { method(x = 1) {} visible() {} }";
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "method" && s.ContainerName == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "visible" && s.ContainerName == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "method" && s.Signature == "method(x = 1) {}");
    }

    [Fact]
    public void Extract_JavaScript_DetectsInlinePrivateAndGeneratorMethods()
    {
        var content = "class Example { #hidden() {} *iterator() {} async *stream() {} visible() {} }";
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "#hidden" && s.ContainerName == "Example");
        Assert.Contains(symbols, s => s.Kind == "generator" && s.Name == "iterator" && s.ContainerName == "Example");
        Assert.Contains(symbols, s => s.Kind == "async_generator" && s.Name == "stream" && s.ContainerName == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "visible" && s.ContainerName == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "#hidden" && s.Signature == "#hidden() {}");
        Assert.Contains(symbols, s => s.Kind == "generator" && s.Name == "iterator" && s.Signature == "*iterator() {}");
        Assert.Contains(symbols, s => s.Kind == "async_generator" && s.Name == "stream" && s.Signature == "async *stream() {}");
    }

    [Fact]
    public void Extract_JavaScript_DetectsInlineComputedMethods()
    {
        var content = "class Example { ['computed']() {} [Symbol.iterator]() {} visible() {} }";
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "['computed']" && s.ContainerName == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "[Symbol.iterator]" && s.ContainerName == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "visible" && s.ContainerName == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "['computed']" && s.Signature == "['computed']() {}");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "[Symbol.iterator]" && s.Signature == "[Symbol.iterator]() {}");
    }

    [Fact]
    public void Extract_JavaScript_PreservesModifierMetadataForComputedMethods()
    {
        var content = """
            class Example {
                async [Symbol.asyncIterator]() {}
                get [Symbol.toStringTag]() {}
                set [key](value) {}
                async *[Symbol.iterator]() {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "async_function" && s.Name == "[Symbol.asyncIterator]" && s.Signature == "async [Symbol.asyncIterator]() {}");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "[Symbol.toStringTag]" && s.Signature == "get [Symbol.toStringTag]() {}");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "[key]" && s.Signature == "set [key](value) {}");
        Assert.Contains(symbols, s => s.Kind == "async_generator" && s.Name == "[Symbol.iterator]" && s.Signature == "async *[Symbol.iterator]() {}");
    }

    [Fact]
    public void Extract_JavaScript_DetectsQuotedAndNumericLiteralMethodNames()
    {
        var content = """
            class Example {
                "run"() {}
                'stop'() {}
                1() {}
                1.5() {}
                0x10() {}
                1_000() {}
                next() {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "\"run\"" && s.Signature == "\"run\"() {}");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "'stop'" && s.Signature == "'stop'() {}");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "1" && s.Signature == "1() {}");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "1.5" && s.Signature == "1.5() {}");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "0x10" && s.Signature == "0x10() {}");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "1_000" && s.Signature == "1_000() {}");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "next" && s.Signature == "next() {}");
    }

    [Fact]
    public void Extract_JavaScript_DetectsEscapedQuotedLiteralMethodNames()
    {
        var content = """
            class Example {
                "a\"b"() {}
                'c\'d'() {}
                next() {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "\"a\\\"b\"" && s.Signature == "\"a\\\"b\"() {}");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "'c\\'d'" && s.Signature == "'c\\'d'() {}");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "next" && s.Signature == "next() {}");
    }

    [Fact]
    public void Extract_JavaScript_ClassifiesAsyncAndGeneratorFunctionKinds()
    {
        var content = """
            function plain() {}
            async function asyncPlain() {}
            function* generated() {}
            async function* asyncGenerated() {}
            export default async function* () {}
            class Example {
                async method() {}
                *items() {}
                async *stream() {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "plain");
        Assert.Contains(symbols, s => s.Kind == "async_function" && s.Name == "asyncPlain");
        Assert.Contains(symbols, s => s.Kind == "generator" && s.Name == "generated");
        Assert.Contains(symbols, s => s.Kind == "async_generator" && s.Name == "asyncGenerated");
        Assert.Contains(symbols, s => s.Kind == "async_generator" && s.Name == "default" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "async_function" && s.Name == "method" && s.ContainerName == "Example");
        Assert.Contains(symbols, s => s.Kind == "generator" && s.Name == "items" && s.ContainerName == "Example");
        Assert.Contains(symbols, s => s.Kind == "async_generator" && s.Name == "stream" && s.ContainerName == "Example");
    }

    [Theory]
    [InlineData(
        """
        class Example {
            handler = function namedHandler() {};
            keep() {}
        }
        """,
        "namedHandler")]
    [InlineData(
        """
        class Example {
            handler = function () {};
            keep() {}
        }
        """,
        "function")]
    [InlineData(
        """
        class Example {
            field = { inner() {} };
            keep() {}
        }
        """,
        "inner")]
    [InlineData(
        """
        class Example {
            field = class Inner { run() {} };
            keep() {}
        }
        """,
        "run")]
    public void Extract_JavaScript_DoesNotTreatClassFieldInitializerMembersAsClassMethods(string content, string unexpectedMethod)
    {
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep" && s.ContainerName == "Example");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == unexpectedMethod && s.ContainerName == "Example");
    }

    [Fact]
    public void Extract_JavaScript_SemicolonlessFieldInitializerDoesNotHideComputedOrGeneratorMethods()
    {
        var content = """
            class Example {
                field = foo
                [Symbol.iterator]() {}
                *generate() {}
                next() {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "[Symbol.iterator]" && s.ContainerName == "Example");
        Assert.Contains(symbols, s => s.Kind == "generator" && s.Name == "generate" && s.ContainerName == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "next" && s.ContainerName == "Example");
    }

    [Fact]
    public void Extract_JavaScript_DetectsInlineClassExpressionMethods()
    {
        var content = "const Service = class NamedService { run() {} };";
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Service");
        var run = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "run"));
        Assert.Equal("class", run.ContainerKind);
        Assert.Equal("Service", run.ContainerName);
    }

    [Fact]
    public void Extract_JavaScript_DetectsCommonJsClassExpressionForms()
    {
        var content = """
            exports.Service = class NamedService { serviceRun() {} };
            module.exports = class { inlineRun() {} };
            module.exports =
                class {
                    multilineRun() {}
                };
            module.exports = (class { parenthesizedRun() {} });
            module.exports.PropertyService = class { propertyRun() {} };
            if (typeof module !== "undefined") {
                module.exports = class { conditionalRun() {} };
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Service");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "NamedService");
        Assert.Equal(4, symbols.Count(s => s.Kind == "class" && s.Name == "default"));
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "PropertyService");
        AssertMember("serviceRun", "Service");
        foreach (var name in new[] { "inlineRun", "multilineRun", "parenthesizedRun", "conditionalRun" })
            AssertMember(name, "default");
        AssertMember("propertyRun", "PropertyService");

        void AssertMember(string name, string containerName) =>
            Assert.Contains(symbols, s => s.Kind == "function" && s.Name == name && s.ContainerName == containerName);
    }

    [Fact]
    public void Extract_JavaScript_DetectsDollarPrefixedClassExpressionBindings()
    {
        var content = """
            export const $Service = class {
                run() {}
            };

            exports.$Handler = class {
                keep() {}
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "$Service");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "$Service");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "$Handler");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep" && s.ContainerName == "$Handler");
    }

    [Fact]
    public void Extract_JavaScript_DoesNotLeakBlockScopedClassesInsideConditionalBlocks()
    {
        var content = """
            if (flag) {
                const Hidden = class {
                    run() {}
                };

                class LocalDecl {
                    keep() {}
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Hidden");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "LocalDecl");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "run");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "keep");
    }

    [Fact]
    public void Extract_JavaScript_LocalClassForms_DoNotLeakFromCallableScopes()
    {
        var content = """
            export class Outer {
                expressionMethod() {
                    const MethodExpression = class {
                        methodExpressionMember() {}
                    };
                }
                directMethod() {
                    class MethodDirect {
                        methodDirectMember() {}
                    }
                }
            }

            function expressionFunction() {
                const FunctionExpression = class {
                    functionExpressionMember() {}
                };
            }
            function directFunction() {
                class FunctionDirect {
                    functionDirectMember() {}
                }
            }
            exports.handler = function () {
                const CommonJsExpression = class {
                    commonJsMember() {}
                };
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Outer");
        foreach (var name in new[] { "expressionMethod", "directMethod" })
            Assert.Contains(symbols, s => s.Kind == "function" && s.Name == name && s.ContainerName == "Outer");
        foreach (var name in new[] { "expressionFunction", "directFunction" })
            Assert.Contains(symbols, s => s.Kind == "function" && s.Name == name);
        Assert.DoesNotContain(symbols, s => s.Name is
            "MethodExpression" or "methodExpressionMember" or "MethodDirect" or "methodDirectMember"
            or "FunctionExpression" or "functionExpressionMember" or "FunctionDirect" or "functionDirectMember"
            or "CommonJsExpression" or "commonJsMember");
    }

    [Fact]
    public void Extract_JavaScript_DetectsCommonJsNamedExportAssignments()
    {
        var content = """
            module.exports.foo = function foo() { return 1; };
            module.exports.bar = () => 2;
            exports.baz = 42;
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "foo");
        Assert.Contains(symbols, s => s.Kind == "lambda" && s.Name == "bar");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "baz");
    }

    [Theory]
    [InlineData("javascript")]
    [InlineData("typescript")]
    public void Extract_JavaScriptTypeScript_DetectsCommonJsNumericBracketNamedExportAssignments(string language)
    {
        var content = """
            exports[404] = notFound;
            module.exports[500] = function serverError() { return 500; };
            exports[dynamicKey] = hidden;
            """;
        var symbols = SymbolExtractor.Extract(1, language, content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "404" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "500" && s.Visibility == "export");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "dynamicKey" && s.Visibility == "export");
    }

    [Fact]
    public void Extract_JavaScript_DoesNotTreatCommonJsNamedExportComparisonsAsAssignments()
    {
        var content = """
            module.exports.foo === undefined;
            exports.bar == null;
            module.exports.baz !== 1;
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Empty(symbols);
    }

    [Fact]
    public void Extract_JavaScript_DetectsMultilineCommonJsNamedExportAssignments()
    {
        var content = """
            module.exports.foo =
              async () => {};
            module.exports.bar =
              () => 2;
            exports.baz =
              42;
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "lambda" && s.Name == "foo");
        Assert.Contains(symbols, s => s.Kind == "lambda" && s.Name == "bar");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "baz");
    }

    [Fact]
    public void Extract_JavaScript_DetectsParenthesizedMultilineCommonJsNamedExportAssignments()
    {
        var content = """
            module.exports.foo =
              (
                async () => 1
              );
            module.exports.bar =
              (
                function () {
                  return 2;
                }
              );
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "lambda" && s.Name == "foo");
        Assert.Contains(symbols, s => s.Kind == "lambda" && s.Name == "bar");
    }

    [Fact]
    public void Extract_JavaScript_DetectsParenthesizedSameLineCommonJsNamedExportAssignments()
    {
        var content = """
            module.exports.foo = (function () { return 1; });
            module.exports.bar = (async function () { return 2; });
            module.exports.baz = (() => 3);
            exports.qux = (42);
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "lambda" && s.Name == "foo");
        Assert.Contains(symbols, s => s.Kind == "lambda" && s.Name == "bar");
        Assert.Contains(symbols, s => s.Kind == "lambda" && s.Name == "baz");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "qux");
    }

    [Fact]
    public void Extract_JavaScript_CommonJsNamedExportFunctionsPreserveMultilineBraceBodyRanges()
    {
        var content = """
            module.exports.foo = function ()
            {
              return 1;
            };
            module.exports.bar = () =>
            {
              return 2;
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        var foo = Assert.Single(symbols.Where(s => s.Kind == "lambda" && s.Name == "foo"));
        Assert.Equal(1, foo.StartLine);
        Assert.Equal(4, foo.EndLine);
        Assert.Equal(2, foo.BodyStartLine);
        Assert.Equal(4, foo.BodyEndLine);

        var bar = Assert.Single(symbols.Where(s => s.Kind == "lambda" && s.Name == "bar"));
        Assert.Equal(5, bar.StartLine);
        Assert.Equal(8, bar.EndLine);
        Assert.Equal(6, bar.BodyStartLine);
        Assert.Equal(8, bar.BodyEndLine);
    }

    [Theory]
    [InlineData("javascript")]
    [InlineData("typescript")]
    public void Extract_JavaScriptTypeScript_DetectsCommonJsDefaultFunctionAssignments(string language)
    {
        var content = """
            module.exports =
              (
                function createServer(req) {
                  return req;
                }
              );
            module.exports = async (value) => {
              return value;
            };
            module.exports = class Service { run() {} };
            module.exports = { named() {} };
            module.exports = 42;
            """;
        var symbols = SymbolExtractor.Extract(1, language, content);

        var defaults = symbols
            .Where(s => s.Kind == "function" && s.Name == "default" && s.Visibility == "export")
            .ToList();
        Assert.Single(defaults);
        Assert.Contains(defaults, s => s.StartLine == 1 && s.BodyStartLine == 3 && s.BodyEndLine == 5);
        Assert.Contains(symbols, s => s.Kind == "lambda" && s.Name == "default" && s.Visibility == "export" && s.StartLine == 7 && s.BodyStartLine == 7 && s.BodyEndLine == 9);
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "default");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "named" && s.ContainerName == "module.exports");
    }

    [Theory]
    [InlineData("javascript")]
    [InlineData("typescript")]
    public void Extract_JavaScriptTypeScript_DetectsCommonJsObjectExportApis(string language)
    {
        var content = """
            Object.defineProperty(exports, "__esModule", { value: true });
            Object.defineProperty(exports, "foo", { enumerable: true, get: function () { return api.foo; } });
            Object.defineProperty(exports, 404, { value: notFound });
            Object.defineProperty(
              module.exports,
              "bar-baz",
              { value: bar }
            );
            Object.defineProperty(
              module.exports,
              500,
              { value: serverError }
            );
            Object.defineProperty(local, "hidden", { value: hidden });
            Object.defineProperties(exports, {
              bulkFoo: { value: foo },
              "bulk-bar": { value: bar },
              ["bulk-computed"]: { value: computed },
              [dynamicBulk]: { value: hidden },
              bulkShorthand,
            });
            Object.defineProperties(module.exports, { default: { value: api }, 501: { value: bulkError } });
            Object.defineProperties(exports, { bulkSameLine: { value: sameLine } });
            Object.assign(module.exports, {
              default: api,
              502: assignError,
              assignFoo,
              assignAlias: value,
              "assign-bar": bar,
              ["assign-computed"]: computed,
              [dynamicAssign]: hidden,
            });
            Object.assign(exports, { assignSameLine: sameLine });
            Object.defineProperties(local, { hiddenBulk: { value: hidden } });
            Object.assign(local, { hiddenAssign });
            """;
        var symbols = SymbolExtractor.Extract(1, language, content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "foo" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "bar-baz" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "404" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "500" && s.Visibility == "export");
        foreach (var name in new[] { "bulkFoo", "bulk-bar", "bulk-computed", "bulkShorthand", "501", "bulkSameLine", "502", "assignFoo", "assignAlias", "assign-bar", "assign-computed", "assignSameLine" })
            Assert.Contains(symbols, s => s.Kind == "property" && s.Name == name && s.Visibility == "export");
        Assert.Equal(2, symbols.Count(s => s.Kind == "property" && s.Name == "default" && s.Visibility == "export"));
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "__esModule");
        Assert.DoesNotContain(symbols, s => s.Kind == "property"
            && s.Name is "hidden" or "dynamicBulk" or "dynamicAssign" or "hiddenBulk" or "hiddenAssign"
            && s.Visibility == "export");
    }

    [Fact]
    public void Extract_JavaScript_DetectsConditionalCommonJsNamedExportAssignmentsInTopLevelBlocks()
    {
        var content = """
            if (process.env.FEATURE) {
              module.exports.enabled = function () {
                return true;
              };
            }
            if (process.env.FLAG) {
              exports.flag = 1;
            }
            function setup() {
              module.exports.hidden = function () {
                return false;
              };
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "lambda" && s.Name == "enabled");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "flag");
        Assert.DoesNotContain(symbols, s => s.Name == "hidden");
    }

    [Fact]
    public void Extract_JavaScript_DoesNotTreatCommonJsNamedExportIdentifierPrefixesAsFunctionsOrClasses()
    {
        var content = """
            module.exports.foo = functionCall();
            module.exports.bar = classyThing;
            module.exports.baz = (functionCall());
            exports.qux = (classyThing);
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "foo");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "bar");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "baz");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "qux");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && (s.Name == "foo" || s.Name == "baz"));
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && (s.Name == "bar" || s.Name == "qux"));
    }

    [Fact]
    public void Extract_JavaScript_DetectsExportedObjectLiteralAliasProperties()
    {
        var content = """
            const foo = 1;
            function inner() { return 3; }
            function named() { return 4; }
            const answer = 42;
            module.exports = { foo, alias: inner, named, method() {} };
            export default { answer };
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "foo" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "alias" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "named" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "method" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "answer" && s.ContainerKind == "object" && s.ContainerName == "default");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "inner" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
    }

    [Fact]
    public void Extract_JavaScript_DetectsDestructuredNamedExports()
    {
        var content = """
            const source = {};
            export const { foo, renamed: localName, nested: { leaf }, items: [first], ...rest } = source;
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "foo" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "localName" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "leaf" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "first" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "rest" && s.Visibility == "export");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && (s.Name == "renamed" || s.Name == "nested" || s.Name == "items"));
    }

    [Fact]
    public void Extract_JavaScript_DetectsMultiLineObjectLiteralBinding()
    {
        // The `{` may sit on a line after the `=` binding; collector must thread
        // the lex state across lines to find the open brace.
        // `{` が `=` バインディングと別行にあっても、collector は lex 状態を
        // 跨いで open brace を検出できること。
        var content = """
            const obj =
            {
                foo() { return 1; },
                *bar() { yield 1; },
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "foo" && s.ContainerKind == "object" && s.ContainerName == "obj");
        Assert.Contains(symbols, s => s.Kind == "generator" && s.Name == "bar" && s.ContainerKind == "object" && s.ContainerName == "obj");
    }

    [Fact]
    public void Extract_JavaScript_DoesNotTreatQuotedOrComputedExportedObjectLiteralKeysAsValueSideShorthandProperties()
    {
        var content = """
            module.exports = { 'foo': bar, [baz]: qux, answer: 42 };
            export default { [name]: value, visible };
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "answer" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "visible" && s.ContainerKind == "object" && s.ContainerName == "default");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "bar" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "qux" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "value" && s.ContainerKind == "object" && s.ContainerName == "default");
    }

    [Fact]
    public void Extract_JavaScript_DoesNotTreatExportedObjectLiteralSpreadsAsProperties()
    {
        var content = """
            const rest = source;
            const defaults = source;
            const answer = 42;
            module.exports = { ...rest, actual: 1, config: { ...rest } };
            export default { ...defaults, answer };
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "actual" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "config" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "answer" && s.ContainerKind == "object" && s.ContainerName == "default");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "rest" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "defaults" && s.ContainerKind == "object" && s.ContainerName == "default");
    }

    [Fact]
    public void Extract_JavaScript_BlocklessArrowReturnShapes_DoNotLeakClasses()
    {
        var content = """
            const plainFactory = () =>
                class HiddenPlain {
                    plainMethod() {}
                };
            const wrappedFactory = () =>
                wrap(
                    class HiddenWrapped {
                        wrappedMethod() {}
                    }
                );
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        AssertRange("plainFactory", 1, 2, 4);
        AssertRange("wrappedFactory", 5, 6, 10);
        Assert.DoesNotContain(symbols, s => s.Name is "HiddenPlain" or "HiddenWrapped" or "plainMethod" or "wrappedMethod");

        void AssertRange(string name, int startLine, int bodyStartLine, int endLine)
        {
            var arrow = Assert.Single(symbols.Where(s => s.Kind == "lambda" && s.Name == name));
            Assert.Equal(startLine, arrow.StartLine);
            Assert.Equal(bodyStartLine, arrow.BodyStartLine);
            Assert.Equal(endLine, arrow.EndLine);
            Assert.Equal(endLine, arrow.BodyEndLine);
        }
    }

    [Fact]
    public void Extract_JavaScript_BlocklessArrowsStopBeforeFollowingTopLevelForms()
    {
        var content = """
            const classFactory = () =>
                class HiddenClass {
                    hiddenClassMethod() {}
                }
            class VisibleClass {
                keepClass() {}
            }
            const expressionFactory = () =>
                class HiddenExpression {
                    hiddenExpressionMethod() {}
                }
            foo();
            class VisibleExpression {
                keepExpression() {}
            }
            const exportFactory = () =>
                class HiddenExport {
                    hiddenExportMethod() {}
                }
            exports.Service = class VisibleExport {
                keepExport() {}
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        AssertArrowRange("classFactory", 1, 2, 4);
        AssertArrowRange("expressionFactory", 8, 9, 11);
        AssertArrowRange("exportFactory", 16, 17, 19);
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "VisibleClass");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keepClass" && s.ContainerName == "VisibleClass");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "VisibleExpression");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keepExpression" && s.ContainerName == "VisibleExpression");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Service");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keepExport" && s.ContainerName == "Service");
        Assert.DoesNotContain(symbols, s => s.Name is "HiddenClass" or "HiddenExpression" or "HiddenExport"
            or "hiddenClassMethod" or "hiddenExpressionMethod" or "hiddenExportMethod");

        void AssertArrowRange(string name, int startLine, int bodyStartLine, int endLine)
        {
            var arrow = Assert.Single(symbols.Where(s => s.Kind == "lambda" && s.Name == name));
            Assert.Equal(startLine, arrow.StartLine);
            Assert.Equal(bodyStartLine, arrow.BodyStartLine);
            Assert.Equal(endLine, arrow.EndLine);
            Assert.Equal(endLine, arrow.BodyEndLine);
        }
    }

    [Fact]
    public void Extract_JavaScript_LocalClasses_DoNotLeakFromNestedCallableAndStaticScopes()
    {
        var content = """
            (function () {
                const IifeLocal = class {
                    iifeMember() {}
                };
            })();
            class Outer {
                static {
                    const StaticExpression = class {
                        staticExpressionMember() {}
                    };
                    class StaticDirect {
                        staticDirectMember() {}
                    }
                }
                keep() {}
            }
            const obj = {
                method() {
                    class ConciseLocal {
                        conciseMember() {}
                    }
                },
                get value() {
                    class GetterLocal {
                        getterMember() {}
                    }
                    return 1;
                },
                set value(input) {
                    class SetterLocal {
                        setterMember() {}
                    }
                }
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Outer");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep" && s.ContainerName == "Outer");
        Assert.DoesNotContain(symbols, s => s.Name is
            "IifeLocal" or "iifeMember" or "StaticExpression" or "staticExpressionMember"
            or "StaticDirect" or "staticDirectMember" or "ConciseLocal" or "conciseMember"
            or "GetterLocal" or "getterMember" or "SetterLocal" or "setterMember");
    }

    [Fact]
    public void Extract_JavaScript_PreservesGetterSetterSignaturesAndMethodNamedGetSet()
    {
        var content = """
            class Example {
                get value() {}
                set value(input) {}
                get() {}
                set() {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "value" && s.Signature == "get value() {}");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "value" && s.Signature == "set value(input) {}");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "get" && s.Signature == "get() {}");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "set" && s.Signature == "set() {}");
    }

    [Fact]
    public void Extract_JavaScript_DoesNotLeakNamedClassExpressionsAfterColon()
    {
        var content = """
            const pick = cond ? value : class Hidden { method() {} };
            const obj = { field: class Inner { run() {} } };
            class Visible { ok() {} }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Hidden");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Inner");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "method");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "run");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Visible");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "ok" && s.ContainerName == "Visible");
    }

    [Fact]
    public void Extract_JavaScript_DoesNotTreatControlFlowBlocksAsFunctions()
    {
        var content = """
            class Parser {
                parse(value) {
                    if (value) {
                    }

                    for (const item of value.items) {
                    }

                    while (value.ready) {
                    }

                    switch (value.mode) {
                        case "fast":
                            break;
                    }
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Parser");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "parse");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "if");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "for");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "while");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "switch");
    }

    [Fact]
    public void Extract_JavaScript_AllowsKeywordNamedMethodsAtClassBodyDepthZero()
    {
        var content = """
            class KeywordMethods {
                if() {}
                catch() {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "KeywordMethods");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "if");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "catch");
    }

    [Fact]
    public void Extract_JavaScript_IgnoresRegexLiteralBracesAndBlockCommentMethodShapes()
    {
        var content = """
            class Example {
                /*
                    fake() {
                    }
                */
                first() {
                    const open = /{/;
                    const close = /}/;
                }

                second() {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "first");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "second");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "fake");
    }

    [Fact]
    public void Extract_JavaScript_KeepsSiblingMethodsAfterWrappedControlFlowRegexLiterals()
    {
        var content = """
            class Example {
                first(value) {
                    if (
                        ready
                    ) /{/.test(value);
                }

                second(value) {
                    if (first) {
                    }
                    else if (
                        secondReady
                    ) /{/.test(value);
                }

                third() {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "first");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "second");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "third");
    }

    [Fact]
    public void Extract_JavaScript_IgnoresHeaderObjectLiteralBracesBeforeClassBody()
    {
        var content = """
            class Derived extends mixin({ value: true }) {
                run() {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Derived");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run");
    }

    [Fact]
    public void Extract_JavaScript_DoesNotTreatHeaderComparisonAsGenericAngleDepth()
    {
        var content = """
            class Derived extends mixin(a < b ? Base : Fallback) {
                run() {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Derived");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run");
    }

    [Fact]
    public void Extract_JavaScript_KeepsSiblingMethodsAfterElseRegexLiteral()
    {
        var content = """
            class Example {
                first(value) {
                    if (cond) {
                    }
                    else /{/.test(value);
                }

                second() {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "first");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "second");
    }

    [Fact]
    public void Extract_JavaScript_KeepsSiblingMethodsAfterDoAndFinallyRegexLiterals()
    {
        var content = """
            class Example {
                first(value) {
                    do /{/.test(value); while (cond);
                }

                second(value) {
                    try {
                    }
                    finally /{/.test(value);
                }

                third() {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "first");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "second");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "third");
    }

    [Fact]
    public void Extract_JavaScript_FunctionRangeIgnoresComparisonAngleBracketsInParameters()
    {
        var content = """
            function choose(value = a < b ? one : two) {
                return value;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        var choose = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "choose"));
        Assert.Equal(1, choose.StartLine);
        Assert.Equal(3, choose.EndLine);
        Assert.Equal(1, choose.BodyStartLine);
        Assert.Equal(3, choose.BodyEndLine);
    }

    [Theory]
    [InlineData("javascript", "export default function load() { return 1; }", "load")]
    [InlineData("javascript", "export default function* () { yield 1; }", "default")]
    [InlineData("typescript", "export default function load<T>(value: T): T { return value; }", "load")]
    [InlineData("typescript", "export default function <T>(value: T): T { return value; }", "default")]
    public void Extract_JavaScriptTypeScript_DetectsExportDefaultFunctionSymbols(
        string language,
        string content,
        string expectedName)
    {
        var symbols = SymbolExtractor.Extract(1, language, content);

        var function = Assert.Single(symbols.Where(s => (s.Kind == "function" || s.Kind == "generator") && s.Name == expectedName));
        Assert.Equal("export", function.Visibility);
        Assert.Equal(content, function.Signature);
    }

    [Theory]
    [InlineData("javascript", "export default\n  (value) => value;", null, null)]
    [InlineData("typescript", "export default\n  (value) => value;", null, null)]
    [InlineData("javascript", "export default async (value) => {\n  return value;\n};", 1, 3)]
    [InlineData("typescript", "export default async (value) => {\n  return value;\n};", 1, 3)]
    public void Extract_JavaScriptTypeScript_DetectsExportDefaultArrowFunctionSymbols(
        string language,
        string content,
        int? expectedBodyStartLine,
        int? expectedBodyEndLine)
    {
        var symbols = SymbolExtractor.Extract(1, language, content);

        var function = Assert.Single(symbols.Where(s => s.Kind == "lambda" && s.Name == "default"));
        Assert.Equal("export", function.Visibility);
        Assert.Equal(1, function.StartLine);
        Assert.Equal(expectedBodyStartLine, function.BodyStartLine);
        Assert.Equal(expectedBodyEndLine, function.BodyEndLine);
    }

    [Theory]
    [InlineData("javascript")]
    [InlineData("typescript")]
    public void Extract_JavaScriptTypeScript_DetectsDynamicImportForms(string language)
    {
        var content = """
            const loader = () => import(
                "./feature"
            );
            const method = client.import(
                "./method"
            );
            const optional = client?.import("./optional");
            class Loader { #import(path) {} load() { return this.#import("./private"); } }
            const text = "import('./string')";
            // import('./comment')

            const data = await import("./data.json", {
                with: { type: "json" }
            });
            const legacy = await import(
                "./legacy.json",
                { assert: { type: "json" } }
            );

            const view = import(`./view.js`);
            const computed = import(`./${name}.js`);
            """;
        var symbols = SymbolExtractor.Extract(1, language, content);

        var importSymbol = Assert.Single(symbols.Where(s => s.Kind == "import" && s.Name == "./feature"));
        Assert.Equal(2, importSymbol.Line);
        Assert.Contains("const loader", importSymbol.Signature);
        Assert.Contains("import(", importSymbol.Signature);
        Assert.Contains("./feature", importSymbol.Signature);

        var dataImport = Assert.Single(symbols.Where(s => s.Kind == "import" && s.Name == "./data.json"));
        Assert.Equal(12, dataImport.Line);
        Assert.Contains("with", dataImport.Signature);
        Assert.Contains("type", dataImport.Signature);

        var legacyImport = Assert.Single(symbols.Where(s => s.Kind == "import" && s.Name == "./legacy.json"));
        Assert.Equal(16, legacyImport.Line);
        Assert.Contains("assert", legacyImport.Signature);
        Assert.Contains("./legacy.json", legacyImport.Signature);

        var viewImport = Assert.Single(symbols.Where(s => s.Kind == "import" && s.Name == "./view.js"));
        Assert.Equal(20, viewImport.Line);
        Assert.Contains("`./view.js`", viewImport.Signature);
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "./method");
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "./optional");
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "./private");
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "./string");
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "./comment");
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name.Contains("${", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("javascript")]
    [InlineData("typescript")]
    public void Extract_JavaScriptTypeScript_DetectsStaticImportModuleSymbols(string language)
    {
        var content = """
            import React from "react";
            import {
                computed,
                ref,
            } from
                "vue";
            import "./setup";
            import data from "./data.json" with { type: "json" };
            import legacy from "./legacy.json" assert {
                type: "json"
            };
            import { with as withAlias, assert as assertAlias } from "./keywords"
            const meta = import.meta.url;
            """;
        var symbols = SymbolExtractor.Extract(1, language, content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "react");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "vue");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./setup");
        var dataImport = Assert.Single(symbols.Where(s => s.Kind == "import" && s.Name == "./data.json"));
        Assert.Contains("with", dataImport.Signature);
        var legacyImport = Assert.Single(symbols.Where(s => s.Kind == "import" && s.Name == "./legacy.json"));
        Assert.Contains("assert", legacyImport.Signature);
        var keywordsImport = Assert.Single(symbols.Where(s => s.Kind == "import" && s.Name == "./keywords"));
        Assert.DoesNotContain("import.meta", keywordsImport.Signature);
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "meta");
    }

    [Fact]
    public void Extract_JavaScript_DetectsMultilineAnonymousDefaultExportClassMembers()
    {
        var content = """
            export default class
            extends Base
            {
                run() {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "default");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "default");
    }

    [Fact]
    public void Extract_JavaScript_WrappedBareMethodHeader_NormalizesCrlfToLf()
    {
        // JS/TS class-body methods whose header wraps across physical lines go through
        // TryCaptureJavaScriptTypeScriptMethodHeader, which appends each line with a '\n'
        // prefix. Without CRLF normalization, Windows sources (autocrlf=true, VS saves)
        // produce a Signature carrying '\r\n' between lines. Pin to '\n' for OS-independent
        // signature equality (#405 follow-up to #382).
        // JS/TS の class body method で header が行を跨ぐ場合、
        // TryCaptureJavaScriptTypeScriptMethodHeader が各行を '\n' 接頭辞で連結する。
        // CRLF 正規化がないと Windows ソース（autocrlf=true、VS 保存など）で Signature に
        // '\r\n' が混入していた。OS 差分で一致判定が崩れないよう '\n' に揃える
        // （#382 に続く #405 対応）。
        var content =
            "class Foo {\r\n" +
            "    myMethod(\r\n" +
            "        a,\r\n" +
            "        b,\r\n" +
            "    ) {\r\n" +
            "    }\r\n" +
            "}\r\n";
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        var method = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "myMethod"));
        Assert.NotNull(method.Signature);
        Assert.DoesNotContain('\r', method.Signature);
        Assert.Contains("\n", method.Signature);
    }

#if NET8_0
    [Fact]
#else
    [Fact(Skip = PracticalBudgetTestTarget.SecondaryTargetSkipReason)]
#endif
    public void Extract_JavaScriptLargeObjectLiteralTargets_CompletesWithinPracticalBudget()
    {
        const int objectCount = 1_000;
        var builder = new StringBuilder();
        for (var i = 0; i < objectCount; i++)
            builder.Append("export const obj").Append(i).Append(" = { run").Append(i).AppendLine("() { return 1; } };");

        var stopwatch = Stopwatch.StartNew();
        var symbols = SymbolExtractor.Extract(1, "javascript", builder.ToString());
        stopwatch.Stop();

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run0" && s.ContainerKind == "object" && s.ContainerName == "obj0");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == $"run{objectCount - 1}" && s.ContainerKind == "object" && s.ContainerName == $"obj{objectCount - 1}");
        var runawayBudget = TimeSpan.FromSeconds(10);
        Assert.True(
            stopwatch.Elapsed < runawayBudget,
            $"Large JavaScript object literal target extraction took {stopwatch.Elapsed.TotalSeconds:F2}s, expected < {runawayBudget.TotalSeconds:F0}s runaway guard budget.");
    }

    [Fact]
    public void Extract_JavaScript_DoesNotEmitObjectLiteralMembersForPlainValues()
    {
        var content = """
            const obj = {
                key: "value",
                count: 42,
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "key");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "count");
    }

    [Fact]
    public void Extract_JavaScript_DetectsClassFieldArrowWithAsiBetweenFields()
    {
        // ASI (Automatic Semicolon Insertion) between class fields must not swallow
        // the next arrow-property header into the previous expression body.
        // クラスフィールド間の ASI により式本体の後続フィールドを取りこぼさないこと。
        var content = """
            class Foo {
                first = () => 42
                second = () => 43
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        var first = symbols.FirstOrDefault(s => s.Kind == "function" && s.Name == "first");
        Assert.NotNull(first);
        Assert.Equal("class", first.ContainerKind);
        Assert.Equal("Foo", first.ContainerName);

        var second = symbols.FirstOrDefault(s => s.Kind == "function" && s.Name == "second");
        Assert.NotNull(second);
        Assert.Equal("class", second.ContainerKind);
        Assert.Equal("Foo", second.ContainerName);
    }

    [Theory]
    [InlineData("javascript")]
    [InlineData("typescript")]
    public void Extract_JavaScriptTypeScript_DetectsExportedObjectLiteralLiteralKeys(string language)
    {
        var content = """
            const handler = () => 1;
            const notFound = () => 2;
            const dynamicKey = "runtime";
            module.exports = {
                "x-api": handler,
                'content-type': handler,
                404: notFound,
                ["computed-api"]: handler,
                [500]: notFound,
                [dynamicKey]: handler,
            };
            export default {
                "dash-key": handler,
                ["computed-dash"]: handler,
            };
            """;
        var symbols = SymbolExtractor.Extract(1, language, content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "x-api" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "content-type" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "404" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "computed-api" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "500" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "dash-key" && s.ContainerKind == "object" && s.ContainerName == "default");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "computed-dash" && s.ContainerKind == "object" && s.ContainerName == "default");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "dynamicKey" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
    }

    [Fact]
    public void Extract_JavaScript_DetectsClassFieldArrowComputedMemberContinuation()
    {
        // A bare `[` on the next line is `foo[bar]` member-access continuation per JS ASI rules,
        // NOT a new computed class method. The scanner must not cut the expression body at `foo`.
        // JS の ASI 規則では、次行頭の `[` は `foo[bar]` メンバアクセスの継続であり、
        // computed method 名の開始ではない。式本体を `foo` で打ち切ってはならない。
        var content = """
            class Foo {
              first = () => foo
                [bar];
              second = () => 43;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        var first = symbols.FirstOrDefault(s => s.Kind == "function" && s.Name == "first");
        Assert.NotNull(first);
        Assert.Equal("class", first.ContainerKind);
        Assert.Equal("Foo", first.ContainerName);
        Assert.Contains("[bar]", first.Signature);

        var second = symbols.FirstOrDefault(s => s.Kind == "function" && s.Name == "second");
        Assert.NotNull(second);
        Assert.Equal("class", second.ContainerKind);
    }

    [Fact]
    public void Extract_JavaScript_DetectsClassFieldArrowStringLiteralBeforeClosingBrace()
    {
        // A string-returning arrow without a trailing `;` must be terminated by the class-body `}`.
        // The lexer preserves opening/closing quote characters in the sanitized header, so the
        // ASI terminator check must treat `"` / `'` / `` ` `` as valid expression ends.
        // セミコロンなしで文字列を返す矢印フィールドは、直後のクラス終了 `}` で終端されなければならない。
        // lexer は開閉クォートを sanitized header 上に残すため、ASI 終端チェックは
        // `"` / `'` / `` ` `` を有効な式終端として扱わなければならない。
        var content = """
            class Foo {
              only = () => "x"
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        var only = symbols.FirstOrDefault(s => s.Kind == "function" && s.Name == "only");
        Assert.NotNull(only);
        Assert.Equal("class", only.ContainerKind);
        Assert.Equal("Foo", only.ContainerName);
    }

    [Fact]
    public void Extract_JavaScript_DetectsClassFieldArrowStringLiteralWithAsiBetweenFields()
    {
        var content = """
            class Foo {
              first = () => "x"
              second = () => 43
              third = () => `template`
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "first" && s.ContainerName == "Foo");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "second" && s.ContainerName == "Foo");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "third" && s.ContainerName == "Foo");
    }
}
