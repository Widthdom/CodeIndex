# JSON Compatibility Alias Lifecycle

## English

JSON compatibility aliases are deprecated JSON fields that stay serialized after a contract field is renamed, split, or replaced. Add one only when removing or renaming the field immediately would break machine-readable CLI or MCP consumers.

When a JSON alias is added, the alias property must:

- keep the alias `JsonPropertyName` so existing consumers still receive the old field;
- be marked `[Obsolete]` with the replacement field name and affected contract;
- be represented in `JsonCompatibilityAliasLifecycles` metadata with the alias field, replacement field, contract, JSON contract type, property name, and removal criteria;
- have serialization tests that assert the alias still appears until removal is intentional.

Removal requires an intentional PR after at least one minor release has announced the deprecation. That PR must update serialization tests, documentation, changelog fragments, and any audit commands that scan production JSON contracts. Use production-scoped searches such as:

```bash
cdidx search "JsonCompatibilityAliasLifecycle" --source-only --origin code --path src/CodeIndex/Cli/JsonCompatibilityAliasLifecycles.cs --json=array
cdidx search "Obsolete" --source-only --origin code --path src/CodeIndex/Cli/JsonOutputContracts.cs --json=array
```

Those searches intentionally exclude docs, tests, changelog fragments, and fixtures so reviewers can separate live contract metadata from explanatory mentions.

## 日本語

JSON compatibility alias は、contract field の rename、分割、置き換え後も serialized output に残す非推奨 JSON field です。すぐに削除または rename すると CLI / MCP の machine-readable consumer を壊す場合にだけ追加します。

JSON alias を追加するときは、alias property で次を満たしてください。

- 既存 consumer が古い field を受け取れるよう alias の `JsonPropertyName` を維持する。
- replacement field 名と対象 contract を含む `[Obsolete]` を付ける。
- alias field、replacement field、contract、JSON contract type、property name、removal criteria を持つ `JsonCompatibilityAliasLifecycles` metadata に登録する。
- intentional removal まで alias が serialized output に残ることを serialization test で確認する。

削除には、少なくとも 1 minor release で deprecation を告知した後の intentional PR が必要です。その PR では serialization test、documentation、changelog fragment、production JSON contract を監査する command を更新してください。production code に scope した検索には次を使います。

```bash
cdidx search "JsonCompatibilityAliasLifecycle" --source-only --origin code --path src/CodeIndex/Cli/JsonCompatibilityAliasLifecycles.cs --json=array
cdidx search "Obsolete" --source-only --origin code --path src/CodeIndex/Cli/JsonOutputContracts.cs --json=array
```

これらの検索は docs、tests、changelog fragment、fixture を意図的に除外するため、reviewer は実際の contract metadata と説明上の言及を分けて確認できます。
