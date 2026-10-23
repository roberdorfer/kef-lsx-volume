# Remove the "LSX II Volume Fixer" tray app installed by install.ps1 (≙ scripts/uninstall.sh; docs/DESIGN.md 10.7, 10.8).
# Deletes the autostart (Run value and its StartupApproved entry), the machine-wide key with State and installId, and
# C:\Program Files\LSXIIVolumeFixer (one UAC prompt). Running instances in every session see the missing installId and
# end the orderly way (tray icon removed); what is left after 10 s is stopped. No audio restart is needed (no driver).
#
# Usage: scripts\uninstall.cmd [--purge] [--dry-run]
#   --purge also deletes the current user's own settings (HKCU) and log (other users keep theirs).
#   --dry-run prints what would run instead of running it (nothing is removed).
#
# Windows PowerShell 5.1 rules (docs/DESIGN.md 10.5): no && or ||, no ternary operator, no ??; saved with a BOM.
# No param() block: $args is read by hand, like the zsh loop. Does not build.

$ErrorActionPreference = 'Stop'

$purge = $false
$dryRun = $false
foreach ($a in $args) {
    $a = [string]$a
    if ($a -ceq '--purge') { $purge = $true }
    elseif ($a -ceq '--dry-run') { $dryRun = $true }
    else { [Console]::Error.WriteLine("unknown argument: $a"); exit 2 }
}

$Suite = 'io.github.roberdorfer.lsxiivolumefixer'
$AppName = 'LSXIIVolumeFixer'
$AppDir = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) $AppName
$MachineKey = "HKLM:\SOFTWARE\$Suite"
$StateKey = "$MachineKey\State"
$RunKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
$ApprovedKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
$UserKey = "HKCU:\Software\$Suite"
$UserLogDir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) $AppName
$UserName = [Environment]::UserName

# Window started by double-click: keep it open at the end (see install.ps1). Never for --dry-run.
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

function Q([string]$s) { "'" + [System.Management.Automation.Language.CodeGeneration]::EscapeSingleQuotedStringContent($s) + "'" }

# Hash-checked start of admin.ps1 from the user-writable %TEMP% (same as install.ps1, see there): the elevated
# PowerShell runs only the bytes whose SHA-256 was taken here, or exits 3.
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

# Addition: 64-bit, like install.ps1 (a 32-bit PowerShell would look into the WOW64 redirections of HKLM and Program Files).
if (-not [Environment]::Is64BitProcess) {
    [Console]::Error.WriteLine('uninstall.ps1 needs the 64-bit PowerShell (C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe)')
    Exit-Script 1
}

