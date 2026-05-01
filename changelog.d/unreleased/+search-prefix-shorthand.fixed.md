---
category: fixed
affected:
  - src/CodeIndex/Database/DbSearchReader.cs
  - src/CodeIndex/Cli/ConsoleUi.cs
  - tests/CodeIndex.Tests/QueryCommandRunnerTests.cs
  - README.md
---

## English

- **Literal-safe `search` now accepts trailing `*` as a prefix shorthand** — `cdidx search auth*` now maps to a prefix FTS lookup, so users can broaden a literal-safe search without switching to raw `--fts` syntax. The help text and README examples now document the shorthand.

## 日本語

- **literal-safe な `search` で末尾の `*` を prefix shorthand として使えるようになりました** — `cdidx search auth*` が prefix FTS 参照に変換されるため、raw な `--fts` 構文へ切り替えずに検索を広げられます。ヘルプ文と README の例もこの shorthand を案内するよう更新しました。
