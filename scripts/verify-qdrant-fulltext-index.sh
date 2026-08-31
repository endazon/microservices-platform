#!/usr/bin/env bash
# FR-03, UC-01, SC-01, Issue #1116 / [[IADR-0318]]:
#   Qdrant の **全文（full-text）ペイロードインデックス**が、実機で本当に効いていることを
#   **陽性対照と陰性対照の対**で確かめる。
#
# 背景:
#   `RetrievalService.QdrantVectorStore.KeywordSearchAsync` はペイロード `text` への
#   full-text `Match` で `ScrollAsync` する。**この呼び出しは索引が在って初めて全文検索になる。**
#
#   🔴 索引が無いときの挙動は Qdrant の版で変わり、**どちらも静かである**:
#     v1.9.2  … `RpcException`（アプリが握り潰して 0 件へ縮退する）
#     v1.18.1 … **例外を投げず、部分文字列の全走査へ落ちる**（本スクリプトが実測した）
#   後者は「当たっているように見えるのに全文検索ではない」——
#   語でない断片（`anpop`）に当たり、語順に依存し、全点を走査する。
#
#   したがって **「1 件以上返ったか」だけでは索引の有無を判定できない。**
#   本スクリプトは *索引が無いときだけ 0 件になるクエリ*（語は同じで順序だけ替えたもの）を使う。
#
# 実行方法:
#   1) 実機 Qdrant を用意する。稼働 k8s なら:
#        kubectl -n platform-infra port-forward svc/qdrant 6333:6333
#   2) 本スクリプトを実行する:
#        QDRANT_URL=http://localhost:6333 bash scripts/verify-qdrant-fulltext-index.sh
#      API キーが必要な場合: QDRANT_API_KEY=... を併せて渡す。
#
# 依存: bash / curl / node（JSON の読み取りに使う。jq は前提にしない）。
#
# 副作用: **検証用の一時コレクションを作成し、最後に削除する**（既定 `iadr0316_verify_1116`）。
#         🔴 稼働中のコレクションには一切触れない（読み取りもしない）。
#
# 終了コード: 0=全項目 PASS / 1=判定の失敗 / 2=前提未整備（Qdrant へ到達できない等）

set -u

QDRANT_URL="${QDRANT_URL:-http://localhost:6333}"
QDRANT_API_KEY="${QDRANT_API_KEY:-}"
COLLECTION="${QDRANT_COLLECTION:-iadr0316_verify_1116}"

# 索引に**在る**語（陽性対照）と、**在らない**語（陰性対照）。
# 語順を替えた問い合わせが要である —— 索引が無いと部分文字列として現れないので 0 件になる。
PRESENT_PHRASE='msp-searchseed-tanpopo'
REORDERED_PHRASE='tanpopo searchseed msp'
ABSENT_TERM='msp-absent-zzzznotexistword'
SUBSTRING_FRAGMENT='anpop'

PASS=0
FAIL=0
hr()   { printf -- '----------------------------------------------------------------------\n'; }
pass() { PASS=$((PASS + 1)); printf '  PASS  %s\n' "$*"; }
fail() { FAIL=$((FAIL + 1)); printf '  FAIL  %s\n' "$*"; }
info() { printf '        %s\n' "$*"; }

for cmd in curl node; do
  command -v "$cmd" >/dev/null 2>&1 || { printf 'ERROR: %s が必要です。\n' "$cmd" >&2; exit 2; }
done

# 🔴 **要求本文はファイル経由で渡す**（`--data-binary @file`）。
#    Windows の Git Bash では、日本語を含む文字列をコマンド引数（特に `node -e` の argv）へ
#    載せると静かに壊れ、**「検索が当たらない」と「引数が壊れた」を取り違える**（実測で踏んだ）。
#    ヒアドキュメントでファイルへ書けばバイト列がそのまま残る。
qcurl() {
  local method="$1" path="$2" body_file="${3:-}"
  local -a args=(-sS -X "$method" "${QDRANT_URL}${path}" -H 'Content-Type: application/json')
  [ -n "$QDRANT_API_KEY" ] && args+=(-H "api-key: ${QDRANT_API_KEY}")
  [ -n "$body_file" ] && args+=(--data-binary "@${body_file}")
  curl "${args[@]}"
}

# JSON 応答から `result.points` の件数を読む（-1 は応答が読めなかったことを表す）。
# node へは **stdin だけ**を渡す（argv に日本語を載せない）。
read_point_count() {
  node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>{try{const o=JSON.parse(s);console.log(o.result&&o.result.points?o.result.points.length:-1)}catch{console.log(-1)}})"
}

# 全文 Match で当たった点の件数を返す。
count_matches() {
  local query="$1" f
  f=$(mktemp)
  # 展開ありのヒアドキュメント（$query を差し込む）。検証用の語に " や \ は含めない。
  cat > "$f" <<JSON
{"limit":50,"with_payload":false,"filter":{"must":[{"key":"text","match":{"text":"${query}"}}]}}
JSON
  qcurl POST "/collections/${COLLECTION}/points/scroll" "$f" | read_point_count
  rm -f "$f"
}

