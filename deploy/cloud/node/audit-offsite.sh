#!/usr/bin/env bash
# NFR-18, NFR-05, IADR-0710 決定 5: ノードの上にしか無い監査の記録（k3s の API サーバーの監査・OpenBao の監査〔Pod のログ〕）の
# **目録（ファイルごとの大きさと sha256）を日次でノードの外へ送り**、後から外の写しと突き合わせて記録の連続性を確かめる。
#
#   sudo bash audit-offsite.sh manifest            # 目録を標準出力へ出す（送らない。読むだけ）
#   sudo bash audit-offsite.sh send                # 目録を外の check（Healthchecks.io の check D）へ本文として送る（systemd の timer が日次で流す）
#   sudo bash audit-offsite.sh verify <目録>       # 外の写し（check D の ping の本文）の各行が、今のファイルの先頭と同じかを確かめる
#
# - 目録の 1 行は「<種類> <ファイル> <大きさ> <先頭から大きさまでの sha256>」。記録は追記と改名（世代交代）でしか変わらないので、
#   前の日の目録の行は、今日のどれかのファイルの先頭（大きさまで）と同じ sha256 を持つ。違えば、記録が書き換えられたか消された。
# - 目録は前の回の目録の sha256（prev）を持つ（鎖）。外の写しは Healthchecks.io が ping の履歴として持つ（ノードの root を取った者は消せない）。
# - 中身（監査の記録そのもの）は送らない。送るのはファイルの名前・大きさ・sha256 だけ（値・トークンは含まない）。
# - 外の check の URL はノードの /etc/msp-audit-offsite/ping-url（root だけが読める 0600）。🔴 URL を引数・ログに出さない
#   （curl へは標準入力の設定〔-K -〕で渡す）。送れない・止まった・timer が外された日は、check D が期限で下がって外からオーナーへ届く。
# - 保持（API サーバーの監査は 30 日・10 世代。Pod のログは kubelet の世代交代）を過ぎた記録は、verify で「見つからない」になるのが正しい。
#
# 終了コード: 0＝済んだ（verify はすべての行が見つかった） / 1＝送れない・見つからない行がある / 2＝引数・設定の誤り
# 手順書: docs/operations/cloud-apiserver-audit-log-runbook.md §4。試験: deploy/cloud/node/audit-offsite.test.sh。
set -uo pipefail

R="${NODE_ROOT:-}"   # 試験のための根（既定はノードの /）
URL_FILE="${AUDIT_OFFSITE_URL_FILE:-$R/etc/msp-audit-offsite/ping-url}"
STATE_DIR="${AUDIT_OFFSITE_STATE_DIR:-$R/var/lib/msp-audit-offsite}"
MAX_PER_KIND="${AUDIT_OFFSITE_MAX_PER_KIND:-40}"
URL_RE='^https://[A-Za-z0-9.-]+(:443)?/[A-Za-z0-9._~/-]+$'

die2() { echo "ERROR: $*" >&2; exit 2; }

