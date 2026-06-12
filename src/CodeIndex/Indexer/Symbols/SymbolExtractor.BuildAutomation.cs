using System.Xml;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static readonly HashSet<string> MsBuildContainerElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "Project",
        "PropertyGroup",
        "ItemGroup",
        "ItemDefinitionGroup",
        "Choose",
        "When",
        "Otherwise",
        "Target",
    };

    private static readonly HashSet<string> MsBuildImportItemElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "Analyzer",
        "Compile",
        "Content",
        "None",
        "PackageReference",
        "ProjectReference",
        "Reference",
    };

    private static List<SymbolRecord> ExtractMsBuildSymbols(long fileId, string content, string[] lines)
    {
        var symbols = new List<SymbolRecord>();
        var elementStack = new Stack<string>();
        var targetStack = new Stack<int>();

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            XmlResolver = null,
        };

        try
        {
            using var reader = XmlReader.Create(new StringReader(content), settings);
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    var elementName = reader.LocalName;
                    var lineNumber = reader is IXmlLineInfo lineInfo && lineInfo.HasLineInfo()
                        ? lineInfo.LineNumber
                        : 1;
                    var parentName = elementStack.Count == 0 ? null : elementStack.Peek();
                    var activeTarget = targetStack.Count == 0 ? null : symbols[targetStack.Peek()];
                    var addedTargetIndex = TryAddMsBuildElementSymbol(
                        fileId,
                        lines,
                        symbols,
                        elementName,
                        parentName,
                        lineNumber,
                        activeTarget);

                    if (addedTargetIndex.HasValue)
                    {
                        var symbol = symbols[addedTargetIndex.Value];
                        symbol.BodyStartLine = lineNumber;
                        symbol.BodyEndLine = lineNumber;
                        if (!reader.IsEmptyElement)
                            targetStack.Push(addedTargetIndex.Value);
                    }

                    if (!reader.IsEmptyElement)
                        elementStack.Push(elementName);
                }
                else if (reader.NodeType == XmlNodeType.EndElement)
                {
                    if (elementStack.Count > 0)
                        elementStack.Pop();

                    if (string.Equals(reader.LocalName, "Target", StringComparison.OrdinalIgnoreCase)
                        && targetStack.Count > 0)
                    {
                        var target = symbols[targetStack.Pop()];
                        var endLine = reader is IXmlLineInfo lineInfo && lineInfo.HasLineInfo()
                            ? lineInfo.LineNumber
                            : target.Line;
                        target.EndLine = endLine;
                        target.BodyEndLine = endLine;
                    }
                }
            }
        }
        catch (XmlException)
        {
            return symbols;
        }

        return symbols;
    }

    private static int? TryAddMsBuildElementSymbol(
        long fileId,
        string[] lines,
        List<SymbolRecord> symbols,
        string elementName,
        string? parentName,
        int lineNumber,
        SymbolRecord? activeTarget)
    {
        var line = GetLineOrEmpty(lines, lineNumber);
        var name = GetMsBuildAttributeValue(line, "Name");
        if (string.Equals(elementName, "Target", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(name))
        {
            symbols.Add(CreateMsBuildSymbol(fileId, "function", name, lineNumber, line, activeTarget));
            return symbols.Count - 1;
        }

        if (string.Equals(elementName, "Import", StringComparison.OrdinalIgnoreCase))
        {
            var project = GetMsBuildAttributeValue(line, "Project");
            if (!string.IsNullOrWhiteSpace(project))
                symbols.Add(CreateMsBuildSymbol(fileId, "import", project, lineNumber, line, activeTarget));
            return null;
        }

        if (MsBuildImportItemElements.Contains(elementName))
        {
            var include = GetMsBuildAttributeValue(line, "Include");
            if (!string.IsNullOrWhiteSpace(include))
                symbols.Add(CreateMsBuildSymbol(fileId, "import", include, lineNumber, line, activeTarget));
            return null;
        }

        if (string.Equals(parentName, "PropertyGroup", StringComparison.OrdinalIgnoreCase)
            && !MsBuildContainerElements.Contains(elementName))
        {
            symbols.Add(CreateMsBuildSymbol(fileId, "property", elementName, lineNumber, line, activeTarget));
        }

        return null;
    }

    private static SymbolRecord CreateMsBuildSymbol(
        long fileId,
        string kind,
        string name,
        int lineNumber,
        string line,
        SymbolRecord? activeTarget)
        => new()
        {
            FileId = fileId,
            Kind = kind,
            Name = name.Trim(),
            Line = lineNumber,
            StartLine = lineNumber,
            EndLine = lineNumber,
            StartColumn = line.IndexOf(name, StringComparison.Ordinal) is var index && index >= 0 ? index : null,
            Signature = line.Trim(),
            ContainerKind = activeTarget?.Kind,
            ContainerName = activeTarget?.Name,
            ContainerQualifiedName = activeTarget?.Name,
        };

    private static string? GetMsBuildAttributeValue(string line, string attributeName)
    {
        var ordinalPattern = attributeName + "=\"";
        var ordinalIndex = line.IndexOf(ordinalPattern, StringComparison.OrdinalIgnoreCase);
        if (ordinalIndex >= 0)
            return ReadQuotedMsBuildAttributeValue(line, ordinalIndex + ordinalPattern.Length, '"');

        var singlePattern = attributeName + "='";
        var singleIndex = line.IndexOf(singlePattern, StringComparison.OrdinalIgnoreCase);
        return singleIndex >= 0
            ? ReadQuotedMsBuildAttributeValue(line, singleIndex + singlePattern.Length, '\'')
            : null;
    }

    private static string? ReadQuotedMsBuildAttributeValue(string line, int startIndex, char quote)
    {
        var endIndex = line.IndexOf(quote, startIndex);
        return endIndex > startIndex ? line[startIndex..endIndex] : null;
    }

    private static string GetLineOrEmpty(string[] lines, int lineNumber)
    {
        var index = lineNumber - 1;
        return index >= 0 && index < lines.Length ? lines[index] : string.Empty;
    }
}
