"""Updater für Yubikey Lifecycle Manager (Docker Compose).

POST /update  {"version": "1.2.3" | null}  -> holt das Image und erstellt den Server-Container neu (202)
POST /refresh                              -> Sicherheitsupdates: alle Images neu laden (gleiche Version),
                                              geänderte Container neu erstellen, auch diesen Updater (202)
GET  /hosting                              -> Hosting-Einstellungen aus .env (Geheimnisse nur als "gesetzt")
POST /hosting {"env": {...}, "files": {...}} -> .env (und certs/) ändern, prüfen und Container neu erstellen (202)
GET  /status                               -> Zustand und Protokoll des letzten Updates
GET  /health                               -> ohne Anmeldung, nur "ok"

Anmeldung: Header "Authorization: Bearer <UPDATER_TOKEN>". Ohne UPDATER_TOKEN nimmt der Updater keine Aufträge an.
Erreichbar nur im internen Compose-Netz (kein veröffentlichter Port).

AUTO_REFRESH=HH:MM (Standard 04:00, "off" = aus) startet /refresh täglich zu dieser Uhrzeit (Zeitzone TZ).
"""
import base64
import hmac
import json
import os
import re
import socket
import shutil
import subprocess
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

TOKEN = os.environ.get("UPDATER_TOKEN", "").strip()
PROJECT_DIR = os.environ.get("PROJECT_DIR", "/project")
SERVICE = os.environ.get("UPDATE_SERVICE", "server")
VERSION_RE = re.compile(r"^v?\d+\.\d+\.\d+([-.][0-9A-Za-z.]+)?$")
AUTO_REFRESH = os.environ.get("AUTO_REFRESH", "04:00").strip().lower()
TIME_RE = re.compile(r"^([01]?\d|2[0-3]):([0-5]\d)$")

# Hosting-Einstellungen, die die Plattform ändern darf (alles andere in .env bleibt unangetastet).
HOSTING_KEYS = {"YLM_MODE", "YLM_PUBLIC_URL", "COMPOSE_PROFILES", "YLM_DOMAIN", "YLM_DOMAIN_ALIASES", "YLM_CADDY_TLS",
                "YLM_HTTPS_BIND", "YLM_HTTPS_PORT", "YLM_HTTP_PORT", "YLM_BIND", "YLM_ACME_EMAIL", "YLM_DNS_PROVIDER",
                "YLM_DNS_TOKEN", "YLM_DNS_KEY_ID", "YLM_DNS_SECRET", "YLM_DNS_REGION", "YLM_DNS_AZURE_TENANT",
                "YLM_DNS_AZURE_SUBSCRIPTION", "YLM_DNS_AZURE_RESOURCE_GROUP", "YLM_DNS_NETCUP_CUSTOMER"}
HOSTING_SECRETS = {"YLM_DNS_TOKEN", "YLM_DNS_SECRET"}
CERT_FILES = {"ylm.crt", "ylm.key"}
# Ohne Steuerzeichen und ohne Zeichen, die Compose in .env auswertet ($ für Variablen, Anführungszeichen, Backslash).
VALUE_RE = re.compile("^[^\\x00-\\x1f\\x7f$'\"\\\\]*$")

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
        log("Server aktualisiert. Weitere Container (Updater, Caddy, Datensicherung) auf dieselbe Version bringen …")
        follow_up_refresh("Update")
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


def env_get(path, key):
    try:
        with open(path, encoding="utf-8") as f:
            for line in f:
                if line.startswith(key + "="):
                    return line.rstrip("\r\n")[len(key) + 1:]
    except FileNotFoundError:
        pass
    return None


def read_env(path):
    values = {}
    try:
        with open(path, encoding="utf-8") as f:
            for line in f:
                line = line.rstrip("\r\n")
                if line and not line.lstrip().startswith("#") and "=" in line:
                    k, v = line.split("=", 1)
                    values[k.strip()] = v
    except FileNotFoundError:
        pass
    return values


def write_env(path, changes):
    """Setzt bzw. entfernt Schlüssel in .env und behält Kommentare und Reihenfolge."""
    lines = []
    if os.path.exists(path):
        with open(path, encoding="utf-8") as f:
            lines = f.read().splitlines()
    done = set()
    out = []
    for line in lines:
        key = line.split("=", 1)[0].strip() if "=" in line and not line.lstrip().startswith("#") else None
        if key in changes:
            done.add(key)
            if changes[key] is not None:
                out.append(f"{key}={changes[key]}")
            continue
        out.append(line)
    for key, value in changes.items():
        if key not in done and value is not None:
            out.append(f"{key}={value}")
    with open(path, "w", encoding="utf-8") as f:
        f.write("\n".join(out) + "\n")


