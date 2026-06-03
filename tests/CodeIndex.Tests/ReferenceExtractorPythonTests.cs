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
    public void Extract_PythonMutualCalls_StampsBothCycleEdges()
    {
        const string content = """
            def alpha():
                beta()

            def beta():
                alpha()
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Contains(references, reference =>
            reference.ContainerName == "alpha"
            && reference.SymbolName == "beta"
            && reference.IsMutualRecursion
            && !reference.IsSelfReference);
        Assert.Contains(references, reference =>
            reference.ContainerName == "beta"
            && reference.SymbolName == "alpha"
            && reference.IsMutualRecursion
            && !reference.IsSelfReference);
    }

    [Fact]
    public void Extract_PythonDataclassField_EmitsMetadataAndDefaultFactoryReferences()
    {
        const string content = """
            from dataclasses import dataclass, field, fields

            @dataclass
            class Job:
                callback: Callable[[Payload], Result] = field(
                    default_factory=list,
                    metadata={
                        "wire_name": "callback",
                    },
                )

            def inspect_job():
                return fields(Job)
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "Payload"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "Job");
        Assert.Contains(references, reference =>
            reference.SymbolName == "Result"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "Job");
        Assert.Contains(references, reference =>
            reference.SymbolName == "list"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "Job");
        Assert.Contains(references, reference =>
            reference.SymbolName == "wire_name"
            && reference.ReferenceKind == "annotation"
            && reference.ContainerName == "Job");
        Assert.Contains(references, reference =>
            reference.SymbolName == "Job"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "inspect_job");
    }

    [Fact]
    public void Extract_PythonCall_AssignsCallerContainer()
    {
        const string content = """
            def login(user, password):
                return authenticate(user, password)
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        var reference = Assert.Single(references);
        Assert.Equal("authenticate", reference.SymbolName);
        Assert.Equal("call", reference.ReferenceKind);
        Assert.Equal("login", reference.ContainerName);
    }

    [Fact]
    public void Extract_PythonFString_PreservesInterpolationCalls()
    {
        const string content = """
            def run():
                return 42

            def use():
                return f"value = {run()}"
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "run"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "use");
    }

    [Fact]
    public void Extract_PythonFString_FormatSpecifier_PreservesFollowingCalls()
    {
        const string content = """
            def real_call():
                return 1

            def caller(value):
                msg = f"{value:#x} {real_call()}"
                return msg
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "real_call"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "caller");
    }

    [Fact]
    public void Extract_PythonDecorators_CaptureBareAndQualifiedNames()
    {
        const string content = """
            def bare_decorator(f):
                return f

            def parametrized(arg):
                def wrap(f):
                    return f
                return wrap

            def target_func():
                pass

            def memoize(fn):
                return fn

            def cache_with(timeout):
                def wrap(f):
                    return f
                return wrap

            def make_factory():
                return target_func

            DEFAULT_TIMEOUT = 30

            @bare_decorator
            @parametrized("value")
            def wrapped():
                pass

            @functools.wraps(target_func)
            def wrapped_target():
                pass

            @cache_with(timeout=30)(memoize(target_func))
            def composed_target():
                pass

            @cache_with(timeout=DEFAULT_TIMEOUT)
            def configured_target():
                pass

            @cache_with(factory=make_factory())
            def keyword_factory_target():
                pass

            @staticmethod
            def method():
                pass

            @pytest.fixture
            def fixture():
                pass

            @pytest.mark.parametrize("value", [1])
            def parametrized_fixture(value):
                pass
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Equal(9, references.Count(reference => reference.ReferenceKind == "decorator"));
        Assert.Contains(references, reference =>
            reference.SymbolName == "bare_decorator"
            && reference.ReferenceKind == "decorator");
        Assert.Contains(references, reference =>
            reference.SymbolName == "parametrized"
            && reference.ReferenceKind == "decorator");
        Assert.Contains(references, reference =>
            reference.SymbolName == "staticmethod"
            && reference.ReferenceKind == "decorator");
        Assert.Contains(references, reference =>
            reference.SymbolName == "pytest.fixture"
            && reference.ReferenceKind == "decorator");
        Assert.Contains(references, reference =>
            reference.SymbolName == "pytest.mark.parametrize"
            && reference.ReferenceKind == "decorator");
        Assert.Contains(references, reference =>
            reference.SymbolName == "parametrized"
            && reference.ReferenceKind == "call");
        Assert.Contains(references, reference =>
            reference.SymbolName == "target_func"
            && reference.ReferenceKind == "reference"
            && reference.Context == "@functools.wraps(target_func)");
        Assert.Contains(references, reference =>
            reference.SymbolName == "memoize"
            && reference.ReferenceKind == "call"
            && reference.Context == "@cache_with(timeout=30)(memoize(target_func))");
        Assert.Contains(references, reference =>
            reference.SymbolName == "target_func"
            && reference.ReferenceKind == "reference"
            && reference.Context == "@cache_with(timeout=30)(memoize(target_func))");
        Assert.Contains(references, reference =>
            reference.SymbolName == "make_factory"
            && reference.ReferenceKind == "call"
            && reference.Context == "@cache_with(factory=make_factory())");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "DEFAULT_TIMEOUT"
            && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_PythonBareRaise_CapturesExceptionTypeReference()
    {
        const string content = """
            def fail():
                raise CustomError
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "CustomError"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "fail");
    }

    [Fact]
    public void Extract_PythonRaiseFrom_CapturesExceptionTypeReference()
    {
        const string content = """
            def fail():
                raise package.CustomError from exc
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "CustomError"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "fail");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "exc"
            && reference.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_PythonExcept_CapturesExceptionTypeReference()
    {
        const string content = """
            def recover():
                try:
                    run()
                except CustomError as exc:
                    return exc
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "CustomError"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "recover");
    }

    [Fact]
    public void Extract_PythonExceptTuple_CapturesEachExceptionTypeReference()
    {
        const string content = """
            def recover():
                try:
                    run()
                except (TimeoutError, network.NetworkError) as exc:
                    return exc
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "TimeoutError"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "recover");
        Assert.Contains(references, reference =>
            reference.SymbolName == "NetworkError"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "recover");
    }

    [Fact]
    public void Extract_PythonIsInstance_CapturesCheckedTypeReference()
    {
        const string content = """
            def accepts(value):
                return isinstance(value, models.User)
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "User"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "accepts");
    }

    [Fact]
    public void Extract_PythonIsInstanceTuple_CapturesEachCheckedTypeReference()
    {
        const string content = """
            def accepts(value):
                return isinstance(value, (models.User, api.Admin))
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "User"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "accepts");
        Assert.Contains(references, reference =>
            reference.SymbolName == "Admin"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "accepts");
    }

    [Fact]
    public void Extract_PythonIsSubclass_CapturesCheckedTypeReference()
    {
        const string content = """
            def accepts(cls):
                return issubclass(cls, services.Plugin)
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "Plugin"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "accepts");
    }

    [Fact]
    public void Extract_PythonIsSubclassTuple_CapturesEachCheckedTypeReference()
    {
        const string content = """
            def accepts(cls):
                return issubclass(cls, (services.Plugin, mixins.Audited))
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "Plugin"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "accepts");
        Assert.Contains(references, reference =>
            reference.SymbolName == "Audited"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "accepts");
    }

    [Fact]
    public void Extract_PythonCast_CapturesTargetTypeReference()
    {
        const string content = """
            def load(value):
                return cast(models.User, value)
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "User"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "load");
    }

    [Fact]
    public void Extract_PythonMultilineAnnotations_CapturesSignatureTypeReferences()
    {
        const string content = """
            def build(
                value: int | "User",
                fallback: list[int | str],
            ) -> "Result":
                pass
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "int"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "build");
        Assert.Contains(references, reference =>
            reference.SymbolName == "User"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "build"
            && reference.Line == 2);
        Assert.Contains(references, reference =>
            reference.SymbolName == "list"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "build"
            && reference.Line == 3);
        Assert.Contains(references, reference =>
            reference.SymbolName == "str"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "build");
        Assert.Contains(references, reference =>
            reference.SymbolName == "Result"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "build"
            && reference.Line == 4);
    }

    [Fact]
    public void Extract_PythonClassHook_AssignsReferencesToHookContainer()
    {
        const string content = """
            class Base:
                def __init_subclass__(cls, plugin: Plugin) -> None:
                    register_plugin(cls)
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "Plugin"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "__init_subclass__");
        Assert.Contains(references, reference =>
            reference.SymbolName == "register_plugin"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "__init_subclass__");
    }

    [Fact]
    public void Extract_PythonMixedBasesAndMetaclass_EmitsBaseAndMetaclassReferences()
    {
        const string content = """
            class Derived(Base, Mixin, metaclass=Meta):
                pass
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "Base"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "Derived");
        Assert.Contains(references, reference =>
            reference.SymbolName == "Mixin"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "Derived");
        Assert.Contains(references, reference =>
            reference.SymbolName == "Meta"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "Derived");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "metaclass"
            && reference.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_PythonSuperInitSubclass_EmitsHookCallReference()
    {
        const string content = """
            class Base:
                def __init_subclass__(cls) -> None:
                    pass

            class Child(Base):
                def __init_subclass__(cls) -> None:
                    super().__init_subclass__()
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        var hookCall = Assert.Single(references, reference =>
            reference.SymbolName == "__init_subclass__"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "__init_subclass__"
            && reference.Line == 7);
        Assert.Equal(17, hookCall.Column);
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "super"
            && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_PythonDynamicImports_EmitImportAndImportlibReferences()
    {
        const string content = """
            import importlib

            def load(module_name):
                importlib.import_module("plugins.alpha")
                __import__('legacy.loader')
                importlib.util.find_spec("optional.backend")
                importlib.import_module(module_name)
                note = "importlib.import_module('not.real')"
                # importlib.import_module("commented.out")
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Equal(3, references.Count(reference =>
            reference.SymbolName == "importlib"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "load"));
        Assert.Contains(references, reference =>
            reference.SymbolName == "plugins.alpha"
            && reference.ReferenceKind == "import"
            && reference.ContainerName == "load");
        Assert.Contains(references, reference =>
            reference.SymbolName == "legacy.loader"
            && reference.ReferenceKind == "import"
            && reference.ContainerName == "load");
        Assert.Contains(references, reference =>
            reference.SymbolName == "optional.backend"
            && reference.ReferenceKind == "import"
            && reference.ContainerName == "load");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "module_name"
            && reference.ReferenceKind == "import");
        Assert.DoesNotContain(references, reference => reference.SymbolName == "not.real");
        Assert.DoesNotContain(references, reference => reference.SymbolName == "commented.out");
    }

    [Fact]
    public void Extract_PythonStringifiedAnnotations_CapturesNestedForwardReferences()
    {
        const string content = """
            from __future__ import annotations

            def load(value: Optional["User"]) -> "Result | None":
                pass
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "Optional"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "load");
        Assert.Contains(references, reference =>
            reference.SymbolName == "User"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "load");
        Assert.Contains(references, reference =>
            reference.SymbolName == "Result"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "load");
        Assert.Contains(references, reference =>
            reference.SymbolName == "None"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "load");
    }

    [Fact]
    public void Extract_PythonQualifiedCast_CapturesTargetTypeReference()
    {
        const string content = """
            def load(value):
                return typing.cast(models.User, value)
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "User"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "load");
    }

    [Fact]
    public void Extract_PythonAssertType_CapturesExpectedTypeReference()
    {
        const string content = """
            def test_user(value):
                assert_type(value, models.User)
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "User"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "test_user");
    }

    [Fact]
    public void Extract_PythonQualifiedAssertType_CapturesExpectedTypeReference()
    {
        const string content = """
            def test_user(value):
                typing.assert_type(value, models.User)
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "User"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "test_user");
    }

    [Fact]
    public void Extract_PythonClassBase_CapturesBaseTypeReference()
    {
        const string content = """
            class UserView(views.BaseView):
                pass
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "BaseView"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "UserView");
    }

    [Fact]
    public void Extract_PythonClassMultipleBases_CapturesEachBaseTypeReference()
    {
        const string content = """
            class UserView(views.BaseView, mixins.AuditedMixin):
                pass
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "BaseView"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "UserView");
        Assert.Contains(references, reference =>
            reference.SymbolName == "AuditedMixin"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "UserView");
    }

    [Fact]
    public void Extract_PythonClassMetaclass_CapturesMetaclassTypeReference()
    {
        const string content = """
            class Model(metaclass=orm.ModelMeta):
                pass
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "ModelMeta"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "Model");
    }

    [Fact]
    public void Extract_PythonFunctionReturnAnnotation_CapturesReturnTypeReference()
    {
        const string content = """
            def load() -> models.User:
                return get_user()
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "User"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "load");
    }

    [Fact]
    public void Extract_PythonGenericReturnAnnotation_CapturesNestedTypeReference()
    {
        const string content = """
            def load_many() -> list[models.User]:
                return []
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "User"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "load_many");
    }

    [Fact]
    public void Extract_PythonFunctionParameterAnnotation_CapturesParameterTypeReference()
    {
        const string content = """
            def save(user: models.User):
                persist(user)
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "User"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "save");
    }

    [Fact]
    public void Extract_PythonGenericParameterAnnotation_CapturesNestedTypeReference()
    {
        const string content = """
            def save(users: Sequence[models.User]):
                persist(users)
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "User"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "save");
    }

    [Fact]
    public void Extract_PythonVariableAnnotation_CapturesVariableTypeReference()
    {
        const string content = """
            def save():
                user: models.User = load_user()
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "User"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "save");
    }

    [Fact]
    public void Extract_PythonGenericVariableAnnotation_CapturesNestedTypeReference()
    {
        const string content = """
            def save():
                users: Sequence[models.User] = []
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "User"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "save");
    }

    [Fact]
    public void Extract_PythonTypeAlias_CapturesAliasedTypeReference()
    {
        const string content = """
            UserAlias: TypeAlias = models.User
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "User"
            && reference.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_PythonNewType_CapturesUnderlyingTypeReference()
    {
        const string content = """
            UserId = NewType("UserId", models.User)
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "User"
            && reference.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_PythonTypeVarBound_CapturesBoundTypeReference()
    {
        const string content = """
            TUser = TypeVar("TUser", bound=models.User)
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "User"
            && reference.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_PythonTypeVarConstraints_CapturesConstraintTypeReferences()
    {
        const string content = """
            TAccount = TypeVar("TAccount", models.User, models.Admin)
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "User"
            && reference.ReferenceKind == "type_reference");
        Assert.Contains(references, reference =>
            reference.SymbolName == "Admin"
            && reference.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_PythonTypeVarConstraints_MultilineCapturesConstraintTypeReferences()
    {
        const string content = """
            TAccount = TypeVar(
                "TAccount",
                models.User,
                models.Admin,
            )
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "User"
            && reference.ReferenceKind == "type_reference"
            && reference.Line == 3);
        Assert.Contains(references, reference =>
            reference.SymbolName == "Admin"
            && reference.ReferenceKind == "type_reference"
            && reference.Line == 4);
    }

    [Fact]
    public void Extract_PythonTypeVarConstraints_MultilineDoesNotCaptureCommentTypeNames()
    {
        const string content = """
            TAccount = TypeVar(
                "TAccount",
                models.Admin,  # models.User should stay a comment
            )
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "Admin"
            && reference.ReferenceKind == "type_reference"
            && reference.Line == 3);
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "User"
            && reference.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_PythonParamSpecBound_CapturesNestedCallableTypeReferences()
    {
        const string content = """
            P = ParamSpec("P", bound=Callable[models.User, results.Result])
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "User"
            && reference.ReferenceKind == "type_reference");
        Assert.Contains(references, reference =>
            reference.SymbolName == "Result"
            && reference.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_PythonCallableParamSpecAnnotation_CapturesReturnTypeAfterComma()
    {
        const string content = """
            def bind(callback: Callable[P.args, results.Result]):
                return callback
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "P"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "bind");
        Assert.Contains(references, reference =>
            reference.SymbolName == "Result"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "bind");
    }

    [Fact]
    public void Extract_PythonCallableParameterAnnotation_DoesNotCaptureNextParameterName()
    {
        const string content = """
            def bind(callback: Callable[P.args, results.Result], Request=None):
                return callback
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "Result"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "bind");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "Request"
            && reference.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_PythonTypeVarTupleUnpack_CapturesTupleTypeReference()
    {
        const string content = """
            type Packed = tuple[*Ts, results.Result]
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "Ts"
            && reference.ReferenceKind == "type_reference");
        Assert.Contains(references, reference =>
            reference.SymbolName == "Result"
            && reference.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_PythonLiteralUnion_CapturesNestedUnionTypeReferences()
    {
        const string content = """
            type Choice = Literal["a", "b"] | models.User | results.Result
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "User"
            && reference.ReferenceKind == "type_reference");
        Assert.Contains(references, reference =>
            reference.SymbolName == "Result"
            && reference.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_PythonGetTypeHints_CapturesTargetTypeReference()
    {
        const string content = """
            def inspect():
                return get_type_hints(models.User)
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "User"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "inspect");
    }

    [Fact]
    public void Extract_PythonQualifiedGetTypeHints_CapturesTargetTypeReference()
    {
        const string content = """
            def inspect():
                return typing.get_type_hints(models.User)
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "User"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "inspect");
    }

    [Fact]
    public void Extract_PythonDataclassesFields_CapturesTargetTypeReference()
    {
        const string content = """
            def inspect():
                return dataclasses.fields(models.User)
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "User"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "inspect");
    }

    [Fact]
    public void Extract_PythonAttrsFields_CapturesTargetTypeReference()
    {
        const string content = """
            def inspect():
                return attrs.fields(models.User)
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "User"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "inspect");
    }

    [Fact]
    public void Extract_PythonPydanticTypeAdapter_CapturesTargetTypeReference()
    {
        const string content = """
            def validate(value):
                adapter = pydantic.TypeAdapter(models.User)
                return adapter.validate_python(value)
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "User"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "validate");
    }

    [Fact]
    public void Extract_PythonPytestRaises_CapturesExceptionTypeReference()
    {
        const string content = """
            def test_invalid():
                with pytest.raises(errors.ValidationError):
                    validate({})
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "ValidationError"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "test_invalid");
    }

    [Fact]
    public void Extract_PythonContextlibSuppress_CapturesExceptionTypeReference()
    {
        const string content = """
            def cleanup():
                with contextlib.suppress(errors.NotFoundError):
                    remove()
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "NotFoundError"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "cleanup");
    }

    [Fact]
    public void Extract_PythonFString_KeepsSingleLineInterpolationCallReferences()
    {
        const string content = """
            def run():
                return 42

            def use():
                value = f"value = {run()}"
                return value
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        var runReference = Assert.Single(references);
        Assert.Equal("run", runReference.SymbolName);
        Assert.Equal("call", runReference.ReferenceKind);
        Assert.Equal("use", runReference.ContainerName);
    }

    [Fact]
    public void Extract_PythonFString_MasksMultilineLiteralTextButKeepsInterpolationReferences()
    {
        const string content = """"
            def run():
                return 42

            def use(user_name):
                value = f"""hello
                {run()}
                goodbye user_name
                """
                return value
            """";

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        var runReference = Assert.Single(references, reference => reference.SymbolName == "run");
        Assert.Equal("call", runReference.ReferenceKind);
        Assert.Equal("use", runReference.ContainerName);
        Assert.DoesNotContain(references, reference => reference.SymbolName is "hello" or "goodbye" or "user_name");
    }

    [Fact]
    public void Extract_PythonFString_KeepsReferencesAfterNestedExpressionStringBrace()
    {
        const string content = """"
            def run():
                return 42

            def use(format_value):
                value = f"""{format_value("}") + run()}"""
                return value
            """";

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "run"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "use");
    }

    [Fact]
    public void Extract_PythonLegitimateCalls_AreNotDroppedByOtherLanguageKeywordLists()
    {
        const string content = """
            def caller():
                run()
                build()
                install()
                clean()
                help()
                print()
                require()
                notexcluded()
                apply()
                task()
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        var names = references.Select(reference => reference.SymbolName).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("run", names);
        Assert.Contains("build", names);
        Assert.Contains("install", names);
        Assert.Contains("clean", names);
        Assert.Contains("help", names);
        Assert.Contains("print", names);
        Assert.Contains("require", names);
        Assert.Contains("notexcluded", names);
        Assert.Contains("apply", names);
        Assert.Contains("task", names);
        Assert.Equal(10, references.Count(reference => reference.ReferenceKind == "call"));
    }

    [Fact]
    public void Extract_PythonRaiseSyntax_IsIgnored()
    {
        const string content = """
            def fail():
                raise(ValueError())
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.DoesNotContain(references, reference => reference.SymbolName == "raise");
        Assert.Contains(references, reference => reference.SymbolName == "ValueError" && reference.ContainerName == "fail");
    }

    [Fact]
    public void Extract_PythonYieldSyntax_IsIgnored()
    {
        const string content = """
            def stream(xs):
                yield(item())
                yield from(source())
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        Assert.DoesNotContain(references, reference => reference.SymbolName == "yield");
        Assert.DoesNotContain(references, reference => reference.SymbolName == "from");
        Assert.Contains(references, reference => reference.SymbolName == "item" && reference.ContainerName == "stream");
        Assert.Contains(references, reference => reference.SymbolName == "source" && reference.ContainerName == "stream");
    }
}
