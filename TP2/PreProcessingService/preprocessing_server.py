"""
=====================================================================
 preprocessing_server.py
 --------------------------------------------------------------------
 Serviço RPC de Pré-processamento de dados ambientais.

 Tecnologia : Python 3 + gRPC
 Função     : recebe medições em bruto vindas dos Gateways, faz
              parsing de formatos heterogéneos (RAW/JSON/XML/CSV),
              converte para unidades SI canónicas e valida gamas.

 Porta default: 50051
=====================================================================
"""
from __future__ import annotations

import csv
import io
import json
import logging
import re
import sys
import time
import xml.etree.ElementTree as ET
from concurrent import futures
from datetime import datetime, timezone

import grpc

import preprocessing_pb2 as pb
import preprocessing_pb2_grpc as pb_grpc

# ---------------------------------------------------------------------
# Configuração de logging
# ---------------------------------------------------------------------
logging.basicConfig(
    level=logging.INFO,
    format="[%(asctime)s] [PREPROC] %(levelname)s - %(message)s",
    datefmt="%H:%M:%S",
)
log = logging.getLogger("preprocessing")

# ---------------------------------------------------------------------
# Tabelas de conversão e gamas válidas por tipo de dado
# (Unidade SI canónica + gama plausível para validação)
# ---------------------------------------------------------------------
GAMAS_VALIDAS = {
    "TEMP":  ("C",     -50.0,  60.0),
    "HUM":   ("%",       0.0, 100.0),
    "PM2.5": ("ug/m3",   0.0, 1000.0),
    "PM10":  ("ug/m3",   0.0, 1000.0),
    "RUIDO": ("dB",      0.0, 140.0),
    "AR":    ("AQI",     0.0, 500.0),
    "LUZ":   ("lux",     0.0, 200000.0),
    "NO2":   ("ug/m3",   0.0, 1000.0),
}


def _converter_temperatura(valor: float, unidade: str) -> tuple[float, str]:
    """Converte temperatura para graus Celsius."""
    u = unidade.upper().strip()
    if u in ("F", "FAHRENHEIT"):
        return (valor - 32.0) * 5.0 / 9.0, "convertido de F para C"
    if u in ("K", "KELVIN"):
        return valor - 273.15, "convertido de K para C"
    return valor, ""


def _converter_concentracao(valor: float, unidade: str) -> tuple[float, str]:
    """Converte concentrações para ug/m3."""
    u = unidade.lower().strip()
    if u in ("mg/m3", "mg/m^3"):
        return valor * 1000.0, "convertido de mg/m3 para ug/m3"
    return valor, ""


# ---------------------------------------------------------------------
# Parsers de payload por formato
# ---------------------------------------------------------------------
def _parse_payload(payload: str, formato: str, tipo_dado: str) -> float:
    """
    Extrai o valor numérico do payload de acordo com o formato declarado.
    Aceita: RAW (texto livre), JSON, XML, CSV.
    """
    formato = (formato or "RAW").upper()

    if formato == "RAW":
        # tenta encontrar o primeiro número (int/float, sinal incluído)
        match = re.search(r"-?\d+(?:[.,]\d+)?", payload)
        if not match:
            raise ValueError(f"RAW: nenhum número encontrado em '{payload}'")
        return float(match.group(0).replace(",", "."))

    if formato == "JSON":
        obj = json.loads(payload)
        # procura uma chave canónica: 'valor', 'value', ou o próprio tipo
        for chave in ("valor", "value", tipo_dado.lower(), tipo_dado):
            if isinstance(obj, dict) and chave in obj:
                return float(obj[chave])
        if isinstance(obj, (int, float)):
            return float(obj)
        raise ValueError(f"JSON sem campo 'valor'/'value': {payload}")

    if formato == "XML":
        root = ET.fromstring(payload)
        # primeiro: <valor>X</valor>; depois: atributo value
        for tag in ("valor", "value", tipo_dado.lower()):
            el = root.find(tag)
            if el is not None and el.text:
                return float(el.text.strip())
        if "value" in root.attrib:
            return float(root.attrib["value"])
        if root.text and root.text.strip():
            return float(root.text.strip())
        raise ValueError(f"XML sem valor reconhecível: {payload}")

    if formato == "CSV":
        # assume formato: timestamp,sensor,valor   OU   valor isolado
        reader = csv.reader(io.StringIO(payload))
        linha = next(reader)
        # devolve o último campo numérico encontrado
        for campo in reversed(linha):
            try:
                return float(campo.strip())
            except ValueError:
                continue
        raise ValueError(f"CSV sem coluna numérica: {payload}")

    raise ValueError(f"Formato desconhecido: {formato}")


