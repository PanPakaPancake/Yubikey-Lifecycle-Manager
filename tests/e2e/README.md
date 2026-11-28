# Ende-zu-Ende-Tests

Diese Tests bedienen die Plattform so, wie es ein Admin tut: im Browser und mit dem Agent auf Windows.
Sie laufen in der CI bei jedem Pull Request (Workflow `CI`, Jobs **E2E: Browser-Tests** und **E2E: Windows-Agent**).

## Was getestet wird

| Teil | Wo | Inhalt |
|---|---|---|
| Browser gegen Testserver | `tests/e2e/specs/0*.spec.ts` | Anmeldung (Entwicklung und lokales Konto mit Passkey über einen virtuellen FIDO2-Authenticator), Agent koppeln, Key für eine Person registrieren, PIN-Brief (PDF) herunterladen, Inventar, Rollout-Übersicht, alle Hauptseiten ohne JavaScript- oder CSP-Fehler |
| Browser gegen Docker-Stack | `tests/e2e/specs/docker.smoke.spec.ts` | Das in diesem Lauf gebaute Image: Gesundheitsprüfung, Sicherheits-Header, Setup-Assistent bei frischer Installation, Hauptseiten |
| Windows-Agent | `tests/agent-smoke/Test-AgentSmoke.ps1` | MSI still installieren, Dienst läuft, Tray-App startet und stürzt nicht ab, Koppeln des Dienstes über HTTPS (wie aus der Tray-App), Agent meldet sich am Server, Deinstallation |

Jeder Browser-Test schlägt fehl, sobald im Browser ein unbehandelter Fehler auftritt oder die Content-Security-Policy
etwas blockiert.

## Der Testserver

`tests/Ylm.E2E.Host` startet den echten Server (dieselbe `Program.cs`, PostgreSQL, Web-Oberfläche, SignalR) und ersetzt
nur Microsoft Entra bzw. Graph durch eine Simulation mit Beispieldaten (Tenant „Contoso GmbH“ mit Anna, Ben, Carla und Dana).
Statt eines YubiKeys gibt es einen simulierten Agent mit Software-Key. Die Anmeldung erfolgt über die Entwicklungs-Anmeldung.
**Nur für Tests, niemals produktiv betreiben.**

Steuer-Endpunkte für die Tests (nur in diesem Host):

- `GET /e2e/info`: Tenant, Zustand des simulierten Agents und Keys
- `POST /e2e/agent/pair` `{ "code": "ABCD-EFGH" }`: simulierten Agent mit einem Kopplungscode koppeln
- `POST /e2e/agent/key` `{ "action": "replace", "serialNumber": 123, "firmware": "5.7.1" }`: anderen Key einstecken (auch `insert`, `remove`)

Umgebungsvariablen: `YLM_E2E_PORT` (Standard 5180), `YLM_E2E_HTTPS_PORT` (optional, mit selbst erzeugtem Zertifikat),
`YLM_E2E_CERT_OUT` (Zertifikat als .cer ablegen), `YLM_TEST_POSTGRES` (PostgreSQL ohne Datenbankname; die Datenbank wird
je Start neu angelegt und beim Beenden gelöscht).

## Lokal ausführen

Voraussetzungen: .NET 10 SDK, Node.js 22, ein PostgreSQL (z. B. `docker run -d -p 5432:5432 -e POSTGRES_USER=ylm -e POSTGRES_PASSWORD=ylm postgres:16`).

```bash
(cd web && npm ci && npm run build)          # Oberfläche nach src/Ylm.Server/wwwroot
dotnet build tests/Ylm.E2E.Host -c Release
cd tests/e2e
npm ci
npx playwright install chromium
npx playwright test --project=host            # startet den Testserver selbst
npx playwright show-report                    # Bericht mit Screenshots, Videos und Traces
```

Gegen einen laufenden Docker-Stack (mit `tests/e2e/docker-compose.e2e.yml`, siehe dort):

```bash
E2E_DOCKER_URL=http://localhost:8080 npx playwright test --project=docker
```

Den Windows-Rauchtest bitte nur auf einem Test-PC oder einer VM ausführen (installiert und entfernt den Agent):

```powershell
dotnet build tests/Ylm.E2E.Host -c Release
./tests/agent-smoke/Test-AgentSmoke.ps1 -Msi C:\Pfad\YubikeyLifecycleManager-Agent.msi -Postgres 'Host=localhost;Username=ylm;Password=ylm'
```
