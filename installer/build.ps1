<#
.SYNOPSIS
    Builds the Forza Telemetry installer.

.DESCRIPTION
    Pipeline: dotnet test, self-contained publish of the WPF app into artifacts\stage, Inno Setup compile into dist\,
    then SHA-256 and size report. It writes only artifacts\ and dist\ (both git-ignored) and never installs anything.

    Publish choice: -p:PublishSingleFile=false on purpose. A single-file WPF app has to extract native libraries
    (PresentationNative, wpfgfx, D3DCompiler) to a temp folder at start-up, which slows launch and trips some
    antivirus products. A plain folder is simpler and the installer compresses it anyway.

.PARAMETER Version
    Installer version, x.y.z. Default: the latest git tag (a leading "v" is removed), else 0.1.0.

.PARAMETER IsccPath
    Optional explicit path to ISCC.exe. Otherwise looked up on PATH, in %LOCALAPPDATA%\Programs\Inno Setup 6 and in
    Program Files.

.PARAMETER SkipTests
    Skip `dotnet test` (CI already runs it separately).
#>
#Requires -Version 5.1
[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')] [string] $Version,
    [string] $IsccPath,
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'

function Write-Step([string] $Text) { Write-Host ''; Write-Host "== $Text" }

function Get-DefaultVersion {
    try {
        $tag = (& git describe --tags --abbrev=0 2>$null)
        if ($LASTEXITCODE -eq 0 -and $tag -match '^v?(\d+\.\d+\.\d+)$') { return $Matches[1] }
    }
    catch { }
    return '0.1.0'
}

function Find-Iscc([string] $Explicit) {
    if ($Explicit) {
        if (-not (Test-Path -LiteralPath $Explicit -PathType Leaf)) { throw "ISCC.exe not found at: $Explicit" }
        return (Resolve-Path -LiteralPath $Explicit).Path
    }
    $onPath = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) { return $candidate }
    }
    throw 'ISCC.exe not found. Install Inno Setup 6 (https://jrsoftware.org/isdl.php) or pass -IsccPath.'
}

try {
    $root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
    Set-Location -LiteralPath $root

    if (-not $Version) { $Version = Get-DefaultVersion }
    Write-Host "Version: $Version"

    $artifacts = Join-Path $root 'artifacts'
    $stage = Join-Path $artifacts 'stage'
    $dist = Join-Path $root 'dist'

    if (-not $SkipTests) {
        Write-Step 'Tests'
        & dotnet test (Join-Path $root 'ForzaTelemetry.slnx') -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw "dotnet test failed ($LASTEXITCODE)." }
    }

    Write-Step 'Publish (self-contained, win-x64)'
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    & dotnet publish (Join-Path $root 'src\ForzaTelemetry.App') -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=false -p:DebugType=none "-p:Version=$Version" -o $stage
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }
    $exe = Join-Path $stage 'ForzaTelemetry.App.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Publish did not produce $exe" }

    Write-Step 'Compile installer'
    $iss = Join-Path $PSScriptRoot 'ForzaTelemetry.iss'
    $iscc = Find-Iscc -Explicit $IsccPath
    New-Item -ItemType Directory -Path $dist -Force | Out-Null
    & $iscc /Qp "/DStageDir=$stage" "/DAppVersion=$Version" "/DOutputDir=$dist" $iss
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)." }

    Write-Step 'Result'
    $installer = Get-Item -LiteralPath (Join-Path $dist "ForzaTelemetry-Setup-$Version.exe")
    $sha256 = (Get-FileHash -LiteralPath $installer.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksumPath = "$($installer.FullName).sha256"
    [IO.File]::WriteAllText($checksumPath, "$sha256 *$($installer.Name)`n")
    Write-Host ("  {0}" -f $installer.FullName)
    Write-Host ("  size:    {0:N1} MB ({1} bytes)" -f ($installer.Length / 1MB), $installer.Length)
    Write-Host "  SHA-256: $sha256"
    Write-Host "  checksum file: $checksumPath"
}
catch {
    Write-Host ''
    Write-Host "BUILD FAILED: $($_.Exception.Message)"
    exit 1
}
