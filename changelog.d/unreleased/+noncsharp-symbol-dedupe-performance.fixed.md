---
category: fixed
affected:
  - src/CodeIndex/Indexer/References/Languages/SwiftReferenceExtractor.cs
  - src/CodeIndex/Indexer/References/Languages/TypeScriptReferenceExtractor.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Cpp.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Dockerfile.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Go.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Java.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.JavaScriptTypeScriptSupport.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Rust.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Shell.cs
  - tests/CodeIndex.Tests/ReferenceExtractorRustSwiftTests.cs
  - tests/CodeIndex.Tests/ReferenceExtractorTests.cs
  - tests/CodeIndex.Tests/SymbolExtractorTests.cs
---

## English

- **Reduced symbol extraction overhead for large non-C# generated files.** Rust `use` expansion, Shell alias expansion, Go grouped declarations, Java/Kotlin primary-constructor or record components, C++ same-line class members, Dockerfile named stage chains, and JavaScript/TypeScript exported surfaces and object/class scan-target collection now avoid candidate-by-candidate scans over growing lists.
- The shared symbol-line identity cache preserves the existing duplicate key of file, line, kind, and name for language paths such as Rust, Shell, Go, and JavaScript/TypeScript synthetic class emission. Java/Kotlin record component materialization now tracks existing component names per parent record, Java compact constructor synthesis reuses the already filtered same-line symbols, C++ same-line class-member backfill tracks member names per container, Dockerfile extraction tracks stage names as the file is scanned, and JavaScript/TypeScript export/object-literal supplement passes keep per-file or per-container name sets plus scan-target identity sets.
- Swift and TypeScript reference extraction also now checks existing type-reference rows through the shared reference dedupe key instead of scanning the accumulated reference list while expanding typealias / type-alias targets.
- These changes avoid theoretical super-linear hot paths in generated files with thousands of declarations, imports, aliases, constructor components, object-literal properties, export variables, object literal targets, or class expression targets while keeping emitted symbols and duplicate semantics unchanged.
- Added large-fixture runaway guards for Rust, Shell, Go, Java, Kotlin, C++, Dockerfile, JavaScript, TypeScript, and Swift so future extractor changes catch this class of non-C# performance regression earlier.

## 日本語

- **大きな C# 以外の生成ファイルに対する symbol 抽出のオーバーヘッドを減らしました。** Rust の `use` 展開、Shell の alias 展開、Go の grouped declaration、Java/Kotlin の primary constructor / record component、C++ の same-line class member、Dockerfile の named stage chain、JavaScript/TypeScript の exported surface と object/class scan-target collection で、候補ごとに増え続ける list を走査しないようにしました。
- 共通の symbol-line identity cache は、Rust、Shell、Go、JavaScript/TypeScript の synthetic class emission などの経路で従来どおり file、line、kind、name を重複キーとして使います。Java/Kotlin の record component materialization は親 record ごとの component name set を使い、Java compact constructor synthesis は同一行に絞り込んだ既存 symbol を再利用し、C++ の same-line class member 補完は container ごとの member name set を使い、Dockerfile 抽出はファイル走査中に stage name を追跡し、JavaScript/TypeScript の export / object literal 補完はファイル単位または container 単位の name set と scan-target identity set を使うようにしました。
- Swift と TypeScript の reference 抽出でも、typealias / type alias target 展開時に accumulated reference list を走査するのではなく、共通の reference dedupe key で既存の type-reference 行を確認するようにしました。
- これにより、数千個の declaration、import、alias、constructor component、object literal property、export variable、object literal target、class expression target を含む生成ファイルで理論上発生しうる super-linear な hot path を避けます。出力される symbol と重複判定の意味は変えていません。
- Rust、Shell、Go、Java、Kotlin、C++、Dockerfile、JavaScript、TypeScript、Swift に対する大規模 fixture の runaway guard を追加し、今後の extractor 変更で同種の C# 以外の性能 regression を早く検出できるようにしました。
