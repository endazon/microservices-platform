---
title: IADR-0261 Keycloak テーマ（loginTheme/accountTheme）の実装方針と smtpServer の非コミット注入方式
type: impl-adr
status: Accepted
related_ids: [SC-13, SC-14, SC-15, SC-16, ADR-0026, ADR-0045]
author: Claude（実装）
created: 2026-08-23
updated: 2026-08-23
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0026_authentication-ux-and-account-management.md
  - planning:projects/microservices-platform/07_adr/ADR-0045_mail-delivery-smtp-relay.md
---


# IADR-0261: Keycloak テーマ（loginTheme/accountTheme）の実装方針と smtpServer の非コミット注入方式

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定ではなく、
> 同一 issue（#438）内で連動する 2 つの決定（テーマ実装・smtp 注入方式）を 1 本にまとめる
> ——後者が前者の realm.json 変更（`loginTheme`/`accountTheme` 追加）と同じ diff に属し、分けると
> 「なぜ smtpServer だけ realm.json に無いのか」の理由（決定 2）が前者の文脈なしに読めなくなるため。

- 状態: Accepted
- 日付: 2026-08-23
- 決定者: Claude（実装）

## 起点・関連

- 関連する計画書 ID: ADR-0026（認証UXとアカウント管理）・ADR-0045（メール送信基盤）・SC-13〜16
- 関連する実装仕様書: [`.ai-context/specs/20260823_issue-438_keycloak-theme-and-smtp.md`](../specs/20260823_issue-438_keycloak-theme-and-smtp.md)
- 先行する実装ADR: [IADR-0197](./IADR-0197_realm-rename-and-auth-policy.md)（レルム改名・認証ポリシー投入。
  「smtpServer とテーマは投入しない」と決めた側。本 IADR がその「投入する側」を引き継ぐ）

## コンテキストと課題

issue #438 の残作業は 2026-08-21 時点で次の 2 点に絞られている（issue 本文コメント参照）。

1. Keycloak テーマ（`loginTheme` / `accountTheme`）が未設定・実体が無い
2. `smtpServer` が未設定（実環境の値が要るため #578/IADR-0197 が意図的に投入しなかった）

決めるべきことは 2 つ。

- **テーマをどこまで作り込むか。** SC-13〜16 の要件（ブランド適用・言語切替・既定テーマの機能はそのまま）を
  満たしつつ、Keycloak 本体のアップデート追随性を失わない実装方式を選ぶ必要がある。
- **smtpServer の秘匿値（`from`/`user`/`password`）を、`smtpServer.host`/`port`/`starttls` のような
  非秘匿値と同じ場所（バージョン管理下の realm.json）に置かない方式をどう設計するか。**

## 検討した選択肢

### 決定 1（テーマ実装方式）の選択肢

| 案 | 内容 | 評価 |
| --- | --- | --- |
| **A. `parent=keycloak` を継承し CSS のみ追加** | テンプレート（`.ftl`）は複製しない。`theme.properties` で親の `styles` 行を引き継ぎ、自テーマの CSS を追加する | **採用**。Keycloak 本体のテンプレート更新（セキュリティ修正・新フロー対応）にそのまま追随できる |
| B. `.ftl` テンプレートを全複製してブランド適用 | 見た目の自由度は最大 | 複製した瞬間に Keycloak 本体のテンプレート更新から切り離される。ADR-0026 が求めるのは「ブランド適用」であり全面カスタムではない |
| C. Keycloak 既定テーマのまま `displayName` のみで済ませる（テーマ未作成） | 工数最小 | `loginTheme`/`accountTheme` が未設定のままであり、issue の受け入れ基準（テーマの実体を作る）を満たさない |

### 決定 2（smtpServer 秘匙値の扱い）の選択肢

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A. realm.json に `smtpServer` 全体を書き、`password` だけ Keycloak の `${vault.expr}` 構文で外だしする | Keycloak 公式のベールト機構に乗る | **不採用**。`${vault.expr}` はいずれの環境（compose／k8s ローカル）にも vault SPI provider（files-plaintext 等）が未配線であり、**新たな SPI 配線が要る**——本 issue の射程（「設定手順の整備」）を超える。加えて `from`/`user` は Keycloak の vault 対応フィールドではないため、この案でも別解が要る |
| **B. `smtpServer` を realm.json へ入れず、Vault → ExternalSecret → k8s Secret → `kcadm.sh update realms/platform -s ...` の手順で実行時に反映する** | 秘匿値はいずれのバージョン管理ファイルにも現れない。既存の Vault/ESO パターン（`keycloak-admin` 等）と同型 | **採用**。既存の secret 供給パイプラインをそのまま延長できる。realm 再インポート時に消える点は runbook の限界として明記する |
| C. Kubernetes Secret を直接 `kubectl create secret` する（Vault を経由しない） | 単純 | 既存の secret はすべて Vault/ESO 経由（IADR-0096）に寄せている。ここだけ別経路にすると供給元が割れる |

