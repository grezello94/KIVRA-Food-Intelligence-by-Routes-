#!/usr/bin/env sh
set -eu

destination="${1:-./postgres-backups}"
mkdir -p "$destination"
destination="$(cd "$destination" && pwd)"

docker compose run --rm \
  -v "$destination:/export" \
  postgres-backup \
  sh -c 'cp -av /backups/*.dump /export/ 2>/dev/null || true'

echo "Backups exported to: $destination"
