---
title: IADR-0585 秘匿管理の kubernetes 認証の role は対象者（audience）を vault に限り、名乗る側（ESO・BFF・datasource・バックアップ）は対象者 vault のトークンを渡し、配備は名乗る側を先に出す
type: impl-adr
status: Accepted
related_ids: [NFR-18, ADR-0095, ADR-0132, IADR-0096, IADR-0433, IADR-0495, IADR-0548, IADR-0523]
author: claude
created: 2026-10-22
updated: 2026-10-22
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0132 （秘匿管理は OpenBao）
  - planning:projects/microservices-platform/07_adr/ADR-0095 （SC-22 の秘密情報の投入面）
related_specs:
  - ../specs/20261022_ghsa-j4qv_vault-k8s-auth-audience.md
---

# IADR-0585: 秘匿管理の kubernetes 認証の対象者（GHSA-j4qv-2w99-xg5p）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-22
- 決定者: コーディネーター指示（2026-10-22。発行者・対象者の検証）・claude（値・パス・順序・issuer の扱い）

## 起点・関連

- 起点: GHSA-j4qv-2w99-xg5p（多層防御の強化・最小権限）。本 IADR はその 5 点目（発行者・対象者の検証）。
- 計画: **ADR-0132**（秘匿管理は OpenBao）・**ADR-0095**（SC-22 の投入面）。矛盾しない（製品・供給の仕組みは変えない）。
- 前提: IADR-0096（ESO の k8s auth）・IADR-0433（BFF の role）・IADR-0495（datasource の role）・IADR-0548（バックアップの role）。
  アプリの JWT の対象者は IADR-0523 が既に検証している（本 IADR の射程外）。

## コンテキストと課題

- 秘匿管理（OpenBao 2.7.1。Vault API 互換）の kubernetes 認証は、ServiceAccount のトークンを TokenReview で確かめて role に束縛する。
  role に `audience` を与えると、トークンの `aud` にその値があることも要求できる。
- k8s API 用のトークン（対象者＝API サーバ）と、秘匿管理へ名乗るためのトークンを分ける。最小権限・多層防御の強化として、
  role が受けるトークンを秘匿管理向けに発行したものに限る。

## 検討した選択肢

1. **role だけに audience を付ける**: 名乗る側が対象者＝API サーバのトークンを渡し続けるため、ログインできなくなる。採らない。
2. **名乗る側に projected token（対象者 vault）を持たせ、role に audience を付ける**（採用）。
3. **issuer も設定して検証させる**: OpenBao の `issuer` / `disable_iss_validation` は非推奨（将来削除）と明記され、既定は検証しない。
   発行者・署名・期限・束縛の検証は TokenReview（API サーバ）が行う。k3s / k3d で issuer の値が配布形態に依存するため、
   固定値を書くと別の配布形態で全ログインが落ちる。採らない（決定 3）。

## 決定

1. **受ける側**: deploy/ の bootstrap が書く k8s auth の role（`eso`・`bff-secret-writer`・`datasource-connector-reader`・`platform-backup-vault`）は
   全て `audience=vault` を持つ。新しい role も同じ（`scripts/vault-k8s-auth-audience.test.js` が deploy/ の .sh を走査して固定する）。
2. **名乗る側**:
   - ESO: `ClusterSecretStore vault-backend` の k8s 認証版の `serviceAccountRef.audiences: [vault]`（ESO が TokenRequest で対象者 vault のトークンを作る）。
   - BFF・datasource: chart が `vault.address` の空でないサービスに projected token（`audience: vault`・`expirationSeconds: 600`）を
     `/var/run/secrets/vault/token` へ読み取り専用で置き、`Vault__ServiceAccountTokenPath` で渡す。コードの既定（`DefaultServiceAccountTokenPath`）も同じパス。
     k8s API 用の既定のトークンは残す（BFF は ExternalSecret の同期に k8s API を使う。IADR-0456）が、秘匿管理へはそれで名乗らない。
   - バックアップの CronJob: projected token に `audience: vault`。
