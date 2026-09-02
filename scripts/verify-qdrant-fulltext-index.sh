#!/usr/bin/env bash
# FR-03, UC-01, SC-01, Issue #1116 / [[IADR-0318]]・Issue #1118 / [[IADR-0331]]:
#   Qdrant の **全文（full-text）ペイロードインデックス**が、実機で本当に効いていることを
#   **陽性対照と陰性対照の対**で確かめる。
#   #1118 で、**日本語の語**（アプリ側 2-gram ペイロード `text_ngram`）も同じ対で判定する（段 7）。
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

# 指定ペイロードへの全文 Match で当たった点の件数を返す。
count_matches_in() {
  local field="$1" query="$2" f
  f=$(mktemp)
  # 展開ありのヒアドキュメント（$query を差し込む）。検証用の語に " や \ は含めない。
  cat > "$f" <<JSON
{"limit":50,"with_payload":false,"filter":{"must":[{"key":"${field}","match":{"text":"${query}"}}]}}
JSON
  qcurl POST "/collections/${COLLECTION}/points/scroll" "$f" | read_point_count
  rm -f "$f"
}

# `text`（識別子の系統・multilingual）への全文 Match。
count_matches() { count_matches_in text "$1"; }

# FR-03, #1118 / [[IADR-0331]] 決定 1: 日本語（CJK）の 2-gram 符号化。
#   **アプリ（Knowledge.Contracts.Indexing.CjkBigramPayload.Encode）の写し**である ——
#   CJK の連なりごとに 2-gram（1 文字の連なりは 1-gram）を空白区切りで並べ、CJK 以外は区切りとしてだけ働く。
#   アプリ側が変わったらここも変える（固定はアプリ側の単体試験 CjkBigramPayloadTests が持つ）。
#   🔴 日本語は **stdin だけ**で渡す（argv に載せない。上の qcurl の注記と同じ罠）。
#      **JS 本体も \`node -e\` の argv に載せず、引用ヒアドキュメントで一時ファイルへ置く** ——
#      正規表現の \`\p{Script=…}\` や日本語の文字が argv の途中で壊れ、符号化が空になって全件 -1 になった（実測）。
# 🔴 node は MSYS の /tmp を解決できない（Windows では mktemp の経路を node に渡せない）。スクリプトと同じ場所に置く。
NGRAM_JS="$(dirname "$0")/.verify-qdrant-ngram.$$.js"
cat > "$NGRAM_JS" <<'JS'
let s = '';
process.stdin.on('data', (d) => (s += d)).on('end', () => {
  const isCjk = (ch) => /[\p{Script=Han}\p{Script=Hiragana}\p{Script=Katakana}ー々〆〤]/u.test(ch);
  const out = [];
  let run = [];
  const flush = () => {
    if (run.length === 1) out.push(run[0]);
    for (let i = 0; i + 1 < run.length; i++) out.push(run[i] + run[i + 1]);
    run = [];
  };
  for (const ch of s) {
    if (isCjk(ch)) run.push(ch);
    else flush();
  }
  flush();
  process.stdout.write(out.join(' '));
});
JS
cjk_ngram_of_file() { node "$NGRAM_JS" < "$1"; }