def hosting_view():
    env = read_env(os.path.join(PROJECT_DIR, ".env"))
    values = {k: v for k, v in env.items() if k in HOSTING_KEYS and k not in HOSTING_SECRETS}
    return {"env": values, "secretsSet": sorted(k for k in HOSTING_SECRETS if env.get(k)),
            "files": sorted(f for f in CERT_FILES if os.path.exists(os.path.join(PROJECT_DIR, "certs", f))),
            "certificate": read_text(os.path.join(PROJECT_DIR, "certs", "ylm.crt")),
            "hosting": True}


def read_text(path):
    try:
        with open(path, encoding="utf-8") as f:
            return f.read(200_000)
    except OSError:
        return None


def caddy_root_ca():
    """Stammzertifikat der internen Zertifizierungsstelle von Caddy (tls internal), zum Verteilen an die Admin-PCs."""
    project = compose_project()
    if not project:
        return None
    ids = subprocess.run(["docker", "ps", "-q", "--filter", f"label=com.docker.compose.project={project}",
                          "--filter", "label=com.docker.compose.service=caddy"], capture_output=True, text=True).stdout.split()
    if not ids:
        return None
    out = subprocess.run(["docker", "exec", ids[0], "cat", "/data/caddy/pki/authorities/local/root.crt"], capture_output=True, text=True)
    return out.stdout if out.returncode == 0 and "BEGIN CERTIFICATE" in out.stdout else None


def validate_hosting(body):
    changes = body.get("env") or {}
    files = body.get("files") or {}
    if not isinstance(changes, dict) or not isinstance(files, dict):
        return "invalid body"
    for k, v in changes.items():
        if k not in HOSTING_KEYS:
            return f"key not allowed: {k}"
        if v is not None and (not isinstance(v, str) or len(v) > 2000 or not VALUE_RE.match(v)):
            return f"invalid value: {k}"
    for name, content in files.items():
        if name not in CERT_FILES or not isinstance(content, str) or len(content) > 200_000 or "-----BEGIN " not in content:
            return f"invalid file: {name}"
    if not changes and not files:
        return "nothing to change"
    return None


def apply_hosting_in_helper():
    """Läuft im Hilfscontainer im Installationsordner (Host-Pfad): .env sichern, ändern, prüfen, Container neu erstellen.
    Schlägt die Prüfung oder der Start fehl, wird der vorige Stand wiederhergestellt."""
    body = json.loads(base64.b64decode(os.environ["YLM_HOSTING"]))
    changes, files = body.get("env") or {}, body.get("files") or {}
    shutil.copyfile(".env", ".env.bak")
    os.makedirs("certs", exist_ok=True)
    backups = {}
    for name, content in files.items():
        target = os.path.join("certs", name)
        if os.path.exists(target):
            backups[target] = open(target, encoding="utf-8").read()
        with open(target, "w", encoding="utf-8") as f:
            f.write(content.replace("\r\n", "\n"))
        os.chmod(target, 0o600 if name.endswith(".key") else 0o644)
    was_https = "https" in (env_get(".env.bak", "COMPOSE_PROFILES") or "")
    write_env(".env", changes)
    https = "https" in (env_get(".env", "COMPOSE_PROFILES") or "")
    print("Einstellungen in .env geändert (vorher: .env.bak): " + (", ".join(sorted(changes)) or "-"), flush=True)

    def sh(cmd):
        print("$ " + " ".join(cmd), flush=True)
        return subprocess.run(cmd).returncode == 0

    def restore(reason):
        print(f"{reason} Stelle den vorigen Stand wieder her.", flush=True)
        shutil.copyfile(".env.bak", ".env")
        for target, content in backups.items():
            with open(target, "w", encoding="utf-8") as f:
                f.write(content)
        sh(["docker", "compose", "up", "-d", "--no-build"])
        sys.exit(1)

    env_args = ["docker", "compose"]
    if not sh(env_args + ["config", "-q"]):
        restore("Die Compose-Konfiguration ist ungültig.")
    if https and not sh(env_args + ["run", "--rm", "--no-deps", "-T", "--entrypoint", "caddy", "caddy",
                                    "validate", "--config", "/etc/caddy/Caddyfile", "--adapter", "caddyfile"]):
        restore("Die Caddy-Konfiguration ist ungültig (Zertifikat, Schlüssel oder DNS-Anbieter prüfen).")
    if was_https and not https:
        sh(["docker", "compose", "--profile", "https", "rm", "-sf", "caddy"])
    if not sh(env_args + ["up", "-d", "--no-build"]):
        restore("Die Container ließen sich nicht starten.")
    print("Hosting-Einstellungen übernommen.", flush=True)


