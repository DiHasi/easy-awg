# awg-node: the agent that runs on every VPN server.
#
# Unlike the control plane image this one is privileged and carries the AmneziaWG tooling.
# It serves no admin UI and opens no inbound management port: the agent only makes outbound
# calls to the control plane, and its health endpoint binds to loopback.

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

FROM golang:1.25-bookworm AS awg-go-build
ARG AMNEZIAWG_GO_REF=master
# This stage tracks amneziawg-go master, so its go.mod can raise the required Go version at any
# time. The golang images pin GOTOOLCHAIN=local, which turns that into a hard build failure;
# letting Go fetch the toolchain its go.mod asks for keeps upstream bumps from breaking us.
ENV GOTOOLCHAIN=auto
WORKDIR /src
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates git make \
    && git clone --depth 1 --branch "${AMNEZIAWG_GO_REF}" https://github.com/amnezia-vpn/amneziawg-go.git . \
    && make \
    && mkdir -p /out \
    && install -m 0755 amneziawg-go /out/amneziawg-go \
    && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
RUN apt-get update \
    && apt-get install -y --no-install-recommends clang zlib1g-dev libicu74 \
    && rm -rf /var/lib/apt/lists/*
COPY ["src/AwgEasy.Contracts/AwgEasy.Contracts.csproj", "src/AwgEasy.Contracts/"]
COPY ["src/AwgEasy.Node/AwgEasy.Node.csproj", "src/AwgEasy.Node/"]
RUN dotnet restore "src/AwgEasy.Node/AwgEasy.Node.csproj"
COPY src/ src/
RUN dotnet publish "src/AwgEasy.Node/AwgEasy.Node.csproj" -c $BUILD_CONFIGURATION -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS final
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        bash \
        ca-certificates \
        iproute2 \
        iptables \
        procps \
    && ldconfig \
    && rm -rf /var/lib/apt/lists/*
COPY --from=awg-tools-build /out/usr/bin/awg /usr/bin/awg
COPY --from=awg-tools-build /out/usr/bin/awg-quick /usr/bin/awg-quick
COPY --from=awg-go-build /out/amneziawg-go /usr/bin/amneziawg-go
COPY --from=build /app/publish .
RUN chmod +x /app/awg-node

ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
ENV WG_QUICK_USERSPACE_IMPLEMENTATION=amneziawg-go
# The base image presets a listen port, but the agent binds its own loopback health address.
# Clearing it keeps a confusing "Overriding HTTP_PORTS" warning out of every node's log.
ENV ASPNETCORE_HTTP_PORTS=

EXPOSE 51820/udp
VOLUME ["/etc/awg-node", "/etc/amnezia/amneziawg"]
ENTRYPOINT ["/app/awg-node"]
