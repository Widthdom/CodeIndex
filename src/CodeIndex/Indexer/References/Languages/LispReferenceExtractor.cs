using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static class LispReferenceExtractor
{
    private static readonly HashSet<string> IgnoredCommonLispHeads = new(StringComparer.OrdinalIgnoreCase)
    {
        "and", "block", "case", "ccase", "cond", "declare", "declaim", "defclass", "defconstant",
        "defgeneric", "define-compiler-macro", "define-condition", "define-modify-macro", "defmacro",
        "defmethod", "defpackage", "defparameter", "defsetf", "defstruct", "defun", "defvar",
        "do", "do*", "dolist", "dotimes", "ecase", "etypecase", "flet", "function", "handler-bind",
        "handler-case", "if", "in-package", "labels", "lambda", "let", "let*", "locally", "loop",
        "macrolet", "multiple-value-bind", "or", "progn", "quote", "return-from", "setf", "symbol-macrolet",
        "tagbody", "the", "typecase", "unless", "unwind-protect", "use-package", "when",
    };

    private static readonly HashSet<string> IgnoredRacketHeads = new(StringComparer.OrdinalIgnoreCase)
    {
        "and", "begin", "begin-for-syntax", "case", "class", "cond", "define", "define/contract",
        "define-for-syntax", "define-syntax", "define-syntax-rule", "define-syntaxes", "define-values",
        "define/public", "define/private", "define/override", "define/augment", "define-struct",
        "if", "lambda", "let", "let*", "let-values", "letrec", "letrec-values", "local", "module",
        "module*", "module+", "or", "provide", "quote", "quasiquote", "require", "set!", "struct",
        "unless", "when",
    };

    public static void EmitReferences(
        string language,
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        IReadOnlySet<string>? definitionNames)
    {
        if (TryReadFirstLispFormHead(preparedLine, out var firstHead, out _, out var afterFirstHead)
            && IsDefinitionHead(language, firstHead))
        {
            if (language == "commonlisp"
                && string.Equals(firstHead, "defmethod", StringComparison.OrdinalIgnoreCase))
            {
                EmitCommonLispDefmethodSpecializerReferences(
                    preparedLine,
                    afterFirstHead,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    resolveContainerForColumn);
            }

            return;
        }

        for (var cursor = 0; cursor < preparedLine.Length; cursor++)
        {
            if (preparedLine[cursor] != '(' || IsQuotedLispForm(preparedLine, cursor))
                continue;

            if (!SymbolExtractor.TryReadLispListHead(preparedLine, cursor, out var rawHead, out var headIndex, out var afterHead))
                continue;

            var normalizedHead = NormalizeReferenceName(rawHead);
            if (normalizedHead.Length == 0)
                continue;

            if (language == "commonlisp")
            {
                if (string.Equals(normalizedHead, "make-instance", StringComparison.OrdinalIgnoreCase))
                {
                    EmitCommonLispMakeInstanceReference(
                        preparedLine,
                        afterHead,
                        references,
                        seen,
                        fileId,
                        context,
                        lineNumber,
                        resolveContainerForColumn(headIndex));
                    continue;
                }

                if (string.Equals(normalizedHead, "function", StringComparison.OrdinalIgnoreCase))
                {
                    EmitCommonLispFunctionQuoteReference(
                        preparedLine,
                        afterHead,
                        references,
                        seen,
                        fileId,
                        context,
                        lineNumber,
                        resolveContainerForColumn(headIndex),
                        definitionNames);
                    continue;
                }
            }

            if (IsIgnoredHead(language, normalizedHead))
                continue;
            if (definitionNames != null && definitionNames.Contains(normalizedHead))
                continue;

            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                normalizedHead,
                headIndex,
                "call",
                context,
                lineNumber,
                resolveContainerForColumn(headIndex));
        }

        if (language == "commonlisp")
        {
            EmitSharpQuoteReferences(
                preparedLine,
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn,
                definitionNames);
        }
    }

    private static bool TryReadFirstLispFormHead(
        string preparedLine,
        out string head,
        out int headIndex,
        out int afterHead)
    {
        head = string.Empty;
        headIndex = -1;
        afterHead = -1;

        for (var cursor = 0; cursor < preparedLine.Length; cursor++)
        {
            if (preparedLine[cursor] != '(' || IsQuotedLispForm(preparedLine, cursor))
                continue;

            return SymbolExtractor.TryReadLispListHead(preparedLine, cursor, out head, out headIndex, out afterHead);
        }

        return false;
    }

    private static void EmitCommonLispMakeInstanceReference(
        string preparedLine,
        int afterHead,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (!SymbolExtractor.TryReadLispSymbolToken(preparedLine, afterHead, out var rawTypeName, out var typeIndex, out _))
            return;

        var typeName = NormalizeReferenceName(rawTypeName);
        if (typeName.Length == 0)
            return;

        ReferenceExtractor.AddReference(
            references,
            seen,
            fileId,
            typeName,
            typeIndex,
            "instantiate",
            context,
            lineNumber,
            container);
    }

    private static void EmitCommonLispFunctionQuoteReference(
        string preparedLine,
        int afterHead,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        IReadOnlySet<string>? definitionNames)
    {
        if (!SymbolExtractor.TryReadLispSymbolToken(preparedLine, afterHead, out var rawName, out var nameIndex, out _))
            return;

        var name = NormalizeReferenceName(rawName);
        if (name.Length == 0 || (definitionNames != null && definitionNames.Contains(name)))
            return;

        ReferenceExtractor.AddReference(references, seen, fileId, name, nameIndex, "call", context, lineNumber, container);
    }

    private static void EmitSharpQuoteReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        IReadOnlySet<string>? definitionNames)
    {
        for (var cursor = 0; cursor + 2 < preparedLine.Length; cursor++)
        {
            if (preparedLine[cursor] != '#' || preparedLine[cursor + 1] != '\'')
                continue;

            if (!SymbolExtractor.TryReadLispSymbolToken(preparedLine, cursor + 2, out var rawName, out var nameIndex, out _))
                continue;

            var name = NormalizeReferenceName(rawName);
            if (name.Length == 0 || (definitionNames != null && definitionNames.Contains(name)))
                continue;

            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                name,
                nameIndex,
                "call",
                context,
                lineNumber,
                resolveContainerForColumn(nameIndex));
        }
    }

    private static void EmitCommonLispDefmethodSpecializerReferences(
        string preparedLine,
        int afterHead,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        for (var cursor = afterHead; cursor < preparedLine.Length; cursor++)
        {
            if (preparedLine[cursor] != '(' || IsQuotedLispForm(preparedLine, cursor))
                continue;

            if (!SymbolExtractor.TryReadLispListHead(preparedLine, cursor, out _, out _, out var afterParameterName))
                continue;

            if (!SymbolExtractor.TryReadLispSymbolToken(preparedLine, afterParameterName, out var rawTypeName, out var typeIndex, out _))
                continue;

            var typeName = NormalizeReferenceName(rawTypeName);
            if (typeName.Length == 0 || IsCommonLispLambdaListKeyword(typeName))
                continue;

            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                typeName,
                typeIndex,
                "type_reference",
                context,
                lineNumber,
                resolveContainerForColumn(typeIndex));
        }
    }

    private static bool IsDefinitionHead(string language, string head)
    {
        if (language == "commonlisp")
        {
            return head.Equals("defpackage", StringComparison.OrdinalIgnoreCase)
                || head.Equals("in-package", StringComparison.OrdinalIgnoreCase)
                || head.Equals("use-package", StringComparison.OrdinalIgnoreCase)
                || head.Equals("import", StringComparison.OrdinalIgnoreCase)
                || head.Equals("shadowing-import", StringComparison.OrdinalIgnoreCase)
                || head.Equals("defclass", StringComparison.OrdinalIgnoreCase)
                || head.Equals("define-condition", StringComparison.OrdinalIgnoreCase)
                || head.Equals("defstruct", StringComparison.OrdinalIgnoreCase)
                || head.Equals("defparameter", StringComparison.OrdinalIgnoreCase)
                || head.Equals("defvar", StringComparison.OrdinalIgnoreCase)
                || head.Equals("defconstant", StringComparison.OrdinalIgnoreCase)
                || head.Equals("defun", StringComparison.OrdinalIgnoreCase)
                || head.Equals("defmacro", StringComparison.OrdinalIgnoreCase)
                || head.Equals("defgeneric", StringComparison.OrdinalIgnoreCase)
                || head.Equals("defmethod", StringComparison.OrdinalIgnoreCase)
                || head.Equals("define-compiler-macro", StringComparison.OrdinalIgnoreCase)
                || head.Equals("define-modify-macro", StringComparison.OrdinalIgnoreCase)
                || head.Equals("defsetf", StringComparison.OrdinalIgnoreCase);
        }

        return head.Equals("module", StringComparison.OrdinalIgnoreCase)
            || head.Equals("module*", StringComparison.OrdinalIgnoreCase)
            || head.Equals("module+", StringComparison.OrdinalIgnoreCase)
            || head.Equals("require", StringComparison.OrdinalIgnoreCase)
            || head.Equals("provide", StringComparison.OrdinalIgnoreCase)
            || head.Equals("struct", StringComparison.OrdinalIgnoreCase)
            || head.Equals("define-struct", StringComparison.OrdinalIgnoreCase)
            || head.Equals("class", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("define", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIgnoredHead(string language, string normalizedHead)
        => language == "commonlisp"
            ? IgnoredCommonLispHeads.Contains(normalizedHead)
            : IgnoredRacketHeads.Contains(normalizedHead);

    private static bool IsCommonLispLambdaListKeyword(string name)
        => name.StartsWith("&", StringComparison.Ordinal);

    private static string NormalizeReferenceName(string rawName)
    {
        var name = rawName.Trim();
        if (name.Length == 0 || name.StartsWith(":", StringComparison.Ordinal))
            return string.Empty;

        var packageSeparator = name.LastIndexOf("::", StringComparison.Ordinal);
        if (packageSeparator >= 0 && packageSeparator + 2 < name.Length)
            return name[(packageSeparator + 2)..];

        packageSeparator = name.LastIndexOf(':');
        if (packageSeparator > 0 && packageSeparator + 1 < name.Length)
            return name[(packageSeparator + 1)..];

        return name;
    }

    private static bool IsQuotedLispForm(string line, int openParenIndex)
    {
        var cursor = openParenIndex - 1;
        while (cursor >= 0 && char.IsWhiteSpace(line[cursor]))
            cursor--;

        return cursor >= 0 && line[cursor] is '\'' or '`' or ',';
    }
}
