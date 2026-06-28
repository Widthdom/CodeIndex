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
    public void Extract_TypeScript_DetectsDeclareExportedVariableSurfaceSymbols()
    {
        var content = """
            export declare const externalThing: string;
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "externalThing" && s.Visibility == "export");
    }

    [Fact]
    public void Extract_TypeScript_DetectsLocalTypeOnlyNamedExportSurfaceSymbols()
    {
        var content = """
            type User = { id: string };
            type Admin = User & { role: string };
            export type { User, Admin as RootAdmin };
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "User" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "RootAdmin" && s.Visibility == "export");
    }

    [Fact]
    public void Extract_TypeScript_DetectsNamedAndTypeReExportSurfaceSymbols()
    {
        var content = """
            export {
              foo, // from './bogus'
              bar,
            } from './other';
            export { default as Helper } from './helper'; // trailing comment
            export type {
              User,
              Admin,
            } from './types';
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./other");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./helper");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./types");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "foo");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "bar");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Helper");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "User");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Admin");
    }

    [Fact]
    public void Extract_TypeScript_DetectsTypeOnlyStarReExportSurfaceSymbols()
    {
        var content = """
            export type * from './types';
            export type * as ns from './types-ns';
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./types");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./types-ns");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "ns");
    }

    [Fact]
    public void Extract_TypeScript_DetectsImportEqualsRequireSurfaceSymbols()
    {
        var content = """
            import fs = require('node:fs');
            import path = require('./path-utils');
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Single(symbols.Where(s => s.Kind == "import" && s.Name == "node:fs"));
        Assert.Single(symbols.Where(s => s.Kind == "import" && s.Name == "./path-utils"));
    }

    [Fact]
    public void Extract_TypeScript_DetectsReExportSurfaceSymbolsWithImportAttributes()
    {
        var content = """
            export * from './util' with { type: 'json' };
            export { foo as bar } from './other' with { type: 'json' };
            export * from './legacy' assert { type: 'json' };
            export { baz as qux } from './older' assert { type: 'json' };
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./util");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./other");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./legacy");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./older");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "bar");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "qux");
    }

    [Fact]
    public void Extract_TypeScript_DetectsMultilineStarReExportSurfaceSymbolsWithImportAttributes()
    {
        var content = """
            export * from './util' with {
              type: 'json'
            };
            export * as ns from './other' assert {
              type: 'json'
            };
            export type * from './types' with {
              type: 'json'
            };
            export type * as typeNs from './types-ns' assert {
              type: 'json'
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./util");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./other");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./types");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./types-ns");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "ns");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "typeNs");
    }

    [Fact]
    public void Extract_TypeScript_DetectsNamedReExportSurfaceSymbolsWhenImportAttributeBraceStartsOnNextLine()
    {
        var content = """
            export { foo as bar } from './other' with
            {
              type: 'json'
            };
            export type { User } from './types' assert
            {
              type: 'json'
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./other");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./types");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "bar");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "User");
    }

    [Fact]
    public void Extract_TypeScript_TemplateInterpolationBracesStillCountTowardMethodRange()
    {
        var content = """"
            export class Example {
              foo() {
                const value = `${format({ answer: 42 })}`;
                return value;
              }

              bar() {
                return 1;
              }
            }
            """";
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        var example = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Example"));
        var foo = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "foo"));
        var bar = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "bar"));

        Assert.Equal(10, example.EndLine);
        Assert.Equal(5, foo.EndLine);
        Assert.Equal("class", bar.ContainerKind);
        Assert.Equal("Example", bar.ContainerName);
    }

    [Fact]
    public void Extract_TypeScript_GenericArrowCommonJsNamedExportFunctionsPreserveMultilineBraceBodyRanges()
    {
        var content = """
            module.exports.foo = <T>(value: T) =>
            {
              return value;
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        var foo = Assert.Single(symbols.Where(s => s.Kind == "lambda" && s.Name == "foo"));
        Assert.Equal(1, foo.StartLine);
        Assert.Equal(4, foo.EndLine);
        Assert.Equal(2, foo.BodyStartLine);
        Assert.Equal(4, foo.BodyEndLine);
    }

    [Fact]
    public void Extract_TypeScript_DetectsGenericArrowCommonJsNamedExportAssignments()
    {
        var content = """
            module.exports.foo = <T>(value: T) => value;
            module.exports.bar =
              <T>(value: T) => value;
            module.exports.baz = (<T>(value: T) => value);
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "lambda" && s.Name == "foo");
        Assert.Contains(symbols, s => s.Kind == "lambda" && s.Name == "bar");
        Assert.Contains(symbols, s => s.Kind == "lambda" && s.Name == "baz");
    }

    [Fact]
    public void Extract_TypeScript_DetectsMultilineAndConstrainedGenericArrowCommonJsNamedExportAssignments()
    {
        var content = """
            module.exports.foo = <T>(
              value: T
            ) => value;
            module.exports.bar = <T extends (...args: any[]) => number>(value: T) => value;
            module.exports.baz = async <T>(
              value: T
            ) => value;
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "lambda" && s.Name == "foo");
        Assert.Contains(symbols, s => s.Kind == "lambda" && s.Name == "bar");
        Assert.Contains(symbols, s => s.Kind == "lambda" && s.Name == "baz");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotTreatCommonJsNamedExportComparisonsAsAssignments()
    {
        var content = """
            module.exports.foo === undefined;
            exports.bar == null;
            module.exports.baz !== 1;
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Empty(symbols);
    }

    [Fact]
    public void Extract_TypeScript_DetectsExportedObjectLiteralShorthandProperties()
    {
        var content = """
            const foo = 1;
            const bar = 2;
            module.exports = {
              foo,
              bar,
              baz: foo,
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "foo" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "bar" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "baz" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
    }

    [Fact]
    public void Extract_TypeScript_DetectsAbstractClassAndNamespace()
    {
        var content = "export abstract class BaseService {\n    abstract getName(): string;\n}\ndeclare module 'express' {\n    interface Request { }\n}\nnamespace App.Models {\n    export type ID = string;\n}";
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "BaseService");
        // Quoted ambient module declaration / 引用符付きアンビエントモジュール宣言
        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "express");
        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "App.Models");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "ID");
    }

    [Fact]
    public void Extract_TypeScript_DetectsExportAsNamespace()
    {
        var content = """
            export as namespace LegacyWidgets;
            export as namespace $Widgets;
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "LegacyWidgets");
        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "$Widgets");
    }

    [Fact]
    public void Extract_TypeScript_DetectsDeclarationOnlyMembersInDeclareClassAndInterface()
    {
        var content = """
            declare class Service {
                run(): void;
                fetch<T>(id: string): Promise<T>;
            }

            interface Api {
                ping(): void;
                fetch<T>(id: string): Promise<T>;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Service");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerKind == "class" && s.ContainerName == "Service" && s.BodyStartLine == null);
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "fetch" && s.ContainerKind == "class" && s.ContainerName == "Service" && s.BodyStartLine == null);
        Assert.Contains(symbols, s => s.Kind == "interface" && s.Name == "Api");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "ping" && s.ContainerKind == "interface" && s.ContainerName == "Api" && s.BodyStartLine == null);
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "fetch" && s.ContainerKind == "interface" && s.ContainerName == "Api" && s.BodyStartLine == null);
    }

    [Fact]
    public void Extract_TypeScript_DetectsAccessorClassFields()
    {
        var content = """
            class Settings {
                accessor theme: string;
                accessor count: number;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Settings");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "theme" && s.ContainerKind == "class" && s.ContainerName == "Settings" && s.ReturnType == "string");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "count" && s.ContainerKind == "class" && s.ContainerName == "Settings" && s.ReturnType == "number");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotMergeAbstractMemberIntoFollowingConcreteMethod()
    {
        var content = """
            export default abstract class Example {
                abstract run(): void;

                keep(): { value: string } {
                    return { value: "x" };
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Example");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "run" && s.Signature != null && s.Signature.Contains("keep()", StringComparison.Ordinal));
        var keep = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "keep"));
        Assert.Equal("{ value: string }", keep.ReturnType);
        Assert.Equal("keep(): { value: string } {", keep.Signature);
    }

    [Fact]
    public void Extract_TypeScript_DetectsExportDefaultClassMembers()
    {
        var content = """
            export default class DefaultTs {
                run(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "DefaultTs");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run");
    }

    [Fact]
    public void Extract_TypeScript_DetectsExportDefaultGenericArrowFunctionSymbol()
    {
        var content = """
            export default <T>(
              value: T
            ) => value;
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        var function = Assert.Single(symbols.Where(s => s.Kind == "lambda" && s.Name == "default"));
        Assert.Equal("export", function.Visibility);
        Assert.Equal(1, function.StartLine);
    }

    [Fact]
    public void Extract_TypeScript_MultilineImportTypeQuery_DoesNotEmitRuntimeImportSymbol()
    {
        var content = """
            type Module = typeof import(
                "./types"
            );
            const runtime = import(
                "./runtime"
            );
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "./types");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./runtime");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotInventExtendsAsAnonymousDefaultClassName()
    {
        var content = """
            export default class extends Base {
                run(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "extends");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotInventExtendsAsAnonymousDefaultDerivedClassName()
    {
        var content = """
            export default class extends mixin(Base) {
                run(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "extends");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotInventImplementsAsAnonymousDefaultClassName()
    {
        var content = """
            export default class implements Runnable {
                run(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "implements");
        var run = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "run"));
        Assert.Equal("class", run.ContainerKind);
        Assert.Equal("default", run.ContainerName);
    }

    [Fact]
    public void Extract_TypeScript_DetectsClassExpressionMethods()
    {
        var content = """
            const Service = class NamedService {
                run(): void {}
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Service");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "NamedService");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run");
        var run = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "run"));
        Assert.Equal("class", run.ContainerKind);
        Assert.Equal("Service", run.ContainerName);
    }

    [Fact]
    public void Extract_TypeScript_DetectsInlineClassMethods()
    {
        var content = "export class Inline { run(): void {} }";
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Inline");
        var run = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "run"));
        Assert.Equal("class", run.ContainerKind);
        Assert.Equal("Inline", run.ContainerName);
    }

    [Fact]
    public void Extract_TypeScript_DetectsInlineMultipleMethods()
    {
        var content = "export class Inline { first(): void {} second(): void {} }";
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Inline");
        var first = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "first"));
        var second = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "second"));
        Assert.Equal("class", first.ContainerKind);
        Assert.Equal("Inline", first.ContainerName);
        Assert.Equal("first(): void {}", first.Signature);
        Assert.Equal("class", second.ContainerKind);
        Assert.Equal("Inline", second.ContainerName);
        Assert.Equal("second(): void {}", second.Signature);
    }

    [Fact]
    public void Extract_TypeScript_DetectsSameLineSiblingClassesWithDistinctMethodNames()
    {
        var content = "export class A { first(): void {} } export class B { second(): void {} }";
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "A");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "B");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "first" && s.ContainerName == "A");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "second" && s.ContainerName == "B");
    }

    [Fact]
    public void Extract_TypeScript_DetectsSameLineClassExpressionAfterStatementPrefixWithCleanSignature()
    {
        var content = "foo(); export const Service = class Visible { keep(): void {} };";
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Service" && s.Signature == "export const Service = class Visible { keep(): void {} }");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Visible");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep" && s.ContainerName == "Service");
    }

    [Fact]
    public void Extract_TypeScript_DetectsSameLineSiblingClassesWithIdenticalMethodNames()
    {
        var content = "export class A { run(): void {} } export class B { run(): void {} }";
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "A");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "B");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "A");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "B");
    }

    [Fact]
    public void Extract_TypeScript_DetectsSameLinePublicClassAfterStatementPrefix()
    {
        var content = "foo(); class Visible { keep(): void {} }";
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Visible");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep" && s.ContainerName == "Visible");
    }

    [Fact]
    public void Extract_TypeScript_DetectsSameLinePublicClassAfterFunctionLocalHiddenClass()
    {
        var content = "function outer(): void { class Hidden { run(): void {} } } class Visible { keep(): void {} }";
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "outer");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Hidden");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "run");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Visible");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep" && s.ContainerName == "Visible");
    }

    [Fact]
    public void Extract_TypeScript_DetectsSameLineClassExpressionAfterStatementPrefix()
    {
        var content = "foo(); const Service = class Visible { keep(): void {} };";
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Service");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Visible");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep" && s.ContainerName == "Service");
    }

    [Fact]
    public void Extract_TypeScript_DetectsInlineDefaultExportClassMethods()
    {
        var content = "export default class Inline { run(): void {} }";
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Inline");
        var run = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "run"));
        Assert.Equal("class", run.ContainerKind);
        Assert.Equal("Inline", run.ContainerName);
    }

    [Fact]
    public void Extract_TypeScript_DetectsTypeAliasesAsImports()
    {
        var content = """
            export type Pair<T> = [T, T];
            type Callback = (x: number) => number;
            declare type User = { name: string; age: number };
            interface Admin { perms: string[]; }
            class Person { name: string = ""; }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Pair");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Callback");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "User");
        Assert.Contains(symbols, s => s.Kind == "interface" && s.Name == "Admin");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Person");
    }

    [Fact]
    public void Extract_TypeScript_DetectsGenericTypeAliasesWithDefaultTypeParameters()
    {
        var content = """
            export type Result<T = string> = { value: T };
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Result");
    }

    [Fact]
    public void Extract_TypeScript_DetectsInlineDefaultExportMultipleMethods()
    {
        var content = "export default class { first(): void {} second(): void {} }";
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "default");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "first" && s.ContainerName == "default");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "second" && s.ContainerName == "default");
    }

    [Fact]
    public void Extract_TypeScript_DetectsParenthesizedDefaultExportClassMembers()
    {
        var content = """
            export default (class {
                run(): void {}
            });
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "default");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "default");
    }

    [Fact]
    public void Extract_TypeScript_DetectsMultilineParenthesizedDefaultExportClassSignature()
    {
        var content = """
            export default
            (
                class {
                    run(): void {}
                }
            );
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "default" && s.Signature == "export default ( class {");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "default");
    }

    [Fact]
    public void Extract_TypeScript_DetectsAnonymousAbstractDefaultExportClassMembers()
    {
        var content = """
            export default abstract class {
                run(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "default");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "default");
    }

    [Fact]
    public void Extract_TypeScript_DetectsMultilineAnonymousAbstractDefaultExportClassMembers()
    {
        var content = """
            export default abstract class
            extends Base
            {
                run(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "default");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "default");
    }

    [Fact]
    public void Extract_TypeScript_DetectsAnonymousGenericDefaultExportClassMembers()
    {
        var content = """
            export default class<T> extends Base<{ value: string }> {
                run(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "default");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "default");
    }

    [Fact]
    public void Extract_TypeScript_DetectsAnonymousAbstractGenericDefaultExportClassMembers()
    {
        var content = """
            export default abstract class<T> extends Base<{ value: string }> {
                run(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "default");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "default");
    }

    [Fact]
    public void Extract_TypeScript_DetectsInlineModifierNamedMethods()
    {
        var content = "export class Example { async(): void {} static(): void {} keep(): void {} }";
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "async" && s.ContainerName == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "static" && s.ContainerName == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep" && s.ContainerName == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "async" && s.Signature == "async(): void {}");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "static" && s.Signature == "static(): void {}");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep" && s.Signature == "keep(): void {}");
    }

    [Fact]
    public void Extract_TypeScript_DetectsInlineMethodsWithDefaultArguments()
    {
        var content = "class Example { method(x: number = 1): void {} visible(): void {} }";
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "method" && s.ContainerName == "Example" && s.ReturnType == "void");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "visible" && s.ContainerName == "Example" && s.ReturnType == "void");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "method" && s.Signature == "method(x: number = 1): void {}");
    }

    [Fact]
    public void Extract_TypeScript_DetectsInlinePrivateAndGeneratorMethods()
    {
        var content = "class Example { #hidden(): void {} *iterator(): Iterable<number> {} async *stream(): AsyncIterable<number> {} visible(): void {} }";
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "#hidden" && s.ContainerName == "Example" && s.ReturnType == "void");
        Assert.Contains(symbols, s => s.Kind == "generator" && s.Name == "iterator" && s.ContainerName == "Example" && s.ReturnType == "Iterable<number>");
        Assert.Contains(symbols, s => s.Kind == "async_generator" && s.Name == "stream" && s.ContainerName == "Example" && s.ReturnType == "AsyncIterable<number>");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "visible" && s.ContainerName == "Example" && s.ReturnType == "void");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "#hidden" && s.Signature == "#hidden(): void {}");
        Assert.Contains(symbols, s => s.Kind == "generator" && s.Name == "iterator" && s.Signature == "*iterator(): Iterable<number> {}");
        Assert.Contains(symbols, s => s.Kind == "async_generator" && s.Name == "stream" && s.Signature == "async *stream(): AsyncIterable<number> {}");
    }

    [Fact]
    public void Extract_TypeScript_DetectsInlineComputedMethods()
    {
        var content = "class Example { ['computed'](): void {} [Symbol.iterator](): Iterable<number> {} visible(): void {} }";
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "['computed']" && s.ContainerName == "Example" && s.ReturnType == "void");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "[Symbol.iterator]" && s.ContainerName == "Example" && s.ReturnType == "Iterable<number>");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "visible" && s.ContainerName == "Example" && s.ReturnType == "void");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "['computed']" && s.Signature == "['computed'](): void {}");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "[Symbol.iterator]" && s.Signature == "[Symbol.iterator](): Iterable<number> {}");
    }

    [Fact]
    public void Extract_TypeScript_PreservesModifierMetadataForComputedMethods()
    {
        var content = """
            class Example {
                async [Symbol.asyncIterator](): AsyncGenerator<number> {}
                public static [Symbol.iterator](): IterableIterator<number> {}
                get [Symbol.toStringTag](): string {}
                set [key](value: string) {}
                async *[Symbol.dispose](): AsyncGenerator<string> {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "async_function" && s.Name == "[Symbol.asyncIterator]" && s.Signature == "async [Symbol.asyncIterator](): AsyncGenerator<number> {}" && s.ReturnType == "AsyncGenerator<number>");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "[Symbol.iterator]" && s.Signature == "public static [Symbol.iterator](): IterableIterator<number> {}" && s.Visibility == "public" && s.ReturnType == "IterableIterator<number>");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "[Symbol.toStringTag]" && s.Signature == "get [Symbol.toStringTag](): string {}" && s.ReturnType == "string");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "[key]" && s.Signature == "set [key](value: string) {}");
        Assert.Contains(symbols, s => s.Kind == "async_generator" && s.Name == "[Symbol.dispose]" && s.Signature == "async *[Symbol.dispose](): AsyncGenerator<string> {}" && s.ReturnType == "AsyncGenerator<string>");
    }

    [Fact]
    public void Extract_TypeScript_DetectsStringAndTemplateLiteralReturnTypes()
    {
        var content = """
            export class Example {
                literal(): 'a' {}
                union(): 'a' | 'b' {}
                message(): `a${string}` {}
                next(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "literal" && s.Signature == "literal(): 'a' {}" && s.ReturnType == "'a'");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "union" && s.Signature == "union(): 'a' | 'b' {}" && s.ReturnType == "'a' | 'b'");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "message" && s.Signature == "message(): `a${string}` {}" && s.ReturnType == "`a${string}`");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "next" && s.Signature == "next(): void {}" && s.ReturnType == "void");
    }

    [Fact]
    public void Extract_TypeScript_DetectsEscapedStringLiteralReturnTypes()
    {
        var content = """
            export class Example {
                method(): "a\"b" {}
                keep(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "method" && s.Signature == "method(): \"a\\\"b\" {}" && s.ReturnType == "\"a\\\"b\"");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep" && s.Signature == "keep(): void {}" && s.ReturnType == "void");
    }

    [Fact]
    public void Extract_TypeScript_DetectsQuotedAndNumericLiteralMethodNames()
    {
        var content = """
            export class Example {
                "run"(): void {}
                'stop'(): void {}
                1(): void {}
                1.5(): void {}
                0x10(): void {}
                1_000(): void {}
                next(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "\"run\"" && s.Signature == "\"run\"(): void {}" && s.ReturnType == "void");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "'stop'" && s.Signature == "'stop'(): void {}" && s.ReturnType == "void");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "1" && s.Signature == "1(): void {}" && s.ReturnType == "void");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "1.5" && s.Signature == "1.5(): void {}" && s.ReturnType == "void");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "0x10" && s.Signature == "0x10(): void {}" && s.ReturnType == "void");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "1_000" && s.Signature == "1_000(): void {}" && s.ReturnType == "void");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "next" && s.Signature == "next(): void {}" && s.ReturnType == "void");
    }

    [Fact]
    public void Extract_TypeScript_DetectsEscapedQuotedLiteralMethodNames()
    {
        var content = """
            export class Example {
                "a\"b"(): void {}
                'c\'d'(): void {}
                next(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "\"a\\\"b\"" && s.Signature == "\"a\\\"b\"(): void {}" && s.ReturnType == "void");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "'c\\'d'" && s.Signature == "'c\\'d'(): void {}" && s.ReturnType == "void");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "next" && s.Signature == "next(): void {}" && s.ReturnType == "void");
    }

    [Theory]
    [InlineData(
        """
        export class Example {
            handler = function namedHandler(): void {};
            keep(): void {}
        }
        """,
        "namedHandler")]
    [InlineData(
        """
        export class Example {
            handler = function (): void {};
            keep(): void {}
        }
        """,
        "function")]
    [InlineData(
        """
        export class Example {
            field = { inner(): void {} };
            keep(): void {}
        }
        """,
        "inner")]
    [InlineData(
        """
        export class Example {
            field = class Inner { run(): void {} };
            keep(): void {}
        }
        """,
        "run")]
    public void Extract_TypeScript_DoesNotTreatClassFieldInitializerMembersAsClassMethods(string content, string unexpectedMethod)
    {
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep" && s.ContainerName == "Example");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == unexpectedMethod && s.ContainerName == "Example");
    }

    [Fact]
    public void Extract_TypeScript_ClassifiesAsyncAndGeneratorFunctionKinds()
    {
        var content = """
            function plain(): void {}
            async function asyncPlain(): Promise<void> {}
            function* generated(): Iterable<number> {}
            async function* asyncGenerated(): AsyncIterable<number> {}
            export default async function* (): AsyncIterable<number> {}
            export class Example {
                async method(): Promise<void> {}
                *items(): Iterable<number> {}
                async *stream(): AsyncIterable<number> {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "plain");
        Assert.Contains(symbols, s => s.Kind == "async_function" && s.Name == "asyncPlain");
        Assert.Contains(symbols, s => s.Kind == "generator" && s.Name == "generated");
        Assert.Contains(symbols, s => s.Kind == "async_generator" && s.Name == "asyncGenerated");
        Assert.Contains(symbols, s => s.Kind == "async_generator" && s.Name == "default" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "async_function" && s.Name == "method" && s.ContainerName == "Example" && s.ReturnType == "Promise<void>");
        Assert.Contains(symbols, s => s.Kind == "generator" && s.Name == "items" && s.ContainerName == "Example" && s.ReturnType == "Iterable<number>");
        Assert.Contains(symbols, s => s.Kind == "async_generator" && s.Name == "stream" && s.ContainerName == "Example" && s.ReturnType == "AsyncIterable<number>");
    }

    [Fact]
    public void Extract_TypeScript_SemicolonlessFieldInitializerDoesNotHideComputedOrGeneratorMethods()
    {
        var content = """
            export class Example {
                field = foo
                [Symbol.iterator](): Iterator<number> {}
                *generate(): Iterable<number> {}
                next(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "[Symbol.iterator]" && s.ContainerName == "Example" && s.ReturnType == "Iterator<number>");
        Assert.Contains(symbols, s => s.Kind == "generator" && s.Name == "generate" && s.ContainerName == "Example" && s.ReturnType == "Iterable<number>");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "next" && s.ContainerName == "Example" && s.ReturnType == "void");
    }

    [Fact]
    public void Extract_TypeScript_DetectsInlineMultipleMethodsWithObjectReturnType()
    {
        var content = """export class Example { first(): { value: string } { return { value: "x" }; } second(): void {} }""";
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Example");
        var first = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "first"));
        var second = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "second"));
        Assert.Equal("first(): { value: string } { return { value: \"x\" }; }", first.Signature);
        Assert.Equal("second(): void {}", second.Signature);
    }

    [Fact]
    public void Extract_TypeScript_DetectsInlineMultipleMethodsWithConditionalObjectReturnType()
    {
        var content = """export class Example { first(): T extends U ? { a: string } : { b: string } {} second(): void {} }""";
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Example");
        var first = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "first"));
        var second = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "second"));
        Assert.Equal("first(): T extends U ? { a: string } : { b: string } {}", first.Signature);
        Assert.Equal("T extends U ? { a: string } : { b: string }", first.ReturnType);
        Assert.Equal("second(): void {}", second.Signature);
        Assert.Equal("void", second.ReturnType);
    }

    [Fact]
    public void Extract_TypeScript_DetectsInlineMultipleMethodsWithFunctionReturningObjectType()
    {
        var content = """export class Example { first(): (() => { value: string }) {} second(): void {} }""";
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Example");
        var first = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "first"));
        var second = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "second"));
        Assert.Equal("first(): (() => { value: string }) {}", first.Signature);
        Assert.Equal("(() => { value: string })", first.ReturnType);
        Assert.Equal("second(): void {}", second.Signature);
        Assert.Equal("void", second.ReturnType);
    }

    [Fact]
    public void Extract_TypeScript_DetectsMultilineClassMethodHeaders()
    {
        var content = """
            export class MultiLineMethod {
                run(
                    value: string,
                ): void {}

                keep(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "MultiLineMethod");
        var run = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "run"));
        var keep = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "keep"));
        Assert.Equal("void", run.ReturnType);
        Assert.Contains("run(", run.Signature);
        Assert.Contains("): void {", run.Signature);
        Assert.Equal("void", keep.ReturnType);
    }

    [Fact]
    public void Extract_TypeScript_DetectsMultilineGenericClassMethodHeaders()
    {
        var content = """
            export class MultiLineGenericMethod {
                run<T>(
                    value: T,
                ): Promise<T> {
                    return Promise.resolve(value);
                }

                keep(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "MultiLineGenericMethod");
        var run = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "run"));
        Assert.Equal("Promise<T>", run.ReturnType);
        Assert.Contains("run<T>(", run.Signature);
        Assert.Contains("): Promise<T> {", run.Signature);
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep" && s.ReturnType == "void");
    }

    [Fact]
    public void Extract_TypeScript_PreservesReturnTypeMetadataForMethodsWithMultilineBodies()
    {
        var content = """
            export class Example {
                run(): void {
                    return;
                }

                build(): (() => { value: string }) {
                    return () => ({ value: "x" });
                }

                async *stream<T>(): AsyncGenerator<T> {
                    yield default(T)!;
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        var run = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "run"));
        var build = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "build"));
        var stream = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "stream"));
        Assert.Equal("void", run.ReturnType);
        Assert.Equal("(() => { value: string })", build.ReturnType);
        Assert.Equal("AsyncGenerator<T>", stream.ReturnType);
    }

    [Fact]
    public void Extract_TypeScript_DetectsInlineClassExpressionMethods()
    {
        var content = "const Service = class NamedService { run(): void {} };";
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Service");
        var run = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "run"));
        Assert.Equal("class", run.ContainerKind);
        Assert.Equal("Service", run.ContainerName);
    }

    [Fact]
    public void Extract_TypeScript_DetectsParenthesizedClassExpressionMethods()
    {
        var content = "const Service = (class { run(): void {} });";
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Service");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "Service");
    }

    [Fact]
    public void Extract_TypeScript_DetectsExportEqualsClassExpressionMethods()
    {
        var content = """
            export = class {
                run(): void {}
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "default");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "default");
    }

    [Fact]
    public void Extract_TypeScript_DetectsExportEqualsNamedClassExpressionMethods()
    {
        var content = """
            export = class Named {
                run(): void {}
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "default");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Named");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "default");
    }

    [Fact]
    public void Extract_TypeScript_DetectsParenthesizedExportEqualsClassExpressionMethods()
    {
        var content = """
            export = (class {
                run(): void {}
            });
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "default");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "default");
    }

    [Fact]
    public void Extract_TypeScript_DetectsMultilineParenthesizedExportEqualsClassSignature()
    {
        var content = """
            export =
            (
                class {
                    run(): void {}
                }
            );
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "default" && s.Signature == "export = ( class {");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "default");
    }

    [Fact]
    public void Extract_TypeScript_DetectsClassExpressionInsideNamespaceBlock()
    {
        var content = """
            namespace Foo {
                export const Service = class {
                    run(): void {}
                };
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "Foo");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Service");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "Service");
    }

    [Fact]
    public void Extract_TypeScript_DetectsDollarPrefixedClassExpressionBindings()
    {
        var content = """
            export const $Service = class {
                run(): void {}
            };

            module.exports.$Handler = class {
                keep(): void {}
            };

            export namespace PublicNs {
                export const $Worker = class {
                    job(): void {}
                };
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "$Service");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "$Service");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "$Handler");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep" && s.ContainerName == "$Handler");
        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "PublicNs");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "$Worker");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "job" && s.ContainerName == "$Worker");
    }

    [Fact]
    public void Extract_TypeScript_DetectsMultilineClassExpressionInsideNamespaceBlock()
    {
        var content = """
            namespace Foo {
                export const Service =
                    class {
                        run(): void {}
                    };
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "Foo");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Service");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "Service");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Service" && s.Signature == "export const Service = class {");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotLeakNonExportedNamespaceClasses()
    {
        var content = """
            namespace Foo {
                class Hidden {
                    run(): void {}
                }

                const HiddenExpr = class {
                    keep(): void {}
                };

                export class Visible {
                    stay(): void {}
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "Foo");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Hidden");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "HiddenExpr");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "run");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "keep");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Visible");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "stay" && s.ContainerName == "Visible");
    }

    [Fact]
    public void Extract_TypeScript_DetectsParenthesizedCommonJsModuleExportsClassExpressionMethods()
    {
        var content = """
            module.exports = (class {
                run(): void {}
            });
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "default");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "default");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotLeakBlockScopedClassesInsideConditionalBlocks()
    {
        var content = """
            if (flag) {
                const Hidden = class {
                    run(): void {}
                };

                class LocalDecl {
                    keep(): void {}
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Hidden");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "LocalDecl");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "run");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "keep");
    }

    [Fact]
    public void Extract_TypeScript_DetectsGenericClassMethods()
    {
        var content = """
            export class Example {
                first<T extends Foo<Bar>>(): void {}
                second(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "first");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "second");
    }

    [Fact]
    public void Extract_TypeScript_DetectsGenericClassMethodsWithFunctionTypeDefault()
    {
        var content = """
            export class Example {
                method<T = () => void>(): number {}
                keep(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "method" && s.ReturnType == "number");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep");
    }

    [Fact]
    public void Extract_TypeScript_DetectsGenericClassMethodsWithFunctionTypeConstraint()
    {
        var content = """
            export class Example {
                method<T extends () => void>(): number {}
                keep(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "method" && s.ReturnType == "number");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotMergeOverloadSignaturesIntoImplementationMethod()
    {
        var content = """
            class Overloaded {
                foo(x: string): string;
                foo(x: number): number;
                foo(x: string | number): string | number {
                    return x;
                }

                bar(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Overloaded");
        var fooDeclarations = symbols.Where(s => s.Kind == "function" && s.Name == "foo" && s.BodyStartLine == null).ToList();
        Assert.Equal(2, fooDeclarations.Count);
        var foo = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "foo" && s.BodyStartLine != null));
        Assert.Equal(4, foo.Line);
        Assert.Equal("string | number", foo.ReturnType);
        Assert.Equal("foo(x: string | number): string | number {", foo.Signature);
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "bar" && s.ReturnType == "void");
    }

    [Fact]
    public void Extract_TypeScript_DetectsGenericClassMethodsWithMultilineFunctionTypeParameters()
    {
        var content = """
            export class Example {
                method<T = () => void>(
                    value: T,
                ): number {
                    return 1;
                }

                constrained<T extends () => void>(
                    value: T,
                ): number {
                    return 2;
                }

                keep(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "method" && s.ReturnType == "number");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "constrained" && s.ReturnType == "number");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep" && s.ReturnType == "void");
    }

    [Fact]
    public void Extract_TypeScript_DetectsInlineGenericClassMethods()
    {
        var content = """export class Example { first<T extends Foo<Bar>>(): void {} second(): void {} }""";
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "first");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "second");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotLeakClassMethodLocalClassExpressionMethods()
    {
        var content = """
            export class Outer {
                method(): void {
                    const Inner = class {
                        run(): void {}
                    };
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Outer");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "method" && s.ContainerName == "Outer");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Inner");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "run");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotLeakClassMethodLocalSyntheticClassExpressionsWithObjectReturnType()
    {
        var content = """
            export class Outer {
                method(): { value: string } {
                    var Service = class Hidden {
                        run(): void {}
                    };
                    module.exports = class ModuleHidden {
                        keep(): void {}
                    };
                    return { value: "x" };
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Outer");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "method" && s.ContainerName == "Outer");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Service");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "default");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "run");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "keep");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotLeakClassMethodDirectLocalClasses()
    {
        var content = """
            export class Outer {
                method(): void {
                    class Hidden {
                        run(): void {}
                    }
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Outer");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "method" && s.ContainerName == "Outer");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Hidden");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "run");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotLeakFunctionLocalClassExpressionMethods()
    {
        var content = """
            function outer(): void {
                const Service = class {
                    run(): void {}
                };
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "outer");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "run");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotLeakDirectFunctionLocalClasses()
    {
        var content = """
            function outer(): void {
                class Hidden {
                    run(): void {}
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "outer");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Hidden");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "run");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotLeakCommonJsFunctionExpressionLocalClassMethods()
    {
        var content = """
            exports.handler = function (): void {
                const Local = class {
                    inside(): void {}
                };
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Local");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "inside");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotLeakIifeLocalClassMethods()
    {
        var content = """
            (() => {
                const Local = class {
                    inside(): void {}
                };
            })();
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Local");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "inside");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotLeakBlocklessArrowReturnedClasses()
    {
        var content = """
            const factory = () =>
                class Hidden {
                    method(): void {}
                };
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "lambda" && s.Name == "factory");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Hidden");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "method");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotLeakWrappedBlocklessArrowReturnedClassesAndKeepsRange()
    {
        var content = """
            const factory = () =>
                wrap(
                    class Hidden {
                        method(): void {}
                    }
                );
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        var factory = Assert.Single(symbols.Where(s => s.Kind == "lambda" && s.Name == "factory"));
        Assert.Equal(1, factory.StartLine);
        Assert.Equal(6, factory.EndLine);
        Assert.Equal(2, factory.BodyStartLine);
        Assert.Equal(6, factory.BodyEndLine);
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Hidden");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "method");
    }

    [Fact]
    public void Extract_TypeScript_BlocklessArrowWithoutSemicolonDoesNotConsumeFollowingTopLevelClass()
    {
        var content = """
            const factory = () =>
                class Hidden {
                    method(): void {}
                }
            export class Visible {
                keep(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        var factory = Assert.Single(symbols.Where(s => s.Kind == "lambda" && s.Name == "factory"));
        Assert.Equal(1, factory.StartLine);
        Assert.Equal(4, factory.EndLine);
        Assert.Equal(2, factory.BodyStartLine);
        Assert.Equal(4, factory.BodyEndLine);
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Visible");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep" && s.ContainerName == "Visible");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Hidden");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "method");
    }

    [Fact]
    public void Extract_TypeScript_BlocklessArrowWithoutSemicolonDoesNotConsumeFollowingExpressionStatement()
    {
        var content = """
            const factory = () =>
                class Hidden {
                    method(): void {}
                }
            foo();
            export class Visible {
                keep(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        var factory = Assert.Single(symbols.Where(s => s.Kind == "lambda" && s.Name == "factory"));
        Assert.Equal(4, factory.EndLine);
        Assert.Equal(4, factory.BodyEndLine);
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Visible");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep" && s.ContainerName == "Visible");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Hidden");
    }

    [Fact]
    public void Extract_TypeScript_BlocklessArrowWithoutSemicolonDoesNotHideFollowingCommonJsClassExport()
    {
        var content = """
            const factory = () =>
                class Hidden {
                    method(): void {}
                }
            exports.Service = class Visible {
                keep(): void {}
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        var factory = Assert.Single(symbols.Where(s => s.Kind == "lambda" && s.Name == "factory"));
        Assert.Equal(4, factory.EndLine);
        Assert.Equal(4, factory.BodyEndLine);
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Service");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep" && s.ContainerName == "Service");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Hidden");
    }

    [Fact]
    public void Extract_TypeScript_DetectsMultilineAnonymousDefaultExportClassMembers()
    {
        var content = """
            export default class
            implements Runnable
            {
                run(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "default");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "default");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotLeakStaticBlockLocalClassMethods()
    {
        var content = """
            export class Outer {
                static {
                    const Local = class {
                        inside(): void {}
                    };
                }

                keep(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Outer");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Local");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "inside");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotLeakDirectStaticBlockLocalClasses()
    {
        var content = """
            export class Outer {
                static {
                    class Local {
                        inside(): void {}
                    }
                }

                keep(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Outer");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Local");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "inside");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotLeakObjectLiteralConciseMethodLocalClasses()
    {
        var content = """
            const obj = {
                method(): void {
                    class Inner {
                        run(): void {}
                    }
                }
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Inner");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "run");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotLeakObjectLiteralConciseMethodSyntheticClassExpressionsWithObjectReturnType()
    {
        var content = """
            const obj = {
                method(): { value: string } {
                    var Service = class Hidden {
                        run(): void {}
                    };
                    module.exports = class ModuleHidden {
                        keep(): void {}
                    };
                    return { value: "x" };
                }
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Service");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "default");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "run");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "keep");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotLeakGetterSetterLocalClasses()
    {
        var content = """
            const obj = {
                get value(): number {
                    class HiddenGetter {
                        run(): void {}
                    }
                    return 1;
                },
                set value(input: number) {
                    class HiddenSetter {
                        run(): void {}
                    }
                }
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "HiddenGetter");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "HiddenSetter");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "run");
    }

    [Fact]
    public void Extract_TypeScript_PreservesGetterSetterSignaturesVisibilityAndMethodNamedGetSet()
    {
        var content = """
            class Example {
                public get value(): number {}
                private set value(input: number) {}
                get(): void {}
                set(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "value" && s.Signature == "public get value(): number {}" && s.Visibility == "public" && s.ReturnType == "number");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "value" && s.Signature == "private set value(input: number) {}" && s.Visibility == "private");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "get" && s.Signature == "get(): void {}" && s.ReturnType == "void");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "set" && s.Signature == "set(): void {}" && s.ReturnType == "void");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotLeakNamedClassExpressionsAfterColon()
    {
        var content = """
            const pick = flag ? value : class Hidden { method(): void {} };
            const obj = { field: class Inner { run(): void {} } };
            class Visible { ok(): void {} }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Hidden");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Inner");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "method");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "run");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Visible");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "ok" && s.ContainerName == "Visible");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotTreatControlFlowBlocksAsFunctions()
    {
        var content = """
            export class Parser {
                override parse(value: Payload): Result {
                    if (value.ready) {
                    }

                    for (const item of value.items) {
                    }

                    while (value.more) {
                    }

                    switch (value.mode) {
                        case "fast":
                            return value.result;
                    }
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Parser");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "parse");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "if");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "for");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "while");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "switch");
    }

    [Fact]
    public void Extract_TypeScript_AllowsKeywordNamedMethodsAtClassBodyDepthZero()
    {
        var content = """
            export class KeywordMethods {
                if(): void {}
                catch(): string {
                    return "ok";
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "KeywordMethods");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "if");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "catch");
    }

    [Fact]
    public void Extract_TypeScript_IgnoresRegexLiteralBracesAndBlockCommentMethodShapes()
    {
        var content = """
            export class Example {
                /*
                    fake(): void {
                    }
                */
                first(): void {
                    const open = /{/;
                    const close = /}/;
                }

                second(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "first");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "second");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "fake");
    }

    [Fact]
    public void Extract_TypeScript_IgnoresHeaderGenericBracesBeforeClassBody()
    {
        var content = """
            export class Derived extends Base<{ value: string }> {
                run(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Derived");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotTreatHeaderComparisonAsGenericAngleDepth()
    {
        var content = """
            export class Derived extends mixin(a < b ? Base : Fallback) {
                run(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Derived");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run");
    }

    [Fact]
    public void Extract_TypeScript_FunctionRangeIgnoresComparisonAngleBracketsInParameters()
    {
        var content = """
            function choose(value = a < b ? one : two): Result {
                return value;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        var choose = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "choose"));
        Assert.Equal(1, choose.StartLine);
        Assert.Equal(3, choose.EndLine);
        Assert.Equal(1, choose.BodyStartLine);
        Assert.Equal(3, choose.BodyEndLine);
    }

    [Fact]
    public void Extract_TypeScript_FunctionRangeIgnoresObjectReturnTypeBraces()
    {
        var content = """
            function outer(): { a: number } {
                return { a: 1 };
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        var outer = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "outer"));
        Assert.Equal(1, outer.StartLine);
        Assert.Equal(3, outer.EndLine);
        Assert.Equal(1, outer.BodyStartLine);
        Assert.Equal(3, outer.BodyEndLine);
        Assert.Equal("function outer(): { a: number } {", outer.Signature);
    }

    [Theory]
    [InlineData(
        "typescript",
        """
        function outer({ value }) {
            var Service = class Hidden {
                run() {}
            };
        }
        """)]
    [InlineData(
        "typescript",
        """
        function outer(value: { a: number }) {
            var Service = class Hidden {
                run() {}
            };
        }
        """)]
    [InlineData(
        "typescript",
        """
        const outer = function(value = { a: 1 }) {
            var Service = class Hidden {
                run() {}
            };
        };
        """)]
    public void Extract_TypeScript_DoesNotLeakClassExpressionsFromClassicFunctionHeaders(string lang, string content)
    {
        var symbols = SymbolExtractor.Extract(1, lang, content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Service");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "run");
    }

    [Fact]
    public void Extract_TypeScript_KeepsSiblingMethodsAfterWrappedControlFlowRegexLiterals()
    {
        var content = """
            export class Example {
                first(value: string): void {
                    if (
                        ready
                    ) /{/.test(value);
                }

                second(value: string): void {
                    if (first) {
                    }
                    else if (
                        secondReady
                    ) /{/.test(value);
                }

                third(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "first");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "second");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "third");
    }

    [Fact]
    public void Extract_TypeScript_KeepsSiblingMethodsAfterElseRegexLiteral()
    {
        var content = """
            export class Example {
                first(value: string): void {
                    if (cond) {
                    }
                    else /{/.test(value);
                }

                second(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "first");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "second");
    }

    [Fact]
    public void Extract_TypeScript_KeepsSiblingMethodsAfterDoAndFinallyRegexLiterals()
    {
        var content = """
            export class Example {
                first(value: string): void {
                    do /{/.test(value); while (cond);
                }

                second(value: string): void {
                    try {
                    }
                    finally /{/.test(value);
                }

                third(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "first");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "second");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "third");
    }

#if NET8_0
    [Fact]
#else
    [Fact(Skip = PracticalBudgetTestTarget.SecondaryTargetSkipReason)]
#endif
    public void Extract_TypeScriptLargeClassExpressionTargets_CompletesWithinPracticalBudget()
    {
        var builder = new StringBuilder();
        for (var i = 0; i < 2_000; i++)
            builder.Append("export const C").Append(i).Append(" = class { method").Append(i).AppendLine("(): number { return 1; } };");

        var stopwatch = Stopwatch.StartNew();
        var symbols = SymbolExtractor.Extract(1, "typescript", builder.ToString());
        stopwatch.Stop();

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "C0");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "C1999");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "method1999" && s.ContainerKind == "class" && s.ContainerName == "C1999");
        var runawayBudget = TimeSpan.FromSeconds(10);
        Assert.True(
            stopwatch.Elapsed < runawayBudget,
            $"Large TypeScript class expression target extraction took {stopwatch.Elapsed.TotalSeconds:F2}s, expected < {runawayBudget.TotalSeconds:F0}s runaway guard budget.");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotEmitObjectLiteralMembersInBlockScopeOrNonExportedNamespace()
    {
        // Non-exported bindings in block scope or namespace scope should be filtered out,
        // matching the scope-filter parity already applied to other JS/TS capture paths.
        // block scope や namespace 内の非 export バインディングは、他の JS/TS 抽出経路と
        // 同じスコープフィルタに合わせて除外されること。
        var content = """
            if (Math.random() > 0.5) {
              const blockScoped = {
                run() { return 1; },
              };
            }

            namespace N {
              const hidden = {
                run() { return 1; },
              };
              export const shown = {
                ok() { return 2; },
              };
            }

            export const topLevel = {
              fn() { return 3; },
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "run");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "ok" && s.ContainerKind == "object");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "fn" && s.ContainerKind == "object");
    }

    [Fact]
    public void Extract_TypeScript_DetectsClassFieldArrowWithAsiBetweenFields()
    {
        var content = """
            class Foo {
                first = (): number => 42
                second = (x: number): number => x + 1
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        var first = symbols.FirstOrDefault(s => s.Kind == "function" && s.Name == "first");
        Assert.NotNull(first);
        Assert.Equal("class", first.ContainerKind);
        Assert.Equal("number", first.ReturnType);

        var second = symbols.FirstOrDefault(s => s.Kind == "function" && s.Name == "second");
        Assert.NotNull(second);
        Assert.Equal("class", second.ContainerKind);
        Assert.Equal("number", second.ReturnType);
    }

    [Fact]
    public void Extract_TypeScript_DetectsClassFieldArrowAsiBeforeClosingBrace()
    {
        // Single field without trailing `;` followed by the class-closing `}` must
        // still be captured; ASI at `}` terminates the expression body.
        // セミコロンなしの単一 field が直後の class 終了 `}` で終端されるケース。
        var content = """
            class Foo {
                only = (): number => 7
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        var only = symbols.FirstOrDefault(s => s.Kind == "function" && s.Name == "only");
        Assert.NotNull(only);
        Assert.Equal("class", only.ContainerKind);
        Assert.Equal("Foo", only.ContainerName);
        Assert.Equal("number", only.ReturnType);
    }

    [Fact]
    public void Extract_TypeScript_DetectsMultiLineObjectLiteralBinding()
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
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "foo" && s.ContainerKind == "object" && s.ContainerName == "obj");
        Assert.Contains(symbols, s => s.Kind == "generator" && s.Name == "bar" && s.ContainerKind == "object" && s.ContainerName == "obj");
    }

    [Fact]
    public void Extract_TypeScript_DetectsExportedObjectLiteralAliasProperties()
    {
        var content = """
            const foo = 1;
            function inner() { return 3; }
            function named() { return 4; }
            const answer = 42;
            module.exports = { foo, alias: inner, named, method() {} };
            export default { answer };
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "foo" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "alias" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "named" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "method" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "answer" && s.ContainerKind == "object" && s.ContainerName == "default");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "inner" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
    }

    [Fact]
    public void Extract_TypeScript_DetectsMultiLineDestructuredNamedExports()
    {
        var content = """
            const cfg = {} as Config;
            export const {
                alpha,
                renamed: beta,
            }: Pick<Config, "alpha" | "renamed"> = cfg;
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "alpha" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "beta" && s.Visibility == "export");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "renamed");
    }

    [Fact]
    public void Extract_TypeScript_DetectsExportDefaultObjectLiteralMembers()
    {
        // `export default { ... }` is a common module-shape; its shorthand members
        // should be captured with container_name == "default".
        // `export default { ... }` のショートハンドメンバは container_name == "default"
        // として抽出されること。
        var content = """
            export default {
                foo() { return 1; },
                async bar() { return 2; },
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        var foo = symbols.FirstOrDefault(s => s.Kind == "function" && s.Name == "foo");
        Assert.NotNull(foo);
        Assert.Equal("object", foo.ContainerKind);
        Assert.Equal("default", foo.ContainerName);

        var bar = symbols.FirstOrDefault(s => s.Kind == "async_function" && s.Name == "bar");
        Assert.NotNull(bar);
        Assert.Equal("object", bar.ContainerKind);
        Assert.Equal("default", bar.ContainerName);
    }

    [Fact]
    public void Extract_TypeScript_DetectsClassFieldArrowComputedMemberContinuation()
    {
        var content = """
            class Foo {
              first = (): unknown => foo
                [bar];
              second = (): number => 43;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        var first = symbols.FirstOrDefault(s => s.Kind == "function" && s.Name == "first");
        Assert.NotNull(first);
        Assert.Equal("class", first.ContainerKind);
        Assert.Contains("[bar]", first.Signature);

        var second = symbols.FirstOrDefault(s => s.Kind == "function" && s.Name == "second");
        Assert.NotNull(second);
        Assert.Equal("number", second.ReturnType);
    }

    [Fact]
    public void Extract_TypeScript_DetectsClassFieldArrowStringLiteralBeforeClosingBrace()
    {
        var content = """
            class Foo {
              only = (): string => "x"
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        var only = symbols.FirstOrDefault(s => s.Kind == "function" && s.Name == "only");
        Assert.NotNull(only);
        Assert.Equal("class", only.ContainerKind);
        Assert.Equal("string", only.ReturnType);
    }

    [Fact]
    public void Extract_TypeScript_DetectsClassFieldArrowStringLiteralWithAsiBetweenFields()
    {
        var content = """
            class Foo {
              first = (): string => "x"
              second = (): number => 43
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        var first = symbols.FirstOrDefault(s => s.Kind == "function" && s.Name == "first");
        Assert.NotNull(first);
        Assert.Equal("string", first.ReturnType);

        var second = symbols.FirstOrDefault(s => s.Kind == "function" && s.Name == "second");
        Assert.NotNull(second);
        Assert.Equal("number", second.ReturnType);

    }

    [Fact]
    public void Extract_TypeScript_ResolvesTsconfigPathAliasImports()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("tsconfig_alias_symbols");
        try
        {
            WriteFile(projectRoot, "tsconfig.json", """
                {
                  "compilerOptions": {
                    "baseUrl": ".",
                    "paths": {
                      "@/*": ["src/*"]
                    }
                  }
                }
                """);
            WriteFile(projectRoot, "src/components/Button.tsx", "export const Button = () => null;\n");
            var sourcePath = WriteFile(projectRoot, "src/app/page.tsx", "import { Button } from \"@/components/Button\";\n");

            var symbols = SymbolExtractor.Extract(1, "typescript", File.ReadAllText(sourcePath), sourcePath);

            Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "src/components/Button.tsx");
            Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "@/components/Button");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Extract_TypeScript_BomPrefixedTsconfigResolvesPathAliasImports()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("tsconfig_alias_bom_symbols");
        try
        {
            var tsconfigPath = Path.Combine(projectRoot, "tsconfig.json");
            File.WriteAllText(
                tsconfigPath,
                """
                {
                  "compilerOptions": {
                    "baseUrl": ".",
                    "paths": {
                      "@/*": ["src/*"]
                    }
                  }
                }
                """,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            WriteFile(projectRoot, "src/components/Button.tsx", "export const Button = () => null;\n");
            var sourcePath = WriteFile(projectRoot, "src/app/page.tsx", "import { Button } from \"@/components/Button\";\n");

            var symbols = SymbolExtractor.Extract(1, "typescript", File.ReadAllText(sourcePath), sourcePath);

            Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "src/components/Button.tsx");
            Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "@/components/Button");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Extract_TypeScript_ResolvesBaseUrlOnlyImports()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("tsconfig_baseurl_symbols");
        try
        {
            WriteFile(projectRoot, "tsconfig.json", """
                {
                  "compilerOptions": {
                    "baseUrl": "src"
                  }
                }
                """);
            WriteFile(projectRoot, "src/components/Button.tsx", "export const Button = 1;\n");
            var sourcePath = WriteFile(projectRoot, "src/app/page.tsx", "import { Button } from \"components/Button\";\n");

            var symbols = SymbolExtractor.Extract(1, "typescript", File.ReadAllText(sourcePath), sourcePath);

            Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "src/components/Button.tsx");
            Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "components/Button");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Extract_TypeScript_ResolvesImportEqualsRequirePathAlias()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("tsconfig_import_equals_alias_symbols");
        try
        {
            WriteFile(projectRoot, "tsconfig.json", """
                {
                  "compilerOptions": {
                    "baseUrl": ".",
                    "paths": {
                      "@/*": ["src/*"]
                    }
                  }
                }
                """);
            WriteFile(projectRoot, "src/services/api.ts", "export = {};\n");
            var sourcePath = WriteFile(projectRoot, "src/main.ts", "import Api = require(\"@/services/api\");\n");

            var symbols = SymbolExtractor.Extract(1, "typescript", File.ReadAllText(sourcePath), sourcePath);

            Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Api");
            Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "src/services/api.ts");
            Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "@/services/api");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Extract_TypeScript_ResolvesInheritedTsconfigPathAliasImports()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("tsconfig_alias_extends_symbols");
        try
        {
            WriteFile(projectRoot, "tsconfig.base.json", """
                {
                  "compilerOptions": {
                    "baseUrl": ".",
                    "paths": {
                      "~lib/*": ["lib/*"]
                    }
                  }
                }
                """);
            WriteFile(projectRoot, "tsconfig.json", """
                {
                  "extends": "./tsconfig.base.json"
                }
                """);
            WriteFile(projectRoot, "lib/math/index.ts", "export const sum = () => 0;\n");
            var sourcePath = WriteFile(projectRoot, "src/app.ts", "import { sum } from \"~lib/math\";\n");

            var symbols = SymbolExtractor.Extract(1, "typescript", File.ReadAllText(sourcePath), sourcePath);

            Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "lib/math/index.ts");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Extract_TypeScript_ResolvesInheritedPathAliasesFromDeclaringBaseUrl()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("tsconfig_alias_extends_baseurl_symbols");
        try
        {
            WriteFile(projectRoot, "tsconfig.base.json", """
                {
                  "compilerOptions": {
                    "baseUrl": ".",
                    "paths": {
                      "~shared/*": ["shared/*"]
                    }
                  }
                }
                """);
            WriteFile(projectRoot, "apps/web/tsconfig.json", """
                {
                  "extends": "../../tsconfig.base.json",
                  "compilerOptions": {
                    "baseUrl": "."
                  }
                }
                """);
            WriteFile(projectRoot, "shared/api.ts", "export const api = 1;\n");
            WriteFile(projectRoot, "apps/web/shared/api.ts", "export const wrong = 1;\n");
            var sourcePath = WriteFile(projectRoot, "apps/web/src/app.ts", "import { api } from \"~shared/api\";\n");

            var symbols = SymbolExtractor.Extract(1, "typescript", File.ReadAllText(sourcePath), sourcePath, projectRoot);

            Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "shared/api.ts");
            Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "apps/web/shared/api.ts");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Extract_TypeScript_PrefersMoreSpecificTsconfigPathAliasImports()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("tsconfig_alias_specificity_symbols");
        try
        {
            WriteFile(projectRoot, "tsconfig.json", """
                {
                  "compilerOptions": {
                    "baseUrl": ".",
                    "paths": {
                      "*": ["fallback/*"],
                      "@app/*": ["src/app/*"]
                    }
                  }
                }
                """);
            WriteFile(projectRoot, "fallback/@app/Button.ts", "export const Wrong = 1;\n");
            WriteFile(projectRoot, "src/app/Button.ts", "export const Button = 1;\n");
            var sourcePath = WriteFile(projectRoot, "src/main.ts", "import { Button } from \"@app/Button\";\n");

            var symbols = SymbolExtractor.Extract(1, "typescript", File.ReadAllText(sourcePath), sourcePath);

            Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "src/app/Button.ts");
            Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "fallback/@app/Button.ts");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Extract_TypeScript_ResolvesNestedTsconfigAliasesRelativeToProjectRoot()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("tsconfig_alias_nested_symbols");
        try
        {
            WriteFile(projectRoot, "packages/app/tsconfig.json", """
                {
                  "compilerOptions": {
                    "baseUrl": ".",
                    "paths": {
                      "@/*": ["src/*"]
                    }
                  }
                }
                """);
            WriteFile(projectRoot, "packages/app/src/components/Button.tsx", "export const Button = 1;\n");
            var sourcePath = WriteFile(projectRoot, "packages/app/src/page.tsx", "import { Button } from \"@/components/Button\";\n");

            var symbols = SymbolExtractor.Extract(1, "typescript", File.ReadAllText(sourcePath), sourcePath, projectRoot);

            Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "packages/app/src/components/Button.tsx");
            Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "src/components/Button.tsx");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Extract_TypeScript_OversizedTsconfigSkipsPathAliasesWithWarning()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("tsconfig_alias_oversized_symbols");
        try
        {
            WriteFile(
                projectRoot,
                "tsconfig.json",
                "{\"compilerOptions\":{\"baseUrl\":\".\",\"paths\":{\"@/*\":[\"src/*\"]}},\"pad\":\"" + new string('a', 260 * 1024) + "\"}");
            WriteFile(projectRoot, "src/components/Button.tsx", "export const Button = 1;\n");
            var sourcePath = WriteFile(projectRoot, "src/main.ts", "import { Button } from \"@/components/Button\";\n");

            List<SymbolRecord> symbols = [];
            var stderr = ConsoleCapture.CaptureError(() =>
                symbols = SymbolExtractor.Extract(1, "typescript", File.ReadAllText(sourcePath), sourcePath));

            Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "@/components/Button");
            Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "src/components/Button.tsx");
            Assert.Contains("Skipped TypeScript path alias config", stderr, StringComparison.Ordinal);
            Assert.Contains("exceeds", stderr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Extract_TypeScript_MalformedTsconfigSkipsPathAliasesWithWarning()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("tsconfig_alias_malformed_symbols");
        try
        {
            WriteFile(projectRoot, "tsconfig.json", """
                {
                  "compilerOptions": {
                    "baseUrl": ".",
                    "paths": {
                      "@/*": ["src/*"]
                    }
                  }
                """);
            WriteFile(projectRoot, "src/components/Button.tsx", "export const Button = 1;\n");
            var sourcePath = WriteFile(projectRoot, "src/main.ts", "import { Button } from \"@/components/Button\";\n");

            List<SymbolRecord> symbols = [];
            var stderr = ConsoleCapture.CaptureError(() =>
                symbols = SymbolExtractor.Extract(1, "typescript", File.ReadAllText(sourcePath), sourcePath));

            Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "@/components/Button");
            Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "src/components/Button.tsx");
            Assert.Contains("Skipped TypeScript path alias config", stderr, StringComparison.Ordinal);
            Assert.Contains("tsconfig_json_invalid", stderr, StringComparison.Ordinal);
            Assert.Contains("could not be parsed as JSON", stderr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Extract_TypeScript_TsconfigWarningSanitizesConfigPath_Issue3819()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("tsconfig_alias_sanitize_symbols");
        try
        {
            WriteFile(projectRoot, "tsconfig.json", "{ invalid json");
            var sourcePath = WriteFile(projectRoot, "src/main.ts", "import { Button } from \"@/components/Button\";\n");

            List<SymbolRecord> symbols = [];
            var stderr = ConsoleCapture.CaptureError(() =>
                symbols = SymbolExtractor.Extract(1, "typescript", File.ReadAllText(sourcePath), sourcePath));

            Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "@/components/Button");
            Assert.Contains("Skipped TypeScript path alias config tsconfig.json", stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(projectRoot, stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(projectRoot.Replace('\\', '/'), stderr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Extract_TypeScript_UnreadableTsconfigSkipsPathAliasesWithReadFailedWarning_Issue3438()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("tsconfig_alias_unreadable_symbols");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "tsconfig.json"));
            WriteFile(projectRoot, "src/components/Button.tsx", "export const Button = 1;\n");
            var sourcePath = WriteFile(projectRoot, "src/main.ts", "import { Button } from \"@/components/Button\";\n");

            List<SymbolRecord> symbols = [];
            var stderr = ConsoleCapture.CaptureError(() =>
                symbols = SymbolExtractor.Extract(1, "typescript", File.ReadAllText(sourcePath), sourcePath));

            Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "@/components/Button");
            Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "src/components/Button.tsx");
            Assert.Contains("Skipped TypeScript path alias config", stderr, StringComparison.Ordinal);
            Assert.Contains("tsconfig_read_failed", stderr, StringComparison.Ordinal);
            Assert.Contains("could not be read", stderr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Extract_TypeScript_DeepTsconfigJsonSkipsPathAliasesWithWarning()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("tsconfig_alias_deep_json_symbols");
        try
        {
            var deepJson = string.Concat(Enumerable.Repeat("{\"nested\":", 40)) + "0" + new string('}', 40);
            WriteFile(
                projectRoot,
                "tsconfig.json",
                "{\"compilerOptions\":{\"baseUrl\":\".\",\"paths\":{\"@/*\":[\"src/*\"]}},\"deep\":" + deepJson + "}");
            WriteFile(projectRoot, "src/components/Button.tsx", "export const Button = 1;\n");
            var sourcePath = WriteFile(projectRoot, "src/main.ts", "import { Button } from \"@/components/Button\";\n");

            List<SymbolRecord> symbols = [];
            var stderr = ConsoleCapture.CaptureError(() =>
                symbols = SymbolExtractor.Extract(1, "typescript", File.ReadAllText(sourcePath), sourcePath));

            Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "@/components/Button");
            Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "src/components/Button.tsx");
            Assert.Contains("Skipped TypeScript path alias config", stderr, StringComparison.Ordinal);
            Assert.Contains("tsconfig_json_invalid", stderr, StringComparison.Ordinal);
            Assert.Contains("32-level depth limit", stderr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Extract_TypeScript_ExcessiveTsconfigExtendsDepthSkipsInheritedPathAliasesWithWarning()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("tsconfig_alias_deep_extends_symbols");
        try
        {
            WriteFile(projectRoot, "tsconfig.json", "{\"extends\":\"./tsconfig.1.json\"}");
            for (var i = 1; i <= 8; i++)
                WriteFile(projectRoot, $"tsconfig.{i}.json", "{\"extends\":\"./tsconfig." + (i + 1) + ".json\"}");
            WriteFile(projectRoot, "tsconfig.9.json", """
                {
                  "compilerOptions": {
                    "baseUrl": ".",
                    "paths": {
                      "~lib/*": ["lib/*"]
                    }
                  }
                }
                """);
            WriteFile(projectRoot, "lib/math.ts", "export const sum = 1;\n");
            var sourcePath = WriteFile(projectRoot, "src/app.ts", "import { sum } from \"~lib/math\";\n");

            List<SymbolRecord> symbols = [];
            var stderr = ConsoleCapture.CaptureError(() =>
                symbols = SymbolExtractor.Extract(1, "typescript", File.ReadAllText(sourcePath), sourcePath));

            Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "~lib/math");
            Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "lib/math.ts");
            Assert.Contains("path_alias_depth_limit", stderr, StringComparison.Ordinal);
            Assert.Contains("extends depth", stderr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Extract_TypeScript_TsconfigExtendsTotalBytesCapSkipsInheritedPathAliasesWithWarning()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("tsconfig_alias_total_bytes_symbols");
        try
        {
            var pad = new string('a', 210 * 1024);
            WriteFile(projectRoot, "tsconfig.json", "{\"extends\":\"./tsconfig.1.json\",\"pad\":\"" + pad + "\"}");
            WriteFile(projectRoot, "tsconfig.1.json", "{\"extends\":\"./tsconfig.2.json\",\"pad\":\"" + pad + "\"}");
            WriteFile(projectRoot, "tsconfig.2.json", "{\"compilerOptions\":{\"baseUrl\":\".\",\"paths\":{\"~lib/*\":[\"lib/*\"]}},\"pad\":\"" + pad + "\"}");
            WriteFile(projectRoot, "lib/math.ts", "export const sum = 1;\n");
            var sourcePath = WriteFile(projectRoot, "src/app.ts", "import { sum } from \"~lib/math\";\n");

            List<SymbolRecord> symbols = [];
            var stderr = ConsoleCapture.CaptureError(() =>
                symbols = SymbolExtractor.Extract(1, "typescript", File.ReadAllText(sourcePath), sourcePath));

            Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "~lib/math");
            Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "lib/math.ts");
            Assert.Contains("extends chain exceeds", stderr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Extract_TypeScript_ExcessiveTsconfigPathAliasRulesTruncatesWithWarning()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("tsconfig_alias_many_rules_symbols");
        try
        {
            var maxRules = GetSymbolExtractorIntConstant("MaxTypeScriptPathAliasRules");
            var paths = new StringBuilder();
            for (var i = 0; i < maxRules; i++)
            {
                if (i > 0)
                    paths.Append(',');
                paths.Append('"').Append("@skip").Append(i).Append("/*").Append("\":[\"missing").Append(i).Append("/*\"]");
            }

            paths.Append(",\"@hit/*\":[\"src/*\"]");

            WriteFile(
                projectRoot,
                "tsconfig.json",
                "{\"compilerOptions\":{\"baseUrl\":\".\",\"paths\":{" + paths + "}}}");
            WriteFile(projectRoot, "src/Button.ts", "export const Button = 1;\n");
            var sourcePath = WriteFile(projectRoot, "src/main.ts", "import { Button } from \"@hit/Button\";\n");

            List<SymbolRecord> symbols = [];
            var stderr = ConsoleCapture.CaptureError(() =>
                symbols = SymbolExtractor.Extract(1, "typescript", File.ReadAllText(sourcePath), sourcePath));

            Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "@hit/Button");
            Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "src/Button.ts");
            Assert.Contains("Truncated TypeScript path alias rules", stderr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Extract_TypeScript_ExcessiveTsconfigPathAliasTargetsTruncatesWithWarning()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("tsconfig_alias_many_targets_symbols");
        try
        {
            var maxTargets = GetSymbolExtractorIntConstant("MaxTypeScriptPathAliasTargetsPerRule");
            var targets = new StringBuilder();
            for (var i = 0; i < maxTargets; i++)
            {
                if (i > 0)
                    targets.Append(',');
                targets.Append('"').Append("missing").Append(i).Append("/*").Append('"');
            }

            targets.Append(",\"src/*\"");

            WriteFile(
                projectRoot,
                "tsconfig.json",
                "{\"compilerOptions\":{\"baseUrl\":\".\",\"paths\":{\"@hit/*\":[" + targets + "]}}}");
            WriteFile(projectRoot, "src/Button.ts", "export const Button = 1;\n");
            var sourcePath = WriteFile(projectRoot, "src/main.ts", "import { Button } from \"@hit/Button\";\n");

            List<SymbolRecord> symbols = [];
            var stderr = ConsoleCapture.CaptureError(() =>
                symbols = SymbolExtractor.Extract(1, "typescript", File.ReadAllText(sourcePath), sourcePath));

            Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "@hit/Button");
            Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "src/Button.ts");
            Assert.Contains("Truncated TypeScript path alias targets", stderr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Extract_TypeScript_ExcessiveTsconfigPathAliasTotalTargetsTruncatesWithWarning()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("tsconfig_alias_total_targets_symbols");
        try
        {
            var maxTargetsPerRule = GetSymbolExtractorIntConstant("MaxTypeScriptPathAliasTargetsPerRule");
            var maxTotalTargets = GetSymbolExtractorIntConstant("MaxTypeScriptPathAliasTotalTargets");
            var paths = new StringBuilder();
            var remainingTargets = maxTotalTargets;
            var rule = 0;
            while (remainingTargets > 0)
            {
                if (paths.Length > 0)
                    paths.Append(',');

                var targetsForRule = Math.Min(maxTargetsPerRule, remainingTargets);
                paths.Append('"').Append("@skip").Append(rule).Append("/*").Append("\":[");
                for (var target = 0; target < targetsForRule; target++)
                {
                    if (target > 0)
                        paths.Append(',');
                    paths.Append('"').Append("missing").Append(rule).Append('_').Append(target).Append("/*").Append('"');
                }

                paths.Append(']');
                remainingTargets -= targetsForRule;
                rule++;
            }

            paths.Append(",\"@hit/*\":[\"src/*\"]");

            WriteFile(
                projectRoot,
                "tsconfig.json",
                "{\"compilerOptions\":{\"baseUrl\":\".\",\"paths\":{" + paths + "}}}");
            WriteFile(projectRoot, "src/Button.ts", "export const Button = 1;\n");
            var sourcePath = WriteFile(projectRoot, "src/main.ts", "import { Button } from \"@hit/Button\";\n");

            List<SymbolRecord> symbols = [];
            var stderr = ConsoleCapture.CaptureError(() =>
                symbols = SymbolExtractor.Extract(1, "typescript", File.ReadAllText(sourcePath), sourcePath));

            Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "@hit/Button");
            Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "src/Button.ts");
            Assert.Contains("Truncated TypeScript path alias targets", stderr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Extract_TypeScript_OverlongTsconfigPathAliasStringsAreIgnoredWithBoundedWarning()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("tsconfig_alias_long_strings_symbols");
        try
        {
            var maxPatternLength = GetSymbolExtractorIntConstant("MaxTypeScriptPathAliasPatternLength");
            var maxTargetLength = GetSymbolExtractorIntConstant("MaxTypeScriptPathAliasTargetLength");
            var longPatternPrefix = "@" + new string('a', maxPatternLength);
            var longPattern = longPatternPrefix + "/*";
            var longTarget = "src/" + new string('b', maxTargetLength + 1) + "/*";
            WriteFile(
                projectRoot,
                "tsconfig.json",
                "{\"compilerOptions\":{\"baseUrl\":\".\",\"paths\":{\""
                + longPattern
                + "\":[\"src/*\"],\"@longtarget/*\":[\""
                + longTarget
                + "\"]}}}");
            WriteFile(projectRoot, "src/Button.ts", "export const Button = 1;\n");
            var sourcePath = WriteFile(projectRoot, "src/main.ts", """
                import { Button } from "__LONG_PATTERN__/Button";
                import { Other } from "@longtarget/Other";
                """.Replace("__LONG_PATTERN__", longPatternPrefix, StringComparison.Ordinal));

            List<SymbolRecord> symbols = [];
            var stderr = ConsoleCapture.CaptureError(() =>
                symbols = SymbolExtractor.Extract(1, "typescript", File.ReadAllText(sourcePath), sourcePath));

            Assert.Contains(symbols, s => s.Kind == "import" && s.Name == longPatternPrefix + "/Button");
            Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "src/Button.ts");
            Assert.Contains("Ignored TypeScript path alias rules", stderr, StringComparison.Ordinal);
            Assert.Contains("Ignored TypeScript path alias targets", stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(longPattern, stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(longTarget, stderr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Extract_TypeScript_OverlongPathAliasModuleSpecifierSkipsResolutionWithBoundedWarning()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("tsconfig_alias_long_module_symbols");
        try
        {
            var maxModuleSpecifierLength = GetSymbolExtractorIntConstant("MaxTypeScriptPathAliasModuleSpecifierLength");
            var longModuleName = "@/" + new string('a', maxModuleSpecifierLength + 1);
            WriteFile(
                projectRoot,
                "tsconfig.json",
                "{\"compilerOptions\":{\"baseUrl\":\".\",\"paths\":{\"@/*\":[\"src/*\"]}}}");
            var sourcePath = WriteFile(projectRoot, "src/main.ts", "import value from \"" + longModuleName + "\";\n");

            List<SymbolRecord> symbols = [];
            var stderr = ConsoleCapture.CaptureError(() =>
                symbols = SymbolExtractor.Extract(1, "typescript", File.ReadAllText(sourcePath), sourcePath));

            Assert.Contains(symbols, s => s.Kind == "import" && s.Name == longModuleName);
            Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name.StartsWith("src/", StringComparison.Ordinal));
            Assert.Contains("Skipped TypeScript path alias resolution", stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(longModuleName, stderr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Extract_TypeScript_OverlongPathAliasSubstitutionSkipsCandidateWithBoundedWarning()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("tsconfig_alias_long_substitution_symbols");
        try
        {
            var maxTargetLength = GetSymbolExtractorIntConstant("MaxTypeScriptPathAliasTargetLength");
            var maxSubstitutedTargetLength = GetSymbolExtractorIntConstant("MaxTypeScriptPathAliasSubstitutedTargetLength");
            var wildcard = new string('a', (maxSubstitutedTargetLength / maxTargetLength) + 2);
            var longSubstitutingTarget = new string('*', maxTargetLength);
            WriteFile(
                projectRoot,
                "tsconfig.json",
                "{\"compilerOptions\":{\"baseUrl\":\".\",\"paths\":{\"@/*\":[\""
                + longSubstitutingTarget
                + "\",\"src/*\"]}}}");
            WriteFile(projectRoot, "src/" + wildcard + ".ts", "export const value = 1;\n");
            var sourcePath = WriteFile(projectRoot, "src/main.ts", "import { value } from \"@/" + wildcard + "\";\n");

            List<SymbolRecord> symbols = [];
            var stderr = ConsoleCapture.CaptureError(() =>
                symbols = SymbolExtractor.Extract(1, "typescript", File.ReadAllText(sourcePath), sourcePath));

            Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "src/" + wildcard + ".ts");
            Assert.Contains("Skipped TypeScript path alias target substitution", stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(longSubstitutingTarget, stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(wildcard, stderr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Extract_TypeScript_ExcessivePathAliasExpansionCandidatesTruncatesWithWarning_Issue3764()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("tsconfig_alias_expansion_candidates_symbols");
        try
        {
            var maxExpansionCandidates = GetSymbolExtractorIntConstant("MaxTypeScriptPathAliasExpansionCandidates");
            var moduleBody = new string('a', 80);
            var ruleCount = (maxExpansionCandidates / 20) + 4;
            var paths = new StringBuilder();
            for (var i = 0; i < ruleCount; i++)
            {
                if (i > 0)
                    paths.Append(',');

                var prefixLength = i % 40;
                var suffixLength = i / 40;
                var pattern = "@/"
                    + moduleBody[..prefixLength]
                    + "*"
                    + (suffixLength == 0 ? string.Empty : moduleBody[^suffixLength..]);
                paths.Append('"').Append(pattern).Append("\":[\"missing").Append(i).Append("/*\"]");
            }

            WriteFile(
                projectRoot,
                "tsconfig.json",
                "{\"compilerOptions\":{\"baseUrl\":\".\",\"paths\":{" + paths + "}}}");
            var moduleName = "@/" + moduleBody;
            var sourcePath = WriteFile(projectRoot, "src/main.ts", "import value from \"" + moduleName + "\";\n");

            List<SymbolRecord> symbols = [];
            var stderr = ConsoleCapture.CaptureError(() =>
                symbols = SymbolExtractor.Extract(1, "typescript", File.ReadAllText(sourcePath), sourcePath));

            Assert.Contains(symbols, s => s.Kind == "import" && s.Name == moduleName);
            Assert.Contains("Truncated TypeScript path alias expansion candidates", stderr, StringComparison.Ordinal);
            Assert.Contains("path_alias_expansion_candidate_limit", stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(moduleName, stderr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Extract_JavaScript_ResolvesJsconfigPathAliasImportsAndKeepsMissesLiteral()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("jsconfig_alias_symbols");
        try
        {
            WriteFile(projectRoot, "jsconfig.json", """
                {
                  "compilerOptions": {
                    "baseUrl": ".",
                    "paths": {
                      "~components/*": ["components/*"]
                    }
                  }
                }
                """);
            WriteFile(projectRoot, "components/Card.jsx", "export const Card = () => null;\n");
            var sourcePath = WriteFile(projectRoot, "src/view.js", """
                import Card from "~components/Card";
                import Missing from "~components/Missing";
                """);

            var symbols = SymbolExtractor.Extract(1, "javascript", File.ReadAllText(sourcePath), sourcePath);

            Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "components/Card.jsx");
            Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "~components/Missing");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }
}
