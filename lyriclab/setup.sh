#!/usr/bin/env bash
# One-time environment setup for the vendored lyriclab aligner (see README.md).
# POSIX counterpart of setup.ps1 (Linux/macOS). Creates .venv beside this script with the
# pinned dependency set. torch 2.5.x pinned deliberately (2.6 breaks Demucs checkpoint
# loading); Python 3.11 pinned for wheel coverage.
#
# Always uv, never the player's own Python: uv (on PATH, else a pinned copy in .uv/, else that
# pinned copy downloaded now, the same version setup.ps1 pins) fetches a managed CPython 3.11 by
# itself, so whatever python3 the system has, or none, is irrelevant. Do NOT fall back to "any
# python3": torch 2.5.1 publishes wheels for CPython 3.9 to 3.12 only, so a 3.13+ venv can never
# install it, while the uv-managed 3.11 works everywhere (backlog 349).
#
# Usage: setup.sh [cpu|cuda] [--plan-only]
#   cpu (default) or cuda (CUDA 12.1 torch wheels).
#   --plan-only prints which uv would be used (or downloaded) and exits without changing anything.
#
# ffmpeg: align_lyrics.py shells out to an `ffmpeg` on PATH. The imageio-ffmpeg wheel's
# bundled static build is linked into the venv's bin dir; the game prepends that dir to
# PATH when it runs the aligner.
set -euo pipefail

cd "$(dirname "$0")"

UV_VERSION='0.5.14'

DEVICE='cpu'
PLAN_ONLY=0
for arg in "$@"; do
    case "$arg" in
        cpu|cuda) DEVICE="$arg" ;;
        --plan-only) PLAN_ONLY=1 ;;
        *) echo "setup failed: unknown argument '$arg' (expected cpu, cuda or --plan-only)" >&2; exit 1 ;;
    esac
done

case "$DEVICE" in
    cpu)  TORCH_INDEX='https://download.pytorch.org/whl/cpu' ;;
    cuda) TORCH_INDEX='https://download.pytorch.org/whl/cu121' ;;
esac

PY=".venv/bin/python"

# One plain line on stderr, drop any half-built venv so the next attempt starts clean (the game
# treats an existing .venv/bin/python as "installed" and never re-runs this script), exit 1.
fail() {
    echo "setup failed: $1" >&2
    [ "$PLAN_ONLY" -eq 1 ] || rm -rf .venv
    exit 1
}

if [ "$PLAN_ONLY" -eq 0 ] && [ -x "$PY" ]; then
    echo 'lyriclab environment already present'
    exit 0
fi

UV=''
if command -v uv >/dev/null 2>&1; then
    UV="$(command -v uv)"
elif [ -x .uv/uv ]; then
    UV="$PWD/.uv/uv"
fi

if [ -z "$UV" ]; then
    case "$(uname -s)" in
        Linux)  OS_TRIPLE='unknown-linux-musl' ;; # static build: no glibc version dependency
        Darwin) OS_TRIPLE='apple-darwin' ;;
        *) fail "no uv build for $(uname -s); install uv (https://docs.astral.sh/uv/) and retry" ;;
    esac
    case "$(uname -m)" in
        x86_64|amd64)  ARCH='x86_64' ;;
        aarch64|arm64) ARCH='aarch64' ;;
        *) fail "no uv build for $(uname -m); install uv (https://docs.astral.sh/uv/) and retry" ;;
    esac
    UV_URL="https://github.com/astral-sh/uv/releases/download/$UV_VERSION/uv-$ARCH-$OS_TRIPLE.tar.gz"
fi

if [ "$PLAN_ONLY" -eq 1 ]; then
    if [ -n "$UV" ]; then echo "plan: use uv at $UV"; else echo "plan: download uv $UV_VERSION from $UV_URL"; fi
    exit 0
fi

if [ -z "$UV" ]; then
    command -v curl >/dev/null 2>&1 || fail 'curl is needed to download uv; install curl (or uv) and retry'
    echo "downloading uv $UV_VERSION (manages its own Python 3.11)..."
    mkdir -p .uv
    # The tarball holds uv-<target>/uv and uvx; strip that top directory.
    curl -fsSL "$UV_URL" | tar -xzf - -C .uv --strip-components=1 || fail 'downloading uv failed'
    [ -x .uv/uv ] || fail 'the downloaded uv archive did not contain uv'
    UV="$PWD/.uv/uv"
fi

echo 'creating venv with uv...'
"$UV" venv .venv --python 3.11 || fail 'creating the Python 3.11 environment failed'
echo "installing torch ($DEVICE), this is the big download..."
"$UV" pip install --python "$PY" --index-url "$TORCH_INDEX" torch==2.5.1 torchaudio==2.5.1 || fail 'installing torch failed'
echo 'installing aligner dependencies...'
"$UV" pip install --python "$PY" demucs==4.0.1 soundfile pyphen num2words tqdm imageio-ffmpeg || fail 'installing the aligner dependencies failed'

[ -x "$PY" ] || fail 'venv creation failed'

echo 'provisioning ffmpeg into the venv...'
"$PY" -c "import imageio_ffmpeg, os; src = imageio_ffmpeg.get_ffmpeg_exe(); dst = '.venv/bin/ffmpeg'; os.path.lexists(dst) and os.remove(dst); os.symlink(src, dst)" || fail 'provisioning ffmpeg failed'

echo 'lyriclab environment ready'
