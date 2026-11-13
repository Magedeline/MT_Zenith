<#
.SYNOPSIS
  Builds the MT_Zenith release zips for GitHub Releases and GameBanana.

.DESCRIPTION
  Writes to Tools/CELMapViewer/dist/:
	MT_Zenith-<ver>-win-x64.zip               self-contained single-file exe (no .NET needed)
	MT_Zenith-<ver>-win-x64-fxdependent.zip   single-file exe, needs the .NET 8+ Desktop Runtime
	SHA256SUMS.txt, release-notes.md, gamebanana.txt
  The version comes from <Version> in CELMapViewer.csproj unless -Version is given.

.EXAMPLE
  ./publish.ps1                 # build both zips
  ./publish.ps1 -SelfTest       # also run the published exe's --selftest (needs a local Celeste + CEL)
#>
param(
	[string]$Version,
	[switch]$SelfTest,
	[switch]$SkipFxDependent
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$proj = Join-Path $here 'CELMapViewer.csproj'
$dist = Join-Path $here 'dist'

if (-not $Version) {
	$Version = ([xml](Get-Content $proj -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if (-not $Version) { throw "No <Version> in $proj" }
$Version = $Version.TrimStart('v')

# Clear only what this script generates; hand-made files (cover art, GameBanana texts) stay.
New-Item -ItemType Directory $dist -Force | Out-Null
Get-ChildItem $dist -Filter 'MT_Zenith-*.zip' | Remove-Item -Force
foreach ($g in 'stage', 'SHA256SUMS.txt', 'release-notes.md', 'gamebanana.txt') {
	$p = Join-Path $dist $g
	if (Test-Path $p) { Remove-Item $p -Recurse -Force }
}

# "What's new" for this version: the "## <Version>" section of CHANGELOG.md.
$changes = ''
$changelog = Join-Path $here 'CHANGELOG.md'
if (Test-Path $changelog) {
	$m = [regex]::Match((Get-Content $changelog -Raw), "(?ms)^## $([regex]::Escape($Version))\s*\r?\n(.*?)(?=^## |\z)")
	if ($m.Success) { $changes = $m.Groups[1].Value.Trim() }
}

$flavors = @(@{ Name = "MT_Zenith-$Version-win-x64"; SelfContained = 'true' })
if (-not $SkipFxDependent) { $flavors += @{ Name = "MT_Zenith-$Version-win-x64-fxdependent"; SelfContained = 'false' } }

foreach ($f in $flavors) {
	$stage = Join-Path $dist ('stage\' + $f.Name)
	Write-Host "==> $($f.Name)"
	dotnet publish $proj -c Release -r win-x64 --self-contained $f.SelfContained `
		-p:PublishSingleFile=true -p:Version=$Version -o $stage -nologo
	if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $($f.Name)" }

	Get-ChildItem $stage -Filter *.pdb | Remove-Item
	Copy-Item (Join-Path $here 'README.md') (Join-Path $stage 'README.txt')
	Copy-Item (Join-Path $here 'MT_Zenith.png') $stage
	Copy-Item (Join-Path $here 'LICENSE') (Join-Path $stage 'LICENSE.txt')
	Copy-Item (Join-Path $here 'THIRD-PARTY-NOTICES.txt') $stage
	if (Test-Path $changelog) { Copy-Item $changelog (Join-Path $stage 'CHANGELOG.txt') }

	if ($SelfTest) {
		$exe = Join-Path $stage 'MT_Zenith.exe'
		$p = Start-Process $exe -ArgumentList '--selftest' -Wait -PassThru -NoNewWindow
		if ($p.ExitCode -ne 0) { throw "$exe --selftest failed ($($p.ExitCode))" }
	}

	Compress-Archive -Path (Join-Path $stage '*') -DestinationPath (Join-Path $dist "$($f.Name).zip") -CompressionLevel Optimal
}
Remove-Item (Join-Path $dist 'stage') -Recurse -Force

$sums = Get-ChildItem $dist -Filter *.zip | ForEach-Object {
	'{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
}
$sums | Set-Content (Join-Path $dist 'SHA256SUMS.txt') -Encoding ascii

$notes = @"
## MT_Zenith $Version

Map viewer/editor for Celeste ``.bin`` maps, built for the CEL (Desolo Zantas) mod, with live game link and AI (MCP/ACP) tools.

$(if ($changes) { "### What's new`n`n$changes`n" })
**Downloads**
- ``MT_Zenith-$Version-win-x64.zip``: self-contained, just unzip and run ``MT_Zenith.exe``.
- ``MT_Zenith-$Version-win-x64-fxdependent.zip``: smaller, needs the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0).

Needs Celeste with Everest; MT_Zenith finds it automatically (Steam/Olympus/common paths) or via *File > Set Celeste folder...*. See README.txt in the zip.

**SHA-256**
``````
$($sums -join "`n")
``````
"@
$notes | Set-Content (Join-Path $dist 'release-notes.md') -Encoding utf8

$gb = @"
MT_Zenith ${Version}: map editor

MT_Zenith (formerly CEL Map Viewer) is a standalone Windows map viewer/editor for Celeste .bin maps, made for the CEL mod.

Features
- Renders rooms with the game's own autotiler, decals and Loenn entity plugins (vanilla, CEL and installed helpers)
- Room, tile, entity, trigger and decal editing with undo, copy/paste between windows, room resizing tiers
- Live link to a running Celeste (Everest debug mode): teleport to the selected room, reload the map on save, read log.txt
- AI tools: built-in MCP server (mt-zenith) and an ACP agent panel (Claude, Codex, Copilot, Gemini...) that can edit the open map and analyze the mod's images, audio and video

Install
1. Install Celeste + Everest (via Olympus). Install CEL for CEL maps.
2. Unzip MT_Zenith anywhere and run MT_Zenith.exe.
3. If your game isn't found automatically: File > Set Celeste folder..., then restart.
The plain zip is self-contained; the "fxdependent" zip needs the .NET 8 Desktop Runtime.
Optional: ffmpeg on PATH for video frames / audio decoding.

This is a tool, not a mod: do not put it in Celeste/Mods.
"@
$gb | Set-Content (Join-Path $dist 'gamebanana.txt') -Encoding utf8

Write-Host ""
Get-ChildItem $dist | Format-Table Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } -AutoSize
