#!/usr/bin/env bash
# SC-17, FR-05, ADR-0116 決定 1（2026-10-09 補完）, IADR-0473（2026-10-09 追記 / #1850）:
# **部門属性の同期の値（authorization-service の env `DepartmentAttributeSync__Mode`）を、helm の利用者値から読み・宣言し直す口。**
# `source` して使う。呼び出し元は `scripts/k8s-local-up.sh` だけ。
#
# 稼働 PoC の値は、運用者が `helm upgrade --reuse-values --set 'services.authorization.extraEnvAppend[0]…'` で入れる
# （docs/operations/operations.md §部門属性の同期）。起動器の [6/7] は `--reuse-values` を使わないので、何もしなければ再実行で
# 要素が外れて `Off` に戻る（#1850）。起動器は `ISTIO` と同じ「明示 ＞ 現行 ＞ 初回の既定」（IADR-0488）で値を選び、
# 宣言するときは authorization の `extraEnvAppend` を**丸ごと**与える（helm のリストは置換なので、添字 0 だけの `--set` は他の要素を消す）。
# 他の要素は `helm get values -o yaml` の字面のまま values ファイルへ写す（`--set` で写し直すと `value: "false"` が bool に化け、チャートの `{{- if .value }}` で値が消える）。
#
# 読むのは `helm get values`（利用者が与えた値）であって `--all` ではない（IADR-0487 と同じ）。読む口は mesh-mtls-mode.sh の
# `current_release_values`。ここの関数はどれも**純関数**（YAML の文字列を受け取るだけで helm を呼ばない）。

DEPT_SYNC_ENV_NAME="DepartmentAttributeSync__Mode"

