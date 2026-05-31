// =====================================================================
//  BaseDados.cs
//  --------------------------------------------------------------------
//  Camada de acesso à base de dados (SQLite).
//
//  - SQLite foi escolhido por ser relacional, leve e sem servidor,
//    o que simplifica execução e avaliação.
//  - Duas tabelas:
//      medicoes  -> dados normalizados recebidos dos gateways
//      analises  -> resultados das análises invocadas via RPC
// =====================================================================
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace OneHealth.TP2.Servidor
{
    public class BaseDados
    {
        private readonly string _connStr;
        private static readonly object _lock = new();

        public BaseDados(string ficheiro)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(ficheiro));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            _connStr = new SqliteConnectionStringBuilder
            {
                DataSource = ficheiro,
                Mode       = SqliteOpenMode.ReadWriteCreate,
                Cache      = SqliteCacheMode.Shared,
            }.ToString();

            CriarTabelas();
        }

        // -----------------------------------------------------------
        // DDL
        // -----------------------------------------------------------
        private void CriarTabelas()
        {
            using var conn = new SqliteConnection(_connStr);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS medicoes (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    gateway_id  TEXT NOT NULL,
    sensor_id   TEXT NOT NULL,
    zona        TEXT NOT NULL,
    tipo_dado   TEXT NOT NULL,
    valor       REAL NOT NULL,
    unidade     TEXT,
    timestamp   TEXT NOT NULL,
    valido      INTEGER NOT NULL DEFAULT 1,
    observacoes TEXT,
    inserido_em TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS ix_med_tipo_ts  ON medicoes(tipo_dado, timestamp);
CREATE INDEX IF NOT EXISTS ix_med_zona     ON medicoes(zona);
CREATE INDEX IF NOT EXISTS ix_med_sensor   ON medicoes(sensor_id);

CREATE TABLE IF NOT EXISTS analises (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    tipo        TEXT NOT NULL,   -- STATS | PATTERNS | RISK
    parametros  TEXT,            -- JSON com os filtros usados
    resultado   TEXT NOT NULL,   -- JSON com o resultado
    criado_em   TEXT NOT NULL DEFAULT (datetime('now'))
);";
            cmd.ExecuteNonQuery();
        }

        // -----------------------------------------------------------
        // Inserção de medição normalizada
        // -----------------------------------------------------------
        public void InserirMedicao(string gw, string sensor, string zona,
                                   string tipo, double valor, string unidade,
                                   string ts, bool valido, string obs)
        {
            lock (_lock)
            {
                using var conn = new SqliteConnection(_connStr);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
INSERT INTO medicoes (gateway_id, sensor_id, zona, tipo_dado, valor,
                      unidade, timestamp, valido, observacoes)
VALUES ($gw, $s, $z, $t, $v, $u, $ts, $val, $obs);";
                cmd.Parameters.AddWithValue("$gw",  gw);
                cmd.Parameters.AddWithValue("$s",   sensor);
                cmd.Parameters.AddWithValue("$z",   zona);
                cmd.Parameters.AddWithValue("$t",   tipo);
                cmd.Parameters.AddWithValue("$v",   valor);
                cmd.Parameters.AddWithValue("$u",   unidade ?? "");
                cmd.Parameters.AddWithValue("$ts",  ts);
                cmd.Parameters.AddWithValue("$val", valido ? 1 : 0);
                cmd.Parameters.AddWithValue("$obs", obs ?? "");
                cmd.ExecuteNonQuery();
            }
        }

        // -----------------------------------------------------------
        // Consulta de medições com filtros opcionais
        // -----------------------------------------------------------
        public List<Medicao> Consultar(string tipo = null, string zona = null,
                                       string sensor = null,
                                       string inicio = null, string fim = null,
                                       int limite = 10000)
        {
            var lista = new List<Medicao>();
            using var conn = new SqliteConnection(_connStr);
            conn.Open();
            using var cmd = conn.CreateCommand();

            var sb = new System.Text.StringBuilder(
                "SELECT gateway_id, sensor_id, zona, tipo_dado, valor, unidade, " +
                "       timestamp, valido, observacoes " +
                "FROM medicoes WHERE 1=1 ");

            if (!string.IsNullOrEmpty(tipo))   { sb.Append("AND tipo_dado = $t ");   cmd.Parameters.AddWithValue("$t", tipo); }
            if (!string.IsNullOrEmpty(zona))   { sb.Append("AND zona      = $z ");   cmd.Parameters.AddWithValue("$z", zona); }
            if (!string.IsNullOrEmpty(sensor)) { sb.Append("AND sensor_id = $s ");   cmd.Parameters.AddWithValue("$s", sensor); }
            if (!string.IsNullOrEmpty(inicio)) { sb.Append("AND timestamp >= $i ");  cmd.Parameters.AddWithValue("$i", inicio); }
            if (!string.IsNullOrEmpty(fim))    { sb.Append("AND timestamp <= $f ");  cmd.Parameters.AddWithValue("$f", fim); }

            sb.Append("ORDER BY timestamp ASC LIMIT $lim");
            cmd.Parameters.AddWithValue("$lim", limite);
            cmd.CommandText = sb.ToString();

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                lista.Add(new Medicao
                {
                    GatewayId  = r.GetString(0),
                    SensorId   = r.GetString(1),
                    Zona       = r.GetString(2),
                    TipoDado   = r.GetString(3),
                    Valor      = r.GetDouble(4),
                    Unidade    = r.IsDBNull(5) ? "" : r.GetString(5),
                    Timestamp  = r.GetString(6),
                    Valido     = r.GetInt32(7) == 1,
                    Observacoes= r.IsDBNull(8) ? "" : r.GetString(8),
                });
            }
            return lista;
        }

        // -----------------------------------------------------------
        // Persistência de resultados de análises
        // -----------------------------------------------------------
        public long GuardarAnalise(string tipo, string parametrosJson, string resultadoJson)
        {
            lock (_lock)
            {
                using var conn = new SqliteConnection(_connStr);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
INSERT INTO analises (tipo, parametros, resultado)
VALUES ($t, $p, $r);
SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("$t", tipo);
                cmd.Parameters.AddWithValue("$p", parametrosJson ?? "");
                cmd.Parameters.AddWithValue("$r", resultadoJson);
                return (long)cmd.ExecuteScalar();
            }
        }

        public List<(long id, string tipo, string criadoEm, string resumo)>
            ListarAnalises(int limite = 50)
        {
            var lista = new List<(long, string, string, string)>();
            using var conn = new SqliteConnection(_connStr);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT id, tipo, criado_em, substr(resultado, 1, 120)
FROM analises ORDER BY id DESC LIMIT $lim;";
            cmd.Parameters.AddWithValue("$lim", limite);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                lista.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3)));
            return lista;
        }

        public string ObterAnalise(long id)
        {
            using var conn = new SqliteConnection(_connStr);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT resultado FROM analises WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteScalar() as string;
        }
    }

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
