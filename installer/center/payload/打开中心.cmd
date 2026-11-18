@echo off
rem 打开中心页面。令牌每次都可能变，所以现读现拼，不写死。
setlocal
set "TOKEN_FILE=%ProgramData%\SevenStation.Core\token.txt"
if not exist "%TOKEN_FILE%" (
    echo 还没生成访问令牌，先确认 SevenStation 中心服务已经启动。
    pause
    exit /b 1
)
set "TOKEN="
set /p TOKEN=<"%TOKEN_FILE%"
start "" "http://127.0.0.1:8787/?token=%TOKEN%"