# 種類ごとの記録のファイル（新しい順。数は MAX_PER_KIND まで）。glob に合うものが無ければ何も出さない。
list_kind() {
  local kind="$1" f
  local -a files=()
  shopt -s nullglob
  case "$kind" in
    apiserver) files=("$R"/var/lib/rancher/k3s/server/logs/audit*.log*) ;;
    openbao) files=("$R"/var/log/pods/openbao_openbao-*/openbao/*.log*) ;;
  esac
  shopt -u nullglob
  [ "${#files[@]}" -gt 0 ] || return 0
  for f in "${files[@]}"; do [ -f "$f" ] && [ ! -L "$f" ] && printf '%s\t%s\n' "$(stat -c %Y "$f")" "$f"; done \
    | sort -rn | head -n "$MAX_PER_KIND" | cut -f2-
}

prefix_sha() { head -c "$2" -- "$1" | sha256sum | cut -d ' ' -f 1; }

manifest() {
  local prev="none" kind f size
  [ -r "$STATE_DIR/last-manifest" ] && prev="$(sha256sum < "$STATE_DIR/last-manifest" | cut -d ' ' -f 1)"
  printf 'msp-audit-manifest v1 %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  printf 'prev %s\n' "$prev"
  for kind in apiserver openbao; do
    while IFS= read -r f; do
      [ -n "$f" ] || continue
      size="$(stat -c %s -- "$f")"
      printf '%s %s %s %s\n' "$kind" "${f#"$R"}" "$size" "$(prefix_sha "$f" "$size")"
    done < <(list_kind "$kind")
  done
}

cmd_send() {
  local url body
  [ -r "$URL_FILE" ] && [ -s "$URL_FILE" ] || die2 "外の check の URL が無い: ${URL_FILE}（手順書 §4。0600 で置く）"
  [ "$(stat -c %a "$URL_FILE")" = 600 ] || die2 "${URL_FILE} の権限が 0600 でない（URL は root だけが読む）"
  url=""
  IFS= read -r url < "$URL_FILE" || true
  [[ "$url" =~ $URL_RE ]] || die2 "外の check の URL が https の URL の形でない（値は表示しない）"
  mkdir -p -m 0700 "$STATE_DIR" || die2 "状態の置き場を作れない: $STATE_DIR"
  body="$(mktemp "$STATE_DIR/manifest.XXXXXX")" || die2 "目録を書けない"
  manifest > "$body"
  local n
  n="$(grep -cv '^msp-audit-manifest \|^prev ' "$body" || true)"
  if [ "$n" -eq 0 ]; then
    echo "WARN: 監査の記録のファイルが 1 つも無い（暗号化した領域のマウント・k3s の監査の設定を確かめる）。目録は送る（空の目録が外に残る）" >&2
  fi
  # 🔴 URL は引数に載せない（curl の設定を標準入力から読ませる）。応答の本文は捨てる。
  if printf 'url = "%s"\n' "$url" | curl -fsS -m 30 --retry 3 --retry-delay 5 -o /dev/null --data-binary "@$body" -K - 2>/dev/null; then
    mv -f -- "$body" "$STATE_DIR/last-manifest"
    echo "OK: 監査の記録の目録（${n} 件）を外の check へ送った（URL は表示しない）"
    return 0
  fi
  rm -f -- "$body"
  echo "ERROR: 外の check へ送れない（URL は表示しない）。check D は期限で下がる" >&2
  return 1
}

cmd_verify() {
  local file="$1" kind path size sha f found bad=0 total=0
  [ -r "$file" ] || die2 "目録を読めない: $file"
  head -n 1 "$file" | grep -q '^msp-audit-manifest v1 ' || die2 "目録の形が違う（1 行目が msp-audit-manifest v1 でない）: $file"
  while read -r kind path size sha; do
    case "$kind" in apiserver | openbao) ;; *) continue ;; esac
    [[ "$size" =~ ^[0-9]+$ ]] && [[ "$sha" =~ ^[0-9a-f]{64}$ ]] || { echo "NG  形の違う行: $kind $path"; bad=1; continue; }
    total=$((total + 1))
    found=""
    while IFS= read -r f; do
      [ -n "$f" ] || continue
      [ "$(stat -c %s -- "$f")" -ge "$size" ] || continue
      if [ "$(prefix_sha "$f" "$size")" = "$sha" ]; then found="${f#"$R"}"; break; fi
    done < <(MAX_PER_KIND=100000 list_kind "$kind")
    if [ -n "$found" ]; then
      echo "ok  $kind $path（${size} バイト）は ${found} の先頭と同じ"
    else
      echo "NG  $kind $path（${size} バイト）の中身がどのファイルの先頭にも無い（書き換えられたか消された。保持を過ぎた記録なら正しい）"
      bad=1
    fi
  done < "$file"
  echo "verify: ${total} 行"
  return "$bad"
}

case "${1:-}" in
  manifest) manifest ;;
  send) cmd_send ;;
  verify) [ "$#" -eq 2 ] || die2 "verify には目録のファイルを 1 つ与える"; cmd_verify "$2" ;;
  *) die2 "操作は manifest・send・verify <目録> のどれか" ;;
esac
