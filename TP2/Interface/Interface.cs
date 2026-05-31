// =====================================================================
//  Interface.cs (CLI)
//  --------------------------------------------------------------------
//  Interface de visualização em linha de comandos.
//
//  - Liga-se ao Servidor Principal por TCP (mesmo protocolo de texto).
//  - Permite:
//        * consultar medições filtradas (QUERY)
//        * pedir análises estatísticas / padrões / risco (ANALYSE)
//        * desenhar pequenos gráficos ASCII no terminal
//
//  Argumentos:  Interface <ipServidor> <porta>
//  Defaults  :  127.0.0.1   9000
// =====================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace OneHealth.TP2.Interface
{
    internal class Program
    {
        private static StreamWriter _esc;
        private static StreamReader _ler;

        private static void Main(string[] args)
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            Console.OutputEncoding = Encoding.UTF8;

            string ip    = args.Length > 0 ? args[0] : "127.0.0.1";
            int    porta = args.Length > 1 && int.TryParse(args[1], out var p) ? p : 9000;

            Console.WriteLine("===========================================");
            Console.WriteLine(" INTERFACE CLI  |  One Health TP2");
            Console.WriteLine("===========================================");
            Console.WriteLine($"  Servidor: {ip}:{porta}\n");

            try
            {
                var cli = new TcpClient(ip, porta);
                var ns  = cli.GetStream();
                _esc = new StreamWriter(ns, Encoding.UTF8) { AutoFlush = true };
                _ler = new StreamReader(ns, Encoding.UTF8);

                Console.WriteLine("[OK] Ligado ao servidor.\n");
                Menu();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERRO] Não foi possível ligar: {ex.Message}");
            }
        }

        // -----------------------------------------------------------
        // Menu principal
        // -----------------------------------------------------------
        private static void Menu()
        {
            while (true)
            {
                Console.WriteLine("\n----- MENU -----");
                Console.WriteLine(" 1) Consultar medições (QUERY)");
                Console.WriteLine(" 2) Análise estatística (STATS)");
                Console.WriteLine(" 3) Deteção de padrões/anomalias (PATTERNS)");
                Console.WriteLine(" 4) Previsão de risco de saúde (RISK)");
                Console.WriteLine(" 5) Gráfico ASCII de série temporal");
                Console.WriteLine(" 0) Sair");
                Console.Write("Opção: ");
                string op = Console.ReadLine()?.Trim();

                try
                {
                    switch (op)
                    {
                        case "1": Consultar();      break;
                        case "2": PedirAnalise("STATS");    break;
                        case "3": PedirAnalise("PATTERNS"); break;
                        case "4": PedirAnalise("RISK");     break;
                        case "5": GraficoAscii();   break;
                        case "0": return;
                        default:  Console.WriteLine("Opção inválida."); break;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERRO] {ex.Message}");
                }
            }
        }

        // -----------------------------------------------------------
        // Helpers para pedir filtros
        // -----------------------------------------------------------
        private static (string tipo, string zona, string sensor, string ini, string fim, int lim)
            LerFiltros(bool incluiLimite)
        {
            Console.Write("Tipo (TEMP/HUM/...; vazio=todos): ");
            string tipo = Console.ReadLine()?.Trim().ToUpper();
            Console.Write("Zona (vazio=todas): ");
            string zona = Console.ReadLine()?.Trim().ToUpper();
            Console.Write("Sensor (vazio=todos): ");
            string sensor = Console.ReadLine()?.Trim().ToUpper();
            Console.Write("Início ISO-8601 (vazio=sem): ");
            string ini = Console.ReadLine()?.Trim();
            Console.Write("Fim    ISO-8601 (vazio=sem): ");
            string fim = Console.ReadLine()?.Trim();

            int lim = 500;
            if (incluiLimite)
            {
                Console.Write("Limite [500]: ");
                int.TryParse(Console.ReadLine(), out lim);
                if (lim <= 0) lim = 500;
            }
            return (tipo, zona, sensor, ini, fim, lim);
        }

        // -----------------------------------------------------------
        // Consulta
        // -----------------------------------------------------------
        private static List<Medicao> ObterDados()
        {
            var f = LerFiltros(incluiLimite: true);
            string pedido = string.Join('|', "QUERY", f.tipo, f.zona, f.sensor, f.ini, f.fim, f.lim);
            _esc.WriteLine(pedido);
            string resp = _ler.ReadLine();
            var c = resp?.Split('|') ?? Array.Empty<string>();
            if (c.Length < 3 || c[1] != "OK")
            {
                Console.WriteLine($"[ERRO] Resposta inválida: {resp}");
                return new List<Medicao>();
            }
            return JsonSerializer.Deserialize<List<Medicao>>(c[2]) ?? new List<Medicao>();
        }

        private static void Consultar()
        {
            var dados = ObterDados();
            Console.WriteLine($"\n>>> {dados.Count} medições");
            Console.WriteLine($"{"Timestamp",-25} {"Sensor",-8} {"Zona",-18} {"Tipo",-7} {"Valor",10} {"Un",-6} {"V"}");
            Console.WriteLine(new string('-', 80));
            foreach (var m in dados.Take(50))
                Console.WriteLine($"{m.Timestamp,-25} {m.SensorId,-8} {m.Zona,-18} {m.TipoDado,-7} " +
                                  $"{m.Valor,10:F2} {m.Unidade,-6} {(m.Valido ? "✓" : "✗")}");
            if (dados.Count > 50)
                Console.WriteLine($"... (apresentadas as primeiras 50 de {dados.Count})");
        }

        // -----------------------------------------------------------
        // Pedido de análise
        // -----------------------------------------------------------
        private static void PedirAnalise(string sub)
        {
            var f = LerFiltros(incluiLimite: false);
            string pedido = string.Join('|', "ANALYSE", sub, f.tipo, f.zona, f.sensor, f.ini, f.fim);
            _esc.WriteLine(pedido);
            string resp = _ler.ReadLine();
            var c = resp?.Split('|') ?? Array.Empty<string>();
            if (c.Length < 3 || c[1] != "OK")
            {
                Console.WriteLine($"[ERRO] {(c.Length > 2 ? c[2] : resp)}");
                return;
            }

            Console.WriteLine($"\n>>> Análise {sub}  (id={c[2]})\n");
            var doc = JsonDocument.Parse(c[3]).RootElement;
            ImprimirJson(doc, 0);
        }

        // pretty print recursivo
        private static void ImprimirJson(JsonElement el, int indent)
        {
            string ind = new(' ', indent * 2);
            switch (el.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var prop in el.EnumerateObject())
                    {
                        Console.Write($"{ind}{prop.Name}: ");
                        if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                        {
                            Console.WriteLine();
                            ImprimirJson(prop.Value, indent + 1);
                        }
                        else
                            Console.WriteLine(prop.Value);
                    }
                    break;
                case JsonValueKind.Array:
                    int idx = 0;
                    foreach (var item in el.EnumerateArray())
                    {
                        Console.WriteLine($"{ind}- [{idx++}]");
                        ImprimirJson(item, indent + 1);
                    }
                    break;
                default:
                    Console.WriteLine($"{ind}{el}");
                    break;
            }
        }

        // -----------------------------------------------------------
        // Gráfico ASCII
        // -----------------------------------------------------------
        private static void GraficoAscii()
        {
            var dados = ObterDados();
            if (dados.Count == 0) { Console.WriteLine("Sem dados."); return; }

            // séries por tipo
            var serie = dados.OrderBy(d => d.Timestamp).Select(d => d.Valor).ToList();
            double min = serie.Min(), max = serie.Max();
            int largura = Math.Min(60, serie.Count);
            int altura  = 12;

            Console.WriteLine($"\nMin={min:F2}  Max={max:F2}  n={serie.Count}  (largura={largura})");
            // re-amostragem por média (downsample) se necessário
            var pontos = new double[largura];
            for (int i = 0; i < largura; i++)
            {
                int start = (int)((long)i * serie.Count / largura);
                int end   = (int)((long)(i + 1) * serie.Count / largura);
                if (end <= start) end = start + 1;
                pontos[i] = serie.Skip(start).Take(end - start).Average();
            }

            for (int row = altura; row >= 0; row--)
            {
                double limiar = min + (max - min) * row / (double)altura;
                Console.Write($"{limiar,8:F2} | ");
                foreach (var v in pontos)
                    Console.Write(v >= limiar ? "█" : " ");
                Console.WriteLine();
            }
            Console.WriteLine(new string(' ', 10) + new string('-', largura));
        }

        // -----------------------------------------------------------
        // DTO (espelha BaseDados.Medicao)
        // -----------------------------------------------------------
        public class Medicao
        {
            public string GatewayId   { get; set; }
            public string SensorId    { get; set; }
            public string Zona        { get; set; }
            public string TipoDado    { get; set; }
            public double Valor       { get; set; }
            public string Unidade     { get; set; }
            public string Timestamp   { get; set; }
            public bool   Valido      { get; set; }
            public string Observacoes { get; set; }
        }
    }
}
