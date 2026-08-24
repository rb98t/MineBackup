<#
.SYNOPSIS
    Builds MineBackup and ships it to the backup server, with a rollback if it fails to run.

.DESCRIPTION
    Run this from the development machine. Connection details come from the `minemania` entry in
    ~/.ssh/config, so no host, user or key path lives in this repo.

    The deployment surface is a single file. MineBackup.exe is a Native AOT single-file build, and
    config.json, credentials.json and token.json sit beside it on the server holding the Google
    OAuth refresh token, the Drive folder id and the MySQL password. None of those are ever touched:
    they are not produced by the build, and losing token.json means the next nightly run stops at an
    interactive browser prompt that a scheduled task can never answer.

.PARAMETER Verify
    After swapping the exe, prove it can still authenticate and upload by running a real targeted
    backup of a small folder with --no-purge. Off by default because it puts a file on Drive.

.EXAMPLE
    .\deploy\Deploy-MineBackup.ps1 -WhatIf
    .\deploy\Deploy-MineBackup.ps1
    .\deploy\Deploy-MineBackup.ps1 -Verify
    .\deploy\Deploy-MineBackup.ps1 -Rollback 2026-08-24_15-10-00
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $SshHost      = 'minemania',
    [string] $RemoteDir    = 'D:/Minecraft/MineBackup',
    [string] $BackupRoot   = 'D:/Backups/minebackup',
    [string] $ScheduledTask = 'MineBackup',
    [switch] $SkipBuild,
    [switch] $Verify,
    [switch] $AllowDirtyTree,
    [string] $Rollback
)

$ErrorActionPreference = 'Stop'
$started = Get-Date

$RepoRoot  = Split-Path -Parent $PSScriptRoot
$Timestamp = Get-Date -Format 'yyyy-MM-dd_HH-mm-ss'
$RemoteWin = $RemoteDir -replace '/', '\'
$BackupWin = $BackupRoot -replace '/', '\'

function Write-Step { param([string] $Text) Write-Host ''; Write-Host "=== $Text ===" -ForegroundColor Cyan }

function Invoke-Ssh {
    param([string] $Command, [switch] $PassThru)
    $output = & ssh $SshHost $Command 2>&1
    $code = $LASTEXITCODE
    if (-not $PassThru -or $code -ne 0) { $output | ForEach-Object { Write-Host $_ } }
    if ($code -ne 0) { throw "Tavoli parancs hiba (kilepesi kod: $code): $Command" }
    if ($PassThru) { return $output }
}

# --------------------------------------------------------------------------------------------
# Rollback
# --------------------------------------------------------------------------------------------
if ($Rollback) {
    Write-Step "Visszaallitas: $BackupWin\$Rollback"
    if ($PSCmdlet.ShouldProcess($SshHost, "Visszaallitas: $Rollback")) {
        Invoke-Ssh -Command ("powershell -NoProfile -Command ""Copy-Item '{0}\{1}\MineBackup.exe' '{2}\MineBackup.exe' -Force; & '{2}\MineBackup.exe' --help | Select-Object -First 1""" -f
            $BackupWin, $Rollback, $RemoteWin)
        Write-Host 'Visszaallitva.'
    }
    return
}

# --------------------------------------------------------------------------------------------
# 1. Eloellenorzes
# --------------------------------------------------------------------------------------------
Write-Step '1/5 Eloellenorzes'

Push-Location $RepoRoot
try {
    $branch = (& git rev-parse --abbrev-ref HEAD).Trim()
    $sha    = (& git rev-parse --short HEAD).Trim()
    $dirty  = & git status --porcelain
    if ($dirty -and -not $AllowDirtyTree) {
        throw 'A working tree nem tiszta. Commitolj, vagy hasznald az -AllowDirtyTree kapcsolot.'
    }
} finally {
    Pop-Location
}
Write-Host "  repo    : $branch @ $sha$(if ($dirty) { ' (piszkos fa)' })"

$hostName = (Invoke-Ssh -Command 'hostname' -PassThru) -join ''
Write-Host "  szerver : $SshHost -> $($hostName.Trim())"

# Swapping the exe out from under a running backup would abort that night's run and leave partial
# archives in temp.
$running = (Invoke-Ssh -PassThru -Command 'powershell -NoProfile -Command "if (Get-Process -Name MineBackup -ErrorAction SilentlyContinue) { ''FUT'' } else { ''all'' }"') -join ''
if ($running -match 'FUT') { throw 'A MineBackup eppen fut. Varj, amig befejezi.' }
Write-Host '  MineBackup nem fut, mehet'

# --------------------------------------------------------------------------------------------
# 2. Build
# --------------------------------------------------------------------------------------------
$publishDir = Join-Path $RepoRoot 'bin\Release\net10.0\win-x64\publish'
$exePath    = Join-Path $publishDir 'MineBackup.exe'

