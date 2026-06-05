using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CodeIndex.Database;
using CodeIndex.Cli;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

/// <summary>
/// Tests for FileIndexer.
/// FileIndexerのテスト。
/// </summary>
[Collection("SQLite pool sensitive")]
public class FileIndexerTests
{
    [Fact]
    public void NormalizeIgnorePath_PosixPreservesLiteralBackslash()
    {
        var normalized = FileIndexer.NormalizeIgnorePath(@"weird\name.py/");

        if (OperatingSystem.IsWindows())
            Assert.Equal("weird/name.py", normalized);
        else
            Assert.Equal(@"weird\name.py", normalized);
    }

    [Fact]
    public void ScanFilesDetailed_CancelledToken_ThrowsBeforeEnumeration()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cdidx-cancel-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            Assert.Throws<OperationCanceledException>(() =>
                new FileIndexer(tempDir).ScanFilesDetailed(cancellationToken: cancellation.Token));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void ScanFilesDetailed_CaseInsensitiveChildDirectory_SkipsCaseOnlyDuplicatePathWithWarning()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cdidx-case-dedupe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var childDir = Path.Combine(tempDir, "LinkedVolume");
            Directory.CreateDirectory(childDir);
            var sourceFile = Path.Combine(childDir, "File.cs");
            File.WriteAllText(sourceFile, "class CaseDuplicateFixture { }\n");
            var duplicateCasePath = Path.Combine(childDir, "file.cs");

            var indexer = new FileIndexer(
                tempDir,
                ignoreCase: false,
                ignoreRuleRoot: null,
                maxFileSizeBytes: null,
                directoryIgnoreCaseProbe: dir => Path.GetFullPath(dir) == Path.GetFullPath(childDir),
                enumerateFiles: dir => Path.GetFullPath(dir) == Path.GetFullPath(childDir)
                    ? [sourceFile, duplicateCasePath]
                    : Directory.EnumerateFiles(dir));

            var result = indexer.ScanFilesDetailed();

