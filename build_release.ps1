Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectFile = Join-Path $projectRoot "src\FeishuMinutes\FeishuMinutes.csproj"
$sourceOutput = Join-Path $projectRoot "src\FeishuMinutes\bin\Release"
$sourceExe = Join-Path $sourceOutput "妙记字幕下载器.exe"
$buildRoot = Join-Path $projectRoot ".native-build"
$referencePackage = Join-Path $buildRoot "net481.nupkg"
$referenceExtract = Join-Path $buildRoot "net481"
$frameworkRoot = Join-Path $referenceExtract "build\"
$releaseRoot = Join-Path $projectRoot "release"
$releaseExe = Join-Path $releaseRoot "妙记字幕下载器.exe"
$referenceUrl = "https://api.nuget.org/v3-flatcontainer/microsoft.netframework.referenceassemblies.net481/1.0.3/microsoft.netframework.referenceassemblies.net481.1.0.3.nupkg"

function Assert-ProjectChild {
    param([string]$Path)
    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullRoot = [IO.Path]::GetFullPath($projectRoot).TrimEnd('\') + '\'
    if (-not $fullPath.StartsWith($fullRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝操作项目目录外的路径：$fullPath"
    }
}

function Remove-GeneratedPath {
    param([string]$Path)
    Assert-ProjectChild -Path $Path
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

foreach ($required in @($projectFile)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "缺少构建所需文件：$required"
    }
}

$msbuild = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
if (-not (Test-Path -LiteralPath $msbuild -PathType Leaf)) {
    $vswhere = "C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
        throw "未找到 Visual Studio 2022 Build Tools。"
    }
    $installation = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
    $msbuild = Join-Path $installation "MSBuild\Current\Bin\MSBuild.exe"
}

Remove-GeneratedPath -Path $buildRoot
New-Item -ItemType Directory -Path $buildRoot, $releaseRoot -Force | Out-Null

Write-Host "[1/3] 下载 .NET Framework 4.8.1 编译参考程序集..."
Invoke-WebRequest -Uri $referenceUrl -OutFile $referencePackage -TimeoutSec 180
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::ExtractToDirectory($referencePackage, $referenceExtract)
$frameworkDirectory = Join-Path $frameworkRoot ".NETFramework\v4.8.1"
if (-not (Test-Path -LiteralPath $frameworkDirectory -PathType Container)) {
    throw "参考程序集包结构不正确：$frameworkDirectory"
}

Write-Host "[2/3] 编译 Windows 11 x64 WPF 程序..."
& $msbuild $projectFile `
    "/t:Rebuild" `
    "/p:Configuration=Release" `
    "/p:Platform=x64" `
    "/p:TargetFrameworkRootPath=$frameworkRoot" `
    "/v:minimal" `
    "/nologo"
if ($LASTEXITCODE -ne 0) { throw "MSBuild 编译失败" }
if (-not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) {
    throw "编译完成但未找到 EXE：$sourceExe"
}

Write-Host "[3/3] 输出单个 EXE..."
Copy-Item -LiteralPath $sourceExe -Destination $releaseExe -Force
$hash = (Get-FileHash -LiteralPath $releaseExe -Algorithm SHA256).Hash.ToLowerInvariant()

Remove-GeneratedPath -Path $buildRoot
Remove-GeneratedPath -Path (Join-Path $projectRoot "src\FeishuMinutes\bin")
Remove-GeneratedPath -Path (Join-Path $projectRoot "src\FeishuMinutes\obj")

Write-Host ""
Write-Host "发布完成："
Write-Host "  EXE: $releaseExe"
Write-Host "  SHA256: $hash"
