param(
    [Parameter(Mandatory = $true)]
    [string]$BackupPath
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$databasePath = Join-Path $repositoryRoot 'src\Kivra.Api\kivra-native.db'
$resolvedBackup = (Resolve-Path -LiteralPath $BackupPath).Path
$backupRoot = (Resolve-Path -LiteralPath (Join-Path $repositoryRoot 'src\Kivra.Api\backups')).Path

if (-not $resolvedBackup.StartsWith($backupRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The backup must be selected from src\Kivra.Api\backups.'
}

if (Get-NetTCPConnection -LocalPort 8080 -State Listen -ErrorAction SilentlyContinue) {
    throw 'Stop KIVRA on port 8080 before restoring a backup.'
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$safetyCopy = "$databasePath.before-restore-$stamp"
Copy-Item -LiteralPath $databasePath -Destination $safetyCopy
Copy-Item -LiteralPath $resolvedBackup -Destination $databasePath -Force

Write-Host "Database restored from: $resolvedBackup"
Write-Host "Previous database preserved at: $safetyCopy"
