param(
    [version]$Version = '1.0.5.0',
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Fa-f]{40}$')][string]$Thumbprint,
    [uri]$Timestamp,
    [string]$Output = "$PSScriptRoot/dist"
)

$ErrorActionPreference = 'Stop'
if ($Version.Revision -lt 0 -or $Version.Major -lt 1 -or
    ($Version.ToString().Split('.') | Where-Object { [int]$_ -gt 65535 })) {
    throw 'Use a four-part MSIX version.'
}
$cert = Get-Item -LiteralPath "Cert:/CurrentUser/My/$Thumbprint"
if (!$cert.HasPrivateKey) { throw 'The certificate needs a signing key.' }
$kits = (Get-ItemProperty 'HKLM:/SOFTWARE/Microsoft/Windows Kits/Installed Roots').KitsRoot10
$kit = Get-ChildItem -LiteralPath "$kits/bin" -Directory | Where-Object {
    (Test-Path -LiteralPath "$($_.FullName)/x64/makeappx.exe") -and
    (Test-Path -LiteralPath "$($_.FullName)/x64/signtool.exe")
} | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$kit) { throw 'Install the Windows SDK packaging tools.' }

$directory = (New-Item -ItemType Directory -Path $Output -Force).FullName
$filename = 'whit_{0}_x64.msix' -f $Version
$destination = Join-Path $directory $filename
if (Test-Path -LiteralPath $destination) { throw 'This release version already exists.' }
$work = New-Item -ItemType Directory -Path "$PSScriptRoot/.deps/$([guid]::NewGuid())" -Force
try {
    $build = Join-Path $work.FullName 'build'
    & "$PSScriptRoot/build.ps1" -Output $build
    $payload = (New-Item -ItemType Directory -Path "$($work.FullName)/payload").FullName
    $manifest = [xml](Get-Content -Raw -LiteralPath "$PSScriptRoot/Package.appxmanifest")
    $manifest.Package.Identity.Publisher = $cert.Subject
    $manifest.Package.Identity.Version = $Version.ToString()
    $manifest.Save("$payload/AppxManifest.xml")
    $files = 'whit.exe','WebView2Loader.dll','Microsoft.Web.WebView2.Core.dll','resources.pri','App.xbf'
    Copy-Item -LiteralPath ($files | ForEach-Object { Join-Path $build $_ }) -Destination $payload
    Copy-Item -LiteralPath "$PSScriptRoot/Assets" -Destination $payload -Recurse
    $restore = Get-Content -Raw -LiteralPath "$PSScriptRoot/obj/project.assets.json" | ConvertFrom-Json -AsHashtable
    $webView = $restore.libraries.Values | Where-Object { $_.path -like 'microsoft.web.webview2/*' } | Select-Object -First 1
    $runtime = $restore.project.frameworks.Values.downloadDependencies |
        Where-Object { $_.name -eq 'Microsoft.NETCore.App.Runtime.NativeAOT.win-x64' } | Select-Object -First 1
    $runtimeVersion = $runtime.version.TrimStart('[').Split(',')[0]
    foreach ($package in $webView.path, "$($runtime.name.ToLowerInvariant())/$runtimeVersion") {
        $root = $restore.packageFolders.Keys | Where-Object { Test-Path -LiteralPath (Join-Path $_ $package) } | Select-Object -First 1
        $licenses = New-Item -ItemType Directory -Path "$payload/Licenses/$(Split-Path -Parent $package)" -Force
        Get-ChildItem -LiteralPath (Join-Path $root $package) -File |
            Where-Object { $_.Name -match '^(LICENSE|.*NOTICE.*)\.txt$' } |
            Copy-Item -Destination $licenses.FullName
    }
    $package = "$($work.FullName)/$filename"
    $log = & "$($kit.FullName)/x64/makeappx.exe" pack /d $payload /p $package /no
    if ($LASTEXITCODE) { throw ($log -join [Environment]::NewLine) }
    $sign = @('sign','/fd','SHA256','/sha1',$Thumbprint)
    if ($Timestamp) { $sign += @('/tr',$Timestamp.AbsoluteUri,'/td','SHA256') }
    & "$($kit.FullName)/x64/signtool.exe" @sign $package
    if ($LASTEXITCODE) { throw 'SignTool failed.' }
    [IO.File]::Move($package, $destination)
    Get-Item -LiteralPath $destination
} finally {
    $parent = (Resolve-Path -LiteralPath "$PSScriptRoot/.deps").Path
    $target = (Resolve-Path -LiteralPath $work.FullName -ErrorAction SilentlyContinue).Path
    if ($target -eq $work.FullName -and (Split-Path -Parent $target) -eq $parent) {
        Remove-Item -LiteralPath $target -Recurse -Force
    }
}
