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
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "authenticate");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "fetch_data");
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
    public void Extract_Python_DetectsAnnotatedClassAttributesAsProperties()
    {
        var content = """
            class User:
                name: str
                age: int = 0

                def hydrate(self) -> None:
                    local_value: str = "ignored"
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "name" && s.ContainerName == "User");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "age" && s.ContainerName == "User");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "local_value");
    }

    [Fact]
    public void Extract_Python_DetectsAssignedClassAttributesAsProperties()
    {
        var content = """
            class Settings:
                DEFAULT_TIMEOUT = 30
                endpoint = "https://example.invalid"

                def configure(self) -> None:
                    local_value = 1
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "DEFAULT_TIMEOUT" && s.ContainerName == "Settings");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "endpoint" && s.ContainerName == "Settings");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "local_value");
    }

    [Fact]
    public void Extract_Python_ExpandsSlotsAsClassProperties()
    {
        var content = """
            class User:
                __slots__ = (
                    "name",
                    "age",
                )
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "name" && s.ContainerName == "User");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "age" && s.ContainerName == "User");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "__slots__");
    }

    [Fact]
    public void Extract_Python_ExpandsAugmentedSlotsAsClassProperties()
    {
        var content = """
            class User:
                __slots__ = ("name",)
                __slots__ += ("email",)
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "name" && s.ContainerName == "User");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "email" && s.ContainerName == "User");
    }

    [Fact]
    public void Extract_Python_ExpandsMatchArgsAsClassProperties()
    {
        var content = """
            class Point:
                __match_args__ = ("x", "y")
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "x" && s.ContainerName == "Point");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "y" && s.ContainerName == "Point");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "__match_args__");
    }

    [Fact]
    public void Extract_Python_ExpandsAnnotationsDictionaryAsClassProperties()
    {
        var content = """
            class User:
                __annotations__ = {
                    "name": str,
                    "age": int,
                }
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "name" && s.ContainerName == "User");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "age" && s.ContainerName == "User");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "__annotations__");
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
    public void Extract_Python_CachedPropertyDecoratorsAreProperties()
    {
        var content = """
            import functools
            from functools import cached_property

            class Metrics:
                @cached_property
                def total(self) -> int:
                    return 1

                @functools.cached_property
                def count(self) -> int:
                    return 2
            """;
        var symbols = SymbolExtractor.Extract(1, "python", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "total");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "count");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "total");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "count");
    }

    [Fact]
    public void Extract_Python_PropertyAccessorDecoratorsAreProperties()
    {
        var content = """
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
            """;
        var symbols = SymbolExtractor.Extract(1, "python", content);

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
    public void Extract_Python_AbstractPropertyDecoratorsAreProperties()
    {
        var content = """
            import abc
            from abc import abstractproperty

            class Base:
                @abstractproperty
                def name(self) -> str:
                    raise NotImplementedError

                @abc.abstractproperty
                def value(self) -> int:
                    raise NotImplementedError
            """;
        var symbols = SymbolExtractor.Extract(1, "python", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "name");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "value");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "name");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "value");
    }

    [Fact]
    public void Extract_Python_ExpandsImportAliasesAndImportedNames()
    {
        var content = """
            import numpy as np
            from  collections   import  defaultdict, OrderedDict as OD
            from itertools import (
                chain,
                zip_longest as zipl,
            )
            from .helpers import build as build_helper
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var imports = symbols.Where(symbol => symbol.Kind == "import").ToList();

        Assert.Contains(imports, symbol => symbol.Name == "numpy");
        Assert.Contains(imports, symbol => symbol.Name == "np");
        Assert.Contains(imports, symbol => symbol.Name == "collections");
        Assert.Contains(imports, symbol => symbol.Name == "defaultdict");
        Assert.Contains(imports, symbol => symbol.Name == "OrderedDict");
        Assert.Contains(imports, symbol => symbol.Name == "OD");
        Assert.Contains(imports, symbol => symbol.Name == "itertools");
        Assert.Contains(imports, symbol => symbol.Name == "chain");
        Assert.Contains(imports, symbol => symbol.Name == "zip_longest");
        Assert.Contains(imports, symbol => symbol.Name == "zipl");
        Assert.Contains(imports, symbol => symbol.Name == "helpers");
        Assert.Contains(imports, symbol => symbol.Name == "build");
        Assert.Contains(imports, symbol => symbol.Name == "build_helper");
    }

    [Fact]
    public void Extract_Python_ExpandsFromImportQualifiedNamesForSearchability()
    {
        var content = """
            from package import submodule as alias
            from .helpers import build
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var imports = symbols.Where(symbol => symbol.Kind == "import").ToList();

        Assert.Contains(imports, symbol => symbol.Name == "package");
        Assert.Contains(imports, symbol => symbol.Name == "package.submodule");
        Assert.Contains(imports, symbol => symbol.Name == "submodule");
        Assert.Contains(imports, symbol => symbol.Name == "alias");
        Assert.Contains(imports, symbol => symbol.Name == "helpers");
        Assert.Contains(imports, symbol => symbol.Name == "helpers.build");
        Assert.Contains(imports, symbol => symbol.Name == "build");
    }

    [Fact]
    public void Extract_Python_ExpandsDottedImportPrefixesForSearchability()
    {
        var content = """
            import package.submodule as alias
            from package.subpackage import helper
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var imports = symbols.Where(symbol => symbol.Kind == "import").ToList();

        Assert.Contains(imports, symbol => symbol.Name == "package");
        Assert.Contains(imports, symbol => symbol.Name == "package.submodule");
        Assert.Contains(imports, symbol => symbol.Name == "package.subpackage");
        Assert.Contains(imports, symbol => symbol.Name == "alias");
        Assert.Contains(imports, symbol => symbol.Name == "helper");
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
    public void Extract_Python_IndexesQualifiedExportsFromInitModules()
    {
        var content = """
            __all__ = [
                "submodule",
                "subpackage.tools",
            ]
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content, "package/subpkg/__init__.py");
        var exports = symbols.Where(symbol => symbol.Kind == "import").Select(symbol => symbol.Name).ToList();

        Assert.Contains("submodule", exports);
        Assert.Contains("package.subpkg.submodule", exports);
        Assert.Contains("subpackage.tools", exports);
        Assert.Contains("package.subpkg.subpackage.tools", exports);
    }

    [Fact]
    public void Extract_Python_IndexesAllAppendExportsFromInitModules()
    {
        var content = """
            __all__ = []
            __all__.append("dynamic_api")
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content, "package/subpkg/__init__.py");
        var exports = symbols.Where(symbol => symbol.Kind == "import").Select(symbol => symbol.Name).ToList();

        Assert.Contains("dynamic_api", exports);
        Assert.Contains("package.subpkg.dynamic_api", exports);
    }

    [Fact]
    public void Extract_Python_IndexesAllExtendExportsFromInitModules()
    {
        var content = """
            __all__ = []
            __all__.extend([
                "first_api",
                "second_api",
            ])
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content, "package/subpkg/__init__.py");
        var exports = symbols.Where(symbol => symbol.Kind == "import").Select(symbol => symbol.Name).ToList();

        Assert.Contains("first_api", exports);
        Assert.Contains("package.subpkg.first_api", exports);
        Assert.Contains("second_api", exports);
        Assert.Contains("package.subpkg.second_api", exports);
    }

    [Fact]
    public void Extract_Python_IndexesAllExtendExportsWhenValuesStartOnNextLine()
    {
        var content = """
            __all__ = []
            __all__.extend(
                [
                    "split_api",
                ]
            )
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content, "package/subpkg/__init__.py");
        var exports = symbols.Where(symbol => symbol.Kind == "import").Select(symbol => symbol.Name).ToList();

        Assert.Contains("split_api", exports);
        Assert.Contains("package.subpkg.split_api", exports);
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