if ($SkipBuild) {
    Write-Step '2/5 Build kihagyva (-SkipBuild)'
    if (-not (Test-Path -LiteralPath $exePath)) { throw "Nincs korabbi build: $exePath" }
} else {
    Write-Step '2/5 Build (Native AOT)'
    if ($PSCmdlet.ShouldProcess('lokalis gep', 'MineBackup buildelese')) {
        # The AOT link step shells out to vswhere.exe to locate the MSVC linker, and vswhere is not
        # on PATH by default. Without this the publish fails with MSB3073 exit code 123.
        $vsInstaller = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer'
        if (Test-Path -LiteralPath (Join-Path $vsInstaller 'vswhere.exe')) {
            $env:PATH = "$vsInstaller;$env:PATH"
        } else {
            Write-Warning "vswhere.exe nem talalhato ($vsInstaller). Az AOT linkeles valoszinuleg elhasal."
        }

        # The csproj deliberately omits RuntimeIdentifier, so it must be given here.
        Push-Location $RepoRoot
        try {
            & dotnet publish -c Release -r win-x64 --nologo
            if ($LASTEXITCODE -ne 0) { throw "A build nem sikerult (kilepesi kod: $LASTEXITCODE)." }
        } finally {
            Pop-Location
        }
    }
}

if (Test-Path -LiteralPath $exePath) {
    Write-Host ("  MineBackup.exe ({0:n1} MB)" -f ((Get-Item -LiteralPath $exePath).Length / 1MB))
}

# --------------------------------------------------------------------------------------------
# 3. Mentes
# --------------------------------------------------------------------------------------------
Write-Step '3/5 A kinti verzio mentese'

if ($PSCmdlet.ShouldProcess($SshHost, 'A jelenlegi exe felretetele')) {
    Invoke-Ssh -Command ("powershell -NoProfile -Command ""New-Item -ItemType Directory -Force -Path '{0}\{1}' | Out-Null; Copy-Item '{2}\MineBackup.exe' '{0}\{1}\MineBackup.exe' -Force; (Get-Item '{0}\{1}\MineBackup.exe').Length""" -f
        $BackupWin, $Timestamp, $RemoteWin)
    Write-Host "  $BackupWin\$Timestamp\MineBackup.exe"
}

# --------------------------------------------------------------------------------------------
# 4. Kiszallitas
# --------------------------------------------------------------------------------------------
Write-Step '4/5 Kiszallitas'

if ($PSCmdlet.ShouldProcess($SshHost, 'Uj exe kiszallitasa')) {
    & scp -q $exePath ("{0}:{1}/MineBackup.exe" -f $SshHost, $RemoteDir)
    if ($LASTEXITCODE -ne 0) { throw 'scp hiba.' }
    Write-Host '  kesz'
}

# --------------------------------------------------------------------------------------------
# 5. Ellenorzes
# --------------------------------------------------------------------------------------------
Write-Step '5/5 Ellenorzes'

$failed = $false
if ($PSCmdlet.ShouldProcess($SshHost, 'Az uj exe ellenorzese')) {
    try {
        # A single-file AOT binary that fails to start does so on any invocation, so --help is a
        # real smoke test: it exercises startup, the CLI parser and the console writer.
        # Backtick, not backslash: PowerShell escapes with backtick, so "\$LASTEXITCODE" would
        # expand here and send the local exit code to the server instead of reading the remote one.
        Invoke-Ssh -Command ("powershell -NoProfile -Command ""& '{0}\MineBackup.exe' --help | Select-Object -First 1; exit `$LASTEXITCODE""" -f $RemoteWin)
        Write-Host '  indul es a kapcsolokat ismeri'

        if ($Verify) {
            # Proves the parts --help cannot: config load, Google OAuth refresh, zip, resumable
            # upload. --no-purge keeps it clear of the nightly retention sweep.
            Write-Host '  eles proba: celzott mentes feltoltessel...'
            Invoke-Ssh -Command ("powershell -NoProfile -Command ""Set-Location '{0}'; & '.\MineBackup.exe' --source '{0}\logs' --prefix DEPLOYCHECK --no-purge | Out-Null; exit `$LASTEXITCODE""" -f $RemoteWin)
            Write-Host '  hitelesites, tomorites es feltoltes rendben'
        }
    } catch {
        $failed = $true
        Write-Warning "ELLENORZES BUKOTT: $($_.Exception.Message)"
        Write-Host 'Visszaallitas...'
        Invoke-Ssh -Command ("powershell -NoProfile -Command ""Copy-Item '{0}\{1}\MineBackup.exe' '{2}\MineBackup.exe' -Force""" -f
            $BackupWin, $Timestamp, $RemoteWin)
        Write-Host 'Visszaallitva a korabbi verziora.'
    }
}

if ($failed) {
    Write-Host ''
    Write-Host 'DEPLOY_EREDMENY: HIBA (visszaallitva)'
    exit 1
}

Write-Step 'Kesz'
Write-Host "Verzio : $branch @ $sha"
Write-Host "Mentes : $BackupWin\$Timestamp"
Write-Host "Vissza : .\deploy\Deploy-MineBackup.ps1 -Rollback $Timestamp"
Write-Host "Utemezes: a(z) '$ScheduledTask' feladat naponta 04:00-kor futtatja"
Write-Host ("Eltelt : {0:n1} perc" -f ((Get-Date) - $started).TotalMinutes)
