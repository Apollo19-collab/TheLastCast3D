# Publishes a new version of the game to GitHub. Every copy of the game checks
# the repo's latest release when it starts and offers to update itself.
#
#   .\Make-Release.ps1 1.1.0 "What's new in this version"
#
# It sets the version in the game (unity-src\...\Version.cs), builds Game\ with
# Make-TheLastCast-Exe.ps1, zips it as TheLastCast-Windows.zip, commits and tags
# the source (v1.1.0), pushes, and creates the GitHub release with the zip.
# Needs the GitHub CLI, signed in (gh auth login).

param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Notes = ''
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'The version must look like 1.2.3' }
$gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
if (-not $gh) { $gh = 'C:\Program Files\GitHub CLI\gh.exe' }
if (-not (Test-Path $gh)) { throw 'Install the GitHub CLI first: winget install GitHub.cli' }

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }
function Git { git -C $root @args; if ($LASTEXITCODE) { throw "git $args failed" } }

Step "Version $Version"
$versionFile = Join-Path $root 'unity-src\Assets\LastCast\Scripts\Version.cs'
$source = Get-Content $versionFile -Raw
$current = [regex]::Match($source, 'Current = "([^"]*)"').Groups[1].Value
if ([version]$Version -le [version]$current) { throw "The new version must be higher than $current" }
[IO.File]::WriteAllText($versionFile, ($source -replace 'Current = "[^"]*"', "Current = `"$Version`""))

Step 'Building'
& (Join-Path $root 'Make-TheLastCast-Exe.ps1') -NoLaunch
if (-not (Test-Path (Join-Path $root 'Game\TheLastCast.exe'))) { throw 'The build failed.' }

Step 'Zipping the game'
$zip = Join-Path $root 'TheLastCast-Windows.zip'
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $root 'Game\*') -DestinationPath $zip

Step 'Committing and pushing the source'
Git add -A
Git commit -m "Version $Version"
Git tag "v$Version"
Git push
Git push origin "v$Version"

Step 'Publishing the release'
$repo = (& $gh repo view --json nameWithOwner -q .nameWithOwner)
if (-not $Notes) { $Notes = "The Last Cast $Version" }
& $gh release create "v$Version" $zip --repo $repo --title "The Last Cast $Version" --notes $Notes
if ($LASTEXITCODE) { throw 'Creating the release failed.' }
Remove-Item $zip
Step "Done! Every copy of the game will offer version $Version next time it starts."
