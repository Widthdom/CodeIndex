namespace CodeIndex.Indexer;

public partial class FileIndexer
{
    // Exact file names (case-insensitive) mapped to language / 完全一致ファイル名→言語マッピング
    private static readonly Dictionary<string, string> FileNameMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Dockerfile"] = "dockerfile",
        [".dockerfile"] = "dockerfile",
        ["Containerfile"] = "dockerfile",   // Podman's Dockerfile alternative / Podman の Dockerfile 代替
        [".containerfile"] = "dockerfile",
        ["Makefile"] = "makefile",
        ["GNUmakefile"] = "makefile",     // GNU Make explicit filename / GNU Make 明示ファイル名
        ["Justfile"] = "justfile",     // Just command runner / Just コマンドランナー
        ["CMakeLists.txt"] = "cmake",
        ["Vagrantfile"] = "ruby",         // Vagrant uses Ruby DSL / Vagrant は Ruby DSL
        ["Gemfile"] = "dependency_manifest", // Bundler dependency manifest / Bundler 依存マニフェスト
        ["Rakefile"] = "ruby",         // Rake task runner / Rake タスクランナー
        ["Podfile"] = "dependency_manifest", // CocoaPods dependency manifest / CocoaPods 依存マニフェスト
        ["Guardfile"] = "ruby",         // Guard file-watcher / Guard ファイルウォッチャー
        ["Capfile"] = "ruby",         // Capistrano deployment / Capistrano デプロイ
        ["NAMESPACE"] = "r",            // R package namespace directives / R パッケージ namespace ディレクティブ
        [".Rprofile"] = "r",            // R startup profile / R 起動プロファイル
        ["Rprofile.site"] = "r",            // Site-wide R startup profile / サイト共通 R 起動プロファイル
        ["BUILD"] = "python",       // Bazel Starlark build file / Bazel Starlark ビルドファイル
        ["BUILD.bazel"] = "python",
        ["WORKSPACE"] = "python",       // Bazel workspace / Bazel ワークスペース
        ["WORKSPACE.bazel"] = "python",
        ["package.json"] = "dependency_manifest", // npm package manifest / npm パッケージマニフェスト
        ["pyproject.toml"] = "dependency_manifest", // Python project manifest / Python プロジェクトマニフェスト
        ["requirements.txt"] = "dependency_manifest", // Python dependencies manifest / Python 依存関係マニフェスト
        ["Pipfile"] = "dependency_manifest", // Pipenv manifest / Pipenv マニフェスト
        ["poetry.toml"] = "dependency_manifest", // Poetry configuration manifest / Poetry 設定マニフェスト
        ["Cargo.toml"] = "dependency_manifest", // Cargo package manifest / Cargo パッケージマニフェスト
        ["composer.json"] = "dependency_manifest", // Composer package manifest / Composer パッケージマニフェスト
        ["go.mod"] = "dependency_manifest", // Go module manifest / Go モジュールマニフェスト
        ["go.work"] = "dependency_manifest", // Go workspace manifest / Go ワークスペースマニフェスト
        ["packages.config"] = "dependency_manifest", // NuGet packages.config manifest / NuGet packages.config マニフェスト
        ["Directory.Packages.props"] = "dependency_manifest", // NuGet central package manifest / NuGet central package マニフェスト
        ["package-lock.json"] = "dependency_lock", // npm lockfile / npm lockfile
        ["npm-shrinkwrap.json"] = "dependency_lock", // npm shrinkwrap lockfile / npm shrinkwrap lockfile
        ["yarn.lock"] = "dependency_lock", // Yarn lockfile / Yarn lockfile
        ["pnpm-lock.yaml"] = "dependency_lock", // pnpm lockfile / pnpm lockfile
        ["bun.lock"] = "dependency_lock", // Bun text lockfile / Bun text lockfile
        ["bun.lockb"] = "dependency_lock", // Bun binary lockfile / Bun binary lockfile
        ["Gemfile.lock"] = "dependency_lock", // Bundler lockfile / Bundler lockfile
        ["Cargo.lock"] = "dependency_lock", // Cargo lockfile / Cargo lockfile
        ["composer.lock"] = "dependency_lock", // Composer lockfile / Composer lockfile
        ["poetry.lock"] = "dependency_lock", // Poetry lockfile / Poetry lockfile
        ["Pipfile.lock"] = "dependency_lock", // Pipenv lockfile / Pipenv lockfile
        ["go.sum"] = "dependency_lock", // Go module checksum lockfile / Go module checksum lockfile
        ["uv.lock"] = "dependency_lock", // uv lockfile / uv lockfile
        ["packages.lock.json"] = "dependency_lock", // NuGet lockfile / NuGet lockfile
        [".editorconfig"] = "editorconfig",
        [".gitignore"] = "gitignore",
        [".gitattributes"] = "gitattributes",
        [".dockerignore"] = "dockerignore",
    };

    // Filename prefixes (with trailing dot) mapped to language for suffixed variants like
    // Dockerfile.dev / Makefile.common / GNUmakefile.am. The suffix must be non-empty.
    // Dockerfile.dev / Makefile.common / GNUmakefile.am のようにサフィックス付きで使われる
    // ファイル名のプレフィックス→言語マッピング。サフィックスは1文字以上必須。
    private static readonly (string Prefix, string Language)[] FileNamePrefixMap =
    [
        ("Dockerfile.",  "dockerfile"),
        ("Dockerfile-",  "dockerfile"),
        ("Dockerfile_",  "dockerfile"),
        ("Containerfile.", "dockerfile"),
        ("Containerfile-", "dockerfile"),
        ("Containerfile_", "dockerfile"),
        ("Makefile.",    "makefile"),
        ("GNUmakefile.", "makefile"),
    ];
}
