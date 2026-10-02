#!/usr/bin/env bash
# Nightly backups to S3, installed as /usr/local/bin/drive-in-backup by deploy.sh and run by drive-in-backup.timer:
#   1. a pg_dump of the database (backups/, kept 30 days by a bucket lifecycle rule): simple and independent of pgBackRest;
#   2. a pgBackRest base backup (pitr/; full on Sundays, differential otherwise; pgBackRest keeps four fulls). With the
#      WAL that PostgreSQL archives continuously, it allows a restore to any moment (deploy/restore.sh);
#   3. the Data Protection keys (backups/), without which sign-in cookies and emailed links stop working after a restore
#      onto a new server.
# Each step runs even if an earlier one failed; the script fails if any did. Each success is recorded in VictoriaMetrics
# so Grafana can alert when one stops.
# Usage: drive-in-backup <ops-bucket> [full]   ("full" forces a full base backup, e.g. right after a restore)
set -uo pipefail

BUCKET="$1"
FORCE_FULL="${2:-}"
STAMP=$(date -u +%Y-%m-%dT%H%M%SZ)
cd /opt/drive-in || exit 1

compose() { docker compose -f docker-compose.prod.yml --env-file .env "$@"; }
failed=0

record() {
  # Not fatal: the backup itself worked.
  curl -fsS --max-time 10 --data-binary "$1 $(date +%s)" http://127.0.0.1:8428/api/v1/import/prometheus \
    || echo "Couldn't record $1 in VictoriaMetrics" >&2
}

echo "==> pg_dump"
if compose exec -T postgres pg_dump -U drivein --format=custom drivein \
    | aws s3 cp - "s3://$BUCKET/backups/drive-in-$STAMP.dump" --region us-east-1; then
  echo "Backed up to s3://$BUCKET/backups/drive-in-$STAMP.dump"
  record drivein_backup_last_success_timestamp_seconds
else
  echo "pg_dump backup failed" >&2
  failed=1
fi

echo "==> pgBackRest base backup"
if [ "$FORCE_FULL" = "full" ] || [ "$(date -u +%u)" = "7" ]; then TYPE="full"; else TYPE="diff"; fi
if compose exec -T -u postgres postgres pgbackrest backup --type="$TYPE" --log-level-console=info; then
  record drivein_pitr_backup_last_success_timestamp_seconds
else
  echo "pgBackRest $TYPE backup failed" >&2
  failed=1
fi

echo "==> Data Protection keys"
# Read through a throwaway container (caddy:2 is already on the server and has tar).
if docker run --rm -v drive-in_dpkeys:/keys:ro caddy:2 tar -czf - -C /keys . \
    | aws s3 cp - "s3://$BUCKET/backups/dpkeys-$STAMP.tar.gz" --region us-east-1; then
  echo "Backed up to s3://$BUCKET/backups/dpkeys-$STAMP.tar.gz"
else
  echo "Data Protection keys backup failed" >&2
  failed=1
fi

exit $failed
