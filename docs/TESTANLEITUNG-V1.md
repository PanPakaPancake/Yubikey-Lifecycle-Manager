# Testanleitung V1: Praxistest mit echtem Tenant und echtem YubiKey

Diese Anleitung führt Schritt für Schritt durch den ersten echten Test von V1. Bisher ist V1 nur gegen einen simulierten YubiKey und ein simuliertes Microsoft Graph getestet. Jetzt prüfen wir:

1. Server per Docker Compose starten
2. Einrichtung im Web-Assistenten: Anmeldung bei Microsoft, die Plattform legt ihre App-Registrierung selbst an
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
| Konto **Globaler Administrator** im Test-Tenant | Für die Einrichtung, Admin-Consent und „Jetzt beheben“. Dieses Konto wird auch der erste Owner der Plattform. |
| **Testperson** im Test-Tenant, z. B. `ylm-test1@<tenant>.onmicrosoft.com` | Normales Konto, aktiviert, **ohne** bisherigen Passkey. Für diese Person wird der YubiKey registriert. |
| **Windows-Admin-PC** (Windows 10/11, x64) mit lokalen Adminrechten | Hier laufen Agent und Tray-App. |
| **Docker Desktop** auf dem Admin-PC (Variante A) oder ein Linux-Server mit Docker (Variante B) | siehe 1. |
| Nur für den Ausweichweg: **PowerShell 7** (`pwsh`) und das Modul **Microsoft.Graph** | `Install-Module Microsoft.Graph -Scope CurrentUser` |
| **Git** | zum Klonen des Repos |
| **Mindestens ein YubiKey 5** (ideal: einer mit Firmware **≥ 5.7** und einer mit **< 5.7**) | Achtung: Der Test kann den Key **zurücksetzen**. Keinen Key verwenden, auf dem wichtige Passkeys liegen. |
| Optional: **Yubico Authenticator** oder `ykman` | Zum Gegenprüfen von Seriennummer und Firmware. Während des Wizards bitte **schließen**, damit er den Key nicht blockiert. |

### 0.2 Testgruppe anlegen

Im Entra Admin Center → Gruppen → Neue Gruppe:

- Typ Sicherheit, Name z. B. **`YLM-Test-Keyuser`**
- Mitglied: die Testperson

Die Gruppe brauchen wir in Schritt 3 als Zielgruppe.

### 0.3 Version notieren

Getestet wird das neueste Release. Die Version steht nach der Installation unter **Globale Einstellungen → Version und Updates**; bitte notieren.

---

## 1. Server installieren

### Welche Variante?

| | Variante A: alles auf dem Admin-PC (**empfohlen für den ersten Test**) | Variante B: Linux-Server/VPS mit Domain |
|---|---|---|
| Adresse | `http://localhost:8080` | `https://ylm.<deine-domain>` |
| Aufwand | gering, kein DNS, kein Zertifikat | DNS-Eintrag, Ports 80/443; HTTPS richtet das Skript mit Caddy ein |
| Entspricht Produktion | nein | ja |

