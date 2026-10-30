#Requires -Version 5.1
<#
.SYNOPSIS
    Installe Inno Setup 6 via winget, avec confirmation.

.DESCRIPTION
    Vérifie d'abord si ISCC.exe est déjà disponible. Sinon, propose d'installer
    le paquet JRSoftware.InnoSetup avec winget. En cas d'échec ou d'absence de
    winget, affiche la procédure manuelle. Aucun téléchargement direct n'est
    effectué par ce script.

.PARAMETER Yes
    Ne pose pas de question et lance winget directement.

.EXAMPLE
    pwsh -File scripts\install-inno.ps1

.EXAMPLE
    pwsh -File scripts\install-inno.ps1 -Yes
#>
[CmdletBinding()]
param(
    [switch]$Yes
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Find-Iscc {
    $candidates = [System.Collections.Generic.List[string]]::new()

    if ($env:INNO_SETUP_HOME) {
        $candidates.Add((Join-Path $env:INNO_SETUP_HOME 'ISCC.exe'))
    }

    $command = Get-Command 'ISCC.exe' -CommandType Application -ErrorAction SilentlyContinue
    if ($command) {
        $candidates.Add($command.Source)
    }

    $programFiles = @($env:ProgramFiles, ${env:ProgramFiles(x86)})
    foreach ($base in $programFiles) {
        if (-not $base) { continue }
        $candidates.Add((Join-Path $base 'Inno Setup 6\ISCC.exe'))
        if (Test-Path -LiteralPath $base) {
            Get-ChildItem -LiteralPath $base -Directory -Filter 'Inno Setup *' -ErrorAction SilentlyContinue |
                ForEach-Object { $candidates.Add((Join-Path $_.FullName 'ISCC.exe')) }
        }
    }

    if ($env:LOCALAPPDATA) {
        $candidates.Add((Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'))

        $wingetRoot = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages'
        if (Test-Path -LiteralPath $wingetRoot) {
            Get-ChildItem -LiteralPath $wingetRoot -Directory -Filter 'JRSoftware.InnoSetup*' -ErrorAction SilentlyContinue |
                ForEach-Object {
                    Get-ChildItem -LiteralPath $_.FullName -Recurse -File -Filter 'ISCC.exe' -ErrorAction SilentlyContinue |
                        ForEach-Object { $candidates.Add($_.FullName) }
                }
        }
    }

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    return $null
}

function Show-ManualInstructions {
    Write-Host ''
    Write-Host 'Installation manuelle de secours :' -ForegroundColor Yellow
    Write-Host '  1. Ouvrir https://jrsoftware.org/isdl.php'
    Write-Host '  2. Télécharger Inno Setup 6 pour Windows.'
    Write-Host '  3. Exécuter l''installateur avec les options par défaut.'
    Write-Host '  4. Relancer scripts\build-installer.ps1.'
    Write-Host ''
    Write-Host 'Ce script ne télécharge pas l''installateur à votre place.'
}

try {
    $existing = Find-Iscc
    if ($existing) {
        Write-Host "Inno Setup est déjà installé : $existing" -ForegroundColor Green
        exit 0
    }

    $winget = Get-Command 'winget.exe' -CommandType Application -ErrorAction SilentlyContinue
    if (-not $winget) {
        Write-Host 'winget est introuvable sur cette machine.' -ForegroundColor Red
        Show-ManualInstructions
        exit 1
    }

    Write-Host 'Inno Setup 6 n''est pas installé.'
    Write-Host 'Commande proposée :'
    Write-Host '  winget install --id JRSoftware.InnoSetup -e --silent --accept-package-agreements --accept-source-agreements'
    Write-Host ''

    if (-not $Yes) {
        if (-not [Environment]::UserInteractive) {
            Write-Host 'Session non interactive : relancez avec -Yes pour confirmer.' -ForegroundColor Yellow
            exit 1
        }
        $answer = Read-Host 'Installer Inno Setup 6 maintenant ? [o/N]'
        if ($answer -notmatch '^(o|oui|y|yes)$') {
            Write-Host 'Installation annulée.' -ForegroundColor Yellow
            exit 1
        }
    }

    & $winget.Source install --id JRSoftware.InnoSetup -e --silent --accept-package-agreements --accept-source-agreements
    $wingetCode = $LASTEXITCODE
    if ($wingetCode -ne 0) {
        Write-Host "winget a échoué (code $wingetCode)." -ForegroundColor Red
        Show-ManualInstructions
        exit $wingetCode
    }

    $installed = Find-Iscc
    if (-not $installed) {
        Write-Host 'winget a terminé, mais ISCC.exe reste introuvable.' -ForegroundColor Yellow
        Write-Host 'Vérifiez l''installation, puis relancez scripts\build-installer.ps1.'
        Show-ManualInstructions
        exit 1
    }

    Write-Host "Inno Setup installé : $installed" -ForegroundColor Green
    exit 0
}
catch {
    Write-Host ''
    Write-Host "Échec : $($_.Exception.Message)" -ForegroundColor Red
    Show-ManualInstructions
    exit 1
}
