# Betrieb und Installation

## 1. Server (Docker)

Voraussetzungen: Linux-Host mit Docker und Docker Compose, ein DNS-Name und ein Reverse Proxy mit TLS (z. B. Caddy, Traefik oder nginx).

```bash
git clone https://github.com/PanPakaPancake/Yubikey-Lifecycle-Manager.git
cd Yubikey-Lifecycle-Manager
cp .env.example .env        # Passwort, öffentliche Adresse und UPDATER_TOKEN eintragen
docker compose up -d        # fertige Images aus den Releases; mit --build lokal aus dem Quellcode
docker compose logs server | grep Setup-Token
```

Die Plattform lauscht nur auf `127.0.0.1:8080`. Der Reverse Proxy leitet HTTPS dorthin weiter und setzt `X-Forwarded-Proto` und `X-Forwarded-Host`. Beispiel für Caddy:

```
ylm.example.com {
    reverse_proxy 127.0.0.1:8080
}
```

> Der Server vertraut den Forwarded-Headern jedes vorgeschalteten Proxys. Port 8080 darf deshalb nicht direkt aus dem Netz erreichbar sein.

Beim ersten Start einer leeren Datenbank meldet Entity Framework einmal einen Fehler beim Lesen der Migrationstabelle. Das ist normal; danach werden die Tabellen angelegt.

### Datensicherung

| Was | Wo | Hinweis |
|---|---|---|
| Datenbank | Volume `db` | z. B. täglich `docker compose exec db pg_dump -U ylm ylm` |
| App-Zertifikat und Data-Protection-Schlüssel | Volume `data` (`/data`) | **Unbedingt sichern.** Ohne diese Schlüssel sind Zertifikat, feste Profil-PINs und Anmelde-Cookies nicht mehr lesbar. |

### Einstellungen (Umgebungsvariablen)

| Variable | Standard | Bedeutung |
|---|---|---|
| `Ylm__PublicBaseUrl` | aus der Einrichtung | Öffentliche HTTPS-Adresse |
| `Ylm__DefaultSyncIntervalMinutes` | 60 | Sync-Intervall für neue Tenants |
| `Ylm__OffboardingRetentionDays` | 30 | Aufbewahrung nach dem Offboarding |
| `Ylm__RequirePhishingResistantSignIn` | true | Anmeldung nur mit Passkey/YubiKey (Claim `amr` = `fido`) |
| `Ylm__UpdateCheckEnabled` | true | Auf GitHub nach neuen Versionen suchen |
| `YLM_VERSION` (in `.env`) | latest | Version des Server-Images festhalten |
| `UPDATER_TOKEN` (in `.env`) | leer | Geheimnis für das Update per Klick; leer = aus |

## 2. Einrichtung (einmalig)

1. `https://<adresse>/setup` öffnen und das Setup-Token aus dem Log eingeben.
2. Organisation und öffentliche Adresse eintragen.
3. **Bei Microsoft anmelden**: Der Assistent zeigt einen Code für `microsoft.com/devicelogin`. Dort als Globaler Administrator (oder Privileged Role Administrator) des **Heim-Tenants** anmelden. Die Plattform erzeugt dann selbst das App-Zertifikat, legt die mandantenfähige App-Registrierung samt Dienstprinzipal an und erteilt den Admin-Consent. Die Anmeldung läuft über Microsofts App „Microsoft Graph Command Line Tools“, weil die Installation zu diesem Zeitpunkt noch keine eigene App hat; das Token bleibt nur im Arbeitsspeicher und wird nach dem Abschluss verworfen.
4. Im Abschluss den ersten Owner bestätigen (vorausgefüllt mit dem angemeldeten Konto) und wählen, ob der Heim-Tenant gleich als erster Tenant angebunden wird (Standard: ja).
5. Mit dem Konto des ersten Owners anmelden (mit YubiKey).

Ausweichweg, z. B. wenn Conditional Access die Code-Anmeldung sperrt: Im Assistenten „Alternativ: per PowerShell-Skript oder manuell anlegen“ aufklappen, das Skript als Globaler Administrator im Heim-Tenant ausführen (oder die App nach der Anleitung von Hand anlegen) und Client-ID und Tenant-ID eintragen.

