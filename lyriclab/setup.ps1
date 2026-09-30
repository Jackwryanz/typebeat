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
# stderr NOT redirected (it passes straight through to the game's log, which is safe even when
# the game captures both streams: uv writes its progress there) and are judged by $LASTEXITCODE;
# if a probe is ever needed again, run it through 'cmd /c "... >nul 2>nul"'.
# tests\test_setup_ps1.ps1 pins this with a fake py that writes to stderr and exits 103.
#
# Setup sentinel (backlog 353): the LAST act of a fully successful run writes
# .venv\.typebeat-setup-ok (the pinned Python and torch versions, the device and a UTC timestamp).
# The game treats the aligner as installed only when that file exists beside the venv's python, so
# a run that died or was killed part way (before 349 a failed torch install could still leave a
# python.exe behind) reads as "needs repair" instead of "installed". Nothing may be added after the
# sentinel write except the final message; tests\test_setup_ps1.ps1 pins the ordering. A .venv
# WITHOUT the sentinel is treated as incomplete and rebuilt from scratch. (The game probes such a
# venv's imports first and writes the sentinel itself when they load, so it only hands this script
# a venv that is actually broken; a by-hand run simply rebuilds.)
#
# -Device cpu  (default): CPU-only torch wheels (~200 MB download).
# -Device cuda: CUDA 12.1 torch wheels (~2.5 GB download) - alignment runs on an NVIDIA GPU
#               (align_lyrics.py --device cuda). Requires a reasonably recent NVIDIA driver.
# -PlanOnly: print which uv would be used (or downloaded) and exit without changing anything.
#            Test seam for tests\test_setup_ps1.ps1. It decides by presence alone; a real run
#            also checks that the chosen uv answers --version and falls through to the next
#            candidate when it does not, so a stale uv on PATH or a half-extracted .uv\ copy
#            heals instead of failing every retry the same way.
#
# Failure output: one plain sentence on stderr that starts "setup failed:", says what failed and
# what the player can do (retry, check the connection, or install later from Settings), then the
# raw detail on a few following lines. The game keeps only the last lines of the output for its
# log, so the sentence comes first and the detail stays short enough not to push it out; the
# failing tool's own output (a uv resolver error, a pip traceback) is already printed above it.
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
$pythonVersion = '3.11'
$torchVersion = '2.5.1'
$sentinel = '.venv\.typebeat-setup-ok'

# What the player can do about a failure. Anything that downloads can fail on the network; the
# rest can only be retried. Either way the game's Settings page offers the install again later.
$retryNetwork = 'check your internet connection and retry, or install the aligner later from Settings'
$retryPlain = 'retry, or install the aligner later from Settings'

# The failure report (see the header): the sentence, then up to five lines of raw detail, on
# stderr. Drops any half-built venv so the next attempt starts clean (the sentinel is inside
# .venv, so it can never outlive a failed run), then exits 1.
function Stop-Setup([string]$what, [string]$advice = $retryPlain, [string[]]$detail = @()) {
    [Console]::Error.WriteLine("setup failed: $what; $advice.")
    $lines = @($detail | ForEach-Object { "$_" -split "`r?`n" } | Where-Object { $_.Trim() -ne '' })
    foreach ($line in ($lines | Select-Object -First 5)) { [Console]::Error.WriteLine("  detail: $($line.Trim())") }
    if (-not $PlanOnly -and (Test-Path '.venv')) {
        try { Remove-Item '.venv' -Recurse -Force -ErrorAction Stop } catch { }
    }
    exit 1
}

# Runs a native command (stderr not redirected, see the header) and fails the setup on a
# non-zero exit code.
function Invoke-Native([string]$what, [string]$advice, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) { Stop-Setup $what $advice "exit code $LASTEXITCODE; the tool's own output is above" }
}

# True when $exe runs and answers --version. Its output (the version line) passes through to the
# log; a file that is not a runnable program throws, which counts as "does not run".
function Test-Uv([string]$exe) {
    try {
        # Captured, then shown with Write-Host, so the function returns only the verdict.
        $version = & $exe --version
        $ok = $LASTEXITCODE -eq 0
        Write-Host "uv at ${exe}: $version (exit code $LASTEXITCODE)"
        return $ok
    } catch {
        # The first line only, without the "At <script>:<line> char:<n>" position PowerShell
        # appends to it: the game keeps just the last few lines of the output for its log.
        $reason = (($_.Exception.Message -split "`r?`n")[0] -replace '\s*At .*:\d+ char:\d+.*$', '').Trim()
        Write-Host "uv at $exe does not run: $reason"
        return $false
    }
}

# Any terminating error from here on (a failed download, a locked file, a missing cmdlet) is
# reported as the step it happened in. A trap rather than try/catch so that nothing follows the
# sentinel write but the final message (tests\test_setup_ps1.ps1 pins that ordering).
$step = 'preparing the setup'
$stepAdvice = $retryPlain
trap {
    Stop-Setup "$step failed" $stepAdvice @($_.Exception.Message, ($_.InvocationInfo.PositionMessage -split "`r?`n" | Select-Object -First 1))
}

if (-not $PlanOnly -and (Test-Path $sentinel)) {
    Write-Output 'lyriclab environment already present'
    exit 0
}

