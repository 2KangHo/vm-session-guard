param(
    [string]$ConfigPath = (Join-Path $PSScriptRoot "VmSessionGuard.json")
)

$ErrorActionPreference = "Stop"
$exe = Join-Path $PSScriptRoot "VmSessionGuard.exe"
if (-not (Test-Path -LiteralPath $exe)) { throw "VmSessionGuard.exe not found: $exe" }
if (-not (Test-Path -LiteralPath $ConfigPath)) { throw "Config file not found: $ConfigPath" }

$startup = [Environment]::GetFolderPath("Startup")
$shortcutPath = Join-Path $startup "VM Session Guard.lnk"
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exe
$shortcut.Arguments = '--config "' + (Resolve-Path -LiteralPath $ConfigPath).Path + '"'
$shortcut.WorkingDirectory = $PSScriptRoot
$shortcut.Description = "Approved VM idle-prevention utility"
$shortcut.Save()

Write-Host "Startup shortcut created: $shortcutPath"
Write-Host "No administrator privileges were required. Sign out and back in, or run the shortcut to test."
