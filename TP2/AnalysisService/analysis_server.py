"""
=====================================================================
 analysis_server.py
 --------------------------------------------------------------------
 Serviço RPC de Análise e Previsão.

 Tecnologia : Python 3 + gRPC + (statistics / math da std-lib)
 Função     : recebe coleções de medições e devolve:
                - estatísticas descritivas
                - deteção de anomalias por z-score
                - previsão de risco para a saúde pública
                  (recomendações WHO / IQA)

 Porta default: 50052
=====================================================================
"""
from __future__ import annotations

import logging
import math
import statistics as st
import sys
import time
from concurrent import futures
from datetime import datetime, timezone

import grpc

import analysis_pb2 as pb
import analysis_pb2_grpc as pb_grpc

logging.basicConfig(
    level=logging.INFO,
    format="[%(asctime)s] [ANALYSIS] %(levelname)s - %(message)s",
    datefmt="%H:%M:%S",
)
log = logging.getLogger("analysis")


# ---------------------------------------------------------------------
# Limiares para risco de saúde pública (simplificação WHO / EEA)
#   tipo_dado -> lista de (limite, nível, recomendação)
# ---------------------------------------------------------------------
LIMIARES_RISCO = {
    "PM2.5": [
        (15,  "BOM",       "Qualidade do ar boa."),
        (25,  "MODERADO",  "Pessoas sensíveis devem reduzir esforço prolongado."),
        (50,  "MAU",       "Evitar atividade física intensa ao ar livre."),
        (1e9, "MUITO_MAU", "Permanecer em interior. Risco para todos."),
    ],
    "PM10": [
        (45,  "BOM",       "Qualidade do ar boa."),
        (75,  "MODERADO",  "Grupos sensíveis devem ter precaução."),
        (150, "MAU",       "Evitar exercício ao ar livre."),
        (1e9, "MUITO_MAU", "Risco elevado — permanecer em interior."),
    ],
    "RUIDO": [
        (55,  "BOM",       "Nível confortável."),
        (65,  "MODERADO",  "Possível incómodo prolongado."),
        (75,  "MAU",       "Exposição prolongada prejudicial."),
        (1e9, "MUITO_MAU", "Risco auditivo significativo."),
    ],
    "NO2": [
        (40,  "BOM",       "Sem risco assinalável."),
        (100, "MODERADO",  "Sensíveis devem ter atenção."),
        (200, "MAU",       "Evitar zonas de tráfego intenso."),
        (1e9, "MUITO_MAU", "Risco elevado — afastar-se de fontes."),
    ],
    "TEMP": [
        (32,  "BOM",       "Temperatura confortável."),
        (35,  "MODERADO",  "Hidratar; cuidado com idosos e crianças."),
        (40,  "MAU",       "Onda de calor — evitar exposição."),
        (1e9, "MUITO_MAU", "Risco extremo de golpe de calor."),
    ],
}

GRUPOS_VULNERAVEIS = {
    "PM2.5": ["crianças", "idosos", "asmáticos", "doentes cardiovasculares"],
    "PM10":  ["crianças", "idosos", "asmáticos"],
    "NO2":   ["asmáticos", "doentes respiratórios"],
    "RUIDO": ["recém-nascidos", "trabalhadores expostos"],
    "TEMP":  ["idosos", "crianças", "doentes crónicos"],
}


# ---------------------------------------------------------------------
# Utilitários
# ---------------------------------------------------------------------
def _filtrar(req: pb.AnalysisRequest) -> list[pb.DataPoint]:
    """Aplica filtros opcionais (tipo, zona, sensor, janela temporal)."""
    pontos = list(req.dados)

    if req.tipo_dado:
        pontos = [p for p in pontos if p.tipo_dado.upper() == req.tipo_dado.upper()]
    if req.zona:
        pontos = [p for p in pontos if p.zona == req.zona]
    if req.sensor_id:
        pontos = [p for p in pontos if p.sensor_id == req.sensor_id]

    def _parse_ts(t: str):
        try:
            return datetime.fromisoformat(t.replace("Z", "+00:00"))
        except Exception:
            return None

    if req.inicio:
        ini = _parse_ts(req.inicio)
        if ini:
            pontos = [p for p in pontos if (_parse_ts(p.timestamp) or ini) >= ini]
    if req.fim:
        fim = _parse_ts(req.fim)
        if fim:
            pontos = [p for p in pontos if (_parse_ts(p.timestamp) or fim) <= fim]

    return pontos


def _percentil(valores: list[float], p: float) -> float:
    """Percentil simples por interpolação linear."""
    if not valores:
        return 0.0
    s = sorted(valores)
    k = (len(s) - 1) * p / 100.0
    f, c = math.floor(k), math.ceil(k)
    if f == c:
        return s[int(k)]
    return s[f] * (c - k) + s[c] * (k - f)


def _regressao_linear(xs: list[float], ys: list[float]) -> tuple[float, float]:
    """Devolve (declive, ordenada na origem) por mínimos quadrados."""
    n = len(xs)
    if n < 2:
        return 0.0, ys[0] if ys else 0.0
    media_x = sum(xs) / n
    media_y = sum(ys) / n
    num = sum((x - media_x) * (y - media_y) for x, y in zip(xs, ys))
    den = sum((x - media_x) ** 2 for x in xs)
    declive = num / den if den != 0 else 0.0
    return declive, media_y - declive * media_x


