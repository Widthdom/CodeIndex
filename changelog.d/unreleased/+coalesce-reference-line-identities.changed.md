---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.References.cs
---

## English

- **Adjacent references reuse their reference-line identity work** — Reference
  insertion now coalesces consecutive references with the same file, line, and
  context while building line rows and assigning their IDs. Dense same-line
  references in every language avoid repeatedly hashing long source contexts,
  while distinct contexts on the same line remain separate.

## 日本語

- **隣接する参照で reference-line identity 処理を再利用します** — reference の
  INSERT は line row の構築時とID設定時に、同じ file・line・context が連続する
  参照をまとめて扱うようになりました。全言語の同一行に密集した参照で長い source
  context の反復 hash 計算を避けつつ、同じ行の異なる context は分離したままです。
