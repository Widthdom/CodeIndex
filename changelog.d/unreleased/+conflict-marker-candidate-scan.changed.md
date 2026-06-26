---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileIndexer.cs
  - tests/CodeIndex.Tests/FileIndexerTests.cs
---

## English

- **Conflict-marker validation uses one candidate scan** — validation now checks for `<<<<<<<` and `>>>>>>>` candidates in a single span pass before running the line-aware conflict-marker scan, reducing normal-file validation work during large indexing runs.

## 日本語

- **conflict-marker validation の候補走査を1回にします** — validation は line-aware な conflict marker scan の前に、`<<<<<<<` と `>>>>>>>` の候補を1回の span pass で確認し、巨大 index 実行時の通常ファイル検証コストを減らします。
