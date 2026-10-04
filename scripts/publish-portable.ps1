param(
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64',
    [ValidatePattern('^[a-zA-Z0-9.-]*$')][string]$ReleaseLabel = ''
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskSuffix = if ($ReleaseLabel) { "-$ReleaseLabel" } else { '' }
$taskOutput = Join-Path $taskRoot "artifacts\portable-$Runtime$taskSuffix"
dotnet publish (Join-Path $taskRoot 'src\WebScrapper.WinForms\WebScrapper.WinForms.csproj') -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false -p:DebugType=none -p:DebugSymbols=false -p:CopyOutputSymbolsToPublishDirectory=false -o $taskOutput
if ($LASTEXITCODE -ne 0) { throw 'Publish gagal.' }
Copy-Item -LiteralPath (Join-Path $taskRoot 'README.md') -Destination $taskOutput
Copy-Item -LiteralPath (Join-Path $taskRoot 'THIRD-PARTY-NOTICES.md') -Destination $taskOutput
$taskLicenses = Join-Path $taskOutput 'licenses'
New-Item -ItemType Directory -Path $taskLicenses -Force | Out-Null
$taskAssets = Get-Content -LiteralPath (Join-Path $taskRoot 'src\WebScrapper.WinForms\obj\project.assets.json') -Raw | ConvertFrom-Json
$taskPackages = $taskAssets.project.restore.packagesPath
$taskWebView = $taskAssets.libraries.PSObject.Properties | Where-Object { $_.Name -like 'Microsoft.Web.WebView2/*' } | Select-Object -First 1
if ($taskWebView) {
    $taskWebViewPath = Join-Path $taskPackages $taskWebView.Value.path
    foreach ($taskNoticeName in @('LICENSE.txt', 'NOTICE.txt')) {
        Copy-Item -LiteralPath (Join-Path $taskWebViewPath $taskNoticeName) -Destination (Join-Path $taskLicenses ('WebView2-' + $taskNoticeName))
    }
}
foreach ($taskFramework in $taskAssets.project.frameworks.PSObject.Properties.Value) {
  foreach ($taskDependency in $taskFramework.downloadDependencies) {
    if ($taskDependency.name -match "(?i)^Microsoft\.(NETCore|WindowsDesktop)\.App\.Runtime\.$([regex]::Escape($Runtime))$") {
        $taskPackVersion = $taskDependency.version.Trim('[', ']').Split(',')[0].Trim()
        $taskPack = Join-Path (Join-Path $taskPackages $taskDependency.name.ToLowerInvariant()) $taskPackVersion
        foreach ($taskNotice in Get-ChildItem -LiteralPath $taskPack -File | Where-Object { $_.Name -match '^(LICENSE|THIRD-PARTY-NOTICES)(\.TXT)?$' }) {
            Copy-Item -LiteralPath $taskNotice.FullName -Destination (Join-Path $taskLicenses ($taskDependency.name + '-' + $taskNotice.Name + '.txt'))
        }
    }
  }
}
if (@(Get-ChildItem -LiteralPath $taskLicenses -File).Count -lt 2) { throw 'Runtime licenses tidak ditemukan.' }
$taskZip = Join-Path $taskRoot "artifacts\WebScrapper-portable-$Runtime$taskSuffix.zip"
Compress-Archive -LiteralPath (Join-Path $taskOutput 'WebScrapper.exe'), (Join-Path $taskOutput 'README.md'), (Join-Path $taskOutput 'THIRD-PARTY-NOTICES.md'), $taskLicenses -DestinationPath $taskZip -Force
$taskHash = (Get-FileHash -LiteralPath $taskZip -Algorithm SHA256).Hash
Set-Content -LiteralPath ($taskZip + '.sha256') -Value "$taskHash  $([IO.Path]::GetFileName($taskZip))" -Encoding utf8
Write-Output "EXE: $(Join-Path $taskOutput 'WebScrapper.exe')"
Write-Output "ZIP: $taskZip"
