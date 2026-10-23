# KEF LSX II volume control for Windows: LSX II Volume Fixer tray app (equivalent of the Makefile;
# docs/DESIGN.md 10.5). Started through make.cmd, which bypasses the execution policy for this process only.
#
# Usage: make.cmd [-s] [target ...]        default target: all
#   all              like helper (this version has no driver)
#   helper           build\LSXIIVolumeFixer.exe: framework-dependent single file for the native architecture
#   driver           not part of this version (reserved for a later driver, docs/DESIGN.md 13)
#   test             build build\config-tests.exe, run test-install and test-uninstall, then the config tests
#   test-install     Tests\install_dry_run.ps1
#   test-uninstall   Tests\uninstall_dry_run.ps1
#   clean            remove build\
#   -s               silent: no command echo, quiet dotnet output (like make -s)
#
# Windows PowerShell 5.1 rules (docs/DESIGN.md 10.5): no && or ||, no ternary operator, no ??; saved with a BOM.
# No param() block: $args is read by hand, like the argument loops of the shell scripts.

$Root = $PSScriptRoot
$Build = Join-Path $Root 'build'
$TestBin = Join-Path $Build 'config-tests.exe'

# Addition (A72): the build sends nothing, like swiftc. Set for this process only.
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
# Same purpose, also for this process only: the SDK's first-run and background steps write outside windows\
# and fetch from nuget.org (measured 2026-10-19: an ASP.NET Core HTTPS development certificate in the user's
# certificate store, a workload advertising manifest downloaded and installed per MSI, repeated every 24 h).
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = '1'
$env:DOTNET_SKIP_WORKLOAD_INTEGRITY_CHECK = 'true'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'

$script:Silent = $false
$targets = @()
foreach ($a in $args) {
    if ($a -ceq '-s' -or $a -eq '--silent' -or $a -eq '--quiet') { $script:Silent = $true }
    else { $targets += [string]$a }
}
if ($targets.Count -eq 0) { $targets = @('all') }

function Fail([string]$target, [int]$code) {
    [Console]::Error.WriteLine("make: *** [$target] Error $code")
    exit 2
}

function Echo-Command([string]$line) {
    if (-not $script:Silent) { Write-Host $line }
}

# Native architecture of the PC, also from an emulated shell (x64 PowerShell on ARM64): IsWow64Process2
# reports the machine, not the process (the ARCH logic of the Makefile). The SDK only brings the apphost of
# its own architecture; the native SDK is the intended one.
function Get-Rid {
    $native = 0
    try {
        if (-not ('LsxMake.Native' -as [type])) {
            Add-Type -Namespace LsxMake -Name Native -MemberDefinition '[DllImport("kernel32.dll")] public static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);'
        }
        $pm = [uint16]0
        $nm = [uint16]0
        if ([LsxMake.Native]::IsWow64Process2([IntPtr]-1, [ref]$pm, [ref]$nm)) { $native = [int]$nm }
    } catch {
        $native = 0
    }
    if ($native -eq 0xAA64) { return 'win-arm64' }
    if ($native -eq 0x8664) { return 'win-x64' }
    $arch = $env:PROCESSOR_ARCHITEW6432
    if (-not $arch) { $arch = $env:PROCESSOR_ARCHITECTURE }
    if ($arch -eq 'ARM64') { return 'win-arm64' }
    return 'win-x64'
}

# dotnet publish into build\ (framework-dependent single file, docs/DESIGN.md 10.5). --disable-build-servers: no
# MSBuild or compiler server keeps running after the build, as nothing does after make.
function Invoke-Publish([string]$target, [string]$project) {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        [Console]::Error.WriteLine('make: dotnet: command not found (install the .NET 10 SDK: winget install Microsoft.DotNet.SDK.10)')
        Fail $target 127
    }
    $rid = Get-Rid
    $cmd = @('publish', $project, '-c', 'Release', '-r', $rid, '--self-contained', 'false', '-p:PublishSingleFile=true', '-nologo', '-o', 'build', '--disable-build-servers')
    if ($script:Silent) { $cmd += @('-v', 'quiet') }
    Echo-Command ('dotnet ' + ($cmd -join ' '))
    & dotnet @cmd
    if ($LASTEXITCODE -ne 0) { Fail $target $LASTEXITCODE }
}

function Invoke-ScriptTest([string]$target, [string]$script, [string]$scriptUnderTest) {
    $path = Join-Path $Root $script
    if (-not (Test-Path -LiteralPath $path)) {
        # Like make, a missing test script fails the target (sh: exit 127) - except in M0-M5, where the dry-run
        # tests have not arrived yet because the install scripts they test do not exist yet (M6).
        if (Test-Path -LiteralPath (Join-Path $Root $scriptUnderTest)) {
            [Console]::Error.WriteLine("${script}: No such file or directory")
            Fail $target 127
        }
        Write-Host "${target}: $script not present yet (M6), skipped"
        return
    }
    Echo-Command $script
    $global:LASTEXITCODE = 0
    & $path
    if ($LASTEXITCODE -ne 0) { Fail $target $LASTEXITCODE }
}

function Target-Helper {
    Invoke-Publish 'helper' 'Helper\LSXIIVolumeFixer.csproj'
}

function Target-Driver {
    [Console]::Error.WriteLine('driver: not part of this version')
    exit 2
}

# Same order as the Makefile: test binary, test-install, test-uninstall, then run the test binary.
function Target-Test {
    Invoke-Publish 'test' 'Tests\ConfigTests.csproj'
    Invoke-ScriptTest 'test-install' 'Tests\install_dry_run.ps1' 'scripts\install.ps1'
    Invoke-ScriptTest 'test-uninstall' 'Tests\uninstall_dry_run.ps1' 'scripts\uninstall.ps1'
    Echo-Command 'build\config-tests.exe'
    & $TestBin
    if ($LASTEXITCODE -ne 0) { Fail 'test' $LASTEXITCODE }
}

function Target-Clean {
    Echo-Command 'Remove-Item build -Recurse -Force'
    if (Test-Path -LiteralPath $Build) {
        # Files under build\obj come within a few characters of MAX_PATH (docs/DESIGN.md 10.6) and pass it from a
        # longer windows\ path; the \\?\ prefix lifts the limit.
        try { Remove-Item -LiteralPath ('\\?\' + $Build) -Recurse -Force -ErrorAction Stop }
        catch {
            [Console]::Error.WriteLine($_.Exception.Message)
            Fail 'clean' 1
        }
    }
}

Push-Location -LiteralPath $Root
try {
    foreach ($t in $targets) {
        switch ($t) {
            'all'            { Target-Helper }
            'helper'         { Target-Helper }
            'driver'         { Target-Driver }
            'test'           { Target-Test }
            'test-install'   { Invoke-ScriptTest 'test-install' 'Tests\install_dry_run.ps1' 'scripts\install.ps1' }
            'test-uninstall' { Invoke-ScriptTest 'test-uninstall' 'Tests\uninstall_dry_run.ps1' 'scripts\uninstall.ps1' }
            'clean'          { Target-Clean }
            default {
                [Console]::Error.WriteLine("make: *** No rule to make target '$t'.  Stop.")
                exit 2
            }
        }
    }
} finally {
    Pop-Location
}
exit 0
