#!/usr/bin/env bash
# Gera os stubs Python a partir dos .proto
# Executar: ./gen_stubs.sh
set -e

cd "$(dirname "$0")"

PROTO_DIR=../proto

python -m grpc_tools.protoc \
    -I"$PROTO_DIR" \
    --python_out=. \
    --grpc_python_out=. \
    "$PROTO_DIR/preprocessing.proto"

echo "[OK] Stubs Python gerados:"
ls -1 preprocessing_pb2*.py