Die Plattform erlaubt `http://` nur für `localhost`. Entra akzeptiert `http://localhost` als Umleitungs-URI, der Agent ebenfalls. Für andere Adressen ist HTTPS Pflicht. Alle Varianten sind in [BETRIEB.md](BETRIEB.md#1-server-installieren) beschrieben.

### 1.1 Installationsskript ausführen

**Variante A (PowerShell auf dem Admin-PC, Docker Desktop läuft):**

```powershell
irm https://raw.githubusercontent.com/PanPakaPancake/Yubikey-Lifecycle-Manager/main/install.ps1 | iex
```

**Variante B (Linux-Server per SSH):**

```bash
curl -fsSL https://raw.githubusercontent.com/PanPakaPancake/Yubikey-Lifecycle-Manager/main/install.sh | sudo bash
```

Variante 1 wählen und die Domain eingeben.

**Erwartet:**

- Das Skript legt den Installationsordner an (A: `%USERPROFILE%\YubikeyLifecycleManager`, B: `/opt/ylm`) und darin eine `.env` mit Zufallsgeheimnissen.
- Es lädt die Images, startet `db`, `server`, `backup`, `updater` (B zusätzlich `caddy`) und meldet „Fertig“ mit Adresse und **Setup-Token**. Token kopieren.
- `docker compose ps` im Installationsordner zeigt `db` und nach ca. 30 Sekunden auch `server` als `healthy`.
- B: `https://ylm.<deine-domain>/healthz` antwortet mit `{"status":"ok"}` und gültigem Zertifikat.

### 1.2 Anmeldemethode

Die Plattform prüft die Anmeldemethode der Admins standardmäßig nicht selbst. Welche Methoden erlaubt sind (z. B. nur YubiKey/Passkey), regelt der Tenant-Betreiber in Entra per Bedingtem Zugriff. Die optionale zusätzliche Prüfung testen wir in Schritt 7.

### 1.3 Datensicherung

```powershell
docker compose exec backup sh /backup.sh now
docker compose exec backup sh /backup.sh list
```

**Erwartet:** Eine Datei `ylm-JJJJMMTT-HHMMSS.tar.gz.enc` im Ordner `backups` (verschlüsselt mit `BACKUP_PASSPHRASE` aus `.env`; ohne Passphrase `.tar.gz`). Wiederherstellen und Umzug beschreibt [BETRIEB.md 1.6 und 1.7](BETRIEB.md#16-datensicherung-und-wiederherstellung).

---

## 2. Einrichtung und App-Registrierung (Web-Assistent)

Die Plattform legt ihre App-Registrierung selbst an. Du meldest dich dazu im Assistenten einmal mit dem Global Admin bei Microsoft an. Das PowerShell-Skript gibt es nur noch als Ausweichweg (2.4).

### 2.1 Setup-Assistent öffnen

Browser (Edge oder Chrome): `http://localhost:8080/setup` (Variante B: `https://ylm.example.com/setup`).

1. **Setup-Token** einfügen → Weiter.
2. **Grunddaten:** Organisation (z. B. „Test“) und öffentliche Adresse (`http://localhost:8080` bzw. deine HTTPS-Adresse) → Speichern und weiter.

### 2.2 Bei Microsoft anmelden

1. **Bei Microsoft anmelden** klicken. Es öffnet sich die normale Microsoft-Anmeldung in einem kleinen Fenster.
2. Mit dem **Global Admin des Test-Tenants** anmelden. Microsoft fragt, ob du dich bei „Microsoft Graph Command Line Tools“ anmelden willst, und zeigt die Berechtigungen `Application.ReadWrite.All`, `AppRoleAssignment.ReadWrite.All`, `DelegatedPermissionGrant.ReadWrite.All` und `User.Read`. Bestätigen.
3. Läuft die Plattform unter `http://localhost:8080`, meldet das Fenster „Angemeldet“ und schließt sich. Auf einem Server zeigt das Fenster „Seite nicht erreichbar“ unter `http://localhost/?code=…`: die komplette Adresse aus der Adresszeile kopieren, im Assistenten einfügen und **Anmeldung abschließen** klicken.

**Erwartet:**

- Nach wenigen Sekunden zeigt der Assistent „Angemeldet als … im Tenant …“ mit deinem Konto und dem Namen des Test-Tenants.
- Ohne weiteren Klick erscheinen nacheinander: „App-Registrierung angelegt“, „Zertifikat hochgeladen“, „Dienstprinzipal angelegt“, „Admin-Consent für 7 Anwendungsberechtigungen erteilt“, „Admin-Consent für die Anmeldung erteilt“.
- Danach prüft der Assistent die Anmeldung mit dem Zertifikat. Das kann **bis zu zwei Minuten** dauern, weil Entra neue Zertifikate und Berechtigungen verzögert übernimmt. Am Ende steht der grüne Hinweis „Fertig: Die Plattform meldet sich mit ihrem Zertifikat an, alle Berechtigungen sind erteilt.“
- Im Entra Admin Center unter App-Registrierungen gibt es eine App **„Yubikey Lifecycle Manager Test“** mit:
  - Kontotyp „Konten in einem beliebigen Organisationsverzeichnis“
  - Umleitungs-URIs (Web): `<adresse>/signin-oidc` und `<adresse>/api/tenants/consent-callback`
  - einem Zertifikat unter „Zertifikate & Geheimnisse“
  - 7 Anwendungsberechtigungen und 4 delegierten Berechtigungen für Microsoft Graph, alle mit grünem Haken „Gewährt für <Tenant>“

Mögliche Fehler:

- Meldung mit `AADSTS53003` oder „blockiert durch Conditional Access“: Die Code-Anmeldung ist im Tenant gesperrt. Dann bitte 2.4 (Skript) nutzen und die Meldung zurückmelden.
- Meldung „Microsoft Graph meldet 403 … braucht die Rolle Globaler Administrator“: Das Konto hat nicht genug Rechte.
- Bleibt die Prüfung bei „Es fehlen noch Berechtigungen“ hängen: **Nur erneut prüfen** klicken. Hilft das nach ein paar Minuten nicht, die Liste zurückmelden.
- **Erneut versuchen** ist gefahrlos: Eine zweite Anmeldung im selben Tenant aktualisiert die schon angelegte App, statt eine zweite anzulegen.

### 2.3 Abschluss vorbereiten

**Weiter** → im Schritt „Abschluss“:

- **Erster Owner** ist mit dem eben angemeldeten Konto vorausgefüllt.
- Der Haken **„Heim-Tenant … gleich als ersten Tenant anbinden“** ist gesetzt. Für diesen Test so lassen.

### 2.4 Ausweichweg: PowerShell-Skript (nur falls 2.2 nicht klappt)

Im Schritt „Microsoft-Anmeldung“ unten **Alternativ: per PowerShell-Skript oder manuell anlegen** aufklappen → **Skript herunterladen** (`ylm-app-registration.ps1`). Benötigt PowerShell 7 und das Modul Microsoft.Graph (`Install-Module Microsoft.Graph -Scope CurrentUser`).

```powershell
cd $HOME\Downloads
Unblock-File .\ylm-app-registration.ps1
Set-ExecutionPolicy -Scope Process Bypass
pwsh .\ylm-app-registration.ps1
```

Am Ende gibt das Skript Client-ID und Heim-Tenant-ID aus. Beide im aufgeklappten Bereich eintragen → **Speichern und testen** → bei grünem Hinweis ohne „Fehlende Berechtigungen“ → **Weiter**. Den ersten Owner (UPN deines Global-Admin-Kontos) trägst du dann im Abschluss selbst ein.

### 2.5 Einrichtung abschließen und anmelden

**Einrichtung abschließen** → **Zur Anmeldung** → mit dem Owner-Konto bei Microsoft anmelden.

**Erwartet:**

- Die Anmeldung landet auf dem Dashboard der Plattform.
- Oben/links bist du als **Owner** angemeldet (Menüpunkte Tenants, Profile, Benutzer, Audit sichtbar).
- Im **Audit-Log** gibt es Einträge `setup.app_provisioned`, `tenant.onboarded` (Heim-Tenant) und `setup.completed`.
- Unter **Tenants** ist der Test-Tenant schon vorhanden, mit Namen, Standard-Domain und ersten Prüfergebnissen.
- Das Setup-Token aus dem Log funktioniert nicht mehr.

Wenn du stattdessen auf `/login?error=remote_failure` landest, bitte zurückmelden (siehe Fehlerbild F2 unten).

---

## 3. Test-Tenant anbinden und „Jetzt beheben“

### 3.1 Ausgangslage im Tenant herstellen (absichtlich „kaputt“)

Damit „Jetzt beheben“ etwas zu tun hat, im Entra Admin Center → Schutz → Authentifizierungsmethoden → Richtlinien → **Passkey (FIDO2)**:

- Methode **deaktivieren** (Schalter „Aktivieren“ aus) und speichern.

Bitte vorher einen **Screenshot der ursprünglichen Einstellungen** machen (Aktivieren, Ziele, Ausschließen, Registerkarte „Konfigurieren“ inkl. Schlüsseleinschränkungen), damit wir das Ergebnis vergleichen können.

### 3.2 Tenant öffnen (schon angebunden) und Consent-Ablauf testen

Der Test-Tenant ist seit 2.3 als erster Tenant angebunden. Plattform → **Tenants** → Test-Tenant öffnen → bei den Prüfungen **Erneut prüfen**, damit die in 3.1 ausgeschaltete FIDO2-Richtlinie erkannt wird.

Den Consent-Ablauf für weitere Kunden-Tenants trotzdem einmal testen: **Tenants** → **Tenant hinzufügen** → **Weiter zu Microsoft** → mit dem Global Admin anmelden → „Im Namen Ihrer Organisation zustimmen“ → **Akzeptieren**.

**Erwartet:**

- Rückkehr auf die Tenant-Seite mit dem grünen Hinweis „Der Tenant ist angebunden. Prüfen Sie unten die FIDO2-Einstellungen.“
- Es gibt weiterhin nur **einen** Eintrag für den Test-Tenant (kein Duplikat).
- Name und Standard-Domain des Test-Tenants sind korrekt.

### 3.3 Zielgruppe setzen

Auf der Tenant-Seite → Einstellungen → **Zielgruppe** → Einschließen: `YLM-Test-Keyuser` suchen und wählen (weitere Gruppen und Ausschlussgruppen sind möglich), Sync-Intervall z. B. 60 Minuten, speichern.

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
  Ab 0.5 endet der Wert mit `--autostart` (damit greift „Mit Windows starten“ in den Tray-Einstellungen).

### 4.4 Tray-App prüfen (vor dem Koppeln)

Rechtsklick auf das Tray-Symbol.

**Erwartet:**

- Erste Zeile: **„Nicht gekoppelt“** (wenn dort „Agent-Dienst nicht erreichbar“ steht, siehe F4)
- Zweite Zeile: „Kein Key eingesteckt“
- Menüpunkte: Status anzeigen, WebGUI öffnen, Koppeln …, Kopplung lösen, Einstellungen …, Logs öffnen, Version 0.x.y, Tray beenden
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

### 4.7 Agent-Einstellungen (ab 0.5)

Tray → Rechtsklick → **Einstellungen …**

| Test | Erwartet |
|---|---|
| Fenster öffnen | Abschnitte Allgemein, Updates, Verbindung. „Installiert: 0.5.x“, „Zuletzt geprüft“ erscheint spätestens 2 Minuten nach dem Dienststart. |
| „Mit Windows starten“ abwählen, abmelden, wieder anmelden | Kein Tray-Symbol. Start über das Startmenü („Yubikey Lifecycle Manager“) geht weiterhin. Wieder anhaken, abmelden/anmelden: Symbol ist da. Gespeichert unter `HKCU\Software\YubikeyLifecycleManager\Tray`, Wert `AutoStart`. |
| „Hinweise anzeigen …“ abwählen, Dienst stoppen (`Stop-Service YubikeyLifecycleManagerAgent`), Key einstecken | Keine Sprechblase. Danach `Start-Service …` und wieder anhaken. |
| „Updates automatisch installieren“ als Administrator abwählen | Meldung „Automatische Updates sind ausgeschaltet.“ Plattform → Globale Einstellungen → Version und Updates → „Alle Agents“: dein PC zeigt Auto-Update **aus**, unter **Agent** steht die Plakette „Auto-Update aus“. Wieder anhaken. |
| Als Standardbenutzer (ohne Adminrechte) anmelden und Einstellungen öffnen | Der Update-Schalter ist ausgegraut mit „Nur Administratoren dieses PCs können das ändern.“ (überspringen, wenn kein zweites Konto da ist) |
| Plattform: **Agent-Updates** auf „Immer automatisch“, dann im Tray „Jetzt nach Updates suchen“ | Schalter im Tray ist angehakt und gesperrt: „Vom Server vorgegeben …“. Danach Vorgabe wieder auf Standard. |
| „Jetzt nach Updates suchen“ ohne neue Version | „Aktuell (Version 0.5.x).“ Im Agent-Log eine Zeile `Update: Aktuell …` |

### 4.8 Auto-Update in der Praxis

Das Update lässt sich nur mit einer **höheren** Version testen. Vorschlag: v0.5.0 taggen, Server aktualisieren, dann v0.5.1 ohne weitere Änderungen taggen.

**A) 0.4.x → 0.5.0** (dein jetziger Agent mit dem alten Update-Ablauf)

1. Server auf 0.5.0 aktualisieren (Globale Einstellungen → Jetzt aktualisieren).
2. Auf dem PC `Restart-Service YubikeyLifecycleManagerAgent`, damit der Agent nach 2 Minuten prüft (sonst bis zu 4 Stunden).
3. **Erwartet:** Nach 2–4 Minuten zeigt die Plattform unter **Agent** Version 0.5.0. Der alte Ablauf beendet die Tray-App nicht: Steht im Tray-Menü noch die alte Version oder fehlt „Einstellungen …“, einmal ab- und wieder anmelden. Das ist nur bei diesem einen Sprung so.

**B) 0.5.0 → 0.5.1** (neuer Ablauf)

