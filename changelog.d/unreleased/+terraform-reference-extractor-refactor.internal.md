---
category: internal
affected:
  - docs/DEVELOPER_GUIDE.md
  - src/CodeIndex/Indexer/ReferenceExtractor.cs
  - src/CodeIndex/Indexer/TerraformReferenceExtractor.cs
---

## English

- **Terraform reference extraction was split out of the large shared extractor** - dotted Terraform dependency forms now use a dedicated helper with a shared pattern loop, reducing repeated reference-emission code while preserving indexed behavior.

## 日本語

- **Terraform reference 抽出を大きな共通 extractor から分離しました** - Terraform の dotted dependency 形式を専用 helper と共通 pattern loop に移し、indexed behavior を維持したまま reference 出力コードの重複を減らしました。
