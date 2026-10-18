[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ToolRoot
)

$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath($ToolRoot)
[System.IO.Directory]::CreateDirectory($root) | Out-Null
$download = Join-Path $root 'innosetup-7.1.0-x64.exe'
$destination = Join-Path $root 'inno-7.1.0'
$url = 'https://github.com/jrsoftware/issrc/releases/download/is-7_1_0/innosetup-7.1.0-x64.exe'
# SHA256 published on the immutable official GitHub release (is-7_1_0).
$expectedHash = '0362a383ed217d4c4239b5933866dd96d3eb2102737da92f80f6057a4b40df2f'
if (-not (Test-Path -LiteralPath $download)) {
    Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $download
}
if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedHash) {
    throw 'Inno Setup download SHA256 does not match the official release.'
}
$signature = Get-AuthenticodeSignature -LiteralPath $download
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'CN=Pyrsys B\.V\.(,|$)') {
    throw 'Inno Setup download must have a valid Pyrsys B.V. Authenticode signature.'
}
# Official portable mode writes only this tool folder: no uninstall registration,
# file associations, Start menu entries, desktop shortcuts or PATH changes.
$arguments = @('/PORTABLE=1', '/CURRENTUSER', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/NOICONS', '/TASKS=""', "/DIR=`"$destination`"")
$process = Start-Process -FilePath $download -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -ne 0) { throw "Portable Inno Setup preparation failed ($($process.ExitCode))." }
$compiler = Join-Path $destination 'ISCC.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Portable compiler was not created.' }
Write-Output $compiler
