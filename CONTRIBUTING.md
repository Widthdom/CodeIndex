# Contributing

Thanks for improving CodeIndex.

## Contribution Policy

Issue reports, feature requests, and improvement suggestions are welcome.
Please open an Issue first if you want to report a bug, request a feature, or
propose an improvement.

This repository currently does not accept external pull requests. Pull request
creation is restricted to collaborators only, and implementation work is handled
by the maintainer or trusted collaborators.

This policy keeps code style, changelog format, review quality, and maintenance
cost consistent. It may be revisited in the future.

## コントリビューション方針

不具合報告、機能要望、改善提案の Issue は歓迎します。バグ報告、機能要望、
改善提案がある場合は、まず Issue を作成してください。

このリポジトリでは、現在外部からの Pull Request は受け付けていません。
Pull Request の作成は collaborator のみに制限しており、実装作業は maintainer
または信頼済み collaborator が行います。

この方針は、コードスタイル、changelog 形式、レビュー品質、保守コストを
一定に保つためのものです。将来的に見直す可能性があります。

## Licensing of Contributions

By submitting a pull request, patch, issue attachment, or other contribution,
you agree that your contribution is provided under the same license that applies
to the file or directory you are contributing to.

In general:

- contributions to the protected implementation are licensed under
  `FSL-1.1-ALv2`;
- contributions to Apache-2.0 integration examples or integration docs are
  licensed under `Apache-2.0`;
- if a file has an explicit SPDX-License-Identifier, that identifier controls.

## Commercial Licensing Note

CodeIndex may offer separate written agreements for competing-product use.
Do not submit contributions unless you are comfortable with the repository's
licensing and commercial-use policy.

If this project later needs a formal contributor license agreement, add it in a
separate explicit change. Do not silently convert this contribution policy into
a copyright assignment.

## Trademarks

Contributions do not grant rights to use the CodeIndex or cdidx names for
derivative products. See `TRADEMARKS.md`.

## Development

Start from the shared project guidance:

- `DEVELOPER_GUIDE.md` for architecture, dependency policy, and build basics.
- `TESTING_GUIDE.md` for test layout, test-writing conventions, and targeted
  test commands.
- `docs/platform-support.md` for official release asset RIDs and unsupported
  platform alternatives.
- `AGENT_GUIDE.md` for repository workflow rules used by coding agents.

For a normal collaborator change, create a topic branch from the latest
`origin/main`. Use a short, descriptive branch name. For issue fixes, use
`fix-issue<issue-number>` unless the issue or maintainer asks for a different
name.

Use English commit messages. Include the relevant issue number when the change
is tied to an issue, for example:

```text
Document contributor workflow guidance (#1590)
```

Keep changes focused and follow the existing style of the files you touch:

- prefer the smallest correct change;
- avoid unrelated refactors;
- update docs when user-visible behavior, commands, workflows, or output
  contracts change;
- add or update tests when behavior changes.

## Release-Critical Paths

Changes to release-critical files require CODEOWNERS review once branch
protection is configured to require it:

- `.github/workflows/`
- `install.sh`
- `src/CodeIndex/CodeIndex.csproj`
- `nuget.config`

When adding a new file that can affect release artifact contents, signing,
publishing, installer behavior, or package restore trust, add it to
`.github/CODEOWNERS` in the same change.

Before a collaborator opens a pull request, run the checks that match the
change. For code changes, the default full validation is:

```bash
make lint
make build
make test
```

Set `FRAMEWORK=net9.0` when you need to match that CI lane, or call
`./dev.sh <task>` directly on systems without `make`. Use narrower
`dotnet test --filter ...` commands while iterating, then finish with the
relevant broader validation before the PR.

## Changelog Fragments

User-visible, behavior-changing, install/release, documentation-contract, and
workflow changes need a changelog fragment under `changelog.d/unreleased/`.
Do not edit `CHANGELOG.md` for ordinary implementation PRs.

Use `.codex/workflows/changelog-fragment.md` for the exact fragment format.
Issue-based changes should use a filename like `<issue>.docs.md` or
`<issue>.fixed.md`, include `issues:` front matter, and include both
`## English` and `## 日本語` sections.

## Pull Request Checklist

Before collaborators request review, confirm:

- the branch is based on the latest practical `origin/main`;
- commits are focused and use English messages;
- relevant tests/builds were run, or the PR explains why they were not needed;
- docs were updated when behavior or workflows changed;
- a bilingual changelog fragment was added when required;
- issue-closing lines use `Fixes #123` in the PR body when the PR should close
  an issue.

Maintainers and coding agents should also follow `.codex/workflows/precommit.md`
before committing.
