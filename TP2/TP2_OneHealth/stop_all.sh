#!/usr/bin/env bash
# Para todos os processos lançados por run_all.sh
ROOT="$(cd "$(dirname "$0")" && pwd)"
for pidf in "$ROOT"/logs/*.pid; do
    [ -f "$pidf" ] || continue
    pid=$(cat "$pidf"); nome=$(basename "$pidf" .pid)
    if kill -0 "$pid" 2>/dev/null; then
        echo "Parando $nome (pid=$pid)"; kill "$pid" 2>/dev/null || true
    fi
    rm -f "$pidf"
done
