# Generated Code and Dependency Metadata Audits

## English

Generated files detected by bounded markers such as `.Designer.cs`,
`.Generated.cs`, `.g.cs`, and top-of-file generated headers are hidden from
query results by default. Use `--include-generated` when generated code is the
target. CodeIndex intentionally keeps a single generated-code visibility switch
instead of a separate generated-only file mode, so generated-code audits should
combine `--include-generated` with structured `files`, `symbols`, `references`,
or `search` output plus ordinary `--path` / `--lang` scoping.

Dependency manifests and lockfiles are indexed as `dependency_manifest` and
`dependency_lock`. For dependency, security, or supply-chain triage, start with
structured package symbols and `dependency` references before broad text search,
for example `symbols --lang dependency_manifest` or
`symbols --lang dependency_lock`.

## 日本語

`.Designer.cs`、`.Generated.cs`、`.g.cs`、file 先頭の generated header など
bounded な marker で検出された生成ファイルは、既定では query 結果から隠れます。
生成コード自体を調べる場合は `--include-generated` を使います。CodeIndex は generated-only の
file mode を別に増やさず、生成コードの可視化境界を 1 つの switch に保つ方針です。
生成コード監査では、`--include-generated` と構造化された `files`、`symbols`、`references`、
`search` 出力に、通常の `--path` / `--lang` scope を組み合わせてください。

dependency manifest と lockfile は `dependency_manifest` / `dependency_lock` として
index されます。dependency、security、supply-chain の triage では、広い text search の前に、
`symbols --lang dependency_manifest` や `symbols --lang dependency_lock` などで
構造化された package symbol と `dependency` reference から確認してください。
