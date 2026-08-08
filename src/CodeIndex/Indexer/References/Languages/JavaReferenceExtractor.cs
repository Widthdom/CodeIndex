using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class JavaReferenceExtractor
{
    private static readonly IReadOnlySet<string> EmptyGenericParameterNames = new HashSet<string>(StringComparer.Ordinal);

    // Java compile-time type literal: `T.class`, `T[].class`, `outer.Inner.class` etc.
    private static readonly Regex DotClassArgRegex = new(
        @"(?<![\w$.])(?<arg>[A-Za-z_][\w.]*)\s*(?:\[\s*\])*\s*\.class\b",
        RegexOptions.Compiled);

    // JPMS module directives (`requires`, `uses`, and `provides`) are dependency edges, not calls.
    private static readonly Regex ModuleRequiresDirectiveReferenceRegex = new(
        @"^\s*requires\s+(?:transitive\s+|static\s+)*(?<name>[\w.]+)\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ModuleUsesDirectiveReferenceRegex = new(
        @"^\s*uses\s+(?<name>[\w.]+)\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ModuleProvidesDirectiveReferenceRegex = new(
        @"^\s*provides\s+(?<service>[\w.]+)\s+with\s+(?<implementations>[\w.,\s]+)\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Java type test (`instanceof Foo`).
    // Java の型テスト (`instanceof Foo`)。
    private static readonly Regex InstanceofRegex = new(
        @"(?<![\w$])instanceof\s+(?:(?:final|@[A-Za-z_]\w*(?:\s*\.\s*[A-Za-z_]\w*)*(?:\s*\([^)]*\))?)\s+)*(?<type>[A-Za-z_]\w*(?:\s*\.\s*[A-Za-z_]\w*)*(?:\s*<[^)\];{}]+>)?(?:\s*\[\s*\])*)",
        RegexOptions.Compiled);

    // Java constructor chain statement (first statement of a constructor body): `this(0);` / `super(42);`.
    // Also matches single-line ctor bodies like `Leaf(int x){super(x);}` where `{` precedes the chain call.
    // Java コンストラクタ連鎖文。`Leaf(int x){super(x);}` のように `{` 直後に連鎖文が続く
    // single-line body 形式にも対応する。
    private static readonly Regex CtorChainRegex = new(@"(?:^\s*|\{\s*)(?<kind>this|super)\s*\(", RegexOptions.Compiled);

    // Java access/method modifier set used by the same-line ctor scanner.
    // same-line ctor 本体のスキャナで使うアクセス / メソッド修飾子一覧。
    private static readonly HashSet<string> CtorModifiers = new(StringComparer.Ordinal)
    {
        "public", "private", "protected", "static", "final", "synchronized",
        "strictfp", "abstract", "native", "default"
    };

    public static void EmitCtorChainReferences(
        string preparedLine,
        Func<IReadOnlyList<SymbolRecord>> getEnclosingTypeCandidates,
        IReadOnlyList<SymbolRecord> symbols,
        string[] structuralLines,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (preparedLine.IndexOf('(') < 0
            || (preparedLine.IndexOf("this", StringComparison.Ordinal) < 0
                && preparedLine.IndexOf("super", StringComparison.Ordinal) < 0))
        {
            return;
        }

        var match = CtorChainRegex.Match(preparedLine);
        if (!match.Success)
            return;

        var enclosingType = ReferenceExtractor.FindInnermostClassLike(getEnclosingTypeCandidates(), lineNumber);
        if (enclosingType == null)
            return;

        // Prefer the innermost container when it is already a constructor of the enclosing class.
        // Otherwise, fall back to scanning all function-kind symbols by name match against the enclosing
        // class. Package-private ctors like `Leaf(int x){super(x);}` can appear without body ranges in
        // SymbolExtractor, so they are excluded from containerCandidates and the innermost container
        // becomes the class itself. Same-line ctor bodies are not emitted as function symbols at all
        // (the enum-member regex requires the line to end with `,`/`{`/`;`), so the final fallback
        // synthesizes a container from the current line when it matches a ctor declaration shape.
        // 外側クラスのコンストラクタが innermost container として既に見つかっていれば使う。
        // そうでなければ、関数シンボル全体を走査して外側クラスと同名のコンストラクタを
        // declaration StartLine ベースで引き直す。same-line body ctor は関数シンボル自体が
        // 存在しないので、行の shape から合成 container を作る。
        SymbolRecord? ctorContainer = null;
        if (container != null && container.Kind == "function"
            && string.Equals(container.Name, enclosingType.Name, StringComparison.Ordinal))
        {
            ctorContainer = container;
        }
        else
        {
            ctorContainer = FindEnclosingJavaConstructor(symbols, enclosingType, lineNumber)
                ?? TrySynthesizeSameLineCtor(preparedLine, enclosingType, lineNumber)
                ?? FindEnclosingJavaConstructorFromStructure(structuralLines, enclosingType, lineNumber);
        }

        if (ctorContainer == null)
            return;

        var kindToken = match.Groups["kind"].Value;
        string? target;
        if (kindToken == "this")
        {
            target = enclosingType.Name;
        }
        else
        {
            // Java base-list can span multiple lines (`class Leaf\n    extends Base`).
            // SymbolRecord.Signature only captures the first declaration line, so reconstruct
            // the enclosing type header from structural lines (reusing the C# helper — the
            // scanner stops at `;` / `{`, which also bounds Java class / record / enum headers).
            // Java の base-list も複数行にまたがる (`class Leaf\n    extends Base`)。
            // SymbolRecord.Signature は 1 行目しか持たないため、structural lines からヘッダを
            // 再構築する (C# 用ヘルパーを流用。`;` / `{` 終端は Java にも適用可)。
            var (_, _, headerText) = ReferenceExtractor.CollectCSharpRecordHeader(structuralLines, enclosingType.StartLine);
            target = ReferenceExtractor.ParseJavaBaseType(headerText);
            if (string.IsNullOrWhiteSpace(target))
                target = ReferenceExtractor.ParseJavaBaseType(enclosingType.Signature);
            if (string.IsNullOrWhiteSpace(target))
                return;
        }

        ReferenceExtractor.AddReference(
            references,
            seen,
            fileId,
            target!,
            match.Groups["kind"].Index,
            "call",
            context,
            lineNumber,
            ctorContainer,
            sourceLength: kindToken.Length);
    }

    public static (SymbolRecord Synthetic, int NameIndex, int OpenBraceIndex, int CloseBraceIndex)?
        TryBuildSameLineCtorSpan(
            string preparedLine,
            int lineNumber,
            Func<IReadOnlyList<SymbolRecord>> getEnclosingTypeCandidates)
    {
        var span = TryExtractSameLineCtorSpan(preparedLine);
        if (span is null)
            return null;
        var enclosingType = ReferenceExtractor.FindInnermostClassLike(getEnclosingTypeCandidates(), lineNumber);
        if (enclosingType == null)
            return null;
        if (!string.Equals(span.Value.Name, enclosingType.Name, StringComparison.Ordinal))
            return null;

        var synthetic = BuildSameLineCtorContainer(enclosingType, lineNumber);
        return (synthetic, span.Value.NameIndex, span.Value.OpenBraceIndex, span.Value.CloseBraceIndex);
    }

    /// <summary>
    /// Depth-aware scanner for `@Annot ... <T extends Comparable<Integer>> Ctor(...) { ... }`
    /// style declarations. Returns the constructor name when the line opens a ctor body, or
    /// null otherwise. Handles qualified annotations (`@demo.Ann`), annotation argument lists
    /// with nested parens, and nested generic bounds that a flat regex cannot balance.
    /// 修飾付きアノテーション・引数付きアノテーション・入れ子の generic 境界を含む
    /// same-line ctor 宣言を depth-aware にスキャンして ctor 名を返すヘルパー。
    /// </summary>
    public static string? TryExtractCtorNameFromLine(string line)
        => TryExtractSameLineCtorSpan(line)?.Name;

    /// <summary>
    /// Same as <see cref="TryExtractCtorNameFromLine"/> but also returns the ctor name
    /// index, body-open `{` index, and the matching body-close `}` index on the same line.
    /// `TryExtractCtorNameFromLine` と同じスキャナだが、ctor 名位置・`{` 位置・対応する
    /// `}` 位置もまとめて返すバリアント。
    /// </summary>
    public static ReferenceExtractor.JavaSameLineCtorSpan? TryExtractSameLineCtorSpan(string line)
    {
        if (line.IndexOf('(') < 0 || line.IndexOf('{') < 0)
            return null;

        int i = 0;
        int n = line.Length;

        SkipWhitespace(line, ref i);

        // Consume annotations and access / misc modifiers in any order so that forms such as
        // `public @Deprecated Leaf(...)` and `@demo.Ann private Leaf(...)` are both accepted
        // instead of bailing when an annotation appears after a modifier keyword.
        // アノテーションと access modifier は順不同で交互に現れ得るため、両方を 1 つのループで
        // 反復消費する。途中でどちらでもないトークンが来たら ctor 名（または `<...>`）へ遷移する。
        while (true)
        {
            SkipWhitespace(line, ref i);
            if (i < n && line[i] == '@')
            {
                i++;
                if (!ConsumeQualifiedIdentifier(line, ref i))
                    return null;
                SkipWhitespace(line, ref i);
                if (i < n && line[i] == '(')
                {
                    if (!SkipBalancedParens(line, ref i))
                        return null;
                }
                continue;
            }

            int wordStart = i;
            while (i < n && char.IsLetter(line[i]))
                i++;
            if (i == wordStart)
                break;
            var word = line.Substring(wordStart, i - wordStart);
            if (!CtorModifiers.Contains(word))
            {
                i = wordStart;
                break;
            }
        }

        SkipWhitespace(line, ref i);

        if (i < n && line[i] == '<')
        {
            if (!SkipBalancedAngles(line, ref i))
                return null;
            SkipWhitespace(line, ref i);
        }

        int nameStart = i;
        if (!ConsumeIdentifier(line, ref i))
            return null;
        var name = line.Substring(nameStart, i - nameStart);

        SkipWhitespace(line, ref i);
        if (i >= n || line[i] != '(')
            return null;
        if (!SkipBalancedParens(line, ref i))
            return null;
        SkipWhitespace(line, ref i);

        if (i + 6 <= n && string.CompareOrdinal(line, i, "throws", 0, 6) == 0 &&
            (i + 6 == n || char.IsWhiteSpace(line[i + 6])))
        {
            i += 6;
            while (i < n && line[i] != '{')
                i++;
        }

        SkipWhitespace(line, ref i);
        if (i >= n || line[i] != '{')
            return null;

        int openBrace = i;
        int closeBrace = FindMatchingBraceClose(line, openBrace);
        return new ReferenceExtractor.JavaSameLineCtorSpan(name, nameStart, openBrace, closeBrace);
    }

    /// <summary>
    /// Find the Java constructor symbol that encloses the given line number by name-matching
    /// the enclosing class. Covers package-private ctors that SymbolExtractor records without
    /// body ranges (so they are absent from containerCandidates).
    /// 指定行を内包する Java コンストラクタを、外側クラス名との一致で走査して探す。
    /// body 範囲を持たない package-private ctor でも declaration StartLine 起点で拾える。
    /// </summary>
    private static SymbolRecord? FindEnclosingJavaConstructor(
        IReadOnlyList<SymbolRecord> symbols,
        SymbolRecord enclosingType,
        int lineNumber)
    {
        var classStart = enclosingType.BodyStartLine ?? enclosingType.StartLine;
        var classEnd = enclosingType.BodyEndLine ?? enclosingType.EndLine;

        // Collect all ctor-name function symbols declared inside the class body at or before
        // `lineNumber`. When the symbol carries a body range, we can check the range directly.
        // Otherwise we fall back to the most recent declaration line at or before `lineNumber`,
        // bounded above by the next same-name ctor declaration (so the reference is still
        // attributed to the constructor that actually owns the chain line).
        // body 範囲があるシンボルは範囲判定、body 範囲の無い package-private ctor は
        // 次の同名 ctor 宣言の直前までを占有範囲として扱う。
        List<SymbolRecord>? candidates = null;
        foreach (var symbol in symbols)
        {
            if (symbol.Kind != "function")
                continue;
            if (!string.Equals(symbol.Name, enclosingType.Name, StringComparison.Ordinal))
                continue;
            if (symbol.StartLine < classStart || symbol.StartLine > classEnd)
                continue;
            if (symbol.StartLine > lineNumber)
                continue;
            (candidates ??= new List<SymbolRecord>(4)).Add(symbol);
        }

        if (candidates == null)
            return null;

        candidates.Sort((a, b) => a.StartLine.CompareTo(b.StartLine));

        SymbolRecord? best = null;
        for (int i = 0; i < candidates.Count; i++)
        {
            var symbol = candidates[i];
            var hasBodyRange = symbol.BodyStartLine != null && symbol.BodyEndLine != null;
            int rangeEnd;
            if (hasBodyRange)
            {
                rangeEnd = symbol.BodyEndLine!.Value;
            }
            else
            {
                // Extend to just before the next same-name ctor declaration, else to the end
                // of the enclosing class body. This approximates the ctor's true range when
                // SymbolExtractor could not parse the braces (e.g. package-private Java ctors).
                // 次の同名 ctor 宣言の直前、もしくは外側クラス body の終端まで拡張する。
                var nextStart = (i + 1 < candidates.Count) ? candidates[i + 1].StartLine : classEnd + 1;
                rangeEnd = nextStart - 1;
            }

            if (rangeEnd < lineNumber)
                continue;

            // Innermost / most recent declaration wins.
            // 一番近い宣言行を選ぶ。
            if (best == null || symbol.StartLine > best.StartLine)
                best = symbol;
        }

        return best;
    }

    /// <summary>
    /// Walk structural lines within the enclosing type body to recover a Java constructor
    /// whose header does not match SymbolExtractor's return-type-required method regex. Java
    /// constructors have no return type, so plain forms like `Leaf() {` / `Shade(int code) {`
    /// are not emitted as function symbols; this fallback parses them directly so chain calls
    /// can still be attributed to the owning ctor. Handles Allman style (`Leaf()\n{`),
    /// multi-line parameter lists, and multi-line `throws` clauses by delegating body range
    /// resolution to <see cref="SymbolExtractor.FindJavaBraceRange"/> (paren/bracket/angle
    /// depth aware, and string/comment/text-block aware).
    /// 外側型の body 内を走査して、return 型を持たない Java コンストラクタ
    /// （`Leaf() {` / `Shade(int code) {` / Allman 形式の `Leaf()\n{` / 複数行 parameter /
    /// 複数行 `throws` など）を復元する。SymbolExtractor のメソッド regex は戻り値型を必須
    /// とするため function シンボルが作られない。body 範囲の決定は FindJavaBraceRange に
    /// 委譲し、`()` / `[]` / `<>` の深さと文字列・コメント・text block を考慮する。
    /// </summary>
    private static SymbolRecord? FindEnclosingJavaConstructorFromStructure(
        string[] structuralLines,
        SymbolRecord enclosingType,
        int lineNumber)
    {
        var classBodyStart = enclosingType.BodyStartLine ?? enclosingType.StartLine;
        var classBodyEnd = enclosingType.BodyEndLine ?? enclosingType.EndLine;
        if (classBodyStart <= 0 || classBodyEnd < classBodyStart)
            return null;
        if (lineNumber < classBodyStart || lineNumber > classBodyEnd)
            return null;

        int i = classBodyStart - 1; // 0-based
        var lastIndex = Math.Min(structuralLines.Length - 1, classBodyEnd - 1);
        while (i <= lastIndex)
        {
            if (!TryMatchCtorHeaderStart(structuralLines, i, lastIndex, enclosingType.Name))
            {
                i++;
                continue;
            }

            int declStart = i + 1; // 1-based

            // Let FindJavaBraceRange scan the entire header + body from the ctor start line.
            // It tracks paren / bracket / angle depth (so multi-line parameter lists,
            // annotations, and bounded generics don't mislead the body scan) and returns the
            // matching `}` as BodyEndLine. A `;`-terminated line without `{` yields
            // BodyEndLine == null; that shape is not a constructor body and is skipped.
            // FindJavaBraceRange がヘッダ + body を一気に走査する。`()`/`[]`/`<>` 深さを追跡
            // するため、複数行の parameter / annotation / bounded generic で誤検出しない。
            var (endLine, _, bodyEndLine) = SymbolExtractor.FindJavaBraceRange(structuralLines, i, 0);
            if (bodyEndLine is null)
            {
                // Not a body: advance past whatever the scanner consumed to avoid re-scanning.
                // body を持たない宣言（`;` 終端など）は ctor ではないので scanner が消費した
                // 範囲だけ進めて次行へ。
                i = Math.Max(i + 1, endLine);
                continue;
            }

            int bodyEnd = bodyEndLine.Value;
            if (lineNumber >= declStart && lineNumber <= bodyEnd)
            {
                return new SymbolRecord
                {
                    Kind = "function",
                    Name = enclosingType.Name,
                    Line = declStart,
                    StartLine = declStart,
                    EndLine = bodyEnd,
                    BodyStartLine = declStart,
                    BodyEndLine = bodyEnd,
                    ContainerKind = enclosingType.Kind,
                    ContainerName = enclosingType.Name,
                    ContainerQualifiedName = enclosingType.ContainerQualifiedName,
                    Visibility = enclosingType.Visibility,
                };
            }

            // Skip past this ctor's body to avoid re-parsing nested declarations inside it.
            // この ctor の body 以降に飛ばし、内部の宣言を誤って拾わないようにする。
            i = Math.Max(i + 1, bodyEnd);
        }

        return null;
    }

    /// <summary>
    /// Return true when line <paramref name="startIndex"/> starts a Java constructor header
    /// for <paramref name="ctorName"/>. Accepts both same-line forms (`Leaf() {`) and
    /// multi-line forms (Allman `Leaf()\n{`, multi-line parameter lists, multi-line `throws`).
    /// The detector confirms that after modifiers / annotations / optional generics, the ctor
    /// name appears immediately followed by `(` (possibly on the next non-blank line). The
    /// body resolver downstream (<see cref="SymbolExtractor.FindJavaBraceRange"/>) rejects
    /// `;`-terminated declarations without a body, so false positives like enum constants
    /// `Shade(1);` would still be discarded there even if this detector returns true.
    /// 指定行が Java コンストラクタヘッダの先頭かを判定する。modifier / annotation /
    /// optional generics を消費したあと ctor 名が続き、その直後（または次の非空行）に `(`
    /// が現れるかを検査する。body 側の判定は FindJavaBraceRange が担うため、`;` 終端の
    /// enum 定数 `Shade(1);` のような偽陽性は下流で落ちる。
    /// </summary>
    private static bool TryMatchCtorHeaderStart(
        string[] lines,
        int startIndex,
        int endIndex,
        string ctorName)
    {
        if (startIndex < 0 || startIndex >= lines.Length)
            return false;

        var line = lines[startIndex];
        int i = 0;
        int n = line.Length;

        SkipWhitespace(line, ref i);

        // Consume modifiers and @annotations in any order (mirrors TryExtractSameLineCtorSpan).
        // modifier と annotation は順不同で交互に現れ得るため、両方を同一ループで消費する。
        while (true)
        {
            SkipWhitespace(line, ref i);
            if (i < n && line[i] == '@')
            {
                i++;
                if (!ConsumeQualifiedIdentifier(line, ref i))
                    return false;
                SkipWhitespace(line, ref i);
                if (i < n && line[i] == '(')
                {
                    if (!SkipBalancedParens(line, ref i))
                        return false;
                }
                continue;
            }

            int wordStart = i;
            while (i < n && char.IsLetter(line[i]))
                i++;
            if (i == wordStart)
                break;
            var word = line.Substring(wordStart, i - wordStart);
            if (!CtorModifiers.Contains(word))
            {
                i = wordStart;
                break;
            }
        }

        SkipWhitespace(line, ref i);

        if (i < n && line[i] == '<')
        {
            if (!SkipBalancedAngles(line, ref i))
                return false;
            SkipWhitespace(line, ref i);
        }

        int nameStart = i;
        if (!ConsumeIdentifier(line, ref i))
            return false;
        var name = line.Substring(nameStart, i - nameStart);
        if (!string.Equals(name, ctorName, StringComparison.Ordinal))
            return false;

        // The next non-whitespace token (possibly on a later line) must be `(`. Anything else
        // means this is not a ctor header — e.g. `Leaf leaf = ...` (field/variable) or
        // `Leaf method() ...` (method with Leaf as return type).
        // ctor 名の直後の非空白（次行以降でも可）は `(` でなければならない。
        SkipWhitespace(line, ref i);
        if (i < n)
            return line[i] == '(';

        for (int j = startIndex + 1; j <= endIndex && j < lines.Length; j++)
        {
            var next = lines[j];
            int k = 0;
            while (k < next.Length && char.IsWhiteSpace(next[k]))
                k++;
            if (k == next.Length)
                continue;
            return next[k] == '(';
        }
        return false;
    }

    /// <summary>
    /// When the current line itself carries a same-line Java constructor body (for example
    /// `Leaf(int x) { super(x); }`), synthesize a function-kind container so the chain rewrite
    /// still attaches the edge to the owning constructor. SymbolExtractor does not emit a
    /// function symbol for this shape because the enum-member regex requires the line to end
    /// with `,`, `{`, or `;`.
    /// same-line body の Java コンストラクタ（`Leaf(int x){super(x);}` など）では SymbolExtractor
    /// が function 化しないため、行自体を直接スキャンして合成 container を作る。
    /// </summary>
    private static SymbolRecord? TrySynthesizeSameLineCtor(
        string preparedLine,
        SymbolRecord enclosingType,
        int lineNumber)
    {
        var name = TryExtractCtorNameFromLine(preparedLine);
        if (name is null)
            return null;
        if (!string.Equals(name, enclosingType.Name, StringComparison.Ordinal))
            return null;

        return BuildSameLineCtorContainer(enclosingType, lineNumber);
    }

    private static SymbolRecord BuildSameLineCtorContainer(SymbolRecord enclosingType, int lineNumber)
        => new SymbolRecord
        {
            Kind = "function",
            Name = enclosingType.Name,
            Line = lineNumber,
            StartLine = lineNumber,
            EndLine = lineNumber,
            BodyStartLine = lineNumber,
            BodyEndLine = lineNumber,
            ContainerKind = enclosingType.Kind,
            ContainerName = enclosingType.Name,
            ContainerQualifiedName = enclosingType.ContainerQualifiedName,
            Visibility = enclosingType.Visibility,
        };

    /// <summary>
    /// Find the `}` that closes the block opened at <paramref name="openBrace"/>, respecting
    /// string / char literals and nested `{ ... }` pairs. Returns -1 when no matching close is
    /// on the same line. Used to bound per-line Java same-line ctor body ranges.
    /// 同一行内で `{` と対応する `}` を探す。文字列・文字リテラルと入れ子の `{}` を尊重する。
    /// </summary>
    private static int FindMatchingBraceClose(string line, int openBrace)
    {
        int depth = 0;
        int n = line.Length;
        for (int i = openBrace; i < n; i++)
        {
            var c = line[i];
            if (c == '"' || c == '\'')
            {
                var quote = c;
                i++;
                while (i < n)
                {
                    var ch = line[i];
                    if (ch == '\\' && i + 1 < n) { i += 2; continue; }
                    if (ch == quote) break;
                    i++;
                }
                if (i >= n) return -1;
                continue;
            }
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    private static void SkipWhitespace(string text, ref int i)
    {
        while (i < text.Length && char.IsWhiteSpace(text[i]))
            i++;
    }

    private static bool ConsumeIdentifier(string text, ref int i)
    {
        if (i >= text.Length)
            return false;
        var c = text[i];
        if (!(char.IsLetter(c) || c == '_' || c == '$'))
            return false;
        i++;
        while (i < text.Length)
        {
            c = text[i];
            if (char.IsLetterOrDigit(c) || c == '_' || c == '$')
                i++;
            else
                break;
        }
        return true;
    }

    private static bool ConsumeQualifiedIdentifier(string text, ref int i)
    {
        if (!ConsumeIdentifier(text, ref i))
            return false;
        while (i < text.Length && text[i] == '.')
        {
            int save = i;
            i++;
            if (!ConsumeIdentifier(text, ref i))
            {
                i = save;
                break;
            }
        }
        return true;
    }

    private static bool SkipBalancedParens(string text, ref int i)
    {
        if (i >= text.Length || text[i] != '(')
            return false;
        int depth = 0;
        for (; i < text.Length; i++)
        {
            var c = text[i];
            // Skip string / char literals so an embedded `)` inside `@Ann(text=")")` does not
            // prematurely close the annotation argument list.
            // 文字列・文字リテラル内の `)` で annotation 引数を早期終了しないようスキップする。
            if (c == '"' || c == '\'')
            {
                var quote = c;
                i++;
                while (i < text.Length)
                {
                    var ch = text[i];
                    if (ch == '\\' && i + 1 < text.Length) { i += 2; continue; }
                    if (ch == quote) break;
                    i++;
                }
                if (i >= text.Length) return false;
                continue;
            }
            if (c == '(') depth++;
            else if (c == ')')
            {
                depth--;
                if (depth == 0)
                {
                    i++;
                    return true;
                }
            }
        }
        return false;
    }

    private static bool SkipBalancedAngles(string text, ref int i)
    {
        if (i >= text.Length || text[i] != '<')
            return false;
        int depth = 0;
        for (; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '<') depth++;
            else if (c == '>')
            {
                depth--;
                if (depth == 0)
                {
                    i++;
                    return true;
                }
            }
            else if (c == '{' || c == ';')
            {
                return false;
            }
        }
        return false;
    }

}
