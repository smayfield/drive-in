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
cat > .env <<EOF
REGISTRY=$REGISTRY
TAG=$TAG
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

log "Starting PostgreSQL"
compose up -d --wait postgres

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
