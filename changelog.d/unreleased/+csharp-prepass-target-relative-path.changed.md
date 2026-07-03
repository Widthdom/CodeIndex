---
category: changed
affected:
  - src/CodeIndex/Indexer/CSharpStaticInterfacePrepass.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerTests.cs
---

## English

- **C# prepass target creation avoids common relative-path calls** — update and MCP indexing now derive project-root-relative C# prepass paths from the absolute path prefix when possible, falling back to `Path.GetRelativePath` only for nonstandard inputs.

## 日本語

- **C# prepass target 作成で一般的な relative-path 呼び出しを回避** — update と MCP indexing で C# prepass の project-root-relative path を可能な場合は絶対パス prefix から導出し、特殊な入力だけ `Path.GetRelativePath` にフォールバックするようにしました。