1. Server auf 0.5.1 aktualisieren. Plattform → **Agent**: Plakette „Update auf 0.5.1 folgt automatisch“ und der Link **Jetzt aktualisieren**.
2. **Jetzt aktualisieren** klicken (oder Tray → Einstellungen → „Jetzt nach Updates suchen“).
3. **Erwartet:**
   - Sprechblase „Agent wird aktualisiert“, das Tray-Symbol verschwindet.
   - Nach etwa einer Minute ist das Symbol von selbst wieder da, Sprechblase „Agent aktualisiert … Version 0.5.1“.
   - Tray → Einstellungen: „Aktualisiert von 0.5.0 auf 0.5.1 am …“. Kein Neustart des PCs nötig.
   - Agent-Log: `Neue Agent-Version 0.5.1 verfügbar`, `Installiere Agent 0.5.1`, nach dem Neustart `Update: Aktualisiert von 0.5.0 auf 0.5.1` und `Tray-App in Sitzung N neu gestartet`.
   - `C:\ProgramData\YubikeyLifecycleManager\Agent\updates\install-0.5.1.log` endet mit `Installation success or error status: 0` (bzw. „Erfolg- oder Fehlerstatus: 0“).
4. Falls es fehlschlägt: Die Meldung steht im Tray unter Einstellungen und in der Plattform (Agent-Liste, rot). Bitte das msiexec-Protokoll und das Agent-Log mitschicken.

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

