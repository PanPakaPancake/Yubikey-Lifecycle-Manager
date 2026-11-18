"""Updater für Yubikey Lifecycle Manager (Docker Compose).

POST /update  {"version": "1.2.3" | null}  -> holt das Image und erstellt den Server-Container neu (202)
GET  /status                               -> Zustand und Protokoll des letzten Updates
GET  /health                               -> ohne Anmeldung, nur "ok"

Anmeldung: Header "Authorization: Bearer <UPDATER_TOKEN>". Ohne UPDATER_TOKEN nimmt der Updater keine Aufträge an.
Erreichbar nur im internen Compose-Netz (kein veröffentlichter Port).
"""
import hmac
import json
import os
import re
import socket
import subprocess
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

TOKEN = os.environ.get("UPDATER_TOKEN", "").strip()
PROJECT_DIR = os.environ.get("PROJECT_DIR", "/project")
SERVICE = os.environ.get("UPDATE_SERVICE", "server")
VERSION_RE = re.compile(r"^v?\d+\.\d+\.\d+([-.][0-9A-Za-z.]+)?$")

state = {"state": "idle", "startedAt": None, "finishedAt": None, "version": None, "log": []}
lock = threading.Lock()


def log(line):
    print(line, flush=True)
    state["log"] = (state["log"] + [line])[-200:]


def compose_project():
    """Projektname aus den Labels des eigenen Containers, damit Compose dieselben Container und Volumes trifft."""
    name = os.environ.get("COMPOSE_PROJECT_NAME")
    if name:
        return name
    out = subprocess.run(["docker", "inspect", socket.gethostname(), "--format",
                          '{{ index .Config.Labels "com.docker.compose.project" }}'], capture_output=True, text=True)
    return out.stdout.strip() or None


def run(cmd, env):
    log("$ " + " ".join(cmd))
    proc = subprocess.Popen(cmd, cwd=PROJECT_DIR, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    for line in proc.stdout:
        log(line.rstrip())
    if proc.wait() != 0:
        raise RuntimeError(f"Befehl fehlgeschlagen ({proc.returncode})")


def do_update(version):
    try:
        project = compose_project()
        if not project:
            raise RuntimeError("Compose-Projekt nicht gefunden. Bitte COMPOSE_PROJECT_NAME setzen.")
        env = dict(os.environ)
        if version:
            env["YLM_VERSION"] = version.lstrip("vV")
        base = ["docker", "compose", "-p", project, "--project-directory", PROJECT_DIR]
        run(base + ["pull", SERVICE], env)
        run(base + ["up", "-d", "--no-build", "--no-deps", SERVICE], env)
        state["state"] = "done"
        log("Update abgeschlossen.")
    except Exception as e:  # noqa: BLE001
        state["state"] = "failed"
        log(f"Fehler: {e}")
    finally:
        state["finishedAt"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
        lock.release()


class Handler(BaseHTTPRequestHandler):
    def _send(self, code, body=None):
        data = json.dumps(body or {}).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def _authorized(self):
        header = self.headers.get("Authorization", "")
        return bool(TOKEN) and header.startswith("Bearer ") and hmac.compare_digest(header[7:].strip(), TOKEN)

    def do_GET(self):
        if self.path == "/health":
            return self._send(200, {"ok": True, "enabled": bool(TOKEN)})
        if not self._authorized():
            return self._send(401, {"error": "unauthorized"})
        if self.path == "/status":
            return self._send(200, state)
        self._send(404)

    def do_POST(self):
        if not self._authorized():
            return self._send(401, {"error": "unauthorized"})
        if self.path != "/update":
            return self._send(404)
        length = int(self.headers.get("Content-Length") or 0)
        try:
            body = json.loads(self.rfile.read(length) or b"{}")
        except ValueError:
            return self._send(400, {"error": "invalid json"})
        version = body.get("version")
        if version is not None and not VERSION_RE.match(str(version)):
            return self._send(400, {"error": "invalid version"})
        if not lock.acquire(blocking=False):
            return self._send(409, {"error": "running"})
        state.update({"state": "running", "startedAt": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
                      "finishedAt": None, "version": version or "latest", "log": []})
        log(f"Update auf {version or 'latest'} gestartet.")
        threading.Thread(target=do_update, args=(version,), daemon=True).start()
        self._send(202, {"state": "running"})

    def log_message(self, fmt, *args):
        pass


if __name__ == "__main__":
    if not TOKEN:
        print("UPDATER_TOKEN ist nicht gesetzt: Updates per Klick sind deaktiviert.", flush=True)
    ThreadingHTTPServer(("0.0.0.0", 8090), Handler).serve_forever()
