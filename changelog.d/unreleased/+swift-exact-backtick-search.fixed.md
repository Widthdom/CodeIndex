---
category: fixed
affected:
  - src/CodeIndex/Database/DbSymbolReader.cs
  - tests/CodeIndex.Tests/DbReaderTests.cs
---

## English

- **Swift exact symbol search now matches backtick-escaped identifiers by plain name** — exact symbol queries such as `repeat` now also resolve declarations stored as `` `repeat` ``, so Swift search behaves more like the other language-specific exact-name normalizers.

## 日本語

- **Swift の exact symbol search でバッククォート付き識別子を素の名前でも引けるようにしました** — `repeat` のような exact クエリが `` `repeat` `` として保存された Swift 宣言にも一致するようになり、他言語の exact-name 正規化と同じ感覚で検索できるようになりました。
