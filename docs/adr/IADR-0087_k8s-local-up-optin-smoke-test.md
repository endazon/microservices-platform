---
title: IADR-0087 k8s-local-up.sh の opt-in ゲートは bash stub-on-PATH でスクリプト無改変のまま横断 smoke test する
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - IADR-0066
  - IADR-0084
author: claude
created: 2026-07-20
updated: 2026-07-20
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/ (NFR 運用性・信頼性)"
---

# IADR-0087: k8s-local-up.sh の opt-in ゲート横断 smoke test の方式（bash stub-on-PATH・スクリプト無改変）

- 状態: Accepted
- 日付: 2026-07-20
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: NFR（運用性・信頼性＝dev 起動スクリプトの opt-in 分岐が「既定オフ＝副作用ゼロ／
  有効化時のみ該当リソース・引数付与」という不変条件を機械で固定し後退を止める）。
- 関連 ADR: [[IADR-0084]]（#328。`k3d cluster create` の apiserver OIDC フラグ＝本テストの主要検証対象）／
  [[IADR-0066]]（経路B＝k3d dev 環境・`k8s-local-up.sh` の全体構造）。
- 関連仕様書: `docs/specs/20260720_issue-334_k8s-optin-gates-smoke-test.md`。
- Issue: #334（運用/dev・testing・priority:could。#331＝#328/IADR-0084 の claude-review 🟡 フォローアップ）。

## コンテキストと課題

> **［2026-07-26 追記］`HEADLAMP_OIDC_APISERVER` ゲートは [[IADR-0105]]（#399）で除去された。** 本 ADR のテスト方式
> （bash stub-on-PATH）と他ゲートのカバレッジは有効なまま。当該ゲートを検証していたテストは「どの env でも
> apiserver 引数・ドロップインを書かない」ことを固定する**回帰テストへ置換**した。

`scripts/k8s-local-up.sh` は複数の opt-in 環境変数ゲート（`HEADLAMP_OIDC_APISERVER`／`PERSIST`／
`OBSERVABILITY`／`VAULT`／`ARGOCD`／`HEADLAMP`）を持つ。#331 の claude-review は、`HEADLAMP_OIDC_APISERVER`
の `CREATE_ARGS` 構築ロジックに自動テストが無い点を 🟡 とし、同時に「これは #331 固有の後退ではなく既存の
他フラグも同様に未カバー」と述べた。単発テストではなく **全ゲート横断の smoke test** が必要である。

課題は「実クラスタを作らず、外部バイナリ（`k3d`/`kubectl`/`helm`/`docker`）を呼ばず、
opt-in 分岐の入力（env）→ 出力（発行される `k3d cluster create` 引数・apply されるオーバーレイ/manifest）を
どう検証するか」である。

## 検討した選択肢

1. **bash stub-on-PATH（スクリプト無改変）**〔採用〕: 外部バイナリを PATH 上の記録スタブへ差し替え、
   `k8s-local-up.sh` を副作用ゼロで実行し、採取したコマンド列へアサートする。テストは
   `scripts/k8s-local-up.test.js`（Node 標準 `assert` のみ・`bash` を spawn）。
2. **arg 構築部を sourceable bash 関数へ抽出**〔不採用〕: `CREATE_ARGS` 構築やゲート判定を
   `scripts/k8s-local-lib.sh` 等へ抽出し `source` してユニットテストする。
3. **k8s-local-up.sh に plan/dry-run モードを追加**〔不採用〕: 全コマンドを `run()` ラッパ経由にし、
   plan モードで実行せず print する。

## 決定

選択肢 1（bash stub-on-PATH・スクリプト無改変）を採用する。

- **`k8s-local-up.sh` を一切編集しない**。opt-in 分岐の挙動を **バイト等価**のまま固定でき、後退リスクが最小
   （#334 の制約「編集する場合は挙動を変えずテスト可能にする最小変更に限定」の最良解＝改変ゼロ）。
- テストは既存 `scripts/scripts.test.js` と同型（Node 標準 `assert` のみ・外部依存ゼロ）とし、CI は
   `ci.yml` に独立ジョブ `node scripts/k8s-local-up.test.js` を追加する（各 `--self-test` 系ジョブと同じ運用）。
- 決定性の担保: `K8S_LOCAL_RUNTIME=k3d` を固定して runtime 自動判定を回避し、`k3d cluster list` スタブを
   非0（未作成）に返させて `cluster create` 経路を必ず通す。`src/ai-stock-trading` submodule 未取得
   （CI 既定チェックアウト）で AST 分岐（realm 同梱・argocd 追加 apply）は決定的に skip する。

## 理由・トレードオフ

- 選択肢 2/3 は `k8s-local-up.sh`（live dev 起動の中核スクリプト）を改変する。テストのための構造変更は
   挙動不変性の証明コストと後退リスクを生む。#334 は純テストタスクであり、スクリプト改変は目的に反する。
- stub-on-PATH の弱点は「スクリプトが外部バイナリを PATH 経由で呼ぶ前提に依存」する点だが、
   `k8s-local-up.sh` は実際に `k3d`/`kubectl`/`helm`/`docker` を PATH 経由で呼ぶため成立する。
   フル実行（[1/7]..[opt-in]）を通すため僅かに遅いが、全スタブが即 0 を返すため実測 <1s。
- 高々 opt-in ゲートの入出力を固定する smoke test であり、スタブのコマンド列に**新種コマンドの混入は許容**
   （存在アサート中心・不在アサートは opt-in 由来リソースに限定）することで、無関係な [1/7]..[7/7] 追記に
   脆くしない。

## 影響

- 追加: `scripts/k8s-local-up.test.js`（新規テスト）・`ci.yml` の `k8s-local-up-smoke` ジョブ・
   本 ADR・作業仕様書。
- 変更なし: `scripts/k8s-local-up.sh`（無改変）・その他 opt-in オーバーレイ・realm・values・Dockerfile 群。
