---
title: 作業仕様書 — リセット申請の床の器を 2 レプリカ＋PDB にし、器が落ちたときの退路を改める（#1543・計画 ADR-0111）
type: spec
status: done
related_ids: [SC-15, NFR-05, NFR-13, NFR-21, ADR-0045, ADR-0094, ADR-0097, ADR-0111, IADR-0432]
author: claude
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0111_reset-floor-replicas-and-no-bypass-on-failure.md (Accepted 2026-09-26)
  - planning:projects/microservices-platform/07_adr/ADR-0097_timing-floor-release-default-on-and-periodic-review.md (決定 2・例外 3 の訂正 2026-09-26)
  - planning:projects/microservices-platform/10_feedback/20260926_reset-floor-availability.md (planning#656 の裁定)
related_specs: [20260926_1500_reset-floor-default-on.md, 20260911_issue-1410_reset-timing-floor.md]
issue: "#1543"
---

# 作業仕様書 — リセット申請の床の器を 2 レプリカ＋PDB にし、器が落ちたときの退路を改める

## 起点

- issue: #1543（計画 ADR-0111 の実装側フォローアップ 1・2）。検知（フォローアップ 3）は #1544 へ分けた。
- 計画 ADR-0111（Accepted 2026-09-26・利用者裁定・planning#656）決定 1〜3。ADR-0097 決定 2 の補完。
- 追随させる実装判断: IADR-0432（［2026-09-26 追記 / #1500］が運用手順書へ書いた「急ぐときの退路は `RESET_FLOOR=0`」）。

## 計画の決定（逐語の要点）と写像

| 決定 | 内容 | 本作業での写像 |
| --- | --- | --- |
| 1 | **器（`reset-floor`）は 2 レプリカ以上。PDB で自発的な退避によって ready な器が 0 になることを防ぐ** | `reset-floor.yaml` の `replicas: 2`。同ファイルへ `PodDisruptionBudget`（`minAvailable: 1`）。分散は `topologySpreadConstraints`（`ScheduleAnyway`）|
| 2 | **予備の経路（fail-open）は足さない。readiness は上流を映さない** | 変えない。`reset-floor.test.js` で「先頭 route の宛先は器 1 つだけ」「readiness は tcpSocket で上流を叩かない」を固定する |
| 3 | **器がすべて落ちたときの 503 は「申請を閉じた状態」。復旧は器を戻す。利用者は管理者の一時パスワード発行。本番で `RESET_FLOOR=0` を退路に使わない（検証での比較に限る）** | 運用手順書・画面／テスト仕様書・`deploy/local/README.md`・マニフェストのコメント・スクリプトのコメントとメッセージ・IADR-0432 追記 |
| フォローアップ 3 | 器が落ちたこと（ready な endpoint が 0）の検知は NFR-21 の通知の配線の射程 | **#1544 を起票**（下記「検知」） |

## 設計判断

### PDB は `minAvailable: 1`（`maxUnavailable: 1` ではない）

- 決定 1 の目的は「**ready な器が 0 にならない**」ことであり、`minAvailable: 1` はそれを**そのまま**書く。
- 2 レプリカでは両者は同じ効果（退避できるのは 1 つまで）だが、**違いが出るのは replicas が変わったとき**である。
  - 誰かが一時的に 1 へ絞った場合: `maxUnavailable: 1` は最後の 1 つの退避を許し、**ready が 0 になる**（決定 1 の目的を割る）。
    `minAvailable: 1` は退避を止める（ノードのドレインは待たされるが、それが決定 1 の求める挙動である）。
  - 3 以上へ増やした場合: `minAvailable: 1` は同時に 2 つ以上の退避を許す。目的（0 にしない）は保たれる。
- Helm チャートの PDB（`templates/pdb.yaml`）も `minAvailable`（既定 1）であり、リポジトリ内の作法と揃う。
- 既知の代償: **単一ノードのクラスタをドレインすると、2 つ目の器の退避で止まる**（代わりの Pod が同じノードへ載れない）。
  単一ノードのドレインは全停止と同義であり、リポジトリのスクリプトは `kubectl drain` を使わない（`scripts/*.sh` を走査して 0 件）。

### 分散は `topologySpreadConstraints` の `ScheduleAnyway`（必須の anti-affinity ではない）

- ローカル（k3d / k3s）は**単一ノード**である。`requiredDuringScheduling` の podAntiAffinity や `DoNotSchedule` の分散を置くと、
  **2 つ目の Pod が Pending のまま**になり、`k8s-local-up.sh` の `rollout status deploy/reset-floor --timeout=120s` が期限切れで止まり、
  `check-stack-ready.js` の G1（`availableReplicas >= spec.replicas`）も赤になる。
- `ScheduleAnyway`（`kubernetes.io/hostname`・`maxSkew: 1`）は、複数ノードでは別ノードへ散らし、単一ノードでは同じノードへ 2 つ載せる。
  **単一ノードで守れるのは Pod 単位の故障・更新・再作成**であり、ノードの喪失は守れない（単一ノードの本質的な限界）。
- 🔴 go-live が複数ノードで「必ず別ノード」を要するなら、go-live 側で `DoNotSchedule` へ強める判断が要る（本作業の射程外。計画は分散の強さを定めていない）。

### 更新の戦略

- Deployment 既定の RollingUpdate（`maxUnavailable: 25%` → 2 レプリカでは切り捨てで 0、`maxSurge: 25%` → 切り上げで 1）により、
  更新中も ready は 2 未満へ下がらない。**明示しない**（既定で目的を満たし、書くと値の出所が 2 つになる）。

### 起動器・門への影響（コードは変えない）

- `k8s-local-up.sh`: `kubectl rollout status deploy/reset-floor --timeout=120s` は**全レプリカの更新と可用**を待つ。2 つは並行に起動し、
  イメージ（`node:22-alpine`）は 1 つ目で取得済みになるため、期限は変えない。
- `check-stack-ready.js` G1: `availableReplicas < spec.replicas` を失敗とする汎用判定であり、2 レプリカでは 2 つとも ready であることを要求する。
  **期待を書き換える箇所は無い**（名前で器を特別扱いしていない）。G11 は Deployment のイメージだけを見るので PDB の追加は影響しない。
  → 依頼の「`check-stack-ready.js` の期待を更新」は**不要と判断した**（変える根拠のあるコードが無い）。`--self-test` だけ回す。

## 検知（フォローアップ 3）—— #1544 を起票し、実装しない

既存のアラート配線で自明に書けるかを確かめた（2026-09-26）:

- Prometheus の scrape 対象は otel-collector だけ（`deploy/local/observability/prometheus.yaml`・`deploy/prometheus.yml`）。**kube-state-metrics は無い**。
- 器は `/metrics` を持たない。Istio / Envoy のメトリクスも収集していない。
- `deploy/prometheus/alerts.yml` に流用できるルールは無い。

→ 信号の新設（kube-state-metrics の導入・器への `/metrics`・エッジの統計のいずれか）が要り、**自明ではない**。#1544 で扱う。

## 母集合（着手時に引き直した）

`git grep`（パス除外のみ: `src/ai-stock-trading`。拡張子で絞らない）。

| 軸 | 検索 | 拾ったもの |
| --- | --- | --- |
| 1 誤りの側（退路の文字列） | `RESET_FLOOR[^_]` | `docs/operations/keycloak-smtp-relay-setup-runbook.md`（334・347）/ `docs/screens/SC-15_password-reset.md`（316・320）/ `docs/tests/SC-15_password-reset.md`（95・96）/ `deploy/local/README.md`（320）/ `deploy/local/edge-istio-reset-floor/kustomization.yaml`（5・6）/ `deploy/mail-relay/kustomization.yaml`（35・36）/ `deploy/mail-relay/reset-floor/reset-floor.yaml`（9）/ `scripts/istio-edge-up.sh`（6・21・78・90）/ `scripts/k8s-local-up.sh`（17）/ `scripts/k8s-local-up.test.js`（274）/ `scripts/reset-floor.test.js`（24・25・198・232）/ `scripts/README.md`（190）/ `scripts/check-password-reset-mail.js`（691・1157）/ `.github/workflows/integration-stack.yml`（90・91・244）/ IADR-0432（277 ほか）|
| 2 誤りの側（語） | `退路\|急ぐとき\|失敗しても安全\|単一の故障\|1 レプリカ`（床の文脈） | 軸 1 と同じ集合 ＋ `reset-floor.yaml` 16 行（「失敗しても安全側」） |
| 3 誤りの側（レプリカ） | `資源都合\|replicas: 1`（器）| `reset-floor.yaml` 42〜44 行 |
| 4 誤りの側（503 と予備の経路） | `遅くならないだけ\|予備の(route\|経路)\|即座の 503\|fail-open` ∩ 床の文脈 | runbook 344・349 / SC-15 画面 314 / `reset-floor.yaml` 16・68 / IADR-0432 258・261・266 |
| 5 PDB を列挙する文書 | `PodDisruptionBudget\|PDB` | `docs/operations/operations.md` §可用性（チャートの対象表）/ `docs/tech/tech-requirements.md` 402 / `deploy/argocd/appproject.yaml` |
| 6 門・起動器 | `rollout status deploy/reset-floor` / `availableReplicas` | `scripts/k8s-local-up.sh` 249 / `scripts/check-stack-ready.js` G1 |
| 7 一時パスワードの手順の置き場 | `一時パスワード` | `docs/screens/SC-15_password-reset.md` §代替 / `docs/tests/SC-15_password-reset.md` T-14 |

**直すもの**: 軸 1〜4 の運用・構成の文（runbook / SC-15 画面 / SC-15 テスト / `deploy/local/README.md` / マニフェスト 3 本のコメント / `istio-edge-up.sh` のコメントとメッセージ / `k8s-local-up.sh` のコメント / 試験 2 本のコメントと名前 / `scripts/README.md`）、IADR-0432 は日付つき追記、`docs/operations/operations.md` §可用性へ器の 1 行。

**除外したものと理由**

- `scripts/check-password-reset-mail.js` 691・1157: 赤の原因として「`RESET_FLOOR=0` の比較実行」を挙げる診断文であり、退路として勧めていない。比較用途は決定 3 が認める。
- `docs/tests/SC-15_password-reset.md` 95・96 行: 同上（赤の原因の列挙。「比較実行」と書いている）。
- `.github/workflows/integration-stack.yml`: `RESET_FLOOR` を与えない理由と赤の原因の列挙。退路の記述ではない。
- `docs/tech/tech-requirements.md` 402: 可用性の手段の一般論（HPA＋PDB）で、個別の部品を列挙していない。
- `deploy/argocd/appproject.yaml`: `policy/PodDisruptionBudget` は許可済み。しかも ArgoCD が同期するのは Helm チャートであり、`deploy/mail-relay` は `k8s-local-up.sh`（と go-live の手作業の apply）が当てる。
- IADR-0432 の本文（277 行の「退路」行を含む #1500 追記）: 凍結記録。本文は書き換えず、新しい日付つき追記で覆す。
- `.ai-context/specs/` の確定済み仕様書（#1410・#1500・#1525）: 凍結記録。
- `CHANGELOG.md`: 生成物。
- 軸 2・4 のうち `IADR-0118` / `IADR-0123` / `check-coverage-floor.js` ほかの「床」「fail-open」: カバレッジの床であり別の意味。
- `deploy/mail-relay/reset-floor.js`: 器のコードは複製しても状態を持たない（要求ごとに時刻を測って待つだけ）。変える所が無い。

## 受け入れ基準

1. `kubectl kustomize deploy/mail-relay` と `deploy/local/infra` の描画で、`reset-floor` の Deployment が `replicas: 2`、
   同じ名前空間に `PodDisruptionBudget reset-floor`（`minAvailable: 1`・selector が器の Pod ラベルと一致）が出る。
2. 分散は `ScheduleAnyway` であり、単一ノードで 2 つ目が Pending にならない（`DoNotSchedule` / 必須 anti-affinity を置かない）。
3. readiness は上流を映さない tcpSocket のまま。overlay の先頭 route は宛先を器 1 つだけに持つ（予備の経路なし）。
4. `node scripts/reset-floor.test.js` が 1〜3 を固定し、変異（replicas を 1 へ・PDB を消す・`maxUnavailable` へ置き換えて selector を外す・
   分散を `DoNotSchedule` へ・予備の宛先を足す・readiness を httpGet で上流へ）で落ちる。
5. 運用手順書から「急ぐときの退路は `RESET_FLOOR=0`」が消え、「503 は申請を閉じた状態・器を戻す・利用者は管理者の一時パスワード発行・
   `RESET_FLOOR=0` は検証での比較に限り本番では使わない」が書かれている。SC-15 の画面／テスト仕様書・`deploy/local/README.md`・
   マニフェストとスクリプトのコメントが同じ言明に揃う。
6. IADR-0432 に `［2026-09-26 追記 / #1543］` がある。
7. `docs/` の可視テキストに計画 ID・IADR・仕様書名を書かない（trace ブロックへ。ADR-0111 は宣言レンジが 0110 の間は trace ブロックにも書けない → 下記）。
8. `k8s-local-up.test.js` / `reset-floor.test.js` / `scripts.test.js` / `scripts.repo.test.js` と文書の検査が通る。

## 計画 ID のレンジ（ADR-0111）

- 本リポジトリの宣言レンジは `ADR-0001..0110`（`.claude/rules/traceability.repo.md`）。**レンジを 0113 へ上げる作業は別の担当が進めている。**
- それまで、コミット件名・PR タイトルと `docs/` の trace ブロックへ ADR-0111 を書かない（件名は `check-commit-messages.js`、trace ブロックは
  `check-trace-blocks.js` が宣言レンジで落とす）。コード・マニフェストのコメントと `.ai-context/` の本文には書く（検査の対象外であることを確かめた。
  実行結果は PR 本文）。レンジが上がった後、`docs/` の trace ブロックへ ADR-0111 を足すのは追随作業とする。
