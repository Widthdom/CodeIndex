---
title: Kotlin name normalization avoids transient trim strings
category: changed
---

## English

- **Kotlin name normalization avoids transient trim strings** — Kotlin companion-object and secondary-constructor normalization now use trimmed spans for prefix and empty checks instead of allocating intermediate trim strings.

## 日本語

- **Kotlin name正規化で一時的なtrim文字列を避けるようになりました** — Kotlin companion objectとsecondary constructorの正規化は、中間のtrim文字列を割り当てず、trim済みspanでprefixと空判定を行うようになりました。
