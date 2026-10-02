# Publishes the backend as a self-contained single file into bin\<rid>\, where the plugin looks for it.
param([string]$Rid = "win-x64")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
dotnet publish "$root/backend/src/Dbbliss.Backend" -c Release -r $Rid `
  --self-contained true -p:PublishSingleFile=true -o "$root/bin/$Rid" --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
