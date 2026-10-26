# scripts\uninstall.ps1 --dry-run must list every installed part and remove nothing (≙ Tests/uninstall_dry_run.sh;
# docs/DESIGN.md 11.2). Run by make.cmd test (test-uninstall), in the same process.

$Root = Split-Path -Parent $PSScriptRoot
$Uninstall = Join-Path $Root 'scripts\uninstall.ps1'

function Fail([string]$what) {
    [Console]::Error.WriteLine("FAIL: $what")
    exit 1
}
function StageDirs { @(Get-ChildItem -LiteralPath ([IO.Path]::GetTempPath()) -Directory -Filter 'kefproxy.*' -ErrorAction SilentlyContinue | ForEach-Object { $_.Name } | Sort-Object) -join '|' }

$savedError = [Console]::Error
[Console]::SetError([IO.TextWriter]::Null)   # the expected "unknown argument" message
$global:LASTEXITCODE = 0
& $Uninstall --bogus 2>$null | Out-Null
$code = $LASTEXITCODE
[Console]::SetError($savedError)
if ($code -ne 2) { Fail "uninstall.ps1 --bogus: exit $code, want 2" }

$before = StageDirs
$global:LASTEXITCODE = 0
$out = @(& $Uninstall --dry-run --purge | ForEach-Object { [string]$_ })
if ($LASTEXITCODE -ne 0) { Fail "uninstall.ps1 --dry-run --purge: exit $LASTEXITCODE" }
$text = $out -join "`n"
$programFiles = [Environment]::GetFolderPath('ProgramFiles')
$logDir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'LSXIIVolumeFixer'
foreach ($want in "Remove-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name 'io.github.roberdorfer.lsxiivolumefixer'",
                  "Remove-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run' -Name 'io.github.roberdorfer.lsxiivolumefixer'",
                  "Remove-Item -LiteralPath '$programFiles\LSXIIVolumeFixer' -Recurse",
                  "Remove-Item -LiteralPath 'HKLM:\SOFTWARE\io.github.roberdorfer.lsxiivolumefixer\State'`n",
                  "Remove-Item -LiteralPath 'HKLM:\SOFTWARE\io.github.roberdorfer.lsxiivolumefixer' -Recurse",
                  "Stop-Process -Id `$p.Id -Force",
                  "Remove-Item 'HKCU:\Software\io.github.roberdorfer.lsxiivolumefixer' -Recurse; Remove-Item '$logDir' -Recurse") {
    if (-not $text.Contains($want)) { Fail "missing: $want" }
}
# State is never deleted recursively (users may set values there; a subkey there stops the script).
if ($text -match "Remove-Item -LiteralPath '[^']*\\State' -Recurse") { Fail 'State deleted recursively' }
$tokens = $null; $errors = $null
$start = [Array]::IndexOf($out, 'dry run; admin script that would run with administrator privileges:')
if ($start -lt 0) { Fail 'no "dry run; admin script ..." line in the output' }
$adminLines = @($out[($start + 1)..($out.Count - 1)] | Where-Object { -not $_.StartsWith('then, as ') })
[void][System.Management.Automation.Language.Parser]::ParseInput(($adminLines -join "`r`n"), [ref]$tokens, [ref]$errors)
if ($errors.Count -gt 0) { Fail ('admin script does not parse: ' + $errors[0].Message) }

# Without --purge only the default-output marker is reset, so a reinstall makes the LSX II the default output again.
$global:LASTEXITCODE = 0
$plain = @(& $Uninstall --dry-run | ForEach-Object { [string]$_ }) -join "`n"
if ($LASTEXITCODE -ne 0) { Fail "uninstall.ps1 --dry-run: exit $LASTEXITCODE" }
if (-not $plain.Contains("Remove-ItemProperty 'HKCU:\Software\io.github.roberdorfer.lsxiivolumefixer' -Name defaultOutputSetFor")) {
    Fail 'default-output marker not reset without --purge'
}
if ($plain.Contains("Remove-Item 'HKCU:")) { Fail 'settings deleted without --purge' }

# The staging directory holding admin.ps1 must be removed (no new %TEMP%\kefproxy.* left behind).
if ((StageDirs) -ne $before) { Fail 'staging directory left behind' }
Write-Output 'uninstall_dry_run: passed'
exit 0
