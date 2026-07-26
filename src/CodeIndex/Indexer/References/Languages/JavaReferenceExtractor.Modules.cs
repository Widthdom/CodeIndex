using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class JavaReferenceExtractor
{
    public static void EmitMethodReferenceReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
        => JvmMethodReferenceExtractor.EmitMethodReferenceReferences(
            "java",
            preparedLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn);

    public static void EmitDotClassTypeLiteralReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        foreach (Match match in ReferenceExtractor.EnumerateReferenceMatches(
                     DotClassArgRegex,
                     preparedLine,
                     references))
        {
            var argGroup = match.Groups["arg"];
            ReferenceExtractor.AddTypeReferenceSegments(
                references,
                seen,
                fileId,
                argGroup.Value,
                argGroup.Index,
                context,
                lineNumber,
                container,
                "java");
        }
    }

    public static void EmitModuleDirectiveReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        EmitModuleDirectiveReference(
            preparedLine,
            ModuleRequiresDirectiveReferenceRegex,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn);

        EmitModuleDirectiveReference(
            preparedLine,
            ModuleUsesDirectiveReferenceRegex,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn);

        foreach (Match match in ReferenceExtractor.EnumerateReferenceMatches(ModuleProvidesDirectiveReferenceRegex, preparedLine, references))
        {
            var serviceGroup = match.Groups["service"];
            ReferenceExtractor.AddTypeReferenceSegment(
                references,
                seen,
                fileId,
                serviceGroup.Value,
                serviceGroup.Index,
                context,
                lineNumber,
                resolveContainerForColumn(serviceGroup.Index),
                "java");

            var implementationsGroup = match.Groups["implementations"];
            var implementations = implementationsGroup.ValueSpan;
            foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(implementations))
            {
                var segmentLeading = ReferenceExtractor.CountLeadingWhitespace(implementations, segmentStart, segmentLength);
                var rawSegmentLength = segmentLength - segmentLeading;
                while (rawSegmentLength > 0 && char.IsWhiteSpace(implementations[segmentStart + segmentLeading + rawSegmentLength - 1]))
                    rawSegmentLength--;
                if (rawSegmentLength == 0)
                    continue;

                var rawSegment = implementations.Slice(segmentStart + segmentLeading, rawSegmentLength);
                var absoluteStart = implementationsGroup.Index
                    + segmentStart
                    + segmentLeading;
                ReferenceExtractor.AddTypeReferenceSegment(
                    references,
                    seen,
                    fileId,
                    rawSegment.ToString(),
                    absoluteStart,
                    context,
                    lineNumber,
                    resolveContainerForColumn(absoluteStart),
                    "java");
            }
        }
    }

    private static void EmitModuleDirectiveReference(
        string preparedLine,
        Regex regex,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        foreach (Match match in ReferenceExtractor.EnumerateReferenceMatches(regex, preparedLine, references))
        {
            var nameGroup = match.Groups["name"];
            ReferenceExtractor.AddTypeReferenceSegment(
                references,
                seen,
                fileId,
                nameGroup.Value,
                nameGroup.Index,
                context,
                lineNumber,
                resolveContainerForColumn(nameGroup.Index),
                "java");
        }
    }
}
