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
        private const int PORTA_SENSORES = 8000;
        private const string IP_SERVIDOR = "127.0.0.1";
        private const int PORTA_SERVIDOR = 9000;
        private const string GATEWAY_ID = "GW01";
        private const string FICHEIRO_SENSORES = "dados/sensores.csv";
        private const int TIMEOUT_HEARTBEAT = 30;

        private static readonly object _lockCSV = new object();
        private static readonly object _lockServidor = new object();

        private static readonly Dictionary<string, InfoSensor> _sensores
            = new Dictionary<string, InfoSensor>();

        private static TcpClient _clienteServidor;
        private static StreamWriter _escritorServidor;
        private static StreamReader _leitorServidor;

        private class InfoSensor
        {
            public string Estado { get; set; }
            public string Zona { get; set; }
            public List<string> TiposDados { get; set; }
            public DateTime UltimoSync { get; set; }
        }

        static void Main(string[] args)
        {
            Console.WriteLine("=== GATEWAY TP1 – One Health ===");

            CarregarCSV();
            LigarAoServidor();

            Thread monitorThread = new Thread(MonitorizarHeartbeats);
            monitorThread.IsBackground = true;
            monitorThread.Start();

            TcpListener listener = new TcpListener(IPAddress.Any, PORTA_SENSORES);
            listener.Start();

            Console.WriteLine($"A escutar sensores na porta {PORTA_SENSORES}...\n");

            while (true)
            {
                TcpClient sensor = listener.AcceptTcpClient();
                Thread t = new Thread(() => TratarSensor(sensor));
                t.IsBackground = true;
                t.Start();
            }
        }

        private static void LigarAoServidor()
        {
            try
            {
                _clienteServidor = new TcpClient(IP_SERVIDOR, PORTA_SERVIDOR);
                NetworkStream ns = _clienteServidor.GetStream();

                _escritorServidor = new StreamWriter(ns, Encoding.UTF8) { AutoFlush = true };
                _leitorServidor = new StreamReader(ns, Encoding.UTF8);

                _escritorServidor.WriteLine(Protocolo.GatewayConnect(GATEWAY_ID));

                string resposta = _leitorServidor.ReadLine();
                string[] campos = Protocolo.Parse(resposta ?? "");

                if (campos.Length > 1 && campos[1] == Msg.OK)
                    Console.WriteLine("[SERVIDOR] Ligação estabelecida");
                else
                    Console.WriteLine("[SERVIDOR] Ligação rejeitada");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERRO] Servidor: {ex.Message}");
            }
        }

        private static void TratarSensor(TcpClient cliente)
        {
            string sensorId = "?";

            try
            {
                using (cliente)
                using (NetworkStream stream = cliente.GetStream())
                {
                    StreamReader leitor = new StreamReader(stream, Encoding.UTF8);
                    StreamWriter escritor = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

                    string linha;

                    while ((linha = leitor.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(linha)) continue;

                        string[] campos = Protocolo.Parse(linha);

                        if (campos.Length == 0)
                        {
                            Console.WriteLine("[ERRO] Mensagem inválida");
                            continue;
                        }

                        switch (campos[0])
                        {
                            case Msg.CONNECT:
                                {
                                    if (!Protocolo.ValidaCampos(campos, 2))
                                    {
                                        escritor.WriteLine(Protocolo.AckConnect(false, "Formato invalido"));
                                        break;
                                    }

                                    sensorId = campos[1];
                                    InfoSensor info = ValidarSensor(sensorId);

                                    if (info == null)
                                    {
                                        escritor.WriteLine(Protocolo.AckConnect(false, "sensor nao registado"));
                                    }
                                    else if (info.Estado == "desativado")
                                    {
                                        escritor.WriteLine(Protocolo.AckConnect(false, "sensor desativado"));
                                    }
                                    else
                                    {
                                        AtualizarSync(sensorId);
                                        escritor.WriteLine(Protocolo.AckConnect(true));
                                    }

                                    break;
                                }

                            case Msg.DATA:
                                {
                                    if (!Protocolo.ValidaCampos(campos, 6))
                                    {
                                        escritor.WriteLine(Protocolo.AckData(false, "Formato invalido"));
                                        break;
                                    }

                                    string sid = campos[1];
                                    string zona = campos[2];
                                    string tipo = campos[3];
                                    string valor = campos[4];
                                    string ts = campos[5];

                                    InfoSensor info = ValidarSensor(sid);

                                    if (info == null)
                                    {
                                        escritor.WriteLine(Protocolo.AckData(false, "sensor desconhecido"));
                                        break;
                                    }

                                    if (info.Estado != "ativo")
                                    {
                                        escritor.WriteLine(Protocolo.AckData(false, "sensor nao ativo"));
                                        break;
                                    }

                                    if (!info.TiposDados.Contains(tipo))
                                    {
                                        escritor.WriteLine(Protocolo.AckData(false, "tipo nao suportado"));
                                        break;
                                    }

                                    bool ok = EnviarParaServidor(
                                        Protocolo.Store(sid, zona, tipo, valor, ts));

                                    AtualizarSync(sid);

                                    escritor.WriteLine(Protocolo.AckData(ok, ok ? "" : "erro no servidor"));
                                    break;
                                }

                            case Msg.HEARTBEAT:
                                {
                                    if (!Protocolo.ValidaCampos(campos, 2))
                                        break;

                                    string sid = campos[1];
                                    AtualizarSync(sid);

                                    Console.WriteLine($"[HB] {sid}");
                                    break;
                                }

                            case Msg.VIDEO_STREAM:
                                {
                                    escritor.WriteLine(Protocolo.AckVideo(true));
                                    break;
                                }

                            case Msg.DISCONNECT:
                                {
                                    escritor.WriteLine(Protocolo.AckDisconnect());
                                    return;
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
                catch
                {
                    return false;
                }
            }
        }

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
                }
            }
        }

        private static void CarregarCSV()
        {
            if (!File.Exists(FICHEIRO_SENSORES)) return;

            foreach (var linha in File.ReadAllLines(FICHEIRO_SENSORES))
            {
                var p = linha.Split(':');
                if (p.Length < 5) continue;

                _sensores[p[0]] = new InfoSensor
                {
                    Estado = p[1],
                    Zona = p[2],
                    TiposDados = new List<string>(p[3].Trim('[', ']').Split(',')),
                    UltimoSync = DateTime.Now
                };
            }
        }

        private static void MonitorizarHeartbeats()
        {
            while (true)
            {
                Thread.Sleep(10000);

                foreach (var kv in _sensores)
                {
                    if (kv.Value.Estado != "ativo") continue;

                    double segundos = (DateTime.Now - kv.Value.UltimoSync).TotalSeconds;

                    if (segundos > TIMEOUT_HEARTBEAT)
                    {
                        Console.WriteLine($"[ALERTA] Sensor {kv.Key} inativo");
                    }
                }
            }
        }
    }
}