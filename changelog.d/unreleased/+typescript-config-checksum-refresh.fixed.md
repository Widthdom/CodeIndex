---
category: fixed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Cli/IndexCommandRunner.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerTests.cs
---

## English

- **TypeScript config refresh now checks config content before skipping unchanged files** — full scans compare `tsconfig` / `jsconfig` checksums before reusing TypeScript files, so platform timestamp quirks no longer leave path-alias metadata stale.

## 日本語

- **TypeScript config refresh が未変更ファイルを skip する前に config 内容を確認するようになりました** — full scan は TypeScript ファイルを再利用する前に `tsconfig` / `jsconfig` の checksum を比較するため、プラットフォームごとの timestamp 差異で path alias metadata が古いまま残らなくなります。
