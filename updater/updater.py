"""Updater für Yubikey Lifecycle Manager (Docker Compose).

POST /update  {"version": "1.2.3" | null}  -> holt das Image und erstellt den Server-Container neu (202)
POST /refresh                              -> Sicherheitsupdates: alle Images neu laden (gleiche Version),
                                              geänderte Container neu erstellen, auch diesen Updater (202)
GET  /status                               -> Zustand und Protokoll des letzten Updates
GET  /health                               -> ohne Anmeldung, nur "ok"

Anmeldung: Header "Authorization: Bearer <UPDATER_TOKEN>". Ohne UPDATER_TOKEN nimmt der Updater keine Aufträge an.
Erreichbar nur im internen Compose-Netz (kein veröffentlichter Port).

AUTO_REFRESH=HH:MM (Standard 04:00, "off" = aus) startet /refresh täglich zu dieser Uhrzeit (Zeitzone TZ).
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
AUTO_REFRESH = os.environ.get("AUTO_REFRESH", "04:00").strip().lower()
TIME_RE = re.compile(r"^([01]?\d|2[0-3]):([0-5]\d)$")

state = {"state": "idle", "startedAt": None, "finishedAt": None, "version": None, "log": [],
         "refresh": {"schedule": AUTO_REFRESH if TIME_RE.match(AUTO_REFRESH) else None,
                     "lastAt": None, "result": None, "trigger": None}}
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


def now_iso():
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())


def inspect(target, fmt):
    out = subprocess.run(["docker", "inspect", target, "--format", fmt], capture_output=True, text=True)
    return out.stdout.strip() if out.returncode == 0 else ""


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


WINDOWS_PATH_RE = re.compile(r"^([A-Za-z]):[\\/](.*)$")


def daemon_path(source):
    """Pfad des Installationsordners, wie ihn der Docker-Daemon für Bind-Mounts versteht.
    Docker Desktop meldet Ordner von Windows als C:\\Users\\...; im Linux-VM des Daemons liegen sie unter
    /run/desktop/mnt/host/c/Users/... Diesen Pfad kann der Hilfscontainer auch als eigenes Arbeitsverzeichnis nutzen."""
    m = WINDOWS_PATH_RE.match(source)
    if not m:
        return source
    return "/run/desktop/mnt/host/" + m.group(1).lower() + "/" + m.group(2).replace("\\", "/").strip("/")


def helper_name(project):
    return f"{project}-refresh"


def do_refresh(trigger):
    """Lädt alle Images der Installation neu (gleiche Version, aktuelle Sicherheitsupdates der Basis-Images)
    und erstellt geänderte Container neu. Läuft in einem eigenen Hilfscontainer: der darf auch diesen Updater
    ersetzen, und er sieht den Installationsordner unter dem Pfad des Hosts, damit relative Pfade in
    docker-compose.yml (Caddyfile, backup.sh, certs) richtig aufgelöst werden."""
    refresh = state["refresh"]
    refresh.update({"lastAt": now_iso(), "result": "running", "trigger": trigger})
    try:
        project = compose_project()
        if not project:
            raise RuntimeError("Compose-Projekt nicht gefunden. Bitte COMPOSE_PROJECT_NAME setzen.")
        me = socket.gethostname()
        host_dir = inspect(me, '{{range .Mounts}}{{if eq .Destination "/project"}}{{.Source}}{{end}}{{end}}')
        image = inspect(me, "{{.Config.Image}}")
        if not host_dir or not image:
            raise RuntimeError("Installationsordner bzw. eigenes Image nicht ermittelbar.")
        host_dir = daemon_path(host_dir)
        # Gleiche Version behalten: Tag des laufenden Server-Images (sonst "latest" aus .env bzw. Standard).
        env_args = []
        server = subprocess.run(["docker", "ps", "-q", "--filter", f"label=com.docker.compose.project={project}",
                                 "--filter", "label=com.docker.compose.service=server"], capture_output=True, text=True).stdout.split()
        if server:
            tag = inspect(server[0], "{{.Config.Image}}").rsplit(":", 1)
            if len(tag) == 2 and VERSION_RE.match(tag[1]):
                env_args = ["-e", f"YLM_VERSION={tag[1]}"]
                log(f"Server bleibt auf Version {tag[1]}.")
        name = helper_name(project)
        subprocess.run(["docker", "rm", "-f", name], capture_output=True)
        script = ("set -e; docker compose pull --ignore-pull-failures; "
                  "docker compose up -d --no-build; docker image prune -f >/dev/null; echo Sicherheitsupdates abgeschlossen.")
        run(["docker", "run", "--name", name, "--label", f"ylm.refresh={project}",
             "-v", "/var/run/docker.sock:/var/run/docker.sock", "-v", f"{host_dir}:{host_dir}:ro", "-w", host_dir,
             "-e", f"COMPOSE_PROJECT_NAME={project}", *env_args, "--entrypoint", "sh", image, "-c", script], dict(os.environ))
        refresh["result"] = "done"
        state["state"] = "done"
    except Exception as e:  # noqa: BLE001
        refresh["result"] = "failed"
        state["state"] = "failed"
        log(f"Fehler: {e}")
    finally:
        state["finishedAt"] = now_iso()
        lock.release()


def start_refresh(trigger):
    if not lock.acquire(blocking=False):
        return False
    state.update({"state": "running", "startedAt": now_iso(), "finishedAt": None, "version": "refresh", "log": []})
    log(f"Sicherheitsupdates der Container gestartet ({trigger}).")
    threading.Thread(target=do_refresh, args=(trigger,), daemon=True).start()
    return True


def restore_refresh_state():
    """Nach einem Neustart (z. B. weil der Hilfscontainer diesen Updater ersetzt hat): Ergebnis des letzten Laufs lesen."""
    project = compose_project()
    if not project:
        return
    name = helper_name(project)
    info = inspect(name, "{{.State.Status}} {{.State.ExitCode}} {{.State.FinishedAt}}").split()
    if len(info) == 3:
        status, code, finished = info
        state["refresh"].update({"lastAt": finished[:19] + "Z", "trigger": "zuletzt",
                                 "result": "running" if status == "running" else ("done" if code == "0" else "failed")})
        logs = subprocess.run(["docker", "logs", "--tail", "60", name], capture_output=True, text=True)
        state["log"] = (logs.stdout + logs.stderr).splitlines()[-60:]


def scheduler():
    m = TIME_RE.match(AUTO_REFRESH)
    if not m or not TOKEN:
        print("Automatische Sicherheitsupdates der Container: aus.", flush=True)
        return
    print(f"Automatische Sicherheitsupdates der Container täglich um {AUTO_REFRESH}.", flush=True)
    target = (int(m.group(1)), int(m.group(2)))
    last_day = time.strftime("%Y-%m-%d")  # nicht direkt nach dem Start
    while True:
        time.sleep(30)
        t = time.localtime()
        day = time.strftime("%Y-%m-%d", t)
        if (t.tm_hour, t.tm_min) >= target and day != last_day:
            last_day = day
            start_refresh("zeitgesteuert")


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
        if self.path == "/refresh":
            return self._send(202, {"state": "running"}) if start_refresh("von Hand") else self._send(409, {"error": "running"})
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
    try:
        restore_refresh_state()
    except Exception as e:  # noqa: BLE001
        print(f"Letzter Lauf nicht lesbar: {e}", flush=True)
    threading.Thread(target=scheduler, daemon=True).start()
    ThreadingHTTPServer(("0.0.0.0", 8090), Handler).serve_forever()
