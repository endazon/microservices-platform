---
title: "k8s-local-up.sh から apiserver OIDC フラグ付与の分岐を除去し HEADLAMP=1 を安全化する（Issue #399）"
type: spec
status: done
related_ids:
  - IADR-0105
  - IADR-0084
  - IADR-0080
  - IADR-0087
  - NFR
author: claude
created: 2026-07-26
updated: 2026-07-26
related_specs:
  - "../adr/IADR-0105_remove-apiserver-oidc-flag-wiring.md"
  - "../adr/IADR-0084_headlamp-oidc-apiserver-flags.md"
  - "../adr/IADR-0087_k8s-local-up-optin-smoke-test.md"
  - "20260726_issue-328_headlamp-token-login-docs.md"
  - "../../deploy/local/README.md"
---

# 仕様書: apiserver OIDC フラグ付与経路の除去（Issue #399）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: 該当なし（NFR 運用性＝ローカル k8s の既定手順がクラスタを壊さないこと・再現性）
- ユースケース（UC）／画面（SC）: 該当なし（dev 環境の起動オーケストレーション）
- 関連 ADR: [IADR-0105](../adr/IADR-0105_remove-apiserver-oidc-flag-wiring.md)（本作業の決定）／
  [IADR-0084](../adr/IADR-0084_headlamp-oidc-apiserver-flags.md)（除去対象の実装・適用不能の実測根拠）／
  [IADR-0080](../adr/IADR-0080_headlamp-k8s-management-ui.md)（Headlamp 導入）／
  [IADR-0087](../adr/IADR-0087_k8s-local-up-optin-smoke-test.md)（opt-in ゲート smoke test）
- Issue: #399（bug/infrastructure・priority:must）。`Refs #328`（wontfix）・#393（docs 側の先行 PR）

## 目的・背景

`scripts/k8s-local-up.sh` の k3d 経路に #328 由来の `HEADLAMP_OIDC_APISERVER` 分岐が残っており、この env は
**未設定なら `HEADLAMP` の値へ追従**する。そのため `HEADLAMP=1`（＝Headlamp を使う通常の立ち上げ）だけで
apiserver に OIDC 4 フラグが付与される。

k8s 1.30+ はレガシー `--oidc-*` を構造化認証設定（`jwt[0]`）へ変換し `issuer.url` に **https を強制**する一方、
経路B の Keycloak は `KC_HOSTNAME_URL=http://keycloak:8080` により token の `iss` が **http 固定**であり両立しない。
実測（`k3s v1.35.4+k3s1`）では apiserver が `URL scheme must be https` で **10 回連続起動失敗し、クラスタが停止**した。

#328 は wontfix（OIDC 化は #388 へ統合）と決まり、#393 で token 方式が正式手順として docs 化された。本作業は
その**コード側のフォローアップ**＝危険な既定を持つ分岐そのものをスクリプトから取り除く。

## 対象範囲

- 対象:
  - `scripts/k8s-local-up.sh`: apiserver OIDC フラグ付与ブロックの削除／既存クラスタ reuse 時の再作成 WARN の削除／
    `HEADLAMP=1` 完了メッセージを token 方式の案内へ更新。
  - `scripts/k8s-local-up.test.js`: 旧挙動を固定していた 4 テストを、**不付与を固定する回帰テスト**へ置換。
  - ドキュメント追随: `deploy/local/README.md`（`HEADLAMP_OIDC_APISERVER=0` 併記の回避策が不要になった旨）／
    `scripts/README.md`（ゲート一覧）／`docs/adr/README.md`（IADR-0105 索引・IADR-0084 の状態）。
- 対象外（無改変）:
  - `deploy/local/headlamp/` の manifest・`headlamp-oidc` Secret・realm.json・ClusterRoleBinding。
    現行では inert だが無害で、#388 成立時にそのまま機能する（IADR-0084 追記 / IADR-0103）。
  - Rancher Desktop 経路（スクリプトは k8s を作らないため元々フラグ付与の対象外）。
  - 退避済みドロップイン `/root/99-headlamp-oidc.yaml.disabled` の扱い（**クラスタ側の状態**であり、注意は
    `deploy/local/README.md`（#393）に記載済み。スクリプトは生成も配置も参照もしない）。
  - `HEADLAMP` 無指定（既定）の挙動全般。

## 設計

### 1. スクリプト（`scripts/k8s-local-up.sh`）

