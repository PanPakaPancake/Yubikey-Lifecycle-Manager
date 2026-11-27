# Yubikey-Lifecycle-Manager

Selbst hostbare Plattform, mit der IT-Administratoren allein oder im Team den Lebenszyklus von YubiKeys in **Microsoft Entra ID** verwalten.

## Funktionen

- **Tenant-On- und -Offboarding** per Assistent, mit automatischen Checks der FIDO2-Richtlinie und „Jetzt beheben“
- **Inventar:** wer hat welchen Key (Seriennummer, Firmware, Registrierdatum), Lagerbestand, Lebenszyklus, freie Felder und Tags
- **Registrierung on Behalf:** einzeln, massenweise oder als geteilter Key, gesteuert über konfigurierbare Profile (PIN-Strategie, erzwungener PIN-Wechsel, Firmware-Vorgaben)
- **PIN-PDF** mit grafischer Anleitung für den Nutzer
- **Reporting** mit CSV/PDF-Export
- **REST-API** für externe Tools wie Ticketsysteme oder RMM

## Aufbau

| Komponente | Aufgabe |
|---|---|
| Backend + WebGUI | Bedienung, Datenhaltung, Kommunikation mit Microsoft Graph. Selbst hostbar (Docker). |
| Windows-Agent + Tray-App | Zugriff auf den lokal eingesteckten YubiKey (PIN, Registrierung, Auslesen). Status in der Taskleiste. |

Ein reiner Browser kann die Registrierung on Behalf nicht durchführen. Die Begründung steht in der Planung.

## Installation

Lokal auf einem Windows-PC mit Docker Desktop (PowerShell):

```powershell
irm https://raw.githubusercontent.com/PanPakaPancake/Yubikey-Lifecycle-Manager/main/install.ps1 | iex
```

Auf einem Linux-Server im internen Netz oder hinter ZTNA, mit HTTPS über interne CA, eigenes Zertifikat oder vorhandenen Reverse Proxy:

```bash
curl -fsSL https://raw.githubusercontent.com/PanPakaPancake/Yubikey-Lifecycle-Manager/main/install.sh | sudo bash
```

Das Skript erzeugt die `.env` mit Zufallsgeheimnissen, lädt die fertigen Images, richtet die tägliche, verschlüsselte Datensicherung ein und zeigt am Ende Passphrase und Setup-Token. Die Plattform ist nicht dafür gedacht, offen im Internet zu stehen. Details zu den Varianten, Reverse Proxy/ZTNA, Sicherung und Umzug: [Betrieb und Installation](docs/BETRIEB.md).

## Dokumentation

- [Projektplanung](docs/PLANUNG.md)
- [Betrieb und Installation](docs/BETRIEB.md)
- [Testanleitung V1 (Praxistest mit Tenant und YubiKey)](docs/TESTANLEITUNG-V1.md)
- [Signierung des Agents (SignPath)](docs/SIGNIERUNG.md)

## Projektstruktur

| Pfad | Inhalt |
|---|---|
| `src/Ylm.Server` | ASP.NET-Core-Backend: Einrichtung, Tenants, Inventar, Wizard, PDFs, Reporting, REST-API mit Rechten pro Schlüssel, SignalR-Hub für Agents |
| `src/Ylm.Core` | Fachlogik ohne Abhängigkeiten: Rollen, Lebenszyklus, Profile, Firmware-Hinweise, Richtlinien-Prüfungen, Audit-Hash-Kette |
| `src/Ylm.Agent`, `src/Ylm.Agent.Core` | Windows-Dienst mit Yubico .NET SDK |
| `src/Ylm.Agent.Tray` | Tray-App (WinForms) |
| `web` | Web-Oberfläche (React, TypeScript, Vite) |
| `installer` | MSI für Agent und Tray-App (WiX) |
| `tests` | Unit-Tests und End-to-End-Tests mit simuliertem Key und simuliertem Graph |

## Status

**V1 umgesetzt (Version 0.1.0)**: Einrichtung, Tenant-On-/Offboarding mit Prüfungen und „Jetzt beheben“, Rollen und Zuständigkeiten, Audit-Log mit Hash-Kette, Entra-Sync, Inventar (Lager, Lebenszyklus, Zusatzfelder, Tags, CSV-Import, Wareneingang per Einstecken), Profile mit Firmware-Prüfung, Einzel-Wizard, PIN-PDF, Dashboard mit CSV/PDF-Export, Lese-API.

