---
title: IADR-0105 k8s-local-up.sh から apiserver OIDC フラグ付与の経路を除去し、HEADLAMP=1 を Headlamp デプロイのみに戻す
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - IADR-0066
  - IADR-0080
  - IADR-0084
  - IADR-0087
  - IADR-0103
author: claude
created: 2026-07-26
updated: 2026-07-26
plan_refs:
  - planning:projects/microservices-platform/02_requirements/ (NFR 運用性＝ローカル環境が既定手順で壊れないこと)
---

# IADR-0105: apiserver OIDC フラグ付与経路の除去（`HEADLAMP=1` の安全化）

- 状態: Accepted
- 日付: 2026-07-26
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: NFR（運用性＝ローカル k8s の既定手順がクラスタを壊さないこと／再現性）
- 関連 ADR: [IADR-0084](./IADR-0084_headlamp-oidc-apiserver-flags.md)（#328。本 ADR が実装を除去する対象。「⚠️ 2026-07-25 追記」が適用不能の根拠＝単一情報源）／
  [IADR-0080](./IADR-0080_headlamp-k8s-management-ui.md)（#271。Headlamp 導入・`oidc:developer` への cluster-admin bind）／[IADR-0066](./IADR-0066_local-k8s-dev-environment.md)（経路B＝ローカル k8s dev 環境）／
  [IADR-0087](./IADR-0087_k8s-local-up-optin-smoke-test.md)（#334。`k8s-local-up.sh` の opt-in ゲート smoke test＝本変更の回帰固定先）／
  [IADR-0103](./IADR-0103_local-sso-persistence-and-claim-design.md)（#354。realm の `headlamp-realm-roles` mapper 恒久化＝現行では inert）
- 関連仕様書: `docs/specs/20260726_issue-399_remove-apiserver-oidc-flags.md`
- Issue: #399（bug/infrastructure・priority:must。#328 wontfix／#393 docs 化のコード側フォローアップ）

## コンテキストと課題

[IADR-0084](./IADR-0084_headlamp-oidc-apiserver-flags.md)（#328）は `scripts/k8s-local-up.sh` の k3d 経路に apiserver の OIDC 検証フラグを opt-in で配線した。
この opt-in は `HEADLAMP_OIDC_APISERVER` が未設定なら **`HEADLAMP` の値へ追従**する設計である
（`${HEADLAMP_OIDC_APISERVER:-${HEADLAMP:-}}`）。

しかし [IADR-0084](./IADR-0084_headlamp-oidc-apiserver-flags.md) の「⚠️ 2026-07-25 追記」のとおり、**k8s 1.30+ ではこの手順が成立しない**。レガシーな
`--oidc-*` は内部で構造化認証設定（`jwt[0]`）へ変換され、`issuer.url` に **https が強制**される
（`URL scheme must be https`。scheme の例外も insecure 用の逃げ道も無い）。一方、経路B の Keycloak は
`deploy/local/infra/keycloak.yaml` の `KC_HOSTNAME_URL=http://keycloak:8080` により token の `iss` が
**http 固定**であり、両立し得ない。実測（`k3s v1.35.4+k3s1`）では apiserver が **10 回連続で起動失敗し
クラスタが停止**した。

問題は、この危険な経路が **`HEADLAMP=1` という通常の立ち上げ手順で発火する**ことである。Headlamp を使いたい
だけの利用者は追加 env を知らずに実行し、クラスタごと起動不能になる。#393 は `deploy/local/README.md` に
「`HEADLAMP_OIDC_APISERVER=0` を必ず併記」という注意を書いたが、**危険な既定をドキュメントの注意書きで
回避させる**構図であり、スクリプト側の既定は安全側に倒っていない。

決めるべき点は「除去するか、既定オフに変えるだけで残すか」である。

## 検討した選択肢

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A. 経路を完全に除去 | フラグ付与ブロックと reuse 時 WARN を削除。`HEADLAMP_OIDC_APISERVER` / `HEADLAMP_OIDC_ISSUER_URL` / `HEADLAMP_OIDC_CLIENT_ID` は参照しない（＝指定しても no-op） | 危険経路が構造的に存在しなくなる。死にコードを残さない。復活は #388 で https issuer と同時に設計し直す |
| B. 追従をやめ `HEADLAMP_OIDC_APISERVER=1` の明示時のみ付与 | 既定は安全になるが、**明示すればクラスタを壊せる**経路が残る | 「現行 k8s では成立しない」と結論済みの手順を実行可能なまま残すことになる。誤って env が残った環境で再発する |
| C. スクリプトは据え置き、docs の注意のみ（現状＝#393） | コード変更ゼロ | 通常手順（`HEADLAMP=1`）が壊れるトラップのまま。本 issue の対象そのもの |

## 決定

**案 A を採る。** `scripts/k8s-local-up.sh` から apiserver OIDC フラグを付与する経路を除去する。

1. **フラグ付与ブロックの削除**: `k3d cluster create` の `CREATE_ARGS` へ `--k3s-arg "--kube-apiserver-arg=oidc-*"`
   を append する `if [ "${HEADLAMP_OIDC_APISERVER:-${HEADLAMP:-}}" = "1" ]` ブロックを削除する。以後スクリプトは
   **apiserver 引数を一切書かない**（`config.yaml.d` のドロップイン生成・配置も行わない。元々行っていない）。
