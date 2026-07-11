---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.cs
  - tests/CodeIndex.Tests/DatabaseTests.cs
---

## English

- **Stat reuse stops scanning rows once per-file caps are exceeded** - unchanged-file eligibility now uses bounded SQLite existence probes for symbol and reference caps instead of counting every row. Pathological generated files and dense multi-language outputs can therefore fail reuse after reading only the configured cap plus one row.

## 日本語

- **stat reuse が file ごとの上限超過時点で行走査を打ち切るようにしました** - 未変更 file の再利用可否は、全 symbol / reference 行を COUNT せず、bounded SQLite existence probe で上限を確認します。病的な generated file や高密度な複数言語出力は、設定上限+1行だけを読んで再利用不可を判定できます。