            var file = Assert.Single(result.Files);
            Assert.Equal(sourceFile, file);
            Assert.Contains("LinkedVolume/file.cs", result.NonIndexablePaths);
            Assert.Contains(
                result.Errors,
                error => error.Severity == FileIndexer.ScanIssueSeverity.Warning
                    && error.Message.Contains("case-sensitivity differs", StringComparison.OrdinalIgnoreCase)
                    && error.Path == "LinkedVolume");
            Assert.Contains(
                result.Errors,
                error => error.Severity == FileIndexer.ScanIssueSeverity.Warning
                    && error.Message.Contains("differs only by case", StringComparison.OrdinalIgnoreCase)
                    && error.Path == "LinkedVolume/file.cs");
            Assert.False(result.HadErrors);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_SkipsBuiltInDirectoriesWithCaseInsensitiveNames()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cdidx-skipdir-case-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(tempDir, "Node_Modules"));
            File.WriteAllText(Path.Combine(tempDir, "Node_Modules", "ignored.js"), "export const ignored = true;");
            File.WriteAllText(Path.Combine(tempDir, "app.js"), "export const app = true;");

            var indexer = new FileIndexer(tempDir, ignoreCase: true);
            var files = indexer.ScanFiles()
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(["app.js"], files);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_PerDirectoryCdidxIgnore_AppliesChildRulesWithoutLeakingToSiblings()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cdidx-per-dir-ignore-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(tempDir, "left"));
            Directory.CreateDirectory(Path.Combine(tempDir, "right"));
            File.WriteAllText(Path.Combine(tempDir, ".cdidxignore"), "*.generated.py\n");
            File.WriteAllText(Path.Combine(tempDir, "root.generated.py"), "print('ignored root')\n");
            File.WriteAllText(Path.Combine(tempDir, "left", ".cdidxignore"), "!keep.generated.py\nlocal.py\n");
            File.WriteAllText(Path.Combine(tempDir, "left", "keep.generated.py"), "print('kept child')\n");
            File.WriteAllText(Path.Combine(tempDir, "left", "local.py"), "print('ignored child')\n");
            File.WriteAllText(Path.Combine(tempDir, "right", "keep.generated.py"), "print('ignored sibling')\n");
            File.WriteAllText(Path.Combine(tempDir, "right", "plain.py"), "print('kept sibling')\n");

            var files = new FileIndexer(tempDir)
                .ScanFiles()
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(["left/keep.generated.py", "right/plain.py"], files);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFilesDetailed_OversizedGitignoreFailsClosedWithError()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cdidx-oversize-gitignore-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(Path.Combine(tempDir, ".gitignore"), "generated.py\n" + new string('x', 300 * 1024));
            File.WriteAllText(Path.Combine(tempDir, "generated.py"), "print('generated')\n");

            var result = new FileIndexer(tempDir).ScanFilesDetailed();
            var files = result.Files
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .ToList();

            Assert.Empty(files);
            Assert.Contains(
                result.Errors,
                error => error.Path == ".gitignore"
                    && error.Severity == FileIndexer.ScanIssueSeverity.Error
                    && error.Message.Contains("exceeds", StringComparison.OrdinalIgnoreCase));
            Assert.True(result.HadErrors);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFilesDetailed_GitignoreRuleCountCapFailsClosedWithError()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cdidx-gitignore-rule-cap-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var rules = Enumerable.Range(0, 4096)
                .Select(i => $"unused{i}.py")
                .Concat(["late.py"]);
            File.WriteAllText(Path.Combine(tempDir, ".gitignore"), string.Join('\n', rules) + "\n");
            File.WriteAllText(Path.Combine(tempDir, "late.py"), "print('late')\n");

            var result = new FileIndexer(tempDir).ScanFilesDetailed();
            var files = result.Files
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .ToList();

            Assert.Empty(files);
            Assert.Contains(
                result.Errors,
                error => error.Path == ".gitignore:4097"
                    && error.Severity == FileIndexer.ScanIssueSeverity.Error
                    && error.Message.Contains("4096 rules", StringComparison.OrdinalIgnoreCase));
            Assert.True(result.HadErrors);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFilesDetailed_HardlinkedFiles_SkipsDuplicatePathWithWarning()
    {
        if (OperatingSystem.IsWindows())
            return;

        var tempDir = Path.Combine(Path.GetTempPath(), $"cdidx-hardlink-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var original = Path.Combine(tempDir, "original.cs");
            var duplicate = Path.Combine(tempDir, "duplicate.cs");
            File.WriteAllText(original, "class HardlinkFixture { }\n");
            CreateHardLink(original, duplicate);

            var result = new FileIndexer(tempDir).ScanFilesDetailed();

            var files = result.Files.Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal).ToList();
            Assert.Single(files);
            Assert.Contains(files[0], new[] { "duplicate.cs", "original.cs" });
            Assert.Contains(result.NonIndexablePaths, path => path is "duplicate.cs" or "original.cs");
            var warning = Assert.Single(
                result.Errors,
                error => error.Severity == FileIndexer.ScanIssueSeverity.Warning
                    && error.Message.Contains("hardlinked", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(warning.Path, new[] { "duplicate.cs", "original.cs" });
            Assert.False(result.HadErrors);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Theory]
    [InlineData("test.py", "python")]
    [InlineData("app.js", "javascript")]
    [InlineData("app.cjs", "javascript")]
    [InlineData("app.mjs", "javascript")]
    [InlineData("main.ts", "typescript")]
    [InlineData("main.cts", "typescript")]
    [InlineData("main.mts", "typescript")]
    [InlineData("types.d.cts", "typescript")]
    [InlineData("types.d.mts", "typescript")]
    [InlineData("lib.go", "go")]
    [InlineData("mod.rs", "rust")]
    [InlineData("App.java", "java")]
    [InlineData("Service.cs", "csharp")]
    [InlineData("Script.kts", "kotlin")]
    [InlineData("style.css", "css")]
    [InlineData("style.scss", "css")]
    [InlineData("page.vue", "vue")]
    [InlineData("page.svelte", "svelte")]
    [InlineData("main.tf", "terraform")]
    [InlineData("app.dart", "dart")]
    [InlineData("Main.scala", "scala")]
    [InlineData("analysis.r", "r")]
    [InlineData("analysis.R", "r")]
    [InlineData("web.ex", "elixir")]
    [InlineData("test.exs", "elixir")]
    [InlineData("script.lua", "lua")]
    [InlineData("Program.fs", "fsharp")]
    [InlineData("Script.fsx", "fsharp")]
    [InlineData("Module.bas", "vb")]
    [InlineData("Customer.cls", "vb")]
    [InlineData("UserControl.ctl", "vb")]
    [InlineData("Document.dob", "vb")]
    [InlineData("DataReport.dsr", "vb")]
    [InlineData("Form1.frm", "vb")]
    [InlineData("SettingsPage.pag", "vb")]
    [InlineData("Macro.vba", "vb")]
    [InlineData("Module1.vb", "vb")]
    [InlineData("Index.vbhtml", "vb")]
    [InlineData("script.vbs", "vb")]
    [InlineData("index.html", "html")]
    [InlineData("legacy.htm", "html")]
    [InlineData("doc.xhtml", "html")]
    [InlineData("page.shtml", "html")]
    [InlineData("Index.cshtml", "csharp")]
    [InlineData("Counter.razor", "csharp")]
    [InlineData("MainWindow.xaml", "xml")]
    [InlineData("App.axaml", "xml")]
    [InlineData("Point.st", "smalltalk")]
    [InlineData("Point.smalltalk", "smalltalk")]
    [InlineData("MyApp.csproj", "msbuild")]
    [InlineData("MyApp.fsproj", "msbuild")]
    [InlineData("MyApp.vbproj", "msbuild")]
    [InlineData("Directory.Build.props", "msbuild")]
    [InlineData("Directory.Build.targets", "msbuild")]
    [InlineData("Main.hs", "haskell")]
    [InlineData("main.zig", "zig")]
    [InlineData("schema.proto", "protobuf")]
    [InlineData("schema.graphql", "graphql")]
    [InlineData("build.gradle", "gradle")]
    [InlineData("build.cmake", "cmake")]
    [InlineData("script.ps1", "powershell")]
    [InlineData("run.bat", "batch")]
    [InlineData("run.cmd", "batch")]
    [InlineData("script.bash", "shell")]
    [InlineData("script.zsh", "shell")]
    [InlineData("script.fish", "shell")]
    [InlineData("Dockerfile", "dockerfile")]
    [InlineData(".dockerfile", "dockerfile")]
    [InlineData("api.Dockerfile", "dockerfile")]
    [InlineData("api.Containerfile", "dockerfile")]
    [InlineData(".containerfile", "dockerfile")]
    [InlineData("Dockerfile-prod", "dockerfile")]
    [InlineData("Dockerfile_prod", "dockerfile")]
    [InlineData("Containerfile-prod", "dockerfile")]
    [InlineData("Containerfile_prod", "dockerfile")]
    [InlineData("Makefile", "makefile")]
    [InlineData("Justfile", "justfile")]
    [InlineData("CMakeLists.txt", "cmake")]
    [InlineData("Vagrantfile", "ruby")]
    // Issue #189: additional filename maps / 追加ファイル名マッピング
    [InlineData("Gemfile", "ruby")]
    [InlineData("Rakefile", "ruby")]
    [InlineData("Podfile", "ruby")]
    [InlineData("Guardfile", "ruby")]
    [InlineData("Capfile", "ruby")]
    [InlineData("NAMESPACE", "r")]
    [InlineData(".Rprofile", "r")]
    [InlineData("Rprofile.site", "r")]
    [InlineData("GNUmakefile", "makefile")]
    [InlineData("Containerfile", "dockerfile")]
    [InlineData("BUILD", "python")]
    [InlineData("BUILD.bazel", "python")]
    [InlineData("WORKSPACE", "python")]
    [InlineData("WORKSPACE.bazel", "python")]
    [InlineData("pyproject.toml", "python")]
    [InlineData("requirements.txt", "python")]
    [InlineData("go.mod", "go")]
    [InlineData("go.work", "go")]
    // Issue #189: additional extensions / 追加拡張子
    [InlineData("types.pyi", "python")]
    [InlineData("windowed.pyw", "python")]
    [InlineData("module.pyx", "cython")]
    [InlineData("module.pxd", "cython")]
    [InlineData("tasks.rake", "ruby")]
    [InlineData("mygem.gemspec", "ruby")]
    [InlineData("MyPod.podspec", "ruby")]
    [InlineData("build.groovy", "groovy")]
    [InlineData("build.gvy", "groovy")]
    [InlineData("build.gy", "groovy")]
    [InlineData("build.gsh", "groovy")]
    [InlineData("common.mk", "makefile")]
    [InlineData("page.htm", "html")]
    [InlineData("style.less", "css")]
    [InlineData("style.sass", "sass")]
    [InlineData("style.styl", "stylus")]
    [InlineData("style.pcss", "css")]
    [InlineData("schema.pgsql", "sql")]
    [InlineData("proc.tsql", "sql")]
    [InlineData("pkg.plsql", "sql")]
    [InlineData("orders_pkg.pls", "sql")]
    [InlineData("orders_pkg.pks", "sql")]
    [InlineData("orders_pkg.pkb", "sql")]
    [InlineData("orders_pkg.plb", "sql")]
    [InlineData("migrate.psql", "sql")]
    // Issue #189: filename prefix matching for Dockerfile.* / Makefile.* / GNUmakefile.*
    [InlineData("Dockerfile.dev", "dockerfile")]
    [InlineData("Dockerfile.prod", "dockerfile")]
    [InlineData("Dockerfile.test", "dockerfile")]
    [InlineData("Containerfile.dev", "dockerfile")]
    [InlineData("Makefile.am", "makefile")]
    [InlineData("Makefile.in", "makefile")]
    [InlineData("Makefile.common", "makefile")]
    [InlineData("GNUmakefile.am", "makefile")]
    [InlineData("kernel.cu", "cuda")]
    [InlineData("kernel.cuh", "cuda")]
    [InlineData("header.hh", "cpp")]
    [InlineData("shader.glsl", "glsl")]
    [InlineData("shader.vert", "glsl")]
    [InlineData("shader.frag", "glsl")]
    [InlineData("shader.hlsl", "hlsl")]
    [InlineData("shader.wgsl", "wgsl")]
    [InlineData("shader.metal", "metal")]
    [InlineData("cpu.s", "assembly")]
    [InlineData("cpu.S", "assembly")]
    [InlineData("cpu.asm", "assembly")]
    [InlineData("cpu.nasm", "assembly")]
    [InlineData("cpu.v", "verilog")]
    [InlineData("cpu.sv", "systemverilog")]
    [InlineData("cpu.svh", "systemverilog")]
    [InlineData("cpu.vhd", "vhdl")]
    [InlineData("cpu.vhdl", "vhdl")]
    [InlineData("demo.lisp", "commonlisp")]
    [InlineData("demo.lsp", "commonlisp")]
    [InlineData("demo.cl", "commonlisp")]
    [InlineData("demo.rkt", "racket")]
    [InlineData("demo.pas", "pascal")]
    [InlineData("demo.pp", "pascal")]
    [InlineData("demo.dpr", "pascal")]
    [InlineData("demo.ada", "ada")]
    [InlineData("demo.adb", "ada")]
    [InlineData("demo.ads", "ada")]
    [InlineData("demo.f", "fortran")]
    [InlineData("demo.f77", "fortran")]
    [InlineData("demo.f90", "fortran")]
    [InlineData("demo.f95", "fortran")]
    [InlineData("demo.f03", "fortran")]
    [InlineData("demo.f08", "fortran")]
    [InlineData("demo.for", "fortran")]
    [InlineData("demo.ftn", "fortran")]
    [InlineData("demo.cbl", "cobol")]
    [InlineData("demo.cob", "cobol")]
    [InlineData("demo.cobol", "cobol")]
    [InlineData("demo.cpy", "cobol")]
    [InlineData("demo.raku", "raku")]
    [InlineData("demo.rakumod", "raku")]
    [InlineData("demo.rakutest", "raku")]
    [InlineData("test.t", "perl")]
    [InlineData("app.psgi", "perl")]
    [InlineData("index.cgi", "perl")]
    [InlineData("index.fcgi", "perl")]
    public void DetectLanguage_KnownExtensions_ReturnsCorrectLang(string filename, string expected)
    {
        Assert.Equal(expected, FileIndexer.DetectLanguage(filename));
    }

    [Fact]
    public void DetectLanguage_WorkspaceLangMapYaml_AliasesExtension()
    {
        lock (TestConsoleLock.Gate)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"cdidx_langmap_{Guid.NewGuid():N}");
            var originalDirectory = Environment.CurrentDirectory;
            try
            {
                Directory.CreateDirectory(tempDir);
                File.WriteAllText(
                    Path.Combine(tempDir, LanguageMapOverrides.WorkspaceFileName),
                    "entries:\n  - extension: \".kts.in\"\n    language: \"kotlin\"\n");
                var outsideDir = Path.Combine(tempDir, "outside");
                Directory.CreateDirectory(outsideDir);
                Environment.CurrentDirectory = outsideDir;

                Assert.Equal("kotlin", FileIndexer.DetectLanguage(Path.Combine(tempDir, "build.kts.in")));
                Assert.Equal("kotlin", FileIndexer.GetLanguageExtensions()[".kts.in"]);
            }
            finally
            {
                Environment.CurrentDirectory = originalDirectory;
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void LanguageMapOverrides_OversizedFileSkipsOverridesWithWarning()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cdidx_langmap_caps_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var oversizedPath = Path.Combine(tempDir, "large-langmap.yaml");
            var fallbackPath = Path.Combine(tempDir, "fallback-langmap.yaml");
            File.WriteAllText(oversizedPath, "entries:\n" + new string('a', 132 * 1024));
            File.WriteAllText(fallbackPath, "entries:\n- extension: ok\n  language: ruby\n");

            var warnings = new List<string>();
            var map = LanguageMapOverrides.LoadEffectiveMapFromPathsForTesting(
                new[] { oversizedPath, fallbackPath },
                warnings.Add);

            Assert.False(map.ContainsKey(".skip"));
            Assert.Equal("ruby", map[".ok"]);
            Assert.Contains(warnings, warning => warning.Contains("exceeds", StringComparison.Ordinal));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void LanguageMapOverrides_TooManyLinesSkipsOverridesWithWarning()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cdidx_langmap_lines_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var tooManyLinesPath = Path.Combine(tempDir, "many-lines-langmap.yaml");
            var fallbackPath = Path.Combine(tempDir, "fallback-langmap.yaml");
            File.WriteAllText(tooManyLinesPath, string.Concat(Enumerable.Repeat("#\n", 16385)));
            File.WriteAllText(fallbackPath, "entries:\n- extension: ok\n  language: ruby\n");

            var warnings = new List<string>();
            var map = LanguageMapOverrides.LoadEffectiveMapFromPathsForTesting(
                new[] { tooManyLinesPath, fallbackPath },
                warnings.Add);

            Assert.Equal("ruby", map[".ok"]);
            Assert.Contains(warnings, warning => warning.Contains("lines", StringComparison.Ordinal));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void LanguageMapOverrides_EntryCountCapTruncatesRemainingOverridesWithWarning()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cdidx_langmap_entries_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var configPath = Path.Combine(tempDir, "langmap.yaml");
            var builder = new StringBuilder("entries:\n");
            for (var i = 0; i <= 4096; i++)
                builder.Append("- extension:x").Append(i).Append('\n').Append("language:l\n");
            File.WriteAllText(configPath, builder.ToString());

            var warnings = new List<string>();
            var map = LanguageMapOverrides.LoadEffectiveMapFromPathsForTesting(
                new[] { configPath },
                warnings.Add);

            Assert.Equal("l", map[".x0"]);
            Assert.Equal("l", map[".x4095"]);
            Assert.False(map.ContainsKey(".x4096"));
            Assert.Contains(warnings, warning => warning.Contains("4096", StringComparison.Ordinal));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void LanguageMapOverrides_EntryCountCapIsPerFileSoWorkspaceOverridesStillLoad()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cdidx_langmap_per_file_entries_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var userConfigPath = Path.Combine(tempDir, "user-langmap.yaml");
            var workspaceConfigPath = Path.Combine(tempDir, LanguageMapOverrides.WorkspaceFileName);
            var builder = new StringBuilder("entries:\n");
            for (var i = 0; i <= 4096; i++)
                builder.Append("- extension:x").Append(i).Append('\n').Append("language:u\n");
            File.WriteAllText(userConfigPath, builder.ToString());
            File.WriteAllText(
                workspaceConfigPath,
                "entries:\n- extension:x0\n  language:workspace\n- extension:workspace\n  language:ruby\n");

            var warnings = new List<string>();
            var map = LanguageMapOverrides.LoadEffectiveMapFromPathsForTesting(
                new[] { userConfigPath, workspaceConfigPath },
                warnings.Add);

            Assert.Equal("workspace", map[".x0"]);
            Assert.Equal("ruby", map[".workspace"]);
            Assert.False(map.ContainsKey(".x4096"));
            Assert.Contains(warnings, warning => warning.Contains("4096", StringComparison.Ordinal));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void LanguageMapOverrides_BomPrefixedFileLoadsOverrides()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cdidx_langmap_bom_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var configPath = Path.Combine(tempDir, LanguageMapOverrides.WorkspaceFileName);
            File.WriteAllText(configPath, "\uFEFFentries:\n- extension:bom\n  language:ruby\n");

            var map = LanguageMapOverrides.LoadEffectiveMapFromPathsForTesting(new[] { configPath });

            Assert.Equal("ruby", map[".bom"]);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(tempDir);
        }
    }

    [Theory]
    [InlineData("App.csproj")]
    [InlineData("Directory.Build.props")]
    [InlineData("Directory.Build.targets")]
    [InlineData("Library.fsproj")]
    [InlineData("Project.vbproj")]
    public void GetProjectMarkerFingerprint_RecognizesMsbuildProjectMarkers(string markerFileName)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cdidx_msbuild_marker_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, markerFileName), "<Project />");

            var indexer = new FileIndexer(tempDir);

            Assert.True(FileIndexer.SupportsHotspotFamilyMarkerLanguage("msbuild"));
            Assert.False(string.IsNullOrWhiteSpace(indexer.GetProjectMarkerFingerprint("msbuild")));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void GetProjectMarkerFingerprint_CancelledToken_ThrowsBeforeTraversal()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cdidx_msbuild_marker_cancel_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            var indexer = new FileIndexer(tempDir);

            Assert.Throws<OperationCanceledException>(() =>
                indexer.GetProjectMarkerFingerprint("msbuild", cancellation.Token));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void GetProjectMarkerFingerprint_DirectoryCapTruncatesTraversal()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cdidx_msbuild_marker_dir_cap_{Guid.NewGuid():N}");
        try
        {
            var nestedDir = Path.Combine(tempDir, "src", "App");
            Directory.CreateDirectory(nestedDir);
            File.WriteAllText(Path.Combine(nestedDir, "App.csproj"), "<Project />");

            var indexer = new FileIndexer(tempDir);

            var fullFingerprint = indexer.GetProjectMarkerFingerprint("msbuild");
            var cappedFingerprint = indexer.GetProjectMarkerFingerprintForTesting("msbuild", maxDirectories: 1, maxMarkerFiles: 100);

            Assert.False(string.IsNullOrWhiteSpace(fullFingerprint));
            Assert.False(string.IsNullOrWhiteSpace(cappedFingerprint));
            Assert.NotEqual(fullFingerprint, cappedFingerprint);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void GetProjectMarkerFingerprint_DirectoryCapReportsIncompleteTraversal()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cdidx_msbuild_marker_incomplete_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            for (var i = 0; i < 4; i++)
                Directory.CreateDirectory(Path.Combine(tempDir, $"project-{i}"));

            var indexer = new FileIndexer(tempDir);

            var result = indexer.GetProjectMarkerFingerprintResultForTesting("msbuild", maxDirectories: 1, maxMarkerFiles: 100);

            Assert.False(result.IsComplete);
            Assert.False(string.IsNullOrWhiteSpace(result.Fingerprint));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void GetProjectMarkerFingerprint_IgnoredGeneratedTreeDoesNotExhaustDirectoryCap()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cdidx_msbuild_marker_ignored_cap_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(Path.Combine(tempDir, ".gitignore"), "generated/\n");
            var generatedDir = Path.Combine(tempDir, "generated");
            Directory.CreateDirectory(generatedDir);
            for (var i = 0; i < 8; i++)
                Directory.CreateDirectory(Path.Combine(generatedDir, $"project-{i}"));

            var appDir = Path.Combine(tempDir, "src", "App");
            Directory.CreateDirectory(appDir);
            File.WriteAllText(Path.Combine(appDir, "App.csproj"), "<Project />");

            var indexer = new FileIndexer(tempDir);

            var result = indexer.GetProjectMarkerFingerprintResultForTesting("msbuild", maxDirectories: 4, maxMarkerFiles: 100);

            Assert.True(result.IsComplete);
            Assert.False(string.IsNullOrWhiteSpace(result.Fingerprint));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void GetProjectMarkerFingerprint_FileCapTruncatesMarkerCollection()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cdidx_msbuild_marker_file_cap_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "App.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(tempDir, "Lib.csproj"), "<Project />");

            var indexer = new FileIndexer(tempDir);

            var fullFingerprint = indexer.GetProjectMarkerFingerprint("msbuild");
            var cappedFingerprint = indexer.GetProjectMarkerFingerprintForTesting("msbuild", maxDirectories: 100, maxMarkerFiles: 1);

            Assert.False(string.IsNullOrWhiteSpace(fullFingerprint));
            Assert.False(string.IsNullOrWhiteSpace(cappedFingerprint));
            Assert.NotEqual(fullFingerprint, cappedFingerprint);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void GetFamilyScopeKey_MsbuildProjectFileIgnoresDirectoryBuildMarkersForScope()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            var srcDir = Path.Combine(tempDir, "src");
            Directory.CreateDirectory(srcDir);
            File.WriteAllText(Path.Combine(srcDir, "App.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(srcDir, "Directory.Build.props"), "<Project />");
            File.WriteAllText(Path.Combine(srcDir, "Directory.Build.targets"), "<Project />");

            var indexer = new FileIndexer(tempDir);

            Assert.Equal("src", indexer.GetFamilyScopeKey(Path.Combine(srcDir, "App.csproj"), "msbuild"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void BuildRecordWithRawBytes_OverExplicitMaxFileBytes_ThrowsActionableOverrideMessage()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var path = Path.Combine(tempDir, "Program.cs");
            File.WriteAllText(path, "class Program {}\n");

            var indexer = new FileIndexer(tempDir, ignoreCase: false, ignoreRuleRoot: null, maxFileSizeBytes: 4);

            var ex = Assert.Throws<FileIndexer.FileTooLargeSkippedException>(() => indexer.BuildRecordWithRawBytes(path));
            Assert.Contains("File too large", ex.Message);
            Assert.Contains("--max-file-bytes", ex.Message);
            Assert.Contains(FileIndexer.MaxFileSizeEnvironmentVariable, ex.Message);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void BuildRecordWithRawBytes_ExplicitMaxFileBytes_AllowsLargerSourceFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var path = Path.Combine(tempDir, "Program.cs");
            File.WriteAllText(path, "class Program {}\n");

            var indexer = new FileIndexer(tempDir, ignoreCase: false, ignoreRuleRoot: null, maxFileSizeBytes: 64);
            var (record, content, rawBytes, warning) = indexer.BuildRecordWithRawBytes(path);

            Assert.Equal("Program.cs", record.Path);
            Assert.Equal("csharp", record.Lang);
            Assert.Equal("class Program {}\n", content);
            Assert.Equal(content.Length, rawBytes.Length);
            Assert.Null(warning);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Theory]
    // Bare trailing-dot forms should not match prefix rules — suffix must be non-empty.
    // 末尾ドットだけの形はプレフィックス規則に一致しない（サフィックス必須）。
    [InlineData("Dockerfile.")]
    [InlineData("Containerfile.")]
    [InlineData("Makefile.")]
    [InlineData("GNUmakefile.")]
    public void DetectLanguage_BareTrailingDot_DoesNotMatchPrefix(string filename)
    {
        Assert.Null(FileIndexer.DetectLanguage(filename));
    }

    [Theory]
    [InlineData("image.png")]
    [InlineData("data.bin")]
    [InlineData("archive.zip")]
    public void DetectLanguage_UnknownExtensions_ReturnsNull(string filename)
    {
        Assert.Null(FileIndexer.DetectLanguage(filename));
    }

    [Theory]
    [InlineData("rbenv", "#!/usr/bin/env bash\nexit 0\n", "shell")]
    [InlineData("tool", "#!/bin/sh\necho hi\n", "shell")]
    [InlineData("worker", "#!/usr/bin/python3\nprint('hi')\n", "python")]
    [InlineData("bundle", "#!/usr/bin/env ruby\nputs 'hi'\n", "ruby")]
    [InlineData("cli", "#!/usr/bin/env node\nconsole.log('hi')\n", "javascript")]
    [InlineData("script", "#!/usr/bin/env pwsh\nWrite-Host hi\n", "powershell")]
    public void DetectLanguage_ExtensionlessShebangScripts_ReturnCorrectLang(string fileName, string content, string expected)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var path = Path.Combine(tempDir, fileName);
            File.WriteAllText(path, content);

            Assert.Equal(expected, FileIndexer.DetectLanguage(path));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Theory]
    [InlineData("elf", new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F', 0x02, 0x01, 0x01, 0x00 })]
    [InlineData("macho", new byte[] { 0xCF, 0xFA, 0xED, 0xFE, 0x07, 0x00, 0x00, 0x01 })]
    [InlineData("pe", new byte[] { (byte)'M', (byte)'Z', 0x90, 0x00, 0x03, 0x00, 0x00, 0x00 })]
    [InlineData("data", new byte[] { (byte)'#', (byte)'!', (byte)'/', (byte)'b', (byte)'i', (byte)'n', (byte)'/', (byte)'s', (byte)'h', 0x00 })]
    public void DetectLanguage_ExtensionlessBinaryLikeFiles_ReturnsNull(string fileName, byte[] bytes)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var path = Path.Combine(tempDir, fileName);
            File.WriteAllBytes(path, bytes);

            Assert.Null(FileIndexer.DetectLanguage(path));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void DetectLanguage_ExtensionlessOverCapShebangLine_ReturnsNull()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var path = Path.Combine(tempDir, "tool");
            File.WriteAllText(path, "#!/usr/bin/env " + new string('x', 256));

            Assert.Null(FileIndexer.DetectLanguage(path));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void DetectLanguage_ExtensionlessNonScript_ReturnsNull()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var path = Path.Combine(tempDir, "README");
            File.WriteAllText(path, "Hello world\n");

            Assert.Null(FileIndexer.DetectLanguage(path));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void DetectLanguage_UnknownExtensionWithShebang_ReturnsNull()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var path = Path.Combine(tempDir, "notes.txt");
            File.WriteAllText(path, "#!/usr/bin/env bash\necho hi\n");

            Assert.Null(FileIndexer.DetectLanguage(path));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void DetectLanguage_LeadingWhitespacePseudoShebang_ReturnsNull()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var path = Path.Combine(tempDir, "tool");
            File.WriteAllText(path, "  #!/usr/bin/env bash\necho hi\n");

            Assert.Null(FileIndexer.DetectLanguage(path));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_IndexesIssue189FilenameAndExtensionCoverage()
    {
        // Locks in the full Issue #189 repro: Ruby / Docker / Makefile / .pyi / .less / .mk /
        // .htm and Dockerfile.* / Makefile.* prefix variants are all indexed (not silently dropped).
        // Issue #189 のリプロを網羅。Ruby / Docker / Makefile / .pyi / .less / .mk / .htm と
        // Dockerfile.* / Makefile.* のプレフィックス変種が黙って落ちないことをロックする。
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var files = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Gemfile"] = "source 'https://rubygems.org'\ngem 'rails', '~> 7.0'\n",
                ["Rakefile"] = "task :default => [:test]\n",
                ["Containerfile"] = "FROM alpine\nRUN echo hi\n",
                ["Dockerfile.dev"] = "FROM alpine AS builder\nRUN echo dev\n",
                ["GNUmakefile"] = "all:\n\techo hi\n",
                ["common.mk"] = "OBJ = foo.o bar.o\n",
                ["stub.pyi"] = "def foo() -> int: ...\n",
                ["style.less"] = ".foo { color: red; }\n",
                ["page.htm"] = "<html><body>old-school</body></html>\n",
                ["Makefile.am"] = "SUBDIRS = lib\n",
            };
            foreach (var (name, content) in files)
                File.WriteAllText(Path.Combine(tempDir, name), content);

            var scanned = new FileIndexer(tempDir).ScanFiles()
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            var expected = files.Keys.OrderBy(n => n, StringComparer.Ordinal).ToList();
            Assert.Equal(expected, scanned);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_LoadsProjectRootPatternConfigsBeforeLanguageDetection_3190()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"cdidx-pattern-scan-project-{Guid.NewGuid():N}");
        var cwdRoot = Path.Combine(Path.GetTempPath(), $"cdidx-pattern-scan-cwd-{Guid.NewGuid():N}");
        lock (TestConsoleLock.Gate)
        {
            var originalDirectory = Environment.CurrentDirectory;
            try
            {
                ExtractorPluginRegistry.ResetForTests();
                Directory.CreateDirectory(projectRoot);
                Directory.CreateDirectory(cwdRoot);
                WriteFileIndexerPatternConfig(
                    projectRoot,
                    "project.yaml",
                    "language: \"projectdsl\"\nextensions:\n  - extension: \".projecttoy\"\npatterns:\n  - kind: \"class\"\n    regex: \"^project (?<name>\\\\w+)\"\n");
                WriteFileIndexerPatternConfig(
                    cwdRoot,
                    "cwd.yaml",
                    "language: \"cwddsl\"\nextensions:\n  - extension: \".cwdtoy\"\npatterns:\n  - kind: \"class\"\n    regex: \"^cwd (?<name>\\\\w+)\"\n");
                File.WriteAllText(Path.Combine(projectRoot, "Project.projecttoy"), "project Widget\n");
                File.WriteAllText(Path.Combine(projectRoot, "CwdLeak.cwdtoy"), "cwd Widget\n");
                Environment.CurrentDirectory = cwdRoot;

                var scanned = new FileIndexer(projectRoot).ScanFiles()
                    .Select(path => Path.GetRelativePath(projectRoot, path).Replace('\\', '/'))
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToList();

                Assert.Equal([".cdidx/patterns/project.yaml", "Project.projecttoy"], scanned);
            }
            finally
            {
                Environment.CurrentDirectory = originalDirectory;
                ExtractorPluginRegistry.ResetForTests();
                TestProjectHelper.DeleteDirectory(projectRoot);
                TestProjectHelper.DeleteDirectory(cwdRoot);
            }
        }
    }

    [Fact]
    public void ScanFiles_IndexesPythonProjectManifests()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(Path.Combine(tempDir, "pyproject.toml"), "[project]\nname = 'sample'\n");
            File.WriteAllText(Path.Combine(tempDir, "requirements.txt"), "pytest\n");
            File.WriteAllText(Path.Combine(tempDir, "unknown.txt"), "ignored\n");

            var files = new FileIndexer(tempDir).ScanFiles()
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(["pyproject.toml", "requirements.txt"], files);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GetLanguageExtensions_ExposesPrefixAndFileNameVariants()
    {
        // `cdidx languages` (and the MCP listing) should advertise everything TryDetectLanguage
        // actually recognizes, including exact-name Dockerfile / Makefile / Gemfile and the
        // Dockerfile.<suffix> / Makefile.<suffix> prefix variants added for Issue #189.
        // `cdidx languages`（および MCP の一覧）は TryDetectLanguage が実際に解釈するものを
        // 網羅すべき。Dockerfile / Makefile / Gemfile の完全一致に加え、Issue #189 で追加した
        // Dockerfile.<suffix> / Makefile.<suffix> などのプレフィックス変種も露出させる。
        var map = FileIndexer.GetLanguageExtensions();

        // Exact filenames surface with their language.
        // 完全一致ファイル名が言語付きで露出する。
        Assert.Equal("dockerfile", map["Dockerfile"]);
        Assert.Equal("dockerfile", map["Containerfile"]);
        Assert.Equal("makefile", map["Makefile"]);
        Assert.Equal("makefile", map["GNUmakefile"]);
        Assert.Equal("ruby", map["Gemfile"]);
        Assert.Equal("ruby", map["Rakefile"]);
        Assert.Equal("r", map["NAMESPACE"]);
        Assert.Equal("r", map[".Rprofile"]);
        Assert.Equal("r", map["Rprofile.site"]);
        Assert.Equal("python", map["BUILD.bazel"]);
        Assert.Equal("python", map["pyproject.toml"]);
        Assert.Equal("python", map["requirements.txt"]);
        Assert.Equal("assembly", map[".s"]);
        Assert.Equal("assembly", map[".S"]);

        // Prefix variants (Dockerfile.dev, Makefile.am, ...) surface as `<Prefix><suffix>` pseudo-entries.
        // プレフィックス変種は `<Prefix><suffix>` 形の擬似エントリとして露出する。
        Assert.Equal("dockerfile", map["Dockerfile.<suffix>"]);
        Assert.Equal("dockerfile", map["Containerfile.<suffix>"]);
        Assert.Equal("makefile", map["Makefile.<suffix>"]);
        Assert.Equal("makefile", map["GNUmakefile.<suffix>"]);

        // Sass / Stylus are distinct buckets now (indented syntax is incompatible with the CSS extractor).
        // Sass / Stylus は別バケット（インデント構文が CSS のシンボル抽出と非互換のため）。
        Assert.Equal("sass", map[".sass"]);
        Assert.Equal("stylus", map[".styl"]);
        Assert.Equal("css", map[".scss"]);
        Assert.Equal("css", map[".less"]);

        // Cython lives in its own bucket for the same reason: `cdef class` / `cpdef` / `cdef` are
        // not parsed by the Python symbol extractor, so advertising `.pyx` / `.pxd` as `python`
        // would claim `symbol_extraction=true` while emitting zero symbols.
        // Cython も同様の理由で別バケット。`cdef class` / `cpdef` / `cdef` は Python の抽出器で
        // 拾えないため、python として広告すると実際には 0 件しか出ない齟齬になる。
        Assert.Equal("cython", map[".pyx"]);
        Assert.Equal("cython", map[".pxd"]);
        Assert.Equal("python", map[".py"]);
        Assert.Equal("python", map[".pyi"]);

        // Issue #205 additions should also surface in the language list.
        // Issue #205 の追加分も言語一覧に露出する。
        Assert.Equal("groovy", map[".groovy"]);
        Assert.Equal("cuda", map[".cu"]);
        Assert.Equal("glsl", map[".glsl"]);
        Assert.Equal("hlsl", map[".hlsl"]);
        Assert.Equal("wgsl", map[".wgsl"]);
        Assert.Equal("metal", map[".metal"]);
        Assert.Equal("assembly", map[".asm"]);
        Assert.Equal("verilog", map[".v"]);
        Assert.Equal("systemverilog", map[".sv"]);
        Assert.Equal("vhdl", map[".vhd"]);
        Assert.Equal("commonlisp", map[".lisp"]);
        Assert.Equal("racket", map[".rkt"]);
        Assert.Equal("pascal", map[".pas"]);
        Assert.Equal("ada", map[".ada"]);
        Assert.Equal("fortran", map[".f90"]);
        Assert.Equal("raku", map[".raku"]);
        Assert.Equal("perl", map[".t"]);
        Assert.Equal("cobol", map[".cbl"]);
        Assert.Equal("cobol", map[".cob"]);
        Assert.Equal("cobol", map[".cobol"]);
        Assert.Equal("cobol", map[".cpy"]);
        // Mainstream extension-only languages should now be recognized for search/indexing.
        // 主要な拡張子ベース言語も search/indexing 用に認識されるべき。
        Assert.Equal("ocaml", map[".ml"]);
        Assert.Equal("ocaml", map[".mli"]);
        Assert.Equal("crystal", map[".cr"]);
        Assert.Equal("clojure", map[".clj"]);
        Assert.Equal("clojure", map[".cljs"]);
        Assert.Equal("clojure", map[".cljc"]);
        Assert.Equal("clojure", map[".edn"]);
        Assert.Equal("d", map[".d"]);
        Assert.Equal("erlang", map[".erl"]);
        Assert.Equal("erlang", map[".hrl"]);
        Assert.Equal("julia", map[".jl"]);
        Assert.Equal("nim", map[".nim"]);
        Assert.Equal("nim", map[".nims"]);
        Assert.Equal("perl", map[".pl"]);
        Assert.Equal("perl", map[".pm"]);
        Assert.Equal("perl", map[".pod"]);
        Assert.Equal("perl", map[".psgi"]);
        Assert.Equal("perl", map[".cgi"]);
        Assert.Equal("perl", map[".fcgi"]);
        Assert.Equal("perl", map[".t"]);
        Assert.Equal("solidity", map[".sol"]);
        Assert.Equal("tcl", map[".tcl"]);
        Assert.Equal("tcl", map[".tk"]);

        // Objective-C lives in its own bucket so `.m` / `.mm` are indexed instead of being skipped.
        // Objective-C は独立バケットにし、`.m` / `.mm` をスキップせずに index する。
        Assert.Equal("objc", map[".m"]);
        Assert.Equal("objc", map[".mm"]);
        Assert.Equal("cpp", map[".hh"]);
    }

    [Fact]
    public void ScanFiles_IndexesCobolExtensions()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var files = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["hello.cbl"] = "       IDENTIFICATION DIVISION.\n       PROGRAM-ID. HELLO.\n",
                ["copy.cpy"] = "       01  COPY-NAME PIC X(10).\n",
                ["legacy.cob"] = "       PROCEDURE DIVISION.\n",
                ["modern.cobol"] = "       STOP RUN.\n",
            };
            foreach (var (name, content) in files)
                File.WriteAllText(Path.Combine(tempDir, name), content);

            var scanned = new FileIndexer(tempDir).ScanFiles()
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(files.Keys.OrderBy(n => n, StringComparer.Ordinal).ToList(), scanned);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_IndexesIssue205AdditionalExtensionCoverage()
    {
        // Locks in the Issue #205 extensions that were silently dropped before:
        // Groovy, assembly, CUDA, GPU shaders, HDL, Common Lisp, Racket, Pascal, Ada,
        // Fortran, Raku, and Perl test scripts all need to survive scan-time filtering.
        // Issue #205 で黙って落ちていた拡張子を固定する。
        // Groovy / assembly / CUDA / GPU shaders / HDL / Common Lisp / Racket / Pascal / Ada /
        // Fortran / Raku / Perl test scripts が scan 時のフィルタを通過することを確認する。
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var files = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["build.groovy"] = "println 'hello'\n",
                ["kernel.cu"] = "__global__ void add() {}\n",
                ["shader.glsl"] = "void main() {}\n",
                ["shader.hlsl"] = "float4 main() : SV_Target { return 0; }\n",
                ["shader.wgsl"] = "@vertex fn main() -> @builtin(position) vec4<f32> { return vec4<f32>(); }\n",
                ["shader.metal"] = "kernel void main() {}\n",
                ["boot.s"] = "mov %eax, %eax\n",
                ["cpu.v"] = "module cpu(); endmodule\n",
                ["cpu.sv"] = "module cpu(); endmodule\n",
                ["cpu.vhd"] = "entity cpu is end entity;\n",
                ["demo.lisp"] = "(defun hello ())\n",
                ["demo.rkt"] = "#lang racket\n(displayln \"hi\")\n",
                ["demo.pas"] = "program demo;\nbegin\nend.\n",
                ["demo.ada"] = "procedure Demo is begin null; end Demo;\n",
                ["demo.f90"] = "program demo\nend program demo\n",
                ["demo.raku"] = "say \"hi\";\n",
                ["test.t"] = "use Test::More;\n",
            };

            foreach (var (name, content) in files)
                File.WriteAllText(Path.Combine(tempDir, name), content);

            var scanned = new FileIndexer(tempDir).ScanFiles()
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            var expected = files.Keys.OrderBy(name => name, StringComparer.Ordinal).ToList();
            Assert.Equal(expected, scanned);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_IndexesKotlinScriptAndExtractsSymbols()
    {
        // Gradle Kotlin DSL files must be indexed as Kotlin, not silently skipped.
        // Gradle Kotlin DSL ファイルは Kotlin として index され、黙って落ちてはいけない。
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var path = Path.Combine(tempDir, "build.gradle.kts");
            var content = """
                plugins {
                    kotlin("jvm") version "1.9.23"
                }

                val answer = 42
                """;
            File.WriteAllText(path, content);

            var scanned = new FileIndexer(tempDir).ScanFiles().ToList();

            Assert.Single(scanned);
            Assert.Equal(path, scanned[0]);
            Assert.Equal("kotlin", FileIndexer.DetectLanguage(path));

            var symbols = SymbolExtractor.Extract(1, "kotlin", content).ToList();
            Assert.Contains(symbols, symbol => symbol.Kind == "property" && symbol.Name == "answer");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void BuildRecordWithRawBytes_CppStyleHeaderContentIsDetectedAsCpp()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var path = Path.Combine(tempDir, "widget.h");
            var content = """
                #pragma once

                namespace demo {
                template <typename T>
                class Widget {
                public:
                    constexpr Widget() = default;
                };
                }
                """;
            File.WriteAllText(path, content);

            var indexer = new FileIndexer(tempDir);
            var (record, decodedContent, _, _) = indexer.BuildRecordWithRawBytes(path);

            Assert.Equal("cpp", record.Lang);
            Assert.Equal(content.Replace("\r\n", "\n"), decodedContent);

            var symbols = SymbolExtractor.Extract(1, record.Lang!, decodedContent).ToList();
            Assert.Contains(symbols, symbol => symbol.Kind == "namespace" && symbol.Name == "demo");
            Assert.Contains(symbols, symbol => symbol.Kind == "class" && symbol.Name == "Widget");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void BuildRecordWithRawBytes_CStyleHeaderContentStaysC()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var path = Path.Combine(tempDir, "legacy.h");
            var content = """
                #ifndef LEGACY_H
                #define LEGACY_H

                struct legacy_point {
                    int x;
                    int y;
                };

                #endif
                """;
            File.WriteAllText(path, content);

            var indexer = new FileIndexer(tempDir);
            var (record, decodedContent, _, _) = indexer.BuildRecordWithRawBytes(path);

            Assert.Equal("c", record.Lang);
            Assert.Equal(content.Replace("\r\n", "\n"), decodedContent);

            var symbols = SymbolExtractor.Extract(1, record.Lang!, decodedContent).ToList();
            Assert.DoesNotContain(symbols, symbol => symbol.Kind == "class");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_SkipsExcludedDirectories()
    {
        // Create a temp directory structure to test scanning
        // テスト用の一時ディレクトリ構造を作成
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(Path.Combine(tempDir, "app.py"), "print('hello')");

            var nodeModules = Path.Combine(tempDir, "node_modules");
            Directory.CreateDirectory(nodeModules);
            File.WriteAllText(Path.Combine(nodeModules, "dep.js"), "module.exports = {}");

            var indexer = new FileIndexer(tempDir);
            var files = indexer.ScanFiles();

            Assert.Single(files);
            Assert.Contains("app.py", files[0]);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Theory]
    [InlineData("node_modules", "dep.js")]
    [InlineData("target", "main.rs")]
    [InlineData("vendor", "dep.go")]
    [InlineData("bin", "app.cs")]
    public void ScanFiles_IndexesExplicitRootEvenWhenRootNameIsSkipped(string rootDirName, string fileName)
    {
        var tempParentDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        var rootDir = Path.Combine(tempParentDir, rootDirName);
        try
        {
            Directory.CreateDirectory(rootDir);
            File.WriteAllText(Path.Combine(rootDir, fileName), "content");

            var nestedNodeModules = Path.Combine(rootDir, "node_modules");
            Directory.CreateDirectory(nestedNodeModules);
            File.WriteAllText(Path.Combine(nestedNodeModules, "nested.js"), "module.exports = {}");

            var indexer = new FileIndexer(rootDir);
            var files = indexer.ScanFiles();

            Assert.Single(files);
            Assert.Contains(fileName, files[0]);
        }
        finally
        {
            if (Directory.Exists(tempParentDir))
                Directory.Delete(tempParentDir, true);
        }
    }

    [Fact]
    public void ScanFiles_SkipsExcludedFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(Path.Combine(tempDir, "app.js"), "console.log('hello')");
            File.WriteAllText(Path.Combine(tempDir, "package-lock.json"), "{}");
            File.WriteAllText(Path.Combine(tempDir, "Cargo.lock"), "# lock");
            File.WriteAllText(Path.Combine(tempDir, "Gemfile.lock"), "GEM");

            var indexer = new FileIndexer(tempDir);
            var files = indexer.ScanFiles();

            // Only app.js should be found, not package-lock.json
            // app.jsのみ検出され、package-lock.jsonは除外される
            Assert.Single(files);
            Assert.Contains("app.js", files[0]);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_SkipsControlCharacterFileNamesWithWarning()
    {
        if (OperatingSystem.IsWindows())
            return;

        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(Path.Combine(tempDir, "ok.cs"), "class Ok { }\n");
            File.WriteAllText(Path.Combine(tempDir, "bad\nname.cs"), "class Bad { }\n");

            var result = new FileIndexer(tempDir).ScanFilesDetailed();
            var files = result.Files
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(["ok.cs"], files);
            Assert.Contains(result.NonIndexablePaths, path => path == "bad\\u000Aname.cs");
            var warning = Assert.Single(result.Errors, error => error.Severity == FileIndexer.ScanIssueSeverity.Warning);
            Assert.Equal("bad\\u000Aname.cs", warning.Path);
            Assert.Contains("control characters", warning.Message);
            Assert.False(result.HadErrors);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void BuildRecordWithRawBytes_RejectsControlCharacterPathBeforeIo()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var indexer = new FileIndexer(tempDir);

            var ex = Assert.Throws<InvalidOperationException>(
                () => indexer.BuildRecordWithRawBytes(Path.Combine(tempDir, "bad\0name.cs")));

            Assert.Contains("control characters", ex.Message);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void EvaluatePathFilter_RejectsControlCharacterPathBeforeNormalization()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var indexer = new FileIndexer(tempDir);

            var filter = indexer.EvaluatePathFilter(Path.Combine(tempDir, "bad\0name.cs"));

            Assert.Equal(FileIndexer.PathFilterKind.ExcludedByDefaultFile, filter.FilterKind);
            Assert.True(filter.ShouldDeleteExisting);
            var warning = Assert.Single(filter.Errors);
            Assert.Equal(FileIndexer.ScanIssueSeverity.Warning, warning.Severity);
            Assert.Contains("control characters", warning.Message);
            Assert.Contains("\\u0000", warning.Path);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_SkipsAppleDoubleResourceForks()
    {
        // AppleDouble (`._*`) files masquerade as the real file's language (e.g. `._app.js`
        // looks like JavaScript) but are binary metadata blobs. They must be skipped wherever
        // they appear in the tree, including nested directories.
        // AppleDouble (`._*`) は原ファイルと同じ拡張子に見えるバイナリメタデータで、ツリーの
        // どこに置かれていても index 対象にしてはならない。
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(Path.Combine(tempDir, "app.js"), "console.log('hello')");
            File.WriteAllText(Path.Combine(tempDir, "._app.js"), "\x00\x05\x16\x07AppleDouble");
            File.WriteAllText(Path.Combine(tempDir, "._.gitignore"), "\x00\x05\x16\x07AppleDouble");

            var sub = Path.Combine(tempDir, "sub");
            Directory.CreateDirectory(sub);
            File.WriteAllText(Path.Combine(sub, "main.py"), "def hello(): pass\n");
            File.WriteAllText(Path.Combine(sub, "._main.py"), "\x00\x05\x16\x07AppleDouble");

            var indexer = new FileIndexer(tempDir);
            var files = indexer.ScanFiles()
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(["app.js", "sub/main.py"], files);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_AllowsRecognizedDotfiles()
    {
        // The AppleDouble denylist must not collateral-damage well-known dotfiles such as
        // .gitignore, .editorconfig, and .cdidxrc.json — they do not start with `._`.
        // AppleDouble の除外は `._` 接頭辞のみで判定するため、.gitignore / .editorconfig /
        // .cdidxrc.json などの既知 dotfile は引き続き走査対象に残る必要がある。
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(Path.Combine(tempDir, ".gitignore"), "node_modules\n");
            File.WriteAllText(Path.Combine(tempDir, ".editorconfig"), "root = true\n");
            File.WriteAllText(Path.Combine(tempDir, ".cdidxrc.json"), "{}");

            var indexer = new FileIndexer(tempDir);
            var files = indexer.ScanFiles()
                .Select(path => Path.GetFileName(path))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            Assert.Contains(".editorconfig", files);
            Assert.Contains(".gitignore", files);
            Assert.Contains(".cdidxrc.json", files);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void EvaluatePathFilter_TreatsAppleDoubleAsDefaultFileExclusion()
    {
        // Update-mode (--files / --commits) must match the walker's denylist so that
        // re-indexing an AppleDouble path explicitly does not bypass the default skip.
        // --files / --commits の更新モードでも AppleDouble を明示的に対象に含められないよう、
        // walker と同じ既定除外を返すこと。
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var appleDouble = Path.Combine(tempDir, "._app.js");
            File.WriteAllText(appleDouble, "\x00\x05\x16\x07AppleDouble");

            var indexer = new FileIndexer(tempDir);
            var filter = indexer.EvaluatePathFilter(appleDouble);

            Assert.Equal(FileIndexer.PathFilterKind.ExcludedByDefaultFile, filter.FilterKind);
            Assert.True(filter.ShouldDeleteExisting);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void EvaluatePathFilter_RejectsFileReachedThroughDirectorySymlinkOutsideProject()
    {
        if (OperatingSystem.IsWindows())
            return; // Creating symlinks on Windows requires admin/developer mode / Windows で symlink 作成には管理者権限が必要

        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        var outsideDir = Path.Combine(Path.GetTempPath(), $"codeindex_outside_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            Directory.CreateDirectory(outsideDir);
            File.WriteAllText(Path.Combine(outsideDir, "outside.cs"), "class Outside { }\n");

            var linkDir = Path.Combine(tempDir, "linked");
            Directory.CreateSymbolicLink(linkDir, outsideDir);
            var linkedFile = Path.Combine(linkDir, "outside.cs");

            var filter = new FileIndexer(tempDir).EvaluatePathFilter(linkedFile);

            Assert.Equal(FileIndexer.PathFilterKind.OutsideProjectRoot, filter.FilterKind);
            Assert.True(filter.ShouldDeleteExisting);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
            if (Directory.Exists(outsideDir))
                Directory.Delete(outsideDir, true);
        }
    }

    [Fact]
    public void ScanFiles_SkipsDirectorySymlinkPointingAtAncestor()
    {
        if (OperatingSystem.IsWindows())
            return; // Creating symlinks on Windows requires admin/developer mode / Windows で symlink 作成には管理者権限が必要

        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            var subDir = Path.Combine(tempDir, "sub");
            Directory.CreateDirectory(subDir);
            File.WriteAllText(Path.Combine(subDir, "foo.py"), "def hello(): pass\n");
            // Directory symlink pointing at the ancestor (self-recursion if followed).
            // 先祖を指すディレクトリ symlink（辿ると無限再帰になる）。
            Directory.CreateSymbolicLink(Path.Combine(subDir, "parent_loop"), "..");

            var indexer = new FileIndexer(tempDir);
            var files = indexer.ScanFiles()
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(["sub/foo.py"], files);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_FollowAllSymlinks_SkipsAlreadyVisitedDirectoryTarget()
    {
        if (OperatingSystem.IsWindows())
            return; // Creating symlinks on Windows requires admin/developer mode / Windows で symlink 作成には管理者権限が必要

        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            var subDir = Path.Combine(tempDir, "sub");
            Directory.CreateDirectory(subDir);
            File.WriteAllText(Path.Combine(subDir, "foo.py"), "def hello(): pass\n");
            Directory.CreateSymbolicLink(Path.Combine(subDir, "parent_loop"), "..");

            var indexer = new FileIndexer(
                tempDir,
                ignoreCase: false,
                ignoreRuleRoot: null,
                maxFileSizeBytes: null,
                directoryIgnoreCaseProbe: null,
                symlinkPolicy: FileIndexer.SymlinkPolicy.All);
            var result = indexer.ScanFilesDetailed();
            var files = result.Files
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(["sub/foo.py"], files);
            Assert.Contains(
                result.Errors,
                error => error.Severity == FileIndexer.ScanIssueSeverity.Warning
                    && error.Path == "sub/parent_loop"
                    && error.Message.Contains("already scanned", StringComparison.OrdinalIgnoreCase));
            Assert.False(result.HadErrors);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_ExcessiveDirectoryDepth_SkipsSubtreeWithWarning()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var current = tempDir;
            for (var i = 0; i < 130; i++)
            {
                current = Path.Combine(current, $"d{i:D3}");
                Directory.CreateDirectory(current);
            }
            File.WriteAllText(Path.Combine(current, "too_deep.py"), "print('too deep')\n");

            var result = new FileIndexer(tempDir).ScanFilesDetailed();

            Assert.Empty(result.Files);
            Assert.Contains(
                result.Errors,
                error => error.Severity == FileIndexer.ScanIssueSeverity.Warning
                    && error.Message.Contains("traversal depth exceeded", StringComparison.OrdinalIgnoreCase));
            Assert.False(result.HadErrors);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_SkipsFileSymlinkToRealFileInProject()
    {
        if (OperatingSystem.IsWindows())
            return; // Creating symlinks on Windows requires admin/developer mode / Windows で symlink 作成には管理者権限が必要

        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            var nested = Path.Combine(tempDir, "a", "b", "c");
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(nested, "foo.py"), "def hello(): pass\n");
            // File symlink that would otherwise cause the same content to be indexed under a second path.
            // 同じ内容が2つ目の path としても index されてしまうのを防ぐ確認。
            File.CreateSymbolicLink(Path.Combine(tempDir, "file_symlink.py"), Path.Combine("a", "b", "c", "foo.py"));

            var indexer = new FileIndexer(tempDir);
            var files = indexer.ScanFiles()
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(["a/b/c/foo.py"], files);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GetFileIndexability_RejectsFileSymlinkSoUpdateModeSkipsIt()
    {
        if (OperatingSystem.IsWindows())
            return; // Creating symlinks on Windows requires admin/developer mode / Windows で symlink 作成には管理者権限が必要

        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var realFile = Path.Combine(tempDir, "real.py");
            File.WriteAllText(realFile, "x = 1\n");
            // File symlink pointing at the same-tree real file. The Unix stat() path would follow this
            // symlink and see it as a regular file, so GetFileIndexability must gate on the reparse-point
            // check to keep --files / --commits update paths symlink-safe.
            // 同ツリー内の実ファイルを指すファイル symlink。Unix の stat() は symlink を辿ってしまうため、
            // GetFileIndexability は reparse-point ガードで弾かないと --files / --commits 経路で
            // 素通りしてしまう。
            var linkPath = Path.Combine(tempDir, "alias.py");
            File.CreateSymbolicLink(linkPath, realFile);

            Assert.True(FileIndexer.CanIndexFile(realFile));
            Assert.False(FileIndexer.CanIndexFile(linkPath));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Theory]
    [InlineData(@"\\.\COM1")]
    [InlineData(@"\\.\NUL")]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy1")]
    [InlineData(@"C:\repo\AUX.cs")]
    [InlineData(@"C:\repo\con.txt")]
    [InlineData(@"C:\repo\COM9")]
    [InlineData(@"C:\repo\LPT1.log")]
    public void IsWindowsDevicePath_RejectsReservedDeviceNames(string path)
    {
        Assert.True(FileIndexer.IsWindowsDevicePath(path));
    }

    [Theory]
    [InlineData(@"C:\repo\COM10.cs")]
    [InlineData(@"C:\repo\company.cs")]
    [InlineData(@"C:\repo\template1.cs")]
    public void IsWindowsDevicePath_AllowsOrdinaryNames(string path)
    {
        Assert.False(FileIndexer.IsWindowsDevicePath(path));
    }

    [Theory]
    [InlineData(FileAttributes.ReparsePoint, false, true)]
    [InlineData(FileAttributes.ReparsePoint, true, true)]
    [InlineData(FileAttributes.Hidden, false, false)]
    [InlineData(FileAttributes.Hidden, true, true)]
    [InlineData(FileAttributes.System, false, false)]
    [InlineData(FileAttributes.System, true, true)]
    [InlineData(FileAttributes.Hidden | FileAttributes.System, true, true)]
    [InlineData(FileAttributes.Archive, true, false)]
    public void HasSkippedAttributes_RejectsWindowsHiddenAndSystemOnly(
        FileAttributes attributes,
        bool isWindows,
        bool expected)
    {
        Assert.Equal(expected, FileIndexer.HasSkippedAttributes(attributes, isWindows));
    }

    [Fact]
    public void ScanFiles_OnWindowsSkipsHiddenAndSystemEntries()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(Path.Combine(tempDir, "visible.py"), "print('visible')\n");

            var hiddenFile = Path.Combine(tempDir, "hidden.py");
            File.WriteAllText(hiddenFile, "print('hidden')\n");
            File.SetAttributes(hiddenFile, File.GetAttributes(hiddenFile) | FileAttributes.Hidden);

            var systemFile = Path.Combine(tempDir, "system.py");
            File.WriteAllText(systemFile, "print('system')\n");
            File.SetAttributes(systemFile, File.GetAttributes(systemFile) | FileAttributes.System);

            var hiddenDir = Path.Combine(tempDir, "hidden_dir");
            Directory.CreateDirectory(hiddenDir);
            File.WriteAllText(Path.Combine(hiddenDir, "nested.py"), "print('hidden nested')\n");
            File.SetAttributes(hiddenDir, File.GetAttributes(hiddenDir) | FileAttributes.Hidden);

            var systemDir = Path.Combine(tempDir, "system_dir");
            Directory.CreateDirectory(systemDir);
            File.WriteAllText(Path.Combine(systemDir, "nested.py"), "print('system nested')\n");
            File.SetAttributes(systemDir, File.GetAttributes(systemDir) | FileAttributes.System);

            var indexer = new FileIndexer(tempDir);
            var files = indexer.ScanFiles()
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(["visible.py"], files);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void ScanFiles_SkipsDanglingSymlinksWithoutAbortingScan()
    {
        if (OperatingSystem.IsWindows())
            return; // Creating symlinks on Windows requires admin/developer mode / Windows で symlink 作成には管理者権限が必要

        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(Path.Combine(tempDir, "real.py"), "def real(): pass\n");
            // Dangling symlinks (target does not exist) must be skipped without aborting the scan.
            // target が存在しない dangling symlink は、scan 全体を落とさずスキップする。
            File.CreateSymbolicLink(Path.Combine(tempDir, "dangling.py"), "missing_target.py");
            Directory.CreateSymbolicLink(Path.Combine(tempDir, "dangling_dir"), Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}"));

            var indexer = new FileIndexer(tempDir);
            var files = indexer.ScanFiles()
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(["real.py"], files);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_RespectsGitignorePatternsAndNegation()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(Path.Combine(tempDir, ".gitignore"), "secret.py\nbuild_output/\n*.generated.js\n!keep.generated.js\n");
            File.WriteAllText(Path.Combine(tempDir, "keep.py"), "print('keep')");
            File.WriteAllText(Path.Combine(tempDir, "secret.py"), "print('secret')");
            File.WriteAllText(Path.Combine(tempDir, "app.generated.js"), "export const ignored = true;");
            File.WriteAllText(Path.Combine(tempDir, "keep.generated.js"), "export const kept = true;");
            Directory.CreateDirectory(Path.Combine(tempDir, "build_output"));
            File.WriteAllText(Path.Combine(tempDir, "build_output", "inside.py"), "print('ignored')");

            var indexer = new FileIndexer(tempDir);
            var files = indexer.ScanFiles()
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal([".gitignore", "keep.generated.js", "keep.py"], files);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_TrimsLeadingWhitespaceBeforeParsingIgnoreLines()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(Path.Combine(tempDir, ".gitignore"), "  # comment\n  *.tmp\n\\ leading.py\n\\#literal.py\n", Encoding.UTF8);
            File.WriteAllText(Path.Combine(tempDir, "keep.py"), "print('keep')");
            File.WriteAllText(Path.Combine(tempDir, "ignored.tmp"), "ignored");
            File.WriteAllText(Path.Combine(tempDir, " leading.py"), "print('literal leading space')");
            File.WriteAllText(Path.Combine(tempDir, "#literal.py"), "print('literal hash')");

            var indexer = new FileIndexer(tempDir);
            var files = indexer.ScanFiles()
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal([".gitignore", "keep.py"], files);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_ReportsOverlongIgnorePatternAndContinues()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(Path.Combine(tempDir, ".gitignore"), $"{new string('a', 513)}\n*.tmp\n", Encoding.UTF8);
            File.WriteAllText(Path.Combine(tempDir, "keep.py"), "print('keep')");
            File.WriteAllText(Path.Combine(tempDir, "ignored.tmp"), "ignored");

            var result = new FileIndexer(tempDir).ScanFilesDetailed();
            var files = result.Files
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal([".gitignore", "keep.py"], files);
            var warning = Assert.Single(result.Errors);
            Assert.Equal(FileIndexer.ScanIssueSeverity.Warning, warning.Severity);
            Assert.Contains("pattern exceeds 512 characters", warning.Message);
            Assert.False(result.HadErrors);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_RespectsCdidxignoreAndNestedGitignore()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            Directory.CreateDirectory(Path.Combine(tempDir, "src"));
            Directory.CreateDirectory(Path.Combine(tempDir, "fixtures"));
            File.WriteAllText(Path.Combine(tempDir, ".cdidxignore"), "fixtures/\n*.cache.js\n");
            File.WriteAllText(Path.Combine(tempDir, "src", ".gitignore"), "*.generated.cs\n");
            File.WriteAllText(Path.Combine(tempDir, "src", "Service.cs"), "public class Service { }");
            File.WriteAllText(Path.Combine(tempDir, "src", "Generated.generated.cs"), "public class Generated { }");
            File.WriteAllText(Path.Combine(tempDir, "fixtures", "sample.py"), "print('fixture')");
            File.WriteAllText(Path.Combine(tempDir, "app.cache.js"), "export const cache = true;");
            File.WriteAllText(Path.Combine(tempDir, "app.js"), "export const app = true;");

            var indexer = new FileIndexer(tempDir);
            var files = indexer.ScanFiles()
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(["app.js", "src/.gitignore", "src/Service.cs"], files);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_ReadsGitignoreAndCdidxignoreAsUtf8()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            Directory.CreateDirectory(Path.Combine(tempDir, "資料"));
            Directory.CreateDirectory(Path.Combine(tempDir, "cafe"));
            File.WriteAllText(Path.Combine(tempDir, ".gitignore"), "資料/\n", Encoding.UTF8);
            File.WriteAllText(Path.Combine(tempDir, ".cdidxignore"), "cafe/éclair.py\n", Encoding.UTF8);
            File.WriteAllText(Path.Combine(tempDir, "資料", "ignored.py"), "print('ignored')");
            File.WriteAllText(Path.Combine(tempDir, "cafe", "éclair.py"), "print('ignored')");
            File.WriteAllText(Path.Combine(tempDir, "keep.py"), "print('kept')");

            var indexer = new FileIndexer(tempDir);
            var files = indexer.ScanFiles()
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal([".gitignore", "keep.py"], files);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_RespectsWorkspaceConfigCdidxignore()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(tempDir, ".codeindex"));
            Directory.CreateDirectory(Path.Combine(tempDir, "generated"));
            File.WriteAllText(Path.Combine(tempDir, ".codeindex", ".cdidxignore"), "generated/\n*.cache.js\n");
            File.WriteAllText(Path.Combine(tempDir, "generated", "Ignored.cs"), "class Ignored { }");
            File.WriteAllText(Path.Combine(tempDir, "app.cache.js"), "export const ignored = true;");
            File.WriteAllText(Path.Combine(tempDir, "app.js"), "export const app = true;");

            var indexer = new FileIndexer(tempDir);
            var files = indexer.ScanFiles()
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(["app.js"], files);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFilesDetailed_SkipsNestedGitRepositoryBoundary()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(tempDir, "nested", ".git"));
            File.WriteAllText(Path.Combine(tempDir, "Root.cs"), "class Root { }");
            File.WriteAllText(Path.Combine(tempDir, "nested", "Nested.cs"), "class Nested { }");

            var indexer = new FileIndexer(tempDir);
            var result = indexer.ScanFilesDetailed();
            var files = result.Files
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(["Root.cs"], files);
            Assert.Equal(["nested"], result.NestedRepositories);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void EvaluatePathFilter_SkipsNestedGitRepositoryBoundary()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(tempDir, "nested", ".git"));
            var nestedFile = Path.Combine(tempDir, "nested", "Nested.cs");
            File.WriteAllText(nestedFile, "class Nested { }");

            var indexer = new FileIndexer(tempDir);
            var filter = indexer.EvaluatePathFilter(nestedFile);

            Assert.Equal(FileIndexer.PathFilterKind.ExcludedByDefaultDirectory, filter.FilterKind);
            Assert.True(filter.ShouldDeleteExisting);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void BuildRecord_NormalizesRelativePathToNfc()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var nfdName = "Cafe\u0301.cs";
            var filePath = Path.Combine(tempDir, nfdName);
            File.WriteAllText(filePath, "class Cafe { }");

            var indexer = new FileIndexer(tempDir);
            var (record, _, _) = indexer.BuildRecord(filePath);

            Assert.Equal("Caf\u00e9.cs", record.Path);
            Assert.True(record.Path.IsNormalized(NormalizationForm.FormC));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_PreservesInheritedRulesWhenRootIgnoreFileIsUnreadable()
    {
        if (OperatingSystem.IsWindows())
            return;

        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        var ignorePath = Path.Combine(tempDir, ".gitignore");
        UnixFileMode? originalMode = null;
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(ignorePath, "secret.py\n");
            File.WriteAllText(Path.Combine(tempDir, "secret.py"), "print('secret')");
            File.WriteAllText(Path.Combine(tempDir, "keep.py"), "print('keep')");
            originalMode = File.GetUnixFileMode(ignorePath);
            SetUnixPermissions(ignorePath, UnixFileMode.None);

            var indexer = new FileIndexer(tempDir);
            var result = indexer.ScanFilesDetailed();

            var files = result.Files
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal([".gitignore", "keep.py", "secret.py"], files);
            Assert.Contains(result.Errors, error =>
                error.Path == ".gitignore" &&
                error.Message == "Could not read .gitignore due to permissions." &&
                error.Severity == FileIndexer.ScanIssueSeverity.Warning);
            Assert.False(result.HadErrors);
        }
        finally
        {
            if (originalMode.HasValue && File.Exists(ignorePath))
                SetUnixPermissions(ignorePath, originalMode.Value);
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_PreservesInheritedRulesWhenNestedIgnoreFileIsUnreadable()
    {
        if (OperatingSystem.IsWindows())
            return;

        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        var nestedDir = Path.Combine(tempDir, "src");
        var ignorePath = Path.Combine(nestedDir, ".gitignore");
        UnixFileMode? originalMode = null;
        try
        {
            Directory.CreateDirectory(nestedDir);
            File.WriteAllText(Path.Combine(tempDir, "keep.py"), "print('keep')");
            File.WriteAllText(ignorePath, "secret.py\n");
            File.WriteAllText(Path.Combine(nestedDir, "secret.py"), "print('secret')");
            File.WriteAllText(Path.Combine(nestedDir, "keep_nested.py"), "print('nested keep')");
            originalMode = File.GetUnixFileMode(ignorePath);
            SetUnixPermissions(ignorePath, UnixFileMode.None);

            var indexer = new FileIndexer(tempDir);
            var result = indexer.ScanFilesDetailed();
            var files = result.Files
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(["keep.py", "src/.gitignore", "src/keep_nested.py", "src/secret.py"], files);
            Assert.Contains(result.Errors, error =>
                error.Path == "src/.gitignore" &&
                error.Message == "Could not read .gitignore due to permissions." &&
                error.Severity == FileIndexer.ScanIssueSeverity.Warning);
            Assert.False(result.HadErrors);
        }
        finally
        {
            if (originalMode.HasValue && File.Exists(ignorePath))
                SetUnixPermissions(ignorePath, originalMode.Value);
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFilesDetailed_LoadsFullAncestorIgnoreChainAndReportsIt()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            var workspace = Path.Combine(tempDir, "workspace");
            var projects = Path.Combine(workspace, "projects");
            var projectRoot = Path.Combine(projects, "subA");
            Directory.CreateDirectory(projectRoot);
            File.WriteAllText(Path.Combine(workspace, ".cdidxignore"), "*.cs\n");
            File.WriteAllText(Path.Combine(projects, ".gitignore"), "!subA/App.cs\n");
            File.WriteAllText(Path.Combine(projectRoot, "App.cs"), "class App { }\n");
            File.WriteAllText(Path.Combine(projectRoot, "Other.cs"), "class Other { }\n");

            var indexer = new FileIndexer(projectRoot, ignoreCase: false, ignoreRuleRoot: workspace);
            var result = indexer.ScanFilesDetailed();
            var files = result.Files
                .Select(path => Path.GetRelativePath(projectRoot, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(["App.cs"], files);
            Assert.Equal([workspace, projects], result.AncestorIgnoreDirectories);
            Assert.DoesNotContain(result.Errors, error => error.IsFatal);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFilesDetailed_FailsClosedWhenAncestorIgnoreDirectoryIsUnreadable()
    {
        if (OperatingSystem.IsWindows())
            return;

        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        var projects = Path.Combine(tempDir, "workspace", "projects");
        UnixFileMode? originalMode = null;
        try
        {
            var workspace = Path.Combine(tempDir, "workspace");
            var projectRoot = Path.Combine(projects, "subA");
            Directory.CreateDirectory(projectRoot);
            File.WriteAllText(Path.Combine(workspace, ".cdidxignore"), "*.cs\n");
            File.WriteAllText(Path.Combine(projectRoot, "App.cs"), "class App { }\n");
            originalMode = File.GetUnixFileMode(projects);
            SetUnixPermissions(projects, UnixFileMode.None);

            var indexer = new FileIndexer(projectRoot, ignoreCase: false, ignoreRuleRoot: workspace);
            var result = indexer.ScanFilesDetailed();

            Assert.Empty(result.Files);
            Assert.Contains(result.Errors, error =>
                error.Path == ".."
                && error.Message.StartsWith("Could not read ancestor ignore directory:", StringComparison.Ordinal));
            Assert.True(result.HadErrors);
        }
        finally
        {
            if (originalMode.HasValue)
                SetUnixPermissions(projects, originalMode.Value);
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFilesDetailed_DoesNotMarkParentsFullyScannedWhenNestedDirectoryFails()
    {
        if (OperatingSystem.IsWindows())
            return;

        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        var srcDir = Path.Combine(tempDir, "src");
        var blockedDir = Path.Combine(srcDir, "blocked");
        UnixFileMode? originalMode = null;
        try
        {
            Directory.CreateDirectory(blockedDir);
            File.WriteAllText(Path.Combine(tempDir, "keep.py"), "print('keep')");
            File.WriteAllText(Path.Combine(srcDir, "service.py"), "print('service')");
            File.WriteAllText(Path.Combine(blockedDir, "secret.py"), "print('secret')");
            originalMode = File.GetUnixFileMode(blockedDir);
            SetUnixPermissions(blockedDir, UnixFileMode.None);

            var indexer = new FileIndexer(tempDir);
            var result = indexer.ScanFilesDetailed();
            var files = result.Files
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(["keep.py", "src/service.py"], files);
            Assert.Contains(result.Errors, error => error.Path == "src/blocked" && error.Message == "Could not scan directory due to permissions.");
            Assert.Contains("", result.ListedDirectories);
            Assert.DoesNotContain("", result.FullyScannedDirectories);
            Assert.DoesNotContain("src", result.FullyScannedDirectories);
            Assert.DoesNotContain("src/blocked", result.FullyScannedDirectories);
        }
        finally
        {
            if (originalMode.HasValue && Directory.Exists(blockedDir))
                SetUnixPermissions(blockedDir, originalMode.Value);
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFilesDetailed_CheckpointedDirectories_AreSkippedAndCarriedForward()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(tempDir, "cached"));
            Directory.CreateDirectory(Path.Combine(tempDir, "fresh"));
            File.WriteAllText(Path.Combine(tempDir, "cached", "old.py"), "print('old')");
            File.WriteAllText(Path.Combine(tempDir, "fresh", "new.py"), "print('new')");

            var result = new FileIndexer(tempDir).ScanFilesDetailed(
                new HashSet<string>(StringComparer.Ordinal) { "cached" });
            var files = result.Files
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(["fresh/new.py"], files);
            Assert.Contains("cached", result.CheckpointedDirectories);
            Assert.Contains("fresh", result.CheckpointedDirectories);
            Assert.DoesNotContain("cached", result.ListedDirectories);
            Assert.DoesNotContain("cached", result.FullyScannedDirectories);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_RespectsRootAnchoredGitignorePatterns()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            Directory.CreateDirectory(Path.Combine(tempDir, "root_only_dir"));
            Directory.CreateDirectory(Path.Combine(tempDir, "src"));
            Directory.CreateDirectory(Path.Combine(tempDir, "src", "root_only_dir"));
            File.WriteAllText(Path.Combine(tempDir, ".gitignore"), "/root_only_dir/\n/secret.py\n");
            File.WriteAllText(Path.Combine(tempDir, "root_only_dir", "root.py"), "print('ignored root dir')");
            File.WriteAllText(Path.Combine(tempDir, "secret.py"), "print('ignored root file')");
            File.WriteAllText(Path.Combine(tempDir, "keep.py"), "print('kept root file')");
            File.WriteAllText(Path.Combine(tempDir, "src", "root_only_dir", "nested.py"), "print('kept nested dir')");
            File.WriteAllText(Path.Combine(tempDir, "src", "secret.py"), "print('kept nested file')");

            var indexer = new FileIndexer(tempDir);
            var files = indexer.ScanFiles()
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal([".gitignore", "keep.py", "src/root_only_dir/nested.py", "src/secret.py"], files);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_RespectsGlobstarPrefixPatternAtProjectRoot()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            Directory.CreateDirectory(Path.Combine(tempDir, "nested"));
            File.WriteAllText(Path.Combine(tempDir, ".gitignore"), "**/*.min.js\n");
            File.WriteAllText(Path.Combine(tempDir, "app.min.js"), "export const ignored = true;");
            File.WriteAllText(Path.Combine(tempDir, "nested", "lib.min.js"), "export const nestedIgnored = true;");
            File.WriteAllText(Path.Combine(tempDir, "app.js"), "export const kept = true;");

            var indexer = new FileIndexer(tempDir);
            var files = indexer.ScanFiles()
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal([".gitignore", "app.js"], files);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_HandlesGitIgnoreWhitespaceLikeGit()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(
                Path.Combine(tempDir, ".gitignore"),
                "  #*.py\n  *.py\n*.cs\t\n");
            File.WriteAllText(Path.Combine(tempDir, "  #x.py"), "print('kept because leading-space # is a comment')");
            File.WriteAllText(Path.Combine(tempDir, "a.py"), "print('ignored after leading-space trim')");
            File.WriteAllText(Path.Combine(tempDir, "  a.py"), "print('ignored by trimmed basename pattern')");
            File.WriteAllText(Path.Combine(tempDir, "a.cs"), "public class IgnoredAfterTrailingTabTrim { }");
            File.WriteAllText(Path.Combine(tempDir, "keep.js"), "export const kept = true;");

            var indexer = new FileIndexer(tempDir);
            var files = indexer.ScanFiles()
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal([".gitignore", "keep.js"], files);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_RespectsGlobstarMiddlePatternWithZeroOrMoreDirectories()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            Directory.CreateDirectory(Path.Combine(tempDir, "foo"));
            Directory.CreateDirectory(Path.Combine(tempDir, "foo", "deep"));
            File.WriteAllText(Path.Combine(tempDir, ".gitignore"), "foo/**/bar.py\n");
            File.WriteAllText(Path.Combine(tempDir, "foo", "bar.py"), "print('ignored shallow')");
            File.WriteAllText(Path.Combine(tempDir, "foo", "deep", "bar.py"), "print('ignored deep')");
            File.WriteAllText(Path.Combine(tempDir, "foo", "keep.py"), "print('kept')");

            var indexer = new FileIndexer(tempDir);
            var files = indexer.ScanFiles()
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal([".gitignore", "foo/keep.py"], files);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_RespectsTrailingGlobstarWithoutIgnoringRootDirectoryItself()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            Directory.CreateDirectory(Path.Combine(tempDir, "foo"));
            Directory.CreateDirectory(Path.Combine(tempDir, "foo", "nested"));
            File.WriteAllText(Path.Combine(tempDir, ".gitignore"), "foo/**\n!foo/bar.py\n");
            File.WriteAllText(Path.Combine(tempDir, "foo", "bar.py"), "print('keep')");
            File.WriteAllText(Path.Combine(tempDir, "foo", "nested", "ignored.py"), "print('ignored')");

            var indexer = new FileIndexer(tempDir);
            var files = indexer.ScanFiles()
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal([".gitignore", "foo/bar.py"], files);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_RespectsTrailingGlobstarDirectoryPatternWithoutIgnoringRootDirectoryItself()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            Directory.CreateDirectory(Path.Combine(tempDir, "foo"));
            Directory.CreateDirectory(Path.Combine(tempDir, "foo", "bar"));
            File.WriteAllText(Path.Combine(tempDir, ".gitignore"), "foo/**/\n!foo/bar.py\n");
            File.WriteAllText(Path.Combine(tempDir, "foo", "bar.py"), "print('keep')");
            File.WriteAllText(Path.Combine(tempDir, "foo", "keep.py"), "print('keep')");
            File.WriteAllText(Path.Combine(tempDir, "foo", "bar", "ignored.py"), "print('ignored')");

            var indexer = new FileIndexer(tempDir);
            var files = indexer.ScanFiles()
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal([".gitignore", "foo/bar.py", "foo/keep.py"], files);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_TreatsNonSpecialDoubleStarAsSingleSegmentWildcard()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            Directory.CreateDirectory(Path.Combine(tempDir, "dir"));
            Directory.CreateDirectory(Path.Combine(tempDir, "dir", "a"));
            Directory.CreateDirectory(Path.Combine(tempDir, "dir", "a", "x"));
            File.WriteAllText(Path.Combine(tempDir, ".gitignore"), "dir/a**b.py\n");
            File.WriteAllText(Path.Combine(tempDir, "dir", "ab.py"), "print('ignored')");
            File.WriteAllText(Path.Combine(tempDir, "dir", "axxb.py"), "print('ignored')");
            File.WriteAllText(Path.Combine(tempDir, "dir", "a", "x", "b.py"), "print('kept')");

            var indexer = new FileIndexer(tempDir);
            var files = indexer.ScanFiles()
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal([".gitignore", "dir/a/x/b.py"], files);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_RespectsGitIgnoreCaseSettingFromRepository()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            RunGit(tempDir, "init");
            RunGit(tempDir, "config", "user.name", "CodeIndex Tests");
            RunGit(tempDir, "config", "user.email", "tests@example.com");
            RunGit(tempDir, "config", "core.ignorecase", "true");
            File.WriteAllText(Path.Combine(tempDir, ".gitignore"), "FOO.py\n");
            File.WriteAllText(Path.Combine(tempDir, "foo.py"), "print('ignored')");
            File.WriteAllText(Path.Combine(tempDir, "keep.py"), "print('kept')");

            var indexer = new FileIndexer(tempDir, GitHelper.ResolveIgnoreCase(tempDir));
            var files = indexer.ScanFiles()
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal([".gitignore", "keep.py"], files);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_RespectsGitIgnoreCaseSettingForAsciiOnly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            RunGit(tempDir, "init");
            RunGit(tempDir, "config", "user.name", "CodeIndex Tests");
            RunGit(tempDir, "config", "user.email", "tests@example.com");
            RunGit(tempDir, "config", "core.ignorecase", "true");
            File.WriteAllText(Path.Combine(tempDir, ".gitignore"), "Å.py\n[[:upper:]].rb\n[A-Z].cs\n");
            File.WriteAllText(Path.Combine(tempDir, "å.py"), "print('kept non-ascii fold')");
            File.WriteAllText(Path.Combine(tempDir, "a.rb"), "puts 'ignored lower via ignorecase'");
            File.WriteAllText(Path.Combine(tempDir, "å.rb"), "puts 'kept non-ascii lower'");
            File.WriteAllText(Path.Combine(tempDir, "a.cs"), "class IgnoredLower { }");
            File.WriteAllText(Path.Combine(tempDir, "å.cs"), "class KeptLower { }");

            var indexer = new FileIndexer(tempDir, GitHelper.ResolveIgnoreCase(tempDir));
            var files = indexer.ScanFiles()
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal([".gitignore", "å.cs", "å.py", "å.rb"], files);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_SubdirectoryProjectRoot_RespectsAncestorGitignore()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        var projectRoot = Path.Combine(tempDir, "subproj");
        try
        {
            Directory.CreateDirectory(projectRoot);
            RunGit(tempDir, "init");
            RunGit(tempDir, "config", "user.name", "CodeIndex Tests");
            RunGit(tempDir, "config", "user.email", "tests@example.com");
            File.WriteAllText(Path.Combine(tempDir, ".gitignore"), "subproj/ignored.py\n");
            File.WriteAllText(Path.Combine(projectRoot, "ignored.py"), "print('ignored')");
            File.WriteAllText(Path.Combine(projectRoot, "keep.py"), "print('kept')");

            var indexer = new FileIndexer(projectRoot, GitHelper.ResolveIgnoreCase(projectRoot), GitHelper.TryGetRepositoryRoot(projectRoot));
            var files = indexer.ScanFiles()
                .Select(path => Path.GetRelativePath(projectRoot, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(["keep.py"], files);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_SubdirectoryProjectRoot_RespectsAncestorGitignoreDirectoryRule()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        var projectRoot = Path.Combine(tempDir, "subproj");
        try
        {
            Directory.CreateDirectory(projectRoot);
            RunGit(tempDir, "init");
            RunGit(tempDir, "config", "user.name", "CodeIndex Tests");
            RunGit(tempDir, "config", "user.email", "tests@example.com");
            File.WriteAllText(Path.Combine(tempDir, ".gitignore"), "subproj/\n");
            File.WriteAllText(Path.Combine(projectRoot, "app.py"), "print('ignored root dir')");

            var indexer = new FileIndexer(projectRoot, GitHelper.ResolveIgnoreCase(projectRoot), GitHelper.TryGetRepositoryRoot(projectRoot));
            var files = indexer.ScanFiles()
                .Select(path => Path.GetRelativePath(projectRoot, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Empty(files);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_ProjectRootNamedNodeModules_IsIndexedButNestedSkipDirsRemainSkipped()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        var projectRoot = Path.Combine(tempDir, "node_modules");
        try
        {
            Directory.CreateDirectory(projectRoot);
            File.WriteAllText(Path.Combine(projectRoot, "app.js"), "console.log('ignored root dir');");
            Directory.CreateDirectory(Path.Combine(projectRoot, "node_modules"));
            File.WriteAllText(Path.Combine(projectRoot, "node_modules", "nested.js"), "console.log('skip child');");

            var indexer = new FileIndexer(projectRoot);
            var files = indexer.ScanFiles()
                .Select(path => Path.GetRelativePath(projectRoot, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(["app.js"], files);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_RespectsGitignoreBracketCharacterClassesAndRanges()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(Path.Combine(tempDir, ".gitignore"), "[ab].cs\nfile[0-9].py\n");
            File.WriteAllText(Path.Combine(tempDir, "a.cs"), "class A { }");
            File.WriteAllText(Path.Combine(tempDir, "b.cs"), "class B { }");
            File.WriteAllText(Path.Combine(tempDir, "c.cs"), "class C { }");
            File.WriteAllText(Path.Combine(tempDir, "file1.py"), "print('ignored')");
            File.WriteAllText(Path.Combine(tempDir, "filex.py"), "print('kept')");

            var indexer = new FileIndexer(tempDir);
            var files = indexer.ScanFiles()
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal([".gitignore", "c.cs", "filex.py"], files);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_RespectsGitignoreNegatedBracketCharacterClass()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(Path.Combine(tempDir, ".gitignore"), "[!a].cs\n");
            File.WriteAllText(Path.Combine(tempDir, "a.cs"), "class A { }");
            File.WriteAllText(Path.Combine(tempDir, "b.cs"), "class B { }");
            File.WriteAllText(Path.Combine(tempDir, "c.cs"), "class C { }");

            var indexer = new FileIndexer(tempDir);
            var files = indexer.ScanFiles()
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal([".gitignore", "a.cs"], files);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_RespectsGitignoreBracketCharacterClassWithLeadingLiteralRightBracket()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(Path.Combine(tempDir, ".gitignore"), "[]].cs\n");
            File.WriteAllText(Path.Combine(tempDir, "].cs"), "class Ignored { }");
            File.WriteAllText(Path.Combine(tempDir, "keep.cs"), "class Kept { }");

            var indexer = new FileIndexer(tempDir);
            var files = indexer.ScanFiles()
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal([".gitignore", "keep.cs"], files);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_RespectsGitignoreAsciiPosixDigitBracketCharacterClass()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(Path.Combine(tempDir, ".gitignore"), "[[:digit:]].py\n");
            File.WriteAllText(Path.Combine(tempDir, "1.py"), "print('ignored')");
            File.WriteAllText(Path.Combine(tempDir, "١.py"), "print('kept non-ascii digit')");
            File.WriteAllText(Path.Combine(tempDir, "a.py"), "print('kept')");

            var indexer = new FileIndexer(tempDir);
            var files = indexer.ScanFiles()
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal([".gitignore", "a.py", "١.py"], files);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_RespectsGitignoreAsciiPosixUpperBracketCharacterClass()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(Path.Combine(tempDir, ".gitignore"), "[[:upper:]].cs\n");
            File.WriteAllText(Path.Combine(tempDir, "A.cs"), "class Ignored { }");
            File.WriteAllText(Path.Combine(tempDir, "É.cs"), "class KeptNonAscii { }");
            File.WriteAllText(Path.Combine(tempDir, "keep.cs"), "class Kept { }");

            var indexer = new FileIndexer(tempDir);
            var files = indexer.ScanFiles()
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal([".gitignore", "keep.cs", "É.cs"], files);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_RespectsGitignorePosixPunctBracketCharacterClass()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(Path.Combine(tempDir, ".gitignore"), "[[:punct:]].cs\n");
            File.WriteAllText(Path.Combine(tempDir, "!.cs"), "class Ignored { }");
            File.WriteAllText(Path.Combine(tempDir, "a.cs"), "class Kept { }");

            var indexer = new FileIndexer(tempDir);
            var files = indexer.ScanFiles()
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal([".gitignore", "a.cs"], files);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_RespectsGitignoreNegatedBracketCharacterClassWithLeadingLiteralRightBracket()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(Path.Combine(tempDir, ".gitignore"), "[!]].cs\n");
            File.WriteAllText(Path.Combine(tempDir, "].cs"), "class Kept { }");
            File.WriteAllText(Path.Combine(tempDir, "a.cs"), "class Ignored { }");

            var indexer = new FileIndexer(tempDir);
            var files = indexer.ScanFiles()
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal([".gitignore", "].cs"], files);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_RespectsGitignoreBracketNegationPrefixesAndLiteralCaret()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(Path.Combine(tempDir, ".gitignore"), "[!a].js\n[^a].py\n[a^b].cs\n[\\^a].rb\n");
            File.WriteAllText(Path.Combine(tempDir, "a.js"), "export const kept = true;");
            File.WriteAllText(Path.Combine(tempDir, "b.js"), "export const ignored = true;");
            File.WriteAllText(Path.Combine(tempDir, "a.py"), "print('kept')");
            File.WriteAllText(Path.Combine(tempDir, "b.py"), "print('ignored')");
            File.WriteAllText(Path.Combine(tempDir, "a.cs"), "class IgnoredA { }");
            File.WriteAllText(Path.Combine(tempDir, "^.cs"), "class IgnoredCaret { }");
            File.WriteAllText(Path.Combine(tempDir, "c.cs"), "class Kept { }");
            File.WriteAllText(Path.Combine(tempDir, "a.rb"), "puts 'ignored'");
            File.WriteAllText(Path.Combine(tempDir, "^.rb"), "puts 'ignored'");
            File.WriteAllText(Path.Combine(tempDir, "b.rb"), "puts 'kept'");

            var indexer = new FileIndexer(tempDir);
            var files = indexer.ScanFiles()
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal([".gitignore", "a.js", "a.py", "b.rb", "c.cs"], files);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_RespectsGitignoreEscapedLiteralCharacters()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(Path.Combine(tempDir, ".gitignore"), "foo\\ bar.py\nliteral\\[name\\].js\n\\#literal.txt\n\\!important.cs\n");
            File.WriteAllText(Path.Combine(tempDir, "foo bar.py"), "print('ignored')");
            File.WriteAllText(Path.Combine(tempDir, "literal[name].js"), "export const ignored = true;");
            File.WriteAllText(Path.Combine(tempDir, "#literal.txt"), "ignored");
            File.WriteAllText(Path.Combine(tempDir, "!important.cs"), "class Ignored { }");
            File.WriteAllText(Path.Combine(tempDir, "keep.py"), "print('kept')");

            var indexer = new FileIndexer(tempDir);
            var files = indexer.ScanFiles()
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal([".gitignore", "keep.py"], files);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFilesDetailed_SkipsMalformedIgnoreRulesWithoutAborting()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(Path.Combine(tempDir, ".gitignore"), "[z-a].py\n[!].cs\n[a.py\n[!a\n[^\n[\n[]\nignored.py\n");
            File.WriteAllText(Path.Combine(tempDir, "[a.py"), "print('kept malformed literal')");
            File.WriteAllText(Path.Combine(tempDir, "ignored.py"), "print('ignored')");
            File.WriteAllText(Path.Combine(tempDir, "keep.py"), "print('kept')");

            var indexer = new FileIndexer(tempDir);
            var scanResult = indexer.ScanFilesDetailed();
            var files = scanResult.Files
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal([".gitignore", "[a.py", "keep.py"], files);
            Assert.Equal(7, scanResult.Errors.Count);
            Assert.All(scanResult.Errors, error => Assert.Contains(".gitignore:", error.Path, StringComparison.Ordinal));
            Assert.All(scanResult.Errors, error => Assert.Contains("Invalid ignore rule skipped", error.Message, StringComparison.Ordinal));
            Assert.Contains(scanResult.Errors, error => error.Message == "Invalid ignore rule skipped: reversed character class range");
            Assert.All(scanResult.Errors, error => Assert.Equal(FileIndexer.ScanIssueSeverity.Warning, error.Severity));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFilesDetailed_SeparatesUnknownExtensionsFromOtherNonIndexableFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(Path.Combine(tempDir, ".gitignore"), "ignored.mystery\n");
            File.WriteAllText(Path.Combine(tempDir, "app.cs"), "class App { }\n");
            File.WriteAllText(Path.Combine(tempDir, "Dockerfile.dev"), "FROM scratch\n");
            File.WriteAllText(Path.Combine(tempDir, "tool"), "plain text without a shebang\n");
            File.WriteAllText(Path.Combine(tempDir, "data.mystery"), "unknown extension\n");
            File.WriteAllText(Path.Combine(tempDir, "ignored.mystery"), "ignored unknown extension\n");

            var indexer = new FileIndexer(tempDir);
            var scanResult = indexer.ScanFilesDetailed();

            Assert.Equal(["data.mystery"], scanResult.UnknownExtensionFiles);
            Assert.Contains("data.mystery", scanResult.NonIndexablePaths);
            Assert.Contains("tool", scanResult.NonIndexablePaths);
            Assert.DoesNotContain("tool", scanResult.UnknownExtensionFiles);
            Assert.DoesNotContain("ignored.mystery", scanResult.UnknownExtensionFiles);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void DetectLanguage_ExtensionlessShebangs_HonorsUnicodeBomEncodings()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);

            var files = new Dictionary<string, Encoding>
            {
                ["utf8"] = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                ["utf8-bom"] = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
                ["utf16-le"] = new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
                ["utf16-be"] = new UnicodeEncoding(bigEndian: true, byteOrderMark: true),
            };

            foreach (var (name, encoding) in files)
            {
                var path = Path.Combine(tempDir, name);
                File.WriteAllText(path, "#!/usr/bin/env bash\nprintf 'ok'\n", encoding);
            }

            var detected = files.Keys
                .ToDictionary(name => name, name => FileIndexer.DetectLanguage(Path.Combine(tempDir, name)));

            Assert.All(detected, pair => Assert.Equal("shell", pair.Value));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_IncludesModernNodeModuleExtensions()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(Path.Combine(tempDir, "index.mjs"), "export const run = () => {};");
            File.WriteAllText(Path.Combine(tempDir, "cli.cjs"), "module.exports = {};");
            File.WriteAllText(Path.Combine(tempDir, "types.cts"), "export type Config = {};");
            File.WriteAllText(Path.Combine(tempDir, "types.d.mts"), "export interface Config {}");
            File.WriteAllText(Path.Combine(tempDir, "notes.txt"), "ignored");

            var indexer = new FileIndexer(tempDir);
            var files = indexer.ScanFiles().Select(Path.GetFileName).OrderBy(name => name).ToList();

            Assert.Equal(["cli.cjs", "index.mjs", "types.cts", "types.d.mts"], files);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_IncludesExtensionlessShebangScripts()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(Path.Combine(tempDir, "rbenv-init"), "#!/usr/bin/env bash\necho init\n");
            File.WriteAllText(Path.Combine(tempDir, "python-tool"), "#!/usr/bin/python3\nprint('hi')\n");
            File.WriteAllText(Path.Combine(tempDir, "plain-text"), "Hello world\n");
            File.WriteAllText(Path.Combine(tempDir, "known.rb"), "puts 'known'\n");

            var indexer = new FileIndexer(tempDir);
            var files = indexer.ScanFiles()
                .Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(["known.rb", "python-tool", "rbenv-init"], files);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_ExcludesUnknownExtensionEvenWhenShebangLooksSupported()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(Path.Combine(tempDir, "notes.txt"), "#!/usr/bin/env bash\necho hi\n");
            File.WriteAllText(Path.Combine(tempDir, "script"), "#!/usr/bin/env bash\necho hi\n");

            var indexer = new FileIndexer(tempDir);
            var files = indexer.ScanFiles()
                .Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(["script"], files);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ScanFiles_IgnoresUnixFifoWithoutHanging()
    {
        if (OperatingSystem.IsWindows())
            return;

        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var extensionlessFifo = Path.Combine(tempDir, "tool");
            var extensionFifo = Path.Combine(tempDir, "tool.sh");
            var knownNameFifo = Path.Combine(tempDir, "Dockerfile");
            CreateUnixFifo(extensionlessFifo);
            CreateUnixFifo(extensionFifo);
            CreateUnixFifo(knownNameFifo);

            Assert.False(FileIndexer.CanIndexFile(extensionlessFifo));
            Assert.False(FileIndexer.CanIndexFile(extensionFifo));
            Assert.False(FileIndexer.CanIndexFile(knownNameFifo));

            var indexer = new FileIndexer(tempDir);
            var scanTask = Task.Run(() => indexer.ScanFiles());
            var completedTask = await Task.WhenAny(scanTask, Task.Delay(TimeSpan.FromSeconds(2)));

            Assert.Same(scanTask, completedTask);
            Assert.Empty(await scanTask);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void BuildRecord_HandlesUnicodeAndCjkContent()
    {
        // Files with Unicode/CJK characters in content should be indexed correctly
        // Unicode/CJK文字を含むファイルが正しくインデックスされること
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var content = "// コメント: 日本語テスト\npublic class 日本語クラス\n{\n    public string 名前 { get; set; }\n    // 中文注释\n    // 한국어 주석\n}\n";
            var filePath = Path.Combine(tempDir, "unicode.cs");
            File.WriteAllText(filePath, content);

            var indexer = new FileIndexer(tempDir);
            var (record, fileContent, warning) = indexer.BuildRecord(filePath);

            Assert.Equal("unicode.cs", record.Path);
            Assert.Equal("csharp", record.Lang);
            Assert.Null(warning); // Valid UTF-8, no warning / 有効なUTF-8なので警告なし
            Assert.Contains("日本語クラス", fileContent);
            Assert.Contains("中文注释", fileContent);
            Assert.Contains("한국어", fileContent);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Theory]
    [InlineData("class A\r\n{\r\n}\r\n", 3)]
    [InlineData("class A\n{\n}\n", 3)]
    [InlineData("class A\r{\r}\r", 3)]
    [InlineData("\uFEFF", 0)]
    public void BuildRecord_CountsPhysicalLinesAfterLineLeadingInvisibleStripping(string content, int expectedLines)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var filePath = Path.Combine(tempDir, "physical.cs");
            File.WriteAllText(filePath, content);

            var indexer = new FileIndexer(tempDir);
            var (record, _, _) = indexer.BuildRecord(filePath);

            Assert.Equal(expectedLines, record.Lines);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ValidateSymbolLineRanges_RejectsLinesOutsideFileRange()
    {
        var record = new FileRecord
        {
            Path = "src/drift.cs",
            Lang = "csharp",
            Lines = 2,
        };
        var symbols = new[]
        {
            new SymbolRecord { FileId = 1, Kind = "class", Name = "Drift", Line = 3, StartLine = 3, EndLine = 3 },
        };

        var ex = Assert.Throws<InvalidOperationException>(() => FileIndexer.ValidateSymbolLineRanges(record, symbols));

        Assert.Contains("outside file line range", ex.Message);
        Assert.Contains("src/drift.cs", ex.Message);
    }

    [Fact]
    public void BuildRecord_CjkSymbolsExtractedCorrectly()
    {
        var content = "// 日本語コメント\npublic class ユーザーサービス\n{\n    public string 名前を取得(int id) { return \"\"; }\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        // CJK class and method names should be extracted / CJKのクラス名・メソッド名が抽出されること
        // Note: \w in .NET regex matches Unicode letters, so CJK identifiers work
        // 注: .NET の \w は Unicode 文字にマッチするため CJK 識別子も動作する
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "ユーザーサービス");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "名前を取得");
    }

    [Fact]
    public void BuildRecord_NormalizesPathSeparators()
    {
        // Ensure Windows-style backslashes are converted to forward slashes
        // Windows形式のバックスラッシュがフォワードスラッシュに変換されることを確認
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            var subDir = Path.Combine(tempDir, "src", "models");
            Directory.CreateDirectory(subDir);
            var filePath = Path.Combine(subDir, "user.py");
            File.WriteAllText(filePath, "class User: pass\n");

            var indexer = new FileIndexer(tempDir);
            var (record, _, _) = indexer.BuildRecord(filePath);

            // Path should use forward slashes regardless of OS
            // OSに関わらずフォワードスラッシュを使うべき
            Assert.DoesNotContain("\\", record.Path);
            Assert.Contains("/", record.Path);
            Assert.Equal("src/models/user.py", record.Path);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void BuildRecord_PreservesBackslashInPosixFilename()
    {
        // On POSIX, '\' is a valid filename character and must not be converted to '/'.
        // Otherwise a file named "back\slash.py" becomes a phantom "back/slash.py".
        // POSIX では '\' は正当なファイル名文字であり、'/' に置換すべきでない。
        // 置換すると "back\slash.py" が幻の "back/slash.py" として保存されてしまう。
        if (OperatingSystem.IsWindows())
            return;

        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var filePath = Path.Combine(tempDir, "back\\slash.py");
            File.WriteAllText(filePath, "def hu(): pass\n");

            var indexer = new FileIndexer(tempDir);
            var (record, _, _) = indexer.BuildRecord(filePath);

            Assert.Equal("back\\slash.py", record.Path);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void NormalizePathSeparators_OnPosixKeepsBackslashInFilename()
    {
        if (OperatingSystem.IsWindows())
            return;

        Assert.Equal("back\\slash.py", FileIndexer.NormalizePathSeparators("back\\slash.py"));
        Assert.Equal("dir/back\\slash.py", FileIndexer.NormalizePathSeparators("dir/back\\slash.py"));
    }

    [Fact]
    public void NormalizePathSeparators_OnWindowsConvertsBackslashToForwardSlash()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Assert.Equal("src/models/user.py", FileIndexer.NormalizePathSeparators("src\\models\\user.py"));
    }

    [Fact]
    public void ScanFiles_IncludesFileNameBasedLanguages()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(Path.Combine(tempDir, "Dockerfile"), "FROM alpine");
            File.WriteAllText(Path.Combine(tempDir, "Makefile"), "all: build");
            File.WriteAllText(Path.Combine(tempDir, "app.py"), "print('hello')");
            File.WriteAllText(Path.Combine(tempDir, "unknown.xyz"), "nothing");

            var indexer = new FileIndexer(tempDir);
            var files = indexer.ScanFiles();

            // Dockerfile, Makefile, and app.py should be found; unknown.xyz should not
            Assert.Equal(3, files.Count);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    [Trait("Platform", "Windows")]
    public void ScanFiles_WindowsLongPath_IndexesAndSurvivesStalePurge()
    {
        // Windows-only syscall coverage: POSIX cannot exercise Win32 MAX_PATH behavior.
        if (!OperatingSystem.IsWindows())
            return;

        var tempRoot = TestProjectHelper.CreateTempProject("cdidx_long_path");
        var projectRoot = Path.Combine(tempRoot, "node_modules");
        DbContext? db = null;
        try
        {
            Directory.CreateDirectory(LongPath.EnsureWindowsPrefix(projectRoot));
            var leafPath = CreateWindowsLongPathFixture(projectRoot);
            Assert.True(leafPath.Length >= 260, $"Fixture path length was {leafPath.Length}, expected >= 260.");

            var indexer = new FileIndexer(projectRoot);
            var scannedFiles = indexer.ScanFiles();

            Assert.Contains(scannedFiles, path => PathsEqual(path, leafPath));

            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            db = new DbContext(dbPath);
            db.InitializeSchema();
            var writer = new DbWriter(db.Connection);

            IndexScannedFiles(projectRoot, writer);
            var relativeLeafPath = FileIndexer.NormalizePathSeparators(Path.GetRelativePath(projectRoot, leafPath));

            Assert.True(IndexedFileExists(db, relativeLeafPath));
            Assert.Equal(0, writer.PurgeStaleFiles(projectRoot));

            IndexScannedFiles(projectRoot, writer);
            Assert.True(IndexedFileExists(db, relativeLeafPath));
        }
        finally
        {
            if (db is not null)
            {
                SqliteConnection.ClearPool(db.Connection);
                db.Dispose();
            }

            DeleteLongPathDirectory(tempRoot);
        }
    }

    [Fact]
    public void GetFamilyScopeKey_MarkerlessRootUsesTopLevelSubtreeScope()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(tempDir, "src"));
            Directory.CreateDirectory(Path.Combine(tempDir, "generated"));

            var srcFile = Path.Combine(tempDir, "src", "Api.Part1.cs");
            var generatedFile = Path.Combine(tempDir, "generated", "Api.Part2.cs");
            File.WriteAllText(srcFile, "public partial class Api {}");
            File.WriteAllText(generatedFile, "public partial class Api {}");

            var indexer = new FileIndexer(tempDir);

            Assert.Equal("src", indexer.GetFamilyScopeKey(srcFile, "csharp"));
            Assert.Equal("generated", indexer.GetFamilyScopeKey(generatedFile, "csharp"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GetFamilyScopeKey_MarkerlessRootLevelFilesShareRootScope()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);

            var firstFile = Path.Combine(tempDir, "Api.Part1.cs");
            var secondFile = Path.Combine(tempDir, "Api.Part2.cs");
            File.WriteAllText(firstFile, "public partial class Api {}");
            File.WriteAllText(secondFile, "public partial class Api {}");

            var indexer = new FileIndexer(tempDir);

            Assert.Equal(".", indexer.GetFamilyScopeKey(firstFile, "csharp"));
            Assert.Equal(".", indexer.GetFamilyScopeKey(secondFile, "csharp"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GetFamilyScopeKey_IgnoresIgnoredProjectMarkers()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            var libDir = Path.Combine(tempDir, "src", "Lib");
            var featureDir = Path.Combine(libDir, "Feature");
            Directory.CreateDirectory(featureDir);
            File.WriteAllText(Path.Combine(tempDir, ".gitignore"), "src/Lib/Lib.csproj\n");
            var projectPath = Path.Combine(libDir, "Lib.csproj");
            File.WriteAllText(projectPath, "<Project />");
            var sourcePath = Path.Combine(featureDir, "Api.Part1.cs");
            File.WriteAllText(sourcePath, "public partial class Api {}");

            var indexer = new FileIndexer(tempDir);
            var familyScopeKey = indexer.GetFamilyScopeKey(sourcePath, "csharp");
            var ignoredMarkerFingerprint = indexer.GetProjectMarkerFingerprint("csharp");
            File.Delete(projectPath);
            var markerlessFingerprint = new FileIndexer(tempDir).GetProjectMarkerFingerprint("csharp");

            Assert.Equal("src", familyScopeKey);
            Assert.Equal(markerlessFingerprint, ignoredMarkerFingerprint);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GetFamilyScopeKey_MultipleProjectMarkersInOneDirectoryUseNarrowerSubtreeScope()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            var srcDir = Path.Combine(tempDir, "src");
            Directory.CreateDirectory(Path.Combine(srcDir, "ProjA"));
            Directory.CreateDirectory(Path.Combine(srcDir, "ProjB"));
            File.WriteAllText(Path.Combine(srcDir, "ProjectA.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(srcDir, "ProjectB.csproj"), "<Project />");

            var projAFile = Path.Combine(srcDir, "ProjA", "Api.Part1.cs");
            var projBFile = Path.Combine(srcDir, "ProjB", "Api.Part1.cs");
            var ambiguousFile = Path.Combine(srcDir, "Api.Part1.cs");
            File.WriteAllText(projAFile, "public partial class Api {}");
            File.WriteAllText(projBFile, "public partial class Api {}");
            File.WriteAllText(ambiguousFile, "public partial class Api {}");

            var indexer = new FileIndexer(tempDir);

            Assert.Equal("src/ProjA", indexer.GetFamilyScopeKey(projAFile, "csharp"));
            Assert.Equal("src/ProjB", indexer.GetFamilyScopeKey(projBFile, "csharp"));
            Assert.Equal("src/__file__/Api.Part1.cs", indexer.GetFamilyScopeKey(ambiguousFile, "csharp"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void BuildRecord_CreatesCorrectRecord()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var filePath = Path.Combine(tempDir, "main.py");
            File.WriteAllText(filePath, "def main():\n    print('hello')\n");

            var indexer = new FileIndexer(tempDir);
            var (record, content, _) = indexer.BuildRecord(filePath);

            Assert.Equal("main.py", record.Path);
            Assert.Equal("python", record.Lang);
            Assert.Equal(2, record.Lines); // "def main():\n    print('hello')\n" = 2 lines (trailing newline ignored)
            Assert.NotNull(record.Checksum);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_SkipsCaseInsensitiveDirectories()
    {
        // SkipDirs should be case-insensitive (e.g. "Build" matches "build")
        // SkipDirsは大文字小文字を区別しない（例: "Build"は"build"にマッチ）
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(Path.Combine(tempDir, "app.py"), "print('hello')");

            var buildDir = Path.Combine(tempDir, "Build");
            Directory.CreateDirectory(buildDir);
            File.WriteAllText(Path.Combine(buildDir, "output.js"), "var x = 1;");

            var indexer = new FileIndexer(tempDir);
            var files = indexer.ScanFiles();

            Assert.Single(files);
            Assert.Contains("app.py", files[0]);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFilesDetailed_FileDeletedAfterEnumeration_RecordsWarning()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cdidx-delete-race-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var scriptPath = Path.Combine(tempDir, "script");
            File.WriteAllText(scriptPath, "#!/usr/bin/env python\nprint('hello')\n");

            var indexer = new FileIndexer(
                tempDir,
                ignoreCase: false,
                ignoreRuleRoot: null,
                maxFileSizeBytes: null,
                directoryIgnoreCaseProbe: _ => false,
                enumerateFiles: dir => Path.GetFullPath(dir) == Path.GetFullPath(tempDir)
                    ? DeleteBeforeProbe(scriptPath)
                    : Directory.EnumerateFiles(dir));

            var result = indexer.ScanFilesDetailed();

            Assert.Empty(result.Files);
            Assert.Contains("script", result.NonIndexablePaths);
            var warning = Assert.Single(result.Errors);
            Assert.Equal("script", warning.Path);
            Assert.Equal(FileIndexer.ScanIssueSeverity.Warning, warning.Severity);
            Assert.Contains("deleted during scanning", warning.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(result.HadErrors);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }

        static IEnumerable<string> DeleteBeforeProbe(string path)
        {
            File.Delete(path);
            yield return path;
        }
    }

    [Fact]
    public void ScanFilesDetailed_DanglingDirectorySymlink_RecordsWarningAndCount()
    {
        if (OperatingSystem.IsWindows())
            return;

        var tempDir = Path.Combine(Path.GetTempPath(), $"cdidx-dangling-symlink-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var linkPath = Path.Combine(tempDir, "missing-link");
            Directory.CreateSymbolicLink(linkPath, Path.Combine(tempDir, "missing-target"));

            var result = new FileIndexer(tempDir).ScanFilesDetailed();

            Assert.Contains("missing-link", result.DanglingSymlinks);
            Assert.Contains(
                result.Errors,
                error => error.Path == "missing-link"
                    && error.Severity == FileIndexer.ScanIssueSeverity.Warning
                    && error.Message.Contains("dangling symlink", StringComparison.OrdinalIgnoreCase));
            Assert.False(result.HadErrors);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_FollowSymlinksInternal_SkipsOutOfTreeDirectorySymlink()
    {
        if (OperatingSystem.IsWindows())
            return;

        var tempDir = Path.Combine(Path.GetTempPath(), $"cdidx-symlink-policy-{Guid.NewGuid():N}");
        var externalDir = Path.Combine(Path.GetTempPath(), $"cdidx-symlink-external-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(externalDir);
        try
        {
            File.WriteAllText(Path.Combine(externalDir, "external.py"), "print('external')\n");
            var linkPath = Path.Combine(tempDir, "external");
            Directory.CreateSymbolicLink(linkPath, externalDir);

            var indexer = new FileIndexer(
                tempDir,
                ignoreCase: false,
                ignoreRuleRoot: null,
                maxFileSizeBytes: null,
                directoryIgnoreCaseProbe: null,
                symlinkPolicy: FileIndexer.SymlinkPolicy.Internal);

            var result = indexer.ScanFilesDetailed();

            Assert.Empty(result.Files);
            Assert.Contains(
                result.Errors,
                error => error.Path == "external"
                    && error.Severity == FileIndexer.ScanIssueSeverity.Warning
                    && error.Message.Contains("symlinked directory", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
            if (Directory.Exists(externalDir))
                Directory.Delete(externalDir, true);
        }
    }

    [Fact]
    public void ScanFiles_FollowSymlinksInternal_FollowsInTreeDirectorySymlinkOnce()
    {
        if (OperatingSystem.IsWindows())
            return;

        var tempDir = Path.Combine(Path.GetTempPath(), $"cdidx-symlink-internal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var targetDir = Path.Combine(tempDir, "src");
            Directory.CreateDirectory(targetDir);
            File.WriteAllText(Path.Combine(targetDir, "app.py"), "print('app')\n");
            Directory.CreateSymbolicLink(Path.Combine(tempDir, "src-link"), targetDir);

            var indexer = new FileIndexer(
                tempDir,
                ignoreCase: false,
                ignoreRuleRoot: null,
                maxFileSizeBytes: null,
                directoryIgnoreCaseProbe: null,
                symlinkPolicy: FileIndexer.SymlinkPolicy.Internal);

            var files = indexer.ScanFiles()
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Single(files);
            Assert.Contains(files[0], new[] { "src/app.py", "src-link/app.py" });
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_FollowSymlinksInternal_SkipsCycleToProjectRoot()
    {
        if (OperatingSystem.IsWindows())
            return;

        var tempDir = Path.Combine(Path.GetTempPath(), $"cdidx-symlink-cycle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "app.py"), "print('app')\n");
            Directory.CreateSymbolicLink(Path.Combine(tempDir, "self"), tempDir);

            var indexer = new FileIndexer(
                tempDir,
                ignoreCase: false,
                ignoreRuleRoot: null,
                maxFileSizeBytes: null,
                directoryIgnoreCaseProbe: null,
                symlinkPolicy: FileIndexer.SymlinkPolicy.Internal);

            var result = indexer.ScanFilesDetailed();

            Assert.Single(result.Files);
            Assert.Contains(
                result.Errors,
                error => error.Path == "self"
                    && error.Severity == FileIndexer.ScanIssueSeverity.Warning
                    && error.Message.Contains("already scanned", StringComparison.OrdinalIgnoreCase));
            Assert.False(result.HadErrors);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_DescendsIntoSubmoduleHostedUnderSkipDir()
    {
        // .gitmodules declared submodule under a SkipDirs-named directory (e.g. vendor/foo)
        // must remain visible: SkipDirs is overridden along the path to the submodule, but
        // unrelated files inside the SkipDir ancestor itself stay excluded. Closes #1511.
        // SkipDirs 名のディレクトリ配下に .gitmodules で宣言された submodule（例: vendor/foo）は
        // 可視化される必要がある。SkipDirs は submodule までの経路でのみ上書きされ、SkipDirs
        // 祖先自身の無関係なファイルは引き続き除外される。Closes #1511.
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(Path.Combine(tempDir, "app.py"), "print('hello')");

            // .gitmodules at project root declaring submodule path "vendor/foo"
            File.WriteAllText(
                Path.Combine(tempDir, ".gitmodules"),
                "[submodule \"foo\"]\n\tpath = vendor/foo\n\turl = https://example.invalid/foo.git\n");

            var vendorDir = Path.Combine(tempDir, "vendor");
            Directory.CreateDirectory(vendorDir);
            // File sitting directly in the SkipDir ancestor — must NOT be indexed
            // SkipDirs 祖先直下のファイル — 索引されてはいけない
            File.WriteAllText(Path.Combine(vendorDir, "vendor_dep.py"), "x = 1");

            var submoduleDir = Path.Combine(vendorDir, "foo");
            Directory.CreateDirectory(submoduleDir);
            File.WriteAllText(Path.Combine(submoduleDir, ".git"), "gitdir: ../../.git/modules/foo\n");
            File.WriteAllText(Path.Combine(submoduleDir, "lib.py"), "def f(): pass");
            Directory.CreateDirectory(Path.Combine(submoduleDir, "src"));
            File.WriteAllText(Path.Combine(submoduleDir, "src", "nested.py"), "def g(): pass");

            var indexer = new FileIndexer(tempDir);
            var files = indexer.ScanFiles();

            var rel = files.Select(f => Path.GetRelativePath(tempDir, f).Replace('\\', '/')).ToHashSet();
            Assert.Contains("app.py", rel);
            Assert.Contains("vendor/foo/lib.py", rel);
            Assert.Contains("vendor/foo/src/nested.py", rel);
            Assert.DoesNotContain("vendor/vendor_dep.py", rel);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFilesDetailed_OversizedGitmodulesSkipsSubmodulePassthroughWithWarning()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(Path.Combine(tempDir, "app.py"), "print('hello')");
            File.WriteAllText(Path.Combine(tempDir, ".gitmodules"), new string('x', 300 * 1024));

            var submoduleDir = Path.Combine(tempDir, "vendor", "foo");
            Directory.CreateDirectory(submoduleDir);
            File.WriteAllText(Path.Combine(submoduleDir, ".git"), "gitdir: ../../.git/modules/foo\n");
            File.WriteAllText(Path.Combine(submoduleDir, "lib.py"), "def f(): pass");

            var result = new FileIndexer(tempDir).ScanFilesDetailed();
            var rel = result.Files.Select(f => Path.GetRelativePath(tempDir, f).Replace('\\', '/')).ToHashSet();

            Assert.Contains("app.py", rel);
            Assert.DoesNotContain("vendor/foo/lib.py", rel);
            Assert.Contains(
                result.Errors,
                error => error.Path == ".gitmodules"
                    && error.Severity == FileIndexer.ScanIssueSeverity.Warning
                    && error.Message.Contains("exceeds", StringComparison.OrdinalIgnoreCase));
            Assert.False(result.HadErrors);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void PurgeFilesOutsideRetainedSet_UsesNfcRetainedPaths()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var dbPath = TestProjectHelper.CreateProjectDb(tempDir);
            var nfcPath = "Caf\u00e9.cs";
            var nfdPath = "Cafe\u0301.cs";
            TestProjectHelper.InsertIndexedFile(dbPath, nfcPath, "csharp", "class CafeFixture { }\n");

            using var db = new DbContext(dbPath);
            db.InitializeSchema();
            var writer = new DbWriter(db.Connection);
            var retainedPaths = new[]
                {
                    Path.Combine(tempDir, nfdPath),
                }
                .Select(path => FileIndexer.NormalizeIndexPath(Path.GetRelativePath(tempDir, path)))
                .ToHashSet(StringComparer.Ordinal);

            var purged = writer.PurgeFilesOutsideRetainedSet(retainedPaths);

            Assert.Equal(0, purged);
            Assert.Equal(1, CountFiles(db.Connection));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void PurgeFilesOutsideRetainedSetWithinListedDirectories_UsesNfcPrunedDirectories()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var dbPath = TestProjectHelper.CreateProjectDb(tempDir);
            TestProjectHelper.InsertIndexedFile(dbPath, "Caf\u00e9/src/File.cs", "csharp", "class NestedCafe { }\n");

            using var db = new DbContext(dbPath);
            db.InitializeSchema();
            var writer = new DbWriter(db.Connection);
            var prunedDirectories = new[] { "Cafe\u0301" }
                .Select(FileIndexer.NormalizeIndexPath)
                .ToHashSet(StringComparer.Ordinal);

            var purged = writer.PurgeFilesOutsideRetainedSetWithinListedDirectories(
                new HashSet<string>(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal),
                prunedDirectories);

            Assert.Equal(1, purged);
            Assert.Equal(0, CountFiles(db.Connection));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void IndexFilesUpdate_UsesOriginalUnicodePathForIoAndNfcPathForDb()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var nfdPath = "Cafe\u0301.cs";
            File.WriteAllText(Path.Combine(tempDir, nfdPath), "class FirstCafe { }\n");

            var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            Assert.Equal(CommandExitCodes.Success, IndexCommandRunner.Run([tempDir, "--json", "--quiet"], jsonOptions));

            File.WriteAllText(Path.Combine(tempDir, nfdPath), "class UpdatedCafe { }\n");
            Assert.Equal(CommandExitCodes.Success, IndexCommandRunner.Run([tempDir, "--files", nfdPath, "--json", "--quiet"], jsonOptions));

            var dbPath = Path.Combine(tempDir, ".cdidx", "codeindex.db");
            Assert.Equal("class UpdatedCafe { }", ReadSingleChunkContent(dbPath, "Caf\u00e9.cs"));

            File.WriteAllBytes(Path.Combine(tempDir, nfdPath), [0, 1, 2, 3]);
            Assert.Equal(CommandExitCodes.Success, IndexCommandRunner.Run([tempDir, "--files", nfdPath, "--json", "--quiet"], jsonOptions));
            Assert.False(HasIndexedFile(dbPath, "Caf\u00e9.cs"));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void ScanFiles_RespectsSubmoduleGitignore()
    {
        // Submodules brought back into the scan must still honor their own .gitignore so
        // build artifacts inside the submodule remain excluded.
        // 可視化された submodule も自身の .gitignore を尊重し、submodule 配下のビルド成果物などは
        // 引き続き除外されること。
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(
                Path.Combine(tempDir, ".gitmodules"),
                "[submodule \"foo\"]\n\tpath = vendor/foo\n\turl = https://example.invalid/foo.git\n");

            var submoduleDir = Path.Combine(tempDir, "vendor", "foo");
            Directory.CreateDirectory(submoduleDir);
            File.WriteAllText(Path.Combine(submoduleDir, "lib.py"), "def f(): pass");
            File.WriteAllText(Path.Combine(submoduleDir, ".gitignore"), "generated.py\n");
            File.WriteAllText(Path.Combine(submoduleDir, "generated.py"), "# generated");

            var indexer = new FileIndexer(tempDir);
            var files = indexer.ScanFiles();

            var rel = files.Select(f => Path.GetRelativePath(tempDir, f).Replace('\\', '/')).ToHashSet();
            Assert.Contains("vendor/foo/lib.py", rel);
            Assert.DoesNotContain("vendor/foo/generated.py", rel);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ScanFiles_StillSkipsSkipDirWithoutMatchingSubmodule()
    {
        // .gitmodules declaring a submodule elsewhere must not relax SkipDirs for unrelated
        // SkipDir-named directories. vendor/ without a declared submodule stays skipped.
        // .gitmodules が別の場所の submodule を宣言していても、無関係な SkipDirs 名ディレクトリ
        // (submodule が宣言されていない vendor/ 等) は引き続きスキップされること。
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(
                Path.Combine(tempDir, ".gitmodules"),
                "[submodule \"foo\"]\n\tpath = third_party/foo\n\turl = https://example.invalid/foo.git\n");

            var submoduleDir = Path.Combine(tempDir, "third_party", "foo");
            Directory.CreateDirectory(submoduleDir);
            File.WriteAllText(Path.Combine(submoduleDir, "lib.py"), "def f(): pass");

            var vendorDir = Path.Combine(tempDir, "vendor");
            Directory.CreateDirectory(vendorDir);
            File.WriteAllText(Path.Combine(vendorDir, "dep.py"), "x = 1");

            var indexer = new FileIndexer(tempDir);
            var files = indexer.ScanFiles();

            var rel = files.Select(f => Path.GetRelativePath(tempDir, f).Replace('\\', '/')).ToHashSet();
            Assert.Contains("third_party/foo/lib.py", rel);
            Assert.DoesNotContain("vendor/dep.py", rel);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void EvaluatePathFilter_AllowsFilesUnderSubmoduleHostedInSkipDir()
    {
        // PathFilter must agree with the walker: files under a submodule declared in
        // .gitmodules are not classified as ExcludedByDefaultDirectory even when an
        // ancestor segment matches SkipDirs. This keeps update-mode (--files / --commits)
        // consistent with full scan output.
        // パスフィルタは walker と整合する必要がある: .gitmodules で宣言された submodule
        // 配下のファイルは、祖先が SkipDirs に該当しても ExcludedByDefaultDirectory に
        // 分類されない。これにより --files / --commits のような更新モードでも
        // フルスキャンと挙動が一致する。
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(
                Path.Combine(tempDir, ".gitmodules"),
                "[submodule \"foo\"]\n\tpath = vendor/foo\n");

            var submoduleDir = Path.Combine(tempDir, "vendor", "foo");
            Directory.CreateDirectory(submoduleDir);
            var libPath = Path.Combine(submoduleDir, "lib.py");
            File.WriteAllText(libPath, "def f(): pass");

            var unrelatedPath = Path.Combine(tempDir, "vendor", "dep.py");
            File.WriteAllText(unrelatedPath, "x = 1");

            var indexer = new FileIndexer(tempDir);
            Assert.Equal(FileIndexer.PathFilterKind.None, indexer.EvaluatePathFilter(libPath).FilterKind);
            Assert.Equal(FileIndexer.PathFilterKind.ExcludedByDefaultDirectory, indexer.EvaluatePathFilter(unrelatedPath).FilterKind);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void BuildRecord_CrlfNormalizedToLf()
    {
        // CRLF line endings in files should be normalized to LF
        // ファイル内のCRLF改行はLFに正規化される
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var filePath = Path.Combine(tempDir, "crlf.py");
            File.WriteAllBytes(filePath, System.Text.Encoding.UTF8.GetBytes("line1\r\nline2\r\nline3\r\n"));

            var indexer = new FileIndexer(tempDir);
            var (record, content, _) = indexer.BuildRecord(filePath);

            Assert.DoesNotContain("\r", content);
            Assert.Equal(3, record.Lines);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Theory]
    [InlineData("line1\r\nline2\r\n", "line1\nline2\n")]
    [InlineData("line1\rline2\r", "line1\nline2\n")]
    [InlineData("crlf\r\ncr\rlf\n", "crlf\ncr\nlf\n")]
    [InlineData("line1\nline2\n", "line1\nline2\n")]
    public void NormalizeLineEndings_CollapsesCrVariantsToLf(string input, string expected)
    {
        Assert.Equal(expected, FileIndexer.NormalizeLineEndings(input));
    }

    [Fact]
    public void BuildRecord_LeadingBomStrippedFromContent()
    {
        // Files whose on-disk bytes begin with UTF-8 BOM (EF BB BF) must have the BOM
        // stripped from the decoded content so downstream consumers never see a phantom
        // U+FEFF glyph on line 1. The checksum must reflect the same canonical content
        // stored in chunks so BOM-only changes do not desynchronize line metadata. Closes #183/#1467.
        // オンディスク先頭に UTF-8 BOM (EF BB BF) を持つファイルは、デコード後の content
        // から BOM を剥がし、下流に幽霊 U+FEFF を渡さないようにする。checksum は chunk
        // に保存される canonical content と同じ内容から算出し、行メタデータとのずれを防ぐ。
        // Closes #183/#1467.
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var filePath = Path.Combine(tempDir, "bom.cs");
            var rawBytes = new byte[] { 0xEF, 0xBB, 0xBF }
                .Concat(System.Text.Encoding.UTF8.GetBytes("using System;\nnamespace BomTest;\n"))
                .ToArray();
            File.WriteAllBytes(filePath, rawBytes);

            var indexer = new FileIndexer(tempDir);
            var (record, content, _) = indexer.BuildRecord(filePath);

            Assert.StartsWith("using System;", content);
            // Culture-aware IndexOf treats U+FEFF as ignorable and spuriously matches at pos 0,
            // so assert on the raw code-point instead of the string overload.
            // カルチャ依存の IndexOf は U+FEFF を無視扱いで pos 0 に誤マッチするため、
            // 文字列オーバーロードではなくコードポイントで確認する。
            Assert.DoesNotContain('\uFEFF', content);
            Assert.Equal(2, record.Lines);
            var expectedChecksum = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
            Assert.Equal(expectedChecksum, record.Checksum);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void BuildRecord_Checksum_CrlfAndLfMatch()
    {
        // The same logical content cloned with CRLF line endings (Windows with
        // core.autocrlf=true) and with LF endings (Linux/macOS) must produce the same
        // checksum, so cross-OS clones / shared NAS workspaces do not trip incremental
        // re-index on every file. Standalone CR (legacy Mac classic) must collapse too.
        // Closes #1544.
        // 同じ論理内容を CRLF (Windows core.autocrlf=true) と LF (Linux/macOS) で clone
        // しても checksum が一致する必要がある。さもないと cross-OS clone や共有 NAS で
        // 初回索引時に全ファイルが「変更あり」扱いとなり再索引が走ってしまう。standalone
        // CR (旧 Mac classic) も同様に LF へ畳む。Closes #1544.
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var lfPath = Path.Combine(tempDir, "lf.py");
            var crlfPath = Path.Combine(tempDir, "crlf.py");
            var crPath = Path.Combine(tempDir, "cr.py");
            File.WriteAllBytes(lfPath, System.Text.Encoding.UTF8.GetBytes("line1\nline2\nline3\n"));
            File.WriteAllBytes(crlfPath, System.Text.Encoding.UTF8.GetBytes("line1\r\nline2\r\nline3\r\n"));
            File.WriteAllBytes(crPath, System.Text.Encoding.UTF8.GetBytes("line1\rline2\rline3\r"));

            var indexer = new FileIndexer(tempDir);
            var (lfRecord, _, _) = indexer.BuildRecord(lfPath);
            var (crlfRecord, _, _) = indexer.BuildRecord(crlfPath);
            var (crRecord, _, _) = indexer.BuildRecord(crPath);

            Assert.Equal(lfRecord.Checksum, crlfRecord.Checksum);
            Assert.Equal(lfRecord.Checksum, crRecord.Checksum);
            // Spot-check the expected value: SHA256 of the LF-normalized payload, so a
            // future regression that re-introduces raw-byte hashing fails loudly.
            // 期待値も固定: LF 正規化後 payload の SHA256。生バイトハッシュへ戻ると
            // 落ちるようにしておく。
            var expected = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes("line1\nline2\nline3\n"))).ToLowerInvariant();
            Assert.Equal(expected, lfRecord.Checksum);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void BuildRecord_Checksum_BomAddRemoveUsesSameCanonicalContent()
    {
        // BOM-only edits should hash to the same canonical content that chunking and
        // excerpts see. This prevents freshness checks from accepting line metadata
        // produced from a different byte sequence. Closes #1467.
        // BOM のみの差分は chunk / excerpt が見る canonical content と同じ内容として
        // hash される。別のバイト列から作られた行メタデータを freshness が受け入れる
        // ずれを防ぐ。Closes #1467.
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var bomPath = Path.Combine(tempDir, "bom.cs");
            var noBomPath = Path.Combine(tempDir, "nobom.cs");
            var payload = System.Text.Encoding.UTF8.GetBytes("using System;\n");
            File.WriteAllBytes(bomPath, new byte[] { 0xEF, 0xBB, 0xBF }.Concat(payload).ToArray());
            File.WriteAllBytes(noBomPath, payload);

            var indexer = new FileIndexer(tempDir);
            var (bomRecord, _, _) = indexer.BuildRecord(bomPath);
            var (noBomRecord, _, _) = indexer.BuildRecord(noBomPath);

            Assert.Equal(bomRecord.Checksum, noBomRecord.Checksum);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ComputeChecksum_MixedLineEndings_NormalizesToLf()
    {
        // Direct-call coverage: mixed CRLF / CR / LF lines all collapse to LF before
        // hashing, matching the content-level normalization in BuildRecord. Pinning the
        // helper directly catches regressions even if BuildRecord shape changes.
        // Closes #1544.
        // direct call の網羅: CRLF / CR / LF が混在しても全て LF に畳まれてから
        // ハッシュ化され、BuildRecord 側の content 正規化と一致する。BuildRecord の
        // 形が変わっても helper 単体で回帰を検知できる。Closes #1544.
        var mixed = System.Text.Encoding.UTF8.GetBytes("a\r\nb\rc\nd\r\n");
        var lfOnly = System.Text.Encoding.UTF8.GetBytes("a\nb\nc\nd\n");
        Assert.Equal(
            FileIndexer.ComputeChecksum(lfOnly),
            FileIndexer.ComputeChecksum(mixed));
    }

    [Fact]
    public void ComputeChecksum_LongInputWithoutCr_MatchesRawByteSha256()
    {
        // For CR-free payloads (the common case), the checksum must still equal raw-byte
        // SHA256 — both as a correctness anchor for existing DBs whose stored checksums
        // were computed from raw bytes on LF-only sources, and to confirm the streaming
        // implementation handles inputs that span multiple AppendData chunks. Closes #1544.
        // CR を含まない payload (一般的なケース) では checksum が生バイト SHA256 と
        // 一致する必要がある。これは LF のみのソースで生バイトから算出された既存 DB の
        // checksum との互換性を保ち、また streaming 実装が AppendData の複数チャンクを
        // またぐ入力でも正しく動くことを示す。Closes #1544.
        var payload = new byte[16 * 1024];
        for (int i = 0; i < payload.Length; i++)
            payload[i] = (byte)(i % 95 + 32); // printable ASCII (no CR / LF)
        var expected = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant();
        Assert.Equal(expected, FileIndexer.ComputeChecksum(payload));
    }

    [Fact]
    public void ComputeChecksum_ReturnsLowercaseHex()
    {
        var checksum = FileIndexer.ComputeChecksum(System.Text.Encoding.UTF8.GetBytes("ABC\n"));

        Assert.Equal(checksum.ToLowerInvariant(), checksum);
        Assert.DoesNotContain(checksum, c => c is >= 'A' and <= 'F');
    }

    [Fact]
    public void BuildRecord_BomOnlyFile_ReportsNoCanonicalLines()
    {
        // A file whose on-disk bytes are exactly the UTF-8 BOM (EF BB BF) and
        // nothing else becomes empty canonical content, so the stored line count must
        // match the chunking/extraction input. Closes #1467/#1890.
        // オンディスクバイト列が UTF-8 BOM (EF BB BF) のみのファイルも、正規化後に
        // chunk/extraction へ渡す canonical content が空になるため、保存する行数も
        // その入力と一致させる。Closes #1467/#1890.
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var filePath = Path.Combine(tempDir, "bomonly.cs");
            File.WriteAllBytes(filePath, new byte[] { 0xEF, 0xBB, 0xBF });

            var indexer = new FileIndexer(tempDir);
            var (record, content, _) = indexer.BuildRecord(filePath);

            Assert.Equal(string.Empty, content);
            Assert.Equal(0, record.Lines);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void BuildRecord_MidFileBom_StrippedFromContent()
    {
        // Mid-file UTF-8 BOM (e.g. from accidental file concatenation or tool insertion)
        // must also be stripped from decoded content so `search` / `excerpt` do not emit
        // a phantom glyph. Closes #183.
        // mid-file UTF-8 BOM (ファイル連結やツール挿入) もデコード後の content から
        // 剥がし、search / excerpt に幽霊グリフを漏らさないようにする。Closes #183.
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var filePath = Path.Combine(tempDir, "midbom.cs");
            var rawBytes = System.Text.Encoding.UTF8.GetBytes("using System;\n")
                .Concat(new byte[] { 0xEF, 0xBB, 0xBF })
                .Concat(System.Text.Encoding.UTF8.GetBytes("namespace MidBom;\n"))
                .ToArray();
            File.WriteAllBytes(filePath, rawBytes);

            var indexer = new FileIndexer(tempDir);
            var (_, content, _) = indexer.BuildRecord(filePath);

            Assert.DoesNotContain('\uFEFF', content);
            Assert.Contains("namespace MidBom;", content);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void BuildRecord_Utf16LeBomFile_DecodedAsUtf16()
    {
        // Files written as UTF-16 LE with BOM (FF FE) must be decoded via UTF-16, not
        // through the UTF-8 fallback that mangles every other byte into U+FFFD / NUL.
        // Closes #1540.
        // UTF-16 LE BOM (FF FE) 付きで書かれたソースは UTF-8 fallback ではなく UTF-16 で
        // デコードしなければならない。UTF-8 経路では 1 バイトおきに U+FFFD / NUL に
        // 化けてシンボル抽出が壊れる。Closes #1540.
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var filePath = Path.Combine(tempDir, "utf16le.cs");
            var payload = "using System;\nnamespace Utf16Le;\n";
            var rawBytes = new byte[] { 0xFF, 0xFE }
                .Concat(System.Text.Encoding.Unicode.GetBytes(payload))
                .ToArray();
            File.WriteAllBytes(filePath, rawBytes);

            var indexer = new FileIndexer(tempDir);
            var (_, content, _, warning) = indexer.BuildRecordWithRawBytes(filePath);

            Assert.Null(warning);
            Assert.Contains("namespace Utf16Le;", content);
            Assert.DoesNotContain('�', content);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void BuildRecord_Utf16BeBomFile_DecodedAsUtf16()
    {
        // UTF-16 BE BOM (FE FF) must also be decoded via UTF-16 BE so files authored on
        // big-endian Windows or by legacy tooling keep their symbols intact. Closes #1540.
        // UTF-16 BE BOM (FE FF) も UTF-16 BE でデコードし、ビッグエンディアン Windows
        // やレガシツール由来のソースが壊れないようにする。Closes #1540.
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var filePath = Path.Combine(tempDir, "utf16be.cs");
            var payload = "using System;\nnamespace Utf16Be;\n";
            var rawBytes = new byte[] { 0xFE, 0xFF }
                .Concat(System.Text.Encoding.BigEndianUnicode.GetBytes(payload))
                .ToArray();
            File.WriteAllBytes(filePath, rawBytes);

            var indexer = new FileIndexer(tempDir);
            var (_, content, _, warning) = indexer.BuildRecordWithRawBytes(filePath);

            Assert.Null(warning);
            Assert.Contains("namespace Utf16Be;", content);
            Assert.DoesNotContain('�', content);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void BuildRecord_Utf16LeWithoutBomFile_DecodedAsUtf16()
    {
        // Legacy Windows tools can save source files as UTF-16 LE without a BOM. The
        // regular every-other-byte NUL pattern should be treated as an encoding signal,
        // not as binary content. Closes #1829.
        // 古い Windows ツールは BOM なし UTF-16 LE でソースを保存することがある。
        // 1 バイトおきの NUL パターンはバイナリ混入ではなくエンコーディングのシグナルとして扱う。
        // Closes #1829.
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var filePath = Path.Combine(tempDir, "utf16le-nobom.cs");
            var payload = "using System;\nnamespace Utf16LeNoBom;\n";
            File.WriteAllBytes(filePath, System.Text.Encoding.Unicode.GetBytes(payload));

            var indexer = new FileIndexer(tempDir);
            var (_, content, _, warning) = indexer.BuildRecordWithRawBytes(filePath);

            Assert.Null(warning);
            Assert.Contains("namespace Utf16LeNoBom;", content);
            Assert.False(FileIndexer.ContainsIndexBlockingNullByte(System.Text.Encoding.Unicode.GetBytes(payload)));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void BuildRecord_Utf16BeWithoutBomFile_DecodedAsUtf16()
    {
        // The heuristic must also handle BOM-less UTF-16 BE text so the fix is not tied
        // to little-endian Windows output only. Closes #1829.
        // BOM なし UTF-16 BE テキストも扱い、little-endian Windows 出力だけに限定しない。
        // Closes #1829.
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var filePath = Path.Combine(tempDir, "utf16be-nobom.cs");
            var payload = "using System;\nnamespace Utf16BeNoBom;\n";
            File.WriteAllBytes(filePath, System.Text.Encoding.BigEndianUnicode.GetBytes(payload));

            var indexer = new FileIndexer(tempDir);
            var (_, content, _, warning) = indexer.BuildRecordWithRawBytes(filePath);

            Assert.Null(warning);
            Assert.Contains("namespace Utf16BeNoBom;", content);
            Assert.False(FileIndexer.ContainsIndexBlockingNullByte(System.Text.Encoding.BigEndianUnicode.GetBytes(payload)));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ValidateContent_Utf16LeBomFile_EmitsUtf16BomNotRawByteIssues()
    {
        // When a file decodes via UTF-16 LE, the raw bytes are full of NULs (every ASCII
        // codepoint) and the CRLF heuristic sees 0D 00 0A 00. ValidateContent must skip the
        // `bom` / `null_byte` / `mixed_line_endings` paths and emit a single `utf16_bom`
        // issue instead. Closes #1540.
        // UTF-16 LE デコード経路では生バイト列に大量の NUL が並び、CRLF 判定は 0D 00 0A 00
        // を見て誤検出する。ValidateContent は `bom` / `null_byte` / `mixed_line_endings`
        // を出さず `utf16_bom` 1 件に集約する。Closes #1540.
        var payload = "using System;\nclass C { }\n";
        var rawBytes = new byte[] { 0xFF, 0xFE }
            .Concat(System.Text.Encoding.Unicode.GetBytes(payload))
            .ToArray();
        // Simulate the content that BuildRecordWithRawBytes would produce.
        var content = payload;

        var issues = FileIndexer.ValidateContent("utf16le.cs", rawBytes, content);

        Assert.Contains(issues, i => i.Kind == "utf16_bom");
        Assert.DoesNotContain(issues, i => i.Kind == "bom");
        Assert.DoesNotContain(issues, i => i.Kind == "null_byte");
        Assert.DoesNotContain(issues, i => i.Kind == "mixed_line_endings");
        Assert.DoesNotContain(issues, i => i.Kind == "replacement_char");
        Assert.DoesNotContain(issues, i => i.Kind == "non_utf8_likely");
    }

    [Fact]
    public void ValidateContent_Utf16WithoutBom_DoesNotEmitNullByteIssue()
    {
        var payload = "using System;\nclass C { }\n";
        var rawBytes = System.Text.Encoding.Unicode.GetBytes(payload);

        var issues = FileIndexer.ValidateContent("utf16le-nobom.cs", rawBytes, payload);

        Assert.DoesNotContain(issues, i => i.Kind == "utf16_bom");
        Assert.DoesNotContain(issues, i => i.Kind == "null_byte");
        Assert.DoesNotContain(issues, i => i.Kind == "mixed_line_endings");
    }

    [Fact]
    public void ValidateContent_HighFffdRatio_EmitsAggregateNonUtf8Likely()
    {
        // A file decoded with many U+FFFD characters (mojibake from SHIFT_JIS / GBK / Latin-1
        // misread as UTF-8) must collapse to one `non_utf8_likely` aggregate issue, not
        // hundreds of per-line `replacement_char` issues that drown the diagnostic. Closes #1540.
        // SHIFT_JIS / GBK / ISO-8859-1 を UTF-8 で読んで化けた content は per-line
        // `replacement_char` で埋め尽くすのではなく `non_utf8_likely` 1 件に集約する。
        // Closes #1540.
        // Build invalid UTF-8 bytes that decode to > 1% U+FFFD ratio and many lines.
        var raw = new List<byte>();
        for (int i = 0; i < 50; i++)
        {
            raw.AddRange(System.Text.Encoding.UTF8.GetBytes("alpha "));
            raw.Add(0xFF);
            raw.AddRange(System.Text.Encoding.UTF8.GetBytes(" beta\n"));
        }
        var rawBytes = raw.ToArray();
        var content = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: false).GetString(rawBytes);

        var issues = FileIndexer.ValidateContent("garbled.cs", rawBytes, content);

        var issue = Assert.Single(issues.Where(i => i.Kind == "non_utf8_likely"));
        Assert.Equal(FileIssue.OriginDecodeReplacement, issue.Origin);
        Assert.Equal(FileIssue.SeverityWarning, issue.Severity);
        // Per-line replacement_char emission must be suppressed when the aggregate fires.
        // アグリゲートが出た場合は per-line replacement_char を抑止する。
        Assert.DoesNotContain(issues, i => i.Kind == "replacement_char");
    }

    [Fact]
    public void ValidateContent_LowFffdRatio_KeepsPerLineReplacementCharIssues()
    {
        // Below the aggregate threshold (a few stray U+FFFD in an otherwise clean file),
        // the existing per-line `replacement_char` issues must still fire so genuine point
        // defects (one stray byte in an otherwise-UTF-8 file) remain actionable. Closes #1540.
        // 集約しきい値未満 (大半が正しく UTF-8 で書かれた中に数文字だけ U+FFFD が残る)
        // の場合は従来の per-line `replacement_char` を出し続け、点の不具合を見逃さない。
        // Closes #1540.
        // 4 U+FFFD chars in a long file → far below 1% ratio AND below the minimum-count
        // floor of 5, so the aggregate must not fire.
        var raw = new List<byte>();
        void AddUtf8(string text) => raw.AddRange(System.Text.Encoding.UTF8.GetBytes(text));

        AddUtf8("line1 clean\n");
        AddUtf8("line2 has ");
        raw.Add(0xFF);
        AddUtf8(" here\n");
        AddUtf8("line3 has ");
        raw.Add(0xFF);
        AddUtf8(" here\n");
        for (int i = 0; i < 200; i++) AddUtf8("filler ascii ascii ascii\n");
        AddUtf8("trailing ");
        raw.Add(0xFF);
        AddUtf8("\n");
        AddUtf8("another ");
        raw.Add(0xFF);
        AddUtf8("\n");
        var rawBytes = raw.ToArray();
        var content = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: false).GetString(rawBytes);

        var issues = FileIndexer.ValidateContent("partial.cs", rawBytes, content);

        Assert.DoesNotContain(issues, i => i.Kind == "non_utf8_likely");
        Assert.Contains(issues, i =>
            i.Kind == "replacement_char"
            && i.Origin == FileIssue.OriginDecodeReplacement
            && i.Severity == FileIssue.SeverityWarning);
    }

    [Fact]
    public void ValidateContent_SourceLiteralFffd_AnnotatesInfoOrigin()
    {
        var content = "line1 clean\nline2 has \uFFFD literal\n";
        var rawBytes = System.Text.Encoding.UTF8.GetBytes(content);

        var issues = FileIndexer.ValidateContent("literal.cs", rawBytes, content);

        var issue = Assert.Single(issues.Where(i => i.Kind == "replacement_char"));
        Assert.Equal(2, issue.Line);
        Assert.Equal(FileIssue.OriginSourceLiteral, issue.Origin);
        Assert.Equal(FileIssue.SeverityInfo, issue.Severity);
        Assert.Contains("source literal", issue.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(issues, i => i.Kind == "non_utf8_likely");
    }

    [Fact]
    public void ValidateContent_Utf32LePrefix_NotMisclassifiedAsUtf16()
    {
        // UTF-32 LE shares the first two bytes with UTF-16 LE (FF FE 00 00). The detector
        // must exclude this prefix so a UTF-32 LE file does not get tagged with `utf16_bom`
        // and skip the raw-byte heuristics that would otherwise catch its NUL pattern.
        // Closes #1540.
        // UTF-32 LE は UTF-16 LE と先頭 2 バイトを共有する (FF FE 00 00)。この prefix を
        // 検出器から除外し、UTF-32 LE を `utf16_bom` と誤判定して NUL バイトの生バイト
        // ヒューリスティクスを飛ばさないようにする。Closes #1540.
        var rawBytes = new byte[] { 0xFF, 0xFE, 0x00, 0x00, 0x41, 0x00, 0x00, 0x00 };
        // The content passed in does not matter much — what matters is that the validator
        // does not emit `utf16_bom`. We pass an ASCII placeholder.
        var content = "A";

        var issues = FileIndexer.ValidateContent("utf32le.txt", rawBytes, content);

        Assert.DoesNotContain(issues, i => i.Kind == "utf16_bom");
    }

    [Fact]
    public void StripLineLeadingInvisibles_InvisibleFreeContent_ReturnsSameInstance()
    {
        // Invisible-free content (the dominant case) must hit the fast path and
        // return the same string instance, asserting no StringBuilder is
        // allocated. Closes #1495/#2117.
        // 対象の不可視文字が無いファイル (支配的ケース) は高速パスで同じ string インスタンスを
        // 返し、StringBuilder を割り当てないことを保証する。Closes #1495/#2117.
        var input = "using System;\nnamespace Plain;\nclass C { }\n";
        var output = FileIndexer.StripLineLeadingInvisibles(input);
        Assert.Same(input, output);
    }

    [Fact]
    public void StripLineLeadingInvisibles_MidLineInvisiblesOnly_ReturnsSameInstance()
    {
        // Content that carries U+FEFF/U+200B only mid-line must also hit the
        // no-allocation path and return the same instance, so the no-op case
        // never pays the StringBuilder cost. Closes #1495/#2117.
        // 行頭以外にのみ U+FEFF/U+200B を含むファイルも割り当て無しのパスを通り、
        // 同じインスタンスを返すことを保証する。Closes #1495/#2117.
        var input = "var s = \"A\uFEFFB\";\nvar t = \"A\u200BB\";\n";
        var output = FileIndexer.StripLineLeadingInvisibles(input);
        Assert.Same(input, output);
        // Mid-line invisibles stay verbatim so the source-of-truth payload is
        // not silently corrupted for code that embeds ZWNBSP intentionally.
        // 行頭以外の不可視文字はそのまま保持し、意図的に ZWNBSP を埋め込んだ
        // コードの payload を破壊しないことを併せて確認する。
        Assert.Contains('\uFEFF', output);
        Assert.Contains('\u200B', output);
    }

    [Fact]
    public void StripLineLeadingInvisibles_EmptyContent_ReturnsSameInstance()
    {
        // Empty input must short-circuit before any scan or allocation.
        // 空入力は走査・割り当ての前に短絡することを保証する。
        Assert.Same(string.Empty, FileIndexer.StripLineLeadingInvisibles(string.Empty));
    }

    [Fact]
    public void StripLineLeadingInvisibles_LineLeadingInvisibles_StrippedWhileMidLineInvisiblesPreserved()
    {
        // File-leading and post-newline U+FEFF/U+200B markers are stripped, while
        // mid-line markers inside a literal are preserved verbatim.
        // 先頭および `\n` 直後の U+FEFF/U+200B は剥がし、行内の marker は
        // そのまま保持することを確認する。
        var input = "\uFEFFline1\n\u200Bline2 has \"A\uFEFFB\"\n\uFEFF\u200Bline3 has \"A\u200BB\"\n";
        var output = FileIndexer.StripLineLeadingInvisibles(input);

        Assert.Equal("line1\nline2 has \"A\uFEFFB\"\nline3 has \"A\u200BB\"\n", output);
    }

    [Fact]
    public void StripLineLeadingInvisibles_ConsecutiveLineLeadingInvisibles_AllStripped()
    {
        // Multiple invisible markers sharing the same logical line-start must all
        // be stripped, preserving the invariant that skipping a marker does not
        // reset `atLineStart`.
        // 同じ論理行頭に重なる連続 marker は全て剥がす。「marker スキップで
        // atLineStart を更新しない」契約を保つ。
        var input = "\uFEFF\u200Bhello\n\u200B\uFEFFworld\n";
        var output = FileIndexer.StripLineLeadingInvisibles(input);

        Assert.Equal("hello\nworld\n", output);
    }

    [Fact]
    public void BuildRecord_ChecksumUsesCanonicalContentAfterLineLeadingBomStrip()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var plainPath = Path.Combine(tempDir, "plain.cs");
            var bomPath = Path.Combine(tempDir, "bom.cs");
            File.WriteAllText(plainPath, "class Plain\n{\n}\n");
            File.WriteAllText(bomPath, "\uFEFFclass Plain\n\uFEFF{\n}\n");

            var indexer = new FileIndexer(tempDir);
            var (plainRecord, plainContent, _) = indexer.BuildRecord(plainPath);
            var (bomRecord, bomContent, _) = indexer.BuildRecord(bomPath);

            Assert.Equal(plainContent, bomContent);
            Assert.Equal(plainRecord.Checksum, bomRecord.Checksum);
            Assert.Equal(plainRecord.Lines, bomRecord.Lines);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void BuildRecord_GitLfsPointerIndexesEmptyBodyAndValidationIssue()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var filePath = Path.Combine(tempDir, "asset.cs");
            var pointer = """
                version https://git-lfs.github.com/spec/v1
                oid sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
                size 12345
                """;
            File.WriteAllText(filePath, pointer);

            var indexer = new FileIndexer(tempDir);
            var (record, content, rawBytes, _) = indexer.BuildRecordWithRawBytes(filePath);
            var issues = FileIndexer.ValidateContent(record.Path, rawBytes, content);

            Assert.Equal(string.Empty, content);
            Assert.Equal(0, record.Lines);
            var issue = Assert.Single(issues, i => i.Kind == "lfs_pointer_skipped");
            Assert.Equal("asset.cs", issue.Path);
            Assert.Equal(1, issue.Line);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void BuildRecord_GitLfsVersionLineWithoutPointerShapePreservesContent()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var filePath = Path.Combine(tempDir, "example.txt");
            var text = """
                version https://git-lfs.github.com/spec/v1
                This is documentation text, not a Git LFS pointer.
                """;
            File.WriteAllText(filePath, text);

            var indexer = new FileIndexer(tempDir);
            var (record, content, rawBytes, _) = indexer.BuildRecordWithRawBytes(filePath);
            var issues = FileIndexer.ValidateContent(record.Path, rawBytes, content);

            Assert.Equal(text.Replace("\r\n", "\n", StringComparison.Ordinal), content);
            Assert.DoesNotContain(issues, issue => issue.Kind == "lfs_pointer_skipped");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void BuildRecord_ThrowsFileTooLargeSkippedExceptionForOversizedFile()
    {
        // Files exceeding the default cap should carry structured skip metadata.
        // 既定上限を超えるファイルは structured skip metadata を持つ例外を投げる。
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var filePath = Path.Combine(tempDir, "large.py");
            // Create a sparse file just over the default cap without allocating a matching test buffer.
            // 既定上限を少し超える sparse file を作り、同サイズのテスト用 buffer 確保を避ける。
            using (var stream = File.Create(filePath))
                stream.SetLength(FileIndexer.DefaultMaxFileSizeBytes + 1);

            var indexer = new FileIndexer(tempDir);
            var ex = Assert.Throws<FileIndexer.FileTooLargeSkippedException>(() => indexer.BuildRecord(filePath));
            Assert.Equal("large.py", ex.RelativePath);
            Assert.Equal(FileIndexer.DefaultMaxFileSizeBytes + 1, ex.ActualBytes);
            Assert.Equal(FileIndexer.DefaultMaxFileSizeBytes, ex.LimitBytes);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void BuildRecord_DefaultRejectsTenMiBFileBeforeReadingPayload()
    {
        // Regression for #1695: a 10 MiB source file must be rejected from the
        // observed stream length before the indexer accumulates one contiguous
        // 10 MiB byte array on the LOH.
        // #1695 の回帰: 10 MiB の source file は stream length の確認時点で拒否し、
        // インデクサが LOH 上に連続した 10 MiB byte 配列を累積しないことを固定する。
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var filePath = Path.Combine(tempDir, "large.py");
            using (var stream = File.Create(filePath))
                stream.SetLength(10 * 1024 * 1024);

            var indexer = new FileIndexer(tempDir);
            var before = GC.GetAllocatedBytesForCurrentThread();

            var ex = Assert.Throws<FileIndexer.FileTooLargeSkippedException>(() => indexer.BuildRecord(filePath));

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Contains("File too large", ex.Message);
            Assert.True(allocated < 1024 * 1024, $"Expected rejection before a 10 MiB payload allocation, saw {allocated} bytes allocated.");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void BuildRecord_AcceptsFileAtSizeLimitBoundary()
    {
        // Regression for #1529: the TOCTOU fix reads through one FileStream and caps the
        // accumulator at MaxFileSize. A file at exactly the default cap must still be accepted so
        // the boundary contract documented by the oversize test stays symmetric (>cap
        // throws, ==cap succeeds).
        // #1529 のリグレッション: TOCTOU 修正で 1 本の FileStream を通して MaxFileSize で
        // 累積バッファを打ち切る実装にした際、ちょうど既定上限のファイルは引き続き受け
        // 入れる必要がある (>上限 が throw / ==上限 が成功という対称契約を維持)。
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var filePath = Path.Combine(tempDir, "boundary.py");
            // Exactly MaxFileSize bytes — ASCII so UTF-8 decode succeeds without warning.
            // ちょうど MaxFileSize バイト — ASCII なら UTF-8 デコードで警告無く成功する。
            var data = new byte[(int)FileIndexer.DefaultMaxFileSizeBytes];
            for (int i = 0; i < data.Length; i++)
                data[i] = (byte)'a';
            File.WriteAllBytes(filePath, data);

            var indexer = new FileIndexer(tempDir);
            var (record, content, _) = indexer.BuildRecord(filePath);

            Assert.Equal(data.Length, record.Size);
            Assert.Equal(data.Length, content.Length);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void BuildRecord_RecordSizeReflectsBytesActuallyRead()
    {
        // Regression for #1529: with the FileStream-based read path, record.Size must
        // come from the bytes streamed through the open handle rather than from a
        // separate FileInfo.Length stat. Asserting record.Size against the byte count
        // documents the contract that downstream consumers (status, freshness checks)
        // see the same value the indexer actually ingested.
        // #1529 のリグレッション: FileStream ベースの読み込み経路では record.Size は
        // 別途取得した FileInfo.Length ではなく、オープンしたハンドル経由で実際に読み
        // 込んだバイト数を反映しなければならない。`record.Size` をバイト数と突き合わ
        // せることで、status や freshness check の下流が実際に取り込まれた値と一致
        // することを契約として固定する。
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var filePath = Path.Combine(tempDir, "sized.py");
            var payload = "print('hello world')\n"u8.ToArray();
            File.WriteAllBytes(filePath, payload);

            var indexer = new FileIndexer(tempDir);
            var (record, _, _) = indexer.BuildRecord(filePath);

            Assert.Equal(payload.Length, record.Size);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void BuildRecord_ExtensionlessShebangScriptUsesDetectedLanguage()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var filePath = Path.Combine(tempDir, "rbenv-hooks");
            File.WriteAllText(filePath, "#!/usr/bin/env bash\necho hooks\n");

            var indexer = new FileIndexer(tempDir);
            var (record, _, warning) = indexer.BuildRecord(filePath);

            Assert.Equal("shell", record.Lang);
            Assert.Null(warning);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    private static string CreateWindowsLongPathFixture(string projectRoot)
    {
        var current = Path.Combine(
            projectRoot,
            ".pnpm",
            "fixture-pkg@1.0.0",
            "fixture-pkg");
        var segment = 0;

        while (Path.Combine(current, "long-file.js").Length < 260)
            current = Path.Combine(current, $"segment{segment++:D2}");

        Directory.CreateDirectory(LongPath.EnsureWindowsPrefix(current));
        var leafPath = Path.Combine(current, "long-file.js");
        File.WriteAllText(LongPath.EnsureWindowsPrefix(leafPath), "export function longPathFixture() { return 42; }\n");
        return leafPath;
    }

    private static void DeleteLongPathDirectory(string path)
    {
        if (!Directory.Exists(LongPath.EnsureWindowsPrefix(path)))
            return;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                DeleteLongPathDirectoryRecursive(path);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                Thread.Sleep(100);
            }
        }
    }

    private static void DeleteLongPathDirectoryRecursive(string path)
    {
        var prefixedPath = LongPath.EnsureWindowsPrefix(path);
        File.SetAttributes(prefixedPath, FileAttributes.Normal);

        foreach (var file in Directory.EnumerateFiles(prefixedPath))
        {
            var filePath = LongPath.RemoveWindowsPrefix(file);
            var prefixedFilePath = LongPath.EnsureWindowsPrefix(filePath);
            File.SetAttributes(prefixedFilePath, FileAttributes.Normal);
            File.Delete(prefixedFilePath);
        }

        foreach (var dir in Directory.EnumerateDirectories(prefixedPath))
            DeleteLongPathDirectoryRecursive(LongPath.RemoveWindowsPrefix(dir));

        Directory.Delete(prefixedPath);
    }

    private static void IndexScannedFiles(string projectRoot, DbWriter writer)
    {
        var indexer = new FileIndexer(projectRoot);
        foreach (var filePath in indexer.ScanFiles())
        {
            var (record, content, rawBytes, _) = indexer.BuildRecordWithRawBytes(filePath);
            var fileId = writer.UpsertFile(record);
            writer.DeleteFileData(fileId);
            writer.InsertChunks(ChunkSplitter.Split(fileId, content));
            var symbols = SymbolExtractor.Extract(fileId, record.Lang, content, record.Path);
            writer.InsertSymbols(symbols);
            writer.InsertReferences(ReferenceExtractor.Extract(fileId, record.Lang, content, symbols, record.Path));
            writer.InsertIssues(fileId, FileIndexer.ValidateContent(record.Path, rawBytes, content));
        }
    }

    private static bool IndexedFileExists(DbContext db, string relativePath)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM files WHERE path = @path";
        cmd.Parameters.AddWithValue("@path", relativePath);
        return Convert.ToInt32(cmd.ExecuteScalar()) == 1;
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void CreateUnixFifo(string path)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "mkfifo",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(path);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start mkfifo / mkfifo の起動に失敗");
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"mkfifo failed: {stderr.Trim()}");
    }

    private static void CreateHardLink(string existingPath, string newPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ln",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(existingPath);
        psi.ArgumentList.Add(newPath);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ln / ln の起動に失敗");
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ln failed: {stderr.Trim()}");
    }

    private static string RunGit(string workDir, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process / gitプロセスの起動に失敗");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr.Trim()}");

        return stdout;
    }

    [UnsupportedOSPlatform("windows")]
    private static void SetUnixPermissions(string path, UnixFileMode mode)
    {
        File.SetUnixFileMode(path, mode);
    }

    // Bare CR (legacy Mac) line endings used to be silently normalized to LF by
    // BuildRecordWithRawBytes, hiding line-counting / regex assumptions that
    // may be wrong elsewhere. Issue #1538: detect CR-only and three-way mixes
    // so they surface in `cdidx validate` / file_issues.
    // BuildRecordWithRawBytes が CR (旧 Mac) 行末を黙って LF に正規化していた問題
    // (Issue #1538) に対し、CR-only と 3 種混在を検出して file_issues に出す。
    [Fact]
    public void ValidateContent_CrOnlyLineEndings_EmitsCrOnlyIssue()
    {
        var rawBytes = System.Text.Encoding.UTF8.GetBytes("line1\rline2\rline3\r");
        var content = "line1\nline2\nline3\n";

        var issues = FileIndexer.ValidateContent("legacy_mac.txt", rawBytes, content);

        var crOnly = Assert.Single(issues, i => i.Kind == "cr_only_line_endings");
        Assert.Equal(0, crOnly.Line);
        Assert.Contains("CR-only", crOnly.Message);
        Assert.DoesNotContain(issues, i => i.Kind == "mixed_line_endings");
        Assert.DoesNotContain(issues, i => i.Kind == "mixed_line_endings_three_way");
    }

    [Fact]
    public void ValidateContent_ThreeWayLineEndings_EmitsThreeWayIssue()
    {
        var rawBytes = System.Text.Encoding.UTF8.GetBytes("crlf\r\nlf-only\rcr-only\n");
        var content = "crlf\nlf-only\ncr-only\n";

        var issues = FileIndexer.ValidateContent("three_way.txt", rawBytes, content);

        var threeWay = Assert.Single(issues, i => i.Kind == "mixed_line_endings_three_way");
        Assert.Equal(0, threeWay.Line);
        Assert.Contains("CRLF", threeWay.Message);
        Assert.Contains("LF", threeWay.Message);
        Assert.Contains("CR", threeWay.Message);
        Assert.DoesNotContain(issues, i => i.Kind == "mixed_line_endings");
        Assert.DoesNotContain(issues, i => i.Kind == "cr_only_line_endings");
    }

    [Fact]
    public void ValidateContent_CrlfPlusCrOnly_EmitsMixedIssue()
    {
        var rawBytes = System.Text.Encoding.UTF8.GetBytes("crlf\r\ncr-only\rmore-crlf\r\n");
        var content = "crlf\ncr-only\nmore-crlf\n";

        var issues = FileIndexer.ValidateContent("mixed_crlf_cr.txt", rawBytes, content);

        var mixed = Assert.Single(issues, i => i.Kind == "mixed_line_endings");
        Assert.Equal(0, mixed.Line);
        Assert.Contains("CRLF and CR", mixed.Message);
        Assert.DoesNotContain(issues, i => i.Kind == "cr_only_line_endings");
        Assert.DoesNotContain(issues, i => i.Kind == "mixed_line_endings_three_way");
    }

    [Fact]
    public void ValidateContent_LfPlusCrOnly_EmitsMixedIssue()
    {
        var rawBytes = System.Text.Encoding.UTF8.GetBytes("lf\nthen-cr\rback-to-lf\n");
        var content = "lf\nthen-cr\nback-to-lf\n";

        var issues = FileIndexer.ValidateContent("mixed_lf_cr.txt", rawBytes, content);

        var mixed = Assert.Single(issues, i => i.Kind == "mixed_line_endings");
        Assert.Equal(0, mixed.Line);
        Assert.Contains("LF and CR", mixed.Message);
        Assert.DoesNotContain(issues, i => i.Kind == "cr_only_line_endings");
    }

    [Fact]
    public void ValidateContent_CrlfPlusLf_StillEmitsExistingMixedIssue()
    {
        // Regression guard: existing CRLF+LF kind / message must not change.
        // 既存の CRLF+LF kind / メッセージが変わっていないことの回帰ガード。
        var rawBytes = System.Text.Encoding.UTF8.GetBytes("crlf\r\nlf\n");
        var content = "crlf\nlf\n";

        var issues = FileIndexer.ValidateContent("mixed.txt", rawBytes, content);

        var mixed = Assert.Single(issues, i => i.Kind == "mixed_line_endings");
        Assert.Equal("Mixed line endings (CRLF and LF)", mixed.Message);
        Assert.DoesNotContain(issues, i => i.Kind == "cr_only_line_endings");
        Assert.DoesNotContain(issues, i => i.Kind == "mixed_line_endings_three_way");
    }

    [Fact]
    public void ValidateContent_PureCrlf_DoesNotFlagLineEndings()
    {
        var rawBytes = System.Text.Encoding.UTF8.GetBytes("a\r\nb\r\nc\r\n");
        var content = "a\nb\nc\n";

        var issues = FileIndexer.ValidateContent("pure_crlf.txt", rawBytes, content);

        Assert.DoesNotContain(issues, i => i.Kind == "mixed_line_endings");
        Assert.DoesNotContain(issues, i => i.Kind == "mixed_line_endings_three_way");
        Assert.DoesNotContain(issues, i => i.Kind == "cr_only_line_endings");
    }

    [Fact]
    public void ValidateContent_PureLf_DoesNotFlagLineEndings()
    {
        var rawBytes = System.Text.Encoding.UTF8.GetBytes("a\nb\nc\n");
        var content = "a\nb\nc\n";

        var issues = FileIndexer.ValidateContent("pure_lf.txt", rawBytes, content);

        Assert.DoesNotContain(issues, i => i.Kind == "mixed_line_endings");
        Assert.DoesNotContain(issues, i => i.Kind == "mixed_line_endings_three_way");
        Assert.DoesNotContain(issues, i => i.Kind == "cr_only_line_endings");
    }

    [Fact]
    public void ValidateContent_ConflictMarkers_EmitsConflictMarkerIssue()
    {
        var content = """
        class Example
        {
        <<<<<<< HEAD
            void MainVersion() {}
        =======
            void BranchVersion() {}
        >>>>>>> feature
        }
        """;
        var raw = System.Text.Encoding.UTF8.GetBytes(content);

        var issues = FileIndexer.ValidateContent("Example.cs", raw, content);

        var conflictMarkers = Assert.Single(issues, i => i.Kind == "conflict_markers");
        Assert.Equal(3, conflictMarkers.Line);
        Assert.Contains("Git conflict markers", conflictMarkers.Message);
    }

    [Fact]
    public void Extractors_ConflictMarkers_ReturnEmptySymbolsAndReferences()
    {
        var content = """
        class Example
        {
        <<<<<<< HEAD
            void MainVersion() {}
        =======
            void BranchVersion() {}
        >>>>>>> feature
        }
        """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content, "Example.cs");
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols, "Example.cs");

        Assert.Empty(symbols);
        Assert.Empty(references);
    }

    [Fact]
    public void ValidateContent_SetextHeadingSeparator_DoesNotEmitConflictMarkerIssue()
    {
        var content = "Title\n=======\n";
        var raw = System.Text.Encoding.UTF8.GetBytes(content);

        var issues = FileIndexer.ValidateContent("README.md", raw, content);

        Assert.DoesNotContain(issues, i => i.Kind == "conflict_markers");
    }

    [Fact]
    public void ValidateContent_OversizeLine_EmitsLineTooLongIssue()
    {
        // A single physical line longer than ChunkSplitter.MaxLineLength (e.g.
        // 1 MB minified `.min.js`) must surface as a `line_too_long` FileIssue
        // pointing at the offending 1-based line number, so the chunk / symbol /
        // reference skip path is observable from the existing issues channel.
        // Closes #1542.
        // ChunkSplitter.MaxLineLength を超える単一物理行 (例: 1 MB minified
        // .min.js) は、対象行を 1-based 行番号で指す `line_too_long` FileIssue
        // として表面化させ、chunk / symbol / reference スキップ経路を既存の
        // issues 経路から観測できるようにする。Closes #1542.
        var oversize = new string('a', ChunkSplitter.MaxLineLength + 1);
        var content = "ok\n" + oversize + "\nok\n";
        var raw = System.Text.Encoding.UTF8.GetBytes(content);

        var issues = FileIndexer.ValidateContent("bundle.min.js", raw, content);

        var lineTooLong = Assert.Single(issues, i => i.Kind == "line_too_long");
        Assert.Equal(2, lineTooLong.Line);
        Assert.Contains("exceeds", lineTooLong.Message);
    }

    [Fact]
    public void ValidateContent_NoOversizeLine_DoesNotEmitLineTooLongIssue()
    {
        // Files whose every physical line stays within the cap must not be
        // flagged, even when the total content is large. The cap is per
        // physical line, not per file. Closes #1542.
        // すべての物理行が上限以内なら、ファイル全体のサイズが大きくても
        // フラグは立たない。上限は物理行ごとに適用される。Closes #1542.
        var line = new string('a', 1024);
        var content = string.Join('\n', Enumerable.Repeat(line, 200));
        var raw = System.Text.Encoding.UTF8.GetBytes(content);

        var issues = FileIndexer.ValidateContent("ok.js", raw, content);

        Assert.DoesNotContain(issues, i => i.Kind == "line_too_long");
    }

    [Fact]
    public void ValidateContent_OversizeFtsToken_EmitsFtsTokenTooLongIssue()
    {
        var token = new string('x', CodeIndex.Database.DbReader.FtsUnicode61MaxTokenLength + 1);
        var content = "ok\nconst value = " + token + ";\n";
        var raw = System.Text.Encoding.UTF8.GetBytes(content);

        var issues = FileIndexer.ValidateContent("generated.js", raw, content);

        var issue = Assert.Single(issues, i => i.Kind == "fts_token_too_long");
        Assert.Equal(2, issue.Line);
        Assert.Contains("not searchable through FTS", issue.Message);
    }

    [Fact]
    public void SymbolExtractor_Extract_OversizeLine_ReturnsEmpty()
    {
        // SymbolExtractor must mirror the ChunkSplitter oversize-line skip so
        // regex-based symbol extraction does not stall on minified payloads.
        // The content below would otherwise expose dozens of `function`
        // signatures to the JavaScript symbol pattern loop. Closes #1542.
        // SymbolExtractor も ChunkSplitter の oversize-line スキップに揃え、
        // 正規表現ベースのシンボル抽出が minified ペイロードで停止しないよう
        // にする。下記の内容は通常なら JavaScript シンボルパターンで
        // 多数の `function` シグネチャを露出させる。Closes #1542.
        var oversize = string.Concat(Enumerable.Repeat("function f(){}", ChunkSplitter.MaxLineLength / 14 + 1));
        var symbols = SymbolExtractor.Extract(fileId: 1, lang: "javascript", content: oversize, filePath: "bundle.min.js");
        Assert.Empty(symbols);
    }

    [Fact]
    public void ReferenceExtractor_Extract_OversizeLine_ReturnsEmpty()
    {
        // ReferenceExtractor must mirror the ChunkSplitter oversize-line skip
        // so regex-based reference extraction does not stall on minified
        // payloads. Closes #1542.
        // ReferenceExtractor も ChunkSplitter の oversize-line スキップに揃え、
        // 正規表現ベースの参照抽出が minified ペイロードで停止しないように
        // する。Closes #1542.
        var oversize = string.Concat(Enumerable.Repeat("foo();bar();", ChunkSplitter.MaxLineLength / 12 + 1));
        var refs = ReferenceExtractor.Extract(fileId: 1, lang: "javascript", content: oversize, symbols: Array.Empty<CodeIndex.Models.SymbolRecord>(), path: "bundle.min.js");
        Assert.Empty(refs);
    }

    [Theory]
    [InlineData("Foo.Designer.cs", "class Foo { }")]
    [InlineData("foo_pb.go", "package foo")]
    [InlineData("foo_pb2.py", "class Foo: pass")]
    [InlineData("model.g.dart", "class Model {}")]
    [InlineData("api.generated.ts", "export const x = 1;")]
    [InlineData("handwritten.cs", "// <auto-generated>\nclass Foo { }")]
    [InlineData("handwritten.go", "// Code generated by protoc. DO NOT EDIT.\npackage foo")]
    [InlineData("handwritten.py", "# @generated\nclass Foo: pass")]
    public void IsGeneratedCodeFile_MarkersAndNames_ReturnsTrue(string path, string content)
    {
        Assert.True(FileIndexer.IsGeneratedCodeFile(path, content));
    }

    [Fact]
    public void IsGeneratedCodeFile_HandwrittenFile_ReturnsFalse()
    {
        Assert.False(FileIndexer.IsGeneratedCodeFile("src/Foo.cs", "class Foo { }\n"));
        Assert.False(FileIndexer.IsGeneratedCodeFile("src/Foo.cs", "// This file is not auto-generated.\nclass Foo { }\n"));
    }

    private static void WriteFileIndexerPatternConfig(string projectRoot, string fileName, string content)
    {
        var path = Path.Combine(projectRoot, ".cdidx", "patterns", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static int CountFiles(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM files";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static string ReadSingleChunkContent(string dbPath, string filePath)
    {
        using var db = new DbContext(dbPath);
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT c.content
            FROM chunks c
            JOIN files f ON f.id = c.file_id
            WHERE f.path = @path
            ORDER BY c.chunk_index
            """;
        cmd.Parameters.AddWithValue("@path", filePath);
        return Assert.IsType<string>(cmd.ExecuteScalar());
    }

    private static bool HasIndexedFile(string dbPath, string filePath)
    {
        using var db = new DbContext(dbPath);
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM files WHERE path = @path";
        cmd.Parameters.AddWithValue("@path", filePath);
        return cmd.ExecuteScalar() != null;
    }
}
