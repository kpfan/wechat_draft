param(
    [string]$DataDirectory = "$PSScriptRoot/artifacts/app/data",
    [string]$AppDirectory = "$PSScriptRoot/artifacts/production-app",
    [string]$Output = "$PSScriptRoot/artifacts/model-config.wdc"
)

$ErrorActionPreference = 'Stop'
$taskDotnet = Join-Path $PSScriptRoot '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $taskDotnet)) { $taskDotnet = (Get-Command dotnet).Source }
$taskProject = Join-Path $PSScriptRoot 'tools/WechatDraft.Integration/WechatDraft.Integration.csproj'
$taskFeed = Join-Path $PSScriptRoot '.tools/feed'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.tools/cli-home'
$taskRestore = @('restore', $taskProject)
if (Test-Path -LiteralPath (Join-Path $taskFeed 'microsoft.data.sqlite.10.0.0.nupkg')) {
    $taskRestore += @('--source', $taskFeed, '-p:NuGetAudit=false')
}
& $taskDotnet @taskRestore
if ($LASTEXITCODE -ne 0) { throw '配置导出工具的依赖恢复失败。' }
& $taskDotnet run --project $taskProject -c Release --no-restore -- export-model-config $DataDirectory $AppDirectory $Output
if ($LASTEXITCODE -ne 0) { throw '导出加密模型配置失败。' }
