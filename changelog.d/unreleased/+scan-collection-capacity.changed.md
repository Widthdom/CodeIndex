---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileIndexer.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.ScanOrchestration.cs
---

## English

- **Full-scan collection growth now starts with conservative capacity hints** - File, language, and directory tracking collections avoid the earliest resize steps during repository scans while keeping small-project overhead bounded.

## 日本語

- **full-scan の collection growth に保守的な初期容量を設定しました** - ファイル、言語、ディレクトリ追跡 collection が、リポジトリ走査時の初期 resize を避けつつ、小規模プロジェクトの余分な確保を抑えます。
