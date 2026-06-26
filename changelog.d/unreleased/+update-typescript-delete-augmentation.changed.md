---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.Update.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerTests.cs
---

## English

- **Scoped updates refresh TypeScript augmentation after deletes** — `cdidx index --files` now clears and rebuilds TypeScript augmentation metadata when an update deletes or purges indexed rows, preventing stale augmentation readiness after TypeScript files disappear.

## 日本語

- **scoped update の削除時に TypeScript augmentation を更新するようになりました** — `cdidx index --files` は update 中に indexed row を delete/purge した場合、TypeScript augmentation metadata を clear/rebuild し、TypeScript ファイル削除後に古い augmentation readiness が残らないようにします。
