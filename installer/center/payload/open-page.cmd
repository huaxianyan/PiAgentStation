@echo off
rem 打开网页版：令牌每次都可能变，所以不在快捷方式里写死。
setlocal
set "TOKEN_FILE=%ProgramData%\SevenStation.Core	oken.txt"
if not exist "%TOKEN_FILE%" (
    echo 还没生成访问令牌，先确认 Seven Station Core 的服务已经跑起来。
    pause
    exit /b 1
)
set "TOKEN="
set /p TOKEN=<"%TOKEN_FILE%"
start "" "http://127.0.0.1:8787/?token=%TOKEN%"
