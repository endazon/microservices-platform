---
title: h2c 実測の手順書を、ネイティブサイドカー・manifest 末尾の空行・DataProtection の警告・Windows の改行で誤判定しない形にする
type: spec
status: done
related_ids: [NFR-16, NFR-09]
author: claude
created: 2026-10-10
updated: 2026-10-10
plan_refs: []
issue: "#1882"
---

# 仕様書: h2c 実測の手順書の誤判定 4 点を直す（#1882）

## 起点

- 要求: `NFR-16`（east-west の通信の運用性）。手順書の起点の仕様書は `20261004_issue-1255_h2c-roundtrip-measurement-runbook`。
- 根拠: 2026-10-10 の PoC 実測の記録（#1517 と #1255 のコメント「稼働 k3s での h2c 往復の実測（2026-10-10・PoC セッション）」）の「期待値と違ったもの」1・2・4 と、③の偽陽性の記述。
- 対象外: 同じ記録の 3（`helm upgrade` のたびに出る AuthorizationPolicy の警告）は別 issue で扱う。

## 変更（`docs/operations/east-west-grpc-h2c-roundtrip-measurement-runbook.md`）

| # | 実測 | 直し方 |
| --- | --- | --- |
| 1 | §0.3 (3) が `spec.containers` だけを見る。`istio-proxy` は全 Pod で `spec.initContainers` に在る（ネイティブサイドカー） | custom-columns に `INIT:.spec.initContainers[*].name` を足し、どちらかの列に在ればよいと書く。失敗の分岐表にも行を足す |
| 2 | §0.3 (2) が 0 行でなく 2 行（`helm get manifest` の末尾の空行が 1 行多い） | `helm template` と `helm get manifest` を比べる 3 か所（§0.3 (2)・§2.4・§6.2）を `diff -B` にする。§6.1 は `helm get manifest` 同士なので変えない（実測でも 0 行） |
| 3 | §3.4 ③ の `grep -iE '…|Unavailable|…'` が DataProtection の警告に当たる | 状態の名前の単独照合と `-i` をやめ、呼び出し元が出す形（下表と「レビューでの是正」）だけに当てる |
| 4 | Windows（Git Bash）で `kubectl logs` の CRLF を除き、node へのパスを `cygpath -m` した | `kubectl logs` / `kubectl exec` の出力を grep・node へ流す箇所に `tr -d '\r'`、node へのパスを `np` で渡す。§0.4 に Windows の注意を置く（自己完結。#1880 は未マージ） |

### ③の照合の根拠（呼び出し元が実際に出す文言。`git grep` で引いた）

| 文言 | 出す場所 |
| --- | --- |
| `… over gRPC`（`rejected over gRPC (…)`・`Failed to collect … over gRPC`） | `GrpcServiceIntrospectionCollector` / `GrpcToolDeclarationCollector` / `GrpcToolInvoker` |
| `Status(StatusCode="…")`（例外の出力。`StatusCode=` で拾う）・`RpcException` | 例外つきで記録する全クライアント |
| `service token` / `s2s トークン` | 上 3 つ / `AuthzScopeGrpcClient` / `UserDirectoryGrpcClient` |
| `gRPC 解決に失敗` / `gRPC 照会に失敗`（`（Unavailable）` など状態名つき） | `AuthzScopeGrpcClient` / `UserDirectoryGrpcClient` |
| `was not executed` / `is unimplemented` | `GrpcToolInvoker` |
| `送出に失敗` | `GrpcPrivateNoteNotifier` |

DataProtection の警告（`Protected data will be unavailable when container is destroyed.`）が新しい照合に当たらないこと、上の各文言が当たることを、
CRLF つきの見本行で手元で確かめた。

### レビューでの是正（PR #1884 の 🟡 2 件）

