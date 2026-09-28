#!/bin/sh
# Installs the AmneziaWG 2.x stack on a VPN node: awg, awg-quick and the kernel module.
#
#   ./install-awg-v2.sh              # tools + kernel module, verified before it is left in place
#   ./install-awg-v2.sh --tools-only # just awg and awg-quick
#
# Both halves are pinned to the 2.x line and must stay pinned together. The netlink ABI for
# H1-H4 changed three times, and the genl family version is what the two ends negotiate on:
#
#   genl 1  ->  H1-H4 as NLA_U32
#   genl 2  ->  H1-H4 as NLA_NUL_STRING   <- this is what we install
#   genl 3  ->  H1-H4 as NLA_U64          <- AmneziaWG 3.0, tags v3.0.20260730 and later
#
# Tools older than v3.0.20260730 only know versions 1 and 2, so they send a string to a genl 3
# module, which rejects it in its netlink policy: awg setconf fails with EINVAL and nothing
# reaches dmesg. Upgrading only one side is what breaks a node, which is why the refs below are
# tags rather than master.
set -eu

TOOLS_REF="v1.0.20260618-2"
MODULE_REF="v1.0.20260725"
DKMS_NAME="amneziawg"
DKMS_VERSION="1.0.0"
GUARD_FILE="/etc/modprobe.d/awg-easy-no-kmod.conf"
PROBE_IF="awgv2probe"
# Verification prefers a real fleet configuration. The agent keeps its copy in a container
# volume, so pass --config with a copy of it when this host has none of its own.
NODE_CONF="/etc/amnezia/amneziawg/awg0.conf"
TOOLS_ONLY=0
SKIP_VERIFY=0
WORKDIR=""

while [ $# -gt 0 ]; do
    case "$1" in
        --tools-only)  TOOLS_ONLY=1; shift ;;
        --skip-verify) SKIP_VERIFY=1; shift ;;
        --tools-ref)   TOOLS_REF="$2"; shift 2 ;;
        --module-ref)  MODULE_REF="$2"; shift 2 ;;
        --config)      NODE_CONF="$2"; shift 2 ;;
        -h|--help)     sed -n '2,5p' "$0"; exit 0 ;;
        *) echo "Unknown option: $1" >&2; exit 2 ;;
    esac
done

log()  { printf '==> %s\n' "$*"; }
warn() { printf '!!  %s\n' "$*" >&2; }
die()  { printf 'ERROR: %s\n' "$*" >&2; exit 1; }

# Loading the module is not the same as being able to use it. Once it is loaded awg-quick stops
# falling back to userspace - it only falls back when ip link add fails - so a module that takes
# the link but rejects the configuration drops the tunnel with no way back from inside a
# container. Everything below that cannot prove the kernel path works ends up here.
rollback() {
    warn "Rolling back to the userspace data path."
    ip link del "$PROBE_IF" 2>/dev/null || true
    dkms remove -m "$DKMS_NAME" -v "$DKMS_VERSION" --all || true
    rm -rf "/usr/src/$DKMS_NAME-$DKMS_VERSION" /etc/modules-load.d/amneziawg.conf
    # Never swallow a failed unload. Removing the files does not evict the module from memory,
    # so a module still held by a live interface keeps serving netlink while every on-disk check
    # says it is gone - which is exactly how a stale build survives an uninstall unnoticed.
    if [ -e "/sys/module/$DKMS_NAME" ] && ! modprobe -r "$DKMS_NAME" 2>/dev/null; then
        warn "Could not unload $DKMS_NAME - something still holds it (refcount $(cat "/sys/module/$DKMS_NAME/refcnt" 2>/dev/null || echo '?')). The kernel keeps running version $(cat "/sys/module/$DKMS_NAME/version" 2>/dev/null || echo unknown) even though its files are gone. Delete the interfaces using it and unload it by hand:"
        warn "    ip link del awg0 && modprobe -r $DKMS_NAME"
    fi
    # blacklist is not enough here: ip link add type amneziawg calls request_module directly.
    printf 'install %s /bin/false\n' "$DKMS_NAME" > "$GUARD_FILE"
    warn "Module uninstalled and blocked in $GUARD_FILE. awg-quick falls back to amneziawg-go wherever that binary exists - the node image carries it - so restart the node agent to pick that up."
}

[ "$(id -u)" -eq 0 ] || die "This installer needs root: re-run with sudo."

cleanup() {
    ip link del "$PROBE_IF" 2>/dev/null || true
    [ -n "$WORKDIR" ] && rm -rf "$WORKDIR"
    return 0
}
trap cleanup EXIT

# ---------------------------------------------------------------- preflight

