#!/usr/bin/env bash
# Restores Drive-In's database (or its Data Protection keys) from the backups in S3. Run on the server, as root, from an
# SSM session: `sudo bash /opt/drive-in/restore.sh <command>`. See the README, "Backups and restores".
#
#   restore.sh list                      what there is to restore from
#   restore.sh drill [<time>|latest]     restore into a scratch database next to the live one and compare them
#                                        (no downtime; the monthly restore drill)
#   restore.sh pitr <time>|latest        point-in-time recovery of the live database, e.g. "2026-10-02 14:05:00+00"
#   restore.sh dump [<s3 key>|latest]    replace the live database with a nightly pg_dump
#   restore.sh dpkeys [<s3 key>|latest]  put back the Data Protection keys (sign-in cookies, emailed links)
#
# pitr, dump and dpkeys stop the web app until they finish and ask for confirmation (or pass --yes).
set -euo pipefail

DIR=${DRIVEIN_DIR:-/opt/drive-in}
COMPOSE_FILE=${DRIVEIN_COMPOSE_FILE:-docker-compose.prod.yml}
PROJECT=${DRIVEIN_PROJECT:-drive-in}
REGION=us-east-1
cd "$DIR"

log() { echo "==> $*"; }
die() { echo "restore: $*" >&2; exit 1; }
compose() { docker compose -f "$COMPOSE_FILE" --env-file .env "$@"; }
psql_live() { compose exec -T postgres psql -qtAX -U drivein -d "${2:-drivein}" -c "$1"; }

YES=0
ARGS=()
for a in "$@"; do
  if [ "$a" = "--yes" ]; then YES=1; else ARGS+=("$a"); fi
done
set -- "${ARGS[@]+"${ARGS[@]}"}"
CMD=${1:-}
TARGET=${2:-latest}

BUCKET=$(grep -s '^OPS_BUCKET=' .env | cut -d= -f2- || true)
[ -n "$BUCKET" ] || die "OPS_BUCKET isn't in $DIR/.env (deploy.sh writes it)."

confirm() {
  [ "$YES" = 1 ] && return 0
  echo "$1"
  read -r -p "Type 'restore' to go ahead: " answer
  [ "$answer" = "restore" ] || die "Cancelled."
}

# The web app's services (one, or blue and green) that are running now, so they can be started again afterwards.
running_web() { compose ps --status running --services | grep '^web' || true; }
stop_web() {
  WEB_SERVICES=$(running_web)
  if [ -n "$WEB_SERVICES" ]; then
    log "Stopping $(echo "$WEB_SERVICES" | tr '\n' ' ')"
    # shellcheck disable=SC2086 # one service name per word
    compose stop $WEB_SERVICES
  fi
}
start_web() {
  if [ -n "${WEB_SERVICES:-}" ]; then
    log "Starting $(echo "$WEB_SERVICES" | tr '\n' ' ')"
    # shellcheck disable=SC2086
    compose start $WEB_SERVICES
  fi
}

wait_promoted() {
  # $1: a command that runs psql with the query as its last argument.
  for _ in $(seq 1 600); do
    if [ "$("$@" "select pg_is_in_recovery()" 2>/dev/null)" = "f" ]; then return 0; fi
    sleep 1
  done
  die "PostgreSQL didn't finish recovery in 10 minutes. Check: docker logs <container>."
}

latest_key() {
  # $1: object name prefix under backups/ (drive-in- or dpkeys-)
  aws s3 ls "s3://$BUCKET/backups/$1" --region "$REGION" | awk '{print $4}' | sort | tail -1 | sed 's#^#backups/#'
}

# pgBackRest's recovery target: the end of the archive, or a moment.
# A time target stops there and promotes (becomes writable) instead of pausing.
if [ "$TARGET" = latest ]; then
  TARGET_ARGS=(--type=default)
else
  TARGET_ARGS=(--type=time "--target=$TARGET" --target-action=promote)
fi

summary_sql="select 'theaters=' || (select count(*) from theaters) || ' users=' || (select count(*) from users)
  || ' tickets=' || (select count(*) from tickets) || ' last sale=' || coalesce((select max(sold_at)::text from tickets), '-')"

