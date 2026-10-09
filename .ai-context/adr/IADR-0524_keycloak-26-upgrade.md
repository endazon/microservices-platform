---
title: IADR-0524 Keycloak を 24.0 から 26.7.4 へ上げる。hostname v2・管理用のポートのヘルス・realm 名どおりの取り込みのファイル名・24 の file H2 の資格・basic スコープ・転送ヘッダで 24 と同じ振る舞いを保ち、ループバックの port 必須は判断材料だけを門で測って残す
type: impl-adr
status: Accepted
related_ids: [FR-16, SC-12, NFR-09, NFR-18, ADR-0134, ADR-0004, ADR-0107, ADR-0112, ADR-0086, ADR-0026, IADR-0516, IADR-0514, IADR-0243, IADR-0369, IADR-0518, IADR-0086, IADR-0079, IADR-0082]
author: claude
created: 2026-10-09
updated: 2026-10-09
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0134_mcp-client-keycloak-template-and-secret-one-time-display.md 決定 1・フォローアップ 3
  - planning:projects/microservices-platform/07_adr/ADR-0107_infrastructure-product-selection-criteria.md 決定 2〜5
  - planning:projects/microservices-platform/07_adr/ADR-0086_user-context-in-body-not-token-exchange.md フォローアップ 4（計画側の作業。契機の通知だけ）
related_specs:
  - ../specs/20261009_1859_keycloak-26-upgrade.md
---

# IADR-0524: Keycloak 24.0 → 26.7.4（#1859）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-09
- 決定者: claude（版の選び方・26 で変わった前提への合わせ方・門の対の形）。ループバックの port 必須を外すかは**製品の判断**として残す。

> 🔴 **番号について**: 起草時の `origin/develop`（`842b970f`）の最大は IADR-0521 である。0522（PR #1860・Valkey）と 0523（#1846 の PR）が未マージで番号を
> 予約しているため、本 IADR は 0524 を採った。両 PR のマージまで `check-adr-numbering.js` の `missing-number` が赤になり得る。

## 起点・関連

- 起点 issue: **#1859**（#1844 / PR #1854 の監査が置いた暫定の統制〔ループバックの port 必須〕を外す条件）。利用者裁定 2026-10-09「着手してよい」。
- 計画: ADR-0134 決定 1・フォローアップ 3（有人のリダイレクト URI と Keycloak の照合）、ADR-0107 決定 2〜5（インフラ製品の基準・digest 固定）。
- 前提: [IADR-0516](./IADR-0516_sc12-keycloak-service-account-provisioning.md)（#1844 追記・PR #1854 追記・本件の #1859 追記）、[IADR-0243](./IADR-0243_keycloak-edge-issuer-migration.md)（issuer の単一情報源）、
  [IADR-0369](./IADR-0369_persist-by-default-and-realm-reconcile-job.md)（永続化が既定・realm の後追い）、[IADR-0518](./IADR-0518_realm-import-secret-with-env-client-secrets.md)（取り込み元の Secret）、
  [IADR-0514](./IADR-0514_infra-image-digest-pinning-and-checker.md)（digest 固定）。作業仕様書 [20261009_1859](../specs/20261009_1859_keycloak-26-upgrade.md)（実測の表 1〜14 が本 IADR の根拠）。

## コンテキストと課題

配備の Keycloak 24.0（24.0.5）は CVE-2024-8883 を持つ（port なしで登録したループバックのリダイレクト URI に、利用者情報で宛先を外へすり替える形が一致する）。
PR #1854 はこれを入口の規則（ループバックは port の明示を必須）で塞いだが、RFC 8252 §7.3 の「任意の port で戻す」使い方を失った。
版を上げる際、26 は 24 の前提を黙って変える点が多い。**起動しない**変化（取り込みのファイル名・H2 の資格）は気付けるが、**起動して違う振る舞いになる**変化
（issuer が揺れる・アクセストークンから `sub` が落ちる・クッキーの属性・ヘルスの 404）は、統合スタックで初めて赤になるか、赤にもならない。

## 決定

### 決定 1 — 版は 26.7.4（index の digest で固定）

26 系で、取得の日に公開から 2 週間を超えた最新のタグ（26.7.4・2026-09-16。26.7.5 は 2026-09-30 で満たない）。`deploy/local/infra/keycloak.yaml` と
`deploy/docker-compose.yml` の 2 参照を同じ `quay.io/keycloak/keycloak:26.7.4@sha256:82a77884…`（multi-arch の index）に揃える。

### 決定 2 — 24 と同じ振る舞いを保つ設定（手元の docker で 24.0.5 と 26.7.4 を並べて実測）

