---
category: changed
affected:
  - src/CodeIndex/Indexer/CSharpStaticInterfacePrepass.cs
---

## English

- **C# static-interface prepass collections now use known target and result sizes** — large scans avoid repeated growth of candidate, pending-path, and symbol buffers, and empty candidate sets skip parallel-loop setup.

## 日本語

- **C# static-interface prepass のコレクションを既知の対象件数と結果件数で確保します** — 大規模 scan で candidate / pending path / symbol buffer の再拡張を避け、候補が空なら parallel loop の準備も省略します。