# Not "command -v apt-get && HAS_APT=1": under set -e a failing AND-list ends the script, so a
# host without apt would exit here instead of taking the manual-dependency path.
HAS_APT=0
if command -v apt-get >/dev/null 2>&1; then
    HAS_APT=1
fi

apt_install() {
    if [ "$HAS_APT" -eq 0 ]; then
        warn "No apt-get here; assuming these are already installed: $*"
        return 0
    fi
    DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends "$@"
}

if [ "$HAS_APT" -eq 1 ]; then
    log "Refreshing the package index"
    apt-get update -qq
fi

log "Installing build dependencies"
apt_install git build-essential ca-certificates

check_kernel_ready() {
    # A container shares the host kernel and cannot load modules of its own. Building here would
    # succeed and leave behind a module that never loads, so stop before that.
    if command -v systemd-detect-virt >/dev/null 2>&1 && systemd-detect-virt -c >/dev/null 2>&1; then
        die "Running inside a $(systemd-detect-virt -c) container. Install the module on the host and re-run this with --tools-only here."
    fi

    running="$(uname -r)"

    # The usual reason a node ends up with a module that will not load: apt installed a newer
    # kernel, nothing rebooted, and the headers for the running one are gone from the archive.
    if [ ! -d "/lib/modules/$running/build" ]; then
        log "Installing headers for the running kernel ($running)"
        if ! apt_install "linux-headers-$running"; then
            die "No linux-headers-$running available. The running kernel is not the one this distribution ships. Install a stock kernel, reboot into it, then re-run: apt-get install -y linux-image-generic linux-headers-generic && reboot"
        fi
        [ -d "/lib/modules/$running/build" ] || die "/lib/modules/$running/build is still missing."
    fi

    newest="$(ls -1 /lib/modules 2>/dev/null | sort -V | tail -1)"
    if [ -n "$newest" ] && [ "$newest" != "$running" ]; then
        warn "A newer kernel ($newest) is installed but not booted, so the next reboot lands there and not on $running. This builds for every installed kernel whose headers are present; install linux-headers-generic if you want that to keep happening automatically."
    fi

    if command -v mokutil >/dev/null 2>&1 && mokutil --sb-state 2>/dev/null | grep -qi enabled; then
        warn "Secure Boot is enabled. An unsigned DKMS module is refused with 'Key was rejected by service'. Enroll a MOK or disable Secure Boot if modprobe fails."
    fi

    apt_install dkms libelf-dev bc
}

[ "$TOOLS_ONLY" -eq 1 ] || check_kernel_ready

WORKDIR="$(mktemp -d)"

# ------------------------------------------------------- remove the 3.x line

# This has to happen before our own tools are installed, not after: dpkg owns /usr/bin/awg and
# /usr/bin/awg-quick, so purging the package later would delete the binaries we just put there.
if [ "$HAS_APT" -eq 1 ] && dpkg -l amneziawg amneziawg-dkms amneziawg-tools 2>/dev/null | grep -q '^ii'; then
    log "Purging the PPA packages: they track the 3.x line"
    DEBIAN_FRONTEND=noninteractive apt-get purge -y amneziawg amneziawg-dkms amneziawg-tools || true
fi

# ------------------------------------------------------------------- tools

log "Building amneziawg-tools $TOOLS_REF"
git clone --quiet --depth 1 --branch "$TOOLS_REF" \
    https://github.com/amnezia-vpn/amneziawg-tools.git "$WORKDIR/tools"
make -s -C "$WORKDIR/tools/src" -j"$(nproc 2>/dev/null || echo 2)"

log "Installing awg and awg-quick"
# WITH_SYSTEMDUNITS gives us awg-quick@.service, so an interface survives a reboot without
# anything else on the box needing to know how to bring it up.
make -s -C "$WORKDIR/tools/src" install \
    WITH_WGQUICK=yes \
    WITH_SYSTEMDUNITS=yes \
    WITH_BASHCOMPLETION=yes

log "Installed: $(awg --version) at $(command -v awg)"

if [ "$TOOLS_ONLY" -eq 1 ]; then
    if [ -e "/sys/module/$DKMS_NAME" ]; then
        warn "A kernel module is already loaded ($(modinfo -F version "$DKMS_NAME" 2>/dev/null || echo 'unknown version')). These tools speak genl 1 and 2 only, so if that module is from the 3.x line every awg setconf will fail. Check it before relying on this node."
    fi
    log "Done: tools only. awg-quick uses the userspace amneziawg-go while no module is loaded."
    exit 0
fi

# ------------------------------------------------------------- kernel module

# The 3.x packages carry the same DKMS name and version as this source, so an existing install
# has to go before ours can be added.
if dkms status -m "$DKMS_NAME" 2>/dev/null | grep -q .; then
    log "Removing the existing $DKMS_NAME DKMS module"
    dkms remove -m "$DKMS_NAME" -v "$DKMS_VERSION" --all || true
