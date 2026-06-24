---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.DryRun.cs
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
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
- Changed the C# static-interface prepass to stream raw token checks before decoding, avoiding whole-file byte-array allocation for non-candidate C# files in large workspaces.
- Reused scan-time language detection while building full-scan records, avoiding a second language probe for every indexed file.
- Reused scan-time file targets in MCP project indexing as well, avoiding repeated relative-path and language work during MCP-triggered large workspace indexes.
- Reused scan-time language data when update mode expands the C# static-interface workspace, reducing repeated language probes on expanded update sets.
- Reused update-mode language probes for stat-based unchanged-file checks, removing another redundant probe on each index target.
- Reused dry-run language probes while building candidate records, reducing duplicate detection work in large dry-run previews.
- Limited scan-time language reuse to content-independent detections so C/C++ header detection still inspects file content when needed.
- Passed only scan-confirmed C# files into the C# static-interface prepass for full-scan and MCP indexing, avoiding an extra prepass walk over unrelated languages.
- Skipped scan-confirmed non-C# targets during update-mode C# static-interface workspace expansion while still preserving deleted contract detection for unscanned paths.
- Reused full-scan generated-code suppression decisions between extraction and write phases, avoiding duplicate pattern checks per indexed file.
- Reused generated-code suppression decisions across unchanged-file checks and write paths in CLI update, full-scan, and MCP indexing.
- Removed per-line string allocations from generated-code header detection, reducing allocation pressure during record construction.
- Consolidated the primary CI lane predicate so package audit, primary build/lint, coverage, publish, and build-artifact upload steps share one workflow decision point.

## 日本語

- index 実行時の file-content decode と checksum 処理の重複を減らし、大規模コードベースの scan で未変更 UTF-8 / UTF-16 content の再処理時間を抑えました。
- C# static-interface prepass は decode 前に raw token 判定を streaming 実行するようになり、大規模 workspace の候補外 C# ファイルでファイル全体の byte-array 割り当てを避けます。
- full scan record の構築時に scan 時点の言語判定を再利用し、index 対象ファイルごとの二度目の language probe を避けるようになりました。
- MCP project index でも scan 時点の file target を再利用し、MCP から大規模 workspace を index する際の相対パス計算と言語判定の繰り返しを避けます。
- update mode が C# static-interface workspace を拡張するときも scan 時点の言語情報を再利用し、拡張された更新対象で language probe を繰り返さないようにしました。
- update mode の language probe を stat-based unchanged-file 判定にも再利用し、index 対象ごとの追加 probe を削りました。
- dry-run の candidate record 構築でも language probe を再利用し、大規模 dry-run preview での重複検出処理を減らしました。
- scan 時点の language 再利用は content に依存しない判定だけに限定し、C/C++ header 判定では必要に応じて file content を確認するようにしました。
- full-scan と MCP index の C# static-interface prepass には scan で確認済みの C# ファイルだけを渡し、関連しない言語を prepass で余分に走査しないようにしました。
- update mode の C# static-interface workspace 拡張では、scan 済みの非 C# target を prepass から除外しつつ、scan されない削除済み contract の検出は維持しました。
- full-scan の generated-code suppression 判定を extraction phase と write phase で再利用し、index 対象ファイルごとの重複 pattern check を避けました。
- CLI update、full-scan、MCP index で generated-code suppression 判定を unchanged-file check と write path の間で再利用するようにしました。
- generated-code header 判定で行ごとの string allocation を発生させず、record 構築中の allocation pressure を減らしました。
- package audit、primary build/lint、coverage、publish、build artifact upload が同じ workflow 判定を使うように、primary CI lane の条件を集約しました。
