using System.Diagnostics;
using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Text.Json;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public partial class SymbolExtractorTests
{
    [Fact]
    public void Extract_PythonDataclassField_IndexesFieldAndMetadataKeys()
    {
        const string content = """
            from dataclasses import dataclass, field

            @dataclass
            class Job:
                callback: Callable[[Payload], Result] = field(
                    default_factory=list,
                    metadata={"wire_name": "callback", "role": "handler"},
                )
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);

        Assert.Contains(symbols, symbol =>
            symbol.Kind == "property"
            && symbol.SubKind == "dataclass_field"
            && symbol.Name == "callback"
            && symbol.Line == 5);
        Assert.Contains(symbols, symbol =>
            symbol.Kind == "reference"
            && symbol.SubKind == "dataclass_field_metadata"
            && symbol.Name == "wire_name"
            && symbol.Line == 7);
        Assert.Contains(symbols, symbol =>
            symbol.Kind == "reference"
            && symbol.SubKind == "dataclass_field_metadata"
            && symbol.Name == "role"
            && symbol.Line == 7);
    }

    [Fact]
    public void Extract_Python_DetectsFunctions()
    {
        // Should detect both sync and async functions
        // 同期・非同期関数を検出する
        var content = "def authenticate(user):\n    pass\nasync def fetch_data():\n    pass";
        var symbols = SymbolExtractor.Extract(1, "python", content);

        Assert.Equal(2, symbols.Count);
        AssertSymbolsContain(symbols, "function", "authenticate", "fetch_data");
    }

    [Fact]
    public void Extract_Python_DetectsAssignedLambdaAsLambda()
    {
        var content = "transform = lambda value: value + 1";
        var symbols = SymbolExtractor.Extract(1, "python", content);

        var lambda = Assert.Single(symbols, s => s.Kind == "lambda");
        Assert.Equal("transform", lambda.Name);
        Assert.Equal(1, lambda.Line);
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
    public void Extract_Python_DetectsGenericFunctionsAndTypeAliases()
    {
        var content = """
            type Vector = list[float]
            type Connection = str | int
            JsonValue: TypeAlias = dict[str, object]
            Handler: typing.TypeAlias = Callable[..., None]
            UserId = NewType("UserId", int)
            OrderId = typing.NewType("OrderId", int)
            T = TypeVar("T")
            P = typing.ParamSpec("P")
            Ts = typing_extensions.TypeVarTuple("Ts")
            Point = NamedTuple("Point", [("x", int), ("y", int)])
            Coordinate = collections.namedtuple("Coordinate", "lat lon")
            DynamicUser = make_dataclass("DynamicUser", [("name", str)])
            DynamicOrder = dataclasses.make_dataclass("DynamicOrder", [("id", int)])
            UserPayload = TypedDict("UserPayload", {"name": str})
            OrderPayload = typing.TypedDict("OrderPayload", {"id": int})
            Color = Enum("Color", "RED BLUE")
            Status = enum.Enum("Status", "OPEN CLOSED")
            ErrorCode = IntEnum("ErrorCode", "NOT_FOUND INVALID")
            Permission = enum.IntFlag("Permission", "READ WRITE")
            RuntimeUser = create_model("RuntimeUser", name=(str, ...))
            RuntimeOrder = pydantic.create_model("RuntimeOrder", id=(int, ...))
            DEFAULT_TIMEOUT: Final[int] = 30
            API_HOST: typing.Final = "example.invalid"

            def first[T](items: list[T]) -> T:
                return items[0]

            async def fetch_all[T](items: list[T]) -> list[T]:
                return items

            class Stack[T]:
                def push(self, value: T) -> None:
                    pass

            class Config:
                type Theme = str
                type = 5
                type(x)
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "first");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "fetch_all");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Stack");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Config");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Vector");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Connection");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "JsonValue");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Handler");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "UserId");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "OrderId");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "T");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "P");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Ts");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Point");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Coordinate");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "DynamicUser");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "DynamicOrder");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "UserPayload");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "OrderPayload");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Color");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Status");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "ErrorCode");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Permission");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "RuntimeUser");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "RuntimeOrder");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "DEFAULT_TIMEOUT");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "API_HOST");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Theme" && s.ContainerName == "Config");
        Assert.DoesNotContain(symbols, s => s.Name == "type");
    }

    [Fact]
    public void Extract_Python_ClassPropertyDeclarations_ReuseAssignmentAndMetadataFixture()
    {
        var content = """
            class AnnotatedUser:
                name: str
                age: int = 0

                def hydrate(self) -> None:
                    annotated_local: str = "ignored"

            class Settings:
                DEFAULT_TIMEOUT = 30
                endpoint = "https://example.invalid"

                def configure(self) -> None:
                    assigned_local = 1

            class SlottedUser:
                __slots__ = (
                    "slot_name",
                    "slot_age",
                )

            class AugmentedSlots:
                __slots__ = ("initial_slot",)
                __slots__ += ("extra_slot",)

            class Point:
                __match_args__ = ("x", "y")

            class DictionaryUser:
                __annotations__ = {
                    "dictionary_name": str,
                    "dictionary_age": int,
                }
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);

        AssertProperty("name", "AnnotatedUser");
        AssertProperty("age", "AnnotatedUser");
        AssertProperty("DEFAULT_TIMEOUT", "Settings");
        AssertProperty("endpoint", "Settings");
        AssertProperty("slot_name", "SlottedUser");
        AssertProperty("slot_age", "SlottedUser");
        AssertProperty("initial_slot", "AugmentedSlots");
        AssertProperty("extra_slot", "AugmentedSlots");
        AssertProperty("x", "Point");
        AssertProperty("y", "Point");
        AssertProperty("dictionary_name", "DictionaryUser");
        AssertProperty("dictionary_age", "DictionaryUser");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name is
            "annotated_local" or "assigned_local" or "__slots__" or "__match_args__" or "__annotations__");

        void AssertProperty(string name, string containerName) =>
            Assert.Contains(symbols, s =>
                s.Kind == "property" && s.Name == name && s.ContainerName == containerName);
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
    public void Extract_Python_PropertyDecoratorFamilies_ReuseSingleFixture()
    {
        var content = """
            import abc
            import functools
            from abc import abstractproperty
            from functools import cached_property

            class Metrics:
                @cached_property
                def total(self) -> int:
                    return 1

                @functools.cached_property
                def count(self) -> int:
                    return 2

            class User:
                @property
                def name(self) -> str:
                    return self._name

                @name.setter
                def name(self, value: str) -> None:
                    self._name = value

                @name.deleter
                def name(self) -> None:
                    del self._name

            class Base:
                @abstractproperty
                def abstract_name(self) -> str:
                    raise NotImplementedError

                @abc.abstractproperty
                def abstract_value(self) -> int:
                    raise NotImplementedError
            """;
        var symbols = SymbolExtractor.Extract(1, "python", content);

        foreach (var propertyName in new[] { "total", "count", "abstract_name", "abstract_value" })
        {
            Assert.Contains(symbols, s => s.Kind == "property" && s.Name == propertyName);
            Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == propertyName);
        }

        Assert.Equal(3, symbols.Count(s => s.Kind == "property" && s.Name == "name"));
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "name" && s.SubKind == "setter");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "name" && s.SubKind == "deleter");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "name");
    }

    [Fact]
    public void Extract_Python_DetectsClassHooksAndWalrusAssignments()
    {
        var content = """
            class Base:
                def __init_subclass__(cls) -> None:
                    pass

                def __class_getitem__(cls, item):
                    return cls

            values = [captured := item for item in range(3)]

            def read(stream):
                while chunk := stream.read(8192):
                    pass
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);

        Assert.Contains(symbols, s => s.Kind == "class_hook" && s.Name == "__init_subclass__");
        Assert.Contains(symbols, s => s.Kind == "class_hook" && s.Name == "__class_getitem__");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "captured" && s.SubKind == "walrus");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "chunk" && s.SubKind == "walrus");
    }

    [Fact]
    public void Extract_Python_StoresMultilineFunctionAndClassHeaders()
    {
        var content = """
            def build_result[
                T,
            ](
                value: T,
                fallback: list[T],
            ) -> Result[T]:
                return Result(value)

            class Repository(
                BaseRepository,
                Generic[T],
            ):
                pass
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);

        Assert.Contains(symbols, s =>
            s.Kind == "function"
            && s.Name == "build_result"
            && s.Signature != null
            && s.Signature.Contains("fallback: list[T]", StringComparison.Ordinal)
            && s.Signature.Contains("-> Result[T]", StringComparison.Ordinal));
        Assert.Contains(symbols, s =>
            s.Kind == "class"
            && s.Name == "Repository"
            && s.Signature != null
            && s.Signature.Contains("BaseRepository", StringComparison.Ordinal)
            && s.Signature.Contains("Generic[T]", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_Python_StaticImports_ReuseAliasQualifiedAndDottedFixture()
    {
        var content = """
            import numpy as np
            from  collections   import  defaultdict, OrderedDict as OD
            from itertools import (
                chain,
                zip_longest as zipl,
            )
            from .helpers import build as build_helper

            from package import submodule as alias
            from .helpers import build

            import dotted.module as dotted_alias
            from package.subpackage import helper
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var imports = symbols.Where(symbol => symbol.Kind == "import")
            .Select(symbol => symbol.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var importName in new[]
                 {
                     "numpy", "np", "collections", "defaultdict", "OrderedDict", "OD",
                     "itertools", "chain", "zip_longest", "zipl", "helpers", "helpers.build",
                     "build", "build_helper", "package", "package.submodule", "submodule", "alias",
                     "dotted", "dotted.module", "dotted_alias", "package.subpackage", "helper",
                 })
        {
            Assert.Contains(importName, imports);
        }
    }

    [Fact]
    public void Extract_Python_IndexesDynamicImportLiteralModules()
    {
        var content = """
            importlib.import_module("plugins.alpha")
            loaded = importlib.import_module("plugins.beta")
            __import__('legacy.loader')
            importlib.util.find_spec("optional.backend")
            importlib.import_module(module_name)
            note = "importlib.import_module('not.real')"
            # importlib.import_module("commented.out")
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var imports = symbols.Where(symbol => symbol.Kind == "import").Select(symbol => symbol.Name).ToList();

        Assert.Contains("plugins.alpha", imports);
        Assert.Contains("plugins.beta", imports);
        Assert.Contains("legacy.loader", imports);
        Assert.Contains("optional.backend", imports);
        Assert.DoesNotContain("module_name", imports);
        Assert.DoesNotContain("not.real", imports);
        Assert.DoesNotContain("commented.out", imports);
    }

    [Fact]
    public void Extract_Python_IndexesAllExportsFromInitModules()
    {
        var content = """
            __all__ = [
                "public_api",
                "secondary_api",
            ]
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var exports = symbols.Where(symbol => symbol.Kind == "import").ToList();

        Assert.Contains(exports, symbol => symbol.Name == "public_api");
        Assert.Contains(exports, symbol => symbol.Name == "secondary_api");
    }

    [Fact]
    public void Extract_Python_InitAllMutations_ReuseAssignmentAppendAndExtendFixture()
    {
        var content = """
            __all__ = [
                "submodule",
                "subpackage.tools",
            ]
            __all__.append("dynamic_api")
            __all__.extend([
                "first_api",
                "second_api",
            ])
            __all__.extend(
                [
                    "split_api",
                ]
            )
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content, "package/subpkg/__init__.py");
        var exports = symbols.Where(symbol => symbol.Kind == "import").Select(symbol => symbol.Name).ToList();

        foreach (var exportName in new[]
                 { "submodule", "subpackage.tools", "dynamic_api", "first_api", "second_api", "split_api" })
        {
            Assert.Contains(exportName, exports);
            Assert.Contains($"package.subpkg.{exportName}", exports);
        }
    }

    [Fact]
    public void Extract_Python_IndexesQualifiedModuleAliasesFromInitModules()
    {
        var content = """
            import submodule as module_alias
            import package.submodule as external_alias
            from . import helper as alias
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content, "package/subpkg/__init__.py");
        var imports = symbols.Where(symbol => symbol.Kind == "import").Select(symbol => symbol.Name).ToList();

        Assert.Contains("submodule", imports);
        Assert.Contains("package.subpkg.submodule", imports);
        Assert.Contains("module_alias", imports);
        Assert.Contains("package.subpkg.module_alias", imports);
        Assert.Contains("package.submodule", imports);
        Assert.DoesNotContain("package.subpkg.package.submodule", imports);
        Assert.Contains("external_alias", imports);
        Assert.Contains("package.subpkg.external_alias", imports);
        Assert.Contains("helper", imports);
        Assert.Contains("alias", imports);
        Assert.Contains("package.subpkg.alias", imports);
    }

    [Fact]
    public void Extract_Python_IndexesCurrentPackageRelativeFromImports()
    {
        var content = """
            from . import helper
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content, "package/subpkg/__init__.py");
        var imports = symbols.Where(symbol => symbol.Kind == "import").Select(symbol => symbol.Name).ToList();

        Assert.Contains("helper", imports);
        Assert.Contains("package.subpkg.helper", imports);
    }

    [Fact]
    public void Extract_Python_IndexesCurrentPackageRelativeModuleImports()
    {
        var content = """
            from .tools import build
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content, "package/subpkg/__init__.py");
        var imports = symbols.Where(symbol => symbol.Kind == "import").Select(symbol => symbol.Name).ToList();

        Assert.Contains("tools.build", imports);
        Assert.Contains("package.subpkg.tools", imports);
        Assert.Contains("package.subpkg.tools.build", imports);
    }

    [Fact]
    public void Extract_Python_IndexesParentPackageRelativeModuleImports()
    {
        var content = """
            from ..shared import helper
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content, "package/subpkg/__init__.py");
        var imports = symbols.Where(symbol => symbol.Kind == "import").Select(symbol => symbol.Name).ToList();

        Assert.Contains("shared.helper", imports);
        Assert.Contains("package.shared.helper", imports);
    }

    [Fact]
    public void Extract_Python_HandlesUnclosedMultilineImportBlocksWithoutPhantomSymbols()
    {
        var content = """
            from itertools import (
                chain,
                zip_longest as zipl,
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var imports = symbols.Where(symbol => symbol.Kind == "import").ToList();

        Assert.Contains(imports, symbol => symbol.Name == "itertools");
        Assert.Contains(imports, symbol => symbol.Name == "chain");
        Assert.Contains(imports, symbol => symbol.Name == "zip_longest");
        Assert.Contains(imports, symbol => symbol.Name == "zipl");
        Assert.DoesNotContain(imports, symbol => symbol.Name == "(");
    }

    [Fact]
    public void Extract_Python_StopsAtUnclosedMultilineImportBlocksBeforeUnrelatedCode()
    {
        var content = """
            from itertools import (
                chain,
                zip_longest as zipl,

            value = 1
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var imports = symbols.Where(symbol => symbol.Kind == "import").ToList();

        Assert.Contains(imports, symbol => symbol.Name == "itertools");
        Assert.Contains(imports, symbol => symbol.Name == "chain");
        Assert.Contains(imports, symbol => symbol.Name == "zip_longest");
        Assert.Contains(imports, symbol => symbol.Name == "zipl");
        Assert.DoesNotContain(imports, symbol => symbol.Name == "value = 1");
    }

    [Fact]
    public void Extract_Python_DetectsModuleDocstringHeading()
    {
        var content = "\"\"\"Payments API helpers.\"\"\"\n\n"
            + "def charge():\n"
            + "    pass\n";
        var symbols = SymbolExtractor.Extract(1, "python", content);

        Assert.Contains(symbols, s => s.Kind == "heading" && s.Name == "Payments API helpers.");
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

    [Fact]
    public void Extract_PythonTripleQuotedString_DoesNotLeakPhantomSymbols()
    {
        // Regression for issue #291: code-shaped fixture text inside """...""" /
        // '''...''' / r"""...""" must not produce phantom class/function rows.
        // issue #291 回帰: """...""" / '''...''' / r"""...""" 内のコード風のフィクスチャ
        // テキストは、phantom の class/function を生成してはならない。
        const string content = """"
            FIXTURE_DOUBLE = """
            class FakeDouble:
                def method_in_double(self): pass
            """

            FIXTURE_SINGLE = '''
            class FakeSingle:
                def method_in_single(self): pass
            '''

            FIXTURE_RAW = r"""
            def raw_fake():
                pass
            """

            class RealClass:
                def real_method(self):
                    pass
            """";

        var symbols = SymbolExtractor.Extract(1, "python", content);

        Assert.DoesNotContain(symbols, s => s.Name == "FakeDouble");
        Assert.DoesNotContain(symbols, s => s.Name == "FakeSingle");
        Assert.DoesNotContain(symbols, s => s.Name == "method_in_double");
        Assert.DoesNotContain(symbols, s => s.Name == "method_in_single");
        Assert.DoesNotContain(symbols, s => s.Name == "raw_fake");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "RealClass");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "real_method");
    }

    [Fact]
    public void Extract_Python_LeadingBom_IndexesFirstLineDef()
    {
        // BOM-prefixed Python: `def at_start():` on line 1 must still be captured.
        // Closes #183.
        // BOM 付き Python: 1 行目の `def at_start():` も取りこぼさない。Closes #183.
        const string content = "\uFEFFdef at_start():\n    pass\n";

        var symbols = SymbolExtractor.Extract(1, "python", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "at_start" && s.Line == 1);
    }
}