| 変化（26） | 合わせ方 |
| --- | --- |
| hostname v1 の `KC_HOSTNAME_URL` を警告だけ出して無視し、issuer が要求の host になる | v2 の `KC_HOSTNAME` に完全な URL。検査器（`check-stack-ready` の G4・G6、`check-password-reset-mail`）も `KC_HOSTNAME` を読む。起動器の試験が `KC_HOSTNAME_URL` の再混入を止める |
| v2 はバックチャネルの URL も hostname で描く（in-cluster の discovery の `jwks_uri` がエッジになる） | `KC_HOSTNAME_BACKCHANNEL_DYNAMIC=true`（24 の v1 の既定と同じ。.NET の `Auth:MetadataAddress` の前提＝IADR-0243 決定 2 を保つ） |
| ヘルスが管理用のポート 9000 に移る（8080 は 404） | readinessProbe・compose の healthcheck を 9000 へ。ポートは名前つきで宣言し、Service にもホストにも出さない |
| 取り込み（`--import-realm`）はファイル名が `<realm>-realm.json` でないと起動しない | 取り込み元の Secret のキーと compose のマウント先を `platform-realm.json` へ。宣言のファイル名と ConfigMap `keycloak-realms` は変えない（読み手は Keycloak ではない）。起動器の試験が「全キー＝中身の realm 名」を固定する |
| dev-file（H2）の資格を `sa` / `password` に差し替えなくなり、24 が作った PVC を開けない | k8s に `KC_DB_USERNAME=sa`・`KC_DB_PASSWORD=password` を明示（H2 のファイルの外へ出ない値）。compose は Postgres なので対象外 |
| 宣言から取り込んだ realm で、利用者のアクセストークンから `sub` が落ちる（25 以降は `basic` スコープの写像。宣言がスコープを明示すると組み込みは作られない） | realm の宣言に `basic`（`oidc-sub-mapper`・`auth_time`）を足し、人の流れ（認可コード）を開く 6 クライアント（`wiki-js`・`bff`・`headlamp`・`grafana`・`argocd`・`vault`）の既定スコープの先頭へ（サービスアカウントだけのクライアントのトークンは `basic` なしでも `sub` を持つので足さない）。有人の MCP クライアントのテンプレートも `["basic","profile"]` にし、読み戻しで確かめる |
| エッジ（TLS 終端）の後ろで、クッキーが `SameSite=Lax`・`Secure` なしになる（要求が http） | `KC_PROXY_HEADERS=xforwarded`。issuer と認可の URL は hostname で固定なので、転送ヘッダはそれらを変えない（実測） |

変えないもの: サービスアカウントのトークンの `client_id`・`clientHost`・`clientAddress`（26 の `service_account` スコープへ移り、宣言から作ると載らない）。読み手は `azp` を先に読み、AST のコードは読まない。
`KEYCLOAK_ADMIN*`（26 で非推奨だが有効。改名は Pod の env を読む検査器と手順書に波及する）。

### 決定 3 — 24 → 26 の更新は一方向。退避を手順の先頭に置く

起動時に Liquibase がスキーマを移し、file H2 の形式も変わる。24 が作った PVC は決定 2 の資格で開け、realm・実行時の利用者と属性は残る（実測）。
移行は全クライアントへ `basic` を足し必須アクションの優先度を変えるが、realm の後追いが宣言との差（22 件。サービスアカウントだけの 19 クライアントの `basic` を外すのを含む）を当てて 0 に収束する（G9 は緑）。外した後もサービスアカウントのトークンは `sub` を持つ（実測）。
戻せるのは退避からだけなので、運用仕様書「Keycloak の版の更新」に退避（経路B は PVC の tar、compose は `pg_dump`）を先頭の段として書いた。
経路B の日次バックアップは Keycloak の PVC を含まない。

### 決定 4 — 門 M9 に「port なしのループバック」の対を足す。port 必須は外さない

SC-12 は port なしを 400 で拒むので、入口を通しては作れない。門は master の管理者で port なしのループバック（`127.0.0.1`・`[::1]`）の公開クライアントを
Keycloak に直接作り、**陽性対照**（登録どおり・任意の port でログイン画面へ進む。否定が空振りしていないこと）と、**横取りの形 4 つ × 2**（`:<任意>@`・`:1@`・`:@`・
`:<任意>:1@` で宛先を `evil.example` へ）がすべて 400 であることを測る。形は純関数 `portlessLoopbackHijackProbes` が作り、WHATWG の URL として宛先が
`evil.example` になることを自己試験で確かめる。既存の否定（SC-12 の 400・Keycloak に何も作らない・port つきの横取りが 400）は残す。

手元で同じ対を当てた結果: **26.7.4 は 15 段すべて期待どおり**、**24.0.5 は 5 段が赤**（横取りの 4 形がログイン画面へ進み、`[::1]` の任意の port を拒む）。
門は版を戻すと赤になる。

規則（port 必須）は本 PR では外さない。外すかは製品の判断であり（失う利便を戻すかと、防御を 2 段から 1 段へ減らすかの釣り合い）、
推奨は IADR-0516 の #1859 追記に置く。

