using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using System.Runtime.CompilerServices;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static List<SymbolRecord> ExtractHtmlSymbols(long fileId, string[] lines)
    {
        const string defaultSlotSymbolName = "(default)";

        // HTML needs proper tag-structure awareness so attribute lookalikes inside
        // other attributes' quoted values (e.g. `<link title="href=evil.css" href="/real.css">`)
        // don't leak phantom imports AND real attributes on the same tag aren't
        // skipped. Regex alone can't do this — the outer tag context is lost once
        // an attribute inside it is rejected — so walk the masked text with a
        // character state machine that enumerates each tag's attributes in order.
        // HTML は同一タグ内で別属性の引用符付き値に書かれた attribute 名の文字列（例:
        // `<link title="href=evil.css" href="/real.css">`）から phantom な import を
        // 漏らさず、かつ本物の属性を飛ばさないために、タグ構造を理解した走査が必要。
        // regex だけでは、タグ内のある属性を mask で落とした瞬間に外側タグのコンテキスト
        // を失うため不可能。マスク済みテキストを文字単位の state machine で走査し、タグ
        // ごとに属性を列挙していく。
        var rawText = string.Join('\n', lines);
        var maskedText = MaskHtmlRawTextRegions(rawText);

        // Precompute per-line absolute offsets for O(log n) line lookup via binary
        // search. Each lines[i] does not include the joining '\n', so lineStarts[i]
        // points at the first character of line i.
        // 各シンボルの行番号を O(log n) で引けるように行ごとの絶対 offset を事前計算。
        // lines[i] 自体は連結に使う '\n' を含まないため、lineStarts[i] は i 行目の
        // 先頭文字位置を指す。
        var lineStarts = new int[lines.Length];
        var lineCursor = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            lineStarts[i] = lineCursor;
            lineCursor += lines[i].Length + 1;
        }

        var symbols = new List<SymbolRecord>();
        var pos = 0;
        while (pos < maskedText.Length)
        {
            if (maskedText[pos] != '<')
            {
                pos++;
                continue;
            }

            // Skip closing tags, comments/doctypes/CDATA, and processing instructions.
            // Raw-text bodies (<script>/<style>) and comments have already been masked
            // by MaskHtmlRawTextRegions, but the opening/closing tags themselves remain.
            // 閉じタグ / コメント / doctype / 処理命令はここで読み飛ばす。raw-text 本文と
            // HTML コメントは MaskHtmlRawTextRegions で既に空白化されているが、開始タグ
            // 自体はそのまま残っているため通常の属性走査対象になる。
            if (pos + 1 < maskedText.Length && (maskedText[pos + 1] == '/' || maskedText[pos + 1] == '!' || maskedText[pos + 1] == '?'))
            {
                pos = IndexOfOrEnd(maskedText, '>', pos + 1) + 1;
                continue;
            }

            var tagNameStart = pos + 1;
            if (tagNameStart >= maskedText.Length || !IsHtmlTagNameStart(maskedText[tagNameStart]))
            {
                pos++;
                continue;
            }

            var tagNameEnd = tagNameStart;
            while (tagNameEnd < maskedText.Length && IsHtmlTagNameChar(maskedText[tagNameEnd]))
                tagNameEnd++;

            var tagName = maskedText[tagNameStart..tagNameEnd];
            var tagNameLower = tagName.ToLowerInvariant();
            var sawNamedSlotDeclaration = false;

            // Emit custom Web Components (hyphenated opening tag) at the `<` position,
            // but skip the standard HTML/SVG/MathML tags that happen to contain a hyphen
            // (`<font-face>`, `<color-profile>`, `<annotation-xml>`, etc.). Those are
            // native elements, not user components, so labeling them as `class` symbols
            // would pollute `symbols` / `definition` / `outline` on any project with
            // inline SVG / MathML content.
            // 開始タグ名にハイフンを含むカスタム Web Components を `<` の位置で emit する。
            // ただしハイフン付きでも仕様で予約されている `<font-face>` / `<color-profile>`
            // / `<annotation-xml>` などの標準タグは除外する。SVG / MathML を埋め込んだ
            // ファイルで `symbols` / `definition` / `outline` が汚染されるのを防ぐ。
            if (tagName.Contains('-') && !HtmlReservedHyphenatedTags.Contains(tagNameLower))
            {
                var startLine = FindHtmlLineNumber(lineStarts, pos);
                var signatureIndex = Math.Clamp(startLine - 1, 0, lines.Length - 1);
                symbols.Add(new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "class",
                    Name = tagName,
                    Line = startLine,
                    StartLine = startLine,
                    EndLine = startLine,
                    Signature = lines[signatureIndex].Trim(),
                });
            }

            // Walk the tag body, enumerating attribute name/value pairs until `>` or EOF.
            // タグ本体を走査し、`>` か EOF まで属性 name/value を順に列挙する。
            var cursor = tagNameEnd;
            while (cursor < maskedText.Length && maskedText[cursor] != '>')
            {
                // Skip whitespace and stray '/' (self-closing marker).
                // 空白文字と self-closing の `/` を読み飛ばす。
                if (char.IsWhiteSpace(maskedText[cursor]) || maskedText[cursor] == '/')
                {
                    cursor++;
                    continue;
                }

                // Read attribute name. HTML5 allows broad attribute-name charsets, but for
                // our emit rules we only need to recognize ASCII names plus `:` / `-` / `.`
                // (xml:id, data-*, aria-*, etc.). Anything else aborts the parse of this tag
                // gracefully by treating it as a non-matching attribute start.
                // 属性名を読む。HTML5 の属性名は広いが、emit 対象の判定には ASCII の名前と
                // `:` / `-` / `.` が拾えれば十分（xml:id, data-*, aria-* 等を含めるため）。
                // それ以外の文字が来たら、このタグのパースは壊さずに 1 文字進めるだけで抜ける。
                if (!IsHtmlAttrNameStart(maskedText[cursor]))
                {
                    cursor++;
                    continue;
                }
                var attrNameStart = cursor;
                while (cursor < maskedText.Length && IsHtmlAttrNameChar(maskedText[cursor]))
                    cursor++;
                var attrName = maskedText[attrNameStart..cursor];
                var attrNameLower = attrName.ToLowerInvariant();

                // Skip whitespace between name and `=`.
                while (cursor < maskedText.Length && char.IsWhiteSpace(maskedText[cursor]))
                    cursor++;

                string? attrValue = null;
                int attrValueStart = -1;
                if (cursor < maskedText.Length && maskedText[cursor] == '=')
                {
                    cursor++;
                    while (cursor < maskedText.Length && char.IsWhiteSpace(maskedText[cursor]))
                        cursor++;
                    if (cursor < maskedText.Length && (maskedText[cursor] == '"' || maskedText[cursor] == '\''))
                    {
                        var quote = maskedText[cursor];
                        cursor++;
                        attrValueStart = cursor;
                        // Use the shared FindHtmlQuoteClose helper so this and the raw-text
                        // mask agree on where quoted attribute values end. The helper allows
                        // multi-line quoted values (valid HTML5 like `<div title="line1\n
                        // line2" id="real">` where `id="real"` must still be emitted) and
                        // tag-like content inside quoted values, identifying the close by
                        // post-value context (`>`, `/`, whitespace, or EOF). Only truly
                        // unterminated quotes (no matching `"` at all) return -1, so the
                        // caller can bail to EOL without walking to EOF.
                        // 共有ヘルパー `FindHtmlQuoteClose` を使い、mask 側とも引用符終端の
                        // 判断を一致させる。複数行 quoted 属性値 (`<div title="line1\n
                        // line2" id="real">` など) とタグ様テキストを含む引用符付き値を
                        // 許容し、`>` / `/` / 空白 / EOF が直後に来る位置を終端として検出する。
                        // 真に未終端（マッチ `"` が存在しない）場合のみ -1 を返し、呼び出し側が
                        // EOF まで走らず行末で被害を止められるようにする。
                        var valueEnd = FindHtmlQuoteClose(maskedText, cursor, quote);
                        if (valueEnd < 0)
                        {
                            // Unterminated: bail to end of current line so the outer tag
                            // loop can restart at the beginning of the next line's `<`.
                            // 未終端: 当該行末まで進め、次行先頭の `<` から外側ループが再開できるようにする。
                            attrValue = null;
                            var eol = maskedText.IndexOf('\n', cursor);
                            cursor = eol < 0 ? maskedText.Length : eol;
                            break;
                        }
                        attrValue = maskedText[cursor..valueEnd];
                        cursor = valueEnd + 1;
                    }
                    else if (cursor < maskedText.Length && maskedText[cursor] != '>')
                    {
                        // Unquoted value: HTML5 excludes space, `"`, `'`, `=`, `<`, `>`, backtick.
                        // 引用符なし値: HTML5 では空白、`"`、`'`、`=`、`<`、`>`、バッククォートを除外。
                        attrValueStart = cursor;
                        while (cursor < maskedText.Length && !IsHtmlUnquotedValueTerminator(maskedText[cursor]))
                            cursor++;
                        attrValue = maskedText[attrValueStart..cursor];
                    }
                }

                if (IsHtmlSemanticStateAttributeName(attrNameLower))
                {
                    var attrStartLine = FindHtmlLineNumber(lineStarts, attrNameStart);
                    var attrSignatureIndex = Math.Clamp(attrStartLine - 1, 0, lines.Length - 1);
                    symbols.Add(new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = "property",
                        Name = attrNameLower,
                        Line = attrStartLine,
                        StartLine = attrStartLine,
                        EndLine = attrStartLine,
                        Signature = lines[attrSignatureIndex].Trim(),
                    });
                }

                if (attrValue == null || attrValue.Length == 0)
                    continue;

                string? emitKind = null;
                List<string>? emittedNames = null;
                if (attrNameLower == "src" && IsHtmlSrcResourceTag(tagNameLower))
                {
                    emitKind = "import";
                    emittedNames = [attrValue.Trim()];
                }
                else if (attrNameLower == "srcset" && IsHtmlSrcsetResourceTag(tagNameLower))
                {
                    emitKind = "import";
                    emittedNames = EnumerateHtmlSrcsetUrls(attrValue).ToList();
                }
                else if ((attrNameLower == "href" || attrNameLower == "xlink:href") && IsHtmlHrefResourceTag(tagNameLower))
                {
                    emitKind = "import";
                    emittedNames = [attrValue.Trim()];
                }
                else if (attrNameLower == "data" && tagNameLower == "object")
                {
                    emitKind = "import";
                    emittedNames = [attrValue.Trim()];
                }
                else if (attrNameLower == "poster" && tagNameLower == "video")
                {
                    emitKind = "import";
                    emittedNames = [attrValue.Trim()];
                }
                else if (attrNameLower == "id" && !attrName.Contains(':') && !attrName.Contains('-') && !attrName.Contains('.'))
                {
                    emitKind = "property";
                    emittedNames = [attrValue.Trim()];
                }
                else if (attrNameLower is "class" or "classname")
                {
                    emitKind = "reference";
                    emittedNames = EnumerateHtmlClassNames(attrValue).ToList();
                }
                else if (attrNameLower == "name" && tagNameLower == "slot")
                {
                    var slotName = attrValue.Trim();
                    if (slotName.Length > 0)
                    {
                        emitKind = "property";
                        emittedNames = [slotName];
                        sawNamedSlotDeclaration = true;
                    }
                }
                else if (attrNameLower == "slot")
                {
                    var slotName = attrValue.Trim();
                    if (slotName.Length > 0)
                    {
                        emitKind = "reference";
                        emittedNames = [slotName];
                    }
                }

                if (emitKind == null || emittedNames == null || emittedNames.Count == 0)
                    continue;

                // Anchor the symbol at the attribute value so cross-line tags like
                // `<script\n  type="module"\n  src="/app.js">` land on the line that
                // actually carries the value.
                // 属性値の位置でシンボルを固定し、属性が折り返されたタグでも値が書かれた
                // 行にジャンプできるようにする。
                var anchor = attrValueStart >= 0 ? attrValueStart : pos;
                var startLine = FindHtmlLineNumber(lineStarts, anchor);
                var signatureIndex = Math.Clamp(startLine - 1, 0, lines.Length - 1);
                foreach (var emittedName in emittedNames)
                {
                    symbols.Add(new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = emitKind,
                        Name = emittedName,
                        Line = startLine,
                        StartLine = startLine,
                        EndLine = startLine,
                        Signature = lines[signatureIndex].Trim(),
                    });
                }
            }

            if (tagNameLower == "slot" && !sawNamedSlotDeclaration)
            {
                var startLine = FindHtmlLineNumber(lineStarts, pos);
                var signatureIndex = Math.Clamp(startLine - 1, 0, lines.Length - 1);
                symbols.Add(new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "property",
                    Name = defaultSlotSymbolName,
                    Line = startLine,
                    StartLine = startLine,
                    EndLine = startLine,
                    Signature = lines[signatureIndex].Trim(),
                });
            }

            pos = cursor < maskedText.Length ? cursor + 1 : cursor;
        }

        AssignContainers(symbols, lines, null);
        PopulateDeclaredContainerQualifiedNames(symbols);
        return symbols;
    }

    private static IEnumerable<string> EnumerateHtmlClassNames(string value)
    {
        foreach (var token in value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            yield return token;
    }

    private static bool IsHtmlSemanticStateAttributeName(string attrNameLower)
    {
        return (attrNameLower.StartsWith("data-", StringComparison.Ordinal) ||
                attrNameLower.StartsWith("aria-", StringComparison.Ordinal)) &&
               attrNameLower.Length > 5;
    }

    private static List<SymbolRecord> ExtractMarkdownSymbols(long fileId, string[] lines)
    {
        // Markdown headings are the closest thing to navigable symbols in docs files.
        // Markdown の見出しは、ドキュメント内でナビゲート可能な symbol に最も近い。
        var referenceTargets = BuildMarkdownReferenceDefinitionTargets(lines);
        var symbols = new List<SymbolRecord>();
        var headingStack = new Stack<(int Level, int SymbolIndex)>();
        var inFence = false;
        var fenceChar = '\0';
        var fenceLength = 0;
        var fenceSymbolIndex = -1;

        for (var i = 0; i < lines.Length; i++)
        {
            if (TryToggleMarkdownFence(lines[i], inFence, fenceChar, fenceLength, out var nextFenceChar, out var nextFenceLength, out var fenceInfo))
            {
                if (!inFence)
                {
                    var codeSymbol = new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = "code",
                        Name = NormalizeMarkdownFenceInfo(fenceInfo),
                        Line = i + 1,
                        StartLine = i + 1,
                        EndLine = lines.Length,
                        BodyStartLine = i + 2,
                        BodyEndLine = lines.Length,
                        Signature = lines[i].Trim(),
                    };

                    if (headingStack.Count > 0)
                    {
                        var parent = symbols[headingStack.Peek().SymbolIndex];
                        codeSymbol.ContainerKind = "heading";
                        codeSymbol.ContainerName = parent.Name;
                    }

                    symbols.Add(codeSymbol);
                    fenceSymbolIndex = symbols.Count - 1;
                }
                else if (fenceSymbolIndex >= 0)
                {
                    symbols[fenceSymbolIndex].EndLine = i + 1;
                    symbols[fenceSymbolIndex].BodyEndLine = Math.Max(symbols[fenceSymbolIndex].BodyStartLine ?? i + 1, i);
                    fenceSymbolIndex = -1;
                }

                inFence = nextFenceLength > 0;
                fenceChar = nextFenceChar;
                fenceLength = nextFenceLength;
                continue;
            }

            if (inFence)
                continue;

            if (i + 1 < lines.Length
                && TryParseMarkdownSetextHeading(lines[i], lines[i + 1], out var setextLevel, out var setextHeadingText))
            {
                while (headingStack.Count > 0 && headingStack.Peek().Level >= setextLevel)
                {
                    var closedHeading = headingStack.Pop();
                    symbols[closedHeading.SymbolIndex].EndLine = i;
                    symbols[closedHeading.SymbolIndex].BodyEndLine = i;
                }

                var setextSymbol = new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "heading",
                    Name = setextHeadingText,
                    Line = i + 1,
                    StartLine = i + 1,
                    EndLine = i + 2,
                    BodyStartLine = i + 3,
                    BodyEndLine = lines.Length,
                    Signature = lines[i].TrimEnd(),
                };

                if (headingStack.Count > 0)
                {
                    var parent = symbols[headingStack.Peek().SymbolIndex];
                    setextSymbol.ContainerKind = "heading";
                    setextSymbol.ContainerName = parent.Name;
                }

                symbols.Add(setextSymbol);
                headingStack.Push((setextLevel, symbols.Count - 1));
                AddMarkdownReferenceSymbols(fileId, lines[i], i + 1, symbols, referenceTargets);
                i++;
                continue;
            }

            if (!TryParseMarkdownHeading(lines[i], out var level, out var headingText))
            {
                AddMarkdownReferenceSymbols(fileId, lines[i], i + 1, symbols, referenceTargets);
                continue;
            }

            while (headingStack.Count > 0 && headingStack.Peek().Level >= level)
            {
                var closedHeading = headingStack.Pop();
                symbols[closedHeading.SymbolIndex].EndLine = i;
                symbols[closedHeading.SymbolIndex].BodyEndLine = i;
            }

            var symbol = new SymbolRecord
            {
                FileId = fileId,
                Kind = "heading",
                Name = headingText,
                Line = i + 1,
                StartLine = i + 1,
                EndLine = i + 1,
                BodyStartLine = i + 2,
                BodyEndLine = lines.Length,
                Signature = lines[i].Trim(),
            };

            if (headingStack.Count > 0)
            {
                var parent = symbols[headingStack.Peek().SymbolIndex];
                symbol.ContainerKind = "heading";
                symbol.ContainerName = parent.Name;
            }

            symbols.Add(symbol);
            headingStack.Push((level, symbols.Count - 1));
            AddMarkdownReferenceSymbols(fileId, lines[i], i + 1, symbols, referenceTargets);
        }

        while (headingStack.Count > 0)
        {
            var closedHeading = headingStack.Pop();
            symbols[closedHeading.SymbolIndex].EndLine = lines.Length;
            symbols[closedHeading.SymbolIndex].BodyEndLine = lines.Length;
        }

        return symbols;
    }

    private static Dictionary<string, string> BuildMarkdownReferenceDefinitionTargets(string[] lines)
    {
        var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inFence = false;
        var fenceChar = '\0';
        var fenceLength = 0;

        foreach (var line in lines)
        {
            if (TryToggleMarkdownFence(line, inFence, fenceChar, fenceLength, out var nextFenceChar, out var nextFenceLength, out _))
            {
                inFence = nextFenceLength > 0;
                fenceChar = nextFenceChar;
                fenceLength = nextFenceLength;
                continue;
            }

            if (inFence)
                continue;

            foreach (Match match in MarkdownReferenceDefinitionRegex.Matches(line))
                targets[match.Groups["label"].Value.Trim()] = match.Groups["target"].Value.Trim();
        }

        return targets;
    }

    private static void AddMarkdownReferenceSymbols(long fileId, string line, int lineNumber, List<SymbolRecord> symbols, IReadOnlyDictionary<string, string> referenceTargets)
    {
        foreach (Match match in MarkdownLocalAnchorLinkRegex.Matches(line))
            AddMarkdownReferenceSymbol(fileId, match.Groups["target"].Value, line, lineNumber, symbols);

        foreach (Match match in MarkdownLocalAnchorReferenceRegex.Matches(line))
            AddMarkdownReferenceSymbol(fileId, match.Groups["target"].Value, line, lineNumber, symbols);

        foreach (Match match in MarkdownReferenceLinkRegex.Matches(line))
        {
            var label = match.Groups["label"].Value.Trim();
            if (label.Length == 0)
                continue;

            if (referenceTargets.TryGetValue(label, out var target) && target.TrimStart().StartsWith("#", StringComparison.Ordinal))
                AddMarkdownReferenceSymbol(fileId, target, line, lineNumber, symbols);
        }
    }

    private static void AddMarkdownReferenceSymbol(long fileId, string target, string line, int lineNumber, List<SymbolRecord> symbols)
    {
        var normalizedTarget = NormalizeMarkdownAnchorTarget(target);
        if (normalizedTarget.Length == 0)
            return;

        symbols.Add(new SymbolRecord
        {
            FileId = fileId,
            Kind = "reference",
            Name = normalizedTarget,
            Line = lineNumber,
            StartLine = lineNumber,
            EndLine = lineNumber,
            Signature = line.Trim(),
        });
    }

    private static bool TryToggleMarkdownFence(
        string line,
        bool inFence,
        char fenceChar,
        int fenceLength,
        out char nextFenceChar,
        out int nextFenceLength,
        out string fenceInfo)
    {
        nextFenceChar = '\0';
        nextFenceLength = 0;
        fenceInfo = string.Empty;

        var index = 0;
        while (index < line.Length && index < 3 && line[index] == ' ')
            index++;

        if (index > 3 || index >= line.Length)
            return false;

        var marker = line[index];
        if (marker is not ('`' or '~'))
            return false;

        var length = index;
        while (length < line.Length && line[length] == marker)
            length++;

        if (length - index < 3)
            return false;

        if (inFence && marker == fenceChar && length - index >= fenceLength)
            return true;

        if (!inFence)
        {
            nextFenceChar = marker;
            nextFenceLength = length - index;
            fenceInfo = line[length..].Trim();
            return true;
        }

        return false;
    }

    private static string NormalizeMarkdownFenceInfo(string fenceInfo)
    {
        var normalized = fenceInfo.Trim();
        if (normalized.Length == 0)
            return "code";

        var separatorIndex = normalized.IndexOfAny([' ', '\t', '\r', '\n']);
        if (separatorIndex >= 0)
            normalized = normalized[..separatorIndex];

        return normalized.Length == 0 ? "code" : normalized;
    }

    private static bool TryParseMarkdownSetextHeading(string currentLine, string nextLine, out int level, out string headingText)
    {
        level = 0;
        headingText = string.Empty;

        var trimmedHeading = currentLine.Trim();
        if (trimmedHeading.Length == 0)
            return false;

        if (TryParseMarkdownHeading(currentLine, out _, out _))
            return false;

        var trimmedUnderline = nextLine.Trim();
        if (trimmedUnderline.Length < 3)
            return false;

        var underlineChar = trimmedUnderline[0];
        if (underlineChar is not ('=' or '-'))
            return false;

        for (var i = 1; i < trimmedUnderline.Length; i++)
        {
            if (trimmedUnderline[i] != underlineChar)
                return false;
        }

        level = underlineChar == '=' ? 1 : 2;
        headingText = trimmedHeading;
        return true;
    }

    private static bool TryParseMarkdownHeading(string line, out int level, out string headingText)
    {
        level = 0;
        headingText = string.Empty;

        var index = 0;
        while (index < line.Length && index < 3 && line[index] == ' ')
            index++;

        if (index > 3 || index >= line.Length || line[index] != '#')
            return false;

        var hashStart = index;
        while (index < line.Length && line[index] == '#')
            index++;

        level = index - hashStart;
        if (level is < 1 or > 6)
            return false;

        if (index < line.Length && !char.IsWhiteSpace(line[index]))
            return false;

        while (index < line.Length && char.IsWhiteSpace(line[index]))
            index++;

        if (index >= line.Length)
            return false;

        headingText = line[index..].Trim();
        if (headingText.Length == 0)
            return false;

        var closingHashesStart = headingText.Length;
        while (closingHashesStart > 0 && headingText[closingHashesStart - 1] == '#')
            closingHashesStart--;

        if (closingHashesStart < headingText.Length && closingHashesStart > 0 && char.IsWhiteSpace(headingText[closingHashesStart - 1]))
            headingText = headingText[..(closingHashesStart - 1)].TrimEnd();

        return headingText.Length > 0;
    }

    private static string NormalizeMarkdownAnchorTarget(string target)
    {
        var normalized = target.Trim();
        if (normalized.Length >= 2 && normalized[0] == '<' && normalized[^1] == '>')
            normalized = normalized[1..^1].Trim();

        return normalized.StartsWith("#", StringComparison.Ordinal)
            ? normalized[1..]
            : normalized;
    }

    private static List<SymbolRecord> ExtractXmlSymbols(long fileId, string[] lines)
    {
        var hasXamlNamespace = false;
        foreach (var line in lines)
        {
            if (line.IndexOf("xmlns:x=", StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            if (line.IndexOf("schemas.microsoft.com/winfx/2006/xaml", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("github.com/avaloniaui", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                hasXamlNamespace = true;
                break;
            }
        }

        if (!hasXamlNamespace)
            return [];

        var rawText = string.Join('\n', lines);
        var lineStarts = new int[lines.Length];
        var lineCursor = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            lineStarts[i] = lineCursor;
            lineCursor += lines[i].Length + 1;
        }

        var symbols = new List<SymbolRecord>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            foreach (Match classMatch in XamlClassRegex.Matches(line))
            {
                var value = classMatch.Groups["value"].Value.Trim();
                if (value.Length == 0)
                    continue;
                symbols.Add(new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "class",
                    Name = value,
                    Line = i + 1,
                    StartLine = i + 1,
                    EndLine = i + 1,
                    Signature = line.Trim(),
                });
            }

            foreach (Match dataTypeMatch in XamlDataTypeRegex.Matches(line))
            {
                var value = NormalizeXamlKeyValue(dataTypeMatch.Groups["value"].Value);
                if (value.Length == 0)
                    continue;
                symbols.Add(new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "class",
                    Name = value,
                    Line = i + 1,
                    StartLine = i + 1,
                    EndLine = i + 1,
                    Signature = line.Trim(),
                });
            }

            foreach (Match typeArgumentsMatch in XamlTypeArgumentsRegex.Matches(line))
            {
                foreach (var value in NormalizeXamlTypeArgumentsValue(typeArgumentsMatch.Groups["value"].Value))
                {
                    symbols.Add(new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = "class",
                        Name = value,
                        Line = i + 1,
                        StartLine = i + 1,
                        EndLine = i + 1,
                        Signature = line.Trim(),
                    });
                }
            }

            foreach (Match targetTypeMatch in XamlTargetTypeRegex.Matches(line))
            {
                var value = NormalizeXamlKeyValue(targetTypeMatch.Groups["value"].Value);
                if (value.Length == 0)
                    continue;
                symbols.Add(new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "class",
                    Name = value,
                    Line = i + 1,
                    StartLine = i + 1,
                    EndLine = i + 1,
                    Signature = line.Trim(),
                });
            }

            foreach (Match nameMatch in XamlNameRegex.Matches(line))
            {
                var value = nameMatch.Groups["value"].Value.Trim();
                if (value.Length == 0)
                    continue;
                symbols.Add(new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "property",
                    Name = value,
                    Line = i + 1,
                    StartLine = i + 1,
                    EndLine = i + 1,
                    Signature = line.Trim(),
                });
            }

            foreach (Match keyMatch in XamlKeyRegex.Matches(line))
            {
                var value = NormalizeXamlKeyValue(keyMatch.Groups["value"].Value);
                if (value.Length == 0)
                    continue;
                symbols.Add(new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "property",
                    Name = value,
                    Line = i + 1,
                    StartLine = i + 1,
                    EndLine = i + 1,
                    Signature = line.Trim(),
                });
            }

            foreach (Match handlerMatch in XamlEventHandlerRegex.Matches(line))
            {
                var value = handlerMatch.Groups["value"].Value.Trim();
                if (value.Length == 0)
                    continue;
                symbols.Add(new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "function",
                    Name = value,
                    Line = i + 1,
                    StartLine = i + 1,
                    EndLine = i + 1,
                    Signature = line.Trim(),
                });
            }
        }

        AddWrappedXamlTypeArgumentSymbols(fileId, rawText, lines, lineStarts, symbols);
        AddWrappedXamlTypeBearingAttributeSymbols(fileId, rawText, lines, lineStarts, symbols);
        AddWrappedXamlSearchAttributeSymbols(fileId, rawText, lines, lineStarts, symbols);
        AddXamlTypeObjectElementSymbols(fileId, rawText, lines, lineStarts, symbols);
        AddXamlTypePropertyElementSymbols(fileId, rawText, lines, lineStarts, symbols);
        AddXamlTypeMarkupSymbols(fileId, rawText, lines, lineStarts, symbols);
        AddXamlStaticMemberTypeSymbols(fileId, rawText, lines, lineStarts, symbols);
        AddXamlReferenceSymbols(fileId, rawText, lines, lineStarts, symbols);
        AddXamlResourceReferenceSymbols(fileId, rawText, lines, lineStarts, symbols);
        AddXamlBindingElementNameSymbols(fileId, rawText, lines, lineStarts, symbols);
        AddXamlBindingObjectElementSymbols(fileId, rawText, lines, lineStarts, symbols);

        foreach (Match bindingMatch in XamlBindingRegex.Matches(rawText))
        {
            var value = NormalizeXamlBindingValue(bindingMatch.Groups["kind"].Value, bindingMatch.Groups["content"].Value);
            if (value.Length == 0)
                continue;

            var startLine = FindHtmlLineNumber(lineStarts, bindingMatch.Index);
            var signatureIndex = Math.Clamp(startLine - 1, 0, lines.Length - 1);
            symbols.Add(new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = value,
                Line = startLine,
                StartLine = startLine,
                EndLine = startLine,
                Signature = lines[signatureIndex].Trim(),
            });
        }

        return symbols;
    }

    private static void AddWrappedXamlTypeArgumentSymbols(
        long fileId,
        string rawText,
        string[] lines,
        int[] lineStarts,
        List<SymbolRecord> symbols)
    {
        var cursor = 0;
        while (cursor < rawText.Length)
        {
            var attributeIndex = rawText.IndexOf("x:TypeArguments", cursor, StringComparison.Ordinal);
            if (attributeIndex < 0)
                break;

            var equalsIndex = rawText.IndexOf('=', attributeIndex);
            if (equalsIndex < 0)
            {
                cursor = attributeIndex + 1;
                continue;
            }

            var quoteIndex = equalsIndex + 1;
            while (quoteIndex < rawText.Length && char.IsWhiteSpace(rawText[quoteIndex]))
                quoteIndex++;

            if (quoteIndex >= rawText.Length)
                break;

            var quote = rawText[quoteIndex];
            if (quote is not ('"' or '\''))
            {
                cursor = quoteIndex + 1;
                continue;
            }

            var valueStart = quoteIndex + 1;
            var valueEnd = valueStart;
            while (valueEnd < rawText.Length && rawText[valueEnd] != quote)
                valueEnd++;

            if (valueEnd >= rawText.Length)
            {
                cursor = valueStart;
                continue;
            }

            if (FindHtmlLineNumber(lineStarts, valueEnd) == FindHtmlLineNumber(lineStarts, attributeIndex))
            {
                cursor = valueEnd + 1;
                continue;
            }

            var value = rawText[valueStart..valueEnd];
            if (value.Length > 0)
            {
                var startLine = FindHtmlLineNumber(lineStarts, attributeIndex);
                var signatureIndex = Math.Clamp(startLine - 1, 0, lines.Length - 1);
                foreach (var normalized in NormalizeXamlTypeArgumentsValue(value))
                {
                    symbols.Add(new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = "class",
                        Name = normalized,
                        Line = startLine,
                        StartLine = startLine,
                        EndLine = startLine,
                        Signature = lines[signatureIndex].Trim(),
                    });
                }
            }

            cursor = valueEnd + 1;
        }
    }

    private static void AddWrappedXamlTypeBearingAttributeSymbols(
        long fileId,
        string rawText,
        string[] lines,
        int[] lineStarts,
        List<SymbolRecord> symbols)
    {
        // Handle XAML values that are split away from `=` onto later lines.
        // `x:Class`, `x:DataType`, and `TargetType` are intentionally kept on the
        // same normalization path as the line-based extractor so search results stay consistent.
        foreach (var attributeName in new[] { "x:Class", "x:DataType", "TargetType" })
        {
            var cursor = 0;
            while (cursor < rawText.Length)
            {
                var attributeIndex = rawText.IndexOf(attributeName, cursor, StringComparison.Ordinal);
                if (attributeIndex < 0)
                    break;

                var equalsIndex = rawText.IndexOf('=', attributeIndex);
                if (equalsIndex < 0)
                {
                    cursor = attributeIndex + 1;
                    continue;
                }

                var quoteIndex = equalsIndex + 1;
                while (quoteIndex < rawText.Length && char.IsWhiteSpace(rawText[quoteIndex]))
                    quoteIndex++;

                if (quoteIndex >= rawText.Length)
                    break;

                var quote = rawText[quoteIndex];
                if (quote is not ('"' or '\''))
                {
                    cursor = quoteIndex + 1;
                    continue;
                }

                var valueStart = quoteIndex + 1;
                var valueEnd = valueStart;
                while (valueEnd < rawText.Length && rawText[valueEnd] != quote)
                    valueEnd++;

                if (valueEnd >= rawText.Length)
                {
                    cursor = valueStart;
                    continue;
                }

                if (FindHtmlLineNumber(lineStarts, valueEnd) == FindHtmlLineNumber(lineStarts, attributeIndex))
                {
                    cursor = valueEnd + 1;
                    continue;
                }

                var value = NormalizeXamlKeyValue(rawText[valueStart..valueEnd]);
                if (value.Length > 0)
                {
                    var startLine = FindHtmlLineNumber(lineStarts, attributeIndex);
                    var signatureIndex = Math.Clamp(startLine - 1, 0, lines.Length - 1);
                    symbols.Add(new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = "class",
                        Name = value,
                        Line = startLine,
                        StartLine = startLine,
                        EndLine = startLine,
                        Signature = lines[signatureIndex].Trim(),
                    });
                }

                cursor = valueEnd + 1;
            }
        }
    }

    private static void AddWrappedXamlSearchAttributeSymbols(
        long fileId,
        string rawText,
        string[] lines,
        int[] lineStarts,
        List<SymbolRecord> symbols)
    {
        foreach (var occurrence in EnumerateWrappedXamlAttributeValues(rawText, lineStarts, "x:Name"))
            AddXamlAttributeSymbol(fileId, lines, lineStarts, symbols, occurrence.AttributeIndex, "property", occurrence.Value.Trim());

        foreach (var occurrence in EnumerateWrappedXamlAttributeValues(rawText, lineStarts, "x:Key"))
            AddXamlAttributeSymbol(fileId, lines, lineStarts, symbols, occurrence.AttributeIndex, "property", NormalizeXamlKeyValue(occurrence.Value));

        foreach (var attributeName in XamlEventAttributeNames)
        {
            foreach (var occurrence in EnumerateWrappedXamlAttributeValues(rawText, lineStarts, attributeName))
                AddXamlAttributeSymbol(fileId, lines, lineStarts, symbols, occurrence.AttributeIndex, "function", occurrence.Value.Trim());
        }
    }

    private static IEnumerable<(int AttributeIndex, string Value)> EnumerateWrappedXamlAttributeValues(
        string rawText,
        int[] lineStarts,
        string attributeName)
    {
        var cursor = 0;
        while (cursor < rawText.Length)
        {
            var attributeIndex = rawText.IndexOf(attributeName, cursor, StringComparison.Ordinal);
            if (attributeIndex < 0)
                yield break;

            if (!TryReadXamlAttributeValue(rawText, attributeName, attributeIndex, out var valueStart, out var valueEnd))
            {
                cursor = attributeIndex + 1;
                continue;
            }

            if (FindHtmlLineNumber(lineStarts, valueEnd) != FindHtmlLineNumber(lineStarts, attributeIndex))
                yield return (attributeIndex, rawText[valueStart..valueEnd]);

            cursor = valueEnd + 1;
        }
    }

    private static bool TryReadXamlAttributeValue(
        string rawText,
        string attributeName,
        int attributeIndex,
        out int valueStart,
        out int valueEnd)
    {
        valueStart = -1;
        valueEnd = -1;

        if (!IsXamlAttributeNameMatch(rawText, attributeIndex, attributeName.Length))
            return false;

        var cursor = attributeIndex + attributeName.Length;
        while (cursor < rawText.Length && char.IsWhiteSpace(rawText[cursor]))
            cursor++;

        if (cursor >= rawText.Length || rawText[cursor] != '=')
            return false;

        cursor++;
        while (cursor < rawText.Length && char.IsWhiteSpace(rawText[cursor]))
            cursor++;

        if (cursor >= rawText.Length)
            return false;

        var quote = rawText[cursor];
        if (quote is not ('"' or '\''))
            return false;

        valueStart = cursor + 1;
        valueEnd = valueStart;
        while (valueEnd < rawText.Length && rawText[valueEnd] != quote)
            valueEnd++;

        return valueEnd < rawText.Length;
    }

    private static bool IsXamlAttributeNameMatch(string rawText, int index, int length)
    {
        if (index > 0 && IsXamlAttributeNameChar(rawText[index - 1]))
            return false;

        var after = index + length;
        return after >= rawText.Length || !IsXamlAttributeNameChar(rawText[after]);
    }

    private static bool IsXamlAttributeNameChar(char c)
        => IsXamlMarkupNameChar(c) || c == '-';

    private static void AddXamlAttributeSymbol(
        long fileId,
        string[] lines,
        int[] lineStarts,
        List<SymbolRecord> symbols,
        int attributeIndex,
        string kind,
        string value)
    {
        value = value.Trim();
        if (value.Length == 0)
            return;

        var startLine = FindHtmlLineNumber(lineStarts, attributeIndex);
        var signatureIndex = Math.Clamp(startLine - 1, 0, lines.Length - 1);
        symbols.Add(new SymbolRecord
        {
            FileId = fileId,
            Kind = kind,
            Name = value,
            Line = startLine,
            StartLine = startLine,
            EndLine = startLine,
            Signature = lines[signatureIndex].Trim(),
        });
    }

    private static void AddXamlBindingElementNameSymbols(
        long fileId,
        string rawText,
        string[] lines,
        int[] lineStarts,
        List<SymbolRecord> symbols)
    {
        foreach (Match bindingMatch in XamlBindingRegex.Matches(rawText))
        {
            if (!bindingMatch.Groups["kind"].Value.Equals("Binding", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = NormalizeXamlBindingElementNameValue(bindingMatch.Groups["content"].Value);
            if (value.Length == 0)
                continue;

            AddXamlAttributeSymbol(fileId, lines, lineStarts, symbols, bindingMatch.Index, "property", value);
        }

        var cursor = 0;
        while (cursor < rawText.Length)
        {
            var tagIndex = rawText.IndexOf("<Binding", cursor, StringComparison.Ordinal);
            if (tagIndex < 0)
                break;

            var nameEnd = tagIndex + "<Binding".Length;
            if (nameEnd < rawText.Length && IsXamlAttributeNameChar(rawText[nameEnd]))
            {
                cursor = nameEnd;
                continue;
            }

            var tagEnd = FindXamlTagEnd(rawText, nameEnd);
            if (tagEnd < 0)
                break;

            var elementNameAttributeIndex = IndexOfXamlAttributeInRange(rawText, "ElementName", nameEnd, tagEnd);
            if (elementNameAttributeIndex >= 0
                && TryReadXamlAttributeValue(rawText, "ElementName", elementNameAttributeIndex, out var valueStart, out var valueEnd)
                && valueEnd <= tagEnd)
            {
                AddXamlAttributeSymbol(
                    fileId,
                    lines,
                    lineStarts,
                    symbols,
                    elementNameAttributeIndex,
                    "property",
                    NormalizeXamlElementReferenceValue(rawText[valueStart..valueEnd]));
            }

            cursor = tagEnd + 1;
        }

        foreach (Match elementNameMatch in XamlBindingElementNamePropertyElementRegex.Matches(rawText))
        {
            var value = NormalizeXamlElementReferenceValue(elementNameMatch.Groups["value"].Value);
            if (value.Length == 0)
                continue;

            AddXamlAttributeSymbol(fileId, lines, lineStarts, symbols, elementNameMatch.Index, "property", value);
        }
    }

    private static string NormalizeXamlBindingElementNameValue(string content)
    {
        content = content.Trim();
        if (content.Length == 0)
            return "";

        foreach (var argument in SplitTopLevelMarkupArguments(content))
        {
            var equalsIndex = IndexOfTopLevelEquals(argument);
            if (equalsIndex < 0)
                continue;

            var argumentName = argument[..equalsIndex].Trim();
            if (!string.Equals(argumentName, "ElementName", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = NormalizeXamlElementReferenceValue(argument[(equalsIndex + 1)..]);
            if (value.Length > 0)
                return value;
        }

        return "";
    }

    private static string NormalizeXamlElementReferenceValue(string value)
    {
        value = NormalizeXamlMarkupValue(value);
        return value.Trim();
    }

    private static void AddXamlBindingObjectElementSymbols(
        long fileId,
        string rawText,
        string[] lines,
        int[] lineStarts,
        List<SymbolRecord> symbols)
    {
        var cursor = 0;
        while (cursor < rawText.Length)
        {
            var tagIndex = rawText.IndexOf("<Binding", cursor, StringComparison.Ordinal);
            if (tagIndex < 0)
                break;

            var nameEnd = tagIndex + "<Binding".Length;
            if (nameEnd < rawText.Length && IsXamlAttributeNameChar(rawText[nameEnd]))
            {
                cursor = nameEnd;
                continue;
            }

            var tagEnd = FindXamlTagEnd(rawText, nameEnd);
            if (tagEnd < 0)
                break;

            var pathAttributeIndex = IndexOfXamlAttributeInRange(rawText, "Path", nameEnd, tagEnd);
            if (pathAttributeIndex >= 0
                && TryReadXamlAttributeValue(rawText, "Path", pathAttributeIndex, out var valueStart, out var valueEnd)
                && valueEnd <= tagEnd)
            {
                AddXamlAttributeSymbol(
                    fileId,
                    lines,
                    lineStarts,
                    symbols,
                    pathAttributeIndex,
                    "property",
                    NormalizeXamlBindingPathValue(rawText[valueStart..valueEnd]));
            }

            cursor = tagEnd + 1;
        }

        foreach (Match pathMatch in XamlBindingPathPropertyElementRegex.Matches(rawText))
        {
            var value = NormalizeXamlBindingPathValue(pathMatch.Groups["value"].Value);
            if (value.Length == 0)
                continue;

            var startLine = FindHtmlLineNumber(lineStarts, pathMatch.Index);
            var signatureIndex = Math.Clamp(startLine - 1, 0, lines.Length - 1);
            symbols.Add(new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = value,
                Line = startLine,
                StartLine = startLine,
                EndLine = startLine,
                Signature = lines[signatureIndex].Trim(),
            });
        }
    }

    private static int FindXamlTagEnd(string rawText, int startIndex)
    {
        char? quote = null;
        for (var i = startIndex; i < rawText.Length; i++)
        {
            var ch = rawText[i];
            if (quote is { } activeQuote)
            {
                if (ch == activeQuote)
                    quote = null;
                continue;
            }

            if (ch is '"' or '\'')
            {
                quote = ch;
                continue;
            }

            if (ch == '>')
                return i;
        }

        return -1;
    }

    private static int IndexOfXamlAttributeInRange(string rawText, string attributeName, int startIndex, int endIndex)
    {
        char? quote = null;
        for (var i = startIndex; i < endIndex; i++)
        {
            var ch = rawText[i];
            if (quote is { } activeQuote)
            {
                if (ch == activeQuote)
                    quote = null;
                continue;
            }

            if (ch is '"' or '\'')
            {
                quote = ch;
                continue;
            }

            if (!rawText.AsSpan(i, endIndex - i).StartsWith(attributeName, StringComparison.Ordinal))
                continue;

            if (IsXamlAttributeNameMatch(rawText, i, attributeName.Length)
                && TryReadXamlAttributeValue(rawText, attributeName, i, out _, out var valueEnd)
                && valueEnd <= endIndex)
            {
                return i;
            }
        }

        return -1;
    }

    private static void AddXamlTypeObjectElementSymbols(
        long fileId,
        string rawText,
        string[] lines,
        int[] lineStarts,
        List<SymbolRecord> symbols)
    {
        foreach (Match typeMatch in XamlTypeObjectElementRegex.Matches(rawText))
        {
            var value = NormalizeXamlKeyValue(typeMatch.Groups["value"].Value);
            if (value.Length == 0)
                continue;

            var startLine = FindHtmlLineNumber(lineStarts, typeMatch.Index);
            var signatureIndex = Math.Clamp(startLine - 1, 0, lines.Length - 1);
            symbols.Add(new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = value,
                Line = startLine,
                StartLine = startLine,
                EndLine = startLine,
                Signature = lines[signatureIndex].Trim(),
            });
        }
    }

    private static void AddXamlTypePropertyElementSymbols(
        long fileId,
        string rawText,
        string[] lines,
        int[] lineStarts,
        List<SymbolRecord> symbols)
    {
        foreach (Match typeMatch in XamlTypePropertyElementRegex.Matches(rawText))
        {
            var value = NormalizeXamlKeyValue(typeMatch.Groups["value"].Value);
            if (value.Length == 0)
                continue;

            var startLine = FindHtmlLineNumber(lineStarts, typeMatch.Index);
            var signatureIndex = Math.Clamp(startLine - 1, 0, lines.Length - 1);
            symbols.Add(new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = value,
                Line = startLine,
                StartLine = startLine,
                EndLine = startLine,
                Signature = lines[signatureIndex].Trim(),
            });
        }
    }

    private static void AddXamlTypeMarkupSymbols(
        long fileId,
        string rawText,
        string[] lines,
        int[] lineStarts,
        List<SymbolRecord> symbols)
    {
        AddXamlMarkupExtensionTypeSymbols(fileId, rawText, lines, lineStarts, symbols, "{x:TypeExtension", false);
        AddXamlMarkupExtensionTypeSymbols(fileId, rawText, lines, lineStarts, symbols, "{x:Type", true);
    }

    private static void AddXamlMarkupExtensionTypeSymbols(
        long fileId,
        string rawText,
        string[] lines,
        int[] lineStarts,
        List<SymbolRecord> symbols,
        string prefix,
        bool rejectNameCharAfterPrefix)
    {
        var cursor = 0;
        while (cursor < rawText.Length)
        {
            var braceIndex = rawText.IndexOf(prefix, cursor, StringComparison.Ordinal);
            if (braceIndex < 0)
                break;

            var afterPrefix = braceIndex + prefix.Length;
            if (rejectNameCharAfterPrefix
                && afterPrefix < rawText.Length
                && IsXamlMarkupNameChar(rawText[afterPrefix]))
            {
                cursor = afterPrefix;
                continue;
            }

            var closingBraceIndex = FindMatchingBrace(rawText, braceIndex);
            if (closingBraceIndex < 0)
            {
                cursor = braceIndex + 1;
                continue;
            }

            if (ShouldSkipXamlMarkupExtensionSymbol(rawText, braceIndex))
            {
                cursor = closingBraceIndex + 1;
                continue;
            }

            var value = NormalizeXamlMarkupValue(rawText[braceIndex..(closingBraceIndex + 1)]);
            if (value.Length > 0)
            {
                var startLine = FindHtmlLineNumber(lineStarts, braceIndex);
                var signatureIndex = Math.Clamp(startLine - 1, 0, lines.Length - 1);
                symbols.Add(new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "class",
                    Name = value,
                    Line = startLine,
                    StartLine = startLine,
                    EndLine = startLine,
                    Signature = lines[signatureIndex].Trim(),
                });
            }

            cursor = closingBraceIndex + 1;
        }
    }

    private static bool ShouldSkipXamlMarkupExtensionSymbol(string rawText, int braceIndex)
    {
        var tagStart = rawText.LastIndexOf('<', braceIndex);
        if (tagStart < 0 || tagStart > braceIndex)
            return false;

        var tagEnd = rawText.IndexOf('>', tagStart);
        if (tagEnd >= 0 && tagEnd < braceIndex)
            return false;

        var tagSlice = rawText[tagStart..braceIndex];
        if (tagSlice.IndexOf("<x:Type", StringComparison.OrdinalIgnoreCase) >= 0
            || tagSlice.IndexOf("<x:TypeExtension", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        if (tagSlice.IndexOf("TargetType=", StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        return false;
    }

    private static void AddXamlStaticMemberTypeSymbols(
        long fileId,
        string rawText,
        string[] lines,
        int[] lineStarts,
        List<SymbolRecord> symbols)
    {
        var cursor = 0;
        while (cursor < rawText.Length)
        {
            var braceIndex = rawText.IndexOf("{x:Static", cursor, StringComparison.Ordinal);
            if (braceIndex < 0)
                break;

            var closingBraceIndex = FindMatchingBrace(rawText, braceIndex);
            if (closingBraceIndex < 0)
            {
                cursor = braceIndex + 1;
                continue;
            }

            var value = NormalizeXamlMarkupValue(rawText[braceIndex..(closingBraceIndex + 1)]);
            var lastDot = value.LastIndexOf('.');
            if (lastDot > 0)
            {
                var typeName = value[..lastDot].Trim();
                if (typeName.Length > 0)
                {
                    var startLine = FindHtmlLineNumber(lineStarts, braceIndex);
                    var signatureIndex = Math.Clamp(startLine - 1, 0, lines.Length - 1);
                    symbols.Add(new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = "class",
                        Name = typeName,
                        Line = startLine,
                        StartLine = startLine,
                        EndLine = startLine,
                        Signature = lines[signatureIndex].Trim(),
                    });
                }
            }

            cursor = closingBraceIndex + 1;
        }
    }

    private static void AddXamlReferenceSymbols(
        long fileId,
        string rawText,
        string[] lines,
        int[] lineStarts,
        List<SymbolRecord> symbols)
    {
        foreach (var prefix in XamlReferenceMarkupPrefixes)
            AddXamlReferenceMarkupSymbols(fileId, rawText, lines, lineStarts, symbols, prefix);

        foreach (var prefix in XamlReferenceObjectElementPrefixes)
            AddXamlReferenceObjectElementSymbols(fileId, rawText, lines, lineStarts, symbols, prefix);

        foreach (Match nameMatch in XamlReferenceNamePropertyElementRegex.Matches(rawText))
        {
            var value = NormalizeXamlElementReferenceValue(nameMatch.Groups["value"].Value);
            if (value.Length == 0)
                continue;

            AddXamlAttributeSymbol(fileId, lines, lineStarts, symbols, nameMatch.Index, "property", value);
        }
    }

    private static void AddXamlReferenceMarkupSymbols(
        long fileId,
        string rawText,
        string[] lines,
        int[] lineStarts,
        List<SymbolRecord> symbols,
        string prefix)
    {
        var cursor = 0;
        while (cursor < rawText.Length)
        {
            var braceIndex = rawText.IndexOf(prefix, cursor, StringComparison.Ordinal);
            if (braceIndex < 0)
                break;

            var afterPrefix = braceIndex + prefix.Length;
            if (afterPrefix < rawText.Length && IsXamlMarkupNameChar(rawText[afterPrefix]))
            {
                cursor = afterPrefix;
                continue;
            }

            var closingBraceIndex = FindMatchingBrace(rawText, braceIndex);
            if (closingBraceIndex < 0)
            {
                cursor = braceIndex + 1;
                continue;
            }

            var value = NormalizeXamlRequiredMarkupArgumentValue(rawText[braceIndex..(closingBraceIndex + 1)]);
            if (value.Length > 0)
                AddXamlAttributeSymbol(fileId, lines, lineStarts, symbols, braceIndex, "property", value);

            cursor = closingBraceIndex + 1;
        }
    }

    private static void AddXamlReferenceObjectElementSymbols(
        long fileId,
        string rawText,
        string[] lines,
        int[] lineStarts,
        List<SymbolRecord> symbols,
        string prefix)
    {
        var cursor = 0;
        while (cursor < rawText.Length)
        {
            var tagIndex = rawText.IndexOf(prefix, cursor, StringComparison.Ordinal);
            if (tagIndex < 0)
                break;

            var nameEnd = tagIndex + prefix.Length;
            if (nameEnd < rawText.Length && IsXamlAttributeNameChar(rawText[nameEnd]))
            {
                cursor = nameEnd;
                continue;
            }

            var tagEnd = FindXamlTagEnd(rawText, nameEnd);
            if (tagEnd < 0)
                break;

            var nameAttributeIndex = IndexOfXamlAttributeInRange(rawText, "Name", nameEnd, tagEnd);
            if (nameAttributeIndex >= 0
                && TryReadXamlAttributeValue(rawText, "Name", nameAttributeIndex, out var valueStart, out var valueEnd)
                && valueEnd <= tagEnd)
            {
                AddXamlAttributeSymbol(
                    fileId,
                    lines,
                    lineStarts,
                    symbols,
                    nameAttributeIndex,
                    "property",
                    NormalizeXamlElementReferenceValue(rawText[valueStart..valueEnd]));
            }

            cursor = tagEnd + 1;
        }
    }

    private static void AddXamlResourceReferenceSymbols(
        long fileId,
        string rawText,
        string[] lines,
        int[] lineStarts,
        List<SymbolRecord> symbols)
    {
        foreach (var prefix in XamlResourceReferenceMarkupPrefixes)
            AddXamlResourceReferenceSymbols(fileId, rawText, lines, lineStarts, symbols, prefix);
    }

    private static void AddXamlResourceReferenceSymbols(
        long fileId,
        string rawText,
        string[] lines,
        int[] lineStarts,
        List<SymbolRecord> symbols,
        string prefix)
    {
        var cursor = 0;
        while (cursor < rawText.Length)
        {
            var braceIndex = rawText.IndexOf(prefix, cursor, StringComparison.Ordinal);
            if (braceIndex < 0)
                break;

            var afterPrefix = braceIndex + prefix.Length;
            if (afterPrefix < rawText.Length && IsXamlMarkupNameChar(rawText[afterPrefix]))
            {
                cursor = afterPrefix;
                continue;
            }

            var closingBraceIndex = FindMatchingBrace(rawText, braceIndex);
            if (closingBraceIndex < 0)
            {
                cursor = braceIndex + 1;
                continue;
            }

            var value = NormalizeXamlResourceReferenceValue(rawText[braceIndex..(closingBraceIndex + 1)]);
            if (value.Length > 0)
            {
                var startLine = FindHtmlLineNumber(lineStarts, braceIndex);
                var signatureIndex = Math.Clamp(startLine - 1, 0, lines.Length - 1);
                symbols.Add(new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "property",
                    Name = value,
                    Line = startLine,
                    StartLine = startLine,
                    EndLine = startLine,
                    Signature = lines[signatureIndex].Trim(),
                });
            }

            cursor = closingBraceIndex + 1;
        }
    }

    private static string NormalizeXamlResourceReferenceValue(string value)
        => NormalizeXamlRequiredMarkupArgumentValue(value);

    private static string NormalizeXamlRequiredMarkupArgumentValue(string value)
    {
        value = value.Trim();
        if (value.Length == 0 || value[0] != '{')
            return value;

        var closingBraceIndex = FindMatchingBrace(value, 0);
        if (closingBraceIndex < 0)
            return "";

        var content = value[1..closingBraceIndex].Trim();
        var payloadStart = FindTopLevelMarkupPayloadStart(content);
        if (payloadStart < 0)
            return "";

        var payload = content[(payloadStart + 1)..].TrimStart();
        if (payload.Length == 0)
            return "";

        foreach (var argument in SplitTopLevelMarkupArguments(payload))
        {
            var normalized = NormalizeXamlMarkupArgument(argument);
            if (normalized.Length > 0)
                return normalized;
        }

        return "";
    }

    private static bool IsXamlMarkupNameChar(char c)
        => char.IsLetterOrDigit(c) || c == '_' || c == ':' || c == '.';

    private static string NormalizeXamlKeyValue(string value)
    {
        value = value.Trim();
        if (value.Length < 2 || value[0] != '{' || value[^1] != '}')
            return value;

        return NormalizeXamlMarkupExtensionContent(value[1..^1].Trim());
    }

    private static string NormalizeXamlBindingValue(string kind, string content)
    {
        kind = kind.Trim();
        content = content.Trim();
        if (content.Length == 0)
            return content;

        var isTemplateBinding = kind.Equals("TemplateBinding", StringComparison.OrdinalIgnoreCase);
        var payload = kind.Equals("x:Bind", StringComparison.OrdinalIgnoreCase)
            ? $"x:Bind {content}"
            : isTemplateBinding
                ? $"TemplateBinding {content}"
                : $"Binding {content}";

        var firstPath = NormalizeXamlBindingPath(payload, isTemplateBinding);
        return firstPath.Length > 0 ? firstPath : content;
    }

    private static string NormalizeXamlBindingPath(string value, bool allowPropertyArgument)
    {
        value = value.Trim();
        if (value.Length == 0)
            return value;

        var payloadStart = FindTopLevelMarkupPayloadStart(value);
        if (payloadStart < 0)
            return value;

        var payload = value[(payloadStart + 1)..].TrimStart();
        if (payload.Length == 0)
            return value;

        string? fallback = null;
        foreach (var argument in SplitTopLevelMarkupArguments(payload))
        {
            var equalsIndex = IndexOfTopLevelEquals(argument);
            if (equalsIndex >= 0)
            {
                var argumentName = argument[..equalsIndex].Trim();
                if (string.Equals(argumentName, "Path", StringComparison.OrdinalIgnoreCase)
                    || (allowPropertyArgument && string.Equals(argumentName, "Property", StringComparison.OrdinalIgnoreCase)))
                {
                    var pathValue = NormalizeXamlBindingPathValue(argument[(equalsIndex + 1)..]);
                    if (pathValue.Length > 0)
                        return pathValue;
                }

                continue;
            }

            var normalized = NormalizeXamlBindingArgument(argument);
            if (normalized.Length == 0)
                continue;

            fallback ??= normalized;
        }

        return fallback ?? value;
    }

    private static string NormalizeXamlBindingArgument(string value)
    {
        value = value.Trim();
        if (value.Length == 0)
            return value;

        var equalsIndex = IndexOfTopLevelEquals(value);
        if (equalsIndex >= 0)
        {
            var name = value[..equalsIndex].Trim();
            var normalized = value[(equalsIndex + 1)..].Trim();
            if (string.Equals(name, "Path", StringComparison.OrdinalIgnoreCase))
                return NormalizeXamlBindingPathValue(normalized);
            if (normalized.Length > 0)
                return NormalizeXamlMarkupValue(normalized);
        }

        return NormalizeXamlBindingPathValue(value);
    }

    private static string NormalizeXamlBindingPathValue(string value)
    {
        value = value.Trim();
        if (value.Length == 0)
            return value;

        value = NormalizeXamlMarkupValue(value);
        var lastDot = value.LastIndexOf('.');
        if (lastDot >= 0 && lastDot + 1 < value.Length)
            value = value[(lastDot + 1)..];

        return value.Trim();
    }

    private static string NormalizeXamlMarkupValue(string value)
    {
        value = value.Trim();
        if (value.Length == 0 || value[0] != '{')
            return value;

        var closingBraceIndex = FindMatchingBrace(value, 0);
        if (closingBraceIndex < 0)
            return value;

        var normalized = NormalizeXamlMarkupExtensionContent(value[1..closingBraceIndex].Trim());
        var suffix = value[(closingBraceIndex + 1)..].Trim();
        return suffix.Length == 0 ? normalized : normalized + suffix;
    }

    private static string NormalizeXamlMarkupExtensionContent(string value)
    {
        value = value.Trim();
        if (value.Length == 0)
            return value;

        var payloadStart = FindTopLevelMarkupPayloadStart(value);
        if (payloadStart < 0)
            return value;

        var payload = value[(payloadStart + 1)..].TrimStart();
        if (payload.Length == 0)
            return value;

        foreach (var argument in SplitTopLevelMarkupArguments(payload))
        {
            var normalized = NormalizeXamlMarkupArgument(argument);
            if (normalized.Length > 0)
                return normalized;
        }

        return value;
    }

    private static IEnumerable<string> NormalizeXamlTypeArgumentsValue(string value)
    {
        value = value.Trim();
        if (value.Length == 0)
            yield break;

        foreach (var argument in SplitTopLevelTypeArguments(value))
        {
            var normalized = NormalizeXamlMarkupArgument(argument);
            if (normalized.Length > 0)
            {
                foreach (var expanded in ExpandXamlTypeArgument(normalized))
                    yield return expanded;
            }
        }
    }

    private static IEnumerable<string> ExpandXamlTypeArgument(string value)
    {
        // Peel nested generic constructor shapes recursively so XAML type arguments like
        // `Outer(Inner(A, B), C)` still surface every referenced type name.
        value = value.Trim();
        if (value.Length == 0)
            yield break;

        var payloadStart = FindTopLevelTypeConstructorStart(value);
        if (payloadStart < 0)
        {
            yield return value;
            yield break;
        }

        var payloadEnd = FindMatchingTypeConstructorEnd(value, payloadStart);
        if (payloadEnd < 0)
        {
            yield return value;
            yield break;
        }

        var prefix = value[..payloadStart].Trim();
        if (prefix.Length > 0)
            yield return prefix;

        var payload = value[(payloadStart + 1)..payloadEnd].Trim();
        if (payload.Length > 0)
        {
            foreach (var nestedArgument in SplitTopLevelTypeArguments(payload))
            {
                var nestedNormalized = NormalizeXamlMarkupArgument(nestedArgument);
                if (nestedNormalized.Length == 0)
                    continue;

                foreach (var nestedExpanded in ExpandXamlTypeArgument(nestedNormalized))
                    yield return nestedExpanded;
            }
        }

        var suffix = value[(payloadEnd + 1)..].Trim();
        if (suffix.Length > 0)
        {
            foreach (var expanded in ExpandXamlTypeArgument(suffix))
                yield return expanded;
        }
    }

    private static string NormalizeXamlMarkupArgument(string value)
    {
        value = value.Trim();
        if (value.Length == 0)
            return value;

        var equalsIndex = IndexOfTopLevelEquals(value);
        if (equalsIndex >= 0)
            return NormalizeXamlMarkupValue(value[(equalsIndex + 1)..].Trim());

        return NormalizeXamlMarkupValue(value);
    }

    private static int FindTopLevelMarkupPayloadStart(string value)
    {
        var braceDepth = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch == '{')
            {
                braceDepth++;
                continue;
            }
            if (ch == '}')
            {
                if (braceDepth > 0)
                    braceDepth--;
                continue;
            }
            if (braceDepth == 0 && (char.IsWhiteSpace(ch) || ch == ','))
                return i;
        }

        return -1;
    }

    private static int FindTopLevelTypeConstructorStart(string value)
    {
        var braceDepth = 0;
        var parenDepth = 0;
        var angleDepth = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch == '{')
            {
                braceDepth++;
                continue;
            }
            if (ch == '}')
            {
                if (braceDepth > 0)
                    braceDepth--;
                continue;
            }
            if (ch == '(' || ch == '<')
            {
                if (braceDepth == 0 && parenDepth == 0 && angleDepth == 0)
                    return i;

                if (ch == '(')
                    parenDepth++;
                else
                    angleDepth++;
                continue;
            }
            if (ch == ')')
            {
                if (parenDepth > 0)
                    parenDepth--;
                continue;
            }
            if (ch == '>')
            {
                if (angleDepth > 0)
                    angleDepth--;
                continue;
            }
        }

        return -1;
    }

    private static int FindMatchingTypeConstructorEnd(string value, int startIndex)
    {
        var open = value[startIndex];
        var close = open == '(' ? ')' : '>';
        var depth = 0;
        var braceDepth = 0;
        for (var i = startIndex; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch == '{')
            {
                braceDepth++;
                continue;
            }
            if (ch == '}')
            {
                if (braceDepth > 0)
                    braceDepth--;
                continue;
            }
            if (braceDepth > 0)
                continue;

            if (ch == open)
            {
                depth++;
                continue;
            }
            if (ch == close)
            {
                depth--;
                if (depth == 0)
                    return i;
            }
        }

        return -1;
    }

    private static int IndexOfTopLevelEquals(string value)
    {
        var braceDepth = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch == '{')
            {
                braceDepth++;
                continue;
            }
            if (ch == '}')
            {
                if (braceDepth > 0)
                    braceDepth--;
                continue;
            }
            if (braceDepth == 0 && ch == '=')
                return i;
        }

        return -1;
    }

    private static IEnumerable<string> SplitTopLevelMarkupArguments(string value)
    {
        var braceDepth = 0;
        char? quote = null;
        var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (quote is { } activeQuote)
            {
                if (ch == activeQuote)
                    quote = null;
                continue;
            }
            if (ch is '"' or '\'')
            {
                quote = ch;
                continue;
            }
            if (ch == '{')
            {
                braceDepth++;
                continue;
            }
            if (ch == '}')
            {
                if (braceDepth > 0)
                    braceDepth--;
                continue;
            }
            if (braceDepth == 0 && ch == ',')
            {
                var segment = value[start..i].Trim();
                if (segment.Length > 0)
                    yield return segment;
                start = i + 1;
            }
        }

        var tail = value[start..].Trim();
        if (tail.Length > 0)
            yield return tail;
    }

    private static IEnumerable<string> SplitTopLevelTypeArguments(string value)
    {
        var braceDepth = 0;
        var parenDepth = 0;
        var angleDepth = 0;
        var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch == '{')
            {
                braceDepth++;
                continue;
            }
            if (ch == '}')
            {
                if (braceDepth > 0)
                    braceDepth--;
                continue;
            }
            if (ch == '(')
            {
                parenDepth++;
                continue;
            }
            if (ch == ')')
            {
                if (parenDepth > 0)
                    parenDepth--;
                continue;
            }
            if (ch == '<')
            {
                angleDepth++;
                continue;
            }
            if (ch == '>')
            {
                if (angleDepth > 0)
                    angleDepth--;
                continue;
            }
            if (braceDepth == 0 && parenDepth == 0 && angleDepth == 0 && ch == ',')
            {
                var segment = value[start..i].Trim();
                if (segment.Length > 0)
                    yield return segment;
                start = i + 1;
            }
        }

        var tail = value[start..].Trim();
        if (tail.Length > 0)
            yield return tail;
    }

    private static int FindMatchingBrace(string value, int startIndex)
    {
        var braceDepth = 0;
        for (var i = startIndex; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch == '{')
            {
                braceDepth++;
                continue;
            }
            if (ch == '}')
            {
                braceDepth--;
                if (braceDepth == 0)
                    return i;
            }
        }

        return -1;
    }

    private static bool IsHtmlTagNameStart(char c) => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

    private static bool IsHtmlTagNameChar(char c) =>
        (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_';

    private static bool IsHtmlAttrNameStart(char c) =>
        (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_' || c == ':';

    private static bool IsHtmlAttrNameChar(char c) =>
        IsHtmlAttrNameStart(c) || (c >= '0' && c <= '9') || c == '-' || c == '.';

    private static bool IsHtmlUnquotedValueTerminator(char c) =>
        char.IsWhiteSpace(c) || c == '"' || c == '\'' || c == '=' || c == '<' || c == '>' || c == '`';

    private static int IndexOfOrEnd(string text, char needle, int start)
    {
        var idx = text.IndexOf(needle, start);
        return idx < 0 ? text.Length : idx;
    }

    private static int FindHtmlLineNumber(int[] lineStarts, int offset)
    {
        if (lineStarts.Length == 0)
            return 1;
        var lo = 0;
        var hi = lineStarts.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (lineStarts[mid] <= offset)
                lo = mid;
            else
                hi = mid - 1;
        }
        return lo + 1;
    }

    private static readonly Regex MarkdownLocalAnchorLinkRegex = new(@"(?<!\!)\[[^\]]+\]\(\s*(?<target>#[^) \t]+)\s*\)", RegexOptions.Compiled);
    private static readonly Regex MarkdownLocalAnchorReferenceRegex = new(@"(?<!\!)\[[^\]]+\]:\s*(?<target>#[^\s>]+)", RegexOptions.Compiled);
    private static readonly Regex MarkdownReferenceDefinitionRegex = new(@"^\s{0,3}\[(?<label>[^\]]+)\]:\s*(?<target>.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex MarkdownReferenceLinkRegex = new(@"(?<!\!)\[[^\]]+\]\[(?<label>[^\]]*)\]", RegexOptions.Compiled);

    private static readonly HashSet<string> HtmlRawTextElementNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "textarea", "title",
    };

    // Native HTML/SVG/MathML tag names that happen to contain a hyphen but are
    // reserved by the spec, so they must NOT be treated as custom-element class
    // symbols. See https://html.spec.whatwg.org/multipage/custom-elements.html#valid-custom-element-name
    // for the PotentialCustomElementName / reserved names production.
    // ハイフンを含むが仕様で予約されている標準 HTML / SVG / MathML タグ名。custom
    // element の class シンボルとして扱ってはいけない。
    private static readonly HashSet<string> HtmlReservedHyphenatedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "annotation-xml",
        "color-profile",
        "font-face",
        "font-face-src",
        "font-face-uri",
        "font-face-format",
        "font-face-name",
        "missing-glyph",
    };

    private static bool IsHtmlSrcResourceTag(string tagNameLower) =>
        tagNameLower is "audio" or "embed" or "iframe" or "img" or "input" or "script" or "source" or "track" or "video";

    private static bool IsHtmlHrefResourceTag(string tagNameLower) =>
        tagNameLower is "a" or "area" or "image" or "link" or "use";

    private static bool IsHtmlSrcsetResourceTag(string tagNameLower) =>
        tagNameLower is "img" or "source";

    private static IEnumerable<string> EnumerateHtmlSrcsetUrls(string value)
    {
        var index = 0;
        while (index < value.Length)
        {
            while (index < value.Length && (char.IsWhiteSpace(value[index]) || value[index] == ','))
                index++;

            if (index >= value.Length)
                yield break;

            var start = index;
            var isDataUrl = value.AsSpan(index).StartsWith("data:", StringComparison.OrdinalIgnoreCase);
            if (isDataUrl)
            {
                index += "data:".Length;
                while (index < value.Length)
                {
                    if (char.IsWhiteSpace(value[index]))
                        break;
                    if (value[index] == ',' && (index + 1 >= value.Length || char.IsWhiteSpace(value[index + 1])))
                        break;
                    index++;
                }
            }
            else
            {
                while (index < value.Length && !char.IsWhiteSpace(value[index]) && value[index] != ',')
                    index++;
            }

            var url = value[start..index].Trim();
            if (url.Length > 0)
                yield return url;

            while (index < value.Length && value[index] != ',')
                index++;

            if (index < value.Length && value[index] == ',')
                index++;
        }
    }

    private static string MaskHtmlRawTextRegions(string text)
    {
        // Walk `text` character by character, masking the body of raw-text /
        // RCDATA elements (`<script>` / `<style>` / `<textarea>` / `<title>`)
        // and `<!-- ... -->` comments. Regex-based masking could not reliably
        // handle cases like `<script data-note="a > b" src="/app.js">` (quoted
        // `>` inside an attribute terminated the naive `[^>]*` pattern) or
        // `<script data-note="oops\nconst tpl = '<evil-card id="phantom">';`
        // (unterminated quote let nested `"..."` pairs match across script
        // body content). The state machine uses the same quote-handling logic
        // as the symbol extractor's state machine so both agree on where a
        // raw-text opener ends, and falls back to masking through EOF when an
        // opener is unterminated — that matches HTML's spec behavior (an
        // unclosed raw-text element swallows everything until EOF or
        // `</name>`) and prevents script-body content from leaking as phantom
        // HTML symbols.
        // マスクを正規表現ではなく文字単位の state machine で行い、`<script>` /
        // `<style>` / `<textarea>` / `<title>` の本体と `<!-- ... -->` コメントを
        // マスクする。正規表現だと `<script data-note="a > b" src="/app.js">` の
        // ように属性値内の引用符付き `>` で早期終了したり、未終端引用符を持つ
        // `<script data-note="oops\nconst tpl = '<evil-card id="phantom">';`
        // のような入力で引用符ペアが script 本体をまたいで誤マッチする問題が
        // あった。state machine は symbol extractor と同じ引用符処理を共有して
        // 開始タグの境界を一致させ、開始タグが未終端の場合は EOF までマスクする
        // （仕様上、未閉鎖 raw-text 要素は EOF か `</name>` まで本体を飲むため）。
        var chars = text.ToCharArray();
        var i = 0;
        while (i < chars.Length)
        {
            if (chars[i] != '<')
            {
                i++;
                continue;
            }

            // `<!-- ... -->` comment. Closing `-->` is optional (masked through
            // EOF) so mid-edit working-tree HTML with an unclosed comment does
            // not leak following tags as phantom symbols.
            // 未閉鎖コメントは EOF までマスクし、以降のタグが phantom にならないようにする。
            if (i + 3 < chars.Length && chars[i + 1] == '!' && chars[i + 2] == '-' && chars[i + 3] == '-')
            {
                var commentClose = text.IndexOf("-->", i + 4, StringComparison.Ordinal);
                var commentEnd = commentClose < 0 ? chars.Length : commentClose + 3;
                BlankPreservingNewlines(chars, i, commentEnd);
                i = commentEnd;
                continue;
            }

            // `<![CDATA[ ... ]]>` section. In XHTML / SVG / MathML these are
            // valid and must not leak their content as phantom tags. The
            // terminator is specifically `]]>`, not the first `>`, so a naive
            // `IndexOf('>', ...)` would stop early on inner markup and let the
            // remaining CDATA body be parsed as real HTML. Unterminated CDATA
            // masks through EOF, matching the comment-branch behavior.
            // `<![CDATA[ ... ]]>` は XHTML / SVG / MathML で有効。終端は
            // `]]>` のみであり、単純な `>` 検索では内部のタグで早期終了して
            // 残り本体が phantom として抽出される。未閉鎖は EOF までマスクする。
            if (i + 8 < chars.Length && chars[i + 1] == '!' && chars[i + 2] == '[' &&
                chars[i + 3] == 'C' && chars[i + 4] == 'D' && chars[i + 5] == 'A' &&
                chars[i + 6] == 'T' && chars[i + 7] == 'A' && chars[i + 8] == '[')
            {
                var cdataClose = text.IndexOf("]]>", i + 9, StringComparison.Ordinal);
                var cdataEnd = cdataClose < 0 ? chars.Length : cdataClose + 3;
                BlankPreservingNewlines(chars, i, cdataEnd);
                i = cdataEnd;
                continue;
            }

            // Other `<!...>` declarations (DOCTYPE and similar). Content
            // between `<!` and the first unquoted `>` is a declaration, not a
            // tag body, so mask it to prevent attribute-lookalike tokens from
            // being emitted as symbols. Quoted values inside DOCTYPE PUBLIC /
            // SYSTEM are walked via FindHtmlQuoteClose so embedded `>` does
            // not terminate the declaration early.
            // DOCTYPE などの `<!...>` 宣言は `FindHtmlTagOpenerEnd` で閉じ `>` を
            // 探して丸ごとマスクする。引用符内の `>` で早期終了しないようにする。
            if (i + 1 < chars.Length && chars[i + 1] == '!')
            {
                var declEnd = FindHtmlTagOpenerEnd(text, i);
                if (declEnd < 0)
                {
                    BlankPreservingNewlines(chars, i, chars.Length);
                    i = chars.Length;
                    continue;
                }
                BlankPreservingNewlines(chars, i, declEnd + 1);
                i = declEnd + 1;
                continue;
            }

            // Processing instructions `<?...?>` (XML prolog, XSLT PIs, PHP
            // short tags embedded in XHTML). Terminator is `?>`, not bare `>`.
            // Content between can include tag-like markup that must not leak.
            // `<?...?>` 処理命令。終端は `?>` で、内部のタグ様テキストは漏らさない。
            if (i + 1 < chars.Length && chars[i + 1] == '?')
            {
                var piClose = text.IndexOf("?>", i + 2, StringComparison.Ordinal);
                var piEnd = piClose < 0 ? chars.Length : piClose + 2;
                BlankPreservingNewlines(chars, i, piEnd);
                i = piEnd;
                continue;
            }

            var rawName = TryMatchHtmlRawTextOpenerName(text, i);
            if (rawName != null)
            {
                // Walk the opening tag to find its closing `>`. Multi-line
                // quoted attribute values are allowed; the helper only returns
                // -1 if the opener cannot be closed before EOF.
                // 開始タグの `>` を探す。複数行に跨る引用符付き属性値は OK。
                // EOF 前に閉じられない場合のみ -1 を返す。
                var openerEnd = FindHtmlTagOpenerEnd(text, i);
                if (openerEnd < 0)
                {
                    // Unterminated raw-text opener. Mask from `<` to EOF — this
                    // matches HTML spec behavior and prevents script-body
                    // content from leaking as phantom symbols.
                    // 開始タグが未終端の場合、仕様どおり EOF までマスクする。
                    BlankPreservingNewlines(chars, i, chars.Length);
                    i = chars.Length;
                    continue;
                }

                var bodyStart = openerEnd + 1;
                var closeIdx = FindHtmlRawTextClose(text, bodyStart, rawName);
                var bodyEnd = closeIdx < 0 ? chars.Length : closeIdx;
                BlankPreservingNewlines(chars, bodyStart, bodyEnd);

                if (closeIdx < 0)
                {
                    i = chars.Length;
                    continue;
                }

                var closeGt = text.IndexOf('>', closeIdx);
                i = closeGt < 0 ? chars.Length : closeGt + 1;
                continue;
            }

            // Non-raw-text tag opener (including closing tags `</...`). Walk
            // past the whole opener so quoted attribute values like
            // `<div title="<script>">` or `<div title="<!--">` do not re-enter
            // the raw-text / comment branches on the next character and get
            // misidentified as raw-text/comment openers. Without this skip,
            // the char-by-char scan would re-encounter `<script>` / `<!--`
            // inside the attribute value and mask through EOF.
            // raw-text 以外のタグ opener（`</...` を含む）に遭遇したら、opener 全体を
            // 飛ばして属性値内の `<script>` / `<!--` が次の文字で raw-text / comment
            // として再解釈されないようにする。これを入れないと属性値内の `<script>`
            // を raw-text 本体マスク対象と誤認して以降の兄弟タグを全部飲み込む。
            if (i + 1 < chars.Length && (IsHtmlTagNameStart(chars[i + 1]) || chars[i + 1] == '/'))
            {
                var openerEnd = FindHtmlTagOpenerEnd(text, i);
                if (openerEnd >= 0)
                {
                    i = openerEnd + 1;
                    continue;
                }

                // Unterminated non-raw-text tag opener (mid-edit quoted attribute
                // like `<div title="<!--` or `<div title="<script>`). Advance
                // past the current line so the `<!--` / `<script>` inside the
                // broken quoted value is not re-encountered on the very next
                // character and misidentified as a real comment / raw-text
                // opener that would mask through EOF. Sibling tags on later
                // lines still get their chance to be walked.
                // 未終端の non-raw-text タグ opener（`<div title="<!--` のような
                // 編集途中の引用属性）に遭遇した場合、`i++` で戻ると引用値内の
                // `<!--` / `<script>` が次文字で comment / raw-text opener として
                // 再解釈されて EOF までマスクされるため、現在行末まで一気に進めて
                // 次行以降の兄弟タグを拾えるようにする。
                var eolIdx = text.IndexOf('\n', i);
                i = eolIdx < 0 ? chars.Length : eolIdx + 1;
                continue;
            }

            i++;
        }
        return new string(chars);
    }

    private static string? TryMatchHtmlRawTextOpenerName(string text, int start)
    {
        // Check if `text[start]` (must be `<`) begins `<script` / `<style` /
        // `<textarea` / `<title` followed by a non-tag-name-char (so `<scriptx`
        // is NOT matched as `<script`).
        // `start` は `<` の位置。`<script` / `<style` / `<textarea` / `<title`
        // に続く文字がタグ名文字でないもののみ一致させる（`<scriptx` は除外）。
        foreach (var name in HtmlRawTextElementNames)
        {
            var nameStart = start + 1;
            if (nameStart + name.Length > text.Length)
                continue;
            var match = true;
            for (var j = 0; j < name.Length; j++)
            {
                if (char.ToLowerInvariant(text[nameStart + j]) != name[j])
                {
                    match = false;
                    break;
                }
            }
            if (!match)
                continue;
            var after = nameStart + name.Length;
            if (after >= text.Length || !IsHtmlTagNameChar(text[after]))
                return name;
        }
        return null;
    }

    private static int FindHtmlTagOpenerEnd(string text, int start)
    {
        // Walk from `start` (position of `<`) forward to find the opening `>`,
        // skipping over quoted attribute values. Multi-line quoted values are
        // allowed per HTML5 spec.
        // `start` は `<` の位置。引用符付き属性値を `FindHtmlQuoteClose` で飛ばしつつ
        // 開始タグの閉じ `>` を探す。HTML5 仕様どおり複数行値も許容する。
        var i = start + 1;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '>')
                return i;
            if (c == '"' || c == '\'')
            {
                var closeIdx = FindHtmlQuoteClose(text, i + 1, c);
                if (closeIdx < 0)
                    return -1;
                i = closeIdx + 1;
                continue;
            }
            i++;
        }
        return -1;
    }

    private static int FindHtmlQuoteClose(string text, int start, char quote)
    {
        // Scan forward for the matching closing quote. HTML5 allows newlines
        // inside quoted attribute values (`<meta description="line1\nline2">`)
        // and tag-like content (`<div title="<section id=x>">`), so we cross
        // line boundaries and tag-name-like bytes without bailing. A quote is
        // accepted as the close when it has "strong valid" post-value context:
        // per HTML5 the char immediately after a quoted attribute value must
        // be whitespace, `>`, or EOF. `/` alone is intentionally excluded from
        // strong context — a `/` following a quote is ambiguous between the
        // self-closing marker (`attr="v"/>`) and the opening `"` of a later
        // path-like attribute (`href="/app.css"`). Accepting bare `/` would
        // let an earlier unterminated `title="...` silently steal the opening
        // quote of `href="/app.css"` and swallow every sibling tag between
        // them. The self-closing form `"/>` IS accepted (ambiguity gone —
        // the `/` is followed by `>`), so void-element tags like
        // `<link href="/app.css"/>` still close cleanly without triggering
        // the nested-attribute fallback on the following sibling tag.
        //
        // When a non-strong `"` is encountered and it matches an "attribute-
        // start" pattern (preceded by `[attr-name-chars]+=` with whitespace
        // before the ident), the scanner treats it as a nested attribute
        // opening: it walks past that attribute's value (finding the matching
        // inner quote) and resumes scanning, instead of mis-taking the inner
        // opening for our close. This preserves strict-HTML5 behavior on
        // well-formed multi-line quoted values (they contain no spurious
        // `ident="` patterns) while keeping mid-edit resilience — if the
        // outer quote is truly unterminated, we'll walk through all nested
        // attributes without finding a strong close, and return -1 so the
        // attribute parser can bail at EOL and recover sibling tags on the
        // next lines.
        //
        // If neither a strong close nor a nested pattern is ever seen, fall
        // back to the first bare `"` candidate (matches spec tokenizer
        // recovery for malformed content like `<div id="foo"bar>`). If nested
        // patterns WERE seen but no strong close was found, return -1 to
        // signal the attribute is effectively unterminated for our purposes.
        //
        // 閉じ引用符を探す。HTML5 は属性値内の改行とタグ様テキストを許容するため、
        // 改行やタグ様の文字では早期中断しない。引用符を閉じとして採用する条件は、
        // 直後が空白 / `>` / EOF の「strong な属性値終端」であること。`/` は
        // self-closing (`attr="v"/>`) と後続属性の開始引用符 (`href="/app.css"`)
        // の区別が文脈無しでは付かないため、`/` 単独は strong には含めない。
        // bare `/` を許容すると、未終端の `title="...` が後続 `href="/app.css"`
        // の開き `"` を奪って兄弟タグを丸呑みする。
        //
        // strong でない `"` が「属性開始パターン」(`[attr-name-chars]+=` の前が
        // 空白) にマッチしたら、それは nested な属性開始と判断し、その属性の値を
        // 次の引用符まで飛ばして外側 scan を再開する。これにより Blocker 2
        // (`<div title="line1\n<section></section>\nline3" id="real">`) のような
        // 真に妥当な複数行引用属性値は strong 終端まで到達して通り、一方で未終端な
        // 外側 `"` は nested を何個かスキップしても strong 終端に到達せず、最終的に
        // -1 を返して属性パーサが EOL で bail → 次行以降の兄弟タグを拾える。
        //
        // strong 終端にも nested にも該当しない `"` は弱い候補として記録し、EOF
        // 到達時に nested を見ていなければ fallback として返す（`<div id="foo"bar>`
        // のような malformed でも spec に近い形で拾う）。nested を見ていれば -1 を
        // 返して、未終端扱いにする。
        var firstCandidate = -1;
        var sawNested = false;
        var i = start;
        while (i < text.Length)
        {
            if (text[i] == quote)
            {
                var after = i + 1;
                if (after >= text.Length)
                    return i;
                var nextCh = text[after];
                if (nextCh == '>' || char.IsWhiteSpace(nextCh))
                    return i;
                // Accept the XML-style self-closing marker `"/>` as strong
                // post-context. Bare `/` is still rejected because it cannot
                // be distinguished from a path-like `href="/app.css"` opener.
                // 自己閉鎖タグの `"/>` は strong として受理する。bare `/` は
                // `href="/app.css"` の開きとの区別が付かないため受理しない。
                if (nextCh == '/' && after + 1 < text.Length && text[after + 1] == '>')
                    return i;

                if (IsPrecededByHtmlAttributeStart(text, i, start))
                {
                    sawNested = true;
                    var inner = i + 1;
                    while (inner < text.Length && text[inner] != quote)
                        inner++;
                    if (inner >= text.Length)
                        break;
                    i = inner + 1;
                    continue;
                }

                if (firstCandidate < 0)
                    firstCandidate = i;
            }
            i++;
        }
        if (sawNested)
            return -1;
        return firstCandidate;
    }

    private static bool IsPrecededByHtmlAttributeStart(string text, int quotePos, int scanStart)
    {
        // Return true if the characters immediately before `quotePos` form a
        // `[attr-name-chars]+=` pattern AND the ident is preceded by whitespace
        // within the current scan — i.e. it looks like the start of a new
        // attribute inside an outer quoted value. This is the signal that the
        // `"` is more likely a nested attribute opening than the true close of
        // the outer value.
        // `quotePos` の直前が `[attr-name-chars]+=` で、その ident の前が
        // scan 範囲内の空白文字なら true。外側引用値の中で新しい属性が
        // 始まっているパターンと判定する。
        if (quotePos <= scanStart)
            return false;
        if (text[quotePos - 1] != '=')
            return false;
        var j = quotePos - 2;
        var identEnd = j + 1;
        while (j >= scanStart && IsHtmlAttrNameChar(text[j]))
            j--;
        if (j + 1 >= identEnd)
            return false;
        if (j < scanStart)
            return false;
        return char.IsWhiteSpace(text[j]);
    }

    private static int FindHtmlRawTextClose(string text, int start, string tagName)
    {
        // Locate the next `</tagName` (case-insensitive) at or after `start`.
        // Returns the position of `<`, or -1 if none.
        // `</tagName` を大文字小文字非区別で `start` 以降から探し、`<` の位置を返す。
        var i = start;
        while (i < text.Length - tagName.Length - 2)
        {
            if (text[i] == '<' && text[i + 1] == '/')
            {
                var match = true;
                for (var j = 0; j < tagName.Length; j++)
                {
                    if (char.ToLowerInvariant(text[i + 2 + j]) != tagName[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                {
                    var after = i + 2 + tagName.Length;
                    if (after >= text.Length)
                        return i;
                    var nc = text[after];
                    if (nc == '>' || nc == '/' || char.IsWhiteSpace(nc))
                        return i;
                }
            }
            i++;
        }
        return -1;
    }

    private static void BlankPreservingNewlines(char[] chars, int start, int end)
    {
        var limit = Math.Min(end, chars.Length);
        for (var i = start; i < limit; i++)
        {
            if (chars[i] != '\n' && chars[i] != '\r')
                chars[i] = ' ';
        }
    }





}
