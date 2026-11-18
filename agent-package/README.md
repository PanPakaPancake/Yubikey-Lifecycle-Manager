Der Release-Build legt hier `YubikeyLifecycleManager-Agent.msi` und `version.txt` ab, bevor das Docker-Image gebaut wird.
Die Plattform bietet das MSI dann unter „Mein Agent“ zum Download an und verteilt es per Auto-Update an verbundene Agents.
Bei lokalen Builds ohne MSI verweist die Oberfläche auf das neueste GitHub-Release.
