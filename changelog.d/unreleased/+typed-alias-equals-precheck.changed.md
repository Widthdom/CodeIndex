---
title: Tighten TypeScript and Swift alias prechecks
category: changed
---

## English

- Require both the alias keyword and `=` before building TypeScript or Swift type-alias scope tables, avoiding unnecessary brace-depth scans in files that merely mention the keywords.

## 日本語

- TypeScript と Swift の type alias scope table を構築する前に alias keyword と `=` の両方を要求し、keyword に触れているだけのファイルで不要な brace-depth 走査を避けるようにしました。
