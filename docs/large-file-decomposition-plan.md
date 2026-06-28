# Large File Decomposition Plan

> **[日本語版はこちら / Japanese version](#巨大ファイル分割計画)**

This plan tracks the intended decomposition path for the largest production
files called out by `cdidx map --json --limit 30 --path src/ --exclude-tests`
in issue #3007. It is a planning document only: each implementation PR should
move one ownership boundary at a time, preserve public behavior, and carry the
focused tests for the behavior it moves.

## Baseline

| File | Current role | Pressure |
|---|---|---|
| `src/CodeIndex/Cli/QueryCommandRunner.cs` | Query command parsing, validation, dispatch, and formatting glue. | Many command families share one runner, making review scope broad for query changes. |
| `src/CodeIndex/Indexer/Symbols/SymbolExtractor.cs` | Multi-language symbol extraction plus shared scanner helpers. | Language-specific logic and shared extraction contracts are interleaved. |
| `src/CodeIndex/Indexer/Symbols/SymbolExtractor.JavaScriptTypeScriptSupport.cs` | JavaScript/TypeScript symbol support. | Lexing, scope tracking, import/export parsing, and symbol emission live together. |
| `src/CodeIndex/Indexer/References/Support/LanguageReferenceExtractionSupport.cs` | Shared reference-extraction helpers for typed languages. | Masking, type-position scanning, trailing-lambda handling, JVM method references, and SQL name resolution share one support file. |
| `src/CodeIndex/Mcp/McpToolHandlers.cs` | MCP tool argument parsing, command execution, and result shaping. | Query, index, status, and maintenance handlers share one large orchestration file. |
| `src/CodeIndex/Indexer/Scanning/FileIndexer.cs` | File enumeration, ignore handling, language detection, validation, and record construction. | Filesystem policy and per-file extraction preparation are coupled. |

## Issue #4061 Snapshot

Issue #4061 refreshes the baseline with a fresh local index and tracks every
split with the same dogfood queries before and after each move:

```bash
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll files --bytes --path src/ --exclude-tests --limit 40 --json
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll hotspots --group-by file --path src/ --exclude-tests --limit 40 --json
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll symbols --sort size --lang csharp --exclude-tests --limit 120 --json
```

The pre-split snapshot on `origin/main` at
`eaf0e9114c945598e44b11d8ac6010f2087ea046` still shows the same primary
concentration points:

| Rank | File or symbol | Dogfood signal |
|---|---|---|
| 1 | `QueryCommandRunner.cs` / `QueryCommandRunner` | 791,221 bytes, 15,893 file lines, 15,736 symbol lines, 3,233 file-hotspot refs |
| 2 | `SymbolExtractor.cs` / `SymbolExtractor` | 630,225 bytes, 11,308 file lines, 11,294 symbol lines, 2,635 file-hotspot refs |
| 3 | `SymbolExtractor.JavaScriptTypeScriptSupport.cs` / `SymbolExtractor` | 444,233 bytes, 11,661 file lines, 11,653 symbol lines, 665 file-hotspot refs |
| 4 | `McpToolHandlers.cs` / `McpServer` | 383,924 bytes, 7,708 file lines, 7,689 symbol lines, 826 file-hotspot refs |
| 5 | `DbSymbolReader.cs` | 289,445 bytes, 6,016 file lines, 855 file-hotspot refs |
| 6 | `LanguageReferenceExtractionSupport.cs` / `LanguageReferenceExtractionSupport` | 229,143 bytes, 5,149 file lines, 5,143 symbol lines, 629 file-hotspot refs |
| 7 | `McpServer.cs` / `McpServer` | 219,237 bytes, 4,417 file lines, 4,388 symbol lines, 926 file-hotspot refs |
| 8 | `DbWriter.cs` / `DbWriter` | 200,679 bytes, 4,458 file lines, 4,441 symbol lines, 1,898 file-hotspot refs |
| 9 | `FileIndexer.cs` / `FileIndexer` | 199,007 bytes, 5,022 file lines, 5,005 symbol lines, 871 file-hotspot refs |

This PR starts the staged plan by moving the `hotspots` command implementation
from `QueryCommandRunner.cs` into `QueryCommandRunner.Hotspots.cs`. The move is
mechanical and preserves the CLI/MCP behavior contract; it exists to prove the
tracking loop and reduce the first command-family ownership boundary without
mixing it with parser or output changes.

After that first split, the same dogfood queries report:

| File or symbol | Before | After |
|---|---:|---:|
| `QueryCommandRunner.cs` file size | 791,221 bytes / 15,893 lines | 765,328 bytes / 15,439 lines |
| `QueryCommandRunner` symbol size | 15,736 lines | 15,282 lines |
| `QueryCommandRunner.cs` file-hotspot refs | 3,233 refs / 555 symbols | 3,194 refs / 551 symbols |
| `QueryCommandRunner.Hotspots.cs` | not present | 25,993 bytes / 461 lines / 306 refs |

## Decomposition Sequence

1. `QueryCommandRunner.cs`
   - Extract query option parsing and validation into command-family helpers.
   - Move result formatting helpers only after the parsing boundary is stable.
   - Keep CLI text, JSON, LSP, quickfix, and SARIF tests in the same PR as the moved command family.
   - Current #4061 split: `hotspots` command execution lives in `QueryCommandRunner.Hotspots.cs`; keep follow-up command-family moves similarly mechanical.

2. `SymbolExtractor.cs`
   - Separate shared extraction contracts from language-specific scanners.
   - Move one language or language family per PR.
   - Preserve the extractor concurrency contract in `DEVELOPER_GUIDE.md` and keep language-specific symbol tests beside the move.

3. `SymbolExtractor.JavaScriptTypeScriptSupport.cs`
   - Split lexical helpers, scope/import/export tracking, and symbol emission into focused partials or support classes.
   - Keep TypeScript and JavaScript fixture tests paired so shared behavior does not drift between the two languages.

4. `LanguageReferenceExtractionSupport.cs`
   - Split masking, type-position scanning, trailing-lambda handling, JVM method references, and SQL name resolution into focused support modules.
   - Keep reference-extractor tests attached to each moved helper so graph edges and language guards do not drift.
   - Preserve the typed-language reference extraction contract before changing any language-specific extractor.

5. `McpToolHandlers.cs`
   - Group handlers by tool family: query tools, indexing tools, status/diagnostic tools, and maintenance tools.
   - Preserve MCP schema names, stability markers, annotations, and error envelopes in each move.
   - Update MCP tests when any handler boundary changes result shaping or validation.

6. `FileIndexer.cs`
   - Separate path discovery and ignore policy from file content validation and record construction.
   - Keep content loading, decoding, line normalization, and checksum logic in `FileContentLoader` so `FileIndexer` can stay focused on traversal and record orchestration.
   - Move filesystem-sensitive behavior behind testable helpers before changing indexing flow.
   - Keep cross-platform path, symlink, hidden/system attribute, and cleanup tests attached to each boundary move.

## Review Gates

Each decomposition PR should include:

- a narrow diff that moves one boundary or one language family;
- no public CLI/MCP output changes unless the PR explicitly documents and tests them;
- targeted tests for the moved behavior plus existing regression coverage for the surrounding command or extractor family;
- a changelog fragment only when the move changes user-visible behavior, CLI/MCP output, docs, or workflow contracts;
- an adversarial review focused on behavior drift, missing tests, and stale documentation.

## Non-Goals

- Do not rename commands, flags, MCP tool names, JSON fields, or status contract fields as part of decomposition-only work.
- Do not change database schema or extraction semantics just to reduce file size.
- Do not combine broad refactors across CLI, indexer, and MCP ownership areas in one PR.

---

<a id="巨大ファイル分割計画"></a>
# 巨大ファイル分割計画

この計画は、issue #3007 で `cdidx map --json --limit 30 --path src/ --exclude-tests`
により指摘された最大級の production file を分割していくための追跡文書です。
これは計画のみであり、各実装 PR は ownership boundary を 1 つずつ移動し、
public behavior を維持し、移動した挙動に対応する focused test を含めてください。

## ベースライン

| ファイル | 現在の役割 | 圧力 |
|---|---|---|
| `src/CodeIndex/Cli/QueryCommandRunner.cs` | query command の parsing、validation、dispatch、formatting glue。 | 多くの command family が 1 つの runner を共有しており、query 変更の review scope が広くなる。 |
| `src/CodeIndex/Indexer/Symbols/SymbolExtractor.cs` | 複数言語の symbol extraction と共有 scanner helper。 | 言語固有 logic と共有 extraction contract が入り混じっている。 |
| `src/CodeIndex/Indexer/Symbols/SymbolExtractor.JavaScriptTypeScriptSupport.cs` | JavaScript/TypeScript symbol support。 | lexing、scope tracking、import/export parsing、symbol emission が同居している。 |
| `src/CodeIndex/Indexer/References/Support/LanguageReferenceExtractionSupport.cs` | typed language 向けの共有 reference-extraction helper。 | masking、type-position scanning、trailing-lambda handling、JVM method reference、SQL name resolution が 1 つの support file を共有している。 |
| `src/CodeIndex/Mcp/McpToolHandlers.cs` | MCP tool の argument parsing、command execution、result shaping。 | query、index、status、maintenance handler が 1 つの大きな orchestration file を共有している。 |
| `src/CodeIndex/Indexer/Scanning/FileIndexer.cs` | file enumeration、ignore handling、language detection、validation、record construction。 | filesystem policy と per-file extraction preparation が結合している。 |

## Issue #4061 のスナップショット

issue #4061 では、fresh なローカル index でベースラインを更新し、各 split
の前後を同じ dogfood query で追跡します。

```bash
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll files --bytes --path src/ --exclude-tests --limit 40 --json
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll hotspots --group-by file --path src/ --exclude-tests --limit 40 --json
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll symbols --sort size --lang csharp --exclude-tests --limit 120 --json
```

`origin/main` の `eaf0e9114c945598e44b11d8ac6010f2087ea046` で取得した
split 前のスナップショットでは、主要な集中箇所は引き続き次のとおりです。

| 順位 | ファイルまたはシンボル | dogfood signal |
|---|---|---|
| 1 | `QueryCommandRunner.cs` / `QueryCommandRunner` | 791,221 bytes、15,893 file lines、15,736 symbol lines、3,233 file-hotspot refs |
| 2 | `SymbolExtractor.cs` / `SymbolExtractor` | 630,225 bytes、11,308 file lines、11,294 symbol lines、2,635 file-hotspot refs |
| 3 | `SymbolExtractor.JavaScriptTypeScriptSupport.cs` / `SymbolExtractor` | 444,233 bytes、11,661 file lines、11,653 symbol lines、665 file-hotspot refs |
| 4 | `McpToolHandlers.cs` / `McpServer` | 383,924 bytes、7,708 file lines、7,689 symbol lines、826 file-hotspot refs |
| 5 | `DbSymbolReader.cs` | 289,445 bytes、6,016 file lines、855 file-hotspot refs |
| 6 | `LanguageReferenceExtractionSupport.cs` / `LanguageReferenceExtractionSupport` | 229,143 bytes、5,149 file lines、5,143 symbol lines、629 file-hotspot refs |
| 7 | `McpServer.cs` / `McpServer` | 219,237 bytes、4,417 file lines、4,388 symbol lines、926 file-hotspot refs |
| 8 | `DbWriter.cs` / `DbWriter` | 200,679 bytes、4,458 file lines、4,441 symbol lines、1,898 file-hotspot refs |
| 9 | `FileIndexer.cs` / `FileIndexer` | 199,007 bytes、5,022 file lines、5,005 symbol lines、871 file-hotspot refs |

この PR では staged plan の最初の move として、`hotspots` command 実装を
`QueryCommandRunner.cs` から `QueryCommandRunner.Hotspots.cs` へ移しました。
この移動は mechanical であり、CLI/MCP behavior contract は変更しません。
parser や output 変更と混ぜずに、最初の command-family ownership boundary を
小さくしつつ tracking loop を確認するための split です。

この最初の split 後に同じ dogfood query で確認した結果は次のとおりです。

| ファイルまたはシンボル | Before | After |
|---|---:|---:|
| `QueryCommandRunner.cs` file size | 791,221 bytes / 15,893 lines | 765,328 bytes / 15,439 lines |
| `QueryCommandRunner` symbol size | 15,736 lines | 15,282 lines |
| `QueryCommandRunner.cs` file-hotspot refs | 3,233 refs / 555 symbols | 3,194 refs / 551 symbols |
| `QueryCommandRunner.Hotspots.cs` | なし | 25,993 bytes / 461 lines / 306 refs |

## 分割順序

1. `QueryCommandRunner.cs`
   - query option parsing と validation を command-family helper へ抽出する。
   - parsing boundary が安定してから result formatting helper を移動する。
   - 移動した command family と同じ PR で CLI text、JSON、LSP、quickfix、SARIF test を維持する。
   - 現在の #4061 split: `hotspots` command 実行は `QueryCommandRunner.Hotspots.cs` に置く。後続の command-family move も同じく mechanical に保つ。

2. `SymbolExtractor.cs`
   - 共有 extraction contract と言語固有 scanner を分ける。
   - 1 PR につき 1 言語または 1 language family を移動する。
   - `DEVELOPER_GUIDE.md` の extractor concurrency contract を維持し、言語固有 symbol test を移動と一緒に保つ。

3. `SymbolExtractor.JavaScriptTypeScriptSupport.cs`
   - lexical helper、scope/import/export tracking、symbol emission を focused partial または support class へ分ける。
   - TypeScript と JavaScript の fixture test を組にして維持し、共有挙動が 2 言語間で drift しないようにする。

4. `LanguageReferenceExtractionSupport.cs`
   - masking、type-position scanning、trailing-lambda handling、JVM method reference、SQL name resolution を focused support module へ分ける。
   - 移動した helper ごとに reference-extractor test を付け、graph edge と language guard が drift しないようにする。
   - 言語別 extractor を変更する前に typed-language reference extraction contract を維持する。

5. `McpToolHandlers.cs`
   - handler を tool family ごとにまとめる: query tools、indexing tools、status/diagnostic tools、maintenance tools。
   - MCP schema name、stability marker、annotation、error envelope を各移動で維持する。
   - handler boundary の変更が result shaping や validation に影響する場合は MCP test を更新する。

6. `FileIndexer.cs`
   - path discovery と ignore policy を file content validation と record construction から分ける。
   - content loading、decoding、line normalization、checksum logic は `FileContentLoader` に置き、`FileIndexer` は traversal と record orchestration に集中させる。
   - indexing flow を変更する前に、filesystem-sensitive behavior を testable helper の背後へ移す。
   - cross-platform path、symlink、hidden/system attribute、cleanup test を各 boundary move に付ける。

## レビューゲート

各 decomposition PR は次を含めてください。

- 1 つの boundary または 1 つの language family だけを移動する narrow diff;
- PR が明示的に文書化し test していない限り、public CLI/MCP output を変更しないこと;
- 移動した挙動の targeted test と、周辺 command/extractor family の既存 regression coverage;
- user-visible behavior、CLI/MCP output、docs、workflow contract が変わる場合のみ changelog fragment;
- behavior drift、missing test、stale documentation に焦点を当てた adversarial review。

## 非目標

- decomposition-only work の一部として command、flag、MCP tool name、JSON field、status contract field を rename しない。
- file size を減らすことだけを目的に database schema や extraction semantics を変更しない。
- CLI、indexer、MCP の ownership area をまたぐ広範な refactor を 1 PR にまとめない。
