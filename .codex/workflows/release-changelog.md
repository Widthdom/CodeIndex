# Release Changelog Workflow

This workflow is shared by Codex, Claude Code, and human maintainers.
Use it only for release-preparation PRs.

## Inputs

- Target version, for example `1.17.0`.
- Release date, for example `2026-05-01`.

## Start from latest main

```bash
git fetch origin
git switch -c release/v1.17.0 origin/main
```

## Prepare the changelog

```bash
dotnet run --project tools/CodeIndex.Changelog -- prepare --version 1.17.0 --date 2026-05-01
```

This command must:

- validate `changelog.d/unreleased/*.md`;
- aggregate fragments into both the English and 日本語 sections of
  `CHANGELOG.md`;
- preserve and carry forward any legacy direct content already present under
  `### [Unreleased]`;
- reset both English and 日本語 `### [Unreleased]` sections to empty;
- add `### [1.17.0] - 2026-05-01` release sections;
- update `version.json` to `1.17.0`;
- remove consumed fragments from `changelog.d/unreleased/` while keeping
  `.gitkeep`;
- update the compare-link footer.

## Compare-link footer

For a release from `1.16.0` to `1.17.0`, the footer must change from:

```md
[Unreleased]: https://github.com/Widthdom/CodeIndex/compare/v1.16.0...HEAD
[1.16.0]: https://github.com/Widthdom/CodeIndex/compare/v1.15.3...v1.16.0
```

to:

```md
[Unreleased]: https://github.com/Widthdom/CodeIndex/compare/v1.17.0...HEAD
[1.17.0]: https://github.com/Widthdom/CodeIndex/compare/v1.16.0...v1.17.0
[1.16.0]: https://github.com/Widthdom/CodeIndex/compare/v1.15.3...v1.16.0
```

Do not duplicate existing version links.

## Validate

```bash
dotnet restore CodeIndex.sln
dotnet build CodeIndex.sln -c Release
dotnet test CodeIndex.sln -c Release
dotnet pack src/CodeIndex/CodeIndex.csproj -c Release
```

## Commit and PR

```bash
git add CHANGELOG.md version.json changelog.d/unreleased tools/CodeIndex.Changelog .codex/workflows changelog.d/README.md
git commit -m "Prepare release v1.17.0"
git push -u origin release/v1.17.0
```

Create a PR titled:

```text
Prepare release v1.17.0
```

## If main moves before merge

Before merging the release PR, make sure the release branch is based on latest
`origin/main`.

```bash
git fetch origin
git switch release/v1.17.0
git merge origin/main

dotnet run --project tools/CodeIndex.Changelog -- prepare --version 1.17.0 --date 2026-05-01
```

If new fragments arrived from main, the command must add them to the existing
release section and remove those new fragments. Commit the refresh if needed.

## After the release PR merges

```bash
git switch main
git pull origin main
git tag -a v1.17.0 -m "Release v1.17.0"
git push origin v1.17.0
```

### Verify the tag points to the merged release commit

After pushing the tag, confirm it resolves to the same commit as `origin/main`.
This catches the race where a maintainer pushed an unrelated commit to `main`
between `git pull` and `git tag`, which would tag the wrong commit and leave a
release whose `version.json`, `CHANGELOG.md`, and binary identity disagree.

```bash
git fetch origin --tags
[ "$(git rev-parse v1.17.0)" = "$(git rev-parse origin/main)" ] \
  && echo "tag OK" \
  || { echo "tag does not match origin/main; do not announce the release" >&2; exit 1; }
```

If the rev-parse values differ, do not announce the release. Delete the tag
locally and remotely, re-pull `main`, and re-tag from the correct commit:

```bash
git tag -d v1.17.0
git push origin :refs/tags/v1.17.0
git pull origin main
git tag -a v1.17.0 -m "Release v1.17.0"
git push origin v1.17.0
```

Then re-run the verification above before continuing.

### Verify `version.json` matches the tag

The release CI's install-verification step asserts that `cdidx --version` equals
the tag name, but the tag/`version.json` consistency is also worth checking at
the source-tree level before any artifact is built. Run this immediately after
the tag is pushed:

```bash
git show "v1.17.0:version.json" | grep -q '"version": "1.17.0"' \
  && echo "version.json OK" \
  || { echo "version.json does not match tag v1.17.0" >&2; exit 1; }
```

If the check fails, the release PR most likely landed without the
`version.json` bump from `tools/CodeIndex.Changelog prepare`. Delete the tag
(local + remote, as above), open a follow-up PR that fixes `version.json` on
`main`, and re-tag once it merges. Do not paper over the mismatch by editing
the tag in place.

### Partial-failure recovery

The `Release` workflow has three post-tag jobs: the `release` build/publish
matrix and two jobs that depend on it (`create-release`, `publish-nuget`).
Failures usually only affect one of them.

- **One `release` matrix lane (Linux/Windows/macOS publish) failed.**
  Re-run the failed lane from the GitHub Actions UI. When `create-release`
  runs afterward, its `if gh release view … then gh release upload --clobber
  else gh release create` block uploads any missing artifacts into the
  existing release without manual cleanup.
- **`create-release` failed after some assets uploaded (partial release).**
  Re-run `create-release` from the GitHub Actions UI. The job is idempotent:
  it detects the existing release and runs `gh release upload --clobber` for
  the missing files, then re-runs the CDN-propagation poll and `install.sh`
  verification.
- **`publish-nuget` failed but the GitHub release succeeded.**
  Re-run `publish-nuget` from the GitHub Actions UI. `dotnet nuget push` uses
  `--skip-duplicate`, so already-pushed packages are not republished. Do not
  delete and re-tag for a NuGet-only failure — the GitHub release is
  already public and tag deletion would invalidate it for downstream installs.
- **Install-verification step failed (e.g. `cdidx --version` mismatch, missing
  asset).** This means the published release is *not* usable. Investigate
  before announcing. If the cause is a transient CDN/network blip, re-run the
  job. If the cause is a real artifact problem, delete the GitHub release
  (`gh release delete v1.17.0 --cleanup-tag --yes`) and start over from the
  tag-creation step above.
- **`CHANGELOG.md` mismatch discovered after the tag was pushed (e.g. a
  fragment was missed, or the English/日本語 sections diverged).** Do **not**
  delete and re-tag a public release just to fix the changelog. Land a
  follow-up `docs:` PR against `main` and roll the correction into the next
  release section. Note the correction in that release's notes so the audit
  trail stays honest.
