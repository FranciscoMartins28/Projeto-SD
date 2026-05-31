// =====================================================================
//  Protocolo.cs (Gateway)
//  --------------------------------------------------------------------
//  Define o protocolo aplicacional baseado em texto usado entre
//  Gateway e Servidor Principal (TCP).
//  É o MESMO formato do TP1 (mensagens delimitadas por '|', terminadas
//  com '\n'); apenas se acrescenta o STORE_NORM para enviar dados já
//  normalizados pelo serviço RPC de pré-processamento.
// =====================================================================
using System;

namespace OneHealth.TP2.Gateway
{
    public static class Msg
    {
        public const string GATEWAY_CONNECT = "GATEWAY_CONNECT";
        public const string STORE_NORM      = "STORE_NORM";   // dado já normalizado
        public const string ACK_GATEWAY     = "ACK_GATEWAY";
        public const string ACK_STORE       = "ACK_STORE";
        public const string OK              = "OK";
        public const string ERR             = "ERR";
    }

    public static class Protocolo
    {
        private const char SEP = '|';

        public static string Construir(params string[] campos)
            => string.Join(SEP, campos);

        public static string[] Parse(string mensagem)
            => mensagem?.Split(SEP) ?? Array.Empty<string>();

        public static string GatewayConnect(string gatewayId)
            => Construir(Msg.GATEWAY_CONNECT, gatewayId);

        // STORE_NORM | gw | sensor | zona | tipo | valor | unidade | ts | valido(0/1) | obs
        public static string StoreNorm(string gw, string sensor, string zona,
                                       string tipo, double valor, string unidade,
                                       string ts, bool valido, string obs)
            => Construir(Msg.STORE_NORM, gw, sensor, zona, tipo,
                         valor.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                         unidade, ts,
                         valido ? "1" : "0",
                         obs ?? "");
    }
}
