#!/usr/bin/env bash
# Installiert oder aktualisiert Yubikey Lifecycle Manager mit Docker Compose (Linux, z. B. VPS).
#
#   curl -fsSL https://raw.githubusercontent.com/PanPakaPancake/Yubikey-Lifecycle-Manager/main/install.sh | sudo bash
#   curl -fsSL .../install.sh | sudo bash -s -- --domain ylm.example.com --yes
#
# Optionen:
#   --dir PFAD         Installationsordner (Standard: /opt/ylm)
#   --mode MODUS       internal = internes Netz/ZTNA, HTTPS mit eigener interner Zertifizierungsstelle (Caddy)
#                      cert     = internes Netz/ZTNA, HTTPS mit eigenem Zertifikat (z. B. Firmen-PKI)
#                      proxy    = hinter einem vorhandenen Reverse Proxy oder ZTNA-Connector (nginx, Traefik,
#                                 Cloudflare Tunnel, Entra Private Access/App Proxy, ...)
#                      https    = öffentliche Domain mit Let's Encrypt (Server muss aus dem Internet erreichbar sein)
#                      local    = nur auf diesem Rechner unter http://localhost:8080
#   --domain NAME      DNS-Name für internal, cert und https (z. B. ylm.firma.intern)
#   --cert DATEI       Zertifikat (PEM, mit Zwischenzertifikaten) für --mode cert
#   --key DATEI        privater Schlüssel (PEM) für --mode cert
#   --url URL          Adresse für --mode proxy, z. B. https://ylm.firma.de
#   --bind ADRESSE     Netzwerkadresse, auf der die Plattform lauscht (Standard je Modus, z. B. 10.0.0.5)
#   --version vX.Y.Z   bestimmte Version statt der neuesten
#   --yes              keine Rückfragen (Standardwerte bzw. Optionen verwenden)
#   --no-host-updates  automatische Sicherheitsupdates des Hosts (unattended-upgrades) nicht einrichten
#
# Erneut ausführen ist gefahrlos: Eine vorhandene .env bleibt erhalten, die Compose-Dateien werden
# auf den Stand der Version gebracht und die Images aktualisiert.
set -euo pipefail

REPO="PanPakaPancake/Yubikey-Lifecycle-Manager"
# Quelle der Compose-Dateien; nur für Forks oder Tests ändern.
RAW="${YLM_RAW_URL:-https://raw.githubusercontent.com/$REPO}"
DIR="/opt/ylm"
MODE=""
DOMAIN=""
URL=""
CERT=""
KEY=""
BIND=""
VERSION=""
YES=0
HOST_UPDATES=1

while [ $# -gt 0 ]; do
	case "$1" in
		--dir) DIR="$2"; shift 2 ;;
		--mode) MODE="$2"; shift 2 ;;
		--domain) DOMAIN="$2"; shift 2 ;;
		--cert) CERT="$2"; MODE="${MODE:-cert}"; shift 2 ;;
		--key) KEY="$2"; shift 2 ;;
		--url) URL="$2"; MODE="${MODE:-proxy}"; shift 2 ;;
		--bind) BIND="$2"; shift 2 ;;
		--version) VERSION="$2"; shift 2 ;;
		--yes|-y) YES=1; shift ;;
		--no-host-updates) HOST_UPDATES=0; shift ;;
		-h|--help) sed -n '2,26p' "$0" 2>/dev/null || true; exit 0 ;;
		*) echo "Unbekannte Option: $1" >&2; exit 2 ;;
	esac
done
# Wie bisher: --domain ohne --mode heißt Let's Encrypt. Für interne Netze --mode internal angeben.
[ -n "$MODE" ] || [ -z "$DOMAIN" ] || MODE=https

bold() { printf '\033[1m%s\033[0m\n' "$*"; }
info() { printf '  %s\n' "$*"; }
warn() { printf '\033[33m! %s\033[0m\n' "$*"; }
fail() { printf '\033[31mFehler: %s\033[0m\n' "$*" >&2; exit 1; }

