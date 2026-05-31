@echo off
title TP2 OneHealth - Launcher
chcp 65001 >nul
echo ========================================
echo  TP2 One Health - Arranque automatico
echo ========================================
echo.

REM === 1) RabbitMQ ===
echo [1/7] A arrancar RabbitMQ (Docker)...
docker start rabbit >nul 2>&1
if errorlevel 1 (
    echo      Container 'rabbit' nao existe. A criar...
    docker run -d --name rabbit -p 5672:5672 -p 15672:15672 rabbitmq:3-management >nul
)
echo      [OK] RabbitMQ a arrancar.
timeout /t 5 /nobreak >nul

REM === 2) Servico de Pre-processamento (Python) ===
echo [2/7] A arrancar Pre-processamento...
start "PreProcessing" cmd /k "cd /d %~dp0PreProcessingService && py preprocessing_server.py"
timeout /t 3 /nobreak >nul

REM === 3) Servico de Analise (Python) ===
echo [3/7] A arrancar Analise...
start "Analysis" cmd /k "cd /d %~dp0AnalysisService && py analysis_server.py"
timeout /t 3 /nobreak >nul

REM === 4) Servidor (C#) ===
echo [4/7] A arrancar Servidor...
start "Servidor" cmd /k "cd /d %~dp0Servidor && dotnet run"
timeout /t 8 /nobreak >nul

REM === 5) Gateway (C#) ===
echo [5/7] A arrancar Gateway...
start "Gateway" cmd /k "cd /d %~dp0Gateway && dotnet run -- GW01 localhost 127.0.0.1 9000 localhost 50051 # #"
timeout /t 5 /nobreak >nul

REM === 6) Sensor (C#) ===
echo [6/7] A arrancar Sensor S101...
start "Sensor S101" cmd /k "cd /d %~dp0Sensor && dotnet run -- S101 ZONA_CENTRO auto localhost 3000"
timeout /t 3 /nobreak >nul

REM === 7) Interface Web (Python/Flask) ===
echo [7/7] A arrancar Interface Web...
start "WebUI" cmd /k "cd /d %~dp0Interface && py web_interface.py"
timeout /t 5 /nobreak >nul

REM === Abrir browser automaticamente ===
echo.
echo A abrir o Dashboard no browser...
start "" "http://localhost:8080"

echo.
echo ========================================REM === 8) Interface CLI ===
echo [8/8] A arrancar Interface CLI...
start "CLI" cmd /k "cd /d %~dp0Interface && dotnet run"
echo  Tudo iniciado!
echo.
echo   Dashboard Web : http://localhost:8080
echo   RabbitMQ UI   : http://localhost:15672  (guest/guest)
echo.
echo  Para parar tudo: duplo clique no parar.bat
echo ========================================
start "" "http://localhost:15672"
echo.
pause