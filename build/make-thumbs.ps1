# Builds the Explorer thumbnail handler (shell\FeatherCadThumbs) into <Out>\FeatherCadThumbs.dll, x64.
# Needs the Visual Studio C++ build tools (the "Desktop development with C++" workload, or:
#   winget install Microsoft.VisualStudio.2022.BuildTools --override "--quiet --wait --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"
# ). The C runtime is linked statically, so the DLL needs no Visual C++ redistributable on users' PCs.
# Run from anywhere:  pwsh -File build\make-thumbs.ps1 [-Out publish\thumbs]
# To try a build without the installer:  regsvr32 <Out>\FeatherCadThumbs.dll   (current user; /u to remove)
param([string]$Out)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Out) { $Out = Join-Path $root 'publish\thumbs' }
$src = Join-Path $root 'shell\FeatherCadThumbs'

# The C++ tools: vswhere (installed with any Visual Studio or Build Tools) finds an install with the x64 compiler.
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = if (Test-Path $vswhere) {
  & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
}
if (-not $vs) {
  throw "Visual Studio C++ build tools not found (needed for the thumbnail handler). Install them with:`n" +
        "  winget install Microsoft.VisualStudio.2022.BuildTools --override `"--quiet --wait --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended`""
}
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'

New-Item -ItemType Directory -Force $Out | Out-Null
$obj = Join-Path ([IO.Path]::GetTempPath()) 'feathercad-thumbs-obj'
New-Item -ItemType Directory -Force $obj | Out-Null
$dll = Join-Path $Out 'FeatherCadThumbs.dll'
Write-Host "== building $dll ==" -ForegroundColor Cyan
# /MT: static C runtime; /W4 /WX keeps it warning-free
$cl = "cl /nologo /O2 /MT /EHsc /W4 /WX /permissive- /std:c++17 /DUNICODE /D_UNICODE /LD " +
      "/Fo`"$obj\\`" `"$src\FeatherCadThumbs.cpp`" /link /DEF:`"$src\FeatherCadThumbs.def`" /OUT:`"$dll`" /NOIMPLIB /NOEXP"
cmd /c "call `"$vcvars`" >nul && $cl"
if ($LASTEXITCODE -ne 0) { throw "cl failed with exit code $LASTEXITCODE" }
Write-Host "== thumbnail handler ready -> $dll ==" -ForegroundColor Green
