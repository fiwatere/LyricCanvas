param(
    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $projectRoot 'src\LyricCanvas.App\LyricCanvas.App.csproj'
[xml]$projectXml = Get-Content -LiteralPath $project -Raw
$version = [string]($projectXml.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if ([string]::IsNullOrWhiteSpace($version) -or $version -notmatch '^[0-9A-Za-z.+-]+$') {
    throw '应用工程未声明有效版本。'
}

$packageName = 'LyricCanvas-{0}-{1}' -f $version, $Runtime
$packagesRoot = Join-Path $projectRoot 'artifacts\packages'
$archivePath = Join-Path $packagesRoot ($packageName + '.zip')
if ((Test-Path -LiteralPath $archivePath) -and -not $Force) {
    throw ('压缩包已存在：{0}。需要更新时显式传入 -Force。' -f $archivePath)
}

# 每次使用新目录，避免旧发布文件残留；仅发布 App，避免多个工程同名文件相互覆盖。
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$packageDirectory = Join-Path $packagesRoot ($packageName + '-' + $stamp + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 6))
New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
& dotnet publish $project --configuration Release --runtime $Runtime --self-contained true --artifacts-path (Join-Path $projectRoot 'artifacts\publish') --output $packageDirectory --nologo -v minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$guide = Join-Path $projectRoot 'docs\portable-guide.txt'
Copy-Item -LiteralPath $guide -Destination (Join-Path $packageDirectory '使用说明.txt')
# 分发空歌词目录供用户添加文件，不把开发用歌词/联网缓存/个人配置打包给别人。
New-Item -ItemType Directory -Path (Join-Path $packageDirectory 'lyrics') -Force | Out-Null
Set-Content -LiteralPath (Join-Path $packageDirectory 'lyrics\放入你的LRC歌词.txt') -Value '将本地 LRC 文件放在此目录，或在设置中选择自己的歌词目录。' -Encoding UTF8

if (-not (Test-Path -LiteralPath (Join-Path $packageDirectory 'lyc.exe'))) {
    throw '发布输出中缺少 lyc.exe。'
}
Compress-Archive -LiteralPath $packageDirectory -DestinationPath $archivePath -Force
$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
[IO.File]::WriteAllText($archivePath + '.sha256', $hash + '  ' + [IO.Path]::GetFileName($archivePath) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
Write-Host ('完整运行目录：{0}' -f $packageDirectory)
Write-Host ('便携压缩包：{0}' -f $archivePath)
