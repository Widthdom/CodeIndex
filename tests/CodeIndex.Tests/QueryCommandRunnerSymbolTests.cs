using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void RunSymbols_ExactNameFindsPythonDottedImportPrefixes()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_python_dotted_import_prefix");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.py",
                "python",
                """
                import package.submodule as alias
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["package", "--db", dbPath, "--lang", "python", "--exact-name", "--count"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("1", stdout.Trim());
            Assert.Equal(string.Empty, stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_ExactNameFindsPythonFromImportQualifiedNames()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_python_from_import_qualified");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.py",
                "python",
                """
                from package import submodule as alias
                from .helpers import build
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["package.submodule", "--db", dbPath, "--lang", "python", "--exact-name", "--count"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("1", stdout.Trim());
            Assert.Equal(string.Empty, stderr);

            var (relativeExitCode, relativeStdout, relativeStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["helpers.build", "--db", dbPath, "--lang", "python", "--exact-name", "--count"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, relativeExitCode);
            Assert.Equal("1", relativeStdout.Trim());
            Assert.Equal(string.Empty, relativeStderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_ExactNameFindsPythonQualifiedInitAllExports()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_python_qualified_init_all_exports");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "package/subpkg/__init__.py",
                "python",
                """
                __all__ = [
                    "submodule",
                ]
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["package.subpkg.submodule", "--db", dbPath, "--lang", "python", "--exact-name", "--count"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("1", stdout.Trim());
            Assert.Equal(string.Empty, stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_ExactNameFindsWrappedXamlTypeBearingAttributes()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_xaml_wrapped_type_bearing");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "GenericPage.xaml"),
                """
                <Window
                    x:Class=
                        "Sample.MainWindow"
                    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:vm="clr-namespace:Sample.ViewModels">
                    <DataTemplate
                        x:DataType=
                            "vm:PersonViewModel">
                        <Style
                            TargetType=
                                "{x:Type vm:CustomButton}" />
                    </DataTemplate>
                </Window>
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["vm:PersonViewModel", "--db", dbPath, "--json", "--exact-name", "--lang", "xml"],
                _jsonOptions));

            var rows = ParseJsonLines(stdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Single(rows);
            Assert.Equal("vm:PersonViewModel", rows[0].RootElement.GetProperty("name").GetString());
            Assert.Equal("class", rows[0].RootElement.GetProperty("kind").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_ExactNameFindsXamlTypeObjectElements()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_xaml_type_object_elements");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "GenericPage.xaml"),
                """
                <ResourceDictionary
                    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:vm="clr-namespace:Sample.ViewModels">
                    <DataTemplate.DataType>
                        <x:Type TypeName=
                            "vm:PersonViewModel" />
                    </DataTemplate.DataType>
                    <Style.TargetType>
                        <x:TypeExtension TypeName=
                            "{x:Type vm:CustomButton}" />
                    </Style.TargetType>
                </ResourceDictionary>
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["vm:PersonViewModel", "--db", dbPath, "--json", "--exact-name", "--lang", "xml"],
                _jsonOptions));

            var rows = ParseJsonLines(stdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Single(rows);
            Assert.Equal("vm:PersonViewModel", rows[0].RootElement.GetProperty("name").GetString());
            Assert.Equal("class", rows[0].RootElement.GetProperty("kind").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_ExactNameFindsXamlTypePropertyElements()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_xaml_type_property_elements");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "GenericPage.xaml"),
                """
                <ResourceDictionary
                    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:vm="clr-namespace:Sample.ViewModels">
                    <x:Type.TypeName>
                        vm:PersonViewModel
                    </x:Type.TypeName>
                    <x:TypeExtension.TypeName>
                        {x:Type vm:CustomButton}
                    </x:TypeExtension.TypeName>
                </ResourceDictionary>
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["vm:PersonViewModel", "--db", dbPath, "--json", "--exact-name", "--lang", "xml"],
                _jsonOptions));

            var rows = ParseJsonLines(stdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Single(rows);
            Assert.Equal("vm:PersonViewModel", rows[0].RootElement.GetProperty("name").GetString());
            Assert.Equal("class", rows[0].RootElement.GetProperty("kind").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_ExactNameFindsXamlTypeMarkupExtensions()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_xaml_type_markup_extensions");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "GenericPage.xaml"),
                """
                <ContentPage xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                             xmlns:vm="clr-namespace:Sample.ViewModels">
                    <ContentPage.Resources>
                        <ControlTemplate TargetType="{x:Type vm:PersonViewModel}" />
                        <TextBlock ToolTip="{x:TypeExtension TypeName=vm:CustomButton}" />
                    </ContentPage.Resources>
                </ContentPage>
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["vm:CustomButton", "--db", dbPath, "--json", "--exact-name", "--lang", "xml"],
                _jsonOptions));

            var rows = ParseJsonLines(stdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Single(rows);
            Assert.Equal("vm:CustomButton", rows[0].RootElement.GetProperty("name").GetString());
            Assert.Equal("class", rows[0].RootElement.GetProperty("kind").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_ExactNameFindsXamlStaticMemberTypeReferences()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_xaml_static_member_types");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "GenericPage.xaml"),
                """
                <ResourceDictionary
                    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:local="clr-namespace:Sample.ViewModels">
                    <SolidColorBrush x:Key="{x:Static local:Keys.AccentBrush}" Color="Tomato" />
                    <TextBlock Text="{x:Static local:App.DisplayName}" />
                    <Style x:Key="{x:Static Member={x:Type local:Keys}.PrimaryStyleKey}">
                        <Setter Property="Background" Value="Tomato" />
                    </Style>
                </ResourceDictionary>
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["local:App", "--db", dbPath, "--json", "--exact-name", "--lang", "xml"],
                _jsonOptions));

            var rows = ParseJsonLines(stdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Single(rows);
            Assert.Equal("local:App", rows[0].RootElement.GetProperty("name").GetString());
            Assert.Equal("class", rows[0].RootElement.GetProperty("kind").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_ExactNameFindsPythonInitModuleAliases()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_python_init_module_aliases");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "package/subpkg/__init__.py",
                "python",
                """
                import submodule as module_alias
                import package.submodule as external_alias
                from . import helper as alias
                """);

            var (moduleExitCode, moduleStdout, moduleStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["package.subpkg.module_alias", "--db", dbPath, "--lang", "python", "--exact-name", "--count"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, moduleExitCode);
            Assert.Equal("1", moduleStdout.Trim());
            Assert.Equal(string.Empty, moduleStderr);

            var (moduleNameExitCode, moduleNameStdout, moduleNameStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["package.subpkg.submodule", "--db", dbPath, "--lang", "python", "--exact-name", "--count"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, moduleNameExitCode);
            Assert.Equal("1", moduleNameStdout.Trim());
            Assert.Equal(string.Empty, moduleNameStderr);

            var (externalExitCode, externalStdout, externalStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["package.submodule", "--db", dbPath, "--lang", "python", "--exact-name", "--count"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, externalExitCode);
            Assert.Equal("1", externalStdout.Trim());
            Assert.Equal(string.Empty, externalStderr);

            var (noisyExitCode, noisyStdout, noisyStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["package.subpkg.package.submodule", "--db", dbPath, "--lang", "python", "--exact-name", "--count"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, noisyExitCode);
            Assert.Equal("0", noisyStdout.Trim());
            Assert.Equal(string.Empty, noisyStderr);

            var (aliasExitCode, aliasStdout, aliasStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["package.subpkg.alias", "--db", dbPath, "--lang", "python", "--exact-name", "--count"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, aliasExitCode);
            Assert.Equal("1", aliasStdout.Trim());
            Assert.Equal(string.Empty, aliasStderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    // Issue #1507: command-specific value-taking flags should also surface the per-flag hint, so
    // commands that wire through TryReadStringOptionValue / TryReadRawOptionValue stay consistent
    // with the search-side coverage.
    // Issue #1507: コマンド固有の値取りフラグも同じテーブル経由でヒントを表示する。
    [Fact]
    public void RunSymbols_MissingNameValueShowsPerFlagHint_Issue1507()
    {
        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(["--name"], _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("Error: --name requires a value.", stderr);
        Assert.Contains("Hint: pass a literal symbol name", stderr);
        Assert.Contains("--name UserService", stderr);
    }

    [Fact]
    public void RunDefinition_MissingKindValueShowsPerFlagHint_Issue1507()
    {
        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunDefinition(["QueryCommandRunner", "--kind"], _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("Error: --kind requires a value.", stderr);
        Assert.Contains("Hint: pass a kind identifier", stderr);
        Assert.Contains("--kind function", stderr);
    }

    [Fact]
    public void BuildSymbolQueryList_TreatsPipeAsLiteralNameCharacter()
    {
        // `|` is a legitimate character in operator symbols (C# `operator |`, etc.); it must not
        // be treated as OR syntax so those names stay searchable.
        // `|` は `operator |` など演算子名に出現する有効な文字。OR 構文として分割してはならない。
        var options = QueryCommandRunner.ParseArgs(["|"], jsonDefault: false);
        var (queries, hadInput) = QueryCommandRunner.BuildSymbolQueryList(options);
        Assert.True(hadInput);
        Assert.NotNull(queries);
        Assert.Equal(new[] { "|" }, queries!);

        var compound = QueryCommandRunner.ParseArgs(["operator|"], jsonDefault: false);
        var (compoundQueries, _) = QueryCommandRunner.BuildSymbolQueryList(compound);
        Assert.Equal(new[] { "operator|" }, compoundQueries!);
    }

    [Fact]
    public void BuildSymbolQueryList_EmptyNameNowFailsAtParseTime()
    {
        // Empty inline/separated string values are now rejected during argument parsing before
        // symbol-query normalization runs, so they cannot broaden into an all-symbols dump.
        // 空文字の値は symbol-query 正規化まで進む前に引数解析で拒否される。
        var rejected = QueryCommandRunner.ParseArgs(["--name", ""], jsonDefault: false);
        Assert.NotNull(rejected.ParseError);
        Assert.Contains("--name requires a value", rejected.ParseError);
    }

    [Fact]
    public void RunSymbols_EmptyAfterNormalizationFailsClosed()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_empty_norm");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var (exit, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(["--name", "", "--db", dbPath], _jsonOptions));
            Assert.Equal(1, exit);
            Assert.Contains("--name requires a value", stderr);

            var (definitionExit, definitionStdout, definitionStderr) = CaptureConsole(() => QueryCommandRunner.RunDefinition(
                ["@", "--db", dbPath, "--count", "--json"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, definitionExit);
            Assert.Contains("bare verbatim prefixes", definitionStderr);
            Assert.Equal(string.Empty, definitionStdout);

            var (referencesExit, referencesStdout, referencesStderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["@", "--db", dbPath, "--count", "--json"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, referencesExit);
            Assert.Contains("bare verbatim prefixes", referencesStderr);
            Assert.Equal(string.Empty, referencesStdout);

            var (callersExit, callersStdout, callersStderr) = CaptureConsole(() => QueryCommandRunner.RunCallers(
                ["@", "--db", dbPath, "--count", "--json"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, callersExit);
            Assert.Contains("bare verbatim prefixes", callersStderr);
            Assert.Equal(string.Empty, callersStdout);

            var (calleesExit, calleesStdout, calleesStderr) = CaptureConsole(() => QueryCommandRunner.RunCallees(
                ["@", "--db", dbPath, "--count", "--json"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, calleesExit);
            Assert.Contains("bare verbatim prefixes", calleesStderr);
            Assert.Equal(string.Empty, calleesStdout);

            var (impactExit, impactStdout, impactStderr) = CaptureConsole(() => QueryCommandRunner.RunImpact(
                ["@", "--db", dbPath, "--count", "--json"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, impactExit);
            Assert.Contains("bare verbatim prefixes", impactStderr);
            Assert.Equal(string.Empty, impactStdout);

            var (inspectExit, inspectStdout, inspectStderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["@", "--db", dbPath, "--json"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, inspectExit);
            Assert.Contains("bare verbatim prefixes", inspectStderr);
            Assert.Equal(string.Empty, inspectStdout);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_ExactNameFindsXamlTargetType()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_xaml_target_type");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "MainPage.xaml"),
                """
                <ContentPage xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                             xmlns:vm="clr-namespace:Sample.ViewModels">
                    <ContentPage.Resources>
                        <Style TargetType="Button" />
                        <ControlTemplate TargetType="{x:Type vm:CustomButton}" />
                    </ContentPage.Resources>
                </ContentPage>
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var (buttonExitCode, buttonStdout, buttonStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["Button", "--db", dbPath, "--json", "--exact-name", "--lang", "xml"],
                _jsonOptions));
            var (customButtonExitCode, customButtonStdout, customButtonStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["vm:CustomButton", "--db", dbPath, "--json", "--exact-name", "--lang", "xml"],
                _jsonOptions));

            var buttonRows = ParseJsonLines(buttonStdout);
            var customButtonRows = ParseJsonLines(customButtonStdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, buttonExitCode);
            Assert.Equal(string.Empty, buttonStderr);
            Assert.Single(buttonRows);
            Assert.Equal("Button", buttonRows[0].RootElement.GetProperty("name").GetString());
            Assert.Equal("class", buttonRows[0].RootElement.GetProperty("kind").GetString());
            Assert.Equal(CommandExitCodes.Success, customButtonExitCode);
            Assert.Equal(string.Empty, customButtonStderr);
            Assert.Single(customButtonRows);
            Assert.Equal("vm:CustomButton", customButtonRows[0].RootElement.GetProperty("name").GetString());
            Assert.Equal("class", customButtonRows[0].RootElement.GetProperty("kind").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_RejectsOversizedMultiNameBatches()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_oversize");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var names = Enumerable.Range(0, QueryCommandRunner.MaxSymbolQueryNames + 5)
                .Select(i => $"Name{i}")
                .ToArray();
            var argv = names.Concat(new[] { "--db", dbPath }).ToArray();
            int exit;
            string stderr;
            (exit, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(argv, _jsonOptions));
            Assert.Equal(1, exit);
            Assert.Contains("too many symbol names", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_JsonZeroResults_ReturnEmptyStdout()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_zero_json");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["MissingSymbol", "--db", dbPath, "--json"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Equal(string.Empty, stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunUnused_WithJsonIncludesConfidenceBuckets()
    {
        var (projectRoot, dbPath) = CreateUnusedFixtureDb();
        try
        {
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunUnused(
                ["--db", dbPath, "--json", "--lang", "csharp"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            var symbols = json.GetProperty("symbols");

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(9, json.GetProperty("count").GetInt32());
            Assert.Equal(1, json.GetProperty("returned_bucket_counts").GetProperty("likely_unused_private").GetInt32());
            Assert.Equal(1, json.GetProperty("returned_bucket_counts").GetProperty("maybe_unused_nonpublic").GetInt32());
            Assert.Equal(6, json.GetProperty("returned_bucket_counts").GetProperty("public_or_exported_no_refs").GetInt32());
            Assert.Equal(1, json.GetProperty("returned_bucket_counts").GetProperty("reflection_or_config_suspect").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("by_bucket").GetProperty("likely_unused_private").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("by_confidence").GetProperty("medium").GetInt32());
            Assert.Equal("medium", json.GetProperty("bucket_taxonomy").GetProperty("likely_unused_private").GetProperty("confidence").GetString());
            Assert.Contains("external API", json.GetProperty("bucket_taxonomy").GetProperty("public_or_exported_no_refs").GetProperty("description").GetString());
            Assert.Equal("Hidden", symbols[0].GetProperty("name").GetString());
            Assert.Equal("likely_unused_private", symbols[0].GetProperty("unused_bucket").GetString());
            Assert.Equal("medium", symbols[0].GetProperty("unused_confidence").GetString());
            Assert.Equal("PathResolver", symbols[2].GetProperty("name").GetString());
            Assert.Equal("public_or_exported_no_refs", symbols[2].GetProperty("unused_bucket").GetString());
            Assert.Equal("ConnectionString", symbols[3].GetProperty("name").GetString());
            Assert.Equal("reflection_or_config_suspect", symbols[3].GetProperty("unused_bucket").GetString());
            Assert.Equal("ApplyConfiguration", symbols[7].GetProperty("name").GetString());
            Assert.Equal("public_or_exported_no_refs", symbols[7].GetProperty("unused_bucket").GetString());
            Assert.Equal("UseIOptions", symbols[8].GetProperty("name").GetString());
            Assert.Equal("public_or_exported_no_refs", symbols[8].GetProperty("unused_bucket").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunUnused_WithJsonByBucketGroupsReturnedSymbolsByTaxonomyBucket()
    {
        var (projectRoot, dbPath) = CreateUnusedFixtureDb();
        try
        {
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunUnused(
                ["--db", dbPath, "--json", "--lang", "csharp", "--by-bucket"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            var byBucket = json.GetProperty("by_bucket");

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("Hidden", byBucket.GetProperty("likely_unused_private")[0].GetProperty("name").GetString());
            Assert.Equal("InternalOnly", byBucket.GetProperty("maybe_unused_nonpublic")[0].GetProperty("name").GetString());
            Assert.Equal(6, byBucket.GetProperty("public_or_exported_no_refs").GetArrayLength());
            Assert.Equal("ConnectionString", byBucket.GetProperty("reflection_or_config_suspect")[0].GetProperty("name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunUnused_WithJsonBucketFilterReturnsOnlyRequestedBucket()
    {
        var (projectRoot, dbPath) = CreateUnusedFixtureDb();
        try
        {
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunUnused(
                ["--db", dbPath, "--json", "--lang", "csharp", "--bucket", "likely_unused_private"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            var symbols = json.GetProperty("symbols");

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(1, json.GetProperty("count").GetInt32());
            Assert.Equal(1, json.GetProperty("returned_bucket_counts").GetProperty("likely_unused_private").GetInt32());
            Assert.False(json.GetProperty("returned_bucket_counts").TryGetProperty("maybe_unused_nonpublic", out _));
            Assert.Equal("Hidden", symbols[0].GetProperty("name").GetString());
            Assert.Equal("likely_unused_private", symbols[0].GetProperty("unused_bucket").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunUnused_WithJsonMinConfidenceFiltersLowerConfidenceBuckets()
    {
        var (projectRoot, dbPath) = CreateUnusedFixtureDb();
        try
        {
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunUnused(
                ["--db", dbPath, "--json", "--lang", "csharp", "--min-confidence", "medium"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(1, json.GetProperty("count").GetInt32());
            Assert.Equal("Hidden", json.GetProperty("symbols")[0].GetProperty("name").GetString());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("by_confidence").GetProperty("medium").GetInt32());
            Assert.False(json.GetProperty("summary").GetProperty("by_confidence").TryGetProperty("low", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunUnused_CountJsonWithBucketFilterCountsFilteredSymbols()
    {
        var (projectRoot, dbPath) = CreateUnusedFixtureDb();
        try
        {
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunUnused(
                ["--db", dbPath, "--json", "--lang", "csharp", "--bucket", "public_or_exported_no_refs", "--count"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(6, json.GetProperty("count").GetInt32());
            Assert.Equal(1, json.GetProperty("files").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("--bucket", "missing_bucket", "invalid --bucket value")]
    [InlineData("--min-confidence", "high", "invalid --min-confidence value")]
    public void RunUnused_InvalidBucketOrConfidenceFails(string optionName, string value, string expectedError)
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunUnused(
            [optionName, value],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.InvalidArgument, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains(expectedError, stderr);
        Assert.Contains($"Usage: {ConsoleUi.GetUsageLine("unused")}", stderr);
    }

    [Fact]
    public void RunUnused_WithJsonUsesReturnedBucketCountsForCurrentPage()
    {
        var (projectRoot, dbPath) = CreateUnusedFixtureDb();
        try
        {
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunUnused(
                ["--db", dbPath, "--json", "--lang", "csharp", "--limit", "2"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(2, json.GetProperty("count").GetInt32());
            Assert.True(json.TryGetProperty("returned_bucket_counts", out var returnedBucketCounts));
            Assert.False(json.TryGetProperty("bucket_counts", out _));
            Assert.Equal(1, returnedBucketCounts.GetProperty("likely_unused_private").GetInt32());
            Assert.Equal(1, returnedBucketCounts.GetProperty("maybe_unused_nonpublic").GetInt32());
            Assert.False(returnedBucketCounts.TryGetProperty("public_or_exported_no_refs", out _));
            Assert.False(returnedBucketCounts.TryGetProperty("reflection_or_config_suspect", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunUnused_WithJsonDiversifiesBucketsBeforeLimit()
    {
        var (projectRoot, dbPath) = CreateUnusedFixtureDb();
        try
        {
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunUnused(
                ["--db", dbPath, "--json", "--lang", "csharp", "--limit", "4"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            var symbols = json.GetProperty("symbols");

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(["Hidden", "InternalOnly", "PathResolver", "ConnectionString"], symbols.EnumerateArray().Select(symbol => symbol.GetProperty("name").GetString()).ToArray());
            Assert.Equal(1, json.GetProperty("returned_bucket_counts").GetProperty("likely_unused_private").GetInt32());
            Assert.Equal(1, json.GetProperty("returned_bucket_counts").GetProperty("maybe_unused_nonpublic").GetInt32());
            Assert.Equal(1, json.GetProperty("returned_bucket_counts").GetProperty("public_or_exported_no_refs").GetInt32());
            Assert.Equal(1, json.GetProperty("returned_bucket_counts").GetProperty("reflection_or_config_suspect").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunUnused_WithJsonMarksReflectionAttributedPropertyAsSuspect()
    {
        var (projectRoot, dbPath) = CreateReflectionUnusedFixtureDb();
        try
        {
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunUnused(
                ["--db", dbPath, "--json", "--lang", "csharp"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            var symbols = json.GetProperty("symbols");

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("UserDto", symbols[0].GetProperty("name").GetString());
            Assert.Equal("public_or_exported_no_refs", symbols[0].GetProperty("unused_bucket").GetString());
            Assert.Equal("FullName", symbols[1].GetProperty("name").GetString());
            Assert.Equal("reflection_or_config_suspect", symbols[1].GetProperty("unused_bucket").GetString());
            Assert.Contains("attribute-driven reflection surface", symbols[1].GetProperty("unused_reason").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunUnused_WithJsonMarksCommentSeparatedReflectionAttributeAsSuspect()
    {
        var (projectRoot, dbPath) = CreateReflectionCommentedUnusedFixtureDb();
        try
        {
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunUnused(
                ["--db", dbPath, "--json", "--lang", "csharp"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            var symbols = json.GetProperty("symbols");

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("FullName", symbols[1].GetProperty("name").GetString());
            Assert.Equal("reflection_or_config_suspect", symbols[1].GetProperty("unused_bucket").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunUnused_WithJsonMarksQualifiedAndSuffixedAttributesAsSuspect()
    {
        var (projectRoot, dbPath) = CreateQualifiedReflectionUnusedFixtureDb();
        try
        {
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunUnused(
                ["--db", dbPath, "--json", "--lang", "csharp"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var symbols = document.RootElement.GetProperty("symbols").EnumerateArray()
                .ToDictionary(symbol => symbol.GetProperty("name").GetString()!, StringComparer.Ordinal);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("reflection_or_config_suspect", symbols["QualifiedName"].GetProperty("unused_bucket").GetString());
            Assert.Equal("reflection_or_config_suspect", symbols["SuffixedName"].GetProperty("unused_bucket").GetString());
            Assert.Equal("public_or_exported_no_refs", symbols["IgnoredName"].GetProperty("unused_bucket").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunUnused_WithJsonKeepsPlainCliOptionsPropertiesInPublicBucket()
    {
        var (projectRoot, dbPath) = CreatePlainCliOptionsUnusedFixtureDb();
        try
        {
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunUnused(
                ["--db", dbPath, "--json", "--lang", "csharp"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var symbols = document.RootElement.GetProperty("symbols").EnumerateArray()
                .ToDictionary(symbol => symbol.GetProperty("name").GetString()!, StringComparer.Ordinal);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("public_or_exported_no_refs", symbols["ShowHelp"].GetProperty("unused_bucket").GetString());
            Assert.Equal("public_or_exported_no_refs", symbols["ProjectPath"].GetProperty("unused_bucket").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunUnused_WithJsonMarksBlockCommentSeparatedReflectionAttributeAsSuspect()
    {
        var (projectRoot, dbPath) = CreateBlockCommentReflectionUnusedFixtureDb();
        try
        {
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunUnused(
                ["--db", dbPath, "--json", "--lang", "csharp"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var symbols = document.RootElement.GetProperty("symbols").EnumerateArray()
                .ToDictionary(symbol => symbol.GetProperty("name").GetString()!, StringComparer.Ordinal);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("reflection_or_config_suspect", symbols["FullName"].GetProperty("unused_bucket").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunUnused_WithJsonDiversifiesReflectionSuspectBeforeLimit()
    {
        var (projectRoot, dbPath) = CreateReflectionDiversifiedUnusedFixtureDb();
        try
        {
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunUnused(
                ["--db", dbPath, "--json", "--lang", "csharp", "--limit", "4"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            var symbols = json.GetProperty("symbols");

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(["InternalOnly", "UserDto", "FullName", "Run"], symbols.EnumerateArray().Select(symbol => symbol.GetProperty("name").GetString()).ToArray());
            Assert.False(json.GetProperty("returned_bucket_counts").TryGetProperty("likely_unused_private", out _));
            Assert.Equal(1, json.GetProperty("returned_bucket_counts").GetProperty("maybe_unused_nonpublic").GetInt32());
            Assert.Equal(2, json.GetProperty("returned_bucket_counts").GetProperty("public_or_exported_no_refs").GetInt32());
            Assert.Equal(1, json.GetProperty("returned_bucket_counts").GetProperty("reflection_or_config_suspect").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunUnused_WithJsonIncludesGraphSupportMetadataForUnsupportedLanguage()
    {
        var (projectRoot, dbPath) = CreateUnsupportedLanguageUnusedFixtureDb();
        try
        {
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunUnused(
                ["--db", dbPath, "--json", "--lang", "text"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.False(json.GetProperty("graph_supported").GetBoolean());
            Assert.Contains("not indexed", json.GetProperty("graph_support_reason").GetString());
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.Equal(0, json.GetProperty("symbols").GetArrayLength());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunUnused_CountJsonUnsupportedLanguage_ReturnsZero()
    {
        var (projectRoot, dbPath) = CreateUnsupportedLanguageUnusedFixtureDb();
        try
        {
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunUnused(
                ["--db", dbPath, "--json", "--lang", "text", "--count"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.False(json.GetProperty("graph_supported").GetBoolean());
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.Equal(0, json.GetProperty("files").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunUnused_WithJsonZeroResults_UsesUnusedSchema()
    {
        var (projectRoot, dbPath) = CreateUnusedFixtureDb();
        try
        {
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunUnused(
                ["--db", dbPath, "--json", "--lang", "csharp", "--path", "does-not-exist"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.True(json.GetProperty("graph_supported").GetBoolean());
            Assert.Contains("indexed", json.GetProperty("graph_support_reason").GetString());
            Assert.True(json.TryGetProperty("symbols", out var symbols));
            Assert.Equal(0, symbols.GetArrayLength());
            Assert.True(json.TryGetProperty("returned_bucket_counts", out var bucketCounts));
            Assert.Empty(bucketCounts.EnumerateObject());
            Assert.False(json.TryGetProperty("unused", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunUnused_WithJsonUnsupportedLanguageZeroResults_UsesUnusedSchema()
    {
        var (projectRoot, dbPath) = CreateUnusedFixtureDb();
        try
        {
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunUnused(
                ["--db", dbPath, "--json", "--lang", "markdown"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.False(json.GetProperty("graph_supported").GetBoolean());
            Assert.Contains("not indexed", json.GetProperty("graph_support_reason").GetString());
            Assert.Equal(0, json.GetProperty("symbols").GetArrayLength());
            Assert.Empty(json.GetProperty("returned_bucket_counts").EnumerateObject());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunUnused_WithJsonMissingGraphTable_UsesUnusedSchema()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_unused_missing_graph_json");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunUnused(
                ["--db", dbPath, "--json", "--lang", "csharp"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.True(json.GetProperty("degraded").GetBoolean());
            Assert.False(json.GetProperty("graph_table_available").GetBoolean());
            Assert.True(json.TryGetProperty("symbols", out var symbols));
            Assert.Equal(0, symbols.GetArrayLength());
            Assert.True(json.TryGetProperty("returned_bucket_counts", out var bucketCounts));
            Assert.Empty(bucketCounts.EnumerateObject());
            Assert.False(json.TryGetProperty("unused", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunUnused_CountJson_DoesNotNeedChunksForReflectionClassification()
    {
        var (projectRoot, dbPath) = CreateReflectionUnusedFixtureDb();
        try
        {
            using (var db = new DbContext(dbPath))
            using (var cmd = db.Connection.CreateCommand())
            {
                cmd.CommandText = "DROP TABLE chunks;";
                cmd.ExecuteNonQuery();
            }

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunUnused(
                ["--db", dbPath, "--json", "--lang", "csharp", "--count"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(2, json.GetProperty("count").GetInt32());
            Assert.Equal(1, json.GetProperty("files").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunUnused_WithJsonMissingChunks_DegradesReflectionClassificationWithoutCrashing()
    {
        var (projectRoot, dbPath) = CreateReflectionUnusedFixtureDb();
        try
        {
            using (var db = new DbContext(dbPath))
            using (var cmd = db.Connection.CreateCommand())
            {
                cmd.CommandText = "DROP TABLE chunks;";
                cmd.ExecuteNonQuery();
            }

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunUnused(
                ["--db", dbPath, "--json", "--lang", "csharp"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var symbols = document.RootElement.GetProperty("symbols").EnumerateArray()
                .ToDictionary(symbol => symbol.GetProperty("name").GetString()!, StringComparer.Ordinal);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("public_or_exported_no_refs", symbols["FullName"].GetProperty("unused_bucket").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunUnused_CountHumanMissingGraphTable_WarnsDegradedZero()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_unused_missing_graph_count_human");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunUnused(
                ["--db", dbPath, "--lang", "csharp", "--count"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("0", stdout.Trim());
            Assert.Contains("degraded", stderr);
            Assert.Contains("symbol_references table missing", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunUnused_WithInlineAttributedProperty_ClassifiesPropertyAsReflectionSuspect()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_unused_inline_attr_property");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/user_dto.cs",
                "csharp",
                """
                using System.Text.Json.Serialization;

                public class UserDto
                {
                    [JsonPropertyName("full_name")] public string FullName { get; set; } = string.Empty;
                }
                """);
            using (var db = new DbContext(dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.MarkGraphReady();
            }

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunUnused(
                ["--db", dbPath, "--json", "--lang", "csharp"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var symbols = document.RootElement.GetProperty("symbols").EnumerateArray().ToList();
            var fullName = Assert.Single(symbols, symbol => symbol.GetProperty("name").GetString() == "FullName");

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("reflection_or_config_suspect", fullName.GetProperty("unused_bucket").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_ExactNameAliasMatchesBackwardCompatibleExact()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_exact_alias");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.cs",
                "csharp",
                "public class App\n{\n    public void Run() { }\n    public void RunAsync() { }\n}\n");

            var exact = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["Run", "--db", dbPath, "--json", "--exact"],
                _jsonOptions));
            var alias = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["Run", "--db", dbPath, "--json", "--exact-name"],
                _jsonOptions));

            Assert.Equal(exact.Result, alias.Result);
            Assert.Equal(exact.Stdout, alias.Stdout);
            Assert.Equal(exact.Stderr, alias.Stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_SwiftKindFiltersSeparateTypealiasAssociatedtypeAndProtocol()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_swift_kind_filters");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "models.swift"),
                """
                public typealias `Callback`<T> = (T) -> Void where T: Sendable

                public protocol Store {
                    associatedtype Item
                }
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var (typealiasExitCode, typealiasStdout, typealiasStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", dbPath, "--json", "--lang", "swift", "--kind", "typealias"],
                _jsonOptions));
            var (associatedtypeExitCode, associatedtypeStdout, associatedtypeStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", dbPath, "--json", "--lang", "swift", "--kind", "associatedtype"],
                _jsonOptions));
            var (protocolExitCode, protocolStdout, protocolStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", dbPath, "--json", "--lang", "swift", "--kind", "protocol"],
                _jsonOptions));

            var typealiasRows = ParseJsonLines(typealiasStdout);
            var associatedtypeRows = ParseJsonLines(associatedtypeStdout);
            var protocolRows = ParseJsonLines(protocolStdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            Assert.Equal(CommandExitCodes.Success, typealiasExitCode);
            Assert.Equal(string.Empty, typealiasStderr);
            Assert.Single(typealiasRows);
            Assert.Equal("typealias", typealiasRows[0].RootElement.GetProperty("kind").GetString());
            Assert.Contains("Callback", typealiasRows[0].RootElement.GetProperty("name").GetString(), StringComparison.Ordinal);

            Assert.Equal(CommandExitCodes.Success, associatedtypeExitCode);
            Assert.Equal(string.Empty, associatedtypeStderr);
            Assert.Single(associatedtypeRows);
            Assert.Equal("associatedtype", associatedtypeRows[0].RootElement.GetProperty("kind").GetString());
            Assert.Contains("Item", associatedtypeRows[0].RootElement.GetProperty("name").GetString(), StringComparison.Ordinal);

            Assert.Equal(CommandExitCodes.Success, protocolExitCode);
            Assert.Equal(string.Empty, protocolStderr);
            Assert.Single(protocolRows);
            Assert.Equal("protocol", protocolRows[0].RootElement.GetProperty("kind").GetString());
            Assert.Equal("Store", protocolRows[0].RootElement.GetProperty("name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_SwiftSetterRestrictedBacktickEscapedPropertiesRemainQueryable()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_swift_private_set_backtick_properties");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "settings.swift"),
                """
                public struct Settings {
                    private(set) var `class`: Int = 0
                    fileprivate(set) var `repeat`: Int = 1
                }
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var (classExitCode, classStdout, classStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", dbPath, "--json", "--lang", "swift", "--kind", "property", "--name", "`class`", "--exact-name", "--count"],
                _jsonOptions));
            var (repeatExitCode, repeatStdout, repeatStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", dbPath, "--json", "--lang", "swift", "--kind", "property", "--name", "`repeat`", "--exact-name", "--count"],
                _jsonOptions));

            using var classDocument = ParseJsonOutput(classStdout);
            using var repeatDocument = ParseJsonOutput(repeatStdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            Assert.Equal(CommandExitCodes.Success, classExitCode);
            Assert.Equal(string.Empty, classStderr);
            Assert.Equal(1, classDocument.RootElement.GetProperty("count").GetInt32());

            Assert.Equal(CommandExitCodes.Success, repeatExitCode);
            Assert.Equal(string.Empty, repeatStderr);
            Assert.Equal(1, repeatDocument.RootElement.GetProperty("count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_And_Definition_NormalizeCSharpVerbatimIdentifiers()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_csharp_verbatim_query_normalization");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/verbatim.cs",
                "csharp",
                """
                public class @class
                {
                    public int @int() => 0;
                    public void @caller() => @int();
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (symbolsExitCode, symbolsStdout, symbolsStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", dbPath, "--json", "--name", "@int", "--exact-name", "--count"],
                _jsonOptions));
            var (invalidVerbatimExitCode, invalidVerbatimStdout, invalidVerbatimStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", dbPath, "--json", "--name", "@", "--exact-name", "--count"],
                _jsonOptions));
            var (definitionExitCode, definitionStdout, definitionStderr) = CaptureConsole(() => QueryCommandRunner.RunDefinition(
                ["@class", "--db", dbPath, "--json", "--exact-name", "--count"],
                _jsonOptions));
            var (referencesExitCode, referencesStdout, referencesStderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["@int", "--db", dbPath, "--json", "--exact-name", "--count"],
                _jsonOptions));
            var (callersExitCode, callersStdout, callersStderr) = CaptureConsole(() => QueryCommandRunner.RunCallers(
                ["@int", "--db", dbPath, "--json", "--exact-name"],
                _jsonOptions));
            var (calleesExitCode, calleesStdout, calleesStderr) = CaptureConsole(() => QueryCommandRunner.RunCallees(
                ["@caller", "--db", dbPath, "--json", "--exact-name"],
                _jsonOptions));
            var (impactExitCode, impactStdout, impactStderr) = CaptureConsole(() => QueryCommandRunner.RunImpact(
                ["@int", "--db", dbPath, "--json"],
                _jsonOptions));
            var (inspectExitCode, inspectStdout, inspectStderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["@class", "--db", dbPath, "--json", "--exact"],
                _jsonOptions));

            using var symbolsDocument = ParseJsonOutput(symbolsStdout);
            using var definitionDocument = ParseJsonOutput(definitionStdout);
            using var referencesDocument = ParseJsonOutput(referencesStdout);
            using var callersDocument = ParseJsonOutput(callersStdout);
            using var calleesDocument = ParseJsonOutput(calleesStdout);
            using var inspectDocument = ParseJsonOutput(inspectStdout);

            Assert.Equal(CommandExitCodes.Success, symbolsExitCode);
            Assert.Equal(string.Empty, symbolsStderr);
            Assert.Equal(1, symbolsDocument.RootElement.GetProperty("count").GetInt32());

            Assert.Equal(CommandExitCodes.UsageError, invalidVerbatimExitCode);
            Assert.Contains("empty after normalization", invalidVerbatimStderr, StringComparison.Ordinal);

            Assert.Equal(CommandExitCodes.Success, definitionExitCode);
            Assert.Equal(string.Empty, definitionStderr);
            Assert.Equal(1, definitionDocument.RootElement.GetProperty("count").GetInt32());

            Assert.Equal(CommandExitCodes.Success, referencesExitCode);
            Assert.Equal(string.Empty, referencesStderr);
            Assert.True(referencesDocument.RootElement.GetProperty("count").GetInt32() > 0);

            Assert.Equal(CommandExitCodes.Success, callersExitCode);
            Assert.Equal(string.Empty, callersStderr);
            Assert.Equal("caller", callersDocument.RootElement.GetProperty("caller_name").GetString());
            Assert.Equal("int", callersDocument.RootElement.GetProperty("callee_name").GetString());

            Assert.Equal(CommandExitCodes.Success, calleesExitCode);
            Assert.Equal(string.Empty, calleesStderr);
            Assert.Equal("caller", calleesDocument.RootElement.GetProperty("caller_name").GetString());
            Assert.Equal("int", calleesDocument.RootElement.GetProperty("callee_name").GetString());

            Assert.Equal(CommandExitCodes.Success, impactExitCode);
            Assert.Equal(string.Empty, impactStderr);
            Assert.NotEmpty(impactStdout);

            using var impactDocument = ParseJsonOutput(impactStdout);

            Assert.NotEqual("none", impactDocument.RootElement.GetProperty("impact_mode").GetString());
            Assert.True(impactDocument.RootElement.GetProperty("count").GetInt32() > 0);

            Assert.Equal(CommandExitCodes.Success, inspectExitCode);
            Assert.Equal(string.Empty, inspectStderr);
            Assert.Single(inspectDocument.RootElement.GetProperty("definitions").EnumerateArray());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_SqlExactNameCountSupportsMultipleUnicodeLeafQueries()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_sql_exact_multi_count");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/procs.sql",
                "sql",
                """
                CREATE PROCEDURE dbo.Äpfel
                AS
                BEGIN
                    SELECT 1;
                END;
                GO

                CREATE PROCEDURE dbo.Bananen
                AS
                BEGIN
                    SELECT 2;
                END;
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", dbPath, "--json", "--lang", "sql", "--name", "Äpfel", "--name", "Bananen", "--exact-name", "--count"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(2, json.GetProperty("count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_CSharpConversionOperatorsUseDistinctExactNames()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_csharp_conversion_names");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Money.cs",
                "csharp",
                """
                using System.Collections.Generic;

                public struct Money
                {
                    public Money(decimal amount) { }
                    public static explicit operator Money(decimal d) => new();
                    public static explicit operator Dictionary<string,int>(Money m) => new();
                    public static explicit operator (int whole,int cents)(Money m) => (0, 0);
                    public static explicit operator (Dictionary<string, int> map, int count)?(Money m) => null;
                    public static explicit operator (int[] items, int count)(Money m) => ([], 0);
                    public static explicit operator ((int a, int b) pair, int count)(Money m) => ((0, 0), 0);
                    public static unsafe explicit operator int*(Money m) => (int*)0;
                    public static unsafe explicit operator delegate* unmanaged[Cdecl]<int, void>(Money m) => (delegate* unmanaged[Cdecl]<int, void>)0;
                }
                """);

            var (operatorExitCode, operatorStdout, operatorStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", dbPath, "--json", "--lang", "csharp", "--kind", "operator", "--name", "explicit operator Money", "--exact-name"],
                _jsonOptions));

            using var operatorDocument = ParseJsonOutput(operatorStdout);
            var operatorSymbol = operatorDocument.RootElement;

            Assert.Equal(CommandExitCodes.Success, operatorExitCode);
            Assert.Equal(string.Empty, operatorStderr);
            Assert.Equal("explicit operator Money", operatorSymbol.GetProperty("name").GetString());

            var (genericExitCode, genericStdout, genericStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", dbPath, "--json", "--lang", "csharp", "--kind", "operator", "--name", "explicit operator Dictionary<string,int>", "--exact-name"],
                _jsonOptions));

            using var genericDocument = ParseJsonOutput(genericStdout);
            var genericSymbol = genericDocument.RootElement;

            Assert.Equal(CommandExitCodes.Success, genericExitCode);
            Assert.Equal(string.Empty, genericStderr);
            Assert.Equal("explicit operator Dictionary<string,int>", genericSymbol.GetProperty("name").GetString());

            var (tupleExitCode, tupleStdout, tupleStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", dbPath, "--json", "--lang", "csharp", "--kind", "operator", "--name", "explicit operator (int whole,int cents)", "--exact-name"],
                _jsonOptions));

            using var tupleDocument = ParseJsonOutput(tupleStdout);
            var tupleSymbol = tupleDocument.RootElement;

            Assert.Equal(CommandExitCodes.Success, tupleExitCode);
            Assert.Equal(string.Empty, tupleStderr);
            Assert.Equal("explicit operator (int whole,int cents)", tupleSymbol.GetProperty("name").GetString());

            var (arrayTupleExitCode, arrayTupleStdout, arrayTupleStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", dbPath, "--json", "--lang", "csharp", "--kind", "operator", "--name", "explicit operator (int[] items, int count)", "--exact-name"],
                _jsonOptions));

            using var arrayTupleDocument = ParseJsonOutput(arrayTupleStdout);
            var arrayTupleSymbol = arrayTupleDocument.RootElement;

            Assert.Equal(CommandExitCodes.Success, arrayTupleExitCode);
            Assert.Equal(string.Empty, arrayTupleStderr);
            Assert.Equal("explicit operator (int[] items, int count)", arrayTupleSymbol.GetProperty("name").GetString());

            var (pointerExitCode, pointerStdout, pointerStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", dbPath, "--json", "--lang", "csharp", "--kind", "operator", "--name", "explicit operator int*", "--exact-name"],
                _jsonOptions));

            using var pointerDocument = ParseJsonOutput(pointerStdout);
            var pointerSymbol = pointerDocument.RootElement;

            Assert.Equal(CommandExitCodes.Success, pointerExitCode);
            Assert.Equal(string.Empty, pointerStderr);
            Assert.Equal("explicit operator int*", pointerSymbol.GetProperty("name").GetString());

            var (functionPointerExitCode, functionPointerStdout, functionPointerStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", dbPath, "--json", "--lang", "csharp", "--kind", "operator", "--name", "explicit operator delegate* unmanaged[Cdecl]<int,void>", "--exact-name"],
                _jsonOptions));

            using var functionPointerDocument = ParseJsonOutput(functionPointerStdout);
            var functionPointerSymbol = functionPointerDocument.RootElement;

            Assert.Equal(CommandExitCodes.Success, functionPointerExitCode);
            Assert.Equal(string.Empty, functionPointerStderr);
            Assert.Equal("explicit operator delegate* unmanaged[Cdecl]<int,void>", functionPointerSymbol.GetProperty("name").GetString());

            var (constructorExitCode, constructorStdout, constructorStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", dbPath, "--json", "--lang", "csharp", "--kind", "function", "--name", "Money", "--exact-name"],
                _jsonOptions));

            using var constructorDocument = ParseJsonOutput(constructorStdout);
            var constructorSymbol = constructorDocument.RootElement;

            Assert.Equal(CommandExitCodes.Success, constructorExitCode);
            Assert.Equal(string.Empty, constructorStderr);
            Assert.Equal("Money", constructorSymbol.GetProperty("name").GetString());
            Assert.Contains("public Money(decimal amount)", constructorSymbol.GetProperty("signature").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_VBOperatorDeclarationsUseDistinctExactNames()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_vb_operator_names");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Money.vb",
                "vb",
                """
                Namespace MyApp
                    Public Class Money
                        Public Shared Operator +(left As Money, right As Money) As Money
                            Return New Money()
                        End Operator

                        Public Shared Widening Operator CType(value As Money) As Decimal
                            Return 0D
                        End Operator
                    End Class
                End Namespace
                """);

            var (addExitCode, addStdout, addStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", dbPath, "--json", "--lang", "vb", "--kind", "operator", "--name", "Operator +", "--exact-name"],
                _jsonOptions));

            using var addDocument = ParseJsonOutput(addStdout);
            var addSymbol = addDocument.RootElement;

            Assert.Equal(CommandExitCodes.Success, addExitCode);
            Assert.Equal(string.Empty, addStderr);
            Assert.Equal("Operator +", addSymbol.GetProperty("name").GetString());
            Assert.Equal("operator", addSymbol.GetProperty("kind").GetString());
            Assert.Equal("Money", addSymbol.GetProperty("container_name").GetString());

            var (conversionExitCode, conversionStdout, conversionStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", dbPath, "--json", "--lang", "vb", "--kind", "operator", "--name", "Operator CType", "--exact-name"],
                _jsonOptions));

            using var conversionDocument = ParseJsonOutput(conversionStdout);
            var conversionSymbol = conversionDocument.RootElement;

            Assert.Equal(CommandExitCodes.Success, conversionExitCode);
            Assert.Equal(string.Empty, conversionStderr);
            Assert.Equal("Operator CType", conversionSymbol.GetProperty("name").GetString());
            Assert.Equal("operator", conversionSymbol.GetProperty("kind").GetString());
            Assert.Equal("Money", conversionSymbol.GetProperty("container_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_CSharpPlainFieldEdgeCasesExposeAllDeclarators()
    {
        // End-to-end coverage for the three plain-field shapes the codex adversarial
        // review flagged: (1) multi-line field where the type wraps onto its own line,
        // (2) declarator list where one statement declares several fields, and
        // (3) delegate*<...> function-pointer field. Each name must round-trip through
        // the real extractor, database write, and CLI `symbols` read path.
        // Closes #298 follow-up (codex adversarial review).
        // 以下 3 つの通常フィールド形状が extractor → DB 書き込み → CLI `symbols`
        // までを通ることを end-to-end で検証する: (1) 型宣言が改行される multi-line
        // field、(2) 1 文で複数 field を宣言する declarator list、
        // (3) `delegate*<...>` function-pointer field。Closes #298 follow-up。
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_csharp_field_edge");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Edge.cs",
                "csharp",
                """
                using System;
                using System.Collections.Generic;
                namespace Demo;
                public unsafe class Edge
                {
                    public int SingleLine;

                    private Dictionary<string, int>
                        _map = new();

                    private int _x, _y;

                    public delegate*<int, void> Callback;
                }
                """);

            var (mapExitCode, mapStdout, _) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", dbPath, "--json", "--lang", "csharp", "--kind", "property", "--name", "_map", "--exact-name"],
                _jsonOptions));
            using var mapDocument = ParseJsonOutput(mapStdout);
            Assert.Equal(CommandExitCodes.Success, mapExitCode);
            Assert.Equal("_map", mapDocument.RootElement.GetProperty("name").GetString());

            var (xExitCode, xStdout, _) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", dbPath, "--json", "--lang", "csharp", "--kind", "property", "--name", "_x", "--exact-name"],
                _jsonOptions));
            using var xDocument = ParseJsonOutput(xStdout);
            Assert.Equal(CommandExitCodes.Success, xExitCode);
            Assert.Equal("_x", xDocument.RootElement.GetProperty("name").GetString());

            var (yExitCode, yStdout, _) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", dbPath, "--json", "--lang", "csharp", "--kind", "property", "--name", "_y", "--exact-name"],
                _jsonOptions));
            using var yDocument = ParseJsonOutput(yStdout);
            Assert.Equal(CommandExitCodes.Success, yExitCode);
            Assert.Equal("_y", yDocument.RootElement.GetProperty("name").GetString());

            var (callbackExitCode, callbackStdout, _) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", dbPath, "--json", "--lang", "csharp", "--kind", "property", "--name", "Callback", "--exact-name"],
                _jsonOptions));
            using var callbackDocument = ParseJsonOutput(callbackStdout);
            Assert.Equal(CommandExitCodes.Success, callbackExitCode);
            Assert.Equal("Callback", callbackDocument.RootElement.GetProperty("name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_CSharpIssue363Fixture_DoesNotReturnPhantomSymbols()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_csharp_issue363");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "R.cs"),
                """""
                namespace CsRawStringPhantom;

                public class Svc
                {
                    public int RealMethod() => 0;

                    public string DocsExample() => """
                        public void FakeMethod() { }
                        public int FakeProp { get; set; }
                        public class FakeClass { }
                        public interface IFakeIface { }
                        public delegate int FakeDel();
                        public event System.EventHandler FakeEvent;
                        public Foo() { }
                        """;

                    public string VerbatimExample() => @"
                        public void VerbatimFake() { }
                    ";

                    public string InterpExample() => $"""
                        public void InterpFake() { }
                        """;

                    public int AnotherReal() => 1;
                }
                """"");

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", dbPath, "--json", "--lang", "csharp"],
                _jsonOptions));

            var rows = ParseJsonLines(stdout);
            var symbols = rows
                .Select(row => row.RootElement.GetProperty("name").GetString())
                .OfType<string>()
                .ToHashSet(StringComparer.Ordinal);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(7, rows.Count);

            Assert.Contains("CsRawStringPhantom", symbols);
            Assert.Contains("Svc", symbols);
            Assert.Contains("RealMethod", symbols);
            Assert.Contains("DocsExample", symbols);
            Assert.Contains("VerbatimExample", symbols);
            Assert.Contains("InterpExample", symbols);
            Assert.Contains("AnotherReal", symbols);

            Assert.DoesNotContain("FakeMethod", symbols);
            Assert.DoesNotContain("FakeProp", symbols);
            Assert.DoesNotContain("FakeClass", symbols);
            Assert.DoesNotContain("IFakeIface", symbols);
            Assert.DoesNotContain("FakeDel", symbols);
            Assert.DoesNotContain("FakeEvent", symbols);
            Assert.DoesNotContain("Foo", symbols);
            Assert.DoesNotContain("VerbatimFake", symbols);
            Assert.DoesNotContain("InterpFake", symbols);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_CssExactNameSeparatesLiteralSelectors()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_css_exact_name");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "styles.css"),
                """
                :root { --accent: #09f; }
                .root { color: red; }
                #root { color: blue; }
                [hidden] { display: none; }
                @media screen { .inline-media { color: green; } }
                .btn:hover { color: green; }
                %button-base { padding: 4px; }
                @font-face { src: url("no-family.woff2"); }
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var (classExitCode, classStdout, classStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                [".root", "--db", dbPath, "--json", "--exact-name", "--lang", "css"],
                _jsonOptions));
            var (idExitCode, idStdout, idStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["#root", "--db", dbPath, "--json", "--exact-name", "--lang", "css"],
                _jsonOptions));
            var (pseudoExitCode, pseudoStdout, pseudoStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                [".btn:hover", "--db", dbPath, "--json", "--exact-name", "--lang", "css"],
                _jsonOptions));
            var (attributeExitCode, attributeStdout, attributeStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["[hidden]", "--db", dbPath, "--json", "--exact-name", "--lang", "css"],
                _jsonOptions));
            var (inlineMediaExitCode, inlineMediaStdout, inlineMediaStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                [".inline-media", "--db", dbPath, "--json", "--exact-name", "--lang", "css"],
                _jsonOptions));
            var (propertyExitCode, propertyStdout, propertyStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--name=--accent", "--db", dbPath, "--json", "--exact-name", "--lang", "css"],
                _jsonOptions));
            var (placeholderExitCode, placeholderStdout, placeholderStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--name=%button-base", "--db", dbPath, "--json", "--exact-name", "--lang", "css"],
                _jsonOptions));
            var (fontFaceExitCode, fontFaceStdout, fontFaceStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["@font-face", "--db", dbPath, "--json", "--exact-name", "--lang", "css"],
                _jsonOptions));

            var classRows = ParseJsonLines(classStdout);
            var idRows = ParseJsonLines(idStdout);
            var pseudoRows = ParseJsonLines(pseudoStdout);
            var attributeRows = ParseJsonLines(attributeStdout);
            var inlineMediaRows = ParseJsonLines(inlineMediaStdout);
            var propertyRows = ParseJsonLines(propertyStdout);
            var placeholderRows = ParseJsonLines(placeholderStdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            Assert.Equal(CommandExitCodes.Success, classExitCode);
            Assert.Equal(string.Empty, classStderr);
            Assert.Single(classRows);
            Assert.Equal(".root", classRows[0].RootElement.GetProperty("name").GetString());
            Assert.Equal("class", classRows[0].RootElement.GetProperty("kind").GetString());

            Assert.Equal(CommandExitCodes.Success, idExitCode);
            Assert.Equal(string.Empty, idStderr);
            Assert.Single(idRows);
            Assert.Equal("#root", idRows[0].RootElement.GetProperty("name").GetString());
            Assert.Equal("class", idRows[0].RootElement.GetProperty("kind").GetString());

            Assert.Equal(CommandExitCodes.Success, pseudoExitCode);
            Assert.Equal(string.Empty, pseudoStderr);
            Assert.Single(pseudoRows);
            Assert.Equal(".btn:hover", pseudoRows[0].RootElement.GetProperty("name").GetString());
            Assert.Equal("class", pseudoRows[0].RootElement.GetProperty("kind").GetString());

            Assert.Equal(CommandExitCodes.Success, attributeExitCode);
            Assert.Equal(string.Empty, attributeStderr);
            Assert.Single(attributeRows);
            Assert.Equal("[hidden]", attributeRows[0].RootElement.GetProperty("name").GetString());
            Assert.Equal("class", attributeRows[0].RootElement.GetProperty("kind").GetString());

            Assert.Equal(CommandExitCodes.Success, inlineMediaExitCode);
            Assert.Equal(string.Empty, inlineMediaStderr);
            Assert.Single(inlineMediaRows);
            Assert.Equal(".inline-media", inlineMediaRows[0].RootElement.GetProperty("name").GetString());
            Assert.Equal("class", inlineMediaRows[0].RootElement.GetProperty("kind").GetString());

            Assert.Equal(CommandExitCodes.Success, propertyExitCode);
            Assert.Equal(string.Empty, propertyStderr);
            Assert.Single(propertyRows);
            Assert.Equal("--accent", propertyRows[0].RootElement.GetProperty("name").GetString());
            Assert.Equal("property", propertyRows[0].RootElement.GetProperty("kind").GetString());

            Assert.Equal(CommandExitCodes.Success, placeholderExitCode);
            Assert.Equal(string.Empty, placeholderStderr);
            Assert.Single(placeholderRows);
            Assert.Equal("%button-base", placeholderRows[0].RootElement.GetProperty("name").GetString());
            Assert.Equal("class", placeholderRows[0].RootElement.GetProperty("kind").GetString());

            Assert.Equal(CommandExitCodes.Success, fontFaceExitCode);
            Assert.Equal(string.Empty, fontFaceStderr);
            Assert.Equal(string.Empty, fontFaceStdout);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_CSharpExactNameFindsLowercaseEnumMembers()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_csharp_enum_member_exact_name");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "status.cs"),
                """
                namespace Demo;

                public enum Status
                {
                    active,
                    inactive,
                    pending
                }
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["active", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp"],
                _jsonOptions));

            var rows = ParseJsonLines(stdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Single(rows);
            Assert.Equal("active", rows[0].RootElement.GetProperty("name").GetString());
            Assert.Equal("enum", rows[0].RootElement.GetProperty("kind").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_CSharpExactNameFindsCompactAndZeroIndentEnumMembers()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_csharp_enum_compact_and_flat");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "mode.cs"),
                """
                namespace Demo;

                public enum Compact { A, B = A }
                public enum Flat
                {
                C,
                D = C
                }
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var (compactExitCode, compactStdout, compactStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["A", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp"],
                _jsonOptions));
            var (flatExitCode, flatStdout, flatStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["C", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp"],
                _jsonOptions));

            var compactRows = ParseJsonLines(compactStdout);
            var flatRows = ParseJsonLines(flatStdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, compactExitCode);
            Assert.Equal(string.Empty, compactStderr);
            Assert.Single(compactRows);
            Assert.Equal("A", compactRows[0].RootElement.GetProperty("name").GetString());
            Assert.Equal("enum", compactRows[0].RootElement.GetProperty("kind").GetString());
            Assert.Equal("enum", compactRows[0].RootElement.GetProperty("container_kind").GetString());
            Assert.Equal("Compact", compactRows[0].RootElement.GetProperty("container_name").GetString());
            Assert.Equal(CommandExitCodes.Success, flatExitCode);
            Assert.Equal(string.Empty, flatStderr);
            Assert.Single(flatRows);
            Assert.Equal("C", flatRows[0].RootElement.GetProperty("name").GetString());
            Assert.Equal("enum", flatRows[0].RootElement.GetProperty("kind").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_CSharpExactNameFindsSameLineSiblingEnums()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_csharp_enum_same_line_siblings");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "mode.cs"),
                """
                namespace Demo;

                public enum InlineA { A1 } public enum InlineB { B1 }
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var (enumExitCode, enumStdout, enumStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["InlineB", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp"],
                _jsonOptions));
            var (memberExitCode, memberStdout, memberStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["B1", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp"],
                _jsonOptions));

            var enumRows = ParseJsonLines(enumStdout);
            var memberRows = ParseJsonLines(memberStdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, enumExitCode);
            Assert.Equal(string.Empty, enumStderr);
            Assert.Single(enumRows);
            Assert.Equal("InlineB", enumRows[0].RootElement.GetProperty("name").GetString());
            Assert.Equal(CommandExitCodes.Success, memberExitCode);
            Assert.Equal(string.Empty, memberStderr);
            Assert.Single(memberRows);
            Assert.Equal("B1", memberRows[0].RootElement.GetProperty("name").GetString());
            Assert.Equal("enum", memberRows[0].RootElement.GetProperty("container_kind").GetString());
            Assert.Equal("InlineB", memberRows[0].RootElement.GetProperty("container_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_CSharpExactNameFindsEnumInsideSameLineClassBody()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_csharp_enum_same_line_class");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/mode.cs",
                "csharp",
                """
                public class Holder { public enum E { A } }
                """);

            var (enumExitCode, enumStdout, enumStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["E", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp"],
                _jsonOptions));
            var (memberExitCode, memberStdout, memberStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["A", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp"],
                _jsonOptions));

            var enumRows = ParseJsonLines(enumStdout);
            var memberRows = ParseJsonLines(memberStdout);

            Assert.Equal(CommandExitCodes.Success, enumExitCode);
            Assert.Equal(string.Empty, enumStderr);
            Assert.Single(enumRows);
            Assert.Equal("E", enumRows[0].RootElement.GetProperty("name").GetString());
            Assert.Equal("class", enumRows[0].RootElement.GetProperty("container_kind").GetString());
            Assert.Equal("Holder", enumRows[0].RootElement.GetProperty("container_name").GetString());

            Assert.Equal(CommandExitCodes.Success, memberExitCode);
            Assert.Equal(string.Empty, memberStderr);
            Assert.Single(memberRows);
            Assert.Equal("A", memberRows[0].RootElement.GetProperty("name").GetString());
            Assert.Equal("enum", memberRows[0].RootElement.GetProperty("container_kind").GetString());
            Assert.Equal("E", memberRows[0].RootElement.GetProperty("container_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_CSharpExactNameFindsCompactEnumMembersWithAttributesAndCastValues()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_csharp_enum_compact_attr_cast");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "mode.cs"),
                """
                using System.Runtime.Serialization;

                public enum Mode { [Obsolete] A = (int)B, [EnumMember(Value = "b")] B = (MyFlags)(A | C), C = 1 }
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var (aExitCode, aStdout, aStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["A", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp"],
                _jsonOptions));
            var (bExitCode, bStdout, bStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["B", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp"],
                _jsonOptions));

            var aRows = ParseJsonLines(aStdout);
            var bRows = ParseJsonLines(bStdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, aExitCode);
            Assert.Equal(string.Empty, aStderr);
            Assert.Single(aRows);
            Assert.Equal("A", aRows[0].RootElement.GetProperty("name").GetString());
            Assert.Equal("enum", aRows[0].RootElement.GetProperty("kind").GetString());
            Assert.Equal("enum", aRows[0].RootElement.GetProperty("container_kind").GetString());
            Assert.Equal("Mode", aRows[0].RootElement.GetProperty("container_name").GetString());
            Assert.Equal(CommandExitCodes.Success, bExitCode);
            Assert.Equal(string.Empty, bStderr);
            Assert.Single(bRows);
            Assert.Equal("B", bRows[0].RootElement.GetProperty("name").GetString());
            Assert.Equal("enum", bRows[0].RootElement.GetProperty("kind").GetString());
            Assert.Equal("enum", bRows[0].RootElement.GetProperty("container_kind").GetString());
            Assert.Equal("Mode", bRows[0].RootElement.GetProperty("container_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_CSharpExactNameFindsEnumMembersAcrossDirectiveLines()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_csharp_enum_directives");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "mode.cs"),
                """
                public enum Mode
                {
                #if DEBUG
                    A,
                #endif
                #region values
                    B,
                #endregion
                }
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var (aExitCode, aStdout, aStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["A", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp"],
                _jsonOptions));
            var (bExitCode, bStdout, bStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["B", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp"],
                _jsonOptions));

            var aRows = ParseJsonLines(aStdout);
            var bRows = ParseJsonLines(bStdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, aExitCode);
            Assert.Equal(string.Empty, aStderr);
            Assert.Single(aRows);
            Assert.Equal("A", aRows[0].RootElement.GetProperty("name").GetString());
            Assert.Equal("enum", aRows[0].RootElement.GetProperty("kind").GetString());
            Assert.Equal(CommandExitCodes.Success, bExitCode);
            Assert.Equal(string.Empty, bStderr);
            Assert.Single(bRows);
            Assert.Equal("B", bRows[0].RootElement.GetProperty("name").GetString());
            Assert.Equal("enum", bRows[0].RootElement.GetProperty("kind").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_CSharpExactNameFindsEnumMembersWhenAttributeSharesDeclarationLine()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_csharp_enum_attr_same_line");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "mode.cs"),
                """
                namespace Demo;

                [Flags] public enum Mode
                {
                    A,
                    B = A
                }
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["A", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp"],
                _jsonOptions));

            var rows = ParseJsonLines(stdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Single(rows);
            Assert.Equal("A", rows[0].RootElement.GetProperty("name").GetString());
            Assert.Equal("enum", rows[0].RootElement.GetProperty("kind").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_CSharpExactNameFindsEnumMembersWhenMemberAttributesShareLine()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_csharp_enum_member_attr_same_line");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "mode.cs"),
                """
                using System.Runtime.Serialization;

                namespace Demo;

                public enum Mode
                {
                    [Obsolete] A,
                    [EnumMember(Value = "a")] B = A
                }
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["A", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp"],
                _jsonOptions));

            var rows = ParseJsonLines(stdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Single(rows);
            Assert.Equal("A", rows[0].RootElement.GetProperty("name").GetString());
            Assert.Equal("enum", rows[0].RootElement.GetProperty("kind").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_CSharpExactNameIgnoresMultilineEnumMemberAttributeArguments()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_csharp_enum_member_multiline_attr");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "mode.cs"),
                """
                using System.Runtime.Serialization;

                public enum Mode
                {
                    [EnumMember(
                        Value = Alias,
                        Other = 1)]
                    A,
                }
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var (valueExitCode, valueStdout, valueStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["Value", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp"],
                _jsonOptions));
            var (otherExitCode, otherStdout, otherStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["Other", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, valueExitCode);
            Assert.Equal(string.Empty, valueStdout);
            Assert.Equal(string.Empty, valueStderr);
            Assert.Equal(CommandExitCodes.Success, otherExitCode);
            Assert.Equal(string.Empty, otherStdout);
            Assert.Equal(string.Empty, otherStderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_CSharpExactNameFindsTabIndentedEnumMembers()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_csharp_enum_tab_indent");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "mode.cs"),
                "public enum Mode\n{\n\tA,\n\tB = A,\n}\n");

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["A", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp"],
                _jsonOptions));

            var rows = ParseJsonLines(stdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Single(rows);
            Assert.Equal("A", rows[0].RootElement.GetProperty("name").GetString());
            Assert.Equal("enum", rows[0].RootElement.GetProperty("kind").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_CSharpExactNameRecoversAfterIncompleteEnumDeclarationAttribute()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_csharp_enum_broken_decl_attr");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "broken.cs"),
                """
                [Attr(
                public enum Mode
                {
                    A,
                }

                public class After
                {
                }
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["After", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp"],
                _jsonOptions));

            var rows = ParseJsonLines(stdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Single(rows);
            Assert.Equal("After", rows[0].RootElement.GetProperty("name").GetString());
            Assert.Equal("class", rows[0].RootElement.GetProperty("kind").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_CSharpExactNameRecoversAfterIncompleteEnumMemberAttribute()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_csharp_enum_broken_member_attr");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "broken.cs"),
                """
                public enum Mode
                {
                    [Attr()
                    A,
                    B
                }

                public class After
                {
                }
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var (memberExitCode, memberStdout, memberStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["B", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp"],
                _jsonOptions));
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["After", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp"],
                _jsonOptions));

            var memberRows = ParseJsonLines(memberStdout);
            var rows = ParseJsonLines(stdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, memberExitCode);
            Assert.Equal(string.Empty, memberStderr);
            Assert.Single(memberRows);
            Assert.Equal("B", memberRows[0].RootElement.GetProperty("name").GetString());
            Assert.Equal("enum", memberRows[0].RootElement.GetProperty("kind").GetString());
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Single(rows);
            Assert.Equal("After", rows[0].RootElement.GetProperty("name").GetString());
            Assert.Equal("class", rows[0].RootElement.GetProperty("kind").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_CSharpExactNameRecoversAfterIncompleteEnumMemberAttributeMissingParen()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_csharp_enum_broken_member_attr_missing_paren");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "broken.cs"),
                """
                public enum BrokenAttr
                {
                    [Attr(
                    X,
                    Y
                }
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var (xExitCode, xStdout, xStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["X", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp"],
                _jsonOptions));
            var (yExitCode, yStdout, yStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["Y", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp"],
                _jsonOptions));

            var xRows = ParseJsonLines(xStdout);
            var yRows = ParseJsonLines(yStdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, xExitCode);
            Assert.Equal(string.Empty, xStderr);
            Assert.Single(xRows);
            Assert.Equal("X", xRows[0].RootElement.GetProperty("name").GetString());
            Assert.Equal(CommandExitCodes.Success, yExitCode);
            Assert.Equal(string.Empty, yStderr);
            Assert.Single(yRows);
            Assert.Equal("Y", yRows[0].RootElement.GetProperty("name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_CSharpExactNameRecoversModifierlessMembersAfterIncompleteAttribute()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_csharp_broken_member_attr_modifierless");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "broken.cs"),
                """
                public class C
                {
                    [Attr(
                    void M() {}
                    string Name { get; }
                }
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var (methodExitCode, methodStdout, methodStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["M", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp"],
                _jsonOptions));
            var (propertyExitCode, propertyStdout, propertyStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["Name", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp"],
                _jsonOptions));

            var methodRows = ParseJsonLines(methodStdout);
            var propertyRows = ParseJsonLines(propertyStdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, methodExitCode);
            Assert.Equal(string.Empty, methodStderr);
            Assert.Single(methodRows);
            Assert.Equal("M", methodRows[0].RootElement.GetProperty("name").GetString());
            Assert.Equal("function", methodRows[0].RootElement.GetProperty("kind").GetString());
            Assert.Equal(CommandExitCodes.Success, propertyExitCode);
            Assert.Equal(string.Empty, propertyStderr);
            Assert.Single(propertyRows);
            Assert.Equal("Name", propertyRows[0].RootElement.GetProperty("name").GetString());
            Assert.Equal("property", propertyRows[0].RootElement.GetProperty("kind").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_CSharpExactNameRecoversBracketTypedMembersAfterIncompleteAttribute()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_csharp_broken_member_attr_brackets");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "broken.cs"),
                """
                public class C
                {
                    [Attr(
                    int[] Values { get; }
                    int[] Build() => [];
                }
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var (propertyExitCode, propertyStdout, propertyStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["Values", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp"],
                _jsonOptions));
            var (methodExitCode, methodStdout, methodStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["Build", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp"],
                _jsonOptions));

            var propertyRows = ParseJsonLines(propertyStdout);
            var methodRows = ParseJsonLines(methodStdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, propertyExitCode);
            Assert.Equal(string.Empty, propertyStderr);
            Assert.Single(propertyRows);
            Assert.Equal("Values", propertyRows[0].RootElement.GetProperty("name").GetString());
            Assert.Equal("property", propertyRows[0].RootElement.GetProperty("kind").GetString());
            Assert.Equal(CommandExitCodes.Success, methodExitCode);
            Assert.Equal(string.Empty, methodStderr);
            Assert.Single(methodRows);
            Assert.Equal("Build", methodRows[0].RootElement.GetProperty("name").GetString());
            Assert.Equal("function", methodRows[0].RootElement.GetProperty("kind").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_CSharpExactNameReportsOwningEnumForDuplicateMemberNames()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_csharp_enum_member_container");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "enums.cs"),
                """
                namespace Demo;

                public enum First
                {
                    None,
                }

                public enum Second
                {
                    None,
                }
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["None", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp"],
                _jsonOptions));

            var rows = ParseJsonLines(stdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(2, rows.Count);
            Assert.Equal("enum", rows[0].RootElement.GetProperty("container_kind").GetString());
            Assert.Equal("First", rows[0].RootElement.GetProperty("container_name").GetString());
            Assert.Equal("enum", rows[1].RootElement.GetProperty("container_kind").GetString());
            Assert.Equal("Second", rows[1].RootElement.GetProperty("container_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_CSharpExactNameDoesNotReturnObjectInitializerNumericAssignments_Issue374()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_csharp_object_initializer_issue374");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "fixture.cs"),
                """"
                using System.Collections.Generic;

                namespace CsObjInitPhantom;

                public class Person
                {
                    public string Name { get; set; } = "";
                    public int Age { get; set; }
                    public int Priority { get; set; }
                }

                public class Creator
                {
                    public Person CreatePerson() => new Person
                    {
                        Name = "Alice",
                        Age = 30,
                        Priority = 1
                    };

                    public List<Person> CreateMany() => new()
                    {
                        new Person { Name = "Bob", Age = 25, Priority = 2 },
                        new Person
                        {
                            Name = "Carol",
                            Age = 45,
                            Priority = 3
                        }
                    };
                }
                """");

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var (ageExitCode, ageStdout, ageStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["Age", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp"],
                _jsonOptions));
            var (priorityExitCode, priorityStdout, priorityStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["Priority", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp"],
                _jsonOptions));

            var ageRows = ParseJsonLines(ageStdout);
            var priorityRows = ParseJsonLines(priorityStdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, ageExitCode);
            Assert.Equal(string.Empty, ageStderr);
            Assert.Equal(CommandExitCodes.Success, priorityExitCode);
            Assert.Equal(string.Empty, priorityStderr);

            Assert.Single(ageRows);
            Assert.Equal("property", ageRows[0].RootElement.GetProperty("kind").GetString());
            Assert.Equal("Person", ageRows[0].RootElement.GetProperty("container_name").GetString());

            Assert.Single(priorityRows);
            Assert.Equal("property", priorityRows[0].RootElement.GetProperty("kind").GetString());
            Assert.Equal("Person", priorityRows[0].RootElement.GetProperty("container_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_CSharpExactNameFindsEnumMembersWithComplexConstantExpressionValues_Issue357()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_csharp_enum_complex_value_issue357");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "E.cs"),
                """
                namespace CsEnumComplexValue;

                public class K
                {
                    public const int Foo = 1;
                }

                public enum E1
                {
                    Plain = 0,
                    Hex = 0xFF,
                    Combined = Plain | 0,
                    Shifted = 1 << 3,
                    Arith = 1 + 2,
                    ConstRef = K.Foo,
                    Casted = (int)1.5,
                    Paren = (1 + 2),
                    CharCast = (int)'A',
                    MemberAccess = System.Int32.MaxValue,
                }

                [System.Flags]
                public enum Permissions
                {
                    None = 0,
                    Read = 1,
                    Write = 2,
                    All = Read | Write,
                    Execute = K.Foo,
                }
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));

            var expectedContainers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ConstRef"] = "E1",
                ["Casted"] = "E1",
                ["Paren"] = "E1",
                ["CharCast"] = "E1",
                ["MemberAccess"] = "E1",
                ["Execute"] = "Permissions",
            };

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            foreach (var pair in expectedContainers)
            {
                var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                    [pair.Key, "--db", dbPath, "--json", "--exact-name", "--lang", "csharp"],
                    _jsonOptions));

                var rows = ParseJsonLines(stdout);
                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.Single(rows);
                Assert.Equal(pair.Key, rows[0].RootElement.GetProperty("name").GetString());
                Assert.Equal("enum", rows[0].RootElement.GetProperty("kind").GetString());
                Assert.Equal("enum", rows[0].RootElement.GetProperty("container_kind").GetString());
                Assert.Equal(pair.Value, rows[0].RootElement.GetProperty("container_name").GetString());
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunDefinition_CSharpExactNameFindsEnumMembersWithComplexConstantExpressionValues_Issue357()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_definition_csharp_enum_complex_value_issue357");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "E.cs"),
                """
                namespace CsEnumComplexValue;

                public class K
                {
                    public const int Foo = 1;
                }

                public enum E1
                {
                    Plain = 0,
                    Hex = 0xFF,
                    Combined = Plain | 0,
                    Shifted = 1 << 3,
                    Arith = 1 + 2,
                    ConstRef = K.Foo,
                    Casted = (int)1.5,
                    Paren = (1 + 2),
                    CharCast = (int)'A',
                    MemberAccess = System.Int32.MaxValue,
                }

                [System.Flags]
                public enum Permissions
                {
                    None = 0,
                    Read = 1,
                    Write = 2,
                    All = Read | Write,
                    Execute = K.Foo,
                }
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            foreach (var pair in new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ConstRef"] = "E1",
                ["Execute"] = "Permissions",
            })
            {
                var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunDefinition(
                    [pair.Key, "--db", dbPath, "--json", "--exact-name", "--lang", "csharp"],
                    _jsonOptions));

                var rows = ParseJsonLines(stdout);
                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.Single(rows);
                Assert.Equal(pair.Key, rows[0].RootElement.GetProperty("name").GetString());
                Assert.Equal("enum", rows[0].RootElement.GetProperty("kind").GetString());
                Assert.Equal("enum", rows[0].RootElement.GetProperty("container_kind").GetString());
                Assert.Equal(pair.Value, rows[0].RootElement.GetProperty("container_name").GetString());
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunDefinition_CSharpExactNameFindsNormalizedVerbatimQualifiedNames_Issues626And627()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_definition_csharp_verbatim_qualified_issue626_627");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "Verbatim.cs"),
                """
                using Outer.@class;
                using System.Collections.Generic;

                namespace Outer.@class;

                public class Target
                {
                }

                public class C
                {
                    public static implicit operator List<@class>(C value) => new();
                }
                """);

            File.WriteAllText(
                Path.Combine(projectRoot, "src", "GlobalType.cs"),
                """
                public class @class
                {
                }
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            var (namespaceExitCode, namespaceStdout, namespaceStderr) = CaptureConsole(() => QueryCommandRunner.RunDefinition(
                ["Outer.class", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp", "--kind", "namespace"],
                _jsonOptions));

            var namespaceRows = ParseJsonLines(namespaceStdout);
            Assert.Equal(CommandExitCodes.Success, namespaceExitCode);
            Assert.Equal(string.Empty, namespaceStderr);
            Assert.Single(namespaceRows);
            Assert.Equal("Outer.class", namespaceRows[0].RootElement.GetProperty("name").GetString());

            var (importExitCode, importStdout, importStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", dbPath, "--json", "--lang", "csharp", "--kind", "import", "--name", "Outer.class", "--exact-name"],
                _jsonOptions));

            using var importDocument = ParseJsonOutput(importStdout);
            var importSymbol = importDocument.RootElement;

            Assert.Equal(CommandExitCodes.Success, importExitCode);
            Assert.Equal(string.Empty, importStderr);
            Assert.Equal("Outer.class", importSymbol.GetProperty("name").GetString());

            var (classExitCode, classStdout, classStderr) = CaptureConsole(() => QueryCommandRunner.RunDefinition(
                ["C", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp"],
                _jsonOptions));

            var classRows = ParseJsonLines(classStdout);
            Assert.Equal(CommandExitCodes.Success, classExitCode);
            Assert.Equal(string.Empty, classStderr);
            Assert.Single(classRows);
            Assert.Equal("Outer.class", classRows[0].RootElement.GetProperty("container_name").GetString());

            var (operatorExitCode, operatorStdout, operatorStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", dbPath, "--json", "--lang", "csharp", "--kind", "operator", "--name", "implicit operator List<class>", "--exact-name"],
                _jsonOptions));

            using var operatorDocument = ParseJsonOutput(operatorStdout);
            var operatorSymbol = operatorDocument.RootElement;

            Assert.Equal(CommandExitCodes.Success, operatorExitCode);
            Assert.Equal(string.Empty, operatorStderr);
            Assert.Equal("implicit operator List<class>", operatorSymbol.GetProperty("name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_ExactNameStaleCSharpCanonicalNamesReportDegradedState()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_csharp_conversion_stale");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Money.cs",
                "csharp",
                """
                public struct Money
                {
                    public Money(decimal amount) { }
                    public static explicit operator Money(decimal d) => new();
                    public int this[int index] => index;
                }
                """);

            using (var db = new DbContext(dbPath))
            {
                using var cmd = db.Connection.CreateCommand();
                cmd.CommandText = """
                    UPDATE symbols SET name = 'explicit' WHERE name = 'explicit operator Money';
                    UPDATE symbols SET name = 'this' WHERE name = 'Item';
                    DELETE FROM codeindex_meta WHERE key = 'csharp_symbol_name_contract_version';
                    """;
                cmd.ExecuteNonQuery();
            }

            var (countExitCode, countStdout, countStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", dbPath, "--json", "--lang", "csharp", "--kind", "operator", "--name", "explicit operator Money", "--exact-name", "--count"],
                _jsonOptions));

            using var countDocument = ParseJsonOutput(countStdout);
            var countJson = countDocument.RootElement;

            Assert.Equal(CommandExitCodes.Success, countExitCode);
            Assert.Equal(string.Empty, countStderr);
            Assert.Equal(0, countJson.GetProperty("count").GetInt32());
            Assert.False(countJson.GetProperty("exact_index_available").GetBoolean());
            Assert.Contains("csharp_symbol_name_ready=false", countJson.GetProperty("degraded_reason").GetString());

            var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", dbPath, "--lang", "csharp", "--kind", "operator", "--name", "explicit operator Money", "--exact-name"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("No symbols found.", stderr);
            Assert.Contains("WARN: --exact symbol query may return false negatives", stderr);
            Assert.Contains("csharp_symbol_name_ready=false", stderr);
            Assert.Contains(Path.GetFullPath(projectRoot), stderr);
            Assert.Contains(Path.GetFullPath(dbPath), stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunUnused_WithPropertyTargetWhitespaceInlineAttribute_ClassifiesPropertyAsReflectionSuspect()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_unused_property_target_inline_attr");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/user_dto.cs",
                "csharp",
                """
                using System.Text.Json.Serialization;

                public class UserDto
                {
                    [property : JsonPropertyName("full_name")] public string FullName { get; set; } = string.Empty;
                }
                """);
            using (var db = new DbContext(dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.MarkGraphReady();
            }

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunUnused(
                ["--db", dbPath, "--json", "--lang", "csharp"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var fullName = Assert.Single(
                document.RootElement.GetProperty("symbols").EnumerateArray(),
                symbol => symbol.GetProperty("name").GetString() == "FullName");

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("reflection_or_config_suspect", fullName.GetProperty("unused_bucket").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunUnused_WithPropertyTargetWhitespaceMultilineAttribute_ClassifiesPropertyAsReflectionSuspect()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_unused_property_target_multiline_attr");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/user_dto.cs",
                "csharp",
                """
                using System.Text.Json.Serialization;

                public class UserDto
                {
                    [property : JsonPropertyName("full_name")]
                    public string FullName { get; set; } = string.Empty;
                }
                """);
            using (var db = new DbContext(dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.MarkGraphReady();
            }

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunUnused(
                ["--db", dbPath, "--json", "--lang", "csharp"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var fullName = Assert.Single(
                document.RootElement.GetProperty("symbols").EnumerateArray(),
                symbol => symbol.GetProperty("name").GetString() == "FullName");

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("reflection_or_config_suspect", fullName.GetProperty("unused_bucket").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunUnused_WithJsonLargePublicLimit_IsNotCappedAtBudget()
    {
        var (projectRoot, dbPath) = CreateLargePublicUnusedFixtureDb();
        try
        {
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunUnused(
                ["--db", dbPath, "--json", "--lang", "csharp", "--limit", "3000"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(2500, json.GetProperty("count").GetInt32());
            Assert.Equal(2500, json.GetProperty("symbols").GetArrayLength());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunUnused_HumanOutputGroupsByConfidenceBucket()
    {
        var (projectRoot, dbPath) = CreateUnusedFixtureDb();
        try
        {
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunUnused(
                ["--db", dbPath, "--lang", "csharp"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("Likely unused private (1)", stdout);
            Assert.Contains("Maybe unused non-public (1)", stdout);
            Assert.Contains("Public/exported with no refs (6)", stdout);
            Assert.Contains("Reflection/config suspects (1)", stdout);
            Assert.Contains("confidence=medium", stdout);
            Assert.Contains("confidence=low", stdout);
            Assert.Contains("returned potentially unused symbols; returned buckets:", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_RejectsExactSubstringAlias()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_wrong_exact_alias");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["Run", "--db", dbPath, "--exact-substring"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains("--exact-name", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("--exact", "--exact-name")]
    [InlineData("--exact", "--exact-substring")]
    [InlineData("--exact-substring", "--exact-name")]
    public void RunDefinition_RejectsCombinedExactFlags(string first, string second)
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_definition_combined_exact");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunDefinition(
                ["Run", "--db", dbPath, first, second],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains("pass only one of --exact, --exact-substring, --exact-name", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_RejectsCombinedExactAndExactName()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_combined_exact_name");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["Run", "--db", dbPath, "--exact", "--exact-name"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains("pass only one of --exact, --exact-substring, --exact-name", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunDefinition_ExactNameFindsUnicodeEscapedCSharpAndJavaSymbols()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_definition_exact_unicode_escapes");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Escaped.cs",
                "csharp",
                "namespace Demo.\\u004eames;\n\n"
                + "public class \\u0046oo\n"
                + "{\n"
                + "    public void \\u0042ar() { }\n"
                + "}\n");
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/App.java",
                "java",
                "package demo.\\u0061pp;\n\n"
                + "public class \\u004aavaFoo {\n"
                + "    public void \\u0062ar() { }\n"
                + "}\n");

            var (csharpExitCode, csharpStdout, csharpStderr) = CaptureConsole(() => QueryCommandRunner.RunDefinition(
                ["Bar", "--db", dbPath, "--json", "--lang", "csharp", "--kind", "function", "--exact-name"],
                _jsonOptions));
            var csharpRows = ParseJsonLines(csharpStdout);

            var (javaExitCode, javaStdout, javaStderr) = CaptureConsole(() => QueryCommandRunner.RunDefinition(
                ["bar", "--db", dbPath, "--json", "--lang", "java", "--kind", "function", "--exact-name"],
                _jsonOptions));
            var javaRows = ParseJsonLines(javaStdout);

            Assert.Equal(CommandExitCodes.Success, csharpExitCode);
            Assert.Equal(string.Empty, csharpStderr);
            Assert.Single(csharpRows);
            Assert.Equal("Bar", csharpRows[0].RootElement.GetProperty("name").GetString());
            Assert.Equal("Foo", csharpRows[0].RootElement.GetProperty("container_name").GetString());

            Assert.Equal(CommandExitCodes.Success, javaExitCode);
            Assert.Equal(string.Empty, javaStderr);
            Assert.Single(javaRows);
            Assert.Equal("bar", javaRows[0].RootElement.GetProperty("name").GetString());
            Assert.Equal("JavaFoo", javaRows[0].RootElement.GetProperty("container_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunDefinition_ExactNameFindsKotlinBacktickedSymbolNames()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_definition_exact_kotlin_backticks");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/App.kt",
                "kotlin",
                """
                class `when` {
                    fun `is`(): Int = 1
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunDefinition(
                ["is", "--db", dbPath, "--json", "--lang", "kotlin", "--kind", "function", "--exact-name"],
                _jsonOptions));

            var rows = ParseJsonLines(stdout);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Single(rows);
            Assert.Equal("is", rows[0].RootElement.GetProperty("name").GetString());
            Assert.Equal("function", rows[0].RootElement.GetProperty("kind").GetString());
            Assert.Equal("when", rows[0].RootElement.GetProperty("container_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunDefinition_MissingQueryUsageMentionsExactNameAlias()
    {
        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunDefinition([], _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("--exact|--exact-name", stderr);
    }

    [Fact]
    public void RunHotspots_ZeroJson_ReportsDegradedHotspotFamilyTrust()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_hotspots_family_zero_json");
        try
        {
            var dbPath = CreateHotspotFamilyFixtureDb(projectRoot, markHotspotFamilyReady: false);
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunHotspots(
                ["--db", dbPath, "--json", "--lang", "csharp", "--kind", "function"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.False(json.GetProperty("hotspot_family_ready").GetBoolean());
            Assert.True(json.GetProperty("degraded").GetBoolean());
            Assert.Contains("hotspot_family_support_not_indexed=csharp", json.GetProperty("hotspot_family_degraded_reason").GetString());
            Assert.True(json.GetProperty("graph_table_available").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunHotspots_ZeroJson_ReportsLegacyNullFamilyKeysAsDegraded()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_hotspots_family_legacy_zero_json");
        try
        {
            var dbPath = CreateHotspotFamilyFixtureDb(projectRoot, markHotspotFamilyReady: true);
            using (var db = new DbContext(dbPath))
            {
                using var cmd = db.Connection.CreateCommand();
                cmd.CommandText = """
                    UPDATE symbols
                    SET family_key = NULL,
                        container_qualified_name = NULL
                    WHERE file_id IN (
                        SELECT id FROM files WHERE lang = 'csharp'
                    );
                    """;
                cmd.ExecuteNonQuery();

                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.GetHotspotFamilyVersionMetaKey("csharp"), null);
                writer.SetMeta(DbContext.GetHotspotFamilyMarkerFingerprintMetaKey("csharp"), null);
            }

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunHotspots(
                ["--db", dbPath, "--json", "--lang", "csharp", "--kind", "function"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.False(json.GetProperty("hotspot_family_ready").GetBoolean());
            Assert.True(json.GetProperty("degraded").GetBoolean());
            Assert.Contains("hotspot_family_support_not_indexed=csharp", json.GetProperty("hotspot_family_degraded_reason").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunUnused_CountJson_MixedRepoStaleSqlGraphContractIncludesDegradedStateWhenCountContainsSql()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_unused_mixed_sql_graph_contract_count");
        try
        {
            var dbPath = CreateMixedSqlGraphContractCountFixtureDb(projectRoot);
            DowngradeMixedSqlGraphContractCountRows(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunUnused(
                ["--db", dbPath, "--json", "--count"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(3, json.GetProperty("count").GetInt32());
            Assert.Equal(2, json.GetProperty("files").GetInt32());
            Assert.False(json.GetProperty("sql_graph_contract_ready").GetBoolean());
            Assert.Contains("sql_graph_contract_ready=false", json.GetProperty("sql_graph_contract_degraded_reason").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunHotspots_ZeroJson_StaleSqlGraphContractIncludesDegradedStateWhenSqlScopeIsEmpty()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_hotspots_zero_sql_graph_contract");
        try
        {
            var dbPath = CreateSqlGraphContractZeroResultFixtureDb(projectRoot);
            DowngradeSqlGraphContractVersion(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunHotspots(
                ["--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.False(json.GetProperty("sql_graph_contract_ready").GetBoolean());
            Assert.Contains("sql_graph_contract_ready=false", json.GetProperty("sql_graph_contract_degraded_reason").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunUnused_ZeroJson_StaleSqlGraphContractStaysCleanWhenSqlSymbolsCannotMatchKind()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_unused_zero_sql_graph_contract");
        try
        {
            var dbPath = CreateSqlGraphContractZeroResultFixtureDb(projectRoot);
            DowngradeSqlGraphContractVersion(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunUnused(
                ["--db", dbPath, "--json", "--kind", "interface"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.False(json.TryGetProperty("sql_graph_contract_ready", out _));
            Assert.False(json.TryGetProperty("sql_graph_contract_degraded_reason", out _));
            Assert.False(json.TryGetProperty("degraded", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunUnused_CountZeroJson_StaleSqlGraphContractStaysCleanWhenSqlSymbolsCannotMatchKind()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_unused_zero_sql_graph_contract_count");
        try
        {
            var dbPath = CreateSqlGraphContractZeroResultFixtureDb(projectRoot);
            DowngradeSqlGraphContractVersion(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunUnused(
                ["--db", dbPath, "--json", "--kind", "interface", "--count"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.Equal(0, json.GetProperty("files").GetInt32());
            Assert.False(json.TryGetProperty("sql_graph_contract_ready", out _));
            Assert.False(json.TryGetProperty("sql_graph_contract_degraded_reason", out _));
            Assert.False(json.GetProperty("degraded").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunHotspots_ZeroJson_StaleSqlGraphContractStaysCleanWhenSqlSymbolsCannotMatchKind()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_hotspots_zero_sql_graph_contract_kind");
        try
        {
            var dbPath = CreateSqlGraphContractZeroResultFixtureDb(projectRoot);
            DowngradeSqlGraphContractVersion(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunHotspots(
                ["--db", dbPath, "--json", "--kind", "class"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.False(json.TryGetProperty("sql_graph_contract_ready", out _));
            Assert.False(json.TryGetProperty("sql_graph_contract_degraded_reason", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunHotspots_ZeroJson_ReportsMissingMarkerFingerprintAsDegraded()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_hotspots_family_missing_fingerprint_zero_json");
        try
        {
            var dbPath = CreateHotspotFamilyFixtureDb(projectRoot, markHotspotFamilyReady: true);
            using (var db = new DbContext(dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.GetHotspotFamilyMarkerFingerprintMetaKey("csharp"), null);
            }

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunHotspots(
                ["--db", dbPath, "--json", "--lang", "csharp", "--kind", "function"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.False(json.GetProperty("hotspot_family_ready").GetBoolean());
            Assert.True(json.GetProperty("degraded").GetBoolean());
            Assert.Contains("hotspot_family_disabled_at_index_time=csharp", json.GetProperty("hotspot_family_degraded_reason").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunHotspots_ZeroJson_ReportsStaleHotspotFamilyMetadata()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_hotspots_family_stale_zero_json");
        try
        {
            var dbPath = CreateHotspotFamilyFixtureDb(projectRoot, markHotspotFamilyReady: true);
            using (var db = new DbContext(dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.GetHotspotFamilyVersionMetaKey("csharp"), "1");
            }

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunHotspots(
                ["--db", dbPath, "--json", "--lang", "csharp", "--kind", "function"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.False(json.GetProperty("hotspot_family_ready").GetBoolean());
            Assert.True(json.GetProperty("degraded").GetBoolean());
            Assert.Contains("hotspot_family_metadata_stale=csharp", json.GetProperty("hotspot_family_degraded_reason").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunHotspots_HumanOutput_WarnsWhenHotspotFamilyTrustIsDegraded()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_hotspots_family_zero_human");
        try
        {
            var dbPath = CreateHotspotFamilyFixtureDb(projectRoot, markHotspotFamilyReady: false);
            var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunHotspots(
                ["--db", dbPath, "--lang", "csharp", "--kind", "function"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("cross-file hotspot family grouping", stderr);
            Assert.Contains("authoritative cross-file hotspot families", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("definition")]
    [InlineData("symbols")]
    [InlineData("unused")]
    [InlineData("hotspots")]
    public void SymbolKindCommands_InvalidKindFailsWithValidKindList(string command)
    {
        var args = command switch
        {
            "definition" => new[] { "Target", "--kind", "badkind" },
            "symbols" => ["Target", "--kind", "badkind"],
            "unused" => ["--kind", "badkind"],
            "hotspots" => ["--kind", "badkind"],
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
        };

        var (exitCode, stdout, stderr) = CaptureConsole(() => command switch
        {
            "definition" => QueryCommandRunner.RunDefinition(args, _jsonOptions),
            "symbols" => QueryCommandRunner.RunSymbols(args, _jsonOptions),
            "unused" => QueryCommandRunner.RunUnused(args, _jsonOptions),
            "hotspots" => QueryCommandRunner.RunHotspots(args, _jsonOptions),
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
        });

        Assert.Equal(CommandExitCodes.InvalidArgument, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("invalid --kind value `badkind`", stderr);
        Assert.Contains("Hint: use one of:", stderr);
        Assert.Contains("function", stderr);
        Assert.Contains($"Usage: {ConsoleUi.GetUsageLine(command)}", stderr);
    }

    [Fact]
    public void RunSymbols_ExactJson_CSharpMultiLineExpressionBodiedMethodTerminatorLineKeepsSiblingField()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_csharp_multiline_expr_body_sibling");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Use.cs", "csharp",
                """
                namespace Demo;

                public enum Color
                {
                    Red
                }

                public sealed class Uses
                {
                    public bool Match(object value) => value is
                        Red; public int X;
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["X", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name"],
                _jsonOptions));

            var rows = ParseJsonLines(stdout)
                .Select(document => document.RootElement)
                .ToList();
            var x = Assert.Single(rows);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("property", x.GetProperty("kind").GetString());
            Assert.Equal("X", x.GetProperty("name").GetString());
            Assert.Equal("Uses", x.GetProperty("container_name").GetString());
            Assert.Equal("public int X;", x.GetProperty("signature").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunUnused_WithJsonKeepsReferencedCSharpEnumMembersOutWithoutDegradedMetadata()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_unused_enum_members");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace Demo;

                public enum Nested
                {
                    A = 1,
                    B = A
                }

                public class UsesEnum
                {
                    public Nested Value => Nested.A;
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunUnused(
                ["--db", dbPath, "--json", "--lang", "csharp"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var names = document.RootElement
                .GetProperty("symbols")
                .EnumerateArray()
                .Select(symbol => symbol.GetProperty("name").GetString())
                .Where(name => name != null)
                .ToHashSet(StringComparer.Ordinal);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.True(document.RootElement.GetProperty("graph_supported").GetBoolean());
            Assert.False(document.RootElement.TryGetProperty("graph_degraded", out _));
            Assert.False(document.RootElement.TryGetProperty("unsupported_symbol_kind", out _));
            Assert.DoesNotContain("A", names);
            Assert.Contains("B", names);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunUnused_WithJsonIncludesUnusedCSharpEnumMembers()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_unused_enum_declarations");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace Demo;

                public enum Color
                {
                    Red,
                    Blue
                }

                public enum TrulyUnused
                {
                    Green
                }

                public class UsesColor
                {
                    public Color Shade => Color.Red;
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunUnused(
                ["--db", dbPath, "--json", "--lang", "csharp"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var names = document.RootElement
                .GetProperty("symbols")
                .EnumerateArray()
                .Select(symbol => symbol.GetProperty("name").GetString())
                .Where(name => name != null)
                .ToHashSet(StringComparer.Ordinal);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.True(document.RootElement.GetProperty("graph_supported").GetBoolean());
            Assert.False(document.RootElement.TryGetProperty("graph_degraded", out _));
            Assert.False(document.RootElement.TryGetProperty("unsupported_symbol_kind", out _));
            Assert.DoesNotContain("Color", names);
            Assert.Contains("TrulyUnused", names);
            Assert.DoesNotContain("Red", names);
            Assert.Contains("Blue", names);
            Assert.Contains("Green", names);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunUnused_WithKindEnum_IncludesUnusedEnumDeclarationsAndMembers()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_unused_kind_enum_declarations");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace Demo;

                public enum Color
                {
                    Red,
                    Blue
                }

                public enum TrulyUnused
                {
                    Green
                }

                public class UsesColor
                {
                    public Color Shade => Color.Red;
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunUnused(
                ["--db", dbPath, "--json", "--lang", "csharp", "--kind", "enum"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var names = document.RootElement
                .GetProperty("symbols")
                .EnumerateArray()
                .Select(symbol => symbol.GetProperty("name").GetString())
                .Where(name => name != null)
                .Cast<string>()
                .ToHashSet(StringComparer.Ordinal);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.DoesNotContain("Color", names);
            Assert.Contains("TrulyUnused", names);
            Assert.DoesNotContain("Red", names);
            Assert.Contains("Blue", names);
            Assert.Contains("Green", names);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunUnused_WithJsonOnCSharpProjectWithoutEnumMembersDoesNotMarkGraphDegraded()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_unused_without_enum_members");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace Demo;

                public class Holder
                {
                    public int Value { get; }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunUnused(
                ["--db", dbPath, "--json", "--lang", "csharp"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.True(json.GetProperty("graph_supported").GetBoolean());
            Assert.False(json.TryGetProperty("graph_degraded", out _));
            Assert.False(json.TryGetProperty("unsupported_symbol_kind", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunUnused_JsonWithoutLangKeepsGraphMetadataCleanWhenScopeContainsEnumMembers()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_unused_without_lang_enum_members");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace Demo;

                public enum Color
                {
                    Red,
                    Blue
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunUnused(
                ["--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(JsonValueKind.Null, json.GetProperty("graph_supported").ValueKind);
            Assert.False(json.TryGetProperty("graph_degraded", out _));
            Assert.False(json.TryGetProperty("unsupported_symbol_kind", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunHotspots_GroupByNameJson_CountIsNameKindGroupCount()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_hotspots_group_json");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/FirstHelper.cs", "csharp",
                """
                public class FirstHelper
                {
                    private void SharedHelper() { }

                    public void Use()
                    {
                        SharedHelper();
                    }
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/SecondHelper.cs", "csharp",
                """
                public class SecondHelper
                {
                    private void SharedHelper() { }

                    public void Use()
                    {
                        SharedHelper();
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunHotspots(
                ["--db", dbPath, "--json", "--kind", "function", "--path", "Helper.cs", "--group-by-name"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            var hotspot = Assert.Single(json.GetProperty("hotspots").EnumerateArray());

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(1, json.GetProperty("count").GetInt32());
            Assert.Equal(2, json.GetProperty("definition_site_total").GetInt32());
            Assert.Equal("name_kind", json.GetProperty("grouped_by").GetString());
            Assert.Equal("SharedHelper", hotspot.GetProperty("name").GetString());
            Assert.Equal("function", hotspot.GetProperty("kind").GetString());
            Assert.Equal(2, hotspot.GetProperty("reference_count").GetInt32());
            Assert.Equal(2, hotspot.GetProperty("definition_sites").GetInt32());
            Assert.Equal(2, hotspot.GetProperty("paths").GetArrayLength());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunHotspots_GroupByFileJson_RollsUpSymbolHotspotsByPath()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_hotspots_group_file_json");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/One.cs", "csharp",
                """
                public class One
                {
                    private void A() { A(); A(); }
                    private void B() { B(); }
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Two.cs", "csharp",
                """
                public class Two
                {
                    private void C() { C(); }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunHotspots(
                ["--db", dbPath, "--json", "--kind", "function", "--group-by=file", "--limit", "1"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            var hotspot = Assert.Single(json.GetProperty("hotspots").EnumerateArray());

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("file", json.GetProperty("grouped_by").GetString());
            Assert.Equal(1, json.GetProperty("count").GetInt32());
            Assert.Equal("src/One.cs", hotspot.GetProperty("path").GetString());
            Assert.Equal(3, hotspot.GetProperty("reference_count").GetInt32());
            Assert.Equal(2, hotspot.GetProperty("symbol_count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunHotspots_SqlJson_DefaultsGroupedByStatement()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_hotspots_sql_group_default");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunHotspots(
                ["--db", dbPath, "--json", "--lang", "sql", "--kind", "function"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("statement", json.GetProperty("grouped_by").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunHotspots_GroupByNameAndGroupBy_IsRejected()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_hotspots_group_conflict");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);

            var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunHotspots(
                ["--db", dbPath, "--group-by-name", "--group-by", "symbol"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains("--group-by-name cannot be combined with --group-by", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunHotspots_GroupByNameHumanOutput_ShowsCollapsedSiteCount()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_hotspots_group_human");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/FirstHelper.cs", "csharp",
                """
                public class FirstHelper
                {
                    private void SharedHelper() { }

                    public void Use()
                    {
                        SharedHelper();
                    }
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/SecondHelper.cs", "csharp",
                """
                public class SecondHelper
                {
                    private void SharedHelper() { }

                    public void Use()
                    {
                        SharedHelper();
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunHotspots(
                ["--db", dbPath, "--kind", "function", "--path", "Helper.cs", "--group-by-name"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("SharedHelper", stdout);
            Assert.Contains("(×2 sites)", stdout);
            Assert.DoesNotContain("(×1 sites)", stdout);
            Assert.Contains("(1 unique name/kind groups, 2 definition sites)", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunHotspots_CountJson_IgnoresLimitForSymbolAndFileGroups()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_hotspots_count_limit");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            for (var i = 0; i < 3; i++)
            {
                TestProjectHelper.InsertIndexedFile(dbPath, $"src/Hotspot{i}.cs", "csharp",
                    $$"""
                    public class HotspotContainer{{i}}
                    {
                        private void Hotspot{{i}}()
                        {
                            Hotspot{{i}}();
                        }
                    }
                    """);
            }
            MarkGraphAndFoldReady(dbPath);

            var (symbolExitCode, symbolStdout, symbolStderr) = CaptureConsole(() => QueryCommandRunner.RunHotspots(
                ["--db", dbPath, "--json", "--kind", "function", "--count", "--limit", "1"],
                _jsonOptions));
            var (fileExitCode, fileStdout, fileStderr) = CaptureConsole(() => QueryCommandRunner.RunHotspots(
                ["--db", dbPath, "--json", "--kind", "function", "--group-by", "file", "--count", "--limit", "1"],
                _jsonOptions));

            using var symbolDocument = ParseJsonOutput(symbolStdout);
            using var fileDocument = ParseJsonOutput(fileStdout);
            var symbolJson = symbolDocument.RootElement;
            var fileJson = fileDocument.RootElement;

            Assert.Equal(CommandExitCodes.Success, symbolExitCode);
            Assert.Equal(string.Empty, symbolStderr);
            Assert.Equal(3, symbolJson.GetProperty("count").GetInt32());
            Assert.Equal(3, symbolJson.GetProperty("files").GetInt32());
            Assert.Equal("symbol", symbolJson.GetProperty("grouped_by").GetString());

            Assert.Equal(CommandExitCodes.Success, fileExitCode);
            Assert.Equal(string.Empty, fileStderr);
            Assert.Equal(3, fileJson.GetProperty("count").GetInt32());
            Assert.Equal(3, fileJson.GetProperty("files").GetInt32());
            Assert.Equal("file", fileJson.GetProperty("grouped_by").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunHotspots_GroupByName_CountJson_UsesGroupedSemantics()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_hotspots_group_count");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/FirstHelper.cs", "csharp",
                """
                public class FirstHelper
                {
                    private void SharedHelper() { }

                    public void Use()
                    {
                        SharedHelper();
                    }
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/SecondHelper.cs", "csharp",
                """
                public class SecondHelper
                {
                    private void SharedHelper() { }

                    public void Use()
                    {
                        SharedHelper();
                    }
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/UniqueHelper.cs", "csharp",
                """
                public class UniqueHelper
                {
                    private void UniqueHotspot()
                    {
                        UniqueHotspot();
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunHotspots(
                ["--db", dbPath, "--json", "--group-by-name", "--count", "--limit", "1"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(2, json.GetProperty("count").GetInt32());
            Assert.Equal(3, json.GetProperty("definition_site_total").GetInt32());
            Assert.Equal(3, json.GetProperty("files").GetInt32());
            Assert.Equal("name_kind", json.GetProperty("grouped_by").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunHotspots_GroupByName_ZeroJson_PreservesGroupedShape()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_hotspots_group_zero_json");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Helper.cs", "csharp",
                """
                public class Helper
                {
                    private void SharedHelper()
                    {
                        SharedHelper();
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunHotspots(
                ["--db", dbPath, "--json", "--group-by-name", "--path", "DOES_NOT_EXIST"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.Equal(0, json.GetProperty("definition_site_total").GetInt32());
            Assert.Equal("name_kind", json.GetProperty("grouped_by").GetString());
            Assert.Equal(0, json.GetProperty("hotspots").GetArrayLength());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunHotspots_GroupByName_CountZeroJson_PreservesGroupedShape()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_hotspots_group_count_zero_json");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Helper.cs", "csharp",
                """
                public class Helper
                {
                    private void SharedHelper()
                    {
                        SharedHelper();
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunHotspots(
                ["--db", dbPath, "--json", "--group-by-name", "--count", "--path", "DOES_NOT_EXIST"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.Equal(0, json.GetProperty("files").GetInt32());
            Assert.Equal(0, json.GetProperty("definition_site_total").GetInt32());
            Assert.Equal("name_kind", json.GetProperty("grouped_by").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunHotspots_GroupByName_AppliesLimitAfterGrouping()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_hotspots_group_limit");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            for (var i = 0; i < 4; i++)
            {
                TestProjectHelper.InsertIndexedFile(dbPath, $"src/DupHelper{i}.cs", "csharp",
                    $$"""
                    public class DupHelper{{i}}
                    {
                        private void SharedHelper()
                        {
                            SharedHelper();
                            SharedHelper();
                        }
                    }
                    """);
            }

            TestProjectHelper.InsertIndexedFile(dbPath, "src/UniqueHelper.cs", "csharp",
                """
                public class UniqueHelper
                {
                    private void UniqueHotspot()
                    {
                        UniqueHotspot();
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunHotspots(
                ["--db", dbPath, "--json", "--kind", "function", "--path", "Helper", "--group-by-name", "--limit", "2"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            var hotspots = json.GetProperty("hotspots").EnumerateArray().ToList();

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(2, json.GetProperty("count").GetInt32());
            Assert.Equal(5, json.GetProperty("definition_site_total").GetInt32());
            Assert.Contains(hotspots, h => h.GetProperty("name").GetString() == "SharedHelper");
            Assert.Contains(hotspots, h => h.GetProperty("name").GetString() == "UniqueHotspot");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunHotspots_GroupByNameJson_CapsPathSamples()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_hotspots_group_paths");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            for (var i = 0; i < 25; i++)
            {
                TestProjectHelper.InsertIndexedFile(dbPath, $"src/Helper{i:D2}.cs", "csharp",
                    $$"""
                    public class Helper{{i}}
                    {
                        private void SharedHelper()
                        {
                            SharedHelper();
                        }
                    }
                    """);
            }
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunHotspots(
                ["--db", dbPath, "--json", "--kind", "function", "--group-by-name", "--limit", "1"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            var hotspot = Assert.Single(json.GetProperty("hotspots").EnumerateArray());

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(25, hotspot.GetProperty("definition_sites").GetInt32());
            Assert.Equal(20, hotspot.GetProperty("paths").GetArrayLength());
            Assert.True(hotspot.GetProperty("paths_truncated").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunHotspots_GroupByName_CountsSameFileOverloadsAsSeparateDefinitionSites()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_hotspots_group_overloads");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Overloads.cs", "csharp",
                """
                public class Overloads
                {
                    private void SharedHelper()
                    {
                        SharedHelper();
                        SharedHelper(1);
                    }

                    private void SharedHelper(int value)
                    {
                        SharedHelper();
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunHotspots(
                ["--db", dbPath, "--json", "--kind", "function", "--group-by-name", "--limit", "1"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            var hotspot = Assert.Single(json.GetProperty("hotspots").EnumerateArray());

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(1, json.GetProperty("count").GetInt32());
            Assert.Equal(2, json.GetProperty("definition_site_total").GetInt32());
            Assert.Equal(3, hotspot.GetProperty("reference_count").GetInt32());
            Assert.Equal(2, hotspot.GetProperty("definition_sites").GetInt32());
            Assert.Equal(1, hotspot.GetProperty("paths").GetArrayLength());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunHotspots_GroupByName_UsesDeterministicRepresentativeSite()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_hotspots_group_rep");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/z_last.cs", "csharp",
                """
                public class LastHelper
                {
                    private void SharedHelper()
                    {
                        SharedHelper();
                    }
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/a_first.cs", "csharp",
                """
                public class FirstHelper
                {
                    private void SharedHelper()
                    {
                        SharedHelper();
                    }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunHotspots(
                ["--db", dbPath, "--json", "--kind", "function", "--group-by-name", "--limit", "1"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            var hotspot = Assert.Single(json.GetProperty("hotspots").EnumerateArray());

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("src/a_first.cs", hotspot.GetProperty("path").GetString());
            Assert.Equal(3, hotspot.GetProperty("line").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunDefinition_ExactZeroJson_ReturnsEmptyStdout()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_definition_exact_zero");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.cs",
                "csharp",
                """
                public class App
                {
                    public void HandleRequest() { }
                    public void HandleRequestAsync() { HandleRequest(); }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunDefinition(
                ["Handle", "--db", dbPath, "--json", "--exact"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Equal(string.Empty, stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunDefinition_ExactZeroJson_PreservesRelaxedCountAndCapsSamplesToFive()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_definition_exact_zero_cap");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.cs",
                "csharp",
                """
                public class App
                {
                    public void HandleRequest1() { }
                    public void HandleRequest2() { }
                    public void HandleRequest3() { }
                    public void HandleRequest4() { }
                    public void HandleRequest5() { }
                    public void HandleRequest6() { }
                    public void HandleRequest7() { }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunDefinition(
                ["Handle", "--db", dbPath, "--json", "--count", "--exact", "--limit", "99"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.Equal(7, json.GetProperty("exact_zero_hint").GetProperty("relaxed_count").GetInt32());
            Assert.Equal(5, json.GetProperty("exact_zero_hint").GetProperty("sample_names").GetArrayLength());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunDefinition_ExactZeroJson_RespectsRequestedLimitForRelaxedCount()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_definition_exact_zero_limit");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.cs",
                "csharp",
                """
                public class App
                {
                    public void HandleRequest1() { }
                    public void HandleRequest2() { }
                    public void HandleRequest3() { }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunDefinition(
                ["Handle", "--db", dbPath, "--json", "--count", "--exact", "--limit", "1"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(1, json.GetProperty("exact_zero_hint").GetProperty("relaxed_count").GetInt32());
            Assert.Equal(1, json.GetProperty("exact_zero_hint").GetProperty("sample_names").GetArrayLength());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_MultiNameExactZeroJson_OmitsRelaxedCountButReturnsSamples()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_symbols_multi_exact_zero");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.cs",
                "csharp",
                """
                public class App
                {
                    public void AlphaWorker() { }
                    public void BetaWorker() { }
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["Alpha", "Beta", "--db", dbPath, "--json", "--count", "--exact", "--limit", "999"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, json.GetProperty("count").GetInt32());
            Assert.False(json.GetProperty("exact_zero_hint").TryGetProperty("relaxed_count", out _));
            Assert.Contains("AlphaWorker", json.GetProperty("exact_zero_hint").GetProperty("sample_names").EnumerateArray().Select(e => e.GetString()));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_ExactOnReadOnlyLegacyDb_WarnsAboutMissingIndex()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_symbol_exact_warn");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/session.py", "python", "def Run(user):\n    return user\n\ndef login(user, password):\n    return Run(user)\n");
            DropSymbolExactFallbackIndex(dbPath);

            var readOnlyUri = new Uri(dbPath).AbsoluteUri + "?immutable=1";
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["Run", "--db", readOnlyUri, "--exact"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("Run", stdout);
            Assert.Contains("WARN: --exact symbol query ran without the supporting index", stderr);
            Assert.Contains("idx_symbols_name_nocase", stderr);
            Assert.Contains("re-index with `cdidx index <projectPath>`", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_ExactWithoutQuery_OnReadOnlyLegacyDb_OmitsExactSignalAndWarning()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_symbol_exact_no_query");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/session.py", "python", "def Run(user):\n    return user\n");
            DropSymbolExactFallbackIndex(dbPath);

            var readOnlyUri = new Uri(dbPath).AbsoluteUri + "?immutable=1";
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", readOnlyUri, "--exact", "--json", "--limit", "1"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("Run", json.GetProperty("name").GetString());
            Assert.False(json.TryGetProperty("exact_index_available", out _));
            Assert.False(json.TryGetProperty("degraded_reason", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunDefinition_ExactJsonOnReadOnlyLegacyDb_IncludesExactIndexSignal()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_definition_exact_json");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/session.py", "python", "def Run(user):\n    return user\n\ndef login(user, password):\n    return Run(user)\n");
            DropSymbolExactFallbackIndex(dbPath);

            var readOnlyUri = new Uri(dbPath).AbsoluteUri + "?immutable=1";
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunDefinition(
                ["Run", "--db", readOnlyUri, "--exact", "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("Run", json.GetProperty("name").GetString());
            Assert.False(json.GetProperty("exact_index_available").GetBoolean());
            Assert.Contains("idx_symbols_name_nocase", json.GetProperty("degraded_reason").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunDefinition_CountOnlyOnReadOnlyDbMissingChunks_FailsLikeDefinition()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_definition_count_missing_chunks");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/session.py", "python", "def Run(user):\n    return user\n");
            DropChunksTables(dbPath);

            var readOnlyUri = new Uri(dbPath).AbsoluteUri + "?immutable=1";

            var (countExitCode, countStdout, countStderr) = CaptureConsole(() => QueryCommandRunner.RunDefinition(
                ["Run", "--db", readOnlyUri, "--json", "--count"],
                _jsonOptions));

            var (definitionExitCode, definitionStdout, definitionStderr) = CaptureConsole(() => QueryCommandRunner.RunDefinition(
                ["Run", "--db", readOnlyUri, "--json"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.DatabaseError, countExitCode);
            Assert.Equal(string.Empty, countStdout);
            Assert.Contains("no such table: chunks", countStderr);

            Assert.Equal(CommandExitCodes.DatabaseError, definitionExitCode);
            Assert.Equal(string.Empty, definitionStdout);
            Assert.Contains("no such table: chunks", definitionStderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_BlankPositionalQueryReturnsDistinctUsageError()
    {
        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
            ["   "],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("Error: symbols query cannot be empty or whitespace-only", stderr);
        Assert.DoesNotContain("symbol name list is empty after normalization", stderr);
        Assert.Contains("empty or whitespace-only arguments", stderr);
        Assert.Contains($"Usage: {ConsoleUi.GetUsageLine("symbols")}", stderr);
    }

    [Fact]
    public void RunSymbols_Json_CSharpNestedRawStringInsideInterpolationDoesNotCreatePhantomSymbols()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_csharp_nested_raw_fixture");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "app.cs"),
                """"
                public class App
                {
                    private int Run() => 1;
                    private string Id(string value) => value;

                    public int Render()
                    {
                        return $"""
                            value = {Id("""
                                public class Phantom
                                {
                                    public void Go() { }
                                }
                                """) + Run()}
                            """.Length;
                    }
                }
                """");

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["Phantom", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Equal(string.Empty, stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_Json_CSharpInterpolatedVerbatimStringEscapedBracesDoNotCreatePhantomSymbols()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_csharp_escaped_verbatim_braces");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "app.cs"),
                """
                public class App
                {
                    public string Render()
                    {
                        return $@"{{
                            public class Phantom
                        }}";
                    }
                }
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["Phantom", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Equal(string.Empty, stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_AcceptsLangPythonCaseInsensitively()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_lang_case");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "a.py", "python", "def hello(): pass\n");

            var (exitCodeUpper, stdoutUpper, _) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", dbPath, "--lang", "Python"],
                _jsonOptions));
            var (exitCodeLower, stdoutLower, _) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", dbPath, "--lang", "python"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCodeUpper);
            Assert.Equal(CommandExitCodes.Success, exitCodeLower);
            Assert.Contains("hello", stdoutUpper);
            Assert.Equal(stdoutLower, stdoutUpper);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("route", "/products/{id:int}")]
    [InlineData("implements", "IDisposable")]
    [InlineData("attribute", "Authorize")]
    [InlineData("layout", "MainLayout")]
    public void RunSymbols_AcceptsRazorDirectiveKindFilters(string kind, string expectedName)
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_razor_kind_filter");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "Pages/Product.razor",
                "csharp",
                """
                @page "/products/{id:int}"
                @implements IDisposable
                @attribute [Authorize]
                @layout MainLayout
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", dbPath, "--kind", kind, "--json"],
                _jsonOptions));

            var rows = ParseJsonLines(stdout).Select(document => document.RootElement).ToList();

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Single(rows);
            Assert.Equal(kind, rows[0].GetProperty("kind").GetString());
            Assert.Equal(expectedName, rows[0].GetProperty("name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_AcceptsKindFunctionCaseInsensitively()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_kind_case");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "a.py", "python", "def hello(): pass\n");

            var (exitCodeUpper, stdoutUpper, _) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", dbPath, "--kind", "FUNCTION"],
                _jsonOptions));
            var (exitCodeLower, stdoutLower, _) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", dbPath, "--kind", "function"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCodeUpper);
            Assert.Equal(CommandExitCodes.Success, exitCodeLower);
            Assert.Contains("hello", stdoutUpper);
            Assert.Equal(stdoutLower, stdoutUpper);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_AcceptsKindLambda()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_kind_lambda");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "a.py", "python", "transform = lambda value: value + 1\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", dbPath, "--json", "--kind", "lambda"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var symbol = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("lambda", symbol.GetProperty("kind").GetString());
            Assert.Equal("transform", symbol.GetProperty("name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_AcceptsScalaObjectKindFilter()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_scala_object_kind");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Main.scala", "scala", "object Main {\n  def run(): Unit = ()\n}\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", dbPath, "--json", "--lang", "scala", "--kind", "object"],
                _jsonOptions));

            var row = Assert.Single(ParseJsonLines(stdout)).RootElement;
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("object", row.GetProperty("kind").GetString());
            Assert.Equal("Main", row.GetProperty("name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_PreservesCommonJsMultilineBraceBodyRanges()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_commonjs_multiline_body_ranges");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/repro.js",
                "javascript",
                """
                module.exports.foo = function ()
                {
                  return 1;
                };
                module.exports.bar = () =>
                {
                  return 2;
                };
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", dbPath, "--json", "--lang", "javascript"],
                _jsonOptions));

            var symbols = stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => JsonDocument.Parse(line).RootElement)
                .ToList();
            var foo = Assert.Single(symbols, symbol => symbol.GetProperty("name").GetString() == "foo");
            var bar = Assert.Single(symbols, symbol => symbol.GetProperty("name").GetString() == "bar");

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("lambda", foo.GetProperty("kind").GetString());
            Assert.Equal(1, foo.GetProperty("start_line").GetInt32());
            Assert.Equal(4, foo.GetProperty("end_line").GetInt32());
            Assert.Equal(2, foo.GetProperty("body_start_line").GetInt32());
            Assert.Equal(4, foo.GetProperty("body_end_line").GetInt32());
            Assert.Equal("lambda", bar.GetProperty("kind").GetString());
            Assert.Equal(5, bar.GetProperty("start_line").GetInt32());
            Assert.Equal(8, bar.GetProperty("end_line").GetInt32());
            Assert.Equal(6, bar.GetProperty("body_start_line").GetInt32());
            Assert.Equal(8, bar.GetProperty("body_end_line").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_EmitsLangHintForUnknownLang()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_lang_hint");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "a.py", "python", "def hello(): pass\n");

            var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", dbPath, "--lang", "nonexistent"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("'nonexistent' not found in index. Available: python", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunDefinition_EmitsLangHintForUnknownLang()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_definition_lang_hint");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "a.py", "python", "def hello(): pass\n");

            var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunDefinition(
                ["hello", "--db", dbPath, "--lang", "nonexistent"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("'nonexistent' not found in index. Available: python", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunDefinition_AcceptsLangPythonCaseInsensitively()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_definition_lang_case");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "a.py", "python", "def hello(): pass\n");

            var (exitCode, stdout, _) = CaptureConsole(() => QueryCommandRunner.RunDefinition(
                ["hello", "--db", dbPath, "--lang", "Python"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("hello", stdout);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbols_SqlExactNamePreservesLeadingAt()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_query_runner_sql_verbatim_{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new DbContext(dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                var fileId = writer.UpsertFile(new FileRecord
                {
                    Path = "src/proc.sql",
                    Lang = "sql",
                    Size = 64,
                    Lines = 1,
                    Modified = new DateTime(2025, 1, 1),
                    Checksum = Guid.NewGuid().ToString("N"),
                });
                writer.InsertChunks([new ChunkRecord
                {
                    FileId = fileId,
                    ChunkIndex = 0,
                    StartLine = 1,
                    EndLine = 1,
                    Content = "DECLARE @count int = 1;",
                }]);
                writer.InsertSymbols([new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "variable",
                    Name = "@count",
                    Line = 1,
                    StartLine = 1,
                    EndLine = 1,
                }]);
            }

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", dbPath, "--json", "--lang", "sql", "--name", "@count", "--exact-name", "--count"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(1, document.RootElement.GetProperty("count").GetInt32());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(dbPath); } catch { }
        }
    }
}