payload_schema_tokenizer() {
  qcurl GET "/collections/${COLLECTION}" "" \
    | node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>{try{const p=JSON.parse(s).result.payload_schema.text;console.log(p&&p.params?p.params.tokenizer:'(none)')}catch{console.log('(none)')}})"
}

# 一時ファイルへ本文を書いて POST/PUT する小さな補助（呼び出し側の見通しのため）。
send_body() {
  local method="$1" path="$2" body="$3" f
  f=$(mktemp)
  printf '%s' "$body" > "$f"
  qcurl "$method" "$path" "$f"
  rm -f "$f"
}

cleanup() { qcurl DELETE "/collections/${COLLECTION}" "" >/dev/null 2>&1; }
trap cleanup EXIT

hr
printf 'Qdrant 全文ペイロードインデックスの実機検証（Issue #1116 / FR-03）\n'
printf '  qdrant     : %s\n' "$QDRANT_URL"
printf '  collection : %s（使い捨て。終了時に削除する）\n' "$COLLECTION"
hr

version=$(curl -sS "${QDRANT_URL}/" | node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>{try{console.log(JSON.parse(s).version)}catch{console.log('')}})")
if [ -z "$version" ]; then
  printf 'SKIP: Qdrant へ到達できません（%s）。port-forward を確認してください。\n' "$QDRANT_URL" >&2
  exit 2
fi
printf 'Qdrant version: %s\n' "$version"

# ---- 準備: 既知の文書を入れる ---------------------------------------------------------
cleanup
send_body PUT "/collections/${COLLECTION}" \
  '{"vectors":{"size":4,"distance":"Cosine"}}' >/dev/null

seed_body=$(mktemp)
cat > "$seed_body" <<JSON
{"points":[
  {"id":1,"vector":[1,0,0,0],"payload":{"text":"合言葉は ${PRESENT_PHRASE} である。型番 RX-7800X3D と略語 ABAC を含む。"}},
  {"id":2,"vector":[1,0,0,0],"payload":{"text":"オブジェクトストレージへ本文を格納し、チャンクに分けて索引へ登録する。"}},
  {"id":3,"vector":[1,0,0,0],"payload":{"text":"3. IngestionService が本文を読み、チャンクに分け、埋め込みを得て Qdrant へ登録する（MarkdownUri）"}}
]}
JSON
upsert_out=$(qcurl PUT "/collections/${COLLECTION}/points?wait=true" "$seed_body")
rm -f "$seed_body"
if ! printf '%s' "$upsert_out" | grep -q '"status":"ok"'; then
  printf 'SKIP: 検証用の点を投入できません: %s\n' "$(printf '%s' "$upsert_out" | head -c 200)" >&2
  exit 2
fi

# ---- 段 1) 索引が無い状態 —— 「当たる」ことが判定にならないのを示す --------------------
printf '\n[1/7] 索引が無い状態では、合言葉がそのままでも当たる（＝件数では欠陥を検出できない）\n'
n_plain_before=$(count_matches "$PRESENT_PHRASE")
n_reordered_before=$(count_matches "$REORDERED_PHRASE")
n_fragment_before=$(count_matches "$SUBSTRING_FRAGMENT")
info "合言葉そのまま        : ${n_plain_before} 件"
info "語順を替えた合言葉    : ${n_reordered_before} 件"
info "語でない断片(${SUBSTRING_FRAGMENT}) : ${n_fragment_before} 件"
if [ "$n_plain_before" -ge 1 ] && [ "$n_reordered_before" = "0" ]; then
  pass "索引なしでも合言葉そのままは当たる（部分文字列の全走査）。語順を替えると 0 件になる"
elif [ "$n_plain_before" = "0" ] && [ "$n_reordered_before" = "0" ]; then
  pass "索引なしでは全文 Match が成立しない（この版は例外か 0 件へ倒れる）"
else
  fail "索引なしの挙動が想定のどちらでもない（そのまま=${n_plain_before} / 語順替え=${n_reordered_before}）"
fi

# ---- 段 2) 索引を張る（アプリと同じパラメータ） ----------------------------------------
printf '\n[2/7] 全文ペイロードインデックスを張る（tokenizer=multilingual）\n'
create_out=$(send_body PUT "/collections/${COLLECTION}/index?wait=true" \
  '{"field_name":"text","field_schema":{"type":"text","tokenizer":"multilingual","min_token_len":1,"max_token_len":40,"lowercase":true}}')
tokenizer=$(payload_schema_tokenizer)
if [ "$tokenizer" = "multilingual" ]; then
  pass "multilingual トークナイザが受理された（このイメージで使える）"
else
  fail "索引を張れない、または tokenizer が multilingual にならない（実際: ${tokenizer}）"
  info "$(printf '%s' "$create_out" | head -c 200)"
fi

