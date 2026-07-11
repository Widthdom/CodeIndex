using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class LanguageReferenceExtractionSupport
{
    private static void EmitCppTypeReferences(
        string language,
        string preparedLine,
        string originalLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var hasCppIncludeMarker = !string.IsNullOrWhiteSpace(preparedLine)
            && (originalLine.IndexOf('#') >= 0
                || originalLine.IndexOf("import", StringComparison.Ordinal) >= 0
                || originalLine.IndexOf("include", StringComparison.Ordinal) >= 0);
        var includeMatch = hasCppIncludeMarker
            ? CppIncludeRegex.Match(originalLine)
            : Match.Empty;
        if (includeMatch.Success)
        {
            var group = includeMatch.Groups["name"];
            ReferenceExtractor.AddReference(references, seen, fileId, group.Value, group.Index, "type_reference", context, lineNumber, resolveContainerForColumn(group.Index));
        }

        var baseMatch = preparedLine.IndexOf(':') >= 0
            && (ContainsOrdinalKeyword(preparedLine, "class")
                || ContainsOrdinalKeyword(preparedLine, "struct"))
            ? CppBaseListRegex.Match(preparedLine)
            : Match.Empty;
        if (baseMatch.Success)
        {
            var group = baseMatch.Groups["bases"];
            foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(group.Value))
            {
                var segment = group.Value.Substring(segmentStart, segmentLength);
                var expression = StripCppAccessPrefix(segment);
                if (expression.Length == 0)
                    continue;

                var absoluteStart = group.Index + segmentStart + segment.IndexOf(expression, StringComparison.Ordinal);
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, expression, absoluteStart, context, lineNumber, resolveContainerForColumn(absoluteStart), language);
            }
        }

        if (ContainsOrdinalKeyword(preparedLine, "new"))
        {
            foreach (Match match in CppNewTypeRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                var typeName = LastCppQualifiedSegment(group.Value);
                var typeStart = group.Index + group.Value.LastIndexOf(typeName, StringComparison.Ordinal);
                ReferenceExtractor.AddReference(references, seen, fileId, typeName, typeStart, "instantiate", context, lineNumber, resolveContainerForColumn(typeStart));
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
            }
        }

        if (preparedLine.IndexOf("_cast", StringComparison.Ordinal) >= 0)
        {
            foreach (Match match in CppNamedCastTypeRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
            }
        }

        if (preparedLine.IndexOf('(') >= 0)
        {
            foreach (Match match in CppCStyleCastTypeRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
            }
        }

        if (language == "c")
        {
            var hasCParen = preparedLine.IndexOf('(') >= 0;
            var hasCTypedefTypeMarker = preparedLine.IndexOf("_t", StringComparison.Ordinal) >= 0;
            var hasCTaggedTypeMarker = ContainsOrdinalKeyword(preparedLine, "struct")
                || preparedLine.IndexOf("enum", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("union", StringComparison.Ordinal) >= 0;
            if (hasCParen && hasCTypedefTypeMarker)
            {
                foreach (Match match in CTypedefCastTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            var hasCSizeofMarker = hasCParen
                && preparedLine.IndexOf("sizeof", StringComparison.Ordinal) >= 0;
            if (hasCSizeofMarker && hasCTypedefTypeMarker)
            {
                foreach (Match match in CTypedefSizeofTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            if (hasCSizeofMarker && hasCTaggedTypeMarker)
            {
                foreach (Match match in CTaggedSizeofTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            var hasCAlignofMarker = hasCParen
                && (preparedLine.IndexOf("alignof", StringComparison.Ordinal) >= 0
                    || preparedLine.IndexOf("_Alignof", StringComparison.Ordinal) >= 0
                    || preparedLine.IndexOf("__alignof", StringComparison.Ordinal) >= 0);
            if (hasCAlignofMarker && hasCTypedefTypeMarker)
            {
                foreach (Match match in CTypedefAlignofTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            if (hasCAlignofMarker && hasCTaggedTypeMarker)
            {
                foreach (Match match in CTaggedAlignofTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            var hasCDeclarationTerminator = preparedLine.IndexOf('=') >= 0
                || preparedLine.IndexOf(',') >= 0
                || preparedLine.IndexOf(';') >= 0
                || preparedLine.IndexOf('[') >= 0;
            if (hasCDeclarationTerminator && hasCTypedefTypeMarker)
            {
                foreach (Match match in CTypedefDeclarationTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            if (hasCDeclarationTerminator && hasCTaggedTypeMarker)
            {
                foreach (Match match in CTaggedDeclarationTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            if (hasCParen && hasCTypedefTypeMarker)
            {
                foreach (Match match in CTypedefFunctionReturnTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            if (hasCParen && hasCTaggedTypeMarker)
            {
                foreach (Match match in CTaggedFunctionReturnTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            var hasCParameterDelimiter = hasCParen || preparedLine.IndexOf(',') >= 0;
            if (hasCParameterDelimiter && hasCTypedefTypeMarker)
            {
                foreach (Match match in CTypedefParameterTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            if (hasCParameterDelimiter && hasCTaggedTypeMarker)
            {
                foreach (Match match in CTaggedParameterTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            var hasCCompoundLiteralMarkers = hasCParen && preparedLine.IndexOf('{') >= 0;
            if (hasCCompoundLiteralMarkers && hasCTypedefTypeMarker)
            {
                foreach (Match match in CTypedefCompoundLiteralTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            if (hasCCompoundLiteralMarkers && hasCTaggedTypeMarker)
            {
                foreach (Match match in CTaggedCompoundLiteralTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            var hasCTypeofMarker = hasCParen
                && preparedLine.IndexOf("typeof", StringComparison.Ordinal) >= 0;
            if (hasCTypeofMarker && hasCTypedefTypeMarker)
            {
                foreach (Match match in CTypedefTypeofTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            if (hasCTypeofMarker && hasCTaggedTypeMarker)
            {
                foreach (Match match in CTaggedTypeofTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            if (hasCTypeofMarker && hasCTypedefTypeMarker)
            {
                foreach (Match match in CTypedefTypeofUnqualTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            if (hasCTypeofMarker && hasCTaggedTypeMarker)
            {
                foreach (Match match in CTaggedTypeofUnqualTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            var hasCBuiltinTypesCompatibleMarker = hasCParen
                && preparedLine.IndexOf("__builtin_types_compatible_p", StringComparison.Ordinal) >= 0;
            if (hasCBuiltinTypesCompatibleMarker && hasCTypedefTypeMarker)
            {
                foreach (Match match in CTypedefBuiltinTypesCompatibleFirstTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            if (hasCBuiltinTypesCompatibleMarker && hasCTypedefTypeMarker)
            {
                foreach (Match match in CTypedefBuiltinTypesCompatibleSecondTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            if (hasCBuiltinTypesCompatibleMarker && hasCTaggedTypeMarker)
            {
                foreach (Match match in CTaggedBuiltinTypesCompatibleFirstTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            if (hasCBuiltinTypesCompatibleMarker && hasCTaggedTypeMarker)
            {
                foreach (Match match in CTaggedBuiltinTypesCompatibleSecondTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            var hasCGenericAssociationMarker = preparedLine.IndexOf(':') >= 0
                && (preparedLine.IndexOf("_Generic", StringComparison.Ordinal) >= 0
                    || preparedLine.IndexOf(',') >= 0);
            if (hasCGenericAssociationMarker && hasCTypedefTypeMarker)
            {
                foreach (Match match in CTypedefGenericAssociationTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            if (hasCGenericAssociationMarker && hasCTaggedTypeMarker)
            {
                foreach (Match match in CTaggedGenericAssociationTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            var hasCAtomicMarker = hasCParen
                && preparedLine.IndexOf("_Atomic", StringComparison.Ordinal) >= 0;
            if (hasCAtomicMarker && hasCTypedefTypeMarker)
            {
                foreach (Match match in CTypedefAtomicTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            if (hasCAtomicMarker && hasCTaggedTypeMarker)
            {
                foreach (Match match in CTaggedAtomicTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            var hasCAlignasMarker = hasCParen
                && (preparedLine.IndexOf("alignas", StringComparison.Ordinal) >= 0
                    || preparedLine.IndexOf("_Alignas", StringComparison.Ordinal) >= 0);
            if (hasCAlignasMarker && hasCTypedefTypeMarker)
            {
                foreach (Match match in CTypedefAlignasTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            if (hasCAlignasMarker && hasCTaggedTypeMarker)
            {
                foreach (Match match in CTaggedAlignasTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            var hasCFunctionPointerMarker = hasCParen
                && preparedLine.IndexOf('*') >= 0;
            var hasCFunctionPointerAliasMarker = hasCFunctionPointerMarker
                && ContainsOrdinalKeyword(preparedLine, "typedef");
            if (hasCFunctionPointerAliasMarker && hasCTypedefTypeMarker)
            {
                foreach (Match match in CTypedefFunctionPointerAliasTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            if (hasCFunctionPointerAliasMarker && hasCTaggedTypeMarker)
            {
                foreach (Match match in CTaggedFunctionPointerAliasTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            if (hasCFunctionPointerMarker && hasCTypedefTypeMarker)
            {
                foreach (Match match in CTypedefFunctionPointerDeclarationTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            if (hasCFunctionPointerMarker && hasCTaggedTypeMarker)
            {
                foreach (Match match in CTaggedFunctionPointerDeclarationTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            var hasCPointerArrayMarker = hasCFunctionPointerMarker
                && preparedLine.IndexOf('[') >= 0;
            if (hasCPointerArrayMarker && hasCTypedefTypeMarker)
            {
                foreach (Match match in CTypedefPointerArrayDeclarationTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            if (hasCPointerArrayMarker && hasCTaggedTypeMarker)
            {
                foreach (Match match in CTaggedPointerArrayDeclarationTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            var hasCOffsetofMarker = hasCParen
                && preparedLine.IndexOf(',') >= 0
                && (preparedLine.IndexOf("offsetof", StringComparison.Ordinal) >= 0
                    || preparedLine.IndexOf("__builtin_offsetof", StringComparison.Ordinal) >= 0);
            if (hasCOffsetofMarker && hasCTypedefTypeMarker)
            {
                foreach (Match match in CTypedefOffsetofTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            if (hasCOffsetofMarker && hasCTaggedTypeMarker)
            {
                foreach (Match match in CTaggedOffsetofTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            var hasCVaArgMarker = hasCParen
                && preparedLine.IndexOf(',') >= 0
                && (preparedLine.IndexOf("va_arg", StringComparison.Ordinal) >= 0
                    || preparedLine.IndexOf("__builtin_va_arg", StringComparison.Ordinal) >= 0);
            if (hasCVaArgMarker && hasCTypedefTypeMarker)
            {
                foreach (Match match in CTypedefVaArgTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            if (hasCVaArgMarker && hasCTaggedTypeMarker)
            {
                foreach (Match match in CTaggedVaArgTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            if (hasCVaArgMarker)
                EmitCVaArgTypeOperandReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn, language);
        }

        var hasCppParen = preparedLine.IndexOf('(') >= 0;
        var hasCppTypeOperandOperatorMarker = hasCppParen
            && (preparedLine.IndexOf("sizeof", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("alignof", StringComparison.Ordinal) >= 0);
        if (hasCppTypeOperandOperatorMarker)
        {
            foreach (Match match in CppTypeOperandOperatorRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
            }
        }

        var hasCppTypeIdMarker = hasCppParen
            && preparedLine.IndexOf("typeid", StringComparison.Ordinal) >= 0;
        if (hasCppTypeIdMarker)
        {
            foreach (Match match in CppTypeIdRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
            }
        }

        var hasCppDecltypeBraceMarker = hasCppParen
            && preparedLine.IndexOf('{') >= 0
            && preparedLine.IndexOf("decltype", StringComparison.Ordinal) >= 0;
        if (hasCppDecltypeBraceMarker)
        {
            foreach (Match match in CppDecltypeBraceConstructionRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
            }
        }

        var hasCppTemplateOpen = preparedLine.IndexOf('<') >= 0;
        var hasCppFactoryTemplateMarker = hasCppParen
            && hasCppTemplateOpen
            && preparedLine.IndexOf("make_", StringComparison.Ordinal) >= 0;
        if (hasCppFactoryTemplateMarker)
        {
            foreach (Match match in CppFactoryTemplateArgumentRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
            }
        }

        var hasCppTypeTraitTemplateMarker = hasCppTemplateOpen
            && preparedLine.IndexOf("is_", StringComparison.Ordinal) >= 0;
        if (hasCppTypeTraitTemplateMarker)
        {
            foreach (Match match in CppTypeTraitTemplateArgumentRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
            }
        }

        var hasCppBrace = preparedLine.IndexOf('{') >= 0;
        var hasCppBraceConstructionMarker = hasCppBrace
            && (preparedLine.IndexOf("return", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("throw", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf('=') >= 0);
        if (hasCppBraceConstructionMarker)
        {
            foreach (Match match in CppBraceConstructionRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                var typeName = LastCppQualifiedSegment(group.Value);
                var typeStart = group.Index + group.Value.LastIndexOf(typeName, StringComparison.Ordinal);
                ReferenceExtractor.AddReference(references, seen, fileId, typeName, typeStart, "instantiate", context, lineNumber, resolveContainerForColumn(typeStart));
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
            }
        }

        var hasCppScopeSeparator = preparedLine.IndexOf("::", StringComparison.Ordinal) >= 0;
        var hasCppQualifiedTemplateBraceMarker = hasCppBraceConstructionMarker
            && hasCppTemplateOpen
            && hasCppScopeSeparator;
        if (hasCppQualifiedTemplateBraceMarker)
        {
            foreach (Match match in CppQualifiedTemplateBraceConstructionRegex.Matches(preparedLine))
            {
                var group = match.Groups["args"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
            }
        }

        var hasCppUsingAliasMarker = preparedLine.IndexOf("using", StringComparison.Ordinal) >= 0
            && preparedLine.IndexOf('=') >= 0
            && preparedLine.IndexOf(';') >= 0;
        if (hasCppUsingAliasMarker)
        {
            foreach (Match match in CppUsingAliasTargetRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
            }
        }

        var hasCppTypedefAliasMarker = !hasCppParen
            && ContainsOrdinalKeyword(preparedLine, "typedef")
            && preparedLine.IndexOf(';') >= 0;
        if (hasCppTypedefAliasMarker)
        {
            foreach (Match match in CppTypedefAliasTargetRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
            }
        }

        var hasCppExplicitTemplateInstantiationMarker = ContainsOrdinalKeyword(preparedLine, "template")
            && preparedLine.IndexOf(';') >= 0
            && (ContainsOrdinalKeyword(preparedLine, "class")
                || ContainsOrdinalKeyword(preparedLine, "struct"));
        if (hasCppExplicitTemplateInstantiationMarker)
        {
            foreach (Match match in CppExplicitTemplateInstantiationRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                var typeName = LastCppQualifiedSegment(group.Value);
                var typeStart = group.Index + group.Value.LastIndexOf(typeName, StringComparison.Ordinal);
                ReferenceExtractor.AddReference(references, seen, fileId, typeName, typeStart, "instantiate", context, lineNumber, resolveContainerForColumn(typeStart));
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
            }
        }

        var hasCppTemplateClose = preparedLine.IndexOf('>') >= 0;
        var hasCppTemplateIdDeclarationMarker = hasCppTemplateOpen
            && hasCppTemplateClose
            && (preparedLine.IndexOf('=') >= 0
                || preparedLine.IndexOf(';') >= 0
                || preparedLine.IndexOf('{') >= 0
                || preparedLine.IndexOf(',') >= 0
                || preparedLine.IndexOf(')') >= 0
                || preparedLine.IndexOf('[') >= 0);
        if (hasCppTemplateIdDeclarationMarker)
        {
            foreach (Match match in CppTemplateIdDeclarationRegex.Matches(preparedLine))
            {
                if (IsCppTemplateDeclarationOrSpecializationLine(preparedLine, match.Index))
                    continue;

                var group = match.Groups["type"];
                var typeName = LastCppQualifiedSegment(group.Value);
                var typeStart = group.Index + group.Value.LastIndexOf(typeName, StringComparison.Ordinal);
                ReferenceExtractor.AddReference(references, seen, fileId, typeName, typeStart, "instantiate", context, lineNumber, resolveContainerForColumn(typeStart));
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, match.Groups["args"].Value, match.Groups["args"].Index, context, lineNumber, resolveContainerForColumn(match.Groups["args"].Index), language);
            }
        }

        var hasCppTemplateParameterDefaultMarker = preparedLine.IndexOf('=') >= 0
            && (preparedLine.IndexOf("typename", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("class", StringComparison.Ordinal) >= 0);
        if (hasCppTemplateParameterDefaultMarker)
        {
            foreach (Match match in CppTemplateParameterDefaultTypeRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
            }
        }

        if (hasCppScopeSeparator)
        {
            foreach (Match match in CppQualifiedMemberReceiverRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
            }
        }

        var hasCppPointerToMemberMarker = hasCppScopeSeparator
            && preparedLine.IndexOf('*') >= 0;
        if (hasCppPointerToMemberMarker)
        {
            foreach (Match match in CppPointerToMemberTypeRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
            }
        }

        var hasCppTrailingReturnMarker = preparedLine.IndexOf(')') >= 0
            && preparedLine.IndexOf("->", StringComparison.Ordinal) >= 0;
        if (hasCppTrailingReturnMarker)
        {
            foreach (Match match in CppTrailingReturnTypeRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
            }
        }

        if (preparedLine.Contains("requires", StringComparison.Ordinal) || preparedLine.Contains("concept", StringComparison.Ordinal))
        {
            var hasCppRequiresConceptTypeMarker = hasCppTemplateOpen
                && preparedLine.IndexOf("requires", StringComparison.Ordinal) >= 0;
            if (hasCppRequiresConceptTypeMarker)
            {
                foreach (Match match in CppRequiresConceptTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            if (hasCppRequiresConceptTypeMarker && hasCppParen)
            {
                foreach (Match match in CppParenthesizedRequiresConceptTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }

            var hasCppQualifiedRequiresConceptMarker = hasCppRequiresConceptTypeMarker
                && hasCppScopeSeparator;
            if (hasCppQualifiedRequiresConceptMarker)
            {
                foreach (Match match in CppQualifiedRequiresConceptConstraintRegex.Matches(preparedLine))
                {
                    var conceptGroup = match.Groups["concept"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, conceptGroup.Value, conceptGroup.Index, context, lineNumber, resolveContainerForColumn(conceptGroup.Index), language);

                    var argsGroup = match.Groups["args"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, argsGroup.Value, argsGroup.Index, context, lineNumber, resolveContainerForColumn(argsGroup.Index), language);
                }
            }

            var hasCppConceptExpressionMarker = hasCppTemplateOpen
                && (preparedLine.IndexOf('=') >= 0
                    || preparedLine.IndexOf("&&", StringComparison.Ordinal) >= 0
                    || preparedLine.IndexOf("||", StringComparison.Ordinal) >= 0);
            if (hasCppConceptExpressionMarker)
            {
                foreach (Match match in CppConceptExpressionTypeRegex.Matches(preparedLine))
                {
                    var group = match.Groups["type"];
                    ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
                }
            }
        }

        var hasCppCompoundRequirementConceptMarker = hasCppTemplateOpen
            && hasCppTemplateClose
            && preparedLine.IndexOf("->", StringComparison.Ordinal) >= 0;
        if (hasCppCompoundRequirementConceptMarker)
        {
            foreach (Match match in CppCompoundRequirementConceptRegex.Matches(preparedLine))
            {
                var conceptGroup = match.Groups["concept"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, conceptGroup.Value, conceptGroup.Index, context, lineNumber, resolveContainerForColumn(conceptGroup.Index), language);

                var argsGroup = match.Groups["args"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, argsGroup.Value, argsGroup.Index, context, lineNumber, resolveContainerForColumn(argsGroup.Index), language);
            }
        }

        var hasCppFriendTypeMarker = preparedLine.IndexOf("friend", StringComparison.Ordinal) >= 0
            && preparedLine.IndexOf(';') >= 0
            && (preparedLine.IndexOf("class", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("struct", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("union", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("typename", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("enum", StringComparison.Ordinal) >= 0);
        if (hasCppFriendTypeMarker)
        {
            foreach (Match match in CppFriendTypeRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
            }
        }

        var hasCppDynamicExceptionSpecMarker = hasCppParen
            && preparedLine.IndexOf("throw", StringComparison.Ordinal) >= 0;
        if (hasCppDynamicExceptionSpecMarker)
        {
            foreach (Match match in CppDynamicExceptionSpecRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), language);
            }
        }

        var hasCppDeclarationTerminator = preparedLine.IndexOf(',') >= 0
            || preparedLine.IndexOf(';') >= 0
            || preparedLine.IndexOf(')') >= 0
            || preparedLine.IndexOf('=') >= 0;
        var hasCppDeclarationTypeMarker = hasCppDeclarationTerminator
            && (ContainsAsciiUppercase(preparedLine)
                || hasCppScopeSeparator
                || hasCppTemplateOpen
                || preparedLine.IndexOf("const", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("volatile", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("static", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("inline", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("constexpr", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("typename", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("class", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("struct", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("enum", StringComparison.Ordinal) >= 0);
        if (hasCppDeclarationTypeMarker)
        {
            foreach (Match match in CppDeclarationTypeRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                var expression = StripCppAccessPrefix(group.Value);
                if (expression.Length == 0)
                    continue;

                var start = group.Index + group.Value.IndexOf(expression, StringComparison.Ordinal);
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, expression, start, context, lineNumber, resolveContainerForColumn(start), language);
            }
        }
    }


}
