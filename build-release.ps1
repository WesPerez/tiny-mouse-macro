[CmdletBinding()]
param([string]$TargetExe)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName Microsoft.VisualBasic

function Send-FileToRecycleBin([string]$Path) {
    [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile($Path,
        [Microsoft.VisualBasic.FileIO.UIOption]::OnlyErrorDialogs,
        [Microsoft.VisualBasic.FileIO.RecycleOption]::SendToRecycleBin,
        [Microsoft.VisualBasic.FileIO.UICancelOption]::ThrowException)
}

# Preserve the installed path used by startup. Never execute a registry command line.
if (-not $TargetExe) {
    $runKey = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue
    $entry = if ($runKey) { $runKey.PSObject.Properties['TinyMouseMacro'] } else { $null }
    if ($entry -and $entry.Value) {
        $command = [Environment]::ExpandEnvironmentVariables([string]$entry.Value).Trim()
        if ($command -match '^"([^"]+\\TinyMouseMacro\.exe)"(?:\s+.*)?$') { $TargetExe = $Matches[1] }
        elseif ($command -match '^([^"\r\n]+\\TinyMouseMacro\.exe)$') { $TargetExe = $Matches[1] }
        else { throw 'Cannot parse TinyMouseMacro startup path. Specify -TargetExe with the existing executable path.' }
    }
    else {
        $desktopExe = Join-Path ([Environment]::GetFolderPath('Desktop')) 'TinyMouseMacro.exe'
        if (Test-Path -LiteralPath $desktopExe -PathType Leaf) { $TargetExe = $desktopExe }
        else { throw 'No installed path found. Specify -TargetExe; do not create an alternate release directory.' }
    }
}
if (-not [IO.Path]::IsPathRooted($TargetExe)) { throw 'TargetExe must be an absolute path.' }
$TargetExe = [IO.Path]::GetFullPath($TargetExe)
if ([IO.Path]::GetFileName($TargetExe) -ine 'TinyMouseMacro.exe') { throw 'TargetExe must name TinyMouseMacro.exe.' }
$targetDirectory = Split-Path -Parent $TargetExe
if (-not (Test-Path -LiteralPath $targetDirectory -PathType Container)) { throw 'The installed directory must already exist.' }

if (-not ('TinyMouseMacro.DeploymentProcess' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
namespace TinyMouseMacro {
    public static class DeploymentProcess {
        [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder buffer, ref uint size);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        public static string PathOf(int pid) {
            var handle = OpenProcess(0x1000, false, pid);
            if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try {
                var buffer = new StringBuilder(32768); uint size = 32768;
                if (!QueryFullProcessImageName(handle, 0, buffer, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
                return buffer.ToString();
            } finally { CloseHandle(handle); }
        }
    }
}
'@
}

$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$stage = Join-Path $temporaryRoot ('TinyMouseMacro-build-' + [Guid]::NewGuid().ToString('N'))
$buildArtifacts = Join-Path $stage 'build'
$appPublish = Join-Path $stage 'app'
$launcherPublish = Join-Path $stage 'launcher'
$replaced = [Collections.Generic.List[object]]::new()
$keepRecovery = $false
function Install-FileAtomically([string]$Source, [string]$Destination) {
    $incoming = $Destination + '.incoming-' + [Guid]::NewGuid().ToString('N')
    try {
        Copy-Item -LiteralPath $Source -Destination $incoming
        if ((Get-FileHash -LiteralPath $Source).Hash -ne (Get-FileHash -LiteralPath $incoming).Hash) { throw "Copy verification failed: $Destination" }
        if (Test-Path -LiteralPath $Destination) { [IO.File]::Replace($incoming, $Destination, [NullString]::Value) }
        else { [IO.File]::Move($incoming, $Destination) }
    }
    finally { if (Test-Path -LiteralPath $incoming) { Send-FileToRecycleBin $incoming } }
}
Push-Location $PSScriptRoot
try {
    New-Item -ItemType Directory -Path $stage | Out-Null
    Write-Output "Installed target: $TargetExe"
    dotnet build TinyMouseMacro.csproj -c Release --artifacts-path $buildArtifacts
    if ($LASTEXITCODE -ne 0) { throw 'Build failed; installed files were not changed.' }
    $testAssembly = Join-Path $buildArtifacts 'bin/TinyMouseMacro/release/TinyMouseMacro.dll'
    $verification = Join-Path $stage 'verification'
    dotnet $testAssembly --self-test $verification
    if ($LASTEXITCODE -ne 0) {
        Get-Content (Join-Path $verification 'results.txt') -ErrorAction SilentlyContinue | Write-Output
        throw 'Offline checks failed; installed files were not changed.'
    }
    $checks = @(Get-Content (Join-Path $verification 'results.txt'))
    Write-Output ("Offline checks: {0} PASS" -f @($checks | Where-Object { $_ -like 'PASS *' }).Count)
    dotnet publish TinyMouseMacro.csproj -c Release -r win-x64 --self-contained true --artifacts-path $buildArtifacts -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $appPublish
    if ($LASTEXITCODE -ne 0) { throw 'App publish failed; installed files were not changed.' }
    dotnet publish launcher/SwitchLauncher.csproj -c Release -r win-x64 --self-contained true --artifacts-path $buildArtifacts -p:PublishSingleFile=true -p:PublishTrimmed=true -p:IncludeNativeLibrariesForSelfExtract=true -o $launcherPublish
    if ($LASTEXITCODE -ne 0) { throw 'Launcher publish failed; installed files were not changed.' }

    # Query full paths even for elevated processes, and never stop unrelated copies/apps.
    foreach ($process in @(Get-Process -Name TinyMouseMacro -ErrorAction SilentlyContinue)) {
        try { $processPath = [TinyMouseMacro.DeploymentProcess]::PathOf($process.Id) }
        catch { if (-not (Get-Process -Id $process.Id -ErrorAction SilentlyContinue)) { continue }; throw }
        if ($processPath -ine $TargetExe) { continue }
        Write-Output "Stopping TinyMouseMacro PID $($process.Id) at $processPath"
        try {
            Stop-Process -Id $process.Id -Force -ErrorAction Stop
            if (-not $process.WaitForExit(10000)) { throw 'Timed out waiting for process exit.' }
        }
        catch { throw "Cannot stop the installed instance. Run this script from an administrator PowerShell. No installed files were replaced. $($_.Exception.Message)" }
    }

    $files = @(
        @{ Source = (Join-Path $launcherPublish 'TinyMouseMacro.Switch.exe'); Target = (Join-Path $targetDirectory 'TinyMouseMacro.Switch.exe') },
        @{ Source = (Join-Path $PSScriptRoot 'README.md'); Target = (Join-Path $targetDirectory 'TinyMouseMacro.README.md') },
        @{ Source = (Join-Path $appPublish 'TinyMouseMacro.exe'); Target = $TargetExe }
    )
    # Keep recovery data if execution is interrupted during replacement.
    $keepRecovery = $true
    try {
        foreach ($file in $files) {
            $backup = $null
            if (Test-Path -LiteralPath $file.Target -PathType Leaf) {
                $backup = Join-Path $stage ([IO.Path]::GetFileName($file.Target) + '.previous')
                Copy-Item -LiteralPath $file.Target -Destination $backup
            }
            $replaced.Add(@{ Target = $file.Target; Backup = $backup })
            Install-FileAtomically $file.Source $file.Target
            if ((Get-FileHash -LiteralPath $file.Source).Hash -ne (Get-FileHash -LiteralPath $file.Target).Hash) { throw "Deployed file verification failed: $($file.Target)" }
        }
    }
    catch {
        $deploymentError = $_
        for ($index = $replaced.Count - 1; $index -ge 0; $index--) {
            $file = $replaced[$index]
            try {
                if ($file.Backup) { Install-FileAtomically $file.Backup $file.Target }
                elseif (Test-Path -LiteralPath $file.Target) { Send-FileToRecycleBin $file.Target }
            }
            catch { $keepRecovery = $true; Write-Warning "Restore failed for $($file.Target). Recovery files retained at $stage" }
        }
        throw $deploymentError
    }
    $keepRecovery = $false
    Write-Output "Deployed and verified: $TargetExe"
    Write-Output 'Startup entry and user data preserved. The application was not restarted.'
}
finally {
    Pop-Location
    # Recycle only this invocation's verified temporary directory, never the install directory.
    $resolvedStage = [IO.Path]::GetFullPath($stage)
    if ($keepRecovery) { Write-Warning "Deployment did not complete. Recovery files retained at $resolvedStage" }
    if (-not $keepRecovery -and (Test-Path -LiteralPath $resolvedStage)) {
        if ((Split-Path -Parent $resolvedStage) -ine $temporaryRoot -or (Split-Path -Leaf $resolvedStage) -notmatch '^TinyMouseMacro-build-[a-f0-9]{32}$') { throw 'Refusing cleanup outside this build temporary directory.' }
        [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory($resolvedStage,
            [Microsoft.VisualBasic.FileIO.UIOption]::OnlyErrorDialogs,
            [Microsoft.VisualBasic.FileIO.RecycleOption]::SendToRecycleBin,
            [Microsoft.VisualBasic.FileIO.UICancelOption]::ThrowException)
    }
}
