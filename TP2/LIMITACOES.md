# Limitações conhecidas e trabalho futuro

Lista honesta das limitações da implementação atual, para referência e melhoria futura.

## Segurança
- Os canais gRPC usam ligações inseguras (`insecure_port` / `http://`).
  Em produção dever-se-ia usar TLS. Aceitável no contexto académico, em rede local.

## Escalabilidade
- A ligação TCP Gateway->Servidor é serializada com um lock global e leitura
  síncrona. Com `prefetchCount=1` funciona bem, mas múltiplos gateways
  concorrentes ao mesmo servidor seriam um gargalo.

## Análise
- A previsão de risco (`PredictHealthRisk`) extrapola sobre o índice das
  amostras, não sobre o tempo real. Com medições irregulares no tempo, deve
  ler-se como "tendência projetada" e não como previsão temporal rigorosa.

## Robustez
- O parsing do catálogo de sensores (`sensores.csv`) assume ordem fixa das
  colunas e usa apenas os primeiros 4 campos por índice.