param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $projectRoot 'LyricCanvas.sln'
$artifacts = Join-Path $projectRoot 'artifacts\check'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '需要安装 .NET 8 SDK，并让 dotnet 在 PATH 中可用。'
}

# SDK 8 将所有项目输出放到独立目录，不覆盖正在运行的 lyc.exe。
# 只执行离线回归用例；不启动播放器、不操作桌面、不结束用户的应用。
Write-Host ('[1/2] 构建 {0}，输出：{1}' -f $Configuration, $artifacts)
& dotnet build $solution --configuration $Configuration --artifacts-path $artifacts --nologo -v minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host '[2/2] 运行核心、媒体服务及界面组件回归测试'
& dotnet test $solution --configuration $Configuration --artifacts-path $artifacts --no-build --no-restore --nologo -v minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host '构建和离线回归通过。播放器实机同步与界面交互仍需单独验收。' -ForegroundColor Green
exit 0
