# Build the Ask extension, register the package, restart Command Palette so it
# picks the new build up. No Visual Studio needed: the Windows SDK build tools
# come from NuGet (see AskExtension.csproj). Needs Developer Mode on.
$ErrorActionPreference = 'Stop'
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
$proj = Join-Path $PSScriptRoot 'AskExtension\AskExtension.csproj'

# The running extension locks its exe; stop it before building over it.
Get-Process AskExtension -ErrorAction SilentlyContinue | Stop-Process -Force

& $dotnet build $proj -c Debug -p:Platform=x64 --nologo -v:minimal
if ($LASTEXITCODE -ne 0) { throw "build failed ($LASTEXITCODE)" }

$manifest = Get-ChildItem (Join-Path $PSScriptRoot 'AskExtension\bin\x64\Debug') -Recurse -Filter AppxManifest.xml |
    Select-Object -First 1
try {
    Add-AppxPackage -Register $manifest.FullName -ForceUpdateFromAnyVersion
} catch {
    # 0x80073CFB: a manifest change (capabilities, identity) can't be re-registered over
    # the dev package of the same version. Remove it and register fresh.
    Get-AppxPackage BenSmart.AskExtension | Remove-AppxPackage
    Add-AppxPackage -Register $manifest.FullName
}

# Same effect as the palette's "Reload" command, which can't be triggered from outside
# while settings.json has AllowExternalReload = false.
Get-Process Microsoft.CmdPal.UI -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800
Start-Process 'shell:AppsFolder\Microsoft.CommandPalette_8wekyb3d8bbwe!App'
Write-Host 'Deployed; Command Palette restarted.'
