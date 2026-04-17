using CodeIndex.Indexer;

namespace CodeIndex.Tests;

/// <summary>
/// Tests for SymbolExtractor.
/// SymbolExtractorのテスト。
/// </summary>
public class SymbolExtractorTests
{
    [Fact]
    public void Extract_Python_DetectsFunctions()
    {
        // Should detect both sync and async functions
        // 同期・非同期関数を検出する
        var content = "def authenticate(user):\n    pass\nasync def fetch_data():\n    pass";
        var symbols = SymbolExtractor.Extract(1, "python", content);

        Assert.Equal(2, symbols.Count);
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "authenticate");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "fetch_data");
    }

    [Fact]
    public void Extract_Python_DetectsClasses()
    {
        var content = "class UserService:\n    pass";
        var symbols = SymbolExtractor.Extract(1, "python", content);

        Assert.Single(symbols);
        Assert.Equal("class", symbols[0].Kind);
        Assert.Equal("UserService", symbols[0].Name);
    }

    [Fact]
    public void Extract_Python_DetectsDecoratedAndDunderMethods()
    {
        var content = "@dataclass\nclass User:\n    name: str\n    age: int\n\n    def __init__(self, name: str, age: int) -> None:\n        self.name = name\n\n    @property\n    def display_name(self) -> str:\n        return self.name\n\n    def __str__(self) -> str:\n        return self.name\n\n    @staticmethod\n    def create(name: str) -> 'User':\n        return User(name, 0)";
        var symbols = SymbolExtractor.Extract(1, "python", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "User");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "__init__");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "display_name");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "__str__");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "create");
    }

    [Fact]
    public void Extract_JavaScript_DetectsFunctionsAndClasses()
    {
        // Should detect exported and non-exported functions and classes
        // export有無にかかわらず関数とクラスを検出する
        var content = "export function login() {}\nclass AuthService {}\nimport React from 'react'";
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "login");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "AuthService");
        Assert.Contains(symbols, s => s.Kind == "import");
    }

    [Fact]
    public void Extract_JavaScript_StringBraceDoesNotBreakFollowingContainerAssignment()
    {
        var content = """
            export class Example {
              foo() {
                const value = "}";
                return value;
              }

              bar() {
                return 1;
              }
            }
            """;
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
        var content = """
            export class Example {
              foo() {
                const value = `}`;
                return value;
              }

              bar() {
                return 1;
              }
            }
            """;
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
    public void Extract_TypeScript_TemplateInterpolationBracesStillCountTowardMethodRange()
    {
        var content = """
            export class Example {
              foo() {
                const value = `${format({ answer: 42 })}`;
                return value;
              }

              bar() {
                return 1;
              }
            }
            """;
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
    public void Extract_JavaScript_DetectsExportDefaultClassMembers()
    {
        var content = """
            export default class DefaultJs {
                run() {}
            }
            """;
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
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "iterator" && s.ContainerName == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "stream" && s.ContainerName == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "visible" && s.ContainerName == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "#hidden" && s.Signature == "#hidden() {}");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "iterator" && s.Signature == "*iterator() {}");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "stream" && s.Signature == "async *stream() {}");
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

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "[Symbol.asyncIterator]" && s.Signature == "async [Symbol.asyncIterator]() {}");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "[Symbol.toStringTag]" && s.Signature == "get [Symbol.toStringTag]() {}");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "[key]" && s.Signature == "set [key](value) {}");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "[Symbol.iterator]" && s.Signature == "async *[Symbol.iterator]() {}");
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
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "generate" && s.ContainerName == "Example");
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
    public void Extract_JavaScript_DetectsCommonJsExportsClassExpressionMethods()
    {
        var content = "exports.Service = class NamedService { run() {} };";
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Service");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "NamedService");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "Service");
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
    public void Extract_JavaScript_DetectsCommonJsModuleExportsClassExpressionMethods()
    {
        var content = "module.exports = class { run() {} };";
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "default");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "default");
    }

    [Fact]
    public void Extract_JavaScript_DetectsMultilineCommonJsModuleExportsClassExpressionMethods()
    {
        var content = """
            module.exports =
                class {
                    run() {}
                };
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "default");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "default");
    }

    [Fact]
    public void Extract_JavaScript_DetectsParenthesizedCommonJsModuleExportsClassExpressionMethods()
    {
        var content = """
            module.exports = (class {
                run() {}
            });
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "default");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "default");
    }

    [Fact]
    public void Extract_JavaScript_DetectsCommonJsModuleExportsPropertyClassExpressionMethods()
    {
        var content = "module.exports.Service = class { run() {} };";
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Service");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "Service");
    }

    [Fact]
    public void Extract_JavaScript_DetectsCommonJsClassExpressionInsideTopLevelConditionalBlock()
    {
        var content = """
            if (typeof module !== "undefined") {
                module.exports = class {
                    run() {}
                };
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "default");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "default");
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
    public void Extract_JavaScript_DoesNotLeakClassMethodLocalClassExpressionMethods()
    {
        var content = """
            export class Outer {
                method() {
                    const Inner = class {
                        run() {}
                    };
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Outer");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "method" && s.ContainerName == "Outer");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Inner");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "run");
    }

    [Fact]
    public void Extract_JavaScript_DoesNotLeakClassMethodDirectLocalClasses()
    {
        var content = """
            class Outer {
                method() {
                    class Hidden {
                        run() {}
                    }
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Outer");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "method" && s.ContainerName == "Outer");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Hidden");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "run");
    }

    [Fact]
    public void Extract_JavaScript_DoesNotLeakFunctionLocalClassExpressionMethods()
    {
        var content = """
            function outer() {
                const Service = class {
                    run() {}
                };
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "outer");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "run");
    }

    [Fact]
    public void Extract_JavaScript_DoesNotLeakDirectFunctionLocalClasses()
    {
        var content = """
            function outer() {
                class Hidden {
                    run() {}
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "outer");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Hidden");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "run");
    }

    [Fact]
    public void Extract_JavaScript_DoesNotLeakCommonJsFunctionExpressionLocalClassMethods()
    {
        var content = """
            exports.handler = function () {
                const Local = class {
                    inside() {}
                };
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Local");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "inside");
    }

    [Fact]
    public void Extract_JavaScript_DoesNotLeakBlocklessArrowReturnedClasses()
    {
        var content = """
            const factory = () =>
                class Hidden {
                    method() {}
                };
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "factory");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Hidden");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "method");
    }

    [Fact]
    public void Extract_JavaScript_DoesNotLeakWrappedBlocklessArrowReturnedClassesAndKeepsRange()
    {
        var content = """
            const factory = () =>
                wrap(
                    class Hidden {
                        method() {}
                    }
                );
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        var factory = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "factory"));
        Assert.Equal(1, factory.StartLine);
        Assert.Equal(6, factory.EndLine);
        Assert.Equal(2, factory.BodyStartLine);
        Assert.Equal(6, factory.BodyEndLine);
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Hidden");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "method");
    }

    [Fact]
    public void Extract_JavaScript_BlocklessArrowWithoutSemicolonDoesNotConsumeFollowingTopLevelClass()
    {
        var content = """
            const factory = () =>
                class Hidden {
                    method() {}
                }
            class Visible {
                keep() {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        var factory = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "factory"));
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
    public void Extract_JavaScript_BlocklessArrowWithoutSemicolonDoesNotConsumeFollowingExpressionStatement()
    {
        var content = """
            const factory = () =>
                class Hidden {
                    method() {}
                }
            foo();
            class Visible {
                keep() {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        var factory = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "factory"));
        Assert.Equal(4, factory.EndLine);
        Assert.Equal(4, factory.BodyEndLine);
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Visible");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep" && s.ContainerName == "Visible");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Hidden");
    }

    [Fact]
    public void Extract_JavaScript_BlocklessArrowWithoutSemicolonDoesNotHideFollowingCommonJsClassExport()
    {
        var content = """
            const factory = () =>
                class Hidden {
                    method() {}
                }
            exports.Service = class Visible {
                keep() {}
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        var factory = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "factory"));
        Assert.Equal(4, factory.EndLine);
        Assert.Equal(4, factory.BodyEndLine);
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Service");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep" && s.ContainerName == "Service");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Hidden");
    }

    [Fact]
    public void Extract_JavaScript_DoesNotLeakIifeLocalClassMethods()
    {
        var content = """
            (function () {
                const Local = class {
                    inside() {}
                };
            })();
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Local");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "inside");
    }

    [Fact]
    public void Extract_JavaScript_DoesNotLeakStaticBlockLocalClassMethods()
    {
        var content = """
            class Outer {
                static {
                    const Local = class {
                        inside() {}
                    };
                }

                keep() {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Outer");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Local");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "inside");
    }

    [Fact]
    public void Extract_JavaScript_DoesNotLeakDirectStaticBlockLocalClasses()
    {
        var content = """
            class Outer {
                static {
                    class Local {
                        inside() {}
                    }
                }

                keep() {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Outer");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Local");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "inside");
    }

    [Fact]
    public void Extract_JavaScript_DoesNotLeakObjectLiteralConciseMethodLocalClasses()
    {
        var content = """
            const obj = {
                method() {
                    class Inner {
                        run() {}
                    }
                }
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Inner");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "run");
    }

    [Fact]
    public void Extract_JavaScript_DoesNotLeakGetterSetterLocalClasses()
    {
        var content = """
            const obj = {
                get value() {
                    class HiddenGetter {
                        run() {}
                    }
                    return 1;
                },
                set value(input) {
                    class HiddenSetter {
                        run() {}
                    }
                }
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "HiddenGetter");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "HiddenSetter");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "run");
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

    [Fact]
    public void Extract_TypeScript_DetectsAbstractClassAndNamespace()
    {
        var content = "export abstract class BaseService {\n    abstract getName(): string;\n}\ndeclare module 'express' {\n    interface Request { }\n}\nnamespace App.Models {\n    export type ID = string;\n}";
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "BaseService");
        // Quoted ambient module declaration / 引用符付きアンビエントモジュール宣言
        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "express");
        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "App.Models");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "ID");
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
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "iterator" && s.ContainerName == "Example" && s.ReturnType == "Iterable<number>");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "stream" && s.ContainerName == "Example" && s.ReturnType == "AsyncIterable<number>");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "visible" && s.ContainerName == "Example" && s.ReturnType == "void");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "#hidden" && s.Signature == "#hidden(): void {}");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "iterator" && s.Signature == "*iterator(): Iterable<number> {}");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "stream" && s.Signature == "async *stream(): AsyncIterable<number> {}");
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

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "[Symbol.asyncIterator]" && s.Signature == "async [Symbol.asyncIterator](): AsyncGenerator<number> {}" && s.ReturnType == "AsyncGenerator<number>");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "[Symbol.iterator]" && s.Signature == "public static [Symbol.iterator](): IterableIterator<number> {}" && s.Visibility == "public" && s.ReturnType == "IterableIterator<number>");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "[Symbol.toStringTag]" && s.Signature == "get [Symbol.toStringTag](): string {}" && s.ReturnType == "string");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "[key]" && s.Signature == "set [key](value: string) {}");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "[Symbol.dispose]" && s.Signature == "async *[Symbol.dispose](): AsyncGenerator<string> {}" && s.ReturnType == "AsyncGenerator<string>");
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
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "generate" && s.ContainerName == "Example" && s.ReturnType == "Iterable<number>");
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

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "factory");
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

        var factory = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "factory"));
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

        var factory = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "factory"));
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

        var factory = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "factory"));
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

        var factory = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "factory"));
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

    [Fact]
    public void Extract_CSharp_DetectsClassesAndMethods()
    {
        var content = "public class UserService\n{\n    public async Task<User> GetUser(int id)\n    {\n    }\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "UserService");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "GetUser");
    }

    [Fact]
    public void Extract_CSharp_RawStringFixturesDoNotLeakPhantomSymbols()
    {
        var content = """""
            public class FixtureHost
            {
                public void UsesRawFixture()
                {
                    const string fixture = """
                        public class App
                        {
                            public void Run()
                            {
                            }
                        }

                        function main()
                        end
                        """;

                    const string wider = """"
                        public class Wider
                        """";
                }
            }
            """"";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var method = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "UsesRawFixture"));
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "FixtureHost");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "App");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Wider");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "Run");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "main");
        Assert.Equal(20, method.EndLine);
        Assert.Equal(20, method.BodyEndLine);
    }

    [Fact]
    public void Extract_CSharp_NestedRawStringInsideInterpolation_DoesNotLeakPhantomSymbols()
    {
        var content = """""
            public class FixtureHost
            {
                public int Run() => 1;
                public string Id(string value) => value;

                public int UsesRawFixture()
                {
                    return $"""
                        value = {Id("""
                            public class Phantom
                            {
                                public void Go() { }
                            }
                            """) + Run()}
                        """.Length;
                }
            }
            """"";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, symbol => symbol.Kind == "class" && symbol.Name == "FixtureHost");
        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "UsesRawFixture");
        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "Run");
        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "Id");
        Assert.DoesNotContain(symbols, symbol => symbol.Kind == "class" && symbol.Name == "Phantom");
        Assert.DoesNotContain(symbols, symbol => symbol.Kind == "function" && symbol.Name == "Go");
    }

    [Fact]
    public void Extract_CSharp_InterpolatedVerbatimStringWithEscapedBraces_DoesNotLeakPhantomSymbols()
    {
        var content = """
            public class FixtureHost
            {
                public string Render()
                {
                    return $@"{{
                        public class Phantom
                    }}";
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, symbol => symbol.Kind == "class" && symbol.Name == "FixtureHost");
        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "Render");
        Assert.DoesNotContain(symbols, symbol => symbol.Kind == "class" && symbol.Name == "Phantom");
    }

    [Fact]
    public void Extract_CSharp_CommentedTripleQuotesDoNotHideFollowingMembers()
    {
        var content = "public class FixtureHost\n{\n    // \"\"\" this is only a comment marker\n    public void Run() { }\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "FixtureHost");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Run");
    }

    [Fact]
    public void Extract_CSharp_CharLiteralBraceDoesNotBreakFollowingContainerAssignment()
    {
        var content = """
            namespace Demo;

            public class FixtureHost
            {
                public bool IsClosingBrace(char c)
                {
                    return c is not '}';
                }

                public void AfterBraceLiteral()
                {
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var host = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "FixtureHost"));
        var after = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "AfterBraceLiteral"));

        Assert.Equal(13, host.EndLine);
        Assert.Equal("class", after.ContainerKind);
        Assert.Equal("FixtureHost", after.ContainerName);
    }

    [Fact]
    public void Extract_CSharp_MultilineRawStringBraceDoesNotBreakFollowingContainerAssignment()
    {
        var content = """"
            namespace Demo;

            public class FixtureHost
            {
                public string UsesRawFixture()
                {
                    return """
                        }
                        """;
                }

                public void AfterRawString()
                {
                }
            }
            """";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var host = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "FixtureHost"));
        var uses = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "UsesRawFixture"));
        var after = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "AfterRawString"));

        Assert.Equal(15, host.EndLine);
        Assert.Equal(10, uses.EndLine);
        Assert.Equal("class", after.ContainerKind);
        Assert.Equal("FixtureHost", after.ContainerName);
    }

    [Fact]
    public void Extract_CSharp_MultilineVerbatimStringBraceDoesNotBreakFollowingRangeDetection()
    {
        var content = """
            namespace Demo;

            public class FixtureHost
            {
                public string UsesVerbatimFixture()
                {
                    return @"
            {
            ";
                }

                public void AfterVerbatimString()
                {
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var host = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "FixtureHost"));
        var uses = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "UsesVerbatimFixture"));
        var after = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "AfterVerbatimString"));

        Assert.Equal(15, host.EndLine);
        Assert.Equal(10, uses.EndLine);
        Assert.Equal("class", after.ContainerKind);
        Assert.Equal("FixtureHost", after.ContainerName);
    }

    [Fact]
    public void Extract_CSharp_DetectsFileScopedNamespaceAndRecordStruct()
    {
        // C# 10+: file-scoped namespace, global using, record struct
        var content = "global using System.Text;\nnamespace MyApp.Models;\n\npublic record struct Point(int X, int Y);\n\npublic record class User(string Name);";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name.Contains("System.Text"));
        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "MyApp.Models");
        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "Point");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "User");
    }

    [Fact]
    public void Extract_CSharp_DetectsDelegateAndEvent()
    {
        // C# delegate and event / C# デリゲートとイベント
        var content = "public delegate void EventHandler(object sender, EventArgs e);\n\npublic class Button\n{\n    public event EventHandler Click;\n    public static event Action<string> OnLog;\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "delegate" && s.Name == "EventHandler");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Button");
        Assert.Contains(symbols, s => s.Kind == "event" && s.Name == "Click");
        Assert.Contains(symbols, s => s.Kind == "event" && s.Name == "OnLog");
    }

    [Fact]
    public void Extract_CSharp_DetectsSpacedGenericDelegatesAndEvents()
    {
        var content = """
            namespace App;
            using System;

            public delegate Task<int> GetIdAsync(string user);
            public delegate Task<Dictionary<string, int>> LoadAsync();
            public delegate TResult Func<T1, T2, TResult>(T1 a, T2 b);

            public class Pub
            {
                public event Action<string, int> NamedEvent;
                public event Func<string, int, bool> Filter;
                public event EventHandler<ChangedArgs> Changed;
                public event Action OnReady;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "delegate" && s.Name == "GetIdAsync" && s.ReturnType == "Task<int>");
        Assert.Contains(symbols, s => s.Kind == "delegate" && s.Name == "LoadAsync" && s.ReturnType == "Task<Dictionary<string,int>>");
        Assert.Contains(symbols, s => s.Kind == "delegate" && s.Name == "Func" && s.ReturnType == "TResult");

        Assert.Contains(symbols, s => s.Kind == "event" && s.Name == "NamedEvent" && s.ReturnType == "Action<string,int>");
        Assert.Contains(symbols, s => s.Kind == "event" && s.Name == "Filter" && s.ReturnType == "Func<string,int,bool>");
        Assert.Contains(symbols, s => s.Kind == "event" && s.Name == "Changed" && s.ReturnType == "EventHandler<ChangedArgs>");
        Assert.Contains(symbols, s => s.Kind == "event" && s.Name == "OnReady" && s.ReturnType == "Action");
        Assert.DoesNotContain(symbols, s => s.Kind == "event" && s.Name == "int");
    }

    [Fact]
    public void Extract_CSharp_DetectsProperties()
    {
        // C# property with get/set / C# プロパティ（get/set付き）
        var content = "public class User\n{\n    public string Name { get; set; }\n    public int Age { get; init; }\n    public virtual string? Email { get; set; }\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "User");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Name");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Age");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Email");
    }

    [Fact]
    public void Extract_CSharp_DetectsPartialProperties()
    {
        var content = """
            namespace Demo;

            public abstract partial class BaseModel
            {
                public abstract string Description { get; }
            }

            public partial class Model : BaseModel
            {
                public partial string Name
                {
                    get;
                    set;
                }

                public partial int Count
                {
                    get;
                }

                public string NotPartial { get; set; } = string.Empty;
            }

            public partial class Model
            {
                private string _name = string.Empty;

                public partial string Name
                {
                    get => _name;
                    set => _name = value;
                }

                public partial int Count
                    => 42;

                public partial override string Description
                    => "demo";
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.True(symbols.Count(s => s.Kind == "property" && s.Name == "Name") >= 2);
        Assert.True(symbols.Count(s => s.Kind == "property" && s.Name == "Count") >= 2);
        Assert.True(symbols.Count(s => s.Kind == "property" && s.Name == "Description") >= 2);
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "NotPartial");

        var countImplementation = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Count" && s.Line == 34));
        Assert.Equal(34, countImplementation.StartLine);
        Assert.Equal(35, countImplementation.EndLine);

        var descriptionImplementation = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Description" && s.Line == 37));
        Assert.Equal(37, descriptionImplementation.StartLine);
        Assert.Equal(38, descriptionImplementation.EndLine);
    }

    [Fact]
    public void Extract_CSharp_MultilinePropertyHeader_DoesNotCreatePhantomFunctionAndKeepsSignature()
    {
        var content = """
            namespace Demo;

            public class Model
            {
                public string
                    SplitName
                    => "x";
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var property = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "SplitName"));
        Assert.Equal(5, property.StartLine);
        Assert.Equal(7, property.EndLine);
        Assert.Equal("public string SplitName => \"x\";", property.Signature);
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "SplitName");
    }

    [Fact]
    public void Extract_CSharp_LongGenericMultilinePropertyHeader_KeepsReturnTypeAndSignature()
    {
        var content = """
            namespace Demo;

            public class Model
            {
                public Dictionary<
                    string,
                    List<
                        int
                    >>
                    Count
                    => new();
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var property = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Count"));
        Assert.Equal("Dictionary<string,List<int>>", property.ReturnType);
        Assert.Equal("public Dictionary< string, List< int >> Count => new();", property.Signature);
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "Count");
    }

    [Fact]
    public void Extract_CSharp_BraceOnNextLinePropertyHeader_KeepsHeaderSignature()
    {
        var content = """
            namespace Demo;

            public class Model
            {
                public string SplitName
                {
                    get;
                    set;
                }

                public int Count
                { get => 1; }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var splitName = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "SplitName"));
        var count = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Count"));
        Assert.Equal("public string SplitName {", splitName.Signature);
        Assert.Equal("public int Count {", count.Signature);
    }

    [Fact]
    public void Extract_CSharp_MultilineExpressionBodiedProperty_KeepsExpressionBodyRange()
    {
        var content = """
            namespace Demo;

            public partial class Model
            {
                public partial int Count
                    => DateTime.Now.Day switch
                    {
                        > 15 => 2,
                        _ => 1
                    };
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var count = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Count"));
        Assert.Equal(5, count.StartLine);
        Assert.Equal(10, count.EndLine);
        Assert.Equal("public partial int Count => DateTime.Now.Day switch", count.Signature);
    }

    [Fact]
    public void Extract_CSharp_PartialPropertyImplementation_WithAccessorAttribute_IsDetected()
    {
        var content = """
            namespace Demo;

            public partial class Model
            {
                public partial string Name { get; set; }
            }

            public partial class Model
            {
                public partial string Name { [System.Obsolete] get => "x"; set { } }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Equal(2, symbols.Count(s => s.Kind == "property" && s.Name == "Name"));
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Name" && s.Signature != null && s.Signature.Contains("[System.Obsolete]", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_CSharp_PartialPropertyImplementation_WithMultilineAccessorAttribute_IsDetected()
    {
        var content = """
            namespace Demo;

            public partial class Model
            {
                public partial string Name
                {
                    [System.Obsolete(
                        "x"
                    )]
                    get => "x";
                    set { }
                }

                public int Other => 1;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Name");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Other");
    }

    [Fact]
    public void Extract_CSharp_DetectsInlineAttributedProperty()
    {
        var content = """
            using System.Text.Json.Serialization;

            public class UserDto
            {
                [JsonPropertyName("full_name")] public string FullName { get; set; } = string.Empty;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "UserDto");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "FullName");
    }

    [Fact]
    public void Extract_CSharp_DetectsInlinePropertyTargetAttributeWithWhitespace()
    {
        var content = """
            using System.Text.Json.Serialization;

            public class UserDto
            {
                [property : JsonPropertyName("full_name")] public string FullName { get; set; } = string.Empty;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "FullName");
    }

    [Fact]
    public void Extract_CSharp_DetectsNoVisibilityMembers()
    {
        // Classes/methods without explicit visibility (internal by default)
        // 明示的な visibility のないクラス/メソッド（デフォルト internal）
        var content = "namespace MyApp;\n\nclass InternalClass\n{\n    static void Helper() { }\n}\n\nstatic class Utils\n{\n    static int Add(int a, int b) => a + b;\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "InternalClass");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Helper");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Utils");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Add");
    }

    [Fact]
    public void Extract_CSharp_DetectsPrimaryConstructors()
    {
        // C# 12 primary constructors on class, struct, record
        // C# 12 のクラス・構造体・record の primary constructor
        var content = "public class Service(ILogger logger, IDb db)\n{\n}\n\npublic struct Point(double x, double y);";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Service");
        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "Point");
        // Signature should contain the parameter list / シグネチャにパラメータリストが含まれるべき
        var service = symbols.First(s => s.Name == "Service");
        Assert.Contains("ILogger logger", service.Signature);
    }

    [Fact]
    public void Extract_CSharp_AttributeDoesNotBlockNextLine()
    {
        // [Attribute] on the line before class/method should not prevent extraction
        // [Attribute] がクラス/メソッドの前行にあっても抽出を妨げないこと
        var content = "[Serializable]\npublic class Config\n{\n    [Obsolete(\"Use V2\")]\n    public void OldMethod() { }\n\n    [HttpGet(\"/api\")]\n    public async Task<IActionResult> GetItems() { return null; }\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Config");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "OldMethod");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "GetItems");
    }

    [Fact]
    public void Extract_CSharp_TargetedAttributeDeclarations_DoNotBecomePhantomFunctions()
    {
        var content = """
            using System;
            using System.Runtime.CompilerServices;
            using System.Runtime.InteropServices;

            [assembly: CLSCompliant(true)]
            [assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
            [module: SkipLocalsInit]

            public class Fixture
            {
                [return: MarshalAs(UnmanagedType.Bool)]
                public bool Method([param: MarshalAs(UnmanagedType.Bool)] bool value) => value;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Fixture");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Method");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "CLSCompliant");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "AssemblyVersion");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "SkipLocalsInit");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "MarshalAs");
    }

    [Fact]
    public void Extract_CSharp_MultiSectionAttribute_DoesNotLeakTrailingAttributeNamesAsPhantoms()
    {
        // [A, B(args)] multi-section attributes must not leak B/C as phantom `function` symbols.
        // The attribute-stripper must blank out the whole bracket group even when it consumes the entire line,
        // otherwise the method regex latches onto the content after the comma.
        // xUnit/MSTest/ASP.NET/EF attribute conventions rely heavily on this shape.
        // 複数セクション属性 [A, B(args)] の 2つ目以降の属性名が phantom function として漏れないこと。
        var content = """
            using System;
            using System.Diagnostics;

            namespace MultiSectionAttr;

            public class Svc
            {
                [Obsolete, Conditional("DEBUG")]
                public void A() {}

                [Obsolete][Conditional("DEBUG")]
                public void B() {}

                [Obsolete] [Conditional("DEBUG")]
                public void C() {}

                [Obsolete, Conditional("DEBUG")]
                [System.ComponentModel.Description("x")]
                public void D() {}

                [Obsolete, Conditional("DEBUG"), System.ComponentModel.Description("y")]
                public void E() {}

                [Fact, Trait("cat", "io")]
                public void F() {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Svc");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "A");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "B");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "C");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "D");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "E");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "F");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "Conditional");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "Description");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "Trait");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "Obsolete");
    }

    [Fact]
    public void Extract_CSharp_MultiSectionAttributeOnClassOrProperty_DoesNotLeakPhantoms()
    {
        // Comma-separated attribute sections on types and properties (EF/ASP.NET/DataAnnotations shape)
        // must stay clean as well — [Required, StringLength(50), Column("name")] etc.
        // 型・プロパティに付く [Required, StringLength(50), Column("name")] 形でも phantom が出ないこと。
        var content = """
            using System.ComponentModel.DataAnnotations;
            using System.ComponentModel.DataAnnotations.Schema;

            namespace Data;

            [Serializable, ApiController]
            public class User
            {
                [Required, StringLength(50), Column("name")]
                public string Name { get; set; } = "";

                [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
                public int Id { get; set; }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "User");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Name");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Id");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "StringLength");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "Column");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "DatabaseGenerated");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "ApiController");
    }

    [Fact]
    public void Extract_CSharp_DetectsRecordVariants()
    {
        // record, record class, record struct with various modifiers
        var content = "public record UserDto(string Name, int Age);\npublic sealed record class Config(string Key, string Value);\ninternal readonly record struct Point(double X, double Y);";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "UserDto");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Config");
        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "Point");
        // Signature should contain parameters / シグネチャにパラメータが含まれるべき
        var userDto = symbols.First(s => s.Name == "UserDto");
        Assert.Contains("string Name", userDto.Signature);
    }

    [Fact]
    public void Extract_CSharp_DetectsRecordPrimaryComponentsAsProperties()
    {
        var content = """
            namespace App;

            public record Point(int X, int Y);
            public readonly record struct Vec3(double X, double Y, double Z);
            public record Animal(string Name);
            public record Dog(string Name, string Breed) : Animal(Name);
            public record Options(
                string Host,
                int Port) { public bool UseTls { get; init; } = true; }
            public record Container<T>(T Value, int Count) where T : class;
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var pointX = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "X" && s.ContainerName == "Point"));
        Assert.Equal("int", pointX.ReturnType);
        Assert.Equal("class", pointX.ContainerKind);
        Assert.Equal("App.Point", pointX.ContainerQualifiedName);
        Assert.Equal("public", pointX.Visibility);

        var vec3Z = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Z" && s.ContainerName == "Vec3"));
        Assert.Equal("double", vec3Z.ReturnType);
        Assert.Equal("struct", vec3Z.ContainerKind);

        var dogBreed = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Breed" && s.ContainerName == "Dog"));
        Assert.Equal("string", dogBreed.ReturnType);

        var optionsHost = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Host" && s.ContainerName == "Options"));
        Assert.Equal("string", optionsHost.ReturnType);
        Assert.Contains("string Host", optionsHost.Signature);
        Assert.Equal(8, optionsHost.Line);
        var optionsRecord = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Options"));
        Assert.Equal(9, optionsRecord.EndLine);

        var containerValue = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Value" && s.ContainerName == "Container"));
        Assert.Equal("T", containerValue.ReturnType);
    }

    [Fact]
    public void Extract_CSharp_DetectsLongAndCommentedRecordPrimaryComponentsAsProperties()
    {
        var componentLines = string.Join('\n', Enumerable.Range(1, 30).Select(i => $"    int P{i},"));
        var content = $$"""
            namespace App;

            public record Big(
            {{componentLines}}
                int Tail);

            public record Point(
                int X /* separator, comment */,
                // the next component must still parse
                int Y);
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "P1" && s.ContainerName == "Big");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "P30" && s.ContainerName == "Big");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Tail" && s.ContainerName == "Big");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "X" && s.ContainerName == "Point");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Y" && s.ContainerName == "Point");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "separator" && s.ContainerName == "Point");

        var tail = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Tail" && s.ContainerName == "Big"));
        Assert.Equal(34, tail.Line);
        var bigRecord = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Big"));
        Assert.Equal(34, bigRecord.EndLine);

        var pointY = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Y" && s.ContainerName == "Point"));
        Assert.Equal(39, pointY.Line);
    }

    [Fact]
    public void Extract_CSharp_DetectsRecordPrimaryComponentsWithComparisonDefaults()
    {
        var content = """
            public record Threshold(bool Enabled = 1 < 2, int Count = 0);
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var enabled = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Enabled" && s.ContainerName == "Threshold"));
        Assert.Equal("bool", enabled.ReturnType);

        var count = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Count" && s.ContainerName == "Threshold"));
        Assert.Equal("int", count.ReturnType);
    }

    [Fact]
    public void Extract_CSharp_DetectsRecordPrimaryComponentsWithTightComparisonDefaults()
    {
        var content = """
            public record Threshold(bool Enabled = left<right, int Count = 0);
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var enabled = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Enabled" && s.ContainerName == "Threshold"));
        Assert.Equal("bool", enabled.ReturnType);

        var count = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Count" && s.ContainerName == "Threshold"));
        Assert.Equal("int", count.ReturnType);
    }

    [Fact]
    public void Extract_CSharp_DoesNotInjectRecordPrimaryComponentsIntoEarlierSameNamedClass()
    {
        var content = """
            namespace A
            {
                public class Point {}
            }

            namespace B
            {
                public record Point(int X);
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "X" && s.ContainerQualifiedName == "A.Point");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "X" && s.ContainerQualifiedName == "B.Point");
    }

    [Fact]
    public void Extract_CSharp_DetectsBodylessRecordStructDeclarationRange()
    {
        var content = """
            namespace App;

            public readonly record struct Bodyless(
                int X,
                int Y);
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var bodyless = Assert.Single(symbols.Where(s => s.Kind == "struct" && s.Name == "Bodyless"));
        Assert.Equal(5, bodyless.EndLine);

        var y = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Y" && s.ContainerName == "Bodyless"));
        Assert.Equal(5, y.Line);
        Assert.Equal("App.Bodyless", y.ContainerQualifiedName);
    }

    [Fact]
    public void Extract_CSharp_DetectsBodylessRecordBaseAndWhereDeclarationRange()
    {
        var content = """
            namespace App;

            public record Animal(string Name);

            public record Dog(
                string Name)
                : Animal(Name);

            public record Box<T>(
                T Value)
                where T : class;
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var dog = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Dog"));
        Assert.Equal(7, dog.EndLine);

        var box = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Box"));
        Assert.Equal(11, box.EndLine);
    }

    [Fact]
    public void Extract_CSharp_DetectsBodylessRecordBaseComparisonDeclarationRange()
    {
        var content = """
            namespace App;

            public record Child(int X)
                : Base(1 < 2);

            public record Base(bool Flag);
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var child = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Child"));
        Assert.Equal(4, child.EndLine);
    }

    [Fact]
    public void Extract_CSharp_DetectsBodylessRecordTightBaseComparisonDeclarationRange()
    {
        var content = """
            namespace App;

            public record Child(int X)
                : Base(left<right);

            public record Base(bool Flag);
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var child = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Child"));
        Assert.Equal(4, child.EndLine);
    }

    [Fact]
    public void Extract_CSharp_DetectsEmptyBodylessRecordDeclarationRange()
    {
        var content = """
            namespace Repro;

            public record Empty(
            )
                : Base();

            public record Base();

            public record struct EmptyStruct(
            );
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var empty = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Empty"));
        Assert.Equal(5, empty.EndLine);

        var emptyStruct = Assert.Single(symbols.Where(s => s.Kind == "struct" && s.Name == "EmptyStruct"));
        Assert.Equal(10, emptyStruct.EndLine);
    }

    [Fact]
    public void Extract_CSharp_TracksRecordPrimaryComponentLineAfterAttributes()
    {
        var content = """
            public record Person(
                [property: Obsolete]
                string Name,
                [property: Obsolete]
                int Age);
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var name = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Name" && s.ContainerName == "Person"));
        Assert.Equal(3, name.Line);

        var age = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Age" && s.ContainerName == "Person"));
        Assert.Equal(5, age.Line);
    }

    [Fact]
    public void Extract_CSharp_RecordComponents_DoNotDisruptBodyMembersOrDuplicateExplicitProperties()
    {
        var content = """
            namespace App;

            public record Person(string Name)
            {
                public string Name { get; init; } = Name;

                public string Upper() => Name.ToUpperInvariant();
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var nameProperties = symbols.Where(s => s.Kind == "property" && s.Name == "Name" && s.ContainerName == "Person").ToList();
        var nameProperty = Assert.Single(nameProperties);
        Assert.Equal(5, nameProperty.Line);
        Assert.Equal("App.Person", nameProperty.ContainerQualifiedName);

        var upper = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "Upper"));
        Assert.Equal("class", upper.ContainerKind);
        Assert.Equal("Person", upper.ContainerName);
        Assert.Equal("App.Person", upper.ContainerQualifiedName);
    }

    [Fact]
    public void Extract_CSharp_DetectsCompoundVisibility()
    {
        // protected internal and private protected / 複合アクセス修飾子
        var content = "public class Base\n{\n    protected internal void Foo() { }\n    private protected int Bar { get; set; }\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var foo = symbols.FirstOrDefault(s => s.Name == "Foo");
        Assert.NotNull(foo);
        Assert.Equal("protected internal", foo.Visibility);

        var bar = symbols.FirstOrDefault(s => s.Name == "Bar");
        Assert.NotNull(bar);
        Assert.Equal("private protected", bar.Visibility);
    }

    [Fact]
    public void Extract_CSharp_DetectsUsingAlias()
    {
        var content = "using System;\nusing Json = System.Text.Json;\nusing static System.Math;\nglobal using Logging = Microsoft.Extensions.Logging;";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "System");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Json");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "System.Math");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Logging");
    }

    [Fact]
    public void Extract_CSharp_DetectsConstAndStaticReadonly()
    {
        var content = "public class Config\n{\n    public const string Version = \"1.0\";\n    private const int MaxRetries = 3;\n    internal static readonly Dictionary<string, string> Map = new();\n    public string MutableField;\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Version" && s.ReturnType == "string");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "MaxRetries" && s.ReturnType == "int");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Map");
        // Regular mutable fields should NOT be extracted / 通常のフィールドは抽出されないこと
        Assert.DoesNotContain(symbols, s => s.Name == "MutableField");
    }

    [Fact]
    public void Extract_CSharp_DetectsExpressionBodiedMembers()
    {
        var content = "public class Calc\n{\n    public int X => 42;\n    public string Name => \"calc\";\n    public static double Pi => 3.14;\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "X" && s.ReturnType == "int");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Name" && s.ReturnType == "string");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Pi" && s.ReturnType == "double");
    }

    [Fact]
    public void Extract_CSharp_ExpressionBodiedMembers_HaveBodyRanges()
    {
        // issue #233: expression-bodied members must report a body range covering the
        // declaration line through the terminating ';' so reference attribution can find
        // them as the innermost enclosing container.
        // issue #233: 式本体メンバーは、宣言行から終端 ';' までを本体範囲として報告する必要がある。
        // そうすることで参照属性解決が内側コンテナとして認識できる。
        var content = "public class Calc\n{\n    public int Compute() => 42;\n    public int Wrap1() => Compute();\n    public int Wrap3 => Compute();\n    public int MultiLine()\n        => Compute();\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var compute = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "Compute"));
        Assert.Equal(3, compute.BodyStartLine);
        Assert.Equal(3, compute.BodyEndLine);

        var wrap1 = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "Wrap1"));
        Assert.Equal(4, wrap1.BodyStartLine);
        Assert.Equal(4, wrap1.BodyEndLine);

        var wrap3 = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Wrap3"));
        Assert.Equal(5, wrap3.BodyStartLine);
        Assert.Equal(5, wrap3.BodyEndLine);

        var multi = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "MultiLine"));
        Assert.Equal(6, multi.BodyStartLine);
        Assert.Equal(7, multi.BodyEndLine);
    }

    [Fact]
    public void Extract_CSharp_BlockBodiedProperty_AllmanStyle_IsExtracted()
    {
        // issue #233 review follow-up: Allman-style block-bodied properties (with `{` on
        // the next line) were not matched by the property regex, so `callers` would
        // attribute accessor-internal references to the enclosing class. The widened
        // regex plus `ShouldSkipCSharpHeaderOnlyPropertyCandidate` verification must
        // still recognize them as properties with proper body ranges.
        // issue #233 のレビュー指摘: Allman スタイル（次行に `{`）の block-bodied property が
        // property regex でマッチしておらず、accessor 内の参照がクラスに帰属していた。
        // widened regex と `ShouldSkipCSharpHeaderOnlyPropertyCandidate` の組み合わせで
        // 正しく property として認識され、本体範囲も持つ必要がある。
        var content = "public class Calc\n{\n    public int Compute() => 42;\n    public int Wrap\n    {\n        get { return Compute(); }\n    }\n    public string Name\n    {\n        get;\n        set;\n    }\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var wrap = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Wrap"));
        Assert.Equal(4, wrap.StartLine);
        Assert.Equal(7, wrap.EndLine);
        Assert.Equal(5, wrap.BodyStartLine);
        Assert.Equal(7, wrap.BodyEndLine);
        Assert.Equal("int", wrap.ReturnType);
        Assert.Equal("public", wrap.Visibility);

        var name = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Name"));
        Assert.Equal(8, name.StartLine);
        Assert.Equal(12, name.EndLine);
        Assert.Equal(9, name.BodyStartLine);
        Assert.Equal(12, name.BodyEndLine);
        Assert.Equal("string", name.ReturnType);
    }

    [Fact]
    public void Extract_CSharp_MultiLineExpressionBodiedProperty_IsExtracted()
    {
        // issue #233 second review follow-up: multi-line expression-bodied properties,
        // where the declaration is on one line and `=> expr;` on the continuation line,
        // must still be extracted as properties with a body range spanning the two lines.
        // Without this, accessor-internal calls fall through to the enclosing class.
        // issue #233 の再レビュー指摘: 宣言行の次行に `=> expr;` が来る multi-line 式本体
        // プロパティも property として抽出され、本体範囲が宣言行から `;` 行までを覆う必要がある。
        // これができないと accessor 内呼び出しが外側クラスに誤帰属する。
        var content = "public class Calc\n{\n    public int Compute() => 42;\n    public int Wrap\n        => Compute();\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var wrap = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Wrap"));
        Assert.Equal(4, wrap.StartLine);
        Assert.Equal(5, wrap.EndLine);
        Assert.Equal(4, wrap.BodyStartLine);
        Assert.Equal(5, wrap.BodyEndLine);
        Assert.Equal("int", wrap.ReturnType);
        Assert.Equal("public", wrap.Visibility);
    }

    [Fact]
    public void Extract_CSharp_AllmanBlockBodiedProperty_WithIntermediateBlockComment_IsExtracted()
    {
        // issue #233 fourth review follow-up: when an Allman-style block-bodied property
        // has a multi-line `/* ... */` block comment between the header line and the `{`
        // line, the skip guard must traverse the comment via `LexCSharpLine` and still
        // recognize the continuation `{`. A naive prefix-based comment skip only
        // handled `*` / `//` / `/*` line starts and dropped the property entirely.
        // issue #233 第4次レビュー指摘: Allman スタイルの block-bodied property で
        // header 行と `{` の間に multi-line `/* ... */` のブロックコメントがあっても、
        // `LexCSharpLine` でコメントを通り抜けて次の `{` を認識する必要がある。
        // 行頭 prefix だけの素朴なスキップでは `*` / `//` / `/*` の開始行しか飛ばせず、
        // この形の property は落ちていた。
        var content = "public class Calc\n{\n    public int Compute() => 42;\n    public int Wrap\n    /* some multi-line\n       block comment */\n    {\n        get { return Compute(); }\n    }\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var wrap = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Wrap"));
        Assert.Equal(4, wrap.StartLine);
        Assert.Equal(9, wrap.EndLine);
        Assert.Equal(7, wrap.BodyStartLine);
        Assert.Equal(9, wrap.BodyEndLine);
        Assert.Equal("int", wrap.ReturnType);
    }

    [Fact]
    public void Extract_CSharp_MultiLineExpressionBodiedProperty_WithIntermediateBlockComment_IsExtracted()
    {
        // issue #233 fourth review follow-up: same scenario for multi-line expression
        // bodies — `public int Wrap` followed by `/* ... */` and then `=> Compute();`
        // must still be extracted with the property spanning declaration through `;`.
        // issue #233 第4次レビュー指摘: multi-line 式本体プロパティでも同じく、
        // `public int Wrap` の後に `/* ... */`、さらに `=> Compute();` が続く形で
        // 宣言行から `;` 行までを本体範囲とする property が抽出されること。
        var content = "public class Calc\n{\n    public int Compute() => 42;\n    public int WrapExpr\n    /* multi-line\n       comment */\n        => Compute();\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var wrap = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "WrapExpr"));
        Assert.Equal(4, wrap.StartLine);
        Assert.Equal(7, wrap.EndLine);
        Assert.Equal(4, wrap.BodyStartLine);
        Assert.Equal(7, wrap.BodyEndLine);
        Assert.Equal("int", wrap.ReturnType);
    }

    [Fact]
    public void Extract_CSharp_BraceSameLineAccessorNextLineProperty_IsExtracted()
    {
        // issue #233 fifth review follow-up: the common Microsoft-style block-bodied
        // property — `{` on the same line as the declaration and the accessor on the
        // following line — must be recognized as a property with a body range spanning
        // declaration through closing `}`.
        // issue #233 第5次レビュー指摘: `{` が宣言行末にあり、accessor が次行にある
        // 標準的な block-bodied property が property として抽出され、宣言行から `}` 行
        // までを本体範囲として持つこと。
        var content = "public class Calc\n{\n    public int Compute() => 42;\n    public int Wrap {\n        get { return Compute(); }\n    }\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var wrap = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Wrap"));
        Assert.Equal(4, wrap.StartLine);
        Assert.Equal(6, wrap.EndLine);
        Assert.Equal(4, wrap.BodyStartLine);
        Assert.Equal(6, wrap.BodyEndLine);
        Assert.Equal("int", wrap.ReturnType);
    }

    [Fact]
    public void Extract_CSharp_BraceSameLineAccessorNextLine_AcceptsAttributeAndVisibility()
    {
        // issue #233 fifth review follow-up: the bare-brace-same-line guard must also
        // accept next lines that begin with accessor attributes (`[JsonIgnore]`) or a
        // visibility modifier (`private set`) before the `get` / `set` / `init` token.
        // issue #233 第5次レビュー指摘: 同一行 bare `{` のガードは、accessor attribute
        // (`[JsonIgnore]`) や visibility 修飾子 (`private set`) で始まる行も受け入れる必要がある。
        var content = "public class Calc\n{\n    public int Compute() => 42;\n"
            + "    public int WithAttr {\n        [System.Obsolete] get => Compute();\n    }\n"
            + "    public int WithVis {\n        private set { }\n        get { }\n    }\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "WithAttr");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "WithVis");
    }

    [Fact]
    public void Extract_CSharp_BraceSameLineWithoutAccessor_IsNotMisclassifiedAsProperty()
    {
        // issue #233 fifth review follow-up: the bare-brace-same-line guard must reject
        // non-property shapes that happen to be `Type Name {` followed by a body that
        // does not start an accessor (for example a stray method-like block).
        // issue #233 第5次レビュー指摘: 同一行 bare `{` のガードは、`Type Name {` に
        // 続く行が accessor 宣言でない場合（例: accessor でない任意のブロック）を
        // property として採用してはならない。
        var content = "public class Calc\n{\n    public int Stray {\n        Console.WriteLine(1);\n    }\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "Stray");
    }

    [Fact]
    public void Extract_CSharp_HeaderOnlyNonProperty_IsNotMisclassified()
    {
        // issue #233 review follow-up: the header-only property alternation must not
        // swallow keyword lines such as `public class X` or `return Foo` even if they
        // happen to look like `Type Name` before a newline.
        // issue #233 のレビュー指摘: header-only の alternation が `public class X` や
        // `return Foo` のようなキーワード行を property と誤分類しないことを担保する。
        var content = "public class Thing\n{\n    public int Method()\n    {\n        return Thing;\n    }\n}\n";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "property");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Thing");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Method");
    }

    [Fact]
    public void Extract_CSharp_SwitchExpressionArms_DoNotProducePhantomProperties()
    {
        var content = """
            public class Matcher
            {
                public string Describe(object x) => x switch
                {
                    int n when n > 0 => "pos",
                    int neg => "non-pos",
                    string text => text,
                    double d => "double",
                    List<string> list => "list",
                    _ => "other",
                };

                public int Count(int y) => y switch
                {
                    > 0 => 1,
                    0 => 0,
                    _ => -1,
                };
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Matcher");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Describe");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Count");
        Assert.DoesNotContain(symbols, s => s.Kind == "property");
        Assert.DoesNotContain(symbols, s => s.Name == "neg");
        Assert.DoesNotContain(symbols, s => s.Name == "text");
        Assert.DoesNotContain(symbols, s => s.Name == "d");
        Assert.DoesNotContain(symbols, s => s.Name == "list");
        Assert.DoesNotContain(symbols, s => s.Name == "0");
    }

    [Fact]
    public void Extract_CSharp_MultiLineSwitchExpressionArms_DoNotProducePhantomProperties()
    {
        // issue #233 third review follow-up: switch-expression arms whose `=>` is placed
        // on a continuation line must not be misclassified as multi-line expression-bodied
        // properties. Without switch-expression guard coverage on the continuation `=>`,
        // each pattern variable (e.g. `text`, `neg`) would be emitted as a phantom property
        // and `callers` / `impact` would misattribute calls inside the arm to it.
        // issue #233 第3次レビュー指摘: switch expression arm の `=>` が継続行にある
        // multi-line 形を、multi-line 式本体プロパティと誤認しないこと。continuation `=>`
        // まで switch-expression ガードが及ばないと、`text` や `neg` のようなパターン変数が
        // phantom property として抽出され、arm 内の呼び出しが phantom に誤帰属する。
        var content = """
            public class Matcher
            {
                public string Describe(object x) => x switch
                {
                    string text
                        => text.Trim(),
                    int neg
                        => "non-pos",
                    _
                        => "other",
                };
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Matcher");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Describe");
        Assert.DoesNotContain(symbols, s => s.Kind == "property");
        Assert.DoesNotContain(symbols, s => s.Name == "text");
        Assert.DoesNotContain(symbols, s => s.Name == "neg");
    }

    [Fact]
    public void Extract_CSharp_DetectsExplicitInterfaceImpl()
    {
        var content = "public class MyClass : IDisposable, IComparable<MyClass>\n{\n    void IDisposable.Dispose()\n    {\n    }\n    int IComparable<MyClass>.CompareTo(MyClass other) => 0;\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Dispose" && s.ReturnType == "void");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "CompareTo" && s.ReturnType == "int");
    }

    [Fact]
    public void Extract_CSharp_DetectsIndexer()
    {
        var content = "public class Collection\n{\n    public string this[int index]\n    {\n        get => _items[index];\n        set => _items[index] = value;\n    }\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var indexer = symbols.FirstOrDefault(s => s.Name == "Item");
        Assert.NotNull(indexer);
        Assert.Equal("function", indexer.Kind);
        Assert.Equal("string", indexer.ReturnType);
    }

    [Fact]
    public void Extract_CSharp_DetectsOperatorOverloads()
    {
        var content = "using System.Collections.Generic;\npublic unsafe struct Money\n{\n    public static (int whole, int cents) operator +(Money a, Money b) => (0, 0);\n    public static Dictionary<string, int> operator -(Money a, Money b) => new();\n    public static bool operator ==(Money a, Money b) => true;\n    public static checked Money operator checked +(Money a, Money b) => new();\n    public static implicit operator decimal(Money m) => 0m;\n    public static explicit operator Money(decimal d) => new();\n    public static explicit operator checked byte(Money m) => 0;\n    public static explicit operator Dictionary<string,int>(Money m) => new();\n    public static explicit operator (int whole,int cents)(Money m) => (0, 0);\n    public static explicit operator (Dictionary<string, int> map, int count)?(Money m) => null;\n    public static explicit operator (int[] items, int count)(Money m) => ([], 0);\n    public static explicit operator ((int a, int b) pair, int count)(Money m) => ((0, 0), 0);\n    public static unsafe explicit operator int*(Money m) => (int*)0;\n    public static unsafe explicit operator delegate* unmanaged[Cdecl]<int, void>(Money m) => (delegate* unmanaged[Cdecl]<int, void>)0;\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "Money");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "operator +");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "operator -");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "operator ==");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "operator checked +");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "implicit operator decimal");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "explicit operator Money");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "explicit operator checked byte");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "explicit operator Dictionary<string, int>");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "explicit operator (int whole, int cents)");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "explicit operator (Dictionary<string, int> map, int count)?");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "explicit operator (int[] items, int count)");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "explicit operator ((int a, int b) pair, int count)");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "explicit operator int*");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "explicit operator delegate* unmanaged[Cdecl]<int, void>");
    }

    [Fact]
    public void Extract_CSharp_DetectsCheckedOperators()
    {
        // Issue #238: C# 11 user-defined checked operators (unary, binary, and explicit
        // conversion) must be indexed alongside their unchecked counterparts instead of
        // being silently dropped, and the `checked` keyword must survive into the symbol
        // name so AI clients can disambiguate the two overloads.
        // Issue #238: C# 11 のユーザー定義 `operator checked` (単項 / 二項 / 明示的変換) は
        // unchecked 版と両方インデックスされ、`checked` の有無がシンボル名に残ることで
        // AI クライアントがオーバーロードを区別できるようにする。
        var content = """
            namespace Demo;

            public struct N
            {
                public int V;
                public static N operator +(N a, N b) => new() { V = a.V + b.V };
                public static N operator checked +(N a, N b) => checked(new() { V = a.V + b.V });
                public static N operator -(N a, N b) => new() { V = a.V - b.V };
                public static N operator checked -(N a, N b) => checked(new() { V = a.V - b.V });
                public static N operator -(N a) => new() { V = -a.V };
                public static N operator checked -(N a) => checked(new() { V = -a.V });
                public static explicit operator int(N n) => n.V;
                public static explicit operator checked int(N n) => checked((int)n.V);
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "operator +");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "operator checked +");
        Assert.Equal(2, symbols.Count(s => s.Kind == "function" && s.Name == "operator -"));
        Assert.Equal(2, symbols.Count(s => s.Kind == "function" && s.Name == "operator checked -"));
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "explicit operator int");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "explicit operator checked int");
    }

    [Fact]
    public void Extract_CSharp_DetectsPointerReturnTypes()
    {
        // Issue #234: methods with pointer / function-pointer return types must still be indexed.
        // Issue #234: ポインタ / 関数ポインタ戻り値型のメソッドも取りこぼさずインデックスする。
        var content = "namespace Demo;\n\npublic unsafe class FP\n{\n    public int* Get(int[] a) { fixed (int* p = a) { return p; } }\n    public void** Double() => null;\n    public byte* Get1() => null;\n    public delegate*<int, int> Transform() => null;\n    public static unsafe int*[] Arr() => null!;\n    public unsafe void Modify(int* p, int v) { *p = v; }\n    public static unsafe int Deref(int* p) => *p;\n    public int* P { get; set; }\n    public byte* Q => null;\n    public int* this[int i] => null;\n}\n\npublic unsafe delegate int* PointerDelegate(int x);\n";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Get" && s.ReturnType == "int*");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Double" && s.ReturnType == "void**");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Get1" && s.ReturnType == "byte*");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Transform" && s.ReturnType == "delegate*<int, int>");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Arr" && s.ReturnType == "int*[]");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Modify");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Deref");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "P" && s.ReturnType == "int*");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Q" && s.ReturnType == "byte*");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Item" && s.ReturnType == "int*");
        Assert.Contains(symbols, s => s.Kind == "delegate" && s.Name == "PointerDelegate" && s.ReturnType == "int*");
    }

    [Fact]
    public void Extract_CSharp_DetectsExplicitInterfacePointerReturnTypes()
    {
        // Issue #234: explicit-interface implementations with pointer / function-pointer
        // return types must still be indexed, including nested generics inside the
        // function-pointer payload and `delegate* unmanaged[Cdecl]<...>` calling conventions.
        // Issue #234: explicit-interface 実装のポインタ / 関数ポインタ戻り値型も取りこぼさず、
        // function-pointer 内部に入れ子の generic がある場合や `delegate* unmanaged[Cdecl]<...>` でも動くこと。
        var content = """
            namespace Demo;

            public unsafe interface IFoo
            {
                int* Get();
                delegate*<int, int> Transform();
                delegate*<System.Collections.Generic.List<int>, int> TransformNested();
                delegate*<delegate*<int, void>, int> TransformFp();
                delegate* unmanaged[Cdecl]<System.Collections.Generic.List<int>, int> TransformUnmanaged();
                byte** Double();
                int*[] Arr();
            }

            public unsafe class Foo : IFoo
            {
                int* IFoo.Get() => null;
                delegate*<int, int> IFoo.Transform() => null;
                delegate*<System.Collections.Generic.List<int>, int> IFoo.TransformNested() => null;
                delegate*<delegate*<int, void>, int> IFoo.TransformFp() => null;
                delegate* unmanaged[Cdecl]<System.Collections.Generic.List<int>, int> IFoo.TransformUnmanaged() => null;
                byte** IFoo.Double() => null;
                int*[] IFoo.Arr() => null!;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Equal(2, symbols.Count(s => s.Kind == "function" && s.Name == "Get"));
        Assert.Equal(2, symbols.Count(s => s.Kind == "function" && s.Name == "Transform"));
        Assert.Equal(2, symbols.Count(s => s.Kind == "function" && s.Name == "TransformNested"));
        Assert.Equal(2, symbols.Count(s => s.Kind == "function" && s.Name == "TransformFp"));
        Assert.Equal(2, symbols.Count(s => s.Kind == "function" && s.Name == "TransformUnmanaged"));
        Assert.Equal(2, symbols.Count(s => s.Kind == "function" && s.Name == "Double"));
        Assert.Equal(2, symbols.Count(s => s.Kind == "function" && s.Name == "Arr"));

        var impls = symbols.Where(s => s.Kind == "function" && s.ContainerName == "Foo").ToList();
        Assert.Equal("int*", impls.Single(s => s.Name == "Get").ReturnType);
        Assert.Equal("delegate*<int, int>", impls.Single(s => s.Name == "Transform").ReturnType);
        Assert.Equal("delegate*<System.Collections.Generic.List<int>, int>", impls.Single(s => s.Name == "TransformNested").ReturnType);
        Assert.Equal("delegate*<delegate*<int, void>, int>", impls.Single(s => s.Name == "TransformFp").ReturnType);
        // Spaces inside `unmanaged[...]<...>` payload are collapsed by CollapseCSharpGenericTypeWhitespace
        // because the outer `<` has `]` as predecessor (a recognized generic-angle start); non-unmanaged
        // `delegate*<...>` keeps payload spaces because the outer `<` has `*` as predecessor.
        // `unmanaged[...]<...>` の payload 内のスペースは CollapseCSharpGenericTypeWhitespace で除去される
        // （outer `<` の直前が `]` で generic angle start と認識されるため）。通常の `delegate*<...>` は
        // outer `<` の直前が `*` で認識されないため payload 内スペースは保持される。
        Assert.Equal("delegate* unmanaged[Cdecl]<System.Collections.Generic.List<int>,int>", impls.Single(s => s.Name == "TransformUnmanaged").ReturnType);
        Assert.Equal("byte**", impls.Single(s => s.Name == "Double").ReturnType);
        Assert.Equal("int*[]", impls.Single(s => s.Name == "Arr").ReturnType);
    }

    [Fact]
    public void Extract_CSharp_DetectsPartialMethods()
    {
        // C# 9 extended partial methods / C# 9 拡張 partial メソッド
        var content = "public partial class App\n{\n    partial void OnInit();\n    public partial string GetName();\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "App");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "OnInit");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "GetName");
    }

    [Fact]
    public void Extract_CSharp_DetectsGenericMethodOverloads()
    {
        // Issue #41: generic method overloads should both be extracted as definitions
        // Issue #41: ジェネリックメソッドのオーバーロードは両方とも定義として抽出されるべき
        var content = "public class App\n{\n    private static void TryRaise(Action? handler, string context) { }\n    private static void TryRaise<T>(Action<T>? handler, T argument, string context) { }\n    public Task<List<T>> GetItems<T>(int page) { return null; }\n    public void Process<TKey, TValue>(Dictionary<TKey, TValue> map) { }\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "TryRaise" && s.Line == 3);
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "TryRaise" && s.Line == 4);
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "GetItems");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Process");
    }

    [Fact]
    public void Extract_CSharp_DetectsSpacedAndNestedGenericReturnTypeMethods()
    {
        var content = """
            public class App
            {
                public Task<Result<string, Error>> WithSpace() => null!;
                public Task<Result<string,Error>> NoSpace() => null!;
                public Dictionary<string, List<int>> Map() => new();
                public Tuple<int, int, int, int> Quad() => new(1, 2, 3, 4);
                public Func<int, int, int> Make() => null!;
                public Task<List<Tuple<int, int, int>>> Deep() => null!;
                public (int Left, string Right) Pair() => default;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var withSpace = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "WithSpace"));
        Assert.Equal("Task<Result<string,Error>>", withSpace.ReturnType);

        var noSpace = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "NoSpace"));
        Assert.Equal("Task<Result<string,Error>>", noSpace.ReturnType);

        var map = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "Map"));
        Assert.Equal("Dictionary<string,List<int>>", map.ReturnType);

        var quad = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "Quad"));
        Assert.Equal("Tuple<int,int,int,int>", quad.ReturnType);

        var make = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "Make"));
        Assert.Equal("Func<int,int,int>", make.ReturnType);

        var deep = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "Deep"));
        Assert.Equal("Task<List<Tuple<int,int,int>>>", deep.ReturnType);

        var pair = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "Pair"));
        Assert.Equal("(int Left, string Right)", pair.ReturnType);
    }

    [Fact]
    public void Extract_CSharp_DetectsSpacedGenericTypeMembersBeyondMethods()
    {
        var content = """
            public delegate Dictionary<string, int> Factory();

            public interface IFoo
            {
                Dictionary<string, int> Get();
            }

            public class Holder : IFoo
            {
                public Dictionary<string, int> Lookup { get; set; } = new();
                public event Action<string, int> OnLog;

                Dictionary<string, int> IFoo.Get() => Lookup;
                public Dictionary<string, int> this[int index] => Lookup;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "delegate" && s.Name == "Factory");
        Assert.Contains(symbols, s => s.Kind == "event" && s.Name == "OnLog" && s.ContainerName == "Holder");

        var lookup = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Lookup"));
        Assert.Equal("Dictionary<string,int>", lookup.ReturnType);
        Assert.Equal("Holder", lookup.ContainerName);

        var indexer = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "Item"));
        Assert.Equal("Dictionary<string,int>", indexer.ReturnType);
        Assert.Equal("Holder", indexer.ContainerName);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Get" && s.Line == 5 && s.ReturnType == "Dictionary<string,int>");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Get" && s.ContainerName == "Holder" && s.ReturnType == "Dictionary<string,int>");
    }

    [Fact]
    public void Extract_CSharp_DetectsFileScopedType()
    {
        // C# 11 file-scoped type / C# 11 のファイルスコープ型
        var content = "file class InternalHelper\n{\n    file static void DoWork() { }\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "InternalHelper");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "DoWork");
    }

    [Fact]
    public void Extract_CSharp_DetectsRefStruct()
    {
        var content = "public readonly ref struct Span2D<T> { }\nref struct StackBuffer { }\npublic readonly struct ImmutablePoint { }";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "Span2D");
        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "StackBuffer");
        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "ImmutablePoint");
    }

    [Fact]
    public void Extract_CSharp_DetectsRefReturnMethodsPropertiesAndIndexers()
    {
        var content = """
            public interface IRefBox
            {
                ref int GetRef();
                ref readonly int GetRefReadonly();
            }

            public class RefBox : IRefBox
            {
                private static int _value;
                private static readonly int[] _items = [1, 2, 3];

                public static ref int RefReturn(ref int[] arr, int i) => ref arr[i];
                public static ref readonly int RefReadonlyReturn(int[] arr, int i) => ref arr[i];
                public ref int PropRef => ref _value;
                public ref readonly int PropRefRo { get => ref _value; }
                public ref readonly int this[int index] => ref _items[index];
                ref int IRefBox.GetRef() => ref _value;
                ref readonly int IRefBox.GetRefReadonly() => ref _value;
            }

            public struct RefReadonlyMembers
            {
                private static int _value;

                public readonly ref readonly int PropReadonly => ref _value;
                public readonly ref readonly int this[int index] => ref _value;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "RefReturn" && s.ReturnType == "int");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "RefReadonlyReturn" && s.ReturnType == "int");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "PropRef" && s.ReturnType == "int");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "PropRefRo" && s.ReturnType == "int");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "PropReadonly" && s.ReturnType == "int");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Item" && s.ReturnType == "int");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Item" && s.Signature != null && s.Signature.Contains("public readonly ref readonly int this[int index]"));

        var explicitGetRef = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "GetRef" && s.ContainerName == "RefBox"));
        Assert.Equal("int", explicitGetRef.ReturnType);
        Assert.Contains("ref int IRefBox.GetRef()", explicitGetRef.Signature);

        var explicitGetRefReadonly = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "GetRefReadonly" && s.ContainerName == "RefBox"));
        Assert.Equal("int", explicitGetRefReadonly.ReturnType);
        Assert.Contains("ref readonly int IRefBox.GetRefReadonly()", explicitGetRefReadonly.Signature);
    }

    [Fact]
    public void Extract_CSharp_DetectsEnumMembers()
    {
        var content = "public enum Color\n{\n    Red,\n    Green = 1,\n    Blue = 2,\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Color");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Red");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Green");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Blue");
    }

    [Fact]
    public void Extract_CSharp_DetectsCompactAndZeroIndentEnumMembers()
    {
        var content = "namespace Demo;\n\npublic enum Compact { A, B = A }\npublic enum Flat\n{\nC,\nD = C\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var compactA = Assert.Single(symbols.Where(s => s.Kind == "enum" && s.Name == "A"));
        var compactB = Assert.Single(symbols.Where(s => s.Kind == "enum" && s.Name == "B"));

        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Compact");
        Assert.Equal("enum", compactA.ContainerKind);
        Assert.Equal("Compact", compactA.ContainerName);
        Assert.Equal("Demo.Compact", compactA.ContainerQualifiedName);
        Assert.Equal("enum", compactB.ContainerKind);
        Assert.Equal("Compact", compactB.ContainerName);
        Assert.Equal("Demo.Compact", compactB.ContainerQualifiedName);
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Flat");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "C");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "D");
    }

    [Fact]
    public void Extract_CSharp_DetectsSameLineSiblingEnums()
    {
        var content = "namespace Demo;\n\npublic enum InlineA { A1 } public enum InlineB { B1 }";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var a1 = Assert.Single(symbols.Where(s => s.Kind == "enum" && s.Name == "A1"));
        var b1 = Assert.Single(symbols.Where(s => s.Kind == "enum" && s.Name == "B1"));

        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "InlineA");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "InlineB");
        Assert.Equal("InlineA", a1.ContainerName);
        Assert.Equal("Demo.InlineA", a1.ContainerQualifiedName);
        Assert.Equal("InlineB", b1.ContainerName);
        Assert.Equal("Demo.InlineB", b1.ContainerQualifiedName);
    }

    [Fact]
    public void Extract_CSharp_DetectsCompactEnumMembersWithAttributesAndCastValues()
    {
        var content = "public enum Mode { [Obsolete] A = (int)B, [EnumMember(Value = \"b\")] B = (MyFlags)(A | C), C = 1 }";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Mode");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "A");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "B");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "C");
    }

    [Fact]
    public void Extract_CSharp_DetectsEnumMembersAcrossDirectiveLines()
    {
        var content = "public enum Mode\n{\n#if DEBUG\n    A,\n#endif\n#region values\n    B,\n#endregion\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var memberA = Assert.Single(symbols.Where(s => s.Kind == "enum" && s.Name == "A"));
        var memberB = Assert.Single(symbols.Where(s => s.Kind == "enum" && s.Name == "B"));

        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Mode");
        Assert.Equal("enum", memberA.ContainerKind);
        Assert.Equal("Mode", memberA.ContainerName);
        Assert.Equal("enum", memberB.ContainerKind);
        Assert.Equal("Mode", memberB.ContainerName);
    }

    [Fact]
    public void Extract_CSharp_TrimsClosingBraceFromFinalEnumMemberSpan()
    {
        var content = "public enum Status\n{\n    Ready,\n    Busy\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var busy = Assert.Single(symbols.Where(s => s.Kind == "enum" && s.Name == "Busy"));

        Assert.Equal("Busy", busy.Signature);
        Assert.Equal(4, busy.EndLine);
        Assert.DoesNotContain("}", busy.Signature);
    }

    [Fact]
    public void Extract_CSharp_DetectsLowercaseAndUnicodeEnumMembers()
    {
        var content = "public enum Status\n{\n    active,\n    inactive,\n    Δelta = active,\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Status");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "active");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "inactive");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Δelta");
    }

    [Fact]
    public void Extract_CSharp_DetectsEnumMembersWhenAttributeSharesDeclarationLine()
    {
        var content = "[Flags] public enum Mode\n{\n    A,\n    B = A,\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Mode");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "A");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "B");
    }

    [Fact]
    public void Extract_CSharp_DetectsEnumMembersWhenMemberAttributesShareLine()
    {
        var content = "public enum Mode\n{\n    [Obsolete] A,\n    [EnumMember(Value = \"a\")] B = A,\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Mode");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "A");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "B");
    }

    [Fact]
    public void Extract_CSharp_DoesNotTreatMultilineEnumMemberAttributeArgumentsAsMembers()
    {
        var content = "using System.Runtime.Serialization;\n\npublic enum Mode\n{\n    [EnumMember(\n        Value = Alias,\n        Other = 1)]\n    A,\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.DoesNotContain(symbols, s => s.Name == "Value");
        Assert.DoesNotContain(symbols, s => s.Name == "Other");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "A");
    }

    [Fact]
    public void Extract_CSharp_DetectsTabIndentedEnumMembers()
    {
        var content = "public enum Mode\n{\n\tA,\n\tB = A,\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "A");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "B");
    }

    [Fact]
    public void Extract_CSharp_RecoversAfterIncompleteEnumDeclarationAttribute()
    {
        var content = "[Attr(\npublic enum Mode\n{\n    A,\n}\n\npublic class After\n{\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Mode");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "A");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "After");
    }

    [Fact]
    public void Extract_CSharp_RecoversAfterIncompleteEnumMemberAttribute()
    {
        var content = "public enum Mode\n{\n    [Attr()\n    A,\n    B\n}\n\npublic class After\n{\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "A");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "B");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "After");
    }

    [Fact]
    public void Extract_CSharp_RecoversAfterIncompleteEnumMemberAttributeMissingParen()
    {
        var content = "public enum BrokenAttr\n{\n    [Attr(\n    X,\n    Y\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var memberY = Assert.Single(symbols.Where(s => s.Kind == "enum" && s.Name == "Y"));

        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "BrokenAttr");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "X");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Y");
        Assert.Equal("Y", memberY.Signature);
        Assert.Equal(5, memberY.EndLine);
        Assert.DoesNotContain("}", memberY.Signature);
    }

    [Fact]
    public void Extract_CSharp_RecoversModifierlessMembersAfterIncompleteAttribute()
    {
        var content = "public class C\n{\n    [Attr(\n    void M() {}\n    string Name { get; }\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "M");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Name");
    }

    [Fact]
    public void Extract_CSharp_RecoversBracketTypedMembersAfterIncompleteAttribute()
    {
        var content = "public class C\n{\n    [Attr(\n    int[] Values { get; }\n    int[] Build() => [];\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Values");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Build");
    }

    [Fact]
    public void Extract_CSharp_EnumMembersTrackOwningEnum()
    {
        var content = "namespace Demo;\n\npublic enum First\n{\n    None,\n}\n\npublic enum Second\n{\n    None,\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var noneMembers = symbols
            .Where(s => s.Kind == "enum" && s.Name == "None")
            .OrderBy(s => s.Line)
            .ToList();

        Assert.Equal(2, noneMembers.Count);
        Assert.Equal("enum", noneMembers[0].ContainerKind);
        Assert.Equal("First", noneMembers[0].ContainerName);
        Assert.Equal("Demo.First", noneMembers[0].ContainerQualifiedName);
        Assert.Equal("enum", noneMembers[1].ContainerKind);
        Assert.Equal("Second", noneMembers[1].ContainerName);
        Assert.Equal("Demo.Second", noneMembers[1].ContainerQualifiedName);
    }

    [Fact]
    public void Extract_CSharp_EnumMemberDoesNotMatchObjectInitializer()
    {
        // Object initializer lines should not be extracted as enum members
        // オブジェクト初期化子行は enum メンバーとして抽出されないこと
        var content = "var user = new User\n{\n    Name = \"Alice\",\n    Age = 30,\n    Email = GetEmail(),\n};";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.DoesNotContain(symbols, s => s.Name == "Name");
        Assert.DoesNotContain(symbols, s => s.Name == "Age");
        Assert.DoesNotContain(symbols, s => s.Name == "Email");
    }

    [Fact]
    public void Extract_CSharp_DetectsRegionDirectives()
    {
        var content = "#region Private Methods\nvoid Helper() { }\n#endregion\n\n#region Properties\npublic int X { get; set; }\n#endregion";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "Private Methods");
        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "Properties");
    }

    [Fact]
    public void Extract_CSharp_DetectsStaticConstructorAndFinalizer()
    {
        var content = "public class Cache\n{\n    static Cache()\n    {\n        // init\n    }\n\n    ~Cache()\n    {\n        // cleanup\n    }\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Cache");
        // Static constructor — extracted by the static ctor pattern
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Cache" && s.Line == 3);
        // Finalizer
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Cache" && s.Line == 8);
    }

    [Fact]
    public void Extract_CSharp_RangeIgnoresLiteralBracesInsideMethodBodies()
    {
        var content = """
            public class BraceExamples
            {
                public void First()
                {
                    var open = "{";
                    var close = '}';
                    var interpolated = $"{{";
                    var verbatim = @"{";
                }

                public void Second()
                {
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var first = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "First"));
        var second = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "Second"));
        Assert.Equal(3, first.Line);
        Assert.Equal(9, first.EndLine);
        Assert.Equal(11, second.Line);
        Assert.Equal(13, second.EndLine);
        Assert.Equal(2, symbols.Count(s => s.Kind == "function"));
    }

    [Fact]
    public void Extract_CSharp_DoesNotExtractSymbolsFromRawStringsVerbatimStringsOrComments()
    {
        var content = """"
            public class RealExample
            {
                public const string RawFixture = """
                    class Hidden
                    {
                        public void Fake()
                        {
                        }
                    }
                    """;

                public const string VerbatimFixture = @"class AlsoHidden
            {
                public void FakeVerbatim()
                {
                }
            }";

                /*
                class CommentHidden
                {
                    public void FakeComment()
                    {
                    }
                }
                */

                public void Keep()
                {
                }
            }
            """";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "RealExample");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Keep");
        Assert.DoesNotContain(symbols, s => s.Name == "Hidden");
        Assert.DoesNotContain(symbols, s => s.Name == "Fake");
        Assert.DoesNotContain(symbols, s => s.Name == "AlsoHidden");
        Assert.DoesNotContain(symbols, s => s.Name == "FakeVerbatim");
        Assert.DoesNotContain(symbols, s => s.Name == "CommentHidden");
        Assert.DoesNotContain(symbols, s => s.Name == "FakeComment");
        Assert.Equal(1, symbols.Count(s => s.Kind == "class"));
    }

    [Fact]
    public void Extract_CSharp_DetectsNullableReturnTypeMethods()
    {
        var content = "public static class GitHelper\n{\n    public static string? ResolveGitCommonDir(string projectRoot)\n    {\n        return null;\n    }\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var method = Assert.Single(symbols.Where(s => s.Name == "ResolveGitCommonDir"));
        Assert.Equal("function", method.Kind);
        Assert.Equal("string?", method.ReturnType);
    }

    [Fact]
    public void Extract_AssignsContainerAndRanges_ForNestedMembers()
    {
        var content = "public class UserService\n{\n    public async Task<User> GetUser(int id)\n    {\n        return default!;\n    }\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var classSymbol = Assert.Single(symbols.Where(s => s.Kind == "class"));
        var method = Assert.Single(symbols.Where(s => s.Kind == "function"));

        Assert.Equal(1, classSymbol.StartLine);
        Assert.Equal(7, classSymbol.EndLine);
        Assert.Equal(2, classSymbol.BodyStartLine);
        Assert.Equal(7, classSymbol.BodyEndLine);
        Assert.Equal("class", method.ContainerKind);
        Assert.Equal("UserService", method.ContainerName);
        Assert.Equal(3, method.StartLine);
        Assert.Equal(6, method.EndLine);
    }

    [Fact]
    public void Extract_Go_DetectsFunctions()
    {
        // Should detect both regular and method functions
        // 通常関数とメソッド関数の両方を検出する
        var content = "func NewHandler() *Handler {\n}\nfunc (h *Handler) ServeHTTP(w http.ResponseWriter, r *http.Request) {\n}";
        var symbols = SymbolExtractor.Extract(1, "go", content);

        Assert.Equal(2, symbols.Count);
        Assert.Contains(symbols, s => s.Name == "NewHandler");
        Assert.Contains(symbols, s => s.Name == "ServeHTTP");
    }

    [Fact]
    public void Extract_Shell_DetectsFunctions()
    {
        var content = "function setup() {\n  echo 'setup'\n}\n\ncleanup() {\n  echo 'cleanup'\n}";
        var symbols = SymbolExtractor.Extract(1, "shell", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "setup");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "cleanup");
    }

    [Fact]
    public void Extract_SQL_DetectsCreateStatements()
    {
        var content = "CREATE TEMP TABLE users (\n  id INT PRIMARY KEY\n);\n\nCREATE OR REPLACE FUNCTION get_user(id INT) RETURNS void;\n\nCREATE MATERIALIZED VIEW active_users AS SELECT * FROM users;\n\nCREATE TYPE color AS ENUM ('red', 'green');\nCREATE TYPE inventory_item AS (name text);\nCREATE SCHEMA analytics;\nCREATE SCHEMA AUTHORIZATION analytics_owner;\nCREATE SEQUENCE order_seq;\nCREATE EXTENSION IF NOT EXISTS pgcrypto;\nCREATE DOMAIN positive_int AS integer CHECK (VALUE > 0);\nCREATE UNIQUE INDEX users_email_idx ON users (id);\n\nALTER TABLE users ADD COLUMN email TEXT;";
        var symbols = SymbolExtractor.Extract(1, "sql", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "users");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "get_user");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "active_users");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "color");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "inventory_item");
        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "analytics");
        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "analytics_owner");
        Assert.DoesNotContain(symbols, s => s.Kind == "namespace" && s.Name == "AUTHORIZATION");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "order_seq");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "pgcrypto");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "positive_int");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "users_email_idx");
    }

    [Fact]
    public void Extract_SQL_DoesNotCaptureOnAsAnonymousIndexName()
    {
        var content = "CREATE INDEX ON users (email);\nCREATE INDEX IF NOT EXISTS users_name_idx ON users (name);";
        var symbols = SymbolExtractor.Extract(1, "sql", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "ON");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "users_name_idx");
    }

    [Fact]
    public void Extract_Terraform_DetectsResources()
    {
        var content = "resource \"aws_s3_bucket\" \"my_bucket\" {\n  bucket = \"my-bucket\"\n}\n\nvariable \"region\" {\n  default = \"us-east-1\"\n}\n\noutput \"bucket_arn\" {\n  value = aws_s3_bucket.my_bucket.arn\n}\n\nmodule \"vpc\" {\n  source = \"./modules/vpc\"\n}";
        var symbols = SymbolExtractor.Extract(1, "terraform", content);

        // resource captures logical name (second quoted token), not provider type
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "my_bucket");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "region");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "bucket_arn");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "vpc");
    }

    [Fact]
    public void Extract_PHP_DetectsExpandedFeatures()
    {
        var content = "<?php\nnamespace App\\Models;\n\nuse Illuminate\\Database\\Eloquent\\Model;\n\nreadonly class Config {\n    public const VERSION = '1.0';\n    public function getName(): string { return ''; }\n}\n\nenum Status: string {\n    case Active = 'active';\n}";
        var symbols = SymbolExtractor.Extract(1, "php", content);

        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name.Contains("App"));
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name.Contains("Model"));
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Config");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "VERSION");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "getName");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Status");
    }

    [Fact]
    public void Extract_Swift_DetectsActorAndTypealias()
    {
        var content = "public actor NetworkManager {\n    func fetch() { }\n}\n\npublic typealias Handler = (Data) -> Void\n\ndistributed actor RemoteWorker { }";
        var symbols = SymbolExtractor.Extract(1, "swift", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "NetworkManager");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "fetch");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Handler");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "RemoteWorker");
    }

    [Fact]
    public void Extract_Ruby_DetectsAttrAndRailsDSL()
    {
        var content = "class User < ActiveRecord::Base\n  attr_accessor :name\n  attr_reader :email\n  has_many :posts\n  belongs_to :company\n  scope :active\n\n  def initialize(name)\n    @name = name\n  end\nend";
        var symbols = SymbolExtractor.Extract(1, "ruby", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "User");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "name");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "email");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "posts");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "company");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "active");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "initialize");
    }

    [Fact]
    public void Extract_Rust_DetectsExpandedFeatures()
    {
        var content = "macro_rules! my_macro {\n    () => {};\n}\n\npub mod utils {\n}\n\nconst MAX_SIZE: usize = 1024;\nstatic COUNTER: AtomicU32 = AtomicU32::new(0);\npub const fn default_value() -> i32 { 42 }\npub unsafe fn raw_ptr() { }\ntype Result<T> = std::result::Result<T, Error>;\npub union MyUnion { f: f32 }";
        var symbols = SymbolExtractor.Extract(1, "rust", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "my_macro");
        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "utils");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "MAX_SIZE");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "COUNTER");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "default_value");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "raw_ptr");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Result");
        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "MyUnion");
    }

    [Fact]
    public void Extract_Go_DetectsTypeAliasAndConst()
    {
        var content = "type Handler struct {\n}\ntype ID = string\ntype Logger interface {\n}\n\nconst (\n    MaxRetries = 3\n    DefaultTimeout = 30\n)\n\nvar GlobalConfig Config";
        var symbols = SymbolExtractor.Extract(1, "go", content);

        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "Handler");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "ID");
        Assert.Contains(symbols, s => s.Kind == "interface" && s.Name == "Logger");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "MaxRetries");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "DefaultTimeout");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "GlobalConfig");
    }

    [Fact]
    public void Extract_Rust_DetectsFunctionsAndStructs()
    {
        var content = "pub fn handle_request() {}\npub struct Config {}\nimpl Config {";
        var symbols = SymbolExtractor.Extract(1, "rust", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "handle_request");
        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "Config");
    }

    [Fact]
    public void Extract_Rust_LifetimeAnnotationsDoNotBreakBraceRanges()
    {
        var content = """
            pub struct Holder<'a> {
                value: &'a str,
            }

            impl<'a> Holder<'a> {
                pub fn get(&self) -> &'a str {
                    self.value
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "rust", content);

        var holder = Assert.Single(symbols.Where(s => s.Kind == "struct" && s.Name == "Holder"));
        var get = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "get"));

        Assert.Equal(3, holder.EndLine);
        Assert.Equal(8, get.EndLine);
        Assert.Equal("class", get.ContainerKind);
        Assert.Equal("Holder", get.ContainerName);
    }

    [Fact]
    public void Extract_UnknownLang_ReturnsEmpty()
    {
        // Unsupported languages return no symbols
        // 未サポート言語は空を返す
        var symbols = SymbolExtractor.Extract(1, "markdown", "# Heading");
        Assert.Empty(symbols);
    }

    [Fact]
    public void Extract_NullLang_ReturnsEmpty()
    {
        var symbols = SymbolExtractor.Extract(1, null, "some content");
        Assert.Empty(symbols);
    }

    [Fact]
    public void Extract_LineNumbers_AreOneBased()
    {
        // Line numbers should be 1-based
        // 行番号は1始まりであること
        var content = "x = 1\ndef foo():\n    pass";
        var symbols = SymbolExtractor.Extract(1, "python", content);

        Assert.Single(symbols);
        Assert.Equal(2, symbols[0].Line);
    }

    [Fact]
    public void Extract_Java_DetectsClassesAndMethods()
    {
        // Java: class, interface, methods / Java: クラス、インターフェース、メソッド
        var content = "public class UserService {\n    public User getUser(int id) {\n    }\n}";
        var symbols = SymbolExtractor.Extract(1, "java", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "UserService");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "getUser");
    }

    [Fact]
    public void Extract_Java_DetectsRecordAndSealedClass()
    {
        // Java 16+ record, Java 17+ sealed class / Java 16 の record、Java 17 の sealed class
        var content = "public record Point(int x, int y) { }\npublic sealed class Shape permits Circle, Rect { }";
        var symbols = SymbolExtractor.Extract(1, "java", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Point");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Shape");
    }

    [Fact]
    public void Extract_Java_DetectsRecordPrimaryComponentsAsProperties()
    {
        var content = """
            package com.example;

            public record Point(int x, int y) {}
            public record Range(
                int low,
                int high
            ) {
                public int span() { return high - low; }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "java", content);

        var pointX = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "x" && s.ContainerName == "Point"));
        Assert.Equal("int", pointX.ReturnType);
        Assert.Equal("class", pointX.ContainerKind);
        Assert.Equal("Point", pointX.ContainerQualifiedName);
        Assert.Equal("public", pointX.Visibility);

        var rangeLow = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "low" && s.ContainerName == "Range"));
        Assert.Equal("int", rangeLow.ReturnType);
        Assert.Equal(5, rangeLow.Line);

        var rangeHigh = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "high" && s.ContainerName == "Range"));
        Assert.Equal("int", rangeHigh.ReturnType);
        Assert.Equal(6, rangeHigh.Line);

        var span = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "span"));
        Assert.Equal("class", span.ContainerKind);
        Assert.Equal("Range", span.ContainerName);
        Assert.Equal("Range", span.ContainerQualifiedName);
    }

    [Fact]
    public void Extract_Java_DetectsLongAndCommentedRecordPrimaryComponentsAsProperties()
    {
        var componentLines = string.Join('\n', Enumerable.Range(1, 30).Select(i => $"    int p{i},"));
        var content = $$"""
            package com.example;

            public record Big(
            {{componentLines}}
                int tail
            ) {}

            public record Point(
                int x /* separator, comment */,
                // the next component must still parse
                int y
            ) {}
            """;
        var symbols = SymbolExtractor.Extract(1, "java", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "p1" && s.ContainerName == "Big");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "p30" && s.ContainerName == "Big");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "tail" && s.ContainerName == "Big");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "x" && s.ContainerName == "Point");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "y" && s.ContainerName == "Point");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "separator" && s.ContainerName == "Point");

        var tail = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "tail" && s.ContainerName == "Big"));
        Assert.Equal(34, tail.Line);

        var pointY = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "y" && s.ContainerName == "Point"));
        Assert.Equal(40, pointY.Line);
    }

    [Fact]
    public void Extract_Java_DetectsRecordPrimaryComponentsWithSpacedGenericTypes()
    {
        var content = """
            import java.util.Map;

            public record Sample(Map <String, Integer> values, int count) {}
            """;
        var symbols = SymbolExtractor.Extract(1, "java", content);

        var values = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "values" && s.ContainerName == "Sample"));
        Assert.Equal("Map <String, Integer>", values.ReturnType);

        var count = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "count" && s.ContainerName == "Sample"));
        Assert.Equal("int", count.ReturnType);
    }

    [Fact]
    public void Extract_Java_TracksRecordPrimaryComponentLineAfterAnnotations()
    {
        var content = """
            public record Person(
                @Deprecated
                String name,
                @Deprecated
                int age
            ) {}
            """;
        var symbols = SymbolExtractor.Extract(1, "java", content);

        var name = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "name" && s.ContainerName == "Person"));
        Assert.Equal(3, name.Line);

        var age = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "age" && s.ContainerName == "Person"));
        Assert.Equal(5, age.Line);
    }

    [Fact]
    public void Extract_Java_RecordComponents_DoNotDisruptBodyMembers()
    {
        var content = """
            package repro;

            public record Point(int x) {
                public int doubled() { return x * 2; }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "java", content);

        var doubled = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "doubled"));
        Assert.Equal("class", doubled.ContainerKind);
        Assert.Equal("Point", doubled.ContainerName);
        Assert.Equal("Point", doubled.ContainerQualifiedName);
    }

    [Fact]
    public void Extract_Java_DetectsStaticFinalAndEnumMembers()
    {
        var content = "public class Config {\n    public static final String VERSION = \"1.0\";\n    private static final int MAX_RETRIES = 3;\n}\n\npublic enum Status {\n    ACTIVE,\n    INACTIVE,\n    PENDING;\n}";
        var symbols = SymbolExtractor.Extract(1, "java", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "VERSION");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "MAX_RETRIES");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Status");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "ACTIVE");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "INACTIVE");
    }

    [Fact]
    public void Extract_Java_DetectsAnnotationType()
    {
        var content = "public @interface MyAnnotation {\n    String value();\n}";
        var symbols = SymbolExtractor.Extract(1, "java", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "MyAnnotation");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "value");
    }

    [Fact]
    public void Extract_Java_DetectsFlexibleConstantOrder()
    {
        // final static order (reversed) and generic types with spaces
        var content = "public class Config {\n    private final static int MAX = 100;\n    public static final Map<String, Integer> COUNTS = Map.of();\n}";
        var symbols = SymbolExtractor.Extract(1, "java", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "MAX");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "COUNTS");
    }

    [Fact]
    public void Extract_Java_DetectsEnumMembersWithTwoSpaceIndent()
    {
        // 2-space indent enum / 2スペースインデントの enum
        var content = "public enum Color {\n  RED,\n  GREEN,\n  BLUE;\n}";
        var symbols = SymbolExtractor.Extract(1, "java", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "RED");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "GREEN");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "BLUE");
    }

    [Fact]
    public void Extract_Java_DetectsDefaultAndSynchronizedMethods()
    {
        var content = "public interface Service {\n    default void init() { }\n    static Service create() { return null; }\n}\npublic class Worker {\n    synchronized void process() { }\n}";
        var symbols = SymbolExtractor.Extract(1, "java", content);

        Assert.Contains(symbols, s => s.Kind == "interface" && s.Name == "Service");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "init");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "create");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "process");
    }

    [Fact]
    public void Extract_Kotlin_DetectsFunctionsAndClasses()
    {
        // Kotlin: class, fun / Kotlin: クラス、関数
        var content = "data class Config(val name: String)\nfun process(input: String): String {\n}";
        var symbols = SymbolExtractor.Extract(1, "kotlin", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Config");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "process");
    }

    [Fact]
    public void Extract_Kotlin_DetectsExpandedFeatures()
    {
        var content = "sealed interface Shape\nvalue class Email(val value: String)\ninner class Handler\n\ncompanion object {\n    const val MAX = 100\n}\n\nfun String.truncate(max: Int): String = take(max)\nsuspend fun fetchData(): List<Int> = emptyList()\ninline fun <reified T> parse(json: String): T = TODO()";
        var symbols = SymbolExtractor.Extract(1, "kotlin", content);

        Assert.Contains(symbols, s => s.Kind == "interface" && s.Name == "Shape");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Email");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Handler");
        // Companion object (unnamed) / コンパニオンオブジェクト（無名）
        Assert.Contains(symbols, s => s.Kind == "class" && s.Signature != null && s.Signature.Contains("companion object"));
        // Extension function / 拡張関数
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "truncate");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "fetchData");
        // const val / 定数プロパティ
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "MAX");
    }

    [Fact]
    public void Extract_Ruby_DetectsDefAndClass()
    {
        // Ruby: def, class, module / Ruby: メソッド、クラス、モジュール
        var content = "class UserService\n  def find_user(id)\n  end\nend";
        var symbols = SymbolExtractor.Extract(1, "ruby", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "UserService");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "find_user");
    }

    [Fact]
    public void Extract_PHP_DetectsFunctionsAndClasses()
    {
        // PHP: function, class, interface / PHP: 関数、クラス、インターフェース
        var content = "class AuthService {\n    public function login($user) {\n    }\n}";
        var symbols = SymbolExtractor.Extract(1, "php", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "AuthService");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "login");
    }

    [Fact]
    public void Extract_Swift_DetectsFuncAndStruct()
    {
        // Swift: func, class, struct / Swift: 関数、クラス、構造体
        var content = "struct Config {\n    func validate() -> Bool {\n        return true\n    }\n}";
        var symbols = SymbolExtractor.Extract(1, "swift", content);

        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "Config");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "validate");
    }

    [Fact]
    public void Extract_C_DetectsFunctionsAndStructs()
    {
        // C: functions, struct / C: 関数、構造体
        var content = "typedef struct Config {\n    int value;\n};\nint main(int argc) {\n}";
        var symbols = SymbolExtractor.Extract(1, "c", content);

        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "Config");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "main");
    }

    [Fact]
    public void Extract_Cpp_DetectsClassAndNamespace()
    {
        // C++: class, namespace, functions / C++: クラス、名前空間、関数
        var content = "namespace MyApp {\nclass Handler {\n    void process(int data) {\n    }\n};\n}";
        var symbols = SymbolExtractor.Extract(1, "cpp", content);

        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "MyApp");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Handler" && s.ContainerName == "MyApp");
    }

    [Fact]
    public void Extract_TypeScript_DetectsInterfaceAndEnum()
    {
        // TypeScript: interface, type, enum / TypeScript: インターフェース、型、列挙型
        var content = "export interface IUser {\n    name: string;\n}\nexport enum Status {\n    Active,\n}";
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "interface" && s.Name == "IUser");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Status");
    }

    [Fact]
    public void Extract_Haskell_DetectsTypeSignaturesAndDataTypes()
    {
        // Haskell: type signature, data, import / Haskell: 型シグネチャ、data型、import
        var content = "import Data.List\nimport qualified Data.Map as Map\n\ndata Tree a = Leaf | Node a (Tree a) (Tree a)\n\ninsert :: Ord a => a -> Tree a -> Tree a\ninsert x Leaf = Node x Leaf Leaf";
        var symbols = SymbolExtractor.Extract(1, "haskell", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Data.List");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Data.Map");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Tree");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "insert");
    }

    [Fact]
    public void Extract_FSharp_DetectsLetTypeModuleOpen()
    {
        // F#: let, type, module, open / F#: let束縛、型、モジュール、open
        var content = "module MyApp.Domain\n\nopen System\n\ntype User = { Name: string; Age: int }\n\nlet validate user =\n    user.Age > 0\n\nlet rec factorial n =\n    if n <= 1 then 1 else n * factorial (n - 1)";
        var symbols = SymbolExtractor.Extract(1, "fsharp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "MyApp.Domain");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "System");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "User");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "validate");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "factorial");
    }

    [Fact]
    public void Extract_FSharp_DoesNotMatchValueBindings()
    {
        // Value bindings should not be detected as functions / 値束縛は関数として検出されないこと
        var content = "let x = 5\nlet name = \"hello\"\nlet list = [1; 2; 3]";
        var symbols = SymbolExtractor.Extract(1, "fsharp", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "function");
    }

    [Fact]
    public void Extract_VB_DetectsCompoundVisibility()
    {
        // VB.NET compound visibility: Protected Friend / VB.NET 複合可視性
        var content = "Protected Friend Sub OnInit()\nEnd Sub\n\nPrivate Protected Function GetData() As String\n    Return \"\"\nEnd Function";
        var symbols = SymbolExtractor.Extract(1, "vb", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "OnInit");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "GetData");
    }

    [Fact]
    public void Extract_CSharp_DoesNotMatchFieldDeclarations()
    {
        // Fields should not be detected as properties / フィールドはプロパティとして検出されないこと
        var content = "public class Config\n{\n    public string Name;\n    private int _count;\n    public readonly string Id = \"x\";\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.DoesNotContain(symbols, s => s.Name == "Name" && s.Kind == "function");
        Assert.DoesNotContain(symbols, s => s.Name == "_count");
        Assert.DoesNotContain(symbols, s => s.Name == "Id" && s.Kind == "function");
    }

    [Fact]
    public void Extract_VB_DetectsSubFunctionClassModule()
    {
        // VB.NET: Sub, Function, Class, Module, Imports / VB.NET: サブ、関数、クラス、モジュール、Imports
        var content = "Imports System.IO\n\nPublic Class UserService\n    Public Sub Save(user As User)\n    End Sub\n\n    Private Function Validate(user As User) As Boolean\n        Return True\n    End Function\nEnd Class";
        var symbols = SymbolExtractor.Extract(1, "vb", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name.Contains("System.IO"));
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "UserService");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Save");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Validate");
    }

    [Fact]
    public void Extract_VB_DetectsNamespaceAndImplicitVisibilityDeclarations()
    {
        var content = """
            Imports System

            Namespace MyApp
                Public Class Foo
                    Sub Bar()
                    End Sub

                    Function Quux() As Integer
                        Return 1
                    End Function
                End Class

                Class Helper
                End Class

                Public Module Utils
                    Sub Log()
                    End Sub
                End Module
            End Namespace
            """;
        var symbols = SymbolExtractor.Extract(1, "vb", content);

        var ns = Assert.Single(symbols.Where(s => s.Kind == "namespace" && s.Name == "MyApp"));
        Assert.Equal(3, ns.StartLine);
        Assert.Equal(20, ns.EndLine);

        var foo = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Foo"));
        Assert.Equal("namespace", foo.ContainerKind);
        Assert.Equal("MyApp", foo.ContainerName);

        var helper = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Helper"));
        Assert.Equal("namespace", helper.ContainerKind);
        Assert.Equal("MyApp", helper.ContainerName);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Bar");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Quux");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Log");
    }

    [Fact]
    public void Extract_VB_DetectsLeadingModifiersAndMemberKindsWithoutVisibility()
    {
        var content = """
            Namespace MyApp
                Partial Class Form1
                End Class

                Public Class Widget
                    Partial Private Sub OnReady()
                    End Sub

                    Shared Sub Log()
                    End Sub

                    Overrides Function ToString() As String
                        Return ""
                    End Function

                    Property Count As Integer

                    Event Changed As EventHandler
                End Class
            End Namespace
            """;
        var symbols = SymbolExtractor.Extract(1, "vb", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Form1");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "OnReady");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Log");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "ToString");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Count");
        Assert.Contains(symbols, s => s.Kind == "event" && s.Name == "Changed");
    }

    [Fact]
    public void Extract_Haskell_DetectsIndentedAndLiterateSignatures()
    {
        // Indented where-clause signature and literate Haskell '>' prefix
        // インデントされたwhere節のシグネチャとliterate Haskellの'>'接頭辞
        var content = "  helper :: Int -> Int\n  helper x = x + 1\n> main :: IO ()\n> main = putStrLn \"hello\"";
        var symbols = SymbolExtractor.Extract(1, "haskell", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "helper");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "main");
    }

    [Fact]
    public void Extract_R_DoesNotMatchOrdinaryAssignment()
    {
        // Ordinary assignment should not be detected as a function
        // 通常の代入は関数として検出されないこと
        var content = "x <- 42\ny <- some_func(x)\nz <- list(1, 2, 3)";
        var symbols = SymbolExtractor.Extract(1, "r", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "function");
    }

    [Fact]
    public void Extract_R_DetectsFunctionAssignmentAndLibrary()
    {
        // R: function assignment, library / R: 関数代入、library
        var content = "library(ggplot2)\n\nmy_plot <- function(data, x, y) {\n  ggplot(data, aes(x, y))\n}";
        var symbols = SymbolExtractor.Extract(1, "r", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "ggplot2");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "my_plot");
    }

    [Fact]
    public void Extract_Lua_DetectsFunctionsAndRequire()
    {
        // Lua: function, local function, require / Lua: 関数、ローカル関数、require
        var content = "local http = require('socket.http')\n\nfunction greet(name)\n  print(name)\nend\n\nlocal function helper(x)\n  return x\nend";
        var symbols = SymbolExtractor.Extract(1, "lua", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "socket.http");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "greet");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "helper");
    }

    [Fact]
    public void Extract_Elixir_DetectsModuleAndFunctions()
    {
        // Elixir: defmodule, def, defp / Elixir: モジュール、関数、プライベート関数
        var content = "defmodule MyApp.Router do\n  def call(conn, _opts) do\n    conn\n  end\n\n  defp parse(data) do\n    data\n  end\nend";
        var symbols = SymbolExtractor.Extract(1, "elixir", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "MyApp.Router");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "call");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "parse");
    }

    [Fact]
    public void Extract_Scala_DetectsObjectTraitAndDef()
    {
        // Scala: object, trait, def, case class / Scala: オブジェクト、トレイト、def、ケースクラス
        var content = "object Main {\n  def run(): Unit = {\n  }\n}\nsealed trait Message\ncase class Ping(id: Int) extends Message";
        var symbols = SymbolExtractor.Extract(1, "scala", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Main");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run");
        Assert.Contains(symbols, s => s.Kind == "interface" && s.Name == "Message");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Ping");
    }

    [Fact]
    public void Extract_Dart_DetectsClassFunctionAndMixin()
    {
        // Dart: class, mixin, function / Dart: クラス、mixin、関数
        var content = "abstract class Widget {\n  void build(BuildContext ctx) {\n  }\n}\nmixin Logging on Widget {\n}";
        var symbols = SymbolExtractor.Extract(1, "dart", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Widget");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "build");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Logging");
    }

    [Fact]
    public void Extract_Dart_DetectsImportAndEnum()
    {
        // Dart: import, enum / Dart: インポート、列挙型
        var content = "import 'package:flutter/material.dart';\n\nenum Status {\n  active,\n  inactive,\n}";
        var symbols = SymbolExtractor.Extract(1, "dart", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name.Contains("flutter"));
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Status");
    }

    [Fact]
    public void Extract_Dart_DoesNotMatchExpressionLines()
    {
        // Expressions that look like "type name(" but are not function definitions
        // 関数定義に見えるが実際は式の行を誤検出しないことを検証
        var content = "void main() {\n  return foo(bar);\n  await task(x);\n  const Widget(key: k);\n  throw Error('oops');\n}";
        var symbols = SymbolExtractor.Extract(1, "dart", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "foo");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "task");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "Widget");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "Error");
        // main() should still be detected / main()は検出されるべき
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "main");
    }

    [Fact]
    public void Extract_GraphQL_DetectsSymbols()
    {
        var content = """
            type User {
              id: ID!
              name: String!
            }

            input CreateUserInput {
              name: String!
              email: String!
            }

            enum Role {
              ADMIN
              USER
            }

            query GetUser($id: ID!) {
              user(id: $id) { name }
            }

            mutation CreateUser($input: CreateUserInput!) {
              createUser(input: $input) { id }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "graphql", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "User");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "CreateUserInput");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Role");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "GetUser");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "CreateUser");
    }

    [Fact]
    public void Extract_Gradle_DetectsSymbols()
    {
        // Both legacy apply plugin: and modern plugins { id '...' } forms
        // レガシーの apply plugin: と新しい plugins { id '...' } の両形式
        var content = "apply plugin: 'java'\n\nplugins {\n  id 'org.springframework.boot'\n}\n\ntask build {\n  doLast { println 'Building' }\n}\n\ndef customTask {\n  println 'custom'\n}\n";
        var symbols = SymbolExtractor.Extract(1, "gradle", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "java");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "org.springframework.boot");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "build");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "customTask");
    }

    [Fact]
    public void Extract_CSharp_DoesNotMatchCallSitesAsDefinitions()
    {
        // Issue #40: await/return/throw calls should not be extracted as method definitions
        // Issue #40: await/return/throw の呼び出しがメソッド定義として抽出されないこと
        var content = "public class Service\n{\n    public async Task InitAsync()\n    {\n        await TryNotifyAsync(true);\n        return GetResult();\n        throw CreateException(\"err\");\n        var x = ComputeValue(42);\n        yield return GenerateItem();\n    }\n\n    private async Task TryNotifyAsync(bool force)\n    {\n    }\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        // Real definitions should be extracted / 実際の定義は抽出されるべき
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Service");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "InitAsync");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "TryNotifyAsync");
        // Call sites should NOT be extracted as definitions / 呼び出し箇所は定義として抽出されないこと
        Assert.DoesNotContain(symbols, s => s.Name == "TryNotifyAsync" && s.Line == 5);
        Assert.DoesNotContain(symbols, s => s.Name == "GetResult");
        Assert.DoesNotContain(symbols, s => s.Name == "CreateException");
        Assert.DoesNotContain(symbols, s => s.Name == "ComputeValue");
        Assert.DoesNotContain(symbols, s => s.Name == "GenerateItem");
    }

    [Fact]
    public void Extract_CSharp_DoesNotMatchQualifiedCallSitesAsDefinitions()
    {
        // Qualified call sites (obj.Method()) should not match the explicit interface pattern
        // 修飾付き呼び出し (obj.Method()) は明示的インターフェース実装パターンにマッチしないこと
        var content = "public class Service\n{\n    public async Task Run()\n    {\n        return service.GetResult();\n        await client.SendAsync();\n        throw factory.CreateException(\"err\");\n    }\n\n    void IDisposable.Dispose()\n    {\n    }\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        // Explicit interface impl should be extracted / 明示的インターフェース実装は抽出されるべき
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Dispose" && s.ReturnType == "void");
        // Qualified call sites should NOT be extracted / 修飾付き呼び出しは抽出されないこと
        Assert.DoesNotContain(symbols, s => s.Name == "GetResult");
        Assert.DoesNotContain(symbols, s => s.Name == "SendAsync");
        Assert.DoesNotContain(symbols, s => s.Name == "CreateException");
    }

    [Fact]
    public void Extract_CSharp_DoesNotMatchNamedArgumentFrameworkCallsAsDefinitions()
    {
        // Named-argument labels preceding qualified framework calls must not look like explicit interface impls.
        // 修飾付き framework call の前にある named-argument label を明示的インターフェース実装と誤認しないこと。
        var content = """
            public class PlatformState
            {
                public PlatformState(bool isWindows, bool isMacCatalyst)
                {
                }

                public static PlatformState Detect() =>
                    new(
                        isWindows: OperatingSystem.IsWindows(),
                        isMacCatalyst: OperatingSystem.IsMacCatalyst());
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "PlatformState");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Detect");
        Assert.DoesNotContain(symbols, s => s.Name == "IsWindows");
        Assert.DoesNotContain(symbols, s => s.Name == "IsMacCatalyst");
    }

    [Fact]
    public void Extract_CSharp_DetectsAliasQualifiedExplicitInterfaceImplementations()
    {
        // Alias-qualified return types should still match explicit interface implementations.
        // alias-qualified な戻り値型でも明示的インターフェース実装として抽出されること。
        var content = """
            public interface IFoo
            {
                string Name();
                object Create();
            }

            public class Impl : IFoo
            {
                global::System.String IFoo.Name() => "x";
                Alias::Type IFoo.Create() => default;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Name" && s.ReturnType == "global::System.String");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Create" && s.ReturnType == "Alias::Type");
    }

    [Fact]
    public void Extract_CSharp_DoesNotMatchTernaryContinuationCallsAsDefinitions()
    {
        var content = """
            public class Dispatcher
            {
                private string Select(bool isUpdate)
                    => isUpdate
                        ? RunUpdateMode()
                        : RunFullScan();

                private string RunUpdateMode() => "update";
                private string RunFullScan() => "full";

                public int? NullableCount() => null;

                public Dispatcher(bool isUpdate)
                    : base()
                {
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Select");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "RunUpdateMode" && s.Line == 8);
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "RunFullScan" && s.Line == 9);
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "NullableCount" && s.ReturnType == "int?");
        Assert.DoesNotContain(symbols, s => s.Name == "RunUpdateMode" && s.Line == 5);
        Assert.DoesNotContain(symbols, s => s.Name == "RunFullScan" && s.Line == 6);
        Assert.DoesNotContain(symbols, s => s.Name == "base");
    }

    [Fact]
    public void Extract_CSharp_DetectsNewModifierMethods()
    {
        // C# `new` modifier for member hiding should still be extracted as definitions
        // C# のメンバー隠蔽用 `new` 修飾子は定義として抽出されるべき
        var content = "public class Derived : Base\n{\n    new void Reset() { }\n    new int Compare(object obj) { return 0; }\n    public new string ToString() { return \"\"; }\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Reset");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Compare");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "ToString");
    }

    [Fact]
    public void Extract_Python_DetectsPropertyDecorator()
    {
        var content = "class User:\n    @property\n    def name(self):\n        return self._name\n\n    def greet(self):\n        print(self.name)";
        var symbols = SymbolExtractor.Extract(1, "python", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "User");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "name");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "greet");
    }

    [Theory]
    [InlineData("csharp", "public interface IFoo { }", "interface")]
    [InlineData("csharp", "public enum Color { }", "enum")]
    [InlineData("csharp", "public struct Point { }", "struct")]
    [InlineData("csharp", "public delegate void Handler();", "delegate")]
    [InlineData("csharp", "public event EventHandler Click;", "event")]
    [InlineData("csharp", "public string Name { get; set; }", "property")]
    [InlineData("java", "interface Foo { }", "interface")]
    [InlineData("java", "enum Color { RED }", "enum")]
    [InlineData("kotlin", "interface Foo", "interface")]
    [InlineData("kotlin", "enum class Color { RED }", "enum")]
    [InlineData("kotlin", "val name: String = \"\"", "property")]
    [InlineData("typescript", "export interface IFoo { }", "interface")]
    [InlineData("typescript", "export enum Status { A }", "enum")]
    [InlineData("go", "type Foo struct { }", "struct")]
    [InlineData("go", "type Foo interface { }", "interface")]
    [InlineData("rust", "pub struct Config { }", "struct")]
    [InlineData("rust", "pub enum Color { }", "enum")]
    [InlineData("rust", "pub trait Foo { }", "interface")]
    [InlineData("swift", "struct Config { }", "struct")]
    [InlineData("swift", "enum Color { }", "enum")]
    [InlineData("swift", "protocol Foo { }", "interface")]
    [InlineData("c", "struct Config { };", "struct")]
    [InlineData("c", "enum Color { RED };", "enum")]
    [InlineData("cpp", "struct Config { };", "struct")]
    [InlineData("cpp", "enum Color { RED };", "enum")]
    [InlineData("php", "interface Foo { }", "interface")]
    [InlineData("php", "enum Color { }", "enum")]
    [InlineData("scala", "trait Foo", "interface")]
    [InlineData("scala", "enum Color", "enum")]
    [InlineData("dart", "enum Status { active }", "enum")]
    [InlineData("graphql", "interface Node { }", "interface")]
    [InlineData("graphql", "enum Role { ADMIN }", "enum")]
    [InlineData("haskell", "class Functor f where", "interface")]
    [InlineData("elixir", "defprotocol Enumerable do\nend", "interface")]
    [InlineData("ruby", "  attr_accessor :name", "property")]
    [InlineData("python", "@property\ndef name(self):", "property")]
    public void Extract_CrossLanguage_GranularKindsAreConsistent(string lang, string content, string expectedKind)
    {
        var symbols = SymbolExtractor.Extract(1, lang, content);
        Assert.Contains(symbols, s => s.Kind == expectedKind);
    }

    [Fact]
    public void EstimateComplexity_StraightLineFunction_ReturnsOne()
    {
        var body = "var x = 1;\nreturn x + 2;";
        Assert.Equal(1, SymbolExtractor.EstimateComplexity(body));
    }

    [Fact]
    public void EstimateComplexity_BranchingFunction_CountsBranches()
    {
        var body = "if (x > 0)\n    return x;\nelse if (x < 0)\n    return -x;\nfor (int i = 0; i < n; i++)\n    sum += i;\nwhile (retry)\n    attempt();\nvar result = a ?? b;\nvar flag = x && y || z;";
        // 1 (baseline) + if + else if + for + while + ?? + && + || = 8
        Assert.Equal(8, SymbolExtractor.EstimateComplexity(body));
    }

    [Fact]
    public void EstimateComplexity_EmptyBody_ReturnsOne()
    {
        Assert.Equal(1, SymbolExtractor.EstimateComplexity(""));
        Assert.Equal(1, SymbolExtractor.EstimateComplexity("   "));
    }

    [Fact]
    public void Extract_CSS_DetectsSymbols()
    {
        var content = "@import 'reset.css';\n\n$primary-color: #333;\n\n@mixin flex-center {\n  display: flex;\n  align-items: center;\n}\n\n@keyframes fade-in {\n  from { opacity: 0; }\n  to { opacity: 1; }\n}\n\n.container {\n  max-width: 1200px;\n}\n\n#header {\n  background: $primary-color;\n}";
        var symbols = SymbolExtractor.Extract(1, "css", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name.Contains("reset.css"));
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "primary-color");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "flex-center");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "fade-in");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == ".container");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "#header");
    }

    [Fact]
    public void Extract_CSS_DetectsCustomPropertiesFontFacesAndSelectorVariants()
    {
        var content = """
            :root {
              --accent-color: #09f;
            }

            @font-face {
              font-family: "Block Font";
              src: url("block.woff2");
            }

            @FONT-FACE {
              font-family:
                "Split Font";
              src: url("split.woff2");
            }

            a:hover {
              color: red;
            }

            input[type="text"] {
              color: blue;
            }

            [hidden] {
              display: none;
            }

            .btn::before {
              content: "";
            }

            %button-base {
              padding: 4px;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "css", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == ":root");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "--accent-color");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Block Font");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Split Font");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "a:hover");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "input[type=\"text\"]");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "[hidden]");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == ".btn::before");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "%button-base");
    }

    [Fact]
    public void Extract_CSS_DetectsInlineFontFaceFamilyNames()
    {
        var content = """
            @font-face { font-family: "Inline Font"; src: url("inline.woff2"); }
            @font-face { src: url("same-line.woff2"); font-family: "Trailing Font"; unicode-range: U+0-5FF; }
            @font-face { src: url("valid-last.woff2"); font-family: "Last No Semicolon" }
            @font-face { font-family: /* keep */ "Comment Gap"; src: url("comment-gap.woff2"); }
            @font-face {
              src: url("commented.woff2");
              /* font-family: bogus; */
              font-family: "Commented Font";
            }
            @font-face {
              src: url("data:application/font-woff2;charset=utf-8;base64,font-family:bogus");
              font-family: "Real Font";
            }
            @font-face { src: url(data:text/plain;charset=utf-8;foo=1;font-family:bogus); font-family: Real Data Font; }
            @font-face { src: url(data:image/svg+xml,<svg>{}</svg>); font-family: Svg Data Font; }
            @font-face { src: url("no-family.woff2"); }
            @font-face {
              font-family:
                "Split Font";
              src: url("split.woff2");
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "css", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Inline Font");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Trailing Font");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Last No Semicolon");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Comment Gap");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Commented Font");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Real Font");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Real Data Font");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Svg Data Font");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Split Font");
        Assert.DoesNotContain(symbols, s => s.Name == "@font-face");
        Assert.DoesNotContain(symbols, s => s.Name == "bogus)");
    }

    [Fact]
    public void Extract_CSS_CapturesSelectorsInsideGroupingAtRulesButNotTrueNesting()
    {
        var content = """
            @media screen {
              .media-class {
                color: red;
              }
            }

            .parent {
              .nested-child {
                color: blue;
              }
            }

            @media screen { .inline-media { color: red; } }
            """;
        var symbols = SymbolExtractor.Extract(1, "css", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == ".media-class");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == ".inline-media");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == ".nested-child");
    }

    [Fact]
    public void Extract_CSS_DoesNotLeakNestedSelectorsAfterSameLineGroupingAndQualifiedRule()
    {
        var content = """
            @media screen { .outer {
              .inner { color: red; }
            } }
            """;
        var symbols = SymbolExtractor.Extract(1, "css", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == ".inner");
    }

    [Fact]
    public void Extract_CSS_TopLevelDetection_IgnoresScssLineCommentsAndEscapedQuotes()
    {
        var content = """
            // {
            .top-level { color: red; }

            .foo::before { content: "\""; }
            .bar { color: blue; }

            .parent { background-image: url(http://example.com/a.png); }
            .top-level:hover { color: green; }

            .parent2 { background-image: url(//cdn.example.com/app.css); }
            [data-theme="dark"] { color: white; }
            """;
        var symbols = SymbolExtractor.Extract(1, "css", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == ".top-level");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == ".foo::before");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == ".bar");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == ".top-level:hover");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "[data-theme=\"dark\"]");
    }

    [Fact]
    public void Extract_CSS_PreservesLiteralSelectorNames()
    {
        var content = """
            :root { --accent: #09f; }
            .root { color: red; }
            #root { color: blue; }
            """;
        var symbols = SymbolExtractor.Extract(1, "css", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == ":root");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "--accent");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == ".root");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "#root");
    }

    [Fact]
    public void Extract_PowerShell_DetectsSymbols()
    {
        var content = "Import-Module ActiveDirectory\nusing module PSDesiredStateConfiguration\n\nclass ServerConfig {\n    [string]$Name\n}\n\nenum Environment {\n    Dev\n    Staging\n    Prod\n}\n\nfunction Get-UserInfo {\n    param($UserId)\n    Get-ADUser -Identity $UserId\n}\n\nfilter Where-Active {\n    if ($_.Enabled) { $_ }\n}";
        var symbols = SymbolExtractor.Extract(1, "powershell", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "ActiveDirectory");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "PSDesiredStateConfiguration");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "ServerConfig");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Environment");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Get-UserInfo");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Where-Active");
    }

    [Fact]
    public void Extract_Zig_DetectsSymbols()
    {
        var content = "const std = @import(\"std\");\n\npub fn main() !void {\n    std.debug.print(\"hello\", .{});\n}\n\nfn helper(x: u32) u32 {\n    return x + 1;\n}\n\npub const Config = struct {\n    name: []const u8,\n};\n\nconst Direction = enum {\n    north,\n    south,\n};\n\ntest \"basic test\" {\n    try std.testing.expect(true);\n}";
        var symbols = SymbolExtractor.Extract(1, "zig", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "std");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "main" && s.Visibility == "pub");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "helper");
        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "Config" && s.Visibility == "pub");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Direction");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "basic test");
    }

    [Fact]
    public void Extract_Makefile_DetectsTargets()
    {
        var content = "all: build test\n\nbuild:\n\tgcc -o main main.c\n\ntest:\n\t./run_tests\n\nclean:\n\trm -f main\n";
        var symbols = SymbolExtractor.Extract(1, "makefile", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "all");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "build");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "test");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "clean");
    }

    [Fact]
    public void Extract_Dockerfile_DetectsStages()
    {
        var content = "FROM node:18 AS builder\nWORKDIR /app\nCOPY . .\nRUN npm build\n\nFROM alpine:3.18\nCOPY --from=builder /app/dist /app\n";
        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);

        // Named stages (AS builder) take priority over base image on the same line
        // 同一行では名前付きステージ(AS builder)がベースイメージより優先
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "builder");
        // Unnamed FROM lines produce base image class / 名前なしFROM行はベースイメージclassを生成
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "alpine:3.18");
        Assert.Equal(2, symbols.Count);
    }

    [Fact]
    public void Extract_Protobuf_DetectsSymbols()
    {
        var content = """
            syntax = "proto3";
            import "google/protobuf/timestamp.proto";

            message User {
              string name = 1;
              int32 age = 2;
            }

            enum Status {
              UNKNOWN = 0;
              ACTIVE = 1;
            }

            service UserService {
              rpc GetUser (GetUserRequest) returns (User);
              rpc ListUsers (ListUsersRequest) returns (ListUsersResponse);
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "protobuf", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "google/protobuf/timestamp.proto");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "User");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Status");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "UserService");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "GetUser");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "ListUsers");
    }
}
