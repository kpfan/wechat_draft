param([switch]$Test, [switch]$Publish, [switch]$Production)
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$taskDotnet = Join-Path $taskRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $taskDotnet)) {
    $taskCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($taskCommand) { $taskDotnet = $taskCommand.Source } else { throw '请安装 .NET 10 SDK，或将 SDK 放入 .tools\dotnet。' }
}
$env:DOTNET_CLI_HOME = Join-Path $taskRoot '.tools\cli-home'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Push-Location $taskRoot
try {
    $taskModeArguments = @("-p:ProductionMode=$($Production.IsPresent.ToString().ToLowerInvariant())")
    $taskAppArguments = @($taskModeArguments)
    if ($Production -and $Publish) { $taskAppArguments += '-p:SelfContained=true' }
    $taskRestoreArguments = @('restore','src/WechatDraft.App/WechatDraft.App.csproj','-p:Platform=x64')
    if (Test-Path -LiteralPath (Join-Path $taskRoot '.tools\feed\microsoft.windowsappsdk.winui.1.8.260803003.nupkg')) {
        $taskRestoreArguments += @('--source', (Join-Path $taskRoot '.tools\feed'), '-p:NuGetAudit=false')
    }
    & $taskDotnet @taskRestoreArguments @taskAppArguments
    if ($LASTEXITCODE -ne 0) { throw '项目依赖恢复失败。' }
    & $taskDotnet build src/WechatDraft.App/WechatDraft.App.csproj -c Release -p:Platform=x64 --no-restore @taskAppArguments
    if ($LASTEXITCODE -ne 0) { throw '客户端构建失败。' }
    if ($Test) {
        $taskTestRestore = @('restore','tests/WechatDraft.Tests/WechatDraft.Tests.csproj')
        if (Test-Path -LiteralPath (Join-Path $taskRoot '.tools\feed\microsoft.windowsappsdk.winui.1.8.260803003.nupkg')) {
            $taskTestRestore += @('--source', (Join-Path $taskRoot '.tools\feed'), '-p:NuGetAudit=false')
        }
        & $taskDotnet @taskTestRestore @taskModeArguments
        if ($LASTEXITCODE -ne 0) { throw '验证项目依赖恢复失败。' }
        & $taskDotnet run --project tests/WechatDraft.Tests/WechatDraft.Tests.csproj -c Release --no-restore @taskModeArguments
        if ($LASTEXITCODE -ne 0) { throw '验证未通过。' }
    }
    if ($Publish) {
        $taskPublishDirectory = if ($Production) { 'artifacts/production-app' } else { 'artifacts/app' }
        & $taskDotnet publish src/WechatDraft.App/WechatDraft.App.csproj -c Release -p:Platform=x64 --no-restore -o $taskPublishDirectory @taskAppArguments
        if ($LASTEXITCODE -ne 0) { throw '发布失败。' }
    }
} finally { Pop-Location }
