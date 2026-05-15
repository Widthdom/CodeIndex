using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using CodeIndex.Cli;
using CodeIndex.Indexer;

namespace CodeIndex.Tests;

/// <summary>
/// Tests for FileIndexer.
/// FileIndexerのテスト。
/// </summary>
public class FileIndexerTests
{
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
                ["Gemfile"]         = "source 'https://rubygems.org'\ngem 'rails', '~> 7.0'\n",
                ["Rakefile"]        = "task :default => [:test]\n",
                ["Containerfile"]   = "FROM alpine\nRUN echo hi\n",
                ["Dockerfile.dev"]  = "FROM alpine AS builder\nRUN echo dev\n",
                ["GNUmakefile"]     = "all:\n\techo hi\n",
                ["common.mk"]       = "OBJ = foo.o bar.o\n",
                ["stub.pyi"]        = "def foo() -> int: ...\n",
                ["style.less"]      = ".foo { color: red; }\n",
                ["page.htm"]        = "<html><body>old-school</body></html>\n",
                ["Makefile.am"]     = "SUBDIRS = lib\n",
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
    public void ScanFiles_FailsClosedWhenRootIgnoreFileIsUnreadable()
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

            Assert.Empty(result.Files);
            Assert.Contains(result.Errors, error => error.Path == ".gitignore" && error.Message == "Could not read .gitignore.");
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
    public void ScanFiles_FailsClosedWhenNestedIgnoreFileIsUnreadable()
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

            Assert.Equal(["keep.py"], files);
            Assert.Contains(result.Errors, error => error.Path == "src/.gitignore" && error.Message == "Could not read .gitignore.");
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
            Assert.All(scanResult.Errors, error => Assert.Equal(FileIndexer.ScanIssueSeverity.Warning, error.Severity));
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
            CreateUnixFifo(Path.Combine(tempDir, "tool"));
            CreateUnixFifo(Path.Combine(tempDir, "tool.sh"));
            CreateUnixFifo(Path.Combine(tempDir, "Dockerfile"));

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

