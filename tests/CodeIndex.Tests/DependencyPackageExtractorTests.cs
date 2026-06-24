using CodeIndex.Indexer;

namespace CodeIndex.Tests;

public class DependencyPackageExtractorTests
{
    [Fact]
    public void Extract_DependencyManifest_EmitsPackageVersionSymbols_Issue3899()
    {
        var propsSymbols = SymbolExtractor.Extract(
            1,
            "dependency_manifest",
            """
            <Project>
              <ItemGroup>
                <PackageVersion Include="Newtonsoft.Json" Version="13.0.3" />
              </ItemGroup>
            </Project>
            """,
            filePath: "Directory.Packages.props");
        var propsReferences = ReferenceExtractor.Extract(
            1,
            "dependency_manifest",
            """
            <Project>
              <ItemGroup>
                <PackageVersion Include="Newtonsoft.Json" Version="13.0.3" />
              </ItemGroup>
            </Project>
            """,
            propsSymbols,
            path: "Directory.Packages.props");
        var packagesConfigSymbols = SymbolExtractor.Extract(
            2,
            "dependency_manifest",
            """
            <?xml version="1.0" encoding="utf-8"?>
            <packages>
              <package id="xunit" version="2.5.3" targetFramework="net8.0" />
            </packages>
            """,
            filePath: "packages.config");
        var requirementsSymbols = SymbolExtractor.Extract(
            3,
            "dependency_manifest",
            """
            requests==2.31.0
            pytest>=8.0
            """,
            filePath: "requirements.txt");
        var pyprojectSymbols = SymbolExtractor.Extract(
            4,
            "dependency_manifest",
            """
            [project]
            dependencies = [
              "httpx>=0.27",
            ]

            [tool.poetry.group.dev.dependencies]
            pytest = "^8.2"
            """,
            filePath: "pyproject.toml");

        Assert.Contains(propsSymbols, symbol =>
            symbol.Kind == "package"
            && symbol.SubKind == "manifest_dependency"
            && symbol.Name == "Newtonsoft.Json"
            && symbol.Signature?.Contains("version=13.0.3", StringComparison.Ordinal) == true);
        Assert.Contains(propsReferences, reference =>
            reference.ReferenceKind == "dependency"
            && reference.SymbolName == "Newtonsoft.Json"
            && reference.Context.Contains("Newtonsoft.Json", StringComparison.Ordinal));
        Assert.Contains(packagesConfigSymbols, symbol =>
            symbol.Kind == "package"
            && symbol.Name == "xunit"
            && symbol.Signature?.Contains("version=2.5.3", StringComparison.Ordinal) == true);
        Assert.Contains(requirementsSymbols, symbol =>
            symbol.Name == "requests"
            && symbol.Signature?.Contains("constraint===2.31.0", StringComparison.Ordinal) == true);
        Assert.Contains(requirementsSymbols, symbol =>
            symbol.Name == "pytest"
            && symbol.Signature?.Contains("constraint=>=8.0", StringComparison.Ordinal) == true);
        Assert.Contains(pyprojectSymbols, symbol =>
            symbol.Name == "httpx"
            && symbol.ContainerName == "python.project"
            && symbol.Signature?.Contains("constraint=>=0.27", StringComparison.Ordinal) == true);
        Assert.Contains(pyprojectSymbols, symbol =>
            symbol.Name == "pytest"
            && symbol.ContainerName == "tool.poetry.group.dev.dependencies"
            && symbol.Signature?.Contains("constraint=^8.2", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Extract_DependencyLock_EmitsResolvedPackageSymbolsAndReferences_Issue3899()
    {
        var content =
            """
            {
              "version": 1,
              "dependencies": {
                "net8.0": {
                  "Newtonsoft.Json": {
                    "type": "Direct",
                    "requested": "[13.0.3, )",
                    "resolved": "13.0.3"
                  },
                  "Serilog": {
                    "type": "Transitive",
                    "resolved": "3.1.1"
                  }
                }
              }
            }
            """.Replace("\n", "\r\n", StringComparison.Ordinal);

        var symbols = SymbolExtractor.Extract(10, "dependency_lock", content, filePath: "packages.lock.json");
        var references = ReferenceExtractor.Extract(10, "dependency_lock", content, symbols, path: "packages.lock.json");

        Assert.Contains(symbols, symbol =>
            symbol.Kind == "package"
            && symbol.SubKind == "lock_direct_dependency"
            && symbol.Name == "Newtonsoft.Json"
            && symbol.ContainerName == "net8.0"
            && symbol.Signature?.Contains("role=direct", StringComparison.Ordinal) == true
            && symbol.Signature?.Contains("resolved=13.0.3", StringComparison.Ordinal) == true
            && symbol.Signature?.Contains("requested=[13.0.3, )", StringComparison.Ordinal) == true);
        Assert.Contains(symbols, symbol =>
            symbol.Kind == "package"
            && symbol.SubKind == "lock_transitive_dependency"
            && symbol.Name == "Serilog"
            && symbol.Signature?.Contains("role=transitive", StringComparison.Ordinal) == true);
        Assert.Contains(references, reference =>
            reference.SymbolName == "Newtonsoft.Json"
            && reference.ReferenceKind == "dependency"
            && reference.ContainerName == "net8.0"
            && reference.Context.Contains("Newtonsoft.Json", StringComparison.Ordinal));
        Assert.Contains(references, reference =>
            reference.SymbolName == "Serilog"
            && reference.ReferenceKind == "dependency");
    }
}
