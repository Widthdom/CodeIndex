---
category: fixed
affected:
  - src/CodeIndex/Cli/QueryCommandRunner.cs
  - src/CodeIndex/Database/DbReader.cs
  - tests/CodeIndex.Tests/QueryCommandRunnerTests.cs
---

## English

- **`--lang ts` now filters TypeScript results** — query commands treat `ts` as `typescript`, and TypeScript is now listed among the language aliases, so searches and completions accept the common shorthand instead of requiring the full language name.

## 日本語

- **`--lang ts` で TypeScript の結果を絞り込めるようになりました** — クエリ系コマンドで `ts` を `typescript` として扱い、TypeScript も言語別名として列挙するため、検索や補完で長い正式名を毎回入力しなくても済みます。
