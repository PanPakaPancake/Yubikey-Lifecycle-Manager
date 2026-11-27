# Agent signieren (SignPath Foundation)

Ohne Signatur warnt Windows SmartScreen beim Installieren des Agents, und Virenscanner sind misstrauischer.
Der Release-Build signiert `YlmAgent.exe`, `YlmTray.exe` und das MSI über **SignPath.io**. Für Open-Source-Projekte
stellt die **SignPath Foundation** Zertifikat und Dienst kostenlos bereit. Bis die Zugangsdaten im Repository
hinterlegt sind, entsteht wie bisher ein unsigniertes MSI, es bricht nichts.

Wichtig zu wissen:

- Im Zertifikat steht **„SignPath Foundation“** als Herausgeber, nicht der Projektname. So zeigt es auch Windows an.
- Jede Signierung muss in SignPath **von Hand freigegeben** werden (Vorgabe der Foundation). Ein Release braucht zwei
  Freigaben: erst die beiden EXE-Dateien, dann das MSI. Der Release-Lauf wartet je Schritt bis zu einer Stunde.
- Signiert wird nur, was in GitHub Actions aus diesem Repository gebaut wurde. SignPath prüft das selbst nach.

## Was der Agent prüft

- Ist der laufende Agent signiert, installiert er nur Updates mit **gültiger Signatur vom selben Unterzeichner**
  (Name im Zertifikat). Sonst bricht das Update ab und das MSI wird gelöscht. Die Prüfsumme vom Server wird wie
  bisher zusätzlich geprüft.
- Weil die SignPath Foundation viele Projekte unter demselben Namen signiert, prüft der Agent bei signierten Updates
  außerdem den **UpgradeCode** des MSI. Ein fremdes, ebenfalls von der Foundation signiertes Paket wird abgelehnt.
- Ein noch unsignierter Agent nimmt das erste signierte Update an. Ab dann gilt die Regel oben.
- Optional in `appsettings.json` im Programmordner des Agents (oder als Umgebungsvariable `Agent__...`):
  - `Agent:RequireSignedUpdates=true` lehnt unsignierte Updates auch bei einem unsignierten Agent ab.
  - `Agent:TrustedSigner="CN=..., O=..., C=US"` erlaubt einen bestimmten Unterzeichner zusätzlich, z. B. beim Wechsel
    zu einem anderen Zertifikat.

## Voraussetzungen der SignPath Foundation

