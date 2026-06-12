FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build

WORKDIR /src
COPY . .

ARG TARGETARCH=amd64
RUN dotnet restore src/CodeIndex/CodeIndex.csproj
RUN case "$TARGETARCH" in \
      amd64) rid="linux-musl-x64" ;; \
      arm64) rid="linux-musl-arm64" ;; \
      *) echo "Unsupported container architecture: $TARGETARCH" >&2; exit 1 ;; \
    esac && \
    dotnet publish src/CodeIndex/CodeIndex.csproj \
      --configuration Release \
      --runtime "$rid" \
      --self-contained true \
      -p:PublishSingleFile=true \
      -p:PublishTrimmed=true \
      --output /out

FROM mcr.microsoft.com/dotnet/runtime-deps:8.0-alpine AS runtime

RUN apk add --no-cache ca-certificates

WORKDIR /repo
COPY --from=build /out/ /usr/local/lib/cdidx/
COPY LICENSE COMMERCIAL_LICENSE.md INTEGRATION_POLICY.md TRADEMARKS.md /usr/local/lib/cdidx/
COPY LICENSES/ /usr/local/lib/cdidx/LICENSES/
RUN ln -s /usr/local/lib/cdidx/cdidx /usr/local/bin/cdidx

ENTRYPOINT ["cdidx"]
CMD ["--help"]
