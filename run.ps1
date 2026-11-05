$ErrorActionPreference = 'Stop'
$taskLocalRuntime = Join-Path $PSScriptRoot '.tools\dotnet'
if (Test-Path -LiteralPath (Join-Path $taskLocalRuntime 'shared\Microsoft.WindowsDesktop.App')) {
    $env:DOTNET_ROOT = $taskLocalRuntime
    $env:DOTNET_ROOT_X64 = $taskLocalRuntime
}
$taskExecutable = Join-Path $PSScriptRoot 'artifacts\app\WechatDraft.exe'
if (-not (Test-Path -LiteralPath $taskExecutable)) {
    & (Join-Path $PSScriptRoot 'build.ps1') -Publish
}
Start-Process -FilePath $taskExecutable -WorkingDirectory (Split-Path $taskExecutable)
