# One-line installer (release asset, the counterpart of scripts/install-macos.sh): downloads the prebuilt app of the
# latest release and runs its scripts\install.cmd, so no .NET SDK is needed. It brings in the .NET 10 Desktop Runtime
# with winget when that is missing (the exe is framework-dependent, so Microsoft Update keeps the runtime patched).
# A file fetched with Invoke-WebRequest carries no Mark of the Web, so SmartScreen treats the exe like one built here;
# Smart App Control still has to be off (README).
#
#   irm https://github.com/roberdorfer/kef-lsx-volume/releases/latest/download/install-windows.ps1 | iex
#
# scripts/release.sh publishes this file a second time as uninstall-windows.ps1 with $Action = 'uninstall'.
# Run through iex: plain ASCII without BOM, no exit (it would close the caller's window), Windows PowerShell 5.1 rules.
$Action = 'install'

& {
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'   # the 5.1 progress bar slows Invoke-WebRequest down a lot

    # Native architecture, also from an emulated x64 PowerShell on ARM64 (Get-Rid in make.ps1).
    $native = 0
    try {
        if (-not ('LsxGet.Native' -as [type])) {
            Add-Type -Namespace LsxGet -Name Native -MemberDefinition '[DllImport("kernel32.dll")] public static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);'
        }
        $pm = [uint16]0
        $nm = [uint16]0
        if ([LsxGet.Native]::IsWow64Process2([IntPtr]-1, [ref]$pm, [ref]$nm)) { $native = [int]$nm }
    } catch { }
    $arch = 'x64'
    if ($native -eq 0xAA64) { $arch = 'arm64' }

    if ($Action -eq 'install') {
        # install.ps1 refuses to run elevated (the app would start with administrator rights); say so before downloading.
        $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
        if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            throw 'Run this in a normal PowerShell window, not as administrator: it asks for administrator rights itself (one UAC prompt).'
        }
        $runtimes = Join-Path $env:ProgramFiles 'dotnet\shared\Microsoft.WindowsDesktop.App'
        if (-not (Get-ChildItem -LiteralPath $runtimes -Filter '10.*' -ErrorAction SilentlyContinue)) {
            if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
                throw 'The .NET 10 Desktop Runtime is missing and winget is not available: install the runtime from https://dotnet.microsoft.com/download/dotnet/10.0 and run this again.'
            }
            Write-Host 'Installing the .NET 10 Desktop Runtime (winget)'
            # --source winget: without it winget also searches the Microsoft Store source, which can ask for its own terms.
            winget install --exact --id Microsoft.DotNet.DesktopRuntime.10 --source winget --architecture $arch
            if ($LASTEXITCODE -ne 0) { throw "winget failed (exit code $LASTEXITCODE)" }
        }
    }

    $url = "https://github.com/roberdorfer/kef-lsx-volume/releases/latest/download/kef-lsx-volume-windows-$arch.zip"
    $dir = Join-Path ([IO.Path]::GetTempPath()) ('lsx-ii-volume-fixer.' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $dir | Out-Null
    try {
        Write-Host "Downloading $url"
        $zip = Join-Path $dir 'release.zip'
        Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $zip
        Expand-Archive -LiteralPath $zip -DestinationPath $dir
        & (Join-Path $dir "scripts\$Action.cmd")
        if ($LASTEXITCODE -ne 0) { Write-Host "$Action failed (exit code $LASTEXITCODE)" }
    } finally {
        Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
