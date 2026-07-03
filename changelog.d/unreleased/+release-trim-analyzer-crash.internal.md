---
category: internal
affected:
  - src/CodeIndex/CodeIndex.csproj
  - DEVELOPER_GUIDE.md
---

## English

- Disabled the Roslyn compile-time trim analyzer for trimmed publishes so release RIDs still run the real ILLink trim pass without failing early on the .NET 8 analyzer AD0001 crash.

## 日本語

- trimmed publish では Roslyn compile-time trim analyzer を無効化し、.NET 8 analyzer の AD0001 crash で早期失敗せずに、実際の ILLink trim pass を release RID で継続実行できるようにしました。
