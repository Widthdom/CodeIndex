using System.Text.RegularExpressions;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

/// <summary>
/// Source-code detection result with a stable reason code for diagnostics.
/// 診断用の安定した理由コードを持つソースコード検出結果。
/// </summary>
public readonly record struct SourceCodeDetectionResult(
    bool ContainsSourceCode,
    string? ReasonCode,
    IReadOnlyDictionary<string, int> ReasonCounts)
{
    private static readonly IReadOnlyDictionary<string, int> EmptyReasonCounts = new Dictionary<string, int>(StringComparer.Ordinal);

    public SourceCodeDetectionResult(bool containsSourceCode, string? reasonCode)
        : this(containsSourceCode, reasonCode, EmptyReasonCounts)
    {
    }

    public static SourceCodeDetectionResult NoSourceCode { get; } = new(false, null, EmptyReasonCounts);

    public static SourceCodeDetectionResult Detected(string reasonCode) =>
        new(true, reasonCode, new Dictionary<string, int>(StringComparer.Ordinal) { [reasonCode] = 1 });

    public static SourceCodeDetectionResult Detected(string reasonCode, IReadOnlyDictionary<string, int> reasonCounts) =>
        new(true, reasonCode, reasonCounts);
}

/// <summary>
/// Detects whether a text string likely contains source code.
/// テキスト文字列にソースコードが含まれている可能性があるかを検出する。
///
/// PURPOSE / 目的:
///   This class is used by the suggest_improvement MCP tool to prevent
///   AI agents from accidentally sending source code in their suggestions.
///   Only structured, natural-language gap descriptions should be transmitted.
///   このクラスは suggest_improvement MCPツールで使用され、AIエージェントが
///   提案にソースコードを誤って含めることを防ぐ。送信されるべきは構造化された
///   自然言語によるギャップ記述のみである。
///
/// DESIGN PHILOSOPHY / 設計思想:
///   - Each detection rule is a separate, clearly named method.
///     各検出ルールは独立した、明確な名前のメソッドである。
///   - Each method documents WHAT it detects and WHY that pattern
///     indicates source code rather than natural language.
///     各メソッドは「何を検出するか」と「なぜそのパターンがソースコードの
///     兆候なのか」を文書化している。
///   - Short inline code examples (e.g. `const foo = () => {}`) are
///     intentionally allowed. Only multi-line code blocks are rejected.
///     短いインラインコード例（例: `const foo = () => {}`）は意図的に許容する。
///     複数行のコードブロックのみを拒否する。
///   - False negatives (missing some code) are acceptable.
///     False positives (rejecting valid natural language) are not.
///     偽陰性（コードの見逃し）は許容する。偽陽性（有効な自然言語の拒否）は
///     許容しない。
///
/// WHAT THIS CLASS DOES NOT DO / このクラスがやらないこと:
///   - This is NOT a security boundary. A determined agent could bypass it.
///     セキュリティ境界ではない。意図的に回避しようとするエージェントは回避できる。
///   - This does NOT parse code. It uses surface-level heuristics only.
///     コードの構文解析はしない。表面的なヒューリスティックのみを使用する。
///   - This does NOT check for obfuscated or encoded code.
///     難読化やエンコードされたコードは検出しない。
///
/// SOURCE CODE / ソースコード:
///   This file is part of the open-source cdidx project.
///   Anyone can inspect this logic to verify that no source code is transmitted.
///   このファイルはオープンソースの cdidx プロジェクトの一部である。
///   誰でもこのロジックを検査し、ソースコードが送信されないことを確認できる。
/// </summary>
public static class SourceCodeDetector
{
    // ---------------------------------------------------------------
    // Thresholds — intentionally conservative to avoid false positives.
    // しきい値 — 偽陽性を避けるため意図的に保守的に設定。
    // ---------------------------------------------------------------

    /// <summary>
    /// Minimum number of lines required for the "statement endings" rule to fire.
    /// 「文末パターン」ルールが発動するために必要な最小行数。
    /// A short text with a few semicolons (e.g. "use ; as delimiter") should not trigger.
    /// セミコロンが数個含まれる短文（例: 「; を区切り文字に使う」）では発動させない。
    /// </summary>
    private const int MinLinesForStatementCheck = 5;

