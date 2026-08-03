$startup = [Environment]::GetFolderPath("Startup")
$shortcutPath = Join-Path $startup "VM Session Guard.lnk"
if (Test-Path -LiteralPath $shortcutPath) {
    Remove-Item -LiteralPath $shortcutPath
    Write-Host "Removed: $shortcutPath"
} else {
    Write-Host "Startup shortcut is not installed."
}
