FROM ubuntu:24.04 AS awg-tools-build
ARG AMNEZIAWG_TOOLS_REF=master
WORKDIR /src
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        bash \
        ca-certificates \
        gcc \
        git \
        libc6-dev \
        make \
        pkg-config \
    && git clone --depth 1 --branch "${AMNEZIAWG_TOOLS_REF}" https://github.com/amnezia-vpn/amneziawg-tools.git . \
    && make -C src \
    && make -C src install \
        DESTDIR=/out \
        PREFIX=/usr \
        WITH_WGQUICK=yes \
        WITH_BASHCOMPLETION=no \
        WITH_SYSTEMDUNITS=no \
    && rm -rf /var/lib/apt/lists/*

FROM golang:1.24-bookworm AS awg-go-build
ARG AMNEZIAWG_GO_REF=master
WORKDIR /src
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates git make \
    && git clone --depth 1 --branch "${AMNEZIAWG_GO_REF}" https://github.com/amnezia-vpn/amneziawg-go.git . \
    && make \
    && mkdir -p /out \
    && install -m 0755 amneziawg-go /out/amneziawg-go \
    && rm -rf /var/lib/apt/lists/*

FROM node:24-bookworm AS frontend-build
WORKDIR /src/frontend
RUN corepack enable
COPY frontend/package.json frontend/pnpm-lock.yaml frontend/pnpm-workspace.yaml ./
RUN pnpm install --frozen-lockfile
COPY frontend ./
RUN pnpm run generate

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER root
WORKDIR /app
EXPOSE 8080
EXPOSE 51820/udp
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        bash \
        ca-certificates \
        iproute2 \
        iptables \
        libicu74 \
        procps \
    && ldconfig \
    && rm -rf /var/lib/apt/lists/*
COPY --from=awg-tools-build /out/usr/bin/awg /usr/bin/awg
COPY --from=awg-tools-build /out/usr/bin/awg-quick /usr/bin/awg-quick
COPY --from=awg-go-build /out/amneziawg-go /usr/bin/amneziawg-go
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
ENV WG_QUICK_USERSPACE_IMPLEMENTATION=amneziawg-go

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
RUN apt-get update \
    && apt-get install -y --no-install-recommends clang zlib1g-dev libicu74 \
    && rm -rf /var/lib/apt/lists/*
COPY ["Awg-easy.csproj", "./"]
COPY ["src/AwgEasy.Contracts/AwgEasy.Contracts.csproj", "src/AwgEasy.Contracts/"]
RUN dotnet restore "Awg-easy.csproj"
COPY . .
WORKDIR "/src/"
RUN dotnet build "./Awg-easy.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Awg-easy.csproj" -c $BUILD_CONFIGURATION -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
COPY --from=frontend-build /src/frontend/.output/public ./wwwroot
VOLUME ["/etc/awg-easy", "/etc/amnezia/amneziawg"]
RUN chmod +x /app/Awg-easy
ENTRYPOINT ["/app/Awg-easy"]
