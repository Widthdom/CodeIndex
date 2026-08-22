using System.Runtime.Loader;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;

namespace CodeIndex.PostExtractionHookFixture;

public static class PostExtractionHookFixtureEnvironment
{
    public const string SlowHookDelayMilliseconds = "CDIDX_TEST_SLOW_POST_EXTRACTION_HOOK_MS";
    public const string SlowHookCompletionPath = "CDIDX_TEST_SLOW_POST_EXTRACTION_HOOK_DONE_PATH";
    public const string CancellableHookDelayMilliseconds = "CDIDX_TEST_CANCELLABLE_POST_EXTRACTION_HOOK_MS";
    public const string CancellableHookCompletionPath = "CDIDX_TEST_CANCELLABLE_POST_EXTRACTION_HOOK_DONE_PATH";
    public const string SlowConstructorHookDelayMilliseconds = "CDIDX_TEST_SLOW_CTOR_POST_EXTRACTION_HOOK_MS";
    public const string StatefulHook = "CDIDX_TEST_STATEFUL_POST_EXTRACTION_HOOK";
    public const string ThrowingConstructorHook = "CDIDX_TEST_THROWING_CTOR_POST_EXTRACTION_HOOK";
    public const string ExpandingHook = "CDIDX_TEST_EXPANDING_POST_EXTRACTION_HOOK";
    public const string CSharpDeclarationMutation = "CDIDX_TEST_CSHARP_DECLARATION_MUTATION_HOOK";
}

public sealed class AWaitingPostExtractionHook : IPostExtractionHook
{
    public void OnSymbolsExtracted(FileContext context, IList<SymbolRecord> symbols)
    {
        DelayAndSignalWhenRequested();
    }

    public void OnReferencesExtracted(FileContext context, IList<ReferenceRecord> references)
    {
        DelayAndSignalWhenRequested();
    }

    private static void DelayAndSignalWhenRequested()
    {
        var raw = Environment.GetEnvironmentVariable(
            PostExtractionHookFixtureEnvironment.CancellableHookDelayMilliseconds);
        if (!int.TryParse(raw, out var milliseconds) || milliseconds <= 0)
            return;

        Thread.Sleep(milliseconds);
        var completionPath = Environment.GetEnvironmentVariable(
            PostExtractionHookFixtureEnvironment.CancellableHookCompletionPath);
        if (!string.IsNullOrWhiteSpace(completionPath))
            File.WriteAllText(completionPath, "done");
    }
}

