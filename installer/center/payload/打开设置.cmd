@echo off
rem 中心不在界面上改配置，改的是这个 JSON；改完要到服务里重启一次。
setlocal
set "SETTINGS=%ProgramData%\SevenStation.Core\settings.json"
if not exist "%SETTINGS%" (
    echo 还没有配置文件，先启动一次 SevenStation 中心服务，它会自己生成。
    pause
    exit /b 1
)
notepad "%SETTINGS%"
echo 改完记得重启服务：services.msc 里找「SevenStation 中心」，或执行
echo   sc stop SevenStationCore ^&^& sc start SevenStationCore
pause
