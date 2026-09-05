#!/bin/sh
# Installs the awg-easy node agent and enrolls it with a control plane.
#
#   curl -fsSL https://panel.example.com/install.sh | sh -s -- \
#       --url https://panel.example.com --token <token> --port 51820
#
# Re-running is safe: it upgrades the agent in place and keeps the node's identity, so the
# tunnel is not disturbed.
set -eu

CONTROL_URL=""
ENROLLMENT_TOKEN=""
IMAGE="${AWG_NODE_IMAGE:-dihasi/awg-node:latest}"
STATE_DIR="/etc/awg-node"
# Must match the fleet listen port, or Docker publishes a port nothing listens on.
# The panel fills this in for you.
AWG_PORT="51820"

while [ $# -gt 0 ]; do
    case "$1" in
        --url)   CONTROL_URL="$2"; shift 2 ;;
        --token) ENROLLMENT_TOKEN="$2"; shift 2 ;;
        --image) IMAGE="$2"; shift 2 ;;
        --port)  AWG_PORT="$2"; shift 2 ;;
        *) echo "Unknown option: $1" >&2; exit 2 ;;
    esac
done

[ -n "$CONTROL_URL" ] || { echo "--url is required" >&2; exit 2; }

if [ "$(id -u)" -ne 0 ]; then
    echo "This installer needs root: re-run with sudo." >&2
    exit 1
fi

if ! command -v docker >/dev/null 2>&1; then
    echo "==> Installing Docker"
    curl -fsSL https://get.docker.com | sh
fi

# The tunnel needs a tun device and forwarding; without these the interface comes up but
# no traffic is routed.
modprobe tun 2>/dev/null || true
sysctl -w net.ipv4.ip_forward=1 >/dev/null

mkdir -p "$STATE_DIR"
chmod 700 "$STATE_DIR"

echo "==> Pulling $IMAGE"
docker pull "$IMAGE"

docker rm -f awg-node >/dev/null 2>&1 || true

echo "==> Starting the agent"
docker run -d \
    --name awg-node \
    --restart unless-stopped \
    --cap-add NET_ADMIN \
    --cap-add SYS_MODULE \
    --device /dev/net/tun:/dev/net/tun \
    --sysctl net.ipv4.ip_forward=1 \
    --sysctl net.ipv4.conf.all.src_valid_mark=1 \
    -p "$AWG_PORT":"$AWG_PORT"/udp \
    -e AWG_CONTROL_URL="$CONTROL_URL" \
    -e AWG_ENROLLMENT_TOKEN="$ENROLLMENT_TOKEN" \
    -e AWG_NODE_STATE_PATH="$STATE_DIR" \
    -v "$STATE_DIR":"$STATE_DIR" \
    -v awg-node-config:/etc/amnezia/amneziawg \
    "$IMAGE"

echo "==> Done. Follow the agent with: docker logs -f awg-node"
