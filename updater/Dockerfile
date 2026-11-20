# Kleiner Updater für die Docker-Installation: holt auf Anforderung der Plattform das neue Image und startet den Server neu.
# Nur dieser Container hat Zugriff auf den Docker-Socket, nicht der Server selbst.
# Außerdem: tägliche Sicherheitsupdates aller Container (AUTO_REFRESH, siehe updater.py).
# Ohne feste Version: jeder Build (auch der tägliche Rebuild) bekommt die aktuelle Docker-CLI mit Go-Sicherheitsfixes.
FROM docker:cli
RUN apk upgrade --no-cache && apk add --no-cache python3 tzdata
COPY updater.py /updater.py
EXPOSE 8090
CMD ["python3", "-u", "/updater.py"]