# Fragen auch dann stellen können, wenn das Skript per "curl | bash" läuft.
TTY=/dev/tty
[ -r "$TTY" ] || YES=1
ask() { # ask "Frage" "Standard" -> Antwort
	local answer=""
	if [ "$YES" = 1 ]; then echo "$2"; return; fi
	printf '%s [%s]: ' "$1" "$2" > "$TTY"
	read -r answer < "$TTY" || true
	echo "${answer:-$2}"
}
confirm() { # confirm "Frage" -> 0 bei ja
	[ "$YES" = 1 ] && return 0
	local a; a=$(ask "$1 (j/n)" "j")
	case "$a" in j|J|ja|Ja|y|Y|yes) return 0 ;; *) return 1 ;; esac
}
secret() { # Zufallsgeheimnis in Hex
	if command -v openssl >/dev/null 2>&1; then openssl rand -hex "$1"; else head -c "$1" /dev/urandom | od -An -tx1 | tr -d ' \n'; fi
}
env_get() { [ -f "$DIR/.env" ] && sed -n "s/^$1=//p" "$DIR/.env" | tail -n1 || true; }
env_set() { # setzt oder ergänzt KEY=VALUE in .env
	if grep -q "^$1=" "$DIR/.env"; then
		local tmp; tmp=$(mktemp); awk -v k="$1" -v v="$2" 'BEGIN{FS=OFS="="} $1==k{print k"="v; next} {print}' "$DIR/.env" > "$tmp" && cat "$tmp" > "$DIR/.env" && rm -f "$tmp"
	else
		printf '%s=%s\n' "$1" "$2" >> "$DIR/.env"
	fi
}

bold "Yubikey Lifecycle Manager – Installation"

# --- 1. Voraussetzungen ---------------------------------------------------------------------------
command -v curl >/dev/null 2>&1 || fail "curl fehlt. Bitte installieren (z. B. apt install curl)."
if ! command -v docker >/dev/null 2>&1; then
	warn "Docker ist nicht installiert."
	if [ "$(id -u)" = 0 ] && confirm "Docker jetzt mit dem offiziellen Skript von get.docker.com installieren?"; then
		curl -fsSL https://get.docker.com | sh
	else
		fail "Bitte Docker Engine mit Compose-Plugin installieren: https://docs.docker.com/engine/install/"
	fi
fi
docker compose version >/dev/null 2>&1 || fail "Das Compose-Plugin fehlt (docker compose). Siehe https://docs.docker.com/compose/install/linux/"
docker info >/dev/null 2>&1 || fail "Kein Zugriff auf Docker. Skript mit sudo ausführen oder den Benutzer zur Gruppe docker hinzufügen."
info "Docker $(docker version --format '{{.Server.Version}}' 2>/dev/null), $(docker compose version --short 2>/dev/null | sed 's/^/Compose /')"

# Kernel (z. B. TCP-Stack), Docker und Systembibliotheken teilen sich alle Container mit dem Host. Die Container
# hält der Updater aktuell, den Host nur dessen eigene Updates: auf Debian/Ubuntu per unattended-upgrades.
if [ "$HOST_UPDATES" = 1 ] && [ "$(id -u)" = 0 ] && command -v apt-get >/dev/null 2>&1; then
	if dpkg -s unattended-upgrades >/dev/null 2>&1 && grep -qs 'Unattended-Upgrade "1"' /etc/apt/apt.conf.d/20auto-upgrades; then
		info "Automatische Sicherheitsupdates des Hosts: aktiv (unattended-upgrades)."
	elif confirm "Automatische Sicherheitsupdates für diesen Server einrichten (unattended-upgrades, empfohlen)?"; then
		DEBIAN_FRONTEND=noninteractive apt-get install -y -qq unattended-upgrades >/dev/null
		printf 'APT::Periodic::Update-Package-Lists "1";\nAPT::Periodic::Unattended-Upgrade "1";\n' > /etc/apt/apt.conf.d/20auto-upgrades
		info "Automatische Sicherheitsupdates des Hosts eingerichtet. Neustarts nach Kernel-Updates: siehe docs/BETRIEB.md."
	fi
fi

# --- 2. Version und Dateien -----------------------------------------------------------------------
if [ -z "$VERSION" ]; then
	VERSION=$(curl -fsSL "https://api.github.com/repos/$REPO/releases/latest" 2>/dev/null | sed -n 's/.*"tag_name": *"\([^"]*\)".*/\1/p' | head -n1 || true)
	[ -n "$VERSION" ] || { warn "Neueste Version nicht ermittelbar, verwende den Stand von main."; VERSION=main; }