fi
rm -rf "/usr/src/$DKMS_NAME-$DKMS_VERSION"

# A previous rollback may have blocked the module from loading at all.
if [ -f "$GUARD_FILE" ]; then
    log "Lifting the modprobe guard at $GUARD_FILE"
    rm -f "$GUARD_FILE"
fi

# An already-loaded module makes the modprobe below a no-op, and the kernel would keep running
# whatever was loaded before - a 3.x build, most likely - while every check here reports the
# 2.x files we are about to install. Unload it now, while a failure to do so is still visible.
if [ -e "/sys/module/$DKMS_NAME" ]; then
    log "Unloading the running module ($(cat "/sys/module/$DKMS_NAME/version" 2>/dev/null || echo 'version unset'))"
    modprobe -r "$DKMS_NAME" 2>/dev/null || die "$DKMS_NAME is loaded and in use. Stop whatever holds it (the node agent, or an interface still up: ip link del awg0) and re-run."
fi

log "Building the kernel module $MODULE_REF"
git clone --quiet --depth 1 --branch "$MODULE_REF" \
    https://github.com/amnezia-vpn/amneziawg-linux-kernel-module.git "$WORKDIR/kmod"
make -s -C "$WORKDIR/kmod/src" dkms-install

dkms add -m "$DKMS_NAME" -v "$DKMS_VERSION"
dkms build -m "$DKMS_NAME" -v "$DKMS_VERSION" -k "$(uname -r)"
dkms install -m "$DKMS_NAME" -v "$DKMS_VERSION" -k "$(uname -r)" --force

