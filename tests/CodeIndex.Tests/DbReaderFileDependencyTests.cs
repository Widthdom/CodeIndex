using System.Reflection;
using System.Text;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

public partial class DbReaderTests
{
    [Fact]
    public void GetFileDependencies_CapsDenseSymbolSample_Issue3155()
    {
        var sourceFileId = InsertSyntheticDependencyFile("src/DenseCaller.cs");
        var targetFileId = InsertSyntheticDependencyFile("src/DenseTarget.cs");
        var symbolNames = Enumerable
            .Range(0, DbReader.DependencySymbolSampleLimit + 5)
            .Select(index => $"DenseTarget{index:D2}")
            .ToArray();

        _writer.InsertSymbols(symbolNames.Select((name, index) => new SymbolRecord
        {
            FileId = targetFileId,
            Kind = "class",
            Name = name,
            Line = index + 1,
            StartLine = index + 1,
            EndLine = index + 1,
        }).ToArray());
        _writer.InsertReferences(symbolNames.Select((name, index) => new ReferenceRecord
        {
            FileId = sourceFileId,
            SymbolName = name,
            ReferenceKind = "type_reference",
            Line = index + 1,
            Column = 1,
            Context = name,
        }).ToArray());

        var dependency = Assert.Single(_reader.GetFileDependencies(
            limit: 10,
            lang: "csharp",
            pathPatterns: ["DenseCaller.cs"],
            excludePathPatterns: null,
            excludeTests: false));

        Assert.Equal(symbolNames.Length, dependency.ReferenceCount);
        Assert.Equal(DbReader.DependencySymbolSampleLimit, dependency.Symbols.Split(',').Length);
        Assert.DoesNotContain(symbolNames[^1], dependency.Symbols);
    }

    [Fact]
    public void GetFileDependencies_RanksNoiseAdjustedEdgesBeforeApplyingLimit_Issue4113()
    {
        var sourceFileId = InsertSyntheticDependencyFile("src/RankingCaller.cs");
        var noiseTargetFileId = InsertSyntheticDependencyFile("src/CommonNoise.cs");
        var domainTargetFileId = InsertSyntheticDependencyFile("src/DomainWorkflow.cs");
        var domainSymbols = Enumerable
            .Range(0, 9)
            .Select(index => $"DomainWorkflowStep{index}")
            .ToArray();

        _writer.InsertSymbols(
        [
            new SymbolRecord { FileId = noiseTargetFileId, Kind = "class", Name = "Regex", Line = 1, StartLine = 1, EndLine = 1 },
            new SymbolRecord { FileId = noiseTargetFileId, Kind = "class", Name = "String", Line = 2, StartLine = 2, EndLine = 2 },
            .. domainSymbols.Select((name, index) => new SymbolRecord
            {
                FileId = domainTargetFileId,
                Kind = "class",
                Name = name,
                Line = index + 1,
                StartLine = index + 1,
                EndLine = index + 1,
            }),
        ]);

        var references = new List<ReferenceRecord>();
        var line = 1;
        foreach (var symbolName in new[] { "Regex", "String" })
        {
            for (var i = 0; i < 50; i++)
                references.Add(new ReferenceRecord
                {
                    FileId = sourceFileId,
                    SymbolName = symbolName,
                    ReferenceKind = "type_reference",
                    Line = line++,
                    Column = 1,
                    Context = symbolName,
                });
        }
        foreach (var symbolName in domainSymbols)
        {
            for (var i = 0; i < 3; i++)
                references.Add(new ReferenceRecord
                {
                    FileId = sourceFileId,
                    SymbolName = symbolName,
                    ReferenceKind = "type_reference",
                    Line = line++,
                    Column = 1,
                    Context = symbolName,
                });
        }
        _writer.InsertReferences(references);

        var dependencies = _reader.GetFileDependencies(
            limit: 10,
            lang: "csharp",
            pathPatterns: ["src/RankingCaller.cs"],
            excludePathPatterns: null,
            excludeTests: false);

        var noiseEdge = Assert.Single(dependencies, dependency => dependency.TargetPath == "src/CommonNoise.cs");
        var domainEdge = Assert.Single(dependencies, dependency => dependency.TargetPath == "src/DomainWorkflow.cs");
        Assert.True(noiseEdge.ReferenceCount > domainEdge.ReferenceCount);
        Assert.True(domainEdge.RankingScore > noiseEdge.RankingScore);

        var topDependency = Assert.Single(_reader.GetFileDependencies(
            limit: 1,
            lang: "csharp",
            pathPatterns: ["src/RankingCaller.cs"],
            excludePathPatterns: null,
            excludeTests: false));
        Assert.Equal("src/DomainWorkflow.cs", topDependency.TargetPath);
    }

