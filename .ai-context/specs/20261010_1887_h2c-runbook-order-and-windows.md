---
title: h2c 実測の手順書の発火の順序（N-1 の再起動で document の証拠が消える）と、Windows で壊れる node -e を直す
type: spec
status: done
related_ids: [NFR-16]
author: claude
created: 2026-10-10
updated: 2026-10-10
plan_refs: []
issue: "#1887"
---

# 仕様書: h2c 実測の手順書の順序と Windows の `node -e`（#1887）

## 起点

- 要求: `NFR-16`。手順書は `docs/operations/east-west-grpc-h2c-roundtrip-measurement-runbook.md`（§3.2.1・§3.2.2 は #1889 / IADR-0530 で足した）。
- 根拠: PoC が稼働 k3s で本書を実行したときの報告 2 点。
  1. N-1 は `rollout restart deploy/document-service` を伴う。それより前に document で取った受け手のログとサイドカーの計数が消え、D-3（graph → document）の証拠が残らなかった。
  2. Windows で Volta の `node` は shim で `cmd` を経るため、`node -e '…'` の引数の `=>`・`||`・`&&`・`>`・`|` が壊れた。

## 変更

| # | 対象 | 直し方 |
| --- | --- | --- |
| 1 | 本書 §3.2 | 発火の順序を「N-1（document の再起動）→ I・M → §1.2 の画面の操作と §3.2.1」と明記。`snap before` は取り直さなくてよい理由（document 宛の行は 0 から数え直す）も書く |
| 2 | 本書 §1.2 の表（N-1 の行）と表の下・§0.1 の表（`deploy/document-service` の行）・§3.2.2 | 「モードの回の発火の最初に行う」を足す |
| 3 | 本書 §3.2.2 | 代わりの手段として、graph のサイドカーの `reporter=source` の計数を生成の前後で取る `gsrc` を足す。rpc を区別できない・mTLS の列が `unknown` になる限界も書く |
| 4 | 本書 §3.1 の `istio-grpc.mjs` | 引数 `source` で `reporter=source` を選べるようにした（既定は従前どおり `destination`） |
| 5 | 本書 §6.1 の `node -e` | heredoc で `$W/measurement-values.mjs` に書き、`node "$(np …)"` で実行する（本書の既存の部品 `gen-overlay.mjs`・`istio-grpc.mjs` と同じ形） |
| 6 | 本書 §0.4 | Volta の shim の行を足し、「node へ処理を引数で渡さない」を規範にした |
| 7 | `llm-model-pin-runbook.md`（2 か所）・`llm-output-token-measurement-runbook.md`（1 か所）・`password-reset-relay-state-measurement-runbook.md` §3 の `mailrelay_alerts` | `node -e` を `node <<'EOF'`（処理を標準入力で渡す）へ。アラートの取得は `curl \| node -e` をやめ、node の `fetch` で取る |

### 直し方の選択

- **ファイルか標準入力で処理を渡す**を採った。引数を経ないので shim と `cmd` の解釈に依存しない。Linux・macOS・WSL でも同じ形で動く。
- 記号を避けて書き直す形は採らない。`=>` を `function` に替えても `>`（比較）や `|`（正規表現）が残り、書き手が毎回守る必要がある。
- `node.exe` を直に呼ぶ・`winpty` を挟む形は採らない。Volta の配置や Git Bash の版で効き方が違う。
- ペアの鍵の回し方の手順書（`paired-secret-rotation-runbook.md`）は `jq` を使い `node -e` を持たないので、倣う型は無かった。Windows の注意を表で持つ形（症状・原因・避け方）だけ倣った。本書の §0.4 も同じ形である。

## 母集合（規則 9）

| 走査 | 結果 | 扱い |
| --- | --- | --- |
| `git grep -n 'node -e' -- docs .claude scripts/*.md` | `docs/operations/` の 4 文書・7 か所だけ | 手元の node で実行する 5 か所を直した。`password-reset-relay-state-measurement-runbook.md` の 2 か所（`kubectl exec deploy/reset-gate -- node -e`）は Pod の中の node で動き、Windows の shim を通らないので直さない |
| 本書の `rollout restart` | `mcp-service`（M）と `document-service`（N-1） | M は mcp が呼び出し元の A-5・X-3 より前に行う順で既に書かれている（§3.2 の注で明記）。N-1 を直した |
| 本書の順序を述べる箇所（`L-4・D-3・D-2・N-1` など） | §0.1・§2.2・§3.2 | §2.2 は列挙だけで順序を述べないので変えない。§0.1・§3.2 を直した |

## 規則 10・11

- 規則 10: 変更後に `git grep -n "node -e" -- docs` を引き直し、手元の node で処理を引数に渡す形が残っていないことを確かめた。
- 規則 11: 窓（時間差）の是正ではない（順序の是正）ので該当しない。

## 検証

- 本書の `istio-grpc.mjs`（`destination` / `source`）と `measurement-values.mjs` を本文から切り出し、見本の入力で期待どおりの行が出ることを確かめた。
- `node <<'EOF'` で `require('./src/…/appsettings.json')` が解決され、`fetch` の失敗が `catch` で 1 行になることを確かめた。
- `node scripts/check-trace-blocks.js` / `node scripts/gen-knowledge-graph.js --check` / `node scripts/check-doc-links.js` / `node scripts/check-commit-messages.js` / `node scripts/check-cross-repo-refs.js` / `node scripts/check-plan-id-qualification.js`
- 稼働クラスタと Windows での再実行はしていない。