**V1.1 umgesetzt**: Stapelregistrierung (Liste aus Entra-Gruppe, CSV oder Suche; Keys der Reihe nach einstecken, optional feste Seriennummer pro Person; alle PIN-Briefe als ein PDF), geteilter Key und Breakglass (mehrere Konten auf einem Key, Pflichtfeld Verwahrer, ein PIN-Brief an den Verwahrer), Nacherfassen selbst registrierter Keys per Einstecken.

**V2 umgesetzt**: ToDo-Liste (von Hand oder per `POST /api/v1/todos` aus dem Ticketsystem, wird mit der Registrierung erledigt), Vier-Augen-Freigabe für Breakglass (pro Tenant), signierte Webhooks und Mail-Alarme mit frei wählbaren Ereignissen, Mail-Berichte per SMTP mit PDF/CSV. NinjaRMM bekommt Ereignisse über die Webhooks (keine eigene Anbindung, Entscheidung 2026-11-19).

**Inventar mit „Wer hat welchen Key“**: alle Keys oder nach Person, pro Tenant oder über alle Tenants, mit Verwahrern geteilter Keys und Auffälligkeiten; Export als Excel, CSV und PDF; in der Lese-API unter `GET /api/v1/assignments`.

**Whitelabeling**: PIN-Briefe, Berichte und PDF-Exporte mit eigenem Firmennamen, Logo, Farbe, Kontakt und Fußzeile; Vorgabe in den globalen Einstellungen, pro Tenant einzeln überschreibbar.

Automatisch getestet sind die Fachlogik und der komplette Ablauf im Backend mit simuliertem YubiKey und simuliertem Microsoft Graph.

**Noch nicht getestet:**

- echte YubiKeys mit dem Yubico SDK (Auslesen, Reset, PIN setzen, Registrierung, PIN-Wechsel erzwingen)
- ein echter Entra-Tenant: Anmeldung, Admin-Consent, Graph-Aufrufe, insbesondere die Registrierung on Behalf über die Beta-Schnittstelle (`fido2Methods/creationOptions`)
- die automatische App-Registrierung im Setup-Assistenten (Anmeldung per Code) und das App-Registrierungs-Skript als Ausweichweg
- MSI-Installation, Windows-Dienst und Tray-App auf Windows (das MSI wird in der CI gebaut, aber nicht installiert)

Die Liste der YubiKey-AAGUIDs (`src/Ylm.Core/YubicoAaguids.cs`) sollte vor dem produktiven Einsatz mit der von Yubico veröffentlichten Liste abgeglichen werden.

## Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org)

Signiert werden nur die Dateien des Windows-Agents aus den [Releases](https://github.com/PanPakaPancake/Yubikey-Lifecycle-Manager/releases): `YlmAgent.exe`, `YlmTray.exe` und `YubikeyLifecycleManager-Agent.msi`. Sie entstehen ausschließlich in GitHub Actions aus dem Quellcode dieses Repositorys (`.github/workflows/release.yml`); jede Signierung wird von Hand freigegeben. Einrichtung und Ablauf: [Signierung](docs/SIGNIERUNG.md).

Rollen:

- Committers and reviewers: [PanPakaPancake](https://github.com/PanPakaPancake)
- Approvers: [PanPakaPancake](https://github.com/PanPakaPancake)

Änderungen von außen kommen nur per Pull Request und werden vor dem Merge geprüft. Für GitHub und SignPath ist Mehr-Faktor-Anmeldung Pflicht.

Datenschutz: This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it. Der Agent spricht nur mit dem Server, mit dem er gekoppelt wurde. Der Server spricht mit den Microsoft-Entra-Tenants, Mail-Servern, Webhooks und Ticketsystemen, die der Betreiber einrichtet, und fragt täglich bei der GitHub-API nach neuen Versionen (abschaltbar mit `Ylm__UpdateCheckEnabled=false`, siehe [Betrieb](docs/BETRIEB.md)).

## Lizenz

Copyright (C) 2026 PanPakaPancake und Mitwirkende

Der Yubikey-Lifecycle-Manager steht unter der [GNU Affero General Public License v3.0](LICENSE) (AGPL-3.0-only). Er darf frei genutzt, verändert und weitergegeben werden. Wer eine veränderte Fassung weitergibt oder anderen über das Netz zur Nutzung bereitstellt (z. B. als Dienstleister für Kunden), muss deren Quellcode unter derselben Lizenz zugänglich machen.

## Lizenzen von Abhängigkeiten

PDFsharp ist bewusst auf Version 6.2.4 (MIT) festgelegt; neuere Versionen stehen unter einer anderen Lizenz.
