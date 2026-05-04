---
category: internal
affected:
  - src/CodeIndex/Indexer/ReferenceExtractor.cs
  - src/CodeIndex/Indexer/PythonReferenceExtractor.cs
  - src/CodeIndex/Indexer/RReferenceExtractor.cs
  - src/CodeIndex/Indexer/PhpReferenceExtractor.cs
  - src/CodeIndex/Indexer/PowerShellReferenceExtractor.cs
  - docs/DEVELOPER_GUIDE.md
---

## English

- **Extracted additional scripting-language reference helpers** — Python decorator references, R namespace references, PHP member/type references, and PowerShell cmdlet-style calls now live in dedicated helper classes while preserving the existing reference emission path.

## 日本語

- **追加のスクリプト系 reference helper を分割しました** — Python decorator 参照、R namespace 参照、PHP の member / type 参照、PowerShell の cmdlet 形式呼び出しを専用 helper へ移し、既存の reference 発行経路は維持しました。
