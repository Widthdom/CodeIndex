---
title: Index TypeScript namespace shadow lines once
category: changed
---

## English

- Build a single local-declaration lookup for TypeScript namespace alias shadow lines instead of rescanning every line for each alias binding.

## 日本語

- TypeScript namespace alias の shadow line 判定で、alias binding ごとに全行を再走査せず、ローカル宣言 lookup を一度だけ構築するようにしました。
