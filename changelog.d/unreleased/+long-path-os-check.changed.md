---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/LongPath.cs
---

## English

- **Long-path helpers cache the OS check** — index traversal no longer repeats the Windows platform probe for every long-path prefix add/remove helper call.

## 日本語

- **long-path helper が OS 判定を cache** — index traversal で long-path prefix の付与/除去 helper を呼ぶたびに Windows platform 判定を繰り返さないようにしました。
