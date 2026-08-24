---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileContentLoader.cs
  - src/CodeIndex/Indexer/Scanning/FileContentLoader.FileHandleSnapshot.cs
  - src/CodeIndex/Indexer/Scanning/FileContentLoader.RawBytes.cs
  - src/CodeIndex/Indexer/Scanning/FileContentLoader.UnknownLanguage.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.FileHandleSnapshot.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.FileIdentity.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.Types.cs
  - tests/CodeIndex.Tests/FileIndexerContentLoadingTests.cs
  - tests/CodeIndex.Tests/FileIndexerTests.cs
  - DEVELOPER_GUIDE.md
  - TESTING_GUIDE.md
---

## English

- **Open-file metadata probes are aggregated across content-loading paths** — authoritative raw loads, raw-chunk checks, the specialized C# prepass, and unknown-language probes now obtain length, modification time, and file identity together from one platform-native handle snapshot before and after each stable read. This removes redundant handle metadata calls while retaining bounded mutation retries, atomic-replacement and symlink-retarget detection, max-file enforcement, and safe managed fallbacks.

## 日本語

- **open済みfileのmetadata probeをcontent-loading経路全体で集約しました** — authoritative raw load、raw-chunk判定、C#専用prepass、未知言語probeは、stableなreadの前後にplatform-nativeなhandle snapshotを1回ずつ取得し、length・更新時刻・file identityをまとめて得るようになりました。重複するhandle metadata callを削減しつつ、上限付きmutation retry、atomic replacement / symlink retarget検出、max-file強制、安全なmanaged fallbackを維持します。
