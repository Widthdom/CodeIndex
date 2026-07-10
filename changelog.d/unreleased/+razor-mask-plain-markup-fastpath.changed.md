---
category: changed
affected:
  - src/CodeIndex/Indexer/References/Support/LanguageReferenceExtractionSupport.cs
  - tests/CodeIndex.Tests/ReferenceExtractorTests.cs
---

## English

- **Razor reference masking skips plain markup copies** — Razor/Blazor reference extraction now bypasses per-line masking work when a file contains no Razor markers or HTML comments.

## 日本語

- **Razor 参照マスキングで plain markup のコピーを避けるようになりました** — Razor/Blazor の参照抽出は、Razor marker や HTML comment がないファイルでは行ごとのマスキング処理を省略します。