# _dept_sync_awk <want: mode|others> <`helm get values -o yaml` の出力>
#   トップレベル `services:` → 直下の `authorization:` → 直下の `extraEnvAppend:` のブロック形式のリストだけを見る。
#   要素の区切りは要素の字下げ（最初の `- ` の位置）に置かれた `- `。helm の出力（`- ` を親のキーと同じ字下げに置く形）と、
#   2 段下げた形の両方を受ける。
#   want=mode   … 終了コード 0: 要素が 1 つで値が Off / Report / Fix（大小文字・前後の空白を許す。サービスの解釈と同じ）。正規化した値を出力
#                            1: 要素が無い（リストが無い・空・名前の違う要素だけ）
#                            2: 要素が壊れている（値域外・値でない形〔secretKeyRef 等〕・同名が 2 つ以上・リストが流れ形式で読めない）
#   want=others … 同名以外の要素を、要素の字下げを除いた字面のまま出力する（終了コードは常に 0）
_dept_sync_awk() {
  printf '%s\n' "${2:-}" | awk -v want="$1" -v target="$DEPT_SYNC_ENV_NAME" '
    function unq(v,   q) {
      sub(/^[ \t]+/, "", v); sub(/[ \t]+#.*$/, "", v); sub(/[ \t]+$/, "", v)
      q = substr(v, 1, 1)
      if (length(v) >= 2 && q == substr(v, length(v), 1) && (q == "\"" || q == sprintf("%c", 39))) v = substr(v, 2, length(v) - 2)
      return v
    }
    function field(t,   k) {                       # 要素の直下のキーを 1 つ処理する（t は字下げと "- " を除いた字面）
      if (t ~ /^name:/)  { k = t; sub(/^name:/, "", k);  name[n] = unq(k) }
      else if (t ~ /^value:/) { k = t; sub(/^value:/, "", k); val[n] = unq(k); hasval[n] = 1 }
      else if (t ~ /^(secretKeyRef|valueFrom|configMapKeyRef):/) nonplain[n] = 1
    }
    { line = $0; sub(/\r$/, "", line) }
    # 要素の中（state 3）の空行は保留し、次の行が同じ要素の続き（要素の "- " より深い字下げ）なら字面へ戻す
    # （`|-` の値の中の空行を黙って落とさない）。要素の外の空行は従来どおり捨てる。
    line ~ /^[ \t]*$/ { if (state == 3 && n > 0) blanks++; next }
    {
      match(line, /^ */); ind = RLENGTH; t = substr(line, ind + 1)
      # `#` で始まる行は、要素の続きの深さなら `|-` の値の中身（helm の出力に注釈は出ない）として保つ。それ以外は注釈として捨てる。
      if (t ~ /^#/ && !(state == 3 && n > 0 && dind >= 0 && ind > dind)) { blanks = 0; next }
      if (state == 3 && n > 0 && dind >= 0 && ind > dind) { for (; blanks > 0; blanks--) text[n] = text[n] "\n" }
      blanks = 0
      if (state == 3) {
        if (ind > eind || (ind == eind && t ~ /^-( |$)/)) {
          if (dind < 0) dind = ind
          if (t ~ /^#/) { text[n] = text[n] "\n" substr(line, dind + 1); next }
          if (ind == dind && t ~ /^-( |$)/) {
            n++; text[n] = substr(line, dind + 1); fi = dind + 2
            r = t; sub(/^-[ \t]*/, "", r); if (r != "") field(r)
          } else if (n > 0) {
            text[n] = text[n] "\n" substr(line, dind + 1)
            if (ind == fi) field(t)
          }
          next
        }
        state = 2
      }
      if (state == 2) {
        if (ind > sind) {
          if (aind < 0) aind = ind
          if (ind == aind && t ~ /^extraEnvAppend:/) {
            r = t; sub(/^extraEnvAppend:/, "", r); r = unq(r)
            if (r == "") { state = 3; eind = ind; dind = -1 }
            else if (r != "[]" && r != "null" && r != "~") broken = 1   # 流れ形式の非空リストは読めない（黙って「無い」と読まない）
          }
          next
        }
        state = 1
      }
      if (state == 1) {
        if (ind > 0) {
          if (sind < 0) sind = ind
          if (ind == sind && t ~ /^authorization:[ \t]*$/) { state = 2; aind = -1 }
          next
        }
        state = 0
      }
      if (ind == 0 && t ~ /^services:[ \t]*$/) { state = 1; sind = -1 }
    }
    END {
      if (want == "others") {
        for (i = 1; i <= n; i++) if (name[i] != target) print text[i]
        exit 0
      }
      hits = 0
      for (i = 1; i <= n; i++) if (name[i] == target) { hits++; hit = i }
      if (broken) exit 2
      if (hits == 0) exit 1
      if (hits > 1 || nonplain[hit] || !hasval[hit]) exit 2
      v = tolower(val[hit]); gsub(/^[ \t]+|[ \t]+$/, "", v)
      if (v == "off") { print "Off"; exit 0 }
      if (v == "report") { print "Report"; exit 0 }
      if (v == "fix") { print "Fix"; exit 0 }
      exit 2
    }'
}

# dept_sync_values_mode <`helm get values -o yaml` の出力>   純関数。終了コードと出力は _dept_sync_awk の want=mode
dept_sync_values_mode() { _dept_sync_awk mode "${1:-}"; }

# dept_sync_values_other_entries <`helm get values -o yaml` の出力>   純関数。同名以外の要素を字面のまま出す
dept_sync_values_other_entries() { _dept_sync_awk others "${1:-}"; }

# dept_sync_values_file <Off|Report|Fix> <他の要素（dept_sync_values_other_entries の出力。空可）>   純関数
#   [6/7] へ `-f` で渡す values ファイルの中身。authorization の `extraEnvAppend` を「他の要素 ＋ DepartmentAttributeSync__Mode」で与える。
dept_sync_values_file() {
  local mode="$1" others="${2:-}"
  case "$mode" in
    Off | Report | Fix) ;;
    *) echo "ERROR: dept_sync_values_file: 未知の値 '$mode'（Off / Report / Fix のいずれか）" >&2; return 1 ;;
  esac
  printf 'services:\n  authorization:\n    extraEnvAppend:\n'
  if [ -n "$others" ]; then printf '%s\n' "$others" | sed 's/^/      /'; fi
  printf '      - name: %s\n        value: "%s"\n' "$DEPT_SYNC_ENV_NAME" "$mode"
}
