---
title: Reduce PowerShell splat assignment allocations
category: changed
---

## English

- Delay PowerShell splat-assignment dictionary allocation until a real assignment is found, and pre-size the splat body builder from the first line.

## 日本語

- PowerShell の splat assignment 用 dictionary は実際の assignment 検出まで遅延し、splat 本文 builder も開始行から容量を見積もるようにしました。
