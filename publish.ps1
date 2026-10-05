# Builds self-contained, ReadyToRun-compiled FeatherCAD for the given platforms into ./publish/<rid>
# Usage:  ./publish.ps1                 (current OS)
#         ./publish.ps1 win-x64 linux-x64 osx-arm64 osx-x64
param([string[]]$Rids)
$ErrorActionPreference = "Stop"
if (-not $Rids -or $Rids.Count -eq 0) {
    if ($IsLinux) { $Rids = @("linux-x64") } elseif ($IsMacOS) { $Rids = @("osx-arm64") } else { $Rids = @("win-x64") }
}
foreach ($rid in $Rids) {
    Write-Host "Publishing $rid ..."
    dotnet publish "$PSScriptRoot/src/FeatherCad.App/FeatherCad.App.csproj" -c Release -r $rid --self-contained true `
        -p:PublishReadyToRun=true `
        -p:DebugType=none -o "$PSScriptRoot/publish/$rid"
}
