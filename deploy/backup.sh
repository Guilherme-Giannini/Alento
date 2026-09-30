#!/usr/bin/env bash
# Backup diário criptografado do PostgreSQL para Cloudflare R2 / Backblaze B2 (via rclone).
# Cron na VPS:  15 3 * * * cd /opt/alento && ./deploy/backup.sh >> /var/log/alento-backup.log 2>&1
set -euo pipefail
cd "$(dirname "$0")/.."
source .env

ARQUIVO="alento-$(date +%Y-%m-%d_%H%M).sql.gz.gpg"
mkdir -p deploy/backups

docker compose exec -T db pg_dump -U alento -d alento --no-owner \
  | gzip -9 \
  | gpg --batch --yes --symmetric --cipher-algo AES256 --passphrase "$BACKUP_GPG_SENHA" \
  > "deploy/backups/$ARQUIVO"

rclone copy "deploy/backups/$ARQUIVO" "$RCLONE_DESTINO/"

# Mantém 7 dias localmente; o bucket guarda o histórico (configure lifecycle de 35+ dias).
find deploy/backups -name 'alento-*.gpg' -mtime +7 -delete
echo "$(date -Is) backup ok: $ARQUIVO"

# Restaurar:
# gpg --batch --passphrase "$BACKUP_GPG_SENHA" -d arquivo.sql.gz.gpg | gunzip | docker compose exec -T db psql -U alento -d alento
