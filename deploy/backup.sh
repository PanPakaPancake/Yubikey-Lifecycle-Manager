#!/bin/sh
# Datensicherung für Yubikey Lifecycle Manager. Läuft im Compose-Dienst "backup" (Image postgres:16, Debian,
# bringt pg_dump und openssl mit).
#
#   sh /backup.sh                 Dienst: sichert täglich um BACKUP_TIME (Standard 02:30, "off" = nie)
#   sh /backup.sh now             sofort sichern
#   sh /backup.sh list            vorhandene Sicherungen anzeigen
#   sh /backup.sh restore DATEI   Sicherung einspielen (Server vorher stoppen: docker compose stop server)
#
# Eine Sicherung ist ein Archiv ylm-JJJJMMTT-HHMMSS.tar.gz mit
#   db.dump    PostgreSQL-Dump (pg_dump -Fc)
#   data.tar   Volume "data": App-Zertifikat (verschlüsselt), Data-Protection-Schlüssel, Geheimnisse
# Das Archiv enthält damit alles, was die Plattform braucht, auch den Schlüssel zum App-Zertifikat, mit dem die
# Plattform in jedem angebundenen Tenant Anmeldemethoden verwalten kann.
#
# Mit BACKUP_PASSPHRASE (in .env) wird das Archiv verschlüsselt: ylm-JJJJMMTT-HHMMSS.tar.gz.enc
# (AES-256-CBC, Schlüssel per PBKDF2-SHA256 mit 600 000 Runden, OpenSSL-Format). Die Passphrase gehört zusätzlich
# in einen Passwortmanager: Ohne sie lässt sich keine Sicherung mehr einspielen.
# Von Hand entschlüsseln: openssl enc -d -aes-256-cbc -pbkdf2 -iter 600000 -md sha256 -in DATEI.enc -out DATEI.tar.gz
set -eu

DIR=/backups
KEEP_DAYS="${BACKUP_KEEP_DAYS:-14}"
AT="${BACKUP_TIME:-02:30}"
PASSPHRASE="${BACKUP_PASSPHRASE:-}"
export PGHOST=db PGUSER=ylm PGDATABASE=ylm PGPASSWORD="${POSTGRES_PASSWORD:?POSTGRES_PASSWORD fehlt}"
umask 077

log() { echo "$(date '+%Y-%m-%d %H:%M:%S') $*"; }

need_openssl() {
	command -v openssl >/dev/null 2>&1 && return 0
	log "Für verschlüsselte Sicherungen fehlt openssl. Bitte das Image postgres:16 aus der docker-compose.yml der aktuellen Version verwenden."
	exit 1
}

encrypt() { openssl enc -aes-256-cbc -pbkdf2 -iter 600000 -md sha256 -salt -pass env:BACKUP_PASSPHRASE; }
decrypt() { openssl enc -d -aes-256-cbc -pbkdf2 -iter 600000 -md sha256 -pass env:BACKUP_PASSPHRASE; }

backup() {
	mkdir -p "$DIR"
	ts=$(date +%Y%m%d-%H%M%S)
	tmp=$(mktemp -d)
	trap 'rm -rf "$tmp"' EXIT
	pg_dump -Fc -f "$tmp/db.dump"
	tar -C /data -cf "$tmp/data.tar" .
	date -Iseconds > "$tmp/created.txt"
	name="ylm-$ts.tar.gz"
	if [ -n "$PASSPHRASE" ]; then
		need_openssl
		name="$name.enc"
		tar -C "$tmp" -czf - db.dump data.tar created.txt | encrypt > "$DIR/.ylm-$ts.part"
	else
		tar -C "$tmp" -czf "$DIR/.ylm-$ts.part" db.dump data.tar created.txt
	fi
	mv "$DIR/.ylm-$ts.part" "$DIR/$name"
	rm -rf "$tmp"
	trap - EXIT
	log "Sicherung erstellt: $name ($(du -h "$DIR/$name" | cut -f1))"
	[ -n "$PASSPHRASE" ] || log "Hinweis: Die Sicherung ist unverschlüsselt. BACKUP_PASSPHRASE in .env setzen (docs/BETRIEB.md, Datensicherung)."
	if [ "$KEEP_DAYS" -gt 0 ] 2>/dev/null; then
		find "$DIR" -maxdepth 1 \( -name 'ylm-*.tar.gz' -o -name 'ylm-*.tar.gz.enc' \) -mtime +"$KEEP_DAYS" -print -exec rm -f {} \; | sed 's/^/Alte Sicherung entfernt: /'
	fi
}

