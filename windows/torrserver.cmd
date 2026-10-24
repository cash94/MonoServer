@echo off
rem TorrServer (TorrServer-Tizen для Windows): запуск с выбором порта и автозапуск.
rem
rem   torrserver.cmd             — спросит порт (Enter — 8090), проверит, свободен ли, и запустит
rem   torrserver.cmd 8091        — без вопроса
rem   torrserver.cmd install     — запускать при входе в Windows (спросит порт) и запустить сейчас
rem   torrserver.cmd uninstall   — убрать автозапуск
rem
rem Данные — %LOCALAPPDATA%\torrserver-tizen. Остановить — закрыть окно или Ctrl+C.
chcp 65001 >nul
setlocal EnableExtensions EnableDelayedExpansion

set "HERE=%~dp0"
set "EXE=%HERE%TorrServer.exe"
set "DATA=%LOCALAPPDATA%\torrserver-tizen"
set "AUTORUN=%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\TorrServer-Tizen.cmd"
set "DEFAULT_PORT=8090"

if not exist "%EXE%" (
    echo Рядом со скриптом нет TorrServer.exe — распакуйте архив целиком.
    pause
    exit /b 1
)

if /i "%~1"=="uninstall" (
    if exist "%AUTORUN%" (del "%AUTORUN%" && echo Автозапуск убран.) else echo Автозапуска не было.
    exit /b 0
)

set "MODE=run"
set "PORT="
if /i "%~1"=="install" (set "MODE=install") else if not "%~1"=="" set "PORT=%~1"

:ask
if "%PORT%"=="" (
    set /p "PORT=Порт TorrServer [%DEFAULT_PORT%]: "
    if "!PORT!"=="" set "PORT=%DEFAULT_PORT%"
)
rem Только цифры, 1–65535
set "BAD="
for /f "delims=0123456789" %%c in ("!PORT!") do set "BAD=1"
if defined BAD goto badport
if !PORT! LSS 1 goto badport
if !PORT! GTR 65535 goto badport

rem Занят ли: слушает ли кто-то TCP-порт
set "OWNER="
for /f "tokens=2,5" %%a in ('netstat -ano -p tcp ^| findstr /r /c:"LISTENING"') do (
    for /f "tokens=*" %%p in ("%%a") do (
        echo %%p| findstr /r /c:":!PORT!$" >nul && set "OWNER=%%b"
    )
)
if defined OWNER (
    set "NAME="
    for /f "tokens=1 delims=," %%n in ('tasklist /fi "PID eq !OWNER!" /fo csv /nh 2^>nul') do set "NAME=%%~n"
    echo Порт !PORT! занят: !NAME! ^(PID !OWNER!^).
    if not "%~1"=="" if /i not "%~1"=="install" exit /b 1
    set "PORT="
    goto ask
)

if not exist "%DATA%" mkdir "%DATA%"

if "%MODE%"=="install" (
    rem Автозапуск при входе — скрипт в папке «Автозагрузка»: служба стартует свёрнутой
    > "%AUTORUN%" (
        echo @echo off
        rem Путь может быть с кириллицей — файл в UTF-8
        echo chcp 65001 ^>nul
        echo start "TorrServer" /min "%EXE%" !PORT! "%DATA%"
    )
    if not exist "%AUTORUN%" (
        echo Не удалось включить автозапуск.
        exit /b 1
    )
    echo Автозапуск при входе в Windows включён: порт !PORT!.
    start "TorrServer" /min "%EXE%" !PORT! "%DATA%"
    call :addresses
    echo Окно службы свёрнуто на панели задач. Убрать автозапуск: torrserver.cmd uninstall
    exit /b 0
)

echo TorrServer: порт !PORT!, данные %DATA%
call :addresses
echo   остановить: Ctrl+C или закрыть окно
"%EXE%" !PORT! "%DATA%"
exit /b %errorlevel%

:badport
echo «!PORT!» — не номер порта ^(нужно число 1–65535^).
if not "%~1"=="" if /i not "%~1"=="install" exit /b 1
set "PORT="
goto ask

:addresses
echo   в этом компьютере: http://127.0.0.1:!PORT!/
for /f "tokens=2 delims=:" %%i in ('ipconfig ^| findstr /r /c:"IPv4"') do (
    set "IP=%%i"
    set "IP=!IP: =!"
    echo   в домашней сети:   http://!IP!:!PORT!/  ^(адрес для TorrStream, Lampa^)
    goto :eof
)
goto :eof
