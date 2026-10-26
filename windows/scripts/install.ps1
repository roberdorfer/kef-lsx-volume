# Install the "LSX II Volume Fixer" tray app for ALL Windows users (≙ scripts/install.sh; docs/DESIGN.md 10.2-10.4).
# App -> C:\Program Files\LSXIIVolumeFixer\LSXIIVolumeFixer.exe, autostart -> HKLM Run value, machine-wide keys ->
# HKLM\SOFTWARE\io.github.roberdorfer.lsxiivolumefixer (administrator rights via one UAC prompt). Every user's sign-in
# then starts its own instance, which only acts while that user owns the console.
#
# Usage: scripts\install.cmd [driver|helper|all] [--pref key=value ...] [--dry-run]
#   --pref writes a system-wide default (HKLM\SOFTWARE\io.github.roberdorfer.lsxiivolumefixer) that applies to every
#   user who has not overridden the key in their own settings (HKCU).
#   --dry-run prints the admin script instead of running it (nothing is installed).
#   driver is reserved for a later version (docs/DESIGN.md 13); all installs everything this version has (the helper).
#
# Windows PowerShell 5.1 rules (docs/DESIGN.md 10.5): no && or ||, no ternary operator, no ??; saved with a BOM; groups by
# SID; never New-Item -Force on a registry key (it deletes an existing key with all its values and subkeys).
# No param() block: $args is read by hand, like the zsh loop.

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot

$what = 'all'
$prefs = @()
$dryRun = $false
for ($i = 0; $i -lt $args.Count; $i++) {
    $a = [string]$args[$i]
    if ($a -ceq 'driver' -or $a -ceq 'helper' -or $a -ceq 'all') { $what = $a }
    elseif ($a -ceq '--pref') {
        if ($i + 1 -ge $args.Count) { [Console]::Error.WriteLine('--pref: missing key=value'); exit 2 }
        $i++
        $prefs += [string]$args[$i]
    }
    elseif ($a -ceq '--dry-run') { $dryRun = $true }
    else { [Console]::Error.WriteLine("unknown argument: $a"); exit 2 }
}
# A53: this version has no driver.
if ($what -ceq 'driver') { [Console]::Error.WriteLine('driver: not part of this version'); exit 2 }

$Suite = 'io.github.roberdorfer.lsxiivolumefixer'
$AppName = 'LSXIIVolumeFixer'
$AppDir = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) $AppName
$AppExe = Join-Path $AppDir "$AppName.exe"
$MachineKey = "HKLM:\SOFTWARE\$Suite"
$StateKey = "$MachineKey\State"
$RunKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'

# Window started by double-click (cmd.exe and this PowerShell are the only processes on the console): keep it open at
# the end so the result can be read. Never for --dry-run (make.cmd test runs it) or with redirected input/output.
function Test-OwnConsole {
    try {
        if ([Console]::IsInputRedirected -or [Console]::IsOutputRedirected) { return $false }
        if (-not ('LsxSetup.ConsoleList' -as [type])) {
            Add-Type -Namespace LsxSetup -Name ConsoleList -MemberDefinition '[DllImport("kernel32.dll")] public static extern uint GetConsoleProcessList(uint[] list, uint count);'
        }
        $list = New-Object 'uint32[]' 16
        $n = [LsxSetup.ConsoleList]::GetConsoleProcessList($list, 16)
        return ($n -ge 1 -and $n -le 2)
    } catch { return $false }
}
function Exit-Script([int]$code) {
    if (-not $dryRun -and (Test-OwnConsole)) {
        Write-Host ''
        Write-Host 'Press Enter to close this window.'
        [void][Console]::ReadLine()
    }
    exit $code
}

# Single-quoted PowerShell literal for the admin script. Measured: replacing ' by '' alone fails on typographic quotes
# (U+2019 and its kin are quote characters for PowerShell too); the language's own escaper handles all of them.
function Q([string]$s) { "'" + [System.Management.Automation.Language.CodeGeneration]::EscapeSingleQuotedStringContent($s) + "'" }

