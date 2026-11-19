#!/usr/bin/env bash
# Installiert oder aktualisiert Yubikey Lifecycle Manager mit Docker Compose (Linux, z. B. VPS).
#
#   curl -fsSL https://raw.githubusercontent.com/PanPakaPancake/Yubikey-Lifecycle-Manager/main/install.sh | sudo bash
#   curl -fsSL .../install.sh | sudo bash -s -- --domain ylm.example.com --yes
#
# Optionen:
#   --dir PFAD         Installationsordner (Standard: /opt/ylm)
#   --mode MODUS       https  = eigene Domain mit automatischem HTTPS (Caddy + Let's Encrypt)
#                      proxy  = hinter einem vorhandenen Reverse Proxy (nginx, Traefik, ...)
#                      local  = nur auf diesem Rechner unter http://localhost:8080
#   --domain NAME      Domain für --mode https (setzt den Modus automatisch)
#   --url URL          öffentliche Adresse für --mode proxy, z. B. https://ylm.example.com
#   --version vX.Y.Z   bestimmte Version statt der neuesten
#   --yes              keine Rückfragen (Standardwerte bzw. Optionen verwenden)
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
VERSION=""
YES=0

while [ $# -gt 0 ]; do
	case "$1" in
		--dir) DIR="$2"; shift 2 ;;
		--mode) MODE="$2"; shift 2 ;;
		--domain) DOMAIN="$2"; MODE="${MODE:-https}"; shift 2 ;;
		--url) URL="$2"; MODE="${MODE:-proxy}"; shift 2 ;;
		--version) VERSION="$2"; shift 2 ;;
		--yes|-y) YES=1; shift ;;
		-h|--help) sed -n '2,20p' "$0" 2>/dev/null || true; exit 0 ;;
		*) echo "Unbekannte Option: $1" >&2; exit 2 ;;
	esac
done

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

# --- 2. Version und Dateien -----------------------------------------------------------------------
if [ -z "$VERSION" ]; then
	VERSION=$(curl -fsSL "https://api.github.com/repos/$REPO/releases/latest" 2>/dev/null | sed -n 's/.*"tag_name": *"\([^"]*\)".*/\1/p' | head -n1 || true)
	[ -n "$VERSION" ] || { warn "Neueste Version nicht ermittelbar, verwende den Stand von main."; VERSION=main; }
fi
REF="$VERSION"
info "Version: $VERSION"

mkdir -p "$DIR/deploy" "$DIR/backups"
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

# --- 3. .env --------------------------------------------------------------------------------------
if [ -f .env ]; then
	info "Vorhandene .env wird beibehalten."
	[ -n "$(env_get UPDATER_TOKEN)" ] || { env_set UPDATER_TOKEN "$(secret 32)"; info "UPDATER_TOKEN ergänzt (Update per Klick)."; }
	if [ -n "$DOMAIN" ]; then
		env_set COMPOSE_PROFILES https; env_set YLM_DOMAIN "$DOMAIN"; env_set YLM_PUBLIC_URL "https://$DOMAIN"
		info "HTTPS für $DOMAIN eingestellt."
	fi
else
	if curl -fsS -m 3 http://127.0.0.1:8080/healthz >/dev/null 2>&1; then
		fail "Auf Port 8080 läuft schon eine Installation (anderer Ordner?). Bitte dort arbeiten (--dir) oder sie vorher stoppen (docker compose down)."
	fi
	if [ -z "$MODE" ]; then
		echo
		bold "Wie soll die Plattform erreichbar sein?"
		info "1) Eigene Domain mit automatischem HTTPS (Server/VPS, empfohlen)"
		info "2) Hinter einem vorhandenen Reverse Proxy"
		info "3) Nur auf diesem Rechner (http://localhost:8080)"
		case "$(ask "Auswahl" "1")" in 2) MODE=proxy ;; 3) MODE=local ;; *) MODE=https ;; esac
	fi
	case "$MODE" in
		https)
			[ -n "$DOMAIN" ] || DOMAIN=$(ask "Domain (DNS-Eintrag muss auf diesen Server zeigen)" "")
			[ -n "$DOMAIN" ] || fail "Ohne Domain kein HTTPS. Mit --domain angeben oder --mode local/proxy wählen."
			DOMAIN="${DOMAIN#https://}"; DOMAIN="${DOMAIN%%/*}"
			URL="https://$DOMAIN" ;;
		proxy)
			[ -n "$URL" ] || URL=$(ask "Öffentliche HTTPS-Adresse" "https://ylm.example.com")
			case "$URL" in https://*) ;; *) fail "Die öffentliche Adresse muss mit https:// beginnen (Passkeys und Entra verlangen HTTPS)." ;; esac
			URL="${URL%/}" ;;
		local) URL="http://localhost:8080" ;;
		*) fail "Unbekannter Modus: $MODE (https, proxy oder local)" ;;
	esac
	{
		echo "# Angelegt von install.sh am $(date '+%Y-%m-%d %H:%M'). Erklärung aller Werte: .env.example"
		echo "COMPOSE_PROJECT_NAME=ylm"
		echo "POSTGRES_PASSWORD=$(secret 24)"
		echo "YLM_PUBLIC_URL=$URL"
		echo "UPDATER_TOKEN=$(secret 32)"
		if [ "$MODE" = https ]; then echo "COMPOSE_PROFILES=https"; echo "YLM_DOMAIN=$DOMAIN"; fi
		echo "TZ=$( (timedatectl show -p Timezone --value 2>/dev/null || cat /etc/timezone 2>/dev/null) | head -n1 | grep . || echo Europe/Berlin)"
	} > .env
	chmod 600 .env
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
		warn "$DOMAIN lässt sich (noch) nicht auflösen. Let's Encrypt klappt erst, wenn der DNS-Eintrag auf diesen Server zeigt."
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
info "Installationsordner: $DIR (Einstellungen in .env, Sicherungen in backups/)"
info "Status: docker compose ps   Protokoll: docker compose logs -f server"
info "Updates: in der Oberfläche unter Globale Einstellungen → Version und Updates, oder dieses Skript erneut ausführen."
