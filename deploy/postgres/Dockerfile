# PostgreSQL with pgBackRest, for continuous WAL archiving to S3 (point-in-time recovery). The Deploy workflow builds it
# for linux/arm64 and pushes it to ECR as drive-in-postgres:<hash of this file>, only when that tag isn't there yet, so the
# database container is only recreated when this file changes. The version is pinned for the same reason: to take a
# PostgreSQL minor release (security fixes), bump it here through a PR. pgBackRest comes from the PostgreSQL apt
# repository the official image already uses. Its settings are PGBACKREST_* environment variables
# (deploy/docker-compose.prod.yml); see the README's "Backups and restores".
FROM postgres:17.11
RUN apt-get update \
 && apt-get install -y --no-install-recommends pgbackrest ca-certificates \
 && rm -rf /var/lib/apt/lists/* \
 && rm -f /etc/pgbackrest.conf \
 && install -d -o postgres -g postgres -m 0750 /var/log/pgbackrest /var/spool/pgbackrest
