@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"
echo 正在构建小申陪伴，请稍候……
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-Windows.ps1" -Runtime win-x64 -Configuration Release
if errorlevel 1 (
  echo.
  echo 构建失败，请阅读上方提示。
  pause
  exit /b 1
)
echo.
echo 构建成功，文件位于“可直接发送”文件夹。
pause
