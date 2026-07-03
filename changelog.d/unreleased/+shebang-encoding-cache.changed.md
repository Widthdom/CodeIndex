---
title: Reuse strict shebang decoders
category: changed
---

## English
- Reuse strict UTF-8 and UTF-16 shebang decoders instead of allocating encoding instances during extensionless file probes.

## 日本語
- extensionless file の probe 中に encoding instance を都度確保せず、strict UTF-8/UTF-16 shebang decoder を再利用するようにしました。
