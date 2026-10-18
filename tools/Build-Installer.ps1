[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$IsccPath,
    [string]$BuildRoot = (Join-Path $PSScriptRoot '../artifacts/installer')
)

$ErrorActionPreference = 'Stop'
$repo = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$compiler = (Resolve-Path -LiteralPath $IsccPath).Path
$build = [System.IO.Path]::GetFullPath($BuildRoot)
# A new directory prevents stale files or local application data entering the installer.
$publish = Join-Path $build ('staging/' + [guid]::NewGuid().ToString('N'))
$output = Join-Path $build 'output'
[System.IO.Directory]::CreateDirectory($publish) | Out-Null
[System.IO.Directory]::CreateDirectory($output) | Out-Null
[xml]$project = Get-Content -LiteralPath (Join-Path $repo 'src/Typr.Windows/Typr.Windows.csproj') -Raw
$version = [string]$project.Project.PropertyGroup.Version
$fileVersion = [string]$project.Project.PropertyGroup.FileVersion
if ($version -notmatch '^\d+\.\d+\.\d+(?:[-+][A-Za-z0-9.-]+)?$' -or $fileVersion -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw 'The application project must define a semantic Version and four-part FileVersion.'
}
& dotnet publish (Join-Path $repo 'src/Typr.Windows/Typr.Windows.csproj') -c Release -r win-x64 --self-contained true --artifacts-path (Join-Path $build 'artifacts') -m:1 -p:BuildInParallel=false -p:PublishSingleFile=false -p:IncludeNativeLibrariesForSelfExtract=false -p:IncludeAllContentForSelfExtract=false -p:EnableCompressionInSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }
foreach ($required in @('Typr.exe', 'Typr.dll', 'Typr.runtimeconfig.json', 'coreclr.dll', 'onnxruntime.dll', 'sherpa-onnx-c-api.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publish $required))) { throw "Missing publish dependency: $required" }
}
$runtimeConfig = Get-Content -LiteralPath (Join-Path $publish 'Typr.runtimeconfig.json') -Raw | ConvertFrom-Json
$runtimeVersion = [string]$project.Project.PropertyGroup.RuntimeFrameworkVersion
foreach ($framework in @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')) {
    $included = @($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object name -eq $framework)
    if ($included.Count -ne 1 -or $included[0].version -ne $runtimeVersion) {
        throw "Published $framework does not match the pinned runtime $runtimeVersion."
    }
}
$foreignNative = Get-ChildItem -LiteralPath $publish -Recurse -File | Where-Object {
    $_.Extension -in '.so', '.dylib' -or $_.FullName -match '[\\/]runtimes[\\/](?!win-x64[\\/])'
}
if ($foreignNative) { throw 'Publish contains native assets for another OS or architecture.' }
$previousTemp = $env:TEMP
$previousTmp = $env:TMP
try {
    # Keep the compression workspace on the chosen build volume as well.
    $env:TEMP = Join-Path $build 'temp'
    $env:TMP = $env:TEMP
    [System.IO.Directory]::CreateDirectory($env:TEMP) | Out-Null
    & $compiler /Q "/DPublishDir=$publish" "/DOutputDir=$output" "/DAppVersion=$version" "/DFileVersion=$fileVersion" (Join-Path $repo 'installer/Typr.iss')
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed ($LASTEXITCODE)." }
}
finally {
    $env:TEMP = $previousTemp
    $env:TMP = $previousTmp
}
$installer = Join-Path $output "Typr-$version-win-x64-Setup.exe"
$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText("$installer.sha256", "$hash  $([System.IO.Path]::GetFileName($installer))`n")
Write-Output "Installer: $installer"
Write-Output "SHA256: $hash"
Write-Output "Published files: $publish"
