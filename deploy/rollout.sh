#!/usr/bin/env bash
# Blue/green rollout of the web app on the one server, run by deploy.sh after migrations. Two compose services,
# web-blue and web-green, run the same image; one serves at a time. This:
#   1. starts the idle color with the new image and waits until its /readyz passes (it can reach the database);
#   2. points Caddy at it (the "upstream" file next to the Caddyfile) and reloads Caddy, with no dropped requests;
#   3. lets the old color drain for DRAIN_SECONDS (open Blazor circuits, e.g. a checkout in progress), then stops it.
# If the new color never gets ready, or Caddy rejects the switch, it's stopped and the old one keeps serving.
# The two overlap for about DRAIN_SECONDS. That's safe because background jobs only run in the copy holding the
# Postgres jobs lock (IJobLeadership), and migrations must keep working with the previous version (CLAUDE.md).
# Live seat-map updates are in-process, so during the overlap a map open on one copy doesn't see holds made through
# the other until it refreshes. The database still lets only one buyer hold a spot.
set -euo pipefail

DIR=${DRIVEIN_DIR:-/opt/drive-in}
COMPOSE_FILE=${DRIVEIN_COMPOSE_FILE:-docker-compose.prod.yml}
PROJECT=${DRIVEIN_PROJECT:-drive-in}
DRAIN_SECONDS=${DRAIN_SECONDS:-60}
READY_TIMEOUT=${READY_TIMEOUT:-180}
cd "$DIR"

log() { echo "==> $*"; }
compose() { docker compose -f "$COMPOSE_FILE" --env-file .env "$@"; }

# The color Caddy sends traffic to now, if any. (None on a new server, or before the first blue/green deploy, when
# the app ran as the single service "web".)
ACTIVE=$(sed -n 's/^to web-\(blue\|green\):8080$/\1/p' caddy/upstream 2>/dev/null || true)
if [ "$ACTIVE" = blue ]; then NEW=green; else NEW=blue; fi
LEGACY="${PROJECT}-web-1"

# Asks the new copy directly, from a throwaway container on the network it shares with Caddy, so it works whether or not
# Caddy is up yet. Host: localhost is one of the app's AllowedHosts.
ready() {
  docker run --rm --network "${PROJECT}_edge" caddy:2 \
    wget -q -O /dev/null -T 5 --header "Host: localhost" "http://web-$NEW:8080/readyz" >/dev/null 2>&1
}

log "Starting web-$NEW"
compose up -d --no-deps --force-recreate "web-$NEW"
log "Waiting for web-$NEW to be ready"
deadline=$((SECONDS + READY_TIMEOUT))
until ready; do
  if [ "$SECONDS" -ge "$deadline" ]; then
    echo "web-$NEW wasn't ready after ${READY_TIMEOUT}s; leaving ${ACTIVE:+web-}${ACTIVE:-the current app} serving." >&2
    compose logs --tail 80 "web-$NEW" >&2 || true
    compose stop "web-$NEW" || true
    exit 1
  fi
  sleep 2
done

log "Switching Caddy to web-$NEW"
# The upstream and the Caddyfile deploy.sh staged (Caddyfile.next) change together, keeping the old ones to switch back.
mkdir -p caddy
rm -f caddy/upstream.previous caddy/Caddyfile.previous
[ -f caddy/upstream ] && cp caddy/upstream caddy/upstream.previous
[ -f caddy/Caddyfile ] && cp caddy/Caddyfile caddy/Caddyfile.previous
printf 'to web-%s:8080\n' "$NEW" > caddy/upstream.new
mv caddy/upstream.new caddy/upstream
[ -f caddy/Caddyfile.next ] && mv caddy/Caddyfile.next caddy/Caddyfile
# A just-created Caddy needs a moment before its admin API answers, so a few tries; a bad config fails every time.
reload() {
  for _ in 1 2 3 4 5 6 7 8 9 10; do
    compose exec -T caddy caddy reload --config /etc/caddy/Caddyfile --adapter caddyfile && return 0
    sleep 2
  done
  return 1
}
# Creates Caddy on a new server, or recreates it if its service definition changed (it then starts on the new files).
if ! compose up -d --no-deps caddy || ! reload; then
  echo "Caddy rejected the new configuration; switching back." >&2
  if [ -f caddy/upstream.previous ]; then mv caddy/upstream.previous caddy/upstream; else rm -f caddy/upstream; fi
  [ -f caddy/Caddyfile.previous ] && mv caddy/Caddyfile.previous caddy/Caddyfile
  compose up -d --no-deps caddy || true
  reload || true
  compose stop "web-$NEW" || true
  exit 1
fi

OLD=()
[ -n "$ACTIVE" ] && OLD+=("web-$ACTIVE")
if docker ps -a --format '{{.Names}}' | grep -qx "$LEGACY"; then OLD+=("legacy web"); fi
if [ ${#OLD[@]} -gt 0 ]; then
  log "Draining ${OLD[*]} for ${DRAIN_SECONDS}s"
  sleep "$DRAIN_SECONDS"
  # Graceful: in-flight requests finish, circuits close, and the jobs lock is released for the new copy.
  if [ -n "$ACTIVE" ]; then
    compose stop -t 40 "web-$ACTIVE"
    compose rm -f "web-$ACTIVE"
  fi
  if docker ps -a --format '{{.Names}}' | grep -qx "$LEGACY"; then
    docker stop -t 40 "$LEGACY" >/dev/null
    docker rm "$LEGACY" >/dev/null
  fi
fi
log "web-$NEW is serving"