3. **issuer は設定しない**（選択肢 3）。`auth/kubernetes/config` は `kubernetes_host` だけのまま。発行者の検証は TokenReview に委ねる。
4. **配備の順序は「名乗る側を先、role の audience を後」**。OpenBao の k8s auth は、role に audience が無いとき TokenReview に
   **トークン自身の対象者**を渡す（`internal/builtin/credential/kubernetes/path_login.go` の `lookup`。v2.7.1 の版で確認）。
   したがって対象者 vault のトークンは audience の無い role でもログインでき、名乗る側を先に出せば途中で失敗しない。
   逆順（role を先）だと、名乗る側が入れ替わるまで対象者＝API サーバのトークンが拒まれる。
   起動器（`scripts/k8s-local-up.sh`）は [6/7] の chart → VAULT ブロック（overlay・バックアップの bootstrap）→ ESO ブロック（bootstrap → store）の順で、
   この条件を満たす（ESO の bootstrap の間、store は VAULT ブロックが当て直した token 認証版である）。順序は同試験 5 が固定する。
   **稼働中の環境では、起動器を 2 回に分けて当てる**（下の「配備の 2 段」）。起動器は 1 回で role まで当てるため、
   名乗る側だけを入れ替えた状態で止めて確かめる口として、環境変数 `VAULT_ROLE_AUDIENCE_DEFER=1` を設ける
   （`scripts/lib/vault-role-audience.sh`。2 つの bootstrap の role の書き込みはこの口を通り、`1` のときだけ引数 `audience=vault` を除く。
   role の書き込みは与えないフィールドを保つので、role の audience は書き換わらない）。既定（未設定・`1` 以外）は字面どおり
   `audience=vault` を書く。口の挙動・全 role が口を通ることは同試験 5b が変異つきで固定する。
5. **後方互換の期間は設けない**（ローカル・PoC 専用の構成で、起動器 1 回の再実行で全てが入れ替わるため）。

## 結果・残る制約

- 決定 4 の根拠（audience の無い role はトークン自身の対象者で TokenReview する）はソースの読解であり、実機では未確認である。
  したがって稼働中の環境の配備は次の 2 段に分け、段の間で確かめる（PR 本文の PoC 手順と同じ）。
- **配備の 2 段**:
  1. **1 段目（名乗る側だけ）**: `VAULT_ROLE_AUDIENCE_DEFER=1` を付けて起動器を通常の引数のまま再実行する。chart（BFF・datasource の
     projected token）・ESO の store（`audiences: [vault]`）・バックアップの CronJob が入れ替わり、role は audience の無いまま残る。
  2. **確かめる（role を変える前）**: BFF の書き込み（`/admin/secrets` で 1 項目）、`kubectl get externalsecret -A` が全て SecretSynced
     （store を当てた後に force-sync して再同期させたもの）、datasource がコネクタの資格情報を読めること、バックアップの CronJob を手で 1 回
     起こして Complete になること。**1 つでも失敗したら 2 段目へ進まない。** role は変わっていないので、前の版の作業ツリーで起動器を
     再実行して名乗る側を戻せば元に戻る（切り戻し A）。
  3. **2 段目（role）**: 変数なしで起動器を再実行する（2 つの bootstrap が role に `audience=vault` を付ける）。同じ確かめをもう一度行い、
     4 つの role の `audience` が `vault` であることを読む。
- `vault write auth/kubernetes/role/<r>` は与えないフィールドを保つ。**古い版の bootstrap を再実行しても・`VAULT_ROLE_AUDIENCE_DEFER=1` で
  再実行しても、付いた audience は外れない。**
- **2 段目の後の切り戻し（切り戻し B）**は、role の audience を外してから名乗る側を戻す（順序は決定 4 の逆）。外し方は 2 通り:
  - B1: `audience=""` を明示して role を書き直す。空文字が「与えた」として audience を空にすることは `path_role.go`（v2.7.1）の読解であり、
    **実機では未検証である**。2 段目の直後（確かめが通った後）に、1 つの role で `audience=""` を書いて `vault read -field=audience` が空になる
    ことを 1 回試し、すぐ `audience=vault` へ戻しておく（戻すには変数なしで起動器を再実行するか、同じ role を `audience=vault` で書き直す）。
    空にならなければ B1 は使わず B2 による。
  - B2（代替）: role を消して audience 無しで作り直す。`vault delete auth/kubernetes/role/<r>` の後、`VAULT_ROLE_AUDIENCE_DEFER=1` で
    起動器（または 2 つの bootstrap）を再実行すると、role は audience の無い形で作られる（消してから作り直すまでの間は、その role での
    新しいログインが通らない）。
- 統合スタック（`integration-stack`）は VAULT / ESO を有効にしない。ESO=1 の経路は CI では描画・字面の試験だけで、実機の確かめは PoC の配備後の確認に委ねる。
- ESO の版（chart 2.8.0）の `serviceAccountRef.audiences` は v1 の API の項目である。

## 検証

- `node scripts/vault-k8s-auth-audience.test.js`（7 本・変異つき。5b は配備の 2 段の口）・`node scripts/platform-backup.test.js`・.NET の 2 本
  （`SecretItemVaultPolicyTests`・`ConnectorSecretVaultPolicyTests`）。新しい試験は deploy/ と src/ の変更を外すと赤になる。
