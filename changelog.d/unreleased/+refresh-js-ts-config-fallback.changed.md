---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.cs
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Cli/IndexCommandRunner.Update.cs
  - src/CodeIndex/Database/DbWriter.cs
---

## English

- **JavaScript/TypeScript config refreshes re-extract JS/TS references** — scoped updates that fall back to a full scan after `jsconfig*.json` or `tsconfig*.json` changes, and ordinary full scans that observe changed or removed JS/TS config files, now force JavaScript and TypeScript files through extraction instead of reusing unchanged rows, so path-alias reference targets are recalculated while unrelated no-op full scans still keep the new stat-skip fast path.

## 日本語

- **JavaScript/TypeScript 設定変更時に JS/TS 参照を再抽出するようになりました** — `jsconfig*.json` または `tsconfig*.json` の変更で scoped update が full scan にフォールバックした場合に加え、通常の full scan が JS/TS 設定ファイルの変更または削除を検出した場合も、JavaScript/TypeScript ファイルは未変更行の再利用ではなく抽出を通すようにし、無関係な no-op full scan では新しい stat skip 高速化を維持したまま path alias の参照先を再計算します。
