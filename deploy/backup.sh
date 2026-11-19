#!/bin/sh
# Datensicherung für Yubikey Lifecycle Manager. Läuft im Compose-Dienst "backup" (Image postgres:16-alpine).
#
#   sh /backup.sh                 Dienst: sichert täglich um BACKUP_TIME (Standard 02:30, "off" = nie)
#   sh /backup.sh now             sofort sichern
#   sh /backup.sh list            vorhandene Sicherungen anzeigen
#   sh /backup.sh restore DATEI   Sicherung einspielen (Server vorher stoppen: docker compose stop server)
#
# Eine Sicherung ist ein Archiv ylm-JJJJMMTT-HHMMSS.tar.gz mit
#   db.dump    PostgreSQL-Dump (pg_dump -Fc)
#   data.tar   Volume "data": App-Zertifikat (verschlüsselt), Data-Protection-Schlüssel, Geheimnisse
# Das Archiv enthält damit alles, was die Plattform braucht. Es ist so schützenswert wie der Server selbst.
set -eu

DIR=/backups
KEEP_DAYS="${BACKUP_KEEP_DAYS:-14}"
AT="${BACKUP_TIME:-02:30}"
export PGHOST=db PGUSER=ylm PGDATABASE=ylm PGPASSWORD="${POSTGRES_PASSWORD:?POSTGRES_PASSWORD fehlt}"
umask 077

log() { echo "$(date '+%Y-%m-%d %H:%M:%S') $*"; }

backup() {
	mkdir -p "$DIR"
	ts=$(date +%Y%m%d-%H%M%S)
	tmp=$(mktemp -d)
	trap 'rm -rf "$tmp"' EXIT
	pg_dump -Fc -f "$tmp/db.dump"
	tar -C /data -cf "$tmp/data.tar" .
	date -Iseconds > "$tmp/created.txt"
	tar -C "$tmp" -czf "$DIR/.ylm-$ts.part" db.dump data.tar created.txt
	mv "$DIR/.ylm-$ts.part" "$DIR/ylm-$ts.tar.gz"
	rm -rf "$tmp"
	trap - EXIT
	log "Sicherung erstellt: ylm-$ts.tar.gz ($(du -h "$DIR/ylm-$ts.tar.gz" | cut -f1))"
	if [ "$KEEP_DAYS" -gt 0 ] 2>/dev/null; then
		find "$DIR" -maxdepth 1 -name 'ylm-*.tar.gz' -mtime +"$KEEP_DAYS" -print -exec rm -f {} \; | sed 's/^/Alte Sicherung entfernt: /'
	fi
}

restore() {
	file="${1:?Bitte die Sicherung angeben, z. B.: restore ylm-20261119-023000.tar.gz}"
	[ -f "$file" ] || file="$DIR/$file"
	[ -f "$file" ] || { log "Datei nicht gefunden: $1 (gesucht auch in $DIR)"; exit 1; }

	others=$(psql -Atc "select count(*) from pg_stat_activity where datname = 'ylm' and pid <> pg_backend_pid()")
	if [ "$others" != "0" ] && [ "${2:-}" != "--force" ]; then
		log "Die Datenbank wird noch benutzt ($others Verbindungen). Erst den Server stoppen: docker compose stop server"
		exit 1
	fi

	tmp=$(mktemp -d)
	trap 'rm -rf "$tmp"' EXIT
	tar -xzf "$file" -C "$tmp"
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
		for f in "$DIR"/ylm-*.tar.gz; do
			[ -f "$f" ] || continue
			found=1; echo "$(basename "$f")  $(du -h "$f" | cut -f1)"
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
		log "Automatische Sicherung täglich um $AT nach $DIR, Aufbewahrung $KEEP_DAYS Tage."
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
