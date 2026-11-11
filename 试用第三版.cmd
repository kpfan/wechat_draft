@echo off
setlocal
set "DOTNET_ROOT=%~dp0.tools\dotnet"
set "DOTNET_ROOT_X64=%~dp0.tools\dotnet"
set "WECHAT_DRAFT_THIRD_VERSION=1"
set "WECHAT_DRAFT_DATA=%~dp0artifacts\app\data-third-version"
if not exist "%~dp0artifacts\app\WechatDraft.exe" (
  echo Build required: build.ps1 -Test -Publish
  pause
  exit /b 1
)
start "" /D "%~dp0artifacts\app" "%~dp0artifacts\app\WechatDraft.exe"