# Building only for the running kernel is a trap on a host that has a newer one installed but not
# booted: the next reboot lands on the newer kernel, finds no module, and the node silently drops
# to the userspace data path. Cover every installed kernel whose headers are here.
for buildlink in /lib/modules/*/build; do
    [ -d "$buildlink" ] || continue
    kver="$(basename "$(dirname "$buildlink")")"
    # Not "[ x = y ] && continue": a false test makes the AND-list fail, which under set -e ends
    # the script instead of skipping the iteration.
    [ "$kver" != "$(uname -r)" ] || continue
    log "Also building for installed kernel $kver"
    if dkms build -m "$DKMS_NAME" -v "$DKMS_VERSION" -k "$kver" \
        && dkms install -m "$DKMS_NAME" -v "$DKMS_VERSION" -k "$kver" --force; then
        :
    else
        warn "Could not build for $kver. Booting that kernel will leave this node on userspace."
    fi
done

mkdir -p /etc/modules-load.d
echo "$DKMS_NAME" > /etc/modules-load.d/amneziawg.conf
modprobe "$DKMS_NAME"

# modinfo reads the file on disk; /sys/module/<name>/version is what the kernel is actually
# running. They diverge whenever a module was already loaded when this ran, because modprobe is
# a no-op then - and a stale 3.x module in memory rejects everything these 2.x tools send while
# every on-disk check looks perfectly healthy.
ondisk="$(modinfo -F version "$DKMS_NAME" 2>/dev/null || echo unknown)"
runtime="$(cat "/sys/module/$DKMS_NAME/version" 2>/dev/null || echo unknown)"
log "Module on disk: $ondisk; running in the kernel: $runtime"
if [ "$ondisk" != "$runtime" ]; then
    warn "The running module is not the one just installed. It was already loaded, so modprobe did nothing."
    rollback
    exit 1
fi

# ------------------------------------------------------------------ verify

if [ "$SKIP_VERIFY" -eq 1 ]; then
    log "Skipping verification as asked. This node is now on the kernel data path untested."
    exit 0
fi

PROBE_CONF="$WORKDIR/probe.conf"
PROBE_KEY="$(awg genkey)"

# The module explains every rejection through net_dbg_ratelimited, which the kernel compiles as
# dynamic debug. Without turning that on, a rejected configuration produces EINVAL in userspace
# and complete silence in dmesg, which is what makes this failure so hard to read.
enable_module_debug() {
    if [ ! -e /sys/kernel/debug/dynamic_debug/control ]; then
        mount -t debugfs none /sys/kernel/debug 2>/dev/null || true
    fi
    if [ -w /sys/kernel/debug/dynamic_debug/control ]; then
        echo "module $DKMS_NAME +p" > /sys/kernel/debug/dynamic_debug/control 2>/dev/null || true
        return 0
    fi
    warn "Cannot enable dynamic debug for $DKMS_NAME; a rejection will not say why in dmesg."
    return 1
}

# Applies one configuration to a throwaway interface. 0 accepted, 1 rejected, 2 link refused.
probe_config() {
    ip link del "$PROBE_IF" 2>/dev/null || true
    ip link add "$PROBE_IF" type "$DKMS_NAME" 2>/dev/null || return 2
    if awg setconf "$PROBE_IF" "$1" 2>"$WORKDIR/probe.err"; then
        ip link del "$PROBE_IF" 2>/dev/null || true
        return 0
    fi
    ip link del "$PROBE_IF" 2>/dev/null || true
    return 1
}

# Which setting the module objects to is the whole question, so add them in groups and report
# the first group that stops being accepted rather than guessing at a cause.
bisect_config() {
    ladder="$WORKDIR/ladder"
    printf '[Interface]\nPrivateKey = %s\n' "$PROBE_KEY" > "$ladder-1.conf"
    cp "$ladder-1.conf" "$ladder-2.conf"
    printf 'Jc = 4\nJmin = 40\nJmax = 70\n' >> "$ladder-2.conf"
    cp "$ladder-2.conf" "$ladder-3.conf"
    printf 'S1 = 15\nS2 = 30\n' >> "$ladder-3.conf"
    cp "$ladder-3.conf" "$ladder-4.conf"
    printf 'H1 = 1234567891\nH2 = 1234567892\nH3 = 1234567893\nH4 = 1234567894\n' >> "$ladder-4.conf"

    for step in 1 2 3 4; do
        case "$step" in
            1) label="keys only" ;;
            2) label="+ Jc/Jmin/Jmax" ;;
            3) label="+ S1/S2" ;;
            4) label="+ H1-H4" ;;
        esac
        if probe_config "$ladder-$step.conf"; then
            log "  accepted: $label"
        else
            warn "  REJECTED: $label  ($(cat "$WORKDIR/probe.err" 2>/dev/null))"
            return 0
        fi
    done
    log "  the whole synthetic ladder was accepted"
    return 0
}

report_failure() {
    warn "The module took the interface but rejected the configuration."
    warn "Narrowing it down:"
    bisect_config
    warn "Kernel messages from $DKMS_NAME:"
    # A pipeline ending in tail always succeeds, so test the captured text, not the exit status.
    kmsg="$(dmesg 2>/dev/null | grep -iE "$PROBE_IF|$DKMS_NAME" | tail -n 10 || true)"
    if [ -n "$kmsg" ]; then
        printf '%s\n' "$kmsg" >&2
    else
        warn "  (none - the rejection came from netlink policy validation, before the module's own checks ran)"
    fi
    warn "Tools: $TOOLS_REF, $(awg --version 2>/dev/null) at $(command -v awg)"
    warn "Module: $MODULE_REF, running version $(cat "/sys/module/$DKMS_NAME/version" 2>/dev/null || echo unknown)"
    if command -v genl >/dev/null 2>&1; then
        # A genl family version of 3 means these tools cannot configure this module at all:
        # they encode H1-H4 as a string, which a genl 3 policy rejects as not being a u64.
        warn "Netlink family: $(genl ctrl list 2>/dev/null | grep -A2 -i "$DKMS_NAME" | tr '\n' ' ' || echo unknown)"
    fi
}

enable_module_debug || true

# The node's own configuration is the only thing that proves this node will come up, so prefer
# it. Never let a problem reading it abort the script, though: bailing out here would leave the
# module installed and the data path switched but untested, which is the failure we are guarding
# against in the first place.
if [ -r "$NODE_CONF" ] && awg-quick strip "$NODE_CONF" > "$PROBE_CONF" 2>/dev/null; then
    log "Verifying with this node's own configuration"
else
    log "Verifying with a synthetic 2.x configuration"
    # No ListenPort here: a port the running tunnel already holds would fail the probe for a
    # reason that has nothing to do with the module.
    {
        printf '[Interface]\nPrivateKey = %s\n' "$PROBE_KEY"
        printf 'Jc = 4\nJmin = 40\nJmax = 70\n'
        printf 'S1 = 15\nS2 = 30\n'
        printf 'H1 = 1234567891\nH2 = 1234567892\nH3 = 1234567893\nH4 = 1234567894\n'
    } > "$PROBE_CONF"
fi

# Not a bare call: under set -e a non-zero return outside a condition ends the script, and this
# one is expected to fail sometimes - that is the entire point of the check.
PROBE_RC=0
probe_config "$PROBE_CONF" || PROBE_RC=$?
case "$PROBE_RC" in
    0) ;;
    2)  warn "The module is loaded but 'ip link add type $DKMS_NAME' failed."
        rollback
        exit 1 ;;
    *)  report_failure
        rollback
        exit 1 ;;
esac

log "Verified: the kernel data path applies a real 2.x configuration."
log "Done. Bring an interface up with:  awg-quick up awg0"
log "      Persist it with:             systemctl enable --now awg-quick@awg0"
