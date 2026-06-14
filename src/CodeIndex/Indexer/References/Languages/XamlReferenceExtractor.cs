using System.Text;
using System.Text.RegularExpressions;
using CodeIndex.Models;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Indexer;

internal static class XamlReferenceExtractor
{
    private static readonly Regex XamlTypeAttributeRegex = new(
        @"\b(?<attr>x:Class|x:DataType|TargetType|DataType)\s*=\s*[""'](?<value>[^""']+)[""']",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex XamlTypeArgumentsRegex = new(
        @"\bx:TypeArguments\s*=\s*[""'](?<value>[^""']+)[""']",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex XamlResourceReferenceRegex = new(
        @"\{(?:StaticResource|StaticResourceExtension|DynamicResource|DynamicResourceExtension)\b(?<content>(?:[^{}]|{[^{}]*})*)\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex XamlReferenceRegex = new(
        @"\{x:Reference(?:Extension)?\b(?<content>(?:[^{}]|{[^{}]*})*)\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex XamlBindingRegex = new(
        @"\{(?<kind>Binding|x:Bind|TemplateBinding|CompiledBinding|ReflectionBinding)\b(?<content>(?:[^{}]|{[^{}]*})*)\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex XamlBindingElementRegex = new(
        @"<\s*(?:[A-Za-z_][\w.-]*:)?Binding\b(?<attributes>[^<>]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex XamlBindingPropertyElementRegex = new(
        @"<\s*(?:[A-Za-z_][\w.-]*:)?Binding\.(?<property>Path|ElementName)\s*>\s*(?<value>[^<]+?)\s*</\s*(?:[A-Za-z_][\w.-]*:)?Binding\.\k<property>\s*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex XamlAttributeRegex = new(
        @"\b(?<name>[A-Za-z_][\w:.-]*)\s*=\s*[""'](?<value>[^""']+)[""']",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex XamlEventHandlerRegex = new(
        @"\b(?:Click|Clicked|Tapped|Loaded|Unloaded|SelectionChanged|TextChanged|CheckedChanged|Unchecked|SelectedIndexChanged|PointerPressed|PointerReleased|PointerEntered|PointerExited|Drop|DragOver|Completed|Appearing|Disappearing|NavigatedTo|NavigatedFrom|SizeChanged)\s*=\s*[""'](?<value>[^""']+)[""']",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsXaml(string[] lines)
    {
        foreach (var line in lines)
        {
            if (line.IndexOf("xmlns:x=", StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            if (line.IndexOf("schemas.microsoft.com/winfx/2006/xaml", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("github.com/avaloniaui", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }

        return false;
    }

    public static string StripXmlComments(string line, ref bool inComment)
    {
        var builder = new StringBuilder(line.Length);
        var index = 0;

        while (index < line.Length)
        {
            if (inComment)
            {
                var commentEnd = line.IndexOf("-->", index, StringComparison.Ordinal);
                if (commentEnd < 0)
                {
                    builder.Append(' ', line.Length - index);
                    return builder.ToString();
                }

                builder.Append(' ', commentEnd + 3 - index);
                index = commentEnd + 3;
                inComment = false;
                continue;
            }

            var commentStart = line.IndexOf("<!--", index, StringComparison.Ordinal);
            if (commentStart < 0)
            {
                builder.Append(line, index, line.Length - index);
                return builder.ToString();
            }

            if (commentStart > index)
                builder.Append(line, index, commentStart - index);

            var sameLineCommentEnd = line.IndexOf("-->", commentStart + 4, StringComparison.Ordinal);
            if (sameLineCommentEnd < 0)
            {
                builder.Append(' ', line.Length - commentStart);
                inComment = true;
                return builder.ToString();
            }

            builder.Append(' ', sameLineCommentEnd + 3 - commentStart);
            index = sameLineCommentEnd + 3;
        }

        return builder.ToString();
    }

    public static void Emit(
        string originalLine,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        SymbolRecord? container)
    {
        if (originalLine.Length == 0)
            return;

        foreach (Match match in XamlTypeAttributeRegex.Matches(originalLine))
        {
            foreach (var name in NormalizeXamlTypeValues(match.Groups["value"].Value))
            {
                AddReference(references, seen, fileId, name, match.Groups["value"].Index, "type_reference", context, lineNumber, container);
            }
        }

        foreach (Match match in XamlTypeArgumentsRegex.Matches(originalLine))
        {
            foreach (var name in NormalizeXamlTypeArguments(match.Groups["value"].Value))
            {
                AddReference(references, seen, fileId, name, match.Groups["value"].Index, "type_reference", context, lineNumber, container);
            }
        }

        foreach (Match match in XamlResourceReferenceRegex.Matches(originalLine))
        {
            var value = NormalizeNamedMarkupReference(match.Groups["content"].Value);
            if (value.Length > 0)
                AddReference(references, seen, fileId, value, match.Groups["content"].Index, "reference", context, lineNumber, container);
        }

        foreach (Match match in XamlReferenceRegex.Matches(originalLine))
        {
            var value = NormalizeNamedMarkupReference(match.Groups["content"].Value);
            if (value.Length > 0)
                AddReference(references, seen, fileId, value, match.Groups["content"].Index, "reference", context, lineNumber, container);
        }

        foreach (Match match in XamlBindingRegex.Matches(originalLine))
        {
            foreach (var name in NormalizeBindingReferences(match.Groups["kind"].Value, match.Groups["content"].Value))
            {
                AddReference(references, seen, fileId, name, match.Groups["content"].Index, "reference", context, lineNumber, container);
            }
        }

        foreach (Match match in XamlBindingElementRegex.Matches(originalLine))
        {
            EmitBindingElementAttributeReferences(
                match.Groups["attributes"].Value,
                match.Groups["attributes"].Index,
                references,
                seen,
                fileId,
                context,
                lineNumber,
                container);
        }

        foreach (Match match in XamlBindingPropertyElementRegex.Matches(originalLine))
        {
            var value = NormalizeBindingPath(match.Groups["value"].Value);
            if (value.Length > 0)
                AddReference(references, seen, fileId, value, match.Groups["value"].Index, "reference", context, lineNumber, container);
        }

        foreach (Match match in XamlEventHandlerRegex.Matches(originalLine))
        {
            var value = match.Groups["value"].Value.Trim();
            if (value.Length > 0)
                AddReference(references, seen, fileId, value, match.Groups["value"].Index, "call", context, lineNumber, container);
        }
    }

    private static void AddReference(
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string name,
        int nameIndex,
        string kind,
        string context,
        int lineNumber,
        SymbolRecord? container)
        => ReferenceExtractor.AddReference(references, seen, fileId, name, nameIndex, kind, context, lineNumber, container, "xml");

    private static IEnumerable<string> NormalizeXamlTypeValues(string value)
    {
        var normalized = NormalizeXamlMarkupArgument(value);
        if (normalized.Length > 0)
            yield return normalized;
    }

    private static IEnumerable<string> NormalizeXamlTypeArguments(string value)
    {
        foreach (var segment in value.Split(new[] { ',', '(', ')' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalized = NormalizeXamlMarkupArgument(segment);
            if (normalized.Length > 0)
                yield return normalized;
        }
    }

    private static string NormalizeNamedMarkupReference(string content)
    {
        foreach (var argument in SplitMarkupArguments(content))
        {
            var normalized = NormalizeXamlMarkupArgument(argument);
            if (normalized.Length > 0)
                return normalized;
        }

        return "";
    }

    private static IEnumerable<string> NormalizeBindingReferences(string kind, string content)
    {
        foreach (var argument in SplitMarkupArguments(content))
        {
            var trimmed = argument.Trim();
            if (trimmed.Length == 0)
                continue;

            var equalsIndex = trimmed.IndexOf('=');
            var key = equalsIndex >= 0 ? trimmed[..equalsIndex].Trim() : "";
            var value = equalsIndex >= 0 ? trimmed[(equalsIndex + 1)..].Trim() : trimmed;
            if (key.Equals("Source", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Converter", StringComparison.OrdinalIgnoreCase)
                || key.Equals("ConverterParameter", StringComparison.OrdinalIgnoreCase))
                continue;

            if (key.Length == 0
                || key.Equals("Path", StringComparison.OrdinalIgnoreCase)
                || key.Equals("ElementName", StringComparison.OrdinalIgnoreCase)
                || kind.Equals("x:Bind", StringComparison.OrdinalIgnoreCase)
                || kind.Equals("TemplateBinding", StringComparison.OrdinalIgnoreCase)
                || kind.Equals("CompiledBinding", StringComparison.OrdinalIgnoreCase)
                || kind.Equals("ReflectionBinding", StringComparison.OrdinalIgnoreCase))
            {
                var normalized = NormalizeBindingPath(value);
                if (normalized.Length > 0)
                    yield return normalized;
            }
        }
    }

    private static void EmitBindingElementAttributeReferences(
        string attributes,
        int attributesIndex,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        foreach (Match attribute in XamlAttributeRegex.Matches(attributes))
        {
            var name = attribute.Groups["name"].Value;
            if (!name.Equals("Path", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("ElementName", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = NormalizeBindingPath(attribute.Groups["value"].Value);
            if (value.Length > 0)
                AddReference(references, seen, fileId, value, attributesIndex + attribute.Groups["value"].Index, "reference", context, lineNumber, container);
        }
    }

    private static string NormalizeBindingPath(string value)
    {
        var normalized = NormalizeXamlMarkupArgument(value);
        if (normalized.Length == 0)
            return "";

        var lastDot = normalized.LastIndexOf('.');
        if (lastDot >= 0 && lastDot + 1 < normalized.Length)
            normalized = normalized[(lastDot + 1)..];

        return normalized.Trim();
    }

    private static string NormalizeXamlMarkupArgument(string value)
    {
        value = value.Trim().Trim('"', '\'');
        if (value.Length == 0)
            return "";

        var equalsIndex = value.IndexOf('=');
        if (equalsIndex >= 0)
            value = value[(equalsIndex + 1)..].Trim();

        if (value.StartsWith("{x:Type ", StringComparison.Ordinal))
            value = value["{x:Type ".Length..].TrimEnd('}', ' ');
        else if (value.StartsWith("{x:TypeExtension ", StringComparison.Ordinal))
            value = value["{x:TypeExtension ".Length..].TrimEnd('}', ' ');
        else if (value.StartsWith("{x:Static ", StringComparison.Ordinal))
            value = value["{x:Static ".Length..].TrimEnd('}', ' ');

        if (value.StartsWith("TypeName=", StringComparison.Ordinal))
            value = value["TypeName=".Length..].Trim();
        if (value.StartsWith("Member=", StringComparison.Ordinal))
            value = value["Member=".Length..].Trim();
        if (value.StartsWith("Name=", StringComparison.Ordinal))
            value = value["Name=".Length..].Trim();
        if (value.StartsWith("ResourceKey=", StringComparison.Ordinal))
            value = value["ResourceKey=".Length..].Trim();

        if (value.StartsWith("{x:Type ", StringComparison.Ordinal))
            value = value["{x:Type ".Length..].TrimEnd('}', ' ');
        if (value.StartsWith("{x:TypeExtension ", StringComparison.Ordinal))
            value = value["{x:TypeExtension ".Length..].TrimEnd('}', ' ');

        var memberTypeEnd = value.IndexOf("}.", StringComparison.Ordinal);
        if (memberTypeEnd >= 0 && memberTypeEnd + 2 < value.Length)
        {
            var typeStart = value.LastIndexOf(' ', memberTypeEnd);
            var typeName = typeStart >= 0 ? value[(typeStart + 1)..memberTypeEnd] : "";
            var memberName = value[(memberTypeEnd + 2)..].Trim();
            if (typeName.Length > 0 && memberName.Length > 0)
                return $"{typeName}.{memberName}";
        }

        return value.Trim().TrimEnd('}', '/', '>');
    }

    private static IEnumerable<string> SplitMarkupArguments(string content)
    {
        var start = 0;
        var depth = 0;
        for (var i = 0; i <= content.Length; i++)
        {
            if (i < content.Length)
            {
                if (content[i] == '{')
                    depth++;
                else if (content[i] == '}' && depth > 0)
                    depth--;
                else if (content[i] != ',' || depth != 0)
                    continue;
            }

            var segment = content[start..i].Trim();
            if (segment.Length > 0)
                yield return segment;
            start = i + 1;
        }
    }
}
