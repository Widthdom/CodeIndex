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
    public void Extract_Swift_DetectsRepresentativeDeclarationKinds()
    {
        const string content = """
            import Foundation
            import SwiftUI

            public protocol StoreProtocol {
                associatedtype Element
            }

            @MainActor
            public final class UserStore {
                public var currentUser: User?
                @State private var count = 0
                @Environment(\.colorScheme) var scheme
                @MyWrapper var value: Int
                @MyLib.State var qualifiedCount = 0
                @IBOutlet weak var titleLabel: UILabel!
                @NSManaged var persistedName: String
                @objc var exposedName: String = ""
                var implicitName: String {
                    model.set(value)
                }
                var fullName: String {
                    get {
                        "A"
                    }
                    set {
                        _ = newValue
                    }
                }

                public init() {}

                public func loadUser() -> User {
                    fetchUser()
                }

                deinit {}

                subscript(index: Int) -> User {
                    currentUser!
                }
            }

            struct User {}

            enum Status {
                case active, disabled = "disabled"
            }

            typealias UserIdentifier = String
            macro stringify<T>(_ value: T) = #externalMacro(module: "Macros", type: "StringifyMacro")
            precedencegroup PipelinePrecedence {}
            infix operator |>: PipelinePrecedence
            extension Array where Element == User {}
            """;

        var symbols = SymbolExtractor.Extract(1, "swift", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Foundation");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "SwiftUI");
        Assert.Contains(symbols, s => s.Kind == "protocol" && s.Name == "StoreProtocol");
        Assert.Contains(symbols, s => s.Kind == "associatedtype" && s.Name == "Element");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "UserStore");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "currentUser" && s.ContainerName == "UserStore");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "count" && s.SubKind == "swift_wrapped_property");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "$count" && s.SubKind == "swift_projected_value");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "scheme" && s.SubKind == "swift_wrapped_property");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "$scheme" && s.SubKind == "swift_projected_value");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "value" && s.SubKind == "swift_wrapped_property");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "$value" && s.SubKind == "swift_projected_value");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "qualifiedCount" && s.SubKind == "swift_wrapped_property");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "$qualifiedCount" && s.SubKind == "swift_projected_value");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "titleLabel" && s.SubKind != "swift_wrapped_property");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "persistedName" && s.SubKind != "swift_wrapped_property");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "exposedName" && s.SubKind != "swift_wrapped_property");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name is "$titleLabel" or "$persistedName" or "$exposedName");
        Assert.DoesNotContain(symbols, s => s.Kind == "accessor" && s.Name == "implicitName.set");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "fullName" && s.SubKind == "swift_computed_property");
        Assert.Contains(symbols, s => s.Kind == "accessor" && s.Name == "fullName.get" && s.ContainerName == "fullName");
        Assert.Contains(symbols, s => s.Kind == "accessor" && s.Name == "fullName.set" && s.ContainerName == "fullName");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "init" && s.ContainerName == "UserStore");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "loadUser" && s.ContainerName == "UserStore");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "deinit" && s.ContainerName == "UserStore");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "subscript" && s.ContainerName == "UserStore");
        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "User");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Status");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "active" && s.ContainerName == "Status");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "disabled" && s.ContainerName == "Status");
        Assert.Contains(symbols, s => s.Kind == "typealias" && s.Name == "UserIdentifier");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "stringify");
        Assert.Contains(symbols, s => s.Kind == "interface" && s.Name == "PipelinePrecedence");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "|>");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name.StartsWith("Array", StringComparison.Ordinal));
        Assert.DoesNotContain(symbols, s => s.Name == "fetchUser" && s.Kind == "function");
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
    public void Extract_Swift_DetectsAttributedDeclarations()
    {
        var content = """
            @available(*, deprecated) public struct LegacyCache {}
            @discardableResult public func load() -> Int { 1 }
            @available(*, deprecated) public typealias LegacyHandler = Int
            """;
        var symbols = SymbolExtractor.Extract(1, "swift", content);

        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "LegacyCache");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "load");
        Assert.Contains(symbols, s => s.Kind == "typealias" && s.Name == "LegacyHandler");
    }

    [Fact]
    public void Extract_Swift_DetectsExtensionsAndEscapedFunctionNames()
    {
        var content = """
            public extension URLSession {
                func `repeat`() {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "swift", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "URLSession");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "`repeat`");
    }

    [Fact]
    public void Extract_Swift_DetectsGenericExtensionTargets()
    {
        var content = """
            extension Array<String> where Element == String {
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "swift", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Array<String>");
    }

    [Fact]
    public void Extract_Swift_DetectsNestedGenericExtensionTargetsWithConformance()
    {
        var content = """
            extension Foundation.Dictionary<String, Array<Int>>: Sendable where Value == Int {
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "swift", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Foundation.Dictionary<String, Array<Int>>");
    }

    [Fact]
    public void Extract_Swift_DetectsInitDeinitSubscriptStoredPropertyAndAssociatedType()
    {
        var content = """
            public extension Foundation.URLSession {
                public convenience init?(configuration: URLSessionConfiguration) {
                }

                deinit {
                }

                subscript(index: Int) -> String {
                    "value"
                }
            }

            public protocol CacheStore {
                associatedtype Key
            }

            public struct UserCache {
                public let capacity: Int
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "swift", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Foundation.URLSession");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "init");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "deinit");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "subscript");
        Assert.Contains(symbols, s => s.Kind == "associatedtype" && s.Name == "Key");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "capacity");
    }

    [Fact]
    public void Extract_Swift_SupportsPackageVisibility()
    {
        var content = """
            package struct SessionCache {
                package func save() { }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "swift", content);

        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "SessionCache");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "save");
    }

    [Fact]
    public void Extract_Swift_DetectsMacroDeclarations()
    {
        var content = """
            public macro stringify<T>(_ value: T) = #externalMacro(module: "MyMacros", type: "StringifyMacro")
            """;
        var symbols = SymbolExtractor.Extract(1, "swift", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "stringify");
    }

    [Fact]
    public void Extract_Swift_PropertyObserversAreChildrenOfProperty()
    {
        var content = """
            class C {
                var x: Int = 0 {
                    didSet { print(x) }
                    @willSet { precondition(newValue >= 0) }
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "swift", content);

        var property = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "x"));
        Assert.Equal("swift_computed_property", property.SubKind);
        Assert.Equal(2, property.BodyStartLine);
        Assert.Equal(5, property.BodyEndLine);

        Assert.Contains(symbols, s =>
            s.Kind == "accessor"
            && s.Name == "x.didSet"
            && s.ContainerKind == "property"
            && s.ContainerName == "x");
        Assert.Contains(symbols, s =>
            s.Kind == "accessor"
            && s.Name == "x.willSet"
            && s.ContainerKind == "property"
            && s.ContainerName == "x");
    }

    [Fact]
    public void Extract_Swift_DetectsOperatorsAndPrecedenceGroup()
    {
        var content = """
            public precedencegroup ForwardApplicationPrecedence {
                associativity: left
            }

            infix operator |> : ForwardApplicationPrecedence
            """;
        var symbols = SymbolExtractor.Extract(1, "swift", content);

        Assert.Contains(symbols, s => s.Kind == "interface" && s.Name == "ForwardApplicationPrecedence");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "|>");
    }
}
