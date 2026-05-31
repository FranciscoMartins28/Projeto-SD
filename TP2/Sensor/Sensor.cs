// =====================================================================
//  Sensor.cs
//  --------------------------------------------------------------------
//  Sensor urbano (One Health) — versão TP2.
//
//  Diferenças face ao TP1:
//    - Já NÃO usa sockets TCP diretos com o Gateway.
//    - Publica as medições num broker RabbitMQ (exchange 'sensores',
//      tipo 'topic'), com routing-key  zona.tipo  ex: ZONA_CENTRO.TEMP
//    - Suporta envio em vários formatos (RAW, JSON, XML, CSV) para
//      exercitar o pré-processamento RPC do Gateway.
//    - Pode correr em modo MANUAL (menu) ou em modo AUTO (publicações
//      periódicas com valores simulados).
//
//  Argumentos (todos opcionais):
//      Sensor <sensorId> <zona> <auto|manual> <hostRabbit> <intervaloMs>
//  Exemplos:
//      dotnet run -- S101 ZONA_CENTRO auto localhost 3000
//      dotnet run -- S102 ZONA_ESCOLAR manual
// =====================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading;
using RabbitMQ.Client;

namespace OneHealth.TP2.Sensor
{
    internal class Program
    {
        // -----------------------------------------------------------
        // Configuração
        // -----------------------------------------------------------
        private const string EXCHANGE     = "sensores";
        private const string EXCHANGE_TYPE = ExchangeType.Topic;

        // Tipos suportados + unidade default + intervalos para simulação
        private static readonly Dictionary<string, (string unidade, double min, double max)> CATALOGO
            = new()
            {
                ["TEMP"]  = ("C",       10, 35),
                ["HUM"]   = ("%",       30, 90),
                ["RUIDO"] = ("dB",      35, 85),
                ["PM2.5"] = ("ug/m3",    5, 70),
                ["PM10"]  = ("ug/m3",   10, 120),
                ["AR"]    = ("AQI",     20, 180),
                ["LUZ"]   = ("lux",    100, 50000),
                ["NO2"]   = ("ug/m3",   10, 150),
            };

        // -----------------------------------------------------------
        // Entry-point
        // -----------------------------------------------------------
        private static void Main(string[] args)
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            Console.OutputEncoding = Encoding.UTF8;

            string sensorId = args.Length > 0 ? args[0].ToUpper() : "S101";
            string zona     = args.Length > 1 ? args[1].ToUpper() : "ZONA_CENTRO";
            string modo     = args.Length > 2 ? args[2].ToLower() : "manual";
            string host     = args.Length > 3 ? args[3]           : "localhost";
            int    interv   = args.Length > 4 && int.TryParse(args[4], out var i) ? i : 5000;

            Console.WriteLine("===========================================");
            Console.WriteLine(" SENSOR TP2  |  One Health Monitoring");
            Console.WriteLine("===========================================");
            Console.WriteLine($"  ID         : {sensorId}");
            Console.WriteLine($"  Zona       : {zona}");
            Console.WriteLine($"  Modo       : {modo}");
            Console.WriteLine($"  RabbitMQ   : {host}");
            Console.WriteLine($"  Exchange   : {EXCHANGE} ({EXCHANGE_TYPE})");
            Console.WriteLine();

            // ----- conexão ao RabbitMQ ---------------------------
            var factory = new ConnectionFactory
            {
                HostName               = host,
                DispatchConsumersAsync = false,
            };

