---
category: internal
affected:
  - tests/CodeIndex.Tests/PerformanceTests.cs
  - TESTING_GUIDE.md
---

## English

- **Record-component allocation coverage allows for Linux runtime variance** — the C# guard now uses a 12.8 MB ceiling so normal Ubuntu runner allocation does not fail release CI, while Java and Kotlin budgets and all component-result assertions remain unchanged.

## 日本語

- **record component の allocation coverage が Linux runtime の差を許容するようになりました** — C# guard の上限を 12.8 MB とし、Ubuntu runner の通常 allocation で release CI が失敗しないようにしました。Java / Kotlin の budget と component 結果の全 assertion は変更していません。