# The staging folder is in %TEMP%, which the user (and anything running as the user) can write while the UAC prompt
# is open. So the elevated PowerShell never runs admin.ps1 by path: it gets a fixed bootstrap as -EncodedCommand that
# reads the file once, checks its SHA-256 against the hash taken here and runs exactly those verified bytes from
# memory. A file changed in between exits 3 without running anything.
function New-ElevatedBootstrap([string]$path) {
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    $boot = "`$ErrorActionPreference = 'Stop'`n" +
        "`$b = [IO.File]::ReadAllBytes($(Q $path))`n" +
        "`$h = -join ([Security.Cryptography.SHA256]::Create().ComputeHash(`$b) | ForEach-Object { `$_.ToString('X2') })`n" +
        "if (`$h -ne '$hash') { exit 3 }`n" +
        "& ([scriptblock]::Create((New-Object Text.UTF8Encoding(`$false)).GetString(`$b).TrimStart([char]0xFEFF)))`n" +
        "exit `$LASTEXITCODE"
    [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($boot))
}

# The admin part's log comes back through a named pipe that the elevated process itself creates, not through a file:
# the elevated side writing into the user-writable %TEMP% could be redirected by a junction to any folder (§10.3).
# The elevated side only writes, never reads; whoever connects first gets the lines, which are plain status text.
function Add-AdminLogPipe([System.Text.StringBuilder]$s, [string]$pipeName) {
    [void]$s.AppendLine('} finally {')
    [void]$s.AppendLine('    try {')
    [void]$s.AppendLine("        `$pipe = New-Object IO.Pipes.NamedPipeServerStream($(Q $pipeName), [IO.Pipes.PipeDirection]::Out, 1)")
    [void]$s.AppendLine('        try {')
    [void]$s.AppendLine('            $wait = $pipe.BeginWaitForConnection($null, $null)')
    [void]$s.AppendLine('            if ($wait.AsyncWaitHandle.WaitOne(10000)) {')
    [void]$s.AppendLine('                $pipe.EndWaitForConnection($wait)')
    [void]$s.AppendLine('                $w = New-Object IO.StreamWriter($pipe, (New-Object Text.UTF8Encoding($false)))')
    [void]$s.AppendLine('                foreach ($line in $lines) { $w.WriteLine($line) }')
    [void]$s.AppendLine('                $w.Flush()')
    [void]$s.AppendLine('                $pipe.WaitForPipeDrain()')
    [void]$s.AppendLine('            }')
    [void]$s.AppendLine('        } finally { $pipe.Dispose() }')
    [void]$s.AppendLine('    } catch { }')
    [void]$s.AppendLine('}')
}

# Waits for the elevated process and prints its log (see Add-AdminLogPipe); without a pipe (cancelled, exit 3) nothing.
function Receive-AdminLog([System.Diagnostics.Process]$p, [string]$pipeName) {
    while (-not $p.HasExited) {
        $c = New-Object IO.Pipes.NamedPipeClientStream('.', $pipeName, [IO.Pipes.PipeDirection]::In)
        try { $c.Connect(250) } catch { $c.Dispose(); continue }
        try {
            $r = New-Object IO.StreamReader($c, (New-Object Text.UTF8Encoding($false)))
            while ($null -ne ($line = $r.ReadLine())) { Write-Output $line }
        } finally { $c.Dispose() }
        break
    }
    $p.WaitForExit()
}

# Checks (Addition, A64). 64-bit always (also for --dry-run, whose paths would be wrong otherwise): a 32-bit PowerShell
# would land in the WOW64 redirections of Program Files and HKLM.
if (-not [Environment]::Is64BitProcess) {
    [Console]::Error.WriteLine('install.ps1 needs the 64-bit PowerShell (C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe); a 32-bit one would install into the WOW64 redirections')
    Exit-Script 1
}
# Not elevated (only without --dry-run): the app started at the end would otherwise run with the administrator token.
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $dryRun -and ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    [Console]::Error.WriteLine('install.ps1 must not run elevated: the app would start with administrator rights. Start scripts\install.cmd from a normal window or by double-click; it asks for administrator rights itself (one UAC prompt).')
    Exit-Script 1
}

# Processes that count as "the new version" in step 8 started after this moment.
$begin = Get-Date

# ≙ make -s all: always builds, also for --dry-run.
$global:LASTEXITCODE = 0
& (Join-Path $Root 'make.ps1') -s all
if ($LASTEXITCODE -ne 0) { Exit-Script 1 }

