using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using System.Runtime.CompilerServices;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{

    private static List<SymbolRecord> ExtractXmlSymbols(long fileId, string rawText, string[] lines)
    {
        if (!XamlReferenceExtractor.IsXaml(lines))
            return ExtractGenericXmlSymbols(fileId, rawText, lines);
        if (!MayContainXamlSymbolMarkers(lines))
            return [];

        if (TryGetXmlStructureIssue(rawText, out _))
            return [];

        int[]? lineStarts = null;

        var symbols = CreateSymbolListForLines(lines.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var hasAttributeAssignment = line.IndexOf('=') >= 0;
            string? signature = null;
            string Signature() => signature ??= line.Trim();

            if (hasAttributeAssignment && line.Contains("x:Class", StringComparison.Ordinal))
            {
                foreach (Match classMatch in Regex.EnumerateMatches(XamlClassRegex, line))
                {
                    var value = classMatch.Groups["value"].ValueSpan.Trim().ToString();
                    if (value.Length == 0)
                        continue;
                    if (TryAddBoundedStructuredDataSymbol(symbols, new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = "class",
                        Name = value,
                        Line = i + 1,
                        StartLine = i + 1,
                        EndLine = i + 1,
                        Signature = Signature(),
                    }))
                        continue;

                    return TrimStructuredDataSymbols(symbols, fileId, "structured_data_xml_symbol_budget_exceeded", lines);
                }
            }

            if (hasAttributeAssignment && line.Contains("x:DataType", StringComparison.Ordinal))
            {
                foreach (Match dataTypeMatch in Regex.EnumerateMatches(XamlDataTypeRegex, line))
                {
                    var value = NormalizeXamlKeyValue(dataTypeMatch.Groups["value"].Value);
                    if (value.Length == 0)
                        continue;
                    if (TryAddBoundedStructuredDataSymbol(symbols, new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = "class",
                        Name = value,
                        Line = i + 1,
                        StartLine = i + 1,
                        EndLine = i + 1,
                        Signature = Signature(),
                    }))
                        continue;

                    return TrimStructuredDataSymbols(symbols, fileId, "structured_data_xml_symbol_budget_exceeded", lines);
                }
            }

            if (hasAttributeAssignment && line.Contains("x:TypeArguments", StringComparison.Ordinal))
            {
                foreach (Match typeArgumentsMatch in Regex.EnumerateMatches(XamlTypeArgumentsRegex, line))
                {
                    foreach (var value in NormalizeXamlTypeArgumentsValue(typeArgumentsMatch.Groups["value"].Value))
                    {
                        if (TryAddBoundedStructuredDataSymbol(symbols, new SymbolRecord
                        {
                            FileId = fileId,
                            Kind = "class",
                            Name = value,
                            Line = i + 1,
                            StartLine = i + 1,
                            EndLine = i + 1,
                            Signature = Signature(),
                        }))
                            continue;

                        return TrimStructuredDataSymbols(symbols, fileId, "structured_data_xml_symbol_budget_exceeded", lines);
                    }
                }
            }

            if (hasAttributeAssignment && line.Contains("TargetType", StringComparison.Ordinal))
            {
                foreach (Match targetTypeMatch in Regex.EnumerateMatches(XamlTargetTypeRegex, line))
                {
                    var value = NormalizeXamlKeyValue(targetTypeMatch.Groups["value"].Value);
                    if (value.Length == 0)
                        continue;
                    if (TryAddBoundedStructuredDataSymbol(symbols, new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = "class",
                        Name = value,
                        Line = i + 1,
                        StartLine = i + 1,
                        EndLine = i + 1,
                        Signature = Signature(),
                    }))
                        continue;

                    return TrimStructuredDataSymbols(symbols, fileId, "structured_data_xml_symbol_budget_exceeded", lines);
                }
            }

            if (hasAttributeAssignment && line.Contains("x:Name", StringComparison.Ordinal))
            {
                foreach (Match nameMatch in Regex.EnumerateMatches(XamlNameRegex, line))
                {
                    var value = nameMatch.Groups["value"].ValueSpan.Trim().ToString();
                    if (value.Length == 0)
                        continue;
                    if (TryAddBoundedStructuredDataSymbol(symbols, new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = "property",
                        Name = value,
                        Line = i + 1,
                        StartLine = i + 1,
                        EndLine = i + 1,
                        Signature = Signature(),
                    }))
                        continue;

                    return TrimStructuredDataSymbols(symbols, fileId, "structured_data_xml_symbol_budget_exceeded", lines);
                }
            }

            if (hasAttributeAssignment && line.Contains("x:Key", StringComparison.Ordinal))
            {
                foreach (Match keyMatch in Regex.EnumerateMatches(XamlKeyRegex, line))
                {
                    var value = NormalizeXamlKeyValue(keyMatch.Groups["value"].Value);
                    if (value.Length == 0)
                        continue;
                    if (TryAddBoundedStructuredDataSymbol(symbols, new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = "property",
                        Name = value,
                        Line = i + 1,
                        StartLine = i + 1,
                        EndLine = i + 1,
                        Signature = Signature(),
                    }))
                        continue;

                    return TrimStructuredDataSymbols(symbols, fileId, "structured_data_xml_symbol_budget_exceeded", lines);
                }
            }

            if (MayContainXamlEventHandlerAttribute(line))
            {
                foreach (Match handlerMatch in Regex.EnumerateMatches(XamlEventHandlerRegex, line))
                {
                    var value = handlerMatch.Groups["value"].ValueSpan.Trim().ToString();
                    if (value.Length == 0)
                        continue;
                    if (TryAddBoundedStructuredDataSymbol(symbols, new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = "function",
                        Name = value,
                        Line = i + 1,
                        StartLine = i + 1,
                        EndLine = i + 1,
                        Signature = Signature(),
                    }))
                        continue;

                    return TrimStructuredDataSymbols(symbols, fileId, "structured_data_xml_symbol_budget_exceeded", lines);
                }
            }
        }

        if (symbols.Count < StructuredDataMaxSymbols
            && rawText.Contains("x:TypeArguments", StringComparison.Ordinal))
        {
            AddWrappedXamlTypeArgumentSymbols(fileId, rawText, lines, lineStarts ??= BuildLineStarts(lines), symbols);
        }
        if (symbols.Count < StructuredDataMaxSymbols
            && MayContainWrappedXamlTypeBearingAttribute(rawText))
        {
            AddWrappedXamlTypeBearingAttributeSymbols(fileId, rawText, lines, lineStarts ??= BuildLineStarts(lines), symbols);
        }
        if (symbols.Count < StructuredDataMaxSymbols
            && MayContainWrappedXamlSearchAttribute(rawText))
        {
            AddWrappedXamlSearchAttributeSymbols(fileId, rawText, lines, lineStarts ??= BuildLineStarts(lines), symbols);
        }
        if (symbols.Count < StructuredDataMaxSymbols
            && rawText.Contains("x:Type", StringComparison.Ordinal)
            && rawText.Contains("TypeName", StringComparison.Ordinal))
        {
            AddXamlTypeObjectElementSymbols(fileId, rawText, lines, lineStarts ??= BuildLineStarts(lines), symbols);
        }
        if (symbols.Count < StructuredDataMaxSymbols
            && rawText.Contains(".TypeName", StringComparison.Ordinal))
        {
            AddXamlTypePropertyElementSymbols(fileId, rawText, lines, lineStarts ??= BuildLineStarts(lines), symbols);
        }
        if (symbols.Count < StructuredDataMaxSymbols
            && rawText.Contains("{x:Type", StringComparison.Ordinal))
        {
            AddXamlTypeMarkupSymbols(fileId, rawText, lines, lineStarts ??= BuildLineStarts(lines), symbols);
        }
        if (symbols.Count < StructuredDataMaxSymbols
            && rawText.Contains("{x:Static", StringComparison.Ordinal))
        {
            AddXamlStaticMemberTypeSymbols(fileId, rawText, lines, lineStarts ??= BuildLineStarts(lines), symbols);
        }
        if (symbols.Count < StructuredDataMaxSymbols
            && MayContainXamlReferenceSymbol(rawText))
        {
            AddXamlReferenceSymbols(fileId, rawText, lines, lineStarts ??= BuildLineStarts(lines), symbols);
        }
        if (symbols.Count < StructuredDataMaxSymbols
            && TextContainsAny(rawText, XamlResourceReferenceMarkupPrefixes))
        {
            AddXamlResourceReferenceSymbols(fileId, rawText, lines, lineStarts ??= BuildLineStarts(lines), symbols);
        }
        if (symbols.Count < StructuredDataMaxSymbols
            && MayContainXamlBindingElementNameSymbol(rawText))
        {
            AddXamlBindingElementNameSymbols(fileId, rawText, lines, lineStarts ??= BuildLineStarts(lines), symbols);
        }
        if (symbols.Count < StructuredDataMaxSymbols
            && MayContainXamlBindingObjectElementSymbol(rawText))
        {
            AddXamlBindingObjectElementSymbols(fileId, rawText, lines, lineStarts ??= BuildLineStarts(lines), symbols);
        }

        if (symbols.Count <= StructuredDataMaxSymbols
            && MayContainXamlBindingMarkup(rawText))
        {
            foreach (Match bindingMatch in Regex.EnumerateMatches(XamlBindingRegex, rawText))
            {
                var value = NormalizeXamlBindingValue(bindingMatch.Groups["kind"].Value, bindingMatch.Groups["content"].Value);
                if (value.Length == 0)
                    continue;

                var currentLineStarts = lineStarts ??= BuildLineStarts(lines);
                var startLine = FindHtmlLineNumber(currentLineStarts, bindingMatch.Index);
                var signatureIndex = Math.Clamp(startLine - 1, 0, lines.Length - 1);
                if (TryAddBoundedStructuredDataSymbol(symbols, new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "property",
                    Name = value,
                    Line = startLine,
                    StartLine = startLine,
                    EndLine = startLine,
                    Signature = lines[signatureIndex].Trim(),
                }))
                    continue;

                return TrimStructuredDataSymbols(symbols, fileId, "structured_data_xml_symbol_budget_exceeded", lines);
            }
        }

        return TrimStructuredDataSymbols(symbols, fileId, "structured_data_xml_symbol_budget_exceeded", lines);
    }

}
