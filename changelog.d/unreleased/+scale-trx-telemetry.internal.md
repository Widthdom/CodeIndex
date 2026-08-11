---
category: internal
affected:
  - tools/CodeIndex.TestTelemetry/TrxTelemetry.cs
  - TESTING_GUIDE.md
---

## English

- **TRX telemetry now handles the full repository suite efficiently** — The guarded per-file limit now accommodates current full-suite result files, while bounded slow/failure rankings use ordered insertion instead of sorting their full retained list for every test result.

## 日本語

- **TRX telemetry が repository の full suite を効率的に扱えるようになりました** — file ごとの保護上限を現行 full-suite result に対応させ、上限付き slow/failure ranking は test result ごとの全 retained list 再 sort を ordered insertion に置き換えました。
