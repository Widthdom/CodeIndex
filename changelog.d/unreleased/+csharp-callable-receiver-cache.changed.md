---
category: changed
affected:
  - src/CodeIndex/Indexer/References/Languages/CSharpReferenceExtractor.Support.cs
  - src/CodeIndex/Indexer/References/Languages/CSharpReferenceExtractor.QualifiedPatterns.cs
  - src/CodeIndex/Indexer/References/Languages/CSharpReferenceExtractor.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.CoreLookups.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.CoreCallSuppressions.cs
  - tests/CodeIndex.Tests/ReferenceExtractorReceiverScopeTests.cs
  - tests/CodeIndex.Tests/ReferenceExtractorPerformanceBudgetTests.cs
---

## English

- **C# receiver checks analyze only the callable scopes they need** — Initial full indexing of C#, Razor, Blazor, and CSHTML avoids scanning unrelated method and property bodies for enum reads and value-receiver calls. Scope results, including empty scopes, are cached; local-function resolution still completes the full lookup, preserving same-line declaration precedence and shadowing behavior.

## 日本語

- **C# の receiver 判定で必要な関数スコープだけを解析** — C#、Razor、Blazor、CSHTML の初回フルインデックスで、列挙型メンバーの読み取りと値 receiver の呼び出しを判定する際、無関係なメソッドやプロパティの本体を走査しなくなりました。空のスコープを含めて結果を再利用し、ローカル関数の解決では全体の lookup を完成させることで、同一行の宣言の優先順位と名前の隠蔽規則を維持します。
