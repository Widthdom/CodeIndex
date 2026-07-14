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
    public void Extract_Go_ClassifiesTestBenchmarkExampleInitFunctions()
    {
        const string content = """
            package demo

            import "testing"

            func init() {}
            func TestMain(m *testing.M) {}
            func TestWidget(t *testing.T) {}
            func Test1(t *testing.T) {}
            func Test_HTTP(t *testing.T) {}
            func BenchmarkWidget(b *testing.B) {}
            func Benchmark1(b *testing.B) {}
            func FuzzWidget(f *testing.F) {}
            func Fuzz_HTTP(f *testing.F) {}
            func ExampleWidget() {}
            func helper() {}
            func TestExporter() {}
            func TestTransaction(t Transaction) {}
            func BenchmarkBuilder(b Builder) {}
            func FuzzFactory(f Factory) {}
            """;

        var symbols = SymbolExtractor.Extract(1, "go", content, filePath: "widget_test.go");

        Assert.Contains(symbols, symbol => symbol.Name == "init" && symbol.SubKind == "init");
        Assert.Contains(symbols, symbol => symbol.Name == "TestMain" && symbol.SubKind == "test_main");
        Assert.Contains(symbols, symbol => symbol.Name == "TestWidget" && symbol.SubKind == "test");
        Assert.Contains(symbols, symbol => symbol.Name == "Test1" && symbol.SubKind == "test");
        Assert.Contains(symbols, symbol => symbol.Name == "Test_HTTP" && symbol.SubKind == "test");
        Assert.Contains(symbols, symbol => symbol.Name == "BenchmarkWidget" && symbol.SubKind == "benchmark");
        Assert.Contains(symbols, symbol => symbol.Name == "Benchmark1" && symbol.SubKind == "benchmark");
        Assert.Contains(symbols, symbol => symbol.Name == "FuzzWidget" && symbol.SubKind == "fuzz");
        Assert.Contains(symbols, symbol => symbol.Name == "Fuzz_HTTP" && symbol.SubKind == "fuzz");
        Assert.Contains(symbols, symbol => symbol.Name == "ExampleWidget" && symbol.SubKind == "example");
        Assert.Contains(symbols, symbol => symbol.Name == "helper" && symbol.SubKind == "test_helper");
        Assert.Contains(symbols, symbol => symbol.Name == "TestExporter" && symbol.SubKind == "test_helper");
        Assert.Contains(symbols, symbol => symbol.Name == "TestTransaction" && symbol.SubKind == "test_helper");
        Assert.Contains(symbols, symbol => symbol.Name == "BenchmarkBuilder" && symbol.SubKind == "test_helper");
        Assert.Contains(symbols, symbol => symbol.Name == "FuzzFactory" && symbol.SubKind == "test_helper");
    }

    [Fact]
    public void Extract_Go_DoesNotClassifyTestRolesOutsideTestFiles()
    {
        const string content = """
            package demo

            import "testing"

            func init() {}
            func TestWidget(t *testing.T) {}
            func BenchmarkWidget(b *testing.B) {}
            func FuzzWidget(f *testing.F) {}
            func ExampleWidget() {}
            """;

        var symbols = SymbolExtractor.Extract(1, "go", content, filePath: "widget.go");

        Assert.Contains(symbols, symbol => symbol.Name == "init" && symbol.SubKind == "init");
        Assert.Contains(symbols, symbol => symbol.Name == "TestWidget" && symbol.SubKind == null);
        Assert.Contains(symbols, symbol => symbol.Name == "BenchmarkWidget" && symbol.SubKind == null);
        Assert.Contains(symbols, symbol => symbol.Name == "FuzzWidget" && symbol.SubKind == null);
        Assert.Contains(symbols, symbol => symbol.Name == "ExampleWidget" && symbol.SubKind == null);
    }

    [Fact]
    public void Extract_Go_QualifiedReceiverAndLabelSymbolsShareFunctionFixture()
    {
        const string content = """
            package demo

            type Widget struct {}

            func (w *pkg.Widget) Run() {}

            func run() {
            Retry:
                for {
                    break Retry
                }

                item := User{
                    Retry: true,
                }
                value := 1
                _ = item
                switch value {
                case 1:
                default:
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "go", content);

        Assert.Contains(symbols, symbol =>
            symbol.Kind == "function"
            && symbol.Name == "Run"
            && symbol.ContainerName == "Widget"
            && symbol.ContainerKind == "struct");
        var label = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "Retry");
        Assert.Equal(8, label.Line);
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name is "case" or "default");
    }

    [Fact]
    public void Extract_Go_DetectsImportsDirectivesAndCgoTogether()
    {
        var content = """
            package demo

            //go:build darwin && cgo
            //go:test integration
            import "C"
            import "bytes" // ERROR "invalid import path (empty string)"
            import alias "fmt" // trailing comment

            import (
                "io"
                . "strings"
                _ "net/http/pprof"
                alias2 "example.com/project"
                // ignored comment
            )

               import (
                   "unicode/utf8" )

            func main() {}
            func CallCCode() {}
            """;

        var symbols = SymbolExtractor.Extract(1, "go", content);
        var imports = symbols.Where(s => s.Kind == "import").ToList();

        Assert.Equal(7, imports.Count);
        AssertSymbolsContain(symbols, "namespace", "demo");
        AssertSymbolsContain(symbols, "function", "main");
        Assert.Contains(imports, s => s.Name == "\"bytes\"");
        Assert.Contains(imports, s => s.Name == "alias \"fmt\"");
        Assert.Contains(imports, s => s.Name == "\"io\"");
        Assert.Contains(imports, s => s.Name == ". \"strings\"");
        Assert.Contains(imports, s => s.Name == "_ \"net/http/pprof\"");
        Assert.Contains(imports, s => s.Name == "alias2 \"example.com/project\"");
        Assert.Contains(imports, s => s.Name == "\"unicode/utf8\"");
        Assert.DoesNotContain(imports, s => s.Name == "(");
        Assert.DoesNotContain(imports, s => s.Name.Contains("ERROR", StringComparison.Ordinal));
        Assert.Contains(symbols, s => s.Kind == "annotation" && s.Name == "go:build darwin && cgo");
        Assert.Contains(symbols, s => s.Kind == "annotation" && s.Name == "go:test integration");
        Assert.Contains(symbols, s => s.Kind == "cgo" && s.Name == @"""C""");
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == @"""C""");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "CallCCode");
    }

    [Fact]
    public void Extract_Go_DetectsGroupedTopLevelDeclarationsAndBlankIdentifiers()
    {
        var content = """
            package demo

            type (
                Stack[T any] struct { items []T }
                Container[T comparable, U any] interface {
                    io.Reader
                    Get() U
                }
                GroupAlias[T any] string
            )

            const MaxRetries = 3
            const Timeout int = 30
            const PrimaryStatus, SecondaryStatus = 1, 2
            const (
                StatusActive = "active"
                DefaultTimeout int = 30
                Named, Other = 1, 2
            )
            const _, exported = 1, 2

            var ErrNotFound = errors.New("not found")
            var DefaultConfig *Config = &Config{}
            var Primary, Secondary *Config
            var (
                GroupPrimary, GroupSecondary *Client
                _ int
                _unused string
                _, err = open()
            )

            func build() {
                var local, cached *Config
                const localStatus, cachedStatus = 1, 2
                var (
                    localGrouped = 1
                    cachedGrouped = 2
                )
                const (
                    localGroupedStatus = 1
                    cachedGroupedStatus = 2
                )
                user := User{Name: "alice"}
                _ = user
            }

            func bracesInText() {
                _ = "{"
                _ = `{
            }`
                // {
            }

            const AfterText = 4
            var AfterTextConfig *Config
            """;

        var symbols = SymbolExtractor.Extract(1, "go", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "MaxRetries");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Timeout");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "PrimaryStatus");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "SecondaryStatus");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "StatusActive");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "ErrNotFound");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "DefaultConfig");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Primary");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Secondary");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "AfterText");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "AfterTextConfig");
        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "Stack");
        Assert.Contains(symbols, s => s.Kind == "protocol" && s.Name == "Container");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "GroupAlias");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Get");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "io.Reader");
        foreach (var propertyName in new[] { "DefaultTimeout", "Named", "Other", "exported", "GroupPrimary", "GroupSecondary", "_unused", "err" })
        {
            Assert.Contains(symbols, s => s.Kind == "property" && s.Name == propertyName);
        }

        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "_");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "local");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "cached");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "localStatus");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "cachedStatus");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "localGrouped");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "cachedGrouped");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "localGroupedStatus");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "cachedGroupedStatus");
        Assert.DoesNotContain(symbols, s => s.Name == "Name");
    }

    [Fact]
    public void Extract_Go_DetectsNamedAliasAndGenericTypeSymbols()
    {
        var content = """
            package demo

            type Identifier = string
            type Count int

            type (
                Alias = otherpkg.Value
                Score uint64
                Node struct {
                    ID Identifier
                }
                Reader interface {
                    Read([]byte) (int, error)
                }
            )

            type Stack[T any] struct {
                items []T
            }

            type Container[T comparable, U any] interface {
                Get() U
            }

            type GenericAlias[T any] string
            """;

        var symbols = SymbolExtractor.Extract(1, "go", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Identifier");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Count");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Alias");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Score");
        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "Node");
        Assert.Contains(symbols, s => s.Kind == "protocol" && s.Name == "Reader");
        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "Stack");
        Assert.Contains(symbols, s => s.Kind == "protocol" && s.Name == "Container");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "GenericAlias");
    }

    [Fact]
    public void Extract_Go_DetectsFunctionsMethodsGenericsAndAssignedLambdas()
    {
        // Should detect both regular and method functions
        // 通常関数とメソッド関数の両方を検出する
        var content = "type Handler struct {\n}\ntype Store[T, U any] struct {\n}\nfunc NewHandler() *Handler {\n}\nfunc Load(input User) Result {\n}\nfunc (h *Handler) ServeHTTP(w http.ResponseWriter, r *http.Request) {\n}\nfunc (s *Store[T, U]) Save(value T) {\n}\nfunc (Store[T, U]) Snapshot() {\n}\nfunc Identity[T any](value T) T { return value }\nfunc Run() {\ntransform := func(value int) int {\nreturn value + 1\n}\n_ = transform\n}";
        var symbols = SymbolExtractor.Extract(1, "go", content);

        Assert.Equal(7, symbols.Count(s => s.Kind == "function"));
        Assert.Contains(symbols, s => s.Name == "NewHandler");
        var regularFunction = Assert.Single(symbols, s => s.Name == "Load");
        Assert.Null(regularFunction.ContainerName);
        var method = Assert.Single(symbols, s => s.Name == "ServeHTTP");
        Assert.Equal("struct", method.ContainerKind);
        Assert.Equal("Handler", method.ContainerName);
        var genericMethod = Assert.Single(symbols, s => s.Name == "Save");
        Assert.Equal("struct", genericMethod.ContainerKind);
        Assert.Equal("Store", genericMethod.ContainerName);
        var unnamedGenericMethod = Assert.Single(symbols, s => s.Name == "Snapshot");
        Assert.Equal("struct", unnamedGenericMethod.ContainerKind);
        Assert.Equal("Store", unnamedGenericMethod.ContainerName);
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Identity");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Run");
        Assert.Equal("transform", Assert.Single(symbols, s => s.Kind == "lambda").Name);
    }

    [Fact]
    public void Extract_Go_DetectsEmbeddedStructAndInterfaceTypesTogether()
    {
        var content = """
            package demo

            type Inline[T any] struct { Reader[T]; *pkg.Writer[U] }

            type Container[T any, U any] struct {
                Reader[T]
                *pkg.Writer[U]
                Named Field[T]
            }

            type ContractReader interface {
                io.Reader
                Close() error
            }

            type StoreContract interface { io.Writer }
            """;
        var symbols = SymbolExtractor.Extract(1, "go", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Reader" && s.Signature == "Reader[T]");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "pkg.Writer" && s.Signature == "*pkg.Writer[U]");
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "Named");
        var close = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "Close");
        Assert.Equal("protocol", close.ContainerKind);
        Assert.Equal("ContractReader", close.ContainerName);

        var reader = Assert.Single(symbols, s => s.Kind == "import" && s.Name == "io.Reader");
        Assert.Equal("protocol", reader.ContainerKind);
        Assert.Equal("ContractReader", reader.ContainerName);

        var writer = Assert.Single(symbols, s => s.Kind == "import" && s.Name == "io.Writer");
        Assert.Equal("protocol", writer.ContainerKind);
        Assert.Equal("StoreContract", writer.ContainerName);
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name is "ContractReader" or "StoreContract");
    }

    [Fact]
    public void Extract_Go_InterfaceMethodSignatureIncludesTypeParameters()
    {
        var content = """
            package demo

            type Ordered interface {
                Method[T constraints.Ordered](x T) T
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "go", content);

        var method = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "Method");
        Assert.Equal("Method[T constraints.Ordered](x T) T", method.Signature);
    }

}
