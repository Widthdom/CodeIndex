---
category: fixed
affected:
  - .github/dependabot.yml
  - .github/workflows/release.yml
  - DEVELOPER_GUIDE.md
  - src/CodeIndex/CodeIndex.csproj
  - src/CodeIndex/packages.lock.json
  - tests/CodeIndex.Tests/ReleaseWorkflowTests.cs
---

## English

- **Release publishing now validates the NuGet trusted-publishing policy creator before login** — the NuGet job reads `NUGET_TRUSTED_PUBLISHING_USER` before calling `NuGet/login`, and `Microsoft.NET.ILLink.Tasks` stays on the .NET 8 package line so container publish does not load a .NET 10 analyzer under the .NET 8 SDK.

## 日本語

- **release publish が NuGet trusted publishing policy の作成者を login 前に検証するようになりました** — NuGet job は `NuGet/login` を呼ぶ前に `NUGET_TRUSTED_PUBLISHING_USER` を読み、container publish が .NET 8 SDK 上で .NET 10 analyzer を読み込まないよう `Microsoft.NET.ILLink.Tasks` を .NET 8 系に留めます。
