---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.DryRun.cs
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Cli/IndexFreshnessChecker.cs
  - src/CodeIndex/Cli/IndexCommandRunner.Update.cs
  - src/CodeIndex/Indexer/Scanning/FileContentLoader.cs
  - src/CodeIndex/Indexer/Scanning/FileContentLoader.Checksum.cs
  - src/CodeIndex/Indexer/CSharpStaticInterfacePrepass.cs
  - src/CodeIndex/Indexer/Scanning/FileContentLoader.RawBytes.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.cs
  - src/CodeIndex/Mcp/McpToolHandlers.cs
  - .github/workflows/dotnet.yml
  - TESTING_GUIDE.md
---

## English

- Reduced redundant file-content decoding and checksum work during indexing so large codebase scans spend less time reprocessing unchanged UTF-8 and UTF-16 content.
- Used direct SHA-256 hashing for CR-free raw byte payloads, avoiding the normalized-checksum scan on the common LF-only source-file path.
- Streamed checksum calculation now appends CR-free chunks directly, avoiding per-byte normalization loops during unchanged-file probes on LF-only files.
- Short-circuited UTF-16 heuristic detection when the sample has no NUL bytes, avoiding pair-count scans for ordinary UTF-8 source files.
- Deferred UTF-16 heuristic text-byte counting until NUL parity passes, reducing decode preflight work on NUL-containing non-UTF-16 files.
- Changed the C# static-interface prepass to stream raw token checks before decoding, avoiding whole-file byte-array allocation for non-candidate C# files in large workspaces.
- Reused scan-time language detection while building full-scan records, avoiding a second language probe for every indexed file.
- Reused scan-time file targets in MCP project indexing as well, avoiding repeated relative-path and language work during MCP-triggered large workspace indexes.
- Reused scan-time language data when update mode expands the C# static-interface workspace, reducing repeated language probes on expanded update sets.
- Reused update-mode language probes for stat-based unchanged-file checks, removing another redundant probe on each index target.
- Reused dry-run language probes while building candidate records, reducing duplicate detection work in large dry-run previews.
- Limited scan-time language reuse to cases that do not require a second content-sensitive pass, so C/C++ header detection still inspects file content when needed.
- Passed only scan-confirmed C# files into the C# static-interface prepass for full-scan and MCP indexing, avoiding an extra prepass walk over unrelated languages.
- Skipped scan-confirmed non-C# targets during update-mode C# static-interface workspace expansion while still preserving deleted contract detection for unscanned paths.
- Reused full-scan generated-code suppression decisions between extraction and write phases, avoiding duplicate pattern checks per indexed file.
- Reused generated-code suppression decisions across unchanged-file checks and write paths in CLI update, full-scan, and MCP indexing.
- Removed per-line string allocations from generated-code header detection, reducing allocation pressure during record construction.
- Avoided string allocation while checking generated-code filename suffixes during record construction.
- Reused scan-time language detection during `status --check` workspace freshness validation, avoiding duplicate language probes while hashing large workspaces.
- Skipped generated-code classification during `status --check` freshness hashing, since freshness only compares path, checksum, and line metadata.
- Avoided allocating generated-code suppression issue objects during the C# static-interface prepass, where only the suppression decision is needed.
- Added an allocation-free word-boundary precheck before C# static-interface comment/string masking, avoiding large masks for obvious non-candidates.
- Collapsed C# prepass content-normalization probing to one scan instead of separate CR/BOM/zero-width-space checks.
- Cached raw prepass NUL-byte detection per scan/probe so C# static-interface candidate checks do not repeat UTF-16 eligibility scans for every token.
- Removed C# static-interface prepass member-header substring allocations by running word-boundary checks over spans.
- Consolidated the primary CI lane predicate so package audit, primary build/lint, coverage, publish, and build-artifact upload steps share one workflow decision point.

## 日本語

- index 実行時の file-content decode と checksum 処理の重複を減らし、大規模コードベースの scan で未変更 UTF-8 / UTF-16 content の再処理時間を抑えました。
- CR を含まない raw byte payload では直接 SHA-256 を使い、一般的な LF-only source file で normalized-checksum scan を避けるようにしました。
- streaming checksum calculation でも CR-free chunk を直接 append し、LF-only file の unchanged-file probe で byte 単位の normalization loop を避けるようにしました。
- sample に NUL byte がない場合は UTF-16 heuristic detection を即終了し、通常の UTF-8 source file で pair-count scan を避けるようにしました。
- NUL parity が UTF-16 heuristic の閾値を満たした場合だけ text-byte counting を行い、NUL を含む非 UTF-16 file の decode preflight work を減らしました。
- C# static-interface prepass は decode 前に raw token 判定を streaming 実行するようになり、大規模 workspace の候補外 C# ファイルでファイル全体の byte-array 割り当てを避けます。
- full scan record の構築時に scan 時点の言語判定を再利用し、index 対象ファイルごとの二度目の language probe を避けるようになりました。
- MCP project index でも scan 時点の file target を再利用し、MCP から大規模 workspace を index する際の相対パス計算と言語判定の繰り返しを避けます。
- update mode が C# static-interface workspace を拡張するときも scan 時点の言語情報を再利用し、拡張された更新対象で language probe を繰り返さないようにしました。
- update mode の language probe を stat-based unchanged-file 判定にも再利用し、index 対象ごとの追加 probe を削りました。
- dry-run の candidate record 構築でも language probe を再利用し、大規模 dry-run preview での重複検出処理を減らしました。
- scan 時点の language 再利用は二度目の content-sensitive pass が不要な場合に限定し、C/C++ header 判定では必要に応じて file content を確認するようにしました。
- full-scan と MCP index の C# static-interface prepass には scan で確認済みの C# ファイルだけを渡し、関連しない言語を prepass で余分に走査しないようにしました。
- update mode の C# static-interface workspace 拡張では、scan 済みの非 C# target を prepass から除外しつつ、scan されない削除済み contract の検出は維持しました。
- full-scan の generated-code suppression 判定を extraction phase と write phase で再利用し、index 対象ファイルごとの重複 pattern check を避けました。
- CLI update、full-scan、MCP index で generated-code suppression 判定を unchanged-file check と write path の間で再利用するようにしました。
- generated-code header 判定で行ごとの string allocation を発生させず、record 構築中の allocation pressure を減らしました。
- record 構築中の generated-code filename suffix 判定で string allocation を避けるようにしました。
- `status --check` の workspace freshness validation でも scan 時点の language 判定を再利用し、大規模 workspace を hash する際の重複 language probe を避けるようにしました。
- freshness は path、checksum、line metadata だけを比較するため、`status --check` の freshness hashing 中は generated-code classification を省略するようにしました。
- C# static-interface prepass では suppression 判定だけが必要なため、generated-code suppression issue object の割り当てを避けるようにしました。
- C# static-interface の comment/string masking 前に allocation-free の word-boundary precheck を追加し、明らかな候補外ファイルで大きな mask を作らないようにしました。
- C# prepass content normalization の事前判定を、CR / BOM / zero-width-space の個別 scan ではなく1回の scan にまとめました。
- raw prepass の NUL-byte 検出を scan/probe ごとに cache し、C# static-interface candidate 判定で token ごとに UTF-16 eligibility scan を繰り返さないようにしました。
- C# static-interface prepass の member-header substring allocation をなくし、word-boundary check を span 上で実行するようにしました。
- package audit、primary build/lint、coverage、publish、build artifact upload が同じ workflow 判定を使うように、primary CI lane の条件を集約しました。
