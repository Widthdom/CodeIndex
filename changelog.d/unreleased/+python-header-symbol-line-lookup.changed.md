---
title: Cache Python header symbols by line
category: changed
---

## English

- Cache Python header symbols by line so reference extraction avoids scanning the full symbol list for every line in large Python files.

## 日本語

- Python header symbol を行番号でキャッシュし、大きな Python ファイルの reference 抽出で各行ごとに symbol list 全体を走査しないようにしました。
