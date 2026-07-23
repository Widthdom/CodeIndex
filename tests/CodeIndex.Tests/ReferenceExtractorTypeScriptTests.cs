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
    public void Extract_TypeScriptGenericConditionalConstraint_EmitsBranchTypeReferences()
    {
        const string content = """
            export function unwrap<T extends Array<infer U> ? Promise<U[]> : Nested<Fallback>>(value: T): T {
                return value;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "typescript", content);
        var references = ReferenceExtractor.Extract(1, "typescript", content, symbols);

        AssertReferencesContain(references, "type_reference", "unwrap", "Array", "U", "Promise", "Nested", "Fallback");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "infer"
            && reference.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_TypeScriptTypeAliasGenericDefaults_EmitsDefaultAndRhsTypeReferences()
    {
        const string content = """
            type DefaultKey = string;
            type DefaultValue = unknown;
            type Dict<K = DefaultKey, V = DefaultValue> = Record<K, V>;
            """;

        var symbols = SymbolExtractor.Extract(1, "typescript", content);
        var references = ReferenceExtractor.Extract(1, "typescript", content, symbols);

        AssertReferencesContainInContext(
            references,
            "type_reference",
            "type Dict<K = DefaultKey, V = DefaultValue> = Record<K, V>;",
            "DefaultKey",
            "DefaultValue",
            "Record");
    }

    [Fact]
    public void Extract_TypeScriptTypeAliasExpansion_CoversHeritageGenericAndValuePositions()
    {
        const string content = """
            class PlainTarget {}
            class FunctionTarget {}
            class Arg {}
            type PlainAlias = PlainTarget;
            type FunctionAlias<T> = (value: T) => FunctionTarget;
            class PlainDerived extends PlainAlias {}
            class FunctionDerived extends FunctionAlias<Arg> {}
            function get(value: unknown) { return value; }
            const x: PlainAlias = get(PlainAlias);
            """;

        var symbols = SymbolExtractor.Extract(1, "typescript", content);
        var references = ReferenceExtractor.Extract(1, "typescript", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "PlainAlias"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "PlainDerived");
        Assert.Contains(references, reference =>
            reference.SymbolName == "PlainTarget"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "PlainDerived"
            && reference.Context == "class PlainDerived extends PlainAlias {}");
        Assert.Contains(references, reference =>
            reference.SymbolName == "FunctionTarget"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "FunctionDerived"
            && reference.Context == "class FunctionDerived extends FunctionAlias<Arg> {}");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "T"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "FunctionDerived");

        var expanded = references
            .Where(reference =>
                reference.SymbolName == "PlainTarget"
                && reference.ReferenceKind == "type_reference"
                && reference.Context == "const x: PlainAlias = get(PlainAlias);")
            .ToList();

        Assert.Single(expanded);
        Assert.Equal(10, expanded[0].Column);
    }

#if NET8_0
    [Fact]
#else
    [Fact(Skip = PracticalBudgetTestTarget.SecondaryTargetSkipReason)]
#endif
    public void Extract_TypeScriptLargeTypeAliasUseSet_CompletesWithinPracticalBudget()
    {
        const int useCount = 500;
        var uses = string.Join('\n', Enumerable.Range(0, useCount).Select(index => $"let v{index}: MyAlias = value;"));
        var content = $$"""
            class SomeType {}
            type MyAlias = SomeType;
            {{uses}}
            """;

        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        var stopwatch = Stopwatch.StartNew();
        var references = ReferenceExtractor.Extract(1, "typescript", content, symbols);
        stopwatch.Stop();

        Assert.Contains(references, reference =>
            reference.SymbolName == "SomeType"
            && reference.ReferenceKind == "type_reference"
            && reference.Context == "let v0: MyAlias = value;");
        Assert.Contains(references, reference =>
            reference.SymbolName == "SomeType"
            && reference.ReferenceKind == "type_reference"
            && reference.Context == $"let v{useCount - 1}: MyAlias = value;");
        var runawayBudget = TimeSpan.FromSeconds(5);
        Assert.True(
            stopwatch.Elapsed < runawayBudget,
            $"Large TypeScript type alias reference extraction took {stopwatch.Elapsed.TotalSeconds:F2}s, expected < {runawayBudget.TotalSeconds:F0}s runaway guard budget.");
    }

    [Fact]
    public void Extract_TypeScriptTypeAliasWithGenericDefault_EmitsUnderlyingTypeReference()
    {
        const string content = """
            class DefaultKey {}
            class SomeType {}
            class Arg {}
            type MyAlias<T = DefaultKey> = SomeType & Box<T>;
            class Derived extends MyAlias<Arg> {}
            """;

        var symbols = SymbolExtractor.Extract(1, "typescript", content);
        var references = ReferenceExtractor.Extract(1, "typescript", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "DefaultKey"
            && reference.ReferenceKind == "type_reference"
            && reference.Context == "type MyAlias<T = DefaultKey> = SomeType & Box<T>;");
        Assert.Contains(references, reference =>
            reference.SymbolName == "SomeType"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "Derived"
            && reference.Context == "class Derived extends MyAlias<Arg> {}");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "T"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "Derived"
            && reference.Context == "class Derived extends MyAlias<Arg> {}");
    }

    [Fact]
    public void Extract_TypeScriptTypeAliasShadowedByScope_UsesActiveAliasBinding()
    {
        const string content = """
            class One {}
            class Two {}
            type MyAlias = One;
            namespace Inner {
                type MyAlias = Two;
                export class B extends MyAlias {}
            }
            class A extends MyAlias {}
            """;

        var symbols = SymbolExtractor.Extract(1, "typescript", content);
        var references = ReferenceExtractor.Extract(1, "typescript", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "One"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "A"
            && reference.Context == "class A extends MyAlias {}");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "Two"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "A"
            && reference.Context == "class A extends MyAlias {}");
        Assert.Contains(references, reference =>
            reference.SymbolName == "Two"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "B"
            && reference.Context == "export class B extends MyAlias {}");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "One"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "B"
            && reference.Context == "export class B extends MyAlias {}");
    }

    [Fact]
    public void Extract_TypeScriptTypeAliasShadowedByTypeDeclaration_DoesNotExpandOuterAlias()
    {
        const string content = """
            class One {}
            type MyAlias = One;
            namespace Inner {
                class MyAlias {}
                export class B extends MyAlias {}
            }
            class Box<MyAlias> extends MyAlias {}
            """;

        var symbols = SymbolExtractor.Extract(1, "typescript", content);
        var references = ReferenceExtractor.Extract(1, "typescript", content, symbols);

        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "One"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "B"
            && reference.Context == "export class B extends MyAlias {}");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "One"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "Box"
            && reference.Context == "class Box<MyAlias> extends MyAlias {}");
    }

    [Fact]
    public void Extract_TypeScriptLineContinuationString_DoesNotLeakPhantomReferences()
    {
        const string content = """
            function caller() {
              const s = 'line1\
            } externalCall() line2';
              runTask();
            }
            function runTask() {}
            """;

        var symbols = SymbolExtractor.Extract(1, "typescript", content);
        var references = ReferenceExtractor.Extract(1, "typescript", content, symbols);

        Assert.Contains(references, reference => reference.SymbolName == "runTask" && reference.ContainerName == "caller");
        Assert.DoesNotContain(references, reference => reference.SymbolName == "externalCall");
    }

    [Fact]
    public void Extract_TypeScriptTypeQueries_DistinguishTypeAndRuntimeLayouts()
    {
        const string content = """
            class Point {}

            type PointCtor = typeof Point;
            type PointKeys = keyof Point;
            type PointCtorMultiline =
                typeof Point;

            function caller(valueWrapped: unknown, valueMultiline: unknown) {
              const runtime =
                typeof valueWrapped === "string";
              const another =
                Promise<
                  string
                >;
              const multilineRuntime =
                typeof valueMultiline === "string";
              return runtime && multilineRuntime && another.length > 0;
            }
            const inline = (valueInline: unknown) => typeof valueInline === "string";
            """;

        var symbols = SymbolExtractor.Extract(1, "typescript", content);
        var references = ReferenceExtractor.Extract(1, "typescript", content, symbols);

        Assert.Equal(3, references.Count(reference =>
            reference.SymbolName == "Point"
            && reference.ReferenceKind == "type_reference"));
        Assert.DoesNotContain(references, reference =>
            reference.ReferenceKind == "type_reference"
            && (reference.SymbolName == "valueWrapped"
                || reference.SymbolName == "valueMultiline"
                || reference.SymbolName == "valueInline"));
    }

    [Fact]
    public void Extract_TypeScriptTypeQueries_CaptureImportAndWrappedTargets()
    {
        const string content = """
            class Point {}

            type ImportedPoint = typeof import("./point").Point;
            type KeyedPoint = keyof typeof import("./point").Point;
            type WrappedPoint =
                Promise<
                    typeof Point
                >;
            """;

        var symbols = SymbolExtractor.Extract(1, "typescript", content);
        var references = ReferenceExtractor.Extract(1, "typescript", content, symbols);

        Assert.Equal(5, references.Count(reference =>
            reference.SymbolName == "Point"
            && reference.ReferenceKind == "type_reference"));
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "import"
            && reference.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_TypeScriptTaggedTemplateLayouts_CaptureCallableTags()
    {
        // issue #268: member-access tags like `styled.button\`...\`` must emit a `call` row on
        // the last segment so the existing CallRegex convention (capture the final identifier)
        // carries over to tagged templates.
        // issue #268: `styled.button\`...\`` のようなメンバアクセスタグは、既存 CallRegex の
        // 規約に揃えて末尾セグメントを `call` として発行する。
        // Generic tags must skip balanced function types, and nested tags inside an outer
        // interpolation hole must retain both opener references.
        // generic tag は function type を正しく読み飛ばし、外側 interpolation hole 内の
        // nested tag は両方の opener reference を維持する。
        const string content = """
            const Btn = styled.button`
              color: red;
            `;

            function render(user: User) {
                return html<User>`<p>${user.name}</p>`;
            }
            function renderFunctionType<U>(value: U) {
                return tag<(x: number) => U>`value=${value}`;
            }
            function demo(user: User) {
                return outer`header ${inner`${user.name}`} footer`;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "typescript", content);
        var references = ReferenceExtractor.Extract(1, "typescript", content, symbols);

        var button = Assert.Single(references.Where(r => r.SymbolName == "button"));
        var html = Assert.Single(references.Where(r => r.SymbolName == "html"));
        Assert.Equal("call", button.ReferenceKind);
        Assert.Equal("call", html.ReferenceKind);
        Assert.Equal("render", html.ContainerName);
        var tag = Assert.Single(references.Where(r => r.SymbolName == "tag" && r.ReferenceKind == "call"));
        Assert.Equal("renderFunctionType", tag.ContainerName);
        Assert.Contains(references, r => r.SymbolName == "outer" && r.ReferenceKind == "call" && r.ContainerName == "demo");
        Assert.Contains(references, r => r.SymbolName == "inner" && r.ReferenceKind == "call" && r.ContainerName == "demo");
        Assert.DoesNotContain(references, r => r.SymbolName == "color");
        Assert.DoesNotContain(references, r => r.SymbolName == "red");
    }

    [Fact]
    public void Extract_TypeScriptJsxComponentOpenTags_CaptureCapitalizedElementUsages()
    {
        const string content = """
            function MyButton({ label }: { label: string }) {
                return <button>{label}</button>;
            }

            function Header() {
                return <h1>hi</h1>;
            }

            const UI = { Card: ({ children }: any) => <div>{children}</div> };

            export default function App() {
                const n = 0;
                return (
                    <div>
                        <Header />
                        <MyButton label="click" />
                        <MyButton label={`n=${n}`} />
                        <UI.Card>
                            <MyButton label="nested" />
                        </UI.Card>
                        <MyButton
                            label="multiline"
                        />
                        <MyButton label={String(n)} />
                    </div>
                );
            }
            """;

        var references = ReferenceExtractor.Extract(
            1,
            "typescript",
            content,
            Array.Empty<CodeIndex.Models.SymbolRecord>(),
            path: "App.tsx");

        Assert.Contains(references, r =>
            r.SymbolName == "MyButton"
            && r.ReferenceKind == "call"
            && r.Context.StartsWith("<MyButton", StringComparison.Ordinal));
        Assert.Contains(references, r =>
            r.SymbolName == "Header"
            && r.ReferenceKind == "call"
            && r.Context.StartsWith("<Header", StringComparison.Ordinal));
        Assert.Contains(references, r =>
            r.SymbolName == "UI"
            && r.ReferenceKind == "call"
            && r.Context.StartsWith("<UI.Card", StringComparison.Ordinal));
        Assert.Contains(references, r =>
            r.SymbolName == "Card"
            && r.ReferenceKind == "call"
            && r.Context.StartsWith("<UI.Card", StringComparison.Ordinal));
        Assert.Contains(references, r =>
            r.SymbolName == "String"
            && r.ReferenceKind == "call"
            && r.Context.Contains("String(n)", StringComparison.Ordinal));
        Assert.DoesNotContain(references, r => r.SymbolName is "button" or "div" or "h1");
    }

    [Fact]
    public void Extract_TypeScriptJsxComponentOpenTags_AreIgnoredOutsideJsxFiles()
    {
        const string content = """
            function MyButton({ label }: { label: string }) {
                return <button>{label}</button>;
            }

            function Header() {
                return <h1>hi</h1>;
            }

            const UI = { Card: ({ children }: any) => <div>{children}</div> };

            export default function App() {
                const n = 0;
                return (
                    <div>
                        <Header />
                        <MyButton label="click" />
                        <MyButton label={`n=${n}`} />
                        <UI.Card>
                            <MyButton label="nested" />
                        </UI.Card>
                        <MyButton
                            label="multiline"
                        />
                        <MyButton label={String(n)} />
                    </div>
                );
            }
            """;

        var references = ReferenceExtractor.Extract(
            1,
            "typescript",
            content,
            Array.Empty<CodeIndex.Models.SymbolRecord>(),
            path: "App.ts");

        Assert.DoesNotContain(references, r =>
            r.SymbolName == "MyButton"
            && r.ReferenceKind == "call"
            && r.Context.StartsWith("<", StringComparison.Ordinal));
        Assert.DoesNotContain(references, r =>
            r.SymbolName == "Header"
            && r.ReferenceKind == "call"
            && r.Context.StartsWith("<", StringComparison.Ordinal));
        Assert.DoesNotContain(references, r =>
            r.SymbolName == "UI"
            && r.ReferenceKind == "call"
            && r.Context.StartsWith("<", StringComparison.Ordinal));
        Assert.DoesNotContain(references, r =>
            r.SymbolName == "Card"
            && r.ReferenceKind == "call"
            && r.Context.StartsWith("<", StringComparison.Ordinal));
        Assert.Contains(references, r => r.SymbolName == "String" && r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_TypeScriptTypedDeclarations_CaptureStructuralTypeReferences()
    {
        const string content = """
            interface Handler extends BaseHandler<Request>, Disposable {
                handle?: (callbackInput: Request) => Response;
            }

            class Service extends BaseService implements Runnable, Closeable {
                load(input: User, options?: LoadOptions): Promise<Result> {
                    const current: User = input as User;
                    const active = current as User && current.enabledFlag;
                    const fallback = current as User || fallbackValue;
                    const ready = current instanceof Service && current.readyFlag;
                    const checked = current satisfies Runnable ? current : fallbackValue;
                    const handler: (variableInput: Request) => Response = makeHandler();
                    return build(current);
                }
            }

            type HandlerFactory = (factoryInput: Request) => Response;
            type UserPick = Pick<User, "id">;

            function runtime(input: unknown) {
                return typeof input === "string";
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "typescript", content);
        var references = ReferenceExtractor.Extract(1, "typescript", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "BaseHandler" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Request" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Disposable" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "BaseService" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Runnable" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Closeable" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "User" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "LoadOptions" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Promise" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Result" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Pick" && r.ReferenceKind == "type_reference");
        Assert.Equal(3, references.Count(r => r.SymbolName == "Response" && r.ReferenceKind == "type_reference"));
        Assert.DoesNotContain(references, r => r.SymbolName == "callbackInput" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "variableInput" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "factoryInput" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "enabledFlag" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "fallbackValue" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "readyFlag" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "input" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "string" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_TypeScriptSatisfies_CapturesTypeReferencesWithoutCalls()
    {
        const string content = """
            const config = { port: 8080 } satisfies ServerConfig;
            const wrapped = (x: number) => x satisfies Brand;
            const chained = config satisfies ServerConfig satisfies RuntimeConfig;
            const nested = wrap<ServerConfig satisfies RuntimeConfig>(config);
            const parser = {} satisfies { parse(input: Request): Response };
            """;

        var symbols = SymbolExtractor.Extract(1, "typescript", content);
        var references = ReferenceExtractor.Extract(1, "typescript", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "ServerConfig" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Brand" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "RuntimeConfig" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Request" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Response" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "ServerConfig" && r.ReferenceKind == "call");
        Assert.DoesNotContain(references, r => r.SymbolName == "Brand" && r.ReferenceKind == "call");
        Assert.DoesNotContain(references, r => r.SymbolName == "RuntimeConfig" && r.ReferenceKind == "call");
        Assert.DoesNotContain(references, r => r.SymbolName == "parse" && r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_TypeScriptConstAssertion_CapturesSyntheticAndLiteralTypeReferences()
    {
        const string content = """
            const tuple = ["alpha", "beta"] as const;
            const config = { mode: "strict", retries: 3, enabled: true } as const;
            const escaped = { pattern: "{", closing: "]", mode: "strict" } as const;
            const multilineTuple = [
                "multi-alpha",
                "multi-beta"
            ] as const;
            const multilineConfig = {
                mode: "wide",
                retries: 5
            } as const;
            const withComments = [/* 99 */ "kept", // 100
                101
            ] as const;
            const urlTuple = ["https://example.test/a", "after-url"] as const;
            const expressions = [1 + 2, flag ? "yes" : "no", { "quotedKey": "quotedValue" }] as const;
            const compactExpressions = [1+2, 1-2, -1] as const;
            const radixNumbers = [0x10, 0b1010, 0o755, 123n] as const;
            const cast = value as RuntimeConfig;
            """;

        var symbols = SymbolExtractor.Extract(1, "typescript", content);
        var references = ReferenceExtractor.Extract(1, "typescript", content, symbols);

        Assert.Equal(10, references.Count(r => r.SymbolName == "const" && r.ReferenceKind == "const_assertion"));
        Assert.Contains(references, r => r.SymbolName == "\"alpha\"" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "\"beta\"" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "\"strict\"" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "\"{\"" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "\"]\"" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "\"multi-alpha\"" && r.ReferenceKind == "type_reference" && r.Line == 5);
        Assert.Contains(references, r => r.SymbolName == "\"multi-beta\"" && r.ReferenceKind == "type_reference" && r.Line == 6);
        Assert.Contains(references, r => r.SymbolName == "\"wide\"" && r.ReferenceKind == "type_reference" && r.Line == 9);
        Assert.Contains(references, r => r.SymbolName == "5" && r.ReferenceKind == "type_reference" && r.Line == 10);
        Assert.Contains(references, r => r.SymbolName == "\"kept\"" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "101" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "\"https://example.test/a\"" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "\"after-url\"" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "\"quotedValue\"" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "-1" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "0x10" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "0b1010" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "0o755" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "123n" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "3" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "true" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "RuntimeConfig" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "3" && r.ReferenceKind == "type_reference" && r.Column == 43);
        Assert.Contains(references, r => r.SymbolName == "true" && r.ReferenceKind == "type_reference" && r.Column == 55);
        Assert.DoesNotContain(references, r => r.SymbolName is "99" or "100" or "1" or "2" or "1+2" or "1-2");
        Assert.DoesNotContain(references, r => r.SymbolName is "\"yes\"" or "\"no\"" or "\"quotedKey\"");
        Assert.DoesNotContain(references, r => r.SymbolName == "const" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "RuntimeConfig" && r.ReferenceKind == "const_assertion");
    }

    [Fact]
    public void Extract_TypeScriptDecoratedMembers_CaptureDecoratorAndTypeReferences()
    {
        const string content = """
            class Controller {
                @Get("/users") find(@Optional() @Inject(USER_REPOSITORY) repo: UserRepository, @Param("id") id: UserId): Promise<UserDto> {
                    return repo.find(id);
                }

                @Input() profile: UserProfile;
                @Column({ type: "json" }) settings!: SettingsDocument;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "typescript", content);
        var references = ReferenceExtractor.Extract(1, "typescript", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "Get" && r.ReferenceKind == "annotation");
        Assert.Contains(references, r => r.SymbolName == "Optional" && r.ReferenceKind == "annotation");
        Assert.Contains(references, r => r.SymbolName == "Inject" && r.ReferenceKind == "annotation");
        Assert.Contains(references, r => r.SymbolName == "Param" && r.ReferenceKind == "annotation");
        Assert.Contains(references, r => r.SymbolName == "Input" && r.ReferenceKind == "annotation");
        Assert.Contains(references, r => r.SymbolName == "Column" && r.ReferenceKind == "annotation");

        Assert.Contains(references, r => r.SymbolName == "UserRepository" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "UserId" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Promise" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "UserDto" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "UserProfile" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "SettingsDocument" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_TypeScriptImportExportAliases_DoNotEmitTypeReferences()
    {
        const string content = """
            import * as Mod from "mod";
            import { Foo as Bar } from "mod";
            export { Foo as Baz } from "mod";
            export type { Model as PublicModel } from "mod";
            export * as NamespaceMod from "mod";
            export type * as PublicNamespace from "mod";
            import*as CompactImport from "mod";
            export*as CompactNamespace from "mod";
            export type*as CompactPublicNamespace from "mod";
            import {
                MultiFoo as MultiBar
            } from "mod";
            import DefaultThing, {
                Secondary as SecondaryAlias
            } from "mod";
            export {
                MultiExport as MultiBaz
            } from "mod";
            export type {
                Internal as PublicInternal
            } from "mod";
            import Existing from "mod";

            {
                const checked = value as RealType;
            }
            import "polyfill"

            {
                const checkedAfterSideEffect = value as AfterSideEffectType;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "typescript", content);
        var references = ReferenceExtractor.Extract(1, "typescript", content, symbols);

        Assert.DoesNotContain(references, r => r.SymbolName == "Mod" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "Bar" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "Baz" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "PublicModel" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "NamespaceMod" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "PublicNamespace" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "CompactImport" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "CompactNamespace" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "CompactPublicNamespace" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "MultiBar" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "SecondaryAlias" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "MultiBaz" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "PublicInternal" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "from" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "RealType" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "AfterSideEffectType" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_TypeScriptTypeQuery_DynamicImportTypeMapsToImportSymbol()
    {
        const string content = """
            const loaded = import("./mod");
            type ModuleNamespace = typeof import("./mod");
            """;

        var symbols = SymbolExtractor.Extract(1, "typescript", content);
        var references = ReferenceExtractor.Extract(1, "typescript", content, symbols);

        Assert.Contains(symbols, symbol =>
            symbol.Kind == "import"
            && symbol.Name == "./mod");
        Assert.Contains(references, reference =>
            reference.SymbolName == "./mod"
            && reference.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_TypeScriptMappedAndConditionalTypes_EmitTypeParameterReferences()
    {
        const string content = """
            type Getters<T> = {
              [K in keyof T as `get${Capitalize<K>}`]: () => T[K];
            };
            type AwaitedValue<T> = T extends Promise<infer U> ? U : never;
            function useApi(api: Api) {
              api.in();
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "typescript", content);
        var references = ReferenceExtractor.Extract(1, "typescript", content, symbols);

        Assert.Contains(references, r =>
            r.SymbolName == "T" && r.ReferenceKind == "type_reference" && r.Line == 2);
        Assert.Contains(references, r =>
            r.SymbolName == "K" && r.ReferenceKind == "type_reference" && r.Line == 2);
        Assert.Contains(references, r =>
            r.SymbolName == "Capitalize" && r.ReferenceKind == "type_reference" && r.Line == 2);
        Assert.Contains(references, r =>
            r.SymbolName == "T" && r.ReferenceKind == "type_reference" && r.Line == 4);
        Assert.Contains(references, r =>
            r.SymbolName == "Promise" && r.ReferenceKind == "type_reference" && r.Line == 4);
        Assert.Contains(references, r =>
            r.SymbolName == "U" && r.ReferenceKind == "type_reference" && r.Line == 4);
        Assert.Contains(references, r =>
            r.SymbolName == "in" && r.ReferenceKind == "call" && r.Line == 6);
        var forbiddenTypeOperators = references
            .Where(r =>
                (r.SymbolName is "keyof" or "in" or "as" or "extends" or "infer" or "never")
                && r.ReferenceKind == "type_reference")
            .Select(r => $"{r.SymbolName}@{r.Line}:{r.Column}");
        Assert.Empty(forbiddenTypeOperators);
    }

    [Fact]
    public void Extract_TypeScriptTypeExpressions_IgnoreTemplateRawTextAndStringKeys()
    {
        const string content = """
            class User {}

            type Label = `prefix;${User}_suffix`;
            type UserId = User["id"];
            """;

        var symbols = SymbolExtractor.Extract(1, "typescript", content);
        var references = ReferenceExtractor.Extract(1, "typescript", content, symbols);

        Assert.Equal(2, references.Count(r => r.SymbolName == "User" && r.ReferenceKind == "type_reference"));
        Assert.DoesNotContain(references, r => r.SymbolName == "prefix" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "suffix" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "id" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_TypeScriptOptionalChains_EmitsFullMemberChains()
    {
        const string content = """
            export function render(viewModel: ViewModel) {
                viewModel?.profile?.avatar?.url ?? fallbackUrl;
                viewModel?.profile?.load?.();
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "typescript", content);
        var references = ReferenceExtractor.Extract(1, "typescript", content, symbols);

        Assert.Contains(references, r =>
            r.SymbolName == "viewModel.profile"
            && r.ReferenceKind == "reference"
            && r.ContainerName == "render");
        Assert.Contains(references, r =>
            r.SymbolName == "viewModel.profile.avatar"
            && r.ReferenceKind == "reference"
            && r.ContainerName == "render");
        Assert.Contains(references, r =>
            r.SymbolName == "viewModel.profile.avatar.url"
            && r.ReferenceKind == "reference"
            && r.ContainerName == "render");
        Assert.Contains(references, r =>
            r.SymbolName == "viewModel.profile.load"
            && r.ReferenceKind == "reference"
            && r.ContainerName == "render");
    }

    [Theory]
    [InlineData("javascript")]
    [InlineData("typescript")]
    public void Extract_JavaScriptTypeScriptDiscriminantStringGuard_EmitsPropertyAndTypeTag(string language)
    {
        const string content = """
            export function area(shape) {
                if (shape.type === 'circle') {
                    return shape.radius;
                }
                /* x.kind === 'fake' */ if (shape.type === 'circle' || shape.type === 'square') {
                    return 0;
                }
            }
            """;

        var (_, references) = ExtractSymbolsAndReferences(language, content);

        Assert.Contains(references, r =>
            r.SymbolName == "shape.type"
            && r.ReferenceKind == "reference"
            && r.ContainerName == "area");
        Assert.Contains(references, r =>
            r.SymbolName == "type"
            && r.ReferenceKind == "reference"
            && r.ContainerName == "area");
        Assert.Contains(references, r =>
            r.SymbolName == "shape.type=circle"
            && r.ReferenceKind == "type_tag"
            && r.ContainerName == "area");
        Assert.Contains(references, r =>
            r.SymbolName == "shape.type=square"
            && r.ReferenceKind == "type_tag"
            && r.ContainerName == "area");
        Assert.DoesNotContain(references, r =>
            r.SymbolName == "shape.type=fake"
            && r.ReferenceKind == "type_tag");
    }

    [Fact]
    public void Extract_TypeScriptNamespaceReExportQualifiedUsage_EmitsModuleReference()
    {
        const string content = """
            export * as Widgets from "./widgets";

            export function render() {
                return Widgets.Button.create();
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "typescript", content);
        var references = ReferenceExtractor.Extract(1, "typescript", content, symbols);

        Assert.Contains(references, r =>
            r.SymbolName == "./widgets"
            && r.ReferenceKind == "reference"
            && r.Line == 4
            && r.ContainerKind == "function"
            && r.ContainerName == "render");
        Assert.Contains(references, r => r.SymbolName == "create" && r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_TypeScriptNamespaceImportQualifiedUsage_StopsAfterLocalShadow()
    {
        const string content = """
            import * as Api from "./api";

            export function before() {
                Api.Client.connect();
            }

            const Api = localFactory();

            export function after() {
                Api.Client.connect();
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "typescript", content);
        var references = ReferenceExtractor.Extract(1, "typescript", content, symbols);

        Assert.Contains(references, r =>
            r.SymbolName == "./api"
            && r.ReferenceKind == "reference"
            && r.Line == 4
            && r.ContainerName == "before");
        Assert.DoesNotContain(references, r =>
            r.SymbolName == "./api"
            && r.ReferenceKind == "reference"
            && r.Line == 10);
    }

    [Fact]
    public void Extract_TypeScriptDynamicImportNamespaceQualifiedUsage_EmitsModuleReference()
    {
        const string content = """
            async function render() {
                const Lazy = await import("./lazy");
                return Lazy.Widget.mount();
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "typescript", content);
        var references = ReferenceExtractor.Extract(1, "typescript", content, symbols);

        Assert.Contains(references, r =>
            r.SymbolName == "./lazy"
            && r.ReferenceKind == "reference"
            && r.Line == 3
            && r.ContainerName == "render");
    }

    [Fact]
    public void Extract_TypeScriptDynamicImportNamespaceQualifiedUsage_StopsOutsideLocalScope()
    {
        const string content = """
            async function render() {
                const Lazy = await import("./lazy");
                return Lazy.Widget.mount();
            }

            export function after(Lazy: any) {
                return Lazy.Widget.mount();
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "typescript", content);
        var references = ReferenceExtractor.Extract(1, "typescript", content, symbols);

        Assert.Contains(references, r =>
            r.SymbolName == "./lazy"
            && r.ReferenceKind == "reference"
            && r.Line == 3
            && r.ContainerName == "render");
        Assert.DoesNotContain(references, r =>
            r.SymbolName == "./lazy"
            && r.ReferenceKind == "reference"
            && r.Line == 7);
    }

    [Fact]
    public void Extract_TypeScriptNamedImportAliasQualifiedUsage_EmitsModuleReference()
    {
        const string content = """
            import { InternalNamespace as PublicNamespace } from "./public-api";

            export function render() {
                PublicNamespace.Widget.mount();
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "typescript", content);
        var references = ReferenceExtractor.Extract(1, "typescript", content, symbols);

        Assert.Contains(references, r =>
            r.SymbolName == "./public-api"
            && r.ReferenceKind == "reference"
            && r.Line == 4
            && r.ContainerName == "render");
    }

    [Fact]
    public void Extract_TypeScriptNamedReExportAliasQualifiedUsage_DoesNotCreateLocalModuleReference()
    {
        const string content = """
            export { InternalNamespace as PublicNamespace } from "./public-api";

            export function render() {
                PublicNamespace.Widget.mount();
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "typescript", content);
        var references = ReferenceExtractor.Extract(1, "typescript", content, symbols);

        Assert.DoesNotContain(references, r =>
            r.SymbolName == "./public-api"
            && r.ReferenceKind == "reference"
            && r.Line == 4);
    }

    [Fact]
    public void Extract_TypeScriptNamespaceImportQualifiedUsage_StopsInsideParameterShadowScope()
    {
        const string content = """
            import * as Api from "./api";
            import { InternalNamespace as PublicNamespace } from "./public-api";

            export function before() {
                Api.Client.connect();
                PublicNamespace.Widget.mount();
            }

            export function shadowed(Api: LocalApi, PublicNamespace: LocalPublic) {
                Api.Client.connect();
                PublicNamespace.Widget.mount();
            }

            export function after() {
                Api.Client.connect();
                PublicNamespace.Widget.mount();
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "typescript", content);
        var references = ReferenceExtractor.Extract(1, "typescript", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "./api" && r.ReferenceKind == "reference" && r.Line == 5);
        Assert.Contains(references, r => r.SymbolName == "./public-api" && r.ReferenceKind == "reference" && r.Line == 6);
        Assert.DoesNotContain(references, r => r.SymbolName == "./api" && r.ReferenceKind == "reference" && r.Line == 10);
        Assert.DoesNotContain(references, r => r.SymbolName == "./public-api" && r.ReferenceKind == "reference" && r.Line == 11);
        Assert.Contains(references, r => r.SymbolName == "./api" && r.ReferenceKind == "reference" && r.Line == 15);
        Assert.Contains(references, r => r.SymbolName == "./public-api" && r.ReferenceKind == "reference" && r.Line == 16);
    }

    [Fact]
    public void Extract_TypeScriptNamespaceImportQualifiedUsage_ManyAliasesAcrossManyLines()
    {
        var imports = string.Join(
            "\n",
            Enumerable.Range(0, 12).Select(index => $"import * as Api{index} from \"./api{index}\";"));
        var calls = string.Join(
            "\n",
            Enumerable.Range(0, 12).Select(index => $"    Api{index}.Client.connect();"));
        var content = $$"""
            {{imports}}

            export function render() {
            {{calls}}
                Api1Extra.Client.connect();
                OtherApi1.Client.connect();
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "typescript", content);
        var references = ReferenceExtractor.Extract(1, "typescript", content, symbols);

        for (var index = 0; index < 12; index++)
        {
            var expectedLine = 15 + index;
            Assert.Contains(references, r =>
                r.SymbolName == $"./api{index}"
                && r.ReferenceKind == "reference"
                && r.Line == expectedLine
                && r.ContainerName == "render");
        }

        Assert.DoesNotContain(references, r =>
            r.SymbolName == "./api1"
            && r.ReferenceKind == "reference"
            && r.Line is 27 or 28);
    }
}
