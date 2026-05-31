// =====================================================================
//  Servidor.cs
//  --------------------------------------------------------------------
//  Servidor Principal TP2 — One Health
//
//  Responsabilidades:
//    1) Aceitar ligações dos Gateways e armazenar as medições
//       normalizadas em SQLite.
//    2) Aceitar ligações da Interface de Visualização (CLI/Web) e
//       responder a QUERY (dados) e ANALYSE (pedidos de análise).
//    3) Para ANALYSE, invocar via gRPC o serviço Python de Análise
//       (estatísticas, padrões, risco de saúde).
//
//  Argumentos:
//     Servidor <porta> <ficheiroBd> <hostGrpc> <portaGrpc>
//     defaults:  9000   dados/onehealth.db   localhost   50052
// =====================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using Google.Protobuf.Collections;
using Grpc.Net.Client;
using OneHealth.Analysis;

namespace OneHealth.TP2.Servidor
{
    internal class Program
    {
        // -----------------------------------------------------------
        // Estado
        // -----------------------------------------------------------
        private static int    _porta;
        private static string _ficheiroBd;
        private static string _hostGrpc;
        private static int    _portaGrpc;

        private static BaseDados _bd;
        private static GrpcChannel _grpcChannel;
        private static AnalysisService.AnalysisServiceClient _analysisClient;

        // -----------------------------------------------------------
        // Main
        // -----------------------------------------------------------
        private static void Main(string[] args)
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            Console.OutputEncoding = Encoding.UTF8;

            _porta      = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 9000;
            _ficheiroBd = args.Length > 1 ? args[1] : "dados/onehealth.db";
            _hostGrpc   = args.Length > 2 ? args[2] : "localhost";
            _portaGrpc  = args.Length > 3 && int.TryParse(args[3], out var g) ? g : 50052;

            Console.WriteLine("===========================================");
            Console.WriteLine(" SERVIDOR PRINCIPAL TP2 - One Health");
            Console.WriteLine("===========================================");
            Console.WriteLine($"  Porta TCP        : {_porta}");
            Console.WriteLine($"  BD SQLite        : {_ficheiroBd}");
            Console.WriteLine($"  Análise gRPC     : {_hostGrpc}:{_portaGrpc}");
            Console.WriteLine();

            _bd = new BaseDados(_ficheiroBd);
            LigarServicoGrpcAnalise();

            var listener = new TcpListener(IPAddress.Any, _porta);
            listener.Start();
            Console.WriteLine($"[INFO] A escutar na porta {_porta}...\n");

            while (true)
            {
                var cliente = listener.AcceptTcpClient();
                var t = new Thread(() => TratarCliente(cliente)) { IsBackground = true };
                t.Start();
            }
        }

        // -----------------------------------------------------------
        // gRPC ↔ Serviço de Análise
        // -----------------------------------------------------------
        private static void LigarServicoGrpcAnalise()
        {
            string url = $"http://{_hostGrpc}:{_portaGrpc}";
            _grpcChannel    = GrpcChannel.ForAddress(url);
            _analysisClient = new AnalysisService.AnalysisServiceClient(_grpcChannel);
            try
            {
                var pong = _analysisClient.Ping(new PingRequest { Origem = "Servidor" });
                Console.WriteLine($"[gRPC] Análise OK: {pong.Mensagem}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[gRPC] AVISO: serviço de análise indisponível ({ex.Message}).");
            }
        }

