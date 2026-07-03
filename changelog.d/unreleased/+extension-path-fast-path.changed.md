---
title: Avoid redundant file-name slicing before extension checks
category: changed
---

## English

- Avoid redundant file-name extraction before extension checks in scanner, encoding issue, Razor, and JSX paths so large indexing runs do less per-file path work.

## 日本語

- scanner、encoding issue、Razor、JSX の拡張子判定前に不要なファイル名切り出しを行わないようにし、大規模 indexing 時のファイルごとの path 処理を減らしました。