2. **reuse 時 WARN の削除**: 「既存クラスタには後付け不可・delete して再作成せよ」という WARN は、後付けする対象の
   フラグ自体が無くなるため削除する（残すと存在しない機能の再作成を促すことになる）。
3. **`HEADLAMP=1` の意味を戻す**: Headlamp の overlay 適用（`deploy/local/headlamp`）と OIDC client secret
   （`headlamp-oidc`。ESO=1 なら ExternalSecret 供給）**のみ**。ログインは **token 方式**が正式手順で、完了メッセージも
   `kubectl -n platform-infra create token headlamp-viewer --duration=24h` を案内する（[IADR-0084](./IADR-0084_headlamp-oidc-apiserver-flags.md) 追記／#393）。
4. **旧 env は no-op**: `HEADLAMP_OIDC_APISERVER` / `HEADLAMP_OIDC_ISSUER_URL` / `HEADLAMP_OIDC_CLIENT_ID` は
   スクリプトから参照されなくなる。**シェルに残っていても・古い手順書をなぞっても壊れない**（値は引数へ漏れない）。
   新しいエラーや fail-fast は足さない（未使用 env の検出は本リポの「起こり得ないケースへの防御的実装を足さない」方針の外）。
5. **回帰固定**: [IADR-0087](./IADR-0087_k8s-local-up-optin-smoke-test.md) の smoke test（`scripts/k8s-local-up.test.js`）で、`HEADLAMP=1` / 旧 env 明示 /
   override env / 既存クラスタ reuse のいずれでも `kube-apiserver-arg`・`--k3s-arg`・`oidc-*`・`99-headlamp-oidc` が
   **一切現れない**ことを固定する。`HEADLAMP=1` の `k3d cluster create` は既定とバイト等価であることも併せて固定する。

Headlamp 自身の OIDC 設定（`deploy/local/headlamp/` の manifest・realm の client / mapper・`headlamp-oidc` Secret）と
ClusterRoleBinding は**無改変**とする。現行では inert（`oidc:` の identity が生成されない）だが無害であり、#388 成立時に
そのまま機能する（[IADR-0084](./IADR-0084_headlamp-oidc-apiserver-flags.md) / [IADR-0103](./IADR-0103_local-sso-persistence-and-claim-design.md)）。

## 理由

- **安全既定の原則**: 本リポの opt-in ゲートは「既定オフ・fail-safe」で運用してきた。ところが本件の opt-in は
  `HEADLAMP` 追従により**実質的に既定オン**であり、しかも失敗モードが「クラスタ全体の起動不能」＝最も重い。
  ドキュメントの注意書き（案 C）ではなく、コード側を安全側へ倒す。
- **成立しない手順を残さない**: 案 B の「明示時のみ」は、現行 k8s で**必ず失敗すると分かっている**操作を実行可能な
  まま残す。設定ミス由来の環境変数漏れや古い手順の再利用で再発する。OIDC 化は issuer の https 統一とセットでしか
  成立せず、その設計は #388 で改めて行う（そのとき必要な配線は当時の issuer/CA 前提に合わせて作り直す方が正しい）。
- **後方互換**: `HEADLAMP` 無指定の既定経路は完全に不変（`k3d cluster create` 引数もその後の全ステップも同一）。
  `HEADLAMP=1` の挙動は「壊れる → 壊れない」方向の変化のみで、Headlamp のデプロイ内容は変わらない。

## 結果

- 良い影響:
  - `HEADLAMP=1 bash scripts/k8s-local-up.sh` が k3d 経路でもクラスタを壊さない（通常手順のトラップ解消）。
  - `deploy/local/README.md` の「`HEADLAMP_OIDC_APISERVER=0` を必ず併記」という**利用者側の記憶に依存した回避策が不要**になる。
  - opt-in ゲートの意味論が「既定オフ・明示時のみ・fail-safe」に揃う。
- 悪い影響・トレードオフ:
  - apiserver OIDC の**単体検証用の手段がスクリプトから失われる**。必要なら `k3d cluster create` を手で叩く
    （ただし現行 k8s では https issuer が無い限り失敗する）。
  - [IADR-0084](./IADR-0084_headlamp-oidc-apiserver-flags.md) の決定1・4 は本 ADR により実装が失われる（ADR 本文は履歴として残し、状態を Superseded へ移す）。
- フォローアップ:
  - **#388**（全経路 HTTPS 化）で issuer を https へ統一する際、apiserver 側の OIDC 配線（`oidc-ca-file` を含む）を
    改めて設計・実装する。本 ADR はその再導入を妨げない。
  - **#398**（`headlamp-viewer` SA ＋ ClusterRoleBinding の manifest 化）が入れば、token 方式が `HEADLAMP=1` だけで
    再現可能になる（本 ADR の範囲外）。

## 関連

- Supersedes: [IADR-0084](./IADR-0084_headlamp-oidc-apiserver-flags.md)（決定1「`HEADLAMP_OIDC_APISERVER` による opt-in 付与」・決定4「reuse 時の再作成 WARN」の実装。
  適用不能の根拠と実測記録は IADR-0084 の追記が引き続き単一情報源）