    [Fact]
    public void GetFileDependencies_DoesNotJoinSameNameTargetsAcrossLanguages()
    {
        InsertIndexedFile("src/Foo.cs", "csharp",
            """
            public class Foo
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/Caller.cs", "csharp",
            """
            public class Caller
            {
                public void Call(Foo foo)
                {
                    foo.Run();
                }
            }
            """);
        InsertIndexedFile("src/foo.py", "python",
            """
            def Run():
                return True
            """);

        var dependencies = _reader.GetFileDependencies(
            limit: 10,
            lang: "csharp",
            pathPatterns: ["src/Caller.cs"],
            excludePathPatterns: null,
            excludeTests: false);

        var dependency = Assert.Single(dependencies);
        Assert.Equal("src/Caller.cs", dependency.SourcePath);
        Assert.Equal("src/Foo.cs", dependency.TargetPath);
        Assert.Equal(2, dependency.ReferenceCount);
        Assert.DoesNotContain("foo.py", dependency.TargetPath, StringComparison.Ordinal);
    }

    [Fact]
    public void GetFileDependencies_IncludesMetadataReferencesAsCompileTimeDependencies()
    {
        // issue #293 follow-up: the attribute class `JsonConverter` is referenced
        // both as a runtime `new JsonConverter(...)` call AND as compile-time
        // attribute metadata `[JsonConverter(...)]`. Renaming or removing the
        // class breaks both sites, so `cdidx deps` MUST surface both edges as
        // real file-level dependencies. (`callers` / `callees` stay call-graph-
        // only and reject `--kind attribute|annotation` separately at the CLI /
        // MCP boundary — that is a different contract.)
        // issue #293 補足: attribute クラス `JsonConverter` は runtime の
        // `new JsonConverter(...)` としても、compile-time の `[JsonConverter(...)]`
        // 属性 metadata としても参照される。クラスを rename / 削除すれば両方の
        // サイトが壊れるため、`cdidx deps` は両方のエッジをファイル単位の本物の
        // 依存として出す必要がある。(`callers` / `callees` は call-graph 専用で、
        // metadata 種別は CLI / MCP boundary 側で別途拒否する)
        InsertIndexedFile("src/JsonConverterAttribute.cs", "csharp",
            """
            using System;

            [AttributeUsage(AttributeTargets.Class)]
            public class JsonConverter : Attribute
            {
                public Type ConverterType { get; }
                public JsonConverter(Type converterType) => ConverterType = converterType;
            }
            """);
        // Metadata-only usage — attribute form. Compile-time dependency: renaming
        // `JsonConverter` breaks this file at build time.
        // metadata-only の利用 (attribute 形式)。compile-time 依存:
        // `JsonConverter` を rename すればこのファイルも build-time で壊れる。
        InsertIndexedFile("src/Serializer.cs", "csharp",
            """
            [JsonConverter(typeof(int))]
            public class SerializerConfig
            {
            }
            """);
        // Runtime dependency — `new JsonConverter(...)` is a `call` / `instantiate` edge.
        // 実行時の依存 — `new JsonConverter(...)` は `call` / `instantiate` 種別の edge。
        InsertIndexedFile("src/Caller.cs", "csharp",
            """
            public class Caller
            {
                public void Do()
                {
                    var c = new JsonConverter(typeof(int));
                }
            }
            """);

        var dependencies = _reader.GetFileDependencies(limit: 10, lang: "csharp");

        // Both Caller.cs (runtime `instantiate`) and Serializer.cs (attribute
        // metadata) must appear as dependencies of JsonConverterAttribute.cs.
        // Caller.cs (runtime `instantiate`) と Serializer.cs (attribute metadata)
        // の両方が JsonConverterAttribute.cs への依存として現れる。
        Assert.Equal(2, dependencies.Count);
        Assert.Contains(dependencies, d => d.SourcePath == "src/Caller.cs" && d.TargetPath == "src/JsonConverterAttribute.cs");
        Assert.Contains(dependencies, d => d.SourcePath == "src/Serializer.cs" && d.TargetPath == "src/JsonConverterAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_MatchesCSharpAttributeSuffixConvention()
    {
        // issue #293 follow-up: C# convention — a class `FooAttribute` is used in
        // source as `[Foo]`, so the reference site is stored with symbol_name `Foo`.
        // `deps` must canonicalize these so the attribute class file is still
        // recognized as a dependency target for pure-attribute consumers.
        // issue #293 補足: C# の規約では、クラス `FooAttribute` はソース中で `[Foo]`
        // として使われるため、参照サイトは symbol_name `Foo` として保存される。
        // `deps` はこれを正規化し、attribute 専用の consumer でも attribute クラスの
        // ファイルを依存 target として認識できるようにする。
        InsertIndexedFile("src/MyAuditAttribute.cs", "csharp",
            """
            using System;

            [AttributeUsage(AttributeTargets.Class)]
            public sealed class MyAuditAttribute : Attribute
            {
            }
            """);
        // Idiomatic `[MyAudit]` usage — symbol_name recorded as `MyAudit` but target
        // class is `MyAuditAttribute`.
        // 慣用的な `[MyAudit]` 利用 — symbol_name は `MyAudit` として記録されるが
        // target クラスは `MyAuditAttribute`。
        InsertIndexedFile("src/Svc.cs", "csharp",
            """
            [MyAudit]
            public class Svc
            {
            }
            """);

        var dependencies = _reader.GetFileDependencies(limit: 10, lang: "csharp");

        Assert.Contains(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/MyAuditAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharpGenericNoArgAttribute_StillIndexedAndResolvesToAttributeClass()
    {
        // issue #293 round-15 follow-up: generic no-arg C# attributes like
        // `[MyAudit<int>]` and multi-line `[\n MyAttr<int>\n]` must still be
        // indexed as `attribute` references so `deps` can route them through
        // the suffix-alias synthesizer to the real attribute class file.
        // Before the regex was widened these forms fell through both CallRegex
        // (no `(`) and the no-arg regex (generic `<...>` after the name broke
        // the `(?=[\],]|$)` anchor), producing zero edges.
        // issue #293 round-15 補足: `[MyAudit<int>]` や複数行の `[\n MyAttr<int>\n]`
        // のようなジェネリック引数なし属性も `attribute` として取り込まれ、
        // suffix alias を経由して実属性クラスへの依存エッジに正規化される
        // こと。正規表現の拡張前は両 regex とも拾えず、エッジが 0 件だった。
        InsertIndexedFile("src/MyAuditAttribute.cs", "csharp",
            """
            using System;

            public sealed class MyAuditAttribute<T> : Attribute
            {
            }
            """);
        InsertIndexedFile("src/MyAttrAttribute.cs", "csharp",
            """
            using System;

            public sealed class MyAttrAttribute<T> : Attribute
            {
            }
            """);
        InsertIndexedFile("src/Svc.cs", "csharp",
            """
            [MyAudit<int>]
            [
                MyAttr<int>
            ]
            public class Svc
            {
            }
            """);

        var dependencies = _reader.GetFileDependencies(limit: 20, lang: "csharp");

        Assert.Contains(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/MyAuditAttribute.cs");
        Assert.Contains(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/MyAttrAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharpGenericNoArgAttribute_AssemblyTarget_IsIndexed()
    {
        // issue #293 round-15 follow-up: `[assembly: MyAttr<string>]` — assembly
        // targeted generic no-arg attribute must also reach the attribute class.
        // issue #293 round-15 補足: `[assembly: MyAttr<string>]` のような
        // assembly targeted ジェネリック引数なし属性も同様にインデックスされ、
        // attribute クラスに解決されること。
        InsertIndexedFile("src/MyAttrAttribute.cs", "csharp",
            """
            using System;

            public sealed class MyAttrAttribute<T> : Attribute
            {
            }
            """);
        InsertIndexedFile("src/AssemblyInfo.cs", "csharp",
            """
            [assembly: MyAttr<string>]
            """);

        var dependencies = _reader.GetFileDependencies(limit: 20, lang: "csharp");

        Assert.Contains(dependencies, d => d.SourcePath == "src/AssemblyInfo.cs" && d.TargetPath == "src/MyAttrAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharpAttributeRawDoesNotLeakToBareNameClass_WhenSuffixTargetExists()
    {
        // issue #293 follow-up: `[MyAudit]` in C# is stored as symbol_name='MyAudit'.
        // When both `class MyAudit` (plain class) and `class MyAuditAttribute`
        // (the real attribute target) exist, the metadata edge must resolve only
        // to `MyAuditAttribute` via the synthetic suffix alias. Keeping the raw
        // bare-name edge would over-report: `[MyAudit]` would falsely depend on
        // the unrelated plain `class MyAudit` file.
        // issue #293 補足: C# の `[MyAudit]` は symbol_name='MyAudit' で保存される。
        // `class MyAudit` (plain) と `class MyAuditAttribute` (本物の attribute)
        // が両方あるとき、metadata エッジは synthetic suffix alias 経由で
        // `MyAuditAttribute` だけに解決されるべき。raw の bare-name エッジを
        // 残すと、`[MyAudit]` が無関係な plain `class MyAudit` のファイルにも
        // 誤って依存してしまう。
        InsertIndexedFile("src/MyAuditAttribute.cs", "csharp",
            """
            using System;

            [AttributeUsage(AttributeTargets.Class)]
            public sealed class MyAuditAttribute : Attribute
            {
            }
            """);
        InsertIndexedFile("src/PlainMyAudit.cs", "csharp",
            """
            public class MyAudit
            {
            }
            """);
        InsertIndexedFile("src/Svc.cs", "csharp",
            """
            [MyAudit]
            public class Svc
            {
            }
            """);

        var dependencies = _reader.GetFileDependencies(limit: 10, lang: "csharp");

        // Only MyAuditAttribute.cs should be a dependency target for Svc.cs;
        // PlainMyAudit.cs must not appear.
        // Svc.cs の依存先は MyAuditAttribute.cs のみで、PlainMyAudit.cs は
        // 出現してはならない。
        Assert.Contains(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/MyAuditAttribute.cs");
        Assert.DoesNotContain(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/PlainMyAudit.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharpAttributeDoesNotLeakToSameNameMethodOrProperty()
    {
        // issue #293 follow-up: `[MyAuditAttribute]` (fully qualified) must only
        // match a class-like attribute target. A method / property named
        // `MyAuditAttribute` in an unrelated file must never show up as a deps
        // edge from the metadata reference. Non-metadata call-graph edges keep
        // their previous behavior (they can still resolve to any symbol kind).
        // issue #293 補足: `[MyAuditAttribute]` (完全形) は class 系の attribute
        // target にしか一致してはならない。別ファイルの同名メソッド/プロパティ
        // `MyAuditAttribute` が metadata 参照の deps エッジに現れてはいけない。
        // 非 metadata の call-graph エッジは従来どおり任意の kind に解決できる。
        InsertIndexedFile("src/MyAuditAttribute.cs", "csharp",
            """
            using System;

            [AttributeUsage(AttributeTargets.Class)]
            public sealed class MyAuditAttribute : Attribute
            {
            }
            """);
        InsertIndexedFile("src/Helpers.cs", "csharp",
            """
            public class Helpers
            {
                public void MyAuditAttribute()
                {
                }
            }
            """);
        InsertIndexedFile("src/Svc.cs", "csharp",
            """
            [MyAuditAttribute]
            public class Svc
            {
            }
            """);

        var dependencies = _reader.GetFileDependencies(limit: 10, lang: "csharp");

        Assert.DoesNotContain(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/Helpers.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharpAttributeAmbiguityCountsSameFileDuplicateClassDefinitions()
    {
        // issue #293 follow-up: when a single source file defines TWO same-named
        // class-like attribute targets under different namespaces (idiomatic C#
        // with multiple `namespace { ... }` blocks in one .cs file), the metadata
        // edge must be dropped as ambiguous just like the multi-file case. A
        // path-level count (COUNT DISTINCT target_path) would see `count = 1`
        // because both definitions live in the same file, so the previous
        // target_ambiguity CTE falsely treated the target as unambiguous. The
        // rewritten CTE joins back through files + symbols so it counts at
        // symbol-identity level and correctly sees `count = 2`.
        // issue #293 補足: 1 つの .cs ファイルに別名前空間で同名 class-like が 2 つ
        // 定義されている場合 (C# でよくある `namespace { ... }` 複数ブロック形式)
        // でも、複数ファイルのときと同様に metadata edge は ambiguous として落とす
        // 必要がある。path 単位 (COUNT DISTINCT target_path) だと両方が同じ file に
        // あるため count=1 となり、従来の target_ambiguity では誤って一意扱いされた。
        // 書き直した CTE は files + symbols に JOIN し直すため、symbol identity 単位
        // で count=2 を正しく検出する。
        InsertIndexedFile("src/MyAuditAttribute.cs", "csharp",
            """
            using System;

            namespace A
            {
                [AttributeUsage(AttributeTargets.Class)]
                public sealed class MyAuditAttribute : Attribute
                {
                }
            }

            namespace B
            {
                [AttributeUsage(AttributeTargets.Class)]
                public sealed class MyAuditAttribute : Attribute
                {
                }
            }
            """);
        InsertIndexedFile("src/Svc.cs", "csharp",
            """
            [MyAudit]
            public class Svc
            {
            }
            """);

        var dependencies = _reader.GetFileDependencies(limit: 10, lang: "csharp");

        // Even though both MyAuditAttribute definitions live in the same file, the
        // metadata reference is still ambiguous and must not produce a deps edge.
        // 同じファイル内にある 2 つの MyAuditAttribute 定義でも metadata 参照は
        // 曖昧扱いのため、deps edge を出してはならない。
        Assert.DoesNotContain(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/MyAuditAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharpAttributeDoesNotFanOutWhenMultipleSameNameAttributeClasses()
    {
        // issue #293 follow-up: if multiple same-named attribute classes exist
        // (e.g. two `MyAuditAttribute` classes in separate namespaces/files),
        // a metadata reference `[MyAudit]` must not fan out to BOTH files. We
        // cannot statically resolve which one the C# compiler picks without
        // namespace / using analysis, so we drop the ambiguous metadata edge
        // and let `impact` / `references` surface both candidates to the user.
        // issue #293 補足: 同名 attribute クラスが複数ある場合 (例: 別名前空間/別
        // ファイルに 2 つの `MyAuditAttribute` がある場合)、metadata 参照
        // `[MyAudit]` を両方に fan-out させない。cdidx は namespace / using を
        // 解析しないため正しい解決ができず、あいまいな metadata エッジは落として
        // 両候補は `impact` / `references` 経由でユーザーに示す。
        InsertIndexedFile("src/A/MyAuditAttribute.cs", "csharp",
            """
            using System;

            namespace A
            {
                [AttributeUsage(AttributeTargets.Class)]
                public sealed class MyAuditAttribute : Attribute
                {
                }
            }
            """);
        InsertIndexedFile("src/B/MyAuditAttribute.cs", "csharp",
            """
            using System;

            namespace B
            {
                [AttributeUsage(AttributeTargets.Class)]
                public sealed class MyAuditAttribute : Attribute
                {
                }
            }
            """);
        InsertIndexedFile("src/Svc.cs", "csharp",
            """
            [MyAudit]
            public class Svc
            {
            }
            """);

        var dependencies = _reader.GetFileDependencies(limit: 10, lang: "csharp");

        // Neither fan-out edge should exist; the metadata reference is ambiguous.
        // あいまいな metadata 参照はどちらの fan-out エッジも出してはならない。
        Assert.DoesNotContain(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/A/MyAuditAttribute.cs");
        Assert.DoesNotContain(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/B/MyAuditAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharpMetadataTargetResolverExcludesNonAttributeImpostor()
    {
        // issue #435: a non-attribute class that happens to share the suffix-convention
        // name (e.g. `class FooAttribute : BaseService`) must not fake ambiguity against
        // a real `class FooAttribute : Attribute` elsewhere. Before the persisted
        // `is_metadata_target` resolver, the `signature LIKE '%: %'` heuristic counted
        // both as plausible candidates and the deps edge was silently dropped. With the
        // resolver stamped, only the real attribute target is counted, so the edge from
        // `[Foo]` consumers reaches the real attribute file.
        // issue #435: `class FooAttribute : BaseService` のような suffix 規約の名前を
        // 偶然持つ非 attribute class が、別ファイルの本物 `class FooAttribute : Attribute`
        // との ambiguity を偽装してはならない。永続化された is_metadata_target resolver
        // 以前は `signature LIKE '%: %'` ヒューリスティックで両方を候補に数えてしまい、
        // deps エッジが暗黙に落ちていた。resolver stamp 後は本物の attribute target だけが
        // 候補となり、`[Foo]` 利用側からのエッジが本物の attribute ファイルに届く。
        InsertIndexedFile("src/RealFooAttribute.cs", "csharp",
            """
            using System;

            [AttributeUsage(AttributeTargets.Class)]
            public sealed class FooAttribute : Attribute
            {
            }
            """);
        InsertIndexedFile("src/ImpostorFooAttribute.cs", "csharp",
            """
            public class FooAttribute : BaseService
            {
            }
            """);
        InsertIndexedFile("src/Svc.cs", "csharp",
            """
            [Foo]
            public class Svc
            {
            }
            """);

        _writer.ResolveCSharpMetadataTargets();
        _writer.MarkMetadataTargetReady("csharp");
        var resolverReader = new DbReader(_db.Connection);

        var dependencies = resolverReader.GetFileDependencies(limit: 10, lang: "csharp");

        Assert.Contains(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/RealFooAttribute.cs");
        Assert.DoesNotContain(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/ImpostorFooAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharpMetadataTargetResolverKeepsQualifiedAttributeEdgeWithSameNameNonAttributeSibling()
    {
        // issue #443: a fully-qualified metadata reference like `[A.Foo]` must still
        // resolve to the real `A.FooAttribute` even when a sibling namespace contains
        // a same-named non-Attribute impostor. The ambiguity guard must not let the
        // impostor suppress the legitimate deps edge.
        // issue #443: `[A.Foo]` のような fully-qualified metadata 参照は、別 namespace に
        // 同名の non-Attribute impostor があっても本物の `A.FooAttribute` に解決される必要がある。
        // impostor を理由に legitimate な deps edge を消してはならない。
        InsertIndexedFile("src/A/FooAttribute.cs", "csharp",
            """
            namespace A;

            public sealed class FooAttribute : System.Attribute
            {
            }
            """);
        InsertIndexedFile("src/B/FooAttribute.cs", "csharp",
            """
            namespace B;

            public class BaseService
            {
            }

            public sealed class FooAttribute : BaseService
            {
            }
            """);
        InsertIndexedFile("src/Svc.cs", "csharp",
            """
            namespace A;

            [A.Foo]
            public class Svc
            {
            }
            """);

        _writer.ResolveCSharpMetadataTargets();
        _writer.MarkMetadataTargetReady("csharp");
        var resolverReader = new DbReader(_db.Connection);

        var dependencies = resolverReader.GetFileDependencies(limit: 10, lang: "csharp");

        Assert.Contains(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/A/FooAttribute.cs");
        Assert.DoesNotContain(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/B/FooAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharpMetadataTargetResolverHandlesTransitiveAttributeDerivation()
    {
        // issue #435: derivation can be transitive — `class FooAttribute : BaseAttr`
        // where `class BaseAttr : Attribute`. The resolver's fixed-point iteration
        // must mark FooAttribute as a metadata target. Otherwise the same-name
        // impostor would re-introduce ambiguity and the metadata edge would drop.
        // issue #435: 派生は推移的になり得る — `class FooAttribute : BaseAttr` で
        // `class BaseAttr : Attribute` の場合、resolver の fixed-point iteration が
        // FooAttribute を metadata target として印付ける必要がある。さもなければ
        // 同名 impostor が ambiguity を再導入し、metadata エッジが落ちてしまう。
        InsertIndexedFile("src/BaseAttr.cs", "csharp",
            """
            using System;

            public abstract class BaseAttr : Attribute
            {
            }
            """);
        InsertIndexedFile("src/RealFooAttribute.cs", "csharp",
            """
            public sealed class FooAttribute : BaseAttr
            {
            }
            """);
        InsertIndexedFile("src/ImpostorFooAttribute.cs", "csharp",
            """
            public class FooAttribute : BaseService
            {
            }
            """);
        InsertIndexedFile("src/Svc.cs", "csharp",
            """
            [Foo]
            public class Svc
            {
            }
            """);

        _writer.ResolveCSharpMetadataTargets();
        _writer.MarkMetadataTargetReady("csharp");
        var resolverReader = new DbReader(_db.Connection);

        var dependencies = resolverReader.GetFileDependencies(limit: 10, lang: "csharp");

        Assert.Contains(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/RealFooAttribute.cs");
        Assert.DoesNotContain(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/ImpostorFooAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharpMetadataTargetResolverPreservesAmbiguityWhenMultipleRealAttributes()
    {
        // issue #435 invariant: the resolver fix narrows the candidate set, but it
        // must NOT mask genuine ambiguity. When two REAL attribute classes share the
        // same name (both transitively derive from Attribute), the metadata edge
        // must still be dropped — namespace/using disambiguation is out of scope.
        // issue #435 invariant: resolver は候補集合を絞るが、本物の曖昧さを隠してはならない。
        // 2 つの本物 attribute class が同名で両方 Attribute 由来なら、従来どおり
        // metadata エッジは落ちる必要がある（namespace / using 解析はスコープ外）。
        InsertIndexedFile("src/A/FooAttribute.cs", "csharp",
            """
            using System;

            namespace A
            {
                public sealed class FooAttribute : Attribute
                {
                }
            }
            """);
        InsertIndexedFile("src/B/FooAttribute.cs", "csharp",
            """
            using System;

            namespace B
            {
                public sealed class FooAttribute : Attribute
                {
                }
            }
            """);
        InsertIndexedFile("src/Svc.cs", "csharp",
            """
            [Foo]
            public class Svc
            {
            }
            """);

        _writer.ResolveCSharpMetadataTargets();
        _writer.MarkMetadataTargetReady("csharp");
        var resolverReader = new DbReader(_db.Connection);

        var dependencies = resolverReader.GetFileDependencies(limit: 10, lang: "csharp");

        Assert.DoesNotContain(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/A/FooAttribute.cs");
        Assert.DoesNotContain(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/B/FooAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharpMetadataTargetResolverDistinguishesQualifiedBases()
    {
        // issue #435 codex review iter 1: a qualified base like `: B.BaseAttr` must
        // resolve specifically against the B.BaseAttr class, not leak into an unrelated
        // A.BaseAttr that happens to be a metadata target. Before the fix, the resolver
        // collapsed the base to its simple head (`BaseAttr`) and treated "any same-name
        // class is target" as "this qualified reference is target", producing a false
        // positive metadata target and therefore a spurious deps edge for `[Impostor]`.
        // issue #435 codex review iter 1: `: B.BaseAttr` のような修飾名基底は、無関係な
        // `A.BaseAttr`（metadata target）に誤解決してはならない。修正前は simple-name
        // `BaseAttr` に潰して「どれかが target なら当該修飾参照も target」化していた。
        InsertIndexedFile("src/A/BaseAttr.cs", "csharp",
            """
            using System;

            namespace A
            {
                public abstract class BaseAttr : Attribute
                {
                }
            }
            """);
        InsertIndexedFile("src/B/BaseAttr.cs", "csharp",
            """
            namespace B
            {
                public class BaseAttr : BaseService
                {
                }
            }
            """);
        InsertIndexedFile("src/ImpostorFooAttribute.cs", "csharp",
            """
            namespace B
            {
                public class ImpostorFooAttribute : B.BaseAttr
                {
                }
            }
            """);
        InsertIndexedFile("src/Svc.cs", "csharp",
            """
            [ImpostorFoo]
            public class Svc
            {
            }
            """);

        _writer.ResolveCSharpMetadataTargets();
        _writer.MarkMetadataTargetReady("csharp");
        var resolverReader = new DbReader(_db.Connection);

        var dependencies = resolverReader.GetFileDependencies(limit: 10, lang: "csharp");

        Assert.DoesNotContain(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/ImpostorFooAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharpMetadataTargetResolverHonorsQualifiedExternalSuffixFallback()
    {
        // issue #435 codex review iter 1: when a class derives from a qualified
        // external base (`: ThirdParty.ValidationAttribute`), the resolver must still
        // apply the BCL suffix fallback even if an unrelated in-repo class happens to
        // share the same simple name (`ValidationAttribute`) and is NOT a metadata
        // target. Pre-fix, the resolver collapsed to the simple name, found the
        // in-repo non-target, and suppressed the suffix fallback — silently dropping
        // the metadata edge.
        // issue #435 codex review iter 1: `: ThirdParty.ValidationAttribute` のように
        // 外部の修飾基底を継承するとき、repo 内に同名 non-target class がいても
        // suffix 規約 fallback を殺してはならない。修正前は単純名に潰して in-repo
        // non-target にぶつかり suffix fallback を潰していた。
        InsertIndexedFile("src/InRepo/ValidationAttribute.cs", "csharp",
            """
            namespace InRepo
            {
                public class ValidationAttribute : BaseService
                {
                }
            }
            """);
        InsertIndexedFile("src/MyValidatorAttribute.cs", "csharp",
            """
            public class MyValidatorAttribute : ThirdParty.ValidationAttribute
            {
            }
            """);
        InsertIndexedFile("src/Svc.cs", "csharp",
            """
            [MyValidator]
            public class Svc
            {
            }
            """);

        _writer.ResolveCSharpMetadataTargets();
        _writer.MarkMetadataTargetReady("csharp");
        var resolverReader = new DbReader(_db.Connection);

        var dependencies = resolverReader.GetFileDependencies(limit: 10, lang: "csharp");

        Assert.Contains(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/MyValidatorAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharpMetadataTargetResolverHandlesPartialClassBase()
    {
        // issue #435 codex review iter 2: legal C# `partial class` can split a single
        // logical type across multiple declaration sites, each producing its own symbol
        // row. Only one of the partial declarations carries the real base list
        // (`: Attribute`). The qualified-base index must accumulate ALL rows sharing the
        // same FQN so the fixed-point lookup can still find the target-bearing partial,
        // regardless of which file was indexed first. Before the iter-2 fix, the index
        // used `Dictionary<string, long>` with `TryAdd`, so whichever partial row was
        // inserted first won: when the base-less partial was inserted first, the
        // qualified reference from `FooAttribute : B.BaseAttr` resolved only to that
        // base-less row, never iterating to the partial that carries `: Attribute`,
        // and the metadata edge was silently dropped in a file-order dependent way.
        // issue #435 codex review iter 2: `partial class` は 1 つの論理型が複数行に
        // 分かれる。修飾名索引が `Dictionary<string, long>` + TryAdd だった旧実装では、
        // 先に insert された partial 行しか拾われず、`: Attribute` を持つ真の target
        // partial が別ファイルにあるとファイル順で metadata edge が落ちていた。List で
        // 候補集合を保持する修正により、fixed-point 反復でどれかが target になれば
        // qualified 参照も正しく解決される。
        InsertIndexedFile("src/B/BaseAttr.Core.cs", "csharp",
            """
            namespace B
            {
                public partial class BaseAttr
                {
                }
            }
            """);
        InsertIndexedFile("src/B/BaseAttr.Marker.cs", "csharp",
            """
            using System;

            namespace B
            {
                public partial class BaseAttr : Attribute
                {
                }
            }
            """);
        InsertIndexedFile("src/FooAttribute.cs", "csharp",
            """
            public class FooAttribute : B.BaseAttr
            {
            }
            """);
        InsertIndexedFile("src/Svc.cs", "csharp",
            """
            [Foo]
            public class Svc
            {
            }
            """);

        _writer.ResolveCSharpMetadataTargets();
        _writer.MarkMetadataTargetReady("csharp");
        var resolverReader = new DbReader(_db.Connection);

        var dependencies = resolverReader.GetFileDependencies(limit: 10, lang: "csharp");

        Assert.Contains(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/FooAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharpMetadataTargetResolverPrefersSameNamespaceBaseOverGlobalImpostor()
    {
        // issue #435 codex review iter 4: unqualified base names must resolve through
        // the deriving class's own namespace / nesting chain — NOT through a global
        // simple-name bucket. Before the iter-4 fix, `namespace B { class FooAttribute
        // : BaseAttr {} }` could be falsely promoted to `is_metadata_target=1` solely
        // because an unrelated `namespace A { class BaseAttr : Attribute {} }` existed
        // elsewhere in the repo, even though B's own `BaseAttr : BaseService` was the
        // actually reachable base for the unqualified reference. The result was a false
        // `deps` / `impact` edge from `[Foo] class Svc` to `B.FooAttribute`. The fix
        // indexes classes under `(enclosing scope, simple name)` and walks the deriving
        // row's scope chain inside → outside, consulting only the first scope level
        // that has a same-name row. If no scope level matches, the resolver falls back
        // to the BCL `Attribute`-suffix heuristic for external bases — the global
        // simple-name bucket is no longer consulted.
        // issue #435 codex review iter 4: 非修飾基底は deriving の名前空間 /
        // 入れ子チェーンのみで解決する。グローバル単純名索引に落とすと、別名前空間に
        // 同名の本物 Attribute 派生が居るだけで非 Attribute 派生 class が偽の
        // `is_metadata_target=1` に昇格し、`deps` / `impact` に偽エッジが残る。
        InsertIndexedFile("src/A/BaseAttr.cs", "csharp",
            """
            using System;

            namespace A
            {
                public class BaseAttr : Attribute
                {
                }
            }
            """);
        InsertIndexedFile("src/B/BaseAttr.cs", "csharp",
            """
            namespace B
            {
                public class BaseService
                {
                }

                public class BaseAttr : BaseService
                {
                }
            }
            """);
        InsertIndexedFile("src/B/FooAttribute.cs", "csharp",
            """
            namespace B
            {
                public class FooAttribute : BaseAttr
                {
                }
            }
            """);
        InsertIndexedFile("src/Svc.cs", "csharp",
            """
            namespace B
            {
                [Foo]
                public class Svc
                {
                }
            }
            """);

        _writer.ResolveCSharpMetadataTargets();
        _writer.MarkMetadataTargetReady("csharp");
        var resolverReader = new DbReader(_db.Connection);

        var dependencies = resolverReader.GetFileDependencies(limit: 10, lang: "csharp");

        Assert.DoesNotContain(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/B/FooAttribute.cs");

        // Column-level invariant: the scope-aware resolver must classify each row
        // against its own scope chain. A.BaseAttr is a real Attribute derivative;
        // B.BaseAttr (same simple name, different namespace) derives from an unrelated
        // BaseService and must stay non-target; B.FooAttribute's unqualified `BaseAttr`
        // must resolve to B.BaseAttr (not A.BaseAttr), so it also stays non-target.
        // 列レベル不変条件: scope-aware resolver は各行を自身のスコープチェーンで判定する。
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = @"
            SELECT f.path, s.is_metadata_target
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE s.kind = 'class' AND s.name IN ('BaseAttr', 'FooAttribute')
            ORDER BY f.path, s.name";
        var rows = new List<(string Path, long Flag)>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
                rows.Add((reader.GetString(0), reader.GetInt64(1)));
        }
        Assert.Contains(rows, r => r.Path == "src/A/BaseAttr.cs" && r.Flag == 1);
        Assert.Contains(rows, r => r.Path == "src/B/BaseAttr.cs" && r.Flag == 0);
        Assert.Contains(rows, r => r.Path == "src/B/FooAttribute.cs" && r.Flag == 0);
    }

    [Fact]
    public void GetFileDependencies_CSharpMetadataTargetResolverHandlesImportedNamespaceBase()
    {
        // issue #435 codex review iter 5: the iter-4 fix made unqualified base resolution
        // strictly same-scope only. That regressed the common C# pattern
        // `using A; namespace B { class FooAttribute : BaseAttr {} }` where `A.BaseAttr :
        // Attribute` is indexed in a sibling file. The iter-5 fix threads the deriving
        // file's `using` directives into the resolver so, after a same-scope lookup miss,
        // `BaseAttr` is probed as `A.BaseAttr` via `using A;` before falling through to
        // the BCL `Attribute`-suffix convention.
        // issue #435 codex review iter 5: iter 4 の strict same-scope 限定が
        // `using A; class FooAttribute : BaseAttr` の一般的 C# パターンで false-negative を
        // 招いた。iter 5 で `using` 指令を resolver に通し、same-scope 解決失敗後に
        // `using A;` 経由で `A.BaseAttr` を qualified 索引に引き当てる。
        InsertIndexedFile("src/A/BaseAttr.cs", "csharp",
            """
            using System;

            namespace A
            {
                public class BaseAttr : Attribute
                {
                }
            }
            """);
        InsertIndexedFile("src/B/FooAttribute.cs", "csharp",
            """
            using A;

            namespace B
            {
                public class FooAttribute : BaseAttr
                {
                }
            }
            """);
        InsertIndexedFile("src/B/Svc.cs", "csharp",
            """
            namespace B
            {
                [Foo]
                public class Svc
                {
                }
            }
            """);

        _writer.ResolveCSharpMetadataTargets();
        _writer.MarkMetadataTargetReady("csharp");
        var resolverReader = new DbReader(_db.Connection);

        // Column-level invariant: FooAttribute must resolve through `using A;` to
        // `A.BaseAttr : Attribute` even though B has no same-scope `BaseAttr` of its own.
        // 列レベル不変条件: B 側に `BaseAttr` が無くても `using A;` 経由で解決されること。
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = @"
            SELECT f.path, s.is_metadata_target
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE s.kind = 'class' AND s.name = 'FooAttribute'";
        long flag;
        using (var reader = cmd.ExecuteReader())
        {
            Assert.True(reader.Read(), "FooAttribute row must exist");
            flag = reader.GetInt64(1);
        }
        Assert.Equal(1L, flag);

        var dependencies = resolverReader.GetFileDependencies(limit: 10, lang: "csharp");
        Assert.Contains(dependencies, d => d.SourcePath == "src/B/Svc.cs" && d.TargetPath == "src/B/FooAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharpMetadataTargetResolverHandlesUsingAliasBase()
    {
        // issue #435 codex review iter 5: the alias form of the same regression —
        // `using AliasAttr = A.BaseAttr; class FooAttribute : AliasAttr {}`. Before iter 5,
        // the resolver had no knowledge of alias imports and left FooAttribute at
        // `is_metadata_target=0`, dropping the `[Foo]` → FooAttribute metadata edge.
        // issue #435 codex review iter 5: alias 形式の同一 regression。
        // `using AliasAttr = A.BaseAttr;` の alias 索引を resolver に取り込み、
        // `class FooAttribute : AliasAttr` が qualified 索引上で `A.BaseAttr` に解決される。
        InsertIndexedFile("src/A/BaseAttr.cs", "csharp",
            """
            using System;

            namespace A
            {
                public class BaseAttr : Attribute
                {
                }
            }
            """);
        InsertIndexedFile("src/B/FooAttribute.cs", "csharp",
            """
            using AliasAttr = A.BaseAttr;

            namespace B
            {
                public class FooAttribute : AliasAttr
                {
                }
            }
            """);
        InsertIndexedFile("src/B/Svc.cs", "csharp",
            """
            namespace B
            {
                [Foo]
                public class Svc
                {
                }
            }
            """);

        _writer.ResolveCSharpMetadataTargets();
        _writer.MarkMetadataTargetReady("csharp");
        var resolverReader = new DbReader(_db.Connection);

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = @"
            SELECT s.is_metadata_target
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE f.path = 'src/B/FooAttribute.cs' AND s.kind = 'class' AND s.name = 'FooAttribute'";
        var flag = cmd.ExecuteScalar();
        Assert.Equal(1L, Convert.ToInt64(flag));

        var dependencies = resolverReader.GetFileDependencies(limit: 10, lang: "csharp");
        Assert.Contains(dependencies, d => d.SourcePath == "src/B/Svc.cs" && d.TargetPath == "src/B/FooAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharpMetadataTargetResolverHandlesVerbatimNamespaceImport()
    {
        // issue #435 codex review iter 6: C# verbatim identifiers (`@Foo`) are a source-level
        // escape for keywords; `using @Foo.@Bar;` is semantically identical to
        // `using Foo.Bar;`. Before iter 6 the resolver stored the raw `@Foo.@Bar` token in the
        // per-file import map and never matched the qualified index, leaving
        // `VerbatimImportAttribute : BaseAttr` as `is_metadata_target=0`.
        // issue #435 codex review iter 6: verbatim 識別子 `@Foo.@Bar` は非 verbatim 形と等価。
        // 修正前は import map に生の `@Foo.@Bar` が載り、qualified 索引に当たらず
        // `VerbatimImportAttribute : BaseAttr` が `is_metadata_target=0` のまま残っていた。
        InsertIndexedFile("src/Foo/Bar/BaseAttr.cs", "csharp",
            """
            using System;

            namespace Foo.Bar
            {
                public class BaseAttr : Attribute
                {
                }
            }
            """);
        InsertIndexedFile("src/V/VerbatimImportAttribute.cs", "csharp",
            """
            using @Foo.@Bar;

            namespace V
            {
                public class VerbatimImportAttribute : BaseAttr
                {
                }
            }
            """);
        InsertIndexedFile("src/V/Consumer.cs", "csharp",
            """
            namespace V
            {
                [VerbatimImport]
                public class Consumer
                {
                }
            }
            """);

        _writer.ResolveCSharpMetadataTargets();
        _writer.MarkMetadataTargetReady("csharp");
        var resolverReader = new DbReader(_db.Connection);

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = @"
            SELECT s.is_metadata_target
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE f.path = 'src/V/VerbatimImportAttribute.cs' AND s.kind = 'class' AND s.name = 'VerbatimImportAttribute'";
        var flag = cmd.ExecuteScalar();
        Assert.Equal(1L, Convert.ToInt64(flag));

        var dependencies = resolverReader.GetFileDependencies(limit: 10, lang: "csharp");
        Assert.Contains(dependencies, d => d.SourcePath == "src/V/Consumer.cs" && d.TargetPath == "src/V/VerbatimImportAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharpMetadataTargetResolverHandlesVerbatimAliasNameAndTarget()
    {
        // issue #435 codex review iter 6: verbatim on both sides of an alias import —
        // `using @AliasAttr = @Foo.@Bar.BaseAttr;` should parse, be captured as an import
        // row (the SymbolExtractor regex was too strict to accept the leading `@` on alias
        // names), and resolve identically to the non-verbatim spelling.
        // issue #435 codex review iter 6: alias 両辺の verbatim — alias 名にも target にも
        // `@` が付くケース。旧 SymbolExtractor regex は alias 名の `@` を受けず import 行
        // 自体が生成されなかったため、resolver に届く前に情報が欠落していた。
        InsertIndexedFile("src/Foo/Bar/BaseAttr.cs", "csharp",
            """
            using System;

            namespace Foo.Bar
            {
                public class BaseAttr : Attribute
                {
                }
            }
            """);
        InsertIndexedFile("src/V/VerbatimAliasAttribute.cs", "csharp",
            """
            using @AliasAttr = @Foo.@Bar.BaseAttr;

            namespace V
            {
                public class VerbatimAliasAttribute : AliasAttr
                {
                }
            }
            """);
        InsertIndexedFile("src/V/AliasConsumer.cs", "csharp",
            """
            namespace V
            {
                [VerbatimAlias]
                public class AliasConsumer
                {
                }
            }
            """);

        _writer.ResolveCSharpMetadataTargets();
        _writer.MarkMetadataTargetReady("csharp");
        var resolverReader = new DbReader(_db.Connection);

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = @"
            SELECT s.is_metadata_target
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE f.path = 'src/V/VerbatimAliasAttribute.cs' AND s.kind = 'class' AND s.name = 'VerbatimAliasAttribute'";
        var flag = cmd.ExecuteScalar();
        Assert.Equal(1L, Convert.ToInt64(flag));

        var dependencies = resolverReader.GetFileDependencies(limit: 10, lang: "csharp");
        Assert.Contains(dependencies, d => d.SourcePath == "src/V/AliasConsumer.cs" && d.TargetPath == "src/V/VerbatimAliasAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharpMetadataTargetResolverHandlesVerbatimBaseClassDeclaration()
    {
        // issue #435 codex review iter 7: the defining side uses a verbatim identifier in
        // the declaration itself (`public class @BaseAttr : Attribute`). Before iter 7 the
        // C# class-declaration regex only accepted `\w+` for the name capture, so this file
        // did not produce a class row at all. The deriving file's `class Verbatim : BaseAttr`
        // then had no in-repo target to resolve against and stayed `is_metadata_target=0`.
        // issue #435 codex review iter 7: 宣言側自体が verbatim（`public class @BaseAttr :
        // Attribute`）のケース。iter 7 以前の C# class 宣言 regex は name キャプチャが `\w+`
        // のみで、この file は class 行をまったく生成しなかった。その結果 `class Verbatim :
        // BaseAttr` 側も in-repo target を持てず `is_metadata_target=0` のままだった。
        InsertIndexedFile("src/V/VerbatimBase.cs", "csharp",
            """
            using System;

            namespace Foo.Bar
            {
                public class @BaseAttr : Attribute
                {
                }
            }
            """);
        InsertIndexedFile("src/V/VerbatimBaseTypeAttribute.cs", "csharp",
            """
            using Foo.Bar;

            namespace V
            {
                public class VerbatimBaseTypeAttribute : BaseAttr
                {
                }
            }
            """);
        InsertIndexedFile("src/V/VerbatimBaseConsumer.cs", "csharp",
            """
            namespace V
            {
                [VerbatimBaseType]
                public class VerbatimBaseConsumer
                {
                }
            }
            """);

        _writer.ResolveCSharpMetadataTargets();
        _writer.MarkMetadataTargetReady("csharp");
        var resolverReader = new DbReader(_db.Connection);

        using (var defnCmd = _db.Connection.CreateCommand())
        {
            // Verify the verbatim class declaration is persisted with its canonical name.
            // 宣言側 verbatim が canonical 名で永続化されていることを確認。
            defnCmd.CommandText = @"
                SELECT s.name
                FROM symbols s
                JOIN files f ON f.id = s.file_id
                WHERE f.path = 'src/V/VerbatimBase.cs' AND s.kind = 'class'";
            var defnName = defnCmd.ExecuteScalar() as string;
            Assert.Equal("BaseAttr", defnName);
        }

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = @"
            SELECT s.is_metadata_target
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE f.path = 'src/V/VerbatimBaseTypeAttribute.cs' AND s.kind = 'class' AND s.name = 'VerbatimBaseTypeAttribute'";
        var flag = cmd.ExecuteScalar();
        Assert.Equal(1L, Convert.ToInt64(flag));

        var dependencies = resolverReader.GetFileDependencies(limit: 10, lang: "csharp");
        Assert.Contains(dependencies, d => d.SourcePath == "src/V/VerbatimBaseConsumer.cs" && d.TargetPath == "src/V/VerbatimBaseTypeAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharpMetadataTargetResolverHandlesGlobalQualifiedVerbatimBase()
    {
        // issue #435 codex review iter 7: the consumer writes its base as
        // `global::@Foo.@Bar.BaseAttr`. iter 6's `StripCSharpVerbatimPrefixes` only handled
        // `.` boundaries, so after splitting into segments the first segment
        // `global::@Foo` kept its `@`, the later `global::` trim produced
        // `@Foo.Bar.BaseAttr`, and the qualified-index lookup missed the canonical key
        // `Foo.Bar.BaseAttr`. iter 7 teaches the helper about the `::` boundary.
        // issue #435 codex review iter 7: consumer が基底を `global::@Foo.@Bar.BaseAttr`
        // と書くケース。iter 6 の `StripCSharpVerbatimPrefixes` は `.` 境界しか扱わず、
        // 最初のセグメント `global::@Foo` の `@` が残り、後段の `global::` 剥がしを経て
        // `@Foo.Bar.BaseAttr` になって canonical なキー `Foo.Bar.BaseAttr` と一致しなかった。
        // iter 7 で helper が `::` 境界も処理するようになった。
        InsertIndexedFile("src/Foo/Bar/BaseAttr.cs", "csharp",
            """
            using System;

            namespace Foo.Bar
            {
                public class BaseAttr : Attribute
                {
                }
            }
            """);
        InsertIndexedFile("src/Q/QualifiedVerbatimNamespaceAttribute.cs", "csharp",
            """
            namespace Q
            {
                public class QualifiedVerbatimNamespaceAttribute : global::@Foo.@Bar.BaseAttr
                {
                }
            }
            """);
        InsertIndexedFile("src/Q/QualifiedVerbatimConsumer.cs", "csharp",
            """
            namespace Q
            {
                [QualifiedVerbatimNamespace]
                public class QualifiedVerbatimConsumer
                {
                }
            }
            """);

        _writer.ResolveCSharpMetadataTargets();
        _writer.MarkMetadataTargetReady("csharp");
        var resolverReader = new DbReader(_db.Connection);

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = @"
            SELECT s.is_metadata_target
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE f.path = 'src/Q/QualifiedVerbatimNamespaceAttribute.cs' AND s.kind = 'class' AND s.name = 'QualifiedVerbatimNamespaceAttribute'";
        var flag = cmd.ExecuteScalar();
        Assert.Equal(1L, Convert.ToInt64(flag));

        var dependencies = resolverReader.GetFileDependencies(limit: 10, lang: "csharp");
        Assert.Contains(dependencies, d => d.SourcePath == "src/Q/QualifiedVerbatimConsumer.cs" && d.TargetPath == "src/Q/QualifiedVerbatimNamespaceAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharpMetadataTargetResolverHandlesAliasQualifiedBase()
    {
        // issue #435 codex review iter 8: `using Alias = A;` followed by
        // `class FooAttribute : Alias.MetaBase` where `A.MetaBase : Attribute`.
        // Before the fix, the resolver entered the qualified branch (the base
        // contains `.`), looked up `Alias.MetaBase` in the qualified index —
        // which stores the real FQN `A.MetaBase` — found nothing, and fell
        // through to the BCL `Attribute`-suffix heuristic. `head = "MetaBase"`
        // does not end with `Attribute`, so the resolver returned false and
        // the metadata edge from `[Foo]` consumers was silently dropped even
        // though `MetaBase` is a real in-repo attribute.
        // issue #435 codex review iter 8: `using Alias = A;` の下で
        // `class FooAttribute : Alias.MetaBase` のパターン。修正前は resolver が
        // qualified 分岐に入り、qualified 索引を `Alias.MetaBase` で引いて miss し、
        // BCL サフィックス規約にフォールバックしたが `MetaBase` は `Attribute` で
        // 終わらないため false を返し、`[Foo]` consumer の metadata edge が黙って
        // 落ちていた。
        InsertIndexedFile("src/A/MetaBase.cs", "csharp",
            """
            namespace A
            {
                public class MetaBase : System.Attribute
                {
                }
            }
            """);
        InsertIndexedFile("src/B/FooAttribute.cs", "csharp",
            """
            using Alias = A;
            namespace B
            {
                public class FooAttribute : Alias.MetaBase
                {
                }
            }
            """);
        InsertIndexedFile("src/B/Svc.cs", "csharp",
            """
            namespace B
            {
                [Foo]
                public class Svc
                {
                }
            }
            """);

        _writer.ResolveCSharpMetadataTargets();
        _writer.MarkMetadataTargetReady("csharp");
        var resolverReader = new DbReader(_db.Connection);

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = @"
            SELECT s.is_metadata_target
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE f.path = 'src/B/FooAttribute.cs' AND s.kind = 'class' AND s.name = 'FooAttribute'";
        var flag = cmd.ExecuteScalar();
        Assert.Equal(1L, Convert.ToInt64(flag));

        var dependencies = resolverReader.GetFileDependencies(limit: 10, lang: "csharp");
        Assert.Contains(dependencies, d => d.SourcePath == "src/B/Svc.cs" && d.TargetPath == "src/B/FooAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharpMetadataTargetResolverHandlesAliasNamespacePointingAtSystem()
    {
        // issue #435 codex review iter 8: `using Sys = System;` followed by
        // `class Foo : Sys.Attribute`. After alias expansion the base is
        // `System.Attribute`, which must trigger the direct-attribute rule
        // rather than fall through to the qualified-index lookup (which
        // would miss `System.Attribute` since System is external to the repo).
        // issue #435 codex review iter 8: `using Sys = System;` + `class Foo :
        // Sys.Attribute` は alias 展開後に `System.Attribute` となり、修飾索引を
        // 引く前に直接 Attribute 派生ルールで拾わなければならない。
        InsertIndexedFile("src/Svc.cs", "csharp",
            """
            using Sys = System;
            namespace Svc
            {
                public class FooAttribute : Sys.Attribute
                {
                }
            }
            """);
        InsertIndexedFile("src/Usage.cs", "csharp",
            """
            namespace Svc
            {
                [Foo]
                public class Usage
                {
                }
            }
            """);

        _writer.ResolveCSharpMetadataTargets();
        _writer.MarkMetadataTargetReady("csharp");
        var resolverReader = new DbReader(_db.Connection);

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = @"
            SELECT s.is_metadata_target
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE f.path = 'src/Svc.cs' AND s.kind = 'class' AND s.name = 'FooAttribute'";
        var flag = cmd.ExecuteScalar();
        Assert.Equal(1L, Convert.ToInt64(flag));

        var dependencies = resolverReader.GetFileDependencies(limit: 10, lang: "csharp");
        Assert.Contains(dependencies, d => d.SourcePath == "src/Usage.cs" && d.TargetPath == "src/Svc.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharpMetadataTargetResolverHandlesAliasColonColonQualifiedBase()
    {
        // issue #435 codex review iter 9: C# allows both `Alias.X` (member access)
        // and `Alias::X` (qualified-alias-member, §7.8) for a using alias that
        // names a namespace. Iter 8 only taught the qualified branch to split on
        // `.`, so `class FooAttribute : Alias::MetaBase` under `using Alias = A;`
        // skipped the alias expansion entirely (the helper's IndexOf('.') returned
        // -1 and bailed), fell through to the BCL suffix heuristic, and dropped
        // the `[Foo] -> FooAttribute` metadata edge even though `A.MetaBase :
        // Attribute` lives in the repo.
        // issue #435 codex review iter 9: C# では using alias が名前空間を指す場合、
        // `Alias.X` と `Alias::X` の両方が合法。iter 8 は `.` 区切りしか扱わなかった
        // ため `using Alias = A;` 配下の `class FooAttribute : Alias::MetaBase` は
        // alias 展開に入らず（helper の IndexOf('.') が -1 で即 return）、BCL サフィ
        // ックス規約に落ちて `[Foo] -> FooAttribute` edge が落ちていた。
        InsertIndexedFile("src/A/MetaBase.cs", "csharp",
            """
            namespace A
            {
                public class MetaBase : System.Attribute
                {
                }
            }
            """);
        InsertIndexedFile("src/B/FooAttribute.cs", "csharp",
            """
            using Alias = A;
            namespace B
            {
                public class FooAttribute : Alias::MetaBase
                {
                }
            }
            """);
        InsertIndexedFile("src/B/Svc.cs", "csharp",
            """
            namespace B
            {
                [Foo]
                public class Svc
                {
                }
            }
            """);

        _writer.ResolveCSharpMetadataTargets();
        _writer.MarkMetadataTargetReady("csharp");
        var resolverReader = new DbReader(_db.Connection);

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = @"
            SELECT s.is_metadata_target
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE f.path = 'src/B/FooAttribute.cs' AND s.kind = 'class' AND s.name = 'FooAttribute'";
        var flag = cmd.ExecuteScalar();
        Assert.Equal(1L, Convert.ToInt64(flag));

        var dependencies = resolverReader.GetFileDependencies(limit: 10, lang: "csharp");
        Assert.Contains(dependencies, d => d.SourcePath == "src/B/Svc.cs" && d.TargetPath == "src/B/FooAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharpMetadataTargetReaderFallsBackToNameSuffixWhenColumnMissing()
    {
        // issue #435 codex review iter 1: reader branch (3) — when the entire
        // `is_metadata_target` column is absent (truly ancient legacy DB that the
        // current binary is opening read-only), the reader must degrade to the
        // `name LIKE '%Attribute'` fallback. Pre-fix, branch (2) only required the
        // `signature` column, so a column-missing DB still ran the signature
        // heuristic — contradicting the documented 3-way branch.
        // issue #435 codex review iter 1: reader branch (3) — `is_metadata_target` 列
        // 自体が無い古い legacy DB では命名規約のみに縮退するべき。修正前は branch (2)
        // が `signature` 列の有無だけで判定され、column 欠落 DB でも signature
        // ヒューリスティックに落ちて 3 way 分岐のドキュメントと食い違っていた。
        InsertIndexedFile("src/RealFooAttribute.cs", "csharp",
            """
            using System;

            [AttributeUsage(AttributeTargets.Class)]
            public sealed class FooAttribute : Attribute
            {
            }
            """);
        InsertIndexedFile("src/ImpostorFooAttribute.cs", "csharp",
            """
            public class FooAttribute : BaseService
            {
            }
            """);
        InsertIndexedFile("src/Svc.cs", "csharp",
            """
            [Foo]
            public class Svc
            {
            }
            """);

        // Drop the `is_metadata_target` column to simulate a read-only legacy DB that
        // the current binary cannot in-place migrate. SQLite supports DROP COLUMN
        // since 3.35 (we target 3.39+ via Microsoft.Data.Sqlite).
        // `is_metadata_target` 列を落として、in-place 移行できない古い read-only 相当の
        // DB を模擬する。DROP COLUMN は SQLite 3.35 以降でサポート。
        using (var drop = _db.Connection.CreateCommand())
        {
            drop.CommandText = "ALTER TABLE symbols DROP COLUMN is_metadata_target";
            drop.ExecuteNonQuery();
        }

        var legacyReader = new DbReader(_db.Connection, isReadOnly: true);
        var dependencies = legacyReader.GetFileDependencies(limit: 10, lang: "csharp");

        // Both FooAttribute files match `name LIKE '%Attribute'`, so without
        // signature-shape disambiguation the ambiguity suppresses the deps edge.
        // 命名規約のみでは 2 つの同名 FooAttribute が候補になり、曖昧さでエッジ抑制。
        Assert.DoesNotContain(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/RealFooAttribute.cs");
        Assert.DoesNotContain(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/ImpostorFooAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharpAttributeAliasOnlyMatchesClassLikeTargets()
    {
        // issue #293 review: the C# attribute suffix alias UNION synthesizes a
        // `FooAttribute` lookup key for `[Foo]` references. Without a kind guard the
        // subsequent name-only join would spuriously attribute the consumer to any
        // file that merely defines a function / property / variable also named
        // `FooAttribute`. Only class-like target symbols should match synthetic alias
        // rows.
        // issue #293 レビュー指摘: `[Foo]` 用の alias UNION は `FooAttribute` という
        // lookup key を合成するが、kind によるガードが無いと、偶然 `FooAttribute`
        // という名前を持つ関数 / プロパティ / 変数を含むファイルにまで依存が張られて
        // しまう。合成 alias 行は class 系の target にのみ一致すべき。
        InsertIndexedFile("src/MyAuditAttribute.cs", "csharp",
            """
            using System;

            [AttributeUsage(AttributeTargets.Class)]
            public sealed class MyAuditAttribute : Attribute
            {
            }
            """);
        // Unrelated file containing a function named `MyAuditAttribute` — not an
        // attribute class, so `[MyAudit]` must not produce a dependency edge to it.
        // 無関係なファイルに関数として `MyAuditAttribute` が居るケース。
        // `[MyAudit]` はこのファイルへの依存を作ってはいけない。
        InsertIndexedFile("src/Util.cs", "csharp",
            """
            public static class Util
            {
                public static void MyAuditAttribute()
                {
                }
            }
            """);
        InsertIndexedFile("src/Svc.cs", "csharp",
            """
            [MyAudit]
            public class Svc
            {
            }
            """);

        var dependencies = _reader.GetFileDependencies(limit: 20, lang: "csharp");

        Assert.Contains(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/MyAuditAttribute.cs");
        Assert.DoesNotContain(dependencies, d => d.SourcePath == "src/Svc.cs" && d.TargetPath == "src/Util.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharp_PlainClassWithAttributeSuffixName_DoesNotCountAsAmbiguity()
    {
        // issue #293 round-16: same metadata-eligibility filter must apply to
        // the `deps` command. target_files.has_metadata_target_kind and the
        // target_ambiguity JOIN both require C# class-like targets to inherit
        // from an Attribute-suffixed base, so a plain `MyAuditAttribute`
        // cannot ambiguate the edge from `Svc.cs` to the real attribute class.
        // issue #293 round-16: 同じ適格性フィルタを deps にも適用する。
        // target_files.has_metadata_target_kind と target_ambiguity JOIN は
        // C# では Attribute suffix 継承を要求するため、plain `MyAuditAttribute` が
        // 存在しても実 attribute クラスへのエッジは残る。
        InsertIndexedFile("src/Real/MyAuditAttribute.cs", "csharp",
            """
            namespace Real;

            public sealed class MyAuditAttribute : System.Attribute
            {
            }
            """);
        InsertIndexedFile("src/Unrelated/MyAuditAttribute.cs", "csharp",
            """
            namespace Unrelated;

            public sealed class MyAuditAttribute
            {
            }
            """);
        InsertIndexedFile("src/Real/Svc.cs", "csharp",
            """
            namespace Real;

            [MyAudit]
            public class Svc
            {
            }
            """);

        var deps = _reader.GetFileDependencies(
            limit: 50,
            lang: "csharp");

        Assert.Contains(deps, d =>
            d.SourcePath == "src/Real/Svc.cs" &&
            d.TargetPath == "src/Real/MyAuditAttribute.cs");
        Assert.DoesNotContain(deps, d =>
            d.SourcePath == "src/Real/Svc.cs" &&
            d.TargetPath == "src/Unrelated/MyAuditAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharpNestedGenericNoArgAttribute_ResolvesToAttributeClass()
    {
        // issue #293 round-16: the no-arg C# attribute regex must handle
        // nested generic type arguments (e.g. `[MyAttr<Dictionary<string, int>>]`).
        // Previously the inner `<...>` segment excluded `>`, which broke on the
        // first inner `>` and classified the reference as a call.
        // issue #293 round-16: 引数なし C# 属性 regex が
        // `[MyAttr<Dictionary<string, int>>]` のようなネスト generic を
        // 扱えること。以前は内側の `<...>` セグメントが `>` を除外していて、
        // 最初の内側 `>` で崩れて call として誤分類されていた。
        InsertIndexedFile("src/MyAttrAttribute.cs", "csharp",
            """
            using System;
            using System.Collections.Generic;

            public sealed class MyAttrAttribute : Attribute
            {
            }
            """);
        InsertIndexedFile("src/Svc.cs", "csharp",
            """
            using System.Collections.Generic;

            [MyAttr<Dictionary<string, int>>]
            public class Svc
            {
            }
            """);

        var deps = _reader.GetFileDependencies(
            limit: 50,
            lang: "csharp");

        Assert.Contains(deps, d =>
            d.SourcePath == "src/Svc.cs" &&
            d.TargetPath == "src/MyAttrAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharp_IndirectAttributeInheritance_ResolvesAsMetadataTarget()
    {
        // issue #293 round-17: the metadata-eligibility filter must not require
        // the immediate base class to end in `Attribute`. Indirect inheritance
        // like `class MyAuditAttribute : BaseAudit` where `BaseAudit : Attribute`
        // is a valid `[MyAudit]` target at compile time. The previous strict
        // pattern (`signature LIKE '%: %Attribute%'`) wrongly excluded the
        // indirectly-derived class and dropped the deps edge. The loosened
        // pattern (`signature LIKE '%: %'`) accepts any class with an
        // inheritance clause, which is the best portable approximation since
        // SQL cannot resolve base types transitively.
        // issue #293 round-17: metadata 適格性フィルタは直接基底が
        // `Attribute` で終わることを要求してはならない。
        // `class MyAuditAttribute : BaseAudit` で `BaseAudit : Attribute` の
        // ような間接継承も `[MyAudit]` の有効な target である。以前の
        // 厳格パターンは間接継承を弾いて deps エッジを落としていた。
        // 緩和パターンは「継承節を持つ class」を近似として採用する。
        InsertIndexedFile("src/BaseAudit.cs", "csharp",
            """
            namespace App;

            public abstract class BaseAudit : System.Attribute
            {
            }
            """);
        InsertIndexedFile("src/MyAuditAttribute.cs", "csharp",
            """
            namespace App;

            public sealed class MyAuditAttribute : BaseAudit
            {
            }
            """);
        InsertIndexedFile("src/Svc.cs", "csharp",
            """
            namespace App;

            [MyAudit]
            public class Svc
            {
            }
            """);

        var deps = _reader.GetFileDependencies(
            limit: 50,
            lang: "csharp");

        Assert.Contains(deps, d =>
            d.SourcePath == "src/Svc.cs" &&
            d.TargetPath == "src/MyAuditAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_JavaScriptFunctionDecorator_ResolvesAsDependency()
    {
        // issue #293 round-18: JavaScript/TypeScript decorators legitimately target
        // factory `function`s (e.g. `function sealed(target) { ... }`), not only
        // class-like definitions. The metadata-target predicate must accept
        // `function` for JS/TS or decorator edges to a function target are dropped
        // from `deps`.
        // issue #293 round-18: JS/TS decorator は `function sealed(target){...}` のような
        // factory 関数も正当な target となる。JS/TS では `function` を metadata target の
        // 対象 kind として許可しないと、function を対象とする decorator edge が deps から欠落する。
        InsertIndexedFile("src/decorators.js", "javascript",
            """
            export function sealed(target) {
                Object.freeze(target);
            }
            """);
        InsertIndexedFile("src/model.js", "javascript",
            """
            import { sealed } from './decorators.js';

            @sealed
            class Foo {
            }
            """);

        var deps = _reader.GetFileDependencies(
            limit: 50,
            lang: "javascript");

        Assert.Contains(deps, d =>
            d.SourcePath == "src/model.js" &&
            d.TargetPath == "src/decorators.js");
    }

    [Fact]
    public void GetFileDependencies_CSharp_LegacyDbWithNullSignature_StillResolvesAttributeEdge()
    {
        // issue #293 round-19: the metadata-target signature clause must degrade
        // gracefully when the `symbols.signature` column exists but individual
        // rows carry NULL values — the common shape of a DB whose schema was
        // migrated in place (`TryMigrateForRead`) without reindexing. Requiring
        // `signature LIKE '%: %'` would silently drop the real
        // `[MyAudit]` → `class MyAuditAttribute : System.Attribute` edge there,
        // so the clause must treat NULL signature as eligible (equivalent to the
        // column-missing `1 = 1` fallback).
        // issue #293 round-19: metadata-target の signature 句は、列は存在するが
        // row の値が NULL の legacy-migration DB でも degrade する必要がある。
        // LIKE を強要すると本物の `[MyAudit]` edge が silent に落ちる。
        // 列欠落時の `1 = 1` fallback と同じく NULL も eligible にする。
        InsertIndexedFile("src/MyAuditAttribute.cs", "csharp",
            """
            public sealed class MyAuditAttribute : System.Attribute
            {
            }
            """);
        InsertIndexedFile("src/Svc.cs", "csharp",
            """
            [MyAudit]
            public class Svc
            {
            }
            """);

        // Simulate the partial-migration shape: signature column is present but the
        // C# class row has a NULL signature, as if the schema were upgraded in place
        // without re-running extraction.
        // partial-migration の形を再現: signature 列はあるが C# class 行の signature が
        // NULL の状態 — その場 schema 移行後に再抽出していない DB と同じ。
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "UPDATE symbols SET signature = NULL WHERE name = 'MyAuditAttribute' AND kind = 'class'";
            cmd.ExecuteNonQuery();
        }

        var deps = _reader.GetFileDependencies(
            limit: 50,
            lang: "csharp");

        Assert.Contains(deps, d =>
            d.SourcePath == "src/Svc.cs" &&
            d.TargetPath == "src/MyAuditAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_CSharp_LegacyDbWithNullSignature_NonAttributeName_DoesNotBlockMetadataEdge()
    {
        // issue #293 round-20: the NULL-signature fallback must not treat
        // arbitrary classes as metadata targets. Before this round the clause
        // accepted `signature IS NULL` for every C# `class`, so on a legacy-migration
        // DB a non-attribute class named `HelperClient` could share a name with an
        // attribute-applied site and silently inject false ambiguity. The tightened
        // fallback requires the canonical C# attribute naming convention
        // (`name LIKE '%Attribute'`), so a NULL-signature `HelperClient` is no
        // longer counted and the real `[MyAudit]` edge to `MyAuditAttribute`
        // survives even when both rows have NULL signatures.
        // issue #293 round-20: NULL-signature フォールバックが任意の class を
        // metadata target 扱いしないこと。以前は legacy-migration DB で
        // `signature IS NULL` のすべての C# class を許容しており、attribute 名と
        // 同名の非 attribute class (`HelperClient`) が偽の曖昧さを発生させ得た。
        // 新しいフォールバックは C# の命名規約 `name LIKE '%Attribute'` を要求する
        // ため、NULL-sig かつ非 *Attribute 名の class は候補から外れ、本物の
        // `[MyAudit]` → `MyAuditAttribute` edge は両行の signature が NULL でも
        // 残る。
        InsertIndexedFile("src/MyAuditAttribute.cs", "csharp",
            """
            public sealed class MyAuditAttribute : System.Attribute
            {
            }
            """);
        InsertIndexedFile("src/HelperClient.cs", "csharp",
            """
            namespace Unrelated;

            public class HelperClient : BaseService
            {
            }
            """);
        InsertIndexedFile("src/Svc.cs", "csharp",
            """
            [MyAudit]
            public class Svc
            {
            }
            """);

        // Simulate partial-migration: all C# class rows have NULL signature.
        // partial-migration 再現: すべての C# class 行の signature を NULL 化。
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "UPDATE symbols SET signature = NULL WHERE kind = 'class'";
            cmd.ExecuteNonQuery();
        }

        var deps = _reader.GetFileDependencies(
            limit: 50,
            lang: "csharp");

        Assert.Contains(deps, d =>
            d.SourcePath == "src/Svc.cs" &&
            d.TargetPath == "src/MyAuditAttribute.cs");
    }

    [Fact]
    public void GetFileDependencies_JavaScript_SameNameInterface_DoesNotBlockFunctionDecoratorEdge()
    {
        // issue #293 round-20: TypeScript `interface` is a compile-time type-only
        // construct and cannot be a runtime decorator target, so a same-name
        // `interface` must NOT count toward metadata-target ambiguity against a
        // real `function` provider. The metadata-target predicate for JS/TS
        // therefore restricts candidate kinds to `class` and `function` only.
        // issue #293 round-20: TS の `interface` はコンパイル時型のため runtime
        // decorator target になれない。同名 `interface` が本物の `function`
        // provider への decorator edge を潰さないよう、JS/TS の metadata-target
        // 候補 kind は `class` と `function` に限定する。
        InsertIndexedFile("src/decorators.ts", "typescript",
            """
            export function sealed(target: any): void {
                Object.freeze(target);
            }
            """);
        InsertIndexedFile("src/types.ts", "typescript",
            """
            export interface sealed {
                readonly frozen: boolean;
            }
            """);
        InsertIndexedFile("src/model.ts", "typescript",
            """
            import { sealed } from './decorators';

            @sealed
            class Foo {
            }
            """);

        var deps = _reader.GetFileDependencies(
            limit: 50,
            lang: "typescript");

        Assert.Contains(deps, d =>
            d.SourcePath == "src/model.ts" &&
            d.TargetPath == "src/decorators.ts");
    }

    [Fact]
    public void GetFileDependencies_CSharp_SameNameInterface_DoesNotBlockMetadataEdge()
    {
        // issue #293 round-18: ambiguity should only count truly attribute-eligible
        // duplicates. In C#, only `class` can inherit from `System.Attribute` —
        // a same-named `interface` or `struct` cannot be an attribute target, so it
        // must not suppress the metadata deps edge to the legitimate attribute class.
        // issue #293 round-18: ambiguity 判定は attribute 適格な重複だけを数えるべき。
        // C# では `class` のみが `System.Attribute` を継承できるため、同名の
        // `interface` や `struct` が存在しても metadata deps edge を抑止してはならない。
        InsertIndexedFile("src/MyAuditAttribute.cs", "csharp",
            """
            public sealed class MyAuditAttribute : System.Attribute
            {
            }
            """);
        InsertIndexedFile("src/IMyAudit.cs", "csharp",
            """
            public interface MyAuditAttribute
            {
            }
            """);
        InsertIndexedFile("src/Svc.cs", "csharp",
            """
            [MyAudit]
            public class Svc
            {
            }
            """);

        var deps = _reader.GetFileDependencies(
            limit: 50,
            lang: "csharp");

        Assert.Contains(deps, d =>
            d.SourcePath == "src/Svc.cs" &&
            d.TargetPath == "src/MyAuditAttribute.cs");
    }
}
