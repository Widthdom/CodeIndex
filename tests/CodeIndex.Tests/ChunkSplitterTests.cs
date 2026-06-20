using CodeIndex.Indexer;

namespace CodeIndex.Tests;

/// <summary>
/// Tests for ChunkSplitter.
/// ChunkSplitterのテスト。
/// </summary>
public class ChunkSplitterTests
{
    [Fact]
    public void Split_SmallFile_ReturnsSingleChunk()
    {
        // A file with fewer lines than chunk size should yield one chunk
        // チャンクサイズ未満の行数のファイルは1チャンクになる
        var content = string.Join('\n', Enumerable.Range(1, 10).Select(i => $"line {i}"));
        var chunks = ChunkSplitter.Split(1, content);

        Assert.Single(chunks);
        Assert.Equal(0, chunks[0].ChunkIndex);
        Assert.Equal(1, chunks[0].StartLine);
        Assert.Equal(10, chunks[0].EndLine);
    }

    [Fact]
    public void Split_ExactChunkSize_ReturnsSingleChunk()
    {
        // Exactly 80 lines should yield one chunk
        // ちょうど80行なら1チャンクになる
        var content = string.Join('\n', Enumerable.Range(1, 80).Select(i => $"line {i}"));
        var chunks = ChunkSplitter.Split(1, content);

        Assert.Single(chunks);
        Assert.Equal(80, chunks[0].EndLine);
    }

    [Fact]
    public void Split_LargeFile_CreatesOverlappingChunks()
    {
        // 160 lines: chunk 0 = 1-80, chunk 1 = 71-150, chunk 2 = 141-160
        // 160行: チャンク0 = 1-80, チャンク1 = 71-150, チャンク2 = 141-160
        var content = string.Join('\n', Enumerable.Range(1, 160).Select(i => $"line {i}"));
        var chunks = ChunkSplitter.Split(1, content);

        Assert.True(chunks.Count >= 3);

        // Second chunk should start at line 71 (overlap of 10)
        // 2番目のチャンクは71行目から始まる（10行の重複）
        Assert.Equal(71, chunks[1].StartLine);
    }

    [Fact]
    public void Split_ManyValidLines_KeepsAllocationBounded()
    {
        var content = string.Join('\n', Enumerable.Range(1, 5000).Select(i => $"line {i:D4} value"));
        _ = ChunkSplitter.Split(1, "warmup\nline\n");

        var before = GC.GetAllocatedBytesForCurrentThread();
        var chunks = ChunkSplitter.Split(1, content);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(72, chunks.Count);
        Assert.Equal(1, chunks[0].StartLine);
        Assert.Equal(80, chunks[0].EndLine);
        Assert.Equal(4971, chunks[^1].StartLine);
        Assert.Equal(5000, chunks[^1].EndLine);
        Assert.True(allocated < 450_000, $"Chunk splitting allocated {allocated} bytes.");
    }

    [Fact]
    public void Split_EmptyFile_ReturnsNoChunks()
    {
        // Empty content produces no chunks
        // 空のコンテンツではチャンクは生成されない
        var chunks = ChunkSplitter.Split(1, "");
        Assert.Empty(chunks);
    }

    [Fact]
    public void Split_ChunkIndex_IsSequential()
    {
        // Chunk indices should be sequential starting from 0
        // チャンクインデックスは0から連番になる
        var content = string.Join('\n', Enumerable.Range(1, 200).Select(i => $"line {i}"));
        var chunks = ChunkSplitter.Split(1, content);

        for (int i = 0; i < chunks.Count; i++)
        {
            Assert.Equal(i, chunks[i].ChunkIndex);
        }
    }

    [Fact]
    public void Split_CrlfInput_NormalizesToLf()
    {
        // CRLF line endings should be normalized to LF before splitting
        // CRLF改行はLFに正規化されてから分割される
        var content = "line 1\r\nline 2\r\nline 3\r\n";
        var chunks = ChunkSplitter.Split(1, content);

        Assert.Single(chunks);
        Assert.Equal(3, chunks[0].EndLine);
        Assert.DoesNotContain("\r", chunks[0].Content);
    }

