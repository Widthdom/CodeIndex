using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class LanguageReferenceExtractionSupport
{
    private sealed class CppTypeReferenceLineContext(
        string language,
        string preparedLine,
        string originalLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string sourceContext,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        internal string Language { get; } = language;
        internal string PreparedLine { get; } = preparedLine;
        internal string OriginalLine { get; } = originalLine;
        internal bool LimitReached =>
            ReferenceExtractor.ReferenceLimitReached(references);

        internal void EmitTypeExpressions(Regex regex)
        {
            foreach (Match match in Regex.EnumerateMatches(regex, PreparedLine))
            {
                if (LimitReached)
                    break;

                AddTypeExpression(match.Groups["type"]);
            }
        }

        internal void AddTypeExpression(Group group)
            => AddTypeExpression(group.Value, group.Index);

        internal void AddTypeExpression(string expression, int start)
            => ReferenceExtractor.AddTypeExpressionSegments(
                references,
                seen,
                fileId,
                expression,
                start,
                sourceContext,
                lineNumber,
                resolveContainerForColumn(start),
                Language);

        internal void AddReference(Group group, string kind)
            => ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                group.Value,
                group.Index,
                kind,
                sourceContext,
                lineNumber,
                resolveContainerForColumn(group.Index));

        internal void AddInstantiation(Group group)
        {
            AddInstantiationReference(group);
            AddTypeExpression(group);
        }

        internal void AddInstantiationReference(Group group)
        {
            var typeName = LastCppQualifiedSegment(group.Value);
            var typeStart =
                group.Index
                + group.Value.LastIndexOf(typeName, StringComparison.Ordinal);
            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                typeName,
                typeStart,
                "instantiate",
                sourceContext,
                lineNumber,
                resolveContainerForColumn(typeStart));
        }

        internal void EmitCVaArgTypeOperandReferences()
            => LanguageReferenceExtractionSupport.EmitCVaArgTypeOperandReferences(
                PreparedLine,
                references,
                seen,
                fileId,
                sourceContext,
                lineNumber,
                resolveContainerForColumn,
                Language);
    }

    private static void EmitCppHeaderConstructionAndCastReferences(
        CppTypeReferenceLineContext line)
    {
        var preparedLine = line.PreparedLine;
        var originalLine = line.OriginalLine;
        var hasCppIncludeMarker = !string.IsNullOrWhiteSpace(preparedLine)
            && (originalLine.IndexOf('#') >= 0
                || originalLine.IndexOf("import", StringComparison.Ordinal) >= 0
                || originalLine.IndexOf("include", StringComparison.Ordinal) >= 0);
        var includeMatch = hasCppIncludeMarker
            ? CppIncludeRegex.Match(originalLine)
            : Match.Empty;
        if (includeMatch.Success)
            line.AddReference(includeMatch.Groups["name"], "type_reference");

        var baseMatch = preparedLine.IndexOf(':') >= 0
            && (ContainsOrdinalKeyword(preparedLine, "class")
                || ContainsOrdinalKeyword(preparedLine, "struct"))
            ? CppBaseListRegex.Match(preparedLine)
            : Match.Empty;
        if (baseMatch.Success)
        {
            var group = baseMatch.Groups["bases"];
            foreach (var (segmentStart, segmentLength) in
                     ReferenceExtractor.SplitTopLevelCommaSpans(group.Value))
            {
                var segment = group.Value.Substring(segmentStart, segmentLength);
                var expression = StripCppAccessPrefix(segment);
                if (expression.Length == 0)
                    continue;

                var absoluteStart = group.Index
                    + segmentStart
                    + segment.IndexOf(expression, StringComparison.Ordinal);
                line.AddTypeExpression(expression, absoluteStart);
            }
        }

        if (ContainsOrdinalKeyword(preparedLine, "new"))
        {
            foreach (Match match in Regex.EnumerateMatches(
                         CppNewTypeRegex,
                         preparedLine))
            {
                if (line.LimitReached)
                    break;

                line.AddInstantiation(match.Groups["type"]);
            }
        }

        if (preparedLine.IndexOf("_cast", StringComparison.Ordinal) >= 0)
            line.EmitTypeExpressions(CppNamedCastTypeRegex);
        if (preparedLine.IndexOf('(') >= 0)
            line.EmitTypeExpressions(CppCStyleCastTypeRegex);
    }

    private static void EmitCTypeReferences(CppTypeReferenceLineContext line)
    {
        var preparedLine = line.PreparedLine;
        var hasParen = preparedLine.IndexOf('(') >= 0;
        var hasTypedef = preparedLine.IndexOf("_t", StringComparison.Ordinal) >= 0;
        var hasTagged = ContainsOrdinalKeyword(preparedLine, "struct")
            || preparedLine.IndexOf("enum", StringComparison.Ordinal) >= 0
            || preparedLine.IndexOf("union", StringComparison.Ordinal) >= 0;

        EmitCTypePair(
            line,
            hasParen,
            hasTypedef,
            CTypedefCastTypeRegex,
            hasTagged: false,
            taggedRegex: null);

        var hasSizeof = hasParen
            && preparedLine.IndexOf("sizeof", StringComparison.Ordinal) >= 0;
        EmitCTypePair(
            line,
            hasSizeof,
            hasTypedef,
            CTypedefSizeofTypeRegex,
            hasTagged,
            CTaggedSizeofTypeRegex);

        var hasAlignof = hasParen
            && (preparedLine.IndexOf("alignof", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("_Alignof", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("__alignof", StringComparison.Ordinal) >= 0);
        EmitCTypePair(
            line,
            hasAlignof,
            hasTypedef,
            CTypedefAlignofTypeRegex,
            hasTagged,
            CTaggedAlignofTypeRegex);

        EmitCDeclarationTypeReferences(line, hasParen, hasTypedef, hasTagged);
        EmitCExtensionTypeReferences(line, hasParen, hasTypedef, hasTagged);
        EmitCFunctionPointerAndBuiltinOperandTypeReferences(
            line,
            hasParen,
            hasTypedef,
            hasTagged);
    }

    private static void EmitCDeclarationTypeReferences(
        CppTypeReferenceLineContext line,
        bool hasParen,
        bool hasTypedef,
        bool hasTagged)
    {
        var preparedLine = line.PreparedLine;
        var hasDeclarationTerminator = preparedLine.IndexOf('=') >= 0
            || preparedLine.IndexOf(',') >= 0
            || preparedLine.IndexOf(';') >= 0
            || preparedLine.IndexOf('[') >= 0;
        EmitCTypePair(
            line,
            hasDeclarationTerminator,
            hasTypedef,
            CTypedefDeclarationTypeRegex,
            hasTagged,
            CTaggedDeclarationTypeRegex);
        EmitCTypePair(
            line,
            hasParen,
            hasTypedef,
            CTypedefFunctionReturnTypeRegex,
            hasTagged,
            CTaggedFunctionReturnTypeRegex);

        var hasParameterDelimiter = hasParen
            || preparedLine.IndexOf(',') >= 0;
        EmitCTypePair(
            line,
            hasParameterDelimiter,
            hasTypedef,
            CTypedefParameterTypeRegex,
            hasTagged,
            CTaggedParameterTypeRegex);

        var hasCompoundLiteral = hasParen
            && preparedLine.IndexOf('{') >= 0;
        EmitCTypePair(
            line,
            hasCompoundLiteral,
            hasTypedef,
            CTypedefCompoundLiteralTypeRegex,
            hasTagged,
            CTaggedCompoundLiteralTypeRegex);
    }

    private static void EmitCExtensionTypeReferences(
        CppTypeReferenceLineContext line,
        bool hasParen,
        bool hasTypedef,
        bool hasTagged)
    {
        var preparedLine = line.PreparedLine;
        var hasTypeof = hasParen
            && preparedLine.IndexOf("typeof", StringComparison.Ordinal) >= 0;
        EmitCTypePair(
            line,
            hasTypeof,
            hasTypedef,
            CTypedefTypeofTypeRegex,
            hasTagged,
            CTaggedTypeofTypeRegex);
        EmitCTypePair(
            line,
            hasTypeof,
            hasTypedef,
            CTypedefTypeofUnqualTypeRegex,
            hasTagged,
            CTaggedTypeofUnqualTypeRegex);

        var hasBuiltinTypesCompatible = hasParen
            && preparedLine.IndexOf(
                "__builtin_types_compatible_p",
                StringComparison.Ordinal) >= 0;
        EmitCTypePair(
            line,
            hasBuiltinTypesCompatible,
            hasTypedef,
            CTypedefBuiltinTypesCompatibleFirstTypeRegex,
            hasTagged,
            CTaggedBuiltinTypesCompatibleFirstTypeRegex);
        EmitCTypePair(
            line,
            hasBuiltinTypesCompatible,
            hasTypedef,
            CTypedefBuiltinTypesCompatibleSecondTypeRegex,
            hasTagged,
            CTaggedBuiltinTypesCompatibleSecondTypeRegex);

        var hasGenericAssociation = preparedLine.IndexOf(':') >= 0
            && (preparedLine.IndexOf("_Generic", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf(',') >= 0);
        EmitCTypePair(
            line,
            hasGenericAssociation,
            hasTypedef,
            CTypedefGenericAssociationTypeRegex,
            hasTagged,
            CTaggedGenericAssociationTypeRegex);

        var hasAtomic = hasParen
            && preparedLine.IndexOf("_Atomic", StringComparison.Ordinal) >= 0;
        EmitCTypePair(
            line,
            hasAtomic,
            hasTypedef,
            CTypedefAtomicTypeRegex,
            hasTagged,
            CTaggedAtomicTypeRegex);

        var hasAlignas = hasParen
            && (preparedLine.IndexOf("alignas", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("_Alignas", StringComparison.Ordinal) >= 0);
        EmitCTypePair(
            line,
            hasAlignas,
            hasTypedef,
            CTypedefAlignasTypeRegex,
            hasTagged,
            CTaggedAlignasTypeRegex);
    }

    private static void EmitCFunctionPointerAndBuiltinOperandTypeReferences(
        CppTypeReferenceLineContext line,
        bool hasParen,
        bool hasTypedef,
        bool hasTagged)
    {
        var preparedLine = line.PreparedLine;
        var hasFunctionPointer = hasParen
            && preparedLine.IndexOf('*') >= 0;
        var hasFunctionPointerAlias = hasFunctionPointer
            && ContainsOrdinalKeyword(preparedLine, "typedef");
        EmitCTypePair(
            line,
            hasFunctionPointerAlias,
            hasTypedef,
            CTypedefFunctionPointerAliasTypeRegex,
            hasTagged,
            CTaggedFunctionPointerAliasTypeRegex);
        EmitCTypePair(
            line,
            hasFunctionPointer,
            hasTypedef,
            CTypedefFunctionPointerDeclarationTypeRegex,
            hasTagged,
            CTaggedFunctionPointerDeclarationTypeRegex);

        var hasPointerArray = hasFunctionPointer
            && preparedLine.IndexOf('[') >= 0;
        EmitCTypePair(
            line,
            hasPointerArray,
            hasTypedef,
            CTypedefPointerArrayDeclarationTypeRegex,
            hasTagged,
            CTaggedPointerArrayDeclarationTypeRegex);

        var hasOffsetof = hasParen
            && preparedLine.IndexOf(',') >= 0
            && (preparedLine.IndexOf("offsetof", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf(
                    "__builtin_offsetof",
                    StringComparison.Ordinal) >= 0);
        EmitCTypePair(
            line,
            hasOffsetof,
            hasTypedef,
            CTypedefOffsetofTypeRegex,
            hasTagged,
            CTaggedOffsetofTypeRegex);

        var hasVaArg = hasParen
            && preparedLine.IndexOf(',') >= 0
            && (preparedLine.IndexOf("va_arg", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf(
                    "__builtin_va_arg",
                    StringComparison.Ordinal) >= 0);
        EmitCTypePair(
            line,
            hasVaArg,
            hasTypedef,
            CTypedefVaArgTypeRegex,
            hasTagged,
            CTaggedVaArgTypeRegex);
        if (hasVaArg)
            line.EmitCVaArgTypeOperandReferences();
    }

    private static void EmitCTypePair(
        CppTypeReferenceLineContext line,
        bool syntaxPresent,
        bool hasTypedef,
        Regex typedefRegex,
        bool hasTagged,
        Regex? taggedRegex)
    {
        if (!syntaxPresent)
            return;
        if (hasTypedef)
            line.EmitTypeExpressions(typedefRegex);
        if (hasTagged && taggedRegex != null)
            line.EmitTypeExpressions(taggedRegex);
    }

    private static void EmitCppOperandConstructionAndAliasReferences(
        CppTypeReferenceLineContext line)
    {
        var preparedLine = line.PreparedLine;
        var hasParen = preparedLine.IndexOf('(') >= 0;
        var hasTemplateOpen = preparedLine.IndexOf('<') >= 0;
        if (hasParen
            && (preparedLine.IndexOf("sizeof", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("alignof", StringComparison.Ordinal) >= 0))
        {
            line.EmitTypeExpressions(CppTypeOperandOperatorRegex);
        }
        if (hasParen
            && preparedLine.IndexOf("typeid", StringComparison.Ordinal) >= 0)
        {
            line.EmitTypeExpressions(CppTypeIdRegex);
        }
        if (hasParen
            && preparedLine.IndexOf('{') >= 0
            && preparedLine.IndexOf("decltype", StringComparison.Ordinal) >= 0)
        {
            line.EmitTypeExpressions(CppDecltypeBraceConstructionRegex);
        }
        if (hasParen
            && hasTemplateOpen
            && preparedLine.IndexOf("make_", StringComparison.Ordinal) >= 0)
        {
            line.EmitTypeExpressions(CppFactoryTemplateArgumentRegex);
        }
        if (hasTemplateOpen
            && preparedLine.IndexOf("is_", StringComparison.Ordinal) >= 0)
        {
            line.EmitTypeExpressions(CppTypeTraitTemplateArgumentRegex);
        }

        var hasBraceConstruction = preparedLine.IndexOf('{') >= 0
            && (preparedLine.IndexOf("return", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("throw", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf('=') >= 0);
        if (hasBraceConstruction)
        {
            foreach (Match match in Regex.EnumerateMatches(
                         CppBraceConstructionRegex,
                         preparedLine))
            {
                if (line.LimitReached)
                    break;

                line.AddInstantiation(match.Groups["type"]);
            }
        }

        var hasScopeSeparator =
            preparedLine.IndexOf("::", StringComparison.Ordinal) >= 0;
        if (hasBraceConstruction && hasTemplateOpen && hasScopeSeparator)
        {
            foreach (Match match in Regex.EnumerateMatches(
                         CppQualifiedTemplateBraceConstructionRegex,
                         preparedLine))
            {
                if (line.LimitReached)
                    break;

                var group = match.Groups["args"];
                line.AddTypeExpression(group);
            }
        }

        if (preparedLine.IndexOf("using", StringComparison.Ordinal) >= 0
            && preparedLine.IndexOf('=') >= 0
            && preparedLine.IndexOf(';') >= 0)
        {
            line.EmitTypeExpressions(CppUsingAliasTargetRegex);
        }
        if (!hasParen
            && ContainsOrdinalKeyword(preparedLine, "typedef")
            && preparedLine.IndexOf(';') >= 0)
        {
            line.EmitTypeExpressions(CppTypedefAliasTargetRegex);
        }

        if (ContainsOrdinalKeyword(preparedLine, "template")
            && preparedLine.IndexOf(';') >= 0
            && (ContainsOrdinalKeyword(preparedLine, "class")
                || ContainsOrdinalKeyword(preparedLine, "struct")))
        {
            foreach (Match match in Regex.EnumerateMatches(
                         CppExplicitTemplateInstantiationRegex,
                         preparedLine))
            {
                if (line.LimitReached)
                    break;

                line.AddInstantiation(match.Groups["type"]);
            }
        }
    }

    private static void EmitCppConstraintAndDeclarationReferences(
        CppTypeReferenceLineContext line)
    {
        var preparedLine = line.PreparedLine;
        var hasParen = preparedLine.IndexOf('(') >= 0;
        var hasTemplateOpen = preparedLine.IndexOf('<') >= 0;
        var hasTemplateClose = preparedLine.IndexOf('>') >= 0;
        var hasScopeSeparator =
            preparedLine.IndexOf("::", StringComparison.Ordinal) >= 0;

        var hasTemplateIdDeclaration = hasTemplateOpen
            && hasTemplateClose
            && (preparedLine.IndexOf('=') >= 0
                || preparedLine.IndexOf(';') >= 0
                || preparedLine.IndexOf('{') >= 0
                || preparedLine.IndexOf(',') >= 0
                || preparedLine.IndexOf(')') >= 0
                || preparedLine.IndexOf('[') >= 0);
        if (hasTemplateIdDeclaration)
        {
            foreach (Match match in Regex.EnumerateMatches(
                         CppTemplateIdDeclarationRegex,
                         preparedLine))
            {
                if (line.LimitReached)
                    break;

                if (IsCppTemplateDeclarationOrSpecializationLine(
                        preparedLine,
                        match.Index))
                {
                    continue;
                }

                line.AddInstantiationReference(match.Groups["type"]);
                var args = match.Groups["args"];
                line.AddTypeExpression(args);
            }
        }

        if (preparedLine.IndexOf('=') >= 0
            && (preparedLine.IndexOf("typename", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("class", StringComparison.Ordinal) >= 0))
        {
            line.EmitTypeExpressions(CppTemplateParameterDefaultTypeRegex);
        }
        if (hasScopeSeparator)
            line.EmitTypeExpressions(CppQualifiedMemberReceiverRegex);
        if (hasScopeSeparator && preparedLine.IndexOf('*') >= 0)
            line.EmitTypeExpressions(CppPointerToMemberTypeRegex);
        if (preparedLine.IndexOf(')') >= 0
            && preparedLine.IndexOf("->", StringComparison.Ordinal) >= 0)
        {
            line.EmitTypeExpressions(CppTrailingReturnTypeRegex);
        }

        EmitCppConceptReferences(
            line,
            hasParen,
            hasTemplateOpen,
            hasTemplateClose,
            hasScopeSeparator);

        if (preparedLine.IndexOf("friend", StringComparison.Ordinal) >= 0
            && preparedLine.IndexOf(';') >= 0
            && (preparedLine.IndexOf("class", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("struct", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("union", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("typename", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("enum", StringComparison.Ordinal) >= 0))
        {
            line.EmitTypeExpressions(CppFriendTypeRegex);
        }
        if (hasParen
            && preparedLine.IndexOf("throw", StringComparison.Ordinal) >= 0)
        {
            line.EmitTypeExpressions(CppDynamicExceptionSpecRegex);
        }

        var hasDeclarationTerminator = preparedLine.IndexOf(',') >= 0
            || preparedLine.IndexOf(';') >= 0
            || preparedLine.IndexOf(')') >= 0
            || preparedLine.IndexOf('=') >= 0;
        var hasDeclarationType = hasDeclarationTerminator
            && (ContainsAsciiUppercase(preparedLine)
                || hasScopeSeparator
                || hasTemplateOpen
                || preparedLine.IndexOf("const", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("volatile", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("static", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("inline", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("constexpr", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("typename", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("class", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("struct", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("enum", StringComparison.Ordinal) >= 0);
        if (!hasDeclarationType)
            return;

        foreach (Match match in Regex.EnumerateMatches(
                     CppDeclarationTypeRegex,
                     preparedLine))
        {
            if (line.LimitReached)
                break;

            var group = match.Groups["type"];
            var expression = StripCppAccessPrefix(group.Value);
            if (expression.Length == 0)
                continue;

            var start = group.Index
                + group.Value.IndexOf(expression, StringComparison.Ordinal);
            line.AddTypeExpression(expression, start);
        }
    }

    private static void EmitCppConceptReferences(
        CppTypeReferenceLineContext line,
        bool hasParen,
        bool hasTemplateOpen,
        bool hasTemplateClose,
        bool hasScopeSeparator)
    {
        var preparedLine = line.PreparedLine;
        if (preparedLine.Contains("requires", StringComparison.Ordinal)
            || preparedLine.Contains("concept", StringComparison.Ordinal))
        {
            var hasRequiresConcept = hasTemplateOpen
                && preparedLine.IndexOf("requires", StringComparison.Ordinal) >= 0;
            if (hasRequiresConcept)
                line.EmitTypeExpressions(CppRequiresConceptTypeRegex);
            if (hasRequiresConcept && hasParen)
                line.EmitTypeExpressions(CppParenthesizedRequiresConceptTypeRegex);
            if (hasRequiresConcept && hasScopeSeparator)
            {
                foreach (Match match in Regex.EnumerateMatches(
                             CppQualifiedRequiresConceptConstraintRegex,
                             preparedLine))
                {
                    if (line.LimitReached)
                        break;

                    line.AddTypeExpression(match.Groups["concept"]);
                    line.AddTypeExpression(match.Groups["args"]);
                }
            }

            if (hasTemplateOpen
                && (preparedLine.IndexOf('=') >= 0
                    || preparedLine.IndexOf("&&", StringComparison.Ordinal) >= 0
                    || preparedLine.IndexOf("||", StringComparison.Ordinal) >= 0))
            {
                line.EmitTypeExpressions(CppConceptExpressionTypeRegex);
            }
        }

        if (!hasTemplateOpen
            || !hasTemplateClose
            || preparedLine.IndexOf("->", StringComparison.Ordinal) < 0)
        {
            return;
        }

        foreach (Match match in
                 CppCompoundRequirementConceptRegex.Matches(preparedLine))
        {
            line.AddTypeExpression(match.Groups["concept"]);
            line.AddTypeExpression(match.Groups["args"]);
        }
    }
}
