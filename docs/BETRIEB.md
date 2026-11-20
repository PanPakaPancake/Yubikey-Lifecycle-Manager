# Betrieb und Installation

## 1. Server installieren

Die Plattform läuft als kleiner Docker-Compose-Stapel aus fertigen Images. Gebaut wird nichts, es braucht nur Docker.

| Dienst | Aufgabe |
|---|---|
| `server` | Plattform (Web-Oberfläche, API, Agent-Verbindung) |
| `db` | PostgreSQL |
| `backup` | tägliche Datensicherung nach `backups/` (nutzt das PostgreSQL-Image, kein zusätzlicher Download) |
| `updater` | Update per Klick aus der Oberfläche |
| `caddy` | nur auf Servern: HTTPS mit interner CA, eigenem Zertifikat oder Let's Encrypt |

Ressourcen: 1 vCPU, 1 GB RAM und 10 GB Platte reichen für den Anfang. Die Images gibt es für `linux/amd64` und `linux/arm64` (ab dem nächsten Release).

### Welche Variante?

Die Plattform verwaltet Anmeldemethoden in allen angebundenen Tenants. Sie gehört deshalb **nicht offen ins Internet**, sondern ins interne Netz oder hinter einen geschützten Zugang (ZTNA, VPN, Application Proxy). Darauf sind die Varianten 1 bis 3 ausgelegt.

| Nr. | Variante | Adresse | HTTPS | Für wen |
|---|---|---|---|---|
| 1 | **Intern, eigene CA** (`--mode internal`, Standard) | `https://ylm.firma.local` | Caddy mit interner Zertifizierungsstelle; Stammzertifikat per GPO/Intune verteilen | internes Netz, ZTNA ohne eigene PKI |
| 2 | **Intern, eigenes Zertifikat** (`--mode cert`) | `https://ylm.firma.de` | Zertifikat der Firmen-PKI oder ein gekauftes | wer schon eine PKI hat |
| 3 | **Reverse Proxy / ZTNA-Connector** (`--mode proxy`) | Adresse des Proxys | macht der Proxy (nginx, Traefik, Cloudflare Tunnel, Entra Private Access, Zscaler …) | wer Zugänge zentral veröffentlicht |
| 4 | Öffentlich mit Let's Encrypt (`--mode https`) | `https://ylm.example.com` | automatisch | nur wenn der Server bewusst aus dem Internet erreichbar sein soll |
| 5 | Lokal auf dem Admin-PC (`--mode local`, `install.ps1`) | `http://localhost:8080` | nicht nötig | Ausprobieren, Einzelplatz |

Passkeys und Entra funktionieren außerhalb von `localhost` nur über HTTPS mit einem Zertifikat, dem der Browser vertraut. Deshalb gibt es auf einem Server immer HTTPS, auch intern.

Was in jedem Fall gilt:

- Die Plattform braucht **ausgehend** HTTPS zu `login.microsoftonline.com`, `graph.microsoft.com` und (für Updates) `ghcr.io`/`api.github.com`. **Eingehend** braucht es nur den Zugang der Admin-PCs. Entra ruft die Plattform nie selbst auf; die Umleitung nach der Anmeldung läuft über den Browser des Admins. Deshalb reicht ein interner DNS-Name.
- Die Agents verbinden sich ausgehend vom Admin-PC zur Plattform (WebSocket unter `/hubs/`). Über ZTNA muss diese Verbindung ebenfalls durchgehen.
- Die Datenbank lauscht nie nach außen, der Server-Port 8080 nur auf `127.0.0.1` (änderbar mit `YLM_BIND`).

### 1.1 Lokal auf dem Admin-PC (Windows)

