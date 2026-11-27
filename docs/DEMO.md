# Demo with sample data

[README](../README.md) · [Deutsch: siehe unten](#deutsch)

The demo shows the whole web interface with made-up tenants, people and YubiKeys. You need Docker, nothing else: no Entra tenant, no YubiKey, no Windows.

## Start

```bash
git clone https://github.com/PanPakaPancake/Yubikey-Lifecycle-Manager.git
cd Yubikey-Lifecycle-Manager
docker compose -f docker-compose.demo.yml up -d
```

Open **http://localhost:8099** and pick a role:

| Role | What it can do |
|---|---|
| Owner | Everything, including settings, roles and tenants |
| Operator | Register, lock and manage keys |
| Helpdesk | Look up people and keys, lock lost keys |
| Auditor | Read everything, change nothing |

Log out (bottom left) to try another role.

| Command | Effect |
|---|---|
| `docker compose -f docker-compose.demo.yml down` | Stop the demo, keep its data |
| `docker compose -f docker-compose.demo.yml down -v` | Stop and wipe; the next start creates fresh sample data |
| `docker compose -f docker-compose.demo.yml pull` | Get the latest version |

## What is in it

- Two customer tenants, **Contoso Demo GmbH** (40 people) and **Fabrikam Demo AG** (22 people), plus a partner stock for MSPs.
- Keys with serial number, firmware and model, some on older firmware that is affected by Yubico security advisories, a few keys that users registered themselves (no serial number yet), a lost key, a retired key, a shared key and a breakglass key.
- Authentication methods for the passkey rollout overview (Authenticator, SMS, Windows Hello, passkeys).
- A failed FIDO2 policy check at Contoso, which you can fix with "Jetzt beheben".
- Open to-dos as if they came from a ticket system, and an audit log.

The tenants live in the server's memory instead of Microsoft Graph. Changes in the demo tenants (removed methods, Temporary Access Passes, policy fixes) last until the server restarts; data in the platform itself (inventory, profiles, settings) stays until you wipe the demo.

## What does not work

- **Sign-in with Microsoft and the setup assistant**: the demo has no Entra app.
- **Registering keys**: needs the Windows agent and a real YubiKey. You can still look at the wizard and the profiles.
- **Mail**: reports and alerts are only written to the log (`docker compose -f docker-compose.demo.yml logs server`).
- **Updates by click**: the demo has no updater; use `pull` instead.

## Safety

The demo is meant for trying things out on your own computer. It listens on `127.0.0.1:8099` only, uses a fixed database password and lets anyone pick a role without a password. Do not expose it to a network and do not enter real data.

The demo runs as a separate Docker project (`ylm-demo`) with its own database and volumes, so it does not touch a real installation on the same machine. The server also checks this itself: in demo mode it refuses to start on a database that belongs to a real installation, and a real installation refuses to start with the demo's data folder.

Technically, demo mode is the server setting `Ylm__DemoMode=true`. Run from source:

```bash
docker build -t ylm-demo .
YLM_DEMO_IMAGE=ylm-demo YLM_DEMO_PULL=never docker compose -f docker-compose.demo.yml up -d
```

---

## Deutsch

Die Demo zeigt die komplette Oberfläche mit erfundenen Tenants, Personen und YubiKeys. Nötig ist nur Docker, kein Entra-Tenant, kein YubiKey, kein Windows.

Starten mit den Befehlen oben, dann **http://localhost:8099** öffnen und eine Rolle wählen (Owner, Operator, Helpdesk, Auditor). `down` hält die Demo an, `down -v` löscht ihre Daten, beim nächsten Start entstehen frische Beispieldaten.

Enthalten sind zwei Kunden-Tenants (Contoso Demo GmbH, Fabrikam Demo AG), ein Partnerlager, Keys mit älterer Firmware und Sicherheitshinweisen, selbst registrierte Keys zum Nacherfassen, ein verlorener, ein ausgemusterter, ein geteilter und ein Breakglass-Key, Anmeldemethoden für die Rollout-Übersicht, eine fehlgeschlagene Prüfung der FIDO2-Richtlinie bei Contoso („Jetzt beheben“), offene ToDos und ein Audit-Log.

Nicht möglich sind die Anmeldung mit Microsoft, der Setup-Assistent, das Registrieren von Keys (braucht Agent und echten YubiKey) und Mails (stehen nur im Log).

Die Demo ist zum Ausprobieren auf dem eigenen Rechner gedacht: nur über `127.0.0.1:8099` erreichbar, festes Datenbank-Passwort, Anmeldung ohne Passwort. Nicht im Netz freigeben und keine echten Daten eingeben. Sie läuft als eigenes Docker-Projekt (`ylm-demo`) mit eigener Datenbank. Der Server startet im Demo-Modus nicht auf der Datenbank einer echten Installation und eine echte Installation nicht mit dem Datenordner der Demo.