restore() {
	file="${1:?Bitte die Sicherung angeben, z. B.: restore ylm-20261119-023000.tar.gz.enc}"
	[ -f "$file" ] || file="$DIR/$file"
	[ -f "$file" ] || { log "Datei nicht gefunden: $1 (gesucht auch in $DIR)"; exit 1; }

	others=$(psql -Atc "select count(*) from pg_stat_activity where datname = 'ylm' and pid <> pg_backend_pid()")
	if [ "$others" != "0" ] && [ "${2:-}" != "--force" ]; then
		log "Die Datenbank wird noch benutzt ($others Verbindungen). Erst den Server stoppen: docker compose stop server"
		exit 1
	fi

	tmp=$(mktemp -d)
	trap 'rm -rf "$tmp"' EXIT
	case "$file" in
		*.enc)
			[ -n "$PASSPHRASE" ] || { log "Die Sicherung ist verschlüsselt. Bitte BACKUP_PASSPHRASE in .env setzen oder mitgeben: docker compose exec -e BACKUP_PASSPHRASE='...' backup sh /backup.sh restore DATEI"; exit 1; }
			need_openssl
			decrypt < "$file" > "$tmp/archive.tar.gz" || { log "Entschlüsseln fehlgeschlagen: falsche Passphrase oder beschädigte Datei."; exit 1; }
			tar -xzf "$tmp/archive.tar.gz" -C "$tmp" || { log "Archiv beschädigt oder falsche Passphrase: $file"; exit 1; }
			rm -f "$tmp/archive.tar.gz"
			;;
		*) tar -xzf "$file" -C "$tmp" ;;
	esac
	[ -f "$tmp/db.dump" ] && [ -f "$tmp/data.tar" ] || { log "Keine gültige YLM-Sicherung: $file"; exit 1; }

	log "Spiele Datenbank ein ..."
	pg_restore --clean --if-exists --no-owner --single-transaction -d ylm "$tmp/db.dump"
	log "Spiele Volume data ein ..."
	find /data -mindepth 1 -maxdepth 1 -exec rm -rf {} +
	tar -C /data -xpf "$tmp/data.tar"
	log "Wiederhergestellt aus $(basename "$file") (erstellt $(cat "$tmp/created.txt" 2>/dev/null || echo unbekannt)). Jetzt: docker compose start server"
}

case "${1:-serve}" in
	now) backup ;;
	list)
		found=0
		for f in "$DIR"/ylm-*.tar.gz "$DIR"/ylm-*.tar.gz.enc; do
			[ -f "$f" ] || continue
			case "$f" in *.enc) kind="verschlüsselt" ;; *) kind="unverschlüsselt" ;; esac
			found=1; echo "$(basename "$f")  $(du -h "$f" | cut -f1)  $kind"
		done
		[ "$found" = 1 ] || echo "Noch keine Sicherungen in $DIR."
		;;
	restore) shift; restore "$@" ;;
	serve)
		if [ "$AT" = "off" ]; then
			log "Automatische Sicherung ist abgeschaltet (BACKUP_TIME=off)."
			exec sleep infinity
		fi
		case "$AT" in [0-2][0-9]:[0-5][0-9]) ;; *) log "BACKUP_TIME muss HH:MM sein, ist aber '$AT'."; exit 1 ;; esac
		[ -z "$PASSPHRASE" ] || need_openssl
		if [ -n "$PASSPHRASE" ]; then mode="verschlüsselt"; else mode="UNVERSCHLÜSSELT (BACKUP_PASSPHRASE fehlt)"; fi
		log "Automatische Sicherung täglich um $AT nach $DIR, Aufbewahrung $KEEP_DAYS Tage, $mode."
		last=""
		while true; do
			if [ "$(date +%H:%M)" = "$AT" ] && [ "$last" != "$(date +%F)" ]; then
				last=$(date +%F)
				( backup ) || log "Sicherung fehlgeschlagen."
			fi
			sleep 20
		done
		;;
	*) echo "Aufruf: backup.sh [now|list|restore DATEI [--force]]"; exit 2 ;;
esac
