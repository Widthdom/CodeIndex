# Container Support Plan — cdidx を dotnet なし環境で使えるようにする

## Problem / 課題

AI コーディングツール（Claude Code, Cursor, Windsurf 等）のコンテナ環境には .NET ランタイムが入っていない。
`rg` (ripgrep) は単一バイナリのため多くのコンテナに標準搭載されているが、cdidx は `dotnet` が必要なため使えない。

これが cdidx の採用障壁になっている。

## Goal / 目標

**dotnet がインストールされていないコンテナ環境でも、cdidx を簡単にインストール・実行できるようにする。**

## Current State / 現状

- `release.yml` で self-contained single-file バイナリを既にビルドしている（linux-x64, win-x64, osx-arm64）
- GitHub Releases にアーカイブ（.tar.gz / .zip）+ sha256sums.txt を公開している
- NuGet グローバルツールとしても公開している（ただしこれは dotnet SDK が必要）

## Tasks / タスク

### 1. ワンライナーインストールスクリプト

`rg` や `fzf` のように、curl 一発でインストールできるスクリプトを用意する。

**ファイル:** `install.sh`（リポジトリルートに配置）

```bash
# 想定される使い方:
curl -fsSL https://raw.githubusercontent.com/widthdom/codeindex/main/install.sh | bash

# または特定バージョン:
curl -fsSL https://raw.githubusercontent.com/widthdom/codeindex/main/install.sh | bash -s -- v1.3.0
```

**スクリプトの要件:**
- OS/アーキテクチャを自動検出（`uname -s`, `uname -m`）
- 対応: linux-x64, osx-arm64（最低限）。linux-arm64 は将来追加
- バージョン未指定時は GitHub API で最新リリースを取得
- GitHub Releases から該当アーカイブをダウンロード → 展開 → `~/.local/bin/` 等に配置
- sha256 チェックサム検証（sha256sum or shasum -a 256）
- PATH への追加が必要な場合はガイダンスを表示
- 既にインストール済みの場合はバージョンを確認して上書き/スキップ判断
- エラーハンドリング: ネットワーク失敗、権限不足、未対応プラットフォーム

**参考にすべきスクリプト:**
- https://github.com/BurntSushi/ripgrep の install 系ドキュメント
- https://github.com/junegunn/fzf の install スクリプト
- Rust の rustup installer パターン

### 2. release.yml に linux-arm64 を追加

現在 linux-x64 のみだが、ARM64 コンテナ（Apple Silicon の Docker 等）も増えているため追加。

**変更箇所:** `.github/workflows/release.yml`

```yaml
matrix:
  include:
    - os: ubuntu-latest
      rid: linux-x64
    - os: ubuntu-latest        # ← 追加
      rid: linux-arm64         # ← 追加（クロスコンパイル）
    - os: windows-latest
      rid: win-x64
    - os: macos-latest
      rid: osx-arm64
```

**注意:** linux-arm64 は ubuntu-latest (x64) 上でのクロスコンパイルになる。
`dotnet publish -r linux-arm64 --self-contained` は .NET のクロスコンパイルで対応可能。
テスト実行はスキップするか、QEMU ベースのテストを別途検討。

### 3. Dockerfile（オプショナル）

AI ツールのカスタムコンテナイメージ用に、cdidx を追加するための Dockerfile snippet を用意。

**ファイル:** `Dockerfile.snippet`（またはドキュメント内に記載）

```dockerfile
# cdidx を追加する例
RUN curl -fsSL https://raw.githubusercontent.com/widthdom/codeindex/main/install.sh | bash
```

### 4. MCP 設定テンプレート

AI ツールが cdidx の MCP サーバーに接続するための設定例を提供。

**README に追記する内容:**

```json
{
  "mcpServers": {
    "cdidx": {
      "command": "cdidx",
      "args": ["mcp"]
    }
  }
}
```

### 5. README 更新

- Installation セクションに「Container / CI 環境」サブセクションを追加
- ワンライナーインストールの使い方を記載
- `rg` との比較（cdidx が提供する追加価値: シンボル検索、参照グラフ、MCP 等）

### 6. ドキュメント更新

CLAUDE.md の per-commit checklist に従い:
- CHANGELOG.md — Added エントリ
- README.md — 英語・日本語の Installation セクション
- DEVELOPER_GUIDE.md — 配布方法のセクション（必要に応じて）

## Priority Order / 優先順

1. **install.sh** — これが最もインパクトが大きい。コンテナで `curl | bash` できればほぼ解決
2. **linux-arm64 追加** — Apple Silicon Docker ユーザー向け
3. **README 更新** — インストール方法のドキュメント
4. **MCP 設定テンプレート** — AI ツール連携を簡単に
5. **Dockerfile snippet** — カスタムイメージ構築者向け

## Out of Scope / スコープ外

- Alpine Linux (musl) 対応 — 需要が出てから検討
- Windows コンテナ対応 — ほぼ使われていない
- Homebrew / apt リポジトリ — 将来タスクとして別途検討
- 自動更新機能 — まずは手動インストールで十分

## Acceptance Criteria / 完了条件

- [ ] `install.sh` が linux-x64 環境で動作し、cdidx がインストールされる
- [ ] `install.sh` が osx-arm64 環境で動作する
- [ ] `release.yml` に linux-arm64 が追加され、リリースにバイナリが含まれる
- [ ] README にコンテナ/CI 向けのインストール手順が記載されている
- [ ] dotnet 未インストールの Docker コンテナで cdidx が実行できることを確認
