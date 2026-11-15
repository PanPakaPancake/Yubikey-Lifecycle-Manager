# Yubikey-Lifecycle-Manager

Selbst hostbare Plattform, mit der IT-Administratoren allein oder im Team den Lebenszyklus von YubiKeys in **Microsoft Entra ID** verwalten.

## Funktionen (geplant)

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

## Dokumentation

- [Projektplanung](docs/PLANUNG.md)

## Status

Planung abgestimmt, Umsetzung noch nicht begonnen.
