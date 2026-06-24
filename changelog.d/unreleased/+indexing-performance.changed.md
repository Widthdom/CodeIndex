---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileContentLoader.cs
  - src/CodeIndex/Indexer/Scanning/FileContentLoader.Checksum.cs
  - .github/workflows/dotnet.yml
  - TESTING_GUIDE.md
---

## English

- Reduced redundant file-content decoding and checksum work during indexing so large codebase scans spend less time reprocessing unchanged UTF-8 and UTF-16 content.
- Consolidated the primary CI test lane predicate so coverage, publish, and build-artifact upload steps share one workflow decision point.

## 日本語

- index 実行時の file-content decode と checksum 処理の重複を減らし、大規模コードベースの scan で未変更 UTF-8 / UTF-16 content の再処理時間を抑えました。
- coverage、publish、build artifact upload が同じ workflow 判定を使うように、primary CI test lane の条件を集約しました。
