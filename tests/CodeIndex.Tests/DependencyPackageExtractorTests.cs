using CodeIndex.Indexer;
using CodeIndex.Models;

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
    public void Extract_DependencyManifest_RejectsDtdWithSharedReaderPolicy_Issue4130()
    {
        var symbols = SymbolExtractor.Extract(
            5,
            "dependency_manifest",
            """
            <!DOCTYPE Project [
              <!ENTITY packageName "Unsafe.Package">
            ]>
            <Project>
              <ItemGroup>
                <PackageVersion Include="&packageName;" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """,
            filePath: "Directory.Packages.props");

        Assert.Empty(symbols);
    }

    [Fact]
    public void Extract_DependencyManifest_RejectsExternalEntityWithSharedReaderPolicy_Issue4345()
    {
        var symbols = SymbolExtractor.Extract(
            5,
            "dependency_manifest",
            """
            <!DOCTYPE Project [
              <!ENTITY packageName SYSTEM "file:///should/not/be/read">
            ]>
            <Project>
              <ItemGroup>
                <PackageVersion Include="&packageName;" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """,
            filePath: "Directory.Packages.props");

        Assert.Empty(symbols);
    }

    [Fact]
    public void Extract_DependencyManifest_StopsAtSharedDocumentLimit_Issue4130()
    {
        var padding = new string('a', (int)SymbolExtractor.XmlExtractionMaxCharactersInDocument + 1);
        var symbols = SymbolExtractor.Extract(
            6,
            "dependency_manifest",
            $"""
            <Project>
              <ItemGroup>
                <PackageVersion Include="TooLarge.Package" Version="1.0.0" />
              </ItemGroup>
              <PropertyGroup>
                <Padding>{padding}</Padding>
              </PropertyGroup>
            </Project>
            """,
            filePath: "Directory.Packages.props");

        Assert.Empty(symbols);
    }

    [Fact]
    public void Extract_DependencyLock_EmitsResolvedSymbolsAndParentPackageReferences_Issues3899And4409()
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
                    "resolved": "13.0.3",
                    "dependencies": {
                      "Serilog": "3.1.1"
                    }
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
            reference.SymbolName == "Serilog"
            && reference.ReferenceKind == "dependency"
            && reference.ContainerKind == "package"
            && reference.ContainerName == "Newtonsoft.Json"
            && reference.Context.Contains("Serilog", StringComparison.Ordinal));
        Assert.DoesNotContain(references, reference => reference.SymbolName == "Newtonsoft.Json");
    }

    [Fact]
    public void Extract_DependencyLock_ReferencesModelNpmParentPackageEdges_Issue4409()
    {
        var content = """
            {
              "packages": {
                "node_modules/left-pad": {
                  "dependencies": {
                    "repeat-string": "1.6.1",
                    "is-number": "7.0.0"
                  }
                },
                "node_modules/repeat-string": {
                  "version": "1.6.1"
                }
              }
            }
            """;
        var symbols = new[]
        {
            new SymbolRecord
            {
                FileId = 11,
                Kind = "package",
                Name = "repeat-string",
                Line = 3,
                StartLine = 3,
                StartColumn = 5,
                EndLine = 3,
                ContainerKind = "project",
                ContainerName = "npm",
            },
            new SymbolRecord
            {
                FileId = 11,
                Kind = "package",
                Name = "is-number",
                Line = 4,
                StartLine = 4,
                StartColumn = 9,
                EndLine = 4,
                ContainerKind = "project",
                ContainerName = "npm",
            },
        };

        var references = ReferenceExtractor.Extract(
            11,
            "dependency_lock",
            content,
            symbols,
            path: "package-lock.json",
            maxReferenceCount: 1);

        var reference = Assert.Single(references);
        Assert.Equal("repeat-string", reference.SymbolName);
        Assert.Equal("dependency", reference.ReferenceKind);
        Assert.Equal("package", reference.ContainerKind);
        Assert.Equal("left-pad", reference.ContainerName);
    }
}
