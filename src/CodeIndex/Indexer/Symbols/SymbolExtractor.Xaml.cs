using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using System.Runtime.CompilerServices;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{

    private static readonly string[] WrappedXamlTypeBearingAttributeNames = ["x:Class", "x:DataType", "TargetType"];

    private static bool MayContainXamlSymbolMarkers(string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.IndexOf('=') >= 0
                || line.Contains("x:", StringComparison.Ordinal)
                || line.IndexOf('{') >= 0
                || line.Contains(".TypeName", StringComparison.Ordinal)
                || line.Contains("Binding", StringComparison.Ordinal)
                || line.Contains("Resource", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MayContainXamlEventHandlerAttribute(string line)
    {
        if (line.IndexOf('=') < 0)
            return false;

        foreach (var eventName in XamlEventAttributeNames)
        {
            if (line.Contains(eventName, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool MayContainXamlBindingMarkup(string rawText)
        => rawText.Contains("{Binding", StringComparison.Ordinal)
        || rawText.Contains("{x:Bind", StringComparison.Ordinal)
        || rawText.Contains("{TemplateBinding", StringComparison.Ordinal)
        || rawText.Contains("{CompiledBinding", StringComparison.Ordinal)
        || rawText.Contains("{ReflectionBinding", StringComparison.Ordinal);

    private static bool MayContainXamlReferenceSymbol(string rawText)
        => TextContainsAny(rawText, XamlReferenceMarkupPrefixes)
        || TextContainsAny(rawText, XamlReferenceObjectElementPrefixes)
        || (rawText.Contains("x:Reference", StringComparison.Ordinal)
            && rawText.Contains(".Name", StringComparison.Ordinal));

    private static bool MayContainXamlBindingElementNameSymbol(string rawText)
        => rawText.Contains("ElementName", StringComparison.Ordinal)
        && (MayContainXamlBindingMarkup(rawText)
            || rawText.Contains("<Binding", StringComparison.Ordinal)
            || rawText.Contains("Binding.ElementName", StringComparison.Ordinal));

    private static bool MayContainXamlBindingObjectElementSymbol(string rawText)
        => (rawText.Contains("<Binding", StringComparison.Ordinal)
            && rawText.Contains("Path", StringComparison.Ordinal))
        || rawText.Contains("Binding.Path", StringComparison.Ordinal);

    private static bool TextContainsAny(string text, IReadOnlyList<string> values)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (text.Contains(values[i], StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool MayContainWrappedXamlTypeBearingAttribute(string rawText) =>
        rawText.Contains("x:Class", StringComparison.Ordinal)
        || rawText.Contains("x:DataType", StringComparison.Ordinal)
        || rawText.Contains("TargetType", StringComparison.Ordinal);

    private static bool MayContainWrappedXamlSearchAttribute(string rawText)
    {
        if (rawText.Contains("x:Name", StringComparison.Ordinal)
            || rawText.Contains("x:Key", StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var attributeName in XamlEventAttributeNames)
        {
            if (rawText.Contains(attributeName, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static void AddWrappedXamlTypeArgumentSymbols(
        long fileId,
        string rawText,
        string[] lines,
        int[] lineStarts,
        List<SymbolRecord> symbols)
    {
        var cursor = 0;
        while (cursor < rawText.Length)
        {
            var attributeIndex = rawText.IndexOf("x:TypeArguments", cursor, StringComparison.Ordinal);
            if (attributeIndex < 0)
                break;

            var equalsIndex = rawText.IndexOf('=', attributeIndex);
            if (equalsIndex < 0)
            {
                cursor = attributeIndex + 1;
                continue;
            }

            var quoteIndex = equalsIndex + 1;
            while (quoteIndex < rawText.Length && char.IsWhiteSpace(rawText[quoteIndex]))
                quoteIndex++;

            if (quoteIndex >= rawText.Length)
                break;

            var quote = rawText[quoteIndex];
            if (quote is not ('"' or '\''))
            {
                cursor = quoteIndex + 1;
                continue;
            }

            var valueStart = quoteIndex + 1;
            var valueEnd = valueStart;
            while (valueEnd < rawText.Length && rawText[valueEnd] != quote)
                valueEnd++;

            if (valueEnd >= rawText.Length)
            {
                cursor = valueStart;
                continue;
            }

            if (FindHtmlLineNumber(lineStarts, valueEnd) == FindHtmlLineNumber(lineStarts, attributeIndex))
            {
                cursor = valueEnd + 1;
                continue;
            }

            var value = rawText[valueStart..valueEnd];
            if (value.Length > 0)
            {
                var startLine = FindHtmlLineNumber(lineStarts, attributeIndex);
                var signatureIndex = Math.Clamp(startLine - 1, 0, lines.Length - 1);
                foreach (var normalized in NormalizeXamlTypeArgumentsValue(value))
                {
                    if (!TryAddBoundedStructuredDataSymbol(symbols, new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = "class",
                        Name = normalized,
                        Line = startLine,
                        StartLine = startLine,
                        EndLine = startLine,
                        Signature = lines[signatureIndex].Trim(),
                    }))
                        return;
                }
            }

            cursor = valueEnd + 1;
        }
    }

    private static void AddWrappedXamlTypeBearingAttributeSymbols(
        long fileId,
        string rawText,
        string[] lines,
        int[] lineStarts,
        List<SymbolRecord> symbols)
    {
        // Handle XAML values that are split away from `=` onto later lines.
        // `x:Class`, `x:DataType`, and `TargetType` are intentionally kept on the
        // same normalization path as the line-based extractor so search results stay consistent.
        foreach (var attributeName in WrappedXamlTypeBearingAttributeNames)
        {
            var cursor = 0;
            while (cursor < rawText.Length)
            {
                var attributeIndex = rawText.IndexOf(attributeName, cursor, StringComparison.Ordinal);
                if (attributeIndex < 0)
                    break;

                var equalsIndex = rawText.IndexOf('=', attributeIndex);
                if (equalsIndex < 0)
                {
                    cursor = attributeIndex + 1;
                    continue;
                }

                var quoteIndex = equalsIndex + 1;
                while (quoteIndex < rawText.Length && char.IsWhiteSpace(rawText[quoteIndex]))
                    quoteIndex++;

                if (quoteIndex >= rawText.Length)
                    break;

                var quote = rawText[quoteIndex];
                if (quote is not ('"' or '\''))
                {
                    cursor = quoteIndex + 1;
                    continue;
                }

                var valueStart = quoteIndex + 1;
                var valueEnd = valueStart;
                while (valueEnd < rawText.Length && rawText[valueEnd] != quote)
                    valueEnd++;

                if (valueEnd >= rawText.Length)
                {
                    cursor = valueStart;
                    continue;
                }

                if (FindHtmlLineNumber(lineStarts, valueEnd) == FindHtmlLineNumber(lineStarts, attributeIndex))
                {
                    cursor = valueEnd + 1;
                    continue;
                }

                var value = NormalizeXamlKeyValue(rawText[valueStart..valueEnd]);
                if (value.Length > 0)
                {
                    var startLine = FindHtmlLineNumber(lineStarts, attributeIndex);
                    var signatureIndex = Math.Clamp(startLine - 1, 0, lines.Length - 1);
                    if (!TryAddBoundedStructuredDataSymbol(symbols, new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = "class",
                        Name = value,
                        Line = startLine,
                        StartLine = startLine,
                        EndLine = startLine,
                        Signature = lines[signatureIndex].Trim(),
                    }))
                        return;
                }

                cursor = valueEnd + 1;
            }
        }
    }

    private static void AddWrappedXamlSearchAttributeSymbols(
        long fileId,
        string rawText,
        string[] lines,
        int[] lineStarts,
        List<SymbolRecord> symbols)
    {
        if (rawText.Contains("x:Name", StringComparison.Ordinal))
        {
            foreach (var occurrence in EnumerateWrappedXamlAttributeValues(rawText, lineStarts, "x:Name"))
            {
                if (!AddXamlAttributeSymbol(fileId, lines, lineStarts, symbols, occurrence.AttributeIndex, "property", occurrence.Value))
                    return;
            }
        }

        if (rawText.Contains("x:Key", StringComparison.Ordinal))
        {
            foreach (var occurrence in EnumerateWrappedXamlAttributeValues(rawText, lineStarts, "x:Key"))
            {
                if (!AddXamlAttributeSymbol(fileId, lines, lineStarts, symbols, occurrence.AttributeIndex, "property", NormalizeXamlKeyValue(occurrence.Value)))
                    return;
            }
        }

        foreach (var attributeName in XamlEventAttributeNames)
        {
            if (!rawText.Contains(attributeName, StringComparison.Ordinal))
                continue;

            foreach (var occurrence in EnumerateWrappedXamlAttributeValues(rawText, lineStarts, attributeName))
            {
                if (!AddXamlAttributeSymbol(fileId, lines, lineStarts, symbols, occurrence.AttributeIndex, "function", occurrence.Value))
                    return;
            }
        }
    }

    private static IEnumerable<(int AttributeIndex, string Value)> EnumerateWrappedXamlAttributeValues(
        string rawText,
        int[] lineStarts,
        string attributeName)
    {
        var cursor = 0;
        while (cursor < rawText.Length)
        {
            var attributeIndex = rawText.IndexOf(attributeName, cursor, StringComparison.Ordinal);
            if (attributeIndex < 0)
                yield break;

            if (!TryReadXamlAttributeValue(rawText, attributeName, attributeIndex, out var valueStart, out var valueEnd))
            {
                cursor = attributeIndex + 1;
                continue;
            }

            if (FindHtmlLineNumber(lineStarts, valueEnd) != FindHtmlLineNumber(lineStarts, attributeIndex))
                yield return (attributeIndex, rawText[valueStart..valueEnd]);

            cursor = valueEnd + 1;
        }
    }

    private static bool TryReadXamlAttributeValue(
        string rawText,
        string attributeName,
        int attributeIndex,
        out int valueStart,
        out int valueEnd)
    {
        valueStart = -1;
        valueEnd = -1;

        if (!IsXamlAttributeNameMatch(rawText, attributeIndex, attributeName.Length))
            return false;

        var cursor = attributeIndex + attributeName.Length;
        while (cursor < rawText.Length && char.IsWhiteSpace(rawText[cursor]))
            cursor++;

        if (cursor >= rawText.Length || rawText[cursor] != '=')
            return false;

        cursor++;
        while (cursor < rawText.Length && char.IsWhiteSpace(rawText[cursor]))
            cursor++;

        if (cursor >= rawText.Length)
            return false;

        var quote = rawText[cursor];
        if (quote is not ('"' or '\''))
            return false;

        valueStart = cursor + 1;
        valueEnd = valueStart;
        while (valueEnd < rawText.Length && rawText[valueEnd] != quote)
            valueEnd++;

        return valueEnd < rawText.Length;
    }

    private static bool IsXamlAttributeNameMatch(string rawText, int index, int length)
    {
        if (index > 0 && IsXamlAttributeNameChar(rawText[index - 1]))
            return false;

        var after = index + length;
        return after >= rawText.Length || !IsXamlAttributeNameChar(rawText[after]);
    }

    private static bool IsXamlAttributeNameChar(char c)
        => IsXamlMarkupNameChar(c) || c == '-';

    private static bool AddXamlAttributeSymbol(
        long fileId,
        string[] lines,
        int[] lineStarts,
        List<SymbolRecord> symbols,
        int attributeIndex,
        string kind,
        string value)
    {
        value = value.Trim();
        if (value.Length == 0)
            return symbols.Count <= StructuredDataMaxSymbols;

        var startLine = FindHtmlLineNumber(lineStarts, attributeIndex);
        var signatureIndex = Math.Clamp(startLine - 1, 0, lines.Length - 1);
        return TryAddBoundedStructuredDataSymbol(symbols, new SymbolRecord
        {
            FileId = fileId,
            Kind = kind,
            Name = value,
            Line = startLine,
            StartLine = startLine,
            EndLine = startLine,
            Signature = lines[signatureIndex].Trim(),
        });
    }

    private static void AddXamlBindingElementNameSymbols(
        long fileId,
        string rawText,
        string[] lines,
        int[] lineStarts,
        List<SymbolRecord> symbols)
    {
        if (MayContainXamlBindingMarkup(rawText)
            && rawText.Contains("ElementName", StringComparison.Ordinal))
        {
            foreach (Match bindingMatch in Regex.EnumerateMatches(XamlBindingRegex, rawText))
            {
                if (!bindingMatch.Groups["kind"].Value.Equals("Binding", StringComparison.OrdinalIgnoreCase))
                    continue;

                var value = NormalizeXamlBindingElementNameValue(bindingMatch.Groups["content"].Value);
                if (value.Length == 0)
                    continue;

                if (!AddXamlAttributeSymbol(fileId, lines, lineStarts, symbols, bindingMatch.Index, "property", value))
                    return;
            }
        }

        if (rawText.Contains("<Binding", StringComparison.Ordinal)
            && rawText.Contains("ElementName", StringComparison.Ordinal))
        {
            var cursor = 0;
            while (cursor < rawText.Length)
            {
                var tagIndex = rawText.IndexOf("<Binding", cursor, StringComparison.Ordinal);
                if (tagIndex < 0)
                    break;

                var nameEnd = tagIndex + "<Binding".Length;
                if (nameEnd < rawText.Length && IsXamlAttributeNameChar(rawText[nameEnd]))
                {
                    cursor = nameEnd;
                    continue;
                }

                var tagEnd = FindXamlTagEnd(rawText, nameEnd);
                if (tagEnd < 0)
                    break;

                var elementNameAttributeIndex = IndexOfXamlAttributeInRange(rawText, "ElementName", nameEnd, tagEnd);
                if (elementNameAttributeIndex >= 0
                    && TryReadXamlAttributeValue(rawText, "ElementName", elementNameAttributeIndex, out var valueStart, out var valueEnd)
                    && valueEnd <= tagEnd)
                {
                    if (!AddXamlAttributeSymbol(
                        fileId,
                        lines,
                        lineStarts,
                        symbols,
                        elementNameAttributeIndex,
                        "property",
                        NormalizeXamlElementReferenceValue(rawText[valueStart..valueEnd])))
                    {
                        return;
                    }
                }

                cursor = tagEnd + 1;
            }
        }

        if (!rawText.Contains("Binding.ElementName", StringComparison.Ordinal))
            return;

        foreach (Match elementNameMatch in Regex.EnumerateMatches(XamlBindingElementNamePropertyElementRegex, rawText))
        {
            var value = NormalizeXamlElementReferenceValue(elementNameMatch.Groups["value"].Value);
            if (value.Length == 0)
                continue;

            if (!AddXamlAttributeSymbol(fileId, lines, lineStarts, symbols, elementNameMatch.Index, "property", value))
                return;
        }
    }

    private static string NormalizeXamlBindingElementNameValue(string content)
    {
        content = content.Trim();
        if (content.Length == 0)
            return "";

        foreach (var argument in SplitTopLevelMarkupArguments(content))
        {
            var equalsIndex = IndexOfTopLevelEquals(argument);
            if (equalsIndex < 0)
                continue;

            var argumentName = argument.AsSpan(0, equalsIndex).Trim();
            if (!argumentName.Equals("ElementName", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = NormalizeXamlElementReferenceValue(argument[(equalsIndex + 1)..]);
            if (value.Length > 0)
                return value;
        }

        return "";
    }

    private static string NormalizeXamlElementReferenceValue(string value)
    {
        return NormalizeXamlMarkupValue(value);
    }

    private static void AddXamlBindingObjectElementSymbols(
        long fileId,
        string rawText,
        string[] lines,
        int[] lineStarts,
        List<SymbolRecord> symbols)
    {
        if (rawText.Contains("<Binding", StringComparison.Ordinal)
            && rawText.Contains("Path", StringComparison.Ordinal))
        {
            var cursor = 0;
            while (cursor < rawText.Length)
            {
                var tagIndex = rawText.IndexOf("<Binding", cursor, StringComparison.Ordinal);
                if (tagIndex < 0)
                    break;

                var nameEnd = tagIndex + "<Binding".Length;
                if (nameEnd < rawText.Length && IsXamlAttributeNameChar(rawText[nameEnd]))
                {
                    cursor = nameEnd;
                    continue;
                }

                var tagEnd = FindXamlTagEnd(rawText, nameEnd);
                if (tagEnd < 0)
                    break;

                var pathAttributeIndex = IndexOfXamlAttributeInRange(rawText, "Path", nameEnd, tagEnd);
                if (pathAttributeIndex >= 0
                    && TryReadXamlAttributeValue(rawText, "Path", pathAttributeIndex, out var valueStart, out var valueEnd)
                    && valueEnd <= tagEnd)
                {
                    if (!AddXamlAttributeSymbol(
                        fileId,
                        lines,
                        lineStarts,
                        symbols,
                        pathAttributeIndex,
                        "property",
                        NormalizeXamlBindingPathValue(rawText[valueStart..valueEnd])))
                    {
                        return;
                    }
                }

                cursor = tagEnd + 1;
            }
        }

        if (!rawText.Contains("Binding.Path", StringComparison.Ordinal))
            return;

        foreach (Match pathMatch in Regex.EnumerateMatches(XamlBindingPathPropertyElementRegex, rawText))
        {
            var value = NormalizeXamlBindingPathValue(pathMatch.Groups["value"].Value);
            if (value.Length == 0)
                continue;

            var startLine = FindHtmlLineNumber(lineStarts, pathMatch.Index);
            var signatureIndex = Math.Clamp(startLine - 1, 0, lines.Length - 1);
            if (!TryAddBoundedStructuredDataSymbol(symbols, new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = value,
                Line = startLine,
                StartLine = startLine,
                EndLine = startLine,
                Signature = lines[signatureIndex].Trim(),
            }))
                return;
        }
    }

    private static int FindXamlTagEnd(string rawText, int startIndex)
    {
        char? quote = null;
        for (var i = startIndex; i < rawText.Length; i++)
        {
            var ch = rawText[i];
            if (quote is { } activeQuote)
            {
                if (ch == activeQuote)
                    quote = null;
                continue;
            }

            if (ch is '"' or '\'')
            {
                quote = ch;
                continue;
            }

            if (ch == '>')
                return i;
        }

        return -1;
    }

    private static int IndexOfXamlAttributeInRange(string rawText, string attributeName, int startIndex, int endIndex)
    {
        char? quote = null;
        for (var i = startIndex; i < endIndex; i++)
        {
            var ch = rawText[i];
            if (quote is { } activeQuote)
            {
                if (ch == activeQuote)
                    quote = null;
                continue;
            }

            if (ch is '"' or '\'')
            {
                quote = ch;
                continue;
            }

            if (!rawText.AsSpan(i, endIndex - i).StartsWith(attributeName, StringComparison.Ordinal))
                continue;

            if (IsXamlAttributeNameMatch(rawText, i, attributeName.Length)
                && TryReadXamlAttributeValue(rawText, attributeName, i, out _, out var valueEnd)
                && valueEnd <= endIndex)
            {
                return i;
            }
        }

        return -1;
    }

    private static void AddXamlTypeObjectElementSymbols(
        long fileId,
        string rawText,
        string[] lines,
        int[] lineStarts,
        List<SymbolRecord> symbols)
    {
        if (!rawText.Contains("x:Type", StringComparison.Ordinal)
            || !rawText.Contains("TypeName", StringComparison.Ordinal))
        {
            return;
        }

        foreach (Match typeMatch in Regex.EnumerateMatches(XamlTypeObjectElementRegex, rawText))
        {
            var value = NormalizeXamlKeyValue(typeMatch.Groups["value"].Value);
            if (value.Length == 0)
                continue;

            var startLine = FindHtmlLineNumber(lineStarts, typeMatch.Index);
            var signatureIndex = Math.Clamp(startLine - 1, 0, lines.Length - 1);
            if (!TryAddBoundedStructuredDataSymbol(symbols, new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = value,
                Line = startLine,
                StartLine = startLine,
                EndLine = startLine,
                Signature = lines[signatureIndex].Trim(),
            }))
                return;
        }
    }

    private static void AddXamlTypePropertyElementSymbols(
        long fileId,
        string rawText,
        string[] lines,
        int[] lineStarts,
        List<SymbolRecord> symbols)
    {
        if (!rawText.Contains(".TypeName", StringComparison.Ordinal))
            return;

        foreach (Match typeMatch in Regex.EnumerateMatches(XamlTypePropertyElementRegex, rawText))
        {
            var value = NormalizeXamlKeyValue(typeMatch.Groups["value"].Value);
            if (value.Length == 0)
                continue;

            var startLine = FindHtmlLineNumber(lineStarts, typeMatch.Index);
            var signatureIndex = Math.Clamp(startLine - 1, 0, lines.Length - 1);
            if (!TryAddBoundedStructuredDataSymbol(symbols, new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = value,
                Line = startLine,
                StartLine = startLine,
                EndLine = startLine,
                Signature = lines[signatureIndex].Trim(),
            }))
                return;
        }
    }

    private static void AddXamlTypeMarkupSymbols(
        long fileId,
        string rawText,
        string[] lines,
        int[] lineStarts,
        List<SymbolRecord> symbols)
    {
        if (rawText.Contains("{x:TypeExtension", StringComparison.Ordinal))
            AddXamlMarkupExtensionTypeSymbols(fileId, rawText, lines, lineStarts, symbols, "{x:TypeExtension", false);
        if (symbols.Count > StructuredDataMaxSymbols)
            return;
        if (rawText.Contains("{x:Type", StringComparison.Ordinal))
            AddXamlMarkupExtensionTypeSymbols(fileId, rawText, lines, lineStarts, symbols, "{x:Type", true);
    }

    private static void AddXamlMarkupExtensionTypeSymbols(
        long fileId,
        string rawText,
        string[] lines,
        int[] lineStarts,
        List<SymbolRecord> symbols,
        string prefix,
        bool rejectNameCharAfterPrefix)
    {
        var cursor = 0;
        while (cursor < rawText.Length)
        {
            var braceIndex = rawText.IndexOf(prefix, cursor, StringComparison.Ordinal);
            if (braceIndex < 0)
                break;

            var afterPrefix = braceIndex + prefix.Length;
            if (rejectNameCharAfterPrefix
                && afterPrefix < rawText.Length
                && IsXamlMarkupNameChar(rawText[afterPrefix]))
            {
                cursor = afterPrefix;
                continue;
            }

            var closingBraceIndex = FindMatchingBrace(rawText, braceIndex);
            if (closingBraceIndex < 0)
            {
                cursor = braceIndex + 1;
                continue;
            }

            if (ShouldSkipXamlMarkupExtensionSymbol(rawText, braceIndex))
            {
                cursor = closingBraceIndex + 1;
                continue;
            }

            var value = NormalizeXamlMarkupValue(rawText[braceIndex..(closingBraceIndex + 1)]);
            if (value.Length > 0)
            {
                var startLine = FindHtmlLineNumber(lineStarts, braceIndex);
                var signatureIndex = Math.Clamp(startLine - 1, 0, lines.Length - 1);
                if (!TryAddBoundedStructuredDataSymbol(symbols, new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "class",
                    Name = value,
                    Line = startLine,
                    StartLine = startLine,
                    EndLine = startLine,
                    Signature = lines[signatureIndex].Trim(),
                }))
                    return;
            }

            cursor = closingBraceIndex + 1;
        }
    }

    private static bool ShouldSkipXamlMarkupExtensionSymbol(string rawText, int braceIndex)
    {
        var tagStart = rawText.LastIndexOf('<', braceIndex);
        if (tagStart < 0 || tagStart > braceIndex)
            return false;

        var tagEnd = rawText.IndexOf('>', tagStart);
        if (tagEnd >= 0 && tagEnd < braceIndex)
            return false;

        var tagSlice = rawText[tagStart..braceIndex];
        if (tagSlice.IndexOf("<x:Type", StringComparison.OrdinalIgnoreCase) >= 0
            || tagSlice.IndexOf("<x:TypeExtension", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        if (tagSlice.IndexOf("TargetType=", StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        return false;
    }

    private static void AddXamlStaticMemberTypeSymbols(
        long fileId,
        string rawText,
        string[] lines,
        int[] lineStarts,
        List<SymbolRecord> symbols)
    {
        if (!rawText.Contains("{x:Static", StringComparison.Ordinal))
            return;

        var cursor = 0;
        while (cursor < rawText.Length)
        {
            var braceIndex = rawText.IndexOf("{x:Static", cursor, StringComparison.Ordinal);
            if (braceIndex < 0)
                break;

            var closingBraceIndex = FindMatchingBrace(rawText, braceIndex);
            if (closingBraceIndex < 0)
            {
                cursor = braceIndex + 1;
                continue;
            }

            var value = NormalizeXamlMarkupValue(rawText[braceIndex..(closingBraceIndex + 1)]);
            var lastDot = value.LastIndexOf('.');
            if (lastDot > 0)
            {
                var typeName = value[..lastDot].Trim();
                if (typeName.Length > 0)
                {
                    var startLine = FindHtmlLineNumber(lineStarts, braceIndex);
                    var signatureIndex = Math.Clamp(startLine - 1, 0, lines.Length - 1);
                    if (!TryAddBoundedStructuredDataSymbol(symbols, new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = "class",
                        Name = typeName,
                        Line = startLine,
                        StartLine = startLine,
                        EndLine = startLine,
                        Signature = lines[signatureIndex].Trim(),
                    }))
                        return;
                }
            }

            cursor = closingBraceIndex + 1;
        }
    }

    private static void AddXamlReferenceSymbols(
        long fileId,
        string rawText,
        string[] lines,
        int[] lineStarts,
        List<SymbolRecord> symbols)
    {
        foreach (var prefix in XamlReferenceMarkupPrefixes)
        {
            if (rawText.Contains(prefix, StringComparison.Ordinal))
                AddXamlReferenceMarkupSymbols(fileId, rawText, lines, lineStarts, symbols, prefix);
            if (symbols.Count > StructuredDataMaxSymbols)
                return;
        }

        foreach (var prefix in XamlReferenceObjectElementPrefixes)
        {
            if (rawText.Contains(prefix, StringComparison.Ordinal))
                AddXamlReferenceObjectElementSymbols(fileId, rawText, lines, lineStarts, symbols, prefix);
            if (symbols.Count > StructuredDataMaxSymbols)
                return;
        }

        if (!rawText.Contains("x:Reference", StringComparison.Ordinal)
            || !rawText.Contains(".Name", StringComparison.Ordinal))
        {
            return;
        }

        foreach (Match nameMatch in Regex.EnumerateMatches(XamlReferenceNamePropertyElementRegex, rawText))
        {
            var value = NormalizeXamlElementReferenceValue(nameMatch.Groups["value"].Value);
            if (value.Length == 0)
                continue;

            if (!AddXamlAttributeSymbol(fileId, lines, lineStarts, symbols, nameMatch.Index, "property", value))
                return;
        }
    }

    private static void AddXamlReferenceMarkupSymbols(
        long fileId,
        string rawText,
        string[] lines,
        int[] lineStarts,
        List<SymbolRecord> symbols,
        string prefix)
    {
        var cursor = 0;
        while (cursor < rawText.Length)
        {
            var braceIndex = rawText.IndexOf(prefix, cursor, StringComparison.Ordinal);
            if (braceIndex < 0)
                break;

            var afterPrefix = braceIndex + prefix.Length;
            if (afterPrefix < rawText.Length && IsXamlMarkupNameChar(rawText[afterPrefix]))
            {
                cursor = afterPrefix;
                continue;
            }

            var closingBraceIndex = FindMatchingBrace(rawText, braceIndex);
            if (closingBraceIndex < 0)
            {
                cursor = braceIndex + 1;
                continue;
            }

            var value = NormalizeXamlRequiredMarkupArgumentValue(rawText[braceIndex..(closingBraceIndex + 1)]);
            if (value.Length > 0
                && !AddXamlAttributeSymbol(fileId, lines, lineStarts, symbols, braceIndex, "property", value))
            {
                return;
            }

            cursor = closingBraceIndex + 1;
        }
    }

    private static void AddXamlReferenceObjectElementSymbols(
        long fileId,
        string rawText,
        string[] lines,
        int[] lineStarts,
        List<SymbolRecord> symbols,
        string prefix)
    {
        var cursor = 0;
        while (cursor < rawText.Length)
        {
            var tagIndex = rawText.IndexOf(prefix, cursor, StringComparison.Ordinal);
            if (tagIndex < 0)
                break;

            var nameEnd = tagIndex + prefix.Length;
            if (nameEnd < rawText.Length && IsXamlAttributeNameChar(rawText[nameEnd]))
            {
                cursor = nameEnd;
                continue;
            }

            var tagEnd = FindXamlTagEnd(rawText, nameEnd);
            if (tagEnd < 0)
                break;

            var nameAttributeIndex = IndexOfXamlAttributeInRange(rawText, "Name", nameEnd, tagEnd);
            if (nameAttributeIndex >= 0
                && TryReadXamlAttributeValue(rawText, "Name", nameAttributeIndex, out var valueStart, out var valueEnd)
                && valueEnd <= tagEnd)
            {
                if (!AddXamlAttributeSymbol(
                    fileId,
                    lines,
                    lineStarts,
                    symbols,
                    nameAttributeIndex,
                    "property",
                    NormalizeXamlElementReferenceValue(rawText[valueStart..valueEnd])))
                {
                    return;
                }
            }

            cursor = tagEnd + 1;
        }
    }

    private static void AddXamlResourceReferenceSymbols(
        long fileId,
        string rawText,
        string[] lines,
        int[] lineStarts,
        List<SymbolRecord> symbols)
    {
        foreach (var prefix in XamlResourceReferenceMarkupPrefixes)
        {
            if (rawText.Contains(prefix, StringComparison.Ordinal))
                AddXamlResourceReferenceSymbols(fileId, rawText, lines, lineStarts, symbols, prefix);
            if (symbols.Count > StructuredDataMaxSymbols)
                return;
        }
    }

    private static void AddXamlResourceReferenceSymbols(
        long fileId,
        string rawText,
        string[] lines,
        int[] lineStarts,
        List<SymbolRecord> symbols,
        string prefix)
    {
        var cursor = 0;
        while (cursor < rawText.Length)
        {
            var braceIndex = rawText.IndexOf(prefix, cursor, StringComparison.Ordinal);
            if (braceIndex < 0)
                break;

            var afterPrefix = braceIndex + prefix.Length;
            if (afterPrefix < rawText.Length && IsXamlMarkupNameChar(rawText[afterPrefix]))
            {
                cursor = afterPrefix;
                continue;
            }

            var closingBraceIndex = FindMatchingBrace(rawText, braceIndex);
            if (closingBraceIndex < 0)
            {
                cursor = braceIndex + 1;
                continue;
            }

            var value = NormalizeXamlResourceReferenceValue(rawText[braceIndex..(closingBraceIndex + 1)]);
            if (value.Length > 0)
            {
                var startLine = FindHtmlLineNumber(lineStarts, braceIndex);
                var signatureIndex = Math.Clamp(startLine - 1, 0, lines.Length - 1);
                if (!TryAddBoundedStructuredDataSymbol(symbols, new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "property",
                    Name = value,
                    Line = startLine,
                    StartLine = startLine,
                    EndLine = startLine,
                    Signature = lines[signatureIndex].Trim(),
                }))
                    return;
            }

            cursor = closingBraceIndex + 1;
        }
    }

    private static string NormalizeXamlResourceReferenceValue(string value)
        => NormalizeXamlRequiredMarkupArgumentValue(value);

    private static string NormalizeXamlRequiredMarkupArgumentValue(string value)
    {
        value = value.Trim();
        if (value.Length == 0 || value[0] != '{')
            return value;

        var closingBraceIndex = FindMatchingBrace(value, 0);
        if (closingBraceIndex < 0)
            return "";

        var content = value.AsSpan(1, closingBraceIndex - 1).Trim().ToString();
        var payloadStart = FindTopLevelMarkupPayloadStart(content);
        if (payloadStart < 0)
            return "";

        var payloadStartIndex = SkipXamlMarkupWhitespace(content, payloadStart + 1);
        if (payloadStartIndex >= content.Length)
            return "";

        var payload = content[payloadStartIndex..];
        foreach (var argument in SplitTopLevelMarkupArguments(payload))
        {
            var normalized = NormalizeXamlMarkupArgument(argument);
            if (normalized.Length > 0)
                return normalized;
        }

        return "";
    }

    private static bool IsXamlMarkupNameChar(char c)
        => char.IsLetterOrDigit(c) || c == '_' || c == ':' || c == '.';

    private static string NormalizeXamlKeyValue(string value)
    {
        var trimmed = value.AsSpan().Trim();
        if (trimmed.Length < 2 || trimmed[0] != '{' || trimmed[^1] != '}')
            return trimmed.ToString();

        return NormalizeXamlMarkupExtensionContent(trimmed[1..^1].Trim().ToString());
    }

    private static string NormalizeXamlBindingValue(string kind, string content)
    {
        var kindSpan = kind.AsSpan().Trim();
        var contentSpan = content.AsSpan().Trim();
        if (contentSpan.IsEmpty)
            return "";

        var contentText = contentSpan.ToString();
        var isTemplateBinding = kindSpan.Equals("TemplateBinding", StringComparison.OrdinalIgnoreCase);
        var payload = kindSpan.Equals("x:Bind", StringComparison.OrdinalIgnoreCase)
            ? $"x:Bind {contentText}"
            : isTemplateBinding
                ? $"TemplateBinding {contentText}"
                : $"Binding {contentText}";

        var firstPath = NormalizeXamlBindingPath(payload, isTemplateBinding);
        return firstPath.Length > 0 ? firstPath : contentText;
    }

    private static string NormalizeXamlBindingPath(string value, bool allowPropertyArgument)
    {
        var trimmed = value.AsSpan().Trim();
        if (trimmed.IsEmpty)
            return "";

        var payloadStart = FindTopLevelMarkupPayloadStart(trimmed);
        if (payloadStart < 0)
            return trimmed.ToString();

        var payloadStartIndex = SkipXamlMarkupWhitespace(trimmed, payloadStart + 1);
        if (payloadStartIndex >= trimmed.Length)
            return trimmed.ToString();

        var payload = trimmed[payloadStartIndex..].ToString();
        string? fallback = null;
        foreach (var argument in SplitTopLevelMarkupArguments(payload))
        {
            var equalsIndex = IndexOfTopLevelEquals(argument);
            if (equalsIndex >= 0)
            {
                var argumentName = argument.AsSpan(0, equalsIndex).Trim();
                if (argumentName.Equals("Path", StringComparison.OrdinalIgnoreCase)
                    || (allowPropertyArgument && argumentName.Equals("Property", StringComparison.OrdinalIgnoreCase)))
                {
                    var pathValue = NormalizeXamlBindingPathValue(argument[(equalsIndex + 1)..]);
                    if (pathValue.Length > 0)
                        return pathValue;
                }

                continue;
            }

            var normalized = NormalizeXamlBindingArgument(argument);
            if (normalized.Length == 0)
                continue;

            fallback ??= normalized;
        }

        return fallback ?? trimmed.ToString();
    }

    private static string NormalizeXamlBindingArgument(string value)
    {
        var trimmed = value.AsSpan().Trim();
        if (trimmed.IsEmpty)
            return "";

        var equalsIndex = IndexOfTopLevelEquals(trimmed);
        if (equalsIndex >= 0)
        {
            var name = trimmed[..equalsIndex].Trim();
            var normalized = trimmed[(equalsIndex + 1)..].Trim();
            if (name.Equals("Path", StringComparison.OrdinalIgnoreCase))
                return NormalizeXamlBindingPathValue(normalized.ToString());
            if (!normalized.IsEmpty)
                return NormalizeXamlMarkupValue(normalized.ToString());
        }

        return NormalizeXamlBindingPathValue(trimmed.ToString());
    }

    private static string NormalizeXamlBindingPathValue(string value)
    {
        var trimmed = value.AsSpan().Trim();
        if (trimmed.IsEmpty)
            return "";

        value = NormalizeXamlMarkupValue(trimmed.ToString());
        var lastDot = value.LastIndexOf('.');
        if (lastDot >= 0 && lastDot + 1 < value.Length)
            return value.AsSpan(lastDot + 1).Trim().ToString();

        return value.AsSpan().Trim().ToString();
    }

    private static string NormalizeXamlMarkupValue(string value)
    {
        var trimmed = value.AsSpan().Trim();
        if (trimmed.IsEmpty || trimmed[0] != '{')
            return trimmed.ToString();

        var closingBraceIndex = FindMatchingBrace(trimmed, 0);
        if (closingBraceIndex < 0)
            return trimmed.ToString();

        var normalized = NormalizeXamlMarkupExtensionContent(trimmed.Slice(1, closingBraceIndex - 1).Trim().ToString());
        var suffix = trimmed[(closingBraceIndex + 1)..].Trim();
        return suffix.IsEmpty ? normalized : string.Concat(normalized, suffix.ToString());
    }

    private static string NormalizeXamlMarkupExtensionContent(string value)
    {
        var trimmed = value.AsSpan().Trim();
        if (trimmed.IsEmpty)
            return "";

        var payloadStart = FindTopLevelMarkupPayloadStart(trimmed);
        if (payloadStart < 0)
            return trimmed.ToString();

        var payloadStartIndex = SkipXamlMarkupWhitespace(trimmed, payloadStart + 1);
        if (payloadStartIndex >= trimmed.Length)
            return trimmed.ToString();

        var payload = trimmed[payloadStartIndex..].ToString();
        foreach (var argument in SplitTopLevelMarkupArguments(payload))
        {
            var normalized = NormalizeXamlMarkupArgument(argument);
            if (normalized.Length > 0)
                return normalized;
        }

        return trimmed.ToString();
    }

    private static IEnumerable<string> NormalizeXamlTypeArgumentsValue(string value)
    {
        var trimmed = value.AsSpan().Trim();
        if (trimmed.IsEmpty)
            yield break;

        foreach (var argument in SplitTopLevelTypeArguments(trimmed.ToString()))
        {
            var normalized = NormalizeXamlMarkupArgument(argument);
            if (normalized.Length > 0)
            {
                foreach (var expanded in ExpandXamlTypeArgument(normalized))
                    yield return expanded;
            }
        }
    }

    private static IEnumerable<string> ExpandXamlTypeArgument(string value)
    {
        // Peel nested generic constructor shapes recursively so XAML type arguments like
        // `Outer(Inner(A, B), C)` still surface every referenced type name.
        value = value.Trim();
        if (value.Length == 0)
            yield break;

        var payloadStart = FindTopLevelTypeConstructorStart(value);
        if (payloadStart < 0)
        {
            yield return value;
            yield break;
        }

        var payloadEnd = FindMatchingTypeConstructorEnd(value, payloadStart);
        if (payloadEnd < 0)
        {
            yield return value;
            yield break;
        }

        var prefix = value.AsSpan(0, payloadStart).Trim();
        if (!prefix.IsEmpty)
            yield return prefix.ToString();

        var payload = value.AsSpan(payloadStart + 1, payloadEnd - payloadStart - 1).Trim();
        if (!payload.IsEmpty)
        {
            foreach (var nestedArgument in SplitTopLevelTypeArguments(payload.ToString()))
            {
                var nestedNormalized = NormalizeXamlMarkupArgument(nestedArgument);
                if (nestedNormalized.Length == 0)
                    continue;

                foreach (var nestedExpanded in ExpandXamlTypeArgument(nestedNormalized))
                    yield return nestedExpanded;
            }
        }

        var suffix = value.AsSpan(payloadEnd + 1).Trim();
        if (!suffix.IsEmpty)
        {
            foreach (var expanded in ExpandXamlTypeArgument(suffix.ToString()))
                yield return expanded;
        }
    }

    private static string NormalizeXamlMarkupArgument(string value)
    {
        var trimmed = value.AsSpan().Trim();
        if (trimmed.IsEmpty)
            return "";

        var equalsIndex = IndexOfTopLevelEquals(trimmed);
        if (equalsIndex >= 0)
            return NormalizeXamlMarkupValue(trimmed[(equalsIndex + 1)..].ToString());

        return NormalizeXamlMarkupValue(trimmed.ToString());
    }

    private static int FindTopLevelMarkupPayloadStart(string value) => FindTopLevelMarkupPayloadStart(value.AsSpan());

    private static int FindTopLevelMarkupPayloadStart(ReadOnlySpan<char> value)
    {
        var braceDepth = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch == '{')
            {
                braceDepth++;
                continue;
            }
            if (ch == '}')
            {
                if (braceDepth > 0)
                    braceDepth--;
                continue;
            }
            if (braceDepth == 0 && (char.IsWhiteSpace(ch) || ch == ','))
                return i;
        }

        return -1;
    }

    private static int SkipXamlMarkupWhitespace(string value, int start) => SkipXamlMarkupWhitespace(value.AsSpan(), start);

    private static int SkipXamlMarkupWhitespace(ReadOnlySpan<char> value, int start)
    {
        start = Math.Clamp(start, 0, value.Length);
        while (start < value.Length && char.IsWhiteSpace(value[start]))
            start++;

        return start;
    }

    private static int FindTopLevelTypeConstructorStart(string value)
    {
        var braceDepth = 0;
        var parenDepth = 0;
        var angleDepth = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch == '{')
            {
                braceDepth++;
                continue;
            }
            if (ch == '}')
            {
                if (braceDepth > 0)
                    braceDepth--;
                continue;
            }
            if (ch == '(' || ch == '<')
            {
                if (braceDepth == 0 && parenDepth == 0 && angleDepth == 0)
                    return i;

                if (ch == '(')
                    parenDepth++;
                else
                    angleDepth++;
                continue;
            }
            if (ch == ')')
            {
                if (parenDepth > 0)
                    parenDepth--;
                continue;
            }
            if (ch == '>')
            {
                if (angleDepth > 0)
                    angleDepth--;
                continue;
            }
        }

        return -1;
    }

    private static int FindMatchingTypeConstructorEnd(string value, int startIndex)
    {
        var open = value[startIndex];
        var close = open == '(' ? ')' : '>';
        var depth = 0;
        var braceDepth = 0;
        for (var i = startIndex; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch == '{')
            {
                braceDepth++;
                continue;
            }
            if (ch == '}')
            {
                if (braceDepth > 0)
                    braceDepth--;
                continue;
            }
            if (braceDepth > 0)
                continue;

            if (ch == open)
            {
                depth++;
                continue;
            }
            if (ch == close)
            {
                depth--;
                if (depth == 0)
                    return i;
            }
        }

        return -1;
    }

    private static int IndexOfTopLevelEquals(string value) => IndexOfTopLevelEquals(value.AsSpan());

    private static int IndexOfTopLevelEquals(ReadOnlySpan<char> value)
    {
        var braceDepth = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch == '{')
            {
                braceDepth++;
                continue;
            }
            if (ch == '}')
            {
                if (braceDepth > 0)
                    braceDepth--;
                continue;
            }
            if (braceDepth == 0 && ch == '=')
                return i;
        }

        return -1;
    }

    private static IEnumerable<string> SplitTopLevelMarkupArguments(string value)
    {
        var braceDepth = 0;
        char? quote = null;
        var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (quote is { } activeQuote)
            {
                if (ch == activeQuote)
                    quote = null;
                continue;
            }
            if (ch is '"' or '\'')
            {
                quote = ch;
                continue;
            }
            if (ch == '{')
            {
                braceDepth++;
                continue;
            }
            if (ch == '}')
            {
                if (braceDepth > 0)
                    braceDepth--;
                continue;
            }
            if (braceDepth == 0 && ch == ',')
            {
                var segment = value.AsSpan(start, i - start).Trim();
                if (!segment.IsEmpty)
                    yield return segment.ToString();
                start = i + 1;
            }
        }

        var tail = value.AsSpan(start).Trim();
        if (!tail.IsEmpty)
            yield return tail.ToString();
    }

    private static IEnumerable<string> SplitTopLevelTypeArguments(string value)
    {
        var braceDepth = 0;
        var parenDepth = 0;
        var angleDepth = 0;
        var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch == '{')
            {
                braceDepth++;
                continue;
            }
            if (ch == '}')
            {
                if (braceDepth > 0)
                    braceDepth--;
                continue;
            }
            if (ch == '(')
            {
                parenDepth++;
                continue;
            }
            if (ch == ')')
            {
                if (parenDepth > 0)
                    parenDepth--;
                continue;
            }
            if (ch == '<')
            {
                angleDepth++;
                continue;
            }
            if (ch == '>')
            {
                if (angleDepth > 0)
                    angleDepth--;
                continue;
            }
            if (braceDepth == 0 && parenDepth == 0 && angleDepth == 0 && ch == ',')
            {
                var segment = value.AsSpan(start, i - start).Trim();
                if (!segment.IsEmpty)
                    yield return segment.ToString();
                start = i + 1;
            }
        }

        var tail = value.AsSpan(start).Trim();
        if (!tail.IsEmpty)
            yield return tail.ToString();
    }

    private static int FindMatchingBrace(string value, int startIndex) => FindMatchingBrace(value.AsSpan(), startIndex);

    private static int FindMatchingBrace(ReadOnlySpan<char> value, int startIndex)
    {
        var braceDepth = 0;
        for (var i = startIndex; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch == '{')
            {
                braceDepth++;
                continue;
            }
            if (ch == '}')
            {
                braceDepth--;
                if (braceDepth == 0)
                    return i;
            }
        }

        return -1;
    }

}
