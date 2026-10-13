# Publishes each layer test app as a self-contained single file.
param([string[]]$Rids = @('win-x64', 'linux-x64'))
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
foreach ($rid in $Rids) {
  foreach ($layer in 'Core', 'Application', 'Infrastructure', 'Presentation', 'Monitoring') {
    dotnet publish "tools/ATM.TestApp.$layer" -c Release -r $rid --self-contained `
      -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
      -o "artifacts/testapps/$rid"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
  }
}
foreach ($rid in $Rids) {
  dotnet publish tools/ATM.FieldTest -c Release -r $rid --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -o "artifacts/fieldtest/$rid"
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