- **合否表と照合の食い違い（規則 10）**: 初版の照合は文言の列挙（`gRPC 解決に失敗` など）だったため、状態名を全角括弧で出すが文言が列挙に無い
  `GrpcOwnerReadPolicyStatusSource`（`…件数を引けなかった（Unimplemented）`）と、`status=` で出す `GrpcTagDictionaryReader`
  （`タグ辞書を gRPC で引けなかった（status=Unimplemented）`）を拾わず、§5.1 と失敗の分岐表の「③に `Unimplemented`」が成り立たなかった。
  `git grep -ln 'catch (RpcException' -- 'src/*.cs'` で記録をすべて引き、状態名が現れる 3 つの形（例外の `StatusCode="…"`・`（<状態名>）`・`status=<状態名>`）で
  照合する形に改めた。表の文言もこの 3 形と `is unimplemented` に揃えた。
- **ロケール依存（`の?`）**: マルチバイト文字に量指定子を掛ける形をやめた（字面の連結と選択肢だけにした）。
- 見本行 14 行（DataProtection の警告 2・HTTP の `status=404` 1・各クライアントの失敗 11）を `LC_ALL=C` と `LC_ALL=C.UTF-8` の両方で照合し、
  失敗 11 行がすべて当たり、他の 3 行が当たらないことを確かめた。

## 母集合（規則 9。誤りの側の文字列で走査した）

| 走査 | 結果 | 扱い |
| --- | --- | --- |
| `git grep -n 'spec.containers\|\.containers\[\*\]' -- docs scripts .claude` | サイドカーの有無を見るのは本書 §0.3 (3) と `scripts/k8s-local-up.sh` の「注入の確認」の案内文 1 行。他は `containers[0].env` / `.image` / `.args` の読み取りで、サイドカーの判定ではない。`check-stack-ready.js` は `initContainers` も見ている。`deploy/istio/README.md` は既に `initContainers` も見る形（2026-08-30 の誤答の記録つき。今回が同型の 2 回目） | 本書と `k8s-local-up.sh` の案内文を直した。他は対象外 |
| `git grep -n 'helm get manifest' -- docs scripts` | `helm template` と素の diff で比べるのは本書の 3 か所だけ。`check-bff-multi-replica-session.js` は行末の空白を落として比べ、`check-stack-ready.js` G12 は資材の集合で比べる | 本書の 3 か所を直した |
| `git grep -nE "grep -[a-zA-Z]*i.*Unavailable" -- docs scripts` | 本書 §3.4 ③ だけ | 直した |
| `git grep -nE 'kubectl[^|]* logs[^|]*\|' -- docs` | 本書のほか `keycloak-smtp-relay-setup-runbook.md`・`llm-output-token-measurement-runbook.md`・`local-sso-recovery-runbook.md`・`operations.md`・`password-reset-relay-state-measurement-runbook.md`・`secret-item-live-sync-check-runbook.md`・`secret-store-openbao-migration-runbook.md` | **他は直さない。** いずれも行末に掛からない部分一致の `grep` / `grep -c` で、切り出した値を後段で使わないので CRLF で結果が変わらない。本書は `grep -o` の切り出しと node への入力があるので直した |

## 規則 10・11

- 規則 10: 変更後に `git grep -n "Unavailable\|spec.containers\|diff \"\$W/render\|node \"\$W" -- docs/operations/east-west-grpc-h2c-roundtrip-measurement-runbook.md` で引き直し、旧い形が残っていないことを確かめた（§2.3 の `diff render-before render-after` は `helm template` 同士なので `-B` は要らない）。
- 規則 11: 窓（時間差）を扱わないので該当しない。

## 検証

- `node scripts/check-trace-blocks.js` / `node scripts/gen-knowledge-graph.js --check` / `node scripts/check-commit-messages.js` / `node scripts/check-cross-repo-refs.js` / `node scripts/check-doc-links.js` / `node scripts/check-reading-budget.js` / `node scripts/check-plan-id-qualification.js`
- `bash -n scripts/k8s-local-up.sh`
- 稼働クラスタでの再実行はしていない（クラウドのセッションからは測れない）。直した部品は PoC が実際に使った形（`tr -d '\r'`・`cygpath -m`・空行の差）に合わせた。
