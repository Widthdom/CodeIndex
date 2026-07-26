using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class PhpReferenceExtractor
{
    public static void EmitAttributeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (!preparedLine.Contains("#[", StringComparison.Ordinal))
            return;

        foreach (Match match in Regex.EnumerateMatches(AttributeRegex, preparedLine))
        {
            if (ReferenceExtractor.ReferenceLimitReached(references))
                break;

            var nameGroup = match.Groups["name"];
            var rawName = nameGroup.Value;
            var leadingBackslashCount = 0;
            while (leadingBackslashCount < rawName.Length && rawName[leadingBackslashCount] == '\\')
                leadingBackslashCount++;
            if (leadingBackslashCount == rawName.Length)
                continue;

            var trimmedName = rawName.Substring(leadingBackslashCount);
            var qualifiedNameIndex = nameGroup.Index + leadingBackslashCount;
            if (trimmedName.Contains('\\', StringComparison.Ordinal))
            {
                ReferenceExtractor.AddReference(
                    references,
                    seen,
                    fileId,
                    trimmedName,
                    qualifiedNameIndex,
                    "type_reference",
                    context,
                    lineNumber,
                    container);
            }

            var shortNameStart = trimmedName.LastIndexOf('\\') + 1;
            var shortName = trimmedName[shortNameStart..];
            if (shortName.Length == 0)
                continue;

            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                shortName,
                qualifiedNameIndex + shortNameStart,
                "type_reference",
                context,
                lineNumber,
                container);
        }
    }

    public static void EmitStaticAccessReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (preparedLine.IndexOf("::", StringComparison.Ordinal) < 0)
            return;

        foreach (Match match in Regex.EnumerateMatches(StaticAccessRegex, preparedLine))
        {
            if (ReferenceExtractor.ReferenceLimitReached(references))
                break;

            var nameGroup = match.Groups["name"];
            var rawName = nameGroup.Value;
            var leadingBackslashCount = 0;
            while (leadingBackslashCount < rawName.Length && rawName[leadingBackslashCount] == '\\')
                leadingBackslashCount++;
            if (leadingBackslashCount == rawName.Length)
                continue;

            var trimmedName = rawName.Substring(leadingBackslashCount);
            var shortNameStart = trimmedName.LastIndexOf('\\') + 1;
            var shortName = trimmedName[shortNameStart..];
            if (shortName.Length == 0)
                continue;

            var qualifiedNameIndex = nameGroup.Index + leadingBackslashCount;
            if (trimmedName.Length > shortName.Length)
            {
                ReferenceExtractor.AddReference(
                    references,
                    seen,
                    fileId,
                    trimmedName,
                    qualifiedNameIndex,
                    "type_reference",
                    context,
                    lineNumber,
                    container);
            }

            if (!string.Equals(shortName, "self", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(shortName, "static", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(shortName, "parent", StringComparison.OrdinalIgnoreCase))
            {
                var shortNameIndex = qualifiedNameIndex + shortNameStart;
                ReferenceExtractor.AddReference(
                    references,
                    seen,
                    fileId,
                    shortName,
                    shortNameIndex,
                    "type_reference",
                    context,
                    lineNumber,
                    container);
            }

            var memberGroup = match.Groups["member"];
            if (memberGroup.Success
                && !memberGroup.Value.Equals("class", StringComparison.OrdinalIgnoreCase)
                && !IsPhpCallAfterStaticMember(preparedLine, memberGroup.Index + memberGroup.Length))
            {
                ReferenceExtractor.AddReference(
                    references,
                    seen,
                    fileId,
                    memberGroup.Value,
                    memberGroup.Index,
                    "reference",
                    context,
                    lineNumber,
                    container);
            }
        }
    }

    public static void EmitInstanceofReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (preparedLine.IndexOf("instanceof", StringComparison.OrdinalIgnoreCase) < 0)
            return;

        foreach (Match match in Regex.EnumerateMatches(InstanceofRegex, preparedLine))
        {
            if (ReferenceExtractor.ReferenceLimitReached(references))
                break;

            AddPhpTypeReferenceFromQualifiedName(
                match.Groups["name"],
                references,
                seen,
                fileId,
                context,
                lineNumber,
                container);
        }
    }

    public static void EmitCatchTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (preparedLine.IndexOf("catch", StringComparison.OrdinalIgnoreCase) < 0)
            return;

        foreach (Match match in Regex.EnumerateMatches(CatchTypeRegex, preparedLine))
        {
            if (ReferenceExtractor.ReferenceLimitReached(references))
                break;

            foreach (Capture capture in match.Groups["name"].Captures)
            {
                AddPhpTypeReferenceFromQualifiedName(
                    capture,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container);
            }
        }
    }

    public static void EmitReturnTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (preparedLine.IndexOf(':') < 0
            || preparedLine.IndexOf(')') < 0)
        {
            return;
        }

        foreach (Match match in Regex.EnumerateMatches(ReturnTypeRegex, preparedLine))
        {
            if (ReferenceExtractor.ReferenceLimitReached(references))
                break;

            foreach (Capture capture in match.Groups["name"].Captures)
            {
                if (IsPhpBuiltinTypeName(capture.Value))
                    continue;

                AddPhpTypeReferenceFromQualifiedName(
                    capture,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container);
            }
        }
    }

    public static void EmitParameterTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (preparedLine.IndexOf('$') < 0)
            return;

        foreach (Match match in Regex.EnumerateMatches(ParameterTypeRegex, preparedLine))
        {
            if (ReferenceExtractor.ReferenceLimitReached(references))
                break;

            foreach (Capture capture in match.Groups["name"].Captures)
            {
                if (IsPhpBuiltinTypeName(capture.Value))
                    continue;

                AddPhpTypeReferenceFromQualifiedName(
                    capture,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container);
            }
        }
    }

    public static void EmitPropertyTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (preparedLine.IndexOf('$') < 0
            || (preparedLine.IndexOf("public", StringComparison.OrdinalIgnoreCase) < 0
                && preparedLine.IndexOf("private", StringComparison.OrdinalIgnoreCase) < 0
                && preparedLine.IndexOf("protected", StringComparison.OrdinalIgnoreCase) < 0
                && preparedLine.IndexOf("var", StringComparison.OrdinalIgnoreCase) < 0))
        {
            return;
        }

        foreach (Match match in Regex.EnumerateMatches(PropertyTypeRegex, preparedLine))
        {
            if (ReferenceExtractor.ReferenceLimitReached(references))
                break;

            foreach (Capture capture in match.Groups["name"].Captures)
            {
                if (IsPhpBuiltinTypeName(capture.Value))
                    continue;

                AddPhpTypeReferenceFromQualifiedName(
                    capture,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container);
            }
        }
    }

    public static void EmitInheritanceTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (preparedLine.IndexOf("extends", StringComparison.OrdinalIgnoreCase) < 0
            && preparedLine.IndexOf("implements", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return;
        }

        foreach (Match match in Regex.EnumerateMatches(InheritanceTypeRegex, preparedLine))
        {
            if (ReferenceExtractor.ReferenceLimitReached(references))
                break;

            foreach (Capture capture in match.Groups["name"].Captures)
            {
                AddPhpTypeReferenceFromQualifiedName(
                    capture,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container);
            }
        }
    }

    public static void EmitUseTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (preparedLine.IndexOf("use", StringComparison.OrdinalIgnoreCase) < 0)
            return;

        var groupMatch = GroupUseTypeRegex.Match(preparedLine);
        if (groupMatch.Success)
        {
            EmitGroupUseTypeReferences(groupMatch, references, seen, fileId, context, lineNumber, container);
            return;
        }

        var match = UseTypeRegex.Match(preparedLine);
        if (!match.Success)
            return;

        foreach (Capture capture in match.Groups["name"].Captures)
        {
            AddPhpTypeReferenceFromQualifiedName(
                capture,
                references,
                seen,
                fileId,
                context,
                lineNumber,
                container);
        }
    }

    public static void EmitUseFunctionReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (preparedLine.IndexOf("use", StringComparison.OrdinalIgnoreCase) < 0
            || preparedLine.IndexOf("function", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return;
        }

        var groupFunctionMatch = GroupUseFunctionRegex.Match(preparedLine);
        if (groupFunctionMatch.Success)
        {
            EmitGroupUseImportReferences(groupFunctionMatch, references, seen, fileId, context, lineNumber, container, "function", requireImportKind: false);
            return;
        }

        var groupMatch = GroupUseTypeRegex.Match(preparedLine);
        if (groupMatch.Success)
        {
            EmitGroupUseImportReferences(groupMatch, references, seen, fileId, context, lineNumber, container, "function", requireImportKind: true);
            return;
        }

        var match = UseFunctionRegex.Match(preparedLine);
        if (!match.Success)
            return;

        var importsGroup = match.Groups["imports"];
        foreach (Match itemMatch in Regex.EnumerateMatches(
                     UseImportItemRegex,
                     importsGroup.Value))
        {
            if (ReferenceExtractor.ReferenceLimitReached(references))
                break;

            var itemGroup = itemMatch.Groups["name"];
            AddPhpReferenceFromName(
                itemGroup.Value,
                importsGroup.Index + itemGroup.Index,
                "reference",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                container);
        }
    }

    public static void EmitUseConstReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (preparedLine.IndexOf("use", StringComparison.OrdinalIgnoreCase) < 0
            || preparedLine.IndexOf("const", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return;
        }

        var groupConstMatch = GroupUseConstRegex.Match(preparedLine);
        if (groupConstMatch.Success)
        {
            EmitGroupUseImportReferences(groupConstMatch, references, seen, fileId, context, lineNumber, container, "const", requireImportKind: false);
            return;
        }

        var groupMatch = GroupUseTypeRegex.Match(preparedLine);
        if (groupMatch.Success)
        {
            EmitGroupUseImportReferences(groupMatch, references, seen, fileId, context, lineNumber, container, "const", requireImportKind: true);
            return;
        }

        var match = UseConstRegex.Match(preparedLine);
        if (!match.Success)
            return;

        var importsGroup = match.Groups["imports"];
        foreach (Match itemMatch in Regex.EnumerateMatches(
                     UseImportItemRegex,
                     importsGroup.Value))
        {
            if (ReferenceExtractor.ReferenceLimitReached(references))
                break;

            var itemGroup = itemMatch.Groups["name"];
            AddPhpReferenceFromName(
                itemGroup.Value,
                importsGroup.Index + itemGroup.Index,
                "reference",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                container);
        }
    }

    private static void EmitGroupUseTypeReferences(
        Match groupMatch,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        var prefixGroup = groupMatch.Groups["prefix"];
        var rawPrefix = prefixGroup.Value;
        var prefixEnd = rawPrefix.Length;
        while (prefixEnd > 0 && rawPrefix[prefixEnd - 1] == '\\')
            prefixEnd--;
        if (prefixEnd == 0)
            return;

        var prefix = prefixEnd == rawPrefix.Length ? rawPrefix : rawPrefix.Substring(0, prefixEnd);
        var itemsGroup = groupMatch.Groups["items"];
        foreach (Match itemMatch in Regex.EnumerateMatches(
                     GroupUseTypeItemRegex,
                     itemsGroup.Value))
        {
            if (ReferenceExtractor.ReferenceLimitReached(references))
                break;

            if (itemMatch.Groups["kind"].Success)
                continue;

            var itemGroup = itemMatch.Groups["name"];
            var rawItemName = itemGroup.Value;
            var leadingBackslashCount = 0;
            while (leadingBackslashCount < rawItemName.Length && rawItemName[leadingBackslashCount] == '\\')
                leadingBackslashCount++;
            if (leadingBackslashCount == rawItemName.Length)
                continue;

            var trimmedItemName = rawItemName.Substring(leadingBackslashCount);
            var itemShortNameStart = trimmedItemName.LastIndexOf('\\') + 1;
            var shortNameIndex = itemsGroup.Index + itemGroup.Index + leadingBackslashCount + itemShortNameStart;
            AddPhpTypeReferenceFromName(
                prefix + "\\" + trimmedItemName,
                prefixGroup.Index,
                references,
                seen,
                fileId,
                context,
                lineNumber,
                container,
                shortNameIndex);
        }
    }

    private static void EmitGroupUseImportReferences(
        Match groupMatch,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        string importKind,
        bool requireImportKind)
    {
        var prefixGroup = groupMatch.Groups["prefix"];
        var rawPrefix = prefixGroup.Value;
        var prefixEnd = rawPrefix.Length;
        while (prefixEnd > 0 && rawPrefix[prefixEnd - 1] == '\\')
            prefixEnd--;
        if (prefixEnd == 0)
            return;

        var prefix = prefixEnd == rawPrefix.Length ? rawPrefix : rawPrefix.Substring(0, prefixEnd);
        var itemsGroup = groupMatch.Groups["items"];
        foreach (Match itemMatch in Regex.EnumerateMatches(
                     GroupUseTypeItemRegex,
                     itemsGroup.Value))
        {
            if (ReferenceExtractor.ReferenceLimitReached(references))
                break;

            var isTargetKind = itemMatch.Groups["kind"].Success
                && itemMatch.Groups["kind"].Value.Equals(importKind, StringComparison.OrdinalIgnoreCase);
            if (requireImportKind != isTargetKind)
                continue;

            var itemGroup = itemMatch.Groups["name"];
            var rawItemName = itemGroup.Value;
            var leadingBackslashCount = 0;
            while (leadingBackslashCount < rawItemName.Length && rawItemName[leadingBackslashCount] == '\\')
                leadingBackslashCount++;
            if (leadingBackslashCount == rawItemName.Length)
                continue;

            var trimmedItemName = rawItemName.Substring(leadingBackslashCount);
            var itemShortNameStart = trimmedItemName.LastIndexOf('\\') + 1;
            var shortNameIndex = itemsGroup.Index + itemGroup.Index + leadingBackslashCount + itemShortNameStart;
            AddPhpReferenceFromName(
                prefix + "\\" + trimmedItemName,
                prefixGroup.Index,
                "reference",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                container,
                shortNameIndex);
        }
    }

}
