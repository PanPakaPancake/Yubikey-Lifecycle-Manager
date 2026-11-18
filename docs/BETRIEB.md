# Betrieb und Installation

## 1. Server (Docker)

Voraussetzungen: Linux-Host mit Docker und Docker Compose, ein DNS-Name und ein Reverse Proxy mit TLS (z. B. Caddy, Traefik oder nginx).

```bash
git clone https://github.com/PanPakaPancake/Yubikey-Lifecycle-Manager.git
cd Yubikey-Lifecycle-Manager
cp .env.example .env        # Passwort und öffentliche Adresse eintragen
docker compose up -d --build
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

## 2. Einrichtung (einmalig)

1. `https://<adresse>/setup` öffnen und das Setup-Token aus dem Log eingeben.
2. Organisation und öffentliche Adresse eintragen.
3. **Bei Microsoft anmelden**: Der Assistent zeigt einen Code für `microsoft.com/devicelogin`. Dort als Globaler Administrator (oder Privileged Role Administrator) des **Heim-Tenants** anmelden. Die Plattform erzeugt dann selbst das App-Zertifikat, legt die mandantenfähige App-Registrierung samt Dienstprinzipal an und erteilt den Admin-Consent. Die Anmeldung läuft über Microsofts App „Microsoft Graph Command Line Tools“, weil die Installation zu diesem Zeitpunkt noch keine eigene App hat; das Token bleibt nur im Arbeitsspeicher und wird nach dem Abschluss verworfen.
4. Im Abschluss den ersten Owner bestätigen (vorausgefüllt mit dem angemeldeten Konto) und wählen, ob der Heim-Tenant gleich als erster Tenant angebunden wird (Standard: ja).
5. Mit dem Konto des ersten Owners anmelden (mit YubiKey).

Ausweichweg, z. B. wenn Conditional Access die Code-Anmeldung sperrt: Im Assistenten „Alternativ: per PowerShell-Skript oder manuell anlegen“ aufklappen, das Skript als Globaler Administrator im Heim-Tenant ausführen (oder die App nach der Anleitung von Hand anlegen) und Client-ID und Tenant-ID eintragen.

Benötigte Anwendungsberechtigungen (Microsoft Graph): `UserAuthenticationMethod.ReadWrite.All`, `User.Read.All`, `GroupMember.Read.All`, `Policy.Read.All`, `Policy.ReadWrite.AuthenticationMethod`, `AuditLog.Read.All`, `Organization.Read.All`.

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
