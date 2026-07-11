using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class LanguageReferenceExtractionSupport
{
    private static void EmitFortranTypeReferences(
        string preparedLine,
        string originalLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        SymbolRecord? container)
    {
        var hasFortranUseMarker = StartsWithKeywordIgnoringLeadingWhitespace(preparedLine, "use");
        if (hasFortranUseMarker)
        {
            foreach (Match match in FortranUseRegex.Matches(preparedLine))
                ReferenceExtractor.AddReference(references, seen, fileId, match, "type_reference", context, lineNumber, container);

            var useOnlyMatch = FortranUseOnlyRegex.Match(preparedLine);
            if (useOnlyMatch.Success)
            {
                EmitCommaSeparatedNames(useOnlyMatch.Groups["list"].Value, useOnlyMatch.Groups["list"].Index, "fortran", references, seen, fileId, context, lineNumber, container);

                var list = useOnlyMatch.Groups["list"];
                foreach (Match match in FortranUseAliasRegex.Matches(list.Value))
                {
                    var group = match.Groups["alias"];
                    ReferenceExtractor.AddReference(references, seen, fileId, group.Value, list.Index + group.Index, "type_reference", context, lineNumber, container);
                }

                foreach (Match match in FortranUseAliasTargetRegex.Matches(list.Value))
                {
                    var group = match.Groups["target"];
                    ReferenceExtractor.AddReference(references, seen, fileId, group.Value, list.Index + group.Index, "type_reference", context, lineNumber, container);
                }
            }

            var useRenameMatch = FortranUseRenameListRegex.Match(preparedLine);
            if (useRenameMatch.Success)
            {
                var list = useRenameMatch.Groups["list"];
                foreach (Match match in FortranUseAliasRegex.Matches(list.Value))
                {
                    var group = match.Groups["alias"];
                    ReferenceExtractor.AddReference(references, seen, fileId, group.Value, list.Index + group.Index, "type_reference", context, lineNumber, container);
                }

                foreach (Match match in FortranUseAliasTargetRegex.Matches(list.Value))
                {
                    var group = match.Groups["target"];
                    ReferenceExtractor.AddReference(references, seen, fileId, group.Value, list.Index + group.Index, "type_reference", context, lineNumber, container);
                }
            }
        }

        if (StartsWithKeywordIgnoringLeadingWhitespace(preparedLine, "import"))
        {
            var importMatch = FortranImportRegex.Match(preparedLine);
            if (importMatch.Success)
                EmitCommaSeparatedNames(importMatch.Groups["list"].Value, importMatch.Groups["list"].Index, "fortran", references, seen, fileId, context, lineNumber, container);
        }

        if (StartsWithKeywordIgnoringLeadingWhitespace(originalLine, "include")
            && (originalLine.IndexOf('\'') >= 0 || originalLine.IndexOf('"') >= 0))
        {
            foreach (Match match in FortranIncludeRegex.Matches(originalLine))
                ReferenceExtractor.AddReference(references, seen, fileId, match, "reference", context, lineNumber, container);
        }

        var isFortranCommonLine = StartsWithKeywordIgnoringLeadingWhitespace(preparedLine, "common");
        var isFortranNamelistLine = StartsWithKeywordIgnoringLeadingWhitespace(preparedLine, "namelist");
        if (isFortranCommonLine || isFortranNamelistLine)
        {
            if (preparedLine.IndexOf('/') >= 0)
            {
                foreach (Match match in FortranSlashGroupNameRegex.Matches(preparedLine))
                    ReferenceExtractor.AddReference(references, seen, fileId, match, "reference", context, lineNumber, container);

                foreach (Match memberListMatch in FortranSlashGroupMemberListRegex.Matches(preparedLine))
                {
                    var list = memberListMatch.Groups["list"];
                    foreach (Match match in FortranSimpleListNameRegex.Matches(list.Value))
                    {
                        var group = match.Groups["name"];
                        ReferenceExtractor.AddReference(references, seen, fileId, group.Value, list.Index + group.Index, "reference", context, lineNumber, container);
                    }
                }
            }
        }

        if (isFortranCommonLine && preparedLine.IndexOf('/') < 0)
        {
            var blankCommonMemberListMatch = FortranBlankCommonMemberListRegex.Match(preparedLine);
            if (blankCommonMemberListMatch.Success)
            {
                var list = blankCommonMemberListMatch.Groups["list"];
                foreach (Match match in FortranSimpleListNameRegex.Matches(list.Value))
                {
                    var group = match.Groups["name"];
                    ReferenceExtractor.AddReference(references, seen, fileId, group.Value, list.Index + group.Index, "reference", context, lineNumber, container);
                }
            }
        }

        if (StartsWithKeywordIgnoringLeadingWhitespace(preparedLine, "equivalence")
            && preparedLine.IndexOf('(') >= 0)
        {
            foreach (Match listMatch in FortranParenthesizedNameListRegex.Matches(preparedLine))
            {
                var list = listMatch.Groups["list"];
                foreach (Match match in FortranSimpleListNameRegex.Matches(list.Value))
                {
                    var group = match.Groups["name"];
                    ReferenceExtractor.AddReference(references, seen, fileId, group.Value, list.Index + group.Index, "reference", context, lineNumber, container);
                }
            }
        }

        if (StartsWithKeywordIgnoringLeadingWhitespace(preparedLine, "data"))
        {
            var dataLineMatch = FortranDataLineRegex.Match(preparedLine);
            if (dataLineMatch.Success)
            {
                var tail = dataLineMatch.Groups["tail"];
                if (tail.Value.IndexOf('/') >= 0)
                {
                    foreach (Match groupMatch in FortranDataObjectGroupRegex.Matches(tail.Value))
                    {
                        var list = groupMatch.Groups["list"];
                        foreach (Match match in FortranSimpleListNameRegex.Matches(list.Value))
                        {
                            var group = match.Groups["name"];
                            ReferenceExtractor.AddReference(references, seen, fileId, group.Value, tail.Index + list.Index + group.Index, "reference", context, lineNumber, container);
                        }
                    }
                }
            }
        }

        if (StartsWithKeywordIgnoringLeadingWhitespace(preparedLine, "save"))
        {
            var saveMatch = FortranSaveRegex.Match(preparedLine);
            if (saveMatch.Success)
            {
                var list = saveMatch.Groups["list"];
                if (list.Value.IndexOf('/') >= 0)
                {
                    foreach (Match match in FortranSlashGroupNameRegex.Matches(list.Value))
                        ReferenceExtractor.AddReference(references, seen, fileId, match.Groups["name"].Value, list.Index + match.Groups["name"].Index, "reference", context, lineNumber, container);
                }

                foreach (Match match in FortranSimpleListNameRegex.Matches(list.Value))
                {
                    var group = match.Groups["name"];
                    ReferenceExtractor.AddReference(references, seen, fileId, group.Value, list.Index + group.Index, "reference", context, lineNumber, container);
                }
            }
        }

        if (StartsWithKeywordIgnoringLeadingWhitespace(preparedLine, "submodule")
            && preparedLine.IndexOf('(') >= 0)
        {
            var submoduleMatch = FortranSubmoduleParentRegex.Match(preparedLine);
            if (submoduleMatch.Success)
            {
                var parent = submoduleMatch.Groups["parent"];
                ReferenceExtractor.AddReference(references, seen, fileId, parent.Value, parent.Index, "type_reference", context, lineNumber, container);

                var ancestor = submoduleMatch.Groups["ancestor"];
                if (ancestor.Success)
                    ReferenceExtractor.AddReference(references, seen, fileId, ancestor.Value, ancestor.Index, "type_reference", context, lineNumber, container);
            }
        }

        if (StartsWithKeywordIgnoringLeadingWhitespace(preparedLine, "external"))
        {
            var externalMatch = FortranExternalRegex.Match(preparedLine);
            if (externalMatch.Success)
            {
                var list = externalMatch.Groups["list"];
                foreach (Match match in FortranSimpleListNameRegex.Matches(list.Value))
                {
                    var group = match.Groups["name"];
                    ReferenceExtractor.AddReference(references, seen, fileId, group.Value, list.Index + group.Index, "reference", context, lineNumber, container);
                }
            }
        }

        if (StartsWithKeywordIgnoringLeadingWhitespace(preparedLine, "intrinsic"))
        {
            var intrinsicProcedureMatch = FortranIntrinsicProcedureRegex.Match(preparedLine);
            if (intrinsicProcedureMatch.Success)
            {
                var list = intrinsicProcedureMatch.Groups["list"];
                foreach (Match match in FortranSimpleListNameRegex.Matches(list.Value))
                {
                    var group = match.Groups["name"];
                    ReferenceExtractor.AddReference(references, seen, fileId, group.Value, list.Index + group.Index, "reference", context, lineNumber, container);
                }
            }
        }

        if (StartsWithKeywordIgnoringLeadingWhitespace(preparedLine, "public")
            || StartsWithKeywordIgnoringLeadingWhitespace(preparedLine, "private"))
        {
            var accessListMatch = FortranAccessListRegex.Match(preparedLine);
            if (accessListMatch.Success)
            {
                var list = accessListMatch.Groups["list"];
                foreach (Match match in FortranSimpleListNameRegex.Matches(list.Value))
                {
                    var group = match.Groups["name"];
                    ReferenceExtractor.AddReference(references, seen, fileId, group.Value, list.Index + group.Index, "reference", context, lineNumber, container);
                }
            }
        }

        if (StartsWithKeywordIgnoringLeadingWhitespace(preparedLine, "final"))
        {
            var finalizerMatch = FortranFinalizerRegex.Match(preparedLine);
            if (finalizerMatch.Success)
            {
                var list = finalizerMatch.Groups["list"];
                foreach (Match match in FortranSimpleListNameRegex.Matches(list.Value))
                {
                    var group = match.Groups["name"];
                    ReferenceExtractor.AddReference(references, seen, fileId, group.Value, list.Index + group.Index, "reference", context, lineNumber, container);
                }
            }
        }

        if (StartsWithKeywordIgnoringLeadingWhitespace(preparedLine, "procedure")
            || StartsWithKeywordIgnoringLeadingWhitespace(preparedLine, "generic"))
        {
            if (preparedLine.IndexOf("=>", StringComparison.Ordinal) >= 0)
            {
                var targetListMatch = FortranBindingTargetListRegex.Match(preparedLine);
                if (targetListMatch.Success)
                {
                    foreach (Match match in FortranBindingTargetRegex.Matches(targetListMatch.Value))
                    {
                        var group = match.Groups["name"];
                        ReferenceExtractor.AddReference(references, seen, fileId, group.Value, targetListMatch.Index + group.Index, "reference", context, lineNumber, container);
                    }
                }
            }
        }

        if (preparedLine.IndexOf("=>", StringComparison.Ordinal) >= 0)
        {
            var firstNonWhitespace = FirstNonWhitespaceIndex(preparedLine);
            if (firstNonWhitespace >= 0 && CanStartFortranIdentifierPattern(preparedLine[firstNonWhitespace]))
            {
                var pointerAssignmentMatch = FortranPointerAssignmentRegex.Match(preparedLine);
                if (pointerAssignmentMatch.Success)
                {
                    var group = pointerAssignmentMatch.Groups["name"];
                    if (!group.Value.Equals("null", StringComparison.OrdinalIgnoreCase))
                        ReferenceExtractor.AddReference(references, seen, fileId, group.Value, group.Index, "reference", context, lineNumber, container);
                }
            }
        }

        if (StartsWithKeywordIgnoringLeadingWhitespace(preparedLine, "associate")
            && preparedLine.IndexOf("=>", StringComparison.Ordinal) >= 0)
        {
            var associateMatch = FortranAssociateLineRegex.Match(preparedLine);
            if (associateMatch.Success)
            {
                var list = associateMatch.Groups["list"];
                foreach (Match match in FortranAssociateTargetRegex.Matches(list.Value))
                {
                    var group = match.Groups["name"];
                    ReferenceExtractor.AddReference(references, seen, fileId, group.Value, list.Index + group.Index, "reference", context, lineNumber, container);
                }
            }
        }

        var hasFortranParen = preparedLine.IndexOf('(') >= 0;
        var hasFortranTypeOrClassMarker = preparedLine.IndexOf("type", StringComparison.OrdinalIgnoreCase) >= 0
            || preparedLine.IndexOf("class", StringComparison.OrdinalIgnoreCase) >= 0;
        if (hasFortranParen && hasFortranTypeOrClassMarker)
        {
            foreach (Match match in FortranTypeRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), "fortran");
            }

            if (preparedLine.IndexOf("is", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                foreach (Match match in FortranTypeGuardRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), "fortran");
                }
            }
        }

        if (hasFortranParen && preparedLine.IndexOf("extends", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            foreach (Match match in FortranExtendsRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), "fortran");
            }
        }

        if (hasFortranParen && preparedLine.IndexOf("procedure", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            foreach (Match match in FortranProcedureTypeRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), "fortran");
            }
        }

        var hasFortranAllocateMarker = hasFortranParen
            && StartsWithKeywordIgnoringLeadingWhitespace(preparedLine, "allocate");
        if (hasFortranAllocateMarker && preparedLine.IndexOf("::", StringComparison.Ordinal) >= 0)
        {
            foreach (Match match in FortranAllocateTypeSpecRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), "fortran");
            }
        }

        if (hasFortranAllocateMarker)
        {
            var allocateListMatch = FortranAllocateListRegex.Match(preparedLine);
            if (allocateListMatch.Success)
            {
                var list = allocateListMatch.Groups["list"];
                var objectList = list.Value;
                var objectListStart = list.Index;
                var typeSpecEnd = objectList.IndexOf("::", StringComparison.Ordinal);
                if (typeSpecEnd >= 0)
                {
                    objectListStart += typeSpecEnd + 2;
                    objectList = objectList[(typeSpecEnd + 2)..];
                }

                foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(objectList))
                {
                    var segment = objectList.Substring(segmentStart, segmentLength);
                    if (segment.Contains('=', StringComparison.Ordinal))
                        continue;

                    var leading = 0;
                    while (leading < segment.Length && char.IsWhiteSpace(segment[leading]))
                        leading++;
                    if (leading >= segment.Length || !IsIdentifierStart(segment[leading]))
                        continue;

                    var end = leading + 1;
                    while (end < segment.Length && IsSimpleIdentifierPart(segment[end]))
                        end++;

                    ReferenceExtractor.AddReference(references, seen, fileId, segment[leading..end], objectListStart + segmentStart + leading, "reference", context, lineNumber, container);
                }

                if (list.Value.IndexOf('=') >= 0)
                {
                    if (list.Value.IndexOf("source", StringComparison.OrdinalIgnoreCase) >= 0
                        || list.Value.IndexOf("mold", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        foreach (Match match in FortranAllocateSourceKeywordRegex.Matches(list.Value))
                        {
                            var group = match.Groups["name"];
                            ReferenceExtractor.AddReference(references, seen, fileId, group.Value, list.Index + group.Index, "reference", context, lineNumber, container);
                        }
                    }

                    if (list.Value.IndexOf("stat", StringComparison.OrdinalIgnoreCase) >= 0
                        || list.Value.IndexOf("errmsg", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        foreach (Match match in FortranAllocationStatusKeywordRegex.Matches(list.Value))
                        {
                            var group = match.Groups["name"];
                            ReferenceExtractor.AddReference(references, seen, fileId, group.Value, list.Index + group.Index, "reference", context, lineNumber, container);
                        }
                    }
                }
            }
        }

        if (hasFortranParen && StartsWithKeywordIgnoringLeadingWhitespace(preparedLine, "deallocate"))
        {
            var deallocateListMatch = FortranDeallocateListRegex.Match(preparedLine);
            if (deallocateListMatch.Success)
            {
                var list = deallocateListMatch.Groups["list"];
                foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(list.Value))
                {
                    var segment = list.Value.Substring(segmentStart, segmentLength);
                    if (segment.Contains('=', StringComparison.Ordinal))
                        continue;

                    var leading = 0;
                    while (leading < segment.Length && char.IsWhiteSpace(segment[leading]))
                        leading++;
                    if (leading >= segment.Length || !IsIdentifierStart(segment[leading]))
                        continue;

                    var end = leading + 1;
                    while (end < segment.Length && IsSimpleIdentifierPart(segment[end]))
                        end++;

                    ReferenceExtractor.AddReference(references, seen, fileId, segment[leading..end], list.Index + segmentStart + leading, "reference", context, lineNumber, container);
                }

                if (list.Value.IndexOf('=') >= 0)
                {
                    if (list.Value.IndexOf("stat", StringComparison.OrdinalIgnoreCase) >= 0
                        || list.Value.IndexOf("errmsg", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        foreach (Match match in FortranAllocationStatusKeywordRegex.Matches(list.Value))
                        {
                            var group = match.Groups["name"];
                            ReferenceExtractor.AddReference(references, seen, fileId, group.Value, list.Index + group.Index, "reference", context, lineNumber, container);
                        }
                    }
                }
            }
        }

        var hasFortranIntrinsicKindMarker = hasFortranParen
            && (preparedLine.IndexOf("integer", StringComparison.OrdinalIgnoreCase) >= 0
                || preparedLine.IndexOf("real", StringComparison.OrdinalIgnoreCase) >= 0
                || preparedLine.IndexOf("complex", StringComparison.OrdinalIgnoreCase) >= 0
                || preparedLine.IndexOf("logical", StringComparison.OrdinalIgnoreCase) >= 0
                || preparedLine.IndexOf("character", StringComparison.OrdinalIgnoreCase) >= 0);
        if (hasFortranIntrinsicKindMarker
            && preparedLine.IndexOf('=') >= 0
            && preparedLine.IndexOf("kind", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            foreach (Match match in FortranIntrinsicKeywordKindRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), "fortran");
            }
        }

        if (hasFortranIntrinsicKindMarker)
        {
            foreach (Match match in FortranIntrinsicPositionalKindRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), "fortran");
            }
        }
    }

}
