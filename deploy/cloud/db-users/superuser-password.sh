#!/bin/sh
# NFR-18, NFR-05, IADR-0706: Postgres の Pod の中（postgres の利用者＝uid 70）で走らせる手順。超利用者のパスワードを、
# OpenBao で作り直した値（Secret postgres の password。Pod にファイルで置いたもの。ESO と kubelet が書き換える）へ合わせる。
# 手元の入口は rotate-superuser.sh（kubectl exec -i … sh -s -- <段> < このファイル）。
#
#   check … 読むだけ。Secret の値で入れれば 0、Pod の env の値（起動の時の値）でだけ入れれば 3、どちらでも入れなければ 4。
#   apply … Secret の値で入れれば何もしない（0）。env の値で入れれば、Secret の値へ合わせて（ALTER ROLE）確かめる。どちらでも入れなければ 4（何も変えない）。
#
# 🔴 値は Pod の外へ出ない。ファイルと env から読み、psql へは env で渡す（引数・出力・サーバのログに出さない）。接続はソケットだけ。
set -eu
MODE="${1:-}"
case "$MODE" in check | apply) ;; *) echo "ERROR: 段は check か apply" >&2; exit 2 ;; esac
NEW_FILE="${PG_SUPERUSER_PASSWORD_FILE:-/etc/postgres-superuser-secret/password}"
SOCK="${PG_SOCKET_DIR:-/var/run/postgresql}"
: "${POSTGRES_USER:?}" "${POSTGRES_PASSWORD:?}"
[ -s "$NEW_FILE" ] || { echo "ERROR: Secret の値のファイル（$NEW_FILE）が無い" >&2; exit 1; }

can_login() { PGPASSWORD="$1" psql -X -q -tA -h "$SOCK" -U "$POSTGRES_USER" -d postgres -w -c 'select 1' >/dev/null 2>&1; }

NEW="$(cat "$NEW_FILE")"
if can_login "$NEW"; then
  echo "superuser: Secret の値で入れる（合っている。何もしない）"
  exit 0
fi
if ! can_login "$POSTGRES_PASSWORD"; then
  echo "ERROR: Secret の値でも、Pod の env の値（起動の時の値）でも入れない。何も変えない（手順書 §超利用者・戻し方）" >&2
  exit 4
fi
if [ "$MODE" = check ]; then
  echo "superuser: Pod の env の値でだけ入れる（Secret の値と違う。apply で合わせる）"
  exit 3
fi
PGPASSWORD="$POSTGRES_PASSWORD" PG_NEW_PASSWORD="$NEW" psql -X -q -v ON_ERROR_STOP=1 -v VERBOSITY=terse -v SHOW_CONTEXT=never \
  -h "$SOCK" -U "$POSTGRES_USER" -d postgres -w <<'SQL'
SET log_statement = 'none';
SET log_min_error_statement = 'panic';
SET log_min_duration_statement = -1;
SET client_min_messages = 'warning';
\getenv new_password PG_NEW_PASSWORD
ALTER ROLE CURRENT_USER WITH PASSWORD :'new_password';
SQL
can_login "$NEW" || { echo "ERROR: 合わせた後に Secret の値で入れない" >&2; exit 1; }
echo "superuser: Secret の値へ合わせた（この Pod の env は起動の時の値のまま。次の作り直しの前に Postgres を作り直す）"
