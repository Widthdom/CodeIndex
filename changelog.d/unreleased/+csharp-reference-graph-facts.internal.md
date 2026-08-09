---
category: internal
affected:
  - src/CodeIndex/Database/DbWriter.References.cs
  - src/CodeIndex/Database/DbWriter.ReferenceGraphRefreshScope.cs
  - tests/CodeIndex.Tests/DatabaseTests.cs
  - DEVELOPER_GUIDE.md
  - TESTING_GUIDE.md
---

## English

- **C# graph finalization reuses materialized scalar facts** — full, scoped, and retained reference-graph refreshes now compute C# reference and symbol arity, receiver, constructor, and value-type facts once per applicable row, avoiding repeated managed SQLite callbacks for every candidate in large graphs.

## 日本語

- **C# graph finalization が materialize 済み scalar fact を再利用します** — full / scoped / retained の reference-graph refresh は、C# reference と symbol の arity、receiver、constructor、value-type fact を対象 row ごとに1回だけ計算し、巨大 graph の candidate ごとに managed SQLite callback を反復する処理を避けるようになりました。
