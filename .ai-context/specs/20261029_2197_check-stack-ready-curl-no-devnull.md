---
title: 作業仕様書 — check-stack-ready の G4b・G4c が Windows で curl に /dev/null を渡して全件「測れなかった」になる（#2197）
type: spec
status: done
related_ids: [NFR, IADR-0575, IADR-0586]
author: claude
created: 2026-10-29
updated: 2026-10-29
issue: "#2197"
---

# 作業仕様書 — 状態コードだけを測る curl の呼び出しを出力先に依らない形にする（#2197）

> 本仕様書は実装着手前に作成した（着手 2026-10-29）。

## 現状（実測）

- `scripts/check-stack-ready.js` の `fetchStatus` は `spawnSync('curl', ['-sk', '-o', '/dev/null', '-w', '%{http_code}', …])` で呼ぶ。
  - G4b（エッジは管理面を出さない。IADR-0575）と G4c（管理ツールの集約入口は既定で閉じている。IADR-0586）が使う。
- node から直接起こした Windows の curl には `/dev/null` が無い。本文の書き込みに失敗して終了コード 23 になる。
  - 状態コード（403 など）は標準出力に出ているが、終了コードが 0 でないので「測れなかった」（0）として扱われる。
  - Git Bash から curl を起こすと MSYS が `/dev/null` を変換するので、手で叩くと再現しない。

## 方針

- **`-o` を渡さない。** 本文は標準出力へ出させ、`-w '\n%{http_code}'` で末尾の 1 行に状態コードを置く。最後の行だけを状態として読む。
  - 同じファイルの `wikiJsGraphql`（G7）が既に同じ形（`-w '\\n%{http_code}'` と末尾行の `pop()`）で読んでいる。形を揃える。
  - 本文は `maxBuffer`（8 MiB。`fetchDiscovery` と同じ）まで受けて捨てる。超えたら spawnSync が失敗し、0（測れない）になる＝緑にしない側へ倒れる。
- **選ばなかった案と理由**
  - `-o NUL` を `process.platform` で分岐する: OS ごとの分岐が残り、Windows の分岐は CI（Linux）で一度も走らない。走らない分岐は同じ事故を再び隠す。
  - `-o os.devNull`: 分岐は消えるが、出力先（デバイス名）の解釈を curl とランタイムの組み合わせに委ねる点は同じ。curl が `\\.\nul` を開けるかは版に依る。
  - 一時ファイルへ `-o`: 作成・削除の後始末が要り、失敗時に残骸が残る。状態コードしか見ないのに書き込みを増やす。
  - `-I`（HEAD）: 要求のメソッドが変わり、Linux での測り方（GET の応答）が変わる。管理面の拒否は GET で測る宣言（IADR-0575 / IADR-0586）から外れる。
- Linux での挙動は変えない。要求（GET・`-k`・`--max-time 30`）は同じで、読む値（状態コード・失敗時 0）も同じである。

## 受け入れ基準

- [x] `/dev/null` を出力先に取れない curl（Windows を模したスタブ。`-o /dev/null` なら 23 で終わる）を PATH に置いても、`fetchStatus` が状態コードを返す（修正前は 0＝赤）
- [x] curl の引数に `/dev/null` も `-o` も載らない（`process.platform` に依らない）
- [x] 本文が数字だけの行を含む・末尾に改行が無い場合も、状態コードは末尾の行から読む
- [x] curl が失敗した（終了コードが 0 でない）ときは 0 を返す（測れないを緑にしない）
- [x] Linux の実物の curl で、手元の HTTP サーバ（403 を返す）を測って 403 になる（Linux の挙動を変えていない）

## 母集合（規則 9）

- `git grep -n "['\"\`]/dev/null['\"\`]" -- '*.js' '*.mjs' '*.cjs' '*.ts'` → `scripts/check-stack-ready.js:1620`（`fetchStatus`）の 1 か所だけ。
- `.sh` の `>/dev/null`（`scripts/lib/*.sh` ほか）は bash が解釈する（Git Bash では MSYS が変換する）ので対象外。
- `-H @/dev/fd/3`（`WIKIJS_POST_SH`）は Pod 内（Linux）で走るので対象外。
- 規則 10: 本変更で古くなる自分の記述 → `fetchStatus` の JSDoc（本文は読まない）を「本文は捨てる」へ直す。IADR-0575 / IADR-0586 は `-o` に触れていないので追随不要。

## 規則 11（窓）

- 時間差を扱う是正ではない。表は描かない。
