#!/usr/bin/env bash
# =====================================================================
#  run_all.sh
#  --------------------------------------------------------------------
#  Script de conveniência para levantar todo o sistema localmente,
#  cada componente numa janela própria via tmux ou em processos
#  background (fallback). Pressupõe RabbitMQ a correr em localhost:5672.
# =====================================================================
set -e
ROOT="$(cd "$(dirname "$0")" && pwd)"

iniciar() {
    local nome=$1; shift
    echo "[INFO] A iniciar $nome -> $*"
    ( cd "$ROOT" && "$@" ) > "logs/$nome.log" 2>&1 &
    echo $! > "logs/$nome.pid"
}

mkdir -p logs

iniciar preprocessing python PreProcessingService/preprocessing_server.py
iniciar analysis      python AnalysisService/analysis_server.py
sleep 2

iniciar servidor      dotnet run --project Servidor/Servidor.csproj
sleep 3

iniciar gateway       dotnet run --project Gateway/Gateway.csproj -- GW01 localhost 127.0.0.1 9000 localhost 50051 "#" "#"
sleep 2

iniciar sensor1       dotnet run --project Sensor/Sensor.csproj -- S101 ZONA_CENTRO  auto localhost 3000
iniciar sensor2       dotnet run --project Sensor/Sensor.csproj -- S102 ZONA_ESCOLAR auto localhost 5000

echo "[INFO] Tudo iniciado.  Logs em ./logs/  | PIDs em ./logs/*.pid"
echo "[INFO] Para parar : ./stop_all.sh"
