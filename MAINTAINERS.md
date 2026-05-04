# Maintainers & Authorized Operators

> **[日本語版はこちら / Japanese version](#maintainer-と認可オペレーター向け)**

This index lists the documents and sections that are **only relevant to the
repository owner, maintainers, or authorized release operators** — not to end users who simply
`cdidx index` their codebase. They cover releasing, CI/install plumbing, and
the AI-driven self-improvement workflow on this specific repo.

If you are an end user looking for usage docs, you can ignore everything on
this page — `README.md` is enough.

## What's on this page for

- **Releasing a new version of cdidx.** Only the owner has release push
  permissions; do not reuse the official release workflow, package identity,
  or cdidx/CodeIndex branding for derivative distributions without written
  permission.
  → [DEVELOPER_GUIDE.md → "Release Workflow"](docs/DEVELOPER_GUIDE.md#release-workflow)
- **Bootstrapping a Claude Code cloud session with no local .NET SDK.** Only
  useful to someone who wants to run Claude Code *against this repo* from a
  SDK-less container as an authorized maintainer.
  → [CLOUD_BOOTSTRAP_PROMPT.md](docs/CLOUD_BOOTSTRAP_PROMPT.md) — drop-in first-turn prompt.
  → [DEVELOPER_GUIDE.md → "Cloud Claude Code bootstrap (no .NET SDK)"](docs/DEVELOPER_GUIDE.md#cloud-claude-code-bootstrap-no-net-sdk) — deep dive on the install/runtime mechanics behind the prompt.
- **AI-driven self-improvement loop.** The operating contract used by
  maintainer-run Claude Code sessions to iterate on cdidx itself. End users
  shouldn't need this.
  → [SELF_IMPROVEMENT.md](docs/SELF_IMPROVEMENT.md)

## Why these are separated

The linked documents live under `docs/` so the repository root stays focused
on conventional entry points and release/package assets. This page flags them
as *not part of the end-user documentation surface* so that:

- End users don't waste time reading release / CI internals.
- Maintainers and authorized operators have one entry point to everything operational.
- New maintainer-facing docs have an obvious home to get linked from.

---

# Maintainer と認可オペレーター向け

このページは、**このリポジトリの Maintainer または認可されたリリース担当者にのみ
関係する**ドキュメントとセクションの索引です。単に自分のコードベースを
`cdidx index` したいエンドユーザーには不要な情報です。リリース、CI と
インストールの裏側、およびこのリポジトリ固有の AI 駆動自己改善フローを扱います。

使い方を知りたいだけのエンドユーザーはこのページを無視して構いません。
`README.md` だけで十分です。

## このページが扱う範囲

- **cdidx の新バージョンリリース。** 公式リリースの push 権限を持つのは Maintainer だけです。派生配布で公式 release workflow、package identity、cdidx / CodeIndex branding を再利用するには書面による許可が必要です。
  → [DEVELOPER_GUIDE.md → 「リリース手順」](docs/DEVELOPER_GUIDE.md#リリース手順)
- **.NET SDK のないコンテナから Claude Code Cloud セッションを bootstrap する。** SDK の無いコンテナから *このリポジトリ* に対して Claude Code を走らせたい認可 Maintainer 向けのワークフローです。
  → [CLOUD_BOOTSTRAP_PROMPT.md](docs/CLOUD_BOOTSTRAP_PROMPT.md) — 初回投入用のプロンプト。
  → [DEVELOPER_GUIDE.md → 「Cloud Claude Code bootstrap（.NET SDK なし）」](docs/DEVELOPER_GUIDE.md#cloud-bootstrap-no-dotnet-sdk-ja) — そのプロンプトの裏で走るインストール・ランタイムの詳細解説。
- **AI 駆動の自己改善ループ。** Maintainer が走らせる Claude Code セッションが cdidx 自身を改善するときの運用契約。エンドユーザーには不要です。
  → [SELF_IMPROVEMENT.md](docs/SELF_IMPROVEMENT.md)

## なぜ分離するのか

各ドキュメントは `docs/` 配下に置き、リポジトリルートは慣例的な入口と
リリース／パッケージ資産に絞っています。このページは
「**エンドユーザー向けドキュメントの範囲外**」という旗を立てる役割を担います:

- エンドユーザーがリリース内部や CI 内部の情報を読んで時間を無駄にしない。
- Maintainer と認可オペレーターに、運用系ドキュメントの単一の入口を提供する。
- 今後 maintainer 向けドキュメントを足すときの、明示的なリンク元になる。
