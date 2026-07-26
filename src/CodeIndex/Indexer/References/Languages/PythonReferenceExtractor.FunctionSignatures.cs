using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class PythonReferenceExtractor
{
    public static void EmitFunctionReturnReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        Func<int, SymbolRecord?> resolveContainerForReference,
        Func<string, bool> isIgnoredName)
    {
        if (!StartsWithPythonDefStatement(preparedLine)
            || preparedLine.IndexOf("->", StringComparison.Ordinal) < 0)
        {
            return;
        }

        foreach (Match match in ReferenceExtractor.EnumerateReferenceMatches(
                     FunctionReturnAnnotationExpressionRegex,
                     preparedLine,
                     references))
        {
            var typeGroup = match.Groups["type"];
            EmitPythonTypeExpressionReferences(
                typeGroup,
                references,
                seen,
                fileId,
                context,
                lineNumber,
                container,
                resolveContainerForReference,
                isIgnoredName);
        }

        foreach (Match match in ReferenceExtractor.EnumerateReferenceMatches(
                     FunctionReturnTypeRegex,
                     preparedLine,
                     references))
        {
            var name = match.Groups["name"].Value;
            if (isIgnoredName(name))
                continue;

            var nameIndex = match.Groups["name"].Index;
            ReferenceExtractor.AddTypeReferenceSegments(
                references,
                seen,
                fileId,
                name,
                nameIndex,
                context,
                lineNumber,
                resolveContainerForReference(nameIndex) ?? container,
                "python");
        }
    }

    public static void EmitFunctionParameterReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        Func<int, SymbolRecord?> resolveContainerForReference,
        Func<string, bool> isIgnoredName)
    {
        if (!StartsWithPythonDefStatement(preparedLine)
            || preparedLine.IndexOf('(') < 0
            || preparedLine.IndexOf(')') < 0
            || preparedLine.IndexOf(':') < 0)
        {
            return;
        }

        foreach (Match functionMatch in ReferenceExtractor.EnumerateReferenceMatches(
                     FunctionParameterListRegex,
                     preparedLine,
                     references))
        {
            var paramsGroup = functionMatch.Groups["params"];
            foreach (var (parameterSegment, parameterOffset) in EnumeratePythonTopLevelCommaSegments(paramsGroup.Value))
            {
                if (ReferenceExtractor.ReferenceLimitReached(references))
                    break;

                foreach (Match annotationMatch in ReferenceExtractor.EnumerateReferenceMatches(
                             AnnotationExpressionTypeRegex,
                             parameterSegment,
                             references))
                {
                    var typeGroup = annotationMatch.Groups["type"];
                    EmitPythonTypeExpressionReferences(
                        typeGroup,
                        references,
                        seen,
                        fileId,
                        context,
                        lineNumber,
                        container,
                        index => resolveContainerForReference(paramsGroup.Index + parameterOffset + index),
                        isIgnoredName,
                        paramsGroup.Index + parameterOffset);
                }

                foreach (Match annotationMatch in ReferenceExtractor.EnumerateReferenceMatches(
                             DirectAnnotationTypeRegex,
                             parameterSegment,
                             references))
                {
                    var name = annotationMatch.Groups["name"].Value;
                    if (isIgnoredName(name))
                        continue;

                    var nameIndex = paramsGroup.Index + parameterOffset + annotationMatch.Groups["name"].Index;
                    ReferenceExtractor.AddTypeReferenceSegments(
                        references,
                        seen,
                        fileId,
                        name,
                        nameIndex,
                        context,
                        lineNumber,
                        resolveContainerForReference(nameIndex) ?? container,
                        "python");
                }
            }
        }
    }

    public static void EmitVariableAnnotationReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        Func<string, bool> isIgnoredName)
    {
        if (preparedLine.IndexOf(':') < 0)
            return;

        foreach (Match match in ReferenceExtractor.EnumerateReferenceMatches(
                     VariableAnnotationExpressionRegex,
                     preparedLine,
                     references))
        {
            var typeGroup = match.Groups["type"];
            EmitPythonTypeExpressionReferences(
                typeGroup,
                references,
                seen,
                fileId,
                context,
                lineNumber,
                container,
                resolveContainerForReference: null,
                isIgnoredName);
        }

        foreach (Match match in ReferenceExtractor.EnumerateReferenceMatches(
                     VariableAnnotationTypeRegex,
                     preparedLine,
                     references))
        {
            var name = match.Groups["name"].Value;
            if (isIgnoredName(name))
                continue;

            ReferenceExtractor.AddTypeReferenceSegments(
                references,
                seen,
                fileId,
                name,
                match.Groups["name"].Index,
                context,
                lineNumber,
                container,
                "python");
        }
    }

}
