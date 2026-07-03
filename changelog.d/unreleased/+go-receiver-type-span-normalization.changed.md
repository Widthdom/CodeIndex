---
title: Go receiver type normalization avoids trim chains
category: changed
---

## English

- **Go receiver type normalization avoids trim chains** — Go method receiver lookup now strips receiver names, pointer markers, generic arguments, and qualifiers with spans so only the final receiver type name is allocated.

## 日本語

- **Go receiver type正規化でtrim連鎖を避けるようになりました** — Go method receiver lookupはreceiver名、pointer marker、generic引数、qualifierの除去をspanで行い、最終的なreceiver type名だけを割り当てるようになりました。
