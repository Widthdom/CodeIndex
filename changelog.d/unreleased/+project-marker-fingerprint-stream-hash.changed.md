---
title: Stream project marker fingerprint hashing without building a joined payload
category: changed
---

## English
- Hash project marker fingerprint inputs incrementally so large marker lists no longer allocate a joined payload string and UTF-8 byte array.

## 日本語
- project marker fingerprint の入力を incremental に hash し、大きな marker list で joined payload string と UTF-8 byte array を作らないようにしました。