Links unter **Partner** → **Profile**. Es gibt das Profil **„Standard“** (an alle Tenants vererbt): zufällige PIN, Länge 8, PIN-Wechsel erzwingen, Anzeigename „YubiKey {Seriennummer}“. Nichts ändern.

In den **Tenant-Einstellungen** zeigt die Karte **Registrierungsprofile** das geerbte Profil („Global · geerbt“). Über **Eigenes Profil anlegen** lässt sich ein Profil nur für diesen Tenant ergänzen; mit „Als Standardprofil dieses Tenants verwenden“ ersetzt es dort das Standardprofil des Partners.

### 5.4 Einzel-Wizard (Key mit Firmware ≥ 5.7)

Plattform → **Registrieren** → **Für eine Person**:

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

### 5.11 Stapelregistrierung (ab V1.1, zwei oder mehr Keys)

Vorbereitung: eine kleine Entra-Gruppe mit zwei bis drei Testpersonen, die noch keinen Key haben, und ebenso viele Keys im Werkszustand.

1. Plattform → **Registrieren** → **Stapel** → **Neuer Stapel** → Reiter **Entra-Gruppe** → Gruppe suchen und wählen.
2. Liste prüfen: Personen mit vorhandenem Key sind abgewählt. Optional bei einer Person eine **feste Seriennummer** eintragen (dann bekommt nur genau dieser Key die Person).
3. **Stapel anlegen** → **Abarbeiten starten**. Ersten Key einstecken.
4. Passt der Key ohne Rückfrage (Werkszustand, Profil erfüllt), startet die Registrierung automatisch; sonst erscheint die Prüfung wie im Einzel-Wizard.
5. Nach jedem Key: abziehen, nächsten einstecken. Den Key mit fester Seriennummer gerne außer der Reihe einstecken.
6. Am Ende **Alle PIN-Briefe** herunterladen: ein PDF mit einer Seite pro Person.