fi
REF="$VERSION"
info "Version: $VERSION"

mkdir -p "$DIR/deploy" "$DIR/backups" "$DIR/certs"
cd "$DIR"
if [ -d .git ]; then
	info "$DIR ist ein Git-Checkout: Dateien kommen aus Git (git pull), es wird nichts heruntergeladen."
else
	for f in docker-compose.yml deploy/Caddyfile deploy/backup.sh .env.example; do
		tmp=$(mktemp)
		if ! curl -fsSL "$RAW/$REF/$f" -o "$tmp" 2>/dev/null; then
			# Ältere Releases enthalten noch nicht alle Dateien: dann den Stand von main nehmen.
			if [ "$REF" = main ] || ! curl -fsSL "$RAW/main/$f" -o "$tmp"; then rm -f "$tmp"; fail "Download von $f fehlgeschlagen."; fi
			warn "$f gibt es in $REF noch nicht, verwende den Stand von main."
		fi
		if [ -f "$f" ] && ! cmp -s "$tmp" "$f"; then
			cp "$f" "$f.bak"
			info "$f aktualisiert (vorherige Fassung: $f.bak)"
		fi
		mv "$tmp" "$f"
	done
	chmod 644 docker-compose.yml deploy/Caddyfile deploy/backup.sh .env.example
fi

# --- Erreichbarkeit -------------------------------------------------------------------------------
NEW_PASSPHRASE=0
CADDY_TLS=""
configure_mode() { # fragt die Angaben zum Modus ab und setzt URL, DOMAIN, CADDY_TLS
	case "$MODE" in
		internal|cert|https)
			if [ -z "$DOMAIN" ]; then
				if [ "$MODE" = https ]; then DOMAIN=$(ask "Öffentliche Domain (DNS-Eintrag zeigt auf diesen Server)" "")
				else DOMAIN=$(ask "DNS-Name im internen Netz (z. B. ylm.firma.intern)" "$(hostname -f 2>/dev/null || hostname)"); fi
			fi
			[ -n "$DOMAIN" ] || fail "Ohne DNS-Namen kein HTTPS. Mit --domain angeben."
			DOMAIN="${DOMAIN#https://}"; DOMAIN="${DOMAIN%%/*}"
			URL="https://$DOMAIN"
			case "$MODE" in
				internal) CADDY_TLS="tls internal" ;;
				https) CADDY_TLS="" ;;
				cert)
					[ -n "$CERT" ] || [ -f certs/ylm.crt ] || CERT=$(ask "Zertifikatsdatei (PEM, mit Zwischenzertifikaten)" "")
					[ -n "$KEY" ] || [ -f certs/ylm.key ] || KEY=$(ask "Datei mit dem privaten Schlüssel (PEM, ohne Passwort)" "")
					if [ -n "$CERT" ]; then [ -f "$CERT" ] || fail "Zertifikat nicht gefunden: $CERT"; cp "$CERT" certs/ylm.crt; fi
					if [ -n "$KEY" ]; then [ -f "$KEY" ] || fail "Schlüssel nicht gefunden: $KEY"; cp "$KEY" certs/ylm.key; fi
					[ -f certs/ylm.crt ] && [ -f certs/ylm.key ] || fail "Für --mode cert bitte --cert und --key angeben (oder certs/ylm.crt und certs/ylm.key ablegen)."
					chmod 644 certs/ylm.crt; chmod 600 certs/ylm.key
					CADDY_TLS="tls /certs/ylm.crt /certs/ylm.key" ;;
			esac ;;
		proxy)
			[ -n "$URL" ] || URL=$(ask "Adresse, unter der Benutzer die Plattform aufrufen" "https://ylm.firma.de")
			case "$URL" in https://*) ;; *) fail "Die Adresse muss mit https:// beginnen (Passkeys und Entra verlangen HTTPS)." ;; esac
			URL="${URL%/}"
			if [ -z "$BIND" ]; then
				if confirm "Läuft der Reverse Proxy bzw. ZTNA-Connector auf diesem Server?"; then BIND=127.0.0.1
				else BIND=$(ask "Interne IP-Adresse dieses Servers, die der Proxy erreicht" "$(hostname -I 2>/dev/null | awk '{print $1}')"); fi
			fi ;;
		local) URL="http://localhost:8080" ;;
		*) fail "Unbekannter Modus: $MODE (internal, cert, proxy, https oder local)" ;;
	esac
}
apply_mode() { # schreibt den Modus in .env
	env_set YLM_PUBLIC_URL "$URL"
	case "$MODE" in
		internal|cert|https)
			env_set COMPOSE_PROFILES https; env_set YLM_DOMAIN "$DOMAIN"; env_set YLM_CADDY_TLS "$CADDY_TLS"
			[ -z "$BIND" ] || env_set YLM_HTTPS_BIND "$BIND" ;;
		proxy|local)
			env_set COMPOSE_PROFILES ""
			[ "$MODE" = local ] || env_set YLM_BIND "${BIND:-127.0.0.1}:8080" ;;
	esac
	env_set YLM_MODE "$MODE"
}

