#!/usr/bin/env bash
# Regression test for setup.sh's uv resolution (backlog 349). Run by hand:
#
#   bash tests/test_setup_sh.sh
#
# Exit code 0 when every case passes. Nothing is downloaded: setup.sh runs with --plan-only from a
# scratch copy. A fake python3 / python3.13 / python3.11 on PATH exits 103 with a stderr line, so a
# setup.sh that still consulted the system Python would show it; fake uname binaries pick the
# platform so every per-arch tarball is checked on any host (Git Bash included).
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/fakebin" "$work/lab"
cp "$here/../setup.sh" "$work/lab/setup.sh"

for name in python3 python3.13 python3.11 python; do
    printf '#!/bin/sh\necho "No suitable Python runtime found" >&2\nexit 103\n' > "$work/fakebin/$name"
    chmod +x "$work/fakebin/$name"
done

base_path="$work/fakebin:$(dirname "$(command -v tar)"):$(dirname "$(command -v dirname)")"
failures=0

check() { # name, condition-result, output
    if [ "$2" -eq 0 ]; then echo "PASS  $1"; else echo "FAIL  $1"; printf '%s\n' "$3" | sed 's/^/      /'; failures=$((failures + 1)); fi
}

plan() { # os, arch -> runs setup.sh --plan-only with that fake uname
    printf '#!/bin/sh\ncase "$1" in -s) echo %s ;; -m) echo %s ;; esac\n' "$1" "$2" > "$work/fakebin/uname"
    chmod +x "$work/fakebin/uname"
    PATH="$base_path" bash "$work/lab/setup.sh" --plan-only 2>&1
}

version="$(sed -n "s/^UV_VERSION='\(.*\)'$/\1/p" "$here/../setup.sh")"
ps1_version="$(sed -n "s/^\$uvVersion = '\(.*\)'.*$/\1/p" "$here/../setup.ps1")"
[ -n "$version" ] && [ "$version" = "$ps1_version" ]
check "setup.sh pins the same uv as setup.ps1 ($version vs $ps1_version)" $? ""

while read -r os arch triple; do
    out="$(plan "$os" "$arch")"; rc=$?
    [ $rc -eq 0 ] && printf '%s' "$out" | grep -q "plan: download uv $version from https://github.com/astral-sh/uv/releases/download/$version/uv-$triple.tar.gz" \
        && ! printf '%s' "$out" | grep -q 'No suitable Python'
    check "no uv on $os/$arch -> downloads uv-$triple" $? "$out"
done <<'EOF'
Linux x86_64 x86_64-unknown-linux-musl
Linux aarch64 aarch64-unknown-linux-musl
Darwin x86_64 x86_64-apple-darwin
Darwin arm64 aarch64-apple-darwin
EOF

out="$(plan FreeBSD amd64)"; rc=$?
[ $rc -ne 0 ] && printf '%s' "$out" | grep -q '^setup failed: no uv build for FreeBSD'
check "unsupported OS -> one plain 'setup failed' line" $? "$out"

mkdir -p "$work/lab/.uv"; printf '#!/bin/sh\n' > "$work/lab/.uv/uv"; chmod +x "$work/lab/.uv/uv"
out="$(plan Linux x86_64)"
printf '%s' "$out" | grep -q "plan: use uv at .*/.uv/uv"
check "pinned .uv/uv present -> uses it" $? "$out"

mkdir -p "$work/uvbin"; printf '#!/bin/sh\n' > "$work/uvbin/uv"; chmod +x "$work/uvbin/uv"
out="$(PATH="$work/uvbin:$base_path" bash "$work/lab/setup.sh" --plan-only 2>&1)"
printf '%s' "$out" | grep -q "plan: use uv at $work/uvbin/uv"
check "uv on PATH -> uses it" $? "$out"

bad="$(grep -nE '^[^#]*\bpython3?(\.[0-9]+)?[[:space:]]+-m[[:space:]]+venv' "$here/../setup.sh")"
[ -z "$bad" ]
check "setup.sh never builds a venv from the system python" $? "$bad"

if [ $failures -ne 0 ]; then echo "$failures case(s) failed"; exit 1; fi
echo 'all setup.sh cases passed'
