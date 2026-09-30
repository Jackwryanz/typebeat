# One-time environment setup for the vendored lyriclab aligner (see README.md).
# Creates .venv beside this script with the pinned dependency set. The game runs this from the
# "install local auto-aligner" action (settings / first-run setup); it can also be run by hand.
# torch 2.5.x pinned deliberately (2.6 breaks Demucs checkpoint loading); Python 3.11
# pinned for wheel coverage.
#
# Always uv, never the player's own Python. uv (on PATH, else a pinned copy in .uv\, else that
# pinned copy downloaded now: a single static binary, ~15 MB, no admin, no PATH changes) fetches a
# managed CPython 3.11 by itself, so whatever Python the player has installed, or none, is
# irrelevant. Do NOT "fix" a Python-version problem by accepting a range of system Pythons:
# torch 2.5.1 publishes wheels for CPython 3.9 to 3.12 only, so a 3.13/3.14 venv can never
# install it, while the uv-managed 3.11 works on every machine.
#
# Windows PowerShell 5.1 landmine (backlog 349): under $ErrorActionPreference = 'Stop', a native
# command whose stderr is REDIRECTED ('2>$null', '2>&1', piping stderr) turns each stderr line
# into a terminating NativeCommandError. The old 'py -3.11' probe died that way for every player
# with a non-3.11 Python, before it ever reached the uv download. Native commands here run with
# stderr NOT redirected (it passes straight through to the game's log) and are judged by
# $LASTEXITCODE; if a probe is ever needed again, run it through 'cmd /c "... >nul 2>nul"'.
# tests\test_setup_ps1.ps1 pins this with a fake py that writes to stderr and exits 103.
#
# -Device cpu  (default): CPU-only torch wheels (~200 MB download).
# -Device cuda: CUDA 12.1 torch wheels (~2.5 GB download) - alignment runs on an NVIDIA GPU
#               (align_lyrics.py --device cuda). Requires a reasonably recent NVIDIA driver.
# -PlanOnly: print which uv would be used (or downloaded) and exit without changing anything.
#            Test seam for tests\test_setup_ps1.ps1.
#
# ffmpeg: align_lyrics.py shells out to an `ffmpeg` on PATH for audio decode. Most machines
# don't have one, so the imageio-ffmpeg wheel's bundled static build is copied into the venv's
# Scripts dir as ffmpeg.exe; the game prepends that dir to PATH when it runs the aligner.

param(
    [ValidateSet('cpu', 'cuda')]
    [string]$Device = 'cpu',
    [switch]$PlanOnly
)

$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1's Invoke-WebRequest progress bar slows downloads by an order of magnitude.
$ProgressPreference = 'SilentlyContinue'
Set-Location $PSScriptRoot

$uvVersion = '0.5.14'
$uvUrl = "https://github.com/astral-sh/uv/releases/download/$uvVersion/uv-x86_64-pc-windows-msvc.zip"

# One plain line on stderr, drop any half-built venv so the next attempt starts clean (the game
# treats an existing .venv\Scripts\python.exe as "installed" and never re-runs this script), exit 1.
function Stop-Setup([string]$message) {
    [Console]::Error.WriteLine("setup failed: $message")
    if (-not $PlanOnly -and (Test-Path '.venv')) {
        try { Remove-Item '.venv' -Recurse -Force -ErrorAction Stop } catch { }
    }
    exit 1
}

# Runs a native command (stderr not redirected, see the header) and fails the setup on a
# non-zero exit code.
function Invoke-Native([string]$what, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) { Stop-Setup "$what (exit code $LASTEXITCODE)" }
}

try {
    if (-not $PlanOnly -and (Test-Path '.venv\Scripts\python.exe')) {
        Write-Output 'lyriclab environment already present'
        exit 0
    }

    # Prefer an existing uv; otherwise the pinned local copy; otherwise download that copy.
    $uvExe = $null
    $uvCmd = Get-Command uv -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($uvCmd) {
        $uvExe = $uvCmd.Source
    } elseif (Test-Path '.uv\uv.exe') {
        $uvExe = (Resolve-Path '.uv\uv.exe').Path
    }

    if ($PlanOnly) {
        if ($uvExe) { Write-Output "plan: use uv at $uvExe" } else { Write-Output "plan: download uv $uvVersion from $uvUrl" }
        exit 0
    }

    if (-not $uvExe) {
        Write-Output "downloading uv $uvVersion (manages its own Python 3.11)..."
        # GitHub requires TLS 1.2, which older .NET Framework installs do not enable by default.
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        $zip = Join-Path $env:TEMP "uv-$uvVersion.zip"
        Invoke-WebRequest -UseBasicParsing -Uri $uvUrl -OutFile $zip
        New-Item -ItemType Directory -Force -Path '.uv' | Out-Null
        Expand-Archive -Path $zip -DestinationPath '.uv' -Force
        Remove-Item $zip -ErrorAction SilentlyContinue
        if (-not (Test-Path '.uv\uv.exe')) { Stop-Setup 'the downloaded uv archive did not contain uv.exe' }
        $uvExe = (Resolve-Path '.uv\uv.exe').Path
    }

    $torchIndex = if ($Device -eq 'cuda') { 'https://download.pytorch.org/whl/cu121' } else { 'https://download.pytorch.org/whl/cpu' }
    $py = '.venv\Scripts\python.exe'

    Write-Output 'creating venv with uv...'
    Invoke-Native 'creating the Python 3.11 environment failed' { & $uvExe venv .venv --python 3.11 }
    Write-Output "installing torch ($Device) - this is the big download..."
    Invoke-Native 'installing torch failed' { & $uvExe pip install --python $py --index-url $torchIndex torch==2.5.1 torchaudio==2.5.1 }
    Write-Output 'installing aligner dependencies...'
    Invoke-Native 'installing the aligner dependencies failed' { & $uvExe pip install --python $py demucs==4.0.1 soundfile pyphen num2words tqdm imageio-ffmpeg }

    if (-not (Test-Path $py)) { Stop-Setup 'venv creation failed' }

    Write-Output 'provisioning ffmpeg into the venv...'
    Invoke-Native 'provisioning ffmpeg failed' { & $py -c "import imageio_ffmpeg, shutil; shutil.copy(imageio_ffmpeg.get_ffmpeg_exe(), r'.venv\Scripts\ffmpeg.exe')" }

    if ($Device -eq 'cuda') {
        Write-Output 'verifying CUDA is usable by torch...'
        & $py -c "import torch, sys; sys.exit(0 if torch.cuda.is_available() else 1)"
        if ($LASTEXITCODE -ne 0) {
            Write-Output 'WARNING: torch cannot see a CUDA device (driver too old?) - alignment will fall back to CPU'
        }
    }

    Write-Output 'lyriclab environment ready'
} catch {
    Stop-Setup $_.Exception.Message
}
