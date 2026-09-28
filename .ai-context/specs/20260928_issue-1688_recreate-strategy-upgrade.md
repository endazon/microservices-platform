---
title: 既存リリースへの helm upgrade が wiki-js の strategy（Recreate と残った rollingUpdate の同居）で落ちる形を消す（#1688）
type: spec
status: done
related_ids: [NFR, ADR-0008, IADR-0210, IADR-0377, IADR-0461]
author: claude
created: 2026-09-28
updated: 2026-09-28
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0008_runtime-kubernetes-k3s.md (経路B の実行基盤)
  - planning:projects/microservices-platform/02_requirements/ (NFR 運用性)
issue: "#1688"
---

# 仕様書: Recreate へ変えた Deployment を既存リリースへ upgrade できるようにする（#1688）

> 本仕様書は実装着手前に作成する。起点は #1688（稼働クラスタで `[6/7] helm upgrade` が rc=1 で止まった実測）。

## 起点となる計画書（トレーサビリティ）

- 要求: 無採番の `NFR`（運用性。起動器の再実行で収束すること）。製品の FR には当たらない
- 計画 ADR: ADR-0008（経路B の実行基盤 k3s）
- 関連 IADR: IADR-0210 決定 7（PVC を掴む Deployment は Recreate）、IADR-0377（Helm 4 の SSA と `kubectl patch` の field manager）、IADR-0461（seaweedfs）
- 発端: #1569（wiki-js を Recreate にした）、#1435（チャート側の Recreate 検査）

## 目的・背景

- #1569 で wiki-js の Deployment を `strategy.type: Recreate` にした。#1569 より前から動くリリースでは、
  Deployment に API サーバが既定値で埋めた `strategy.rollingUpdate`（maxSurge / maxUnavailable 25%）が残っている。
- Helm 4 はサーバサイド apply（SSA）で upgrade する。SSA は**適用者が所有していないフィールドを消さない**。
  既定値で埋まった `rollingUpdate` は誰の所有でもないため残り、`type: Recreate` と同居して検証に落ちる。
- 新しく立てたクラスタでは再現しない（作成時から Recreate なので `rollingUpdate` が既定で入らない）。

## 実測（2026-09-28。直し方を選ぶ根拠）

稼働クラスタは使わず、使い捨ての `kube-apiserver` ＋ `etcd`（envtest の 1.34.1 バイナリ。scratchpad 内でローカル起動）に対して、
Helm **v4.0.0** と **v4.2.1** の両方で、最小のチャート（Deployment 1 本。`mode` で strategy を切り替える）を流した。

| 手順 | 結果（v4.0.0 / v4.2.1 とも同じ） |
| --- | --- |
| strategy 無しで install | `{"rollingUpdate":{"maxSurge":"25%","maxUnavailable":"25%"},"type":"RollingUpdate"}`（既定値） |
| `type: Recreate` へ upgrade（develop の形） | **失敗**: `spec.strategy.rollingUpdate: Forbidden: may not be specified when strategy type is 'Recreate'`（#1688 と同じ文言） |
| `type: Recreate` ＋ `rollingUpdate: null` へ upgrade（案 1） | **同じ文言で失敗** |
| `kubectl patch --type=json replace /spec/strategy {"type":"Recreate"}` の後に `type: Recreate` へ upgrade（案 2） | **成功**。strategy は `{"type":"Recreate"}` |
| 続けてもう一度 upgrade | 成功（conflict も出ない） |
| 同じ patch をもう一度 | `patched (no change)`（冪等） |

**案 1 が効かない理由**（どこで null が落ちるかを切り分けた）:

- **描画では落ちない。** `helm template` の出力に `rollingUpdate: null` がそのまま残る（Helm は描画結果をテキストのまま扱う）。
- **Helm も落とさない。** Helm 4 の `pkg/kube/client.go` `patchResourceServerSide` は、描画物を Unstructured に読み、
  `runtime.Encode(unstructured.UnstructuredJSONScheme, target.Object)` をそのまま `ApplyPatchType` で送る（null は保たれる）。
  同じ描画物を `kubectl apply --server-side --field-manager=helm -v=9` で送ると、要求本文は
  `"strategy":{"rollingUpdate":null,"type":"Recreate"}` であり、**null は API サーバまで届いている**。
- **API サーバ（SSA）が null を「削除」として扱わない。** 届いた null は、所有者の無い既存の `rollingUpdate` を消さず、同じ検証エラーになる。
- **よって案 1 は Helm 4 でも ArgoCD（`deploy/argocd/application.yaml` は `ServerSideApply=true`）でも効かない。**
  クライアントサイド apply（戦略的マージパッチ）なら `DeploymentStrategy` の `retainKeys` が `rollingUpdate` を消すので、
  そもそも null は要らない。**どの経路でも効かない記述をチャートに置くと「直した」と誤読されるので、置かない。**

**案 2 の patch が field manager を奪って後の upgrade を壊さないこと**（IADR-0377 の懸念）: patch が書く値（`type: Recreate`）は
チャートの値と**同じ**なので、SSA は共有所有として扱い conflict にならない（上の表の「続けてもう一度 upgrade」で実測）。
IADR-0377 の事故は、patch が**チャートと違う値**を書いた場合である。

## 選んだ直し方

**案 2 のみ**を採る（併用はしない。案 1 は上の実測で効かないため）。

- `scripts/lib/recreate-strategy.sh` に `reconcile_recreate_strategy <ns> <deploy>...` を置き、`k8s-local-up.sh` の
  `[6/7] helm upgrade` の**直前**で呼ぶ。
- 振る舞い（冪等）:
  - `kubectl` が無ければ何もせず 0 で返る。
  - Deployment が無い（新規クラスタ・namespace 未作成）なら何もしない（`get` の失敗は握る）。
  - `spec.strategy.rollingUpdate` が空なら何もしない（Recreate へ移行済み・2 回目以降の実行）。
  - 残っているときだけ `kubectl patch --type=json replace /spec/strategy {"type":"Recreate"}` を当て、その旨を 1 行出す。
  - patch 自体の失敗は握らない（握っても直後の helm upgrade が同じ理由で落ちる。原因の近くで止める）。
- 対象は `RECREATE_DEPLOYMENTS="seaweedfs wiki-js"`（チャートで `type: Recreate` を宣言する Deployment の全件）。
  **名前の集合がチャートと一致することを試験で固定する**（チャートに Recreate を足して列挙を忘れると赤）。
- ArgoCD 経路（`ServerSideApply=true`）で既存の Application を同期している環境は、同じ patch を手で当てる必要がある。
  `deploy/argocd/README.md` に手順を 1 節足す（起動器は ArgoCD 経由の同期より前に [6/7] で patch するので、起動器を流すクラスタでは不要）。

## 母集合（規則 9: 記憶で挙げず走査で引いた。2026-09-28・`origin/develop` = `6bb387df`）

走査: `grep -rln "Recreate" --include=*.{yaml,yml,tpl,sh,js} .`（`node_modules` を除く）。各ファイルは
`git log -S'Recreate'` と `--diff-filter=A` で「Recreate を足したのが作成時か、後からか」を引いた
（作業ツリーは当初 shallow だったため `git fetch --unshallow` してから引いた）。**後から Recreate へ変えた Deployment だけが本件の型である**。

