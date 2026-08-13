---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.References.cs
  - tests/CodeIndex.Tests/DatabaseTests.cs
---

## English

- **Initial C# type-reference matching now evaluates partial families once** — rank-5 candidate construction groups exact name, arity, and type identity before matching references, then expands successful families back to every physical symbol, preserving candidate and ambiguity behavior without repeating the same compatibility work for each partial declaration.

## 日本語

- **初回C# type-reference照合がpartial familyごとに1回だけ評価されるようになりました** — rank 5 candidate構築はexact name・arity・type identityでgroup化してからreferenceを照合し、一致familyを全物理symbolへ展開するため、candidateとambiguityの動作を維持しながら各partial宣言で同じcompatibility処理を繰り返しません。
