---
title: IADR-0606 無人主体の判定は、IdP が書く発行元のクライアント識別（azp）を前提にし、利用者名はそのクライアントのサービスアカウント名と一致するときだけ無人と読む
type: impl-adr
status: Accepted
related_ids: [NFR-09, ADR-0032, ADR-0036, ADR-0085, IADR-0420, IADR-0429, IADR-0449, IADR-0410, IADR-0426, IADR-0476, IADR-0524]
author: claude
created: 2026-10-23
updated: 2026-10-23
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md（NFR-09）
  - planning:projects/microservices-platform/07_adr/ADR-0032_spa-auth-bff-session.md
related_specs:
  - ../specs/20261023_ghsa-94cm_machine-principal-issuing-client.md
---

# IADR-0606: 無人主体の判定を発行元のクライアント識別に基づかせる（GHSA-94cm-55g9-2967）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-23
- 決定者: claude（GHSA-94cm-55g9-2967。多層防御の強化）

## 起点・関連

- 関連する計画書 ID: NFR-09（OIDC/JWT 認証）、ADR-0032（BFF セッション方式）、ADR-0085 決定 4（ユニットの主体の計上）。
- 前提とする実装 ADR: [IADR-0420](./IADR-0420_unit-subject-identification-and-project-missing-metric.md)（`MachinePrincipal` を基盤に 1 つ置く・許可集合を持たない）、
  [IADR-0429](./IADR-0429_platform-spa-removal-and-bearer-caller-narrowing.md) 決定 3（BFF の Bearer 受理）、
  [IADR-0449](./IADR-0449_user-and-group-lookup-are-interactive-user-only.md)（`InteractiveUser`）、
  [IADR-0524](./IADR-0524_keycloak-26-upgrade.md)（宣言から取り込んだ realm ではサービスアカウントのトークンに `client_id` が載らず、`azp` は載る）。
- 作業仕様書: [20261023_ghsa-94cm_machine-principal-issuing-client](../specs/20261023_ghsa-94cm_machine-principal-issuing-client.md)。

## コンテキストと課題

IADR-0420 は無人主体を「トークンが名乗る形」の 2 腕で判定した。腕 A は利用者名の接頭辞 `service-account-` だけを見ていた。
利用者名は realm の利用者管理で付ける値であり、発行元のクライアントを表す値ではない。判定の根拠を、IdP がトークンへ書き、
利用者が変えられない属性に寄せる（多層防御。realm の宣言の側は `check-realm-constraints.js` 検査 7 が既に守っている）。

## 決定

### 決定 1: クライアント識別のクレームを前提にする

`MachinePrincipal.IsMachine` は、クライアント識別のクレーム（`azp` → `client_id` → `clientId` の最初に在るもの）が無ければ偽を返す。
`azp` は Keycloak が発行元のクライアントとして access token に必ず書く値である。利用者名からの復元（`ClientIdOf` の後段）は判定に使わない。

### 決定 2: 腕 A は利用者名とクライアント識別の一致で判定する

利用者名がある主体は、利用者名が `service-account-<クライアント識別>` と**大小を無視して一致する**ときだけ無人と読む。
Keycloak はサービスアカウントの利用者名を `service-account-<clientId>` として作り、利用者名を小文字へ正規化する。
利用者名とクライアント識別の 2 つの出どころが同じサービスアカウントを指すことを求める。腕 B（利用者名が無い）は変えない。

### 決定 3: 許可集合・ロール・セッションの有無は使わない

- 許可集合（構成の一覧）は IADR-0420 の決定どおり持たない。
- ロールはサービスアカウントの全員に共通のものが無い（ロールなしのサービスアカウントがあり、人と同じロールを持つものもある）。
- セッション（`sid`）の有無は、版によって形が変わり得るのに稼働で測っていないので根拠にしない。
- `ClientIdOf` は変えない（ログ・計器の属性。`IsMachine` が真なら常にクレームの値を返す）。

### 決定 4: 試験器は実トークンの形を再現する

試験用の認証ハンドラ・JWT 発行器は、`service-account-<clientId>` を名乗る主体に `azp = <clientId>` を補う（明示したときは補わない）。
実トークンが取らない「サービスアカウント名だけで `azp` が無い」形を陽性に置いていた試験は、`azp` ありの形に畳む。

## 検討した代替案

| 案 | 採否 | 理由 |
| --- | --- | --- |
| 接頭辞の判定を残し、realm の宣言だけで守る | 不採用 | 宣言の外（稼働中の管理操作）で作られる利用者には届かない |
| `client_id` クレーム（26 の `service_account` スコープ）を必須にする | 不採用 | 宣言から取り込んだ realm では載らない（IADR-0524）。採るには全サービスアカウントのクライアントへスコープを配り、稼働の realm を再適用する必要がある |
| サービスアカウント用の専用ロールを新設し、それで判定する | 不採用 | realm の全サービスアカウントと、McpServer が作るクライアントへの付与が要り、付与の一覧が統制の抜け道になる（IADR-0420 が退けた形） |
| 宣言型ユーザープロファイルで利用者名のパターンを禁じる | 本 IADR では採らない | 稼働の realm の再適用と既存のサービスアカウントの更新への影響を稼働で測る必要がある。コード側の判定で足りる |

## 結果

- 良い影響: 無人の判定が、IdP が書く値と利用者名の両方の一致に基づく。正規のサービスアカウント（realm の宣言の全件）は従前どおり無人と判定される（試験で固定）。
- 悪い影響 / 残余リスク:
  1. Keycloak の命名規則（`service-account-<clientId>`）への依存は残る（IADR-0420・IADR-0449 と同じ受容済みのリスク）。
  2. サービスアカウントを持たず人がログインするクライアントの `azp` と、利用者名 `service-account-<そのクライアント>` が揃う組は一致と読む。
     宣言の realm では、そのようなクライアントのトークンは platform の API の audience（IADR-0523）を持たず、受理の前に 401 になる。
     利用者名の側は検査 7 が宣言で禁じる。新しいクライアントへ platform の audience を割り当てるときは、この組を見直す。
  3. `client_id` / `clientId` が人のトークンに載る写像を realm へ足すと前提が崩れる。足さない（足すなら本 IADR を改める）。
