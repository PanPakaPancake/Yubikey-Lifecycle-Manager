# Versionsverlauf

<!--
Pflege: Für jede Version einen Abschnitt "## <Version> · <Datum>" mit kurzen Stichpunkten in Alltagssprache.
Die App zeigt diese Datei unter "Versionsverlauf" an, der Release-Build übernimmt den Abschnitt der getaggten
Version als Beschreibung des GitHub-Releases (und damit als "Was ist neu" beim Update-Hinweis).
Optional vor dem Stichpunkt: "Neu:", "Verbessert:", "Behoben:" oder "Sicherheit:".
-->

## 0.9.1 · 2026-11-20
- Neu: Versionsverlauf in der App. Unter „Hilfe“ steht jetzt, was sich mit jeder Version geändert hat.
- Neu: Die Übersicht zeigt, welche Version läuft und ob ein Update bereitsteht, mit direktem Weg zum Update.
- Neu: Erscheinungsbild der Dokumente: „Farbe aus Logo“ schlägt bis zu drei Farben aus dem hochgeladenen Logo als Akzentfarbe vor.
- Verbessert: Das Logo in der Seitenleiste ist größer.
- Verbessert: Lokale Konten heißen einheitlich „Lokales Konto (Passkey)“ (empfohlen) und „Lokales Konto (Passwort + TOTP)“.
- Neu: Die Tray-App des Agents im selben Design wie die Weboberfläche. Darstellung Dunkel, Hell oder wie Windows.
- Neu: Passkey-Rollout zeigt bei Windows Hello das Gerät, z. B. „LAPTOP-TIMO (Windows)“.
- Behoben: Passkeys im Microsoft Authenticator oder in Windows Hello wurden als Sicherheitsschlüssel gezählt.
- Sicherheit: Die Container bekommen täglich um 4 Uhr Sicherheitsupdates. Das Server-Image ist schlanker, die Images werden auf Schwachstellen geprüft und bei Bedarf automatisch neu gebaut.
- Sicherheit: Das Agent-MSI kann signiert werden. Ein signierter Agent nimmt dann nur noch Updates vom selben Unterzeichner an.

## 0.9.0 · 2026-11-20
- Neu: Notfallcodes für Owner. Einmal-Anmeldecodes zum Ausdrucken, falls kein YubiKey zur Hand ist.
- Behoben: Der Bestätigungsdialog (z. B. beim Entfernen einer Anmeldemethode) lag hinter dem offenen Personen-Dialog.

## 0.8.4 · 2026-11-20
- Verbessert: Breite Bildschirme werden voll genutzt. Die Übersicht ordnet die Kacheln ab 1800 Pixeln in sechs Spalten an.
- Verbessert: Die Kachel „Abdeckung Zielgruppe“ sagt, wer gezählt wird, und führt direkt zur Einstellung der Zielgruppe.
- Behoben: Ohne festgelegte Zielgruppe zählten auch Gastkonten mit.

## 0.8.3 · 2026-11-20
- Verbessert: Bei der Anmeldung wird standardmäßig nicht mehr geprüft, ob mit YubiKey angemeldet wurde. Die Prüfung lässt sich wieder einschalten.

## 0.8.2 · 2026-11-20
- Behoben: Der Setup-Assistent meldete fälschlich fehlende Berechtigungen, weil er ein veraltetes Token prüfte.

## 0.8.1 · 2026-11-20
- Verbessert: Der Setup-Assistent meldet sich direkt bei Microsoft an, statt einen Gerätecode abzufragen.
- Verbessert: Wird eine Anmeldung abgelehnt, steht dabei, welche Methode verwendet wurde. Ein Klick startet die Anmeldung mit YubiKey neu.

## 0.8.0 · 2026-11-20
- Neu: Design „Bento“ mit dunkler Ansicht als Vorgabe, hell umschaltbar, und ein animiertes Logo.
- Neu: „Passkeys erklärt“ ist in die App eingebunden. Den öffentlichen Link für Endbenutzer gibt es weiterhin.
- Sicherheit: Härtung nach Security-Audit, geschützte Konten, verschlüsselte Datensicherungen, Erneuerung des App-Zertifikats.

## 0.7.0 · 2026-11-19
- Neu: Passkey-Rollout. Übersicht, wer welche Anmeldemethoden hat, befristete Zugangspässe (TAP) erzeugen und unsichere Methoden entfernen.

## 0.6.0 · 2026-11-19
- Neu: Key-Detail mit allen Konten, die auf dem Key liegen.
- Behoben: Das Zurücksetzen greift nur noch, wenn der richtige Key eingesteckt ist.

## 0.5.1 · 2026-11-19
- Neu: Infoseite „Passkeys erklärt“ mit Animationen, umschaltbar zwischen Endbenutzer und Techniker. Der PIN-Brief verlinkt darauf.
- Neu: Mail-Alarme mit wählbaren Ereignissen unter „Benachrichtigungen“.
- Verbessert: Frischeres Design und ein gemeinsames Inventar für alle Keys.

## 0.5.0 · 2026-11-19
- Neu: Einstellungen des Agents im Tray und zuverlässiges automatisches Update des Agents, steuerbar über die Plattform.

## 0.4.0 · 2026-11-19
- Neu: Übersicht „Wer hat welchen Key“ mit Export als Excel, CSV und PDF.
- Neu: Eigenes Logo und eigene Farben für PIN-Briefe, Berichte und Exporte.
- Neu: Betrieb auf einem eigenen Server mit Installationsskript, automatischem HTTPS und Datensicherung.

## 0.3.0 · 2026-11-19
- Neu: Geteilte Keys, Nacherfassen, ToDo-Liste, Vier-Augen-Freigabe für Breakglass-Keys, Webhooks und Mail-Berichte.

## 0.2.1 · 2026-11-19
- Neu: Stapelregistrierung, um viele Personen nacheinander mit Keys zu versorgen.
- Verbessert: Überarbeitete Bedienung mit Icons, Hinweisen, hellem und dunklem Modus und mobiler Navigation.

## 0.2.0 · 2026-11-19
- Erste Version: Tenants anbinden, Inventar, Registrierungsprofile, Registrierung per Assistent, PIN-Brief, Übersicht und Lese-API.
- Neu: Eigene Rollen, Partnerlager, lokale Benutzer mit Passkey-Anmeldung und Update per Klick.
