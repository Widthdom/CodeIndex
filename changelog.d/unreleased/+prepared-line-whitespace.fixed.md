---
category: fixed
affected:
  - src/CodeIndex/Indexer/References/ReferenceExtractor.CoreReferenceLoop.cs
  - DEVELOPER_GUIDE.md
---

## English

- **Avoided duplicate full-line whitespace scans during reference extraction** —
  Every supported language now classifies each prepared line once and reuses
  the result across special-line and ordinary empty-line dispatch, which is
  especially important for long structurally masked payloads.

## 日本語

- **reference extraction の重複した全行 whitespace scan を解消しました** —
  全対応言語で prepared line を一度だけ判定し、special-line と通常の empty-line dispatch
  で結果を共有するため、長い構造マスク済み payload の再走査を避けます。
