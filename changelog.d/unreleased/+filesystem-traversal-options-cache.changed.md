---
category: changed
affected:
  - src/CodeIndex/FileSystemTraversalPolicy.cs
---

## English

- **Filesystem traversal reuses enumeration options** — hot directory walks now share the top-directory-only `EnumerationOptions` instance instead of allocating one for every file, directory, or entry enumeration call.

## 日本語

- **filesystem traversal が enumeration options を再利用** — hot directory walk で file、directory、entry の列挙ごとに top-directory-only `EnumerationOptions` を割り当てず、共有 instance を使うようにしました。
