---
title: Decode config text without extra byte copies
category: changed
---

## English

- Decode TypeScript path alias configs and extractor pattern configs directly from bounded read buffers to avoid an extra byte-array copy during indexing setup.

## 日本語

- TypeScript path alias config と extractor pattern config を bounded read buffer から直接 decode し、indexing setup 中の余分な byte array copy を避けるようにしました。
