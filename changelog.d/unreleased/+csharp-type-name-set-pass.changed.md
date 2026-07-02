---
title: Share C# type name prepasses
category: changed
---

## English

- Build C# known type names and non-enum type names in one pass, then reuse the non-enum set for enum and constant-pattern lookups.

## 日本語

- C# の known type name と non-enum type name を 1 回の pass で構築し、non-enum set を enum / constant pattern lookup で再利用するようにしました。