# ≙ mktemp -d /tmp/kefproxy.XXXXXX, removed in finally (≙ trap EXIT).
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
function Invoke-Uninstall {
    $pipeName = 'kefproxy.' + [guid]::NewGuid().ToString('N')
    $s = New-Object System.Text.StringBuilder
    [void]$s.AppendLine('# LSX II Volume Fixer: uninstall (generated by scripts\uninstall.ps1; docs/DESIGN.md 10.7)')
    [void]$s.AppendLine("`$ErrorActionPreference = 'Stop'")
    [void]$s.AppendLine('$lines = New-Object System.Collections.Generic.List[string]')
    [void]$s.AppendLine('function Say([string]$line) { $lines.Add($line) }')
    [void]$s.AppendLine('try {')
    [void]$s.AppendLine("    if (-not [Environment]::Is64BitProcess) { throw 'needs the 64-bit PowerShell' }")
    [void]$s.AppendLine('    # 1. Autostart, with its StartupApproved entry (Addition: a "disabled" there would survive a reinstall).')
    [void]$s.AppendLine("    Remove-ItemProperty -LiteralPath $(Q $RunKey) -Name $(Q $Suite) -ErrorAction SilentlyContinue")
    [void]$s.AppendLine("    if (Test-Path -LiteralPath $(Q $ApprovedKey)) { Remove-ItemProperty -LiteralPath $(Q $ApprovedKey) -Name $(Q $Suite) -ErrorAction SilentlyContinue }")
    [void]$s.AppendLine('    # 2. Machine-wide key: State first and not recursively (users can set values there but create no subkeys; if one')
    [void]$s.AppendLine('    #    is there anyway, stop), then the key itself with installId. Every instance sees installId gone and ends.')
    [void]$s.AppendLine("    if (Test-Path -LiteralPath $(Q $StateKey)) {")
    [void]$s.AppendLine("        `$n = (Get-Item -LiteralPath $(Q $StateKey)).SubKeyCount")
    [void]$s.AppendLine("        if (`$n -gt 0) { throw ('State has ' + `$n + ' subkey(s); not deleted') }")
    [void]$s.AppendLine("        Remove-Item -LiteralPath $(Q $StateKey)")
    [void]$s.AppendLine('    }')
    [void]$s.AppendLine("    if (Test-Path -LiteralPath $(Q $MachineKey)) { Remove-Item -LiteralPath $(Q $MachineKey) -Recurse }")
    [void]$s.AppendLine('    # 3. Wait at most 10 s for the instances to end, then stop what is left.')
    [void]$s.AppendLine('    $deadline = (Get-Date).AddSeconds(10)')
    [void]$s.AppendLine("    while (@(Get-Process -Name $(Q $AppName) -ErrorAction SilentlyContinue).Count -gt 0 -and (Get-Date) -lt `$deadline) { Start-Sleep -Milliseconds 250 }")
    [void]$s.AppendLine("    `$left = @(Get-Process -Name $(Q $AppName) -ErrorAction SilentlyContinue)")
    [void]$s.AppendLine('    foreach ($p in $left) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; Say (''stopped instance '' + $p.Id + '' in session '' + $p.SessionId) }')
    [void]$s.AppendLine("    if (`$left.Count -gt 0) { `$deadline = (Get-Date).AddSeconds(5); while (@(Get-Process -Name $(Q $AppName) -ErrorAction SilentlyContinue).Count -gt 0 -and (Get-Date) -lt `$deadline) { Start-Sleep -Milliseconds 250 } }")
    [void]$s.AppendLine('    # 4. The app.')
    [void]$s.AppendLine("    if (Test-Path -LiteralPath $(Q $AppDir)) { Remove-Item -LiteralPath $(Q $AppDir) -Recurse -Force }")
    [void]$s.AppendLine('    exit 0')
    [void]$s.AppendLine('} catch {')
    [void]$s.AppendLine('    Say (''failed: '' + $_.Exception.Message)')
    [void]$s.AppendLine('    exit 1')
    Add-AdminLogPipe $s $pipeName

    $adminPs1 = Join-Path $stage 'admin.ps1'
    [IO.File]::WriteAllText($adminPs1, $s.ToString(), (New-Object System.Text.UTF8Encoding($true)))

    if ($dryRun) {
        Write-Output 'dry run; admin script that would run with administrator privileges:'
        Get-Content -LiteralPath $adminPs1 | Write-Output
        if ($purge) {
            Write-Output "then, as ${UserName}: Remove-Item $(Q $UserKey) -Recurse; Remove-Item $(Q $UserLogDir) -Recurse"
        } else {
            Write-Output "then, as ${UserName}: Remove-ItemProperty $(Q $UserKey) -Name defaultOutputSetFor"
        }
        $script:code = 0
        return
    }

    Write-Output 'Uninstall KEF LSX II'
    $ps = Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\powershell.exe'
    $boot = New-ElevatedBootstrap $adminPs1
    $p = $null
    try {
        $p = Start-Process -FilePath $ps -Verb RunAs -WindowStyle Hidden -PassThru -ArgumentList '-NoProfile', '-EncodedCommand', $boot
    } catch {
        $p = $null
    }
    if ($null -eq $p) {
        [Console]::Error.WriteLine('cancelled: administrator rights were not granted (nothing removed)')
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
    Write-Output 'app and agent removed'

    if ($purge) {
        if (Test-Path -LiteralPath $UserKey) { Remove-Item -LiteralPath $UserKey -Recurse -ErrorAction SilentlyContinue }
        if (Test-Path -LiteralPath $UserLogDir) { Remove-Item -LiteralPath $UserLogDir -Recurse -Force -ErrorAction SilentlyContinue }
        Write-Output "settings and log of $UserName removed"
    } else {
        # Without this marker a later reinstall would not make the LSX II the default output again.
        if (Test-Path -LiteralPath $UserKey) { Remove-ItemProperty -LiteralPath $UserKey -Name 'defaultOutputSetFor' -ErrorAction SilentlyContinue }
    }
    Write-Output 'Windows keeps the tray-icon visibility setting per user (Settings > Personalization > Taskbar > Other system tray icons); it does nothing once the app is gone.'
    $script:code = 0
}
try {
    Invoke-Uninstall
} finally {
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
}
Exit-Script $script:code