**Erwartet:**

- Jeder Key landet bei der nächsten offenen Person, der feste Key bei „seiner“ Person.
- Derselbe Key ein zweites Mal eingesteckt: Hinweis „bereits registriert, bitte abziehen“.
- Nach der letzten Person steht der Stapel auf **Abgeschlossen**; im Audit-Log stehen `batch.created`, je Person `key.registered` und `batch.completed`.
- Optional als CSV statt Gruppe: Datei mit Kopfzeile `UPN;Seriennummer;Profil`. Unbekannte Personen und falsche Seriennummern werden mit Zeilennummer gemeldet.

### 5.12 Geteilter Key (ab V1.1)

Vorbereitung: zwei Testkonten (z. B. Schichtarbeitsplatz) und ein Key im Werkszustand mit Firmware ≥ 5.7.

1. **Registrieren** → **Geteilter Key / Breakglass** → Reiter **Geteilter Key** → Verwahrer eintragen (Pflichtfeld) → beide Konten hinzufügen → **Weiter**.
2. Key einstecken → Profil prüfen → **Registrieren**.
3. Den einen PIN-Brief herunterladen: er ist an den Verwahrer adressiert und nennt beide Konten.

**Erwartet:**

- In Entra hat jedes der beiden Konten einen Passkey auf demselben Key.
- Im Inventar steht der Key mit Nutzung **Geteilt**, dem Verwahrer und zwei Registrierungen.
- Ohne Verwahrer lässt sich nicht weiterklicken.

Weitere Konten hinzufügen: in der Key-Ansicht **Weitere Konten hinzufügen**. Der Key hat dann schon eine PIN; der Assistent fragt die aktuelle PIN ab und setzt keine neue. **Wichtig:** Steht der erzwungene PIN-Wechsel noch aus, lehnt der Key weitere Passkeys ab. Der Verwahrer muss die PIN also vorher einmal ändern (z. B. bei der ersten Anmeldung).

Einzelnes Konto entfernen: in der Key-Ansicht beim Konto **Entfernen**. Der Passkey wird in Entra gelöscht, der Key bleibt für die anderen Konten aktiv.

### 5.13 Breakglass mit Vier-Augen-Freigabe (ab V2)

Vorbereitung: ein zweites Plattformkonto mit dem Recht „Breakglass freigeben“ (Owner oder eigene Rolle).

1. **Tenant-Einstellungen** → **Vier-Augen-Freigabe für Breakglass-Keys** anhaken → **Speichern**.
2. **Registrieren** → **Geteilter Key / Breakglass** → Reiter **Breakglass** → Verwahrer (z. B. „IT-Tresor“), Breakglass-Konto, Key und Profil wählen. In Schritt 3 erscheint statt **Registrieren** der Kasten **Freigabe beantragen** mit Pflichtfeld Grund.
3. Mit dem zweiten Konto anmelden → **Freigaben** → **Freigeben**.
4. Wieder mit dem ersten Konto: im Assistenten ist die Freigabe jetzt grün, **Registrieren** ist möglich.

**Erwartet:**

- Das eigene Konto kann den eigenen Antrag nicht freigeben.
- Eine Freigabe gilt 24 Stunden und für genau einen Durchlauf; danach steht sie auf **Verwendet**.
- Ablehnen braucht einen Grund. Die Übersicht zeigt offene Anträge unter „Handlungsbedarf“.
- Im Audit-Log: `approval.requested`, `approval.approved`, `approval.used`, `key.registered`.

### 5.14 Nacherfassen (ab V1.1)