if (-not $PlanOnly -and (Test-Path '.venv')) {
    Write-Output 'removing an incomplete environment left by an earlier setup...'
    $step = 'removing the incomplete environment left by an earlier setup'
    $stepAdvice = 'close anything that might be using the aligner folder and retry, or install the aligner later from Settings'
    Remove-Item '.venv' -Recurse -Force
}

# Prefer an existing uv; otherwise the pinned local copy; otherwise download that copy. A real
# run only keeps a candidate that actually runs (see -PlanOnly in the header).
$step = 'looking for uv'
$stepAdvice = $retryPlain
$uvExe = $null
$uvCmd = Get-Command uv -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if ($uvCmd -and ($PlanOnly -or (Test-Uv $uvCmd.Source))) {
    $uvExe = $uvCmd.Source
} elseif (Test-Path '.uv\uv.exe') {
    $pinned = (Resolve-Path '.uv\uv.exe').Path
    if ($PlanOnly -or (Test-Uv $pinned)) {
        $uvExe = $pinned
    } else {
        Write-Output 'the pinned uv copy is damaged, downloading it again...'
        Remove-Item '.uv' -Recurse -Force
    }
}

if ($PlanOnly) {
    if ($uvExe) { Write-Output "plan: use uv at $uvExe" } else { Write-Output "plan: download uv $uvVersion from $uvUrl" }
    exit 0
}

if (-not $uvExe) {
    Write-Output "downloading uv $uvVersion (manages its own Python 3.11)..."
    $step = "downloading uv $uvVersion"
    $stepAdvice = $retryNetwork
    # GitHub requires TLS 1.2, which older .NET Framework installs do not enable by default.
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $zip = Join-Path $env:TEMP ("uv-$uvVersion-" + [Guid]::NewGuid().ToString('N') + '.zip')
    try {
        Invoke-WebRequest -UseBasicParsing -Uri $uvUrl -OutFile $zip
    } catch {
        Stop-Setup "downloading uv $uvVersion failed" $retryNetwork @($_.Exception.Message, $uvUrl)
    }
    $step = "unpacking uv $uvVersion"
    New-Item -ItemType Directory -Force -Path '.uv' | Out-Null
    Expand-Archive -Path $zip -DestinationPath '.uv' -Force
    Remove-Item $zip -ErrorAction SilentlyContinue
    # The zip holds uv.exe at its root today; search rather than assume that layout.
    if (-not (Test-Path '.uv\uv.exe')) {
        $found = Get-ChildItem '.uv' -Recurse -Filter 'uv.exe' | Select-Object -First 1
        if (-not $found) { Stop-Setup 'the downloaded uv archive did not contain uv.exe' $retryNetwork $uvUrl }
        Move-Item $found.FullName '.uv\uv.exe' -Force
    }
    $uvExe = (Resolve-Path '.uv\uv.exe').Path
    if (-not (Test-Uv $uvExe)) { Stop-Setup 'the downloaded uv does not run' $retryNetwork $uvExe }
}
Write-Output "using uv at $uvExe"

$torchIndex = if ($Device -eq 'cuda') { 'https://download.pytorch.org/whl/cu121' } else { 'https://download.pytorch.org/whl/cpu' }
$py = '.venv\Scripts\python.exe'

Write-Output 'creating venv with uv...'
$step = "creating the Python $pythonVersion environment"
Invoke-Native "creating the Python $pythonVersion environment failed" $retryNetwork { & $uvExe venv .venv --python $pythonVersion }
Write-Output "installing torch ($Device) - this is the big download..."
$step = 'installing torch'
Invoke-Native 'installing torch failed' $retryNetwork { & $uvExe pip install --python $py --index-url $torchIndex "torch==$torchVersion" "torchaudio==$torchVersion" }
Write-Output 'installing aligner dependencies...'
$step = 'installing the aligner dependencies'
Invoke-Native 'installing the aligner dependencies failed' $retryNetwork { & $uvExe pip install --python $py demucs==4.0.1 soundfile pyphen num2words tqdm imageio-ffmpeg }

if (-not (Test-Path $py)) { Stop-Setup 'the Python environment was not created' $retryPlain "no $py after uv venv" }

Write-Output 'provisioning ffmpeg into the venv...'
$step = 'provisioning ffmpeg'
Invoke-Native 'provisioning ffmpeg failed' $retryPlain { & $py -c "import imageio_ffmpeg, shutil; shutil.copy(imageio_ffmpeg.get_ffmpeg_exe(), r'.venv\Scripts\ffmpeg.exe')" }

Write-Output 'verifying the installed packages import...'
$step = 'verifying the installed packages'
Invoke-Native 'the installed packages do not import' $retryPlain { & $py -c "import torch, torchaudio, demucs, soundfile, pyphen, num2words" }

if ($Device -eq 'cuda') {
    Write-Output 'verifying CUDA is usable by torch...'
    & $py -c "import torch, sys; sys.exit(0 if torch.cuda.is_available() else 1)"
    if ($LASTEXITCODE -ne 0) {
        Write-Output 'WARNING: torch cannot see a CUDA device (driver too old?) - alignment will fall back to CPU'
    }
}

# LAST act, after every step above checked out: the sentinel the game reads as "installed".
$step = 'writing the setup sentinel'
$stamp = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
[IO.File]::WriteAllText((Join-Path $PSScriptRoot $sentinel), "python=$pythonVersion`ntorch=$torchVersion`ndevice=$Device`ncreated=$stamp`n")
Write-Output 'lyriclab environment ready'
