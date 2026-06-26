---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.cs
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Cli/IndexCommandRunner.Update.cs
---

## English

- **JavaScript/TypeScript config fallback scans refresh JS/TS references** — scoped updates that fall back to a full scan after `jsconfig*.json` or `tsconfig*.json` changes now force JavaScript and TypeScript files through extraction instead of reusing unchanged rows, so path-alias reference targets are recalculated while ordinary no-op full scans still keep the new stat-skip fast path.

## 日本語

- **JavaScript/TypeScript 設定変更の fallback full scan が JS/TS 参照を再抽出するようになりました** — `jsconfig*.json` または `tsconfig*.json` の変更で scoped update が full scan にフォールバックした場合、JavaScript/TypeScript ファイルは未変更行の再利用ではなく抽出を通すようにし、通常の no-op full scan では新しい stat skip 高速化を維持したまま path alias の参照先を再計算します。
