param([string]$Output = "$PSScriptRoot/.deps/publish")

$ErrorActionPreference = 'Stop'
$dotnet = "$PSScriptRoot/.deps/dotnet/dotnet.exe"
if (!(Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source }
Push-Location -LiteralPath $PSScriptRoot
try {
    & $dotnet publish whit.csproj -c Release -o $Output --nologo
    if ($LASTEXITCODE) { throw 'dotnet publish failed.' }
} finally { Pop-Location }
