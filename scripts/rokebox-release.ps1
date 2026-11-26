<#
.SYNOPSIS
    Builds a RokeBox release (signed APK, Windows agent zip, SHA256SUMS) and optionally publishes it on GitHub.

.DESCRIPTION
    Output goes to dist\release-<version>\ with the names the agent's updater looks for:
        RokeBox-<version>.apk
        RokeBoxAgent-<version>-win-x64.zip
        SHA256SUMS-<version>.txt
    The APK must be signed with RokeBox's own key (ROKEBOX_STORE_FILE, ROKEBOX_STORE_PASSWORD, ROKEBOX_KEY_ALIAS,
    ROKEBOX_KEY_PASSWORD in %USERPROFILE%\.gradle\gradle.properties or the environment); an APK signed with the debug key
    stops the script, because phones that installed it could not take the next update.

    Needs JDK 17 (JAVA_HOME), the Android SDK (ANDROID_HOME, or the default %LOCALAPPDATA%\Android\Sdk), the .NET 10 SDK
    (dotnet on PATH) and, with -Publish, the GitHub CLI (gh) signed in with `gh auth login`.

.EXAMPLE
    .\scripts\rokebox-release.ps1             # build only
    .\scripts\rokebox-release.ps1 -Publish    # build, then create the GitHub release v<version> from origin/main
#>
param(
    [switch]$Publish,
    [string]$Repo = 'kervinr-crypto/RokeBox',
    [string]$Dotnet = 'dotnet',
    [string]$Gh = 'gh'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$version = ([xml](Get-Content (Join-Path $root 'agent\PhoneBox.Agent\PhoneBox.Agent.csproj') -Raw)).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1
$gradle = Get-Content (Join-Path $root 'android\app\build.gradle.kts') -Raw
if ($gradle -notmatch "versionName = `"$([regex]::Escape($version))`"") {
    throw "android\app\build.gradle.kts versionName does not match the agent's version $version"
}

$sdk = if ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { Join-Path $env:LOCALAPPDATA 'Android\Sdk' }
$env:ANDROID_HOME = $sdk
$apksigner = Get-ChildItem (Join-Path $sdk 'build-tools') -Directory | Sort-Object { [version]$_.Name } -Descending |
    ForEach-Object { Join-Path $_.FullName 'apksigner.bat' } | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $apksigner) { throw "apksigner not found under $sdk\build-tools" }

$out = Join-Path $root "dist\release-$version"
New-Item -ItemType Directory -Force $out | Out-Null
$apk = Join-Path $out "RokeBox-$version.apk"
$zip = Join-Path $out "RokeBoxAgent-$version-win-x64.zip"
$sums = Join-Path $out "SHA256SUMS-$version.txt"

Write-Host "==> APK $version"
Push-Location (Join-Path $root 'android')
try {
    & .\gradlew.bat assembleRelease --console=plain -q
    if ($LASTEXITCODE) { throw 'gradle assembleRelease failed' }
} finally { Pop-Location }
Copy-Item (Join-Path $root 'android\app\build\outputs\apk\release\app-release.apk') $apk -Force
$certs = (& $apksigner verify --print-certs $apk) -join "`n"
if ($LASTEXITCODE) { throw "apksigner could not verify $apk" }
if ($certs -match 'CN=Android Debug') {
    throw 'The APK is signed with the debug key: set ROKEBOX_STORE_FILE and the other ROKEBOX_* values (see ROKEBOX.md)'
}

Write-Host "==> Agent $version win-x64"
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("rokebox-agent-$version-" + [guid]::NewGuid().ToString('N'))
try {
    & $Dotnet publish (Join-Path $root 'agent\PhoneBox.Agent') -c Release -f net10.0-windows10.0.19041.0 -r win-x64 `
        --self-contained true -p:PublishSingleFile=true -o $tmp --nologo -v q --disable-build-servers
    if ($LASTEXITCODE) { throw 'dotnet publish failed' }
    if (-not (Test-Path (Join-Path $tmp 'RokeBoxAgent.exe'))) { throw 'RokeBoxAgent.exe is missing from the publish output' }
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path "$tmp\*" -DestinationPath $zip
} finally {
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

# "sha256sum" format (<hex><two spaces><name>), which the agent's updater reads
$lines = foreach ($file in @($apk, $zip)) {
    (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + (Split-Path $file -Leaf)
}
[IO.File]::WriteAllText($sums, ($lines -join "`n") + "`n")
Get-ChildItem $out | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize

if ($Publish) {
    Write-Host "==> GitHub release v$version on $Repo"
    $notes = "RokeBox $version.`n`nDownload RokeBoxAgent-$version-win-x64.zip (unzip, run RokeBoxAgent.exe) and RokeBox-$version.apk " +
        "(install on the phone). Agents that are already installed update themselves and the phone app. " +
        "Checksums: SHA256SUMS-$version.txt."
    & $Gh release create "v$version" $apk $zip $sums --repo $Repo --target main --title "RokeBox $version" --notes $notes
    if ($LASTEXITCODE) { throw 'gh release create failed' }
}
