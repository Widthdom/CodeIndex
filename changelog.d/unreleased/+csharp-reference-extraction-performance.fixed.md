---
category: fixed
affected:
  - DEVELOPER_GUIDE.md
  - src/CodeIndex/Indexer/References/Languages/CSharpReferenceExtractor.Support.cs
  - src/CodeIndex/Indexer/References/Languages/CSharpReferenceExtractor.ValueReceivers.cs
  - tests/CodeIndex.Tests/ReferenceExtractorCSharpTests.cs
---

## English

- **Fixed runaway C# reference extraction on very large methods.** Large C# repositories could spend an excessive amount of time in the `references` phase when a file contained a very large method, especially generated code or hand-written methods with thousands of local variables. In affected cases, indexing that previously finished in tens of minutes could continue for hours while making little progress through C# reference extraction.
- The slowdown came from value receiver tracking. For every local value receiver, the extractor rescanned the method body to find the innermost block end and then performed a linear duplicate check against the receivers already collected for the function. Methods with many locals therefore paid the same body scan and growing duplicate check repeatedly, creating super-linear behavior.
- C# reference extraction now builds block scope spans once per function body and reuses them when assigning local receiver scopes. It also uses a hash set for duplicate receiver detection. This keeps receiver collection practical for very large methods while preserving block-scoped behavior for locals that shadow enum or type names.
- Added regression coverage for block-scoped local receiver behavior and a large-method runaway guard so future changes catch this class of performance regression earlier.
- Documented the extractor performance contract in the developer guide so future language-specific extractor changes avoid per-candidate body rescans and linear duplicate checks in hot paths.

## 日本語

- **非常に大きなメソッドで C# 参照抽出が暴走する問題を修正しました。** 大型の C# リポジトリで、巨大な生成コードや数千個規模のローカル変数を持つ手書きメソッドが含まれている場合、インデックス作成が `references` フェーズで極端に長く止まることがありました。影響を受けるケースでは、以前は数十分で終わっていたインデックス作成が、C# の参照抽出中に何時間も進みにくくなることがありました。
- 原因は value receiver 追跡でした。各ローカル value receiver ごとに、最内側ブロックの終端を求めるためメソッド本文を再走査し、その後で関数内に集め済みの receiver に対して線形の重複チェックを行っていました。ローカル変数が多いメソッドでは、同じ本文走査と増え続ける重複チェックを何度も支払うため、super-linear な挙動になっていました。
- C# 参照抽出では、関数本文ごとのブロックスコープ範囲を一度だけ構築し、ローカル receiver のスコープ判定で再利用するようにしました。また、receiver の重複検出にはハッシュセットを使うようにしました。これにより、巨大なメソッドでも実用的な時間で receiver を収集しつつ、enum や type 名を隠すブロックスコープ付きローカルの扱いは維持されます。
- ブロックスコープ付きローカル receiver の挙動と、大きなメソッドでの暴走を検出する回帰テストを追加しました。
- 開発者ガイドに extractor の性能契約を記載し、今後の言語別 extractor 変更で hot path に候補ごとの本文再走査や線形重複チェックを入れないようにしました。
