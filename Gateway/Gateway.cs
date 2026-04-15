// ============================================================
// Gateway.cs  –  Agregação e encaminhamento de dados
// TP1 Sistemas Distribuídos 2025/2026
// Porta sensores : 8000  |  Servidor : localhost:9000
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace TP1
{
    class Gateway
    {
        // --------------------------------------------------------
        // Configuração
        // --------------------------------------------------------
        private const int    PORTA_SENSORES     = 8000;
        private const string IP_SERVIDOR        = "127.0.0.1";
        private const int    PORTA_SERVIDOR     = 9000;
        private const string GATEWAY_ID         = "GW01";
        private const string FICHEIRO_SENSORES  = "dados/sensores.csv";
        private const int    TIMEOUT_HEARTBEAT  = 30; // segundos

        // --------------------------------------------------------
        // Estado partilhado (acesso protegido por lock)
        // --------------------------------------------------------
        private static readonly object _lockCSV = new object();

        // sensorId → InfoSensor
        private static readonly Dictionary<string, InfoSensor> _sensores
            = new Dictionary<string, InfoSensor>();

        // Ligação persistente ao servidor
        private static TcpClient    _clienteServidor;
        private static StreamWriter _escritorServidor;
        private static StreamReader _leitorServidor;
        private static readonly object _lockServidor = new object();

        // --------------------------------------------------------
        // Estrutura de informação de sensor
        // --------------------------------------------------------
        private class InfoSensor
        {
            public string   Estado      { get; set; }
            public string   Zona        { get; set; }
            public List<string> TiposDados { get; set; }
            public DateTime UltimoSync  { get; set; }
        }

        // --------------------------------------------------------
        // Ponto de entrada
        // --------------------------------------------------------
        static void Main(string[] args)
        {
            Console.WriteLine("=== GATEWAY TP1 – One Health ===");

            // 1. Carregar configuração de sensores
            CarregarCSV();

            // 2. Ligar ao servidor
            LigarAoServidor();

            // 3. Thread de monitorização de heartbeats
            Thread monitorThread = new Thread(MonitorizarHeartbeats);
            monitorThread.IsBackground = true;
            monitorThread.Start();

            // 4. Escutar sensores
            Console.WriteLine($"A escutar sensores na porta {PORTA_SENSORES}...\n");
            TcpListener listener = new TcpListener(IPAddress.Any, PORTA_SENSORES);
            listener.Start();

            while (true)
            {
                TcpClient sensor = listener.AcceptTcpClient();
                Thread t = new Thread(() => TratarSensor(sensor));
                t.IsBackground = true;
                t.Start();
            }
        }

        // --------------------------------------------------------
        // Ligação ao servidor (com handshake GATEWAY_CONNECT)
        // --------------------------------------------------------
        private static void LigarAoServidor()
        {
            try
            {
                _clienteServidor  = new TcpClient(IP_SERVIDOR, PORTA_SERVIDOR);
                NetworkStream ns  = _clienteServidor.GetStream();
                _escritorServidor = new StreamWriter(ns, Encoding.UTF8) { AutoFlush = true };
                _leitorServidor   = new StreamReader(ns, Encoding.UTF8);

                _escritorServidor.WriteLine(Protocolo.GatewayConnect(GATEWAY_ID));
                string resposta = _leitorServidor.ReadLine();
                string[] campos = Protocolo.Parse(resposta ?? "");

                if (campos.Length > 1 && campos[1] == Msg.OK)
                    Console.WriteLine($"[SERVIDOR] Ligação estabelecida com {IP_SERVIDOR}:{PORTA_SERVIDOR}");
                else
                    Console.WriteLine("[SERVIDOR] Ligação rejeitada.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERRO] Não foi possível ligar ao servidor: {ex.Message}");
            }
        }

        // --------------------------------------------------------
        // Tratamento de sensor (thread por sensor – fase 4)
        // --------------------------------------------------------
        private static void TratarSensor(TcpClient cliente)
        {
            string sensorId = "?";
            try
            {
                using (cliente)
                using (NetworkStream stream = cliente.GetStream())
                {
                    StreamReader  leitor   = new StreamReader(stream, Encoding.UTF8);
                    StreamWriter  escritor = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

                    string linha;
                    while ((linha = leitor.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(linha)) continue;
                        string[] campos = Protocolo.Parse(linha);

                        switch (campos[0])
                        {
                            // ---- CONNECT ----
                            case Msg.CONNECT:
                            {
                                // CONNECT|sensorId|tipoDado1,tipoDado2,...
                                sensorId = campos.Length > 1 ? campos[1] : "?";
                                InfoSensor info = ValidarSensor(sensorId);

                                if (info == null)
                                {
                                    Console.WriteLine($"[CONNECT] '{sensorId}' REJEITADO (não registado).");
                                    escritor.WriteLine(Protocolo.AckConnect(false, "sensor nao registado"));
                                }
                                else if (info.Estado == "desativado")
                                {
                                    Console.WriteLine($"[CONNECT] '{sensorId}' REJEITADO (desativado).");
                                    escritor.WriteLine(Protocolo.AckConnect(false, "sensor desativado"));
                                }
                                else if (info.Estado == "manutencao")
                                {
                                    Console.WriteLine($"[CONNECT] '{sensorId}' aceite (em manutenção, dados recebidos na mesma).");
                                    AtualizarSync(sensorId);
                                    escritor.WriteLine(Protocolo.AckConnect(true));
                                }
                                else
                                {
                                    Console.WriteLine($"[CONNECT] '{sensorId}' ({info.Zona}) ligou-se.");
                                    AtualizarSync(sensorId);
                                    escritor.WriteLine(Protocolo.AckConnect(true));
                                }
                                break;
                            }

                            // ---- HEARTBEAT ----
                            case Msg.HEARTBEAT:
                            {
                                AtualizarSync(sensorId);
                                Console.WriteLine($"[HB] {sensorId} @ {(campos.Length > 2 ? campos[2] : "?")}");
                                break;
                            }

                            // ---- DATA ----
                            case Msg.DATA:
                            {
                                // DATA|sensorId|zona|tipoDado|valor|timestamp
                                if (campos.Length < 6)
                                {
                                    escritor.WriteLine(Protocolo.AckData(false, "Formato invalido"));
                                    break;
                                }
                                string sid       = campos[1];
                                string zona      = campos[2];
                                string tipoDado  = campos[3];
                                string valor     = campos[4];
                                string timestamp = campos[5];

                                InfoSensor info  = ValidarSensor(sid);
                                if (info == null)
                                {
                                    escritor.WriteLine(Protocolo.AckData(false, "sensor desconhecido"));
                                    break;
                                }
                                if (!info.TiposDados.Contains(tipoDado))
                                {
                                    escritor.WriteLine(Protocolo.AckData(false, $"tipo '{tipoDado}' nao suportado"));
                                    break;
                                }

                                // Encaminhar para servidor
                                bool ok = EnviarParaServidor(
                                    Protocolo.Store(sid, zona, tipoDado, valor, timestamp));

                                AtualizarSync(sid);
                                Console.WriteLine($"[DATA] {sid} {zona} {tipoDado}={valor} → servidor: {(ok ? "OK" : "FALHOU")}");
                                escritor.WriteLine(Protocolo.AckData(ok, ok ? "" : "erro no servidor"));
                                break;
                            }

                            // ---- VIDEO_STREAM ----
                            case Msg.VIDEO_STREAM:
                            {
                                // Processamento edge simulado
                                Console.WriteLine($"[VIDEO] Stream de {sensorId} recebida (processamento edge simulado).");
                                escritor.WriteLine(Protocolo.AckVideo(true));
                                break;
                            }

                            // ---- DISCONNECT ----
                            case Msg.DISCONNECT:
                            {
                                Console.WriteLine($"[DISCONNECT] {sensorId} desligou-se.");
                                escritor.WriteLine(Protocolo.AckDisconnect());
                                return; // Fechar ligação
                            }

                            default:
                                Console.WriteLine($"[AVISO] Mensagem desconhecida: {linha}");
                                break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERRO] Sensor '{sensorId}': {ex.Message}");
            }
        }

        // --------------------------------------------------------
        // Envio de mensagem ao servidor (protegido por lock – fase 4)
        // --------------------------------------------------------
        private static bool EnviarParaServidor(string mensagem)
        {
            lock (_lockServidor)
            {
                try
                {
                    _escritorServidor.WriteLine(mensagem);
                    string resposta = _leitorServidor.ReadLine();
                    string[] campos = Protocolo.Parse(resposta ?? "");
                    return campos.Length > 1 && campos[1] == Msg.OK;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERRO] Servidor: {ex.Message}");
                    return false;
                }
            }
        }

        // --------------------------------------------------------
        // Carregar/guardar CSV de sensores (protegido por lock)
        // --------------------------------------------------------
        private static void CarregarCSV()
        {
            lock (_lockCSV)
            {
                _sensores.Clear();
                if (!File.Exists(FICHEIRO_SENSORES)) return;

                string[] linhas = File.ReadAllLines(FICHEIRO_SENSORES, Encoding.UTF8);
                foreach (string linha in linhas)
                {
                    if (linha.StartsWith("sensor_id") || string.IsNullOrWhiteSpace(linha))
                        continue;
                    // sensor_id:estado:zona:[tipos_dados]:last_sync
                    string[] p = linha.Split(':');
                    if (p.Length < 5) continue;

                    string sid    = p[0];
                    string estado = p[1];
                    string zona   = p[2];
                    // p[3] = "[TEMP,HUM]"
                    string tiposRaw = p[3].Trim('[', ']');
                    List<string> tipos = new List<string>(
                        tiposRaw.Split(new[]{','}, StringSplitOptions.RemoveEmptyEntries));

                    DateTime sync = DateTime.TryParse(p[4], out DateTime d) ? d : DateTime.Now;

                    _sensores[sid] = new InfoSensor
                    {
                        Estado     = estado,
                        Zona       = zona,
                        TiposDados = tipos,
                        UltimoSync = sync
                    };
                }
                Console.WriteLine($"[CSV] {_sensores.Count} sensores carregados.");
            }
        }

        private static void GuardarCSV()
        {
            lock (_lockCSV)
            {
                using (StreamWriter sw = new StreamWriter(FICHEIRO_SENSORES, false, Encoding.UTF8))
                {
                    sw.WriteLine("sensor_id:estado:zona:tipos_dados:last_sync");
                    foreach (var kv in _sensores)
                    {
                        string tipos = $"[{string.Join(",", kv.Value.TiposDados)}]";
                        sw.WriteLine(
                            $"{kv.Key}:{kv.Value.Estado}:{kv.Value.Zona}:{tipos}:{kv.Value.UltimoSync:o}");
                    }
                }
            }
        }

        // --------------------------------------------------------
        // Validação e atualização de estado
        // --------------------------------------------------------
        private static InfoSensor ValidarSensor(string sid)
        {
            lock (_lockCSV)
            {
                _sensores.TryGetValue(sid, out InfoSensor info);
                return info;
            }
        }

        private static void AtualizarSync(string sid)
        {
            lock (_lockCSV)
            {
                if (_sensores.TryGetValue(sid, out InfoSensor info))
                {
                    info.UltimoSync = DateTime.Now;
                    GuardarCSV();
                }
            }
        }

        // --------------------------------------------------------
        // Monitorização de heartbeats (fase 3/4)
        // --------------------------------------------------------
        private static void MonitorizarHeartbeats()
        {
            while (true)
            {
                Thread.Sleep(10_000); // verificar a cada 10 segundos
                lock (_lockCSV)
                {
                    foreach (var kv in _sensores)
                    {
                        if (kv.Value.Estado != "ativo") continue;
                        double segundos = (DateTime.Now - kv.Value.UltimoSync).TotalSeconds;
                        if (segundos > TIMEOUT_HEARTBEAT)
                        {
                            Console.WriteLine(
                                $"[HEARTBEAT] AVISO: sensor '{kv.Key}' sem heartbeat há {(int)segundos}s.");
                        }
                    }
                }
            }
        }
    }
}
