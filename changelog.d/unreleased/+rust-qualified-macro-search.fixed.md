---
category: fixed
affected:
  - src/CodeIndex/Database/DbSymbolReader.cs
  - tests/CodeIndex.Tests/DbReaderTests.cs
---

## English

- **Rust exact macro queries now keep their qualified path** — exact searches for qualified Rust macro invocations no longer collapse to the leaf name first, so `crate::macros::build!` resolves the intended path-specific reference instead of overmatching sibling macros.

## 日本語

- **Rust の exact な macro クエリが qualified path を保持するようになりました** — qualified な Rust macro 呼び出しの exact search では leaf 名に潰さず、そのまま path を使って照合するため、`crate::macros::build!` が sibling macro に誤って広がらず、意図した参照だけを返します。
