---
category: changed
affected:
  - .github/workflows/dotnet.yml
  - tests/CodeIndex.Tests/DbReaderTests.cs
  - tests/CodeIndex.Tests/CiWorkflowTests.cs
  - tests/CodeIndex.Tests/PublishedTrimmedCliFactAttribute.cs
  - tests/CodeIndex.Tests/PublishedTrimmedCliFactAttributeTests.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerTests.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerFullScanTests.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerDryRunTests.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerUpdateTests.cs
  - tests/CodeIndex.Tests/InstallScriptTests.cs
  - tests/CodeIndex.Tests/McpServerTests.cs
  - tests/CodeIndex.Tests/PerformanceTests.cs
  - tests/CodeIndex.Tests/ProductionCliFactAttribute.cs
  - tests/CodeIndex.Tests/ProductionCliFactAttributeTests.cs
  - tests/CodeIndex.Tests/ProductionCliTestTarget.cs
  - tests/CodeIndex.Tests/ProductionCliTheoryAttribute.cs
  - tests/CodeIndex.Tests/ProductionRuntimeFactAttribute.cs
  - tests/CodeIndex.Tests/ProductionRuntimeFactAttributeTests.cs
  - tests/CodeIndex.Tests/ProductionRuntimeTestTarget.cs
  - tests/CodeIndex.Tests/ProductionRuntimeTheoryAttribute.cs
  - tests/CodeIndex.Tests/QueryCommandRunnerFilesTests.cs
  - tests/CodeIndex.Tests/QueryCommandRunnerInspectTests.cs
  - tests/CodeIndex.Tests/QueryCommandRunnerReferencesTests.cs
  - tests/CodeIndex.Tests/QueryCommandRunnerTests.cs
  - tests/CodeIndex.Tests/DbDebugTests.cs
  - tests/CodeIndex.Tests/PostExtractionHookTests.cs
  - tests/CodeIndex.Tests/ProgramCliTests.cs
  - TESTING_GUIDE.md
---

## English

- Published trimmed CLI smoke tests now run only on the `net8.0` test target, avoiding duplicate expensive `dotnet publish` work in `net9.0` Build and Test matrix lanes while retaining focused cross-target in-process coverage.
- Non-primary Build and Test matrix lanes now restore only the test project and its references instead of restoring the full solution before building the test project.
- The MCP invalid UTF-8 stdio ordering test now uses transport signals instead of a fixed 200 ms serializer sleep.
- Build and Test now keeps OS coverage on the production `net8.0` target and runs the `net9.0` compatibility suite on Ubuntu only.
- Performance smoke and allocation budget guards now run only on the `net8.0` test target.
- The file hotspot structural-rank fixture now uses threshold-sized reference counts instead of oversized synthetic call sets.
- The DB debug query-plan cap test now uses the minimum UNION fixture needed to cross the truncation boundary.
- The primary Build and Test lane now builds only the matrix test target instead of building the test project's unused `net9.0` target during the Release solution build.
- Non-primary Build and Test lanes now restore only the matrix target framework, and `.NET 9` SDK setup is skipped outside the primary and `net9.0` compatibility lanes.
- Installer snippet tests now run only on the production `net8.0` test target because `install.sh` is target-framework independent, avoiding duplicate bash subprocess coverage in the `net9.0` compatibility lane.
- Heavy post-extraction hook worker integration tests now run only on the `net8.0` production target, and their timeout/cancellation sentinel delays are shorter while still exceeding the callback budgets they guard.
- `RunBuiltCli` / `RunCliInSubprocess` subprocess tests, including timeout-guarded FIFO probes, now run only on the `net8.0` production target when the subprocess resolves to the production CLI, while direct in-process command-runner tests remain cross-target.
- Reference-count limit tests now use the minimum dense C# call fixture needed to cross the configured threshold.

## 日本語

- published trimmed CLI smoke test は `net8.0` test target でのみ実行するようになり、focused な cross-target in-process coverage は維持しつつ、Build and Test の `net9.0` matrix lane で高コストな `dotnet publish` を重複実行しないようにしました。
- Build and Test の non-primary matrix lane は、test project を build する前に solution 全体ではなく test project とその参照だけを restore するようにしました。
- MCP の invalid UTF-8 stdio ordering test は、固定 200 ms の serializer sleep ではなく transport signal を使うようにしました。
- Build and Test は OS coverage を production target の `net8.0` で維持し、`net9.0` compatibility suite は Ubuntu のみに絞りました。
- performance smoke と allocation budget guard は `net8.0` test target でのみ実行するようにしました。
- file hotspot structural-rank fixture は、過大な synthetic call set ではなく threshold に必要な reference count だけを使うようにしました。
- DB debug の query-plan cap test は、truncation boundary を超えるために必要な最小限の UNION fixture を使うようにしました。
- Build and Test の primary lane は Release solution build で test project の未使用 `net9.0` target まで build せず、matrix の test target だけを build するようにしました。
- Build and Test の non-primary lane は matrix の target framework だけを restore し、`.NET 9` SDK setup は primary lane と `net9.0` compatibility lane 以外では skip するようにしました。
- installer snippet test は、`install.sh` が target framework 非依存であることに合わせて production target の `net8.0` でのみ実行し、`net9.0` compatibility lane で bash subprocess coverage を重複実行しないようにしました。
- 重い post-extraction hook worker integration test は `net8.0` production target でのみ実行し、timeout / cancellation の sentinel delay も guard 対象の callback budget を超える範囲で短縮しました。
- `RunBuiltCli` / `RunCliInSubprocess` を使う subprocess test は、timeout guard 付きの FIFO probe も含め、subprocess が production CLI に解決される場合に `net8.0` production target でのみ実行し、direct in-process の command-runner test は cross-target のままにしました。
- reference-count limit test は、設定した閾値を超えるために必要な最小限の dense C# call fixture を使うようにしました。
