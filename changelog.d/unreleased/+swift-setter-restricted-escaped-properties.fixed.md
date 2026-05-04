---
category: fixed
affected:
  - src/CodeIndex/Indexer/SymbolExtractor.cs
  - tests/CodeIndex.Tests/QueryCommandRunnerTests.cs
  - docs/DEVELOPER_GUIDE.md
---

## English

- **Swift setter-restricted stored properties now keep backtick-escaped names searchable** — Swift property extraction now accepts `private(set)` / `fileprivate(set)` stored properties whose names are written as escaped identifiers, so `symbols` and related search flows can find declarations such as ``private(set) var `class`: Int``.

## 日本語

- **Swift の setter 制限付き stored property でもバッククォート付き名前を検索できるようにしました** — Swift の property 抽出で `private(set)` / `fileprivate(set)` の stored property に加えてエスケープ識別子の名前を受け付けるようになり、``private(set) var `class`: Int`` のような宣言も `symbols` や関連する検索フローから見つけられるようになりました。
