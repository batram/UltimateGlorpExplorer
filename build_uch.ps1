# Builds the BepInEx5 Mono config (the only one used for UCH) and merges
# everything into a single self-contained DLL via ILRepack.
$ErrorActionPreference = "Stop"

dotnet build src/UnityExplorer.csproj -c BIE5_Mono
if ($LASTEXITCODE -ne 0) { throw "build failed" }

$Path = "Release/UnityExplorer.BepInEx5.Mono"

lib/ILRepack.exe /target:library /lib:lib/net35 /lib:$Path /internalize `
    /out:$Path/UltimateGlorpExplorer.BIE5.Mono.dll `
    $Path/UltimateGlorpExplorer.BIE5.Mono.dll `
    $Path/UniverseLib.Mono.dll `
    $Path/mcs.dll `
    $Path/Tomlet.dll `
    $Path/Newtonsoft.Json.dll
if ($LASTEXITCODE -ne 0) { throw "ILRepack failed" }

Remove-Item $Path/UniverseLib.Mono.dll
Remove-Item $Path/mcs.dll
Remove-Item $Path/Tomlet.dll
Remove-Item $Path/Newtonsoft.Json.dll

Write-Output "Merged single DLL: $Path/UltimateGlorpExplorer.BIE5.Mono.dll"
