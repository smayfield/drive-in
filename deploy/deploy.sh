#!/usr/bin/env bash
# Runs on the EC2 server (via SSM Run Command from GitHub Actions).
# Usage: deploy.sh <image-tag> <ecr-registry> <ops-bucket>
set -euo pipefail

TAG="$1"
REGISTRY="$2"
BUCKET="$3"
REGION="us-east-1"
DIR=/opt/drive-in
cd "$DIR"

log() { echo "==> $*"; }

param() {
  aws ssm get-parameter --region "$REGION" --name "/drive-in/$1" --with-decryption \
    --query Parameter.Value --output text
}

# serve-apex is managed by the CloudFormation stack (ServeApex parameter).
if [ "$(param serve-apex)" = "true" ]; then PUBLIC_HOST=drive-in.online; else PUBLIC_HOST=app.drive-in.online; fi

log "Writing .env from SSM Parameter Store"
umask 077
# Assigned here, not in the heredoc below, so a missing parameter stops the deploy (set -e) before anything changes.
GRAFANA_DB_PASSWORD=$(param grafana-db-password)
ALERTS_TOPIC_ARN=$(param alerts-topic-arn)
# Grafana's built-in admin password is never used (no login form or basic auth); keep it random.
GRAFANA_ADMIN_PASSWORD=$(grep -s '^GRAFANA_ADMIN_PASSWORD=' .env | cut -d= -f2- || true)
GRAFANA_ADMIN_PASSWORD=${GRAFANA_ADMIN_PASSWORD:-$(head -c 32 /dev/urandom | od -An -tx1 | tr -d ' \n')}
# PostgreSQL's image is tagged by its Dockerfile's hash, as the Deploy workflow tags it.
PG_TAG="pg-$(sha256sum "$DIR/postgres/Dockerfile" | cut -c1-12)"
cat > .env <<EOF
REGISTRY=$REGISTRY
TAG=$TAG
PG_TAG=$PG_TAG
OPS_BUCKET=$BUCKET
DB_PASSWORD=$(param db-password)
GOOGLE_CLIENT_ID=$(param google-client-id)
GOOGLE_CLIENT_SECRET=$(param google-client-secret)
ADMIN_EMAIL=$(param admin-email)
GEOCODING_CONTACT_EMAIL=$(param geocoding-contact-email 2>/dev/null || true)
PUBLIC_HOST=$PUBLIC_HOST
GRAFANA_DB_PASSWORD=$GRAFANA_DB_PASSWORD
GRAFANA_ADMIN_PASSWORD=$GRAFANA_ADMIN_PASSWORD
ALERTS_TOPIC_ARN=$ALERTS_TOPIC_ARN
EOF
umask 022

if [ "$PUBLIC_HOST" = "drive-in.online" ]; then
  log "Serving drive-in.online (live)"
  install -D -m 0644 Caddyfile.live caddy/Caddyfile
else
  log "Serving app.drive-in.online only (staging)"
  install -D -m 0644 Caddyfile.staging caddy/Caddyfile
fi

compose() { docker compose -f docker-compose.prod.yml --env-file .env "$@"; }

log "Logging in to ECR"
aws ecr get-login-password --region "$REGION" | docker login --username AWS --password-stdin "$REGISTRY"

log "Pulling images for $TAG"
compose --profile migrate pull

# Containers log to CloudWatch (the awslogs driver), and one that can't won't start. Check now, while the old version
# is still running: this fails if the stack's log group or the instance role's log permissions aren't there yet.
log "Checking CloudWatch Logs access"
docker run --rm --log-driver awslogs --log-opt awslogs-region="$REGION" --log-opt awslogs-group=/drive-in/containers \
  --log-opt 'tag=deploy-check/{{.ID}}' caddy:2 true \
  || { echo "Can't write to the /drive-in/containers log group. Apply infra/app.yml first (see the README)." >&2; exit 1; }

log "Starting PostgreSQL"
compose up -d --wait postgres

# pgBackRest's repository in S3 (point-in-time recovery). stanza-create is a no-op once it exists. A failure here doesn't
# stop the deploy: the database still works, WAL waits in pg_wal, and the "WAL archiving failing" alert fires.
log "Checking the WAL archive"
compose exec -T -u postgres postgres pgbackrest stanza-create --log-level-console=warn \
  || echo "WARNING: pgBackRest stanza-create failed; WAL isn't being archived. See README: Backups and restores." >&2

log "Applying EF Core migrations"
compose --profile migrate run --rm migrate

log "Granting Grafana's read-only database role"
compose exec -T -e "GRAFANA_DB_PASSWORD=$GRAFANA_DB_PASSWORD" postgres psql -q -U drivein -d drivein -f - < grafana-ro.sql

log "Starting apps"
compose up -d --remove-orphans caddy web victoriametrics grafana node-exporter postgres-exporter
# Grafana reads its provisioning (dashboards, alert rules) at startup; restart it so changed files apply.
compose restart grafana
# Caddy doesn't watch its config file; reload picks up a changed Caddyfile without downtime.
compose exec -T caddy caddy reload --config /etc/caddy/Caddyfile --adapter caddyfile || true

# Archived WAL is only useful on top of a base backup; take the first one now if there's none yet (later ones are
# backup.sh's). Not fatal, like stanza-create above.
if ! compose exec -T -u postgres postgres pgbackrest info --output=json 2>/dev/null | grep -q '"label"'; then
  log "Taking the first base backup"
  compose exec -T -u postgres postgres pgbackrest backup --type=full --log-level-console=info \
    || echo "WARNING: the first base backup failed; the nightly backup will try again." >&2
fi

log "Installing nightly backup timer"
install -m 0755 "$DIR/backup.sh" /usr/local/bin/drive-in-backup
cat > /etc/systemd/system/drive-in-backup.service <<EOF
[Unit]
Description=Back up the Drive-In database to S3
[Service]
Type=oneshot
ExecStart=/usr/local/bin/drive-in-backup $BUCKET
EOF
cat > /etc/systemd/system/drive-in-backup.timer <<'EOF'
[Unit]
Description=Nightly Drive-In database backup
[Timer]
OnCalendar=*-*-* 07:15:00
Persistent=true
[Install]
WantedBy=timers.target
EOF
systemctl daemon-reload
systemctl enable --now drive-in-backup.timer

log "Cleaning up old images"
docker image prune -af --filter "until=168h" >/dev/null || true

log "Deployed $TAG"
compose ps
