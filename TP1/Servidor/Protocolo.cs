
using System;
using System.Collections.Generic;

namespace TP1
{

    public static class Msg
    {

        public const string CONNECT         = "CONNECT";
        public const string HEARTBEAT       = "HEARTBEAT";
        public const string DATA            = "DATA";
        public const string VIDEO_STREAM    = "VIDEO_STREAM";
        public const string DISCONNECT      = "DISCONNECT";


        public const string ACK_CONNECT     = "ACK_CONNECT";
        public const string ACK_DATA        = "ACK_DATA";
        public const string ACK_VIDEO       = "ACK_VIDEO";
        public const string ACK_DISCONNECT  = "ACK_DISCONNECT";


        public const string GATEWAY_CONNECT = "GATEWAY_CONNECT";
        public const string STORE           = "STORE";


        public const string ACK_GATEWAY     = "ACK_GATEWAY";
        public const string ACK_STORE       = "ACK_STORE";


        public const string OK              = "OK";
        public const string ERR             = "ERR";
        public const string REJECT          = "REJECT";
    }


    public static class Protocolo
    {
        private const char SEP = '|';


        public static string Construir(params string[] campos)
            => string.Join(SEP, campos);


        public static string[] Parse(string mensagem)
            => mensagem.Split(SEP);

        // MELHORIA: validação de campos
        public static bool ValidaCampos(string[] campos, int minCampos)
            => campos != null && campos.Length >= minCampos;

        public static string Connect(string sensorId, string[] tiposDados)
            => Construir(Msg.CONNECT, sensorId, string.Join(",", tiposDados));

        public static string Heartbeat(string sensorId)
            => Construir(Msg.HEARTBEAT, sensorId, DateTime.Now.ToString("o"));

        public static string Data(string sensorId, string zona, string tipoDado, string valor)
            => Construir(Msg.DATA, sensorId, zona, tipoDado, valor, DateTime.Now.ToString("o"));

        public static string VideoStream(string sensorId, string zona)
            => Construir(Msg.VIDEO_STREAM, sensorId, zona);

        public static string Disconnect(string sensorId)
            => Construir(Msg.DISCONNECT, sensorId);



        public static string AckConnect(bool ok, string motivo = "")
            => ok ? Construir(Msg.ACK_CONNECT, Msg.OK)
                  : Construir(Msg.ACK_CONNECT, Msg.REJECT, motivo);

        public static string AckData(bool ok, string motivo = "")
            => ok ? Construir(Msg.ACK_DATA, Msg.OK)
                  : Construir(Msg.ACK_DATA, Msg.ERR, motivo);

        public static string AckVideo(bool ok)
            => Construir(Msg.ACK_VIDEO, ok ? Msg.OK : Msg.ERR);

        public static string AckDisconnect()
            => Construir(Msg.ACK_DISCONNECT, Msg.OK);



        public static string GatewayConnect(string gatewayId)
            => Construir(Msg.GATEWAY_CONNECT, gatewayId);

        public static string Store(string sensorId, string zona, string tipoDado, string valor, string timestamp)
            => Construir(Msg.STORE, sensorId, zona, tipoDado, valor, timestamp);



        public static string AckGateway(bool ok)
            => Construir(Msg.ACK_GATEWAY, ok ? Msg.OK : Msg.ERR);

        public static string AckStore(bool ok, string motivo = "")
            => ok ? Construir(Msg.ACK_STORE, Msg.OK)
                  : Construir(Msg.ACK_STORE, Msg.ERR, motivo);
    }
}
