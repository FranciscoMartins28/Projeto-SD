@echo off
title TP2 OneHealth - Parar
echo A parar todos os servicos...
echo.

REM Para o RabbitMQ
echo [1/4] A parar RabbitMQ...
docker stop rabbit >nul 2>&1

REM Mata os processos Python (preprocessing, analysis, web)
echo [2/4] A parar servicos Python...
taskkill /F /IM python.exe /T >nul 2>&1
taskkill /F /IM py.exe /T >nul 2>&1
taskkill /F /IM pythonw.exe /T >nul 2>&1

REM Mata os processos .NET (Servidor, Gateway, Sensor)
echo [3/4] A parar projetos .NET...
taskkill /F /IM dotnet.exe /T >nul 2>&1

REM Fecha as janelas de comando que sobraram
echo [4/4] A fechar janelas...
taskkill /F /FI "WindowTitle eq PreProcessing*" >nul 2>&1
taskkill /F /FI "WindowTitle eq Analysis*"      >nul 2>&1
taskkill /F /FI "WindowTitle eq Servidor*"      >nul 2>&1
taskkill /F /FI "WindowTitle eq Gateway*"       >nul 2>&1
taskkill /F /FI "WindowTitle eq Sensor*"        >nul 2>&1
taskkill /F /FI "WindowTitle eq WebUI*"         >nul 2>&1

echo.
echo Tudo parado.
timeout /t 3 /nobreak >nul