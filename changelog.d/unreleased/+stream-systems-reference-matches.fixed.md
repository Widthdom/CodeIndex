---
category: fixed
affected:
  - src/CodeIndex/Indexer/References/Languages/CppReferenceExtractor.cs
  - src/CodeIndex/Indexer/References/Languages/GoReferenceExtractor.cs
  - src/CodeIndex/Indexer/References/Languages/RustReferenceExtractor.cs
  - src/CodeIndex/Indexer/References/Languages/RustReferenceExtractor.SignatureTypes.cs
  - src/CodeIndex/Indexer/References/Languages/RustReferenceExtractor.ValueTypes.cs
  - src/CodeIndex/Indexer/References/Languages/SwiftReferenceExtractor.cs
  - src/CodeIndex/Indexer/References/Languages/ScientificNativeReferenceEmitter.cs
  - src/CodeIndex/Indexer/References/Support/LanguageReferenceExtractionSupport.CppTypeGroups.cs
  - src/CodeIndex/Indexer/References/Support/LanguageReferenceExtractionSupport.Go.cs
  - src/CodeIndex/Indexer/References/Support/LanguageReferenceExtractionSupport.GoCompositeTypes.cs
  - src/CodeIndex/Indexer/References/Support/LanguageReferenceExtractionSupport.GoSignatures.cs
  - DEVELOPER_GUIDE.md
  - TESTING_GUIDE.md
---

## English

- **Streamed systems-language reference matches** — C/C++, Rust, Swift, Go,
  and shared scientific/native scanners now consume dense construction, type,
  concurrency, wrapper, and call matches on demand.

## 日本語

- **systems-language の reference match を逐次走査にしました** — C / C++、Rust、
  Swift、Go と共有 scientific / native scanner は dense な construction、type、
  concurrency、wrapper、call match を demand-driven に消費します。
