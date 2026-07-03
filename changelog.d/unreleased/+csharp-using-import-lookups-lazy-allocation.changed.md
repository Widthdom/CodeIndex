---
title: Lazily allocate CSharp using import lookups
category: changed
---

## English

- Reduced per-file reference extraction allocations for C# files that do not declare using aliases, using namespace imports, using static imports, or namespace scopes.

## 日本語

- using alias、using namespace import、using static import、namespace scope を宣言しない C# ファイルで、参照抽出時のファイルごとの割り当てを削減しました。
