"""
=====================================================================
 web_interface.py
 --------------------------------------------------------------------
 Interface web (opcional / valorização) para o sistema One Health TP2.

 Tecnologia : Python 3 + Flask
 Função     : actua como cliente do Servidor Principal (TCP texto),
              expondo um pequeno dashboard HTML com:
                - consulta de medições com filtros
                - botões para pedir cada tipo de análise (RPC)
                - tabela e gráfico simples

 Porta default: 8080
 Executar     : python web_interface.py [servidor_host] [servidor_porta] [http_porta]
=====================================================================
"""
from __future__ import annotations

import json
import socket
import sys
from threading import Lock

from flask import Flask, jsonify, render_template_string, request

# ---------------------------------------------------------------------
# Cliente TCP persistente para o Servidor Principal
# ---------------------------------------------------------------------
class ServidorClient:
    def __init__(self, host: str, port: int):
        self.host, self.port = host, port
        self.sock: socket.socket | None = None
        self.fp = None
        self.lock = Lock()
        self.connect()

    def connect(self) -> None:
        self.sock = socket.create_connection((self.host, self.port), timeout=10)
        self.fp = self.sock.makefile("rwb", buffering=0)

    def _send(self, msg: str) -> str:
        if self.sock is None:
            self.connect()
        try:
            self.fp.write((msg + "\n").encode("utf-8"))
            line = self.fp.readline()
            return line.decode("utf-8", errors="replace").rstrip("\r\n")
        except (BrokenPipeError, ConnectionResetError, OSError):
            self.connect()
            self.fp.write((msg + "\n").encode("utf-8"))
            return self.fp.readline().decode("utf-8", errors="replace").rstrip("\r\n")

    def query(self, tipo="", zona="", sensor="", ini="", fim="", lim=500) -> list[dict]:
        with self.lock:
            resp = self._send("|".join(["QUERY", tipo, zona, sensor, ini, fim, str(lim)]))
        parts = resp.split("|", 2)
        if len(parts) < 3 or parts[1] != "OK":
            return []
        try:
            return json.loads(parts[2])
        except Exception:
            return []

    def analyse(self, sub: str, tipo="", zona="", sensor="", ini="", fim="") -> dict:
        with self.lock:
            resp = self._send("|".join(["ANALYSE", sub, tipo, zona, sensor, ini, fim]))
        parts = resp.split("|", 3)
        if len(parts) < 4 or parts[1] != "OK":
            return {"erro": parts[2] if len(parts) > 2 else resp}
        out = {"id_analise": parts[2]}
        try:
            out["resultado"] = json.loads(parts[3])
        except Exception:
            out["resultado"] = parts[3]
        return out


# ---------------------------------------------------------------------
# Flask app
# ---------------------------------------------------------------------
app = Flask(__name__)
cli: ServidorClient | None = None


