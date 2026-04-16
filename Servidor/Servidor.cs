
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace TP1
{
    class Servidor
    {

        private const int PORTA = 9000;
        private const string DIR_DADOS = "dados_servidor";


        private static readonly Dictionary<string, Mutex> _mutexFicheiros
            = new Dictionary<string, Mutex>();
        private static readonly object _lockMutexDict = new object();


        static void Main(string[] args)
        {
            Directory.CreateDirectory(DIR_DADOS);
            Console.WriteLine("=== SERVIDOR TP1 – One Health ===");
            Console.WriteLine($"A escutar na porta {PORTA}...\n");




            TcpListener listener = new TcpListener(IPAddress.Any, PORTA);
            listener.Start();

            while (true)
            {
                TcpClient client = listener.AcceptTcpClient();
                Thread t = new Thread(() => TratarGateway(client));
                t.IsBackground = true;
                t.Start();

            }
        }


        private static void TratarGateway(TcpClient cliente)
        {
            string gatewayId = "desconhecido";
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
                        string tipo = campos[0];

                        switch (tipo)
                        {

                            case Msg.GATEWAY_CONNECT:
                                gatewayId = campos.Length > 1 ? campos[1] : "?";
                                Console.WriteLine($"[GATEWAY] '{gatewayId}' ligou-se.");
                                escritor.WriteLine(Protocolo.AckGateway(true));
                                break;


                            case Msg.STORE:

                                if (campos.Length < 6)
                                {
                                    escritor.WriteLine(Protocolo.AckStore(false, "Formato invalido"));
                                    break;
                                }
                                string sensorId  = campos[1];
                                string zona      = campos[2];
                                string tipoDado  = campos[3];
                                string valor     = campos[4];
                                string timestamp = campos[5];

                                ArmazenarMedicao(zona, tipoDado, sensorId, valor, timestamp);
                                Console.WriteLine($"[STORE] {gatewayId} → {zona} {tipoDado}={valor} ({sensorId})");
                                escritor.WriteLine(Protocolo.AckStore(true));
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
                Console.WriteLine($"[ERRO] Gateway '{gatewayId}': {ex.Message}");
            }
            finally
            {
                Console.WriteLine($"[GATEWAY] '{gatewayId}' desligou-se.");
            }
        }


        private static void ArmazenarMedicao(string zona, string tipoDado,
                                             string sensorId, string valor, string timestamp)
        {

            string nomeFicheiro = Path.Combine(DIR_DADOS, $"{tipoDado}.csv");

            Mutex mutex = ObterMutex(nomeFicheiro);
            mutex.WaitOne();
            try
            {
                bool existe = File.Exists(nomeFicheiro);
                using (StreamWriter sw = new StreamWriter(nomeFicheiro, append: true, Encoding.UTF8))
                {
                    if (!existe)
                        sw.WriteLine("timestamp,sensor_id,zona,tipo_dado,valor");
                    sw.WriteLine($"{timestamp},{sensorId},{zona},{tipoDado},{valor}");
                }
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }


        private static Mutex ObterMutex(string caminho)
        {
            lock (_lockMutexDict)
            {
                if (!_mutexFicheiros.TryGetValue(caminho, out Mutex m))
                {
                    m = new Mutex();
                    _mutexFicheiros[caminho] = m;
                }
                return m;
            }
        }
    }
}
