# Kleiner Updater für die Docker-Installation: holt auf Anforderung der Plattform das neue Image und startet den Server neu.
# Nur dieser Container hat Zugriff auf den Docker-Socket, nicht der Server selbst.
FROM docker:27-cli
RUN apk add --no-cache python3
COPY updater.py /updater.py
EXPOSE 8090
CMD ["python3", "-u", "/updater.py"]