Vorbereitung: eine Testperson registriert selbst einen YubiKey unter `https://mysignins.microsoft.com/security-info`, danach **Jetzt synchronisieren** im Tenant.

1. **Inventar** → **Nacherfassen** (oder über den Handlungsbedarf auf der Übersicht) → Person in der Liste wählen.
2. Den Key der Person einstecken (die PIN wird nicht gebraucht) → **Nacherfassen**.

**Erwartet:**

- Seriennummer und Firmware stehen danach im Inventar, der Eintrag verschwindet aus der Liste.
- Passt das Modell nicht zu dem, was Entra meldet: Warnung mit **Trotzdem zuordnen**.
- Lag derselbe Key schon im Lager (Wareneingang), werden beide Einträge zusammengeführt; Tags und Zusatzfelder bleiben erhalten.

### 5.15 ToDo-Liste (ab V2)

1. **ToDo-Liste** → **Person eintragen** → Person suchen, Ticket-Nummer eintragen → **Eintragen**.
2. Beim ToDo **Registrieren**: der Einzel-Wizard öffnet sich mit der Person schon gesucht. Registrierung durchführen.
3. Zurück in der ToDo-Liste: das ToDo steht auf **Erledigt** und verlinkt den Key.
4. Per API (Ticketsystem): unter **Globale Einstellungen** einen API-Schlüssel mit **ToDo-Liste schreiben** anlegen, dann:

```bash
curl -X POST https://<plattform>/api/v1/todos -H "X-Api-Key: ylm_…" -H "Content-Type: application/json" \
  -d '{"tenant":"contoso.onmicrosoft.com","userPrincipalName":"anna@contoso.com","ticketId":"INC-4711"}'
```

**Erwartet:** `201` für ein neues ToDo, `200` wenn für die Person schon ein offenes ToDo existiert, `422` bei unbekannter Person, `403` mit einem Schlüssel ohne Schreibrecht. Mehrere offene ToDos lassen sich auswählen und **als Stapel registrieren**.

---

### 5.16 Wer hat welchen Key (Inventar, Ansicht „Nach Person“)

1. Links unter **Kunde** auf **Inventar** klicken. Oben stehen die Kacheln zur Ausgabe (ausgegebene Keys, Personen, Geteilt/Breakglass, Auffälligkeiten) und darunter die Ansichten **Alle Keys** und **Nach Person**. Oben im Umschalter erst einen Tenant wählen, danach **Alle Tenants**.
2. **Nach Person** wählen. Erwartung: alle ausgegebenen Keys mit Person, Seriennummer, Modell und Datum. Geteilte Keys und Breakglass zeigen den Verwahrer. In **Alle Keys** steht der Verwahrer ebenfalls in der Spalte Person.
3. Im Suchfeld eine Person, einen UPN oder eine Seriennummer tippen. Die Liste filtert beim Tippen; Konten auf geteilten Keys werden mitgefunden.
4. Kachel **Auffälligkeiten** anklicken (auch aus **Alle Keys** heraus): die Ansicht wechselt zu **Nach Person**, es bleiben Keys ohne Konto, geteilte Keys ohne Verwahrer usw.
5. Über **Exportieren** „Wer hat welchen Key“ als Excel, PDF und CSV herunterladen sowie **Alle Keys (CSV)**. Erwartung: gleiche Auswahl wie in der Personenansicht, Excel mit Filterzeile, PDF im Querformat.
6. Alte Lesezeichen auf `/assignments` öffnen die Personenansicht des Inventars.

### 5.17 Erscheinungsbild der Dokumente (Whitelabeling)

1. **Globale Einstellungen** → Karte **Erscheinungsbild der Dokumente**: Firmenname, Akzentfarbe, Kontakt, Fußzeile eintragen und ein Logo (PNG/JPEG) hochladen. Die Vorschau rechts folgt den Eingaben sofort.
2. **Muster-PIN-Brief** herunterladen. Erwartung: Logo oben rechts, Farbe im Balken und PIN-Kasten, Kontakt beim Hinweis „Key gesperrt“, Fußzeile statt „Erstellt mit dem Yubikey Lifecycle Manager“.
3. In den **Tenant-Einstellungen** eines Tenants nur den Firmennamen ändern. Erwartung: alle anderen Werte kommen weiter vom Partner.
4. Einen Key registrieren bzw. den Bericht (PDF) und die Key-Zuweisungen (PDF) dieses Tenants herunterladen: das Tenant-Erscheinungsbild wird verwendet. Über alle Tenants gilt das des Partners.

### 5.18 Passkey-Rollout (MFA-Stand, TAP, andere Methoden entfernen)

Voraussetzung: Tenant gewählt. Rolle Owner oder Operator (TAP erzeugen und Methoden entfernen); Auditor sieht nur die Übersicht. Am besten mit einer Testperson, deren Methoden man gefahrlos ändern kann.