# ≙ mktemp -d /tmp/kefproxy.XXXXXX, removed in finally (≙ trap EXIT), also when the UAC prompt is cancelled or a step
# fails.
$chars = 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789'
$rnd = New-Object System.Random
$stage = $null
for ($t = 0; $t -lt 100 -and -not $stage; $t++) {
    $name = 'kefproxy.'
    for ($c = 0; $c -lt 6; $c++) { $name += $chars[$rnd.Next($chars.Length)] }
    $candidate = Join-Path ([IO.Path]::GetTempPath()) $name
    if (-not (Test-Path -LiteralPath $candidate)) {
        New-Item -ItemType Directory -Path $candidate | Out-Null
        $stage = $candidate
    }
}
if (-not $stage) { [Console]::Error.WriteLine('could not create a staging directory in %TEMP%'); Exit-Script 1 }

$script:code = 0
function Invoke-Install {
    Copy-Item -LiteralPath (Join-Path $Root "build\$AppName.exe") -Destination $stage
    $stageExe = Join-Path $stage "$AppName.exe"
    # Checked again on the copy in Program Files (admin-only), so a swapped staged exe is never left installed.
    $exeHash = (Get-FileHash -LiteralPath $stageExe -Algorithm SHA256).Hash
    $pipeName = 'kefproxy.' + [guid]::NewGuid().ToString('N')
    $prompt = 'Install KEF LSX II'

    # The admin script (≙ ADMIN in install.sh): run once with administrator rights, from a file, never with values on
    # the command line (≙ "run from a file, not interpolated into the AppleScript string literal").
    $s = New-Object System.Text.StringBuilder
    [void]$s.AppendLine('# LSX II Volume Fixer: install for all users (generated by scripts\install.ps1; docs/DESIGN.md 10.2)')
    [void]$s.AppendLine("`$ErrorActionPreference = 'Stop'")
    [void]$s.AppendLine('$lines = New-Object System.Collections.Generic.List[string]')
    [void]$s.AppendLine('function Say([string]$line) { $lines.Add($line) }')
    [void]$s.AppendLine("`$exe = $(Q $AppExe)")
    [void]$s.AppendLine("`$old = $(Q ($AppExe + '.old'))")
    [void]$s.AppendLine('$renamed = $false')
    [void]$s.AppendLine('try {')
    [void]$s.AppendLine("    if (-not [Environment]::Is64BitProcess) { throw 'needs the 64-bit PowerShell' }")
    if ($what -ceq 'helper' -or $what -ceq 'all') {
        $prompt += ' ' + [char]0x2013 + ' LSX II Volume Fixer (tray) for all users'
        [void]$s.AppendLine('    # App. A running exe cannot be overwritten but can be renamed; the old one goes once all instances ended.')
        [void]$s.AppendLine("    if (-not (Test-Path -LiteralPath $(Q $AppDir))) { New-Item -ItemType Directory -Path $(Q $AppDir) | Out-Null }")
        [void]$s.AppendLine('    # Old exes left by an earlier update whose old instance was still running (best effort).')
        [void]$s.AppendLine("    Get-ChildItem -LiteralPath $(Q $AppDir) -Filter '$AppName.exe*.old' | ForEach-Object { try { Remove-Item -LiteralPath `$_.FullName -Force } catch { } }")
        [void]$s.AppendLine('    if (Test-Path -LiteralPath $old) { $old = $exe + ''.'' + [guid]::NewGuid().ToString(''N'') + ''.old'' }')
        [void]$s.AppendLine('    if (Test-Path -LiteralPath $exe) { [IO.File]::Move($exe, $old); $renamed = $true }')
        [void]$s.AppendLine("    Copy-Item -LiteralPath $(Q $stageExe) -Destination $(Q $AppExe)")
        [void]$s.AppendLine("    if ((Get-FileHash -LiteralPath `$exe -Algorithm SHA256).Hash -ne '$exeHash') { Remove-Item -LiteralPath `$exe -Force; throw 'the staged exe changed after it was built (SHA-256 mismatch)' }")
        [void]$s.AppendLine("    Say ('app: ' + `$exe)")
        [void]$s.AppendLine('    # Machine-wide key and State, each only when missing (never New-Item -Force): --pref values, "apply for all')
        [void]$s.AppendLine('    # users", State\volume and muteResetBoot survive every reinstall.')
        [void]$s.AppendLine("    if (-not (Test-Path -LiteralPath $(Q $MachineKey))) { New-Item -Path 'HKLM:\SOFTWARE' -Name $(Q $Suite) | Out-Null; Say $(Q ('created ' + $MachineKey)) }")
        [void]$s.AppendLine("    if (-not (Test-Path -LiteralPath $(Q $StateKey))) { New-Item -Path $(Q $MachineKey) -Name 'State' | Out-Null; Say $(Q ('created ' + $StateKey)) }")
        [void]$s.AppendLine('    # State: Users (S-1-5-32-545) may set values (inherited read plus SetValue, no inheritance); set on every run.')
        [void]$s.AppendLine("    `$rule = New-Object Security.AccessControl.RegistryAccessRule((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-545')), [Security.AccessControl.RegistryRights]::SetValue, [Security.AccessControl.InheritanceFlags]::None, [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow)")
        [void]$s.AppendLine('    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)')
        [void]$s.AppendLine("    `$key = `$base.OpenSubKey($(Q ("SOFTWARE\$Suite\State")), [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree, [Security.AccessControl.RegistryRights]'ReadKey, ChangePermissions')")
        [void]$s.AppendLine('    try { $acl = $key.GetAccessControl(); $acl.SetAccessRule($rule); $key.SetAccessControl($acl) } finally { $key.Close(); $base.Close() }')
        [void]$s.AppendLine("    Set-ItemProperty -LiteralPath $(Q $RunKey) -Name $(Q $Suite) -Value $(Q ('"' + $AppExe + '"')) -Type String")
        [void]$s.AppendLine("    Say $(Q ('autostart: ' + $RunKey.Replace('HKLM:', 'HKLM') + '\' + $Suite))")
        [void]$s.AppendLine('    # Other sessions (10.4): a new installId makes every running agent hand over to the new exe (its launcher starts')
        [void]$s.AppendLine('    # the fixed path with --relaunched). Instances that have not ended after 15 s are stopped.')
        [void]$s.AppendLine('    $t0 = Get-Date')
        [void]$s.AppendLine("    `$before = @(Get-Process -Name $(Q $AppName) -ErrorAction SilentlyContinue | ForEach-Object { `$_.Id })")
        [void]$s.AppendLine("    Set-ItemProperty -LiteralPath $(Q $MachineKey) -Name 'installId' -Value ([guid]::NewGuid().ToString()) -Type String")
        [void]$s.AppendLine("    function Get-OldInstances { @(Get-Process -Name $(Q $AppName) -ErrorAction SilentlyContinue | Where-Object { if (`$before -notcontains `$_.Id) { return `$false }; `$st = `$null; try { `$st = `$_.StartTime } catch { }; (`$null -eq `$st) -or (`$st -lt `$t0) }) }")
        [void]$s.AppendLine('    $deadline = (Get-Date).AddSeconds(15)')
        [void]$s.AppendLine('    $left = @(Get-OldInstances)')
        [void]$s.AppendLine('    while ($left.Count -gt 0 -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250; $left = @(Get-OldInstances) }')
        [void]$s.AppendLine('    foreach ($p in $left) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; Say (''stopped instance '' + $p.Id + '' in session '' + $p.SessionId + '' (no handover within 15 s)'') }')
        [void]$s.AppendLine('    if ($left.Count -gt 0) { Start-Sleep -Seconds 1 }')
        [void]$s.AppendLine('    if ($renamed) { try { Remove-Item -LiteralPath $old -Force } catch { Say (''could not remove '' + $old + '' (still in use); the next install or uninstall removes it'') } }')
    }
    foreach ($kv in $prefs) {
        # ≙ ${kv%%=*} and ${kv#*=}: up to the first "=" and after it (without "=" both are the whole argument).
        $eq = $kv.IndexOf('=')
        if ($eq -ge 0) { $k = $kv.Substring(0, $eq); $v = $kv.Substring($eq + 1) } else { $k = $kv; $v = $kv }
        # REG_SZ, like `defaults write` without a type: the app reads numbers from strings too, bools not (8.2, R13).
        [void]$s.AppendLine("    Set-ItemProperty -LiteralPath $(Q $MachineKey) -Name $(Q $k) -Value $(Q $v) -Type String")
    }
    [void]$s.AppendLine('    exit 0')
    [void]$s.AppendLine('} catch {')
    [void]$s.AppendLine('    Say (''failed: '' + $_.Exception.Message)')
    [void]$s.AppendLine('    if ($renamed -and -not (Test-Path -LiteralPath $exe) -and (Test-Path -LiteralPath $old)) { try { [IO.File]::Move($old, $exe); Say ''previous exe restored'' } catch { } }')
    [void]$s.AppendLine('    exit 1')
    Add-AdminLogPipe $s $pipeName

    # UTF-8 with BOM: Windows PowerShell 5.1 reads a file without one as the ANSI code page (ä → Ã¤).
    $adminPs1 = Join-Path $stage 'admin.ps1'
    [IO.File]::WriteAllText($adminPs1, $s.ToString(), (New-Object System.Text.UTF8Encoding($true)))

    if ($dryRun) {
        Write-Output 'dry run; admin script that would run with administrator privileges:'
        # Read back the way powershell.exe -File reads it (no -Encoding: ANSI unless there is a BOM), so the output
        # shows exactly what the elevated PowerShell would run.
        Get-Content -LiteralPath $adminPs1 | Write-Output
        $script:code = 0
        return
    }

    # 10.3: one elevation, hidden window; values only through the file, run through the hash-checked bootstrap.
    Write-Output $prompt
    $ps = Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\powershell.exe'
    $boot = New-ElevatedBootstrap $adminPs1
    $p = $null
    try {
        $p = Start-Process -FilePath $ps -Verb RunAs -WindowStyle Hidden -PassThru -ArgumentList '-NoProfile', '-EncodedCommand', $boot
    } catch {
        $p = $null
    }
    if ($null -eq $p) {
        [Console]::Error.WriteLine('cancelled: administrator rights were not granted (nothing installed)')
        $script:code = 1
        return
    }
    $null = $p.Handle   # PowerShell 5.1: without an open handle ExitCode stays empty
    Receive-AdminLog $p $pipeName
    if ($p.ExitCode -eq 3) {
        [Console]::Error.WriteLine('admin script changed after it was written (SHA-256 mismatch); nothing was run')
        $script:code = 1
        return
    }
    if ($p.ExitCode -ne 0) {
        [Console]::Error.WriteLine("admin script failed ($($p.ExitCode))")
        $script:code = 1
        return
    }

    if ($what -ceq 'helper' -or $what -ceq 'all') {
        # Step 8 (≙ bootstrap after the prompt): an instance of the fixed path in this session, started after the
        # install began. First up to 5 s for the handover (10.4); then start it through explorer.exe, detached from this
        # console (O23), up to 5 attempts. A second start is harmless: the mutex leaves one launcher (10.9).
        $session = (Get-Process -Id $PID).SessionId
        $findNew = {
            @(Get-Process -Name $AppName -ErrorAction SilentlyContinue | Where-Object {
                $ok = $false
                try { $ok = ($_.SessionId -eq $session) -and ($_.StartTime -ge $begin) -and ([string]::Equals($_.Path, $AppExe, [StringComparison]::OrdinalIgnoreCase)) } catch { }
                $ok
            })
        }
        $found = & $findNew
        for ($w = 0; $w -lt 5 -and $found.Count -eq 0; $w++) { Start-Sleep -Seconds 1; $found = & $findNew }
        $explorer = Join-Path $env:SystemRoot 'explorer.exe'
        for ($try = 0; $try -lt 5 -and $found.Count -eq 0; $try++) {
            try { Start-Process -FilePath $explorer -ArgumentList "`"$AppExe`"" } catch { }
            Start-Sleep -Seconds 1
            $found = & $findNew
        }
        if ($found.Count -eq 0) {
            [Console]::Error.WriteLine('helper agent failed to load')
            $script:code = 1
            return
        }
        Write-Output 'LSX II Volume Fixer agent loaded (global, all users)'
    }
    $script:code = 0
}
try {
    Invoke-Install
} finally {
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
}
Exit-Script $script:code