| 箇所 | 変更 |
| --- | --- |
| `[1/7] cluster`（k3d 経路） | `if [ "${HEADLAMP_OIDC_APISERVER:-${HEADLAMP:-}}" = "1" ]` の `CREATE_ARGS+=(--k3s-arg "--kube-apiserver-arg=oidc-*")` ブロックを**削除**。冒頭コメントを IADR-0105 の理由（https 強制 × http issuer・付けるとクラスタ停止）へ差し替え |
| `[1/7]` reuse 分岐 | 「後付け不可・delete して再作成せよ」の WARN を**削除**（後付け対象のフラグが無くなるため）。`cluster '$CLUSTER' exists — reuse` の 1 行のみに戻す |
| `[opt-in] Headlamp` | 完了メッセージから「apiserver OIDC は [1/7] で自動付与」の記述を削除し、**token 方式の発行コマンド**と README 導線を案内 |

`HEADLAMP_OIDC_APISERVER` / `HEADLAMP_OIDC_ISSUER_URL` / `HEADLAMP_OIDC_CLIENT_ID` は**参照しない**＝指定しても
no-op（fail-fast や警告は足さない。IADR-0105 決定4）。

### 2. 回帰テスト（`scripts/k8s-local-up.test.js`）

IADR-0087 の bash stub-on-PATH ハーネスを流用し、旧 4 テスト（4 フラグ付与／`HEADLAMP` 追従／`=0` escape-hatch／
issuer・client override）を以下へ置換する。判定は共通ヘルパー `assertNoApiserverOidc()` で、採取したコマンド列に
`kube-apiserver-arg` / `--k3s-arg` / `oidc-issuer-url` / `oidc-client-id` / `oidc-username-claim` /
`oidc-username-prefix` / `99-headlamp-oidc` が**一切現れない**ことを固定する。

1. `HEADLAMP=1`: `k3d cluster create` が既定とバイト等価・apiserver 痕跡ゼロ・overlay と `headlamp-oidc` は適用される。
2. `HEADLAMP_OIDC_APISERVER=1`（除去済み env の明示）: no-op（create はバイト等価・痕跡ゼロ）。
3. `HEADLAMP_OIDC_ISSUER_URL` / `HEADLAMP_OIDC_CLIENT_ID` の override: 値が引数へ**漏れない**。
4. `HEADLAMP=1` × 既存クラスタ reuse: `cluster create` を呼ばず、**OIDC の WARN も stderr に出ない**。
   reuse 経路を通すため k3d スタブに `STUB_CLUSTER_EXISTS=1`（`cluster list` を exit 0 に）を追加する。

`OPTIN_TOKENS`（既定オフ時の不在チェック）の `kube-apiserver-arg=oidc` は、より広い `kube-apiserver-arg` へ拡張する。

## 受け入れ基準

- [x] `HEADLAMP=1` で apiserver へフラグ（`--kube-apiserver-arg=oidc-*`）を付与しない
- [x] `HEADLAMP=1` の `k3d cluster create` 引数が既定（`HEADLAMP` 無指定）とバイト等価
- [x] `HEADLAMP` 無指定の既定挙動が不変（cluster create もその後の全ステップも変更なし）
- [x] 旧 env（`HEADLAMP_OIDC_APISERVER` / `_ISSUER_URL` / `_CLIENT_ID`）を指定しても no-op で、値が引数へ漏れない
- [x] 既存クラスタ reuse 時に「再作成せよ」の OIDC WARN が出ない
- [x] `HEADLAMP=1` は Headlamp overlay ＋ `headlamp-oidc` Secret の適用のみを行い、完了メッセージが token 方式を案内する
- [x] `node scripts/k8s-local-up.test.js` が green（41 tests）
- [x] `node scripts/check-doc-links.js` が green
- [x] CI（k8s-local-up-smoke / doc-links / gitleaks / commit-messages / pr-title ほか）green

### 検証コマンド

```bash
node scripts/k8s-local-up.test.js
node scripts/check-doc-links.js
bash -n scripts/k8s-local-up.sh
```

回帰検知の確認として、変更前の `scripts/k8s-local-up.sh` に対して新テストを実行し
`HEADLAMP=1 で create がバイト等価でない` で失敗すること（＝旧挙動を捕捉できること）を確認した。

## 計画書との差異

- 差異: なし（NFR 運用性の範囲。計画書の記述に反する変更は無い）

## 未決事項

- OIDC ログインの再導入は **#388**（全経路 HTTPS 化）で issuer/CA 前提とともに設計し直す。本作業はそれを妨げない。
- `headlamp-viewer` SA ＋ ClusterRoleBinding の manifest 化は **#398** で扱う（token 方式の手動 1 ステップの解消）。
