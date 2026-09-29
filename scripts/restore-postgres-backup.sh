#!/usr/bin/env sh
set -eu

if [ "$#" -ne 1 ]; then
  echo "Usage: scripts/restore-postgres-backup.sh /path/to/kivra-backup.dump" >&2
  exit 64
fi

backup_path="$1"
if [ ! -f "$backup_path" ]; then
  echo "Backup file not found: $backup_path" >&2
  exit 66
fi

set -a
. ./.env
set +a

echo "This will replace all data in database '$POSTGRES_DB'."
echo "Create/export a fresh backup before continuing."
printf "Type RESTORE to continue: "
read confirmation
if [ "$confirmation" != "RESTORE" ]; then
  echo "Restore cancelled."
  exit 1
fi

docker compose cp "$backup_path" postgres:/tmp/kivra-restore.dump
docker compose exec -T postgres sh -c '
  set -eu
  export PGPASSWORD="$POSTGRES_PASSWORD"
  dropdb -U "$POSTGRES_USER" --if-exists "$POSTGRES_DB"
  createdb -U "$POSTGRES_USER" "$POSTGRES_DB"
  pg_restore -U "$POSTGRES_USER" -d "$POSTGRES_DB" --no-owner --no-acl --clean --if-exists /tmp/kivra-restore.dump
  rm -f /tmp/kivra-restore.dump
'

echo "PostgreSQL database restored from: $backup_path"
