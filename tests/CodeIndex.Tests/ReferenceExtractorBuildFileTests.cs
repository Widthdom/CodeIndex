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
    public void Extract_Makefile_TargetDependenciesAndPhonyMetadataReferences_Issue4406()
    {
        const string content = """
            .PHONY: clean install # metadata lists declared targets
            all: build $(OPTIONAL)
            build:
            deploy:: build
            clean:
            install: all
            """;

        ReferenceExtractionCase.Extract("makefile", content)
            .ShouldContain("call", "clean", containerName: ".PHONY")
            .ShouldContain("call", "install", containerName: ".PHONY")
            .ShouldContain("call", "build", containerName: "all")
            .ShouldContain("call", "build", containerName: "deploy")
            .ShouldContain("call", "all", containerName: "install")
            .ShouldNotContainSymbol("OPTIONAL");
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
            .ShouldContain("project_reference", "src/App/App.csproj")
            .ShouldContain("import", "xunit")
            .ShouldContain("call", "Restore", containerName: "Generate")
            .ShouldContain("call", "Compile", containerName: "Generate")
            .ShouldContain("call", "Build", containerName: "Generate")
            .ShouldContain("call", "Pack", containerName: "Generate")
            .ShouldContain("call", "Publish", containerName: "Generate");
    }

    [Theory]
    [InlineData("shell", "helper() { nested; }\nnested() { :; }\nhelper\n", "helper", "nested", "helper", 4)]
    [InlineData("powershell", "function Invoke-Task { Write-Host done }\nJoin-Path . child\nWrite-Host done\n", "Invoke-Task", "Write-Host", "Join-Path", 4)]
    public void Extract_ScriptLanguages_CallsUseFunctionAndSyntheticScriptScopes_Issue4421(
        string language,
        string content,
        string functionName,
        string nestedCallName,
        string topLevelCallName,
        int expectedEndLine)
    {
        var symbols = SymbolExtractor.Extract(1, language, content);
        var scriptScope = Assert.Single(symbols, symbol =>
            symbol.Kind == "function"
            && symbol.SubKind == "script_scope"
            && symbol.Name == "<script>");
        Assert.Equal(1, scriptScope.StartLine);
        Assert.Equal(expectedEndLine, scriptScope.EndLine);

        var references = ReferenceExtractor.Extract(1, language, content, symbols);
        var nestedCall = Assert.Single(references, reference =>
            reference.ReferenceKind == "call"
            && reference.SymbolName == nestedCallName
            && reference.ContainerName == functionName);
        Assert.Equal("function", nestedCall.ContainerKind);
        Assert.Equal(functionName, nestedCall.ContainerName);

        var topLevelCall = Assert.Single(references, reference =>
            reference.ReferenceKind == "call"
            && reference.SymbolName == topLevelCallName);
        Assert.Equal("function", topLevelCall.ContainerKind);
        Assert.Equal("<script>", topLevelCall.ContainerName);
    }

    [Fact]
    public void Extract_Yaml_GitHubActionsReferences_Issue4410()
    {
        const string content = """
            name: CI
            jobs:
              build:
                steps:
                  - uses: actions/checkout@0123456789abcdef
                  - run: dotnet build src/App/App.csproj
              test:
                needs: [build, "lint"]
                steps:
                  - run: |
                      ./scripts/test.sh
                      echo ignored.txt
              lint:
                steps:
                  - run: echo lint
            deployment:
              uses: actions/setup-node@fedcba9876543210
              run: ./scripts/not-a-job.sh
            """;

        ReferenceExtractionCase.Extract("yaml", content)
            .ShouldContain("import", "actions/checkout", containerName: "jobs.build")
            .ShouldContain("project_reference", "src/App/App.csproj", containerName: "jobs.build")
            .ShouldContain("call", "jobs.build", containerName: "jobs.test")
            .ShouldContain("call", "jobs.lint", containerName: "jobs.test")
            .ShouldContain("project_reference", "scripts/test.sh", containerName: "jobs.test")
            .ShouldNotContainSymbol("ignored.txt")
            .ShouldNotContainSymbol("actions/setup-node")
            .ShouldNotContainSymbol("scripts/not-a-job.sh");
        Assert.True(ReferenceExtractor.SupportsLanguage("yaml"));
    }

    [Fact]
    public void Extract_Json_IndexesRepositoryLocalPathValues_Issue4460()
    {
        const string content = """
            {
              "core": ".agent_harness/command_guard_core.py",
              "codex": { "command": "/usr/bin/python3 \"$(git rev-parse --show-toplevel)/.codex/hooks/bash_guard.py\"" },
              "claude": [".claude/settings.json", ".claude/hooks/bash-guard.py"],
              "windows": "tools\\runner.ps1",
              "url": "https://example.com/not-local.py",
              "parent": "../outside.py",
              "bare": "ignored.txt"
            }
            """;

        var scenario = ReferenceExtractionCase.Extract("json", content)
            .ShouldHaveCount(5)
            .ShouldContain("project_reference", ".agent_harness/command_guard_core.py", line: 2)
            .ShouldContain("project_reference", ".codex/hooks/bash_guard.py", line: 3)
            .ShouldContain("project_reference", ".claude/settings.json", line: 4)
            .ShouldContain("project_reference", ".claude/hooks/bash-guard.py", line: 4)
            .ShouldContain("project_reference", "tools/runner.ps1", line: 5)
            .ShouldNotContainSymbol("example.com/not-local.py")
            .ShouldNotContainSymbol("../outside.py")
            .ShouldNotContainSymbol("ignored.txt");

        var lines = content.Split('\n');
        Assert.Equal(
            lines[2].IndexOf(".codex/hooks/bash_guard.py", StringComparison.Ordinal) + 1,
            scenario.Single("project_reference", ".codex/hooks/bash_guard.py").Column);
        Assert.Equal(
            lines[4].IndexOf("tools", StringComparison.Ordinal) + 1,
            scenario.Single("project_reference", "tools/runner.ps1").Column);
        Assert.True(ReferenceExtractor.SupportsLanguage("json"));
    }

    [Fact]
    public void Extract_Json_MalformedInputDoesNotEmitPartialReferences_Issue4460()
    {
        ReferenceExtractionCase.Extract("json", "{ \"path\": \"scripts/run.sh\"")
            .ShouldHaveCount(0);
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