# ---- 段 3) 🔴 陽性対照 ------------------------------------------------------------------
printf '\n[3/7] 陽性対照: 索引に在る語（語順を替えた合言葉）で 1 件以上返ること\n'
n_reordered=$(count_matches "$REORDERED_PHRASE")
if [ "$n_reordered" -ge 1 ]; then
  pass "「${REORDERED_PHRASE}」→ ${n_reordered} 件（トークン集合として一致している）"
else
  fail "「${REORDERED_PHRASE}」→ 0 件。索引が効いていない"
fi

# ---- 段 4) 🔴 陰性対照（対で置く。片方だけでは「常に空」と区別できない） ----------------
printf '\n[4/7] 陰性対照: 索引に無い語で 0 件であること\n'
n_absent=$(count_matches "$ABSENT_TERM")
if [ "$n_absent" = "0" ]; then
  pass "「${ABSENT_TERM}」→ 0 件（「常に全件返す」実装ではない）"
else
  fail "「${ABSENT_TERM}」→ ${n_absent} 件。索引に無い語が当たっている"
fi

# ---- 段 5) 語でない断片に当たらない（部分文字列一致に戻っていないこと） ------------------
printf '\n[5/7] 語でない断片（%s）に当たらないこと（部分文字列一致ではないこと）\n' "$SUBSTRING_FRAGMENT"
n_fragment=$(count_matches "$SUBSTRING_FRAGMENT")
if [ "$n_fragment" = "0" ]; then
  pass "「${SUBSTRING_FRAGMENT}」→ 0 件（トークン境界を見ている）"
else
  fail "「${SUBSTRING_FRAGMENT}」→ ${n_fragment} 件。全文索引ではなく部分文字列一致になっている"
fi

# ---- 段 6) 識別子・型番・略語に当たること（キーワード検索の主目的） ----------------------
#
# 🔴 **ここが FR-03 のキーワード側の主目的である。** ベクトル検索が苦手なのは
#    固有名詞・型番・略語であり、全文索引はそこを埋めるために在る。
printf '\n[6/7] 識別子・型番・略語に当たること（ベクトルが苦手な語＝全文索引の主目的）\n'
n_id=$(count_matches "IngestionService")
n_model=$(count_matches "7800X3D")
n_abbr=$(count_matches "abac")   # lowercase=true が大小文字差を吸収する
if [ "$n_id" -ge 1 ] && [ "$n_model" -ge 1 ] && [ "$n_abbr" -ge 1 ]; then
  pass "識別子=${n_id} 件 / 型番(7800X3D)=${n_model} 件 / 略語(abac→ABAC)=${n_abbr} 件"
else
  fail "識別子・型番・略語のいずれかに当たらない（識別子=${n_id} / 型番=${n_model} / 略語=${n_abbr}）"
fi

# ---- 段 7) 日本語の当たり方（**部分的にしか当たらない**ことを毎回そのまま出す） -----------
#
# 🔴 **`multilingual` の日本語の再現率は部分的で、文書の中身に左右される。** 実機での実測:
#    - 短い日本語の文では語中に当たる語がある（`索引` `チャンク` `埋め込み` `オブジェクトストレージ`）
#    - 一方、同じ文の中でも当たらない語がある（`文書` `検索` `統合` `合言葉`）
#    - 実配備の seed チャンク（日本語と識別子・記号が混じる長文）では、**日本語の語で 1 件も当たらなかった**
#      （12 語を試して全滅。識別子は当たる）
#    **だから「日本語で引ける」と言い切らない。** 数字を毎回出し、記録（実装 ADR・機能仕様書の
#    §既知の限界）と食い違ったら記録を更新すること。
printf '\n[7/7] 日本語の当たり方（🔴 部分的である。数字をそのまま出す）\n'
n_ja_word=$(count_matches "索引")           # 点 2（短い日本語の文）
n_ja_other=$(count_matches "文書")          # 同じ文書群に在るのに当たらないことがある語
n_ja_mixed=$(count_matches "チャンクに分け") # 点 3（英数字が混じる文）
info "「索引」          : ${n_ja_word} 件"
info "「文書」          : ${n_ja_other} 件"
info "「チャンクに分け」: ${n_ja_mixed} 件"
if [ "$n_ja_word" -ge 1 ] || [ "$n_ja_other" -ge 1 ] || [ "$n_ja_mixed" -ge 1 ]; then
  pass "日本語でも当たる語がある（＝CJK として扱われている。ただし**全ての語では当たらない**）"
else
  fail "日本語の語が 1 つも当たらない（このイメージの multilingual が CJK を分割していない）"
  info "🔴 ADR と機能仕様書の §既知の限界を更新し、トークナイザの選定をやり直すこと。"
fi
info '🔴 上の 3 つが揃って当たることは期待していない。日本語の再現率は文書に依存する（既知の限界）。'

hr
printf '結果: PASS %d / FAIL %d\n' "$PASS" "$FAIL"
if [ "$FAIL" -gt 0 ]; then
  printf '全文インデックスが期待どおりに機能していません。\n'
  exit 1
fi
printf '全文インデックスは陽性・陰性の両対照で機能しています。\n'
exit 0
