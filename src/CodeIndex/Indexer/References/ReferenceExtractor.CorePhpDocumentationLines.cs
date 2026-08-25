using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private readonly record struct CorePhpDocumentationLineContext(
        long FileId,
        string OriginalLine,
        int LineNumber,
        List<ReferenceRecord> References,
        ReferenceDedupeSet Seen,
        InnermostContainerResolver ContainerResolver);

    private struct PhpDocumentationState
    {
        internal bool InDocblock;
        internal SymbolRecord? DocblockContainer;
        internal HashSet<string>? DocblockPropertyNames;
    }

    private struct PhpLineContainerCache
    {
        private readonly InnermostContainerResolver _resolver;
        private readonly int _lineNumber;
        private bool _resolved;
        private SymbolRecord? _container;

        internal PhpLineContainerCache(
            InnermostContainerResolver resolver,
            int lineNumber)
        {
            _resolver = resolver;
            _lineNumber = lineNumber;
        }

        internal SymbolRecord? Resolve()
        {
            if (!_resolved)
            {
                _container = _resolver.Find(_lineNumber);
                _resolved = true;
            }

            return _container;
        }
    }

    private static void EmitPhpDocumentationReferences(
        in CorePhpDocumentationLineContext line,
        ref PhpDocumentationState state)
    {
        var containerCache = new PhpLineContainerCache(
            line.ContainerResolver,
            line.LineNumber);
        EmitPhpAttributeDocumentationReferences(in line, ref containerCache);
        BeginPhpDocblockIfNeeded(in line, ref state, ref containerCache);

        var context = line.OriginalLine;
        if (!string.IsNullOrWhiteSpace(context))
        {
            EmitPhpDocblockSignatureReferences(in line, context, ref state, ref containerCache);
            EmitPhpDocblockInheritanceReferences(in line, context, ref state, ref containerCache);
            EmitPhpDocblockPropertyReferences(in line, context, ref state, ref containerCache);
            EmitPhpDocblockMethodReferences(in line, context, ref state, ref containerCache);
            EmitPhpDocblockTemplateAndAliasReferences(in line, context, ref state, ref containerCache);
        }

        EndPhpDocblockIfNeeded(in line, ref state);
    }

    private static void EmitPhpAttributeDocumentationReferences(
        in CorePhpDocumentationLineContext line,
        ref PhpLineContainerCache containerCache)
    {
        if (!line.OriginalLine.Contains("#[", StringComparison.Ordinal))
            return;

        var context = line.OriginalLine;
        if (string.IsNullOrWhiteSpace(context))
            return;

        PhpReferenceExtractor.EmitAttributeReferences(
            line.OriginalLine, line.References, line.Seen, line.FileId, context,
            line.LineNumber, containerCache.Resolve());
    }

    private static void BeginPhpDocblockIfNeeded(
        in CorePhpDocumentationLineContext line,
        ref PhpDocumentationState state,
        ref PhpLineContainerCache containerCache)
    {
        if (line.OriginalLine.IndexOf("/**", StringComparison.Ordinal) < 0)
            return;

        state.InDocblock = true;
        state.DocblockContainer = containerCache.Resolve();
        state.DocblockPropertyNames = null;
    }

    private static void EmitPhpDocblockSignatureReferences(
        in CorePhpDocumentationLineContext line,
        string context,
        ref PhpDocumentationState state,
        ref PhpLineContainerCache containerCache)
    {
        if (line.OriginalLine.Contains("param", StringComparison.OrdinalIgnoreCase))
            PhpReferenceExtractor.EmitDocblockParamTypeReferences(
                line.OriginalLine, line.References, line.Seen, line.FileId, context,
                line.LineNumber, ResolvePhpDocblockContainer(ref state, ref containerCache));
        if (line.OriginalLine.Contains("return", StringComparison.OrdinalIgnoreCase))
            PhpReferenceExtractor.EmitDocblockReturnTypeReferences(
                line.OriginalLine, line.References, line.Seen, line.FileId, context,
                line.LineNumber, ResolvePhpDocblockContainer(ref state, ref containerCache));
        if (line.OriginalLine.Contains("var", StringComparison.OrdinalIgnoreCase))
            PhpReferenceExtractor.EmitDocblockVarTypeReferences(
                line.OriginalLine, line.References, line.Seen, line.FileId, context,
                line.LineNumber, ResolvePhpDocblockContainer(ref state, ref containerCache));
        if (line.OriginalLine.Contains("@throws", StringComparison.OrdinalIgnoreCase))
            PhpReferenceExtractor.EmitDocblockThrowsTypeReferences(
                line.OriginalLine, line.References, line.Seen, line.FileId, context,
                line.LineNumber, ResolvePhpDocblockContainer(ref state, ref containerCache));
    }

    private static void EmitPhpDocblockInheritanceReferences(
        in CorePhpDocumentationLineContext line,
        string context,
        ref PhpDocumentationState state,
        ref PhpLineContainerCache containerCache)
    {
        if (line.OriginalLine.Contains("extends", StringComparison.OrdinalIgnoreCase))
            PhpReferenceExtractor.EmitDocblockExtendsTypeReferences(
                line.OriginalLine, line.References, line.Seen, line.FileId, context,
                line.LineNumber, ResolvePhpDocblockContainer(ref state, ref containerCache));
        if (line.OriginalLine.Contains("implements", StringComparison.OrdinalIgnoreCase))
            PhpReferenceExtractor.EmitDocblockImplementsTypeReferences(
                line.OriginalLine, line.References, line.Seen, line.FileId, context,
                line.LineNumber, ResolvePhpDocblockContainer(ref state, ref containerCache));
        if (line.OriginalLine.Contains("@mixin", StringComparison.OrdinalIgnoreCase))
            PhpReferenceExtractor.EmitDocblockMixinTypeReferences(
                line.OriginalLine, line.References, line.Seen, line.FileId, context,
                line.LineNumber, ResolvePhpDocblockContainer(ref state, ref containerCache));
    }

    private static void EmitPhpDocblockPropertyReferences(
        in CorePhpDocumentationLineContext line,
        string context,
        ref PhpDocumentationState state,
        ref PhpLineContainerCache containerCache)
    {
        if (!line.OriginalLine.Contains("property", StringComparison.OrdinalIgnoreCase))
            return;

        PhpReferenceExtractor.EmitDocblockPropertyTypeReferences(
            line.OriginalLine, line.References, line.Seen, line.FileId, context,
            line.LineNumber, ResolvePhpDocblockContainer(ref state, ref containerCache),
            state.InDocblock, ref state.DocblockPropertyNames);
    }

    private static void EmitPhpDocblockMethodReferences(
        in CorePhpDocumentationLineContext line,
        string context,
        ref PhpDocumentationState state,
        ref PhpLineContainerCache containerCache)
    {
        if (!line.OriginalLine.Contains("@method", StringComparison.OrdinalIgnoreCase))
            return;

        PhpReferenceExtractor.EmitDocblockMethodReturnTypeReferences(
            line.OriginalLine, line.References, line.Seen, line.FileId, context,
            line.LineNumber, ResolvePhpDocblockContainer(ref state, ref containerCache));
        PhpReferenceExtractor.EmitDocblockMethodParameterTypeReferences(
            line.OriginalLine, line.References, line.Seen, line.FileId, context,
            line.LineNumber, ResolvePhpDocblockContainer(ref state, ref containerCache));
    }

    private static void EmitPhpDocblockTemplateAndAliasReferences(
        in CorePhpDocumentationLineContext line,
        string context,
        ref PhpDocumentationState state,
        ref PhpLineContainerCache containerCache)
    {
        if (line.OriginalLine.Contains("@template", StringComparison.OrdinalIgnoreCase))
            PhpReferenceExtractor.EmitDocblockTemplateBoundTypeReferences(
                line.OriginalLine, line.References, line.Seen, line.FileId, context,
                line.LineNumber, ResolvePhpDocblockContainer(ref state, ref containerCache));
        if (!line.OriginalLine.Contains("type", StringComparison.OrdinalIgnoreCase))
            return;

        PhpReferenceExtractor.EmitDocblockTypeAliasTargetReferences(
            line.OriginalLine, line.References, line.Seen, line.FileId, context,
            line.LineNumber, ResolvePhpDocblockContainer(ref state, ref containerCache));
        PhpReferenceExtractor.EmitDocblockImportTypeSourceReferences(
            line.OriginalLine, line.References, line.Seen, line.FileId, context,
            line.LineNumber, ResolvePhpDocblockContainer(ref state, ref containerCache));
    }

    private static SymbolRecord? ResolvePhpDocblockContainer(
        ref PhpDocumentationState state,
        ref PhpLineContainerCache containerCache)
        => state.InDocblock ? state.DocblockContainer : containerCache.Resolve();

    private static void EndPhpDocblockIfNeeded(
        in CorePhpDocumentationLineContext line,
        ref PhpDocumentationState state)
    {
        if (!state.InDocblock
            || line.OriginalLine.IndexOf("*/", StringComparison.Ordinal) < 0)
        {
            return;
        }

        state.InDocblock = false;
        state.DocblockContainer = null;
        state.DocblockPropertyNames = null;
    }
}
