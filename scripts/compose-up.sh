#!/usr/bin/env bash
# FR-15, IADR-0029 (#144): dev の compose 起動ヘルパ。実 Git コミット ID・日時・適用者を
# 環境変数として compose に渡し、BFF の構成情報 API（/bff/admin/config）が dev でも
# 実バージョンを返せるようにする。引数はそのまま docker compose へ透過する。
#
# 使い方:
#   scripts/compose-up.sh up -d
#   scripts/compose-up.sh up -d bff        # 一部サービスのみ
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
compose_file="$repo_root/deploy/docker-compose.yml"

# git 情報を取得（リポジトリ外・git 不在でも失敗させない。未取得はフォールバックへ委ねる）。
GIT_COMMIT="$(git -C "$repo_root" rev-parse --short HEAD 2>/dev/null || echo dev-local)"
GIT_COMMIT_DATE="$(git -C "$repo_root" log -1 --format=%cI 2>/dev/null || echo '')"
GIT_COMMIT_BY="$(git -C "$repo_root" log -1 --format='%an' 2>/dev/null || echo compose)"
export GIT_COMMIT GIT_COMMIT_DATE GIT_COMMIT_BY

echo "compose-up: GIT_COMMIT=$GIT_COMMIT AppliedAt=$GIT_COMMIT_DATE AppliedBy=$GIT_COMMIT_BY"
exec docker compose -f "$compose_file" "$@"
