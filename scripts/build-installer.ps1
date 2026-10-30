#Requires -Version 5.1
<#
.SYNOPSIS
    Publie Memo en win-x64 auto-contenu et compile l'installateur Inno Setup.

.DESCRIPTION
    Produit artifacts\publish, puis artifacts\Memo-<version>-win-x64-setup.exe.
    Échoue avec un message explicite si Inno Setup (ISCC.exe) est absent.
    Aucun outil n'est téléchargé automatiquement.

.PARAMETER SkipPublish
    Réutilise artifacts\publish déjà présent (utile en CI après téléchargement
    de l'artefact de build).

.EXAMPLE
    pwsh -File scripts\build-installer.ps1

.EXAMPLE
    pwsh -File scripts\build-installer.ps1 -SkipPublish
#>
[CmdletBinding()]
param(
    [switch]$SkipPublish
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'src\Memo.App\Memo.App.csproj'
$issPath = Join-Path $repoRoot 'installer\Memo.iss'
$artifactsDir = Join-Path $repoRoot 'artifacts'
$publishDir = Join-Path $artifactsDir 'publish'

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

        # Installation via winget : le paquet peut être placé sous WinGet\Packages.
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

function Show-InnoMissingHelp {
    Write-Host ''
    Write-Host 'Inno Setup 6 est introuvable : ISCC.exe n''a pas été trouvé.' -ForegroundColor Red
    Write-Host 'Aucun téléchargement automatique n''est effectué.'
    Write-Host ''
    Write-Host 'Installez Inno Setup avec l''un des moyens suivants :'
    Write-Host '  pwsh -File scripts\install-inno.ps1'
    Write-Host '  winget install --id JRSoftware.InnoSetup -e'
    Write-Host '  téléchargement manuel : https://jrsoftware.org/isdl.php'
    Write-Host ''
    Write-Host 'Emplacements recherchés : INNO_SETUP_HOME, PATH, Program Files,'
    Write-Host 'Program Files (x86), %LOCALAPPDATA%\Programs et le cache winget.'
}

try {
    Write-Host "Dépôt      : $repoRoot"
    Write-Host "Publication: $publishDir"

    $iscc = Find-Iscc
    if (-not $iscc) {
        Show-InnoMissingHelp
        exit 1
    }
    Write-Host "ISCC       : $iscc"

    if (-not $SkipPublish) {
        if (Test-Path -LiteralPath $publishDir) {
            Remove-Item -LiteralPath $publishDir -Recurse -Force
        }
        New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

        Write-Host ''
        Write-Host 'Build Release (artefacts XAML)...' -ForegroundColor Cyan
        & dotnet build $projectPath -c Release
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet build a échoué (code $LASTEXITCODE)."
        }

        Write-Host ''
        Write-Host 'Publication self-contained win-x64...' -ForegroundColor Cyan
        $publishArgs = @(
            'publish', $projectPath,
            '-c', 'Release',
            '-r', 'win-x64',
            '--self-contained',
            '-p:PublishReadyToRun=false',
            '-o', $publishDir
        )
        & dotnet @publishArgs
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish a échoué (code $LASTEXITCODE)."
        }
    }
    else {
        Write-Host 'Publication réutilisée (-SkipPublish).'
    }

    # WinUI non empaqueté : `dotnet publish` omet les artefacts XAML compilés
    # (.xbf) et Memo.pri. L'application publiée démarre alors sans ressources
    # XAML et meurt en STATUS_STOWED_EXCEPTION. On les recopie depuis la sortie
    # de build Release, qui contient les mêmes assemblages.
    $buildOutput = Get-ChildItem (Join-Path $repoRoot 'src\Memo.App\bin\Release') -Directory |
        ForEach-Object { Join-Path $_.FullName 'win-x64' } |
        Where-Object { Test-Path -LiteralPath (Join-Path $_ 'Memo.pri') } |
        Sort-Object { (Get-Item -LiteralPath (Join-Path $_ 'Memo.dll')).LastWriteTime } -Descending |
        Select-Object -First 1
    if (-not $buildOutput) {
        throw "Sortie de build Release introuvable : impossible de copier les artefacts XAML."
    }

    $xbfFiles = @(Get-ChildItem -LiteralPath $buildOutput -Filter '*.xbf' -Recurse -File)
    if ($xbfFiles.Count -eq 0) {
        throw "Aucun fichier .xbf dans $buildOutput. Lancez d'abord un build Release."
    }

    foreach ($file in $xbfFiles) {
        $relative = $file.FullName.Substring($buildOutput.Length + 1)
        $destination = Join-Path $publishDir $relative
        $parent = Split-Path -Parent $destination
        if (-not (Test-Path -LiteralPath $parent)) {
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
        }
        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
    }
    Copy-Item -LiteralPath (Join-Path $buildOutput 'Memo.pri') -Destination (Join-Path $publishDir 'Memo.pri') -Force
    Write-Host ("Artefacts XAML copiés : {0} fichiers .xbf + Memo.pri" -f $xbfFiles.Count)

    $exePath = Join-Path $publishDir 'Memo.exe'
    if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
        throw "Memo.exe est absent de $publishDir. La publication est incomplète."
    }

    if (-not (Test-Path -LiteralPath $artifactsDir)) {
        New-Item -ItemType Directory -Path $artifactsDir -Force | Out-Null
    }

    Write-Host ''
    Write-Host 'Compilation de l''installateur...' -ForegroundColor Cyan
    & $iscc '/Qp' "/O$artifactsDir" $issPath
    if ($LASTEXITCODE -ne 0) {
        throw "ISCC a échoué (code $LASTEXITCODE)."
    }

    $versionLine = Select-String -LiteralPath $issPath -Pattern '^#define\s+MyAppVersion\s+"([^"]+)"' |
        Select-Object -First 1
    if (-not $versionLine) {
        throw 'La version est introuvable dans installer\Memo.iss (#define MyAppVersion).'
    }
    $version = $versionLine.Matches[0].Groups[1].Value

    $installerName = "Memo-$version-win-x64-setup.exe"
    $installerPath = Join-Path $artifactsDir $installerName
    if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
        throw "L'installateur attendu est absent : $installerPath"
    }

    $hash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash
    $size = (Get-Item -LiteralPath $installerPath).Length
    $hashPath = "$installerPath.sha256"
    "$hash  $installerName" | Set-Content -LiteralPath $hashPath -Encoding Ascii

    Write-Host ''
    Write-Host 'Installateur produit.' -ForegroundColor Green
    Write-Host "  Fichier : $installerPath"
    Write-Host ("  Taille  : {0:N1} Mo ({1} octets)" -f ($size / 1MB), $size)
    Write-Host "  SHA-256 : $hash"
    Write-Host "  Empreinte: $hashPath"
    Write-Host ''
    Write-Host 'Rappel : cet installateur n''est pas signé. SmartScreen peut alerter.'
    exit 0
}
catch {
    Write-Host ''
    Write-Host "Échec : $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
