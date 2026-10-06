# Windows installer build: publish the app into .\publish\win-x64, generate the demo parts into .\publish\demos,
# then compile installer\feathercad.iss into
#   publish\FeatherCAD_setup_v<version>.exe
# Needs Inno Setup 6 (https://jrsoftware.org/isdl.php, or: winget install JRSoftware.InnoSetup), and the Visual Studio
# C++ build tools for the Explorer thumbnail handler (see build\make-thumbs.ps1).
# Run from anywhere:  pwsh -File build\make-installer.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'src\FeatherCad.App\FeatherCad.App.csproj'

# 1) Version, always from git: the latest v<x.y.z> tag, and how many commits HEAD is past it. On the tag: 0.0.1;
#    3 commits after it: 0.0.1-dev.3. Windows file versions are four numbers: 0.0.1.3.
$d = git -C $root describe --tags --match 'v[0-9]*' --long 2>$null
if ($LASTEXITCODE -ne 0 -or $d -notmatch '^v(\d+\.\d+\.\d+)-(\d+)-g[0-9a-f]+$') {
  throw "No version: no v<x.y.z> tag is reachable from HEAD (tag a commit, or fetch the tags / full history)."
}
$dev = [int]$Matches[2]
$ver = if ($dev -eq 0) { $Matches[1] } else { "$($Matches[1])-dev.$dev" }
$verNum = "$($Matches[1]).$dev"
Write-Host "== version $ver (file version $verNum) ==" -ForegroundColor Cyan

# 2) Locate the Inno Setup compiler (ISCC.exe) before spending time on the publish: PATH, then the standard
#    install locations (machine-wide, and the per-user install winget uses).
$iscc = (Get-Command 'iscc.exe' -ErrorAction SilentlyContinue | Select-Object -First 1).Source
if (-not $iscc) {
  foreach ($p in @(
      "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
      "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
      "${env:LOCALAPPDATA}\Programs\Inno Setup 6\ISCC.exe")) {
    if (Test-Path $p) { $iscc = $p; break }
  }
}
if (-not $iscc) {
  throw "Inno Setup compiler (ISCC.exe) not found. Install it from https://jrsoftware.org/isdl.php, or add it to PATH."
}
# ... and the C++ tools for the thumbnail handler, also before the publish (make-thumbs.ps1 explains how to get them)
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not ((Test-Path $vswhere) -and (& $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath))) {
  throw "Visual Studio C++ build tools not found (needed for the thumbnail handler). See build\make-thumbs.ps1."
}

# 3) Publish self-contained and ReadyToRun (as publish.ps1 does) into a clean folder, so nothing from an older
#    build ends up in the installer.
$out = Join-Path $root 'publish\win-x64'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
Write-Host "== publishing to $out ==" -ForegroundColor Cyan
& dotnet publish $proj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -p:DebugType=none `
    "-p:Version=$ver" "-p:FileVersion=$verNum" -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

# 3b) The Explorer thumbnail handler, next to the app (the installer registers it for .fcad files).
& (Join-Path $PSScriptRoot 'make-thumbs.ps1') -Out $out

# 4) Generate the parametric demo parts fresh (tests/FeatherCad.Tests/Demos.cs), so the installer ships the
#    current set rather than whatever was last written into samples\demos.
$demos = Join-Path $root 'publish\demos'
if (Test-Path $demos) { Remove-Item $demos -Recurse -Force }
Write-Host "== generating demo parts into $demos ==" -ForegroundColor Cyan
& dotnet run --project (Join-Path $root 'tests\FeatherCad.Tests') -c Release -- demos $demos
if ($LASTEXITCODE -ne 0) { throw "demo generation failed with exit code $LASTEXITCODE" }

# 5) Compile.
$iss = Join-Path $root 'installer\feathercad.iss'
Write-Host "== compiling installer ($iscc) ==" -ForegroundColor Cyan
& $iscc "/DAppVer=$ver" "/DAppVerNum=$verNum" $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }

Write-Host "== installer ready -> $(Join-Path $root "publish\FeatherCAD_setup_v$ver.exe") ==" -ForegroundColor Green
