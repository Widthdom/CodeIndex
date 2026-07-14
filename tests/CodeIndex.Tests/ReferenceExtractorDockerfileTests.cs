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
    public void Extract_DockerfileFromStageReferences_IndexNamedStageVariantsAndIgnoreBaseImages()
    {
        const string content = """
            from golang:1.21 as builder
            FROM builder AS build2
            from builder as lowercase
            copy --from=builder /src/app /usr/local/bin/app
            FROM debian:bookworm-slim AS runner
            COPY --from=builder /src/second /opt/second
            COPY --from=builder /src/third /opt/third
            FROM runner AS final
            FROM --platform=$BUILDPLATFORM builder AS platform
            FROM builder AS commented # reuse the build stage
            FROM alpine:3.20 AS external-base
            FROM node:20 AS build-env
            FROM build-env AS runtime
            COPY --from=build-env /src/app /usr/local/bin/app
            FROM node:20 AS build.env
            FROM build.env AS dotted-runtime
            COPY --from=build.env /src/app /usr/local/bin/app
            """;

        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);
        var references = ReferenceExtractor.Extract(1, "dockerfile", content, symbols);

        var expectedCallCounts = new Dictionary<string, int>
        {
            ["builder"] = 7,
            ["runner"] = 1,
            ["build-env"] = 2,
            ["build.env"] = 2,
        };

        foreach (var (stageName, expectedCount) in expectedCallCounts)
        {
            Assert.Equal(expectedCount, references.Count(reference =>
                reference.SymbolName == stageName
                && reference.ReferenceKind == "call"));
        }

        Assert.DoesNotContain(references, reference =>
            reference.SymbolName is "golang:1.21" or "debian:bookworm-slim" or "alpine:3.20" or "node:20");
    }

    [Fact]
    public void Extract_DockerfileRunMountReferences_IndexStageFormsAndIgnoreShellArguments()
    {
        const string content = """
            FROM alpine AS assets
            FROM alpine AS cache
            FROM alpine AS runtime
            RUN --mount=type=bind,from=assets,target=/mnt/assets cp -r /mnt/assets /app/assets
            RUN --mount=type=bind,from=assets,target=/assets --mount=type=bind,from=cache,target=/cache true
            RUN --mount=type=bind,from="assets",target=/mnt/assets cp -r /mnt/assets /app/assets
            ONBUILD RUN --mount=type=bind,from=assets,target=/mnt/assets cp -r /mnt/assets /app/assets
            RUN echo "--mount=type=bind,from=assets,target=/mnt/assets"
            RUN echo --mount=type=bind,from=assets,target=/mnt/assets
            """;

        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);
        var references = ReferenceExtractor.Extract(1, "dockerfile", content, symbols);

        Assert.Equal(4, references.Count(reference =>
            reference.SymbolName == "assets"
            && reference.ReferenceKind == "call"));
        Assert.Single(references.Where(reference =>
            reference.SymbolName == "cache"
            && reference.ReferenceKind == "call"));
    }

    [Fact]
    public void Extract_DockerfileCopyFromReferences_IndexStageFormsAndIgnoreExternalImages()
    {
        const string content = """
            FROM alpine AS builder
            FROM alpine AS quoted-builder
            FROM alpine AS runtime
            ONBUILD COPY --from=builder /src/app /usr/local/bin/app
            COPY --from="quoted-builder" /src/quoted /opt/quoted
            COPY --from=builder:latest /src/app /usr/local/bin/app
            COPY --from=builder@sha256:123abc /src/app /usr/local/bin/app
            """;

        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);
        var references = ReferenceExtractor.Extract(1, "dockerfile", content, symbols);

        Assert.Single(references.Where(reference =>
            reference.SymbolName == "builder"
            && reference.ReferenceKind == "call"));
        Assert.Single(references.Where(reference =>
            reference.SymbolName == "quoted-builder"
            && reference.ReferenceKind == "call"));
    }

    [Fact]
    public void Extract_DockerfileReferences_IndexArgExpansionVariantsAndIgnoreEscapes()
    {
        const string content = """
            ARG BRACED=20
            ARG DEFAULTED
            ARG COLONLESS
            ARG APP_HOME=/app
            ARG REQUIRED_VAR
            ARG FEATURE_FLAG
            ARG PORT
            ARG PRIMARY_DEFAULT
            ARG FALLBACK_DEFAULT
            ARG PRIMARY_ERROR
            ARG FALLBACK_ERROR
            ARG PRIMARY_ALTERNATE
            ARG FALLBACK_ALTERNATE
            ARG PRIMARY_ASSIGN
            ARG FALLBACK_ASSIGN
            ARG PRIMARY_COLONLESS
            ARG FALLBACK_COLONLESS
            FROM node:${BRACED} AS builder
            FROM node:${DEFAULTED:-20} AS defaulted
            FROM node:${COLONLESS-20} AS colonless
            WORKDIR $APP_HOME
            RUN echo ${REQUIRED_VAR:?must be set}
            RUN echo ${FEATURE_FLAG:+--enable}
            RUN echo ${PORT:=8080}
            RUN echo ${PRIMARY_DEFAULT:-${FALLBACK_DEFAULT}}
            RUN echo ${PRIMARY_ERROR:?${FALLBACK_ERROR}}
            RUN echo ${PRIMARY_ALTERNATE:+${FALLBACK_ALTERNATE}}
            RUN echo ${PRIMARY_ASSIGN:=${FALLBACK_ASSIGN}}
            RUN echo ${PRIMARY_COLONLESS-${FALLBACK_COLONLESS}}
            RUN echo \$APP_HOME
            RUN echo \${APP_HOME}
            """;

        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);
        var references = ReferenceExtractor.Extract(1, "dockerfile", content, symbols);

        var expectedNames = new[]
        {
            "BRACED", "DEFAULTED", "COLONLESS", "APP_HOME", "REQUIRED_VAR", "FEATURE_FLAG", "PORT",
            "PRIMARY_DEFAULT", "FALLBACK_DEFAULT", "PRIMARY_ERROR", "FALLBACK_ERROR",
            "PRIMARY_ALTERNATE", "FALLBACK_ALTERNATE", "PRIMARY_ASSIGN", "FALLBACK_ASSIGN",
            "PRIMARY_COLONLESS", "FALLBACK_COLONLESS",
        };

        foreach (var expectedName in expectedNames)
        {
            Assert.Single(references.Where(reference =>
                reference.SymbolName == expectedName
                && reference.ReferenceKind == "reference"));
        }
    }
}
