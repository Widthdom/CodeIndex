---
title: Share TypeScript namespace alias brace-depth tables
category: changed
---

## English

- Build TypeScript namespace-alias brace-depth tables lazily and reuse them across alias shadow-range checks, avoiding repeated full-file scans for files with many imports.

## 日本語

- TypeScript namespace alias の brace-depth table を遅延構築して alias shadow-range 検査間で共有し、多数の import を持つファイルで全ファイル走査の繰り返しを避けるようにしました。
