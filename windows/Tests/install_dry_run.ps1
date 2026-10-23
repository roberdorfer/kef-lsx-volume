# Regression test for scripts\install.ps1 (≙ Tests/install_dry_run.sh; docs/DESIGN.md 11.2): the --pref value must reach
# Set-ItemProperty unchanged after the admin script is parsed by PowerShell, and the dry run must change nothing.
# Run by make.cmd test (test-install), in the same process (exit in install.ps1 only ends that script).

$Root = Split-Path -Parent $PSScriptRoot
$Install = Join-Path $Root 'scripts\install.ps1'

function Fail([string]$what) {
    [Console]::Error.WriteLine("FAIL: $what")
    exit 1
}

# Grammar (no build needed): driver is not part of this version, unknown arguments are errors; both exit 2. Their
# message goes to [Console]::Error, which is silenced for these two calls.
$savedError = [Console]::Error
[Console]::SetError([IO.TextWriter]::Null)
$global:LASTEXITCODE = 0
& $Install driver --dry-run 2>$null | Out-Null
$code = $LASTEXITCODE
if ($code -ne 2) { [Console]::SetError($savedError); Fail "install.ps1 driver: exit $code, want 2" }
$global:LASTEXITCODE = 0
& $Install --bogus 2>$null | Out-Null
$code = $LASTEXITCODE
[Console]::SetError($savedError)
if ($code -ne 2) { Fail "install.ps1 --bogus: exit $code, want 2" }

# Like macOS: ", \, ', $x and a backtick; Windows traps: a typographic quote (U+2019, a quote character for PowerShell
# too) and a non-ASCII letter (ä: lost if admin.ps1 had no BOM, since PowerShell 5.1 reads such a file as ANSI).
$value = 'a"b\c' + "'" + 'd $x `y`' + [char]0x2019 + 'e' + [char]0x00E4
$global:LASTEXITCODE = 0
$out = @(& $Install helper --dry-run --pref "speakerHost=$value" | ForEach-Object { [string]$_ })
if ($LASTEXITCODE -ne 0) { Fail "install.ps1 --dry-run: exit $LASTEXITCODE" }
$start = [Array]::IndexOf($out, 'dry run; admin script that would run with administrator privileges:')
if ($start -lt 0) { Fail 'no "dry run; admin script ..." line in the output' }
$script = $out[($start + 1)..($out.Count - 1)]

# The whole admin script parses (it is printed the way powershell.exe -File reads the file: without -Encoding, so a
# missing BOM would show up as mangled non-ASCII characters below).
$tokens = $null; $errors = $null
[void][System.Management.Automation.Language.Parser]::ParseInput(($script -join "`r`n"), [ref]$tokens, [ref]$errors)
if ($errors.Count -gt 0) { Fail ('admin script does not parse: ' + $errors[0].Message) }

# The Set-ItemProperty line for speakerHost: parse it and compare the -Value string constant (≙ the /bin/sh printf trick).
$lines = @($script | Where-Object { $_ -match 'Set-ItemProperty' -and $_ -match "-Name 'speakerHost'" })
if ($lines.Count -ne 1) { Fail "want one Set-ItemProperty line for speakerHost, got $($lines.Count)" }
$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseInput($lines[0], [ref]$tokens, [ref]$errors)
if ($errors.Count -gt 0) { Fail ('speakerHost line does not parse: ' + $errors[0].Message) }
$cmd = $ast.Find({ param($a) $a -is [System.Management.Automation.Language.CommandAst] }, $true)
$elements = @($cmd.CommandElements)
$got = $null
for ($i = 0; $i -lt $elements.Count - 1; $i++) {
    if ($elements[$i] -is [System.Management.Automation.Language.CommandParameterAst] -and $elements[$i].ParameterName -eq 'Value') {
        $next = $elements[$i + 1]
        if ($next -is [System.Management.Automation.Language.StringConstantExpressionAst]) { $got = $next.Value }
    }
}
if ($null -eq $got) { Fail 'no string constant after -Value' }
if (-not ($got -ceq $value)) { Fail "value changed: got <$got> want <$value>" }
if ($lines[0] -notmatch [regex]::Escape("-LiteralPath 'HKLM:\SOFTWARE\io.github.roberdorfer.lsxiivolumefixer'") -or $lines[0] -notmatch '-Type String') {
    Fail "speakerHost not written as REG_SZ to the machine-wide key: $($lines[0])"
}

