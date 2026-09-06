# awg-control: the panel and source of truth.
#
# Unprivileged and without the VPN tooling, unlike the node image: this container holds the
# fleet identity, so it gets the smallest possible blast radius. The only reason `awg` is here
# at all is key generation, which now happens exclusively in the control plane.

FROM ubuntu:24.04 AS awg-tools-build
ARG AMNEZIAWG_TOOLS_REF=master
WORKDIR /src
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        bash ca-certificates gcc git libc6-dev make pkg-config \
    && git clone --depth 1 --branch "${AMNEZIAWG_TOOLS_REF}" https://github.com/amnezia-vpn/amneziawg-tools.git . \
    && make -C src \
    && make -C src install DESTDIR=/out PREFIX=/usr WITH_WGQUICK=no WITH_BASHCOMPLETION=no WITH_SYSTEMDUNITS=no \
    && rm -rf /var/lib/apt/lists/*

# Slim: the Nuxt build needs no native toolchain, and the full image costs ~700MB of
# build-host disk that a small VPS does not have.
FROM node:24-bookworm-slim AS frontend-build
WORKDIR /src/frontend
RUN corepack enable
COPY frontend/package.json frontend/pnpm-lock.yaml frontend/pnpm-workspace.yaml ./
RUN pnpm install --frozen-lockfile
COPY frontend ./
RUN pnpm run generate

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["src/AwgEasy.Contracts/AwgEasy.Contracts.csproj", "src/AwgEasy.Contracts/"]
COPY ["src/AwgEasy.Control/AwgEasy.Control.csproj", "src/AwgEasy.Control/"]
RUN dotnet restore "src/AwgEasy.Control/AwgEasy.Control.csproj"
COPY src/ src/
RUN dotnet publish "src/AwgEasy.Control/AwgEasy.Control.csproj" -c $BUILD_CONFIGURATION -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=awg-tools-build /out/usr/bin/awg /usr/bin/awg
COPY --from=build /app/publish .
COPY --from=frontend-build /src/frontend/.output/public ./wwwroot
COPY scripts/install.sh ./wwwroot/install.sh

ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
ENV ASPNETCORE_URLS=http://0.0.0.0:8080

EXPOSE 8080
VOLUME ["/etc/awg-control"]
ENTRYPOINT ["dotnet", "/app/awg-control.dll"]
