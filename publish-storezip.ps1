# Produces self-contained, single-file builds of Eggman's StoreZip for
# Windows (zip), Linux (tar.gz) and macOS (an .app bundle in a tar.gz) in
# dist\. The tar files keep the executable bit, which a zip made on Windows
# can't. macOS builds made here are unsigned: see README-StoreZip.txt for the
# one-time step on a Mac.
param(
    [string[]]$Runtimes = @("win-x64", "linux-x64", "osx-arm64", "osx-x64"),
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

[xml]$proj = Get-Content (Join-Path $PSScriptRoot "src\StoreZip.App\StoreZip.App.csproj")
$version = ($proj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
$readme = Join-Path $PSScriptRoot "src\StoreZip.App\README-StoreZip.txt"
$spec = Join-Path $PSScriptRoot "docs\StoreZip-Format.md"

# A tar.gz of a folder; files named in $executables get mode 755
function Write-TarGz([string]$folder, [string]$destination, [string[]]$executables) {
    if (Test-Path $destination) { Remove-Item $destination }
    $out = [IO.File]::Create($destination)
    try {
        $gz = [IO.Compression.GZipStream]::new($out, [IO.Compression.CompressionLevel]::Optimal)
        $tar = [Formats.Tar.TarWriter]::new($gz, [Formats.Tar.TarEntryFormat]::Pax, $false)
        $root = (Get-Item $folder).Parent.FullName
        foreach ($item in Get-ChildItem $folder -Recurse | Sort-Object FullName) {
            $name = $item.FullName.Substring($root.Length + 1).Replace('\', '/')
            if ($item.PSIsContainer) {
                $entry = [Formats.Tar.PaxTarEntry]::new([Formats.Tar.TarEntryType]::Directory, $name + "/")
                $entry.Mode = [IO.UnixFileMode]493
                $tar.WriteEntry($entry)
                continue
            }
            $entry = [Formats.Tar.PaxTarEntry]::new([Formats.Tar.TarEntryType]::RegularFile, $name)
            $entry.Mode = if ($executables -contains $item.Name) { [IO.UnixFileMode]493 } else { [IO.UnixFileMode]420 }
            $data = [IO.File]::OpenRead($item.FullName)
            try {
                $entry.DataStream = $data
                $tar.WriteEntry($entry)
            } finally {
                $data.Dispose()
            }
        }
        $tar.Dispose()
        $gz.Dispose()
    } finally {
        $out.Dispose()
    }
}

foreach ($rid in $Runtimes) {
    $output = Join-Path $PSScriptRoot "dist\storezip-$rid"
    if (Test-Path $output) { Remove-Item -Recurse -Force $output }

    dotnet publish (Join-Path $PSScriptRoot "src\StoreZip.App\StoreZip.App.csproj") `
        -c $Configuration -r $rid --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none -p:DebugSymbols=false `
        -o $output
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $package = "EggmansStoreZip-v$version-$rid"
    $stage = Join-Path $PSScriptRoot "dist\$package"
    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }

    if ($rid -like "osx-*") {
        # Finder needs an app bundle: Contents/MacOS holds the program, Info.plist names it
        $macos = Join-Path $stage "Eggman's StoreZip.app\Contents\MacOS"
        New-Item -ItemType Directory -Force $macos | Out-Null
        Copy-Item (Join-Path $output "*") $macos -Recurse
        @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>Eggman's StoreZip</string>
    <key>CFBundleDisplayName</key><string>Eggman's StoreZip</string>
    <key>CFBundleIdentifier</key><string>com.eggmansworld.storezip</string>
    <key>CFBundleExecutable</key><string>EggmansStoreZip</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleVersion</key><string>$version</string>
    <key>CFBundleShortVersionString</key><string>$version</string>
    <key>LSMinimumSystemVersion</key><string>12.0</string>
    <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
"@ | Set-Content -Encoding utf8NoBOM (Join-Path $stage "Eggman's StoreZip.app\Contents\Info.plist")
    } else {
        New-Item -ItemType Directory -Force $stage | Out-Null
        Copy-Item (Join-Path $output "*") $stage -Recurse
    }
    Copy-Item $readme $stage
    Copy-Item $spec $stage

    if ($rid -like "win-*") {
        $zip = Join-Path $PSScriptRoot "dist\$package.zip"
        if (Test-Path $zip) { Remove-Item $zip }
        Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip
        Write-Host "Package: $zip"
    } else {
        $tgz = Join-Path $PSScriptRoot "dist\$package.tar.gz"
        Write-TarGz $stage $tgz @("EggmansStoreZip")
        Write-Host "Package: $tgz"
    }
    Remove-Item -Recurse -Force $stage
}

Write-Host ""
Write-Host "Published Eggman's StoreZip v$version for: $($Runtimes -join ', ')"
