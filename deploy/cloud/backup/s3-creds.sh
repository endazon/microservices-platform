#!/usr/bin/env bash
# NFR-05, NFR-18, IADR-0704 決定 1（IADR-0682 の形）: オブジェクトストレージの S3 の鍵を、オーナーの手元で**平文のファイルにも
# シェルの環境にも置かずに**使う。鍵は age で暗号化したファイルだけに持ち、使うときは 1 つのコマンドの環境にだけ載せる。
#
#   bash deploy/cloud/backup/s3-creds.sh store --recipients <age の受取人> --out <鍵>.age
#       → アクセスキーの ID とシークレットを端末から伏せて読み（read -rs）、age で暗号化したファイル（0600）だけを書く。
#   bash deploy/cloud/backup/s3-creds.sh run --creds <鍵>.age --identity <age の識別子> --endpoint <https://…> -- <コマンド> [引数…]
#       → 復号した鍵をこのスクリプトのプロセスの変数へ読み、AWS_* と rclone の objstore の env にして <コマンド> を exec する。
#         鍵が載るのは <コマンド> とその子の環境だけ（呼んだシェルには残らない）。引数・ディスクには載らない。
#
# 鍵は 2 組を別々のファイルに持つ（手順書 cloud-backup-alerts-heartbeat-runbook.md §1）:
#   - 管理の鍵（バケットの作成・Object Lock・lifecycle・確かめ）。手元にだけ置く。クラスタへ入れない。
#   - 書き込みの鍵（クラスタのバックアップの Job が使う。OpenBao へ入れる）。手元でリストア試験にも使う。
# 終了コード: 0＝成功（run は <コマンド> の終了コード） / 2＝引数の誤り・鍵の形が違う / 1＝復号・書き出しの失敗
set -euo pipefail

die() { echo "ERROR: $2" >&2; exit "$1"; }
command -v age >/dev/null 2>&1 || die 1 "age が無い"
CMD="${1:-}"
[ "$#" -ge 1 ] && shift

KEY_SHAPE='^[A-Za-z0-9+/=._-]{8,256}$'

case "$CMD" in
  store)
    RECIPIENTS="" OUT=""
    while [ "$#" -gt 0 ]; do
      case "$1" in
        --recipients) RECIPIENTS="${2:-}"; shift 2 ;;
        --out) OUT="${2:-}"; shift 2 ;;
        *) die 2 "未知の引数: $1（store が受け付けるのは --recipients・--out）" ;;
      esac
    done
    [ -n "$RECIPIENTS" ] && [ -r "$RECIPIENTS" ] || die 2 "age の受取人のファイル（--recipients）を読めない"
    [ -n "$OUT" ] || die 2 "書き出し先（--out）が未指定"
    [ ! -e "$OUT" ] || die 2 "${OUT} は在る。上書きしない"
    # 端末からは伏せて読む（標準入力が端末でなければ 2 行を読む。値は表示しない）。
    if [ -t 0 ]; then
      read -rs -p "アクセスキーの ID: " id; echo >&2
      read -rs -p "シークレットキー: " secret; echo >&2
    else
      IFS= read -r id || true
      IFS= read -r secret || true
    fi
    [[ "$id" =~ $KEY_SHAPE ]] && [[ "$secret" =~ $KEY_SHAPE ]] || { unset id secret; die 2 "鍵の形が違う（値は表示しない）"; }
    if ! ( umask 077; printf '%s\n%s\n' "$id" "$secret" | age -R "$RECIPIENTS" -o "$OUT" ); then
      unset id secret; rm -f "$OUT"; die 1 "暗号化に失敗した（書き出し先は消した）"
    fi
    unset id secret
    chmod 600 "$OUT"
    echo "OK: 鍵を age で暗号化して ${OUT} に書いた（値は表示しない。平文のファイルは作っていない）。" ;;
  run)
    CREDS="" IDENTITY="" ENDPOINT=""
    while [ "$#" -gt 0 ]; do
      case "$1" in
        --creds) CREDS="${2:-}"; shift 2 ;;
        --identity) IDENTITY="${2:-}"; shift 2 ;;
        --endpoint) ENDPOINT="${2:-}"; shift 2 ;;
        --) shift; break ;;
        *) die 2 "未知の引数: $1（run が受け付けるのは --creds・--identity・--endpoint と、-- の後のコマンド）" ;;
      esac
    done
    [ -n "$CREDS" ] && [ -r "$CREDS" ] || die 2 "鍵のファイル（--creds）を読めない"
    [ -n "$IDENTITY" ] && [ -r "$IDENTITY" ] || die 2 "age の識別子のファイル（--identity）を読めない"
    [[ "$ENDPOINT" =~ ^https://[A-Za-z0-9.-]+(:[0-9]+)?/?$ ]] || die 2 "--endpoint は https://<名前> の形で与える"
    [ "$#" -ge 1 ] || die 2 "-- の後に、鍵を渡すコマンドを与える"
    # 復号の出力はこのプロセスの変数へだけ読む（ファイル・引数を経ない）。
    { IFS= read -r id && IFS= read -r secret; } < <(age -d -i "$IDENTITY" "$CREDS") || true
    [[ "${id:-}" =~ $KEY_SHAPE ]] && [[ "${secret:-}" =~ $KEY_SHAPE ]] || { unset id secret; die 1 "鍵のファイルを復号できないか、中身の形が違う（値は表示しない）"; }
    # 鍵は、このプロセス（と exec する <コマンド>）の環境にだけ載せる。
    export AWS_ACCESS_KEY_ID="$id" AWS_SECRET_ACCESS_KEY="$secret" AWS_ENDPOINT_URL="$ENDPOINT" AWS_EC2_METADATA_DISABLED=true
    export RCLONE_CONFIG_OBJSTORE_TYPE=s3 RCLONE_CONFIG_OBJSTORE_PROVIDER=Other RCLONE_CONFIG_OBJSTORE_ENV_AUTH=false \
      RCLONE_CONFIG_OBJSTORE_ENDPOINT="$ENDPOINT" RCLONE_CONFIG_OBJSTORE_ACCESS_KEY_ID="$id" RCLONE_CONFIG_OBJSTORE_SECRET_ACCESS_KEY="$secret"
    unset id secret
    exec "$@" ;;
  *) die 2 "store か run を与える（使い方はファイルの冒頭）" ;;
esac