HTML = """<!doctype html>
<html lang="pt"><head><meta charset="utf-8">
<title>One Health TP2 — Dashboard</title>
<style>
 body{font-family:system-ui,sans-serif;margin:0;padding:1rem;background:#f5f5f7;color:#222}
 h1{margin:0 0 1rem 0;font-size:1.4rem}
 .card{background:#fff;border-radius:8px;padding:1rem;margin-bottom:1rem;box-shadow:0 1px 3px rgba(0,0,0,.08)}
 .row{display:flex;flex-wrap:wrap;gap:.5rem}
 input,select,button{padding:.4rem .6rem;border:1px solid #ccc;border-radius:4px;font-size:.9rem}
 button{background:#2660d9;color:#fff;border-color:#2660d9;cursor:pointer}
 button.alt{background:#fff;color:#2660d9}
 table{border-collapse:collapse;width:100%;font-size:.85rem}
 th,td{border-bottom:1px solid #e5e5e5;padding:.3rem .5rem;text-align:left}
 th{background:#fafafa}
 pre{background:#1e1e1e;color:#dcdcdc;padding:.8rem;border-radius:4px;overflow:auto;font-size:.8rem}
 .bad{color:#c00}
</style></head><body>
<h1>🌍 One Health — Dashboard TP2</h1>

<div class="card">
 <strong>Filtros</strong>
 <div class="row" style="margin-top:.5rem">
  <input id="tipo"   placeholder="Tipo (TEMP,HUM,PM2.5...)">
  <input id="zona"   placeholder="Zona (ZONA_CENTRO...)">
  <input id="sensor" placeholder="Sensor (S101...)">
  <input id="ini"    placeholder="Início (ISO-8601)">
  <input id="fim"    placeholder="Fim (ISO-8601)">
  <input id="lim"    placeholder="Limite" value="500" style="width:90px">
 </div>
 <div class="row" style="margin-top:.7rem">
  <button onclick="consultar()">Consultar</button>
  <button class="alt" onclick="analise('STATS')">Estatísticas</button>
  <button class="alt" onclick="analise('PATTERNS')">Padrões / Anomalias</button>
  <button class="alt" onclick="analise('RISK')">Risco de Saúde</button>
 </div>
</div>

<div class="card" id="resultado"><em>Sem dados.</em></div>

<script>
function val(id){return document.getElementById(id).value.trim()}
function args(){return {
  tipo:val('tipo'),zona:val('zona'),sensor:val('sensor'),
  ini:val('ini'),fim:val('fim'),lim:val('lim')||'500'
}}

async function consultar(){
 const r=await fetch('/api/query?'+new URLSearchParams(args())).then(r=>r.json())
 if(!r.length){document.getElementById('resultado').innerHTML='<em>Nenhum resultado.</em>';return}
 let h='<table><tr><th>Timestamp</th><th>Sensor</th><th>Zona</th><th>Tipo</th>'+
       '<th>Valor</th><th>Unidade</th><th>Válido</th></tr>'
 r.slice(0,200).forEach(m=>{
  h+=`<tr><td>${m.Timestamp}</td><td>${m.SensorId}</td><td>${m.Zona}</td>`
    +`<td>${m.TipoDado}</td><td>${m.Valor}</td><td>${m.Unidade||''}</td>`
    +`<td>${m.Valido?'✓':'<span class=bad>✗</span>'}</td></tr>`
 })
 h+='</table>'
 if(r.length>200)h+=`<p><em>${r.length} no total (mostrando 200)</em></p>`
 document.getElementById('resultado').innerHTML=h
}

async function analise(sub){
 const params=new URLSearchParams(args()); params.set('sub',sub)
 const r=await fetch('/api/analyse?'+params).then(r=>r.json())
 const html='<h3>Análise '+sub+' (id='+r.id_analise+')</h3>'+
            '<pre>'+JSON.stringify(r.resultado,null,2)+'</pre>'
 document.getElementById('resultado').innerHTML=html
}
</script>
</body></html>"""


@app.route("/")
def index():
    return render_template_string(HTML)


@app.route("/api/query")
def api_query():
    a = request.args
    rows = cli.query(
        tipo=a.get("tipo", ""),
        zona=a.get("zona", ""),
        sensor=a.get("sensor", ""),
        ini=a.get("ini", ""),
        fim=a.get("fim", ""),
        lim=int(a.get("lim", "500") or 500),
    )
    return jsonify(rows)


@app.route("/api/analyse")
def api_analyse():
    a = request.args
    sub = a.get("sub", "STATS").upper()
    out = cli.analyse(
        sub,
        tipo=a.get("tipo", ""),
        zona=a.get("zona", ""),
        sensor=a.get("sensor", ""),
        ini=a.get("ini", ""),
        fim=a.get("fim", ""),
    )
    return jsonify(out)


# ---------------------------------------------------------------------
def main() -> None:
    global cli
    host  = sys.argv[1] if len(sys.argv) > 1 else "127.0.0.1"
    porta = int(sys.argv[2]) if len(sys.argv) > 2 else 9000
    httpp = int(sys.argv[3]) if len(sys.argv) > 3 else 8080
    cli = ServidorClient(host, porta)
    print(f"[WEB] http://localhost:{httpp}   (servidor: {host}:{porta})")
    app.run(host="0.0.0.0", port=httpp, debug=False)


if __name__ == "__main__":
    main()