case "$CMD" in
  list)
    log "pgBackRest (point-in-time recovery: from the oldest backup's start to the latest archived WAL)"
    compose exec -T -u postgres postgres pgbackrest info
    log "Nightly pg_dumps (newest last)"
    aws s3 ls "s3://$BUCKET/backups/drive-in-" --region "$REGION" | tail -5
    log "Data Protection keys (newest last)"
    aws s3 ls "s3://$BUCKET/backups/dpkeys-" --region "$REGION" | tail -3
    ;;

  drill)
    # A scratch volume and a throwaway PostgreSQL with the live one's pgBackRest settings, on Docker's default bridge
    # (it can reach S3 for WAL, but not the app). archive-mode=off: it must never push WAL to the live repository.
    VOLUME=${PROJECT}_pgdrill
    NAME=${PROJECT}-pgdrill
    LIVE=$(compose ps -q postgres)
    [ -n "$LIVE" ] || die "The postgres service isn't running."
    IMAGE=$(docker inspect --format '{{.Config.Image}}' "$LIVE")
    ENVFILE=$(mktemp)
    cleanup() { docker rm -f "$NAME" >/dev/null 2>&1 || true; docker volume rm "$VOLUME" >/dev/null 2>&1 || true; rm -f "$ENVFILE"; }
    trap cleanup EXIT
    cleanup
    docker inspect --format '{{range .Config.Env}}{{println .}}{{end}}' "$LIVE" | grep -E '^(PGBACKREST_|POSTGRES_USER=)' > "$ENVFILE"
    echo "PGBACKREST_PG1_PATH=/drill/data" >> "$ENVFILE"
    echo "PGDATA=/drill/data" >> "$ENVFILE"
    echo "PGBACKREST_LOG_LEVEL_CONSOLE=info" >> "$ENVFILE"

    log "Restoring ($TARGET) into the scratch volume $VOLUME"
    docker run --rm -v "$VOLUME:/drill" "$IMAGE" install -d -o postgres -g postgres -m 0700 /drill/data
    docker run --rm --network "${DRIVEIN_DRILL_NETWORK:-bridge}" -u postgres --env-file "$ENVFILE" -v "$VOLUME:/drill" "$IMAGE" \
      pgbackrest "${TARGET_ARGS[@]}" --archive-mode=off restore
    log "Starting the scratch database (replaying WAL)"
    docker run -d --name "$NAME" --network "${DRIVEIN_DRILL_NETWORK:-bridge}" -u postgres --env-file "$ENVFILE" \
      -v "$VOLUME:/drill" "$IMAGE" postgres >/dev/null
    drill_psql() { docker exec "$NAME" psql -qtAX -U drivein -d drivein -c "$1"; }
    wait_promoted drill_psql
    echo "restored: $(drill_psql "$summary_sql")"
    echo "live:     $(psql_live "$summary_sql")"
    log "Drill done; the scratch database is being removed. Restored counts at or below live, with a last sale close to the target, mean it worked."
    ;;

  pitr)
    [ -n "${2:-}" ] || die "Give a time (\"2026-10-02 14:05:00+00\", UTC) or 'latest'."
    confirm "This replaces the live database with its state at $TARGET. Changes after that are lost (they stay in the archive).
The web app is down until it's done."
    stop_web
    log "Stopping PostgreSQL"
    compose stop postgres
    log "Restoring to $TARGET"
    compose run --rm --no-deps -u postgres --entrypoint pgbackrest postgres \
      --delta "${TARGET_ARGS[@]}" restore
    log "Starting PostgreSQL (replaying WAL)"
    compose up -d postgres
    wait_promoted psql_live
    echo "database: $(psql_live "$summary_sql")"
    # Recovery started a new timeline; a fresh full backup makes it the base for future restores.
    log "Taking a full base backup"
    compose exec -T -u postgres postgres pgbackrest backup --type=full --log-level-console=info \
      || echo "WARNING: the full backup failed; run 'sudo drive-in-backup $BUCKET full' once it's fixed." >&2
    start_web
    log "Restored to $TARGET."
    ;;

  dump)
    KEY=$TARGET
    [ "$KEY" = latest ] && KEY=$(latest_key drive-in-)
    [ -n "$KEY" ] || die "No pg_dump found in s3://$BUCKET/backups/."
    confirm "This replaces the live database with s3://$BUCKET/$KEY. Everything since that dump is lost
(but still restorable with 'restore.sh pitr'). The web app is down until it's done."
    stop_web
    log "Recreating the drivein database"
    psql_live "drop database if exists drivein with (force)" postgres
    psql_live "create database drivein owner drivein" postgres
    log "Loading $KEY"
    aws s3 cp "s3://$BUCKET/$KEY" - --region "$REGION" \
      | compose exec -T postgres pg_restore -U drivein -d drivein --no-owner --role=drivein --exit-on-error
    log "Granting Grafana's read-only role"
    GRAFANA_DB_PASSWORD=$(grep -s '^GRAFANA_DB_PASSWORD=' .env | cut -d= -f2-)
    # Only Grafana's dashboards need it, so a failure here doesn't stop the restore (the next deploy re-runs it).
    compose exec -T -e "GRAFANA_DB_PASSWORD=$GRAFANA_DB_PASSWORD" postgres psql -q -U drivein -d drivein -f - < grafana-ro.sql \
      || echo "WARNING: couldn't grant grafana_ro; the next deploy will." >&2
    echo "database: $(psql_live "$summary_sql")"
    log "Taking a full base backup"
    compose exec -T -u postgres postgres pgbackrest backup --type=full --log-level-console=info \
      || echo "WARNING: the full backup failed; run 'sudo drive-in-backup $BUCKET full' once it's fixed." >&2
    start_web
    log "Restored from $KEY."
    ;;

  dpkeys)
    KEY=$TARGET
    [ "$KEY" = latest ] && KEY=$(latest_key dpkeys-)
    [ -n "$KEY" ] || die "No Data Protection keys backup found in s3://$BUCKET/backups/."
    confirm "This replaces the Data Protection keys with s3://$BUCKET/$KEY. The web app restarts."
    stop_web
    log "Loading $KEY into ${PROJECT}_dpkeys"
    aws s3 cp "s3://$BUCKET/$KEY" - --region "$REGION" \
      | docker run --rm -i -v "${PROJECT}_dpkeys:/keys" caddy:2 sh -c 'rm -rf /keys/* && tar -xzf - -C /keys'
    start_web
    log "Restored the keys from $KEY."
    ;;

  *)
    sed -n '2,13p' "$0"
    exit 2
    ;;
esac