# --- 3. .env --------------------------------------------------------------------------------------
if [ -f .env ]; then
	info "Vorhandene .env wird beibehalten."
	[ -n "$(env_get UPDATER_TOKEN)" ] || { env_set UPDATER_TOKEN "$(secret 32)"; info "UPDATER_TOKEN ergänzt (Update per Klick)."; }
	if [ -z "$(env_get BACKUP_PASSPHRASE)" ]; then
		env_set BACKUP_PASSPHRASE "$(secret 24)"; NEW_PASSPHRASE=1
		info "BACKUP_PASSPHRASE ergänzt: neue Sicherungen werden verschlüsselt (ältere bleiben lesbar)."
	fi
	if [ -n "$MODE" ]; then
		configure_mode
		apply_mode
		info "Erreichbarkeit umgestellt auf: $MODE ($URL)."
	fi
else
	if curl -fsS -m 3 http://127.0.0.1:8080/healthz >/dev/null 2>&1; then
		fail "Auf Port 8080 läuft schon eine Installation (anderer Ordner?). Bitte dort arbeiten (--dir) oder sie vorher stoppen (docker compose down)."
	fi
	if [ -z "$MODE" ]; then
		echo
		bold "Wie soll die Plattform erreichbar sein?"
		info "1) Internes Netz oder ZTNA, HTTPS mit eigener interner Zertifizierungsstelle (empfohlen)"
		info "2) Internes Netz oder ZTNA, HTTPS mit eigenem Zertifikat (z. B. Firmen-PKI)"
		info "3) Hinter einem vorhandenen Reverse Proxy oder ZTNA-Connector"
		info "4) Öffentliche Domain mit Let's Encrypt (nur wenn der Server aus dem Internet erreichbar sein soll)"
		info "5) Nur auf diesem Rechner (http://localhost:8080)"
		case "$(ask "Auswahl" "1")" in 2) MODE=cert ;; 3) MODE=proxy ;; 4) MODE=https ;; 5) MODE=local ;; *) MODE=internal ;; esac
	fi
	configure_mode
	{
		echo "# Angelegt von install.sh am $(date '+%Y-%m-%d %H:%M'). Erklärung aller Werte: .env.example"
		echo "COMPOSE_PROJECT_NAME=ylm"
		echo "POSTGRES_PASSWORD=$(secret 24)"
		echo "YLM_PUBLIC_URL=$URL"
		echo "UPDATER_TOKEN=$(secret 32)"
		echo "# Verschlüsselt die Sicherungen. Zusätzlich im Passwortmanager ablegen, ohne sie ist keine Wiederherstellung möglich."
		echo "BACKUP_PASSPHRASE=$(secret 24)"
		echo "TZ=$( (timedatectl show -p Timezone --value 2>/dev/null || cat /etc/timezone 2>/dev/null) | head -n1 | grep . || echo Europe/Berlin)"
	} > .env
	chmod 600 .env
	NEW_PASSPHRASE=1
	apply_mode
	info ".env mit neuen Zufallsgeheimnissen angelegt ($DIR/.env)."
fi

PROFILES=$(env_get COMPOSE_PROFILES)
DOMAIN=$(env_get YLM_DOMAIN)
URL=$(env_get YLM_PUBLIC_URL)

