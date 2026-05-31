// =====================================================================
//  Protocolo.cs (Servidor)
//  Idêntico ao do Gateway — extracção de mensagens partilhadas.
// =====================================================================
using System;

namespace OneHealth.TP2.Servidor
{
    public static class Msg
    {
        public const string GATEWAY_CONNECT = "GATEWAY_CONNECT";
        public const string STORE_NORM      = "STORE_NORM";
        public const string ACK_GATEWAY     = "ACK_GATEWAY";
        public const string ACK_STORE       = "ACK_STORE";

        // novos: pedidos da interface ao servidor (linha de comandos / web)
        public const string QUERY           = "QUERY";        // lê dados
        public const string ANALYSE         = "ANALYSE";      // pede análise
        public const string ACK_QUERY       = "ACK_QUERY";
        public const string ACK_ANALYSE     = "ACK_ANALYSE";

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

        public static string AckGateway(bool ok)
            => Construir(Msg.ACK_GATEWAY, ok ? Msg.OK : Msg.ERR);

        public static string AckStore(bool ok, string motivo = "")
            => ok ? Construir(Msg.ACK_STORE, Msg.OK)
                  : Construir(Msg.ACK_STORE, Msg.ERR, motivo);
    }
}