## 決定

- **決定 1: 案 A**（`parent=keycloak` 継承 + CSS 追加のみ）。テーマ実体は
  `deploy/keycloak/themes/platform/{login,account}/`。`theme.properties` はテンプレートを複製しない。
  realm.json に `loginTheme` / `accountTheme` を `"platform"` として投入する。あわせて SC-13 の主要素
  「言語切替」を実現するため `internationalizationEnabled: true` / `supportedLocales: ["ja","en"]` /
  `defaultLocale: "ja"` を投入する（テーマの作り込みではなく realm 設定で完結する）。
- **決定 2: 案 B**（Vault → ExternalSecret → kcadm）。
  - Vault: `secret/msp/keycloak-smtp`（`bootstrap.sh` が env 由来 or 空既定で seed。既存の他 secret と同型）
  - ExternalSecret: `deploy/local/vault/eso/externalsecret-keycloak-smtp.yaml`（k8s Secret `keycloak-smtp`・platform-infra ns）
  - 反映: `docs/operations/keycloak-smtp-relay-setup-runbook.md` の kcadm 手順（**realm.json は書き換えない**）
  - **`host`/`port`/`starttls` は秘匿値ではない**（ADR-0045 決定 2-b の確定書式）ため Vault 側の既定値に含めてよいが、
    **realm.json への静的投入はしない**——単一の反映経路（kcadm）に統一し、「realm.json に一部だけ入っていて
    残りは実行時」という分割された状態を作らないため。

## 理由

- **決定 1**: ADR-0026 が求めるのは「ブランド適用（表示名・配色）」であり、認証フロー自体の作り込みではない
  （選択肢 1「Keycloak のテーマ機能で認証画面を提供」の主眼は IdP への委譲）。テンプレート非複製は
  この委譲の意図と直接に整合する。
- **決定 2**: `${vault.expr}` 案（A）は一見 Keycloak 純正の機構だが、**未配線の SPI を新設する**ことになり、
  「smtpServer は設定手順の整備までが限度」という本 issue の裁定（利用者裁定 2026-08-15）を超える。
  既存の Vault/ESO パイプライン（IADR-0096）をそのまま延長する案 B は、**新しい仕組みを持ち込まない**という
  点で最小の追加である。

## 結果

- 良い影響: docker-compose 環境ではテーマが即座に有効（ホストマウント）。k8s ローカルでも declarative な
  受け皿（ConfigMap 参照・`optional: true`）を用意済みで、follow-up（scripts/ 配線）が入ればすぐ機能する。
  smtpServer の秘匿値がリポジトリのどこにも現れない。
- 悪い影響 / トレードオフ: **k8s ローカル（`deploy/local/`）は本 PR だけでは loginTheme を解決できない**
  （`scripts/k8s-local-up.sh` の変更が要るが、本 issue の担当領域外——[作業仕様書](../specs/20260823_issue-438_keycloak-theme-and-smtp.md) §残件参照）。
  **realm を再インポートすると smtpServer の実行時反映は消える**（runbook の再実行が要る）。
- フォローアップ:
  1. `scripts/k8s-local-up.sh` へ `keycloak-theme-platform` ConfigMap 生成と
     `externalsecret-keycloak-smtp.yaml` の apply を組み込む（別 issue。scripts/ 配下の担当）。
  2. 組織のメールテナント（ADR-0045 決定 1）へ移行した際、決定 2 の Vault path の値を差し替える
     （手順は runbook 側で完結し、本 IADR の改定は不要）。
  3. `${vault.expr}` による Keycloak 純正のベールト機構は、SPI 配線のコストが見合うと判断されたら
     決定 2 を再検討してよい（現時点では不採用としたに留める）。

## 関連

- Supersedes: なし
- Superseded by: なし
