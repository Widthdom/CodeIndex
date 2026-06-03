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
    public void Extract_SwiftPropertyWrappers_EmitTypeReferences()
    {
        const string content = """
            import SwiftUI

            struct Screen {
                @State private var count = 0
                @Environment(\.colorScheme) var scheme
                @MyWrapper var value: Int
                @MyLib.State var qualifiedCount = 0
                @IBOutlet weak var titleLabel: UILabel!
                @NSManaged var persistedName: String
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "swift", content);
        var references = ReferenceExtractor.Extract(1, "swift", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "State"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "count");
        Assert.Contains(references, reference =>
            reference.SymbolName == "Environment"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "scheme");
        Assert.Contains(references, reference =>
            reference.SymbolName == "MyWrapper"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "value");
        Assert.Contains(references, reference =>
            reference.SymbolName == "State"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "qualifiedCount");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName is "IBOutlet" or "NSManaged"
            && reference.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_RustMacroCalls_CaptureDelimitedFormsWithoutMacroRulesDeclaration()
    {
        // issue #258: Rust macro invocations need to surface as call-like references so
        // callers / callees / impact can follow both std macros and user-defined macros.
        // issue #258: Rust の macro 呼び出しは call 相当の reference として出し、
        // std macro と user 定義 macro の両方を callers / callees / impact から辿れるようにする。
        const string content = """
            macro_rules! my_macro {
                ($x:expr) => { $x + 1 };
            }

            fn helper(x: i32) -> i32 { x }

            fn main() {
                std::println!("hello");
                let v = vec![1, 2, 3];
                let msg = format!("x={}", 42);
                let y = my_macro!(42);
                let z = helper(1);
                dbg!(y + z);
                let _ = msg;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "rust", content);
        var references = ReferenceExtractor.Extract(1, "rust", content, symbols);

        var callReferences = references.Where(reference => reference.ReferenceKind == "call").ToList();
        Assert.Equal(6, callReferences.Count);
        Assert.Contains(callReferences, reference =>
            reference.SymbolName == "std::println"
            && reference.ContainerName == "main");
        Assert.Contains(callReferences, reference =>
            reference.SymbolName == "vec"
            && reference.ContainerName == "main");
        Assert.Contains(callReferences, reference =>
            reference.SymbolName == "format"
            && reference.ContainerName == "main");
        Assert.Contains(callReferences, reference =>
            reference.SymbolName == "my_macro"
            && reference.ContainerName == "main");
        Assert.Contains(callReferences, reference =>
            reference.SymbolName == "dbg"
            && reference.ContainerName == "main");
        Assert.Contains(callReferences, reference =>
            reference.SymbolName == "helper"
            && reference.ContainerName == "main");
        Assert.DoesNotContain(callReferences, reference => reference.SymbolName == "macro_rules");
    }

    [Fact]
    public void Extract_RustMacroCalls_CaptureRawIdentifierNames()
    {
        const string content = """
            fn main() {
                r#type!();
                crate::r#type!();
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "rust", content);
        var references = ReferenceExtractor.Extract(1, "rust", content, symbols);

        var callReferences = references.Where(reference => reference.ReferenceKind == "call").ToList();
        Assert.Equal(2, callReferences.Count);
        Assert.Contains(callReferences, reference =>
            reference.SymbolName == "type"
            && reference.ContainerName == "main");
        Assert.Contains(callReferences, reference =>
            reference.SymbolName == "crate::type"
            && reference.ContainerName == "main");
    }

    [Fact]
    public void Extract_SwiftAttributeWithArgs_ClassifiedAsAnnotation()
    {
        // issue #293 follow-up: Swift `@available(...)` / `@objc` / `@MainActor` are
        // compile-time metadata, not runtime calls. Before the fix they were recorded
        // as `call` references (polluting `callers`/`callees`/`hotspots`/`impact`) and
        // `@objc` / `@MainActor` no-arg attributes dropped entirely from the index.
        // After the fix they must all classify as `annotation`.
        // issue #293 補足: Swift の `@available(...)` / `@objc` / `@MainActor` は compile-time
        // metadata であり runtime の call ではない。修正前は `call` として記録され
        // (`callers`/`callees`/`hotspots`/`impact` が汚染)、`@objc` / `@MainActor` の no-arg
        // 版はインデックスから完全に脱落していた。修正後はすべて `annotation` として分類される。
        const string content = """
            import Foundation

            @available(iOS 13.0, *)
            class NetworkClient {
                @objc func fetch() {}

                @MainActor
                func process() {}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "swift", content);
        var references = ReferenceExtractor.Extract(1, "swift", content, symbols);

        var available = Assert.Single(references.Where(r => r.SymbolName == "available"));
        Assert.Equal("annotation", available.ReferenceKind);

        var objc = Assert.Single(references.Where(r => r.SymbolName == "objc"));
        Assert.Equal("annotation", objc.ReferenceKind);

        var mainActor = Assert.Single(references.Where(r => r.SymbolName == "MainActor"));
        Assert.Equal("annotation", mainActor.ReferenceKind);
    }

    [Fact]
    public void Extract_SwiftTrailingClosureCallSites_AreReferenced()
    {
        // issue #265: Swift trailing-closure call sites do not end with `(`, so the
        // reference extractor must still index them as `call` edges.
        // issue #265: Swift の trailing-closure 呼び出しは末尾に `(` を持たないため、
        // それでも `call` edge として index されること。
        const string content = """
            class Base {}

            class Derived: Base {}

            func run() {
                let items = [1, 2, 3]
                items.forEach { }
                items.filter { $0 > 0 }
                animate { } completion: { }
            }

            func animate(animations: () -> Void, completion: () -> Void) {}
            """;

        var symbols = SymbolExtractor.Extract(1, "swift", content);
        var references = ReferenceExtractor.Extract(1, "swift", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "forEach"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "run");
        Assert.Contains(references, reference =>
            reference.SymbolName == "filter"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "run");
        Assert.Contains(references, reference =>
            reference.SymbolName == "animate"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "run");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "Base"
            && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_SwiftPropertyObserverCalls_AreAttributedToProperty()
    {
        const string content = """
            class C {
                var x: Int = 0 {
                    didSet { print(x) }
                    @willSet { precondition(newValue >= 0) }
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "swift", content);
        var references = ReferenceExtractor.Extract(1, "swift", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "print"
            && reference.ReferenceKind == "call"
            && reference.ContainerKind == "property"
            && reference.ContainerName == "x");
        Assert.Contains(references, reference =>
            reference.SymbolName == "precondition"
            && reference.ReferenceKind == "call"
            && reference.ContainerKind == "property"
            && reference.ContainerName == "x");
    }

    [Fact]
    public void Extract_SwiftTypedThrows_RecordsThrownErrorType()
    {
        const string content = """
            struct NetworkError: Error {}

            func load() throws(NetworkError) -> Data {
                Data()
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "swift", content);
        var references = ReferenceExtractor.Extract(1, "swift", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "NetworkError"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "load");
    }

    [Fact]
    public void Extract_SwiftTypealiasRhs_RecordsReferencedTypes()
    {
        const string content = """
            struct Request {}
            struct Response {}
            struct Failure {}

            typealias Loader = (Request) -> Response
            typealias LoadResult = Result<Response, Failure>
            """;

        var symbols = SymbolExtractor.Extract(1, "swift", content);
        var references = ReferenceExtractor.Extract(1, "swift", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "Request"
            && reference.ReferenceKind == "type_reference");
        Assert.Contains(references, reference =>
            reference.SymbolName == "Response"
            && reference.ReferenceKind == "type_reference");
        Assert.Contains(references, reference =>
            reference.SymbolName == "Failure"
            && reference.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_SwiftAssociatedTypeConstraintsAndDefaults_RecordsReferencedTypes()
    {
        const string content = """
            protocol Identifiable {}
            struct MemoryCache<T> {}
            struct NetworkLoader {}

            protocol Store {
                associatedtype Item: Identifiable
                associatedtype Cache = MemoryCache<Item>
                associatedtype Loader: AsyncSequence = NetworkLoader
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "swift", content);
        var references = ReferenceExtractor.Extract(1, "swift", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "Identifiable"
            && reference.ReferenceKind == "type_reference");
        Assert.Contains(references, reference =>
            reference.SymbolName == "MemoryCache"
            && reference.ReferenceKind == "type_reference");
        Assert.Contains(references, reference =>
            reference.SymbolName == "NetworkLoader"
            && reference.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_SwiftTypealiasHeritage_EmitsUnderlyingTypeReference()
    {
        const string content = """
            class SomeType {}
            typealias MyAlias = SomeType
            class Derived: MyAlias {}
            """;

        var symbols = SymbolExtractor.Extract(1, "swift", content);
        var references = ReferenceExtractor.Extract(1, "swift", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "MyAlias"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "Derived");
        Assert.Contains(references, reference =>
            reference.SymbolName == "SomeType"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "Derived"
            && reference.Context == "class Derived: MyAlias {}");
    }

    [Fact]
    public void Extract_SwiftTypealiasMixedValueUse_OnlyExpandsTypePositionOccurrence()
    {
        const string content = """
            class SomeType {}
            typealias MyAlias = SomeType
            func get(_ value: Any) -> Any { value }
            let x: MyAlias = get("MyAlias")
            """;

        var symbols = SymbolExtractor.Extract(1, "swift", content);
        var references = ReferenceExtractor.Extract(1, "swift", content, symbols);

        var expanded = references
            .Where(reference =>
                reference.SymbolName == "SomeType"
                && reference.ReferenceKind == "type_reference"
                && reference.Context == "let x: MyAlias = get(\"MyAlias\")")
            .ToList();

        Assert.Single(expanded);
        Assert.Equal(8, expanded[0].Column);
    }

    [Fact]
    public void Extract_SwiftGenericTypealiasHeritage_DoesNotEmitTypeParameterAsTarget()
    {
        const string content = """
            class SomeType {}
            class Box<T> {}
            class Arg {}
            typealias MyAlias<T> = SomeType & Box<T>
            class Derived: MyAlias<Arg> {}
            """;

        var symbols = SymbolExtractor.Extract(1, "swift", content);
        var references = ReferenceExtractor.Extract(1, "swift", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "SomeType"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "Derived"
            && reference.Context == "class Derived: MyAlias<Arg> {}");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "T"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "Derived"
            && reference.Context == "class Derived: MyAlias<Arg> {}");
    }

    [Fact]
    public void Extract_SwiftTypealiasShadowedByScope_UsesActiveAliasBinding()
    {
        const string content = """
            class One {}
            class Two {}
            typealias MyAlias = One
            enum Inner {
                typealias MyAlias = Two
                class B: MyAlias {}
            }
            class A: MyAlias {}
            """;

        var symbols = SymbolExtractor.Extract(1, "swift", content);
        var references = ReferenceExtractor.Extract(1, "swift", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "One"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "A"
            && reference.Context == "class A: MyAlias {}");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "Two"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "A"
            && reference.Context == "class A: MyAlias {}");
        Assert.Contains(references, reference =>
            reference.SymbolName == "Two"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "B"
            && reference.Context == "class B: MyAlias {}");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "One"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "B"
            && reference.Context == "class B: MyAlias {}");
    }

    [Fact]
    public void Extract_SwiftTypealiasShadowedByTypeDeclaration_DoesNotExpandOuterAlias()
    {
        const string content = """
            class One {}
            typealias MyAlias = One
            enum Inner {
                class MyAlias {}
                class B: MyAlias {}
            }
            class Box<MyAlias>: MyAlias {}
            """;

        var symbols = SymbolExtractor.Extract(1, "swift", content);
        var references = ReferenceExtractor.Extract(1, "swift", content, symbols);

        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "One"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "B"
            && reference.Context == "class B: MyAlias {}");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "One"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "Box"
            && reference.Context == "class Box<MyAlias>: MyAlias {}");
    }

    [Fact]
    public void Extract_SwiftExtensionTargets_RecordsExtendedTypes()
    {
        const string content = """
            struct Repository<Entity> {}
            protocol Persistable {}
            protocol ObservableObject {}
            struct CacheStore {}

            extension Repository where Entity: Persistable {
                func save(_ value: Entity) {}
            }

            extension CacheStore: ObservableObject {}
            """;

        var symbols = SymbolExtractor.Extract(1, "swift", content);
        var references = ReferenceExtractor.Extract(1, "swift", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "Repository"
            && reference.ReferenceKind == "type_reference");
        Assert.Contains(references, reference =>
            reference.SymbolName == "CacheStore"
            && reference.ReferenceKind == "type_reference");
        Assert.Contains(references, reference =>
            reference.SymbolName == "Persistable"
            && reference.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_SwiftFunctionTypeColonPositions_RecordsReturnTypes()
    {
        const string content = """
            struct InputEvent {}
            struct HandlerResult {}
            struct SourceModel {}
            struct MapperOutput {}

            func register(handler: (InputEvent) -> HandlerResult) {}

            func build() {
                let mapper: (SourceModel) -> MapperOutput = makeMapper()
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "swift", content);
        var references = ReferenceExtractor.Extract(1, "swift", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "HandlerResult"
            && reference.ReferenceKind == "type_reference");
        Assert.Contains(references, reference =>
            reference.SymbolName == "MapperOutput"
            && reference.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_SwiftOpaqueAndExistentialTypeModifiers_AreNotTypeReferences()
    {
        const string content = """
            protocol View {}
            protocol Service {}

            func makeView() -> some View {
                fatalError()
            }

            func use(service: any Service) {}
            """;

        var symbols = SymbolExtractor.Extract(1, "swift", content);
        var references = ReferenceExtractor.Extract(1, "swift", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "View"
            && reference.ReferenceKind == "type_reference");
        Assert.Contains(references, reference =>
            reference.SymbolName == "Service"
            && reference.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName is "some" or "any"
            && reference.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_SwiftMetatypeSuffixes_AreNotTypeReferences()
    {
        const string content = """
            struct User {}
            protocol Service {}

            func inspect(userType: User.Type, serviceType: Service.Protocol) {}
            """;

        var symbols = SymbolExtractor.Extract(1, "swift", content);
        var references = ReferenceExtractor.Extract(1, "swift", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "User"
            && reference.ReferenceKind == "type_reference");
        Assert.Contains(references, reference =>
            reference.SymbolName == "Service"
            && reference.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName is "Type" or "Protocol"
            && reference.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_SwiftTupleTypeLabels_AreNotTypeReferences()
    {
        const string content = """
            struct Coordinate {}
            struct SourceModel {}
            struct DestinationModel {}

            func move(point: (x: Coordinate, y: Coordinate), transform: (source: SourceModel) -> DestinationModel) {}
            """;

        var symbols = SymbolExtractor.Extract(1, "swift", content);
        var references = ReferenceExtractor.Extract(1, "swift", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "Coordinate"
            && reference.ReferenceKind == "type_reference");
        Assert.Contains(references, reference =>
            reference.SymbolName == "SourceModel"
            && reference.ReferenceKind == "type_reference");
        Assert.Contains(references, reference =>
            reference.SymbolName == "DestinationModel"
            && reference.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName is "x" or "y" or "source"
            && reference.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_SwiftClosureLiteralSignatures_RecordParameterAndReturnTypes()
    {
        const string content = """
            struct ClosureInput {}
            struct ClosureOutput {}

            func configure() {
                let transform = { (value: ClosureInput) -> ClosureOutput in
                    ClosureOutput()
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "swift", content);
        var references = ReferenceExtractor.Extract(1, "swift", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "ClosureInput"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "configure");
        Assert.Contains(references, reference =>
            reference.SymbolName == "ClosureOutput"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "configure");
    }

    [Fact]
    public void Extract_SwiftWhereSameTypeConstraints_RecordRightHandTypes()
    {
        const string content = """
            struct Repository<Entity> {}
            struct User {}
            struct Response {}

            extension Repository where Entity == User {}

            func decode<T>(_ value: T) where T.Output == Response {}
            """;

        var symbols = SymbolExtractor.Extract(1, "swift", content);
        var references = ReferenceExtractor.Extract(1, "swift", content, symbols);
        var referenceKeys = references
            .Select(reference => $"{reference.SymbolName}:{reference.ReferenceKind}:{reference.Column}")
            .ToList();

        Assert.Contains("User:type_reference:38", referenceKeys);
        Assert.Contains("Response:type_reference:46", referenceKeys);
        Assert.DoesNotContain("User:call:38", referenceKeys);
        Assert.DoesNotContain("Response:call:46", referenceKeys);
    }

    [Fact]
    public void Extract_SwiftTypeExpressionAttributes_AreNotTypeReferences()
    {
        const string content = """
            protocol Codable {}
            struct Box {}

            extension Box: @retroactive Codable {}

            func register(handler: @escaping @Sendable (Box) -> Codable) {}
            """;

        var symbols = SymbolExtractor.Extract(1, "swift", content);
        var references = ReferenceExtractor.Extract(1, "swift", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "Codable"
            && reference.ReferenceKind == "type_reference");
        Assert.Contains(references, reference =>
            reference.SymbolName == "Box"
            && reference.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName is "retroactive" or "escaping" or "Sendable"
            && reference.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_SwiftTypeParameterModifiers_AreNotTypeReferences()
    {
        const string content = """
            struct Model {}
            actor Worker {}

            func update(value: inout Model, worker: isolated Worker) {}
            func consume(value: consuming Model, send valueToSend: sending Model) {}
            func borrow(value: borrowing Model) {}
            """;

        var symbols = SymbolExtractor.Extract(1, "swift", content);
        var references = ReferenceExtractor.Extract(1, "swift", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "Model"
            && reference.ReferenceKind == "type_reference");
        Assert.Contains(references, reference =>
            reference.SymbolName == "Worker"
            && reference.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName is "inout" or "isolated" or "consuming" or "sending" or "borrowing"
            && reference.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_SwiftFunctionTypeEffects_AreNotTypeReferences()
    {
        const string content = """
            struct Input {}
            struct Output {}
            struct Failure: Error {}

            typealias AsyncLoader = (Input) async throws(Failure) -> Output
            typealias RetryingLoader = (Input) rethrows -> Output
            """;

        var symbols = SymbolExtractor.Extract(1, "swift", content);
        var references = ReferenceExtractor.Extract(1, "swift", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "Input"
            && reference.ReferenceKind == "type_reference");
        Assert.Contains(references, reference =>
            reference.SymbolName == "Failure"
            && reference.ReferenceKind == "type_reference");
        Assert.Contains(references, reference =>
            reference.SymbolName == "Output"
            && reference.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName is "async" or "throws" or "rethrows"
            && reference.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_SwiftKeyPathRoots_AreTypeReferences()
    {
        const string content = """
            struct User {}
            struct Order {}

            func configure() {
                let userName = \User.name
                let orderCustomer = \Order.customer.name
                let implicit = \.title
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "swift", content);
        var references = ReferenceExtractor.Extract(1, "swift", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "User"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "configure");
        Assert.Contains(references, reference =>
            reference.SymbolName == "Order"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "configure");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName is "name" or "customer" or "title"
            && reference.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_SwiftMacroGenericArguments_AreTypeReferences()
    {
        const string content = """
            struct User {}
            struct Order {}
            struct Score {}

            func configure() {
                let predicate = #Predicate<User> { $0.isActive }
                let expression = #Expression<Order, Score> { order in Score() }
                if #available(iOS 17, *) {}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "swift", content);
        var references = ReferenceExtractor.Extract(1, "swift", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "User"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "configure");
        Assert.Contains(references, reference =>
            reference.SymbolName == "Order"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "configure");
        Assert.Contains(references, reference =>
            reference.SymbolName == "Score"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "configure");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "available"
            && reference.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_SwiftGenericInvocationArguments_AreTypeReferences()
    {
        const string content = """
            struct User {}
            struct Failure {}
            struct Result<Value, Error> {}
            struct Data {}

            func decode<T>(_ data: Data) -> T {}

            func configure(data: Data) {
                let user = decode<User>(data)
                let result = decode<Result<User, Failure>>(data)
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "swift", content);
        var references = ReferenceExtractor.Extract(1, "swift", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "User"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "configure");
        Assert.Contains(references, reference =>
            reference.SymbolName == "Result"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "configure");
        Assert.Contains(references, reference =>
            reference.SymbolName == "Failure"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "configure");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "T"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "decode"
            && reference.Context == "func decode<T>(_ data: Data) -> T {}"
            && reference.Column == 13);
    }

    [Fact]
    public void Extract_SwiftGenericTrailingClosureArguments_AreTypeReferences()
    {
        const string content = """
            struct User {}
            struct Failure: Error {}
            struct Task<Success, Failure> {}

            func configure() {
                let task = Task<User, Failure> {
                    User()
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "swift", content);
        var references = ReferenceExtractor.Extract(1, "swift", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "User"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "configure");
        Assert.Contains(references, reference =>
            reference.SymbolName == "Failure"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "configure");
    }

    [Fact]
    public void Extract_SwiftGenericStaticMemberExpressions_AreTypeReferences()
    {
        const string content = """
            struct User {}
            struct Failure {}
            enum Result<Value, Error> {
                case success(Value)
            }

            func configure() {
                let value = Result<User, Failure>.success(User())
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "swift", content);
        var references = ReferenceExtractor.Extract(1, "swift", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "Result"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "configure");
        Assert.Contains(references, reference =>
            reference.SymbolName == "User"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "configure");
        Assert.Contains(references, reference =>
            reference.SymbolName == "Failure"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "configure");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "success"
            && reference.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_SwiftCatchPatternRoots_AreTypeReferences()
    {
        const string content = """
            enum NetworkError: Error {
                case timeout
            }

            enum DatabaseError: Error {
                case connectionLost
            }

            func load() throws {}

            func run() {
                do {
                    try load()
                } catch NetworkError.timeout {
                } catch DatabaseError.connectionLost {
                } catch is DatabaseError {
                } catch {
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "swift", content);
        var references = ReferenceExtractor.Extract(1, "swift", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "NetworkError"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "run");
        Assert.Contains(references, reference =>
            reference.SymbolName == "DatabaseError"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "run");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName is "timeout" or "connectionLost" or "is"
            && reference.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_SwiftCollectionShorthandConstructors_RecordElementTypes()
    {
        const string content = """
            struct User {}
            struct Handler {}

            func configure(items: [Int: () -> Void], index: Int) {
                let users = [User]()
                let handlers = [String: Handler]()
                items[index]()
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "swift", content);
        var references = ReferenceExtractor.Extract(1, "swift", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "User"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "configure");
        Assert.Contains(references, reference =>
            reference.SymbolName == "Handler"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "configure");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "index"
            && reference.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_SwiftVariadicGenericRepeatModifier_IsNotTypeReference()
    {
        const string content = """
            struct Element {}
            struct TuplePack<each T> {}

            typealias Pack = TuplePack<repeat each Element>
            """;

        var symbols = SymbolExtractor.Extract(1, "swift", content);
        var references = ReferenceExtractor.Extract(1, "swift", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "Element"
            && reference.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName is "repeat" or "each"
            && reference.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_SwiftSelfMetatypeExpressions_RecordRootTypes()
    {
        const string content = """
            struct User {}
            protocol Service {}

            func configure(user: User) {
                let userType = User.self
                let serviceType = Service.self
                let collectionType = [User].self
                let instanceSelf = user.self
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "swift", content);
        var references = ReferenceExtractor.Extract(1, "swift", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "User"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "configure");
        Assert.Contains(references, reference =>
            reference.SymbolName == "Service"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "configure");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "user"
            && reference.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_SwiftSelectorDirectiveRoots_AreTypeReferences()
    {
        const string content = """
            class ViewController {
                @objc func handleTap(_ sender: Any) {}
                @objc var titleText: String = ""
            }

            func configure() {
                let action = #selector(ViewController.handleTap(_:))
                let getter = #selector(getter: ViewController.titleText)
                let unqualified = #selector(handleTap(_:))
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "swift", content);
        var references = ReferenceExtractor.Extract(1, "swift", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "ViewController"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "configure");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName is "handleTap" or "titleText"
            && reference.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_SwiftKeyPathDirectiveRoots_AreTypeReferences()
    {
        const string content = """
            class Person {
                @objc var name: String = ""
                @objc var address: Address = Address()
            }

            class Address {
                @objc var street: String = ""
            }

            func configure() {
                let name = #keyPath(Person.name)
                let street = #keyPath(Person.address.street)
                let unqualified = #keyPath(name)
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "swift", content);
        var references = ReferenceExtractor.Extract(1, "swift", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "Person"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "configure");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName is "name" or "address" or "street"
            && reference.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_SwiftAttributeGenericArguments_AreTypeReferences()
    {
        const string content = """
            struct UserViewModel {}
            struct Failure {}
            struct Loader<Value, Error> {}

            @propertyWrapper
            struct Relationship<Value> {
                var wrappedValue: Value
            }

            struct Screen {
                @Relationship<UserViewModel> var viewModel
                @Relationship<Loader<UserViewModel, Failure>> var loader
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "swift", content);
        var references = ReferenceExtractor.Extract(1, "swift", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "UserViewModel"
            && reference.ReferenceKind == "type_reference");
        Assert.Contains(references, reference =>
            reference.SymbolName == "Loader"
            && reference.ReferenceKind == "type_reference");
        Assert.Contains(references, reference =>
            reference.SymbolName == "Failure"
            && reference.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "Relationship"
            && reference.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_SwiftMultilineString_DoesNotLeakPhantomCallReferences()
    {
        // Regression for issue #385: call-looking identifiers inside a Swift
        // multi-line string (""".. .""") must not be captured as references.
        // issue #385 回帰: Swift の複数行文字列（"""..."""）の内側にある
        // 呼び出しらしい識別子は参照として抽出してはならない。
        const string content = """"
            import Foundation

            class Demo {
                func m() {
                    let sql = """
                        SELECT * FROM users
                        WHERE id = swiftPhantomCall(42)
                        AND name = anotherSwiftPhantom("bob")
                        """

                    realSwiftCall()
                }

                func realSwiftCall() {}
                func swiftPhantomCall(_ x: Int) -> Int { x }
                func anotherSwiftPhantom(_ s: String) -> String { s }
            }
            """";

        var symbols = SymbolExtractor.Extract(1, "swift", content);
        var references = ReferenceExtractor.Extract(1, "swift", content, symbols);

        Assert.DoesNotContain(references, r => r.SymbolName == "swiftPhantomCall" && r.ReferenceKind == "call");
        Assert.DoesNotContain(references, r => r.SymbolName == "anotherSwiftPhantom" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "realSwiftCall" && r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_SwiftMultilineStringInterpolationHole_KeepsRealCallReference()
    {
        // Regression for issue #385: `\(expr)` interpolation holes inside a Swift
        // multi-line string must preserve real call edges so the reference graph
        // does not silently drop them. Extended `#"""..."""#` forms need matching
        // `\#(expr)` interpolation to open a hole.
        // issue #385 回帰: Swift の複数行文字列内の `\(expr)` ホールは本物の
        // call エッジを残すこと。拡張 `#"""..."""#` では `\#(expr)` が hole を開く。
        const string content = """"
            import Foundation

            class Demo {
                func m(name: String) {
                    let sql = """
                        phantom: swiftPhantomCall(42)
                        real: \(runTask()) trailing
                        nested: \(helper(factory(deepReal())))
                        """
                    let raw = #"""
                        phantom raw: swiftRawPhantom(99)
                        real raw: \#(rawReal()) done
                        """#
                    realSwiftCall()
                }

                func runTask() -> Int { 1 }
                func helper(_ x: Int) -> Int { x }
                func factory(_ x: Int) -> Int { x }
                func deepReal() -> Int { 0 }
                func rawReal() -> Int { 0 }
                func realSwiftCall() {}
                func swiftPhantomCall(_ x: Int) -> Int { x }
                func swiftRawPhantom(_ x: Int) -> Int { x }
            }
            """";

        var symbols = SymbolExtractor.Extract(1, "swift", content);
        var references = ReferenceExtractor.Extract(1, "swift", content, symbols);

        Assert.DoesNotContain(references, r => r.SymbolName == "swiftPhantomCall" && r.ReferenceKind == "call");
        Assert.DoesNotContain(references, r => r.SymbolName == "swiftRawPhantom" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "runTask" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "helper" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "factory" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "deepReal" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "rawReal" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "realSwiftCall" && r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_SwiftMultilineStringInterpolationHole_WithCommentContainingCloseParen_KeepsRealCallReference()
    {
        // Regression for issue #385 follow-up: a `\(expr)` interpolation hole in a
        // Swift multi-line string may contain a block comment or line comment whose
        // body happens to include `)`. The hole scanner must recognize comments
        // first so the `)` inside the comment does not close the hole prematurely.
        // issue #385 続編: Swift の `\(expr)` 補間ホール内のコメント本体に含まれる
        // `)` でホールを早閉じせず、コメント後の本物の call を残すこと。
        const string content = """"
            import Foundation

            class Demo {
                func m() {
                    let sql = """
                        real: \( /* ) */ swiftAfterComment() )
                        line: \( swiftLineBefore() // )
                        ) tail
                        """
                    realSwiftCall()
                }

                func swiftAfterComment() -> Int { 0 }
                func swiftLineBefore() -> Int { 0 }
                func realSwiftCall() {}
            }
            """";

        var symbols = SymbolExtractor.Extract(1, "swift", content);
        var references = ReferenceExtractor.Extract(1, "swift", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "swiftAfterComment" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "swiftLineBefore" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "realSwiftCall" && r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_SwiftMultilineStringNestedTripleInHole_DoesNotLeakPhantomCalls()
    {
        // Regression for issue #992: a nested `"""..."""` (or `#"""..."""#`) literal
        // opened inside a Swift `\(...)` interpolation hole must not leak its body
        // as phantom calls. Both plain and hash-delimited nested forms should be
        // covered by the hole scanner's nested-triple state.
        // issue #992 回帰: Swift の `\(...)` ホール内で開いた nested triple の本文を
        // phantom call として漏らさないこと（plain と hash-delimited の両方）。
        const string content = """"
            import Foundation

            class Demo {
                func m() {
                    let sql = """
                        outer: \( wrap("""
                            swiftNestedPhantom(99)
                            """) )
                        raw: \( wrap(#"""
                            swiftHashNestedPhantom(99)
                            """#) )
                        """
                    realSwiftCall()
                }

                func wrap(_ x: String) -> String { x }
                func realSwiftCall() {}
                func swiftNestedPhantom(_ x: Int) -> Int { x }
                func swiftHashNestedPhantom(_ x: Int) -> Int { x }
            }
            """";

        var symbols = SymbolExtractor.Extract(1, "swift", content);
        var references = ReferenceExtractor.Extract(1, "swift", content, symbols);

        Assert.DoesNotContain(references, r => r.SymbolName == "swiftNestedPhantom" && r.ReferenceKind == "call");
        Assert.DoesNotContain(references, r => r.SymbolName == "swiftHashNestedPhantom" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "wrap" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "realSwiftCall" && r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_SwiftMultilineStringNestedTripleHole_PreservesRealCallReferences()
    {
        // Regression for issue #996: a nested `"""..."""` (or `#"""..."""#`) literal
        // opened inside an outer Swift `\(...)` interpolation hole still has its own
        // `\(...)` (or `\#(...)` etc.) holes. The masker must preserve real call edges
        // inside those inner holes while continuing to mask the rest of the body.
        // issue #996 回帰: Swift の outer `\(...)` 内に開いた nested triple でも
        // それ自身の `\(expr)` / `\#(expr)` ホール内の本物の call を保持する。
        const string content = """"
            import Foundation

            class Demo {
                func m() {
                    let sql = """
                        outer: \( wrap("""
                            inner: \(innerCall())
                            phantom: swiftNestedPhantom(99)
                            """) )
                        raw: \( wrap(#"""
                            inner-raw: \#(rawInnerCall())
                            phantom-raw: swiftRawNestedPhantom(99)
                            """#) )
                        """
                    realSwiftCall()
                }

                func wrap(_ x: String) -> String { x }
                func innerCall() -> Int { 0 }
                func rawInnerCall() -> Int { 0 }
                func realSwiftCall() {}
                func swiftNestedPhantom(_ x: Int) -> Int { x }
                func swiftRawNestedPhantom(_ x: Int) -> Int { x }
            }
            """";

        var symbols = SymbolExtractor.Extract(1, "swift", content);
        var references = ReferenceExtractor.Extract(1, "swift", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "innerCall" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "rawInnerCall" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "wrap" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "realSwiftCall" && r.ReferenceKind == "call");
        Assert.DoesNotContain(references, r => r.SymbolName == "swiftNestedPhantom" && r.ReferenceKind == "call");
        Assert.DoesNotContain(references, r => r.SymbolName == "swiftRawNestedPhantom" && r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_SwiftTripleBody_QuotedSubstringWithCallShape_IsMasked()
    {
        // Regression for issue #998 review claim: a quoted substring inside a Swift
        // `"""..."""` body that contains call-shaped text must not survive into the
        // reference graph.
        // issue #998 のレビュー懸念に対する回帰: Swift の `"""..."""` 本文中の
        // 引用符付き部分文字列に call 形のテキストが含まれていても、reference graph に
        // 漏らさないこと。
        const string content = """"
            import Foundation

            class Demo {
                func m() {
                    let sql = """
                        WHERE message = "swiftQuotedPhantom(42)"
                        AND extra = "anotherSwiftQuotedPhantom('inner')"
                        """
                    realSwiftCall()
                }

                func realSwiftCall() {}
                func swiftQuotedPhantom(_ x: Int) -> Int { x }
                func anotherSwiftQuotedPhantom(_ s: String) -> String { s }
            }
            """";

        var symbols = SymbolExtractor.Extract(1, "swift", content);
        var references = ReferenceExtractor.Extract(1, "swift", content, symbols);

        Assert.DoesNotContain(references, r => r.SymbolName == "swiftQuotedPhantom" && r.ReferenceKind == "call");
        Assert.DoesNotContain(references, r => r.SymbolName == "anotherSwiftQuotedPhantom" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "realSwiftCall" && r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_SwiftThreeLevelDeepNestedTriple_DoesNotLeakPhantomCalls()
    {
        // Regression for codex review #9 finding: same shape for Swift `\(...)` holes.
        // codex review #9 への回帰: Swift `\(...)` ホールの 3 段深い triple 本文。
        const string content = """"
            import Foundation

            class Demo {
                func m() {
                    let sql = """
                        outer: \( wrap("""
                            inner: \( helper("""
                                swiftDeepPhantom(99)
                                """) )
                            """) )
                        """
                    realSwiftCall()
                }

                func wrap(_ x: String) -> String { x }
                func helper(_ x: String) -> String { x }
                func realSwiftCall() {}
                func swiftDeepPhantom(_ x: Int) -> Int { x }
            }
            """";

        var symbols = SymbolExtractor.Extract(1, "swift", content);
        var references = ReferenceExtractor.Extract(1, "swift", content, symbols);

        Assert.DoesNotContain(references, r => r.SymbolName == "swiftDeepPhantom" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "wrap" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "helper" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "realSwiftCall" && r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_SwiftFourLevelDeepNestedTriple_DoesNotLeakPhantomCalls()
    {
        // Regression for issue #1002: a 4th nested triple opened inside the deep
        // Swift body must not unwind the 3-deep frame early, including hash-
        // delimited nested forms.
        // issue #1002 回帰: Swift で deep body 内に開いた 4 段目の triple が
        // 3 段深い frame を早抜けさせず、hash 付き nested 形式も含めて守ること。
        const string content = """"
            import Foundation

            class Demo {
                func m() {
                    let sql = """
                        outer: \( wrap("""
                            inner: \( helper(#"""
                                deep: \( deeper("""
                                    swiftDeepestPhantom(99)
                                """) )
                                swiftAfterDeep4Phantom()
                            """#) )
                        """) )
                    """
                    realSwiftCall()
                }

                func wrap(_ x: String) -> String { x }
                func helper(_ x: String) -> String { x }
                func deeper(_ x: String) -> String { x }
                func swiftAfterDeep4Phantom() -> Int { 0 }
                func swiftDeepestPhantom(_ x: Int) -> Int { x }
                func realSwiftCall() {}
            }
            """";

        var symbols = SymbolExtractor.Extract(1, "swift", content);
        var references = ReferenceExtractor.Extract(1, "swift", content, symbols);

        Assert.DoesNotContain(references, r => r.SymbolName == "swiftDeepestPhantom" && r.ReferenceKind == "call");
        Assert.DoesNotContain(references, r => r.SymbolName == "swiftAfterDeep4Phantom" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "wrap" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "helper" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "realSwiftCall" && r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_SwiftOuterHole_HashDelimitedRawStringIsMasked()
    {
        // Regression for codex review #9 finding: a `#"..."#` extended raw string
        // opened inside a Swift `\(...)` hole must mask its body so call-shaped
        // text cannot leak and so the body's `(` / `)` cannot break the outer
        // hole's paren counting.
        // codex review #9 への回帰: Swift outer hole 内の `#"..."#` raw string が
        // outer hole の paren 数え上げを崩さず、body の phantom 漏れも防ぐこと。
        const string content = """"
            import Foundation

            class Demo {
                func m() {
                    let sql = """
                        outer: \( wrap(#"foo \(swiftRawHolePhantom())"#) )
                        plain: \( other(#"phantom: anotherSwiftRawPhantom('inner')"#) )
                        """
                    realSwiftCall()
                }

                func wrap(_ x: String) -> String { x }
                func other(_ x: String) -> String { x }
                func realSwiftCall() {}
                func swiftRawHolePhantom() -> Int { 0 }
                func anotherSwiftRawPhantom(_ s: String) -> String { s }
            }
            """";

        var symbols = SymbolExtractor.Extract(1, "swift", content);
        var references = ReferenceExtractor.Extract(1, "swift", content, symbols);

        Assert.DoesNotContain(references, r => r.SymbolName == "swiftRawHolePhantom" && r.ReferenceKind == "call");
        Assert.DoesNotContain(references, r => r.SymbolName == "anotherSwiftRawPhantom" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "wrap" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "other" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "realSwiftCall" && r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_SwiftSingleLineRawString_HashCountedInterpolationHole_PreservesRealCalls()
    {
        // Regression for issue #1001: a single-line `#"..."#` Swift extended raw
        // string (whether at the source root or inside a `\(...)` outer hole) must
        // preserve any matching `\#(...)` interpolation hole bodies so real call
        // edges inside the raw string still reach the reference graph. Mismatched
        // backslash forms (e.g. `\(...)` inside a hash-1 raw string) stay literal
        // text and are masked along with the rest of the body.
        // issue #1001 回帰: 単行 `#"..."#` raw 文字列内の hash 数一致 `\#(...)` ホール本文は
        // 残し、本物の call を reference graph に届けること。
        const string content = """"
            import Foundation

            class Demo {
                func m() {
                    let outer = """
                        in-hole-real:    \( wrap(#"foo \#(realInHoleCall()) bar"#) )
                        in-hole-literal: \( other(#"baz \(notARealCall()) end"#) )
                        """
                    let topLevel = #"prefix \#(realTopLevelCall()) suffix"#
                    realSwiftCall()
                }

                func wrap(_ x: String) -> String { x }
                func other(_ x: String) -> String { x }
                func realInHoleCall() -> Int { 0 }
                func realTopLevelCall() -> Int { 0 }
                func notARealCall() -> Int { 0 }
                func realSwiftCall() {}
            }
            """";

        var symbols = SymbolExtractor.Extract(1, "swift", content);
        var references = ReferenceExtractor.Extract(1, "swift", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "realInHoleCall" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "realTopLevelCall" && r.ReferenceKind == "call");
        Assert.DoesNotContain(references, r => r.SymbolName == "notARealCall" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "wrap" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "other" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "realSwiftCall" && r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_SwiftDeepHashDelimitedTriple_RequiresMatchingHashCloseToExitDeepMask()
    {
        // Regression for issue #1000: when a Swift triple-quoted literal opens
        // 3+ levels deep with a leading hash run (`#"""..."""#` etc.) inside the
        // nested triple's own `\(...)` hole, the deep-mask close path must require
        // the same hash count. Otherwise a stray bare `"""` inside the deep body
        // could exit the deep mask early and let later phantom calls leak.
        // issue #1000 回帰: 3 段以上深い hash-delimited Swift triple は同じ hash 数の
        // 閉じでのみ deep mask を抜け、bare `"""` で早抜けして phantom が漏れないこと。
        const string content = """"
            import Foundation

            class Demo {
                func m() {
                    let sql = """
                        outer: \( wrap("""
                            inner: \( helper(#"""
                                phantom-bare-close: """
                                phantom-call: swiftDeepHashPhantom(99)
                                """#) )
                            """) )
                        """
                    realSwiftCall()
                }

                func wrap(_ x: String) -> String { x }
                func helper(_ x: String) -> String { x }
                func realSwiftCall() {}
                func swiftDeepHashPhantom(_ x: Int) -> Int { x }
            }
            """";

        var symbols = SymbolExtractor.Extract(1, "swift", content);
        var references = ReferenceExtractor.Extract(1, "swift", content, symbols);

        Assert.DoesNotContain(references, r => r.SymbolName == "swiftDeepHashPhantom" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "wrap" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "helper" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "realSwiftCall" && r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_SwiftTypedDeclarations_CaptureStructuralTypeReferences()
    {
        const string content = """
            class Service<T: Entity>: BaseService, Runnable where T: Auditable {
                var current: User

                func load(_ input: User, options: LoadOptions) -> Result<User> {
                    return make(input)
                }

                func cast(_ value: Any) -> User? {
                    return value as? User
                }

                func check(_ value: Any) -> Bool {
                    return value is User && value.isReady
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "swift", content);
        var references = ReferenceExtractor.Extract(1, "swift", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "Entity" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "BaseService" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Runnable" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Auditable" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "User" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "LoadOptions" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Result" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "Any" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "isReady" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_RustTypedDeclarations_CaptureStructuralTypeReferences()
    {
        const string content = """
            trait Handler: Sendable {
                fn handle(&self, input: Request) -> Result<Response, Error>;
            }

            struct Service<T: Store> where T: Clone {
                repo: Repository,
                current: Option<User>,
            }

            impl Handler for Service<StoreImpl> {
                fn handle(&self, input: Request) -> Result<Response, Error> {
                    let current: Option<User> = None;
                    let repo: Repository = make_repo();
                    finish(current, repo)
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "rust", content);
        var references = ReferenceExtractor.Extract(1, "rust", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "Sendable" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Request" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Result" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Response" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Error" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Store" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Clone" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Repository" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Option" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "User" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Handler" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Service" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "StoreImpl" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "self" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_RustAssociatedTypeDefaults_CaptureBoundAndDefaultTypeReferences()
    {
        const string content = """
            trait Builder {
                type Output = ();
                type Error: std::error::Error = String;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "rust", content);
        var references = ReferenceExtractor.Extract(1, "rust", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "Error" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "String" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_RustConstGenericDeclarations_CaptureConstGenericReferences()
    {
        const string content = """
            struct Array<const N: usize> {
                values: [i32; N],
            }

            fn process<const N: usize>(arr: [i32; N]) where const N: usize {
                consume(arr);
            }

            impl<const N: usize> Array<N> {
                fn len(&self) -> usize { N }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "rust", content);
        var references = ReferenceExtractor.Extract(1, "rust", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "N" && r.ReferenceKind == "const_generic_reference");
        Assert.Contains(references, r => r.SymbolName == "usize" && r.ReferenceKind == "annotation");
        Assert.Contains(references, r => r.SymbolName == "Array" && r.ReferenceKind == "type_reference");
        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "Array");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "process");
    }

    [Fact]
    public void Extract_RustRawIdentifierTypeReferences_NormalizesNames()
    {
        const string content = """
            struct r#type;
            struct r#async;

            struct Wrapper {
                value: crate::r#type,
                next: Option<r#async>,
                keyword: r#struct,
            }

            fn build(input: r#type) -> r#async {
                todo!()
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "rust", content);
        var references = ReferenceExtractor.Extract(1, "rust", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "type" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "async" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "struct" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "r" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_RustConstStaticItems_CaptureTypeReferences()
    {
        const string content = """
            const GLOBAL: Arc<User> = Arc::new(User);
            static mut STATE: Option<State> = None;
            pub static CACHE: crate::cache::Cache = crate::cache::Cache::new();
            """;

        var symbols = SymbolExtractor.Extract(1, "rust", content);
        var references = ReferenceExtractor.Extract(1, "rust", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "Arc" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "User" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Option" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "State" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Cache" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_RustTypeAliases_CaptureTargetTypeReferences()
    {
        const string content = """
            type UserMap<K: Key> = std::collections::HashMap<K, User>;
            pub type Callback = Handler<Request, Response>;
            """;

        var symbols = SymbolExtractor.Extract(1, "rust", content);
        var references = ReferenceExtractor.Extract(1, "rust", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "Key" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "HashMap" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "User" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Handler" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Request" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Response" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_RustAssociatedTypes_CaptureBoundTypeReferences()
    {
        const string content = """
            trait Stream {
                type Item: Display + Debug;
                type Error: Into<AppError> = IoError;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "rust", content);
        var references = ReferenceExtractor.Extract(1, "rust", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "Display" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Debug" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Into" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "AppError" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "IoError" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_RustStructFieldTypes_CaptureStructContainerReferences()
    {
        const string content = """
            struct FieldOnly {
                repo: Repository,
            }

            struct UserId(Uuid);
            pub struct Pair(pub User, Repository);
            """;

        var symbols = SymbolExtractor.Extract(1, "rust", content);
        var references = ReferenceExtractor.Extract(1, "rust", content, symbols);

        Assert.Contains(references, r =>
            r.SymbolName == "Repository"
            && r.ReferenceKind == "type_reference"
            && r.ContainerKind == "struct"
            && r.ContainerName == "FieldOnly");
        Assert.Contains(references, r => r.SymbolName == "Uuid" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "User" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Repository" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_RustEnumVariantPayloads_CaptureTypeReferences()
    {
        const string content = """
            enum Event {
                Created(User),
                Moved { from: Point, to: Point },
                Failed(crate::errors::Error),
                Empty,
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "rust", content);
        var references = ReferenceExtractor.Extract(1, "rust", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "User" && r.ReferenceKind == "type_reference" && r.ContainerName == "Event");
        Assert.Contains(references, r => r.SymbolName == "Point" && r.ReferenceKind == "type_reference" && r.ContainerName == "Event");
        Assert.Contains(references, r => r.SymbolName == "Error" && r.ReferenceKind == "type_reference" && r.ContainerName == "Event");
    }

    [Fact]
    public void Extract_RustDeriveAttributes_CaptureTraitTypeReferences()
    {
        const string content = """
            #[derive(Debug, Clone, serde::Serialize)]
            struct User;
            """;

        var symbols = SymbolExtractor.Extract(1, "rust", content);
        var references = ReferenceExtractor.Extract(1, "rust", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "Debug" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Clone" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Serialize" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "derive" && r.ReferenceKind == "call");
        Assert.DoesNotContain(references, r => r.SymbolName == "derive" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_RustCfgAttrDeriveAttributes_CaptureTraitTypeReferences()
    {
        const string content = """
            #[cfg_attr(feature = "serde", derive(Serialize, Deserialize))]
            struct User;
            """;

        var symbols = SymbolExtractor.Extract(1, "rust", content);
        var references = ReferenceExtractor.Extract(1, "rust", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "Serialize" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Deserialize" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "cfg_attr" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_RustMultilineCfgAttrDeriveAttributes_CaptureTraitTypeReferences()
    {
        const string content = """
            #[cfg_attr(
                all(test, not(miri)),
                derive(
                    Debug,
                    Clone,
                    serde::Serialize
                )
            )]
            struct User;
            """;

        var symbols = SymbolExtractor.Extract(1, "rust", content);
        var references = ReferenceExtractor.Extract(1, "rust", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "Debug" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Clone" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Serialize" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Debug" && r.Line == 4 && r.Column == 9);
        Assert.Contains(references, r => r.SymbolName == "Clone" && r.Line == 5 && r.Column == 9);
        Assert.Contains(references, r => r.SymbolName == "Serialize" && r.Line == 6 && r.Column == 16);
        Assert.DoesNotContain(references, r => r.SymbolName == "cfg_attr" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_RustAttributes_CaptureAnnotationReferences()
    {
        const string content = """
            #[tokio::test]
            async fn verifies_user() {}

            #[serde(rename = "id")]
            struct User;
            """;

        var symbols = SymbolExtractor.Extract(1, "rust", content);
        var references = ReferenceExtractor.Extract(1, "rust", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "tokio::test" && r.ReferenceKind == "annotation");
        Assert.Contains(references, r => r.SymbolName == "serde" && r.ReferenceKind == "annotation");
    }

    [Fact]
    public void Extract_RustTypeModifiers_DoNotBecomeTypeReferences()
    {
        const string content = """
            fn make() -> impl Future<Output = User> {
                todo!()
            }

            struct Service {
                handler: Box<dyn Handler + Send>,
                raw: *const Marker,
                mutable: *mut State,
                text: &'static str,
                dynamic: Box<dyn Iterator<Item = User> + Send + 'static>,
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "rust", content);
        var references = ReferenceExtractor.Extract(1, "rust", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "Future" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Output" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "User" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Box" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Handler" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Iterator" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Item" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Send" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Marker" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "State" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "'static" && r.ReferenceKind == "lifetime_reference");
        Assert.DoesNotContain(
            references,
            r => r.ReferenceKind == "type_reference"
                && r.SymbolName is "impl" or "dyn" or "const" or "mut" or "ref" or "static");
    }

    [Fact]
    public void Extract_RustMutableReferenceTypes_CaptureReferencedType()
    {
        const string content = """
            fn demo(buffer: &mut Buffer) {
                let next: &mut crate::io::Cursor = todo!();
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "rust", content);
        var references = ReferenceExtractor.Extract(1, "rust", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "Buffer" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Cursor" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "mut" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_RustMutableDynAndImplReferences_CaptureTraitType()
    {
        const string content = """
            fn demo(writer: &mut dyn Write, parser: &mut impl Parser) {
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "rust", content);
        var references = ReferenceExtractor.Extract(1, "rust", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "Write" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Parser" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(
            references,
            r => r.ReferenceKind == "type_reference" && (r.SymbolName is "dyn" or "impl" or "mut"));
    }

    [Fact]
    public void Extract_RustMutableBorrowExpression_DoesNotEmitTypeReference()
    {
        const string content = """
            fn demo(buffer: &mut Buffer) {
                take(&mut buffer);
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "rust", content);
        var references = ReferenceExtractor.Extract(1, "rust", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "Buffer" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "buffer" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_RustLifetimeParameters_CaptureExplicitLifetimeReferences()
    {
        const string content = """
            trait Borrower<'a> {
                fn borrow<'b, T: Trait<'b>>(input: &'a T) -> &'b dyn Iterator<Item = T>
                where
                    T: for<'c> Parser<'c> + 'static;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "rust", content);
        var references = ReferenceExtractor.Extract(1, "rust", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "'a" && r.ReferenceKind == "lifetime_reference");
        Assert.Contains(references, r => r.SymbolName == "'b" && r.ReferenceKind == "lifetime_reference");
        Assert.Contains(references, r => r.SymbolName == "'c" && r.ReferenceKind == "lifetime_reference");
        Assert.Contains(references, r => r.SymbolName == "'static" && r.ReferenceKind == "lifetime_reference");
        Assert.Contains(references, r => r.SymbolName == "Trait" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Iterator" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Item" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Parser" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_RustLifetimes_DoNotBecomeTypeReferences()
    {
        const string content = """
            struct Holder<'a> {
                value: &'a User,
                fallback: &'static User,
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "rust", content);
        var references = ReferenceExtractor.Extract(1, "rust", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "User" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "a" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "static" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_RustAsCasts_CaptureTargetTypeReferences()
    {
        const string content = """
            use crate::models::User as UserAlias;

            fn convert(raw: *const u8, input: Value) {
                let user = input as User;
                let marker = raw as *const Marker;
                let handler = input as Box<dyn Handler>;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "rust", content);
        var references = ReferenceExtractor.Extract(1, "rust", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "User" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Marker" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Box" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Handler" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "as" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "UserAlias" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_RustExternCrate_CapturesCrateReferences()
    {
        const string content = """
            extern crate serde;
            pub extern crate r#async as async_crate;
            """;

        var symbols = SymbolExtractor.Extract(1, "rust", content);
        var references = ReferenceExtractor.Extract(1, "rust", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "serde" && r.ReferenceKind == "reference");
        Assert.Contains(references, r => r.SymbolName == "async" && r.ReferenceKind == "reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "async_crate" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_RustModDeclarations_CaptureModuleReferences()
    {
        const string content = """
            mod users;
            pub(crate) mod r#async;
            mod inline {
                fn helper() {}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "rust", content);
        var references = ReferenceExtractor.Extract(1, "rust", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "users" && r.ReferenceKind == "reference");
        Assert.Contains(references, r => r.SymbolName == "async" && r.ReferenceKind == "reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "inline" && r.ReferenceKind == "reference");
    }

    [Fact]
    public void Extract_RustUseStatements_CaptureImportTargetReferences()
    {
        const string content = """
            use crate::models::User;
            use crate::services::{Repository, Store as StoreAlias};
            use crate::prelude::{self, Widget};
            pub use r#async::Handler;
            """;

        var symbols = SymbolExtractor.Extract(1, "rust", content);
        var references = ReferenceExtractor.Extract(1, "rust", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "User" && r.ReferenceKind == "reference");
        Assert.Contains(references, r => r.SymbolName == "Repository" && r.ReferenceKind == "reference");
        Assert.Contains(references, r => r.SymbolName == "Store" && r.ReferenceKind == "reference");
        Assert.Contains(references, r => r.SymbolName == "prelude" && r.ReferenceKind == "reference");
        Assert.Contains(references, r => r.SymbolName == "Widget" && r.ReferenceKind == "reference");
        Assert.Contains(references, r => r.SymbolName == "Handler" && r.ReferenceKind == "reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "StoreAlias" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_RustAssociatedCalls_CaptureReceiverTypeReferences()
    {
        const string content = """
            fn build() {
                let user = User::new();
                let store = crate::models::Store::open();
                let users = Vec::<User>::new();
                let helper = crate::helpers::build();
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "rust", content);
        var references = ReferenceExtractor.Extract(1, "rust", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "User" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Store" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Vec" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "helpers" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_RustStructLiterals_CaptureInstantiationReferences()
    {
        const string content = """
            struct Config {
                enabled: bool,
            }

            pub(crate) struct Local {
                enabled: bool,
            }

            fn build() {
                let user = User { id: 1 };
                let store = crate::models::Store { ready: true };
                let wrapped = Wrapper::<User> { value: user };
                let helper = crate::helpers::state { ready: true };
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "rust", content);
        var references = ReferenceExtractor.Extract(1, "rust", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "User" && r.ReferenceKind == "instantiate");
        Assert.Contains(references, r => r.SymbolName == "Store" && r.ReferenceKind == "instantiate");
        Assert.Contains(references, r => r.SymbolName == "Wrapper" && r.ReferenceKind == "instantiate");
        Assert.DoesNotContain(references, r => r.SymbolName == "Config" && r.ReferenceKind == "instantiate");
        Assert.DoesNotContain(references, r => r.SymbolName == "Local" && r.ReferenceKind == "instantiate");
        Assert.DoesNotContain(references, r => r.SymbolName == "state" && r.ReferenceKind == "instantiate");
    }

    [Fact]
    public void Extract_RustTupleConstructors_CaptureInstantiationReferences()
    {
        const string content = """
            fn build(value: Value) {
                let user = User(value);
                let maybe = Some(user);
                let result = Ok(maybe);
                helper(result);
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "rust", content);
        var references = ReferenceExtractor.Extract(1, "rust", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "User" && r.ReferenceKind == "instantiate");
        Assert.Contains(references, r => r.SymbolName == "Some" && r.ReferenceKind == "instantiate");
        Assert.Contains(references, r => r.SymbolName == "Ok" && r.ReferenceKind == "instantiate");
        Assert.Contains(references, r => r.SymbolName == "helper" && r.ReferenceKind == "call");
        Assert.DoesNotContain(references, r => r.SymbolName == "User" && r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_RustGenericDefaults_CaptureDefaultTypeReferences()
    {
        const string content = """
            struct Cache<T = User, E: Error = IoError> {
                value: T,
                error: E,
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "rust", content);
        var references = ReferenceExtractor.Extract(1, "rust", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "User" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Error" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "IoError" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_RustHigherRankedTraitBounds_PreserveBoundTypes()
    {
        const string content = """
            trait Handler<F: for<'a> Fn(&'a User)> {
                fn handle(&self, f: F);
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "rust", content);
        var references = ReferenceExtractor.Extract(1, "rust", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "Fn" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "User" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "for" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "a" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_RustClosureSignatureTypes_CapturesParameterAndReturnTypes()
    {
        const string content = """
            fn configure() {
                let handler = |input: User, ctx: &Context| -> Result<Response, Error> {
                    build(input, ctx)
                };
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "rust", content);
        var references = ReferenceExtractor.Extract(1, "rust", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "User" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Context" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Result" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Response" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Error" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_RustTraitAliasTargetTypes_CapturesAliasedBounds()
    {
        const string content = """
            trait Service = Send + Sync + Handler<User>;
            """;

        var symbols = SymbolExtractor.Extract(1, "rust", content);
        var references = ReferenceExtractor.Extract(1, "rust", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "Send" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Sync" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Handler" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "User" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_RustAssociatedTypeBinding_CapturesBindingKey()
    {
        const string content = """
            fn make() -> impl Future<Output = User> {
                todo!()
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "rust", content);
        var references = ReferenceExtractor.Extract(1, "rust", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "Future" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Output" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "User" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_RustSelfAssociatedCalls_DoNotEmitSelfTypeReference()
    {
        const string content = """
            struct User;

            impl User {
                fn make() -> Self {
                    Self::new();
                    User::new();
                    Self {}
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "rust", content);
        var references = ReferenceExtractor.Extract(1, "rust", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "User" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "Self" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "Self" && r.ReferenceKind == "instantiate");
    }

    [Fact]
    public void Extract_RustQualifiedAssociatedCalls_CapturesReceiverAndTraitTypes()
    {
        const string content = """
            fn run() {
                <User as Service>::handle();
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "rust", content);
        var references = ReferenceExtractor.Extract(1, "rust", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "User" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Service" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_RustAssociatedValues_CapturesReceiverAndTurbofishTypes()
    {
        const string content = """
            fn defaults() {
                let _ = User::DEFAULT;
                let _ = Result::<User, Error>::Ok;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "rust", content);
        var references = ReferenceExtractor.Extract(1, "rust", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "User" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Result" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Error" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "DEFAULT" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "Ok" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_RustGlobImports_CaptureParentModuleReference()
    {
        const string content = """
            use crate::prelude::*;
            """;

        var symbols = SymbolExtractor.Extract(1, "rust", content);
        var references = ReferenceExtractor.Extract(1, "rust", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "prelude" && r.ReferenceKind == "reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "*" && r.ReferenceKind == "reference");
    }

    [Fact]
    public void Extract_RustFunctionTraitBounds_CapturesReturnTypes()
    {
        const string content = """
            fn call<F: FnOnce() -> Result<User, Error>>(f: F) {}
            fn where_call<F>(f: F) where F: FnOnce() -> Response {}
            """;

        var symbols = SymbolExtractor.Extract(1, "rust", content);
        var references = ReferenceExtractor.Extract(1, "rust", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "FnOnce" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Result" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "User" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Error" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Response" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_RustTraitSuperFunctionBounds_CapturesReturnTypes()
    {
        const string content = """
            trait Handler: FnOnce() -> User {}
            """;

        var symbols = SymbolExtractor.Extract(1, "rust", content);
        var references = ReferenceExtractor.Extract(1, "rust", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "FnOnce" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "User" && r.ReferenceKind == "type_reference");
    }
}
