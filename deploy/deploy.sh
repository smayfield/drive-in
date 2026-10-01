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

log "Writing .env from SSM Parameter Store"
umask 077
cat > .env <<EOF
REGISTRY=$REGISTRY
TAG=$TAG
DB_PASSWORD=$(param db-password)
GOOGLE_CLIENT_ID=$(param google-client-id)
GOOGLE_CLIENT_SECRET=$(param google-client-secret)
ADMIN_EMAIL=$(param admin-email)
GEOCODING_CONTACT_EMAIL=$(param geocoding-contact-email 2>/dev/null || true)
EOF
umask 022

# serve-apex is managed by the CloudFormation stack (ServeApex parameter).
if [ "$(param serve-apex)" = "true" ]; then
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

log "Starting apps"
compose up -d --remove-orphans caddy web
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
