# Packs the three.js files the render tool serves offline into src/Sloperator.Core/Render/three.zip:
# build/three.module.js and three.core.js, and the WebGL add-ons (examples/jsm) a scene may import.
# Left out: WebGPU and TSL, the inspector, WebXR, and libraries that fetch WebAssembly (Draco, Basis);
# a page that imports those gets them from the web as it would in any browser.
#   ./scripts/update-three.ps1 [-Version 0.187.0]
param([string]$Version = "0.187.0")
$ErrorActionPreference = "Stop"

$root = Split-Path $PSScriptRoot -Parent
$work = Join-Path ([IO.Path]::GetTempPath()) "sloperator-three-$Version"
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $work | Out-Null
$tgz = Join-Path $work "three.tgz"
Invoke-WebRequest "https://registry.npmjs.org/three/-/three-$Version.tgz" -OutFile $tgz -UseBasicParsing
tar -xzf $tgz -C $work
$package = Join-Path $work "package"

$skipFolders = @("tsl", "inspector", "webxr", "transpiler", "offscreen", "physics", "capabilities\WebGPU.js")
$keepLibs = @("fflate.module.js", "potpack.module.js", "tween.module.js", "mikktspace.module.js", "chevrotain.module.min.js", "utif.module.js", "zstddec.module.js", "ktx-parse.module.js")
$out = Join-Path $work "zip"
New-Item -ItemType Directory $out | Out-Null
Copy-Item (Join-Path $package "LICENSE") $out
New-Item -ItemType Directory (Join-Path $out "build") | Out-Null
Copy-Item (Join-Path $package "build\three.module.js"), (Join-Path $package "build\three.core.js") (Join-Path $out "build")

$jsm = Join-Path $package "examples\jsm"
Get-ChildItem $jsm -Recurse -File | ForEach-Object {
    $relative = $_.FullName.Substring($jsm.Length + 1)
    $top = $relative.Split('\')[0]
    if ($skipFolders -contains $top -or $skipFolders -contains $relative) { return }
    if ($top -eq "libs" -and -not ($keepLibs -contains $relative.Substring(5))) { return }
    # WebGPU-only add-ons import three/webgpu or three/tsl, which aren't packed.
    if ($_.Extension -eq ".js" -and (Select-String -Path $_.FullName -Pattern "from 'three/(webgpu|tsl)'" -Quiet)) { return }
    if ($_.Extension -notin ".js", ".json") { return }
    $target = Join-Path $out ("examples\jsm\" + $relative)
    New-Item -ItemType Directory (Split-Path $target) -Force | Out-Null
    Copy-Item $_.FullName $target
}
Set-Content (Join-Path $out "VERSION") $Version -NoNewline

$zip = Join-Path $root "src\Sloperator.Core\Render\three.zip"
New-Item -ItemType Directory (Split-Path $zip) -Force | Out-Null
Remove-Item $zip -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($out, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
$files = (Get-ChildItem $out -Recurse -File).Count
"three.js $Version`: $files files, {0:N0} KB packed" -f ((Get-Item $zip).Length / 1KB)
Remove-Item $work -Recurse -Force