# 日本語の語（`text_ngram` へ、2-gram にしたクエリで Match）で当たった点の件数を返す。
count_ja_matches() {
  local f q
  f=$(mktemp)
  printf '%s' "$1" > "$f"
  q=$(cjk_ngram_of_file "$f")
  rm -f "$f"
  [ -z "$q" ] && { echo -1; return; }
  count_matches_in text_ngram "$q"
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
# 🔴 符号化の JS は**終了時にだけ**消す（`cleanup` は準備段でも呼ぶので、そこへ入れると使う前に消える。実測で踏んだ）。
trap 'cleanup; rm -f "$NGRAM_JS"' EXIT

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

# 点 1〜3 の本文。点 3 は実配備の seed チャンク（日本語＋識別子＋記号の長文）の形である。
TEXT1="合言葉は ${PRESENT_PHRASE} である。型番 RX-7800X3D と略語 ABAC を含む。"
TEXT2="オブジェクトストレージへ本文を格納し、チャンクに分けて索引へ登録する。"
TEXT3="3. IngestionService が本文を読み、チャンクに分け、埋め込みを得て Qdrant へ登録する（MarkdownUri）"
# #1118: アプリ（取り込み側）と同じく、本文から日本語 2-gram ペイロード `text_ngram` を作って併記する。
ngram_tmp=$(mktemp)
printf '%s' "$TEXT1" > "$ngram_tmp"; NGRAM1=$(cjk_ngram_of_file "$ngram_tmp")
printf '%s' "$TEXT2" > "$ngram_tmp"; NGRAM2=$(cjk_ngram_of_file "$ngram_tmp")
printf '%s' "$TEXT3" > "$ngram_tmp"; NGRAM3=$(cjk_ngram_of_file "$ngram_tmp")
rm -f "$ngram_tmp"

seed_body=$(mktemp)
cat > "$seed_body" <<JSON
{"points":[
  {"id":1,"vector":[1,0,0,0],"payload":{"text":"${TEXT1}","text_ngram":"${NGRAM1}"}},
  {"id":2,"vector":[1,0,0,0],"payload":{"text":"${TEXT2}","text_ngram":"${NGRAM2}"}},
  {"id":3,"vector":[1,0,0,0],"payload":{"text":"${TEXT3}","text_ngram":"${NGRAM3}"}}
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
printf '\n[2/7] 全文ペイロードインデックスを張る（text: multilingual ／ text_ngram: prefix 1..2）\n'
create_out=$(send_body PUT "/collections/${COLLECTION}/index?wait=true" \
  '{"field_name":"text","field_schema":{"type":"text","tokenizer":"multilingual","min_token_len":1,"max_token_len":40,"lowercase":true}}')
tokenizer=$(payload_schema_tokenizer)
if [ "$tokenizer" = "multilingual" ]; then
  pass "multilingual トークナイザが受理された（このイメージで使える）"
else
  fail "索引を張れない、または tokenizer が multilingual にならない（実際: ${tokenizer}）"
  info "$(printf '%s' "$create_out" | head -c 200)"
fi
# #1118 / [[IADR-0331]] 決定 1: 日本語 2-gram ペイロード `text_ngram` の索引も、アプリと同じパラメータで張る
# （tokenizer=prefix / 1..2 文字。`prefix` は 2-gram の 1 文字接頭辞も索引に入れるので 1 文字の語も当たる）。
create_ngram_out=$(send_body PUT "/collections/${COLLECTION}/index?wait=true" \
  '{"field_name":"text_ngram","field_schema":{"type":"text","tokenizer":"prefix","min_token_len":1,"max_token_len":2,"lowercase":true}}')
ngram_tokenizer=$(qcurl GET "/collections/${COLLECTION}" "" \
  | node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>{try{const p=JSON.parse(s).result.payload_schema.text_ngram;console.log(p&&p.params?p.params.tokenizer:'(none)')}catch{console.log('(none)')}})")
if [ "$ngram_tokenizer" = "prefix" ]; then
  pass "text_ngram（日本語 2-gram）の索引を prefix トークナイザで張れた"
else
  fail "text_ngram の索引を張れない、または tokenizer が prefix にならない（実際: ${ngram_tokenizer}）"
  info "$(printf '%s' "$create_ngram_out" | head -c 200)"
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

# ---- 段 7) 🔴 日本語の語が当たること（#1118。陽性・陰性を対で判定する） ----------------------
#
# 🔴 `text`（multilingual）は公式イメージ v1.18.1 では日本語の分かち書きを持たず、語で当たるかは連なりの
#    切れ目次第である（実配備チャンクの日本語 25 語のうち当たるのは 1 語。[[IADR-0331]] 実測 1）。
#    アプリは CJK を 2-gram に割って `text_ngram` に載せ、クエリも同じ変換で引く（[[IADR-0331]] 決定 1）。
#    ここでは **同じ語**を `text`（旧経路）と `text_ngram`（新経路）の両方で引き、対比をそのまま出す。
#    判定は `text_ngram` の側 —— **在る語 4 つが全て 1 件以上・在らない語が 0 件**。
printf '\n[7/7] 日本語の語が当たること（text_ngram の 2-gram。text=旧経路との対比も出す）\n'
ja_fail=0
for w in "索引" "本文" "チャンクに分け" "合言葉"; do
  n_old=$(count_matches "$w")
  n_new=$(count_ja_matches "$w")
  info "「${w}」: text(multilingual)=${n_old} 件 / text_ngram(2-gram)=${n_new} 件"
  [ "$n_new" -ge 1 ] || ja_fail=1
done
n_ja_absent=$(count_ja_matches "零細企業")
n_ja_single=$(count_ja_matches "本")
info "「零細企業」（在らない語・陰性対照）: text_ngram=${n_ja_absent} 件"
info "「本」（1 文字の語）: text_ngram=${n_ja_single} 件"
if [ "$ja_fail" = "0" ] && [ "$n_ja_absent" = "0" ]; then
  pass "日本語の語は text_ngram で全て当たり、在らない語は 0 件（陽性・陰性の対が成立）"
else
  fail "日本語の語が text_ngram で当たらない、または在らない語が当たる（陽性=${ja_fail} / 陰性=${n_ja_absent}）"
  info "🔴 疑う順: ①text_ngram の索引が無い（段 2） ②ペイロードの符号化が CjkBigramPayload.Encode と食い違う"
fi
if [ "$n_ja_single" -ge 1 ]; then
  pass "1 文字の語も当たる（prefix トークナイザが 2-gram の 1 文字接頭辞を索引に持つ）"
else
  fail "1 文字の語が当たらない（text_ngram の tokenizer が prefix ではない疑い）"
fi

hr
printf '結果: PASS %d / FAIL %d\n' "$PASS" "$FAIL"
if [ "$FAIL" -gt 0 ]; then
  printf '全文インデックスが期待どおりに機能していません。\n'
  exit 1
fi
printf '全文インデックスは陽性・陰性の両対照で機能しています。\n'
exit 0
