# Base image digests are multi-arch manifest list digests. Refresh with:
# docker buildx imagetools inspect mcr.microsoft.com/dotnet/<image>:8.0-alpine
FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine@sha256:d9f4f4a5d99a43799b500ee1365c370e3233822fbe7d43666715d9b5b5cda2ab AS build

WORKDIR /src
COPY Directory.Build.props nuget.config version.json ./
COPY src/CodeIndex/CodeIndex.csproj src/CodeIndex/packages.lock.json src/CodeIndex/
RUN dotnet restore src/CodeIndex/CodeIndex.csproj
COPY src/CodeIndex/ src/CodeIndex/
COPY LICENSE COMMERCIAL_LICENSE.md INTEGRATION_POLICY.md TRADEMARKS.md ./
COPY LICENSES/ LICENSES/

ARG TARGETARCH=amd64
ARG CDIDX_BUILD_COMMIT=unknown
ARG CDIDX_BUILD_DATE
ARG CDIDX_BUILD_DIRTY=unknown
RUN case "$TARGETARCH" in \
      amd64) rid="linux-musl-x64" ;; \
      arm64) rid="linux-musl-arm64" ;; \
      *) echo "Unsupported container architecture: $TARGETARCH" >&2; exit 1 ;; \
    esac && \
    build_date="${CDIDX_BUILD_DATE:-$(date -u +%Y-%m-%d)}" && \
    dotnet publish src/CodeIndex/CodeIndex.csproj \
      --configuration Release \
      --runtime "$rid" \
      --self-contained true \
      -p:PublishSingleFile=true \
      -p:PublishTrimmed=true \
      -p:CdidxBuildCommitOverride="$CDIDX_BUILD_COMMIT" \
      -p:CdidxBuildDateOverride="$build_date" \
      -p:CdidxBuildDirtyOverride="$CDIDX_BUILD_DIRTY" \
      --output /out

FROM mcr.microsoft.com/dotnet/runtime-deps:8.0-alpine@sha256:7ec14bf41e70f3ca60f7b369b077636f642a0e6867caf28677d970e0abd9c6e6 AS runtime

COPY scripts/docker-entrypoint.sh /usr/local/bin/cdidx-entrypoint
RUN apk add --no-cache ca-certificates su-exec \
    && addgroup -S -g 10001 cdidx \
    && adduser -S -D -H -u 10001 -G cdidx -h /repo cdidx \
    && mkdir -p /repo \
    && chown cdidx:cdidx /repo \
    && chmod 0755 /usr/local/bin/cdidx-entrypoint

WORKDIR /repo
COPY --from=build /out/ /usr/local/lib/cdidx/
COPY LICENSE COMMERCIAL_LICENSE.md INTEGRATION_POLICY.md TRADEMARKS.md /usr/local/lib/cdidx/
COPY LICENSES/ /usr/local/lib/cdidx/LICENSES/
RUN ln -s /usr/local/lib/cdidx/cdidx /usr/local/bin/cdidx

ENTRYPOINT ["/usr/local/bin/cdidx-entrypoint"]
CMD ["--help"]
