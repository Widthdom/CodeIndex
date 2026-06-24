---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileContentLoader.cs
  - src/CodeIndex/Indexer/Scanning/FileContentLoader.Checksum.cs
  - src/CodeIndex/Indexer/CSharpStaticInterfacePrepass.cs
  - src/CodeIndex/Indexer/Scanning/FileContentLoader.RawBytes.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.cs
  - .github/workflows/dotnet.yml
  - TESTING_GUIDE.md
---

## English

- Reduced redundant file-content decoding and checksum work during indexing so large codebase scans spend less time reprocessing unchanged UTF-8 and UTF-16 content.
- Changed the C# static-interface prepass to stream raw token checks before decoding, avoiding whole-file byte-array allocation for non-candidate C# files in large workspaces.
- Consolidated the primary CI lane predicate so package audit, primary build/lint, coverage, publish, and build-artifact upload steps share one workflow decision point.

## 日本語

- index 実行時の file-content decode と checksum 処理の重複を減らし、大規模コードベースの scan で未変更 UTF-8 / UTF-16 content の再処理時間を抑えました。
- C# static-interface prepass は decode 前に raw token 判定を streaming 実行するようになり、大規模 workspace の候補外 C# ファイルでファイル全体の byte-array 割り当てを避けます。
- package audit、primary build/lint、coverage、publish、build artifact upload が同じ workflow 判定を使うように、primary CI lane の条件を集約しました。
