---
category: fixed
affected:
  - src/CodeIndex/Cli/ConsoleUi.cs
  - src/CodeIndex/Cli/QueryCommandRunner.cs
  - src/CodeIndex/Database/DbReader.cs
  - tests/CodeIndex.Tests/ConsoleUiTests.cs
  - tests/CodeIndex.Tests/QueryCommandRunnerTests.cs
---

## English

- **TypeScript language shorthands now cover the full file-extension family** — `--lang` accepts `ts`, `tsx`, `cts`, and `mts`, the language metadata/completion aliases surface the same set, and the CLI help text now advertises them so TypeScript-family files can be filtered with the shorthand that matches their extension.

## 日本語

- **TypeScript の言語 shorthand が拡張子ファミリー全体をカバーするようになりました** — `--lang` は `ts` / `tsx` / `cts` / `mts` を受け付け、言語メタデータと補完の alias も同じ集合を返し、CLI の help でもその alias 群を案内するため、TypeScript 系ファイルを拡張子に沿った shorthand で絞り込めます。
