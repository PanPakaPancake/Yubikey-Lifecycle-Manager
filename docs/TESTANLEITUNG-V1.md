# Testanleitung V1: Praxistest mit echtem Tenant und echtem YubiKey

Diese Anleitung führt Schritt für Schritt durch den ersten echten Test von V1. Bisher ist V1 nur gegen einen simulierten YubiKey und ein simuliertes Microsoft Graph getestet. Jetzt prüfen wir:

1. Server per Docker Compose starten
2. Einrichtung inkl. PowerShell-Skript für die App-Registrierung
3. Test-Tenant per Assistent anbinden, inkl. „Jetzt beheben“
4. Agent per MSI auf dem Windows-Admin-PC installieren, Dienst und Tray-App prüfen
5. Echter YubiKey: Einzel-Wizard, PIN, erzwungener PIN-Wechsel, Seriennummer/Firmware, PIN-Brief

Jeder Schritt hat eine Liste **Erwartet**. Weicht etwas ab, bitte am Abschnitt [Fehler zurückmelden](#fehler-zurückmelden) orientieren und die Schritt-Nummer (z. B. „3.4“) mitschicken. Am Ende steht ein [Protokollbogen](#protokollbogen) zum Abhaken.

> Rechne für den ersten Durchlauf mit 2 bis 3 Stunden. Du kannst jederzeit nach einem Abschnitt pausieren; alle Daten bleiben in den Docker-Volumes.

---

## 0. Vorbereitung

### 0.1 Was du brauchst

| Was | Hinweis |
|---|---|
| **Test-Tenant** (Entra ID) | Kein Produktiv-Tenant. Ein M365-Developer- oder Trial-Tenant reicht. Der Tenant ist im Test gleichzeitig **Heim-Tenant** (App-Registrierung) und **Kunden-Tenant** (wird angebunden). |
| Konto **Globaler Administrator** im Test-Tenant | Für Skript, Admin-Consent und „Jetzt beheben“. Dieses Konto wird auch der erste Owner der Plattform. |
| **Testperson** im Test-Tenant, z. B. `ylm-test1@<tenant>.onmicrosoft.com` | Normales Konto, aktiviert, **ohne** bisherigen Passkey. Für diese Person wird der YubiKey registriert. |
| **Windows-Admin-PC** (Windows 10/11, x64) mit lokalen Adminrechten | Hier laufen Agent und Tray-App. |
| **Docker Desktop** auf dem Admin-PC (Variante A) oder ein Linux-Server mit Docker (Variante B) | siehe 1. |
| **PowerShell 7** (`pwsh`) und das Modul **Microsoft.Graph** | `Install-Module Microsoft.Graph -Scope CurrentUser` |
| **Git** | zum Klonen des Repos |
| **Mindestens ein YubiKey 5** (ideal: einer mit Firmware **≥ 5.7** und einer mit **< 5.7**) | Achtung: Der Test kann den Key **zurücksetzen**. Keinen Key verwenden, auf dem wichtige Passkeys liegen. |
| Optional: **Yubico Authenticator** oder `ykman` | Zum Gegenprüfen von Seriennummer und Firmware. Während des Wizards bitte **schließen**, damit er den Key nicht blockiert. |

### 0.2 Testgruppe anlegen

Im Entra Admin Center → Gruppen → Neue Gruppe:

- Typ Sicherheit, Name z. B. **`YLM-Test-Keyuser`**
- Mitglied: die Testperson

Die Gruppe brauchen wir in Schritt 3 als Zielgruppe.

### 0.3 Version notieren

Getestet wird der Stand von `main` (V1, Version 0.1.0, Commit `e877d22` oder neuer). Bitte den Commit notieren:

```powershell
git -C Yubikey-Lifecycle-Manager log -1 --oneline
```

---

## 1. Server per Docker Compose starten

### Welche Variante?

| | Variante A: alles auf dem Admin-PC (**empfohlen für den ersten Test**) | Variante B: Linux-Server mit Reverse Proxy |
|---|---|---|
| Adresse | `http://localhost:8080` | `https://ylm.<deine-domain>` |
| Aufwand | gering, kein DNS, kein Zertifikat | DNS-Eintrag, Caddy/Traefik/nginx |
| Entspricht Produktion | nein | ja |

Die Plattform erlaubt `http://` nur für `localhost`. Entra akzeptiert `http://localhost` als Umleitungs-URI, der Agent ebenfalls. Für andere Adressen ist HTTPS Pflicht.

### 1.1 Repo holen und `.env` anlegen

**Variante A (PowerShell auf dem Admin-PC):**

```powershell
git clone https://github.com/PanPakaPancake/Yubikey-Lifecycle-Manager.git
cd Yubikey-Lifecycle-Manager
Copy-Item .env.example .env
notepad .env
```

**Variante B (Linux):**

```bash
git clone https://github.com/PanPakaPancake/Yubikey-Lifecycle-Manager.git
cd Yubikey-Lifecycle-Manager
cp .env.example .env
nano .env
```

Inhalt von `.env`:

```ini
POSTGRES_PASSWORD=<langes zufälliges Passwort>
# Variante A:
YLM_PUBLIC_URL=http://localhost:8080
# Variante B:
# YLM_PUBLIC_URL=https://ylm.example.com
```

### 1.2 Test-Einstellung: Anmeldung vorerst ohne YubiKey-Pflicht

Standardmäßig lässt die Plattform nur Anmeldungen mit Passkey/YubiKey zu (Claim `amr` = `fido`). Damit wir in Schritt 3 „Jetzt beheben“ an einer **ausgeschalteten** FIDO2-Richtlinie testen können, schalten wir diese Pflicht für den ersten Durchlauf ab. Lege dazu im Repo-Ordner eine Datei **`docker-compose.override.yml`** an (Docker Compose liest sie automatisch mit, bitte nicht committen):

```yaml
services:
  server:
    environment:
      Ylm__RequirePhishingResistantSignIn: "false"
```

Die Pflicht testen wir am Ende in Schritt 6 wieder mit eingeschalteter Einstellung.

> Variante B: Der Reverse Proxy muss `X-Forwarded-Proto` und `X-Forwarded-Host` setzen, Port 8080 darf nicht aus dem Netz erreichbar sein. Beispiel für Caddy steht in [BETRIEB.md](BETRIEB.md#1-server-docker).

### 1.3 Bauen und starten

```powershell
docker compose up -d --build
docker compose ps
```

Der erste Build dauert einige Minuten (Node- und .NET-Images werden geladen).

**Erwartet:**

- `docker compose ps` zeigt `db` als `healthy` und `server` als `running`, nach ca. 30 Sekunden ebenfalls `healthy`.
- Im Log ist **einmal** ein Fehler zur Migrationstabelle zu sehen (`__EFMigrationsHistory`). Das ist bei leerer Datenbank normal.

### 1.4 Erreichbarkeit und Setup-Token

```powershell
# Health-Check
curl.exe http://localhost:8080/healthz
# Setup-Token aus dem Log (PowerShell)
docker compose logs server | Select-String Setup-Token
```

Linux: `curl http://localhost:8080/healthz` und `docker compose logs server | grep Setup-Token`.

**Erwartet:**

- `/healthz` antwortet mit `{"status":"ok"}`.
- Im Log steht `Einrichtung noch nicht abgeschlossen. Setup-Token: …`. Token kopieren.

---

## 2. Einrichtung und App-Registrierung (PowerShell-Skript)

### 2.1 Setup-Assistent öffnen

Browser (Edge oder Chrome): `http://localhost:8080/setup` (Variante B: `https://ylm.example.com/setup`).

1. **Setup-Token** einfügen → Weiter.
2. **Grunddaten:** Organisation (z. B. „Test“), öffentliche Adresse (`http://localhost:8080` bzw. deine HTTPS-Adresse), erster Owner = UPN deines Global-Admin-Kontos → Speichern und weiter.
3. **Zertifikat erzeugen** → Weiter.

**Erwartet:** Nach „Zertifikat erzeugen“ erscheint der grüne Hinweis mit Fingerabdruck (Thumbprint) und „gültig bis“.

### 2.2 Skript herunterladen und ausführen

Im Schritt „App-Registrierung im Heim-Tenant“ auf **Skript herunterladen** klicken. Die Datei heißt `ylm-app-registration.ps1`.

```powershell
cd $HOME\Downloads
Unblock-File .\ylm-app-registration.ps1
Set-ExecutionPolicy -Scope Process Bypass
pwsh .\ylm-app-registration.ps1
```

Im Anmeldefenster mit dem **Global Admin des Test-Tenants** anmelden und die Berechtigung `Application.ReadWrite.All` für Microsoft Graph PowerShell bestätigen.

**Erwartet:**

- Keine roten Fehlermeldungen.
- Am Ende stehen zwei Zeilen:
  ```
  Client-ID (Anwendungs-ID): xxxxxxxx-…
  Heim-Tenant-ID:           yyyyyyyy-…
  ```
- Im Entra Admin Center unter App-Registrierungen gibt es eine App **„Yubikey Lifecycle Manager Test“** mit:
  - Kontotyp „Konten in einem beliebigen Organisationsverzeichnis“
  - Umleitungs-URIs (Web): `<adresse>/signin-oidc` und `<adresse>/api/tenants/consent-callback`
  - einem Zertifikat unter „Zertifikate & Geheimnisse“
  - 7 Anwendungsberechtigungen und 4 delegierten Berechtigungen für Microsoft Graph

### 2.3 Admin-Consent im Heim-Tenant erteilen

> **Bekannte Lücke:** Der Text im Assistenten sagt, das Skript erteile den Admin-Consent. Das tut es derzeit **nicht**; es legt nur App und Dienstprinzipal an. Bitte den Consent von Hand erteilen. Das Skript wird in einem eigenen PR nachgezogen.

Entra Admin Center → App-Registrierungen → „Yubikey Lifecycle Manager Test“ → **API-Berechtigungen** → **„Administratorzustimmung für <Tenant> erteilen“** → Ja.

**Erwartet:** Alle Berechtigungen zeigen den grünen Haken „Gewährt für <Tenant>“.

### 2.4 Werte eintragen und testen

Zurück im Setup-Assistenten: Client-ID und Heim-Tenant-ID eintragen → **Speichern und testen**.

**Erwartet:**

- Grüner Hinweis „Anmeldung mit dem App-Zertifikat erfolgreich.“
- **Keine** Zeile „Fehlende Berechtigungen“.

Falls dort fehlende Berechtigungen stehen, obwohl der Consent erteilt ist: 1 bis 2 Minuten warten und erneut testen (Entra braucht manchmal etwas). Bleibt es dabei, die Liste zurückmelden.

### 2.5 Einrichtung abschließen und anmelden

**Weiter** → **Einrichtung abschließen** → **Zur Anmeldung** → mit dem Owner-Konto bei Microsoft anmelden.

**Erwartet:**

- Die Anmeldung landet auf dem Dashboard der Plattform.
- Oben/links bist du als **Owner** angemeldet (Menüpunkte Tenants, Profile, Benutzer, Audit sichtbar).
- Im **Audit-Log** gibt es einen Eintrag zum Abschluss der Einrichtung (Aktion `setup.completed`).
- Das Setup-Token aus dem Log funktioniert nicht mehr.

Wenn du stattdessen auf `/login?error=remote_failure` landest, bitte zurückmelden (siehe Fehlerbild F2 unten).

---

## 3. Test-Tenant anbinden und „Jetzt beheben“

### 3.1 Ausgangslage im Tenant herstellen (absichtlich „kaputt“)

Damit „Jetzt beheben“ etwas zu tun hat, im Entra Admin Center → Schutz → Authentifizierungsmethoden → Richtlinien → **Passkey (FIDO2)**:

- Methode **deaktivieren** (Schalter „Aktivieren“ aus) und speichern.

Bitte vorher einen **Screenshot der ursprünglichen Einstellungen** machen (Aktivieren, Ziele, Ausschließen, Registerkarte „Konfigurieren“ inkl. Schlüsseleinschränkungen), damit wir das Ergebnis vergleichen können.

### 3.2 Tenant hinzufügen

Plattform → **Tenants** → **Tenant hinzufügen** → **Weiter zu Microsoft** → bei Microsoft mit dem Global Admin anmelden → Berechtigungsdialog „Im Namen Ihrer Organisation zustimmen“ → **Akzeptieren**.

**Erwartet:**

- Rückkehr auf die Tenant-Seite mit dem grünen Hinweis „Der Tenant ist angebunden. Prüfen Sie unten die FIDO2-Einstellungen.“
- Name und Standard-Domain des Test-Tenants sind korrekt.
- Im Audit-Log steht das Onboarding.

### 3.3 Zielgruppe setzen

Auf der Tenant-Seite → Einstellungen → **Zielgruppe**: `YLM-Test-Keyuser` suchen und wählen, Sync-Intervall z. B. 60 Minuten, speichern.

**Erwartet:** Die Gruppe wird über die Suche gefunden (zeigt, dass `GroupMember.Read.All`/`Group`-Suche funktioniert).

### 3.4 Prüfungen ansehen

Die Prüfungen laufen nach dem Onboarding automatisch. Nach dem Setzen der Zielgruppe auf **Erneut prüfen** klicken.

**Erwartet** (bei ausgeschalteter Passkey-Methode):

| Prüfung | Status |
|---|---|
| Passkey-/FIDO2-Methode aktiviert | Fehler, „Jetzt beheben“ sichtbar |
| YubiKey-AAGUIDs erlaubt | OK („keine Schlüsseleinschränkungen aktiv“), falls keine Einschränkung gesetzt ist |
| Zielgruppe für die Methode freigeschaltet | Fehler („Die Zielgruppe ist der Methode nicht zugewiesen.“), außer die Methode gilt schon für alle Benutzer |
| Graph-Berechtigungen | OK |

Bei jeder fehlerhaften Prüfung lässt sich „Manuell beheben“ aufklappen; dort steht der Weg im Entra Admin Center.

### 3.5 „Jetzt beheben“

Auf **Alles beheben** (oder einzeln **Jetzt beheben**) klicken.

**Erwartet:**

- Alle Prüfungen werden grün.
- Im Entra Admin Center (Seite neu laden) ist die Passkey-(FIDO2)-Methode **aktiviert** und die Gruppe `YLM-Test-Keyuser` unter „Ziel“ eingetragen.
- Andere Einstellungen der Richtlinie (z. B. Selbstregistrierung, Attestierung) sind **unverändert** gegenüber deinem Screenshot aus 3.1.
- Im Audit-Log steht ein Eintrag zur Richtlinienänderung.

### 3.6 Optional: AAGUID-Einschränkung testen

Im Entra Admin Center unter Passkey (FIDO2) → Konfigurieren: **Schlüsseleinschränkungen erzwingen = Ja**, Verhalten **Zulassen**, eine beliebige fremde AAGUID eintragen, speichern. In der Plattform **Erneut prüfen**.

**Erwartet:** „YubiKey-AAGUIDs erlaubt“ wird rot („… von … YubiKey-AAGUIDs fehlen auf der Zulassungsliste“). **Jetzt beheben** fügt die YubiKey-AAGUIDs hinzu, die fremde AAGUID bleibt erhalten.

### 3.7 Synchronisation

Auf der Tenant-Seite **Jetzt synchronisieren**.

**Erwartet:** Im Bereich „Synchronisation“ steht ein Zeitstempel, Erfolg, und „Personen in Zielgruppe“ = 1 (bzw. die Zahl der Gruppenmitglieder).

---

## 4. Agent auf dem Windows-Admin-PC

### 4.1 MSI besorgen

Das MSI wird in der CI gebaut:

GitHub → Actions → Workflow **CI** → letzter grüner Lauf auf `main` (für `e877d22`: [Lauf #7](https://github.com/PanPakaPancake/Yubikey-Lifecycle-Manager/actions/runs/43820908735)) → unten **Artifacts** → `agent-msi` herunterladen und entpacken. Darin liegt `YubikeyLifecycleManager-Agent.msi`.

Alternativ selbst bauen (Windows, .NET 10 SDK): `pwsh installer/build.ps1`, Ergebnis in `installer\out\`.

### 4.2 Installieren

Doppelklick auf das MSI. Das MSI ist nicht signiert; SmartScreen warnt deshalb („Weitere Informationen“ → „Trotzdem ausführen“). Die UAC-Abfrage bestätigen. Im letzten Dialog „Tray-App jetzt starten“ angehakt lassen.

Wenn die Installation scheitert, bitte mit Protokoll wiederholen:

```powershell
msiexec /i YubikeyLifecycleManager-Agent.msi /l*v "$env:TEMP\ylm-msi.log"
```

**Erwartet:**

- Dateien unter `C:\Program Files\Yubikey Lifecycle Manager\Agent\` (`YlmAgent.exe`, `YlmTray.exe`, `appsettings.json`)
- Startmenü-Eintrag „Yubikey Lifecycle Manager“
- Das Tray-Symbol erscheint unten rechts (ggf. im Überlauf „^“).

### 4.3 Dienst prüfen

```powershell
Get-Service YubikeyLifecycleManagerAgent | Format-List Name, Status, StartType
Get-CimInstance Win32_Service -Filter "Name='YubikeyLifecycleManagerAgent'" | Format-List StartName, PathName
Get-ChildItem C:\ProgramData\YubikeyLifecycleManager\Agent\logs
```

**Erwartet:**

- `Status: Running`, `StartType: Automatic`
- `StartName: LocalSystem`
- Im Ordner `logs` liegt eine aktuelle Logdatei.
- Autostart der Tray-App: Wert `YubikeyLifecycleManagerTray` unter `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run`:
  ```powershell
  Get-ItemProperty HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run | Select-Object YubikeyLifecycleManagerTray
  ```

### 4.4 Tray-App prüfen (vor dem Koppeln)

Rechtsklick auf das Tray-Symbol.

**Erwartet:**

- Erste Zeile: **„Nicht gekoppelt“** (wenn dort „Agent-Dienst nicht erreichbar“ steht, siehe F4)
- Zweite Zeile: „Kein Key eingesteckt“
- Menüpunkte: Status anzeigen, WebGUI öffnen, Koppeln …, Kopplung lösen, Logs öffnen, Version 0.1.x, Tray beenden
- „Logs öffnen“ öffnet den Explorer im Log-Ordner.

### 4.5 Koppeln

1. Plattform → **Agent** → **Kopplungscode erzeugen**. Der Code ist 10 Minuten gültig und nur einmal verwendbar.
2. Tray → **Koppeln …** → Server-Adresse (`http://localhost:8080` bzw. `https://ylm.example.com`) und Code eingeben → Koppeln.

**Erwartet:**

- Im Dialog erscheint eine Erfolgsmeldung.
- Tray-Menü zeigt **„Verbunden – <PC-Name>“**.
- Die Agent-Seite der Plattform listet den PC als verbunden.
- In `C:\ProgramData\YubikeyLifecycleManager\Agent\agent.json` steht die Server-Adresse; das Token ist verschlüsselt (kein Klartext).
- Im Audit-Log steht die Kopplung (Aktion `agent.paired`).

### 4.6 Neustart-Test

PC neu starten und neu anmelden.

**Erwartet:** Dienst läuft, Tray-App startet von selbst und zeigt nach kurzer Zeit wieder „Verbunden“, ohne erneutes Koppeln.

---

## 5. Echter YubiKey

> Vorher: Yubico Authenticator und andere FIDO-Programme schließen. Immer **nur einen** Key einstecken.

### 5.1 Key erkennen: Seriennummer und Firmware

YubiKey einstecken.

**Erwartet:**

- Tray-Sprechblase „YubiKey erkannt: <Modell>, Seriennummer <…>, Firmware <…>“. Bei Firmware < 5.7 zusätzlich „Hinweis: PIN-Wechsel kann nicht erzwungen werden“.
- Tray-Menü: „Key: <Modell> · <Seriennummer> · FW <Version>“.

**Gegenprüfen:** Seriennummer und Firmware mit Yubico Authenticator (Startseite) oder `ykman info` vergleichen, danach das Programm wieder schließen. Bitte das **Modell** notieren, wie es Yubico anzeigt, und wie es die Plattform anzeigt.

### 5.2 Optional: Wareneingang

Plattform → **Keys** → **Wareneingang per Einstecken** → Tenant wählen → Key erfassen.

**Erwartet:** Der Key erscheint im Inventar mit Status „Lager“, korrekter Seriennummer, Firmware und Modell.

### 5.3 Profil prüfen

Plattform → **Profile**. Es gibt das Profil **„Standard“**: zufällige PIN, Länge 8, PIN-Wechsel erzwingen, Anzeigename „YubiKey {Seriennummer}“. Nichts ändern.

### 5.4 Einzel-Wizard (Key mit Firmware ≥ 5.7)

Plattform → **Key registrieren**:

1. **Person:** Test-Tenant wählen, Testperson suchen (mind. 2 Zeichen) und anklicken.
2. **YubiKey einstecken:** Dein Agent ist ausgewählt, der Key wird angezeigt → Weiter.
3. **Profil wählen und prüfen:** Profil „Standard“ → **Prüfen**.
   - Erwartet: „Der Key erfüllt das Profil.“ und der Hinweis, dass der PIN-Wechsel erzwungen wird.
   - Hat der Key schon eine PIN oder Passkeys, ist „FIDO-Werksreset vor der Registrierung (erforderlich)“ fest angehakt. Das ist gewollt. (Dabei fordert die Tray-App auf: Key **abziehen**, wieder **anstecken**, dann innerhalb von 10 Sekunden **berühren**.)
4. **Registrierung starten.** Die Schritte laufen sichtbar durch. Wenn der Key blinkt, **berühren**.

**Erwartet** (Schritte im Wizard und als Tray-Sprechblasen):

1. Key wird geprüft
2. (nur bei Reset) Werksreset der FIDO2-Funktion
3. PIN wird gesetzt
4. Passkey wird auf dem Key erzeugt → Key blinkt, berühren
5. Passkey wird in Entra registriert
6. PIN-Wechsel wird erzwungen
7. Inventar wird aktualisiert

Danach: „Fertig – Der Key ist für <Testperson> registriert.“ mit Button **PIN-Brief (PDF)**.

### 5.5 PIN-Brief

**PIN-Brief (PDF)** klicken. Der Brief ist **30 Minuten** abrufbar und wird nicht gespeichert.

**Erwartet:**

- PDF mit Name der Person, Modell, Seriennummer, der PIN und grafischer Anleitung.
- Hinweis, dass die PIN beim ersten Gebrauch geändert werden muss.
- Umlaute und Layout sind sauber (bitte Screenshot oder das PDF mit geschwärzter PIN schicken).
- Im Audit-Log: Registrierung und Abruf des PIN-Briefs; **die PIN steht nirgends im Audit-Log oder in den Logs**.

### 5.6 Ergebnis in Entra und im Inventar

- **Entra Admin Center** → Benutzer → Testperson → Authentifizierungsmethoden: Es gibt eine Methode **Passkey (FIDO2)** mit Namen „YubiKey <Seriennummer>“ und einem YubiKey-Modell.
- **Plattform → Keys:** Key mit Status **Aktiv**, Zuordnung zur Testperson, Seriennummer, Firmware, Registrierdatum.

### 5.7 Erzwungener PIN-Wechsel in der Praxis

Mit der Testperson auf einem PC in einem privaten Browserfenster `https://myaccount.microsoft.com` öffnen und mit dem YubiKey anmelden („Anmeldeoptionen“ → „Mit Gesichtserkennung, Fingerabdruck, PIN oder Sicherheitsschlüssel anmelden“ bzw. „Sicherheitsschlüssel“).

**Erwartet:**

- Windows fordert vor der Anmeldung auf, die PIN des Sicherheitsschlüssels zu ändern (alte PIN aus dem PIN-Brief, dann neue PIN).
- Danach gelingt die Anmeldung mit der neuen PIN.
- Gegenprobe: Yubico Authenticator zeigt nach dem Wechsel keinen Hinweis „PIN-Änderung erforderlich“ mehr.

Bitte genau notieren, **was Windows anzeigt** (Screenshot), auch wenn statt einer Wechselaufforderung ein Fehler kommt. Das Verhalten hängt von der Windows-Version ab.

### 5.8 Key mit Firmware < 5.7 (falls vorhanden)

Wizard wie in 5.4 mit dem älteren Key.

**Erwartet:**

- Bei „Prüfen“ der Hinweis „Dieser Key (Firmware …) kann keinen PIN-Wechsel erzwingen …“. Das Profil verlangt ihn aber, deshalb muss erst **„Für diesen Key bewusst überschreiben“** angehakt werden, bevor „Registrierung starten“ geht.
- Schritt 6 „PIN-Wechsel wird erzwungen“ fehlt.
- Im PIN-Brief steht „Bitte PIN selbst ändern“ statt des Hinweises auf den erzwungenen Wechsel.
- Im Audit-Log steht die bestätigte Profil-Abweichung.

Besonders interessant: Keys mit Firmware **5.4 bis 5.6**. Bitte melden, ob die Plattform dort trotzdem meint, der PIN-Wechsel lasse sich erzwingen (und ob Schritt 6 dann klappt oder scheitert).

### 5.9 Zurücksetzen (Aufräumen)

Plattform → Keys → den registrierten Key öffnen → **Zurücksetzen und ins Lager** → **Zurücksetzen starten**, Key steckt am Agent. Anweisungen der Tray-App folgen (abziehen, anstecken, berühren).

**Erwartet:**

- Der Passkey der Testperson ist in Entra **entfernt**.
- Der Key ist wieder im Status **Lager**.
- Yubico Authenticator zeigt: keine PIN gesetzt, keine Passkeys.

### 5.10 Negativtests (kurz)

| Test | Erwartet |
|---|---|
| Im Wizard zwei Keys gleichzeitig einstecken | Meldung „Es sind mehrere YubiKeys eingesteckt …“ |
| Key nach „Prüfen“ gegen einen anderen tauschen und starten | Meldung „Es steckt ein anderer Key als in der Vorschau …“ |
| Beim Berühren-Schritt 90 Sekunden nichts tun | Saubere Fehlermeldung (Zeitüberschreitung), kein Hängenbleiben; Wizard bietet „Zurück zur Prüfung“ |
| Tray beenden und Dienst neu starten: `Restart-Service YubikeyLifecycleManagerAgent` | Agent ist nach kurzer Zeit wieder „Verbunden“ |

---

## 6. Anmeldung nur mit YubiKey (Abschlusstest)

1. Für das **Owner-Konto** selbst einen YubiKey/Passkey registrieren (z. B. über `https://mysignins.microsoft.com/security-info`).
2. Die Datei `docker-compose.override.yml` löschen und neu starten:
   ```powershell
   Remove-Item docker-compose.override.yml
   docker compose up -d
   ```
3. Abmelden und neu anmelden.

**Erwartet:**

- Anmeldung mit **Passwort** (ohne Key) wird von der Plattform abgelehnt: Login-Seite mit „Anmeldung muss passwortlos mit Sicherheitsschlüssel erfolgen (amr: pwd …)“.
- Anmeldung mit dem **YubiKey** klappt.

---

## Fehler zurückmelden

Bitte bei jeder Abweichung zurückmelden:

1. **Schritt-Nummer** aus dieser Anleitung (z. B. „4.5“) und was du erwartet hast
2. **Screenshot** der Fehlermeldung in der Oberfläche bzw. im Tray
3. **Uhrzeit** des Fehlers (damit wir in den Logs die richtige Stelle finden)
4. Die passenden **Logs** aus der Tabelle

| Bereich | Wie bekomme ich das Log? |
|---|---|
| Server | `docker compose logs server --since 30m > server.log` (im Repo-Ordner) |
| Datenbank (nur bei Startproblemen) | `docker compose logs db --since 30m > db.log` |
| Web-Oberfläche | Browser F12 → Netzwerk → die rote Anfrage anklicken → Registerkarte „Antwort“ (enthält `code` und `message`) → kopieren oder Screenshot. Zusätzlich die Registerkarte „Konsole“. |
| Agent-Dienst | Ordner `C:\ProgramData\YubikeyLifecycleManager\Agent\logs` (Tray → „Logs öffnen“), die neueste Datei |
| Windows-Ereignisanzeige | Windows-Protokolle → Anwendung, Quelle „Yubikey Lifecycle Manager Agent“ (bei Dienst-Abstürzen auch „.NET Runtime“ und „Application Error“) |
| MSI-Installation | `msiexec /i … /l*v "%TEMP%\ylm-msi.log"` und die Datei mitschicken |
| PowerShell-Skript | die komplette Konsolenausgabe (roter Text inkl. Zeile darüber) |
| Entra | Entra Admin Center → Anmeldeprotokolle (Anmeldefehler, Fehlercode `AADSTS…`) bzw. Überwachung → Überwachungsprotokolle (Richtlinienänderungen) |
| Plattform | Audit-Log der Plattform (Screenshot der betreffenden Einträge) |

Außerdem hilfreich: Commit (`git log -1 --oneline`), Version der Tray-App (steht im Tray-Menü), Windows-Version (`winver`), YubiKey-Modell und Firmware.

**Bitte nicht schicken:** Setup-Token, PINs (im PIN-Brief schwärzen), Inhalt von `.env`, Dateien aus dem Docker-Volume `data`. UPNs und Tenant-IDs eines Test-Tenants sind unkritisch, dürfen aber gern geschwärzt werden.

### Bekannte Fehlerbilder

| Nr. | Symptom | Erste Hilfe |
|---|---|---|
| F1 | Setup-Test: „Fehlende Berechtigungen …“ | Admin-Consent erteilt (2.3)? 1 bis 2 Minuten warten, erneut testen. |
| F2 | Nach der Microsoft-Anmeldung `/login?error=remote_failure` | Browser-Konsole und Server-Log schicken. Bei Variante A zusätzlich in einem anderen Browser (Edge/Chrome) testen. Notfalls auf Variante B (HTTPS) wechseln. |
| F3 | Microsoft meldet `AADSTS50011` (Reply URL mismatch) | Die öffentliche Adresse in 2.1 weicht von der Adresse im Browser ab (z. B. `127.0.0.1` statt `localhost`, fehlender Port). Genau die Adresse aus der Einrichtung verwenden. |
| F4 | Tray zeigt „Agent-Dienst nicht erreichbar“ | `Get-Service YubikeyLifecycleManagerAgent`; falls gestoppt, `Start-Service …` und Ereignisanzeige prüfen. |
| F5 | Tray zeigt „Getrennt“ oder „Nicht angemeldet“ nach dem Koppeln | Server erreichbar? (`curl.exe <adresse>/healthz`). Bei „Nicht angemeldet“ wurde der Agent in der Oberfläche entkoppelt: neu koppeln. |
| F6 | Key wird erkannt, aber ohne FIDO-Infos / PIN setzen scheitert | Agent-Log schicken. Ursache meist fehlende Rechte für FIDO über HID; der Dienst muss als LocalSystem laufen (4.3). |
| F7 | Wizard scheitert bei Schritt 4 oder 5 (Passkey erzeugen / in Entra registrieren) | Das ist der spannendste Teil (Beta-Schnittstelle `fido2Methods/creationOptions`). Server-Log **und** Agent-Log aus demselben Zeitraum schicken. Der Key hat dann schon eine PIN; für den nächsten Versuch im Wizard den Werksreset ankreuzen. |
| F8 | „Die FIDO2-Richtlinie des Tenants lässt diesen Key-Typ (AAGUID …) nicht zu“ | AAGUID notieren und melden (die AAGUID-Liste der Plattform muss evtl. ergänzt werden), dann Tenant-Prüfungen beheben. |

---

## Protokollbogen

Zum Kopieren in eine Antwort, Haken setzen oder Abweichung notieren:

```text
Commit:            ____________
Variante:          A (localhost) / B (HTTPS)
Windows-Version:   ____________
YubiKey 1:         Modell ________  FW ______  SN ________
YubiKey 2:         Modell ________  FW ______  SN ________

[ ] 1.3  Container laufen, healthy
[ ] 1.4  /healthz ok, Setup-Token im Log
[ ] 2.2  Skript ohne Fehler, App in Entra korrekt
[ ] 2.3  Admin-Consent erteilt
[ ] 2.4  Setup-Test grün, keine fehlenden Berechtigungen
[ ] 2.5  Anmeldung als Owner
[ ] 3.2  Tenant angebunden
[ ] 3.3  Zielgruppe gefunden und gesetzt
[ ] 3.4  Prüfungen zeigen die erwarteten Fehler
[ ] 3.5  "Jetzt beheben" korrigiert die Richtlinie, sonst nichts verändert
[ ] 3.6  (optional) AAGUID-Einschränkung erkannt und behoben
[ ] 3.7  Sync erfolgreich, Personenzahl stimmt
[ ] 4.2  MSI installiert
[ ] 4.3  Dienst läuft als LocalSystem, Autostart
[ ] 4.4  Tray-Menü vollständig
[ ] 4.5  Kopplung erfolgreich
[ ] 4.6  Nach Neustart wieder verbunden
[ ] 5.1  Seriennummer/Firmware/Modell stimmen mit Yubico überein
[ ] 5.2  (optional) Wareneingang
[ ] 5.4  Wizard komplett durchgelaufen
[ ] 5.5  PIN-Brief korrekt
[ ] 5.6  Passkey in Entra, Key im Inventar aktiv
[ ] 5.7  PIN-Wechsel wird beim ersten Gebrauch verlangt
[ ] 5.8  (FW < 5.7) Hinweis, manuelle Bestätigung, Brief "PIN selbst ändern"
[ ] 5.9  Zurücksetzen entfernt Passkey und setzt Key zurück
[ ] 5.10 Negativtests
[ ] 6    Anmeldung nur mit YubiKey erzwungen

Abweichungen:
- Schritt __: ...
```
