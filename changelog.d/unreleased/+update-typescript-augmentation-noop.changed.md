---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.Update.cs
---

## English

- **Scoped updates skip unchanged TypeScript augmentation rebuilds** — `cdidx index --files ...` now avoids rebuilding all TypeScript augmentation references when the update only rewrote non-TypeScript files, no stale rows were purged, the project root stayed the same, and the augmentation contract stamp is current.

## 日本語

- **scoped update が未変更 TypeScript augmentation rebuild を skip します** — `cdidx index --files ...` は、TypeScript 以外のファイルだけを書き換え、stale row purge がなく、project root が同じで、augmentation contract stamp が現行の場合に、全 TypeScript augmentation reference の再構築を避けるようになりました。