# ---------------------------------------------------------------------
# Implementação do serviço
# ---------------------------------------------------------------------
class AnalysisServicer(pb_grpc.AnalysisServiceServicer):

    # ---- Estatísticas descritivas ------------------------------------
    def ComputeStatistics(self, request, context):        # noqa: N802
        pontos = _filtrar(request)
        valores = [p.valor for p in pontos]
        log.info("ComputeStatistics | tipo=%s zona=%s sensor=%s -> n=%d",
                 request.tipo_dado, request.zona, request.sensor_id, len(valores))

        if not valores:
            return pb.StatisticsResponse(n=0, tipo_dado=request.tipo_dado,
                                         zona=request.zona)

        return pb.StatisticsResponse(
            n             = len(valores),
            media         = round(st.fmean(valores), 4),
            mediana       = round(st.median(valores), 4),
            minimo        = round(min(valores), 4),
            maximo        = round(max(valores), 4),
            desvio_padrao = round(st.pstdev(valores) if len(valores) > 1 else 0.0, 4),
            p25           = round(_percentil(valores, 25), 4),
            p75           = round(_percentil(valores, 75), 4),
            tipo_dado     = request.tipo_dado,
            zona          = request.zona,
        )

    # ---- Deteção de padrões / anomalias -----------------------------
    def DetectPatterns(self, request, context):           # noqa: N802
        pontos = _filtrar(request)
        log.info("DetectPatterns | n=%d", len(pontos))

        resp = pb.PatternResponse(tendencia="ESTAVEL", inclinacao=0.0)
        if len(pontos) < 3:
            resp.resumo = "Dados insuficientes (mínimo 3 pontos)."
            return resp

        valores = [p.valor for p in pontos]
        media = st.fmean(valores)
        desvio = st.pstdev(valores) if len(valores) > 1 else 0.0

        # ---- anomalias por z-score (|z| > 2.0) ---------------------
        for i, p in enumerate(pontos):
            z = (p.valor - media) / desvio if desvio > 0 else 0.0
            if abs(z) > 2.0:
                severidade = ("CRITICA" if abs(z) > 4
                              else "ALTA"  if abs(z) > 3
                              else "MEDIA")
                resp.anomalias.append(pb.Anomaly(
                    sensor_id  = p.sensor_id,
                    zona       = p.zona,
                    tipo_dado  = p.tipo_dado,
                    valor      = p.valor,
                    timestamp  = p.timestamp,
                    z_score    = round(z, 3),
                    severidade = severidade,
                    descricao  = f"Desvio de {z:+.2f}σ relativo à média ({media:.2f})",
                ))

        # ---- tendência por regressão linear sobre o índice ---------
        xs = list(range(len(valores)))
        declive, _ = _regressao_linear(xs, valores)
        if abs(declive) < 0.01:
            resp.tendencia = "ESTAVEL"
        elif declive > 0:
            resp.tendencia = "SUBIDA"
        else:
            resp.tendencia = "DESCIDA"
        resp.inclinacao = round(declive, 4)
        resp.resumo = (f"{len(resp.anomalias)} anomalias em {len(valores)} pontos; "
                       f"tendência {resp.tendencia} (declive={declive:+.4f}).")
        return resp

    # ---- Previsão de risco para a saúde pública ---------------------
    def PredictHealthRisk(self, request, context):        # noqa: N802
        pontos = _filtrar(request)
        log.info("PredictHealthRisk | tipo=%s n=%d", request.tipo_dado, len(pontos))

        if not pontos:
            return pb.HealthRiskResponse(
                indicador      = request.tipo_dado or "?",
                nivel_risco    = "SEM_DADOS",
                recomendacao   = "Não há dados suficientes para previsão.",
            )

        valores = [p.valor for p in pontos]
        xs = list(range(len(valores)))
        declive, b = _regressao_linear(xs, valores)
        previsto = declive * len(valores) + b   # extrapolação 1 passo

        tipo = (request.tipo_dado or pontos[-1].tipo_dado).upper()
        nivel = "DESCONHECIDO"
        recomendacao = "Sem critérios definidos para este tipo."
        if tipo in LIMIARES_RISCO:
            for limite, nv, rec in LIMIARES_RISCO[tipo]:
                if previsto < limite:
                    nivel, recomendacao = nv, rec
                    break

        return pb.HealthRiskResponse(
            indicador          = tipo,
            valor_previsto     = round(previsto, 4),
            nivel_risco        = nivel,
            recomendacao       = recomendacao,
            grupos_vulneraveis = GRUPOS_VULNERAVEIS.get(tipo, []),
        )

    # ---- Healthcheck -------------------------------------------------
    def Ping(self, request, context):                     # noqa: N802
        return pb.PingResponse(
            mensagem  = f"pong from analysis (saudação a {request.origem})",
            timestamp = datetime.now(timezone.utc).isoformat(),
        )


# ---------------------------------------------------------------------
def main() -> None:
    porta = 50052
    if len(sys.argv) > 1:
        porta = int(sys.argv[1])

    server = grpc.server(futures.ThreadPoolExecutor(max_workers=10))
    pb_grpc.add_AnalysisServiceServicer_to_server(AnalysisServicer(), server)
    server.add_insecure_port(f"[::]:{porta}")
    server.start()
    log.info("Serviço de Análise a escutar na porta %d", porta)

    try:
        while True:
            time.sleep(3600)
    except KeyboardInterrupt:
        log.info("Encerrando...")
        server.stop(grace=2)


if __name__ == "__main__":
    main()
