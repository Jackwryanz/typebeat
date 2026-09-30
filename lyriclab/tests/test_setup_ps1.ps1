# Regression test for setup.ps1 against the Windows PowerShell 5.1 native-stderr landmine
# (backlog 349). No Pester: plain PowerShell, run by hand from any shell:
#
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\test_setup_ps1.ps1
#
# Exit code 0 when every case passes. Nothing here downloads or installs anything: setup.ps1 runs
# with -PlanOnly from a scratch copy, so it only reports which uv it would use.
#
# Every child runs the way the game runs setup.ps1 (powershell.exe -File with stdout and stderr
# redirected) with a PATH holding only System32 plus a fake dir whose py.cmd / python.cmd write
# "No suitable Python runtime found" to stderr and exit 103, which is what the real py launcher
# does when the requested version is not installed.

$ErrorActionPreference = 'Stop'

$setup = Join-Path (Split-Path $PSScriptRoot -Parent) 'setup.ps1'
$work = Join-Path ([IO.Path]::GetTempPath()) ("lyriclab-setup-test-" + [Guid]::NewGuid().ToString('N'))
$fake = Join-Path $work 'fakebin'
$lab = Join-Path $work 'lab'
New-Item -ItemType Directory -Force -Path $fake, $lab | Out-Null
Copy-Item $setup (Join-Path $lab 'setup.ps1')

$failingPython = "@echo No suitable Python runtime found 1>&2`r`n@exit /b 103`r`n"
foreach ($name in 'py.cmd', 'python.cmd') {
    [IO.File]::WriteAllText((Join-Path $fake $name), $failingPython)
}

$powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$basePath = "$fake;$env:SystemRoot\System32;$env:SystemRoot\System32\WindowsPowerShell\v1.0"

function Invoke-Child([string]$script, [string]$extraArgs = '', [string]$pathPrefix = '') {
    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = $powershell
    $psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$script`" $extraArgs"
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $psi.EnvironmentVariables['PATH'] = if ($pathPrefix) { "$pathPrefix;$basePath" } else { $basePath }
    $p = [Diagnostics.Process]::Start($psi)
    $out = $p.StandardOutput.ReadToEndAsync()
    $err = $p.StandardError.ReadToEndAsync()
    $p.WaitForExit()
    [pscustomobject]@{ ExitCode = $p.ExitCode; Output = $out.Result + $err.Result }
}

$failures = 0
function Check([string]$name, [bool]$ok, [string]$detail) {
    if ($ok) {
        Write-Output "PASS  $name"
    } else {
        Write-Output "FAIL  $name"
        Write-Output ($detail -split "`n" | ForEach-Object { "      $_" } | Out-String)
        $script:failures++
    }
}

try {
    # Control: the pre-349 probe really does die on the fake py. Without this the cases below
    # could pass vacuously (a fake that never reaches stderr would hide the landmine).
    $old = Join-Path $work 'old_probe.ps1'
    [IO.File]::WriteAllText($old, @'
$ErrorActionPreference = 'Stop'
$py = Get-Command py -ErrorAction SilentlyContinue
if ($py) {
    & py -3.11 -c 'pass' 2>$null
    if ($LASTEXITCODE -eq 0) { $hasPy311 = $true }
}
Write-Output 'probe survived'
'@)
    $r = Invoke-Child $old
    Check 'control: the old 2>$null probe dies on a py that writes stderr' (
        $r.ExitCode -ne 0 -and $r.Output -match 'NativeCommandError' -and $r.Output -notmatch 'probe survived') $r.Output

    # The safe probe form the setup.ps1 header prescribes, should one ever be needed again.
    $safe = Join-Path $work 'safe_probe.ps1'
    [IO.File]::WriteAllText($safe, @'
$ErrorActionPreference = 'Stop'
cmd /c "py -3.11 -c pass >nul 2>nul"
Write-Output "probe survived LASTEXITCODE=$LASTEXITCODE"
'@)
    $r = Invoke-Child $safe
    Check 'control: the cmd /c probe survives with the exit code' (
        $r.ExitCode -eq 0 -and $r.Output -match 'probe survived LASTEXITCODE=103') $r.Output

    $labSetup = Join-Path $lab 'setup.ps1'

    # No uv anywhere and a broken py on PATH: must plan the pinned uv download, not die.
    $r = Invoke-Child $labSetup '-PlanOnly'
    Check 'setup.ps1: failing py, no uv -> plans the pinned uv download' (
        $r.ExitCode -eq 0 -and $r.Output -match 'plan: download uv \d+\.\d+\.\d+ from https://github\.com/astral-sh/uv/releases/download/.+/uv-x86_64-pc-windows-msvc\.zip' -and
        $r.Output -notmatch 'NativeCommandError|No suitable Python') $r.Output

    # A previously downloaded pinned copy is reused.
    New-Item -ItemType Directory -Force -Path (Join-Path $lab '.uv') | Out-Null
    [IO.File]::WriteAllText((Join-Path $lab '.uv\uv.exe'), 'placeholder')
    $r = Invoke-Child $labSetup '-PlanOnly'
    Check 'setup.ps1: failing py, pinned .uv\uv.exe -> uses it' (
        $r.ExitCode -eq 0 -and $r.Output -match 'plan: use uv at .*\\\.uv\\uv\.exe') $r.Output

    # uv on PATH wins over the pinned copy.
    $uvDir = Join-Path $work 'uvbin'
    New-Item -ItemType Directory -Force -Path $uvDir | Out-Null
    Copy-Item (Join-Path $env:SystemRoot 'System32\where.exe') (Join-Path $uvDir 'uv.exe')
    $r = Invoke-Child $labSetup '-PlanOnly' $uvDir
    Check 'setup.ps1: failing py, uv on PATH -> uses it' (
        $r.ExitCode -eq 0 -and $r.Output -match ('plan: use uv at ' + [regex]::Escape((Join-Path $uvDir 'uv.exe')))) $r.Output

    # Static audit: no code line invokes the py launcher or redirects stderr (2>$null, 2>&1).
    $bad = Get-Content $setup | Where-Object { $_ -notmatch '^\s*#' } |
        Where-Object { $_ -match '2>\s*(\$null|&1)' -or $_ -match '(^|[\s&])py(\.exe)?\s+-' }
    Check 'setup.ps1: no py launcher call and no redirected native stderr' (-not $bad) ($bad -join "`n")
} finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures) {
    Write-Output "$failures case(s) failed"
    exit 1
}
Write-Output 'all setup.ps1 cases passed'
exit 0