    [Fact]
    public void BuildRecord_LeadingBomStrippedFromContent()
    {
        // Files whose on-disk bytes begin with UTF-8 BOM (EF BB BF) must have the BOM
        // stripped from the decoded content so downstream consumers never see a phantom
        // U+FEFF glyph on line 1. The raw-byte checksum must still reflect the on-disk
        // file (BOM included) so incremental change detection keeps working. Closes #183.
        // オンディスク先頭に UTF-8 BOM (EF BB BF) を持つファイルは、デコード後の content
        // から BOM を剥がし、下流に幽霊 U+FEFF を渡さないようにする。checksum は生バイト
        // ベース（BOM を含む）のまま維持し、インクリメンタル更新判定が壊れないようにする。Closes #183.
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
            // Pin the backward-compatibility requirement: checksum must be SHA256 over the
            // raw on-disk bytes (BOM included) so that adding or removing a BOM is detected
            // as a file change by incremental re-indexing. Closes #183.
            // 後方互換性の要件を固定: checksum は生のオンディスクバイト（BOM 含む）の
            // SHA256 で、BOM の追加 / 削除をインクリメンタル再索引で検知できること。Closes #183.
            var expectedChecksum = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(rawBytes)).ToLowerInvariant();
            Assert.Equal(expectedChecksum, record.Checksum);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void BuildRecord_BomOnlyFile_ReportsZeroLines()
    {
        // A file whose on-disk bytes are exactly the UTF-8 BOM (EF BB BF) and
        // nothing else must report `Lines == 0` so `files --json` stays consistent
        // with ChunkSplitter.Split's 0-chunk contract for the same content. Before
        // the fix the line count came from `"".Split('\n') == [""]`, yielding
        // a phantom `Lines = 1`. Closes #183.
        // オンディスクバイト列が UTF-8 BOM (EF BB BF) のみのファイルは Lines == 0 と
        // すべき。そうしないと `files --json` が同じ内容に対する ChunkSplitter.Split の
        // 0 チャンク契約と矛盾する。修正前は `"".Split('\n') == [""]` 由来で
        // 幽霊の Lines = 1 を返していた。Closes #183.
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
    public void StripLineLeadingBom_BomFreeContent_ReturnsSameInstance()
    {
        // BOM-free content (the dominant case) must hit the fast path and
        // return the same string instance, asserting no StringBuilder is
        // allocated. Closes #1495.
        // BOM が無いファイル (支配的ケース) は高速パスで同じ string インスタンスを
        // 返し、StringBuilder を割り当てないことを保証する。Closes #1495.
        var input = "using System;\nnamespace Plain;\nclass C { }\n";
        var output = FileIndexer.StripLineLeadingBom(input);
        Assert.Same(input, output);
    }

    [Fact]
    public void StripLineLeadingBom_MidLineBomOnly_ReturnsSameInstance()
    {
        // Content that carries U+FEFF only mid-line (intentional ZWNBSP in a
        // string literal, identifier, or comment — never line-leading) must
        // also hit the no-allocation path and return the same instance, so
        // the no-op case never pays the StringBuilder cost. Closes #1495.
        // 行頭以外にのみ U+FEFF を含むファイル (文字列リテラル内の意図的な ZWNBSP 等)
        // も割り当て無しのパスを通り、同じインスタンスを返すことを保証する。Closes #1495.
        var input = "var s = \"A\uFEFFB\";\nvar t = \"\uFEFF\";\n";
        var output = FileIndexer.StripLineLeadingBom(input);
        Assert.Same(input, output);
        // Mid-line U+FEFF stays verbatim so the source-of-truth payload is
        // not silently corrupted for code that embeds ZWNBSP intentionally.
        // 行頭以外の U+FEFF はそのまま保持し、意図的に ZWNBSP を埋め込んだ
        // コードの payload を破壊しないことを併せて確認する。
        Assert.Contains('\uFEFF', output);
    }

    [Fact]
    public void StripLineLeadingBom_EmptyContent_ReturnsSameInstance()
    {
        // Empty input must short-circuit before any scan or allocation.
        // 空入力は走査・割り当ての前に短絡することを保証する。
        Assert.Same(string.Empty, FileIndexer.StripLineLeadingBom(string.Empty));
    }

    [Fact]
    public void StripLineLeadingBom_LineLeadingBoms_StrippedWhileMidLineBomPreserved()
    {
        // File-leading and post-newline BOMs are stripped, while mid-line
        // U+FEFF inside a literal is preserved verbatim. This pins the
        // narrowed #183 contract through the new deferred-allocation path.
        // 先頭 BOM および `\n` 直後の BOM は剥がし、行内の U+FEFF は
        // そのまま保持することを確認する。#183 で狭められた契約を新パスで再固定。
        var input = "\uFEFFline1\n\uFEFFline2 has \"A\uFEFFB\"\nline3\n";
        var output = FileIndexer.StripLineLeadingBom(input);

        Assert.Equal("line1\nline2 has \"A\uFEFFB\"\nline3\n", output);
    }

    [Fact]
    public void StripLineLeadingBom_ConsecutiveLineLeadingBoms_AllStripped()
    {
        // Multiple BOMs sharing the same logical line-start (e.g. a doubled
        // BOM at offset 0 from accidental tool concatenation) must all be
        // stripped, matching the original foreach loop's invariant that
        // skipping a BOM does not reset `atLineStart`.
        // 同じ論理行頭に重なる連続 BOM (オフセット 0 の二重 BOM 等) は全て
        // 剥がす。元実装の「BOM スキップで atLineStart を更新しない」契約を保つ。
        var input = "\uFEFF\uFEFFhello\n\uFEFF\uFEFFworld\n";
        var output = FileIndexer.StripLineLeadingBom(input);

        Assert.Equal("hello\nworld\n", output);
    }

    [Fact]
    public void BuildRecord_ThrowsForOversizedFile()
    {
        // Files exceeding 10 MB should throw InvalidOperationException
        // 10MBを超えるファイルはInvalidOperationExceptionを投げる
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var filePath = Path.Combine(tempDir, "large.py");
            // Create a file just over 10 MB / 10MBを少し超えるファイルを作成
            var data = new byte[10 * 1024 * 1024 + 1];
            File.WriteAllBytes(filePath, data);

            var indexer = new FileIndexer(tempDir);
            Assert.Throws<InvalidOperationException>(() => indexer.BuildRecord(filePath));
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
        // accumulator at MaxFileSize. A file at exactly 10 MB must still be accepted so
        // the boundary contract documented by the oversize test stays symmetric (>10MB
        // throws, ==10MB succeeds).
        // #1529 のリグレッション: TOCTOU 修正で 1 本の FileStream を通して MaxFileSize で
        // 累積バッファを打ち切る実装にした際、ちょうど 10 MB のファイルは引き続き受け
        // 入れる必要がある (>10MB が throw / ==10MB が成功という対称契約を維持)。
        var tempDir = Path.Combine(Path.GetTempPath(), $"codeindex_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var filePath = Path.Combine(tempDir, "boundary.py");
            // Exactly MaxFileSize bytes — ASCII so UTF-8 decode succeeds without warning.
            // ちょうど MaxFileSize バイト — ASCII なら UTF-8 デコードで警告無く成功する。
            var data = new byte[10 * 1024 * 1024];
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
}
