# Projektplanung: Yubikey Lifecycle Manager

Stand: 15.11.2026 · Status: Planung abgestimmt, Umsetzung noch nicht begonnen

## 1. Ziel

Eine selbst hostbare Plattform, mit der IT-Administratoren allein oder im Team den gesamten Lebenszyklus von YubiKeys übersichtlich betreuen: Anbindung von Entra-Umgebungen, Inventar, Registrierung on Behalf (einzeln, massenweise, geteilt), Reporting und eine API für externe Tools.

Erste und vorerst einzige Integration: **Microsoft Entra ID**.

## 2. Architektur

### 2.1 Warum keine reine Website

Eine reine Weboberfläche kann die Registrierung on Behalf nicht leisten:

| Aufgabe | Blocker im Browser |
|---|---|
| Passkey für den Nutzer anlegen | Graph liefert `creationOptions` mit RP-ID `login.microsoft.com`. WebAuthn im Browser erlaubt nur die RP-ID der eigenen Domain. |
| PIN setzen, PIN-Wechsel erzwingen, Reset | Dafür gibt es keine Web-API. |
| Seriennummer und Firmware auslesen | Chrome und Firefox sperren FIDO-Geräte für WebHID und WebUSB. |

Quellen: [Graph creationOptions](https://learn.microsoft.com/en-us/graph/api/fido2authenticationmethod-creationoptions), [Chromium: Security-Key-Sperre für WebHID](https://chromium.googlesource.com/chromium/src/+/3484e4660b9459892f0287eeb9295bdaa0260668), [Yubico zu WebUSB](https://www.yubico.com/blog/webusb-and-responsible-disclosure)

### 2.2 Entscheidung: WebGUI + Backend + lokaler Windows-Agent

```
 Admin-Browser ──HTTPS──▶  Backend (selbst gehostet, Docker)  ──▶ Microsoft Graph (je Kunden-Tenant)
                              ▲            │
                              │ ausgehende │ PostgreSQL, Secret-Store
                              │ Verbindung │
                     ┌────────┴────────┐
                     │ Windows-Agent   │  (Dienst, Adminrechte)
                     │  + Tray-App     │  (pro Benutzer, Status)
                     └────────┬────────┘
                              │ USB (CTAP2 / CCID)
                          YubiKey
```

- **Backend und WebGUI** enthalten die gesamte Bedienung, Daten und Graph-Kommunikation.
- Den **Agenten** brauchen nur Admins, die Keys registrieren oder auslesen. Für Inventar und Reports reicht der Browser.
- Der Agent baut eine **ausgehende Verbindung zum Backend** auf (z. B. WebSocket/SignalR) und meldet sich dabei als der jeweilige Admin an. Die Website spricht nie direkt mit `localhost`. Damit gibt es keine Probleme mit Browser-Sperren für lokale Netzwerkzugriffe und keine lokalen Zertifikate, und es funktioniert auch über RDP- und Jump-Hosts.

### 2.3 Windows-Agent im Detail

- **Plattform:** nur Windows-Clients.
- **Installation:** Selbstinstallation durch den Admin, einmalig mit Adminrechten (MSI).
- **Dienst:** Windows erlaubt direkten FIDO-Zugriff (PIN setzen, `forcePINChange`) nur mit erhöhten Rechten. Der Agent läuft deshalb als **Windows-Dienst**, im Alltag gibt es keine UAC-Abfragen. Seriennummer und Firmware ließen sich auch ohne Adminrechte über die CCID-Schnittstelle lesen.
- **Tray-App:** Ein Windows-Dienst kann selbst keine Oberfläche anzeigen. Dazu kommt eine kleine **Tray-App**, die pro Benutzer automatisch startet und über eine Named Pipe mit dem Dienst spricht. Sie zeigt:
  - den Verbindungsstatus zum Backend (verbunden, getrennt, nicht angemeldet) und die Instanz
  - den erkannten Key (Modell, Seriennummer, Firmware, kompatibel ja/nein)
  - den laufenden Vorgang mit Fortschritt (z. B. „Registrierung 4/20“) und Aufforderungen wie „Key berühren“ oder „Key ab- und wieder anstecken“
  - Windows-Benachrichtigungen bei Erfolg oder Fehler
  - Menüpunkte: WebGUI öffnen, an- und abmelden, Logs öffnen, Version und Update-Hinweis
- **Bibliothek:** [Yubico .NET SDK](https://docs.yubico.com/yesdk/)

### 2.4 Tech-Stack

| Bereich | Wahl |
|---|---|
| Backend | .NET (ASP.NET Core), Microsoft Graph SDK |
| Frontend | React + TypeScript |
| Datenbank | PostgreSQL |
| Agent und Tray | .NET (Windows-Dienst + WinForms/WPF-Tray), Yubico .NET SDK |
| Secrets/Zertifikate | austauschbar: Dateisystem/DPAPI, HashiCorp Vault, Azure Key Vault |
| Betrieb | **selbst hostbar** im eigenen RZ, Auslieferung als Docker-Images / Compose |

## 3. Mandanten, Login und Rollen

- **Mandantenfähig:** Eine Organisation in der Plattform verwaltet mehrere Entra-Tenants.
- **Login:** per Entra-SSO, passwortlos mit YubiKey.
- **Rollen:**

| Rolle | Rechte |
|---|---|
| Owner | Tenants an- und abbinden, Profile verwalten, Rollen vergeben |
| Operator | Keys registrieren, sperren, zurücksetzen (benötigt Agent) |
| Helpdesk | Inventar lesen, Keys sperren, keine Neuregistrierung |
| Auditor | nur lesen, Reports |

- **Zuständigkeiten pro Tenant:** Admins lassen sich auf einzelne Tenants beschränken.
- **Audit-Log** ist fest eingebaut: wer hat wann was mit welchem Key gemacht.

## 4. Entra-Anbindung (Tenant-On- und -Offboarding)

### 4.1 Assistent

1. Ein Owner wählt „Tenant hinzufügen“ und meldet sich als Global Admin oder Privileged Role Admin des Ziel-Tenants an.
2. Er erteilt der **mandantenfähigen App** der Installation Admin-Consent. Weil selbst gehostet wird, registriert jede Installation bei der Ersteinrichtung ihre eigene App in ihrem Heim-Tenant. Der Setup-Assistent führt durch diesen Schritt.
3. Danach laufen automatische **Tenant-Checks**:
   - Ist die Passkey-/FIDO2-Methode aktiviert?
   - Sind die YubiKey-AAGUIDs erlaubt?
   - Ist die Zielgruppe für die Methode freigeschaltet?
4. Für jede gefundene Lücke gibt es einen Button **„Jetzt beheben“**. Daneben steht eine Anleitung für alle, die es lieber von Hand umsetzen.

**Offboarding:** Das Service Principal wird aus dem Tenant entfernt. Die Daten werden nach einer Aufbewahrungsfrist gelöscht oder vorher exportiert.

### 4.2 Zugriff

- Die Plattform schreibt mit **eigener App-Identität** (Anwendungsberechtigungen, Zertifikat) in Graph.
- Die feine Kontrolle übernehmen die Plattform-Rollen und das Audit-Log.

| Graph-Berechtigung | Zweck |
|---|---|
| `UserAuthenticationMethod.ReadWrite.All` | Keys on Behalf registrieren, auflisten, löschen |
| `User.Read.All`, `GroupMember.Read.All` | Personen und Gruppen suchen |
| `Policy.Read.All`, `Policy.ReadWrite.AuthenticationMethod` | FIDO2-Richtlinie prüfen und beheben |
| `AuditLog.Read.All` | Registrierungs- und Nutzungsdaten fürs Reporting |

## 5. Inventar

### 5.1 Datenquellen

- **Über die Plattform registriert:** vollständige Daten (Seriennummer, Firmware, Modell, Profil, registriert von, Zeitpunkt).
- **Vom Nutzer selbst registriert:** werden per Sync aus Entra übernommen, aber ohne Seriennummer und Firmware, weil Entra diese nicht kennt. Diese Keys werden markiert, und es gibt ein **Nacherfassen**: Key einstecken, der Agent liest ihn aus und ordnet ihn zu.

### 5.2 Lebenszyklus

`Im Lager → Zugewiesen/Aktiv → Gesperrt (verloren/defekt) → Ausgemustert`

Jeder Statuswechsel landet im Audit-Log.

### 5.3 Weitere Festlegungen

- **Lagerbestand:** Auch noch nicht zugewiesene Keys werden erfasst (Wareneingang per Einstecken oder Import).
- **Frei definierbare Felder pro Tenant** (z. B. Standort, Kostenstelle, Bestellnummer) und **Tags**.
- **Sync mit Entra:** im Intervall, zusätzlich per Button „Jetzt synchronisieren“.
- **Ein Key, mehrere Registrierungen:** Die Regel ist ein Key pro Person. Mehrfachnutzung ist trotzdem möglich (Breakglass-Konten, ein geteilter Key für mehrere Produktionsmitarbeiter). Dafür gibt es ein Feld **Verwahrer**, z. B. Abteilungsleiter. Ausgabe und Rückgabe regelt der Kunde selbst.
- **Keine Mindestbestand-Warnung.** Das wird über andere Tools gelöst.

## 6. Registrierungsprofile und Wizards

### 6.1 Profil

| Einstellung | Optionen |
|---|---|
| PIN-Strategie | zufällige PIN pro Key (**Standard**) oder feste Standard-PIN, Mindestlänge |
| PIN-Wechsel erzwingen | ja (**Standard**), umgesetzt über CTAP 2.1 `forcePINChange`, ab Firmware 5.7 |
| Werksreset vor Registrierung | ja/nein (Key muss dafür kurz ab- und wieder angesteckt werden) |
| Anzeigename in Entra | Vorlage, z. B. `YubiKey {Seriennummer}` |
| Erlaubte Modelle/Firmware | z. B. „nur YubiKey 5 ab Firmware 5.7“ |

**Inkompatibler Key:** Erfüllt ein eingesteckter Key das Profil nicht (z. B. Firmware < 5.7 und damit kein erzwungener PIN-Wechsel), zeigen Wizard und Tray einen Hinweistext. Die Profileinstellung muss dann **für diesen Key manuell bestätigt überschrieben** werden. Das wird im Audit-Log festgehalten, und im PIN-PDF steht „bitte PIN selbst ändern“.

Quelle zur Firmware: [YubiKey 5.7 Firmware Specifics](https://docs.yubico.com/hardware/yubikey/yk-tech-manual/yk5-firmware-5.7.html)

### 6.2 PIN-Übergabe

- PINs werden **nie gespeichert**.
- Für den Nutzer wird ein **PDF** erzeugt, mit der PIN und einer **leicht verständlichen, grafischen Anleitung** zur Verwendung.

### 6.3 Wizards

1. **Einzeln:** Person suchen → Key einstecken → Profil wählen → Registrierung → PIN-PDF.
2. **Massenweise:** Personen per Gruppe oder CSV auswählen. Danach läuft der Wizard als Schleife (Key rein, registrieren, Key raus, nächster). Am Ende gibt es eine Ergebnisübersicht mit Fehlern und ein Sammel-PDF.
3. **Geteilter Key:** mehrere Konten auf einem Key, Pflichtfeld Verwahrer, Anzeige der freien Passkey-Plätze (je nach Firmware 25 bis 100).

**Vier-Augen-Freigabe** für Breakglass-Registrierungen: standardmäßig aus, pro Tenant aktivierbar.

## 7. Reporting

- Dashboard pro Tenant und übergreifend für die Organisation:
  - Keys nach Status, Lagerbestand, Ausgaben pro Zeitraum
  - Abdeckung: Personen der Zielgruppe mit und ohne Key
  - Firmware-Verteilung mit Warnung bei bekannten Schwachstellen (z. B. EUCLEAK, Firmware < 5.7)
  - Keys ohne Nutzung seit X Tagen (aus den Entra-Anmeldeprotokollen)
- **V1:** Ansicht im Web, Export als CSV/PDF.
- **Später:** geplante Berichte per E-Mail.

## 8. Externe API

- **V1:** REST-API mit OpenAPI-Dokumentation, **nur lesend**. API-Schlüssel werden plattformweit verwaltet und gelten für alle oder ausgewählte Tenants (Änderung vom 16.11.2026).
- **Bewusst nicht vorgesehen:** Statusänderungen über die API (z. B. Key sperren).
- **Später:**
  - Webhooks für Ereignisse (Key registriert, gesperrt, …)
  - Integration **NinjaRMM**
  - ToDo-Liste: Ein Ticketsystem kann Nutzer, die einen Key bekommen sollen, in eine Registrierungs-Warteschlange pushen. Das ist die einzige schreibende API-Funktion.

## 9. Roadmap

### V1 (MVP)
- Installations-Setup inkl. eigener App-Registrierung
- Tenant-Onboarding und -Offboarding mit Checks und „Jetzt beheben“
- Login, Rollen, Zuständigkeiten pro Tenant, Audit-Log
- Entra-Sync und Inventar mit Lager, Lebenszyklus, freien Feldern und Tags
- Registrierungsprofile inkl. Firmware-Prüfung und bestätigtem Überschreiben
- Windows-Agent (Dienst) und Tray-App
- Einzel-Wizard mit PIN-PDF
- Dashboard mit CSV/PDF-Export
- Lese-REST-API

### V1.1
- Massen-Wizard
- Geteilter Key mit Verwahrer
- Nacherfassen selbst registrierter Keys

### V2
- Mail-Berichte, Webhooks
- NinjaRMM-Integration
- ToDo-Liste aus dem Ticketsystem
- Vier-Augen-Freigabe pro Tenant

## 10. Entscheidungen aus der Umsetzung (V1)

Die offenen Punkte der Planung sind in der V1 so gelöst:

- **Kopplung des Agents:** per Kopplungscode. Der Admin erzeugt in der Web-Oberfläche (Seite „Agent“) einen Code, der 10 Minuten gilt und nur einmal verwendbar ist, und gibt ihn mit der Plattform-Adresse in der Tray-App ein. Der Agent erhält dafür ein eigenes Token, das mit DPAPI (LocalMachine) gespeichert wird. Ein Agent gehört immer zu genau einem Admin-Konto und kann in der Web-Oberfläche entkoppelt werden.
- **Aktualisierung von Agent und Tray:** über das MSI (Major Upgrade). Verteilung per Softwareverteilung/Intune oder manuell; kein Auto-Update in V1.
- **Aufbewahrung beim Offboarding:** Daten bleiben 30 Tage erhalten (einstellbar über `Ylm__OffboardingRetentionDays`) und werden danach automatisch gelöscht. Vorher ist ein Export (ZIP mit CSVs) möglich. Der Audit-Eintrag zum Löschen bleibt, die Hash-Kette bleibt prüfbar.
- **PIN-PDF:** eine A4-Seite mit PIN, Key-Daten und vier illustrierten Schritten zur ersten Anmeldung. Ist der PIN-Wechsel nicht erzwingbar (Firmware unter 5.7), enthält das PDF einen deutlichen Hinweis, die PIN selbst zu ändern.