# --- 4. Plausibilitätsprüfungen für HTTPS ---------------------------------------------------------
if [[ "$PROFILES" == *https* ]]; then
	[ -n "$DOMAIN" ] || fail "COMPOSE_PROFILES=https ist gesetzt, aber YLM_DOMAIN fehlt in .env."
	resolved=$(getent ahosts "$DOMAIN" 2>/dev/null | awk '{print $1}' | sort -u | tr '\n' ' ' || true)
	if [ -z "$resolved" ]; then
		if [ -z "$(env_get YLM_CADDY_TLS)" ]; then warn "$DOMAIN lässt sich (noch) nicht auflösen. Let's Encrypt klappt erst, wenn der DNS-Eintrag auf diesen Server zeigt."
		else warn "$DOMAIN lässt sich auf diesem Server nicht auflösen. Bitte einen DNS-Eintrag im internen DNS anlegen, der auf diesen Server zeigt."; fi
	else
		info "$DOMAIN zeigt auf: $resolved"
	fi
	if command -v ss >/dev/null 2>&1; then
		busy=$(ss -Hltn '( sport = :80 or sport = :443 )' 2>/dev/null | grep -v docker-proxy || true)
		if [ -n "$busy" ] && ! docker compose ps caddy --status running -q 2>/dev/null | grep -q .; then
			warn "Port 80 oder 443 ist schon belegt (anderer Webserver?). Caddy kann dann nicht starten."
		fi
	fi
	if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q "Status: active"; then
		info "Hinweis ufw: Ports 80/tcp, 443/tcp und 443/udp müssen offen sein (ufw allow 80,443/tcp; ufw allow 443/udp)."
	fi
fi

# --- 5. Starten -----------------------------------------------------------------------------------
echo
bold "Lade Images und starte ..."
docker compose pull || warn "Nicht alle Images ließen sich laden. Es wird mit den vorhandenen weitergemacht."
docker compose up -d --remove-orphans

printf '  Warte auf den Server'
ok=0
for _ in $(seq 1 60); do
	if curl -fsS http://127.0.0.1:8080/healthz >/dev/null 2>&1 || docker compose exec -T server dotnet Ylm.Server.dll --healthcheck >/dev/null 2>&1; then ok=1; break; fi
	printf '.'; sleep 3
done
echo
[ "$ok" = 1 ] || { warn "Der Server antwortet noch nicht. Protokoll: cd $DIR && docker compose logs server"; exit 1; }

echo
bold "Fertig. Die Plattform läuft unter $URL"
token=$(docker compose logs server 2>/dev/null | sed -n 's/.*Setup-Token: *\([^ ]*\).*/\1/p' | tail -n1 | tr -d '\r')
if [ -n "$token" ]; then
	info "Einrichtung: $URL/setup öffnen und dieses Setup-Token eingeben:"
	echo
	echo "      $token"
	echo
fi
if [ "$(env_get YLM_CADDY_TLS)" = "tls internal" ]; then
	for _ in $(seq 1 20); do
		docker compose exec -T caddy cat /data/caddy/pki/authorities/local/root.crt > ylm-root-ca.crt 2>/dev/null && [ -s ylm-root-ca.crt ] && break
		sleep 2
	done
	if [ -s ylm-root-ca.crt ]; then
		chmod 644 ylm-root-ca.crt
		bold "Stammzertifikat der internen Zertifizierungsstelle: $DIR/ylm-root-ca.crt"
		info "Bitte auf allen Admin-PCs (Browser und Agent) als vertrauenswürdige Stammzertifizierungsstelle verteilen,"
		info "z. B. per Gruppenrichtlinie oder Intune. Einzelner PC: certutil -addstore -f Root ylm-root-ca.crt (als Administrator)."
		echo
	else
		rm -f ylm-root-ca.crt
		warn "Stammzertifikat noch nicht verfügbar. Später: docker compose exec caddy cat /data/caddy/pki/authorities/local/root.crt > ylm-root-ca.crt"
	fi
fi
if [ "$NEW_PASSPHRASE" = 1 ]; then
	bold "Passphrase für die Datensicherung (bitte jetzt im Passwortmanager ablegen):"
	echo
	echo "      $(env_get BACKUP_PASSPHRASE)"
	echo
	info "Ohne diese Passphrase lässt sich keine Sicherung einspielen. Sie steht auch in $DIR/.env."
fi
info "Installationsordner: $DIR (Einstellungen in .env, Sicherungen in backups/)"
info "Status: docker compose ps   Protokoll: docker compose logs -f server"
info "Updates: in der Oberfläche unter Globale Einstellungen → Version und Updates, oder dieses Skript erneut ausführen."
