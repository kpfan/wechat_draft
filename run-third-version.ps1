# 兼容旧启动器名称；第三版已直接接入主页“真实微信”，不再打开实验页。
$ErrorActionPreference = 'Stop'
Remove-Item Env:WECHAT_DRAFT_THIRD_VERSION -ErrorAction SilentlyContinue
& (Join-Path $PSScriptRoot 'run.ps1')