public sealed class SamplePostExtractionHook : IPostExtractionHook
{
    public void OnSymbolsExtracted(FileContext context, IList<SymbolRecord> symbols)
    {
        var csharpMutation = Environment.GetEnvironmentVariable(
            PostExtractionHookFixtureEnvironment.CSharpDeclarationMutation);
        if (csharpMutation == "split-and-move")
        {
            var method = symbols.FirstOrDefault(symbol => symbol.Name == "M");
            if (method != null)
            {
                method.Name = "N";
                method.Kind = "test.method";
                method.SubKind = "hook-reclassified";
                method.Signature = "void N();";
            }

            var movedContainer = symbols.FirstOrDefault(symbol => symbol.Name == "Inner");
            if (movedContainer != null)
            {
                movedContainer.ContainerKind = "class";
                movedContainer.ContainerName = "New";
                movedContainer.ContainerQualifiedName = "New";
            }

            var fileType = symbols.FirstOrDefault(symbol => symbol.Name == "SplitType");
            if (fileType != null)
            {
                fileType.Name = "SplitTypeRenamed";
                fileType.Signature = "class SplitTypeRenamed { }";
            }
            return;
        }

        if (csharpMutation == "1")
        {
            var container = symbols.FirstOrDefault(symbol => symbol.Name == "HookContainer");
            if (container != null)
            {
                container.Name = "HookContainerRenamed";
                container.Signature = "file partial class HookContainerRenamed<T>";
            }
            var existing = symbols.FirstOrDefault(symbol => symbol.Name == "HookPartial");
            if (existing != null)
            {
                existing.Name = "HookOrdinary";
                existing.Signature = "void HookOrdinary();";
                existing.ContainerName = "HookContainerRenamed";
                existing.ContainerQualifiedName = "HookContainerRenamed";
            }
            symbols.Add(new SymbolRecord
            {
                FileId = existing?.FileId ?? 0,
                Kind = "function",
                Name = "HookAddedPartial",
                Signature = "[Obsolete] partial void HookAddedPartial();",
                ContainerKind = "class",
                ContainerName = "HookContainerRenamed",
                ContainerQualifiedName = "HookContainerRenamed",
                Line = 3,
                StartLine = 3,
                EndLine = 3,
            });
            symbols.Add(new SymbolRecord
            {
                FileId = existing?.FileId ?? 0,
                Kind = "class",
                Name = "HookFileType",
                Signature = "file partial class HookFileType { }",
                Line = 4,
                StartLine = 4,
                EndLine = 4,
            });
            return;
        }

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

public sealed class ThrowingConstructorPostExtractionHook : IPostExtractionHook
{
    public ThrowingConstructorPostExtractionHook()
    {
        if (Environment.GetEnvironmentVariable(
                PostExtractionHookFixtureEnvironment.ThrowingConstructorHook) == "1")
        {
            throw new InvalidOperationException("ctor boom");
        }
    }

    public void OnSymbolsExtracted(FileContext context, IList<SymbolRecord> symbols)
    {
    }

    public void OnReferencesExtracted(FileContext context, IList<ReferenceRecord> references)
    {
    }
}

public sealed class SlowConstructorPostExtractionHook : IPostExtractionHook
{
    public SlowConstructorPostExtractionHook()
    {
        var raw = Environment.GetEnvironmentVariable(
            PostExtractionHookFixtureEnvironment.SlowConstructorHookDelayMilliseconds);
        if (int.TryParse(raw, out var milliseconds) && milliseconds > 0)
            Thread.Sleep(milliseconds);
    }

    public void OnSymbolsExtracted(FileContext context, IList<SymbolRecord> symbols)
    {
    }

    public void OnReferencesExtracted(FileContext context, IList<ReferenceRecord> references)
    {
    }
}

public sealed class StatefulPostExtractionHook : IPostExtractionHook
{
    private bool sawSymbols;

    public void OnSymbolsExtracted(FileContext context, IList<SymbolRecord> symbols)
    {
        if (Environment.GetEnvironmentVariable(PostExtractionHookFixtureEnvironment.StatefulHook) == "1")
            sawSymbols = true;
    }

    public void OnReferencesExtracted(FileContext context, IList<ReferenceRecord> references)
    {
        if (!sawSymbols
            || Environment.GetEnvironmentVariable(PostExtractionHookFixtureEnvironment.StatefulHook) != "1")
        {
            return;
        }

        references.Add(new ReferenceRecord
        {
            SymbolName = "StatefulHookSawSymbols",
            ReferenceKind = "domain_reference",
            Line = 1,
            Column = 1,
            Context = context.Path,
        });
    }
}

public sealed class SlowPostExtractionHook : IPostExtractionHook
{
    public void OnSymbolsExtracted(FileContext context, IList<SymbolRecord> symbols)
    {
        if (!DelayWhenRequested())
            return;

        symbols.Add(new SymbolRecord
        {
            Kind = "domain_tag",
            Name = "SlowHookTag",
            Line = 1,
            StartLine = 1,
            EndLine = 1,
        });
        SignalCompletionWhenRequested();
    }

    public void OnReferencesExtracted(FileContext context, IList<ReferenceRecord> references)
    {
        if (!DelayWhenRequested())
            return;

        references.Add(new ReferenceRecord
        {
            SymbolName = "SlowHookTag",
            ReferenceKind = "domain_reference",
            Line = 1,
            Column = 1,
            Context = context.Path,
        });
        SignalCompletionWhenRequested();
    }

    private static bool DelayWhenRequested()
    {
        var raw = Environment.GetEnvironmentVariable(
            PostExtractionHookFixtureEnvironment.SlowHookDelayMilliseconds);
        if (!int.TryParse(raw, out var milliseconds) || milliseconds <= 0)
            return false;

        Thread.Sleep(milliseconds);
        return true;
    }

    private static void SignalCompletionWhenRequested()
    {
        var completionPath = Environment.GetEnvironmentVariable(
            PostExtractionHookFixtureEnvironment.SlowHookCompletionPath);
        if (!string.IsNullOrWhiteSpace(completionPath))
            File.WriteAllText(completionPath, "done");
    }
}

public sealed class LoadContextReportingPostExtractionHook : IPostExtractionHook
{
    public void OnSymbolsExtracted(FileContext context, IList<SymbolRecord> symbols)
    {
        var loadContext = AssemblyLoadContext.GetLoadContext(GetType().Assembly);
        if (loadContext is { IsCollectible: true }
            && !ReferenceEquals(loadContext, AssemblyLoadContext.Default))
        {
            symbols.Add(new SymbolRecord
            {
                Kind = "domain_tag",
                Name = "CollectibleHookLoadContext",
                Line = 1,
                StartLine = 1,
                EndLine = 1,
            });
        }
    }

    public void OnReferencesExtracted(FileContext context, IList<ReferenceRecord> references)
    {
    }
}

public sealed class ExpandingPostExtractionHook : IPostExtractionHook
{
    public void OnSymbolsExtracted(FileContext context, IList<SymbolRecord> symbols)
    {
        if (Environment.GetEnvironmentVariable(PostExtractionHookFixtureEnvironment.ExpandingHook) != "1")
            return;

        for (var index = 0; index < 5; index++)
        {
            symbols.Add(new SymbolRecord
            {
                Kind = "domain_tag",
                Name = $"ExpandedHookSymbol{index}",
                Line = index + 1,
                StartLine = index + 1,
                EndLine = index + 1,
            });
        }
    }

    public void OnReferencesExtracted(FileContext context, IList<ReferenceRecord> references)
    {
        if (Environment.GetEnvironmentVariable(PostExtractionHookFixtureEnvironment.ExpandingHook) != "1")
            return;

        for (var index = 0; index < 5; index++)
        {
            references.Add(new ReferenceRecord
            {
                SymbolName = $"ExpandedHookSymbol{index}",
                ReferenceKind = "domain_reference",
                Line = index + 1,
                Column = 1,
                Context = context.Path,
            });
        }
    }
}