| ファイル / Deployment | 適用経路 | Recreate を入れた時期 | 扱い |
| --- | --- | --- | --- |
| `deploy/helm/.../templates/wikijs.yaml` / wiki-js | Helm 4（SSA） | **後から**（#1569。作成は 2026-09-26 以前） | **対象**（本件） |
| `deploy/helm/.../templates/seaweedfs.yaml` / seaweedfs | Helm 4（SSA） | 作成時から（`8b66e42a`） | **対象に含める**（既存リリースに `rollingUpdate` は無いので patch は発火しない。チャートの Recreate 全件を列挙する規則で入る） |
| `deploy/local/infra-persistence/kustomization.yaml` / postgres・keycloak・qdrant | `kubectl apply -k`（クライアントサイド） | 後から（`a45dea1f` / #819） | **除外**: 戦略的マージパッチの `retainKeys` が `rollingUpdate` を消すので同居しない |
| `deploy/local/observability-persistence/kustomization.yaml` / prometheus・loki・tempo・grafana | `kubectl apply -k`（クライアントサイド） | 後から（`a45dea1f` / #819） | **除外**: 同上 |
| `deploy/mail-relay/mail-relay.yaml`・`reset-gate.yaml` | `kubectl apply` | 作成時から | **除外**: 作成時から Recreate。クライアントサイド apply でもある |
| `deploy/local/vault/vault-dev.yaml` | `kubectl apply -k` | 作成時から | **除外**: 同上 |
| `scripts/k8s-local-up.test.js` | — | — | 試験（Recreate の検査の既存箇所） |
| `src/ai-stock-trading`（submodule）の `templates/opend.yaml` | AST のチャート（別リポ） | 本リポでは判定しない | **除外**: 別リポジトリの成果物。本リポの起動器は AST の helm upgrade 前に patch しない。同型の懸念があるかは AST 側の履歴で判断する |

## 変更するもの

| ファイル | 変更 |
| --- | --- |
| `scripts/lib/recreate-strategy.sh` | 新規。`reconcile_recreate_strategy` |
| `scripts/k8s-local-up.sh` | lib を source し、`[6/7]` の helm upgrade の直前で呼ぶ |
| `scripts/k8s-local-up.test.js` | 試験を足す（下記） |
| `deploy/argocd/README.md` | ArgoCD（SSA）経路で既存 Application を持つ環境の手順を 1 節 |
| `scripts/live-scripts.json` | 新しい lib を `offline`（source される関数定義だけ）へ分類（#1550 の閉包検査） |
| `scripts/README.md` | 新しい lib の行 |
| `.ai-context/adr/IADR-0210_...md` | 決定 7 へ日付つき追記（新規 IADR は起こさない。決定 7 の射程の補足で足りる） |

## 受け入れ基準（→ 試験）

1. 既存リリース（`rollingUpdate` が残った wiki-js）に対し、起動器は helm upgrade の**前に** patch を当てる
   → `k8s-local-up.test.js`（stub に `rollingUpdate` を返させ、patch が helm upgrade より前の行に出ることを検査）。
2. 新規クラスタ（`rollingUpdate` が無い）では patch を当てない（既定の出力を増やさない）→ 同上（stub 既定）。
3. lib 単体: `kubectl` 無し・Deployment 無し・移行済みのいずれでも 0 で返り patch しない。残っているときは対象だけ patch する
   → 同上（lib を bash で直接走らせる）。
4. 列挙がチャートの Recreate 全件と一致する → 同上（テンプレートを走査して突き合わせる）。
5. チャートに `rollingUpdate: null` を置かない（SSA で効かないことを実測済み。置くと「直した」と誤読される）→ 同上。

試験の置き場所: 4 はテンプレートの静的走査で行う（#1435 の既存検査と同じ形。`k8s-local-up.test.js` は helm を
スタブにする static-checks ジョブで走り、helm が無い）。チャートの `strategy` は 2 件とも条件分岐の外にあるため、
静的走査と描画結果は一致する（`helm template` v4.2.1 の描画で seaweedfs / wiki-js の 2 件を確認した）。

## 検証

- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` / `node scripts/k8s-local-up.test.js` / `check-trace-blocks` /
  `check-commit-messages --range=origin/develop..HEAD` / `helm template`（v4.2.1）/ `check-deploy-manifests`
- 変異 2 件以上（patch の呼び出しを消す・列挙から wiki-js を落とす 等）で赤になること