Kurzfassung der Bedingungen (vollständig: <https://signpath.org/terms>):

- Repository öffentlich, OSI-Lizenz für das ganze Projekt, keine kommerzielle Doppellizenz, keine proprietären Teile.
- Projekt wird gepflegt und ist bereits in der zu signierenden Form veröffentlicht (Releases mit MSI gibt es).
- Funktionen sind auf der Downloadseite beschrieben (README, Release-Notizen).
- Auf der Startseite steht eine „Code signing policy“ mit dem Satz zu SignPath, den Rollen und einer Datenschutzaussage.
  Das steht im [README](../README.md#code-signing-policy).
- Mehr-Faktor-Anmeldung für alle Beteiligten bei GitHub und SignPath.
- Die EXE-Dateien tragen den Projektnamen als Produktname (`Directory.Build.props`) und überall dieselbe Version.

## Einrichtung Schritt für Schritt

1. **Repository vorbereiten**: Repository öffentlich schalten (*Settings* → *General* → *Danger Zone* → *Change
   visibility*). Prüfen, dass `LICENSE` vorhanden ist und das README die „Code signing policy“ zeigt.
2. **Antrag stellen**: <https://signpath.org/apply> ausfüllen. Repository-Adresse
   `https://github.com/PanPakaPancake/Yubikey-Lifecycle-Manager`, Lizenz, kurze Beschreibung („Selbst hostbare Plattform
   zur Verwaltung von YubiKeys in Microsoft Entra ID; signiert werden Windows-Agent, Tray-App und MSI“). Die Foundation
   prüft das Projekt und meldet sich per Mail. Das kann einige Tage bis Wochen dauern.
3. **Bei SignPath anmelden** (<https://app.signpath.io>) mit der Einladung aus der Mail und die
   Mehr-Faktor-Anmeldung einschalten. Die **Organization ID** steht unter *Organization* → *Settings* (eine GUID),
   notieren.
4. **GitHub verbinden**: Die SignPath-App unter <https://github.com/apps/signpath> installieren und nur dieses
   Repository freigeben. In SignPath unter *Trusted Build Systems* „GitHub.com“ hinzufügen (falls noch nicht da) und
   mit dem Projekt verknüpfen.
5. **Projekt prüfen**: Das Projekt legt meist die Foundation an. Den **Projekt-Slug** notieren (Standard im Workflow:
   `Yubikey-Lifecycle-Manager`). Unter *Signing Policies* gibt es in der Regel `test-signing` und `release-signing`;
   bei `release-signing` dich selbst als *Approver* eintragen.
6. **Artefaktkonfigurationen anlegen**: Im Projekt → *Artifact Configurations* → *Add*, zweimal:
   - Slug `agent-exe`, Inhalt aus [`.signpath/artifact-configurations/agent-exe.xml`](../.signpath/artifact-configurations/agent-exe.xml)
   - Slug `agent-msi`, Inhalt aus [`.signpath/artifact-configurations/agent-msi.xml`](../.signpath/artifact-configurations/agent-msi.xml)
7. **Zugang für GitHub**: *Users* → *Add CI User* (z. B. `github-actions`) → *Generate API Token*, Token kopieren.
   Diesem Benutzer bei beiden Signing Policies die Rolle *Submitter* geben.
8. **In GitHub hinterlegen**: Repository → *Settings* → *Secrets and variables* → *Actions*.
   - Reiter *Secrets*: `SIGNPATH_API_TOKEN` (Token aus Schritt 7).
   - Reiter *Variables*: `SIGNPATH_ORGANIZATION_ID` (aus Schritt 3). Nur wenn abweichend:
     `SIGNPATH_PROJECT_SLUG` und `SIGNPATH_SIGNING_POLICY_SLUG`.
   Erst die Variable `SIGNPATH_ORGANIZATION_ID` schaltet die Signierung ein.
9. **Testen**: Zum Ausprobieren `SIGNPATH_SIGNING_POLICY_SLUG=test-signing` setzen und ein Release taggen. SignPath
   schickt je Signierung eine Mail; in SignPath *Approve* klicken. Die Testsignatur ist für Windows nicht
   vertrauenswürdig, der Schritt „Signaturen prüfen“ zeigt dann nur eine Warnung. Danach die Variable wieder löschen
   (oder `release-signing` eintragen). Beim nächsten Release zeigt „Signaturen prüfen“ für jede Datei `Valid` und
   `CN=SignPath Foundation`. Rechtsklick auf das MSI → *Eigenschaften* → *Digitale Signaturen* zeigt es ebenfalls.

Aufräumen: Falls von der früheren Azure-Variante noch Secrets (`AZURE_CLIENT_ID`, `AZURE_TENANT_ID`,
`AZURE_SUBSCRIPTION_ID`), Variablen (`SIGNING_ENDPOINT`, `SIGNING_ACCOUNT`, `SIGNING_PROFILE`) oder die Umgebung
`release` angelegt wurden, können sie weg.

Hinweis zu SmartScreen: Auch signierte Dateien brauchen anfangs etwas „Reputation“. Die Warnung verschwindet in der
Regel nach einigen Installationen bzw. Tagen.

Fehlersuche: Bleibt ein Schritt bei „waiting for approval“ hängen, ist die Freigabe in SignPath noch offen (nach einer
Stunde bricht der Lauf ab; dann neu taggen oder den Lauf neu starten). „Artifact configuration not found“ heißt, der
Slug aus Schritt 6 stimmt nicht. „Origin verification failed“ heißt, die GitHub-App aus Schritt 4 fehlt oder das
Projekt ist nicht mit „GitHub.com“ verknüpft.
