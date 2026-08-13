---
category: fixed
affected:
  - src/CodeIndex/Database/CSharpBaseListParser.cs
  - src/CodeIndex/Database/DbContext.ConnectionFunctions.cs
  - src/CodeIndex/Database/DbReader.CSharpResolution.cs
  - src/CodeIndex/Database/DbWriter.CSharpMetadataTargets.cs
---

## English

- **C# inheritance resolution now shares one grammar-aware base-list parser** — exact caller and metadata-target resolution no longer confuse generic `where` constraints with base types, and nested generics, tuples, arrays, alias qualifiers, and primary/base-constructor syntax are split consistently.

## 日本語

- **C# 継承解決が grammar-aware な base-list parser を共有するようになりました** — exact caller と metadata-target の解決で generic `where` constraint を基底型と誤認せず、nested generic、tuple、array、alias qualifier、primary / base constructor 構文を一貫して分割します。
