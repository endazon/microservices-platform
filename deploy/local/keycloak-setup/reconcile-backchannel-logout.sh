#!/usr/bin/env bash
# NFR, SC-13, ADR-0026 / ADR-0032, IADR-0273, #1115:
# realm の `bff` クライアントの **`backchannel.logout.url`** を、稼働中の Keycloak realm へ冪等に当てる。
#
#   bash deploy/local/keycloak-setup/reconcile-backchannel-logout.sh
#
# ## なぜ要るか —— realm JSON を直しても既存クラスタには届かない
#
# Keycloak は `start-dev --import-realm` で立っている。**同名 realm が既に在ると import は黙って飛ばされる**
# （`KC_IMPORT_STRATEGY` 既定 = `IGNORE_EXISTING`）。エラーも警告も出ず、pod は Running のままである。
# つまり `deploy/keycloak/microservices-platform-realm.json` を直しても、**realm を捨てて作り直さない限り
# 稼働クラスタの値は古いままである**（#1088 が指している乖離。永続化の有無とは別の問題で、
# 永続化を入れれば「もっと確実に古いまま」になる）。
#
# 直す値は **1 つ**（サーバ間の口）である。ブラウザ向けの URL 群（`redirectUris` / `webOrigins` /
# `post.logout.redirect.uris`）はここでは触らない —— それらは裸の `localhost` が正しい。
#
# ## 作法（deploy/local/wikijs-setup/bootstrap.sh と同型）
#
# - **冪等**: 現在値が期待値と一致していれば何もしない。
# - **期待値は realm JSON から読む**（単一情報源。ここへ URL を二重に書かない）。
# - **管理者資格情報は pod の環境変数をそのまま使う**（`KEYCLOAK_ADMIN` / `KEYCLOAK_ADMIN_PASSWORD`）。
#   スクリプトも標準出力も Secret の実値を持たない。
# - **best-effort**: 失敗しても `k8s-local-up.sh` を止めない（呼び出し側が WARN にしている）。
#
# 環境変数（すべて任意）:
#   INFRA_NS      Keycloak の namespace（既定 platform-infra）
#   KC_REALM      対象 realm（既定 platform）
#   KC_CLIENT_ID  対象 client（既定 bff）
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
INFRA_NS="${INFRA_NS:-platform-infra}"
KC_REALM="${KC_REALM:-platform}"
KC_CLIENT_ID="${KC_CLIENT_ID:-bff}"
REALM_JSON="$ROOT/deploy/keycloak/microservices-platform-realm.json"

if ! kubectl -n "$INFRA_NS" get deploy/keycloak >/dev/null 2>&1; then
  echo "    Keycloak が居ないためスキップします（$INFRA_NS/keycloak）"
  exit 0
fi

# 期待値は realm JSON が正（ここへ URL を二重に書かない）。読めなければ**止める** ——
# 空文字を「期待値」として当ててしまうと、宛先を消したことに気付けない。
#
# ★ 抽出に node を使わない: Git Bash（MSYS）では複数行の `node -e '…'` が
#   `node: -e requires an argument` で落ち、**空文字を返したまま先へ進む**（#1115 で踏んだ）。
#   pod の中の照合と同じ sed にそろえる。
EXPECTED="$(sed -n 's/.*"backchannel\.logout\.url" *: *"\([^"]*\)".*/\1/p' "$REALM_JSON" | sort -u)"
if [ -z "$EXPECTED" ]; then
  echo "ERROR: backchannel.logout.url を $REALM_JSON から読めません" >&2
  exit 1
fi
if [ "$(printf '%s\n' "$EXPECTED" | wc -l)" -ne 1 ]; then
  echo "ERROR: backchannel.logout.url が複数の値を持ちます（client ごとの反映は未対応）:" >&2
  printf '%s\n' "$EXPECTED" >&2
  exit 1
fi

echo "    期待値: $EXPECTED"

# ここから先は pod の中で動かす（kcadm.sh は Keycloak イメージにしか無い）。
# 引数: 1=realm 2=clientId 3=期待する URL
kubectl -n "$INFRA_NS" exec -i -c keycloak deploy/keycloak -- sh -s "$KC_REALM" "$KC_CLIENT_ID" "$EXPECTED" <<'INPOD'
set -eu
REALM="$1"; CLIENT_ID="$2"; EXPECTED="$3"
# 空を当てると宛先を消す。呼び出し側でも見ているが、pod の中でも fail-closed にする。
if [ -z "$EXPECTED" ]; then
  echo "    ERROR: 期待値が空です（realm JSON の読み取りに失敗している）" >&2
  exit 1
fi
cd /opt/keycloak/bin

./kcadm.sh config credentials --server http://localhost:8080 --realm master \
  --user "$KEYCLOAK_ADMIN" --password "$KEYCLOAK_ADMIN_PASSWORD" >/dev/null

ID="$(./kcadm.sh get clients -r "$REALM" -q "clientId=$CLIENT_ID" --fields id --format csv --noquotes | head -1)"
if [ -z "$ID" ]; then
  echo "    ERROR: client '$CLIENT_ID' が realm '$REALM' に在りません" >&2
  exit 1
fi

CURRENT="$(./kcadm.sh get "clients/$ID" -r "$REALM" \
  | sed -n 's/.*"backchannel\.logout\.url" *: *"\([^"]*\)".*/\1/p' | head -1)"

if [ "$CURRENT" = "$EXPECTED" ]; then
  echo "    一致（変更なし）: $CURRENT"
  exit 0
fi

echo "    更新: '${CURRENT:-（未設定）}' -> '$EXPECTED'"
./kcadm.sh update "clients/$ID" -r "$REALM" \
  -s "attributes.\"backchannel.logout.url\"=$EXPECTED" \
  -s 'attributes."backchannel.logout.session.required"=true'

VERIFY="$(./kcadm.sh get "clients/$ID" -r "$REALM" \
  | sed -n 's/.*"backchannel\.logout\.url" *: *"\([^"]*\)".*/\1/p' | head -1)"
if [ "$VERIFY" != "$EXPECTED" ]; then
  echo "    ERROR: 更新後の値が一致しません（実際: '$VERIFY'）" >&2
  exit 1
fi
echo "    反映を確認: $VERIFY"
INPOD
