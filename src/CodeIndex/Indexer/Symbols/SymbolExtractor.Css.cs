using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using System.Runtime.CompilerServices;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static int CountNonOverlappingOccurrences(string text, string value)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(value))
            return 0;

        var count = 0;
        var startIndex = 0;
        while (startIndex < text.Length)
        {
            var index = text.IndexOf(value, startIndex, StringComparison.Ordinal);
            if (index < 0)
                break;

            count++;
            startIndex = index + value.Length;
        }

        return count;
    }

    private static void ExtractCssInlineGroupingSelectors(
        long fileId,
        string rawLine,
        string maskedLine,
        string[] cssScannerLines,
        int lineIndex,
        IReadOnlyList<SymbolPattern> patterns,
        List<SymbolRecord> symbols,
        HashSet<string>? cssSeenSymbols)
    {
        var groupingDepth = 0;
        var qualifiedDepth = 0;
        var segmentStart = 0;

        for (int i = 0; i < maskedLine.Length; i++)
        {
            var ch = maskedLine[i];
            if (ch == ';')
            {
                if (groupingDepth == 0 && qualifiedDepth == 0)
                    TryAddCssLayerListSymbols(fileId, rawLine[segmentStart..i], maskedLine[segmentStart..i], lineIndex, symbols, cssSeenSymbols);

                segmentStart = i + 1;
                continue;
            }

            if (ch == '{')
            {
                var maskedSegment = maskedLine[segmentStart..i].Trim();
                var rawSegment = rawLine[segmentStart..i].Trim();
                var isGroupingAtRule = maskedSegment.StartsWith('@');

                if (isGroupingAtRule)
                    groupingDepth++;
                else
                    qualifiedDepth++;

                segmentStart = i + 1;
                continue;
            }

            if (ch == '}')
            {
                if (qualifiedDepth > 0)
                    qualifiedDepth--;
                else if (groupingDepth > 0)
                    groupingDepth--;

                segmentStart = i + 1;
            }
        }
    }

    private static void TryAddCssInlineSelectorSegment(
        long fileId,
        string rawSegment,
        string maskedSegment,
        string[] cssScannerLines,
        int lineIndex,
        int openingBraceIndex,
        IReadOnlyList<SymbolPattern> patterns,
        List<SymbolRecord> symbols,
        HashSet<string>? cssSeenSymbols)
    {
        if (string.IsNullOrWhiteSpace(maskedSegment))
            return;

        var matchLine = $"{rawSegment} {{";
        foreach (var pattern in patterns)
        {
            if (pattern.BodyStyle != BodyStyle.Brace)
                continue;

            var match = pattern.Regex.Match(matchLine);
            if (!match.Success)
                continue;

            var name = match.Groups["name"].Success
                ? match.Groups["name"].Value.Trim()
                : match.Value.Trim();
            if (string.IsNullOrWhiteSpace(name))
                return;

            var (endLine, bodyStartLine, bodyEndLine) = FindBraceRange(cssScannerLines, lineIndex, openingBraceIndex);
            var startLine = lineIndex + 1;
            AddSymbolRecord(
                symbols,
                cssSeenSymbols,
                startLine,
                new SymbolRecord
                {
                    FileId = fileId,
                    Kind = pattern.Kind,
                    Name = name,
                    Line = startLine,
                    StartLine = startLine,
                    EndLine = Math.Max(startLine, endLine),
                    BodyStartLine = bodyStartLine,
                    BodyEndLine = bodyEndLine,
                    Signature = rawSegment.Length > 0 ? $"{rawSegment} {{" : "{",
                });
            return;
        }
    }

    private static void TryAddCssSelectorListSegments(
        long fileId,
        string rawSegment,
        string maskedSegment,
        string[] cssScannerLines,
        int lineIndex,
        int openingBraceIndex,
        IReadOnlyList<SymbolPattern> patterns,
        List<SymbolRecord> symbols,
        HashSet<string>? cssSeenSymbols)
    {
        foreach (var (rawPart, maskedPart) in EnumerateCssCommaSeparatedSegments(rawSegment, maskedSegment))
        {
            TryAddCssInlineSelectorSegment(
                fileId,
                rawPart,
                maskedPart,
                cssScannerLines,
                lineIndex,
                openingBraceIndex,
                patterns,
                symbols,
                cssSeenSymbols);
        }
    }

    private static void TryAddCssLayerListSymbols(
        long fileId,
        string rawSegment,
        string maskedSegment,
        int lineIndex,
        List<SymbolRecord> symbols,
        HashSet<string>? cssSeenSymbols)
    {
        var trimmedMaskedSegment = maskedSegment.TrimStart();
        if (!trimmedMaskedSegment.StartsWith("@layer", StringComparison.OrdinalIgnoreCase))
            return;

        var trimmedRawSegment = rawSegment.Trim();
        if (trimmedRawSegment.Length == 0)
            return;

        const string atLayerPrefix = "@layer";
        if (trimmedRawSegment.Length <= atLayerPrefix.Length)
            return;

        var rawNames = trimmedRawSegment[atLayerPrefix.Length..].Trim();
        var maskedNames = trimmedMaskedSegment[atLayerPrefix.Length..].Trim();
        if (rawNames.Length == 0 || maskedNames.Length == 0)
            return;

        foreach (var (rawName, maskedName) in EnumerateCssCommaSeparatedSegments(rawNames, maskedNames))
        {
            var name = rawName.Trim();
            if (name.Length == 0 || maskedName.Length == 0)
                continue;

            AddSymbolRecord(
                symbols,
                cssSeenSymbols,
                lineIndex + 1,
                new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "namespace",
                    Name = name,
                    Line = lineIndex + 1,
                    StartLine = lineIndex + 1,
                    EndLine = lineIndex + 1,
                    Signature = trimmedRawSegment,
                });
        }
    }

    private static IEnumerable<(string Raw, string Masked)> EnumerateCssCommaSeparatedSegments(string rawText, string maskedText)
    {
        var segmentStart = 0;
        var parenDepth = 0;
        var bracketDepth = 0;

        for (var index = 0; index < maskedText.Length; index++)
        {
            var ch = maskedText[index];
            if (ch == '(')
            {
                parenDepth++;
                continue;
            }

            if (ch == ')' && parenDepth > 0)
            {
                parenDepth--;
                continue;
            }

            if (ch == '[')
            {
                bracketDepth++;
                continue;
            }

            if (ch == ']' && bracketDepth > 0)
            {
                bracketDepth--;
                continue;
            }

            if (ch == ',' && parenDepth == 0 && bracketDepth == 0)
            {
                yield return (rawText[segmentStart..index].Trim(), maskedText[segmentStart..index].Trim());
                segmentStart = index + 1;
            }
        }

        yield return (rawText[segmentStart..].Trim(), maskedText[segmentStart..].Trim());
    }

    private static int FindCssSameLineBraceEndColumn(string line, int startColumn)
    {
        var maskedLine = MaskCssScannerLines([line])[0];
        var depth = 0;
        var opened = false;

        for (var index = Math.Max(0, startColumn); index < maskedLine.Length; index++)
        {
            var ch = maskedLine[index];
            if (ch == '{')
            {
                depth++;
                opened = true;
            }
            else if (ch == '}' && opened)
            {
                depth--;
                if (depth == 0)
                    return index;
            }
        }

        return -1;
    }

    private static readonly Regex CssFontFaceDeclarationRegex = new(@"(?:^|[;{])\s*font-family\s*:", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CssInlineCustomPropertyRegex = new(@"(?<name>--[\w-]+)\s*:", RegexOptions.Compiled);
    private static readonly Regex CssMediaFeatureNameRegex = new(@"^\s*(?:not\s+)?(?<name>--[\w-]+|[A-Za-z_][\w-]*)(?=\s*(?::|[<>]=?|=|$))|[<>]=?\s*(?<name>--[\w-]+|[A-Za-z_][\w-]*)(?=\s*(?:[<>]=?|$))", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string ResolveCssSymbolName(string matchLine, string name, string[] lines, int startIndex, int endLine)
    {
        if (!matchLine.TrimStart().StartsWith("@font-face", StringComparison.OrdinalIgnoreCase))
            return name;

        return TryGetCssFontFaceFamilyName(lines, startIndex, endLine, out var fontFamily)
            ? fontFamily
            : string.Empty;
    }

    private static void TryAddCssMediaFeatureSymbols(
        long fileId,
        string rawLine,
        string maskedLine,
        int lineIndex,
        List<SymbolRecord> symbols,
        HashSet<string>? cssSeenSymbols)
    {
        var trimmedMaskedLine = maskedLine.TrimStart();
        if (!trimmedMaskedLine.StartsWith("@media", StringComparison.OrdinalIgnoreCase))
            return;

        var blockStart = maskedLine.IndexOf('{');
        if (blockStart < 0)
            blockStart = maskedLine.Length;

        var query = maskedLine[..blockStart];
        for (var index = 0; index < query.Length; index++)
        {
            if (query[index] != '(')
                continue;

            var featureStart = index + 1;
            var depth = 1;
            index++;
            while (index < query.Length && depth > 0)
            {
                if (query[index] == '(')
                    depth++;
                else if (query[index] == ')')
                    depth--;

                index++;
            }

            if (depth != 0)
                break;

            var featureText = query[featureStart..(index - 1)];
            if (string.IsNullOrWhiteSpace(featureText))
                continue;

            var match = CssMediaFeatureNameRegex.Match(featureText);
            if (match.Success)
            {
                var name = match.Groups["name"].Value;
                if (string.Equals(name, "and", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "or", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "not", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var featureColumn = featureStart + match.Groups["name"].Index;
                AddSymbolRecord(
                    symbols,
                    cssSeenSymbols,
                    lineIndex + 1,
                    new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = "property",
                        Name = name,
                        Line = lineIndex + 1,
                        StartLine = lineIndex + 1,
                        StartColumn = featureColumn,
                        EndLine = lineIndex + 1,
                        Signature = rawLine.Trim(),
                    });
            }
        }
    }

    private static bool TryGetCssFontFaceFamilyName(string[] lines, int startIndex, int endLine, out string fontFamily)
    {
        fontFamily = string.Empty;
        var blockLines = lines.Skip(startIndex).Take(Math.Max(1, endLine - startIndex)).ToArray();
        var maskedBlockText = string.Join('\n', MaskCssScannerLines(blockLines));
        var match = CssFontFaceDeclarationRegex.Match(maskedBlockText);
        if (!match.Success)
            return false;

        var valueStart = match.Index + match.Length;
        var valueEnd = valueStart;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        while (valueEnd < maskedBlockText.Length)
        {
            var ch = maskedBlockText[valueEnd];
            if (ch == '\'' && !inDoubleQuote)
                inSingleQuote = !inSingleQuote;
            else if (ch == '"' && !inSingleQuote)
                inDoubleQuote = !inDoubleQuote;
            else if (!inSingleQuote && !inDoubleQuote && ch is ';' or '}')
                break;

            valueEnd++;
        }

        if (valueEnd == valueStart)
            return false;

        var rawBlockText = string.Join('\n', blockLines);
        var rawName = valueEnd <= rawBlockText.Length
            ? rawBlockText[valueStart..valueEnd]
            : rawBlockText[valueStart..];
        rawName = RemoveCssBlockComments(rawName).Trim();
        if (rawName.Length == 0)
            return false;

        if ((rawName[0] == '"' && rawName[^1] == '"') || (rawName[0] == '\'' && rawName[^1] == '\''))
            rawName = rawName[1..^1].Trim();

        if (rawName.Length == 0)
            return false;

        fontFamily = rawName;
        return true;
    }

    private static string RemoveCssBlockComments(string value)
    {
        if (value.Length == 0)
            return value;

        var builder = new System.Text.StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (i + 1 < value.Length && value[i] == '/' && value[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < value.Length && !(value[i] == '*' && value[i + 1] == '/'))
                    i++;

                if (i + 1 < value.Length)
                    i++;

                continue;
            }

            builder.Append(value[i]);
        }

        return builder.ToString();
    }

    private static bool ShouldSkipCssNestedSelectorCandidate(
        string? lang,
        SymbolPattern pattern,
        string matchLine,
        bool[]? cssQualifiedRuleAncestors,
        int lineIndex) =>
        lang == "css"
        && cssQualifiedRuleAncestors != null
        && cssQualifiedRuleAncestors[lineIndex]
        && pattern.Kind == "class"
        && !matchLine.TrimStart().StartsWith('@');

    // Reject JS/TS HOC candidate matches whose captured RHS uses the bare `styled.`
    // or `styled(` forms without a tagged-template backtick on the same statement.
    // The HOC regex accepts `styled[.(` `]` as the first post-identifier token so
    // the real tagged-template bindings (`styled.div\`...\``, `styled(Box)\`...\``)
    // still match, but it also lets through the factory-capture and plain-call
    // shapes (`const F = styled.div;`, `const F = styled(Box);`) which do not
    // declare a rendered component and must not be surfaced as function symbols.
    // The gate reads the raw (unmasked) source because
    // StructuralLineMasker.MaskJsTsTemplateLiteralContents replaces template
    // delimiters with space, so the masked line cannot distinguish the shapes.
    // The backtick scan is statement-local: only characters between the match end
    // and the next `;` (or next statement) are inspected, so an unrelated template
    // literal on another statement does not reopen the gate. The scanner is also
    // multi-line aware — Prettier-style styled bindings place the backtick on the
    // line after `styled.div` / `styled(Component)`, so the scan walks forward
    // across raw lines while carrying block-comment state, bounded to a short
    // lookahead window. A line that starts with a JS/TS statement-starter keyword
    // (`const`, `let`, `var`, `function`, `class`, `return`, `import`, etc.)
    // terminates the scan to model implicit ASI: `const X = styled.div\nconst Y =
    // 5;` must stay rejected even though no `;` appears on the `styled.div` line.
    // The scanner also understands line comments (`//`), block comments
    // (`/* ... */`), and plain string literals (`'...'`, `"..."`), so a backtick
    // that only lives inside a comment or string does not keep a non-template
    // binding alive, and a `;` that only lives inside a comment does not fence
    // a real backtick off from a subsequent tagged template on the same
    // statement. Closes #240 follow-up (codex review #5, #7, #8, and #9 blockers).
    // JS/TS 行における HOC 候補のうち、`styled.` / `styled(` を素のまま使い、同じ文内に
    // タグ付きテンプレートのバッククォートを持たない形（`const F = styled.div;`、
    // `const F = styled(Box);`）を弾く。HOC regex は識別子直後の `styled[.(`、`]`
    // を受け付けるためタグ付きテンプレート形（`styled.div\`...\``、`styled(Box)\`...\``）
    // はマッチさせつつ、factory 捕捉 / 素の呼び出し形も通過させてしまう。これらは
    // コンポーネントを生成しないため function シンボルとして surface してはいけない。
    // ゲートは raw 行（マスク前）を参照する — `StructuralLineMasker.MaskJsTsTemplateLiteralContents`
    // がテンプレート区切りを空白にマスクするため、マスク後では形状を区別できないのが理由。
    // バッククォート探索は文ローカル（match 終端から次の `;` または次の文まで）に限定し、
    // 別の文として配置された無関係なテンプレートリテラルでゲートを誤って解除しない。
    // さらに Prettier 整形のように `styled.div` / `styled(Component)` の次行にバッククォートを
    // 置くケースへ対応するため、スキャナはブロックコメント状態を引き継ぎつつ複数行を前方走査する
    // （行数上限付き）。継続行の最初の実トークンがタグ付きテンプレートの継続として妥当な
    // 文字（バッククォート・`.`・`<`）でない場合は ASI による文終端として走査を打ち切る。
    // これにより `const X = styled.div\nfoo(\`...\`)` や `const X = styled.div\nawait foo(\`...\`)`
    // のような「次行が式文」のケースでも phantom `function` シンボルを出さない。さらに
    // `const X = styled.div\nconst Y = 5;` のような「次行が宣言文」のケースも引き続き除外される。
    // 加えて行コメント（`//`）・ブロックコメント（`/* ... */`）・通常の文字列リテラル
    // （`'...'` / `"..."`）を構文として理解し、コメントや文字列内のバッククォートが非テンプレート
    // 束縛を延命させたり、コメント内の `;` が同一文内の本物のバッククォートより先に文終端として
    // 扱われて実タグ付きテンプレートを落とすことを防ぐ。
    // Closes #240 follow-up（codex レビュー #5・#7・#8・#9・#10・#13 の blocker 対応）。
    // The lookahead window is intentionally generous — Prettier-formatted
    // styled bindings with long `.attrs((props) => ({ ... }))` argument
    // objects routinely span more than ten lines before the backtick, and
    // truncating the scan would silently drop the binding's `function`
    // symbol. 32 lines is large enough for realistic shapes while still
    // keeping the cost bounded per match.
    // lookahead window は意図的に広めに取る — Prettier 整形で
    // `.attrs((props) => ({ ... }))` の引数オブジェクトを持つ styled 束縛は
    // 10 行を超えてからバッククォートに到達することが珍しくなく、走査を
    // 短く打ち切ると binding の `function` シンボルを silently 落としてしまう。
    // 32 行あれば実運用で見られる形は概ねカバーでき、1 マッチあたりの
    // コストも有限に保てる。
    private const int JsTsStyledFactoryGateMaxLookaheadLines = 32;

}
