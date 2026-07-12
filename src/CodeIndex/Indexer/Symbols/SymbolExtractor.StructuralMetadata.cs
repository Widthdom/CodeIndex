using System.Xml;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private const string XmlNamespaceDeclarationUri = "http://www.w3.org/2000/xmlns/";

    private static readonly string[] ManifestAssemblyIdentityAttributes =
    [
        "version",
        "processorArchitecture",
        "type",
        "publicKeyToken",
    ];

    private static List<SymbolRecord> ExtractSolutionSymbols(long fileId, string[] lines)
    {
        List<SymbolRecord>? symbols = null;
        foreach (var project in SolutionFileParser.ExtractProjects(lines))
        {
            (symbols ??= []).Add(new SymbolRecord
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

        return symbols ?? [];
    }

    private static List<SymbolRecord> ExtractAppManifestSymbols(long fileId, string content, string[] lines)
    {
        List<SymbolRecord>? symbols = null;
        var elementPaths = new Stack<string>();
        var truncated = false;
        var elementCount = 0;

        try
        {
            using var reader = XmlReader.Create(new StringReader(content), CreateExtractionXmlReaderSettings(DtdProcessing.Ignore));
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement)
                {
                    if (elementPaths.Count > 0)
                        elementPaths.Pop();
                    continue;
                }

                if (reader.NodeType != XmlNodeType.Element)
                    continue;

                elementCount++;

                var elementName = reader.LocalName;
                var parentPath = elementPaths.Count == 0 ? null : elementPaths.Peek();
                var elementPath = parentPath == null ? elementName : $"{parentPath}.{elementName}";
                var lineNumber = reader is IXmlLineInfo lineInfo && lineInfo.HasLineInfo()
                    ? lineInfo.LineNumber
                    : 1;

                if (elementCount > XmlExtractionMaxElements || reader.Depth + 1 > XmlExtractionMaxDepth)
                {
                    AddStructuredDataDiagnosticSymbol(symbols ??= [], fileId, "structured_data_traversal_budget_exceeded", lineNumber, lines, "Application-manifest traversal exceeded the XML structure limit; remaining symbols were truncated.", ref truncated);
                    return symbols;
                }

                if (elementPath.Length > StructuredDataMaxPathLength)
                {
                    AddStructuredDataDiagnosticSymbol(symbols ??= [], fileId, "structured_data_traversal_budget_exceeded", lineNumber, lines, "XML element path exceeded the structured-data path limit; remaining symbols were truncated.", ref truncated);
                    return symbols;
                }

                if ((symbols?.Count ?? 0) >= StructuredDataMaxSymbols)
                {
                    AddStructuredDataDiagnosticSymbol(symbols ??= [], fileId, "structured_data_xml_symbol_budget_exceeded", lineNumber, lines, "XML symbol extraction exceeded the per-file symbol budget; remaining symbols were truncated.", ref truncated);
                    return symbols;
                }

                (symbols ??= []).Add(CreateManifestSymbol(
                    fileId,
                    string.Equals(elementName, "longPathAware", StringComparison.OrdinalIgnoreCase) ? "property" : "namespace",
                    elementPath,
                    lineNumber,
                    lines,
                    parentPath));

                if (string.Equals(elementName, "assemblyIdentity", StringComparison.OrdinalIgnoreCase))
                {
                    AddManifestAssemblyIdentitySymbols(fileId, lines, ref symbols, reader, lineNumber, elementPath);
                }
                else if (string.Equals(elementName, "requestedExecutionLevel", StringComparison.OrdinalIgnoreCase))
                {
                    AddManifestAttributeSymbol(
                        fileId,
                        lines,
                        ref symbols,
                        "property",
                        $"{elementPath}.@level",
                        reader.GetAttribute("level"),
                        lineNumber,
                        parentName: elementPath);
                    AddManifestAttributeSymbol(
                        fileId,
                        lines,
                        ref symbols,
                        "property",
                        $"{elementPath}.@uiAccess",
                        reader.GetAttribute("uiAccess"),
                        lineNumber,
                        parentName: elementPath);
                }
                else if (string.Equals(elementName, "supportedOS", StringComparison.OrdinalIgnoreCase))
                {
                    var id = reader.GetAttribute("Id");
                    var name = string.IsNullOrWhiteSpace(id) ? elementPath : $"{elementPath}.{id}";
                    AddManifestAttributeSymbol(fileId, lines, ref symbols, "property", name, id, lineNumber, parentName: elementPath);
                }

                if (!reader.IsEmptyElement)
                    elementPaths.Push(elementPath);
            }
        }
        catch (XmlException)
        {
            return TrimStructuredDataSymbols(symbols ?? [], fileId, "structured_data_xml_symbol_budget_exceeded", lines);
        }

        return TrimStructuredDataSymbols(symbols ?? [], fileId, "structured_data_xml_symbol_budget_exceeded", lines);
    }

    private static List<SymbolRecord> ExtractGenericXmlSymbols(long fileId, string content, string[] lines)
    {
        if (TryGetXmlStructureIssue(content, out _))
            return [];

        var symbols = CreateSymbolListForLines(lines.Length);
        var elementPaths = new Stack<string>();
        var elementSymbolIndexes = new Stack<int>();
        var truncated = false;

        try
        {
            using var reader = XmlReader.Create(new StringReader(content), CreateExtractionXmlReaderSettings(DtdProcessing.Prohibit));
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    var parentPath = elementPaths.Count == 0 ? null : elementPaths.Peek();
                    var path = parentPath == null ? reader.LocalName : $"{parentPath}.{reader.LocalName}";
                    var lineNumber = reader is IXmlLineInfo lineInfo && lineInfo.HasLineInfo() ? lineInfo.LineNumber : 1;
                    if (path.Length > StructuredDataMaxPathLength)
                    {
                        AddStructuredDataDiagnosticSymbol(symbols, fileId, "structured_data_traversal_budget_exceeded", lineNumber, lines, "XML element path exceeded the structured-data path limit; remaining symbols were truncated.", ref truncated);
                        return symbols;
                    }
                    if (!TryAddStructuredDataSymbol(fileId, "namespace", path, lineNumber, lines, parentPath, symbols, "structured_data_xml_symbol_budget_exceeded", ref truncated))
                        return symbols;

                    var symbol = symbols[^1];
                    symbol.BodyStartLine = lineNumber;
                    symbol.BodyEndLine = lineNumber;
                    var elementSymbolIndex = symbols.Count - 1;

                    if (reader.HasAttributes && reader.MoveToFirstAttribute())
                    {
                        do
                        {
                            if (reader.NamespaceURI == XmlNamespaceDeclarationUri)
                                continue;

                            var attributePath = $"{path}.@{reader.LocalName}";
                            if (attributePath.Length > StructuredDataMaxPathLength)
                            {
                                AddStructuredDataDiagnosticSymbol(symbols, fileId, "structured_data_traversal_budget_exceeded", lineNumber, lines, "XML attribute path exceeded the structured-data path limit; remaining symbols were truncated.", ref truncated);
                                return symbols;
                            }
                            if (!TryAddStructuredDataSymbol(fileId, "property", attributePath, lineNumber, lines, path, symbols, "structured_data_xml_symbol_budget_exceeded", ref truncated))
                                return symbols;
                        }
                        while (reader.MoveToNextAttribute());
                        reader.MoveToElement();
                    }

                    if (!reader.IsEmptyElement)
                    {
                        elementPaths.Push(path);
                        elementSymbolIndexes.Push(elementSymbolIndex);
                    }
                }
                else if (reader.NodeType == XmlNodeType.EndElement && elementPaths.Count > 0)
                {
                    elementPaths.Pop();
                    var symbol = symbols[elementSymbolIndexes.Pop()];
                    var endLine = reader is IXmlLineInfo lineInfo && lineInfo.HasLineInfo() ? lineInfo.LineNumber : symbol.Line;
                    symbol.EndLine = endLine;
                    symbol.BodyEndLine = endLine;
                }
            }
        }
        catch (XmlException)
        {
            return [];
        }

        return TrimStructuredDataSymbols(symbols, fileId, "structured_data_xml_symbol_budget_exceeded", lines);
    }

    private static void AddManifestAssemblyIdentitySymbols(
        long fileId,
        string[] lines,
        ref List<SymbolRecord>? symbols,
        XmlReader reader,
        int lineNumber,
        string elementPath)
    {
        var assemblyName = reader.GetAttribute("name");
        if (!string.IsNullOrWhiteSpace(assemblyName))
        {
            (symbols ??= []).Add(CreateManifestSymbol(fileId, "assembly", assemblyName, lineNumber, lines, parentName: null));
        }

        foreach (var attributeName in ManifestAssemblyIdentityAttributes)
        {
            AddManifestAttributeSymbol(
                fileId,
                lines,
                ref symbols,
                "property",
                $"{elementPath}.@{attributeName}",
                reader.GetAttribute(attributeName),
                lineNumber,
                parentName: elementPath);
        }
    }

    private static void AddManifestAttributeSymbol(
        long fileId,
        string[] lines,
        ref List<SymbolRecord>? symbols,
        string kind,
        string name,
        string? value,
        int lineNumber,
        string? parentName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        (symbols ??= []).Add(CreateManifestSymbol(fileId, kind, name, lineNumber, lines, parentName));
    }

    private static SymbolRecord CreateManifestSymbol(
        long fileId,
        string kind,
        string name,
        int lineNumber,
        string[] lines,
        string? parentName)
    {
        var line = GetLineOrEmpty(lines, lineNumber);
        return new SymbolRecord
        {
            FileId = fileId,
            Kind = kind,
            Name = name,
            Line = lineNumber,
            StartLine = lineNumber,
            EndLine = lineNumber,
            Signature = string.IsNullOrEmpty(line) ? null : LimitStructuredDataLineSignature(line),
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
