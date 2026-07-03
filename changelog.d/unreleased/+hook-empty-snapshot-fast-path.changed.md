---
title: Avoid empty hook snapshot allocations
category: changed
---

## English

- Avoided allocating snapshot lists for empty post-extraction hooks and diagnostics, reducing default indexing overhead when no hooks are configured.

## 日本語

- post-extraction hook と diagnostics が空のときに snapshot list を割り当てないようにし、hook 未設定時の既定 indexing overhead を削減しました。
