@echo off
chcp 65001 >nul
cd /d "%~dp0.."
set ASPNETCORE_ENVIRONMENT=Development
set ASPNETCORE_URLS=http://127.0.0.1:5088
set Classroom__Database=data\classrooms-local-test.db
set Classroom__BootstrapKey=am-link-local-setup
echo.
echo AM-LINK 本机测试课堂正在启动……
echo 请不要关闭随后出现的服务器窗口。
echo 教师网页：http://127.0.0.1:5088
echo 首次初始化密钥：am-link-local-setup
echo.
start "AM-LINK 本机课堂服务器（请勿关闭）" "%~dp0..\AMLink.ClassroomServer.exe"
timeout /t 2 /nobreak >nul
start "" "http://127.0.0.1:5088"
