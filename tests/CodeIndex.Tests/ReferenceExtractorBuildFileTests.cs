using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public partial class ReferenceExtractorTests
{
    [Fact]
    public void Extract_Solution_IndexesProjectPathReferences_Issue3662()
    {
        var content = """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "src\App\App.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Project("{888888A0-9F3D-457C-B088-3A5042F75D52}") = "PythonApp", "tools\PythonApp\PythonApp.pyproj", "{33333333-3333-3333-3333-333333333333}"
            EndProject
            """.Replace("\n", "\r\n", StringComparison.Ordinal);

        var scenario = ReferenceExtractionCase.Extract("solution", content)
            .ShouldHaveCount(2);

        var reference = scenario.Single("project_reference", "src/App/App.csproj");
        Assert.Equal("project", reference.ContainerKind);
        Assert.Equal("App", reference.ContainerName);
        Assert.Equal(2, reference.Line);
        Assert.True(reference.Column > 0);

        var pythonReference = scenario.Single("project_reference", "tools/PythonApp/PythonApp.pyproj");
        Assert.Equal("project", pythonReference.ContainerKind);
        Assert.Equal("PythonApp", pythonReference.ContainerName);
        Assert.Equal(4, pythonReference.Line);
    }

    [Fact]
    public void Extract_CMake_BuildAutomationReferences()
    {
        const string content = """
            find_package(fmt REQUIRED)
            include(GNUInstallDirs)
            add_library(core src/core.cpp)
            add_executable(app src/main.cpp)
            target_link_libraries(app PRIVATE core fmt::fmt)
            add_dependencies(app generate_assets)
            """;

        ReferenceExtractionCase.Extract("cmake", content)
            .ShouldContain("import", "fmt", line: 1)
            .ShouldContain("import", "GNUInstallDirs", line: 2)
            .ShouldContain("call", "core", containerName: "app")
            .ShouldContain("call", "generate_assets", containerName: "app")
            .ShouldNotContainSymbol("PRIVATE");
    }

    [Fact]
    public void Extract_Justfile_BuildAutomationReferences()
    {
        const string content = """
            import "common.just"

            build:
                cargo build

            test:
                cargo test

            deploy: build test # only dependencies before this comment count
                ./deploy
            """;

        ReferenceExtractionCase.Extract("justfile", content)
            .ShouldContain("import", "common.just", line: 1)
            .ShouldContain("call", "build", containerName: "deploy")
            .ShouldContain("call", "test", containerName: "deploy")
            .ShouldNotContainSymbol("only");
    }

    [Fact]
    public void Extract_MsBuild_BuildAutomationReferences()
    {
        const string content = """
            <Project>
              <Import Project="build/common.props" />
              <ItemGroup>
                <ProjectReference Include="src/App/App.csproj" />
                <PackageReference Include="xunit" Version="2.9.3" />
              </ItemGroup>
              <Target Name="Generate" DependsOnTargets="Restore;Compile" BeforeTargets="Build">
                <CallTarget Targets="Pack,Publish" />
              </Target>
            </Project>
            """;

        ReferenceExtractionCase.Extract("msbuild", content)
            .ShouldContain("import", "build/common.props")
            .ShouldContain("import", "src/App/App.csproj")
            .ShouldContain("import", "xunit")
            .ShouldContain("call", "Restore", containerName: "Generate")
            .ShouldContain("call", "Compile", containerName: "Generate")
            .ShouldContain("call", "Build", containerName: "Generate")
            .ShouldContain("call", "Pack", containerName: "Generate")
            .ShouldContain("call", "Publish", containerName: "Generate");
    }

    private sealed class ReferenceExtractionCase
    {
        private readonly IReadOnlyList<ReferenceRecord> references;

        private ReferenceExtractionCase(IReadOnlyList<ReferenceRecord> references)
        {
            this.references = references;
        }

        public static ReferenceExtractionCase Extract(string language, string content)
        {
            var symbols = SymbolExtractor.Extract(1, language, content);
            return new ReferenceExtractionCase(ReferenceExtractor.Extract(1, language, content, symbols));
        }

        public ReferenceExtractionCase ShouldHaveCount(int expected)
        {
            Assert.Equal(expected, references.Count);
            return this;
        }

        public ReferenceRecord Single(string referenceKind, string symbolName) =>
            Assert.Single(references, reference =>
                reference.ReferenceKind == referenceKind
                && reference.SymbolName == symbolName);

        public ReferenceExtractionCase ShouldContain(
            string referenceKind,
            string symbolName,
            string? containerName = null,
            int? line = null)
        {
            Assert.Contains(references, reference =>
                reference.ReferenceKind == referenceKind
                && reference.SymbolName == symbolName
                && (containerName == null || reference.ContainerName == containerName)
                && (line == null || reference.Line == line.Value));
            return this;
        }

        public ReferenceExtractionCase ShouldNotContainSymbol(string symbolName)
        {
            Assert.DoesNotContain(references, reference => reference.SymbolName == symbolName);
            return this;
        }
    }
}
