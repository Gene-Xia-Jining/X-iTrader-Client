@echo off
chcp 65001 >nul 2>&1
setlocal EnableDelayedExpansion
title iTrader 运行环境安装

rem ============================================================
rem  iTrader 首启运行环境引导
rem
rem  本程序以"框架依赖"方式发布：不内置 .NET 与 Windows App Runtime，
rem  首次使用前需要这两个运行环境。直接双击 X-iTrader.exe 若"没反应"，
rem  通常就是缺少运行环境——运行本脚本即可自动补齐。
rem
rem  检测项：
rem    1) .NET 8 Desktop Runtime (x64)   —— 壳进程本身的托管运行时
rem    2) Windows App Runtime 1.6 (x64)  —— WinUI 3 依赖
rem  缺失项从微软官方 aka.ms 短链下载并静默安装，完成后启动 X-iTrader.exe。
rem ============================================================

set "SCRIPT_DIR=%~dp0"
set "SHELL_EXE=%SCRIPT_DIR%X-iTrader.exe"
set "DOTNET_URL=https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe"
set "WASDK_URL=https://aka.ms/windowsappsdk/1.6/latest/windowsappruntimeinstall-x64.exe"
set "DOTNET_MANUAL=https://dotnet.microsoft.com/download/dotnet/8.0"

echo.
echo ============================================
echo   iTrader 运行环境检查
echo ============================================
echo.

if not exist "%SHELL_EXE%" (
    echo [错误] 未找到 X-iTrader.exe，请确认本脚本与它放在同一目录。
    echo        当前目录: %SCRIPT_DIR%
    goto :fail
)

set "NEED_DOTNET=0"
set "NEED_WASDK=0"

rem ---------- 1. 检测 .NET 8 Desktop Runtime ----------
echo [1/2] 检查 .NET 8 Desktop Runtime ...
set "HAS_DOTNET=0"
where dotnet >nul 2>&1
if %ERRORLEVEL%==0 (
    for /f "tokens=*" %%L in ('dotnet --list-runtimes 2^>nul ^| findstr /C:"Microsoft.WindowsDesktop.App 8."') do (
        set "HAS_DOTNET=1"
    )
)
if "!HAS_DOTNET!"=="0" (
    rem dotnet 命令不存在时，退回注册表探测（用户级与机器级都查）
    for %%R in (
        "HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App"
        "HKCU\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App"
    ) do (
        reg query %%R >nul 2>&1
        if !ERRORLEVEL!==0 set "HAS_DOTNET=1"
    )
)
if "!HAS_DOTNET!"=="1" (
    echo       已安装。
) else (
    echo       未安装，需要安装。
    set "NEED_DOTNET=1"
)

rem ---------- 2. 检测 Windows App Runtime ----------
echo [2/2] 检查 Windows App Runtime 1.6 ...
set "HAS_WASDK=0"
rem 通过 PowerShell 查询已注册的运行时包系列名
for /f "tokens=*" %%P in ('powershell -NoProfile -Command "(Get-AppxPackage -Name Microsoft.WindowsAppRuntime.1.6 -ErrorAction SilentlyContinue | Measure-Object).Count" 2^>nul') do (
    set "PKGCOUNT=%%P"
)
if defined PKGCOUNT if not "!PKGCOUNT!"=="0" set "HAS_WASDK=1"
if "!HAS_WASDK!"=="1" (
    echo       已安装。
) else (
    echo       未安装，需要安装。
    set "NEED_WASDK=1"
)

rem ---------- 安装缺失项 ----------
if "!NEED_DOTNET!"=="1" (
    echo.
    echo ==> 安装 .NET 8 Desktop Runtime ...
    call :download_and_install "%DOTNET_URL%" "dotnet-8-desktop-x64.exe" "/install /quiet /norestart" ".NET 8 Desktop Runtime" "!DOTNET_MANUAL!"
    if !ERRORLEVEL! neq 0 goto :fail
)

if "!NEED_WASDK!"=="1" (
    echo.
    echo ==> 安装 Windows App Runtime 1.6 ...
    call :download_and_install "%WASDK_URL%" "WindowsAppRuntimeInstall-x64.exe" "--quiet --force" "Windows App Runtime" "!WASDK_URL!"
    if !ERRORLEVEL! neq 0 goto :fail
)

echo.
echo ============================================
echo   运行环境就绪，正在启动 iTrader ...
echo ============================================
start "" "%SHELL_EXE%"
endlocal
exit /b 0

rem ---------- 子过程：下载并静默安装 ----------
rem 参数: %1=下载地址 %2=临时文件名 %3=安装参数 %4=显示名 %5=手动下载地址
:download_and_install
set "DL_URL=%~1"
set "DL_NAME=%~2"
set "DL_ARGS=%~3"
set "DL_LABEL=%~4"
set "DL_MANUAL=%~5"
set "DL_PATH=%TEMP%\%DL_NAME%"

echo       下载 %DL_LABEL% ...
if exist "%DL_PATH%" del /f /q "%DL_PATH%" >nul 2>&1
rem 用 PowerShell 下载：不受浏览器 UA/重定向差异影响，且失败可捕获
powershell -NoProfile -Command ^
    "$ProgressPreference='SilentlyContinue';" ^
    "try { Invoke-WebRequest -Uri '%DL_URL%' -OutFile '%DL_PATH%' -UseBasicParsing -TimeoutSec 600 }" ^
    "catch { Write-Host $_.Exception.Message; exit 1 }"
if not exist "%DL_PATH%" (
    echo       [错误] 下载失败，请检查网络后重试。
    echo             手动下载地址: %DL_MANUAL%
    exit /b 1
)

echo       安装 %DL_LABEL%（静默，可能需要 1-2 分钟）...
"%DL_PATH%" %DL_ARGS%
set "INSTALL_RC=%ERRORLEVEL%"
del /f /q "%DL_PATH%" >nul 2>&1

rem 常见返回码：0=成功；1638/3010=已安装或已安装待重启，视为可用
if "%INSTALL_RC%"=="0" exit /b 0
if "%INSTALL_RC%"=="1638" exit /b 0
if "%INSTALL_RC%"=="3010" exit /b 0

echo       [错误] 安装返回码 %INSTALL_RC%
echo             请尝试手动安装: %DL_MANUAL%
exit /b 1

:fail
echo.
echo 运行环境安装未完成，iTrader 可能无法启动。
echo 请手动安装以下两项后重新运行本脚本：
echo   1. .NET 8 Desktop Runtime (x64): %DOTNET_MANUAL%
echo   2. Windows App Runtime 1.6 (x64): %WASDK_URL%
echo.
pause
endlocal
exit /b 1