Benötigte Anwendungsberechtigungen (Microsoft Graph): `UserAuthenticationMethod.ReadWrite.All`, `User.Read.All`, `GroupMember.Read.All`, `Policy.Read.All`, `Policy.ReadWrite.AuthenticationMethod`, `AuditLog.Read.All`, `Organization.Read.All`.

### Updates

**Globale Einstellungen → Version und Updates** zeigt die installierte und die neueste Version (GitHub-Releases, Prüfung alle 6 Stunden). Ein Punkt neben „Globale Einstellungen“ weist Owner auf ein Update hin.

- **Update per Klick:** Der Container `updater` holt das neue Server-Image und startet den Server neu (`docker compose pull server` und `up -d server`). Nur dieser Container hat Zugriff auf den Docker-Socket, der Server selbst nicht. Dafür in `.env` ein langes Geheimnis setzen, z. B. `UPDATER_TOKEN=$(openssl rand -hex 32)`, danach `docker compose up -d`. Ohne Token nimmt der Updater keine Aufträge an.
- **Von Hand:** `docker compose pull && docker compose up -d`.
- **Agents:** Jedes Server-Image bringt das passende Agent-MSI mit. „Mein Agent“ bietet es zum Download an, und verbundene Agents installieren neue Versionen innerhalb von 4 Stunden selbst (still per msiexec, Prüfsumme wird geprüft). Abschalten pro PC mit `"Agent": { "AutoUpdate": false }` in der `appsettings.json` des Agents.
- Die Updateprüfung lässt sich mit `Ylm__UpdateCheckEnabled=false` abschalten (z. B. ohne Internetzugang).

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
3. Im Tray-Symbol „Agent koppeln“ wählen, Adresse und Code eingeben.

Protokolle: `C:\ProgramData\YubikeyLifecycleManager\Agent\logs`. Update: neueres MSI installieren.

Für Tests ohne Key: `YlmAgent.exe --simulate` startet den Agent mit einem simulierten YubiKey.

## 5. Lese-API

API-Schlüssel legen Owner unter „Plattform“ an. Ein Schlüssel gilt für alle Tenants (auch künftig angebundene) oder nur für ausgewählte; die Auswahl lässt sich später ändern. Aufruf mit Header `X-Api-Key: ylm_…`. `GET /api/v1/tenants` listet die Tenants des Schlüssels; die anderen Endpunkte liefern ohne Parameter alle diese Tenants, mit `?tenant=` (Plattform-ID, Entra-Tenant-ID oder Standard-Domain) nur einen. Die OpenAPI-Beschreibung liegt unter `/openapi/v1.json`, die interaktive Dokumentation unter `/api/docs`.

Einzige Schreibfunktion ist die ToDo-Liste (`GET/POST/DELETE /api/v1/todos`). Dafür braucht der Schlüssel das Häkchen „ToDo-Liste schreiben“. `POST` legt für eine Person (`tenant`, `userPrincipalName`, optional `ticketId`, `ticketUrl`, `note`, `profile`, `dueDate`) ein offenes ToDo an; gibt es schon eines, kommt es mit `200` zurück statt `201`.

## 5a. Integrationen

Unter „Integrationen“ (Partnerebene, Recht „API und Integrationen“):

- **Webhooks**: JSON per `POST` bei ausgewählten Ereignissen, signiert mit `X-YLM-Signature: sha256=HMAC-SHA256(Geheimnis, X-YLM-Timestamp + "." + Body)`. Fehlschläge werden bis zu fünfmal wiederholt (1 min bis 6 h). Der Server braucht ausgehenden Zugriff auf die Ziel-URLs.
- **Mail-Berichte**: SMTP-Server eintragen (Passwort wird verschlüsselt gespeichert), dann Berichte täglich, wöchentlich oder monatlich mit Dashboard-PDF und optional Keys-CSV.
- **NinjaOne**: API-App mit Client Credentials (Scopes Monitoring, Management), Organisations-Custom-Fields anlegen, Tenants Organisationen zuordnen. Die Plattform schreibt die Kennzahlen im eingestellten Intervall.

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