## 統制と現在の実現手段

| 統制 | 現在の実現手段 | 配備までの暫定手段 |
| --- | --- | --- |
| ループバックの横取りの形を一致させない（CVE-2024-8883） | 認証基盤の版（26.7.4）＋ 入口の port 必須（PR #1854。外すまで残る） | — |
| 26 の前提の変化を黙って受けない | 起動器の試験（`KC_HOSTNAME_URL` の不在・バックチャネル・転送ヘッダ・H2 の資格・readiness 9000・取り込みのキー）。門 M9 の `sub`・`basic` | 統合スタックの初回の実行（オーケストレーターが dispatch）が稼働での初回の確かめになる |
| 更新を戻せるようにする | 運用仕様書の手順（退避が先頭） | 退避は手作業（日次バックアップは Keycloak の PVC を含まない） |

## 結果

- 良い: CVE-2024-8883 が版で閉じる。26 の前提の変化 7 つを、どれも 24 と同じ振る舞いへ合わせ、そのうち起動器・門で機械的に止められるものは止めた。
- 悪い: realm の宣言と MCP のテンプレートに 25 以降にしか無い `basic` を書いたので、24 へ戻すときは宣言も戻す必要がある（決定 3 の退避と同じ戻し方）。
  26 の新しい既定（`service_account` スコープ・初期管理者の新しい環境変数名）は採っていない。

## 残余

1. **稼働での初回の確かめ**（M1〜M10・G4・G9・ログイン）は、本 PR のマージ後の integration-stack の実行である。手元の実測は docker の単体の Keycloak であり、
   エッジ・メッシュ・床の器を通していない（転送ヘッダの効き方は手元で模した）。
2. **ループバックの port 必須を外すか**は製品の判断（IADR-0516 の #1859 追記）。外す PR では入口・画面の規則・試験・`docs/api/openapi.yaml` の説明（生成物 `bff.schemas.ts` も）を同時に改める。
3. **管理用の 2 クライアントの権限を細粒度の管理権限（v2）で絞る**のは未着手（26.2 以降で可能になった。セキュリティ仕様書の残る穴）。
4. `KEYCLOAK_ADMIN*` → `KC_BOOTSTRAP_ADMIN_*` の改名（`check-password-reset-mail.js` が Pod の env を読む・手順書 2 本）は未着手。
5. アカウントコンソールの `platform` テーマは、親 `keycloak` の account テーマが無く組み込みへ落ちる（24 でも同じ。既存の不具合）。
6. **AST の realm**（同じ Keycloak へ取り込む）は、宣言に `clientScopes` を持たない（submodule の pin の `realm-export.json` で確認）。その場合は Keycloak が
   組み込みのスコープ（`basic` を含む）を作るので、**利用者のアクセストークンの `sub` は落ちないと推定する**（推定。26.7.4 で AST の realm を取り込んで測ってはいない）。
   ［PR #1869 監査で訂正］初版は「宣言から作ると `sub` が落ちる」と書いていたが、それは本リポジトリの realm のように `clientScopes` を明示した場合の話である。
   なお AST のコードは `sub` を読まない（`git grep`）。
7. **計画 ADR-0086 フォローアップ 4**（Keycloak の版更新時に token exchange の着手可否の 2 条件を確かめる）は計画側の作業で、本件がその契機に当たる。計画への環流が要る。
8. **戻し方**（PR #1869 監査）: イメージの版だけを戻すと、realm の宣言の `basic`（25 以降の写像）を realm の後追いが 24 へ当てようとして G9 が収束しない。
   戻すのは「本件の変更の丸ごと」と「退避からの DB」の両方である（運用仕様書「Keycloak の版の更新」の「戻すとき」）。**起動器は file H2 の版を見ない** ——
   新しい版のマニフェストで `k8s-local-up.sh` を走らせるだけで一方向の移行が起きる。検知の仕掛けは置いていない（退避を手順の先頭に置くだけ）。
9. **転送ヘッダの信頼の範囲**（PR #1869 監査）: `KC_PROXY_HEADERS=xforwarded` は送り手を絞っていない。`platform-infra` には NetworkPolicy が無いので、
   クラスタ内の任意の Pod が `keycloak:8080` へ偽の `X-Forwarded-*` を送れる。issuer と認可の URL は `KC_HOSTNAME` で固定なので変わらないが、
   管理イベント・ログの送信元 IP は偽れ、バックチャネルの URL は偽った本人への応答だけが変わる。是正の候補は `KC_PROXY_TRUSTED_ADDRESSES`
   （エッジの Pod の範囲。k3d の Pod CIDR とエッジの所在が経路で変わり、ここでは確かめられないので本件では入れない）か、Keycloak への到達をエッジと
   既知の呼び出し元に絞る NetworkPolicy。
