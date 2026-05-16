using System.Text.RegularExpressions;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static class PhpReferenceExtractor
{
    private static readonly Regex StaticAccessRegex = new(
        @"(?<![\w$\\])(?<name>(?:\\?[A-Za-z_]\w*(?:\\[A-Za-z_]\w*)*))::\$?(?<member>[A-Za-z_]\w*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ObjectMemberAccessRegex = new(
        @"(?:\?->|->)\s*(?<name>[A-Za-z_]\w*)(?!\s*\()",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AttributeRegex = new(
        @"(?:#\[\s*|,\s*)(?<name>\\?[A-Za-z_]\w*(?:\\[A-Za-z_]\w*)*)\b(?=\s*(?:\(|,|\]))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DocblockParamTypeRegex = new(
        @"^\s*(?:/\*\*)?\s*\*?\s*@(?>phpstan-|psalm-)?param(?:-out)?\s+(?<types>\S+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex DocblockReturnTypeRegex = new(
        @"^\s*(?:/\*\*)?\s*\*?\s*@(?>phpstan-|psalm-)?return\s+(?<types>\S+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex DocblockVarTypeRegex = new(
        @"^\s*(?:/\*\*)?\s*\*?\s*@(?>phpstan-|psalm-)?var\s+(?<types>\S+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex DocblockThrowsTypeRegex = new(
        @"^\s*(?:/\*\*)?\s*\*?\s*@throws\s+(?<types>\S+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex DocblockExtendsTypeRegex = new(
        @"^\s*(?:/\*\*)?\s*\*?\s*@(?>phpstan-|psalm-)?extends\s+(?<types>\S+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex DocblockImplementsTypeRegex = new(
        @"^\s*(?:/\*\*)?\s*\*?\s*@(?>phpstan-|psalm-)?implements\s+(?<types>\S+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex DocblockMixinTypeRegex = new(
        @"^\s*(?:/\*\*)?\s*\*?\s*@mixin\s+(?<types>\S+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex DocblockPropertyTypeRegex = new(
        @"^\s*(?:/\*\*)?\s*\*?\s*@(?>phpstan-|psalm-)?property(?:-read|-write)?\s+(?<types>\S+)(?:\s+\$(?<name>[A-Za-z_]\w*))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex DocblockMethodReturnTypeRegex = new(
        @"^\s*(?:/\*\*)?\s*\*?\s*@method\s+(?:static\s+)?(?<types>[^\s()]+)\s+[A-Za-z_]\w*\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex DocblockMethodParameterListRegex = new(
        @"^\s*(?:/\*\*)?\s*\*?\s*@method\s+(?:static\s+)?(?:[^\s()]+\s+)?[A-Za-z_]\w*\s*\((?<params>[^)]*)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex DocblockMethodParameterTypeRegex = new(
        @"(?:^|,)\s*(?<types>[^\s,]+)\s+\$[A-Za-z_]\w*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DocblockTemplateBoundTypeRegex = new(
        @"^\s*(?:/\*\*)?\s*\*?\s*@template(?:-[A-Za-z_]\w*)?\s+[A-Za-z_]\w*\s+(?:of|as)\s+(?<types>\S+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex DocblockTypeAliasTargetRegex = new(
        @"^\s*(?:/\*\*)?\s*\*?\s*@(?>phpstan-|psalm-)?type\s+[A-Za-z_]\w*\s+(?<types>\S+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex DocblockImportTypeSourceRegex = new(
        @"^\s*(?:/\*\*)?\s*\*?\s*@(?>phpstan-|psalm-)?import-type\s+[A-Za-z_]\w*\s+from\s+(?<types>\S+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex DocblockTypeNameRegex = new(
        @"(?<![-\w\\])\??(?<name>\\?[A-Za-z_]\w*(?:\\[A-Za-z_]\w*)*)(?:\[\])?(?![-\w\\])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex InstanceofRegex = new(
        @"\binstanceof\s+(?<name>\\?[A-Za-z_]\w*(?:\\[A-Za-z_]\w*)*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex CatchTypeRegex = new(
        @"\bcatch\s*\(\s*(?<name>\\?[A-Za-z_]\w*(?:\\[A-Za-z_]\w*)*)(?:\s*\|\s*(?<name>\\?[A-Za-z_]\w*(?:\\[A-Za-z_]\w*)*))*\s+\$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex ReturnTypeRegex = new(
        @"\)\s*:\s*\??(?<name>\\?[A-Za-z_]\w*(?:\\[A-Za-z_]\w*)*)(?:\s*\|\s*\??(?<name>\\?[A-Za-z_]\w*(?:\\[A-Za-z_]\w*)*))*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ParameterTypeRegex = new(
        @"(?:^|[(,])\s*\??(?<name>\\?[A-Za-z_]\w*(?:\\[A-Za-z_]\w*)*)(?:\s*[|&]\s*\??(?<name>\\?[A-Za-z_]\w*(?:\\[A-Za-z_]\w*)*))*\s+\$[A-Za-z_]\w*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PropertyTypeRegex = new(
        @"^\s*(?:public|private|protected|var)\s+(?:(?:static|readonly)\s+)*\??(?<name>\\?[A-Za-z_]\w*(?:\\[A-Za-z_]\w*)*)(?:\s*[|&]\s*\??(?<name>\\?[A-Za-z_]\w*(?:\\[A-Za-z_]\w*)*))*\s+\$[A-Za-z_]\w*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex InheritanceTypeRegex = new(
        @"\b(?:extends|implements)\s+(?<name>\\?[A-Za-z_]\w*(?:\\[A-Za-z_]\w*)*)(?:\s*,\s*(?<name>\\?[A-Za-z_]\w*(?:\\[A-Za-z_]\w*)*))*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex UseTypeRegex = new(
        @"^\s*use\s+(?!(?:function|const)\b)(?<name>\\?[A-Za-z_]\w*(?:\\[A-Za-z_]\w*)*)(?:\s+as\s+[A-Za-z_]\w*)?(?:\s*,\s*(?<name>\\?[A-Za-z_]\w*(?:\\[A-Za-z_]\w*)*))*\s*(?:;|\{)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex UseFunctionRegex = new(
        @"^\s*use\s+function\s+(?<imports>.+?)\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex UseConstRegex = new(
        @"^\s*use\s+const\s+(?<imports>.+?)\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex UseImportItemRegex = new(
        @"(?:^|,)\s*(?<name>\\?[A-Za-z_]\w*(?:\\[A-Za-z_]\w*)*)(?:\s+as\s+[A-Za-z_]\w*)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex GroupUseTypeRegex = new(
        @"^\s*use\s+(?!(?:function|const)\b)(?<prefix>\\?[A-Za-z_]\w*(?:\\[A-Za-z_]\w*)*)\\\{\s*(?<items>[^{}]+?)\s*\}\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex GroupUseFunctionRegex = new(
        @"^\s*use\s+function\s+(?<prefix>\\?[A-Za-z_]\w*(?:\\[A-Za-z_]\w*)*)\\\{\s*(?<items>[^{}]+?)\s*\}\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex GroupUseConstRegex = new(
        @"^\s*use\s+const\s+(?<prefix>\\?[A-Za-z_]\w*(?:\\[A-Za-z_]\w*)*)\\\{\s*(?<items>[^{}]+?)\s*\}\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex GroupUseTypeItemRegex = new(
        @"(?:^|,)\s*(?:(?<kind>function|const)\s+)?(?<name>\\?[A-Za-z_]\w*(?:\\[A-Za-z_]\w*)*)(?:\s+as\s+[A-Za-z_]\w*)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> BuiltinTypeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "array", "bool", "callable", "false", "float", "int", "iterable", "mixed", "never",
        "class", "null", "numeric", "object", "resource", "scalar", "self", "static", "string", "true", "void",
    };

    public static void EmitDocblockParamTypeReferences(
        string originalLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
        => EmitDocblockTypeReferences(
            DocblockParamTypeRegex,
            originalLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            container);

    public static void EmitDocblockReturnTypeReferences(
        string originalLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
        => EmitDocblockTypeReferences(
            DocblockReturnTypeRegex,
            originalLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            container);

    public static void EmitDocblockVarTypeReferences(
        string originalLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
        => EmitDocblockTypeReferences(
            DocblockVarTypeRegex,
            originalLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            container);

    public static void EmitDocblockThrowsTypeReferences(
        string originalLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
        => EmitDocblockTypeReferences(
            DocblockThrowsTypeRegex,
            originalLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            container);

    public static void EmitDocblockExtendsTypeReferences(
        string originalLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
        => EmitDocblockTypeReferences(
            DocblockExtendsTypeRegex,
            originalLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            container);

    public static void EmitDocblockImplementsTypeReferences(
        string originalLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
        => EmitDocblockTypeReferences(
            DocblockImplementsTypeRegex,
            originalLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            container);

    public static void EmitDocblockMixinTypeReferences(
        string originalLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
        => EmitDocblockTypeReferences(
            DocblockMixinTypeRegex,
            originalLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            container);

    public static void EmitDocblockPropertyTypeReferences(
        string originalLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        HashSet<string>? seenDocblockPropertyNames = null)
    {
        var match = DocblockPropertyTypeRegex.Match(originalLine);
        if (!match.Success)
            return;

        var nameGroup = match.Groups["name"];
        if (nameGroup.Success && seenDocblockPropertyNames != null
            && !seenDocblockPropertyNames.Add(nameGroup.Value))
        {
            return;
        }

        var typesGroup = match.Groups["types"];
        EmitDocblockTypeGroupReferences(
            typesGroup.Value,
            typesGroup.Index,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            container);
    }

    public static void EmitDocblockMethodReturnTypeReferences(
        string originalLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
        => EmitDocblockTypeReferences(
            DocblockMethodReturnTypeRegex,
            originalLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            container);

    public static void EmitDocblockMethodParameterTypeReferences(
        string originalLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        var match = DocblockMethodParameterListRegex.Match(originalLine);
        if (!match.Success)
            return;

        var paramsGroup = match.Groups["params"];
        foreach (Match parameterMatch in DocblockMethodParameterTypeRegex.Matches(paramsGroup.Value))
        {
            var typesGroup = parameterMatch.Groups["types"];
            EmitDocblockTypeGroupReferences(
                typesGroup.Value,
                paramsGroup.Index + typesGroup.Index,
                references,
                seen,
                fileId,
                context,
                lineNumber,
                container);
        }
    }

    public static void EmitDocblockTemplateBoundTypeReferences(
        string originalLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
        => EmitDocblockTypeReferences(
            DocblockTemplateBoundTypeRegex,
            originalLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            container);

    public static void EmitDocblockTypeAliasTargetReferences(
        string originalLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
        => EmitDocblockTypeReferences(
            DocblockTypeAliasTargetRegex,
            originalLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            container);

    public static void EmitDocblockImportTypeSourceReferences(
        string originalLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
        => EmitDocblockTypeReferences(
            DocblockImportTypeSourceRegex,
            originalLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            container);

    private static void EmitDocblockTypeReferences(
        Regex tagRegex,
        string originalLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        var match = tagRegex.Match(originalLine);
        if (!match.Success)
            return;

        var typesGroup = match.Groups["types"];
        EmitDocblockTypeGroupReferences(
            typesGroup.Value,
            typesGroup.Index,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            container);
    }

    private static void EmitDocblockTypeGroupReferences(
        string typeExpression,
        int typeExpressionIndex,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        foreach (Match typeMatch in DocblockTypeNameRegex.Matches(typeExpression))
        {
            var nameGroup = typeMatch.Groups["name"];
            if (IsPhpBuiltinTypeName(nameGroup.Value))
                continue;

            AddPhpTypeReferenceFromName(
                nameGroup.Value,
                typeExpressionIndex + nameGroup.Index,
                references,
                seen,
                fileId,
                context,
                lineNumber,
                container);
        }
    }

    public static void EmitAttributeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (!preparedLine.Contains("#[", StringComparison.Ordinal))
            return;

        foreach (Match match in AttributeRegex.Matches(preparedLine))
        {
            var nameGroup = match.Groups["name"];
            var rawName = nameGroup.Value;
            var trimmedName = rawName.TrimStart('\\');
            if (trimmedName.Length == 0)
                continue;

            var leadingBackslashCount = rawName.Length - trimmedName.Length;
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
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        foreach (Match match in StaticAccessRegex.Matches(preparedLine))
        {
            var nameGroup = match.Groups["name"];
            var rawName = nameGroup.Value;
            var trimmedName = rawName.TrimStart('\\');
            if (trimmedName.Length == 0)
                continue;

            var leadingBackslashCount = rawName.Length - trimmedName.Length;
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
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        foreach (Match match in InstanceofRegex.Matches(preparedLine))
        {
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
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        foreach (Match match in CatchTypeRegex.Matches(preparedLine))
        {
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
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        foreach (Match match in ReturnTypeRegex.Matches(preparedLine))
        {
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
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        foreach (Match match in ParameterTypeRegex.Matches(preparedLine))
        {
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
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        foreach (Match match in PropertyTypeRegex.Matches(preparedLine))
        {
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
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        foreach (Match match in InheritanceTypeRegex.Matches(preparedLine))
        {
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
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
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
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
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
        foreach (Match itemMatch in UseImportItemRegex.Matches(importsGroup.Value))
        {
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
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
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
        foreach (Match itemMatch in UseImportItemRegex.Matches(importsGroup.Value))
        {
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
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        var prefixGroup = groupMatch.Groups["prefix"];
        var prefix = prefixGroup.Value.TrimEnd('\\');
        if (prefix.Length == 0)
            return;

        var itemsGroup = groupMatch.Groups["items"];
        foreach (Match itemMatch in GroupUseTypeItemRegex.Matches(itemsGroup.Value))
        {
            if (itemMatch.Groups["kind"].Success)
                continue;

            var itemGroup = itemMatch.Groups["name"];
            var rawItemName = itemGroup.Value;
            var trimmedItemName = rawItemName.TrimStart('\\');
            if (trimmedItemName.Length == 0)
                continue;

            var leadingBackslashCount = rawItemName.Length - trimmedItemName.Length;
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
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        string importKind,
        bool requireImportKind)
    {
        var prefixGroup = groupMatch.Groups["prefix"];
        var prefix = prefixGroup.Value.TrimEnd('\\');
        if (prefix.Length == 0)
            return;

        var itemsGroup = groupMatch.Groups["items"];
        foreach (Match itemMatch in GroupUseTypeItemRegex.Matches(itemsGroup.Value))
        {
            var isTargetKind = itemMatch.Groups["kind"].Success
                && itemMatch.Groups["kind"].Value.Equals(importKind, StringComparison.OrdinalIgnoreCase);
            if (requireImportKind != isTargetKind)
                continue;

            var itemGroup = itemMatch.Groups["name"];
            var rawItemName = itemGroup.Value;
            var trimmedItemName = rawItemName.TrimStart('\\');
            if (trimmedItemName.Length == 0)
                continue;

            var leadingBackslashCount = rawItemName.Length - trimmedItemName.Length;
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

    private static bool IsPhpCallAfterStaticMember(string line, int index)
    {
        while (index < line.Length && char.IsWhiteSpace(line[index]))
            index++;

        return index < line.Length && line[index] == '(';
    }

    private static bool IsPhpBuiltinTypeName(string name)
        => !name.Contains('\\', StringComparison.Ordinal)
           && BuiltinTypeNames.Contains(name.TrimStart('\\'));

    private static void AddPhpTypeReferenceFromQualifiedName(
        Capture nameGroup,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
        => AddPhpTypeReferenceFromName(
            nameGroup.Value,
            nameGroup.Index,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            container);

    private static void AddPhpTypeReferenceFromName(
        string rawName,
        int nameIndex,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        int? shortNameIndexOverride = null)
        => AddPhpReferenceFromName(
            rawName,
            nameIndex,
            "type_reference",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            container,
            shortNameIndexOverride);

    private static void AddPhpReferenceFromName(
        string rawName,
        int nameIndex,
        string referenceKind,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        int? shortNameIndexOverride = null)
    {
        var trimmedName = rawName.TrimStart('\\');
        if (trimmedName.Length == 0)
            return;

        var leadingBackslashCount = rawName.Length - trimmedName.Length;
        var qualifiedNameIndex = nameIndex + leadingBackslashCount;
        if (trimmedName.Contains('\\', StringComparison.Ordinal))
        {
            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                trimmedName,
                qualifiedNameIndex,
                referenceKind,
                context,
                lineNumber,
                container);
        }

        var shortNameStart = trimmedName.LastIndexOf('\\') + 1;
        var shortName = trimmedName[shortNameStart..];
        if (shortName.Length == 0)
            return;

        ReferenceExtractor.AddReference(
            references,
            seen,
            fileId,
            shortName,
            shortNameIndexOverride ?? qualifiedNameIndex + shortNameStart,
            referenceKind,
            context,
            lineNumber,
            container);
    }

    public static void EmitObjectMemberAccessReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        foreach (Match match in ObjectMemberAccessRegex.Matches(preparedLine))
        {
            var nameGroup = match.Groups["name"];
            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                nameGroup.Value,
                nameGroup.Index,
                "reference",
                context,
                lineNumber,
                container);
        }
    }
}
