---
title: Dockerfile instruction prefixes avoid trim strings
category: changed
---

## English

- **Dockerfile instruction prefixes avoid trim strings** — Dockerfile `EXPOSE`, `VOLUME`, `SHELL`, and ONBUILD-aware instruction parsing now checks prefixes and bodies with spans before materializing token or JSON payload text.

## 日本語

- **Dockerfile instruction prefixでtrim文字列を避けるようになりました** — Dockerfile `EXPOSE`、`VOLUME`、`SHELL`、ONBUILD対応instruction解析はtokenやJSON payload textを文字列化する前に、prefixとbodyをspanで判定するようになりました。
