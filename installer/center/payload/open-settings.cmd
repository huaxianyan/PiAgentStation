@echo off
rem 配置不在界面上改：改的就是这份 JSON，改完要让服务重新读一次。
setlocal
set "SETTINGS=%ProgramData%\SevenStation.Core\settings.json"
if not exist "%SETTINGS%" (
    echo 还没生成配置文件，先让 Seven Station Core 的服务自己建一次。
    pause
    exit /b 1
)
notepad "%SETTINGS%"
echo 改完记得重启服务：services.msc 里找「Seven Station Core」，或者执行
echo   sc stop SevenStationCore ^&^& sc start SevenStationCore
pause
