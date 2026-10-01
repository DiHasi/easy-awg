# awg-node: the agent that runs on every VPN server, and the probe (AWG_ROLE=probe).
#
# Unlike the control plane image this one is privileged and carries the AmneziaWG tooling.
# It serves no admin UI and opens no inbound management port: the agent only makes outbound
# calls to the control plane, and its health endpoint binds to loopback.
#
# Multi-arch (linux/amd64, linux/arm64 - a probe on a Raspberry Pi is the usual arm case):
#   docker buildx build --platform linux/amd64,linux/arm64 -f docker/node.Dockerfile ...
# The Go and .NET stages run on the build machine and cross-compile; only the small C build of
# the tools and the final package install run emulated. Native AOT under QEMU is slow at best
# and crashes at worst, so it is never asked to.

FROM ubuntu:24.04 AS awg-tools-build
# Pinned, not master: master already carries an awg4 line, and the tools are half of an ABI pair
# with whatever kernel module the host runs. These 3.1 tools negotiate the netlink encoding of
# H1-H4 against the module's genl family version, so they drive a genl 1, 2 or 3 module correctly
# - which is what lets a node be upgraded without its kernel module being upgraded in lockstep.
ARG AMNEZIAWG_TOOLS_REF=v3.1.20260812
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

FROM --platform=$BUILDPLATFORM golang:1.25-bookworm AS awg-go-build
ARG TARGETOS
ARG TARGETARCH
# The userspace implementation, and the default backend for a node: it needs no kernel module and
# it speaks the full 3.1 parameter set. Pinned for the same reason as the tools.
ARG AMNEZIAWG_GO_REF=v3.1.20260828
# amneziawg-go can raise the required Go version at any time. The golang images pin
# GOTOOLCHAIN=local, which turns that into a hard build failure; letting Go fetch the toolchain
# its go.mod asks for keeps upstream bumps from breaking us.
ENV GOTOOLCHAIN=auto
# Go cross-compiles by itself; amneziawg-go needs no cgo.
ENV CGO_ENABLED=0 GOOS=${TARGETOS} GOARCH=${TARGETARCH}
WORKDIR /src
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates git make \
    && git clone --depth 1 --branch "${AMNEZIAWG_GO_REF}" https://github.com/amnezia-vpn/amneziawg-go.git . \
    && make \
    && mkdir -p /out \
    && install -m 0755 amneziawg-go /out/amneziawg-go \
    && rm -rf /var/lib/apt/lists/*

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
ARG TARGETARCH
ARG BUILDARCH
# Stamped into the agent assembly so the panel can tell which nodes it has
# already walked during a synchronized fleet upgrade.
ARG AGENT_VERSION=0.0.0-dev
WORKDIR /src
# Native AOT links with clang against the target's libc. Building arm64 on an amd64 host needs
# the arm64 sysroot, which on Ubuntu means ports.ubuntu.com - the main archive carries amd64 only.
RUN set -eux; \
    if [ "$TARGETARCH" = "arm64" ] && [ "$BUILDARCH" = "amd64" ]; then \
        dpkg --add-architecture arm64; \
        sed -i '/^Types:/a Architectures: amd64' /etc/apt/sources.list.d/ubuntu.sources; \
        printf '%s\n' 'Types: deb' 'URIs: http://ports.ubuntu.com/ubuntu-ports' \
            'Suites: noble noble-updates noble-security' 'Components: main universe' \
            'Architectures: arm64' 'Signed-By: /usr/share/keyrings/ubuntu-archive-keyring.gpg' \
            > /etc/apt/sources.list.d/arm64.sources; \
        apt-get update; \
        apt-get install -y --no-install-recommends clang llvm libicu74 \
            binutils-aarch64-linux-gnu gcc-aarch64-linux-gnu zlib1g-dev:arm64; \
    elif [ "$TARGETARCH" != "$BUILDARCH" ]; then \
        echo "Cross-compiling $BUILDARCH -> $TARGETARCH is not set up; build on amd64." >&2; exit 1; \
    else \
        apt-get update; \
        apt-get install -y --no-install-recommends clang zlib1g-dev libicu74; \
    fi; \
    rm -rf /var/lib/apt/lists/*
COPY ["src/AwgEasy.Contracts/AwgEasy.Contracts.csproj", "src/AwgEasy.Contracts/"]
COPY ["src/AwgEasy.Node/AwgEasy.Node.csproj", "src/AwgEasy.Node/"]
RUN dotnet restore "src/AwgEasy.Node/AwgEasy.Node.csproj"
COPY src/ src/
RUN RID="linux-$([ "$TARGETARCH" = "arm64" ] && echo arm64 || echo x64)" \
    && dotnet publish "src/AwgEasy.Node/AwgEasy.Node.csproj" -c $BUILD_CONFIGURATION -r "$RID" \
        -p:Version=$AGENT_VERSION -o /app/publish

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
