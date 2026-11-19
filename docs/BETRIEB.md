# Betrieb und Installation

## 1. Server installieren

Die Plattform läuft als kleiner Docker-Compose-Stapel aus fertigen Images. Gebaut wird nichts, es braucht nur Docker.

| Dienst | Aufgabe |
|---|---|
| `server` | Plattform (Web-Oberfläche, API, Agent-Verbindung) |
| `db` | PostgreSQL |
| `backup` | tägliche Datensicherung nach `backups/` (nutzt das PostgreSQL-Image, kein zusätzlicher Download) |
| `updater` | Update per Klick aus der Oberfläche |
| `caddy` | nur auf Servern: HTTPS mit automatischem Let's-Encrypt-Zertifikat |

Ressourcen: 1 vCPU, 1 GB RAM und 10 GB Platte reichen für den Anfang. Die Images gibt es für `linux/amd64` und `linux/arm64` (ab dem nächsten Release).

### Welche Variante?

| | Lokal auf dem Admin-PC | Server/VPS mit eigener Domain | Server hinter vorhandenem Reverse Proxy |
|---|---|---|---|
| Adresse | `http://localhost:8080` | `https://ylm.example.com` | `https://ylm.example.com` |
| HTTPS | nicht nötig (`localhost` ist erlaubt) | automatisch (Caddy, Let's Encrypt) | macht der vorhandene Proxy |
| Voraussetzung | Docker Desktop | Linux mit Docker, DNS-Eintrag, Ports 80/443 offen | Linux mit Docker, Proxy-Konfiguration |
| Agents anderer PCs | nein, nur dieser PC | ja | ja |
| Installation | `install.ps1` | `install.sh` (Auswahl 1) | `install.sh` (Auswahl 2) |

Passkeys und Entra funktionieren außerhalb von `localhost` nur über HTTPS. Deshalb gibt es auf einem Server immer HTTPS.

### 1.1 Lokal auf dem Admin-PC (Windows)

Voraussetzung: [Docker Desktop](https://www.docker.com/products/docker-desktop/) ist installiert und gestartet. Dann in PowerShell (keine Adminrechte nötig):

```powershell
irm https://raw.githubusercontent.com/PanPakaPancake/Yubikey-Lifecycle-Manager/main/install.ps1 | iex
```

Das Skript legt `%USERPROFILE%\YubikeyLifecycleManager` an, erzeugt dort die `.env` mit zufälligem Datenbank-Passwort und Updater-Geheimnis, lädt die Images, startet alles und zeigt am Ende das **Setup-Token**. Der Browser öffnet `http://localhost:8080/setup`; weiter mit [Abschnitt 2](#2-einrichtung-einmalig).

Anderer Ordner: `& ([scriptblock]::Create((irm https://raw.githubusercontent.com/PanPakaPancake/Yubikey-Lifecycle-Manager/main/install.ps1))) -Dir D:\ylm`

### 1.2 Server/VPS mit eigener Domain (empfohlen für den Dauerbetrieb)

Voraussetzungen:

- Linux-Server (z. B. Debian 12/13 oder Ubuntu 24.04), Zugang per SSH mit `sudo`
- DNS-Eintrag (A und ggf. AAAA) für die Domain, z. B. `ylm.example.com`, zeigt auf den Server
- Ports **80** und **443** (TCP, für HTTP/3 auch 443/UDP) sind in der Firewall des Anbieters offen
- Docker muss nicht vorinstalliert sein; das Skript bietet die Installation über `get.docker.com` an

```bash
curl -fsSL https://raw.githubusercontent.com/PanPakaPancake/Yubikey-Lifecycle-Manager/main/install.sh | sudo bash
```

Das Skript fragt nach der Variante (1 = eigene Domain) und der Domain, legt `/opt/ylm` mit `.env` an (Rechte `600`), prüft DNS und Ports, startet die Dienste samt Caddy und zeigt das Setup-Token. Caddy holt das Zertifikat beim ersten Aufruf selbst und verlängert es automatisch. Danach `https://ylm.example.com/setup` öffnen und mit [Abschnitt 2](#2-einrichtung-einmalig) weitermachen.

Ohne Rückfragen: `curl -fsSL …/install.sh | sudo bash -s -- --domain ylm.example.com --yes`

Weitere Optionen: `--dir PFAD`, `--version v0.3.0`, `--mode local|https|proxy`, `--url https://…`.

Empfehlungen für einen VPS:

- Nur die Ports 22, 80 und 443 freigeben. Die Plattform selbst lauscht nur auf `127.0.0.1:8080`, die Datenbank gar nicht nach außen.
- SSH nur mit Schlüssel, automatische Sicherheitsupdates des Betriebssystems einschalten (z. B. `unattended-upgrades`).
- Die Sicherungen in `/opt/ylm/backups` regelmäßig auf einen anderen Rechner kopieren (siehe [1.6](#16-datensicherung-und-wiederherstellung)).

### 1.3 Server hinter einem vorhandenen Reverse Proxy

Läuft auf dem Server schon nginx, Traefik o. ä., im Skript Variante 2 wählen (oder `--url https://ylm.example.com`). Caddy bleibt dann aus. Der Proxy leitet HTTPS auf `127.0.0.1:8080` weiter, setzt `X-Forwarded-Proto` und `X-Forwarded-Host` und lässt WebSockets durch (Agent-Verbindung unter `/hubs/`). Beispiel nginx:

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

Läuft der Proxy auf einem anderen Rechner, in `.env` z. B. `YLM_BIND=10.0.0.5:8080` setzen (interne Adresse).

> Der Server übernimmt Forwarded-Header (`X-Forwarded-For/-Proto/-Host`) nur von Loopback und privaten Netzen (10/8, 172.16/12, 192.168/16, fc00::/7). Das passt für Caddy, einen Proxy auf demselben Server und einen Proxy im internen Netz. Steht der Proxy unter einer öffentlichen Adresse, diese in `docker-compose.override.yml` beim Dienst `server` eintragen, z. B. `Ylm__TrustedProxies__0: "203.0.113.10"` (`"*"` vertraut allen, nicht empfohlen). Port 8080 sollte trotzdem nie direkt aus dem Internet erreichbar sein.

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
        Ylm__RequirePhishingResistantSignIn: "false"
  ```

- **Eigenes Zertifikat** statt Let's Encrypt (z. B. internes Netz ohne öffentliches DNS): eigene Caddyfile mit `tls /certs/cert.pem /certs/key.pem` anlegen und per Override einbinden (`./my.Caddyfile:/etc/caddy/Caddyfile:ro` und `./certs:/certs:ro` unter `caddy.volumes`).

Den Ordner der Installation nicht umbenennen, wenn `.env` kein `COMPOSE_PROJECT_NAME` enthält: Docker leitet die Namen der Volumes sonst vom Ordnernamen ab und startet mit leeren Daten.

### 1.6 Datensicherung und Wiederherstellung

Der Dienst `backup` sichert täglich um 02:30 (`BACKUP_TIME`) die Datenbank und das Volume `data` in **ein** Archiv `backups/ylm-JJJJMMTT-HHMMSS.tar.gz` und löscht Sicherungen, die älter als 14 Tage sind (`BACKUP_KEEP_DAYS`). Das Volume `data` enthält App-Zertifikat, Data-Protection-Schlüssel und Geheimnisse. Ohne diese Schlüssel sind Zertifikat, feste Profil-PINs, SMTP-Passwort und Anmelde-Cookies nicht mehr lesbar. Deshalb gehören beide Teile immer zusammen, und das Archiv ist so schützenswert wie der Server.

```bash
docker compose exec backup sh /backup.sh now        # sofort sichern
docker compose exec backup sh /backup.sh list       # vorhandene Sicherungen
```

Wiederherstellen (gleiche oder neuere Version der Plattform):

```bash
docker compose stop server
docker compose exec backup sh /backup.sh restore ylm-20261119-023000.tar.gz
docker compose start server
```

Eine Sicherung von einem anderen Rechner vorher nach `backups/` kopieren. Kopie außer Haus, z. B. per Cron auf einem anderen Rechner: `rsync -a root@vps:/opt/ylm/backups/ /srv/ylm-backups/`.

### 1.7 Umzug, z. B. vom Admin-PC auf einen VPS

1. Alte Installation: `docker compose exec backup sh /backup.sh now`, Archiv aus `backups\` kopieren.
2. Neue Installation per Skript anlegen (1.2) und das Archiv nach `/opt/ylm/backups/` kopieren.
3. Wiederherstellen wie in 1.6. Die Plattform läuft danach mit allen Daten unter der neuen Adresse aus `YLM_PUBLIC_URL`.
4. **Entra:** In der App-Registrierung der Plattform (Heim-Tenant → App-Registrierungen → „Yubikey Lifecycle Manager <Organisation>“ → Authentifizierung) die neuen Umleitungs-URIs `https://<neu>/signin-oidc` und `https://<neu>/api/tenants/consent-callback` ergänzen.
5. **Lokale Benutzer** mit Passkey brauchen einen neuen Einrichtungslink (Passkeys gehören zur Adresse), **Agents** einen neuen Kopplungscode.
6. Alte Installation stoppen: `docker compose down` (Volumes bleiben erhalten, bis sie mit `down -v` gelöscht werden).

### 1.8 Einstellungen (Umgebungsvariablen)

In `.env`:

| Variable | Standard | Bedeutung |
|---|---|---|
| `POSTGRES_PASSWORD` | – | Datenbank-Passwort (Pflicht, nach der Installation nicht mehr ändern) |
| `YLM_PUBLIC_URL` | aus der Einrichtung | öffentliche Adresse für Browser, Entra und Agents |
| `COMPOSE_PROFILES` | leer | `https` schaltet Caddy ein |
| `YLM_DOMAIN` | – | Domain für Caddy |
| `YLM_BIND` | `127.0.0.1:8080` | Adresse, auf der der Server-Port lauscht |
| `UPDATER_TOKEN` | leer | Geheimnis für das Update per Klick; leer = aus |
| `YLM_VERSION` | `latest` | Version des Server-Images festhalten |
| `BACKUP_TIME`, `BACKUP_KEEP_DAYS`, `BACKUP_DIR` | `02:30`, `14`, `./backups` | Datensicherung (`BACKUP_TIME=off` schaltet sie ab) |
| `TZ` | `Europe/Berlin` | Zeitzone |

Weitere Einstellungen des Servers über `docker-compose.override.yml` (`services.server.environment`):

| Variable | Standard | Bedeutung |
|---|---|---|
| `Ylm__DefaultSyncIntervalMinutes` | 60 | Sync-Intervall für neue Tenants |
| `Ylm__OffboardingRetentionDays` | 30 | Aufbewahrung nach dem Offboarding |
| `Ylm__RequirePhishingResistantSignIn` | true | Anmeldung nur mit Passkey/YubiKey (Claim `amr` = `fido`) |
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
- Die Updateprüfung lässt sich mit `Ylm__UpdateCheckEnabled=false` abschalten (z. B. ohne Internetzugang).
- Nicht `docker compose up --build` verwenden: Das ersetzt das Release-Image durch einen lokalen Build und blockiert so spätere Updates. Lokale Builds nur in der Entwicklung (Abschnitt 6).

### 1.10 Alte Installation aus einem Git-Checkout

Wer bisher im geklonten Repository installiert hat, kann dort bleiben: `git pull`, dann `docker compose up -d`. Damit kommen die Datensicherung und (bei Bedarf) Caddy dazu. Eigene Änderungen an `docker-compose.yml` vorher in `docker-compose.override.yml` bzw. `.env` verschieben (`git stash` hilft beim Aufräumen). Für einen Wechsel in einen eigenen Ordner wie oben: sichern, per Skript neu installieren, wiederherstellen (1.7, Schritte 1 bis 3; Adresse und Entra bleiben gleich).

## 2. Einrichtung (einmalig)

1. `https://<adresse>/setup` öffnen und das Setup-Token aus dem Log eingeben.
2. Organisation und öffentliche Adresse eintragen.
3. **Bei Microsoft anmelden**: Der Assistent zeigt einen Code für `microsoft.com/devicelogin`. Dort als Globaler Administrator (oder Privileged Role Administrator) des **Heim-Tenants** anmelden. Die Plattform erzeugt dann selbst das App-Zertifikat, legt die mandantenfähige App-Registrierung samt Dienstprinzipal an und erteilt den Admin-Consent. Die Anmeldung läuft über Microsofts App „Microsoft Graph Command Line Tools“, weil die Installation zu diesem Zeitpunkt noch keine eigene App hat; das Token bleibt nur im Arbeitsspeicher und wird nach dem Abschluss verworfen.
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
