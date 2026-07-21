---
category: changed
affected:
  - src/CodeIndex/Indexer/CSharpStaticInterfacePrepass.cs
  - src/CodeIndex/Indexer/Scanning/FileContentLoader.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.RecordLoading.cs
  - tests/CodeIndex.Tests/FileIndexerTests.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerTests.cs
  - tests/CodeIndex.Tests/PerformanceTests.cs
  - TESTING_GUIDE.md
---

## English

- **C# static-interface prepasses now use one bounded file snapshot per stable candidate** — full scans, scoped updates, and MCP indexing authorize and open each stable candidate once, reuse that handle for raw probing and positive-candidate decoding, and scan comments and strings without allocating a second whole-content mask. A candidate mutated during the read is discarded and reauthorized/reopened once, including atomic-save replacements. Large C# workspaces with many static-interface probe candidates therefore avoid duplicate file opens and multi-megabyte transient allocations while preserving encoding, size, mutation, cancellation, and authorization safeguards.

## 日本語

- **C# static-interface prepass が安定した候補ごとに1つの bounded file snapshot を使うようになりました** — full scan、scoped update、MCP indexing は各安定候補を1回だけ認可・openし、同じ handle を raw probe と positive 候補の decode に再利用し、content 全体の mask を追加確保せず comment と string を走査します。読み取り中に変更された候補は atomic-save replacement を含めて破棄し、1回だけ再認可・再openします。これにより static-interface probe 候補が多い巨大 C# workspace で file open の重複と数 MiB 規模の一時 allocation を避けつつ、encoding、size、mutation、cancellation、authorization の安全契約を維持します。
