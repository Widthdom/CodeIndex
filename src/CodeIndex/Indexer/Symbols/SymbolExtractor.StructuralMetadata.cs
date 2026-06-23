using System.Xml;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static readonly string[] ManifestAssemblyIdentityAttributes =
    [
        "version",
        "processorArchitecture",
        "type",
        "publicKeyToken",
    ];

    private static List<SymbolRecord> ExtractSolutionSymbols(long fileId, string[] lines)
    {
        var symbols = new List<SymbolRecord>();
        foreach (var project in SolutionFileParser.ExtractProjects(lines))
        {
            symbols.Add(new SymbolRecord
            {
                FileId = fileId,
                Kind = "project",
                SubKind = GetSolutionProjectSubKind(project.NormalizedProjectPath),
                Name = project.Name,
                Line = project.LineNumber,
                StartLine = project.LineNumber,
                StartColumn = project.NameIndex,
                EndLine = project.LineNumber,
                Signature = project.Context,
            });
        }

        return symbols;
    }

    private static List<SymbolRecord> ExtractAppManifestSymbols(long fileId, string content, string[] lines)
    {
        var symbols = new List<SymbolRecord>();

        try
        {
            using var reader = XmlReader.Create(new StringReader(content), CreateExtractionXmlReaderSettings(DtdProcessing.Ignore));
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                    continue;

                var elementName = reader.LocalName;
                var lineNumber = reader is IXmlLineInfo lineInfo && lineInfo.HasLineInfo()
                    ? lineInfo.LineNumber
                    : 1;

                if (string.Equals(elementName, "assemblyIdentity", StringComparison.OrdinalIgnoreCase))
                {
                    AddManifestAssemblyIdentitySymbols(fileId, lines, symbols, reader, lineNumber);
                }
                else if (string.Equals(elementName, "requestedExecutionLevel", StringComparison.OrdinalIgnoreCase))
                {
                    AddManifestAttributeSymbol(
                        fileId,
                        lines,
                        symbols,
                        "property",
                        "requestedExecutionLevel.level",
                        reader.GetAttribute("level"),
                        lineNumber,
                        parentName: "requestedExecutionLevel");
                    AddManifestAttributeSymbol(
                        fileId,
                        lines,
                        symbols,
                        "property",
                        "requestedExecutionLevel.uiAccess",
                        reader.GetAttribute("uiAccess"),
                        lineNumber,
                        parentName: "requestedExecutionLevel");
                }
                else if (string.Equals(elementName, "supportedOS", StringComparison.OrdinalIgnoreCase))
                {
                    var id = reader.GetAttribute("Id");
                    var name = string.IsNullOrWhiteSpace(id) ? "supportedOS" : $"supportedOS.{id}";
                    AddManifestAttributeSymbol(fileId, lines, symbols, "property", name, id, lineNumber, parentName: "compatibility");
                }
                else if (string.Equals(elementName, "longPathAware", StringComparison.OrdinalIgnoreCase))
                {
                    symbols.Add(CreateManifestSymbol(
                        fileId,
                        "property",
                        "longPathAware",
                        lineNumber,
                        lines,
                        parentName: "application"));
                }
            }
        }
        catch (XmlException)
        {
            return symbols;
        }

        return symbols;
    }

    private static void AddManifestAssemblyIdentitySymbols(
        long fileId,
        string[] lines,
        List<SymbolRecord> symbols,
        XmlReader reader,
        int lineNumber)
    {
        var assemblyName = reader.GetAttribute("name");
        if (!string.IsNullOrWhiteSpace(assemblyName))
        {
            symbols.Add(CreateManifestSymbol(fileId, "assembly", assemblyName, lineNumber, lines, parentName: null));
        }

        foreach (var attributeName in ManifestAssemblyIdentityAttributes)
        {
            AddManifestAttributeSymbol(
                fileId,
                lines,
                symbols,
                "property",
                $"assemblyIdentity.{attributeName}",
                reader.GetAttribute(attributeName),
                lineNumber,
                parentName: "assemblyIdentity");
        }
    }

    private static void AddManifestAttributeSymbol(
        long fileId,
        string[] lines,
        List<SymbolRecord> symbols,
        string kind,
        string name,
        string? value,
        int lineNumber,
        string? parentName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        symbols.Add(CreateManifestSymbol(fileId, kind, name, lineNumber, lines, parentName));
    }

    private static SymbolRecord CreateManifestSymbol(
        long fileId,
        string kind,
        string name,
        int lineNumber,
        string[] lines,
        string? parentName)
    {
        var line = GetLineOrEmpty(lines, lineNumber).Trim();
        return new SymbolRecord
        {
            FileId = fileId,
            Kind = kind,
            Name = name,
            Line = lineNumber,
            StartLine = lineNumber,
            EndLine = lineNumber,
            Signature = string.IsNullOrEmpty(line) ? null : line,
            ContainerKind = parentName == null ? null : "namespace",
            ContainerName = parentName,
            ContainerQualifiedName = parentName,
        };
    }

    private static string GetSolutionProjectSubKind(string projectPath)
    {
        var extension = Path.GetExtension(projectPath);
        return string.IsNullOrWhiteSpace(extension)
            ? "project"
            : extension.TrimStart('.').ToLowerInvariant();
    }
}
