using System.Runtime.CompilerServices;
using System.Xml;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    internal const int XmlExtractionMaxDepth = 64;
    internal const int XmlExtractionMaxElements = 4096;

    internal readonly record struct XmlStructureIssue(string Kind, int Line, string Message);

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
        var elementCount = 0;

        try
        {
            using var reader = XmlReader.Create(new StringReader(content), CreateExtractionXmlReaderSettings());
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    elementCount++;
                    if (elementCount > XmlExtractionMaxElements || reader.Depth + 1 > XmlExtractionMaxDepth)
                        return symbols;

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

    internal static bool TryGetXmlStructureIssue(
        string content,
        out XmlStructureIssue issue,
        [CallerMemberName] string? callerMemberName = null)
    {
        issue = default;
        var elementCount = 0;

        try
        {
            using var reader = XmlReader.Create(new StringReader(content), CreateExtractionXmlReaderSettings());
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                    continue;

                elementCount++;
                var lineNumber = reader is IXmlLineInfo lineInfo && lineInfo.HasLineInfo()
                    ? lineInfo.LineNumber
                    : 1;

                if (reader.Depth + 1 > XmlExtractionMaxDepth)
                {
                    issue = new XmlStructureIssue(
                        "xml_structure_budget_exceeded",
                        lineNumber,
                        $"XML element depth exceeds the extraction limit of {XmlExtractionMaxDepth}; symbol extraction is capped.");
                    return true;
                }

                if (elementCount > XmlExtractionMaxElements)
                {
                    // XAML symbol extraction has its own capped diagnostic path; content validation still reports the XML file issue.
                    if (string.Equals(callerMemberName, nameof(ExtractXmlSymbols), StringComparison.Ordinal))
                        return false;

                    issue = new XmlStructureIssue(
                        "xml_structure_budget_exceeded",
                        lineNumber,
                        $"XML element count exceeds the extraction limit of {XmlExtractionMaxElements}; symbol extraction is capped.");
                    return true;
                }
            }
        }
        catch (XmlException ex) when (IsDtdProhibitedException(ex))
        {
            issue = new XmlStructureIssue(
                "xml_dtd_prohibited",
                Math.Max(1, ex.LineNumber),
                "XML DTD declarations are prohibited during extraction.");
            return true;
        }
        catch (XmlException)
        {
            return false;
        }

        return false;
    }

    private static XmlReaderSettings CreateExtractionXmlReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        XmlResolver = null,
    };

    private static bool IsDtdProhibitedException(XmlException ex) =>
        ex.Message.Contains("DTD", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("DOCTYPE", StringComparison.OrdinalIgnoreCase);

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
