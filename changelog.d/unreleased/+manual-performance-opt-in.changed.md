---
category: changed
affected:
  - tests/CodeIndex.Tests/ManualPerformanceFactAttribute.cs
  - tests/CodeIndex.Tests/PerformanceTests.cs
---

## English

- **Manual performance tests now have an executable opt-in contract** — setting `CDIDX_RUN_MANUAL_PERFORMANCE_TESTS=1` together with a focused test filter runs the selected production-runtime benchmark, while ordinary CI still skips it. The 1,000-file search fixture is also named for its actual scale.

## 日本語

- **手動performance testに実行可能なopt-in契約を追加しました** — `CDIDX_RUN_MANUAL_PERFORMANCE_TESTS=1` とfocused test filterを併用すると選択したproduction-runtime benchmarkが実行され、通常CIでは引き続きskipされます。1,000-file search fixtureの名前も実際の規模に合わせました。