        // -----------------------------------------------------------
        // Tratamento de cada ligação (gateway OU interface)
        // -----------------------------------------------------------
        private static void TratarCliente(TcpClient cliente)
        {
            string identificador = $"{cliente.Client.RemoteEndPoint}";
            try
            {
                using (cliente)
                using (var stream = cliente.GetStream())
                {
                    var leitor   = new StreamReader(stream, Encoding.UTF8);
                    var escritor = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

                    string linha;
                    while ((linha = leitor.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(linha)) continue;
                        var campos = Protocolo.Parse(linha);
                        if (campos.Length == 0) continue;

                        switch (campos[0])
                        {
                            case Msg.GATEWAY_CONNECT:
                                identificador = campos.Length > 1 ? campos[1] : identificador;
                                Console.WriteLine($"[GATEWAY] '{identificador}' ligou-se.");
                                escritor.WriteLine(Protocolo.AckGateway(true));
                                break;

                            case Msg.STORE_NORM:
                                TratarStoreNorm(campos, escritor, identificador);
                                break;

                            case Msg.QUERY:
                                TratarQuery(campos, escritor);
                                break;

                            case Msg.ANALYSE:
                                TratarAnalyse(campos, escritor);
                                break;

                            default:
                                Console.WriteLine($"[AVISO] Mensagem desconhecida: {linha}");
                                break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERRO] '{identificador}': {ex.Message}");
            }
            finally
            {
                Console.WriteLine($"[FIM] Cliente '{identificador}' desligou-se.");
            }
        }

        // -----------------------------------------------------------
        // STORE_NORM | gw | sensor | zona | tipo | valor | unidade | ts | valido | obs
        // -----------------------------------------------------------
        private static void TratarStoreNorm(string[] campos, StreamWriter escritor, string id)
        {
            if (campos.Length < 10)
            {
                escritor.WriteLine(Protocolo.AckStore(false, "Formato invalido"));
                return;
            }

            try
            {
                string gw      = campos[1];
                string sensor  = campos[2];
                string zona    = campos[3];
                string tipo    = campos[4];
                double valor   = double.Parse(campos[5], CultureInfo.InvariantCulture);
                string unidade = campos[6];
                string ts      = campos[7];
                bool   valido  = campos[8] == "1";
                string obs     = campos[9];

                _bd.InserirMedicao(gw, sensor, zona, tipo, valor, unidade, ts, valido, obs);
                Console.WriteLine($"[STORE] {gw} -> {sensor} {tipo}={valor}{unidade} z={zona}"
                                + (valido ? "" : "  [INVALIDO]"));
                escritor.WriteLine(Protocolo.AckStore(true));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERRO STORE] {ex.Message}");
                escritor.WriteLine(Protocolo.AckStore(false, ex.Message));
            }
        }

        // -----------------------------------------------------------
        // QUERY | tipo | zona | sensor | inicio | fim | limite
        // resposta: ACK_QUERY | OK | JSON
        // -----------------------------------------------------------
        private static void TratarQuery(string[] campos, StreamWriter escritor)
        {
            string tipo   = Get(campos, 1);
            string zona   = Get(campos, 2);
            string sensor = Get(campos, 3);
            string ini    = Get(campos, 4);
            string fim    = Get(campos, 5);
            int    lim    = int.TryParse(Get(campos, 6), out var l) ? l : 500;

            var dados = _bd.Consultar(tipo, zona, sensor, ini, fim, lim);
            string json = JsonSerializer.Serialize(dados);
            escritor.WriteLine(string.Join('|', Msg.ACK_QUERY, Msg.OK, json));
            Console.WriteLine($"[QUERY] tipo={tipo} zona={zona} sensor={sensor} -> {dados.Count} linhas");
        }

        // -----------------------------------------------------------
        // ANALYSE | STATS|PATTERNS|RISK | tipo | zona | sensor | inicio | fim
        // resposta: ACK_ANALYSE | OK | idAnalise | JSON
        // -----------------------------------------------------------
        private static void TratarAnalyse(string[] campos, StreamWriter escritor)
        {
            string subTipo = Get(campos, 1)?.ToUpper();
            string tipo    = Get(campos, 2);
            string zona    = Get(campos, 3);
            string sensor  = Get(campos, 4);
            string ini     = Get(campos, 5);
            string fim     = Get(campos, 6);

            // 1) Lê os dados que servem de base à análise
            var dados = _bd.Consultar(tipo, zona, sensor, ini, fim, 100000);

            // 2) Constrói o pedido gRPC
            var req = new AnalysisRequest
            {
                TipoDado = tipo ?? "",
                Zona     = zona ?? "",
                SensorId = sensor ?? "",
                Inicio   = ini  ?? "",
                Fim      = fim  ?? "",
            };
            foreach (var m in dados)
            {
                req.Dados.Add(new DataPoint
                {
                    SensorId  = m.SensorId,
                    Zona      = m.Zona,
                    TipoDado  = m.TipoDado,
                    Valor     = m.Valor,
                    Timestamp = m.Timestamp,
                });
            }

            // 3) Invoca o RPC correspondente
            string resultadoJson;
            try
            {
                switch (subTipo)
                {
                    case "STATS":
                        var stats = _analysisClient.ComputeStatistics(req);
                        resultadoJson = ProtoParaJson(new
                        {
                            tipo = "STATS",
                            n = stats.N,
                            stats.Media, stats.Mediana, stats.Minimo, stats.Maximo,
                            stats.DesvioPadrao,
                            P25 = stats.P25, P75 = stats.P75,
                            stats.TipoDado, stats.Zona,
                        });
                        break;

                    case "PATTERNS":
                        var pat = _analysisClient.DetectPatterns(req);
                        resultadoJson = ProtoParaJson(new
                        {
                            tipo = "PATTERNS",
                            tendencia    = pat.Tendencia,
                            inclinacao   = pat.Inclinacao,
                            resumo       = pat.Resumo,
                            anomalias    = pat.Anomalias.Select(a => new
                            {
                                a.SensorId, a.Zona, a.TipoDado, a.Valor, a.Timestamp,
                                ZScore = a.ZScore, a.Severidade, a.Descricao,
                            }),
                        });
                        break;

                    case "RISK":
                        var risk = _analysisClient.PredictHealthRisk(req);
                        resultadoJson = ProtoParaJson(new
                        {
                            tipo = "RISK",
                            indicador        = risk.Indicador,
                            valor_previsto   = risk.ValorPrevisto,
                            nivel_risco      = risk.NivelRisco,
                            recomendacao     = risk.Recomendacao,
                            grupos           = risk.GruposVulneraveis.ToArray(),
                        });
                        break;

                    default:
                        escritor.WriteLine(string.Join('|', Msg.ACK_ANALYSE, Msg.ERR, "subtipo invalido"));
                        return;
                }

                // 4) Guarda na BD e responde
                var paramsJson = JsonSerializer.Serialize(new { tipo, zona, sensor, ini, fim, n = dados.Count });
                long idAn = _bd.GuardarAnalise(subTipo, paramsJson, resultadoJson);

                escritor.WriteLine(string.Join('|', Msg.ACK_ANALYSE, Msg.OK, idAn.ToString(), resultadoJson));
                Console.WriteLine($"[ANALYSE] {subTipo} (n={dados.Count}) -> id={idAn}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERRO ANALYSE] {ex.Message}");
                escritor.WriteLine(string.Join('|', Msg.ACK_ANALYSE, Msg.ERR, ex.Message));
            }
        }

        // -----------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------
        private static string Get(string[] a, int i)
            => (i < a.Length && !string.IsNullOrWhiteSpace(a[i])) ? a[i] : null;

        private static string ProtoParaJson(object o)
            => JsonSerializer.Serialize(o, new JsonSerializerOptions { WriteIndented = false });
    }
}
