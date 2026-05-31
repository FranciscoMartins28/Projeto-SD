# TP2 — Serviços de Análise e Monitorização Urbana para One Health

> **Sistemas Distribuídos 2025/2026 — UTAD / ECT / DE**
> Evolução do TP1 para uma arquitetura distribuída com **Pub/Sub (RabbitMQ)** e **RPC (gRPC)**.

---

## 1. Visão geral

```
 SENSORES (C#)
     │  PUB (RabbitMQ topic)
     ▼
 ┌──────────────┐    gRPC    ┌──────────────────────┐
 │  GATEWAY(s)  │ ─────────► │ Pré-processamento    │  (Python)
 │     (C#)     │            │ unifica formatos/un. │
 └──────┬───────┘            └──────────────────────┘
        │ TCP texto (STORE_NORM)
        ▼
 ┌──────────────┐    gRPC    ┌──────────────────────┐
 │  SERVIDOR    │ ─────────► │ Análise & Previsão   │  (Python)
 │  PRINCIPAL   │            │ stats / padrões / risco │
 │   + SQLite   │            └──────────────────────┘
 │     (C#)     │
 └──────┬───────┘
        │ TCP texto (QUERY / ANALYSE)
        ▼
 INTERFACE  CLI (C#)  +  WEB (Python/Flask)
```

* **Sensores → Gateways**: Publish/Subscribe via **RabbitMQ** (`exchange=sensores`, tipo `topic`,
  *routing-key* = `zona.tipo` → ex.: `ZONA_CENTRO.TEMP`).
* **Gateway ↔ Pré-processamento**: **gRPC** (`preprocessing.proto`, porta `50051`).
* **Gateway → Servidor**: TCP, protocolo de texto delimitado por `|` (mantido do TP1).
* **Servidor ↔ Análise**: **gRPC** (`analysis.proto`, porta `50052`).
* **Persistência**: **SQLite** (BD relacional sem servidor).
* **Visualização**: CLI em C# + Dashboard Web em Flask (mesmo protocolo TCP).

A escolha de **linguagens diferentes** (C# para sensores/gateways/servidor, Python para serviços RPC
e dashboard web) constitui um dos factores de **valorização** previstos no enunciado.

---

## 2. Pré-requisitos

| Componente              | Necessário                                    |
|-------------------------|-----------------------------------------------|
| .NET SDK                | .NET 9 (ou superior)                          |
| Python                  | 3.10+                                         |
| RabbitMQ                | Docker (recomendado) ou instalação local      |
| Bibliotecas Python      | `grpcio grpcio-tools protobuf flask` (ver `requirements.txt` em cada pasta) |
| Pacotes NuGet           | resolvidos automaticamente pelo `dotnet restore` |

### Arranque rápido do RabbitMQ (Docker)

```bash
docker run -d --name rabbit-onehealth \
  -p 5672:5672 -p 15672:15672 rabbitmq:3-management
```

UI de gestão: <http://localhost:15672> (utilizador/pwd default: `guest/guest`).

---

## 3. Geração dos stubs gRPC (Python)

```bash
# Pré-processamento
cd PreProcessingService
pip install -r requirements.txt
./gen_stubs.sh                # ou: python -m grpc_tools.protoc ...
cd ..

# Análise
cd AnalysisService
pip install -r requirements.txt
./gen_stubs.sh
cd ..
```

Em C# os stubs são gerados automaticamente em *build-time* através de `Grpc.Tools`
e dos elementos `<Protobuf Include="../proto/...">` nos `.csproj`.

---

## 4. Sequência de arranque

Abrir **6 terminais** (esta é a ordem recomendada):

```bash
# 1) RabbitMQ                                  (Docker, ver acima)

# 2) Serviço de Pré-processamento (Python, gRPC :50051)
cd PreProcessingService && python preprocessing_server.py

# 3) Serviço de Análise (Python, gRPC :50052)
cd AnalysisService && python analysis_server.py

# 4) Servidor Principal (C#, TCP :9000  +  SQLite)
cd Servidor && dotnet run

# 5) Gateway(s) (C#)
cd Gateway && dotnet run -- GW01 localhost 127.0.0.1 9000 \
                            localhost 50051 "#" "#"

# 6) Sensores (C#)
cd Sensor && dotnet run -- S101 ZONA_CENTRO   auto localhost 3000
# em paralelo, mais sensores:
cd Sensor && dotnet run -- S102 ZONA_ESCOLAR  auto localhost 4000
cd Sensor && dotnet run -- S105 ZONA_CENTRO   manual
```

