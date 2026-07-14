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

        AssertReferencesContain(references, "type_reference", "Job", "Payload", "Result");
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
        AssertReferencesContain(
            references,
            "decorator",
            null,
            "bare_decorator",
            "parametrized",
            "staticmethod",
            "pytest.fixture",
            "pytest.mark.parametrize");
        Assert.Contains(references, reference =>
            reference.SymbolName == "parametrized"
            && reference.ReferenceKind == "call");
        AssertReferencesContainInContext(references, "reference", "@functools.wraps(target_func)", "target_func");
        AssertReferencesContainInContext(references, "call", "@cache_with(timeout=30)(memoize(target_func))", "memoize");
        AssertReferencesContainInContext(references, "reference", "@cache_with(timeout=30)(memoize(target_func))", "target_func");
        AssertReferencesContainInContext(references, "call", "@cache_with(factory=make_factory())", "make_factory");
        AssertReferencesDoNotContain(references, "call", "DEFAULT_TIMEOUT");
    }

    [Fact]
    public void Extract_PythonExceptionContexts_ReuseRaiseExceptAndHelperFixture()
    {
        const string content = """
            def fail_bare():
                raise BareError

            def fail_from():
                raise package.ChainedError from exc

            def recover_single():
                try:
                    run()
                except SingleError as exc:
                    return exc

            def recover_tuple():
                try:
                    run()
                except (TimeoutError, network.NetworkError) as exc:
                    return exc

            def test_invalid():
                with pytest.raises(errors.ValidationError):
                    validate({})

            def cleanup():
                with contextlib.suppress(errors.NotFoundError):
                    remove()
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        AssertExceptionType("BareError", "fail_bare");
        AssertExceptionType("ChainedError", "fail_from");
        AssertExceptionType("SingleError", "recover_single");
        AssertExceptionType("TimeoutError", "recover_tuple");
        AssertExceptionType("NetworkError", "recover_tuple");
        AssertExceptionType("ValidationError", "test_invalid");
        AssertExceptionType("NotFoundError", "cleanup");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "exc" && reference.ReferenceKind == "type_reference");

        void AssertExceptionType(string symbolName, string containerName) =>
            Assert.Contains(references, reference =>
                reference.SymbolName == symbolName
                && reference.ReferenceKind == "type_reference"
                && reference.ContainerName == containerName);
    }

    [Fact]
    public void Extract_PythonRuntimeTypeChecks_ReuseSingleAndTupleFixture()
    {
        const string content = """
            def accepts_single(value):
                return isinstance(value, models.User)

            def accepts_tuple(value):
                return isinstance(value, (models.User, api.Admin))

            def accepts_subclass(cls):
                return issubclass(cls, services.Plugin)

            def accepts_subclass_tuple(cls):
                return issubclass(cls, (services.Plugin, mixins.Audited))
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        AssertTypeReference("User", "accepts_single");
        AssertTypeReference("User", "accepts_tuple");
        AssertTypeReference("Admin", "accepts_tuple");
        AssertTypeReference("Plugin", "accepts_subclass");
        AssertTypeReference("Plugin", "accepts_subclass_tuple");
        AssertTypeReference("Audited", "accepts_subclass_tuple");

        void AssertTypeReference(string symbolName, string containerName) =>
            Assert.Contains(references, reference =>
                reference.SymbolName == symbolName
                && reference.ReferenceKind == "type_reference"
                && reference.ContainerName == containerName);
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
    public void Extract_PythonOversizedLogicalHeaderAndStatement_DoesNotThrow()
    {
        var longTypeName = new string('A', 40_000);
        var longValueName = new string('B', 40_000);
        var content = $$"""
            def build(
                value: {{longTypeName}},
            ):
                result = (
                    {{longValueName}}
                )
                return result
            """;

        var exception = Record.Exception(() =>
        {
            var symbols = SymbolExtractor.Extract(1, "python", content);
            ReferenceExtractor.Extract(1, "python", content, symbols);
        });

        Assert.Null(exception);
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
    public void Extract_PythonClassHeaders_ReuseSingleMultipleAndMetaclassFixture()
    {
        const string content = """
            class SingleView(views.BaseView):
                pass

            class MultipleView(views.BaseView, mixins.AuditedMixin):
                pass

            class Model(metaclass=orm.ModelMeta):
                pass
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        AssertClassType("BaseView", "SingleView");
        AssertClassType("BaseView", "MultipleView");
        AssertClassType("AuditedMixin", "MultipleView");
        AssertClassType("ModelMeta", "Model");

        void AssertClassType(string symbolName, string containerName) =>
            Assert.Contains(references, reference =>
                reference.SymbolName == symbolName
                && reference.ReferenceKind == "type_reference"
                && reference.ContainerName == containerName);
    }

    [Fact]
    public void Extract_PythonAnnotations_ReuseReturnParameterAndVariableFixture()
    {
        const string content = """
            def load() -> models.User:
                return get_user()

            def load_many() -> list[models.User]:
                return []

            def save_one(user: models.User):
                persist(user)

            def save_many(users: Sequence[models.User]):
                persist(users)

            def assign_one():
                user: models.User = load_user()

            def assign_many():
                users: Sequence[models.User] = []
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        AssertAnnotated("load");
        AssertAnnotated("load_many");
        AssertAnnotated("save_one");
        AssertAnnotated("save_many");
        AssertAnnotated("assign_one");
        AssertAnnotated("assign_many");

        void AssertAnnotated(string containerName) =>
            Assert.Contains(references, reference =>
                reference.SymbolName == "User"
                && reference.ReferenceKind == "type_reference"
                && reference.ContainerName == containerName);
    }

    [Fact]
    public void Extract_PythonTypingFactories_ReuseAliasNewTypeAndTypeVarFixture()
    {
        const string content = """
            UserAlias: TypeAlias = models.AliasUser
            UserId = NewType("UserId", models.UnderlyingUser)
            TUser = TypeVar("TUser", bound=models.BoundUser)
            TAccount = TypeVar("TAccount", models.ConstraintUser, models.ConstraintAdmin)
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        AssertTypeReference("AliasUser");
        AssertTypeReference("UnderlyingUser");
        AssertTypeReference("BoundUser");
        AssertTypeReference("ConstraintUser");
        AssertTypeReference("ConstraintAdmin");

        void AssertTypeReference(string symbolName) =>
            Assert.Contains(references, reference =>
                reference.SymbolName == symbolName
                && reference.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_PythonAdvancedTyping_ReuseLogicalHeaderFixture()
    {
        const string content = """
            TAccount = TypeVar(
                "TAccount",
                models.MultiUser,
                models.MultiAdmin,
            )

            TComment = TypeVar(
                "TComment",
                models.VisibleAdmin,  # models.CommentOnly should stay a comment
            )

            P = ParamSpec("P", bound=Callable[models.BoundCallableUser, results.BoundResult])

            def bind(callback: Callable[P.args, results.CallbackResult]):
                return callback

            def bind_default(callback: Callable[P.args, results.DefaultResult], Request=None):
                return callback

            type Packed = tuple[*Ts, results.TupleResult]
            type Choice = Literal["a", "b"] | models.UnionUser | results.UnionResult
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        foreach (var typeName in new[]
                 {
                     "MultiUser", "MultiAdmin", "VisibleAdmin", "BoundCallableUser",
                     "BoundResult", "CallbackResult", "DefaultResult", "Ts", "TupleResult",
                     "UnionUser", "UnionResult",
                 })
        {
            Assert.Contains(references, reference =>
                reference.SymbolName == typeName && reference.ReferenceKind == "type_reference");
        }

        Assert.Contains(references, reference =>
            reference.SymbolName == "MultiUser" && reference.ReferenceKind == "type_reference" && reference.Line == 3);
        Assert.Contains(references, reference =>
            reference.SymbolName == "MultiAdmin" && reference.ReferenceKind == "type_reference" && reference.Line == 4);
        Assert.Contains(references, reference =>
            reference.SymbolName == "VisibleAdmin" && reference.ReferenceKind == "type_reference" && reference.Line == 9);
        Assert.Contains(references, reference =>
            reference.SymbolName == "P" && reference.ReferenceKind == "type_reference" &&
            reference.ContainerName == "bind");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName is "Request" or "CommentOnly" &&
            reference.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_PythonTypeIntrospectionHelpers_ReuseApiFixture()
    {
        const string content = """
            def inspect_hints():
                return get_type_hints(models.HintsUser)

            def inspect_qualified_hints():
                return typing.get_type_hints(models.QualifiedHintsUser)

            def inspect_dataclass():
                return dataclasses.fields(models.DataclassUser)

            def inspect_attrs():
                return attrs.fields(models.AttrsUser)

            def validate(value):
                adapter = pydantic.TypeAdapter(models.AdapterUser)
                return adapter.validate_python(value)

            def cast_bare(value):
                return cast(models.BareCastUser, value)

            def cast_qualified(value):
                return typing.cast(models.QualifiedCastUser, value)

            def assert_bare(value):
                assert_type(value, models.BareAssertUser)

            def assert_qualified(value):
                typing.assert_type(value, models.QualifiedAssertUser)
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        AssertHelperType("HintsUser", "inspect_hints");
        AssertHelperType("QualifiedHintsUser", "inspect_qualified_hints");
        AssertHelperType("DataclassUser", "inspect_dataclass");
        AssertHelperType("AttrsUser", "inspect_attrs");
        AssertHelperType("AdapterUser", "validate");
        AssertHelperType("BareCastUser", "cast_bare");
        AssertHelperType("QualifiedCastUser", "cast_qualified");
        AssertHelperType("BareAssertUser", "assert_bare");
        AssertHelperType("QualifiedAssertUser", "assert_qualified");

        void AssertHelperType(string symbolName, string containerName) =>
            Assert.Contains(references, reference =>
                reference.SymbolName == symbolName
                && reference.ReferenceKind == "type_reference"
                && reference.ContainerName == containerName);
    }

    [Fact]
    public void Extract_PythonFStrings_ReuseSingleMultilineAndNestedFixture()
    {
        const string content = """"
            def run_single():
                return 42

            def use_single():
                value = f"value = {run_single()}"
                return value

            def run_multiline():
                return 42

            def use_multiline(user_name):
                value = f"""hello
                {run_multiline()}
                goodbye user_name
                """
                return value

            def run_nested():
                return 42

            def use_nested(format_value):
                value = f"""{format_value("}") + run_nested()}"""
                return value

            def run_format():
                return 1

            def use_format(value):
                return f"{value:#x} {run_format()}"
            """";

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        AssertInterpolationCalls("use_single", "run_single");
        AssertInterpolationCalls("use_multiline", "run_multiline");
        AssertInterpolationCalls("use_nested", "format_value", "run_nested");
        AssertInterpolationCalls("use_format", "run_format");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName is "hello" or "goodbye" or "user_name");

        void AssertInterpolationCalls(string containerName, params string[] symbolNames)
        {
            var containerReferences = references
                .Where(candidate => candidate.ContainerName == containerName)
                .ToArray();
            Assert.All(containerReferences, reference => Assert.Equal("call", reference.ReferenceKind));
            Assert.Equal(
                symbolNames.OrderBy(name => name, StringComparer.Ordinal),
                containerReferences.Select(reference => reference.SymbolName).OrderBy(name => name, StringComparer.Ordinal));
        }
    }

    [Fact]
    public void Extract_PythonKeywordFiltering_ReuseCallsRaiseAndYieldFixture()
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

            def fail():
                raise(ValueError())

            def stream(xs):
                yield(item())
                yield from(source())
            """;

        var symbols = SymbolExtractor.Extract(1, "python", content);
        var references = ReferenceExtractor.Extract(1, "python", content, symbols);

        string[] expectedCallerCalls =
            ["run", "build", "install", "clean", "help", "print", "require", "notexcluded", "apply", "task"];
        var callerCalls = references
            .Where(reference => reference.ReferenceKind == "call" && reference.ContainerName == "caller")
            .Select(reference => reference.SymbolName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedCallerCalls.OrderBy(name => name, StringComparer.Ordinal), callerCalls);

        Assert.DoesNotContain(references, reference => reference.SymbolName is "raise" or "yield" or "from");
        Assert.Contains(references, reference => reference.SymbolName == "ValueError" && reference.ContainerName == "fail");
        Assert.Contains(references, reference => reference.SymbolName == "item" && reference.ContainerName == "stream");
        Assert.Contains(references, reference => reference.SymbolName == "source" && reference.ContainerName == "stream");
    }
}