def run_helper(name_suffix, script, extra_env, writable):
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
    for k, v in extra_env.items():
        env_args += ["-e", f"{k}={v}"]
    name = f"{project}-{name_suffix}"
    subprocess.run(["docker", "rm", "-f", name], capture_output=True)
    mount = f"{host_dir}:{host_dir}" + ("" if writable else ":ro")
    run(["docker", "run", "--name", name, "--label", f"ylm.{name_suffix}={project}",
         "-v", "/var/run/docker.sock:/var/run/docker.sock", "-v", mount, "-w", host_dir,
         "-e", f"COMPOSE_PROJECT_NAME={project}", *env_args, "--entrypoint", "sh", image, "-c", script], dict(os.environ))


def do_hosting(body):
    try:
        payload = base64.b64encode(json.dumps(body).encode()).decode()
        run_helper("hosting", "python3 /updater.py apply-hosting", {"YLM_HOSTING": payload}, writable=True)
        state["state"] = "done"
        log("Hosting-Einstellungen übernommen.")
    except Exception as e:  # noqa: BLE001
        state["state"] = "failed"
        log(f"Fehler: {e}")
    finally:
        state["finishedAt"] = now_iso()
        lock.release()


REFRESH_SCRIPT = ("set -e; docker compose pull --ignore-pull-failures; "
                  "docker compose up -d --no-build; docker image prune -f >/dev/null; echo Sicherheitsupdates abgeschlossen.")


def follow_up_refresh(trigger):
    """Nach dem Server-Update die übrigen Container nachziehen. Der Hilfscontainer übernimmt die Version des
    gerade gestarteten Servers und darf auch diesen Updater ersetzen. Ein Fehler hier lässt das Update selbst gelten."""
    refresh = state["refresh"]
    refresh.update({"lastAt": now_iso(), "result": "running", "trigger": trigger})
    try:
        run_helper("refresh", REFRESH_SCRIPT, {}, writable=False)
        refresh["result"] = "done"
    except Exception as e:  # noqa: BLE001
        refresh["result"] = "failed"
        log(f"Hinweis: Die übrigen Container ließen sich nicht aktualisieren ({e}). "
            "Der Server ist aktuell; „Sicherheitsupdates der Container – Jetzt einspielen“ versucht es erneut.")


def do_refresh(trigger):
    """Lädt alle Images der Installation neu (gleiche Version, aktuelle Sicherheitsupdates der Basis-Images)
    und erstellt geänderte Container neu. Läuft in einem eigenen Hilfscontainer: der darf auch diesen Updater
    ersetzen, und er sieht den Installationsordner unter dem Pfad des Hosts, damit relative Pfade in
    docker-compose.yml (Caddyfile, backup.sh, certs) richtig aufgelöst werden."""
    refresh = state["refresh"]
    refresh.update({"lastAt": now_iso(), "result": "running", "trigger": trigger})
    try:
        run_helper("refresh", REFRESH_SCRIPT, {}, writable=False)
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
        if self.path == "/hosting":
            return self._send(200, hosting_view())
        if self.path == "/hosting/root-ca":
            pem = caddy_root_ca()
            return self._send(200, {"pem": pem}) if pem else self._send(404, {"error": "no root ca"})
        self._send(404)

    def do_POST(self):
        if not self._authorized():
            return self._send(401, {"error": "unauthorized"})
        if self.path == "/refresh":
            return self._send(202, {"state": "running"}) if start_refresh("von Hand") else self._send(409, {"error": "running"})
        if self.path not in ("/update", "/hosting"):
            return self._send(404)
        length = int(self.headers.get("Content-Length") or 0)
        try:
            body = json.loads(self.rfile.read(length) or b"{}")
        except ValueError:
            return self._send(400, {"error": "invalid json"})
        if self.path == "/hosting":
            error = validate_hosting(body)
            if error:
                return self._send(400, {"error": error})
            if not lock.acquire(blocking=False):
                return self._send(409, {"error": "running"})
            state.update({"state": "running", "startedAt": now_iso(), "finishedAt": None, "version": "hosting", "log": []})
            log("Hosting-Einstellungen werden übernommen: " + ", ".join(sorted((body.get("env") or {}).keys()) + sorted(body.get("files") or {})))
            threading.Thread(target=do_hosting, args=(body,), daemon=True).start()
            return self._send(202, {"state": "running"})
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
    if sys.argv[1:] == ["apply-hosting"]:
        apply_hosting_in_helper()
        sys.exit(0)
    if not TOKEN:
        print("UPDATER_TOKEN ist nicht gesetzt: Updates per Klick sind deaktiviert.", flush=True)
    try:
        restore_refresh_state()
    except Exception as e:  # noqa: BLE001
        print(f"Letzter Lauf nicht lesbar: {e}", flush=True)
    threading.Thread(target=scheduler, daemon=True).start()
    ThreadingHTTPServer(("0.0.0.0", 8090), Handler).serve_forever()
