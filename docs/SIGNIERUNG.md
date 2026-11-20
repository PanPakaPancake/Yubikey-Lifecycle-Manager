# Agent-MSI signieren (Azure Artifact Signing)

Ohne Signatur warnt Windows SmartScreen beim Installieren des Agents, und Virenscanner sind misstrauischer.
Der Release-Build signiert `YlmAgent.exe`, `YlmTray.exe` und das MSI mit **Azure Artifact Signing** (früher
„Trusted Signing“), sobald die Zugangsdaten im Repository hinterlegt sind. Bis dahin entsteht wie bisher ein
unsigniertes MSI, es bricht nichts.

Kosten: Tarif Basic etwa 10 US-Dollar im Monat (5.000 Signaturen). Ein eigenes Zertifikat, ein Hardware-Token oder
ein Passwort im Repository sind nicht nötig. GitHub meldet sich per OIDC bei Azure an.

## Was der Agent prüft

- Ist der laufende Agent signiert, installiert er nur Updates mit **gültiger Signatur vom selben Unterzeichner**
  (Name im Zertifikat; die Zertifikate von Artifact Signing wechseln täglich, der Name bleibt). Sonst bricht das
  Update ab und das MSI wird gelöscht. Die Prüfsumme vom Server wird wie bisher zusätzlich geprüft.
- Ein noch unsignierter Agent nimmt das erste signierte Update an. Ab dann gilt die Regel oben.
- Optional in `appsettings.json` im Programmordner des Agents (oder als Umgebungsvariable `Agent__...`):
  - `Agent:RequireSignedUpdates=true` lehnt unsignierte Updates auch bei einem unsignierten Agent ab.
  - `Agent:TrustedSigner="CN=..., O=..., C=DE"` erlaubt einen bestimmten Unterzeichner, z. B. beim Wechsel des
    Firmennamens im Zertifikat.

## Einrichtung Schritt für Schritt

Voraussetzung: eine Azure-Subscription und eine Firma (für Public Trust prüft Microsoft die Identität einer
Organisation; aus Deutschland ist das für Firmen möglich, nicht für Einzelpersonen).

1. **Ressourcenanbieter registrieren**: Azure-Portal → Subscription → *Ressourcenanbieter* → `Microsoft.CodeSigning`
   → *Registrieren*.
2. **Artifact-Signing-Konto anlegen**: Portal → „Artifact Signing Accounts“ (bzw. „Trusted Signing Accounts“) →
   *Erstellen*. Region z. B. *West Europe*, Tarif *Basic*. Den **Konto-URI** notieren, z. B.
   `https://weu.codesigning.azure.net/`, und den Kontonamen.
3. **Rolle für die Identitätsprüfung**: Am Konto → *Zugriffssteuerung (IAM)* → dir selbst die Rolle
   *Artifact Signing Identity Verifier* geben.
4. **Identität prüfen lassen**: Am Konto → *Identity validations* → *New* → *Public* → *Organization*. Firmendaten
   wie im Handelsregister eintragen. Microsoft prüft das, meist innerhalb weniger Werktage; eventuell kommen
   Rückfragen per Mail. Weiter erst, wenn der Status *Completed* ist.
5. **Zertifikatprofil anlegen**: Am Konto → *Certificate profiles* → *Create* → *Public Trust*, die Identitätsprüfung
   aus Schritt 4 auswählen. Den **Profilnamen** notieren, z. B. `ylm-agent`.
6. **App-Registrierung für GitHub** in Entra: *App-Registrierungen* → *Neue Registrierung*, Name z. B.
   `YLM GitHub Signing`. **Anwendungs-ID (Client-ID)** und **Verzeichnis-ID (Tenant-ID)** notieren.
   Dann *Zertifikate & Geheimnisse* → *Verbundanmeldeinformationen* → *Hinzufügen* → *GitHub Actions*:
   - Organisation: `PanPakaPancake`, Repository: `Yubikey-Lifecycle-Manager`
   - Entitätstyp: *Environment*, Name: `release`
   Ein Client-Geheimnis wird nicht gebraucht.
7. **Signierrecht vergeben**: Am Zertifikatprofil (oder am Konto) → *Zugriffssteuerung (IAM)* → der App aus
   Schritt 6 die Rolle *Artifact Signing Certificate Profile Signer* geben.
8. **In GitHub hinterlegen**: Repository → *Settings* → *Secrets and variables* → *Actions*.
   - Reiter *Secrets*: `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` (Subscription-ID aus dem Portal).
   - Reiter *Variables*: `SIGNING_ENDPOINT` (Konto-URI aus Schritt 2), `SIGNING_ACCOUNT` (Kontoname),
     `SIGNING_PROFILE` (Profilname aus Schritt 5).
   Die Umgebung `release` legt GitHub beim nächsten Release-Lauf selbst an. Unter *Settings* → *Environments* →
   `release` kannst du optional festlegen, dass nur Tags `v*` sie nutzen dürfen.
9. **Testen**: neues Release taggen. Im Lauf „Release“, Job „Agent-MSI“, zeigt der Schritt „Signaturen prüfen“ für
   jede Datei `Valid` und den Firmennamen. Rechtsklick auf das MSI → *Eigenschaften* → *Digitale Signaturen* zeigt
   ihn ebenfalls.

Hinweis zu SmartScreen: Auch signierte Dateien brauchen anfangs etwas „Reputation“. Die Warnung verschwindet in der
Regel nach einigen Installationen bzw. Tagen.

Fehlersuche: „AADSTS70021: No matching federated identity record“ heißt, Organisation, Repository oder Umgebung in
Schritt 6 stimmen nicht. „403 Forbidden“ beim Signieren heißt, die Rolle aus Schritt 7 fehlt oder ist noch nicht
aktiv (bis zu 10 Minuten).
