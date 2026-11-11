$ErrorActionPreference = 'Stop'
$taskLocalRuntime = Join-Path $PSScriptRoot '.tools\dotnet'
if (Test-Path -LiteralPath (Join-Path $taskLocalRuntime 'shared\Microsoft.WindowsDesktop.App')) {
    $env:DOTNET_ROOT = $taskLocalRuntime
    $env:DOTNET_ROOT_X64 = $taskLocalRuntime
}
$taskExecutable = Join-Path $PSScriptRoot 'artifacts\app\WechatDraft.exe'
if (-not (Test-Path -LiteralPath $taskExecutable)) { throw '请先在项目目录运行 build.ps1 -Test -Publish，生成实验客户端。' }
$env:WECHAT_DRAFT_THIRD_VERSION = '1'
# 独立实验数据；不读取或覆盖旧客户端的个人配置、数据库和历史。
$env:WECHAT_DRAFT_DATA = Join-Path $PSScriptRoot 'artifacts\app\data-third-version'
Start-Process -FilePath $taskExecutable -WorkingDirectory (Split-Path $taskExecutable)
