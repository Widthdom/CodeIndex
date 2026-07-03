---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileIndexer.DefaultExclusions.cs
  - tests/CodeIndex.Tests/FileIndexerTests.cs
  - USER_GUIDE.md
---

## English

- **Expanded built-in generated/cache directory skips** - `cdidx index` now skips additional common cache/build trees such as `.pnpm-store`, `.turbo`, `.mypy_cache`, `bazel-out`, `.dart_tool`, `.swiftpm`, and `.stack-work` before file content probing, reducing first-scan time on large polyglot repositories.

## 日本語

- **組み込みの generated/cache directory skip を拡張しました** - `cdidx index` は `.pnpm-store`、`.turbo`、`.mypy_cache`、`bazel-out`、`.dart_tool`、`.swiftpm`、`.stack-work` などの一般的な cache / build tree をファイル内容 probing 前に skip するようになり、大規模 polyglot repository の初回 scan 時間を削減します。