1. Links unter **Kunde** auf **Passkey-Rollout** klicken. Erwartung: Kopf mit „x von y Personen sind am Ziel“, Ringdiagramm und vier Stufen **Keine MFA**, **Nur klassische MFA**, **Anderer Passkey**, **Sicherheitsschlüssel**. Unter dem Titel steht, ob die Daten aus dem Entra-Registrierungsbericht kommen (Entra ID P1) oder pro Benutzer abgefragt wurden.
2. Stufe **Keine MFA** anklicken: die Liste zeigt nur diese Personen. Schalter **Nur Zielgruppe**, **Gäste**, **Deaktivierte Konten** ändern Zahlen und Liste.
3. In **Andere Methoden im Einsatz** z. B. „SMS / Anruf (Mobil)“ anklicken: die Liste filtert auf Personen mit SMS.
4. Eine Person anklicken. Erwartung: Methoden live aus Entra mit **Entfernen** (nicht bei Kennwort und Passkeys auf Sicherheitsschlüsseln). Bei der Testperson eine Methode entfernen, im Entra Admin Center prüfen, dass sie weg ist. Audit-Log: `authmethod.removed` mit gekürzter Telefonnummer.
5. Ist der befristete Zugriffspass im Tenant aus, erscheint ein Hinweis mit **Jetzt aktivieren** (nur mit „Tenants verwalten“). Aktivieren und im Admin Center prüfen.
6. Testperson auswählen (Haken) → unten **TAP erzeugen** → Gültigkeit wählen → **Erzeugen**. Erwartung: Code wird einmal angezeigt (kopieren, bei mehreren als CSV). Mit dem TAP auf https://aka.ms/mysecurityinfo anmelden. Audit-Log: `authmethod.tap_created` ohne Code.
7. Mehrere Personen auswählen → **Methoden entfernen** → „SMS / Anruf“. Mit **Nur bei Personen, die danach noch einen Passkey haben** werden Personen ohne Passkey übersprungen; ohne diesen Schutz muss `ENTFERNEN` eingetippt werden.
8. **Exportieren** → Excel/CSV: gleiche Auswahl wie die Stufen- und Schalterfilter.

## 6. Benachrichtigungen (ab V2)

Alles unter **Benachrichtigungen** (Partnerebene, Recht „API und Integrationen“; früher „Integrationen“).

### 6.1 Webhooks

1. Auf `https://webhook.site` eine Test-URL holen.
2. **Webhook anlegen** → Name, URL, Ereignisse (z. B. „Key registriert“, „ToDo erledigt“). Das angezeigte Geheimnis notieren; es erscheint nur einmal.
3. **Testen**, danach eine echte Aktion auslösen (z. B. Key sperren).

**Erwartet:** Ein `POST` mit JSON und den Headern `X-YLM-Event`, `X-YLM-Delivery`, `X-YLM-Timestamp` und `X-YLM-Signature: sha256=…` (HMAC-SHA256 über `Timestamp + "." + Body` mit dem Geheimnis). Im **Protokoll** steht jede Zustellung; Fehlschläge werden nach 1 und 5 Minuten, 30 Minuten, 2 und 6 Stunden wiederholt.

### 6.2 Mail-Alarme

Voraussetzung: Mailserver ist im Reiter **Mail-Berichte** eingetragen (siehe 6.3).

1. Reiter **Mail-Alarme** → **Mail-Alarm anlegen** → Name, Empfänger (mehrere mit Komma), unter **Nur ausgewählte** z. B. „Key gesperrt“ und „Freigabe beantragt“ anhaken → **Speichern**.
2. **Testen**: eine Testmail kommt an.
3. Einen Key sperren, danach eine Person in die ToDo-Liste eintragen.

**Erwartet:** Für das Sperren kommt innerhalb einer Minute eine Mail mit Betreff „[YubiKey] Key gesperrt: …“ und Tenant, Seriennummer und Auslöser im Text. Für das ToDo kommt keine Mail, weil es nicht ausgewählt ist. Im **Protokoll** des Alarms steht nur die Mail zum Sperren.

### 6.3 Mail-Berichte

1. Reiter **Mail-Berichte** → **SMTP** eintragen (Server, Port, STARTTLS/SSL, Absender, optional Anmeldung) → **Testmail senden**.
2. **Bericht anlegen**: Empfänger, täglich/wöchentlich/monatlich, Uhrzeit, Tenants, optional mit CSV → **Jetzt senden**.

**Erwartet:** Mail mit Kennzahlen im Text und dem Dashboard-PDF im Anhang (plus Keys-CSV, wenn gewählt). „nächster Versand“ stimmt mit der eingestellten Uhrzeit (Zeitzone Europe/Berlin) überein.

---

## 7. Optionale Prüfung der Anmeldemethode (Abschlusstest)