    /// <summary>
    /// If more than this fraction of lines end with code-like characters,
    /// the text is likely source code.
    /// この割合を超える行がコード的な文字で終わっている場合、ソースコードの可能性が高い。
    /// </summary>
    private const double StatementEndingThreshold = 0.5;

    /// <summary>
    /// Number of consecutive code-like lines required to trigger detection.
    /// 検出を発動させるのに必要な、連続するコード的な行の数。
    /// Three consecutive indented code lines is the minimum for a "code block."
    /// 3行連続のインデント付きコード行が「コードブロック」の最小単位。
    /// </summary>
    private const int ConsecutiveCodeLineThreshold = 3;

    /// <summary>
    /// Minimum lines inside a brace-delimited block to trigger detection.
    /// 波括弧ブロック内の最小行数（検出発動用）。
    /// </summary>
    private const int MinLinesInsideBlock = 3;

    /// <summary>
    /// Number of consecutive import/using lines required to trigger detection.
    /// 検出を発動させるのに必要な、連続する import/using 行の数。
    /// </summary>
    private const int ConsecutiveImportThreshold = 3;

    public const string ReasonStatementEnding = "statement-ending";
    public const string ReasonIndentedCodeLines = "indented-code-lines";
    public const string ReasonBlockStructure = "block-structure";
    public const string ReasonRepeatedImports = "repeated-imports";
    public const string ReasonFunctionDefinition = "function-definition";
    public const string ReasonFencedCodeBlock = "fenced-code-block";

    // ---------------------------------------------------------------
    // Public API / 公開API
    // ---------------------------------------------------------------

    /// <summary>
    /// Returns true if the given text likely contains source code.
    /// The check is performed by running multiple independent heuristics.
    /// If ANY heuristic matches, the text is considered to contain code.
    /// 与えられたテキストにソースコードが含まれている可能性がある場合 true を返す。
    /// 複数の独立したヒューリスティックを実行して検査する。
    /// いずれか1つでもマッチすれば、コードを含むと判定する。
    /// </summary>
    /// <param name="text">The text to inspect / 検査するテキスト</param>
    /// <returns>true if source code is likely present / ソースコードが含まれる可能性がある場合 true</returns>
    public static bool ContainsSourceCode(string? text) => Detect(text).ContainsSourceCode;

    /// <summary>
    /// Returns a detection result with a stable reason code when source code is likely present.
    /// ソースコードが含まれる可能性がある場合、安定した理由コード付きの検出結果を返す。
    /// </summary>
    /// <param name="text">The text to inspect / 検査するテキスト</param>
    /// <returns>Detection result / 検出結果</returns>
    public static SourceCodeDetectionResult Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return SourceCodeDetectionResult.NoSourceCode;

        // Run each heuristic independently.
        // Any single match is sufficient to flag the text.
        // 各ヒューリスティックを独立して実行する。
        // 1つでもマッチすればテキストをフラグする。

        var reasonCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        string? primaryReason = null;

        CountReason(ReasonStatementEnding, HasCodeStatementPattern(text));
        CountReason(ReasonIndentedCodeLines, HasConsecutiveCodeLines(text));
        CountReason(ReasonBlockStructure, HasBlockStructure(text));
        CountReason(ReasonRepeatedImports, HasRepeatedImports(text));
        CountReason(ReasonFunctionDefinition, HasMultiLineFunctionDefinition(text));
        CountReason(ReasonFencedCodeBlock, HasFencedCodeBlock(text));

        return primaryReason is null
            ? SourceCodeDetectionResult.NoSourceCode
            : SourceCodeDetectionResult.Detected(primaryReason, reasonCounts);

