---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.References.cs
  - tests/CodeIndex.Tests/DatabaseTests.cs
  - DEVELOPER_GUIDE.md
  - TESTING_GUIDE.md
---

## English

- **Cold C# graph finalization now resolves unqualified instantiations with set-based family facts** — Rank-5 constructor and type candidates materialize unique families and explicit-constructor summaries once, preserving exact case, arity, partial-type representatives, overloads, implicit defaults, ambiguity, and lower-rank semantics while avoiding repeated correlated symbol probes.

## 日本語

- **C# graph の cold finalization は無修飾 instantiation を family fact の集合処理で解決するようになりました** — rank 5 の constructor / type candidate は一意 family と明示 constructor summary を一度だけ materialize し、exact case、arity、partial type の代表、overload、implicit default、ambiguity、lower-rank の意味を維持しながら、相関 symbol probe の反復を避けます。
