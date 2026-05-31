// =====================================================================
//  Gateway.cs
//  --------------------------------------------------------------------
//  Gateway TP2 — One Health
//
//  Responsabilidades:
//    1) Subscrever no RabbitMQ os tópicos das zonas/tipos de dados
//       que este gateway gere.
//    2) Para cada mensagem recebida, invocar via gRPC o serviço de
//       Pré-processamento (uniformização de formatos e unidades).
//    3) Encaminhar a medição normalizada para o Servidor Principal,
//       via TCP/texto, com STORE_NORM.
//
//  Argumentos opcionais:
//      Gateway <gatewayId> <rabbitHost> <ipServidor>
//              <portaServidor> <hostGrpc> <portaGrpc>
//              <zonas> <tipos>
//   Ex.:
//      dotnet run -- GW01 localhost 127.0.0.1 9000 \
//                    localhost 50051 ZONA_CENTRO,ZONA_ESCOLAR  TEMP,HUM,PM2.5
//      dotnet run -- GW01                                 # tudo default
// =====================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Net.Client;
using OneHealth.PreProcessing;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace OneHealth.TP2.Gateway
{
    internal class Program
    {
        // -----------------------------------------------------------
        // Estado partilhado
        // -----------------------------------------------------------
        private const string EXCHANGE       = "sensores";
        private const string CSV_SENSORES   = "dados/sensores.csv";

        private static string _gatewayId;
        private static string _rabbitHost;
        private static string _ipServidor;
        private static int    _portaServidor;
        private static string _hostGrpc;
        private static int    _portaGrpc;
        private static string[] _zonas;
        private static string[] _tipos;

        // ligação ao Servidor (partilhada por todos os consumers)
        private static readonly object   _lockServidor = new();
        private static TcpClient         _clienteServidor;
        private static StreamWriter      _escritor;
        private static StreamReader      _leitor;

        // stub gRPC para o serviço de pré-processamento
        private static GrpcChannel                       _grpcChannel;
        private static PreProcessingService.PreProcessingServiceClient _preProcClient;

        // catálogo de sensores válidos (carregado do CSV)
        private static readonly Dictionary<string, InfoSensor> _sensores = new();
        private class InfoSensor
        {
            public string Estado   { get; set; }   // ativo | manutencao | desativado
            public string Zona     { get; set; }
            public List<string> Tipos { get; set; }
        }

        // -----------------------------------------------------------
        // Main
        // -----------------------------------------------------------
        private static void Main(string[] args)
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            Console.OutputEncoding = Encoding.UTF8;

            _gatewayId      = args.Length > 0 ? args[0]                            : "GW01";
            _rabbitHost     = args.Length > 1 ? args[1]                            : "localhost";
            _ipServidor     = args.Length > 2 ? args[2]                            : "127.0.0.1";
            _portaServidor  = args.Length > 3 && int.TryParse(args[3], out var ps) ? ps : 9000;
            _hostGrpc       = args.Length > 4 ? args[4]                            : "localhost";
            _portaGrpc      = args.Length > 5 && int.TryParse(args[5], out var pg) ? pg : 50051;
            _zonas          = args.Length > 6 ? args[6].Split(',')                  : new[] { "#" };  // # == todas
            _tipos          = args.Length > 7 ? args[7].Split(',')                  : new[] { "#" };

            Console.WriteLine("===========================================");
            Console.WriteLine($" GATEWAY TP2  |  {_gatewayId}");
            Console.WriteLine("===========================================");
            Console.WriteLine($"  RabbitMQ        : {_rabbitHost}");
            Console.WriteLine($"  Servidor TCP    : {_ipServidor}:{_portaServidor}");
            Console.WriteLine($"  Pré-proc gRPC   : {_hostGrpc}:{_portaGrpc}");
            Console.WriteLine($"  Zonas           : {string.Join(',', _zonas)}");
            Console.WriteLine($"  Tipos           : {string.Join(',', _tipos)}");
            Console.WriteLine();

            CarregarCSVSensores();
            LigarServicoGrpc();
            LigarServidor();
            IniciarSubscricaoRabbit();

            Console.WriteLine("[INFO] Pressione ENTER para encerrar...");
            Console.ReadLine();
        }

        // -----------------------------------------------------------
        // gRPC ↔ Pré-processamento
        // -----------------------------------------------------------
        private static void LigarServicoGrpc()
        {
            string url = $"http://{_hostGrpc}:{_portaGrpc}";
            _grpcChannel   = GrpcChannel.ForAddress(url);
            _preProcClient = new PreProcessingService.PreProcessingServiceClient(_grpcChannel);

            try
            {
                var pong = _preProcClient.Ping(new PingRequest { Origem = _gatewayId });
                Console.WriteLine($"[gRPC] Pré-processamento OK: {pong.Mensagem}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[gRPC] AVISO: serviço de pré-processamento indisponível: {ex.Message}");
                Console.WriteLine("       Vou tentar invocá-lo na mesma a cada mensagem.");
            }
        }

        // -----------------------------------------------------------
        // TCP ↔ Servidor Principal
        // -----------------------------------------------------------
        private static void LigarServidor()
        {
            try
            {
                _clienteServidor = new TcpClient(_ipServidor, _portaServidor);
                var ns = _clienteServidor.GetStream();
                _escritor = new StreamWriter(ns, Encoding.UTF8) { AutoFlush = true };
                _leitor   = new StreamReader(ns, Encoding.UTF8);

                _escritor.WriteLine(Protocolo.GatewayConnect(_gatewayId));
                string resposta = _leitor.ReadLine();
                var campos = Protocolo.Parse(resposta ?? "");

                if (campos.Length > 1 && campos[1] == Msg.OK)
                    Console.WriteLine($"[SERVIDOR] Ligação estabelecida ({_ipServidor}:{_portaServidor})");
                else
                    Console.WriteLine("[SERVIDOR] Ligação rejeitada");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERRO] Não foi possível ligar ao servidor: {ex.Message}");
            }
        }

        private static bool EnviarParaServidor(string mensagem)
        {
            lock (_lockServidor)
            {
                try
                {
                    _escritor.WriteLine(mensagem);
                    string resposta = _leitor.ReadLine();
                    var campos = Protocolo.Parse(resposta ?? "");
                    return campos.Length > 1 && campos[1] == Msg.OK;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERRO TCP] {ex.Message} — a tentar reconectar...");
                    try { LigarServidor(); } catch { }
                    return false;
                }
            }
        }

        // -----------------------------------------------------------
        // RabbitMQ — subscrição
        // -----------------------------------------------------------
        private static void IniciarSubscricaoRabbit()
        {
            var factory = new ConnectionFactory { HostName = _rabbitHost };
            var conn    = factory.CreateConnection($"gateway-{_gatewayId}");
            var canal   = conn.CreateModel();

            canal.ExchangeDeclare(EXCHANGE, ExchangeType.Topic,
                                  durable: true, autoDelete: false);

            // fila exclusiva por gateway, com nome estável
            string nomeFila = $"gw.{_gatewayId}";
            canal.QueueDeclare(nomeFila, durable: true, exclusive: false, autoDelete: false);

            // binding por cada combinação zona.tipo (suporta '#')
            foreach (var z in _zonas)
                foreach (var t in _tipos)
                {
                    string rk = $"{z}.{t}";
                    canal.QueueBind(nomeFila, EXCHANGE, rk);
                    Console.WriteLine($"[SUB] binding routingKey='{rk}'");
                }

            // QoS: processa 1 a 1 (mais previsível para o RPC)
            canal.BasicQos(prefetchSize: 0, prefetchCount: 1, global: false);

            var consumer = new EventingBasicConsumer(canal);
            consumer.Received += (sender, ea) => TratarMensagem(canal, ea);

            canal.BasicConsume(nomeFila, autoAck: false, consumer: consumer);
            Console.WriteLine($"[INFO] A consumir fila '{nomeFila}'.\n");
        }

        // -----------------------------------------------------------
        // Tratamento de uma mensagem vinda do broker
        // -----------------------------------------------------------
        private static void TratarMensagem(IModel canal, BasicDeliverEventArgs ea)
        {
            string payload = Encoding.UTF8.GetString(ea.Body.ToArray());

            // Recupera metadados dos headers
            var headers = ea.BasicProperties?.Headers;
            string sensorId = ObterHeader(headers, "sensor_id") ?? "?";
            string zona     = ObterHeader(headers, "zona")      ?? "?";
            string tipo     = ObterHeader(headers, "tipo")      ?? "?";
            string formato  = ObterHeader(headers, "formato")   ?? "RAW";
            string unidade  = ObterHeader(headers, "unidade")   ?? "";
            string ts       = ObterHeader(headers, "ts")        ?? DateTime.UtcNow.ToString("o");

            Console.WriteLine($"[RECV] {ea.RoutingKey,-25} from={sensorId,-5} fmt={formato,-4}");

            // 1) Validação de catálogo (mantém a regra do TP1)
            if (_sensores.TryGetValue(sensorId, out var info))
            {
                if (info.Estado != "ativo")
                {
                    Console.WriteLine($"[DROP] {sensorId} não está ativo (estado={info.Estado}).");
                    canal.BasicAck(ea.DeliveryTag, false);
                    return;
                }
                if (!info.Tipos.Contains(tipo))
                {
                    Console.WriteLine($"[DROP] {sensorId} não suporta tipo {tipo}.");
                    canal.BasicAck(ea.DeliveryTag, false);
                    return;
                }
            }
            // (sensor desconhecido é tolerado: continua-se na mesma)

            // 2) RPC ao Pré-processamento
            NormalizedMeasurement norm;
            try
            {
                norm = _preProcClient.Normalize(new RawMeasurement
                {
                    SensorId  = sensorId,
                    Zona      = zona,
                    TipoDado  = tipo,
                    Payload   = payload,
                    Formato   = formato,
                    Unidade   = unidade,
                    Timestamp = ts,
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERRO gRPC] {ex.Message} — descartar mensagem.");
                // requeue=false para não entrar em loop infinito
                canal.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
                return;
            }

            Console.WriteLine($"[NORM] {norm.TipoDado}={norm.Valor} {norm.UnidadeSi}"
                            + (norm.Valido ? "  OK" : $"  INVALIDO ({norm.Observacoes})"));

            // 3) Envia para o Servidor
            bool ok = EnviarParaServidor(Protocolo.StoreNorm(
                _gatewayId, norm.SensorId, norm.Zona, norm.TipoDado,
                norm.Valor, norm.UnidadeSi, norm.Timestamp,
                norm.Valido, norm.Observacoes));

            if (ok) canal.BasicAck(ea.DeliveryTag, false);
            else    canal.BasicNack(ea.DeliveryTag, false, requeue: true);
        }

        // -----------------------------------------------------------
        // CSV de sensores
        // -----------------------------------------------------------
        private static void CarregarCSVSensores()
        {
            if (!File.Exists(CSV_SENSORES))
            {
                Console.WriteLine($"[CSV] '{CSV_SENSORES}' não encontrado — sem catálogo (todos os sensores serão aceites).");
                return;
            }

            int n = 0;
            foreach (var linha in File.ReadAllLines(CSV_SENSORES))
            {
                if (string.IsNullOrWhiteSpace(linha) || linha.StartsWith("sensor_id")) continue;
                var p = linha.Split(':');
                if (p.Length < 4) continue;

                _sensores[p[0]] = new InfoSensor
                {
                    Estado = p[1],
                    Zona   = p[2],
                    Tipos  = new List<string>(p[3].Trim('[', ']').Split(',')),
                };
                n++;
            }
            Console.WriteLine($"[CSV] {n} sensores carregados.");
        }

        // -----------------------------------------------------------
        // Util: lê header AMQP (que vem como byte[] do RabbitMQ.Client)
        // -----------------------------------------------------------
        private static string ObterHeader(IDictionary<string, object> headers, string chave)
        {
            if (headers == null || !headers.TryGetValue(chave, out var v) || v == null)
                return null;
            return v is byte[] bytes ? Encoding.UTF8.GetString(bytes) : v.ToString();
        }
    }
}