        void CountReason(string reasonCode, bool matched)
        {
            if (!matched)
                return;
            reasonCounts[reasonCode] = reasonCounts.TryGetValue(reasonCode, out var count) ? count + 1 : 1;
            primaryReason ??= reasonCode;
        }
    }

    // ---------------------------------------------------------------
    // Heuristic 1: Statement Ending Pattern
    // ヒューリスティック 1: 文末パターン
    // ---------------------------------------------------------------

    /// <summary>
    /// Detects text where the majority of lines end with characters that are
    /// common in programming languages but rare in natural language: ; { }
    ///
    /// WHY this indicates source code:
    ///   Natural language sentences end with periods, question marks, or
    ///   exclamation marks — not semicolons or curly braces. When more than
    ///   half of the lines in a text end with these characters, the text is
    ///   almost certainly a block of source code.
    ///
    /// テキスト内の行の大半が、プログラミング言語では一般的だが自然言語では
    /// 稀な文字（; { }）で終わっているかを検出する。
    ///
    /// なぜこれがソースコードの兆候か:
    ///   自然言語の文はピリオド、疑問符、感嘆符で終わる — セミコロンや波括弧
    ///   ではない。行の半数超がこれらの文字で終わっていれば、ほぼ確実に
    ///   ソースコードのブロックである。
    /// </summary>
    private static bool HasCodeStatementPattern(string text)
    {
        // Count lines ending with code-typical characters.
        // コード的な文字で終わる行をカウントする。
        var nonEmptyLineCount = 0;
        var codeEndingCount = 0;
        foreach (var rawLine in EnumerateLines(text))
        {
            var line = rawLine.TrimEnd();
            if (line.Length == 0)
                continue;
            nonEmptyLineCount++;
            if (line.EndsWith(';') || line.EndsWith('{') || line.EndsWith('}'))
                codeEndingCount++;
        }

        if (nonEmptyLineCount < MinLinesForStatementCheck)
            return false;

        var ratio = (double)codeEndingCount / nonEmptyLineCount;
        return ratio > StatementEndingThreshold;
    }

    // ---------------------------------------------------------------
    // Heuristic 2: Consecutive Code Lines
    // ヒューリスティック 2: 連続コード行
    // ---------------------------------------------------------------

    /// <summary>
    /// Detects three or more consecutive lines that look like indented code.
    /// A "code-like line" is one that starts with whitespace (indentation)
    /// and contains programming syntax tokens.
    ///
    /// WHY this indicates source code:
    ///   Natural-language descriptions are typically not indented. When
    ///   multiple consecutive lines are indented and contain tokens like
    ///   `return`, `if`, `var`, `=`, etc., it strongly suggests a pasted
    ///   code fragment rather than a description of a gap.
    ///
    /// 3行以上連続してインデント付きコードに見える行があるかを検出する。
    /// 「コード的な行」とは、空白（インデント）で始まり、
    /// プログラミング構文トークンを含む行のことである。
    ///
    /// なぜこれがソースコードの兆候か:
    ///   自然言語の説明は通常インデントされない。複数の連続行がインデントされ、
    ///   `return`, `if`, `var`, `=` 等のトークンを含んでいれば、ギャップの
    ///   説明ではなくコード断片のコピペである可能性が高い。
    /// </summary>
    private static bool HasConsecutiveCodeLines(string text)
    {
        int consecutiveCount = 0;

        foreach (var rawLine in EnumerateLines(text))
        {
            if (IsIndentedCodeLine(rawLine))
            {
                consecutiveCount++;
                if (consecutiveCount >= ConsecutiveCodeLineThreshold)
                    return true;
            }
            else
            {
                consecutiveCount = 0;
            }
        }

        return false;
    }

    /// <summary>
    /// Determines whether a single line looks like an indented line of code.
    /// The line must start with whitespace AND contain at least one
    /// programming-specific token.
    /// 単一行がインデント付きコード行に見えるかを判定する。
    /// 行は空白で始まり、かつ少なくとも1つのプログラミング固有トークンを
    /// 含んでいなければならない。
    /// </summary>
    private static bool IsIndentedCodeLine(string line)
    {
        // Must start with whitespace (indentation).
        // 空白（インデント）で始まる必要がある。
        if (line.Length == 0 || !char.IsWhiteSpace(line[0]))
            return false;

        var trimmed = line.Trim();
        if (trimmed.Length == 0)
            return false;

        // Check for common code tokens that would not appear in prose.
        // 散文には現れない一般的なコードトークンを検査する。
        return s_codeTokenPattern.IsMatch(trimmed);
    }

    /// <summary>
    /// Pattern matching common code tokens. Each alternative is a token
    /// that is common in source code but rare in natural language prose.
    /// 一般的なコードトークンにマッチするパターン。各選択肢はソースコードでは
    /// 一般的だが自然言語の散文では稀なトークンである。
    /// </summary>
    private static readonly BoundedRegex s_codeTokenPattern = new(
        @"(?:"
        // Assignment or comparison operators / 代入・比較演算子
        + @"[^=!<>]=[^=]"
        // Statement-ending semicolons / 文末セミコロン
        + @"|;\s*$"
        // Brace-only lines (opening or closing blocks) / 波括弧のみの行（ブロック開閉）
        + @"|^[{}]\s*$"
        // Arrow functions or lambdas / アロー関数・ラムダ
        + @"|=>"
        // Common keywords followed by parentheses / 括弧が続く一般的なキーワード
        + @"|\b(?:if|for|while|switch|foreach|catch)\s*\("
        // Return statements / return 文
        + @"|\breturn\b"
        // Variable declarations / 変数宣言
        + @"|\b(?:var|let|const|int|string|bool|float|double)\s+\w+"
        // Access modifiers (start of line) / アクセス修飾子（行頭）
        + @"|^(?:public|private|protected|internal)\s"
        // Function/method calls: identifier followed by parentheses
        // e.g. print("hello"), result.append(item), foo.bar(x)
        // This catches expression-only lines in Python, Ruby, etc.
        // 関数/メソッド呼び出し: 識別子の後に括弧
        // 例: print("hello"), result.append(item), foo.bar(x)
        // Python, Ruby 等の expression-only 行を検出する。
        + @"|\w+\s*\([^)]*\)\s*$"
        // Dot-chained method access: foo.bar, self.x, this.field
        // ドットチェーンのメソッドアクセス: foo.bar, self.x, this.field
        + @"|\w+\.\w+"
        // Python/Ruby keywords and patterns not covered above
        // 上記でカバーされない Python/Ruby キーワードとパターン
        + @"|\b(?:elif|else:|except|raise|yield|pass|break|continue|del|assert|puts|print)\b"
        // Python-style for/if/while with colon (no parentheses)
        // Python スタイルの for/if/while（括弧なし、コロン付き）
        + @"|\b(?:for|if|while|elif)\s+.+:\s*$"
        // Closing parenthesis/bracket only (continuation lines)
        // 閉じ括弧のみの行（継続行）
        + @"|^[)\]]\s*$"
        + @")",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // ---------------------------------------------------------------
    // Heuristic 3: Block Structure
    // ヒューリスティック 3: ブロック構造
    // ---------------------------------------------------------------

    /// <summary>
    /// Detects brace-delimited blocks with content inside: a line ending
    /// with `{`, followed by 3+ lines, followed by a line that is `}`.
    ///
    /// WHY this indicates source code:
    ///   Curly-brace blocks enclosing multiple lines are the hallmark of
    ///   function bodies, class definitions, and control structures. This
    ///   pattern does not occur in natural language.
    ///
    /// 波括弧で区切られ、内部に内容を持つブロックを検出する。
    /// `{` で終わる行、3行以上の内容行、`}` のみの行という構造。
    ///
    /// なぜこれがソースコードの兆候か:
    ///   複数行を囲む波括弧ブロックは、関数本体、クラス定義、制御構造の
    ///   典型的な特徴である。このパターンは自然言語には現れない。
    /// </summary>
    private static bool HasBlockStructure(string text)
    {
        var inBlock = false;
        var innerLineCount = 0;
        foreach (var line in EnumerateLines(text))
        {
            if (!inBlock)
            {
                var trimmed = line.TrimEnd();
                if (trimmed.EndsWith('{'))
                {
                    inBlock = true;
                    innerLineCount = 0;
                }
                continue;
            }

            var innerTrimmed = line.Trim();
            if (innerTrimmed == "}" || innerTrimmed.StartsWith('}'))
            {
                // Found closing brace. Check if enough lines were inside.
                // 閉じ波括弧を発見。内部に十分な行数があったかを検査。
                if (innerLineCount >= MinLinesInsideBlock)
                    return true;
                inBlock = false;
                continue;
            }

            innerLineCount++;
        }

        return false;
    }

    // ---------------------------------------------------------------
    // Heuristic 4: Repeated Import/Using Statements
    // ヒューリスティック 4: import/using 文の連打
    // ---------------------------------------------------------------

    /// <summary>
    /// Detects three or more consecutive lines that are import/using/require
    /// statements.
    ///
    /// WHY this indicates source code:
    ///   A sequence of import statements is typically the top of a source
    ///   file being pasted verbatim. No natural-language description would
    ///   contain three consecutive import lines.
    ///
    /// 3行以上連続する import/using/require 文を検出する。
    ///
    /// なぜこれがソースコードの兆候か:
    ///   import 文の連続は、ソースファイルの先頭がそのままコピペされた
    ///   典型的なパターンである。自然言語の説明に import 行が3つ連続する
    ///   ことはない。
    /// </summary>
    private static bool HasRepeatedImports(string text)
    {
        int consecutiveCount = 0;

        foreach (var rawLine in EnumerateLines(text))
        {
            var trimmed = rawLine.Trim();
            if (IsImportLine(trimmed))
            {
                consecutiveCount++;
                if (consecutiveCount >= ConsecutiveImportThreshold)
                    return true;
            }
            else
            {
                consecutiveCount = 0;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if a line is an import/using/require/include statement.
    /// 行が import/using/require/include 文であるかを検査する。
    /// </summary>
    private static bool IsImportLine(string trimmedLine)
    {
        // Common import patterns across languages / 各言語の一般的な import パターン
        return trimmedLine.StartsWith("import ", StringComparison.Ordinal)
            || trimmedLine.StartsWith("using ", StringComparison.Ordinal)
            || trimmedLine.StartsWith("#include ", StringComparison.Ordinal)
            || trimmedLine.StartsWith("require ", StringComparison.Ordinal)
            || trimmedLine.StartsWith("require(", StringComparison.Ordinal)
            || trimmedLine.StartsWith("from ", StringComparison.Ordinal) && trimmedLine.Contains(" import ");
    }

    // ---------------------------------------------------------------
    // Heuristic 5: Multi-line Function Definition
    // ヒューリスティック 5: 複数行にわたる関数定義
    // ---------------------------------------------------------------

    /// <summary>
    /// Detects function/method definitions that span multiple lines.
    /// A single-line example like "e.g. `def foo():`" is allowed.
    /// Only multi-line definitions (definition + body) are flagged.
    ///
    /// WHY this indicates source code:
    ///   A function definition followed by a body on subsequent lines is
    ///   a copy-pasted function, not a description of a gap.
    ///
    /// 複数行にわたる関数/メソッド定義を検出する。
    /// 「例: `def foo():`」のような1行の例示は許容する。
    /// 定義 + 本体を含む複数行の定義のみをフラグする。
    ///
    /// なぜこれがソースコードの兆候か:
    ///   関数定義に後続する本体行は、コピペされた関数であり、
    ///   ギャップの説明ではない。
    /// </summary>
    private static bool HasMultiLineFunctionDefinition(string text)
    {
        var remainingBodyProbeLines = 0;
        foreach (var line in EnumerateLines(text))
        {
            if (remainingBodyProbeLines > 0)
            {
                remainingBodyProbeLines--;
                var nextTrimmed = line.Trim();
                if (nextTrimmed.Length == 0)
                    continue;

                // If the next non-empty line is indented code or a brace,
                // this is a multi-line function definition (code).
                // 次の非空行がインデント付きコードか波括弧なら、
                // 複数行の関数定義（コード）である。
                if (nextTrimmed == "{" || nextTrimmed == "}" || IsIndentedCodeLine(line))
                    return true;

                remainingBodyProbeLines = 0; // Next line is prose — single-line example, allowed.
                                             // 次の行は散文 — 1行の例示、許容。
            }

            var trimmed = line.Trim();
            if (!IsFunctionDefinitionLine(trimmed))
                continue;

            // A function definition was found. Check if it spans multiple lines
            // (i.e., the next non-empty line looks like code, not prose).
            // 関数定義を発見。複数行にわたるか確認する（次の非空行がコード的か）。
            remainingBodyProbeLines = 2;
        }

        return false;
    }

    /// <summary>
    /// Pattern matching function/method definition signatures.
    /// 関数/メソッド定義シグネチャにマッチするパターン。
    /// </summary>
    private static readonly BoundedRegex s_functionDefPattern = new(
        @"(?:"
        // Python: def function_name( / Python: def 関数名(
        + @"\bdef\s+\w+\s*\("
        // JavaScript/TypeScript: function name( / JavaScript/TypeScript: function 名前(
        + @"|\bfunction\s+\w+\s*\("
        // Rust: fn name( / Rust: fn 名前(
        + @"|\bfn\s+\w+\s*[<(]"
        // Go: func name( or func (receiver) name( / Go: func 名前( or func (レシーバ) 名前(
        + @"|\bfunc\s+[\w(]"
        // C#/Java/C++: access_modifier return_type name( / C#/Java/C++: 修飾子 戻り値型 名前(
        + @"|(?:public|private|protected|internal|static)\s+.*\w+\s*\("
        + @")",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Checks if a line looks like a function/method definition.
    /// 行が関数/メソッド定義に見えるかを検査する。
    /// </summary>
    private static bool IsFunctionDefinitionLine(string trimmedLine)
    {
        return s_functionDefPattern.IsMatch(trimmedLine);
    }

    // ---------------------------------------------------------------
    // Heuristic 6: Fenced Code Blocks
    // ヒューリスティック 6: フェンスドコードブロック
    // ---------------------------------------------------------------

    /// <summary>
    /// Detects markdown-style fenced code blocks: lines starting with
    /// ``` (triple backtick) that enclose content.
    ///
    /// WHY this indicates source code:
    ///   Fenced code blocks are the standard way to embed source code in
    ///   markdown. When someone pastes ``` followed by code lines and
    ///   a closing ```, the content between the fences is almost certainly
    ///   source code. This pattern bypasses the other heuristics because
    ///   the code inside may not be indented or may be too short to
    ///   trigger line-count thresholds.
    ///
    /// マークダウン形式のフェンスドコードブロックを検出する。
    /// ``` （トリプルバッククォート）で始まる行が内容を囲む構造。
    ///
    /// なぜこれがソースコードの兆候か:
    ///   フェンスドコードブロックはマークダウンでソースコードを埋め込む
    ///   標準的な方法である。``` に続けてコード行を貼り、閉じ ``` で
    ///   終わっている場合、フェンス間の内容はほぼ確実にソースコードである。
    ///   このパターンはインデントがない場合や行数が少ない場合にも
    ///   他のヒューリスティックを回避して検出できる。
    /// </summary>
    private static bool HasFencedCodeBlock(string text)
    {
        char activeMarker = '\0';
        int activeMarkerLength = 0;
        int contentLines = 0;

        foreach (var rawLine in EnumerateLines(text))
        {
            // Check for fence delimiter (``` or ~~~ with optional language tag)
            // フェンス区切り（``` または ~~~＋任意の言語タグ）を検査
            if (TryReadMarkdownFence(rawLine, out var marker, out var markerLength))
            {
                if (activeMarker == '\0')
                {
                    // Opening fence / 開始フェンス
                    activeMarker = marker;
                    activeMarkerLength = markerLength;
                    contentLines = 0;
                }
                else if (marker == activeMarker && markerLength >= activeMarkerLength)
                {
                    // Closing fence — if there was at least 1 content line,
                    // this is a fenced code block.
                    // 閉じフェンス — 内容行が1行以上あれば、
                    // フェンスドコードブロックである。
                    if (contentLines >= 1)
                        return true;
                    activeMarker = '\0';
                    activeMarkerLength = 0;
                }
                else
                {
                    contentLines++;
                }
                continue;
            }

            if (activeMarker != '\0' && rawLine.Trim().Length > 0)
                contentLines++;
        }

        return false;
    }

    private static bool TryReadMarkdownFence(string line, out char marker, out int markerLength)
    {
        marker = '\0';
        markerLength = 0;
        var index = 0;
        while (index < line.Length && line[index] is ' ' or '\t')
            index++;
        if (index >= line.Length)
            return false;

        var candidate = line[index];
        if (candidate is not ('`' or '~'))
            return false;

        while (index < line.Length && line[index] == candidate)
        {
            markerLength++;
            index++;
        }

        if (markerLength < 3)
        {
            markerLength = 0;
            return false;
        }

        marker = candidate;
        return true;
    }

    private static IEnumerable<string> EnumerateLines(string text)
    {
        var start = 0;
        while (start <= text.Length)
        {
            var newline = text.IndexOf('\n', start);
            if (newline < 0)
            {
                yield return text[start..].TrimEnd('\r');
                yield break;
            }

            yield return text[start..newline].TrimEnd('\r');
            start = newline + 1;
        }
    }
}