    [Fact]
    public void Split_LeadingBom_StrippedFromChunkContent()
    {
        // Leading UTF-8 BOM (U+FEFF) must be stripped before chunking so `excerpt`
        // and `search` do not emit a phantom glyph on line 1. Closes #183.
        // 先頭の UTF-8 BOM (U+FEFF) は分割前に剥がし、excerpt / search が 1 行目に
        // 幽霊グリフを出さないようにする。Closes #183.
        var content = "\uFEFFusing System;\n\nnamespace BomTest;\n";
        var chunks = ChunkSplitter.Split(1, content);

        Assert.Single(chunks);
        // Culture-aware IndexOf treats U+FEFF as ignorable and spuriously matches at pos 0,
        // so assert on the raw code-point instead of the string overload.
        // カルチャ依存の IndexOf は U+FEFF を無視扱いで pos 0 に誤マッチするため、
        // 文字列オーバーロードではなくコードポイントで確認する。
        Assert.DoesNotContain('\uFEFF', chunks[0].Content);
        Assert.StartsWith("using System;", chunks[0].Content);
        Assert.Equal(1, chunks[0].StartLine);
        Assert.Equal(3, chunks[0].EndLine);
    }

    [Fact]
    public void Split_MidFileBom_StrippedFromChunkContent()
    {
        // Mid-file UTF-8 BOM (e.g. file concatenation / tool insertion) must also be
        // stripped from chunk content; otherwise `search` / `excerpt` leak a phantom
        // glyph on the affected line. Closes #183.
        // mid-file UTF-8 BOM (ファイル連結 / ツール挿入) もチャンク内容から剥がす。
        // 剥がさないと search / excerpt が該当行に幽霊グリフを漏らす。Closes #183.
        var content = "using System;\n\uFEFFnamespace MidBom;\n";
        var chunks = ChunkSplitter.Split(1, content);

        Assert.Single(chunks);
        Assert.DoesNotContain('\uFEFF', chunks[0].Content);
        Assert.Contains("namespace MidBom;", chunks[0].Content);
    }

    [Fact]
    public void Split_BomOnlyInput_ReturnsNoChunks()
    {
        // BOM-only input has no real content, so it must follow the empty-file
        // contract (0 chunks) rather than producing a phantom empty chunk.
        // Closes #183.
        // BOM のみの入力は実コンテンツを持たないので、空ファイル契約 (0 チャンク) に
        // 従わせる。空内容のチャンクを作らない。Closes #183.
        var chunks = ChunkSplitter.Split(1, "\uFEFF");
        Assert.Empty(chunks);
    }

    [Fact]
    public void Split_MidLineBom_PreservedInChunkContent()
    {
        // Non-line-leading U+FEFF (Unicode 3.2+ ZWNBSP inside a string literal or
        // identifier, e.g. `const s = "A\uFEFFB"`) must be preserved verbatim.
        // The fix for #183 narrows the strip to line-leading BOM only so intentional
        // mid-line ZWNBSP use is not silently corrupted. Closes #183.
        // 行頭以外の U+FEFF (Unicode 3.2+ の ZWNBSP を文字列リテラルや識別子で
        // 意図的に使用しているケース、例: `const s = "A\uFEFFB"`) はそのまま残す。
        // #183 の修正は行頭 BOM のみに絞っており、mid-line ZWNBSP の意図的利用が
        // 黙って壊れないことを保証する。Closes #183.
        var content = "const string s = \"A\uFEFFB\";\n";
        var chunks = ChunkSplitter.Split(1, content);

        Assert.Single(chunks);
        Assert.Contains('\uFEFF', chunks[0].Content);
        Assert.Contains("\"A\uFEFFB\"", chunks[0].Content);
    }

    [Fact]
    public void Split_NullContent_ReturnsNoChunks()
    {
        // The pre-#183 guard used `string.IsNullOrEmpty`, which accepted null.
        // The first iteration of the #183 fix regressed this to `content.Length == 0`
        // and threw NullReferenceException for direct callers passing null. Restore
        // the null-safe contract and pin it here. Closes #183.
        // #183 以前のガードは `string.IsNullOrEmpty` で null を受け付けていた。
        // #183 修正の初版で `content.Length == 0` に変えて null 渡しで NRE を
        // 投げるようになっていたため、null セーフな契約を復元しピンで固定する。
        // Closes #183.
        var chunks = ChunkSplitter.Split(1, null!);
        Assert.Empty(chunks);
    }

    [Fact]
    public void HasOversizeLine_ReturnsTrue_ForLineExceedingCap()
    {
        // A single physical line one char beyond MaxLineLength must trigger
        // the oversize-line skip path. Closes #1542.
        // MaxLineLength を 1 文字でも超える物理行は oversize-line スキップ経路を
        // 起動する。Closes #1542.
        var content = new string('a', ChunkSplitter.MaxLineLength + 1);
        Assert.True(ChunkSplitter.HasOversizeLine(content));
    }

