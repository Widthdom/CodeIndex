---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.Update.cs
---

## English

- **Scoped updates skip unnecessary C# metadata resolution** — `cdidx index --files ...` no longer runs the all-workspace C# metadata-target resolver when only non-C# files were rewritten and the existing C# metadata contract is already current.

## 日本語

- **scoped update が不要な C# metadata 解決を skip します** — `cdidx index --files ...` は、C# 以外のファイルだけを書き換え、既存の C# metadata contract が現行の場合に、全ワークスペースの C# metadata-target resolver を走らせないようになりました。