# ---------------------------------------------------------------------
# Núcleo: normalização de uma medição
# ---------------------------------------------------------------------
def _normalizar(req: pb.RawMeasurement) -> pb.NormalizedMeasurement:
    obs = []
    valor = None
    valido = True
    unidade_si = ""

    try:
        valor = _parse_payload(req.payload, req.formato, req.tipo_dado)
    except Exception as exc:
        log.warning("Falha a fazer parse de '%s' (%s): %s",
                    req.payload, req.formato, exc)
        obs.append(f"parse error: {exc}")
        valido = False
        valor = 0.0

    tipo = req.tipo_dado.upper().strip()
    if tipo in GAMAS_VALIDAS:
        unidade_si, vmin, vmax = GAMAS_VALIDAS[tipo]

        # conversões específicas
        if tipo == "TEMP":
            valor, msg = _converter_temperatura(valor, req.unidade)
            if msg:
                obs.append(msg)
        elif tipo in ("PM2.5", "PM10", "NO2"):
            valor, msg = _converter_concentracao(valor, req.unidade)
            if msg:
                obs.append(msg)

        # validação de gama
        if valido and not (vmin <= valor <= vmax):
            obs.append(f"valor fora da gama [{vmin}, {vmax}]")
            valido = False
    else:
        unidade_si = req.unidade or ""
        obs.append(f"tipo '{tipo}' sem regras específicas")

    # timestamp: se vier vazio ou inválido, gera-se agora em UTC
    ts = req.timestamp.strip() if req.timestamp else ""
    if not ts:
        ts = datetime.now(timezone.utc).isoformat()

    return pb.NormalizedMeasurement(
        sensor_id   = req.sensor_id,
        zona        = req.zona,
        tipo_dado   = tipo,
        valor       = round(valor, 4),
        unidade_si  = unidade_si,
        timestamp   = ts,
        valido      = valido,
        observacoes = "; ".join(obs),
    )


# ---------------------------------------------------------------------
# Implementação do serviço gRPC
# ---------------------------------------------------------------------
class PreProcessingServicer(pb_grpc.PreProcessingServiceServicer):

    def Normalize(self, request, context):                # noqa: N802
        log.info("Normalize  | %s/%s/%s  formato=%s",
                 request.sensor_id, request.zona,
                 request.tipo_dado, request.formato or "RAW")
        return _normalizar(request)

    def NormalizeBatch(self, request, context):           # noqa: N802
        log.info("NormalizeBatch | %d medições", len(request.medicoes))
        resp = pb.BatchResponse()
        for m in request.medicoes:
            resp.medicoes.append(_normalizar(m))
        return resp

    def Ping(self, request, context):                     # noqa: N802
        return pb.PingResponse(
            mensagem  = f"pong from preprocessing (saudação a {request.origem})",
            timestamp = datetime.now(timezone.utc).isoformat(),
        )


# ---------------------------------------------------------------------
# Entry-point
# ---------------------------------------------------------------------
def main() -> None:
    porta = 50051
    if len(sys.argv) > 1:
        porta = int(sys.argv[1])

    server = grpc.server(futures.ThreadPoolExecutor(max_workers=10))
    pb_grpc.add_PreProcessingServiceServicer_to_server(
        PreProcessingServicer(), server)
    server.add_insecure_port(f"[::]:{porta}")
    server.start()

    log.info("Serviço de Pré-processamento a escutar na porta %d", porta)
    log.info("Tipos suportados: %s", ", ".join(sorted(GAMAS_VALIDAS.keys())))

    try:
        while True:
            time.sleep(3600)
    except KeyboardInterrupt:
        log.info("Encerrando...")
        server.stop(grace=2)


if __name__ == "__main__":
    main()
