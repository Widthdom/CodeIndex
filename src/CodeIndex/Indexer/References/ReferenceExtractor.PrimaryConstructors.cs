using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    internal readonly record struct JavaSameLineCtorSpan(
        string Name,
        int NameIndex,
        int OpenBraceIndex,
        int CloseBraceIndex);

    /// <summary>
    /// Depth-aware scanner for `@Annot ... <T extends Comparable<Integer>> Ctor(...) { ... }`
    /// style declarations. Returns the constructor name when the line opens a ctor body, or
    /// null otherwise. Handles qualified annotations (`@demo.Ann`), annotation argument lists
    /// with nested parens, and nested generic bounds that a flat regex cannot balance.
    /// 修飾付きアノテーション・引数付きアノテーション・入れ子の generic 境界を含む
    /// same-line ctor 宣言を depth-aware にスキャンして ctor 名を返すヘルパー。
    /// </summary>
    internal static string? TryExtractJavaCtorNameFromLine(string line)
        => JavaReferenceExtractor.TryExtractCtorNameFromLine(line);

    /// <summary>
    /// Same as <see cref="TryExtractJavaCtorNameFromLine"/> but also returns the ctor name
    /// index, body-open `{` index, and the matching body-close `}` index on the same line.
    /// `TryExtractJavaCtorNameFromLine` と同じスキャナだが、ctor 名位置・`{` 位置・対応する
    /// `}` 位置もまとめて返すバリアント。
    /// </summary>
    internal static JavaSameLineCtorSpan? TryExtractJavaSameLineCtorSpan(string line)
        => JavaReferenceExtractor.TryExtractSameLineCtorSpan(line);

    private static void AddChainReference(
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string name,
        int column,
        string referenceKind,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        var dedupeKey = CreateReferenceDedupeKey(fileId, null, lineNumber, column, referenceKind, name, container);
        if (!seen.Add(dedupeKey))
            return;

        TryAddReference(references, new ReferenceRecord
        {
            FileId = fileId,
            SymbolName = name,
            ReferenceKind = referenceKind,
            Line = lineNumber,
            Column = column,
            Context = context,
            ContainerKind = container?.Kind,
            ContainerName = container?.Name,
        });
    }

    private static void EmitMethodGroupReferences(
        string language,
        string preparedLine,
        HashSet<string>? callableDefinitionNames,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (callableDefinitionNames == null || callableDefinitionNames.Count == 0)
            return;

        foreach (Match match in MethodGroupReferenceRegex.Matches(preparedLine))
        {
            var contextTargetGroup = match.Groups["contextTarget"];
            if (contextTargetGroup.Success && MethodGroupContextTargetIgnoreNames.Contains(contextTargetGroup.Value))
                continue;
            if (!contextTargetGroup.Success)
            {
                var prefix = preparedLine.AsSpan(0, match.Groups["name"].Index).TrimEnd();
                if (prefix.EndsWith("+=", StringComparison.Ordinal) || prefix.EndsWith("-=", StringComparison.Ordinal))
                    continue;
            }

            var nameGroup = match.Groups["name"];
            var rawName = nameGroup.Value;
            var name = language == "csharp" ? NormalizeCSharpIdentifier(rawName) : rawName;
            if (!callableDefinitionNames.Contains(name))
                continue;

            var container = resolveContainerForColumn(nameGroup.Index);
            AddChainReference(references, seen, fileId, name, nameGroup.Index, "call", context, lineNumber, container);
        }
    }

    /// <summary>
    /// Build a list of line ranges paired with synthetic function-kind containers for C# primary
    /// constructor declarations that carry a base primary-constructor call. This covers records
    /// (`record Child(int x) : Parent(x)`), C# 12 classes (`class Child(int x) : Parent(x)`) and
    /// structs (`struct Child(int x) : Parent(x)`), including the multi-line form where
    /// `: Parent(x)` sits on a continuation line. SymbolExtractor does not synthesize a separate
    /// ctor symbol for the implicit primary constructor, so the `Parent(x)` reference would
    /// otherwise land on `container = null` (when the declaration line has no body range) or on
    /// the declaring type itself. The synthetic container covers the header range only; methods
    /// inside a braced body still resolve to their real containers via FindInnermostContainer,
    /// and within the end line the override is limited to columns before the terminator so body
    /// calls sharing the same line (e.g. `record Child(int V) : Parent(V) { ... Add(V, 1); }`)
    /// are not pulled onto the synthetic ctor.
    /// C# の primary constructor 宣言に対して合成 function コンテナの (start, end, endColumn, container)
    /// リストを作る。record だけでなく C# 12 の class / struct primary constructor も対象にし、
    /// 宣言ヘッダーの範囲（end line は終端 `;` / `{` のカラムまで）だけ合成 ctor に差し替えることで、
    /// 同一行 braced body の呼び出しや後続メソッドは本来の container に残る。
    /// </summary>
    private static List<(int StartLine, int StartColumn, int EndLine, int EndColumn, SymbolRecord Container)> BuildCSharpPrimaryCtorContainers(
        string language,
        IReadOnlyList<SymbolRecord> symbols,
        string[] structuralLines)
    {
        if (language != "csharp")
            return [];

        var ranges = new List<(int, int, int, int, SymbolRecord)>(4);
        foreach (var symbol in symbols)
        {
            // SymbolExtractor stores C# records as Kind=class and C# 12 structs as Kind=struct.
            // Interfaces / enums / delegates cannot have primary constructors in C# so skip them.
            // C# record は Kind=class、C# 12 struct は Kind=struct として登録されるため両方対象。
            if (symbol.Kind != "class" && symbol.Kind != "struct")
                continue;
            var signature = symbol.Signature;
            if (string.IsNullOrWhiteSpace(signature))
                continue;

            // SymbolRecord.Signature only captures the first declaration line, so the first-line
            // regex filter misses split-line primary-ctor forms such as
            // `public record Child\n(\n    int Value\n)\n    : Parent(Value);`. Walk the
            // structural-masked lines from StartLine until we hit `;` / `{` and run the
            // primary-ctor detection on the joined header text instead.
            // 宣言の signature は 1 行目だけしか持たないので、`record` / `class` / `struct` と
            // `(` を別行に分ける書式では先頭行 regex の前段フィルタが空振りする。ここでは
            // structuralLines から `;` / `{` までヘッダーを連結し、連結後のテキストで判定する。
            var (headerEndLine, headerEndColumn, headerText) = CollectCSharpRecordHeader(structuralLines, symbol.StartLine);
            if (!IsCSharpPrimaryCtorHeader(headerText))
                continue;
            if (!HasCSharpBasePrimaryCtorCall(headerText))
                continue;

            // Restrict the synthetic container to the actual declaration span, starting at the
            // `class` / `struct` / `record` keyword column on the start line. Without this
            // same-line tokens BEFORE the keyword (e.g. attribute arguments in
            // `[Attr(Helper.Get())] public class Child(int x) : Parent(x) {}`) would get
            // attributed to the synthetic ctor and pollute callers / impact with phantom
            // `Child` callers for `Attr` and `Helper.Get`.
            // 合成 ctor コンテナを本物の宣言範囲に限定する。`class` / `struct` / `record`
            // キーワード位置より前（同一行の属性呼び出しなど）は本来の container に残す。
            var startColumn = FindCSharpPrimaryCtorKeywordColumn(structuralLines, symbol.StartLine);

            var synthetic = new SymbolRecord
            {
                FileId = symbol.FileId,
                Kind = "function",
                Name = symbol.Name,
                Line = symbol.Line,
                StartLine = symbol.StartLine,
                EndLine = headerEndLine,
                BodyStartLine = symbol.StartLine,
                BodyEndLine = headerEndLine,
                Signature = signature,
                ContainerKind = symbol.ContainerKind,
                ContainerName = symbol.ContainerName,
                ContainerQualifiedName = symbol.ContainerQualifiedName,
                FamilyKey = symbol.FamilyKey,
                Visibility = symbol.Visibility,
            };

            ranges.Add((symbol.StartLine, startColumn, headerEndLine, headerEndColumn, synthetic));
        }

        return ranges;
    }

    private static int FindCSharpPrimaryCtorKeywordColumn(string[] structuralLines, int startLine)
    {
        var idx = Math.Max(0, startLine - 1);
        if (idx >= structuralLines.Length)
            return 0;
        var line = structuralLines[idx];
        foreach (var keyword in CSharpPrimaryCtorKeywords)
        {
            int pos = 0;
            while (pos < line.Length)
            {
                var found = line.IndexOf(keyword, pos, StringComparison.Ordinal);
                if (found < 0) break;
                var before = found == 0 ? ' ' : line[found - 1];
                var afterIdx = found + keyword.Length;
                var after = afterIdx < line.Length ? line[afterIdx] : ' ';
                if (!IsCSharpIdentifierPart(before) && !IsCSharpIdentifierPart(after))
                    return found;
                pos = found + 1;
            }
        }
        return 0;
    }

    private static readonly string[] CSharpPrimaryCtorKeywords = { "record", "class", "struct" };

    private static bool IsCSharpIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// Walk structural-masked lines starting at the 1-based <paramref name="startLine"/> and collect
    /// the declaration header up to (but not including) the first `;` or `{` that sits outside a
    /// string or comment. Returns the 1-based line number where the terminator was found (or the
    /// final line index when none was found) and the joined header text for further parsing.
    /// Reused for record primary-ctor container synthesis and multi-line `: base(...)` resolution.
    /// structuralLines を使って、class / struct / record 宣言ヘッダーを最初の `;` / `{` まで連結する。
    /// record primary-ctor のコンテナ合成と、複数行 `: base(...)` 解決の両方で使う。
    /// </summary>
    internal static (int EndLine, int EndColumn, string Text) CollectCSharpRecordHeader(string[] structuralLines, int startLine)
    {
        var startIdx = Math.Max(0, startLine - 1);
        if (structuralLines.Length == 0)
            return (startLine, int.MaxValue, string.Empty);

        // Depth-aware termination so that `{` / `;` inside annotation arg lists (e.g. the `{` in
        // `@Ann({A.class, B.class})`) or attribute-argument brackets does not cut the header off
        // before the real base-list terminator, which would silently drop the base type.
        // We intentionally do NOT track `<` / `>` as generic depth here: comparison operators
        // inside annotation / attribute expressions (e.g. `[Attr(Flag = 1 < 2)]` or
        // `@Ann(flag = 1 < 2)`) are raised as `<` without a matching `>`, so angle-depth tracking
        // would leave the counter pinned above zero and silently drop the real top-level `{` / `;`
        // terminator, letting the synthetic primary-ctor container or the Java base-type parse
        // swallow everything up to EOF. `{` / `;` cannot legally appear inside a top-level
        // `<...>` generic arg list in either C# or Java, so paren/bracket masking is sufficient.
        // EndColumn tracks the column index of the top-level terminator on the end line, or
        // int.MaxValue when no terminator was found (end-of-file), so call-site-scoped container
        // overrides can restrict themselves to the header portion of the end line.
        // アノテーション引数の `{` などを本当のヘッダ終端と誤認しないよう、`()` / `[]` の深さを追いながら
        // 最初の top-level `;` / `{` でのみ終了する。`<` / `>` は annotation / attribute 式内の比較演算子で
        // 非対称に現れうるため generic 深度として扱わない。
        // EndColumn は end line 上の終端 `;` / `{` の位置を返す（終端が無ければ int.MaxValue）。
        var sb = new System.Text.StringBuilder();
        int parenDepth = 0;
        int bracketDepth = 0;
        // Comment / string awareness so unbalanced `(` / `[` / `{` / `;` inside a line
        // comment, block comment, or string literal never advances the depth counters,
        // fires the terminator, or leaks into the returned header text. For Java `extends`
        // headers the structuralLines array is an unmasked clone (StructuralLineMasker is a
        // no-op for Java), so this is what keeps `class Leaf extends Root /* ( stray [ */ {`
        // from pinning parenDepth / bracketDepth at 1 and skipping the real `{` terminator,
        // and it also prevents ParseJavaBaseType from seeing the comment body when it parses
        // the header text downstream.
        // コメント・文字列内の不均衡な `(` / `[` / `{` / `;` を terminator 判定・連結テキスト双方から除外する。
        bool inBlockComment = false;
        bool inString = false;
        for (int i = startIdx; i < structuralLines.Length; i++)
        {
            var line = structuralLines[i];
            char[]? masked = null;
            var terminatorIdx = -1;
            void MaskChar(int index)
            {
                masked ??= line.ToCharArray();
                masked[index] = ' ';
            }

            void MaskRange(int start, int endExclusive)
            {
                masked ??= line.ToCharArray();
                for (int k = start; k < endExclusive; k++)
                    masked[k] = ' ';
            }

            for (int j = 0; j < line.Length; j++)
            {
                var c = line[j];

                if (inBlockComment)
                {
                    MaskChar(j);
                    if (c == '*' && j + 1 < line.Length && line[j + 1] == '/')
                    {
                        inBlockComment = false;
                        MaskChar(j + 1);
                        j++;
                    }
                    continue;
                }

                if (inString)
                {
                    MaskChar(j);
                    if (c == '\\' && j + 1 < line.Length)
                    {
                        MaskChar(j + 1);
                        j++;
                        continue;
                    }
                    if (c == '"')
                        inString = false;
                    continue;
                }

                if (c == '/' && j + 1 < line.Length)
                {
                    if (line[j + 1] == '/')
                    {
                        MaskRange(j, line.Length);
                        break;
                    }
                    if (line[j + 1] == '*')
                    {
                        inBlockComment = true;
                        MaskChar(j);
                        MaskChar(j + 1);
                        j++;
                        continue;
                    }
                }

                if (c == '"')
                {
                    inString = true;
                    MaskChar(j);
                    continue;
                }

                if (c == '\'')
                {
                    // Rust / OCaml lifetime annotation vs. char literal: only skip when a
                    // closing `'` exists within ~12 chars on this line.
                    // Rust の lifetime と char literal を短距離の閉じ `'` の有無で見分ける。
                    var closeIdx = -1;
                    var limit = Math.Min(line.Length, j + 12);
                    for (int k = j + 1; k < limit; k++)
                    {
                        if (line[k] == '\\' && k + 1 < line.Length)
                        {
                            k++;
                            continue;
                        }
                        if (line[k] == '\'')
                        {
                            closeIdx = k;
                            break;
                        }
                    }
                    if (closeIdx > 0)
                    {
                        MaskRange(j, closeIdx + 1);
                        j = closeIdx;
                    }
                    continue;
                }

                if (c == '(') parenDepth++;
                else if (c == ')') { if (parenDepth > 0) parenDepth--; }
                else if (c == '[') bracketDepth++;
                else if (c == ']') { if (bracketDepth > 0) bracketDepth--; }
                else if ((c == ';' || c == '{') && parenDepth == 0 && bracketDepth == 0)
                {
                    terminatorIdx = j;
                    break;
                }
            }

            if (terminatorIdx >= 0)
            {
                if (masked == null)
                    sb.Append(line, 0, terminatorIdx);
                else
                    sb.Append(masked, 0, terminatorIdx);
                return (i + 1, terminatorIdx, sb.ToString());
            }

            if (masked == null)
                sb.Append(line);
            else
                sb.Append(masked);
            sb.Append('\n');
        }

        return (structuralLines.Length, int.MaxValue, sb.ToString());
    }

    /// <summary>
    /// Returns true when the C# type header text carries a base-list entry that looks like a
    /// primary-constructor call (contains `(`). Accepts multi-line header text already joined by
    /// <see cref="CollectCSharpRecordHeader"/>.
    /// C# 型ヘッダー（複数行連結後でも可）の base-list 先頭エントリが `(` を含むかを判定する。
    /// </summary>
    /// <summary>
    /// Return true when a joined C# type-declaration header (possibly spanning multiple lines,
    /// including line-broken primary-ctor parens) looks like a primary-constructor declaration.
    /// Accepts `record Child(...)`, `record class Child(...)`, `record struct Child(...)`,
    /// C# 12 `class Child(...)`, `struct Child(...)`, generic arity such as `class Child<T>(...)`,
    /// and the split-line form where `record Child\n(\n ... )` places the `(` on a continuation line.
    /// 連結済みの C# 宣言ヘッダーが primary-ctor 宣言かを判定する。`record` だけでなく C# 12 の
    /// `class` / `struct` primary constructor も対象にし、`(` が別行に分かれる書式にも対応する。
    /// </summary>
    private static bool IsCSharpPrimaryCtorHeader(string headerText)
    {
        if (string.IsNullOrWhiteSpace(headerText))
            return false;
        return CSharpPrimaryCtorHeaderRegex.IsMatch(headerText);
    }

    private static bool HasCSharpBasePrimaryCtorCall(string headerText)
    {
        var text = headerText.TrimEnd();
        if (text.EndsWith(";", StringComparison.Ordinal))
        {
            var end = text.Length - 1;
            while (end > 0 && char.IsWhiteSpace(text[end - 1]))
                end--;
            text = text.Substring(0, end);
        }
        if (text.EndsWith("{", StringComparison.Ordinal))
        {
            var end = text.Length - 1;
            while (end > 0 && char.IsWhiteSpace(text[end - 1]))
                end--;
            text = text.Substring(0, end);
        }

        var colonIndex = FindSignatureColonIndex(text);
        if (colonIndex < 0)
            return false;

        var baseList = text.Substring(colonIndex + 1);
        var whereMatch = CSharpWhereClauseRegex.Match(baseList);
        if (whereMatch.Success)
            baseList = baseList.Substring(0, whereMatch.Index);

        var firstEntryText = TakeFirstBaseEntry(baseList);
        var firstEntryStart = 0;
        while (firstEntryStart < firstEntryText.Length && char.IsWhiteSpace(firstEntryText[firstEntryStart]))
            firstEntryStart++;

        var firstEntryEnd = firstEntryText.Length;
        while (firstEntryEnd > firstEntryStart && char.IsWhiteSpace(firstEntryText[firstEntryEnd - 1]))
            firstEntryEnd--;

        var firstEntry = firstEntryText.Substring(firstEntryStart, firstEntryEnd - firstEntryStart);
        // Only count a `(` that sits at generic / bracket depth 0 — a primary-ctor base call
        // always puts its argument list directly after the bare type name, whereas generic args
        // and array ranks can legally contain `(` (tuple syntax `<(int, int)>`, function types
        // `<Func<(int, int)>>`, or attribute arg brackets). A naive `.Contains('(')` would treat
        // those as primary-ctor calls and synthesize a phantom record ctor container.
        // 先頭エントリのうち generic/bracket 深度 0 の `(` だけを primary-ctor 呼び出し扱いにする。
        // `IBox<(int, int)>` のような tuple を含む interface 実装を連鎖呼び出しと誤認させない。
        int angleDepth = 0;
        int squareDepth = 0;
        for (int i = 0; i < firstEntry.Length; i++)
        {
            var c = firstEntry[i];
            switch (c)
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    if (angleDepth > 0) angleDepth--;
                    break;
                case '[':
                    squareDepth++;
                    break;
                case ']':
                    if (squareDepth > 0) squareDepth--;
                    break;
                case '(':
                    if (angleDepth == 0 && squareDepth == 0)
                        return true;
                    break;
            }
        }
        return false;
    }

    /// <summary>
    /// Parse the first base-class token from a C# class/struct/record signature such as
    /// `class B : A, IFoo`, `record C(int x) : A(x)`, or `class B<T> : A<T> where T : new()`.
    /// Returns null when no base list is present or when the signature is empty.
    /// C# の class/struct/record シグネチャから最初の基底クラストークンを取り出す。
    /// </summary>
    internal static string? ParseCSharpBaseType(string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
            return null;

        var text = signature.TrimEnd();
        if (text.EndsWith("{", StringComparison.Ordinal))
        {
            var end = text.Length - 1;
            while (end > 0 && char.IsWhiteSpace(text[end - 1]))
                end--;
            text = text.Substring(0, end);
        }

        var colonIndex = FindSignatureColonIndex(text);
        if (colonIndex < 0)
            return null;

        var baseList = text.Substring(colonIndex + 1);
        var whereMatch = CSharpWhereClauseRegex.Match(baseList);
        if (whereMatch.Success)
            baseList = baseList.Substring(0, whereMatch.Index);

        var firstEntryText = TakeFirstBaseEntry(baseList);
        var firstEntryStart = 0;
        while (firstEntryStart < firstEntryText.Length && char.IsWhiteSpace(firstEntryText[firstEntryStart]))
            firstEntryStart++;

        var firstEntryEnd = firstEntryText.Length;
        while (firstEntryEnd > firstEntryStart && char.IsWhiteSpace(firstEntryText[firstEntryEnd - 1]))
            firstEntryEnd--;

        var firstEntry = firstEntryText.Substring(firstEntryStart, firstEntryEnd - firstEntryStart);
        return ExtractBareTypeName(firstEntry);
    }

    /// <summary>
    /// Parse the first extends-clause type from a Java class/interface/record signature.
    /// 例: `class B extends A implements IFoo` → `A`、
    /// `class Leaf extends Outer<Integer>.Base {` → `Base`。
    /// </summary>
    internal static string? ParseJavaBaseType(string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
            return null;

        // Locate `extends` at angle/paren depth 0 so bounded type parameters like
        // `class Leaf<T extends Number> extends Root {` do not resolve to the
        // parameter bound (`Number`) instead of the real base (`Root`).
        // 境界付き型パラメータ（`class Leaf<T extends Number> extends Root {`）で
        // 型パラメータ境界の `extends` を先に拾わないよう、angle / paren 深度 0 の
        // `extends` のみを検出する。
        int start = FindTopLevelExtendsEnd(signature!);
        if (start < 0)
            return null;

        int i = start;
        int angleDepth = 0;
        int parenDepth = 0;
        while (i < signature.Length)
        {
            char c = signature[i];
            if (c == '<')
            {
                angleDepth++;
            }
            else if (c == '>')
            {
                if (angleDepth > 0) angleDepth--;
            }
            else if (c == '(')
            {
                // Track `(...)` depth so that commas inside annotation arguments such as
                // `@Ann(a = 1, b = 2) Root` or `@Ann({A.class, B.class}) Root` are not mistaken
                // for top-level base-list separators. Without this the scanner breaks at the
                // inner `,`, feeds a truncated segment to the annotation stripper, and the
                // super(...) edge gets misattributed or dropped entirely.
                // annotation 引数内のカンマ（`@Ann(a = 1, b = 2) Root` や
                // `@Ann({A.class, B.class}) Root`）が base-list 区切りと誤認されないよう `(...)` の
                // 深さも追跡する。これをやらないと内側の `,` で走査が切れ、annotation stripper に
                // 壊れたセグメントが渡って super(...) の連鎖エッジが落ちる。
                parenDepth++;
            }
            else if (c == ')')
            {
                if (parenDepth > 0) parenDepth--;
            }
            else if (angleDepth == 0 && parenDepth == 0)
            {
                if (c == '{' || c == ',' || c == ';')
                    break;
                // Stop at a word-boundary `implements` or `permits` (Java 17+ sealed types).
                // 単語境界の `implements` / `permits` (Java 17+ sealed 型) で停止する。
                if (IsJavaBaseListTerminatorKeyword(signature, i, start, "implements") ||
                    IsJavaBaseListTerminatorKeyword(signature, i, start, "permits"))
                {
                    break;
                }
            }
            i++;
        }

        var segment = signature.Substring(start, i - start).Trim();
        if (segment.Length == 0)
            return null;

        // Strip Java type-use annotations (JLS 9.7.4): `@Ann`, `@pkg.Ann`, `@Ann(value=1)` can
        // appear before the type itself (`extends @Ann Root`) or between nested-type segments
        // (`Outer<Integer>.@Ann Base`). Without this pass the base resolver returns a phantom
        // type name like `@Ann Root` that misattributes references / callers / impact.
        // Java の type-use annotation (JLS 9.7.4) を剥がす。`extends @Ann Root` や
        // `Outer<Integer>.@Ann Base` のような形で基底型の直前やセグメント間に現れるため、
        // 先に除去しないと `@Ann Root` のような幽霊シンボルへ参照が張られてしまう。
        segment = StripJavaTypeAnnotations(segment);
        return segment.Length == 0 ? null : ExtractBareTypeName(segment);
    }

    /// <summary>
    /// Return the index past the first `extends` keyword that appears at angle/paren depth 0,
    /// or -1 when no such occurrence exists. Matches the semantics of the old `\bextends\s+`
    /// regex entrypoint but skips `extends` inside `<...>` (bounded type parameters) and
    /// `(...)` (annotation argument lists).
    /// </summary>
    private static int FindTopLevelExtendsEnd(string signature)
    {
        int angleDepth = 0;
        int parenDepth = 0;
        for (int i = 0; i < signature.Length; i++)
        {
            char c = signature[i];
            if (c == '<')
            {
                angleDepth++;
            }
            else if (c == '>')
            {
                if (angleDepth > 0) angleDepth--;
            }
            else if (c == '(')
            {
                parenDepth++;
            }
            else if (c == ')')
            {
                if (parenDepth > 0) parenDepth--;
            }
            else if (angleDepth == 0 && parenDepth == 0 && IsExtendsKeywordAt(signature, i))
            {
                int end = i + 7; // "extends".Length
                while (end < signature.Length && char.IsWhiteSpace(signature[end]))
                    end++;
                return end;
            }
        }
        return -1;
    }

    private static bool IsExtendsKeywordAt(string signature, int i)
    {
        const string Keyword = "extends";
        if (i + Keyword.Length > signature.Length)
            return false;
        if (i > 0 && IsJavaIdentifierPart(signature[i - 1]))
            return false;
        if (string.CompareOrdinal(signature, i, Keyword, 0, Keyword.Length) != 0)
            return false;
        int after = i + Keyword.Length;
        // `\bextends\s+` equivalence: must be followed by whitespace so that names like
        // `extendsFoo` or identifiers containing `extends` do not match.
        // `\bextends\s+` 相当: `extendsFoo` のような識別子や合成語を誤認しないよう、
        // 直後に空白が続くものだけを `extends` キーワードとして扱う。
        if (after >= signature.Length)
            return false;
        return char.IsWhiteSpace(signature[after]);
    }

    private static string StripJavaTypeAnnotations(string text)
    {
        if (text.IndexOf('@') < 0)
            return text;

        var sb = new System.Text.StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '@')
            {
                // Skip `@` + qualified identifier (`@pkg.Ann`) + optional balanced `(...)`.
                i++;
                while (i < text.Length && (IsJavaIdentifierPart(text[i]) || text[i] == '.'))
                    i++;
                if (i < text.Length && text[i] == '(')
                {
                    int parenDepth = 1;
                    i++;
                    while (i < text.Length && parenDepth > 0)
                    {
                        var ch = text[i];
                        // Skip string / char literals so `@Ann(text=")")` does not close early.
                        // 文字列・文字リテラル内の `)` で早期終了しないようスキップする。
                        if (ch == '"' || ch == '\'')
                        {
                            var quote = ch;
                            i++;
                            while (i < text.Length)
                            {
                                var lc = text[i];
                                if (lc == '\\' && i + 1 < text.Length) { i += 2; continue; }
                                if (lc == quote) { i++; break; }
                                i++;
                            }
                            continue;
                        }
                        if (ch == '(') parenDepth++;
                        else if (ch == ')') parenDepth--;
                        i++;
                    }
                }
                // Drop a single trailing whitespace run so `@Ann Root` collapses to `Root`.
                while (i < text.Length && char.IsWhiteSpace(text[i]))
                    i++;
                continue;
            }
            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    internal static bool IsJavaIdentifierPart(char c) =>
        char.IsLetterOrDigit(c) || c == '_' || c == '$';

    private static bool IsJavaBaseListTerminatorKeyword(string signature, int i, int start, string keyword) =>
        IsJavaBaseListTerminatorKeyword(signature.AsSpan(), i, start, keyword);

    private static bool IsJavaBaseListTerminatorKeyword(ReadOnlySpan<char> signature, int i, int start, string keyword)
    {
        var keywordSpan = keyword.AsSpan();
        if (i + keywordSpan.Length > signature.Length)
            return false;
        if (i != start && IsJavaIdentifierPart(signature[i - 1]))
            return false;
        if (!signature.Slice(i, keywordSpan.Length).SequenceEqual(keywordSpan))
            return false;
        if (i + keywordSpan.Length < signature.Length && IsJavaIdentifierPart(signature[i + keywordSpan.Length]))
            return false;
        return true;
    }

    private static int FindSignatureColonIndex(string text)
    {
        var depth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '<':
                case '(':
                case '[':
                    depth++;
                    break;
                case '>':
                case ')':
                case ']':
                    if (depth > 0) depth--;
                    break;
                case ':':
                    if (depth == 0)
                    {
                        // Skip `::` alias qualifier (`global::System.Exception`).
                        // `::` エイリアス修飾子（`global::System.Exception`）はスキップ。
                        if (i + 1 < text.Length && text[i + 1] == ':')
                        {
                            i++;
                            continue;
                        }
                        return i;
                    }
                    break;
            }
        }

        return -1;
    }

    private static string TakeFirstBaseEntry(string baseList)
    {
        var depth = 0;
        for (int i = 0; i < baseList.Length; i++)
        {
            var c = baseList[i];
            switch (c)
            {
                case '<':
                case '(':
                case '[':
                    depth++;
                    break;
                case '>':
                case ')':
                case ']':
                    if (depth > 0) depth--;
                    break;
                case ',':
                    if (depth == 0)
                        return baseList.Substring(0, i);
                    break;
            }
        }

        return baseList;
    }

    private static string? ExtractBareTypeName(string entry)
    {
        var trimmed = entry.Trim();
        if (trimmed.Length == 0)
            return null;

        // Split on `.` / `::` at generic depth 0, then return the last segment with generic
        // args stripped. Naive "first `<`, then last `.`" slicing loses nested types such as
        // `Outer<int>.Base`, `Outer<Integer>.Base`, or `global::Ns.Outer<T>.Inner`.
        // 最初の `<` で切ってから末尾 `.` を探す素朴な方法では `Outer<int>.Base` のような
        // ネスト型を取り違えるため、generic 深度 0 の `.` / `::` でセグメント分割して末尾だけ返す。
        int lastSegmentStart = 0;
        int angleDepth = 0;
        int endIndex = trimmed.Length;
        for (int i = 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            if (c == '<')
            {
                angleDepth++;
            }
            else if (c == '>')
            {
                if (angleDepth > 0) angleDepth--;
            }
            else if (angleDepth == 0)
            {
                if (c == '(')
                {
                    // Strip record primary-ctor args at top level: `A(...)` → `A`.
                    // record のプライマリコンストラクタ引数を剥がす。
                    endIndex = i;
                    break;
                }
                if (c == '.')
                {
                    lastSegmentStart = i + 1;
                }
                else if (c == ':' && i + 1 < trimmed.Length && trimmed[i + 1] == ':')
                {
                    lastSegmentStart = i + 2;
                    i++;
                }
            }
        }

        var segment = trimmed.Substring(lastSegmentStart, endIndex - lastSegmentStart).Trim();
        var ltIndex = segment.IndexOf('<');
        if (ltIndex >= 0)
            segment = segment.Substring(0, ltIndex);

        segment = segment.Trim();
        return segment.Length > 0 ? segment : null;
    }

}