# Admin script complete (≙ rm -rf '/Applications/LSXIIVolumeFixer.app'): the copy to the fixed path, Run value, installId.
$exe = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'LSXIIVolumeFixer\LSXIIVolumeFixer.exe'
if (-not ($script | Where-Object { $_ -match '^\s*Copy-Item ' -and $_.Contains("-Destination '$exe'") })) { Fail "admin script incomplete: no copy to $exe" }
if (-not ($script | Where-Object { $_.Contains("Set-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name 'io.github.roberdorfer.lsxiivolumefixer' -Value '`"$exe`"'") })) { Fail 'admin script incomplete: no Run value' }
if (-not ($script | Where-Object { $_ -match "-Name 'installId' -Value \(\[guid\]::NewGuid\(\)" })) { Fail 'admin script incomplete: no new installId' }
# The copied exe is checked against the hash of the staged one (§10.3: %TEMP% is user-writable during the UAC prompt).
if (-not ($script | Where-Object { $_ -match "Get-FileHash -LiteralPath \`$exe -Algorithm SHA256\)\.Hash -ne '[0-9A-F]{64}'" })) { Fail 'admin script incomplete: no SHA-256 check of the copied exe' }
# Its log goes back through a pipe it creates, never into a file in the user-writable staging directory (§10.3).
if ($script | Where-Object { $_ -match 'Add-Content|Out-File|admin\.log' }) { Fail 'admin script writes a file (log belongs in the pipe)' }
if (-not ($script | Where-Object { $_ -match 'NamedPipeServerStream\(''kefproxy\.[0-9a-f]{32}''' })) { Fail 'admin script incomplete: no log pipe' }

# State: Users (S-1-5-32-545) get exactly SetValue, without inheritance.
$rule = @($script | Where-Object { $_ -match 'RegistryAccessRule' })
if ($rule.Count -ne 1) { Fail "want one RegistryAccessRule line, got $($rule.Count)" }
foreach ($want in "SecurityIdentifier('S-1-5-32-545')", '[Security.AccessControl.RegistryRights]::SetValue,', '[Security.AccessControl.InheritanceFlags]::None,', '[Security.AccessControl.PropagationFlags]::None,', '[Security.AccessControl.AccessControlType]::Allow)') {
    if (-not $rule[0].Contains($want)) { Fail "State rule: missing $want" }
}
if (-not ($script | Where-Object { $_.Contains("OpenSubKey('SOFTWARE\io.github.roberdorfer.lsxiivolumefixer\State'") })) { Fail 'State rule is not applied to State' }

# Never New-Item -Force (on a registry key it deletes the key with installId, State and all values; 10.5).
if ($script | Where-Object { $_ -notmatch '^\s*#' -and $_ -match 'New-Item' -and $_ -match '-Force' }) { Fail 'New-Item -Force in the admin script' }

# This run's staging directory (quoted in the admin script) must be gone; other runs' directories are not ours.
$m = [regex]::Match(($script -join "`n"), "'([^']*\\kefproxy\.[A-Za-z0-9]{6})\\")
if (-not $m.Success) { Fail 'staging directory not found in the admin script' }
if (Test-Path -LiteralPath $m.Groups[1].Value) { Fail "staging directory left behind: $($m.Groups[1].Value)" }

Write-Output 'install_dry_run: passed'
exit 0
