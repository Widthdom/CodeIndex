using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class LanguageReferenceExtractionSupport
{
    private static void EmitVbTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var hasVbTypeKeywordMarker = preparedLine.IndexOf("As", StringComparison.OrdinalIgnoreCase) >= 0
            || preparedLine.IndexOf("New", StringComparison.OrdinalIgnoreCase) >= 0
            || preparedLine.IndexOf("Inherits", StringComparison.OrdinalIgnoreCase) >= 0
            || preparedLine.IndexOf("Implements", StringComparison.OrdinalIgnoreCase) >= 0;
        if (hasVbTypeKeywordMarker)
        {
            foreach (Match match in VbTypeKeywordRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), "vb");
            }
        }

        var hasVbGenericArgumentListMarker = preparedLine.Contains('(')
            && preparedLine.IndexOf("Of", StringComparison.OrdinalIgnoreCase) >= 0;
        if (hasVbGenericArgumentListMarker)
        {
            foreach (Match match in VbGenericArgumentListRegex.Matches(preparedLine))
            {
                if (VbGenericDeclarationOwnerRegex.IsMatch(preparedLine[..match.Index]))
                {
                    var constraintGroup = match.Groups["list"];
                    EmitVbGenericConstraintReferences(constraintGroup.Value, constraintGroup.Index, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
                    continue;
                }

                var group = match.Groups["list"];
                EmitCommaSeparatedNames(group.Value, group.Index, "vb", references, seen, fileId, context, lineNumber, resolveContainerForColumn(group.Index));
            }
        }

        var hasVbNewMarker = preparedLine.IndexOf("New", StringComparison.OrdinalIgnoreCase) >= 0;
        if (hasVbNewMarker)
        {
            foreach (Match match in VbNewTypeRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                var rawName = LastQualifiedSegment(group.Value);
                var name = NormalizeVbIdentifierSegment(rawName);
                if (string.Equals(name, "With", StringComparison.OrdinalIgnoreCase))
                    continue;

                var nameOffset = group.Value.LastIndexOf(rawName, StringComparison.Ordinal);
                var nameIndex = group.Index + Math.Max(0, nameOffset);
                ReferenceExtractor.AddReference(references, seen, fileId, name, nameIndex, "instantiate", context, lineNumber, resolveContainerForColumn(nameIndex));
            }
        }

        var hasVbImplementsMarker = preparedLine.IndexOf("Implements", StringComparison.OrdinalIgnoreCase) >= 0;
        if (hasVbImplementsMarker)
        {
            foreach (Match match in VbImplementsListRegex.Matches(preparedLine))
            {
                var group = match.Groups["list"];
                EmitCommaSeparatedNames(group.Value, group.Index, "vb", references, seen, fileId, context, lineNumber, resolveContainerForColumn(group.Index));
                if (IsVisualBasicMemberImplementsClause(preparedLine, match.Index))
                    EmitVisualBasicImplementsOwnerReferences(group.Value, group.Index, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
            }
        }

        var hasVbImportsMarker = preparedLine.IndexOf("Imports", StringComparison.OrdinalIgnoreCase) >= 0;
        if (hasVbImportsMarker)
        {
            var importsMatch = VbImportsListRegex.Match(preparedLine);
            if (importsMatch.Success)
            {
                var group = importsMatch.Groups["list"];
                EmitCommaSeparatedNames(group.Value, group.Index, "vb", references, seen, fileId, context, lineNumber, resolveContainerForColumn(group.Index));
            }
        }

        var hasVbCastTypeMarker = preparedLine.Contains('(')
            && preparedLine.Contains(',')
            && (preparedLine.IndexOf("Cast", StringComparison.OrdinalIgnoreCase) >= 0
                || preparedLine.IndexOf("CType", StringComparison.OrdinalIgnoreCase) >= 0);
        if (hasVbCastTypeMarker)
        {
            foreach (Match match in VbCastTypeRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), "vb");
            }
        }

        var hasVbGetTypeMarker = preparedLine.Contains('(')
            && preparedLine.IndexOf("GetType", StringComparison.OrdinalIgnoreCase) >= 0;
        if (hasVbGetTypeMarker)
        {
            foreach (Match match in VbGetTypeRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), "vb");
            }
        }

        var hasVbTypeOfMarker = preparedLine.IndexOf("TypeOf", StringComparison.OrdinalIgnoreCase) >= 0
            && preparedLine.IndexOf("Is", StringComparison.OrdinalIgnoreCase) >= 0;
        if (hasVbTypeOfMarker)
        {
            foreach (Match match in VbTypeOfRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), "vb");
            }
        }

        var hasVbNameOfMarker = preparedLine.Contains('(')
            && preparedLine.IndexOf("NameOf", StringComparison.OrdinalIgnoreCase) >= 0;
        if (hasVbNameOfMarker)
        {
            foreach (Match match in VbNameOfRegex.Matches(preparedLine))
            {
                var group = match.Groups["name"];
                var rawName = LastQualifiedSegment(group.Value);
                var name = NormalizeVbIdentifierSegment(rawName);
                var nameOffset = group.Value.LastIndexOf(rawName, StringComparison.Ordinal);
                var rawNameIndex = group.Index + Math.Max(0, nameOffset);
                var nameIndex = rawName.StartsWith('[') ? rawNameIndex + 1 : rawNameIndex;
                ReferenceExtractor.AddReference(references, seen, fileId, name, nameIndex, "type_reference", context, lineNumber, resolveContainerForColumn(nameIndex));
            }
        }

        var hasVbGetXmlNamespaceMarker = preparedLine.Contains('(')
            && preparedLine.IndexOf("GetXmlNamespace", StringComparison.OrdinalIgnoreCase) >= 0;
        if (hasVbGetXmlNamespaceMarker)
        {
            foreach (Match match in VbGetXmlNamespaceRegex.Matches(preparedLine))
            {
                var group = match.Groups["name"];
                ReferenceExtractor.AddReference(references, seen, fileId, group.Value, group.Index, "type_reference", context, lineNumber, resolveContainerForColumn(group.Index));
            }
        }

        var hasVbAddressOfMarker = preparedLine.IndexOf("AddressOf", StringComparison.OrdinalIgnoreCase) >= 0;
        if (hasVbAddressOfMarker)
        {
            foreach (Match match in VbAddressOfRegex.Matches(preparedLine))
            {
                var group = match.Groups["name"];
                var rawName = LastQualifiedSegment(group.Value);
                var name = NormalizeVbIdentifierSegment(rawName);
                var nameOffset = group.Value.LastIndexOf(rawName, StringComparison.Ordinal);
                var nameIndex = group.Index + Math.Max(0, nameOffset);
                ReferenceExtractor.AddReference(references, seen, fileId, name, nameIndex, "call", context, lineNumber, resolveContainerForColumn(nameIndex));
            }
        }

        var handlesIndex = preparedLine.IndexOf("Handles", StringComparison.OrdinalIgnoreCase);
        if (handlesIndex >= 0)
        {
            var handlesText = preparedLine[handlesIndex..];
            foreach (Match match in VbHandlesTargetRegex.Matches(handlesText))
            {
                var group = match.Groups["name"];
                var rawName = LastQualifiedSegment(group.Value);
                var name = NormalizeVbIdentifierSegment(rawName);
                var nameOffset = group.Value.LastIndexOf(rawName, StringComparison.Ordinal);
                var nameIndex = handlesIndex + group.Index + Math.Max(0, nameOffset);
                ReferenceExtractor.AddReference(references, seen, fileId, name, nameIndex, "subscribe", context, lineNumber, resolveContainerForColumn(nameIndex));
            }
        }

        var hasVbAddHandlerMarker = preparedLine.IndexOf("AddHandler", StringComparison.OrdinalIgnoreCase) >= 0;
        if (hasVbAddHandlerMarker)
        {
            foreach (Match match in VbAddHandlerRegex.Matches(preparedLine))
            {
                var group = match.Groups["name"];
                var rawName = LastQualifiedSegment(group.Value);
                var name = NormalizeVbIdentifierSegment(rawName);
                var nameOffset = group.Value.LastIndexOf(rawName, StringComparison.Ordinal);
                var nameIndex = group.Index + Math.Max(0, nameOffset);
                ReferenceExtractor.AddReference(references, seen, fileId, name, nameIndex, "subscribe", context, lineNumber, resolveContainerForColumn(nameIndex));
            }
        }

        var hasVbRemoveHandlerMarker = preparedLine.IndexOf("RemoveHandler", StringComparison.OrdinalIgnoreCase) >= 0;
        if (hasVbRemoveHandlerMarker)
        {
            foreach (Match match in VbRemoveHandlerRegex.Matches(preparedLine))
            {
                var group = match.Groups["name"];
                var rawName = LastQualifiedSegment(group.Value);
                var name = NormalizeVbIdentifierSegment(rawName);
                var nameOffset = group.Value.LastIndexOf(rawName, StringComparison.Ordinal);
                var nameIndex = group.Index + Math.Max(0, nameOffset);
                ReferenceExtractor.AddReference(references, seen, fileId, name, nameIndex, "unsubscribe", context, lineNumber, resolveContainerForColumn(nameIndex));
            }
        }

        var hasVbRaiseEventMarker = preparedLine.IndexOf("RaiseEvent", StringComparison.OrdinalIgnoreCase) >= 0;
        if (hasVbRaiseEventMarker)
        {
            foreach (Match match in VbRaiseEventRegex.Matches(preparedLine))
            {
                var group = match.Groups["name"];
                var rawName = LastQualifiedSegment(group.Value);
                var name = NormalizeVbIdentifierSegment(rawName);
                var nameOffset = group.Value.LastIndexOf(rawName, StringComparison.Ordinal);
                var nameIndex = group.Index + Math.Max(0, nameOffset);
                ReferenceExtractor.AddReference(references, seen, fileId, name, nameIndex, "call", context, lineNumber, resolveContainerForColumn(nameIndex));
            }
        }
    }

    private static void EmitVisualBasicEscapedCallReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        IReadOnlySet<string>? definitionNames)
    {
        if (!preparedLine.Contains('(') || !preparedLine.Contains('['))
            return;

        foreach (Match match in VbCallRegex.Matches(preparedLine))
        {
            var group = match.Groups["name"];
            if (!group.Value.Contains('['))
                continue;

            var rawName = LastQualifiedSegment(group.Value);
            var name = NormalizeVbIdentifierSegment(rawName);
            if (ShouldSkipVisualBasicEscapedCall(preparedLine, group.Index, name, definitionNames))
                continue;

            var nameOffset = group.Value.LastIndexOf(rawName, StringComparison.Ordinal);
            var rawNameIndex = group.Index + Math.Max(0, nameOffset);
            var nameIndex = rawName.StartsWith('[') ? rawNameIndex + 1 : rawNameIndex;
            ReferenceExtractor.AddReference(references, seen, fileId, name, nameIndex, "call", context, lineNumber, resolveContainerForColumn(nameIndex));
        }
    }

    private static void EmitVisualBasicBareCallReferences(
        string preparedLine,
        Action<string, int> addCallLikeReference,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var firstNonWhitespace = FirstNonWhitespaceIndex(preparedLine);
        if (firstNonWhitespace < 0 || !CanStartVisualBasicIdentifierPattern(preparedLine[firstNonWhitespace]))
            return;

        var match = VbBareCallRegex.Match(preparedLine);
        if (!match.Success)
            return;

        var group = match.Groups["name"];
        var tail = match.Groups["tail"].Value.TrimStart();
        if (ShouldSkipVisualBasicBareCall(group.Value, tail))
            return;

        var rawName = LastQualifiedSegment(group.Value);
        var name = NormalizeVbIdentifierSegment(rawName);
        var nameOffset = group.Value.LastIndexOf(rawName, StringComparison.Ordinal);
        var rawNameIndex = group.Index + Math.Max(0, nameOffset);
        var nameIndex = rawName.StartsWith('[') ? rawNameIndex + 1 : rawNameIndex;
        if (rawName.StartsWith('['))
            ReferenceExtractor.AddReference(references, seen, fileId, name, nameIndex, "call", context, lineNumber, resolveContainerForColumn(nameIndex));
        else
            addCallLikeReference(name, nameIndex);
    }

    private static void EmitVisualBasicCallByNameReferences(
        string originalLine,
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (!originalLine.Contains('(')
            || !originalLine.Contains(',')
            || !originalLine.Contains('"')
            || originalLine.IndexOf("CallByName", StringComparison.OrdinalIgnoreCase) < 0)
            return;

        foreach (Match match in VbCallByNameRegex.Matches(originalLine))
        {
            if (match.Index >= preparedLine.Length || char.IsWhiteSpace(preparedLine[match.Index]))
                continue;

            var group = match.Groups["name"];
            var name = group.Value.Trim();
            if (!IsSimpleVisualBasicIdentifier(name))
                continue;

            ReferenceExtractor.AddReference(references, seen, fileId, name, group.Index, "call", context, lineNumber, resolveContainerForColumn(group.Index));
        }
    }

    private static bool IsSimpleVisualBasicIdentifier(string value)
    {
        if (value.Length == 0 || !IsIdentifierStart(value[0]))
            return false;

        for (var i = 1; i < value.Length; i++)
        {
            if (!IsSimpleIdentifierPart(value[i]))
                return false;
        }

        return true;
    }

}
