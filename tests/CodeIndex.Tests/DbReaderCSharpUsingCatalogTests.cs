using System.Reflection;
using CodeIndex.Database;

namespace CodeIndex.Tests;

public partial class DbReaderTests
{
    [Fact]
    public void SearchReferences_CSharpUsingScopes_KeepFileScopedEofAndInnermostNestedImports()
    {
        InsertIndexedFile("src/Color.cs", "csharp",
            "namespace Probe;\npublic enum Color { Red }\n");
        InsertIndexedFile("src/FileScoped.cs", "csharp",
            """
            using static Probe.Color;
            namespace RealTypes;
            public sealed class Red { }
            public sealed class FileScopedUse
            {
                public bool Match(object value) => value is Red;
            }
            """);
        InsertIndexedFile("src/Nested.cs", "csharp",
            """
            using static Probe.Color;
            namespace ScopeHost
            {
                namespace Inner
                {
                    using RealTypes;
                    public sealed class NestedUse
                    {
                        public bool Match(object value) => value is Red;
                    }
                }

                public sealed class OutsideUse
                {
                    public bool Match(object value) => value is Red;
                }
            }
            """);

        var results = _reader.SearchReferences(
            "Red",
            limit: 20,
            lang: "csharp",
            referenceKind: "type_reference",
            exact: true);

        Assert.Contains(results, row => row.Path == "src/FileScoped.cs" && row.ContainerName == "Match");
        Assert.Contains(results, row => row.Path == "src/Nested.cs" && row.Line == 9);
        Assert.DoesNotContain(results, row => row.Path == "src/Nested.cs" && row.Line == 15);

        var activeNamespaces = typeof(DbReader).GetMethod(
            "GetActiveCSharpTypeNamespaces",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var first = activeNamespaces.Invoke(_reader, ["src/Nested.cs", 9]);
        var second = activeNamespaces.Invoke(_reader, ["src/Nested.cs", 9]);
        Assert.Same(first, second);
    }

    [Fact]
    public void SearchReferences_CSharpUsingAliases_PreserveShadowedChainsAndTerminateCycles()
    {
        InsertIndexedFile("src/Definitions.cs", "csharp",
            """
            namespace Probe { public enum Color { Red } }
            namespace RealTypes { public sealed class Red { } }
            """);
        InsertIndexedFile("src/GlobalUsings.cs", "csharp",
            """
            global using Root = global::RealTypes;
            global using Chain = Root;
            global using CycleA = CycleB;
            global using CycleB = CycleA;
            """);
        InsertIndexedFile("src/Good.cs", "csharp",
            """
            using static Probe.Color;
            using Red = Chain.Red;
            public sealed class Good
            {
                public bool Match(object value) => value is Red;
            }
            """);
        InsertIndexedFile("src/Shadow.cs", "csharp",
            """
            using static Probe.Color;
            using Chain = MissingTypes;
            using Red = Chain.Red;
            public sealed class Shadow
            {
                public bool Match(object value) => value is Red;
            }
            """);
        InsertIndexedFile("src/Cycle.cs", "csharp",
            """
            using static Probe.Color;
            using Red = CycleA.Red;
            public sealed class Cycle
            {
                public bool Match(object value) => value is Red;
            }
            """);

        var results = _reader.SearchReferences(
            "Red",
            limit: 20,
            lang: "csharp",
            referenceKind: "type_reference",
            exact: true);

        Assert.Contains(results, row => row.Path == "src/Good.cs");
        Assert.DoesNotContain(results, row => row.Path == "src/Shadow.cs");
        Assert.DoesNotContain(results, row => row.Path == "src/Cycle.cs");
    }
}