1. Für das **Owner-Konto** selbst einen YubiKey/Passkey registrieren (z. B. über `https://mysignins.microsoft.com/security-info`).
2. In `.env` die Zeile `YLM_REQUIRE_PHISHING_RESISTANT=true` ergänzen und `docker compose up -d server`.
3. Abmelden und neu anmelden.

**Erwartet:**

- Anmeldung mit **Passwort** (ohne Key) wird von der Plattform abgelehnt; die Anmeldeseite nennt die gemeldete Methode und bietet „Erneut mit YubiKey anmelden“.
- Anmeldung mit dem **YubiKey** klappt.
- Danach die Zeile wieder entfernen (oder auf `false` setzen) und `docker compose up -d server`.

---

## Fehler zurückmelden

Bitte bei jeder Abweichung zurückmelden:

1. **Schritt-Nummer** aus dieser Anleitung (z. B. „4.5“) und was du erwartet hast
2. **Screenshot** der Fehlermeldung in der Oberfläche bzw. im Tray
3. **Uhrzeit** des Fehlers (damit wir in den Logs die richtige Stelle finden)
4. Die passenden **Logs** aus der Tabelle

| Bereich | Wie bekomme ich das Log? |
|---|---|
| Server | `docker compose logs server --since 30m > server.log` (im Installationsordner) |
| Datenbank (nur bei Startproblemen) | `docker compose logs db --since 30m > db.log` |
| Web-Oberfläche | Browser F12 → Netzwerk → die rote Anfrage anklicken → Registerkarte „Antwort“ (enthält `code` und `message`) → kopieren oder Screenshot. Zusätzlich die Registerkarte „Konsole“. |
| Agent-Dienst | Ordner `C:\ProgramData\YubikeyLifecycleManager\Agent\logs` (Tray → „Logs öffnen“), die neueste Datei |
| Windows-Ereignisanzeige | Windows-Protokolle → Anwendung, Quelle „Yubikey Lifecycle Manager Agent“ (bei Dienst-Abstürzen auch „.NET Runtime“ und „Application Error“) |
| MSI-Installation | `msiexec /i … /l*v "%TEMP%\ylm-msi.log"` und die Datei mitschicken |
| Setup-Assistent | die rote Meldung im Assistenten (Screenshot), bei `AADSTS…` den Code; beim Ausweichweg die komplette Skriptausgabe |
| Entra | Entra Admin Center → Anmeldeprotokolle (Anmeldefehler, Fehlercode `AADSTS…`) bzw. Überwachung → Überwachungsprotokolle (Richtlinienänderungen) |
| Plattform | Audit-Log der Plattform (Screenshot der betreffenden Einträge) |

Außerdem hilfreich: Commit (`git log -1 --oneline`), Version der Tray-App (steht im Tray-Menü), Windows-Version (`winver`), YubiKey-Modell und Firmware.

**Bitte nicht schicken:** Setup-Token, PINs (im PIN-Brief schwärzen), Inhalt von `.env`, Dateien aus dem Docker-Volume `data`. UPNs und Tenant-IDs eines Test-Tenants sind unkritisch, dürfen aber gern geschwärzt werden.

### Bekannte Fehlerbilder

| Nr. | Symptom | Erste Hilfe |
|---|---|---|
| F1 | Setup-Test: „Fehlende Berechtigungen …“ | Haken in den API-Berechtigungen prüfen (2.3). 1 bis 2 Minuten warten, erneut testen. |
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
Version:           ____________
Variante:          A (localhost) / B (HTTPS)
Windows-Version:   ____________
YubiKey 1:         Modell ________  FW ______  SN ________
YubiKey 2:         Modell ________  FW ______  SN ________

[ ] 1.1  Skript fertig, Container healthy, Setup-Token angezeigt
[ ] 1.3  Sicherung erstellt
[ ] 2.2  Anmeldung per Code, App automatisch angelegt, Prüfung grün
[ ] 2.3  Owner vorausgefüllt, Heim-Tenant-Haken gesetzt
[ ] 2.4  (nur falls nötig) Skript als Ausweichweg
[ ] 2.5  Anmeldung als Owner, Heim-Tenant unter Tenants vorhanden
[ ] 3.2  Consent-Ablauf ohne Duplikat
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
[ ] 5.11 Stapelregistrierung
[ ] 5.12 Geteilter Key: Verwahrer, mehrere Konten, ein PIN-Brief
[ ] 5.13 Breakglass mit Vier-Augen-Freigabe
[ ] 5.14 Nacherfassen
[ ] 5.15 ToDo-Liste (Oberfläche und API)
[ ] 6.1  Webhooks mit Signatur
[ ] 6.2  Mail-Alarm nur für ausgewählte Ereignisse
[ ] 6.3  Mail-Bericht mit PDF
[ ] 7    Anmeldung nur mit YubiKey erzwungen

Abweichungen:
- Schritt __: ...
```
