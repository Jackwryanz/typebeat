#!/usr/bin/env bash
# Regression test for setup.sh's uv resolution (backlog 349). Run by hand:
#
#   bash tests/test_setup_sh.sh
#
# Exit code 0 when every case passes. Nothing is downloaded: setup.sh runs from scratch copies,
# with --plan-only or, for the setup-sentinel cases (backlog 353), in full against a fake uv. A fake python3 / python3.13 / python3.11 on PATH exits 103 with a stderr line, so a
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

# ---- Setup sentinel (backlog 353): full runs against a FAKE uv, nothing downloaded. ----
# 'uv venv' writes .venv/bin/python as a script exiting 0 (every python -c step "succeeds") or,
# with FAKE_PY_BROKEN, exiting 1 (the first python step fails, as a venv without torch does).
# 'uv pip' succeeds unless FAKE_UV_FAIL_TORCH is set and the call installs torch.
mkdir -p "$work/fakeuv"
cat > "$work/fakeuv/uv" <<'FAKE'
#!/bin/sh
case "$1" in
    venv)
        mkdir -p .venv/bin
        if [ -n "${FAKE_PY_BROKEN:-}" ]; then printf '#!/bin/sh\nexit 1\n'; else printf '#!/bin/sh\nexit 0\n'; fi > .venv/bin/python
        chmod +x .venv/bin/python ;;
    pip)
        if [ -n "${FAKE_UV_FAIL_TORCH:-}" ]; then case "$*" in *torch==*) exit 1 ;; esac; fi ;;
esac
exit 0
FAKE
chmod +x "$work/fakeuv/uv"

new_lab() { mkdir -p "$work/$1"; cp "$here/../setup.sh" "$work/$1/setup.sh"; }
run_lab() { PATH="$work/fakeuv:$base_path" bash "$work/$1/setup.sh" 2>&1; }

new_lab run-ok
out="$(run_lab run-ok)"; rc=$?
content="$(cat "$work/run-ok/.venv/.typebeat-setup-ok" 2>/dev/null)"
[ $rc -eq 0 ] && printf '%s' "$out" | grep -q 'lyriclab environment ready' \
    && printf '%s\n' "$content" | grep -qx 'python=3.11' && printf '%s\n' "$content" | grep -qx 'torch=2.5.1' \
    && printf '%s\n' "$content" | grep -qx 'device=cpu' \
    && printf '%s\n' "$content" | grep -qE '^created=[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$'
check "sentinel: a successful setup writes it" $? "$out
sentinel: $content"

out="$(run_lab run-ok)"; rc=$?
[ $rc -eq 0 ] && printf '%s' "$out" | grep -q 'already present' && ! printf '%s' "$out" | grep -q 'creating venv'
check "sentinel: present -> already present, nothing rebuilt" $? "$out"

new_lab run-stale
mkdir -p "$work/run-stale/.venv/bin"
printf '#!/bin/sh\nexit 0\n' > "$work/run-stale/.venv/bin/python"; chmod +x "$work/run-stale/.venv/bin/python"
echo 'from the broken install' > "$work/run-stale/.venv/canary.txt"
out="$(run_lab run-stale)"; rc=$?
[ $rc -eq 0 ] && printf '%s' "$out" | grep -q 'removing an incomplete environment' \
    && [ ! -e "$work/run-stale/.venv/canary.txt" ] && [ -f "$work/run-stale/.venv/.typebeat-setup-ok" ]
check "sentinel: a sentinel-less venv is removed and rebuilt" $? "$out"

new_lab run-torch-fails
out="$(FAKE_UV_FAIL_TORCH=1 run_lab run-torch-fails)"; rc=$?
[ $rc -eq 1 ] && printf '%s' "$out" | grep -q '^setup failed: installing torch failed' && [ ! -e "$work/run-torch-fails/.venv" ]
check "sentinel: absent, and the venv removed, when torch fails to install" $? "$out"

new_lab run-python-broken
out="$(FAKE_PY_BROKEN=1 run_lab run-python-broken)"; rc=$?
[ $rc -eq 1 ] && printf '%s' "$out" | grep -q '^setup failed:' && [ ! -e "$work/run-python-broken/.venv" ]
check "sentinel: absent, and the venv removed, when the venv python fails" $? "$out"

# The failure cleanup removes the sentinel with the venv, so the runs above cannot see WHERE it is
# written; the ordering protects against a run KILLED part way (the game kills the process on
# cancel, and no cleanup runs). Pinned statically: inside .venv, after the last uv/python command,
# with only the final message after it.
code="$(grep -vE '^[[:space:]]*(#|$)' "$here/../setup.sh")"
write="$(printf '%s\n' "$code" | grep -n '> "\$SENTINEL"' | tail -n 1 | cut -d: -f1)"
last_native="$(printf '%s\n' "$code" | grep -nE '^[[:space:]]*"\$(UV|PY)"' | tail -n 1 | cut -d: -f1)"
after="$(printf '%s\n' "$code" | sed -n "$((${write:-0} + 1)),\$p")"
printf '%s\n' "$code" | grep -q "^SENTINEL='\.venv/" && [ -n "$write" ] && [ -n "$last_native" ] \
    && [ "$write" -gt "$last_native" ] && [ "$after" = "echo 'lyriclab environment ready'" ]
check "sentinel: inside .venv, written after the last native command, then only the final message" $? "write=$write last_native=$last_native after:
$after"

if [ $failures -ne 0 ]; then echo "$failures case(s) failed"; exit 1; fi
echo 'all setup.sh cases passed'
