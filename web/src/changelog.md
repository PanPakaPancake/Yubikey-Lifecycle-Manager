# Versionsverlauf

<!--
Pflege: Für jede Version einen Abschnitt "## <Version> · <Datum>" mit kurzen Stichpunkten in Alltagssprache.
Die App zeigt diese Datei unter "Versionsverlauf" an, der Release-Build übernimmt den Abschnitt der getaggten
Version als Beschreibung des GitHub-Releases (und damit als "Was ist neu" beim Update-Hinweis).
Optional vor dem Stichpunkt: "Neu:", "Verbessert:", "Behoben:" oder "Sicherheit:".
-->

## 0.11.2 · 2026-11-27
- Behoben: Das Tray-Symbol des Agents 0.11.1 stürzte direkt beim Start ab und erschien deshalb nicht. Der Agent-Dienst selbst lief weiter.

## 0.11.1 · 2026-11-26
- Neu: Im Passkey-Rollout zeigt das Personenfenster bei Windows Hello, Authenticator-App und Platform Credential das zugehörige Gerät aus Entra: Name, Modell, Betriebssystem mit Version, Join-Art, Konformität und letzte Aktivität, dazu ein Link „in Entra öffnen“.
- Behoben: Bei Windows Hello for Business fehlte das Gerät oft, weil Entra es nur bei der Einzelabfrage einer Methode mitliefert.
- Verbessert: Kennt der Server die Kopplung eines Agents nicht mehr (z. B. nach dem Entkoppeln in der Weboberfläche), sagt das Tray-Symbol einmal Bescheid. Ein Klick auf die Meldung öffnet direkt das Koppeln-Fenster.
- Behoben: Scheitert beim Koppeln der HTTPS-Verbindungsaufbau, nennt der Agent jetzt den eigentlichen Grund (z. B. nicht vertrauenswürdiges Stammzertifikat) statt pauschal „kein HTTPS“.
- Behoben: Mit der internen Zertifizierungsstelle (https://localhost) scheiterte das Koppeln, weil deren Zertifikate keine Sperrliste haben. Fehlt die Sperrinformation, akzeptiert der Agent das Zertifikat jetzt wie ein Browser; gesperrte oder nicht vertrauenswürdige Zertifikate lehnt er weiterhin ab.
- Verbessert: Das Installationsskript holt das Stammzertifikat der internen Zertifizierungsstelle bei jedem Lauf neu, damit es immer zum laufenden Server passt.

## 0.11.0 · 2026-11-26
- Sicherheit: Aktive Breakglass-Keys lassen sich nicht mehr von einer Person allein sperren, zurücksetzen oder um ein Konto erleichtern. Ein zweiter Admin mit dem Recht „Breakglass freigeben“ muss den Antrag unter „Freigaben“ freigeben (gilt 24 Stunden, einmal). Standardmäßig für alle Tenants an; abschalten darf nur, wer „Geschützte Konten bearbeiten“ hat.
- Sicherheit: Über die API lassen sich aktive Breakglass-Keys nicht sperren.
- Neu: Notfallblatt für den versiegelten Umschlag. Beim Anlegen eines Breakglass-Keys und jederzeit an der Key-Ansicht lassen sich weitere Notfallkonten (z. B. Amazon, Registrar) mit Benutzername, Passwort, MFA-Methode und Beschreibung eintragen und ausdrucken. Die Angaben werden nirgends gespeichert, weder im Browser noch auf dem Server.
- Neu: Der PIN-Brief wird jetzt per Baukasten gestaltet (Globale Einstellungen bzw. Tenant → „PIN-Brief gestalten“). Bausteine wie Überschrift, Text, Hinweiskasten, Anleitung oder Trennlinie lassen sich aufs Blatt ziehen, verschieben, halb breit nebeneinander stellen und mit Platzhaltern füllen. Jeder Baustein kann nur im Brief mit Initial-PIN oder nur mit dauerhafter PIN erscheinen. Mit Rückgängig und PDF-Vorschau.
- Verbessert: Wird der PIN-Wechsel erzwungen, ist die PIN im Brief deutlich als Initial-PIN gekennzeichnet (oranger gestrichelter Rahmen, „Nur für die erste Anmeldung“).
- Neu: Der PIN-Brief erklärt immer, wie man die PIN später selbst ändert (Windows, Mac, Yubico Authenticator).
- Behoben: Im PIN-Brief lief der Text „Sicherheitsschlüssel“ in der Abbildung aus dem Rahmen. Lange Texte vergrößern die Karten jetzt, statt überzulaufen; zu lange Briefe gehen auf einer zweiten Seite weiter.
- Hinweis: Bisher angepasste Brieftexte werden automatisch in die Bausteine übernommen.
- Verbessert: Unter „Adresse und Zertifikat“ erklärt jede Betriebsart und jede Zertifikatsart, was sie macht und wann sie passt. Die aktuelle Einstellung ist markiert.
- Behoben: Der Agent ließ sich auf manchen PCs nicht koppeln („Unknown error (0x1770)“), weil Windows die Verschlüsselung des Agent-Tokens ablehnte. Er speichert es dann ohne diese Verschlüsselung; die Datei ist nur noch für SYSTEM und Administratoren lesbar.
- Verbessert: Beim Koppeln erklärt der Agent typische Adressfehler, z. B. https:// auf Port 8080, ein nicht vertrauenswürdiges Zertifikat oder eine abgelehnte Verbindung.

## 0.10.1 · 2026-11-25
- Behoben: Das Update per Klick bringt jetzt alle Container auf die neue Version, auch Updater, Caddy und Datensicherung. Bisher wurde nur der Server ausgetauscht.

## 0.10.0 · 2026-11-20
- Neu: Adresse und Zertifikat in der Oberfläche ändern (Globale Einstellungen → Adresse und Zertifikat): DNS-Name, weitere Namen, Ports, Betriebsart. Vorher wird geprüft, bei Fehlern bleibt der alte Stand.
- Neu: Zertifikate: interne Zertifizierungsstelle, eigenes Zertifikat hochladen (auch Wildcard), Let's Encrypt und Let's Encrypt per DNS (Cloudflare, Hetzner, IONOS, netcup, Azure DNS, Route 53, DigitalOcean), auch für interne Server und andere Ports.
- Neu: Das eigene Zertifikat lässt sich per API tauschen (Recht „HTTPS-Zertifikat tauschen“), z. B. automatisch aus der Firmen-PKI.
- Neu: Bei einer neuen Adresse trägt die Plattform sie nach einer Microsoft-Anmeldung selbst in Entra ein, und verbundene Agents ziehen automatisch um.
- Verbessert: Neue Windows-Installationen laufen standardmäßig unter https://localhost; das Stammzertifikat wird in Windows eingetragen.
- Verbessert: Globale Einstellungen sind in Bereiche aufgeteilt (Allgemein, Adresse und Zertifikat, Anmeldung und Sicherheit, Updates, API).

## 0.9.3 · 2026-11-20
- Verbessert: Der Versionsverlauf öffnet sich mit einem Klick auf die Versionsangabe unten in der Seitenleiste. Der eigene Menüpunkt unter „Hilfe“ entfällt.
- Neu: Unten in der Seitenleiste erscheint das Profilbild aus Microsoft 365, sonst wie bisher die Initialen.
- Verbessert: „Nacherfassen“ steht im Inventar direkt beim betroffenen Key statt oben als allgemeiner Knopf.
- Verbessert: „Passkeys erklärt“ ohne den Knopf „Wie Endbenutzer öffnen“, der öffentliche Link lässt sich weiterhin kopieren.
- Neu: Brief-Editor unter „Erscheinungsbild der Dokumente“. Alle Texte des PIN-Briefs lassen sich anpassen, mit Platzhaltern wie {{Vorname}} oder {{Seriennummer}} und einer Live-Vorschau als PDF. Tenants können einzelne Texte überschreiben.
- Verbessert: Der PIN-Brief hat ein neues, helles Design passend zur App. Rahmen statt farbiger Flächen sparen beim Drucken Toner.
- Behoben: In „Passkeys erklärt“ sind die Schritte unter „Am PC anmelden“ jetzt mit 1 bis 4 nummeriert.
- Behoben: „Sicherheitsupdates der Container – Jetzt einspielen“ schlug unter Docker Desktop für Windows fehl.

## 0.9.2 · 2026-11-20
- Neu: Rechte pro API-Schlüssel, ähnlich wie Rollen. Jeder Schlüssel bekommt eigene Lese- und Schreibrechte (Inventar, Zuweisungen, Kennzahlen, ToDo-Liste, Audit-Log), kombiniert mit allen oder ausgewählten Tenants. Vorlagen: „Nur lesen“, „Lesen + ToDo“, „Lesen + Schreiben“; „Keys sperren“ wird immer einzeln vergeben.
- Neu: Über die API lassen sich jetzt Tags, Zusatzfelder und Notiz eines Keys pflegen und Keys sperren, z. B. aus dem Ticketsystem bei Verlust. Jede Änderung steht im Audit-Log.
- Neu: Das Audit-Log ist über die API abrufbar, z. B. für ein SIEM.
- Verbessert: Bestehende Schlüssel behalten beim Update genau ihre bisherigen Rechte.

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
