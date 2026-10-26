# Development setup (docs/DESIGN.md 16 M3): exactly the lines that the admin part of install.ps1 will write for the
# machine-wide keys (10.2, step 2 and 3), without copying the exe to Program Files and without the Run value. The
# agent runs from build\ until M6; uninstall.ps1 will remove these keys later.
#   - HKLM\SOFTWARE\io.github.roberdorfer.lsxiivolumefixer and its subkey State, each only if missing (never
#     New-Item -Force, 10.5), so existing values survive;
#   - State: Users (S-1-5-32-545) get SetValue only, in addition to the inherited read, without inheritance (5.14);
#     set idempotently on every run;
#   - installId: a new GUID (10.4).
# Run elevated, once:
#   Start-Process powershell.exe -Verb RunAs -Wait -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File',"<this file>","<log file>"
# The optional first argument is a log file that receives the output (the elevated window closes at the end).

$ErrorActionPreference = 'Stop'
$logFile = $null
if ($args.Count -ge 1) { $logFile = [string]$args[0] }

function Say([string]$line) {
    Write-Host $line
    if ($logFile) { Add-Content -LiteralPath $logFile -Value $line -Encoding UTF8 }
}

try {
    if (-not [Environment]::Is64BitProcess) { throw 'needs 64-bit PowerShell (HKLM would be redirected to WOW6432Node)' }
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not ([Security.Principal.WindowsPrincipal]$id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'needs administrator privileges'
    }

    $suite = 'io.github.roberdorfer.lsxiivolumefixer'
    $machine = "HKLM:\SOFTWARE\$suite"
    $state = "$machine\State"
    if (-not (Test-Path -LiteralPath $machine)) { New-Item -Path 'HKLM:\SOFTWARE' -Name $suite | Out-Null; Say "created $machine" }
    else { Say "exists  $machine" }
    if (-not (Test-Path -LiteralPath $state)) { New-Item -Path $machine -Name 'State' | Out-Null; Say "created $state" }
    else { Say "exists  $state" }

    # Users: SetValue only, no inheritance (groups always by SID, the names are localized).
    $users = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-545')
    $rule = New-Object Security.AccessControl.RegistryAccessRule($users, [Security.AccessControl.RegistryRights]::SetValue,
        [Security.AccessControl.InheritanceFlags]::None, [Security.AccessControl.PropagationFlags]::None,
        [Security.AccessControl.AccessControlType]::Allow)
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
    $key = $base.OpenSubKey("SOFTWARE\$suite\State", [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree,
        [Security.AccessControl.RegistryRights]'ReadKey, ChangePermissions')
    try {
        $acl = $key.GetAccessControl()
        $acl.SetAccessRule($rule)
        $key.SetAccessControl($acl)
    } finally { $key.Close(); $base.Close() }
    Say 'State: Users (S-1-5-32-545) SetValue, no inheritance'

    $installId = [guid]::NewGuid().ToString()
    Set-ItemProperty -LiteralPath $machine -Name 'installId' -Value $installId -Type String
    Say "installId = $installId"
    Say 'dev setup: ok'
    exit 0
} catch {
    Say ("dev setup: failed: " + $_.Exception.Message + ' ' + $_.InvocationInfo.PositionMessage)
    # Diagnostics: the token of this process (integrity level, enabled groups).
    try { (whoami /groups) | Where-Object { $_ -match 'S-1-16-|S-1-5-32-544' } | ForEach-Object { Say ('token: ' + $_.Trim()) } } catch { }
    try { Say ('reg.exe query: ' + ((reg.exe query 'HKLM\SOFTWARE' /ve 2>&1) -join ' ')) } catch { }
    exit 1
}
