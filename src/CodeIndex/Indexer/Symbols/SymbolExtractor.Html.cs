using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using System.Runtime.CompilerServices;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{

    private static List<SymbolRecord> ExtractHtmlSymbols(long fileId, string rawText, string[] lines)
    {
        const string defaultSlotSymbolName = "(default)";

        if (!LinesContain(lines, '<'))
            return [];

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
        var maskedText = MayNeedHtmlRawTextMask(rawText)
            ? MaskHtmlRawTextRegions(rawText)
            : rawText;

        // Build per-line absolute offsets only once a symbol needs O(log n)
        // offset-to-line lookup. Plain markup with no emitted symbols can skip it.
        // シンボルが offset-to-line lookup を必要とする時だけ行ごとの絶対 offset を作る。
        // emit 対象のない plain markup では確保を避ける。
        int[]? lineStarts = null;

        List<SymbolRecord>? symbols = null;
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
            if (tagName.Contains('-') && !HtmlReservedHyphenatedTags.Contains(tagName))
            {
                var startLine = FindHtmlLineNumber(lineStarts ??= BuildLineStarts(lines), pos);
                var signatureIndex = Math.Clamp(startLine - 1, 0, lines.Length - 1);
                (symbols ??= []).Add(new SymbolRecord
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

                if (IsHtmlSemanticStateAttributeName(attrName))
                {
                    var attrStartLine = FindHtmlLineNumber(lineStarts ??= BuildLineStarts(lines), attrNameStart);
                    var attrSignatureIndex = Math.Clamp(attrStartLine - 1, 0, lines.Length - 1);
                    (symbols ??= []).Add(new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = "property",
                        Name = NormalizeHtmlAttributeName(attrName),
                        Line = attrStartLine,
                        StartLine = attrStartLine,
                        EndLine = attrStartLine,
                        Signature = lines[attrSignatureIndex].Trim(),
                    });
                }

                if (attrValue == null || attrValue.Length == 0)
                    continue;

                string? emitKind = null;
                string? singleEmittedName = null;
                IEnumerable<string>? emittedNames = null;
                if (IsHtmlAttributeName(attrName, "src") && IsHtmlSrcResourceTag(tagName))
                {
                    emitKind = "import";
                    singleEmittedName = attrValue.Trim();
                }
                else if (IsHtmlAttributeName(attrName, "srcset") && IsHtmlSrcsetResourceTag(tagName))
                {
                    emitKind = "import";
                    emittedNames = EnumerateHtmlSrcsetUrls(attrValue);
                }
                else if ((IsHtmlAttributeName(attrName, "href") || IsHtmlAttributeName(attrName, "xlink:href")) && IsHtmlHrefResourceTag(tagName))
                {
                    emitKind = "import";
                    singleEmittedName = attrValue.Trim();
                }
                else if (IsHtmlAttributeName(attrName, "data") && IsHtmlTagName(tagName, "object"))
                {
                    emitKind = "import";
                    singleEmittedName = attrValue.Trim();
                }
                else if (IsHtmlAttributeName(attrName, "poster") && IsHtmlTagName(tagName, "video"))
                {
                    emitKind = "import";
                    singleEmittedName = attrValue.Trim();
                }
                else if (IsHtmlAttributeName(attrName, "id") && !attrName.Contains(':') && !attrName.Contains('-') && !attrName.Contains('.'))
                {
                    emitKind = "property";
                    singleEmittedName = attrValue.Trim();
                }
                else if (IsHtmlAttributeName(attrName, "class") || IsHtmlAttributeName(attrName, "classname"))
                {
                    emitKind = "reference";
                    emittedNames = EnumerateHtmlClassNames(attrValue);
                }
                else if (IsHtmlAttributeName(attrName, "name") && IsHtmlTagName(tagName, "slot"))
                {
                    var slotName = attrValue.Trim();
                    if (slotName.Length > 0)
                    {
                        emitKind = "property";
                        singleEmittedName = slotName;
                        sawNamedSlotDeclaration = true;
                    }
                }
                else if (IsHtmlAttributeName(attrName, "slot"))
                {
                    var slotName = attrValue.Trim();
                    if (slotName.Length > 0)
                    {
                        emitKind = "reference";
                        singleEmittedName = slotName;
                    }
                }

                if (emitKind == null || (singleEmittedName == null && emittedNames == null))
                    continue;

                // Anchor the symbol at the attribute value so cross-line tags like
                // `<script\n  type="module"\n  src="/app.js">` land on the line that
                // actually carries the value.
                // 属性値の位置でシンボルを固定し、属性が折り返されたタグでも値が書かれた
                // 行にジャンプできるようにする。
                var anchor = attrValueStart >= 0 ? attrValueStart : pos;
                var startLine = FindHtmlLineNumber(lineStarts ??= BuildLineStarts(lines), anchor);
                var signatureIndex = Math.Clamp(startLine - 1, 0, lines.Length - 1);

                void AddEmittedName(string emittedName)
                {
                    (symbols ??= []).Add(new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = emitKind!,
                        Name = emittedName,
                        Line = startLine,
                        StartLine = startLine,
                        EndLine = startLine,
                        Signature = lines[signatureIndex].Trim(),
                    });
                }

                if (singleEmittedName != null)
                {
                    AddEmittedName(singleEmittedName);
                    continue;
                }

                if (emittedNames == null)
                    continue;

                foreach (var emittedName in emittedNames)
                    AddEmittedName(emittedName);
            }

            if (IsHtmlTagName(tagName, "slot") && !sawNamedSlotDeclaration)
            {
                var startLine = FindHtmlLineNumber(lineStarts ??= BuildLineStarts(lines), pos);
                var signatureIndex = Math.Clamp(startLine - 1, 0, lines.Length - 1);
                (symbols ??= []).Add(new SymbolRecord
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

        if (symbols is null)
            return [];

        AssignContainers(symbols, lines, null);
        PopulateDeclaredContainerQualifiedNames(symbols);
        return symbols;
    }

    private static IEnumerable<string> EnumerateHtmlClassNames(string value)
    {
        var tokenStart = -1;
        for (var index = 0; index <= value.Length; index++)
        {
            var atEnd = index == value.Length;
            if (!atEnd && !char.IsWhiteSpace(value[index]))
            {
                if (tokenStart < 0)
                    tokenStart = index;
                continue;
            }

            if (tokenStart < 0)
                continue;

            yield return value[tokenStart..index];
            tokenStart = -1;
        }
    }

    private static bool IsHtmlSemanticStateAttributeName(string attrName)
    {
        return (attrName.StartsWith("data-", StringComparison.OrdinalIgnoreCase) ||
                attrName.StartsWith("aria-", StringComparison.OrdinalIgnoreCase)) &&
               attrName.Length > 5;
    }

    private static string NormalizeHtmlAttributeName(string attrName)
    {
        for (var index = 0; index < attrName.Length; index++)
        {
            var ch = attrName[index];
            if (ch >= 'A' && ch <= 'Z')
                return attrName.ToLowerInvariant();
        }

        return attrName;
    }

    private static bool IsHtmlAttributeName(string attrName, string expected) =>
        attrName.Equals(expected, StringComparison.OrdinalIgnoreCase);

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

    private static readonly string[] HtmlRawTextElementNames = ["script", "style", "textarea", "title"];

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

    private static bool IsHtmlTagName(string tagName, string expected) =>
        tagName.Equals(expected, StringComparison.OrdinalIgnoreCase);

    private static bool IsHtmlSrcResourceTag(string tagName) =>
        IsHtmlTagName(tagName, "audio")
        || IsHtmlTagName(tagName, "embed")
        || IsHtmlTagName(tagName, "iframe")
        || IsHtmlTagName(tagName, "img")
        || IsHtmlTagName(tagName, "input")
        || IsHtmlTagName(tagName, "script")
        || IsHtmlTagName(tagName, "source")
        || IsHtmlTagName(tagName, "track")
        || IsHtmlTagName(tagName, "video");

    private static bool IsHtmlHrefResourceTag(string tagName) =>
        IsHtmlTagName(tagName, "a")
        || IsHtmlTagName(tagName, "area")
        || IsHtmlTagName(tagName, "image")
        || IsHtmlTagName(tagName, "link")
        || IsHtmlTagName(tagName, "use");

    private static bool IsHtmlSrcsetResourceTag(string tagName) =>
        IsHtmlTagName(tagName, "img") || IsHtmlTagName(tagName, "source");

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

    internal static string MaskHtmlRawTextRegions(string text)
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
        if (!MayContainHtmlRawTextMaskTarget(text))
            return text;

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

    private static bool MayContainHtmlRawTextMaskTarget(string text)
    {
        for (var index = text.IndexOf('<'); index >= 0; index = text.IndexOf('<', index + 1))
        {
            if (index + 1 >= text.Length)
                continue;

            var next = text[index + 1];
            if (next is '!' or '?')
                return true;

            if (TryMatchHtmlRawTextOpenerName(text, index) != null)
                return true;
        }

        return false;
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
                if (!EqualsHtmlAsciiIgnoreCase(text[nameStart + j], name[j]))
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

    private static bool MayNeedHtmlRawTextMask(string text)
        => text.IndexOf("<!", StringComparison.Ordinal) >= 0
           || text.IndexOf("<?", StringComparison.Ordinal) >= 0
           || text.IndexOf("<script", StringComparison.OrdinalIgnoreCase) >= 0
           || text.IndexOf("<style", StringComparison.OrdinalIgnoreCase) >= 0
           || text.IndexOf("<textarea", StringComparison.OrdinalIgnoreCase) >= 0
           || text.IndexOf("<title", StringComparison.OrdinalIgnoreCase) >= 0;

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
                    if (!EqualsHtmlAsciiIgnoreCase(text[i + 2 + j], tagName[j]))
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

    private static bool EqualsHtmlAsciiIgnoreCase(char actual, char expectedLower)
    {
        if (actual >= 'A' && actual <= 'Z')
            actual = (char)(actual + ('a' - 'A'));

        return actual == expectedLower;
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
