---
category: changed
affected:
  - src/CodeIndex/Indexer/CSharpStaticInterfacePrepass.cs
---

## English

- **Extraction-only C# contract prepasses no longer allocate existing-symbol path tracking** — callers that explicitly exclude database symbols avoid a target-sized hash set that cannot affect their result.

## 日本語

- **抽出専用の C# contract prepass では既存 symbol の path 追跡を確保しません** — database symbol を明示的に除外する呼び出しで、結果に影響しない対象件数分の HashSet を避けます。
