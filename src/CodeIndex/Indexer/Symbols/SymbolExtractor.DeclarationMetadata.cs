using System.Text;
using System.Text.RegularExpressions;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static void ExtractCppFriendDeclarationSymbols(
        long fileId,
        string[] lines,
        List<SymbolRecord> symbols,
        SymbolExtractionState extractionState)
    {
        if (!LinesContain(lines, "friend", StringComparison.Ordinal))
            return;

        var declared = BuildSymbolKindNameIdentities(symbols);
        var inBlockComment = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!inBlockComment
                && line.IndexOf("friend", StringComparison.Ordinal) < 0
                && line.IndexOf('/') < 0)
            {
                continue;
            }

            var matchLine = MaskCppFriendDeclarationLine(line, ref inBlockComment);
            if (matchLine.IndexOf("friend", StringComparison.Ordinal) < 0)
                continue;

            var lineNumber = i + 1;

            foreach (Match match in BoundedRegex.EnumerateMatches(CppFriendTypeDeclarationRegex, matchLine))
            {
                var kind = NormalizeCppFriendTypeKind(match.Groups["kind"].Value);
                var group = match.Groups["name"];
                var name = LastCppDeclarationSegment(group.Value);
                AddCppFriendDeclarationSymbol(fileId, symbols, extractionState, declared, kind, name, lineNumber, group.Index, line);
            }

            foreach (Match match in BoundedRegex.EnumerateMatches(CppFriendFunctionDeclarationRegex, matchLine))
            {
                var group = match.Groups["name"];
                var name = LastCppDeclarationSegment(group.Value);
                AddCppFriendDeclarationSymbol(fileId, symbols, extractionState, declared, "function", name, lineNumber, group.Index, line);
            }
        }
    }

    private static string MaskCppFriendDeclarationLine(string line, ref bool inBlockComment)
    {
        char[]? chars = null;

        void MaskAt(int index) =>
            (chars ??= line.ToCharArray())[index] = ' ';

        void MaskToEnd(int start)
        {
            var masked = chars ??= line.ToCharArray();
            for (var index = start; index < line.Length; index++)
                masked[index] = ' ';
        }

        for (var cursor = 0; cursor < line.Length; cursor++)
        {
            if (inBlockComment)
            {
                MaskAt(cursor);
                if (cursor + 1 < line.Length && line[cursor] == '*' && line[cursor + 1] == '/')
                {
                    MaskAt(++cursor);
                    inBlockComment = false;
                }

                continue;
            }

            if (cursor + 1 < line.Length && line[cursor] == '/' && line[cursor + 1] == '/')
            {
                MaskToEnd(cursor);
                break;
            }

            if (cursor + 1 < line.Length && line[cursor] == '/' && line[cursor + 1] == '*')
            {
                MaskAt(cursor++);
                MaskAt(cursor);
                inBlockComment = true;
                continue;
            }

            if (line[cursor] is '"' or '\'')
            {
                var quote = line[cursor];
                MaskAt(cursor++);
                while (cursor < line.Length)
                {
                    if (line[cursor] == '\\' && cursor + 1 < line.Length)
                    {
                        MaskAt(cursor++);
                        MaskAt(cursor);
                        cursor++;
                        continue;
                    }

                    var closes = line[cursor] == quote;
                    MaskAt(cursor++);
                    if (closes)
                        break;
                }

                cursor--;
            }
        }

        return chars is null ? line : new string(chars);
    }

    private const int CSharpTestAttributeMaxLines = 128;
    private const int CSharpTestAttributeMaxCharacters = 32 * 1024;
    private const int CSharpTestAttributeMaxItems = 64;
    private const int CSharpTestAttributeMaxNameCharacters = 512;

    private static bool IsCSharpTestMethod(
        bool[] attributedDeclarationLines,
        int declarationLineIndex,
        bool isMethodDeclaration)
    {
        return isMethodDeclaration
            && declarationLineIndex >= 0
            && declarationLineIndex < attributedDeclarationLines.Length
            && attributedDeclarationLines[declarationLineIndex];
    }

    private static void ConsumeCSharpTestAttributePrefix(
        bool[] attributedDeclarationLines,
        int declarationLineIndex)
    {
        if (declarationLineIndex >= 0
            && declarationLineIndex < attributedDeclarationLines.Length
            && attributedDeclarationLines[declarationLineIndex])
        {
            // An attribute prefix belongs to the first emitted declaration after it.
            // Delaying consumption until emission avoids losing ownership to an
            // overlapping pattern candidate that was accepted but deduplicated.
            attributedDeclarationLines[declarationLineIndex] = false;
        }
    }

    private sealed class CSharpTestAttributePrefixScanner
    {
        private readonly StringBuilder _attributeName = new(64);
        private bool _inAttributeSection;
        private bool _pendingAttributePrefix;
        private bool _blockHasTestAttribute;
        private bool _sectionHasTestAttribute;
        private bool _sectionTargetIgnored;
        private bool _itemNameFinalized;
        private bool _budgetExceeded;
        private bool _canStartAttributePrefix = true;
        private int _bracketDepth;
        private int _parenthesisDepth;
        private int _genericArgumentDepth;
        private bool _inExpressionInitializer;
        private int _expressionInitializerBraceDepth;
        private int _codeParenthesisDepth;
        private int _codeBracketDepth;
        private int _blockLineCount;
        private int _blockCharacterCount;
        private int _blockItemCount;

        public bool ScanLine(string sanitizedLine)
        {
            var firstNonWhitespace = 0;
            while (firstNonWhitespace < sanitizedLine.Length
                   && char.IsWhiteSpace(sanitizedLine[firstNonWhitespace]))
            {
                firstNonWhitespace++;
            }

            if (!_inAttributeSection && !_pendingAttributePrefix)
            {
                if (firstNonWhitespace >= sanitizedLine.Length)
                {
                    return false;
                }

                if (sanitizedLine[firstNonWhitespace] != '['
                    || !_canStartAttributePrefix)
                {
                    UpdateCSharpAttributePrefixContext(sanitizedLine);
                    return false;
                }

                ResetBlock();
            }

            CountLine();
            var cursor = 0;
            while (cursor < sanitizedLine.Length)
            {
                var ch = sanitizedLine[cursor];
                if (!_inAttributeSection)
                {
                    if (char.IsWhiteSpace(ch))
                    {
                        CountCharacter();
                        cursor++;
                        continue;
                    }

                    if (ch == '[')
                    {
                        StartSection();
                        CountCharacter();
                        cursor++;
                        continue;
                    }

                    var isAttributedDeclaration = !_budgetExceeded
                        && _pendingAttributePrefix
                        && _blockHasTestAttribute;
                    UpdateCSharpAttributePrefixContext(sanitizedLine.AsSpan(cursor));
                    ResetBlock();
                    return isAttributedDeclaration;
                }

                CountCharacter();
                cursor++;

                if (ch == '[')
                {
                    _bracketDepth++;
                    continue;
                }

                if (ch == ']')
                {
                    _bracketDepth--;
                    if (_bracketDepth == 0)
                    {
                        CompleteItem();
                        if (!_sectionTargetIgnored)
                            _blockHasTestAttribute |= _sectionHasTestAttribute;

                        _inAttributeSection = false;
                        _pendingAttributePrefix = true;
                        _parenthesisDepth = 0;
                        _genericArgumentDepth = 0;
                    }

                    continue;
                }

                if (_bracketDepth != 1)
                    continue;

                if (ch == '(')
                {
                    if (_parenthesisDepth == 0)
                        FinalizeItemName();
                    _parenthesisDepth++;
                    continue;
                }

                if (ch == ')' && _parenthesisDepth > 0)
                {
                    _parenthesisDepth--;
                    continue;
                }

                if (_parenthesisDepth != 0)
                    continue;

                if (ch == '<')
                {
                    if (_genericArgumentDepth == 0)
                        FinalizeItemName();
                    _genericArgumentDepth++;
                    continue;
                }

                if (ch == '>' && _genericArgumentDepth > 0)
                {
                    _genericArgumentDepth--;
                    continue;
                }

                if (_genericArgumentDepth != 0)
                    continue;

                if (ch == ',')
                {
                    CompleteItem();
                    continue;
                }

                if (ch == ':' && IsTargetSpecifier(_attributeName, out var ignoreTarget))
                {
                    _sectionTargetIgnored = ignoreTarget;
                    _attributeName.Clear();
                    _itemNameFinalized = false;
                    continue;
                }

                if (!char.IsWhiteSpace(ch) && !_itemNameFinalized)
                {
                    if (_attributeName.Length >= CSharpTestAttributeMaxNameCharacters)
                    {
                        _budgetExceeded = true;
                    }
                    else
                    {
                        _attributeName.Append(ch);
                    }
                }
            }

            return false;
        }

        private void UpdateCSharpAttributePrefixContext(ReadOnlySpan<char> sanitizedLine)
        {
            var trimmed = sanitizedLine.Trim();
            if (trimmed.Length == 0)
                return;

            for (var cursor = 0; cursor < sanitizedLine.Length; cursor++)
            {
                var ch = sanitizedLine[cursor];
                if (ch == '(')
                {
                    _codeParenthesisDepth++;
                    continue;
                }

                if (ch == ')' && _codeParenthesisDepth > 0)
                {
                    _codeParenthesisDepth--;
                    continue;
                }

                if (ch == '[')
                {
                    _codeBracketDepth++;
                    continue;
                }

                if (ch == ']' && _codeBracketDepth > 0)
                {
                    _codeBracketDepth--;
                    continue;
                }

                if (!_inExpressionInitializer
                    && ch == '='
                    && _codeParenthesisDepth == 0
                    && _codeBracketDepth == 0
                    && IsCSharpAssignmentOperator(sanitizedLine, cursor))
                {
                    _inExpressionInitializer = true;
                    continue;
                }

                if (!_inExpressionInitializer)
                    continue;

                if (ch == '{')
                {
                    _expressionInitializerBraceDepth++;
                }
                else if (ch == '}' && _expressionInitializerBraceDepth > 0)
                {
                    _expressionInitializerBraceDepth--;
                }
                else if (ch == ';' && _expressionInitializerBraceDepth == 0)
                {
                    _inExpressionInitializer = false;
                }
            }

            // A declaration attribute may begin at the file start or after a completed
            // declaration/statement/body. A bracket-led line following an expression
            // continuation (`=>`, `=`, `return`, an argument list, and so on), including
            // an initializer brace, is instead a collection expression and must not create
            // attribute ownership.
            // declaration attribute は file 先頭または完了した宣言・statement・body の後に
            // 開始できる。initializer brace を含む expression continuation 後の行頭 bracket は
            // collection expression なので、attribute 所有権を作らない。
            _canStartAttributePrefix = !_inExpressionInitializer
                && (trimmed[0] == '#'
                    || trimmed[^1] is ';' or '{' or '}');
        }

        private static bool IsCSharpAssignmentOperator(ReadOnlySpan<char> line, int index)
        {
            var previous = index > 0 ? line[index - 1] : '\0';
            var next = index + 1 < line.Length ? line[index + 1] : '\0';
            return next != '=' && previous is not ('=' or '!' or '<' or '>');
        }

        private void StartSection()
        {
            _inAttributeSection = true;
            _sectionHasTestAttribute = false;
            _sectionTargetIgnored = false;
            _itemNameFinalized = false;
            _bracketDepth = 1;
            _parenthesisDepth = 0;
            _genericArgumentDepth = 0;
            _attributeName.Clear();
        }

        private void CompleteItem()
        {
            FinalizeItemName();
            _blockItemCount++;
            if (_blockItemCount > CSharpTestAttributeMaxItems)
                _budgetExceeded = true;

            _attributeName.Clear();
            _itemNameFinalized = false;
        }

        private void FinalizeItemName()
        {
            if (_itemNameFinalized)
                return;

            if (!_budgetExceeded
                && _attributeName.Length > 0
                && CSharpTestMethodAttributeRegex.IsMatch(_attributeName.ToString()))
            {
                _sectionHasTestAttribute = true;
            }

            _itemNameFinalized = true;
        }

        private static bool IsTargetSpecifier(StringBuilder value, out bool ignoreTarget)
        {
            var target = value.ToString();
            var isTarget = target is
                "assembly" or "event" or "field" or "method" or "module" or
                "param" or "property" or "return" or "type" or "typevar";
            ignoreTarget = target is "assembly" or "module" or "return";
            return isTarget;
        }

        private void CountLine()
        {
            _blockLineCount++;
            if (_blockLineCount > CSharpTestAttributeMaxLines)
                _budgetExceeded = true;
        }

        private void CountCharacter()
        {
            _blockCharacterCount++;
            if (_blockCharacterCount > CSharpTestAttributeMaxCharacters)
                _budgetExceeded = true;
        }

        private void ResetBlock()
        {
            _inAttributeSection = false;
            _pendingAttributePrefix = false;
            _blockHasTestAttribute = false;
            _sectionHasTestAttribute = false;
            _sectionTargetIgnored = false;
            _itemNameFinalized = false;
            _budgetExceeded = false;
            _bracketDepth = 0;
            _parenthesisDepth = 0;
            _genericArgumentDepth = 0;
            _blockLineCount = 0;
            _blockCharacterCount = 0;
            _blockItemCount = 0;
            _attributeName.Clear();
        }
    }

    private static void AddCppFriendDeclarationSymbol(
        long fileId,
        List<SymbolRecord> symbols,
        SymbolExtractionState extractionState,
        HashSet<SymbolKindNameIdentity> declared,
        string kind,
        string name,
        int lineNumber,
        int startColumn,
        string line)
    {
        if (name.Length == 0 || !declared.Add(new SymbolKindNameIdentity(kind, name)))
            return;

        AddSymbolRecord(
            symbols,
            extractionState,
            cssSeenSymbols: null,
            lineNumber,
            new SymbolRecord
            {
                FileId = fileId,
                Kind = kind,
                Name = name,
                Line = lineNumber,
                StartLine = lineNumber,
                StartColumn = startColumn,
                EndLine = lineNumber,
                Signature = line.Trim(),
            },
            line);
    }

    private static string NormalizeCppFriendTypeKind(string kind)
        => kind.StartsWith("enum", StringComparison.Ordinal) ? "enum" : kind;

    private static string LastCppDeclarationSegment(string value)
    {
        var text = value.Trim();
        var qualifierIndex = text.LastIndexOf("::", StringComparison.Ordinal);
        var leaf = qualifierIndex >= 0 ? text.AsSpan(qualifierIndex + 2).Trim() : text.AsSpan();
        if (!leaf.StartsWith("operator".AsSpan(), StringComparison.Ordinal))
        {
            var genericIndex = text.IndexOf('<');
            if (genericIndex >= 0)
                text = text[..genericIndex].TrimEnd();
        }

        return qualifierIndex >= 0 ? text[(qualifierIndex + 2)..].Trim() : text;
    }
}
