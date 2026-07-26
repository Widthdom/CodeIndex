---
category: fixed
affected:
  - src/CodeIndex/Indexer/BoundedRegex.cs
  - src/CodeIndex/Indexer/References/Languages/PythonReferenceExtractor.cs
  - src/CodeIndex/Indexer/References/Languages/PythonReferenceExtractor.ClassBases.cs
  - src/CodeIndex/Indexer/References/Languages/PythonReferenceExtractor.DataclassFields.cs
  - src/CodeIndex/Indexer/References/Languages/PythonReferenceExtractor.FrameworkIntegrations.cs
  - src/CodeIndex/Indexer/References/Languages/PythonReferenceExtractor.FunctionSignatures.cs
  - src/CodeIndex/Indexer/References/Languages/PythonReferenceExtractor.RuntimeTypes.cs
  - src/CodeIndex/Indexer/References/Languages/PythonReferenceExtractor.TypingFactories.cs
  - tests/CodeIndex.Tests/BoundedRegexTests.cs
  - DEVELOPER_GUIDE.md
  - TESTING_GUIDE.md
---

## English

- **Streamed Python reference matches** — Python decorators, annotations,
  runtime type checks, typing factories, dataclass/framework integrations, and
  dynamic imports now stop producing regex matches at the reference cap;
  decorator arguments also stream directly from their start offset.

## 日本語

- **Python の reference match を逐次走査にしました** — decorator、annotation、
  runtime type check、typing factory、dataclass / framework integration、dynamic
  import は reference 上限で regex match の生成を停止し、decorator argument も指定
  offset から直接逐次走査します。