    [Fact]
    public void HasOversizeLine_ReturnsFalse_AtCapBoundary()
    {
        // A line exactly at the cap must not be flagged; only strictly longer
        // lines trigger the skip. Closes #1542.
        // ちょうど上限の長さの行は対象外。厳密に超えた行のみがスキップ対象。
        // Closes #1542.
        var content = new string('a', ChunkSplitter.MaxLineLength);
        Assert.False(ChunkSplitter.HasOversizeLine(content));
    }

    [Fact]
    public void HasOversizeLine_ReturnsFalse_ForManyShortLines()
    {
        // Many short lines below the cap must not be flagged, even when the
        // total content size is large. The cap is per physical line, not per
        // file. Closes #1542.
        // 上限以下の短い行が大量にあってもフラグは立たない。上限はファイル全体
        // ではなく物理行ごとに適用される。Closes #1542.
        var line = new string('a', 1024);
        var content = string.Join('\n', Enumerable.Repeat(line, 200));
        Assert.False(ChunkSplitter.HasOversizeLine(content));
    }

    [Fact]
    public void HasOversizeLine_NullOrEmpty_ReturnsFalse()
    {
        // Defensive null/empty contract — must not throw and must not flag
        // empty input as oversize. Closes #1542.
        // null / 空入力でも例外を出さず、空入力を oversize 扱いしない。
        // Closes #1542.
        Assert.False(ChunkSplitter.HasOversizeLine(null));
        Assert.False(ChunkSplitter.HasOversizeLine(""));
    }

    [Fact]
    public void Split_OversizeSingleLine_ReturnsNoChunks()
    {
        // A 1 MB single-line file (e.g. minified `.min.js`, base64 asset)
        // must yield zero chunks so downstream readers do not persist a
        // multi-MB Content column, and the matching guards in
        // SymbolExtractor / ReferenceExtractor skip regex-based extraction
        // for the same file. Closes #1542.
        // 1 MB の単一行ファイル (例: minified .min.js、base64 ペイロード) は
        // 0 チャンクを返し、複数 MB の Content カラムが永続化されないように
        // する。SymbolExtractor / ReferenceExtractor の同等ガードも合わせて
        // 正規表現抽出をスキップする。Closes #1542.
        var content = new string('x', 1_000_000);
        var chunks = ChunkSplitter.Split(1, content);
        Assert.Empty(chunks);
    }

    [Fact]
    public void Split_OversizeLineAmongShortLines_ReturnsNoChunks()
    {
        // Even when only one line exceeds the cap, the whole file is skipped
        // because that single line is the source of the regex-backtracking
        // / OOM hazard. Closes #1542.
        // 1 行だけが上限を超える場合でもファイル全体をスキップする。その
        // 1 行が正規表現のバックトラック / OOM 危険源だから。Closes #1542.
        var oversize = new string('y', ChunkSplitter.MaxLineLength + 1);
        var content = "short line 1\n" + oversize + "\nshort line 3\n";
        var chunks = ChunkSplitter.Split(1, content);
        Assert.Empty(chunks);
    }

    [Fact]
    public void Split_ConsecutiveLineLeadingBoms_AllStripped()
    {
        // A consecutive run of U+FEFF at a line start (either at offset 0 or
        // immediately after `\n`) must all be stripped, not just the first one.
        // Tools that repeatedly re-save a file or concatenate multiple BOM-bearing
        // sources can produce two or more BOMs in a row; a 1-BOM-only strip would
        // leak the rest as phantom glyphs into `search` / `excerpt` and still hide
        // line-1 declarations from `^\s*`-anchored regex. Closes #183.
        // 行頭に U+FEFF が連続して並ぶケース (オフセット 0 でも `\n` 直後でも)
        // は先頭 1 個だけでなく全て剥がす必要がある。ファイルを繰り返し再保存
        // したり BOM 付きソースを連結するツールで 2 連以上の BOM が発生しうる。
        // 1 個のみ剥がす実装だと残りが search / excerpt に幽霊グリフとして漏れ、
        // 1 行目宣言も `^\s*` 固定正規表現から消えたままになる。Closes #183.
        var content = "\uFEFF\uFEFFusing System;\n\uFEFF\uFEFF\uFEFFnamespace Multi;\n";
        var chunks = ChunkSplitter.Split(1, content);

        Assert.Single(chunks);
        Assert.DoesNotContain('\uFEFF', chunks[0].Content);
        Assert.StartsWith("using System;", chunks[0].Content);
        Assert.Contains("namespace Multi;", chunks[0].Content);
    }
}
