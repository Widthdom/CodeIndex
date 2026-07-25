using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class PythonReferenceExtractor
{
    // Bare Python decorators like `@staticmethod` or `@pytest.fixture` are reference sites even
    // without trailing parentheses. Keep them distinct from `call` rows so the graph can tell
    // decoration apart from invocation.
    // `@staticmethod` や `@pytest.fixture` のような Python の bare decorator を記録する。
    private static readonly Regex DecoratorRegex = new(
        @"^\s*@(?<name>[_\p{L}]\w*(?:\.[_\p{L}]\w*)*)\s*(?:#.*)?$",
        RegexOptions.Compiled);
    private static readonly Regex DecoratorCallRegex = new(
        @"^\s*@(?<name>[_\p{L}]\w*(?:\.[_\p{L}]\w*)*)\s*\(",
        RegexOptions.Compiled);
    private static readonly Regex PythonIdentifierRegex = new(
        @"(?<![\w.])(?<name>[_\p{L}]\w*(?:\.[_\p{L}]\w*)*)",
        RegexOptions.Compiled);
    private static readonly Regex BareRaiseTypeRegex = new(
        @"^\s*raise\s+(?<name>(?:[_\p{L}]\w*\.)*[_\p{Lu}]\w*)(?:\s+from\s+[_\p{L}]\w*)?\s*(?:#.*)?$",
        RegexOptions.Compiled);
    private static readonly Regex ExceptTypeRegex = new(
        @"^\s*except\s+(?<name>(?:[_\p{L}]\w*\.)*[_\p{Lu}]\w*)\s*(?:as\s+\w+)?\s*:",
        RegexOptions.Compiled);
    private static readonly Regex ExceptTupleTypeRegex = new(
        @"^\s*except\s*\((?<types>[^)]*)\)\s*(?:as\s+\w+)?\s*:",
        RegexOptions.Compiled);
    private static readonly Regex TypeNameRegex = new(
        @"(?<name>(?:[_\p{L}]\w*\.)*(?:[_\p{Lu}]\w*|int|str|bytes|bool|float|complex|dict|list|tuple|set|frozenset|bytearray|None|Any))",
        RegexOptions.Compiled);
    private static readonly Regex IsInstanceTypeRegex = new(
        @"\bisinstance\s*\(\s*[^,\n]+,\s*(?<name>(?:[_\p{L}]\w*\.)*[_\p{Lu}]\w*)\s*\)",
        RegexOptions.Compiled);
    private static readonly Regex IsInstanceTupleTypeRegex = new(
        @"\bisinstance\s*\(\s*[^,\n]+,\s*\((?<types>[^)]*)\)\s*\)",
        RegexOptions.Compiled);
    private static readonly Regex IsSubclassTypeRegex = new(
        @"\bissubclass\s*\(\s*[^,\n]+,\s*(?<name>(?:[_\p{L}]\w*\.)*[_\p{Lu}]\w*)\s*\)",
        RegexOptions.Compiled);
    private static readonly Regex IsSubclassTupleTypeRegex = new(
        @"\bissubclass\s*\(\s*[^,\n]+,\s*\((?<types>[^)]*)\)\s*\)",
        RegexOptions.Compiled);
    private static readonly Regex CastTypeRegex = new(
        @"(?<!\.)\bcast\s*\(\s*(?<name>(?:[_\p{L}]\w*\.)*[_\p{Lu}]\w*)\s*,",
        RegexOptions.Compiled);
    private static readonly Regex QualifiedCastTypeRegex = new(
        @"\b(?:typing|typing_extensions)\.cast\s*\(\s*(?<name>(?:[_\p{L}]\w*\.)*[_\p{Lu}]\w*)\s*,",
        RegexOptions.Compiled);
    private static readonly Regex AssertTypeRegex = new(
        @"(?<!\.)\bassert_type\s*\(\s*[^,\n]+,\s*(?<name>(?:[_\p{L}]\w*\.)*[_\p{Lu}]\w*)\s*\)",
        RegexOptions.Compiled);
    private static readonly Regex QualifiedAssertTypeRegex = new(
        @"\b(?:typing|typing_extensions)\.assert_type\s*\(\s*[^,\n]+,\s*(?<name>(?:[_\p{L}]\w*\.)*[_\p{Lu}]\w*)\s*\)",
        RegexOptions.Compiled);
    private static readonly Regex SingleClassBaseTypeRegex = new(
        @"^\s*class\s+\w+\s*\(\s*(?<name>(?:[_\p{L}]\w*\.)*[_\p{Lu}]\w*)\s*\)\s*:",
        RegexOptions.Compiled);
    private static readonly Regex MultipleClassBaseTypesRegex = new(
        @"^\s*class\s+\w+\s*\((?<types>[^)]*,[^)]*)\)\s*:",
        RegexOptions.Compiled);
    private static readonly Regex ClassMetaclassTypeRegex = new(
        @"^\s*class\s+\w+\s*\([^)]*\bmetaclass\s*=\s*(?<name>(?:[_\p{L}]\w*\.)*[_\p{Lu}]\w*)",
        RegexOptions.Compiled);
    private static readonly Regex FunctionReturnTypeRegex = new(
        @"^\s*(?:async\s+)?def\s+\w+\s*\([^)]*\)\s*->\s*(?<name>(?:[_\p{L}]\w*\.)*[_\p{Lu}]\w*)\s*:",
        RegexOptions.Compiled);
    private static readonly Regex FunctionReturnAnnotationExpressionRegex = new(
        @"^\s*(?:async\s+)?def\s+\w+\s*\([^)]*\)\s*->\s*(?<type>[^:]+)\s*:",
        RegexOptions.Compiled);
    private static readonly Regex FunctionParameterListRegex = new(
        @"^\s*(?:async\s+)?def\s+\w+\s*\((?<params>[^)]*)\)",
        RegexOptions.Compiled);
    private static readonly Regex DirectAnnotationTypeRegex = new(
        @":\s*(?<name>(?:[_\p{L}]\w*\.)*[_\p{Lu}]\w*)(?=\s*(?:=|,|$))",
        RegexOptions.Compiled);
    private static readonly Regex AnnotationExpressionTypeRegex = new(
        @":\s*(?<type>[^=]+)(?=\s*(?:=|$))",
        RegexOptions.Compiled);
    private static readonly Regex VariableAnnotationTypeRegex = new(
        @"^\s*(?:self\.)?\w+\s*:\s*(?<name>(?:[_\p{L}]\w*\.)*[_\p{Lu}]\w*)(?=\s*(?:=|#|$))",
        RegexOptions.Compiled);
    private static readonly Regex VariableAnnotationExpressionRegex = new(
        @"^\s*(?:self\.)?\w+\s*:\s*(?<type>[^=#]+)(?=\s*(?:=|#|$))",
        RegexOptions.Compiled);
    private static readonly Regex TypeAliasRhsExpressionRegex = new(
        @"^\s*(?:type\s+\w+(?:\[[^\]]*\])?\s*=|\w+\s*:\s*(?:(?:typing|typing_extensions)\.)?TypeAlias\s*=)\s*(?<type>.+)$",
        RegexOptions.Compiled);
    private static readonly Regex NewTypeUnderlyingTypeRegex = new(
        @"\b(?:(?:typing|typing_extensions)\.)?NewType\s*\(\s*[^,\n]+,\s*(?<name>(?:[_\p{L}]\w*\.)*[_\p{Lu}]\w*)",
        RegexOptions.Compiled);
    private static readonly Regex TypeVarBoundTypeRegex = new(
        @"\b(?:(?:typing|typing_extensions)\.)?(?:TypeVar|ParamSpec)\s*\([^)]*\bbound\s*=\s*(?<type>[^)]*)\)",
        RegexOptions.Compiled);
    private static readonly Regex TypeVarConstraintTypesRegex = new(
        @"\b(?:(?:typing|typing_extensions)\.)?(?:TypeVar|ParamSpec|TypeVarTuple)\s*\(\s*[^,\n]+,\s*(?<types>[^)]*)\)",
        RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex GetTypeHintsTargetRegex = new(
        @"(?<!\.)\bget_type_hints\s*\(\s*(?<name>(?:[_\p{L}]\w*\.)*[_\p{Lu}]\w*)",
        RegexOptions.Compiled);
    private static readonly Regex QualifiedGetTypeHintsTargetRegex = new(
        @"\b(?:typing|typing_extensions)\.get_type_hints\s*\(\s*(?<name>(?:[_\p{L}]\w*\.)*[_\p{Lu}]\w*)",
        RegexOptions.Compiled);
    private static readonly Regex DataclassesFieldsTargetRegex = new(
        @"(?<!\.)\bfields\s*\(\s*(?<name>(?:[_\p{L}]\w*\.)*[_\p{Lu}]\w*)|\bdataclasses\.fields\s*\(\s*(?<name>(?:[_\p{L}]\w*\.)*[_\p{Lu}]\w*)",
        RegexOptions.Compiled);
    private static readonly Regex DataclassFieldCallRegex = new(
        @"^\s*[_\p{L}]\w*\s*(?::\s*[^=]+)?=\s*(?:(?:dataclasses\.)?field)\s*\(",
        RegexOptions.Compiled);
    private static readonly Regex DataclassFieldDefaultFactoryRegex = new(
        @"\bdefault_factory\s*=\s*(?<name>(?:[_\p{L}]\w*\.)*[_\p{L}]\w*)",
        RegexOptions.Compiled);
    private static readonly Regex DataclassFieldMetadataRegex = new(
        @"\bmetadata\s*=\s*(?<values>\{)",
        RegexOptions.Compiled);
    private static readonly Regex AttrsFieldsTargetRegex = new(
        @"\b(?:attr|attrs)\.fields\s*\(\s*(?<name>(?:[_\p{L}]\w*\.)*[_\p{Lu}]\w*)",
        RegexOptions.Compiled);
    private static readonly Regex PydanticTypeAdapterTargetRegex = new(
        @"\bpydantic\.TypeAdapter\s*\(\s*(?<name>(?:[_\p{L}]\w*\.)*[_\p{Lu}]\w*)",
        RegexOptions.Compiled);
    private static readonly Regex PytestRaisesTypeRegex = new(
        @"\bpytest\.raises\s*\(\s*(?<name>(?:[_\p{L}]\w*\.)*[_\p{Lu}]\w*)",
        RegexOptions.Compiled);
    private static readonly Regex ContextlibSuppressTypeRegex = new(
        @"\bcontextlib\.suppress\s*\(\s*(?<name>(?:[_\p{L}]\w*\.)*[_\p{Lu}]\w*)",
        RegexOptions.Compiled);
    private static readonly Regex ImportlibDynamicImportRegex = new(
        @"\bimportlib(?:\.util)?\.(?:import_module|find_spec)\s*\(",
        RegexOptions.Compiled);
    private static readonly Regex ImportlibDynamicImportLiteralRegex = new(
        @"\bimportlib(?:\.util)?\.(?:import_module|find_spec)\s*\(\s*(?<quote>['""])(?<module>[^'""]+)\k<quote>",
        RegexOptions.Compiled);
    private static readonly Regex BuiltinDynamicImportRegex = new(
        @"(?<!\.)\b__import__\s*\(",
        RegexOptions.Compiled);
    private static readonly Regex BuiltinDynamicImportLiteralRegex = new(
        @"(?<!\.)\b__import__\s*\(\s*(?<quote>['""])(?<module>[^'""]+)\k<quote>",
        RegexOptions.Compiled);

    private static string NormalizePythonAnnotationExpression(string expression)
    {
        expression = expression.Trim();
        if (expression.Length >= 2
            && (expression[0] == '\'' || expression[0] == '"')
            && expression[^1] == expression[0])
        {
            return expression[1..^1];
        }

        if (expression.AsSpan().IndexOfAny('\'', '"') < 0)
            return expression;

        return Regex.Replace(
            expression,
            @"(?<quote>['""])(?<name>(?:[_\p{L}]\w*\.)*[_\p{L}]\w*)\k<quote>",
            "${name}",
            RegexOptions.CultureInvariant);
    }

    private static void EmitPythonTypeExpressionReferences(
        Group typeGroup,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        Func<int, SymbolRecord?>? resolveContainerForReference,
        Func<string, bool> isIgnoredName,
        int baseIndex = 0)
    {
        var normalized = NormalizePythonAnnotationExpression(typeGroup.Value);
        var offsetDelta = typeGroup.Value.Length - normalized.Length;
        foreach (Match typeMatch in TypeNameRegex.Matches(normalized))
        {
            var name = typeMatch.Groups["name"].Value;
            if (isIgnoredName(name))
                continue;

            var nameIndex = baseIndex + typeGroup.Index + typeMatch.Groups["name"].Index + Math.Max(0, offsetDelta);
            ReferenceExtractor.AddTypeReferenceSegments(
                references,
                seen,
                fileId,
                name,
                nameIndex,
                context,
                lineNumber,
                resolveContainerForReference?.Invoke(nameIndex) ?? container,
                "python");
        }
    }

    private static IEnumerable<(string Text, int Offset)> EnumeratePythonTopLevelCommaSegments(string value)
    {
        var start = 0;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var inString = false;
        var quote = '\0';

        for (var index = 0; index < value.Length; index++)
        {
            var ch = value[index];
            if (inString)
            {
                if (ch == '\\')
                {
                    index++;
                    continue;
                }

                if (ch == quote)
                    inString = false;
                continue;
            }

            if (ch is '\'' or '"')
            {
                inString = true;
                quote = ch;
                continue;
            }

            if (ch == '(')
                parenDepth++;
            else if (ch == ')' && parenDepth > 0)
                parenDepth--;
            else if (ch == '[')
                bracketDepth++;
            else if (ch == ']' && bracketDepth > 0)
                bracketDepth--;
            else if (ch == '{')
                braceDepth++;
            else if (ch == '}' && braceDepth > 0)
                braceDepth--;
            else if (ch == ',' && parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
            {
                yield return (value[start..index], start);
                start = index + 1;
            }
        }

        if (start <= value.Length)
            yield return (value[start..], start);
    }

    public static void EmitDecoratorReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        HashSet<string>? definitionNames,
        Func<string, bool> isIgnoredName)
    {
        if (preparedLine.IndexOf('@') < 0)
            return;

        var mayBeDecoratorCall = MayBePythonDecoratorCall(preparedLine);
        if (mayBeDecoratorCall)
        {
            foreach (Match match in DecoratorCallRegex.Matches(preparedLine))
            {
                var name = match.Groups["name"].Value;
                if (isIgnoredName(name))
                    continue;
                if (definitionNames != null && definitionNames.Contains(name))
                    continue;

                ReferenceExtractor.AddReference(references, seen, fileId, match, "decorator", context, lineNumber, container);
                EmitDecoratorArgumentReferences(
                    preparedLine,
                    match,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container,
                    isIgnoredName);
            }
        }

        if (!mayBeDecoratorCall)
        {
            foreach (Match match in DecoratorRegex.Matches(preparedLine))
            {
                var name = match.Groups["name"].Value;
                if (isIgnoredName(name))
                    continue;
                if (definitionNames != null && definitionNames.Contains(name))
                    continue;

                ReferenceExtractor.AddReference(references, seen, fileId, match, "decorator", context, lineNumber, container);
            }
        }
    }

    private static void EmitDecoratorArgumentReferences(
        string preparedLine,
        Match decoratorMatch,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        Func<string, bool> isIgnoredName)
    {
        var decoratorName = decoratorMatch.Groups["name"].Value;
        var argumentStart = preparedLine.IndexOf('(', decoratorMatch.Index + decoratorMatch.Length - 1);
        if (argumentStart < 0)
            return;

        foreach (Match identifierMatch in PythonIdentifierRegex.Matches(preparedLine, argumentStart + 1))
        {
            var nameGroup = identifierMatch.Groups["name"];
            var name = nameGroup.Value;
            if (name == decoratorName || isIgnoredName(name) || IsPythonLiteralName(name))
                continue;
            if (IsKeywordArgumentName(preparedLine, nameGroup.Index + nameGroup.Length))
                continue;
            var isCallTarget = IsCallTarget(preparedLine, nameGroup.Index + nameGroup.Length);
            if (IsKeywordArgumentValue(preparedLine, nameGroup.Index) && !isCallTarget)
                continue;

            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                name,
                nameGroup.Index,
                isCallTarget ? "call" : "reference",
                context,
                lineNumber,
                container,
                "python");
        }
    }

    private static bool MayBePythonDecoratorCall(string preparedLine)
    {
        var parenIndex = preparedLine.IndexOf('(');
        if (parenIndex < 0)
            return false;

        var commentIndex = preparedLine.IndexOf('#');
        return commentIndex < 0 || parenIndex < commentIndex;
    }

    private static bool IsKeywordArgumentName(string value, int afterNameIndex)
    {
        while (afterNameIndex < value.Length && char.IsWhiteSpace(value[afterNameIndex]))
            afterNameIndex++;

        return afterNameIndex < value.Length && value[afterNameIndex] == '=';
    }

    private static bool IsKeywordArgumentValue(string value, int nameIndex)
    {
        var beforeNameIndex = nameIndex - 1;
        while (beforeNameIndex >= 0 && char.IsWhiteSpace(value[beforeNameIndex]))
            beforeNameIndex--;

        return beforeNameIndex >= 0 && value[beforeNameIndex] == '=';
    }

    private static bool IsCallTarget(string value, int afterNameIndex)
    {
        while (afterNameIndex < value.Length && char.IsWhiteSpace(value[afterNameIndex]))
            afterNameIndex++;

        return afterNameIndex < value.Length && value[afterNameIndex] == '(';
    }

    private static bool IsPythonLiteralName(string name)
    {
        return name is "True" or "False" or "None" or "Ellipsis";
    }

}
