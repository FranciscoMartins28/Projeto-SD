#!/usr/bin/env bash
set -e
cd "$(dirname "$0")"

PROTO_DIR=../proto

python -m grpc_tools.protoc \
    -I"$PROTO_DIR" \
    --python_out=. \
    --grpc_python_out=. \
    "$PROTO_DIR/analysis.proto"

echo "[OK] Stubs Python gerados:"
ls -1 analysis_pb2*.py
