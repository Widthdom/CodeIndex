using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public partial class ReferenceExtractorTests
{
    [Fact]
    public void Extract_DockerfileFromStageReferences_IndexNamedStagesAndIgnoreBaseImages()
    {
        const string content = """
            FROM golang:1.21 AS builder

            FROM builder AS build2

            FROM alpine:3.20
            """;

        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);
        var references = ReferenceExtractor.Extract(1, "dockerfile", content, symbols);

        Assert.Single(references.Where(reference =>
            reference.SymbolName == "builder"
            && reference.ReferenceKind == "call"));
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "alpine:3.20");
    }

    [Fact]
    public void Extract_DockerfileFromStageReferences_IndexLowercaseInstructions()
    {
        const string content = """
            from golang:1.21 as builder

            from builder as build2

            copy --from=builder /src/app /usr/local/bin/app

            from alpine:3.20
            """;

        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);
        var references = ReferenceExtractor.Extract(1, "dockerfile", content, symbols);

        Assert.Equal(2, references.Count(reference =>
            reference.SymbolName == "builder"
            && reference.ReferenceKind == "call"));
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "alpine:3.20");
    }

    [Fact]
    public void Extract_DockerfileCopyFromReferences_IndexStageDependencies()
    {
        const string content = """
            FROM golang:1.21 AS builder

            FROM debian:bookworm-slim AS runner

            COPY --from=builder /src/app /usr/local/bin/app
            COPY --from=builder /src/assets /opt/assets

            FROM runner AS final
            """;

        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);
        var references = ReferenceExtractor.Extract(1, "dockerfile", content, symbols);

        Assert.Equal(2, references.Count(reference =>
            reference.SymbolName == "builder"
            && reference.ReferenceKind == "call"));
        Assert.Single(references.Where(reference =>
            reference.SymbolName == "runner"
            && reference.ReferenceKind == "call"));
    }

    [Fact]
    public void Extract_DockerfileFromStageReferences_IndexPlatformFlaggedStages()
    {
        const string content = """
            FROM golang:1.21 AS builder

            FROM --platform=$BUILDPLATFORM builder AS final
            COPY --from=builder /src/app /usr/local/bin/app
            """;

        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);
        var references = ReferenceExtractor.Extract(1, "dockerfile", content, symbols);

        Assert.Equal(2, references.Count(reference =>
            reference.SymbolName == "builder"
            && reference.ReferenceKind == "call"));
    }

    [Fact]
    public void Extract_DockerfileReferences_IndexHyphenatedStageNames()
    {
        const string content = """
            FROM node:20 AS build-env

            FROM build-env AS runtime
            COPY --from=build-env /src/app /usr/local/bin/app
            """;

        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);
        var references = ReferenceExtractor.Extract(1, "dockerfile", content, symbols);

        Assert.Equal(2, references.Count(reference =>
            reference.SymbolName == "build-env"
            && reference.ReferenceKind == "call"));
    }

    [Fact]
    public void Extract_DockerfileReferences_IndexDottedStageNames()
    {
        const string content = """
            FROM node:20 AS build.env

            FROM build.env AS runtime
            COPY --from=build.env /src/app /usr/local/bin/app
            """;

        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);
        var references = ReferenceExtractor.Extract(1, "dockerfile", content, symbols);

        Assert.Equal(2, references.Count(reference =>
            reference.SymbolName == "build.env"
            && reference.ReferenceKind == "call"));
    }

    [Fact]
    public void Extract_DockerfileRunMountReferences_IndexStageDependencies()
    {
        const string content = """
            FROM alpine AS assets

            FROM alpine AS runtime
            RUN --mount=type=bind,from=assets,target=/mnt/assets cp -r /mnt/assets /app/assets
            """;

        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);
        var references = ReferenceExtractor.Extract(1, "dockerfile", content, symbols);

        Assert.Single(references.Where(reference =>
            reference.SymbolName == "assets"
            && reference.ReferenceKind == "call"));
    }

    [Fact]
    public void Extract_DockerfileRunMountReferences_IndexMultipleStageDependencies()
    {
        const string content = """
            FROM alpine AS assets
            FROM alpine AS cache

            FROM alpine AS runtime
            RUN --mount=type=bind,from=assets,target=/assets --mount=type=bind,from=cache,target=/cache true
            """;

        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);
        var references = ReferenceExtractor.Extract(1, "dockerfile", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "assets"
            && reference.ReferenceKind == "call");
        Assert.Contains(references, reference =>
            reference.SymbolName == "cache"
            && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_DockerfileRunMountReferences_IndexQuotedStageDependencies()
    {
        const string content = """
            FROM alpine AS assets

            FROM alpine AS runtime
            RUN --mount=type=bind,from="assets",target=/mnt/assets cp -r /mnt/assets /app/assets
            """;

        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);
        var references = ReferenceExtractor.Extract(1, "dockerfile", content, symbols);

        Assert.Single(references.Where(reference =>
            reference.SymbolName == "assets"
            && reference.ReferenceKind == "call"));
    }

    [Fact]
    public void Extract_DockerfileRunMountReferences_IndexOnbuildStageDependencies()
    {
        const string content = """
            FROM alpine AS assets

            FROM alpine AS runtime
            ONBUILD RUN --mount=type=bind,from=assets,target=/mnt/assets cp -r /mnt/assets /app/assets
            """;

        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);
        var references = ReferenceExtractor.Extract(1, "dockerfile", content, symbols);

        Assert.Single(references.Where(reference =>
            reference.SymbolName == "assets"
            && reference.ReferenceKind == "call"));
    }

    [Fact]
    public void Extract_DockerfileRunMountReferences_IgnoresQuotedShellText()
    {
        const string content = """
            FROM alpine AS assets

            FROM alpine AS runtime
            RUN echo "--mount=type=bind,from=assets,target=/mnt/assets"
            """;

        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);
        var references = ReferenceExtractor.Extract(1, "dockerfile", content, symbols);

        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "assets"
            && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_DockerfileRunMountReferences_IgnoresCommandArguments()
    {
        const string content = """
            FROM alpine AS assets

            FROM alpine AS runtime
            RUN echo --mount=type=bind,from=assets,target=/mnt/assets
            """;

        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);
        var references = ReferenceExtractor.Extract(1, "dockerfile", content, symbols);

        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "assets"
            && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_DockerfileCopyFromReferences_IgnoresTaggedExternalImages()
    {
        const string content = """
            FROM alpine AS builder

            FROM alpine AS runtime
            COPY --from=builder:latest /src/app /usr/local/bin/app
            """;

        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);
        var references = ReferenceExtractor.Extract(1, "dockerfile", content, symbols);

        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "builder"
            && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_DockerfileCopyFromReferences_IgnoresDigestExternalImages()
    {
        const string content = """
            FROM alpine AS builder

            FROM alpine AS runtime
            COPY --from=builder@sha256:123abc /src/app /usr/local/bin/app
            """;

        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);
        var references = ReferenceExtractor.Extract(1, "dockerfile", content, symbols);

        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "builder"
            && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_DockerfileCopyFromReferences_IndexOnbuildStageDependencies()
    {
        const string content = """
            FROM alpine AS builder

            FROM alpine AS runtime
            ONBUILD COPY --from=builder /src/app /usr/local/bin/app
            """;

        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);
        var references = ReferenceExtractor.Extract(1, "dockerfile", content, symbols);

        Assert.Single(references.Where(reference =>
            reference.SymbolName == "builder"
            && reference.ReferenceKind == "call"));
    }

    [Fact]
    public void Extract_DockerfileCopyFromReferences_IndexQuotedStageDependencies()
    {
        const string content = """
            FROM alpine AS builder

            FROM alpine AS runtime
            COPY --from="builder" /src/app /usr/local/bin/app
            """;

        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);
        var references = ReferenceExtractor.Extract(1, "dockerfile", content, symbols);

        Assert.Single(references.Where(reference =>
            reference.SymbolName == "builder"
            && reference.ReferenceKind == "call"));
    }

    [Fact]
    public void Extract_DockerfileFromStageReferences_AllowsInlineComments()
    {
        const string content = """
            FROM node:20 AS builder

            FROM builder AS runtime # reuse the build stage
            """;

        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);
        var references = ReferenceExtractor.Extract(1, "dockerfile", content, symbols);

        Assert.Single(references.Where(reference =>
            reference.SymbolName == "builder"
            && reference.ReferenceKind == "call"));
    }

    [Fact]
    public void Extract_DockerfileReferences_IndexBracedArgVariables()
    {
        const string content = """
            ARG NODE_VERSION=20
            FROM node:${NODE_VERSION} AS builder
            """;

        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);
        var references = ReferenceExtractor.Extract(1, "dockerfile", content, symbols);

        Assert.Single(references.Where(reference =>
            reference.SymbolName == "NODE_VERSION"
            && reference.ReferenceKind == "reference"));
    }

    [Fact]
    public void Extract_DockerfileReferences_IndexDefaultedBracedArgVariables()
    {
        const string content = """
            ARG NODE_VERSION
            FROM node:${NODE_VERSION:-20} AS builder
            """;

        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);
        var references = ReferenceExtractor.Extract(1, "dockerfile", content, symbols);

        Assert.Single(references.Where(reference =>
            reference.SymbolName == "NODE_VERSION"
            && reference.ReferenceKind == "reference"));
    }

    [Fact]
    public void Extract_DockerfileReferences_IndexColonlessDefaultedBracedArgVariables()
    {
        const string content = """
            ARG NODE_VERSION
            FROM node:${NODE_VERSION-20} AS builder
            """;

        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);
        var references = ReferenceExtractor.Extract(1, "dockerfile", content, symbols);

        Assert.Single(references.Where(reference =>
            reference.SymbolName == "NODE_VERSION"
            && reference.ReferenceKind == "reference"));
    }

    [Theory]
    [InlineData(":-")]
    [InlineData(":?")]
    [InlineData(":+")]
    [InlineData(":=")]
    [InlineData("-")]
    public void Extract_DockerfileReferences_IndexNestedBracedArgVariablesInsideConditionalExpansion(string modifier)
    {
        var content = "ARG PRIMARY\nARG FALLBACK\nRUN echo ${PRIMARY" + modifier + "${FALLBACK}}\n";

        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);
        var references = ReferenceExtractor.Extract(1, "dockerfile", content, symbols);

        Assert.Single(references.Where(reference =>
            reference.SymbolName == "PRIMARY"
            && reference.ReferenceKind == "reference"));
        Assert.Single(references.Where(reference =>
            reference.SymbolName == "FALLBACK"
            && reference.ReferenceKind == "reference"));
    }

    [Fact]
    public void Extract_DockerfileReferences_IndexUnbracedArgVariables()
    {
        const string content = """
            ARG APP_HOME=/app
            WORKDIR $APP_HOME
            """;

        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);
        var references = ReferenceExtractor.Extract(1, "dockerfile", content, symbols);

        Assert.Single(references.Where(reference =>
            reference.SymbolName == "APP_HOME"
            && reference.ReferenceKind == "reference"));
    }

    [Fact]
    public void Extract_DockerfileReferences_IndexErrorIfUnsetBracedArgVariables()
    {
        const string content = """
            ARG REQUIRED_VAR
            RUN echo ${REQUIRED_VAR:?must be set}
            """;

        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);
        var references = ReferenceExtractor.Extract(1, "dockerfile", content, symbols);

        Assert.Single(references.Where(reference =>
            reference.SymbolName == "REQUIRED_VAR"
            && reference.ReferenceKind == "reference"));
    }

    [Fact]
    public void Extract_DockerfileReferences_IndexUseAlternateBracedArgVariables()
    {
        const string content = """
            ARG FEATURE_FLAG
            RUN echo ${FEATURE_FLAG:+--enable}
            """;

        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);
        var references = ReferenceExtractor.Extract(1, "dockerfile", content, symbols);

        Assert.Single(references.Where(reference =>
            reference.SymbolName == "FEATURE_FLAG"
            && reference.ReferenceKind == "reference"));
    }

    [Fact]
    public void Extract_DockerfileReferences_IndexAssignDefaultBracedArgVariables()
    {
        const string content = """
            ARG PORT
            RUN echo ${PORT:=8080}
            """;

        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);
        var references = ReferenceExtractor.Extract(1, "dockerfile", content, symbols);

        Assert.Single(references.Where(reference =>
            reference.SymbolName == "PORT"
            && reference.ReferenceKind == "reference"));
    }

    [Fact]
    public void Extract_DockerfileReferences_IgnoresEscapedUnbracedVariables()
    {
        const string content = """
            ARG APP_HOME=/app
            RUN echo \$APP_HOME
            """;

        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);
        var references = ReferenceExtractor.Extract(1, "dockerfile", content, symbols);

        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "APP_HOME");
    }

    [Fact]
    public void Extract_DockerfileReferences_IgnoresEscapedBracedVariables()
    {
        const string content = """
            ARG APP_HOME=/app
            RUN echo \${APP_HOME}
            """;

        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);
        var references = ReferenceExtractor.Extract(1, "dockerfile", content, symbols);

        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "APP_HOME");
    }
}
