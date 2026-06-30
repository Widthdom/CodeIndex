---
category: changed
affected:
  - .github/workflows/dotnet.yml
  - .github/scripts/run-dotnet-tests.ps1
  - src/CodeIndex/Indexer/References/Support/StructuralLineMasker.cs
  - tests/CodeIndex.Tests/CiWorkflowTests.cs
  - tests/CodeIndex.Tests/ReferenceExtractorTests.cs
  - TESTING_GUIDE.md
---

## English

- **Reduced CI test-runner duplication and large C# indexing allocation pressure** - the `dotnet.yml` matrix test step now delegates test argument construction, failure-log capture, timeout handling, and flaky retry classification to a dedicated PowerShell helper, while C# structural masking reuses unchanged lines instead of allocating a new string for every line during symbol/reference extraction.

## 日本語

- **CI test runner の重複と巨大 C# indexing 時の allocation 負荷を減らしました** - `dotnet.yml` の matrix test step は test argument 構築、failure log capture、timeout handling、flaky retry classification を専用 PowerShell helper に委譲し、C# structural masking は symbol/reference extraction 中に全行へ新しい string を割り当てず、変更のない行を再利用するようになりました。