### Visualização

```bash
# CLI (C#)
cd Interface && dotnet run -- 127.0.0.1 9000

# Web (Python / Flask)  →  http://localhost:8080
cd Interface && pip install -r requirements.txt && python web_interface.py
```

---

## 5. Mensagens trocadas (resumo)

### 5.1. Pub/Sub (Sensor → Gateway, via RabbitMQ)

* **exchange** : `sensores`  (`topic`, `durable=true`)
* **routing-key** : `<ZONA>.<TIPO>`  (ex.: `ZONA_CENTRO.TEMP`)
* **body** : valor em RAW / JSON / XML / CSV (o formato é declarado nos *headers*)
* **headers AMQP** (todos string) :
  `sensor_id`, `zona`, `tipo`, `formato`, `unidade`, `ts`

### 5.2. gRPC — Pré-processamento

Ver `proto/preprocessing.proto`. Chamadas:

* `Normalize(RawMeasurement) → NormalizedMeasurement`
* `NormalizeBatch(BatchRequest) → BatchResponse`
* `Ping(PingRequest) → PingResponse`

### 5.3. TCP — Gateway → Servidor

* `GATEWAY_CONNECT|<gw>`  ⇄  `ACK_GATEWAY|OK`
* `STORE_NORM|<gw>|<sensor>|<zona>|<tipo>|<valor>|<un>|<ts>|<valido>|<obs>`  ⇄  `ACK_STORE|OK`

### 5.4. TCP — Interface → Servidor

* `QUERY|<tipo>|<zona>|<sensor>|<ini>|<fim>|<lim>`
  → `ACK_QUERY|OK|<json-de-medições>`
* `ANALYSE|<STATS|PATTERNS|RISK>|<tipo>|<zona>|<sensor>|<ini>|<fim>`
  → `ACK_ANALYSE|OK|<id>|<json-resultado>`

### 5.5. gRPC — Análise

Ver `proto/analysis.proto`. Chamadas:

* `ComputeStatistics(AnalysisRequest) → StatisticsResponse`
* `DetectPatterns  (AnalysisRequest) → PatternResponse`
* `PredictHealthRisk(AnalysisRequest) → HealthRiskResponse`

---

## 6. Estrutura do repositório

```
TP2_OneHealth/
├── proto/                        contratos .proto (fonte da verdade)
│   ├── preprocessing.proto
│   └── analysis.proto
├── Sensor/                       C# — publicador RabbitMQ
├── Gateway/                      C# — subscritor + cliente gRPC + cliente TCP
│   └── dados/sensores.csv        catálogo de sensores (idem TP1)
├── Servidor/                     C# — servidor TCP + SQLite + cliente gRPC
├── Interface/
│   ├── Interface.cs              CLI em C#
│   └── web_interface.py          Dashboard Flask
├── PreProcessingService/         Python — serviço gRPC :50051
├── AnalysisService/              Python — serviço gRPC :50052
├── docs/                         relatório técnico (4 páginas) + diagramas
└── TP2_OneHealth.sln             solução .NET (todos os projetos C#)
```

---

## 7. Faseamento implementado (conforme enunciado)

1. **RPC** — `preprocessing.proto` e `analysis.proto`; implementação Python; ligação a partir dos componentes C#.
2. **Pub/Sub** — substituição da comunicação direta Sensor→Gateway por publicação em RabbitMQ; subscrição por *routing-key* (`zona.tipo`).
3. **Funcionalidades adicionais** — persistência em SQLite (`medicoes`, `analises`), interface CLI e dashboard Flask, com parametrização (zona, tipo, sensor, intervalo temporal).

---

## 8. Notas de implementação

* O **catálogo de sensores** (CSV do TP1) é mantido no Gateway para validação (estado, zona, tipos suportados).
* O **timestamp** sai do sensor em ISO-8601 UTC e atravessa todo o sistema sem reescrita até chegar à BD.
* O serviço de pré-processamento devolve `valido=false` em vez de rejeitar, para não perder evidência de problemas (a BD guarda o registo com a respectiva observação).
* A BD usa índices em `(tipo_dado, timestamp)`, `zona` e `sensor_id` para tornar as consultas/análises eficientes.
* As análises são **idempotentes** do ponto de vista do serviço Python (sem estado entre chamadas); o ID guardado em `analises` permite manter histórico das execuções na interface.
