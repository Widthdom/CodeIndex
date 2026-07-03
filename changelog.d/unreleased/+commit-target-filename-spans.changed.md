---
title: Avoid commit-target file-name allocations
category: changed
---

## English

- Check ignore and JavaScript/TypeScript config file names with spans while normalizing commit and update targets, reducing path allocations on large change lists.

## 日本語

- commit/update 対象の正規化時に ignore file と JavaScript/TypeScript config 名を span で判定し、大量の変更ファイル一覧での path allocation を減らしました。
