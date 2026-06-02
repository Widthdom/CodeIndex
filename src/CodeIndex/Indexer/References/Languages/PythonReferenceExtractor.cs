using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static class PythonReferenceExtractor
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

        return Regex.Replace(
            expression,
            @"(?<quote>['""])(?<name>(?:[_\p{L}]\w*\.)*[_\p{L}]\w*)\k<quote>",
            "${name}",
            RegexOptions.CultureInvariant);
    }

    private static void EmitPythonTypeExpressionReferences(
        Group typeGroup,
        List<ReferenceRecord> references,
        HashSet<string> seen,
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
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        HashSet<string>? definitionNames,
        Func<string, bool> isIgnoredName)
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

    private static void EmitDecoratorArgumentReferences(
        string preparedLine,
        Match decoratorMatch,
        List<ReferenceRecord> references,
        HashSet<string> seen,
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

    public static void EmitRaiseReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        Func<string, bool> isIgnoredName)
    {
        foreach (Match match in BareRaiseTypeRegex.Matches(preparedLine))
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

    public static void EmitExceptReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        Func<string, bool> isIgnoredName)
    {
        foreach (Match match in ExceptTupleTypeRegex.Matches(preparedLine))
        {
            var typesGroup = match.Groups["types"];
            foreach (Match typeMatch in TypeNameRegex.Matches(typesGroup.Value))
            {
                var name = typeMatch.Groups["name"].Value;
                if (isIgnoredName(name))
                    continue;

                ReferenceExtractor.AddTypeReferenceSegments(
                    references,
                    seen,
                    fileId,
                    name,
                    typesGroup.Index + typeMatch.Groups["name"].Index,
                    context,
                    lineNumber,
                    container,
                    "python");
            }
        }

        foreach (Match match in ExceptTypeRegex.Matches(preparedLine))
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

    public static void EmitIsInstanceReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        Func<string, bool> isIgnoredName)
    {
        foreach (Match match in IsInstanceTupleTypeRegex.Matches(preparedLine))
        {
            var typesGroup = match.Groups["types"];
            foreach (Match typeMatch in TypeNameRegex.Matches(typesGroup.Value))
            {
                var name = typeMatch.Groups["name"].Value;
                if (isIgnoredName(name))
                    continue;

                ReferenceExtractor.AddTypeReferenceSegments(
                    references,
                    seen,
                    fileId,
                    name,
                    typesGroup.Index + typeMatch.Groups["name"].Index,
                    context,
                    lineNumber,
                    container,
                    "python");
            }
        }

        foreach (Match match in IsInstanceTypeRegex.Matches(preparedLine))
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

    public static void EmitIsSubclassReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        Func<string, bool> isIgnoredName)
    {
        foreach (Match match in IsSubclassTupleTypeRegex.Matches(preparedLine))
        {
            var typesGroup = match.Groups["types"];
            foreach (Match typeMatch in TypeNameRegex.Matches(typesGroup.Value))
            {
                var name = typeMatch.Groups["name"].Value;
                if (isIgnoredName(name))
                    continue;

                ReferenceExtractor.AddTypeReferenceSegments(
                    references,
                    seen,
                    fileId,
                    name,
                    typesGroup.Index + typeMatch.Groups["name"].Index,
                    context,
                    lineNumber,
                    container,
                    "python");
            }
        }

        foreach (Match match in IsSubclassTypeRegex.Matches(preparedLine))
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

    public static void EmitCastReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        Func<string, bool> isIgnoredName)
    {
        foreach (Match match in QualifiedCastTypeRegex.Matches(preparedLine))
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

        foreach (Match match in CastTypeRegex.Matches(preparedLine))
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

    public static void EmitAssertTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        Func<string, bool> isIgnoredName)
    {
        foreach (Match match in QualifiedAssertTypeRegex.Matches(preparedLine))
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

        foreach (Match match in AssertTypeRegex.Matches(preparedLine))
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

    public static void EmitClassBaseReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        Func<int, SymbolRecord?> resolveContainerForReference,
        Func<string, bool> isIgnoredName)
    {
        foreach (Match match in ClassMetaclassTypeRegex.Matches(preparedLine))
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

        foreach (Match match in MultipleClassBaseTypesRegex.Matches(preparedLine))
        {
            var typesGroup = match.Groups["types"];
            foreach (Match typeMatch in TypeNameRegex.Matches(typesGroup.Value))
            {
                var name = typeMatch.Groups["name"].Value;
                if (isIgnoredName(name))
                    continue;
                if (IsPythonClassHeaderKeywordArgument(typesGroup.Value, typeMatch.Groups["name"].Index))
                    continue;

                var nameIndex = typesGroup.Index + typeMatch.Groups["name"].Index;
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

        foreach (Match match in SingleClassBaseTypeRegex.Matches(preparedLine))
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
                resolveContainerForReference(match.Groups["name"].Index) ?? container,
                "python");
        }
    }

    private static bool IsPythonClassHeaderKeywordArgument(string headerArguments, int nameIndex)
    {
        for (var i = nameIndex - 1; i >= 0; i--)
        {
            var ch = headerArguments[i];
            if (char.IsWhiteSpace(ch))
                continue;
            if (ch == '=')
                return true;
            break;
        }

        for (var i = nameIndex; i < headerArguments.Length; i++)
        {
            var ch = headerArguments[i];
            if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '.')
                continue;
            if (char.IsWhiteSpace(ch))
                continue;
            return ch == '=';
        }

        return false;
    }

    public static void EmitFunctionReturnReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        Func<int, SymbolRecord?> resolveContainerForReference,
        Func<string, bool> isIgnoredName)
    {
        foreach (Match match in FunctionReturnAnnotationExpressionRegex.Matches(preparedLine))
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

        foreach (Match match in FunctionReturnTypeRegex.Matches(preparedLine))
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
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        Func<int, SymbolRecord?> resolveContainerForReference,
        Func<string, bool> isIgnoredName)
    {
        foreach (Match functionMatch in FunctionParameterListRegex.Matches(preparedLine))
        {
            var paramsGroup = functionMatch.Groups["params"];
            foreach (var (parameterSegment, parameterOffset) in EnumeratePythonTopLevelCommaSegments(paramsGroup.Value))
            {
                foreach (Match annotationMatch in AnnotationExpressionTypeRegex.Matches(parameterSegment))
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

                foreach (Match annotationMatch in DirectAnnotationTypeRegex.Matches(parameterSegment))
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
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        Func<string, bool> isIgnoredName)
    {
        foreach (Match match in VariableAnnotationExpressionRegex.Matches(preparedLine))
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

        foreach (Match match in VariableAnnotationTypeRegex.Matches(preparedLine))
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

    public static void EmitTypeAliasReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        Func<string, bool> isIgnoredName)
    {
        foreach (Match match in TypeAliasRhsExpressionRegex.Matches(preparedLine))
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
    }

    public static void EmitNewTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        Func<string, bool> isIgnoredName)
    {
        foreach (Match match in NewTypeUnderlyingTypeRegex.Matches(preparedLine))
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

    public static void EmitTypeVarBoundReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        Func<string, bool> isIgnoredName)
    {
        foreach (Match match in TypeVarBoundTypeRegex.Matches(preparedLine))
        {
            EmitPythonTypeExpressionReferences(
                match.Groups["type"],
                references,
                seen,
                fileId,
                context,
                lineNumber,
                container,
                resolveContainerForReference: null,
                isIgnoredName);
        }
    }

    public static void EmitTypeVarConstraintReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        Func<string, bool> isIgnoredName)
    {
        foreach (Match match in TypeVarConstraintTypesRegex.Matches(preparedLine))
        {
            var typesGroup = match.Groups["types"];
            EmitPythonTypeExpressionReferences(
                typesGroup,
                references,
                seen,
                fileId,
                context,
                lineNumber,
                container,
                resolveContainerForReference: null,
                isIgnoredName);
        }
    }

    public static void EmitGetTypeHintsReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        Func<string, bool> isIgnoredName)
    {
        foreach (Match match in QualifiedGetTypeHintsTargetRegex.Matches(preparedLine))
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

        foreach (Match match in GetTypeHintsTargetRegex.Matches(preparedLine))
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

    public static void EmitDataclassesFieldsReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        Func<string, bool> isIgnoredName)
    {
        foreach (Match match in DataclassesFieldsTargetRegex.Matches(preparedLine))
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

    public static void EmitDataclassFieldReferences(
        string[] preparedLines,
        string[] originalLines,
        int lineIndex,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        SymbolRecord? container,
        Func<string, bool> isIgnoredName)
    {
        var preparedLine = preparedLines[lineIndex];
        if (!DataclassFieldCallRegex.IsMatch(preparedLine))
            return;

        var depth = 0;
        var sawFieldCall = false;
        var inString = false;
        var quoteChar = '\0';

        for (var currentLineIndex = lineIndex; currentLineIndex < preparedLines.Length; currentLineIndex++)
        {
            var currentPreparedLine = preparedLines[currentLineIndex];
            var currentOriginalLine = originalLines[currentLineIndex];
            var currentContext = currentOriginalLine.Trim();
            var currentLineNumber = currentLineIndex + 1;

            EmitDataclassFieldDefaultFactoryReferences(
                currentPreparedLine,
                references,
                seen,
                fileId,
                currentContext,
                currentLineNumber,
                container,
                isIgnoredName);
            EmitDataclassFieldMetadataReferences(
                originalLines,
                currentLineIndex,
                references,
                seen,
                fileId,
                container,
                isIgnoredName);

            for (var column = 0; column < currentPreparedLine.Length; column++)
            {
                var ch = currentPreparedLine[column];
                if (inString)
                {
                    if (ch == '\\')
                    {
                        column++;
                        continue;
                    }

                    if (ch == quoteChar)
                        inString = false;
                    continue;
                }

                if (ch == '#')
                    break;
                if (ch is '\'' or '"')
                {
                    inString = true;
                    quoteChar = ch;
                    continue;
                }

                if (ch == '(')
                {
                    depth++;
                    sawFieldCall = true;
                }
                else if (ch == ')' && depth > 0)
                {
                    depth--;
                    if (sawFieldCall && depth == 0)
                        return;
                }
            }

            if (sawFieldCall && depth <= 0)
                return;
        }
    }

    private static void EmitDataclassFieldDefaultFactoryReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        Func<string, bool> isIgnoredName)
    {
        foreach (Match match in DataclassFieldDefaultFactoryRegex.Matches(preparedLine))
        {
            var name = match.Groups["name"].Value;
            if (isIgnoredName(name))
                continue;

            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                name,
                match.Groups["name"].Index,
                "call",
                context,
                lineNumber,
                container,
                "python");
        }
    }

    private static void EmitDataclassFieldMetadataReferences(
        string[] originalLines,
        int lineIndex,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        SymbolRecord? container,
        Func<string, bool> isIgnoredName)
    {
        var metadataMatch = DataclassFieldMetadataRegex.Match(originalLines[lineIndex]);
        if (!metadataMatch.Success)
            return;

        var currentLineIndex = lineIndex;
        var currentColumn = metadataMatch.Groups["values"].Index;
        var depth = 0;
        var inString = false;
        var quoteChar = '\0';
        var stringStartColumn = -1;

        while (currentLineIndex < originalLines.Length)
        {
            var currentLine = originalLines[currentLineIndex];
            if (currentColumn >= currentLine.Length)
            {
                if (depth <= 0 && !inString)
                    break;

                currentLineIndex++;
                currentColumn = 0;
                continue;
            }

            var ch = currentLine[currentColumn];
            if (inString)
            {
                if (ch == '\\' && currentColumn + 1 < currentLine.Length)
                {
                    currentColumn += 2;
                    continue;
                }

                if (ch == quoteChar)
                {
                    var afterStringColumn = currentColumn + 1;
                    while (afterStringColumn < currentLine.Length && char.IsWhiteSpace(currentLine[afterStringColumn]))
                        afterStringColumn++;

                    if (afterStringColumn < currentLine.Length && currentLine[afterStringColumn] == ':')
                    {
                        var name = currentLine[stringStartColumn..currentColumn].Trim();
                        if (name.Length > 0 && !isIgnoredName(name))
                        {
                            ReferenceExtractor.AddReference(
                                references,
                                seen,
                                fileId,
                                name,
                                stringStartColumn,
                                "annotation",
                                currentLine.Trim(),
                                currentLineIndex + 1,
                                container,
                                "python");
                        }
                    }

                    inString = false;
                    quoteChar = '\0';
                    stringStartColumn = -1;
                    currentColumn++;
                    continue;
                }

                currentColumn++;
                continue;
            }

            if (ch == '#')
                break;

            if (ch is '\'' or '"')
            {
                inString = true;
                quoteChar = ch;
                stringStartColumn = currentColumn + 1;
                currentColumn++;
                continue;
            }

            if (ch is '{' or '[' or '(')
            {
                depth++;
                currentColumn++;
                continue;
            }

            if (ch is '}' or ']' or ')')
            {
                if (depth > 0)
                    depth--;
                currentColumn++;
                if (depth <= 0)
                    break;
                continue;
            }

            currentColumn++;
        }
    }

    public static void EmitAttrsFieldsReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        Func<string, bool> isIgnoredName)
    {
        foreach (Match match in AttrsFieldsTargetRegex.Matches(preparedLine))
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

    public static void EmitPydanticTypeAdapterReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        Func<string, bool> isIgnoredName)
    {
        foreach (Match match in PydanticTypeAdapterTargetRegex.Matches(preparedLine))
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

    public static void EmitPytestRaisesReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        Func<string, bool> isIgnoredName)
    {
        foreach (Match match in PytestRaisesTypeRegex.Matches(preparedLine))
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

    public static void EmitContextlibSuppressReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        Func<string, bool> isIgnoredName)
    {
        foreach (Match match in ContextlibSuppressTypeRegex.Matches(preparedLine))
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

    public static void EmitDynamicImportReferences(
        string preparedLine,
        string originalLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        foreach (Match match in ImportlibDynamicImportRegex.Matches(preparedLine))
        {
            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                "importlib",
                match.Index,
                "call",
                context,
                lineNumber,
                container,
                "python");

            var literalMatch = ImportlibDynamicImportLiteralRegex.Match(originalLine, match.Index);
            if (!literalMatch.Success || literalMatch.Index != match.Index)
                continue;

            var moduleGroup = literalMatch.Groups["module"];
            if (moduleGroup.Success && moduleGroup.Value.Length > 0)
            {
                ReferenceExtractor.AddReference(
                    references,
                    seen,
                    fileId,
                    moduleGroup.Value,
                    moduleGroup.Index,
                    "import",
                    context,
                    lineNumber,
                    container,
                    "python");
            }
        }

        foreach (Match match in BuiltinDynamicImportRegex.Matches(preparedLine))
        {
            var literalMatch = BuiltinDynamicImportLiteralRegex.Match(originalLine, match.Index);
            if (!literalMatch.Success || literalMatch.Index != match.Index)
                continue;

            var moduleGroup = literalMatch.Groups["module"];
            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                moduleGroup.Value,
                moduleGroup.Index,
                "import",
                context,
                lineNumber,
                container,
                "python");
        }
    }
}
