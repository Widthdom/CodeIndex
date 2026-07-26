using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class PhpReferenceExtractor
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
        ReferenceDedupeSet seen,
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
        ReferenceDedupeSet seen,
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
        ReferenceDedupeSet seen,
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
        ReferenceDedupeSet seen,
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
        ReferenceDedupeSet seen,
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
        ReferenceDedupeSet seen,
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
        ReferenceDedupeSet seen,
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
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        bool trackDocblockPropertyNames,
        ref HashSet<string>? seenDocblockPropertyNames)
    {
        if (originalLine.IndexOf('@') < 0
            || originalLine.IndexOf("property", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return;
        }

        var match = DocblockPropertyTypeRegex.Match(originalLine);
        if (!match.Success)
            return;

        var nameGroup = match.Groups["name"];
        if (nameGroup.Success
            && trackDocblockPropertyNames
            && !TryAddPhpDocblockPropertyName(ref seenDocblockPropertyNames, nameGroup.Value))
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

    private static bool TryAddPhpDocblockPropertyName(ref HashSet<string>? seenDocblockPropertyNames, string name)
    {
        seenDocblockPropertyNames ??= new HashSet<string>(StringComparer.Ordinal);
        return seenDocblockPropertyNames.Add(name);
    }

    public static void EmitDocblockMethodReturnTypeReferences(
        string originalLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
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
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (originalLine.IndexOf("@method", StringComparison.OrdinalIgnoreCase) < 0)
            return;

        var match = DocblockMethodParameterListRegex.Match(originalLine);
        if (!match.Success)
            return;

        var paramsGroup = match.Groups["params"];
        foreach (Match parameterMatch in ReferenceExtractor.EnumerateReferenceMatches(
                     DocblockMethodParameterTypeRegex,
                     paramsGroup.Value,
                     references))
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
        ReferenceDedupeSet seen,
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
        ReferenceDedupeSet seen,
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
        ReferenceDedupeSet seen,
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
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (originalLine.IndexOf('@') < 0)
            return;

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
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        foreach (Match typeMatch in ReferenceExtractor.EnumerateReferenceMatches(
                     DocblockTypeNameRegex,
                     typeExpression,
                     references))
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

}
