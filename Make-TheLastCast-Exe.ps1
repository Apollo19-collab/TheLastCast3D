# Builds The Last Cast (first person) into a Windows .exe, from scratch:
#
#   1. the Unity CLI and the Unity 6 LTS Editor (Windows asks for admin rights once)
#   2. signing in to Unity and a free Personal licence (your browser opens)
#   3. a new Unity project in TheLastCastFP\, with the packages it needs
#   4. the game's code and data from unity-src\, then the build
#
# The game ends up in Game\TheLastCast.exe. Run it again any time to rebuild
# after changes; finished steps are skipped. Logs go to logs\.

param([string]$Version = '6000.6.5f1', [switch]$NoLaunch)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'TheLastCastFP'
$logs = Join-Path $root 'logs'
New-Item -ItemType Directory -Force $logs | Out-Null

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }
function Json($cmd) { (& unity @cmd --json --no-banner 2>$null | Out-String | ConvertFrom-Json) }

# ---- 1. Unity CLI and Editor --------------------------------------------------------------
Step 'Unity CLI'
$cliBin = Join-Path $env:LOCALAPPDATA 'Unity\bin'
if (-not (Get-Command unity -ErrorAction SilentlyContinue)) {
    if (-not (Test-Path (Join-Path $cliBin 'unity.exe'))) {
        $env:UNITY_CLI_CHANNEL = 'beta'
        Invoke-RestMethod https://public-cdn.cloud.unity3d.com/hub/prod/cli/install.ps1 | Invoke-Expression
    }
    $env:Path += ";$cliBin"
}
unity --version

Step "Unity Editor $Version"
function Find-Editor {
    $list = (Json @('editors', '--installed')).data
    $e = $list | Where-Object { $_.version -eq $Version } | Select-Object -First 1
    if (-not $e) { return $null }
    $path = if ($e.location) { $e.location } else { $e.path }
    foreach ($candidate in @($path, (Join-Path $path 'Unity.exe'), (Join-Path $path 'Editor\Unity.exe'))) {
        if ($candidate -and (Test-Path $candidate -PathType Leaf)) { return $candidate }
    }
    return $null
}
$editor = Find-Editor
if (-not $editor) {
    Write-Host 'Installing the Editor (a few GB). Click Yes when Windows asks for permission.'
    unity install $Version --accept-eula --yes
    if ($LASTEXITCODE -ne 0) { throw 'The Editor install failed. See the message above.' }
    $editor = Find-Editor
    if (-not $editor) { throw 'Installed, but could not find Unity.exe. Run: unity editors --installed' }
}
Write-Host "Editor: $editor"

# ---- 2. Sign-in and licence -----------------------------------------------------------------
Step 'Unity account and licence'
if (-not (Json @('auth', 'status')).data.loggedIn) {
    Write-Host 'Sign in to your Unity account in the browser window that opens.'
    unity auth login
}
if (-not (Json @('license', 'status')).data.active) {
    Write-Host 'Activating a licence (choose the free Personal licence if asked).'
    unity license activate
}

# ---- 3. Project and packages ----------------------------------------------------------------
if (-not (Test-Path (Join-Path $project 'Assets'))) {
    Step 'Creating the Unity project'
    $templates = (Json @('templates', 'list', '--editor', $Version)).data
    $template = ($templates | Where-Object { $_.displayName -eq 'Universal 3D' } | Select-Object -First 1).name
    if (-not $template) { $template = 'com.unity.template.urp-blank' }
    Write-Host "Template: $template"
    unity projects create 'TheLastCastFP' --path $root --editor-version $Version --template $template --with-pipeline --non-interactive
    if ($LASTEXITCODE -ne 0) { throw 'Creating the project failed.' }
    # The template's tutorial readme isn't part of the game.
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue (Join-Path $project 'Assets\TutorialInfo*'), (Join-Path $project 'Assets\Readme.asset*')
}

# Multiplayer: everyone's copy of the game must use the same Unity Cloud project,
# so a rebuilt project links back to the one in unity-src\cloud-project.txt.
$cloudFile = Join-Path $root 'unity-src\cloud-project.txt'
$settings = Join-Path $project 'ProjectSettings\ProjectSettings.asset'
if ((Test-Path $cloudFile) -and -not (Select-String -Path $settings -Pattern ([regex]::Escape((Get-Content $cloudFile -Raw).Trim())) -Quiet)) {
    Step 'Linking the project to its Unity Cloud project (for multiplayer)'
    unity projects link cloud $project --cloud-project (Get-Content $cloudFile -Raw).Trim()
}

$manifest = Get-Content (Join-Path $project 'Packages\manifest.json') -Raw
if ($manifest -notmatch 'com.unity.nuget.newtonsoft-json' -or $manifest -notmatch 'com.unity.inputsystem') {
    Step 'Adding packages (JSON, Input System)'
    $boot = Join-Path $project 'Assets\Editor\ProjectBootstrap'
    New-Item -ItemType Directory -Force $boot | Out-Null
    Copy-Item (Join-Path $root 'unity-src\Bootstrap\PackageInstaller.cs') $boot -Force
    # No -quit: the installer quits by itself once the packages are in.
    $p = Start-Process $editor -ArgumentList @('-batchmode', '-projectPath', "`"$project`"", '-executeMethod', 'ProjectBootstrap.PackageInstaller.Install', '-logFile', "`"$logs\packages.log`"") -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "Adding packages failed (exit $($p.ExitCode)). See logs\packages.log" }
}

# ---- 4. The game, and the build ---------------------------------------------------------------
Step 'Copying the game into the project'
robocopy (Join-Path $root 'unity-src\Assets\LastCast') (Join-Path $project 'Assets\LastCast') /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw 'Copying the game files failed.' }

Step 'Building TheLastCast.exe (the first build takes a few minutes)'
$p = Start-Process $editor -ArgumentList @('-batchmode', '-quit', '-projectPath', "`"$project`"", '-executeMethod', 'LastCast.EditorTools.BuildGame.Windows', '-logFile', "`"$logs\build.log`"") -Wait -PassThru
$exe = Join-Path $project 'Builds\Windows\TheLastCast.exe'
if ($p.ExitCode -ne 0 -or -not (Test-Path $exe)) {
    Write-Host "`nThe build failed. The errors are in logs\build.log:" -ForegroundColor Red
    Select-String -Path (Join-Path $logs 'build.log') -Pattern 'error CS|Error|Exception' | Select-Object -First 25 | ForEach-Object { Write-Host $_.Line }
    exit 1
}

$game = Join-Path $root 'Game'
# Unity's debug-symbol backup folder isn't needed to play, so it stays out of Game\.
robocopy (Split-Path $exe) $game /MIR /XD '*_BackUpThisFolder_ButDontShipItWithYourGame' /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw 'Copying the build into Game\ failed.' }
Remove-Item -Recurse -Force (Join-Path $project 'Builds')
Step "Done! The game is in $game\TheLastCast.exe"
if (-not $NoLaunch) { Start-Process (Join-Path $game 'TheLastCast.exe') }
