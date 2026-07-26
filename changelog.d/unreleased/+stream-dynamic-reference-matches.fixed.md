---
category: fixed
affected:
  - src/CodeIndex/Indexer/References/Languages/PhpReferenceExtractor.cs
  - src/CodeIndex/Indexer/References/Languages/PhpReferenceExtractor.LanguageTypes.cs
  - src/CodeIndex/Indexer/References/Languages/PhpReferenceExtractor.Members.cs
  - src/CodeIndex/Indexer/References/Languages/RubyReferenceExtractor.cs
  - src/CodeIndex/Indexer/References/Languages/RReferenceExtractor.cs
  - src/CodeIndex/Indexer/References/Languages/RReferenceExtractor.Members.cs
  - src/CodeIndex/Indexer/References/Languages/RReferenceExtractor.CallsAndResources.cs
  - src/CodeIndex/Indexer/References/Languages/PerlReferenceExtractor.cs
  - DEVELOPER_GUIDE.md
  - TESTING_GUIDE.md
---

## English

- **Streamed dynamic-language reference matches** — PHP, Ruby, R, and Perl
  scanners now enumerate dense attribute, type, DSL, namespace/member/resource,
  and arrow-call matches only while the reference budget has capacity.

## 日本語

- **dynamic-language の reference match を逐次走査にしました** — PHP、Ruby、R、
  Perl scanner は dense な attribute、type、DSL、namespace / member / resource、
  arrow-call match を reference budget に空きがある間だけ列挙します。