Voraussetzung: [Docker Desktop](https://www.docker.com/products/docker-desktop/) ist installiert und gestartet. Dann in PowerShell (keine Adminrechte nötig):

```powershell
irm https://raw.githubusercontent.com/PanPakaPancake/Yubikey-Lifecycle-Manager/main/install.ps1 | iex
```

Das Skript legt `%USERPROFILE%\YubikeyLifecycleManager` an, erzeugt dort die `.env` mit zufälligem Datenbank-Passwort, Updater-Geheimnis und Passphrase für die Sicherungen, lädt die Images, startet alles und zeigt am Ende die **Passphrase** (in den Passwortmanager übernehmen) und das **Setup-Token**. Der Browser öffnet `http://localhost:8080/setup`; weiter mit [Abschnitt 2](#2-einrichtung-einmalig).

Anderer Ordner oder andere Variante: `& ([scriptblock]::Create((irm https://raw.githubusercontent.com/PanPakaPancake/Yubikey-Lifecycle-Manager/main/install.ps1))) -Dir D:\ylm -Mode internal -Domain ylm.firma.local` (Varianten wie bei `install.sh`, eigenes Zertifikat mit `-Mode cert -Cert ylm.crt -Key ylm.key`).

### 1.2 Server im internen Netz (empfohlen)

Voraussetzungen:

- Linux-Server oder VM (z. B. Debian 12/13 oder Ubuntu 24.04), Zugang per SSH mit `sudo`
- Interner DNS-Eintrag, z. B. `ylm.firma.local`, zeigt auf den Server
- Port **443** (und 80 für die Umleitung) aus dem Admin-Netz bzw. vom ZTNA-Connector erreichbar
- Docker muss nicht vorinstalliert sein; das Skript bietet die Installation über `get.docker.com` an

```bash
curl -fsSL https://raw.githubusercontent.com/PanPakaPancake/Yubikey-Lifecycle-Manager/main/install.sh | sudo bash
```

Das Skript fragt nach der Variante und dem DNS-Namen, legt `/opt/ylm` mit `.env` an (Rechte `600`), startet die Dienste samt Caddy und zeigt Passphrase und Setup-Token. Danach `https://ylm.firma.local/setup` öffnen und mit [Abschnitt 2](#2-einrichtung-einmalig) weitermachen.

**Variante 1, interne CA:** Caddy erzeugt eine eigene Zertifizierungsstelle und stellt damit das Serverzertifikat aus (verlängert es auch selbst). Das Skript legt das Stammzertifikat als `/opt/ylm/ylm-root-ca.crt` ab. Es muss auf allen Admin-PCs als vertrauenswürdige Stammzertifizierungsstelle installiert sein, sonst warnt der Browser und der Agent verbindet sich nicht:

- **GPO:** Computerkonfiguration → Richtlinien → Windows-Einstellungen → Sicherheitseinstellungen → Richtlinien für öffentliche Schlüssel → Vertrauenswürdige Stammzertifizierungsstellen → Importieren.
- **Intune:** Geräte → Konfiguration → Vorlage „Vertrauenswürdiges Zertifikat“ (Windows 10 und höher), Zielspeicher „Computerzertifikatspeicher – Stamm“.
- **Einzelner PC:** als Administrator `certutil -addstore Root ylm-root-ca.crt`.

Die CA liegt im Volume `caddy-data` und bleibt bei Updates erhalten. Das Stammzertifikat ist 10 Jahre gültig.

**Variante 2, eigenes Zertifikat:** Zertifikat (PEM, mit Zwischenzertifikaten) und unverschlüsselten Schlüssel mitgeben. Das Skript kopiert sie nach `/opt/ylm/certs/ylm.crt` und `ylm.key`:

```bash
sudo bash install.sh --mode cert --domain ylm.firma.de --cert ylm.crt --key ylm.key
```

Erneuern: neue Dateien nach `certs/` kopieren (gleiche Namen), dann `docker compose restart caddy`.

Ohne Rückfragen: `… | sudo bash -s -- --mode internal --domain ylm.firma.local --yes`. Weitere Optionen: `--dir PFAD`, `--version v0.7.0`, `--url https://…`, `--bind 10.0.0.5` (Caddy bzw. Port 8080 nur auf dieser Adresse). Die Variante lässt sich später umstellen, indem man das Skript mit einem anderen `--mode` erneut ausführt; Daten und `.env` bleiben erhalten.

Empfehlungen für den Server:

- In der Firewall nur SSH (aus dem Admin-Netz) und 80/443 (aus dem Admin-Netz bzw. vom ZTNA-Connector) zulassen.
- SSH nur mit Schlüssel, automatische Sicherheitsupdates des Betriebssystems einschalten (z. B. `unattended-upgrades`).
- Die Sicherungen in `/opt/ylm/backups` regelmäßig auf einen anderen Rechner kopieren (siehe [1.6](#16-datensicherung-und-wiederherstellung)).

**Variante 4, öffentlich mit Let's Encrypt:** `--mode https --domain ylm.example.com`. Dafür muss der öffentliche DNS-Eintrag auf den Server zeigen und Port 80/443 aus dem Internet offen sein. Nur wählen, wenn das gewollt ist; dann zusätzlich den Zugang beschränken (z. B. Firewall auf bekannte Adressen).

### 1.3 Hinter einem Reverse Proxy oder ZTNA-Connector

Läuft schon nginx, Traefik o. ä. oder veröffentlicht ein ZTNA-Dienst die Plattform (Cloudflare Tunnel, Entra Private Access, Zscaler Private Access …), im Skript Variante 3 wählen (oder `--mode proxy --url https://ylm.firma.de`). Caddy bleibt dann aus. Das Skript fragt, ob der Proxy auf demselben Server läuft; sonst lauscht Port 8080 auf der internen Adresse (`YLM_BIND`, z. B. `10.0.0.5:8080`).

Der Proxy leitet HTTPS auf Port 8080 weiter, setzt `X-Forwarded-Proto` und `X-Forwarded-Host` und lässt WebSockets durch (Agent-Verbindung unter `/hubs/`, Zeitlimit mindestens 5 Minuten). Beispiel nginx:

```nginx
location / {
    proxy_pass http://127.0.0.1:8080;
    proxy_http_version 1.1;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection $connection_upgrade;   # map $http_upgrade $connection_upgrade { default upgrade; '' close; }
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-Host $host;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_read_timeout 300s;
}
```

ZTNA-Hinweise:

- `YLM_PUBLIC_URL` ist die Adresse, die Browser und Agents über den ZTNA-Zugang aufrufen. Unter genau dieser Adresse muss auch die Umleitungs-URI der Entra-App stehen (setzt der Setup-Assistent).
- Vorgeschaltete Anmeldung des ZTNA-Dienstes (z. B. Cloudflare Access) ist gut, darf aber die Agent-Verbindung nicht blockieren: Der Agent kann keine interaktive Anmeldung. Entweder `/hubs/agent` und `/api/agent/` (Kopplung, Updates) per Service-Token oder Geräteprüfung freigeben oder die Agents über den Geräte-Client des ZTNA-Dienstes verbinden.
- Der Server übernimmt Forwarded-Header (Client-IP für Audit-Log und Anmeldebremse) nur von Loopback und privaten Netzen (10/8, 172.16/12, 192.168/16, 100.64/10, fc00::/7). Kommt der Connector von einer anderen Adresse, diese in `.env` eintragen: `YLM_TRUSTED_PROXIES=203.0.113.10, 2001:db8::/32` (`*` vertraut allen, nicht empfohlen). Port 8080 sollte nie direkt aus dem Internet erreichbar sein.

### 1.4 Ohne Skript installieren

```bash
mkdir -p /opt/ylm/deploy && cd /opt/ylm
base=https://raw.githubusercontent.com/PanPakaPancake/Yubikey-Lifecycle-Manager/main
for f in docker-compose.yml deploy/Caddyfile deploy/backup.sh .env.example; do curl -fsSL "$base/$f" -o "$f"; done
cp .env.example .env && nano .env   # POSTGRES_PASSWORD, UPDATER_TOKEN (openssl rand -hex 32), YLM_PUBLIC_URL, ggf. COMPOSE_PROFILES=https und YLM_DOMAIN
docker compose up -d
docker compose logs server | grep Setup-Token
```

Beim ersten Start einer leeren Datenbank meldet Entity Framework einmal einen Fehler beim Lesen der Migrationstabelle. Das ist normal; danach werden die Tabellen angelegt.

### 1.5 Eigene Anpassungen

`docker-compose.yml`, `deploy/Caddyfile` und `deploy/backup.sh` gehören zur Version und werden beim erneuten Ausführen des Installationsskripts ersetzt (die alte Fassung bleibt als `.bak`). Bitte dort nichts ändern:

- **Einstellungen** in `.env` (Erklärung aller Werte in `.env.example`), danach `docker compose up -d`.
- **Ergänzungen** in `docker-compose.override.yml` im selben Ordner; Docker Compose liest sie automatisch mit. Beispiel:

  ```yaml
  services:
    server:
      environment:
        Ylm__TrustedProxies__1: "203.0.113.10"
  ```

- **Eigenes Zertifikat**: Variante 2 in [1.2](#12-server-im-internen-netz-empfohlen), keine eigene Caddyfile mehr nötig.

Den Ordner der Installation nicht umbenennen, wenn `.env` kein `COMPOSE_PROJECT_NAME` enthält: Docker leitet die Namen der Volumes sonst vom Ordnernamen ab und startet mit leeren Daten.

### 1.6 Datensicherung und Wiederherstellung

Der Dienst `backup` sichert täglich um 02:30 (`BACKUP_TIME`) die Datenbank und das Volume `data` in **ein** Archiv in `backups/` und löscht Sicherungen, die älter als 14 Tage sind (`BACKUP_KEEP_DAYS`). Das Volume `data` enthält App-Zertifikat, Data-Protection-Schlüssel und Geheimnisse. Ohne diese Schlüssel sind Zertifikat, feste Profil-PINs, SMTP-Passwort und Anmelde-Cookies nicht mehr lesbar. Deshalb gehören beide Teile immer zusammen.

**Verschlüsselung:** Mit `BACKUP_PASSPHRASE` in `.env` (legt das Installationsskript an, auch bei bestehenden Installationen) wird das Archiv mit AES-256 verschlüsselt (`ylm-JJJJMMTT-HHMMSS.tar.gz.enc`, OpenSSL-Format, Schlüssel per PBKDF2-SHA256 mit 600 000 Runden). Damit kann eine Kopie außer Haus nicht mehr als Generalschlüssel für alle Tenants missbraucht werden. Die Passphrase **zusätzlich im Passwortmanager** ablegen: Ist der Server weg, ist sie sonst mit ihm verloren. Ohne Passphrase sind die Archive unverschlüsselt (`.tar.gz`); das Protokoll des Dienstes weist darauf hin.

```bash
docker compose exec backup sh /backup.sh now        # sofort sichern
docker compose exec backup sh /backup.sh list       # vorhandene Sicherungen (verschlüsselt/unverschlüsselt)
```

Wiederherstellen (gleiche oder neuere Version der Plattform):

```bash
docker compose stop server
docker compose exec backup sh /backup.sh restore ylm-20261119-023000.tar.gz.enc
docker compose start server
```

Auf einem neuen Server mit anderer Passphrase die alte mitgeben: `docker compose exec -e BACKUP_PASSPHRASE='…' backup sh /backup.sh restore DATEI`. Von Hand entschlüsseln: `openssl enc -d -aes-256-cbc -pbkdf2 -iter 600000 -md sha256 -in DATEI.enc -out DATEI.tar.gz`. Ältere, unverschlüsselte Sicherungen lassen sich weiter einspielen.

Eine Sicherung von einem anderen Rechner vorher nach `backups/` kopieren. Kopie außer Haus, z. B. per Cron auf einem anderen Rechner: `rsync -a root@ylm-server:/opt/ylm/backups/ /srv/ylm-backups/`.

### 1.6a App-Zertifikat

Mit dem App-Zertifikat meldet sich die Plattform in jedem angebundenen Tenant bei Microsoft Graph an. Es ist zwei Jahre gültig. **Globale Einstellungen → App-Zertifikat** zeigt Ablaufdatum und Zustand; ab 30 Tagen vor dem Ablauf erscheint es auch unter „Handlungsbedarf“ in der Übersicht, im Audit-Log und als Ereignis „App-Zertifikat läuft bald ab“ für Webhooks und Mail-Alarme (Gruppe „Plattform“).

- **Automatische Erneuerung** (Standard, `YLM_CERT_AUTO_RENEW=true`): 30 Tage vor dem Ablauf erzeugt die Plattform ein neues Zertifikat und hängt es per Graph `addKey` an ihre eigene App-Registrierung. Den Nachweis signiert sie mit dem noch gültigen Zertifikat; ein zusätzliches Recht braucht die App dafür nicht. Erst wenn Entra mit dem neuen Zertifikat Token ausgibt, schaltet sie um. Das alte läuft in Entra von selbst ab. Schlägt es fehl, versucht sie es alle 6 Stunden erneut und warnt wöchentlich, in der letzten Woche täglich. „Jetzt erneuern“ startet die Erneuerung sofort.
- **Von Hand** (App-Registrierung per PowerShell-Skript angelegt, Zertifikat schon abgelaufen, automatische Erneuerung abgeschaltet): „Neues Zertifikat von Hand vorbereiten“, `.cer` herunterladen, im Entra Admin Center unter App-Registrierungen → YLM-App → Zertifikate und Geheimnisse hochladen, dann „Jetzt aktivieren“.
- Nach einem Wechsel sofort eine Sicherung machen (`backup.sh now`): Ältere Sicherungen enthalten nur das alte Zertifikat.

### 1.7 Umzug, z. B. vom Admin-PC auf einen VPS

1. Alte Installation: `docker compose exec backup sh /backup.sh now`, Archiv aus `backups\` kopieren.
2. Neue Installation per Skript anlegen (1.2) und das Archiv nach `/opt/ylm/backups/` kopieren. Bei verschlüsselten Sicherungen die alte Passphrase bereithalten.
3. Wiederherstellen wie in 1.6. Die Plattform läuft danach mit allen Daten unter der neuen Adresse aus `YLM_PUBLIC_URL`.
4. **Entra:** In der App-Registrierung der Plattform (Heim-Tenant → App-Registrierungen → „Yubikey Lifecycle Manager <Organisation>“ → Authentifizierung) die neuen Umleitungs-URIs `https://<neu>/signin-oidc` und `https://<neu>/api/tenants/consent-callback` ergänzen.
5. **Lokale Benutzer** mit Passkey brauchen einen neuen Einrichtungslink (Passkeys gehören zur Adresse), **Agents** einen neuen Kopplungscode.
6. Alte Installation stoppen: `docker compose down` (Volumes bleiben erhalten, bis sie mit `down -v` gelöscht werden).

### 1.8 Einstellungen (Umgebungsvariablen)

In `.env`:

| Variable | Standard | Bedeutung |
|---|---|---|
| `POSTGRES_PASSWORD` | – | Datenbank-Passwort (Pflicht, nach der Installation nicht mehr ändern) |
| `YLM_PUBLIC_URL` | aus der Einrichtung | Adresse für Browser, Entra und Agents |
| `YLM_MODE` | – | gewählte Variante (nur Information; umstellen per Installationsskript) |
| `COMPOSE_PROFILES` | leer | `https` schaltet Caddy ein |
| `YLM_DOMAIN` | – | DNS-Name für Caddy |
| `YLM_CADDY_TLS` | leer | `tls internal` (interne CA), `tls /certs/ylm.crt /certs/ylm.key` (eigenes Zertifikat), leer = Let's Encrypt |
| `YLM_HTTPS_BIND` | `0.0.0.0` | Adresse, auf der Caddy die Ports 80/443 öffnet |
| `YLM_BIND` | `127.0.0.1:8080` | Adresse, auf der der Server-Port lauscht |
| `YLM_TRUSTED_PROXIES` | private Netze | Proxys/Connectoren, deren Forwarded-Header gelten (Komma-Liste) |
| `YLM_AUTO_REFRESH` | `04:00` | Uhrzeit der täglichen Sicherheitsupdates der Container (`off` = aus) |
| `UPDATER_TOKEN` | leer | Geheimnis für das Update per Klick; leer = aus |
| `YLM_VERSION` | `latest` | Version des Server-Images festhalten |
| `BACKUP_TIME`, `BACKUP_KEEP_DAYS`, `BACKUP_DIR` | `02:30`, `14`, `./backups` | Datensicherung (`BACKUP_TIME=off` schaltet sie ab) |
| `BACKUP_PASSPHRASE` | vom Skript erzeugt | verschlüsselt die Sicherungen; leer = unverschlüsselt |
| `YLM_CERT_AUTO_RENEW` | `true` | App-Zertifikat selbst erneuern (sonst nur warnen) |
| `YLM_REQUIRE_PHISHING_RESISTANT` | `false` | zusätzlich die Anmeldemethode der Admins prüfen. Standard aus: das regelt der Tenant-Betreiber in Entra (Bedingter Zugriff, Authentifizierungsstärke „Phishing-resistente MFA“) |
| `YLM_ACCEPTED_AMR` | `fido` | bei eingeschalteter Prüfung akzeptierte Anmeldemethoden laut Microsoft-Token (Komma-Liste); abgelehnte Methode steht auf der Anmeldeseite |
| `TZ` | `Europe/Berlin` | Zeitzone |

Weitere Einstellungen des Servers über `docker-compose.override.yml` (`services.server.environment`):

| Variable | Standard | Bedeutung |
|---|---|---|
| `Ylm__DefaultSyncIntervalMinutes` | 60 | Sync-Intervall für neue Tenants |
| `Ylm__OffboardingRetentionDays` | 30 | Aufbewahrung nach dem Offboarding |
| `Ylm__AppCertificateRenewalDays` | 30 | so viele Tage vor dem Ablauf warnen und erneuern |
| `Ylm__UpdateCheckEnabled` | true | Auf GitHub nach neuen Versionen suchen |

### 1.9 Updates

**Globale Einstellungen → Version und Updates** zeigt die installierte und die neueste Version (GitHub-Releases, Prüfung alle 6 Stunden). Ein Punkt neben „Globale Einstellungen“ weist Owner auf ein Update hin.

- **Update per Klick:** Der Container `updater` holt das neue Server-Image und startet den Server neu (`docker compose pull server` und `up -d server`). Nur dieser Container hat Zugriff auf den Docker-Socket, der Server selbst nicht. Das Installationsskript setzt dafür `UPDATER_TOKEN`; ohne Token nimmt der Updater keine Aufträge an.
- **Installationsdateien auffrischen:** Neue Versionen können auch `docker-compose.yml` und die Dateien in `deploy/` ändern (z. B. neue Dienste). Dafür das Installationsskript gelegentlich erneut ausführen; es behält `.env` und aktualisiert Dateien und alle Images. Ohne Skript: `docker compose pull && docker compose up -d`.
- **Agents:** Jedes Server-Image bringt das passende Agent-MSI mit. „Mein Agent“ bietet es zum Download an, und verbundene Agents installieren neue Versionen innerhalb von 4 Stunden selbst (still per msiexec, Prüfsumme wird geprüft). Vor der Installation beendet der Dienst die Tray-Apps und startet sie danach in denselben Benutzersitzungen neu.
  - **Vorgabe:** Globale Einstellungen → Version und Updates → **Agent-Updates**: „Automatisch, am PC abschaltbar“ (Standard), „Immer automatisch“ oder „Keine automatischen Updates“. Darunter steht die Liste aller Agents mit Version, Auto-Update und letztem Fehler.
  - **Am PC:** Tray → **Einstellungen**: „Mit Windows starten“ (pro Benutzer), „Updates automatisch installieren“ (für den PC, nur lokale Administratoren), „Jetzt nach Updates suchen“. „Jetzt aktualisieren“ gibt es auch unter **Agent** in der Weboberfläche.
  - **Fehlersuche:** Jede Prüfung steht im Agent-Log (`Update: …`), das msiexec-Protokoll unter `C:\ProgramData\YubikeyLifecycleManager\Agent\updates\install-<Version>.log` (nur für Administratoren lesbar). Nach einem Fehler versucht der Agent es nach 30 Minuten erneut.
  - `"Agent": { "AutoUpdate": false }` in der `appsettings.json` des Agents setzt den Standard der lokalen Einstellung auf „aus“ (z. B. für Images).
- **Sicherheitsupdates der Container:** Unabhängig von neuen Versionen lädt der Updater täglich um `YLM_AUTO_REFRESH` (Standard 04:00, nach der Sicherung) alle Images der installierten Version neu und erstellt geänderte Container neu, auch sich selbst (über einen kurzlebigen Hilfscontainer `<projekt>-refresh`). Die Plattform ist dabei kurz nicht erreichbar. „Jetzt einspielen“ unter **Version und Updates** startet es sofort; dort stehen Zeitpunkt und Ergebnis des letzten Laufs.
  - **Woher die Patches kommen:** Der Workflow „Sicherheits-Neubau“ prüft täglich die veröffentlichten Images mit Trivy. Bei behebbaren HIGH/CRITICAL-Schwachstellen und ohnehin jeden Montag baut er Server- und Updater-Image der neuesten Version ohne Cache auf den aktuellen Basis-Images neu und veröffentlicht sie unter demselben Tag. PostgreSQL und Caddy kommen als offizielle Images (`postgres:16`, `caddy:2`), deren Patches der Updater ebenfalls lädt. Die CI scannt jeden Build und schlägt bei behebbaren CRITICAL-Funden fehl; Dependabot schlägt wöchentlich neue Basis-Images, Pakete und Actions vor.
  - **Schlankes Server-Image:** Der Server läuft auf `aspnet:10.0-noble-chiseled-extra` (nur .NET-Laufzeit, ICU, Zeitzonen; keine Shell, kein Paketmanager). `docker compose exec server sh` gibt es daher nicht mehr; Protokolle wie gewohnt mit `docker compose logs server`.
  - **Der Host:** Kernel (z. B. Schwachstellen im TCP-Stack), Docker Engine und Systembibliotheken des Servers teilen sich alle Container, sie aktualisiert kein Container-Update. Auf Debian/Ubuntu richtet das Installationsskript dafür `unattended-upgrades` ein (abschaltbar mit `--no-host-updates`). Nach Kernel-Updates braucht der Server einen Neustart: entweder von Hand in einem Wartungsfenster (`/var/run/reboot-required` zeigt es an) oder automatisch mit `Unattended-Upgrade::Automatic-Reboot "true";` und `Unattended-Upgrade::Automatic-Reboot-Time "04:30";` in `/etc/apt/apt.conf.d/50unattended-upgrades`. Docker startet die Plattform danach von selbst. Unter Windows hält Docker Desktop seinen Linux-Kernel selbst aktuell: Docker Desktop und Windows-Updates bitte aktuell halten (`wsl --update`).
- Die Updateprüfung lässt sich mit `Ylm__UpdateCheckEnabled=false` abschalten (z. B. ohne Internetzugang).
- Nicht `docker compose up --build` verwenden: Das ersetzt das Release-Image durch einen lokalen Build und blockiert so spätere Updates. Lokale Builds nur in der Entwicklung (Abschnitt 6).

### 1.10 Alte Installation aus einem Git-Checkout

Wer bisher im geklonten Repository installiert hat, kann dort bleiben: `git pull`, dann `docker compose up -d`. Damit kommen die Datensicherung und (bei Bedarf) Caddy dazu. Eigene Änderungen an `docker-compose.yml` vorher in `docker-compose.override.yml` bzw. `.env` verschieben (`git stash` hilft beim Aufräumen). Für einen Wechsel in einen eigenen Ordner wie oben: sichern, per Skript neu installieren, wiederherstellen (1.7, Schritte 1 bis 3; Adresse und Entra bleiben gleich).

## 2. Einrichtung (einmalig)

1. `https://<adresse>/setup` öffnen und das Setup-Token aus dem Log eingeben.
2. Organisation und öffentliche Adresse eintragen.
3. **Bei Microsoft anmelden**: normale Microsoft-Anmeldung im Browser (wie `Connect-MgGraph`, Autorisierungscode mit PKCE) als Globaler Administrator des **Heim-Tenants** (alternativ Anwendungsadministrator plus Privileged Role Administrator). Microsoft leitet danach auf `http://localhost` zurück: Läuft die Plattform auf localhost, geht es automatisch weiter, auf einem Server kopiert man die Adresse aus der Adresszeile in den Assistenten. Die Anmeldung mit Code (Device Code) gibt es nur noch als Ausweichweg, weil sie in vielen Tenants per Conditional Access gesperrt ist. Die Plattform erzeugt dann selbst das App-Zertifikat, legt die mandantenfähige App-Registrierung samt Dienstprinzipal an und erteilt den Admin-Consent. Die Anmeldung läuft über Microsofts App „Microsoft Graph Command Line Tools“, weil die Installation zu diesem Zeitpunkt noch keine eigene App hat; das Token bleibt nur im Arbeitsspeicher und wird nach dem Abschluss verworfen.
4. Im Abschluss den ersten Owner bestätigen (vorausgefüllt mit dem angemeldeten Konto) und wählen, ob der Heim-Tenant gleich als erster Tenant angebunden wird (Standard: ja).
5. Mit dem Konto des ersten Owners anmelden (mit YubiKey).

Ausweichweg, z. B. wenn Conditional Access die Code-Anmeldung sperrt: Im Assistenten „Alternativ: per PowerShell-Skript oder manuell anlegen“ aufklappen, das Skript als Globaler Administrator im Heim-Tenant ausführen (oder die App nach der Anleitung von Hand anlegen) und Client-ID und Tenant-ID eintragen.

Benötigte Anwendungsberechtigungen (Microsoft Graph): `UserAuthenticationMethod.ReadWrite.All`, `User.Read.All`, `GroupMember.Read.All`, `Policy.Read.All`, `Policy.ReadWrite.AuthenticationMethod`, `AuditLog.Read.All`, `Organization.Read.All`.

### Neue Version veröffentlichen (Entwicklung)

Tag setzen und pushen, z. B. `git tag v0.2.0 && git push origin v0.2.0`. Der Workflow `Release` baut das Agent-MSI, legt es ins Server-Image, veröffentlicht `ghcr.io/panpakapancake/yubikey-lifecycle-manager` und das Updater-Image auf GHCR und erstellt das GitHub-Release mit dem MSI. Die Pakete auf GHCR müssen einmalig auf „Public“ gestellt werden, damit Installationen sie ohne Anmeldung laden können.

### Benutzer und Anmeldung

Unter **Partner → Benutzer & Rollen** gibt es drei Anmeldearten:

- **Entra-SSO:** Konten aus dem Heim-Tenant melden sich über Microsoft an, passwortlos mit YubiKey.
- **Lokal mit Passkey:** Das Konto existiert nur auf der Plattform. Nach dem Anlegen zeigt die Oberfläche einen Einmal-Link, der drei Tage gilt. Darüber registriert die Person ihren YubiKey als Passkey. Weitere Schlüssel fügt sie unter „Mein Konto“ hinzu.
- **Notfallkonto:** Anmeldung mit Passwort (mindestens 14 Zeichen) und TOTP-Code aus einer Authenticator-App, ebenfalls per Einmal-Link eingerichtet. Nach fünf Fehlversuchen ist das Konto 15 Minuten gesperrt. Ein neuer Link setzt Passwort und TOTP zurück.

**Notfallcodes:** Jeder Owner kann unter **Mein Konto → Notfallcodes** acht Einmal-Codes erzeugen und ausdrucken. Solange es keine gibt (oder nur noch zwei übrig sind), weist ein Hinweis oben auf jeder Seite darauf hin, auch direkt nach der ersten Anmeldung nach der Installation. Auf der Anmeldeseite meldet „Mit Notfallcode anmelden“ den Owner ohne Microsoft oder Passkey an. Jeder Code gilt einmal; gespeichert ist nur ein SHA-256-Hash. Neue Codes machen alle alten ungültig. Jede Nutzung steht als `auth.recovery_code_used` im Audit-Log und lässt sich unter **Benachrichtigungen** als Webhook oder Mail-Alarm („Anmeldung mit Notfallcode“) melden. Codes eines Kontos, das kein aktiver Owner mehr ist, funktionieren nicht.

Passkeys sind an die Server-Adresse gebunden (`Ylm__PublicBaseUrl` bzw. die Adresse aus der Einrichtung) und funktionieren nur über HTTPS (Ausnahme: `localhost`). Wird die Adresse später geändert, müssen lokale Benutzer ihre Passkeys neu einrichten.

Entra-SSO lässt sich unter **Globale Einstellungen → Anmeldung an der Plattform** abschalten, sobald ein aktiver Owner ein eingerichtetes lokales Konto hat.

## 3. Tenant anbinden

„Tenants“ → „Tenant hinzufügen“ → Anmeldung als Globaler Administrator des Kunden-Tenants → Admin-Consent. Danach prüft die Plattform die Passkey-(FIDO2-)Richtlinie und bietet „Jetzt beheben“ an. Zielgruppe (mehrere Einschluss- und Ausschlussgruppen) und Sync-Intervall stellen Sie auf der Tenant-Seite ein.

### Hierarchie und Vererbung

Einstellungen liegen auf drei Ebenen: **Plattform → Tenant → Gruppe**.

| Ebene | Was dort eingestellt wird |
|---|---|
| Plattform (Menü „Plattform“) | API-Schlüssel, globale Zusatzfelder, Profile für alle Tenants, Benutzer und Rollen |
| Tenant | Zielgruppe, FIDO2-Prüfung, Sync, eigene Profile und lokale Zusatzfelder; geerbte Zusatzfelder verändern (Bezeichnung, Pflicht, Optionen) oder deaktivieren |
| Gruppe | Profil pro Entra-Gruppe; der Registrierungs-Assistent schlägt es für Mitglieder vor (erste passende Zeile gewinnt) |

Die Tenant-Seite zeigt bei jedem Zusatzfeld, ob es global geerbt, im Tenant verändert, deaktiviert oder lokal angelegt ist. „Zurücksetzen“ stellt die Vererbung wieder her.

Beim Offboarding entfernt die Plattform ihre Daten nach der Aufbewahrungsfrist. Die Unternehmensanwendung im Kunden-Tenant muss ein Admin dort selbst löschen (Link in der Oberfläche).

## 4. Agent auf dem Admin-PC (Windows)

1. `YubikeyLifecycleManager-Agent.msi` installieren (Adminrechte). Das MSI entsteht in der CI (Artefakt `agent-msi`) oder lokal mit `pwsh installer/build.ps1`.
   - Dienst `YubikeyLifecycleManagerAgent` (LocalSystem, Autostart). Adminrechte sind nötig, weil Windows den direkten FIDO-Zugriff (PIN setzen, Reset) sonst sperrt.
   - Tray-App `YlmTray.exe` startet bei jeder Anmeldung.
2. In der Web-Oberfläche unter „Agent“ einen Kopplungscode erzeugen.
3. Im Tray-Symbol „Agent koppeln“ wählen, Adresse und Code eingeben. Koppeln und Entkoppeln dürfen nur Mitglieder der lokalen Gruppe „Administratoren“ (ohne erhöhte Rechte reicht), weil der Agent-Dienst als SYSTEM Updates vom gekoppelten Server installiert.

Protokolle: `C:\ProgramData\YubikeyLifecycleManager\Agent\logs`. Update: neueres MSI installieren.

Für Tests ohne Key: `YlmAgent.exe --simulate` startet den Agent mit einem simulierten YubiKey.

## 5. Lese-API

API-Schlüssel legen Owner unter „Plattform“ an. Ein Schlüssel gilt für alle Tenants (auch künftig angebundene) oder nur für ausgewählte; die Auswahl lässt sich später ändern. Aufruf mit Header `X-Api-Key: ylm_…`. `GET /api/v1/tenants` listet die Tenants des Schlüssels; die anderen Endpunkte liefern ohne Parameter alle diese Tenants, mit `?tenant=` (Plattform-ID, Entra-Tenant-ID oder Standard-Domain) nur einen. Die OpenAPI-Beschreibung liegt unter `/openapi/v1.json`, die interaktive Dokumentation unter `/api/docs`.

Einzige Schreibfunktion ist die ToDo-Liste (`GET/POST/DELETE /api/v1/todos`). Dafür braucht der Schlüssel das Häkchen „ToDo-Liste schreiben“. `POST` legt für eine Person (`tenant`, `userPrincipalName`, optional `ticketId`, `ticketUrl`, `note`, `profile`, `dueDate`) ein offenes ToDo an; gibt es schon eines, kommt es mit `200` zurück statt `201`.

## 5a. Benachrichtigungen

Unter „Benachrichtigungen“ (Partnerebene, Recht „API und Integrationen“) legt man pro Empfänger fest, welche Ereignisse gemeldet werden (alle oder nur ausgewählte, alle oder ausgewählte Tenants):

- **Webhooks**: JSON per `POST` bei ausgewählten Ereignissen, signiert mit `X-YLM-Signature: sha256=HMAC-SHA256(Geheimnis, X-YLM-Timestamp + "." + Body)`. Fehlschläge werden bis zu fünfmal wiederholt (1 min bis 6 h). Der Server braucht ausgehenden Zugriff auf die Ziel-URLs.
- **Mail-Alarme**: sofortige Mail an feste Empfänger bei ausgewählten Ereignissen, über den SMTP-Server der Mail-Berichte. Gleiche Warteschlange und Wiederholungen wie bei Webhooks.
- **Mail-Berichte**: SMTP-Server eintragen (Passwort wird verschlüsselt gespeichert), dann Berichte täglich, wöchentlich oder monatlich mit Dashboard-PDF und optional Keys-CSV.
- **NinjaRMM**: keine eigene Anbindung; Ereignisse gehen über einen Webhook an NinjaOne bzw. an ein Zwischensystem, das sie dort verarbeitet.

## 6. Entwicklung

```bash
# Backend (PostgreSQL lokal, Benutzer/Passwort ylm/ylm)
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Ylm.Server
# Web-Oberfläche mit Hot Reload (Proxy auf :5080)
cd web && npm ci && npm run dev
# Tests (legt je Test eine Datenbank ylm_test_* an)
dotnet test
```

Im Development-Modus gibt es auf der Login-Seite eine Entwicklungs-Anmeldung ohne Entra (`AuthMode=Development`).

Den kompletten Stapel aus dem Quellcode bauen (statt die Release-Images zu laden):

```bash
cp .env.example .env
docker compose -f docker-compose.yml -f docker-compose.build.yml up -d --build
# zurück zu den Release-Images
docker compose pull && docker compose up -d
```
