#!/usr/bin/env bash
# Dumps the Drive-In database and uploads it to S3 (kept 30 days by a bucket
# lifecycle rule). Installed as /usr/local/bin/drive-in-backup by deploy.sh and
# run nightly by drive-in-backup.timer.
# Usage: drive-in-backup <ops-bucket>
set -euo pipefail

BUCKET="$1"
STAMP=$(date -u +%Y-%m-%dT%H%M%SZ)
cd /opt/drive-in

docker compose -f docker-compose.prod.yml --env-file .env exec -T postgres \
  pg_dump -U drivein --format=custom drivein \
  | aws s3 cp - "s3://$BUCKET/backups/drive-in-$STAMP.dump" --region us-east-1

echo "Backed up to s3://$BUCKET/backups/drive-in-$STAMP.dump"

# Tell VictoriaMetrics, so Grafana can alert when backups stop. Not fatal: the backup itself worked.
curl -fsS --max-time 10 --data-binary "drivein_backup_last_success_timestamp_seconds $(date +%s)" \
  http://127.0.0.1:8428/api/v1/import/prometheus || echo "Couldn't record the backup time in VictoriaMetrics" >&2