            try
            {
                using var conn  = factory.CreateConnection($"sensor-{sensorId}");
                using var canal = conn.CreateModel();

                // exchange durable (sobrevive a restarts do broker)
                canal.ExchangeDeclare(EXCHANGE, EXCHANGE_TYPE, durable: true, autoDelete: false);

                Console.WriteLine("[OK] Ligado ao RabbitMQ.\n");

                if (modo == "auto")
                    LoopAutomatico(canal, sensorId, zona, interv);
                else
                    MenuManual(canal, sensorId, zona);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERRO] Não foi possível ligar ao RabbitMQ: {ex.Message}");
            }
        }

        // -----------------------------------------------------------
        // Publicação base
        // -----------------------------------------------------------
        private static void Publicar(IModel canal, string sensorId, string zona,
                                     string tipo, string payload, string formato,
                                     string unidade)
        {
            string routingKey = $"{zona}.{tipo}";

            // Cabeçalhos: metadados que o consumidor (Gateway) pode usar
            var props = canal.CreateBasicProperties();
            props.ContentType  = formato switch
            {
                "JSON" => "application/json",
                "XML"  => "application/xml",
                "CSV"  => "text/csv",
                _      => "text/plain",
            };
            props.Persistent = true;
            props.MessageId  = Guid.NewGuid().ToString();
            props.Timestamp  = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            props.Headers = new Dictionary<string, object>
            {
                ["sensor_id"] = sensorId,
                ["zona"]      = zona,
                ["tipo"]      = tipo,
                ["formato"]   = formato,
                ["unidade"]   = unidade,
                ["ts"]        = DateTime.UtcNow.ToString("o"),
            };

            byte[] body = Encoding.UTF8.GetBytes(payload);
            canal.BasicPublish(EXCHANGE, routingKey, props, body);

            Console.WriteLine($"[PUB] {routingKey,-25} fmt={formato,-4} -> {payload}");
        }

        // -----------------------------------------------------------
        // Helpers para gerar payloads em formatos diferentes
        // -----------------------------------------------------------
        private static string ComoJson(string tipo, double valor, string unidade)
            => JsonSerializer.Serialize(new
            {
                tipo,
                valor   = Math.Round(valor, 2),
                unidade,
                ts      = DateTime.UtcNow.ToString("o"),
            });

        private static string ComoXml(string tipo, double valor, string unidade)
            => $"<medicao tipo=\"{tipo}\" unidade=\"{unidade}\">"
             + $"<valor>{valor.ToString("F2", CultureInfo.InvariantCulture)}</valor>"
             + $"<ts>{DateTime.UtcNow:o}</ts>"
             + $"</medicao>";

        private static string ComoCsv(string sensorId, string tipo, double valor)
            => $"{DateTime.UtcNow:o},{sensorId},{tipo},{valor.ToString("F2", CultureInfo.InvariantCulture)}";

        private static string ComoRaw(double valor)
            => valor.ToString("F2", CultureInfo.InvariantCulture);

        // -----------------------------------------------------------
        // Modo automático: cicla pelos tipos do catálogo
        // -----------------------------------------------------------
        private static void LoopAutomatico(IModel canal, string sensorId,
                                           string zona, int intervaloMs)
        {
            Console.WriteLine($"[AUTO] A publicar a cada {intervaloMs} ms.  Ctrl+C para sair.\n");
            var rnd     = new Random();
            var tipos   = new List<string>(CATALOGO.Keys);
            string[] formatos = { "RAW", "JSON", "XML", "CSV" };
            int contador = 0;

            while (true)
            {
                string tipo = tipos[contador % tipos.Count];
                var (unidade, vmin, vmax) = CATALOGO[tipo];
                double valor = Math.Round(vmin + rnd.NextDouble() * (vmax - vmin), 2);

                // de quando em vez injeta um outlier para testar deteção de anomalias
                if (rnd.Next(0, 25) == 0) valor *= 3.0;

                string formato = formatos[contador % formatos.Length];
                string payload = formato switch
                {
                    "JSON" => ComoJson(tipo, valor, unidade),
                    "XML"  => ComoXml(tipo, valor, unidade),
                    "CSV"  => ComoCsv(sensorId, tipo, valor),
                    _      => ComoRaw(valor),
                };

                try { Publicar(canal, sensorId, zona, tipo, payload, formato, unidade); }
                catch (Exception ex) { Console.WriteLine($"[ERRO] {ex.Message}"); }

                contador++;
                Thread.Sleep(intervaloMs);
            }
        }

        // -----------------------------------------------------------
        // Modo manual: menu interativo
        // -----------------------------------------------------------
        private static void MenuManual(IModel canal, string sensorId, string zona)
        {
            while (true)
            {
                Console.WriteLine("\n--- MENU SENSOR ---");
                Console.WriteLine(" 1) Publicar medição (RAW)");
                Console.WriteLine(" 2) Publicar medição (JSON)");
                Console.WriteLine(" 3) Publicar medição (XML)");
                Console.WriteLine(" 4) Publicar medição (CSV)");
                Console.WriteLine(" 5) Mudar zona");
                Console.WriteLine(" 0) Sair");
                Console.Write("Opção: ");
                string opcao = Console.ReadLine()?.Trim();

                if (opcao == "0") return;
                if (opcao == "5")
                {
                    Console.Write("Nova zona: ");
                    string nova = Console.ReadLine()?.Trim().ToUpper();
                    if (!string.IsNullOrEmpty(nova)) zona = nova;
                    Console.WriteLine($"[OK] Zona = {zona}");
                    continue;
                }

                string formato = opcao switch
                {
                    "1" => "RAW",
                    "2" => "JSON",
                    "3" => "XML",
                    "4" => "CSV",
                    _   => null,
                };
                if (formato == null) { Console.WriteLine("Opção inválida."); continue; }

                Console.Write($"Tipo ({string.Join(", ", CATALOGO.Keys)}): ");
                string tipo = Console.ReadLine()?.Trim().ToUpper();
                if (!CATALOGO.ContainsKey(tipo)) { Console.WriteLine("Tipo inválido."); continue; }

                Console.Write("Valor: ");
                if (!double.TryParse(Console.ReadLine(),
                                     NumberStyles.Float, CultureInfo.InvariantCulture,
                                     out double valor))
                { Console.WriteLine("Valor inválido."); continue; }

                string unidade = CATALOGO[tipo].unidade;
                string payload = formato switch
                {
                    "JSON" => ComoJson(tipo, valor, unidade),
                    "XML"  => ComoXml(tipo, valor, unidade),
                    "CSV"  => ComoCsv(sensorId, tipo, valor),
                    _      => ComoRaw(valor),
                };

                try { Publicar(canal, sensorId, zona, tipo, payload, formato, unidade); }
                catch (Exception ex) { Console.WriteLine($"[ERRO] {ex.Message}"); }
            }
        }
    }
}
