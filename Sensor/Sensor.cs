
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace TP1
{
    class Sensor
    {

        private const int    PORTA_GATEWAY   = 8000;
        private const int    INTERVALO_HB    = 10_000; 


        private static readonly string[] TIPOS_DADOS_DISPONIVEIS =
        {
            "TEMP", "HUM", "RUIDO", "PM2.5", "PM10", "AR", "LUZ"
        };


        private static string        _sensorId;
        private static string        _zona;
        private static List<string>  _tiposDados;
        private static StreamWriter  _escritor;
        private static StreamReader  _leitor;
        private static bool          _ligado = false;
        private static readonly object _lockStream = new object();


        static void Main(string[] args)
        {
            string ipGateway = args.Length > 0 ? args[0] : "127.0.0.1";

            Console.WriteLine("=== SENSOR – One Health ===");

            if (args.Length > 1)
            {
                _sensorId = args[1];
            }
            else
            {
                Console.Write("Introduza o ID deste Sensor (ex: S101, S102): ");
                _sensorId = Console.ReadLine()?.Trim().ToUpper();


                if (string.IsNullOrEmpty(_sensorId))
                {
                    _sensorId = "S101";
                }
            }

            Console.WriteLine($"\n[INFO] A iniciar Sensor {_sensorId}...");

            _tiposDados = EscolherTiposDados();

            if (!LigarAoGateway(ipGateway)) return;

            Thread hbThread = new Thread(EnviarHeartbeats);
            hbThread.IsBackground = true;
            hbThread.Start();

            MenuPrincipal();
        }



        private static bool LigarAoGateway(string ip)
        {
            try
            {
                TcpClient cliente      = new TcpClient(ip, PORTA_GATEWAY);
                NetworkStream stream   = cliente.GetStream();
                _escritor = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
                _leitor   = new StreamReader(stream, Encoding.UTF8);


                _escritor.WriteLine(Protocolo.Connect(_sensorId, _tiposDados.ToArray()));
                string resposta = _leitor.ReadLine();
                string[] campos = Protocolo.Parse(resposta ?? "");

                if (campos.Length > 1 && campos[1] == Msg.OK)
                {
                    _ligado = true;
                    Console.WriteLine($"[OK] Ligado ao gateway {ip}:{PORTA_GATEWAY}\n");
                    return true;
                }
                else
                {
                    string motivo = campos.Length > 2 ? campos[2] : "desconhecido";
                    Console.WriteLine($"[REJEITADO] {motivo}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERRO] Não foi possível ligar ao gateway: {ex.Message}");
                return false;
            }
        }


        private static void EnviarHeartbeats()
        {
            while (_ligado)
            {
                Thread.Sleep(INTERVALO_HB);
                if (!_ligado) break;
                lock (_lockStream)
                {
                    try { _escritor.WriteLine(Protocolo.Heartbeat(_sensorId)); }
                    catch { _ligado = false; }
                }
            }
        }


        private static void MenuPrincipal()
        {
            while (_ligado)
            {
                Console.WriteLine("\n--- MENU SENSOR ---");
                Console.WriteLine(" 1) Enviar medição ambiental");
                Console.WriteLine(" 2) Iniciar stream de vídeo");
                Console.WriteLine(" 3) Enviar heartbeat manual");
                Console.WriteLine(" 4) Desligar");
                Console.Write("Opção: ");

                string opcao = Console.ReadLine()?.Trim();
                switch (opcao)
                {
                    case "1": EnviarMedicao(); break;
                    case "2": EnviarVideo();   break;
                    case "3": HeartbeatManual(); break;
                    case "4": Desligar(); return;
                    default:  Console.WriteLine("Opção inválida."); break;
                }
            }
        }


        private static void EnviarMedicao()
        {

            Console.WriteLine("\nTipos de dados disponíveis:");
            for (int i = 0; i < _tiposDados.Count; i++)
                Console.WriteLine($"  {i + 1}) {_tiposDados[i]}");
            Console.Write("Escolha o tipo (número): ");

            if (!int.TryParse(Console.ReadLine(), out int idx) ||
                idx < 1 || idx > _tiposDados.Count)
            {
                Console.WriteLine("Seleção inválida."); return;
            }
            string tipoDado = _tiposDados[idx - 1];

            Console.Write("Valor: ");
            string valor = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(valor)) { Console.WriteLine("Valor inválido."); return; }

            Console.Write("Zona (ex: ZONA_CENTRO): ");
            string zona = Console.ReadLine()?.Trim().ToUpper();
            if (string.IsNullOrEmpty(zona)) zona = "ZONA_GENERICA";

            lock (_lockStream)
            {
                try
                {
                    _escritor.WriteLine(Protocolo.Data(_sensorId, zona, tipoDado, valor));
                    string resposta = _leitor.ReadLine();
                    string[] campos = Protocolo.Parse(resposta ?? "");

                    if (campos.Length > 1 && campos[1] == Msg.OK)
                        Console.WriteLine("[DATA] Medição enviada com sucesso.");
                    else
                    {
                        string motivo = campos.Length > 2 ? campos[2] : "?";
                        Console.WriteLine($"[DATA] Erro: {motivo}");
                    }
                }
                catch (Exception ex) { Console.WriteLine($"[ERRO] {ex.Message}"); }
            }
        }


        private static void EnviarVideo()
        {
            Console.Write("Zona do stream: ");
            string zona = Console.ReadLine()?.Trim().ToUpper();
            if (string.IsNullOrEmpty(zona)) zona = "ZONA_GENERICA";

            lock (_lockStream)
            {
                try
                {
                    _escritor.WriteLine(Protocolo.VideoStream(_sensorId, zona));
                    string resposta = _leitor.ReadLine();
                    Console.WriteLine(resposta != null && resposta.Contains(Msg.OK)
                        ? "[VIDEO] Stream aceite pelo gateway."
                        : "[VIDEO] Gateway recusou o stream.");
                }
                catch (Exception ex) { Console.WriteLine($"[ERRO] {ex.Message}"); }
            }
        }


        private static void HeartbeatManual()
        {
            lock (_lockStream)
            {
                try
                {
                    _escritor.WriteLine(Protocolo.Heartbeat(_sensorId));
                    Console.WriteLine("[HB] Heartbeat enviado.");
                }
                catch (Exception ex) { Console.WriteLine($"[ERRO] {ex.Message}"); }
            }
        }


        private static void Desligar()
        {
            _ligado = false;
            lock (_lockStream)
            {
                try
                {
                    _escritor.WriteLine(Protocolo.Disconnect(_sensorId));
                    string resposta = _leitor.ReadLine();
                    Console.WriteLine("[DISCONNECT] Sensor desligado correctamente.");
                }
                catch {  }
            }
        }


        private static List<string> EscolherTiposDados()
        {
            Console.WriteLine("\nTipos de dados disponíveis:");
            for (int i = 0; i < TIPOS_DADOS_DISPONIVEIS.Length; i++)
                Console.WriteLine($"  {i + 1}) {TIPOS_DADOS_DISPONIVEIS[i]}");
            Console.Write("Escolha os tipos (ex: 1,3,5) ou Enter para todos: ");

            string input = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(input))
                return new List<string>(TIPOS_DADOS_DISPONIVEIS);

            List<string> escolhidos = new List<string>();
            foreach (string parte in input.Split(','))
            {
                if (int.TryParse(parte.Trim(), out int n) &&
                    n >= 1 && n <= TIPOS_DADOS_DISPONIVEIS.Length)
                {
                    escolhidos.Add(TIPOS_DADOS_DISPONIVEIS[n - 1]);
                }
            }
            return escolhidos.Count > 0 ? escolhidos : new List<string>(TIPOS_DADOS_DISPONIVEIS);
        }
    }
}
