using System.Reflection;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public class PostExtractionHookTests
{
    [Fact]
    public void Discover_LoadsHooksAndAllowsSymbolAndReferenceMutation()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("post-extraction-hooks");
        try
        {
            var hooksDir = Path.Combine(projectRoot, "hooks");
            Directory.CreateDirectory(hooksDir);
            File.Copy(Assembly.GetExecutingAssembly().Location, Path.Combine(hooksDir, "CodeIndex.Tests.dll"));

            {
                using var runner = PostExtractionHookRunner.Discover(hooksDir);
                var context = new FileContext(projectRoot, "src/App.cs", Path.Combine(projectRoot, "src", "App.cs"), "csharp");
                var symbols = new List<SymbolRecord>
                {
                    new() { FileId = 10, Kind = "class", Name = "App", Line = 1, StartLine = 1, EndLine = 1 },
                };
                var references = new List<ReferenceRecord>();

                runner.OnSymbolsExtracted(context, symbols);
                runner.OnReferencesExtracted(context, references);

                Assert.Contains(runner.Hooks, hook => hook.TypeName == typeof(SamplePostExtractionHook).FullName);
                var synthetic = Assert.Single(symbols, symbol => symbol.Name == "AppDomainTag");
                Assert.Equal(10, synthetic.FileId);
                var reference = Assert.Single(references, item => item.SymbolName == "AppDomainTag");
                Assert.Equal(10, reference.FileId);
            }
            CollectUnloadedHookAssemblies();
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void CallbackExceptions_AreDiagnosticsAndDoNotBlockOtherHooks()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("post-extraction-hook-failure");
        try
        {
            var hooksDir = Path.Combine(projectRoot, "hooks");
            Directory.CreateDirectory(hooksDir);
            File.Copy(Assembly.GetExecutingAssembly().Location, Path.Combine(hooksDir, "CodeIndex.Tests.dll"));

            {
                using var runner = PostExtractionHookRunner.Discover(hooksDir);
                var context = new FileContext(projectRoot, "src/App.cs", Path.Combine(projectRoot, "src", "App.cs"), "csharp");
                var symbols = new List<SymbolRecord>();

                runner.OnSymbolsExtracted(context, symbols);

                Assert.Contains(symbols, symbol => symbol.Name == "AppDomainTag");
                Assert.Contains(runner.Diagnostics, diagnostic => diagnostic.TypeName == typeof(ThrowingPostExtractionHook).FullName);
            }
            CollectUnloadedHookAssemblies();
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    private static void CollectUnloadedHookAssemblies()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}

public sealed class SamplePostExtractionHook : IPostExtractionHook
{
    public void OnSymbolsExtracted(FileContext context, IList<SymbolRecord> symbols)
    {
        symbols.Add(new SymbolRecord
        {
            FileId = symbols.FirstOrDefault()?.FileId ?? 0,
            Kind = "domain_tag",
            Name = "AppDomainTag",
            Line = 1,
            StartLine = 1,
            EndLine = 1,
            Signature = $"domain tag for {context.Path}",
        });
    }

    public void OnReferencesExtracted(FileContext context, IList<ReferenceRecord> references)
    {
        references.Add(new ReferenceRecord
        {
            FileId = 10,
            SymbolName = "AppDomainTag",
            ReferenceKind = "domain_reference",
            Line = 1,
            Column = 1,
            Context = context.Path,
        });
    }
}

public sealed class ThrowingPostExtractionHook : IPostExtractionHook
{
    public void OnSymbolsExtracted(FileContext context, IList<SymbolRecord> symbols)
        => throw new InvalidOperationException("boom");

    public void OnReferencesExtracted(FileContext context, IList<ReferenceRecord> references)
        => throw new InvalidOperationException("boom");
}
