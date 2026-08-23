---
category: internal
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.CSharpScanner.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.ExtractCore.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.ExtractionPhases.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.PatternExtraction.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.PatternFlow.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.PatternLoop.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.PatternMatching.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Patterns.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.cs
  - tests/CodeIndex.Tests/SymbolExtractorCSharpRegexProbeTests.cs
  - DEVELOPER_GUIDE.md
---

## English

- **C# symbol extraction now avoids regex probes that cannot succeed** — completed property inputs and plain-field inputs without a required terminator are rejected with allocation-free character checks, while wrapped-modifier recovery caches absent and present lookups and materializes confirmed prefixes once. Multiline default arguments, incomplete-attribute recovery, same-line offsets, diagnostics, and cancellation behavior remain unchanged.

## 日本語

- **C# symbol extraction が成功不可能な regex probe を回避するようになりました** — 完結済み property input と必須終端を含まない plain-field input を allocation-free な文字判定で除外し、wrapped-modifier recovery は有無どちらの lookup も cache し、確定した prefix を1回だけ materialize します。複数行 default argument、不完全 attribute recovery、same-line offset、diagnostic、cancellation の振る舞いは変わりません。
