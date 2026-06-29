# Base image digests are multi-arch manifest list digests.
# SDK digest refresh helper (replace <image> with sdk):
# docker buildx imagetools inspect mcr.microsoft.com/dotnet/<image>:9.0.301-alpine3.22
# Build uses the repository-pinned .NET 9 SDK; runtime stays on .NET 8
# runtime-deps because cdidx targets net8.0. Refresh pinned images with:
# docker buildx imagetools inspect mcr.microsoft.com/dotnet/sdk:9.0.301-alpine3.22
# docker buildx imagetools inspect mcr.microsoft.com/dotnet/runtime-deps:8.0-alpine
FROM mcr.microsoft.com/dotnet/sdk:9.0.301-alpine3.22@sha256:bdd1c9e2215a71e43d2f0c6978ace0a0652d7ecc21bf6f659d42d840500e1c44 AS build

WORKDIR /src
COPY Directory.Build.props nuget.config version.json ./
COPY src/CodeIndex/CodeIndex.csproj src/CodeIndex/packages.lock.json src/CodeIndex/

ARG TARGETARCH=amd64
RUN case "$TARGETARCH" in \
      amd64) rid="linux-musl-x64" ;; \
      arm64) rid="linux-musl-arm64" ;; \
      *) echo "Unsupported container architecture: $TARGETARCH" >&2; exit 1 ;; \
    esac && \
    dotnet restore src/CodeIndex/CodeIndex.csproj \
      --runtime "$rid" \
      --locked-mode

COPY src/CodeIndex/ src/CodeIndex/
COPY LICENSE COMMERCIAL_LICENSE.md INTEGRATION_POLICY.md TRADEMARKS.md ./
COPY LICENSES/ LICENSES/

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
      --no-restore \
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
