---
title: エッジ経由 OIDC 認証導線の実機検証 — 認可コード＋PKCE を通し切る検証スクリプトの新設
type: spec
status: done
related_ids: [NFR, FR-05, SC-01, SC-02, SC-03, ADR-0021, ADR-0032, IADR-0076, IADR-0086, IADR-0091]
author: Claude
created: 2026-08-05
updated: 2026-08-05
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0032_spa-auth-bff-session.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0021_edge-istio-gateway-caddy.md"
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
related_specs:
  - ../adr/IADR-0091_local-edge-aggregation-traefik.md
  - ../adr/IADR-0086_oidc-issuer-metadata-split.md
  - ../../deploy/local/edge/README.md
---

# 仕様書: エッジ経由 OIDC 認証導線の実機検証（issue #466 の前提）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（NFR: 検証可能性・セキュリティ。認証は FR-05）
- 画面（SC）: SC-01〜03（認証後に到達する画面。導線の出口）
- 関連 ADR（計画）:
  [ADR-0032](../../planning/projects/microservices-platform/07_adr/ADR-0032_spa-auth-bff-session.md)（SPA 認証。BFF セッション方式への移行は #439）／
  [ADR-0021](../../planning/projects/microservices-platform/07_adr/ADR-0021_edge-istio-gateway-caddy.md)（エッジ・実行基盤）
- 関連 IADR（実装）:
  [IADR-0091](../adr/IADR-0091_local-edge-aggregation-traefik.md)（経路B のエッジは Traefik。Istio ではない）／
  [IADR-0086](../adr/IADR-0086_oidc-issuer-metadata-split.md)（issuer と metadata 取得先の分離）／
  IADR-0076（issuer は最小案 `http://keycloak:8080` ＋ 手順A を維持）
- 実装 issue: [#466](https://github.com/endazon/microservices-platform/issues/466)（E2E スモークを統合スタックで CI 実行可能にする）

## 目的・背景

現行の E2E は**バックエンド不要のスモークのみ**で、`/login` への誘導など「認証前」の導線しか見ていない。
**Keycloak を通した認証後の導線は一度も検証されていない**（#466）。

本作業は、その空白を埋める最初の一歩として、**認可コード ＋ PKCE を最後まで通し切る検証スクリプト**を
リポジトリに残す。ブラウザ操作を伴わない（curl のみ）ため、**#466 が目指す CI 実行の土台**にもなる。
既存の [`verify-qdrant-attribute-payload.sh`](../../scripts/verify-qdrant-attribute-payload.sh)（実機 Qdrant の
挙動確認）と同じ「実機検証スクリプト」の系列に置く。

## 対象範囲

- 対象:
  - `scripts/verify-oidc-edge-flow.sh` の新設（読み取り専用・curl と openssl のみ）
  - 検証する導線: SPA 配信 → 認可エンドポイント → ログイン → callback（認可コード）→
    トークン交換（PKCE 検証）→ クレーム確認 → **エッジ経由**での BFF 呼び出し
  - 実機で実行し、結果を本仕様書へ記録する
- 対象外:
  - **E2E の CI 結線そのもの**（#466 本体。本作業はその前提となる検証手段の用意）
  - ブラウザ自動操作（Playwright）による画面遷移の検証（#466 で扱う）
  - Keycloak をエッジへ出す設計変更（現行は IADR-0076 の手順A を維持する決定であり、変更は別 issue）
  - ABAC ポリシー未投入で応答が空になる件（#517）・必須属性の欠落（#516）の解消

## 設計

### 検証する経路と判定

| # | 手順 | 期待 |
| --- | --- | --- |
| 1 | エッジ（`http://localhost/`）から SPA を取得 | 200 かつ SPA の HTML |
| 2 | `config.js` の `oidc.authority` を読む | 実行時 config が注入されている |
| 3 | 認可エンドポイントへ GET | Keycloak のログインフォームが返る |
| 4 | 資格情報を POST | `redirect_uri` へ 302 し **認可コード**が付く |
| 5 | トークン交換（`code_verifier`） | `access_token` が得られる（PKCE 検証が通る） |
| 6 | トークンのクレームを確認 | `iss` ／ `preferred_username` ／ **`clearance` / `department`**（ABAC 入力）／ ロール |
| 7 | **エッジ経由**で BFF を叩く | 200（応答が空でも到達性は満たす。空の理由は #517） |
| 8 | 無トークンで読み取り系を叩く | 200（**設計どおり**。読み取りは匿名許容。§計画書との差異 参照） |
| 9 | 無トークンで書き込み系を叩く | **401**（認証必須） |

### 再現性

- `code_verifier` は**固定値**（乱数を使わない）。同じ入力で同じ結果になる。
- 読み取り専用。書き込み系は**認証拒否の確認のみ**で、成功する呼び出しを行わない。
- 前提（hosts・port-forward・エッジ）が欠けている場合は、**何が足りないかを名指しして終了する**
  （「失敗」と「前提未整備」を区別する）。

### 前提（IADR-0076 手順A）

経路B は issuer を `http://keycloak:8080` に固定しており、**Keycloak をエッジへ出していない**
（[deploy/local/edge/README.md](../../deploy/local/edge/README.md) の決定）。したがってブラウザ／CLI からの
認証には次が要る。

```sh
# hosts: 127.0.0.1 keycloak
kubectl -n platform-infra port-forward svc/keycloak 8080:8080
```

**この前提こそが #466（CI 実行）の障害**である。CI では port-forward と hosts を用意できないため、
スクリプトは前提の有無を明示的に報告する（#466 側の判断材料にする）。

## 受け入れ基準

- [x] 認可コード ＋ PKCE の全 9 手順が実機で通り、結果が本仕様書に記録されている（§実測結果。PASS 14 / FAIL 0）
- [x] 検証スクリプトが再実行可能な形でリポジトリに残り、前提未整備を「失敗」と区別して報告する（終了コード 2 を実測）
- [x] トークンに ABAC 入力（`clearance` / `department`）が載っていることを確認している
- [x] エッジ（Traefik）経由で BFF へ到達できることを確認している
- [x] 書き込み系が無トークンで 401 になることを確認している
- [x] #466 が CI 実行を阻んでいる要因（issuer と手順A）が結果として言語化されている（§#466 にとっての含意）

## テスト方針

本作業の成果物は検証スクリプトそのものであり、**実機実行が試験**である。単体テストは設けない
（`verify-qdrant-attribute-payload.sh` と同じ扱い。集計ロジックを持たず、判定は実機応答に対する
その場の照合であるため）。

## 計画書との差異

- 差異: あり（内容・対応: 読み取り系 BFF は**匿名でも 200 を返す**。これは
  [DocumentBffEndpoints.cs](../../src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/DocumentBffEndpoints.cs) に
  「読み取りは SC-02/03 用に無制限」と明記された既存の設計であり、本作業では変更しない。
  計画 NFR「全 API OIDC/JWT」との差は **#458** が扱う。本作業は現状を**測って記録する**に留める）

## 未決事項

- CI で認証導線を回すには issuer の扱い（手順A の脱却 ＝ Keycloak をエッジへ出すか）の決定が要る。
  これは IADR-0076 の決定変更にあたるため、#466 の中で別途決める。

## 実測結果

**測定日**: 2026-08-05 ／ **対象**: 経路B ローカル k8s（Rancher Desktop 内蔵 k3s ＋ Traefik エッジ）／
**コマンド**: `bash scripts/verify-oidc-edge-flow.sh`

### 手順A あり（hosts ＋ port-forward）: **PASS 14 / FAIL 0**（終了コード 0）

| # | 検証 | 結果 |
| --- | --- | --- |
| 1 | エッジから SPA を取得 | PASS |
| 2 | `config.js` の `authority` | PASS（`http://keycloak:8080/realms/microservices-platform`。検証対象の issuer と一致） |
| 3 | 認可エンドポイント → ログイン画面 | PASS |
| 4 | 資格情報 POST → 認可コード | PASS（redirect 先 `http://localhost/callback`） |
| 5 | トークン交換（PKCE） | PASS |
| 6 | クレーム | PASS ×4（`iss` / `preferred_username=developer` / **`clearance=restricted`** / **`department=engineering`**） |
| 7 | エッジ経由の BFF（認証あり） | PASS ×3（`/bff/documents` `/bff/dashboard/summary` `/bff/datasources` すべて 200） |
| 8 | 無トークンの読み取り | PASS（200。**現行設計どおり**） |
| 9 | 無トークンの書き込み | PASS（**401**） |

**認証導線そのものは最後まで通る。** SPA 配信・認可コード・PKCE 検証・ABAC 入力クレーム・
エッジ経由の BFF 到達まで、どこも壊れていない。

### 手順A なし（port-forward を止めた状態）: **終了コード 2（SKIP）**

前提未整備を「失敗」と区別し、何を用意すべきか（hosts ＋ port-forward）を名指しして終了することを確認した。

### #466 にとっての含意

- **導線は健全。塞いでいるのは前提のほう**である。issuer を `http://keycloak:8080` に固定する決定
  （IADR-0076・[deploy/local/edge/README.md](../../deploy/local/edge/README.md)）により **Keycloak がエッジに出ておらず**、
  認証には hosts 追記と port-forward が要る。**CI ではどちらも用意できない**——これが #466 の実体である。
  E2E を CI で回すには、issuer の扱いを決め直す（Keycloak をエッジへ出す）判断が要る。
- **認証を通しても画面は空**になる。BFF は 200 を返すが中身は空配列で、原因は ABAC ポリシー 0 件の
  deny-by-default（**#517**）。**E2E で「認証後に結果が出る」ことを検証したいなら #517 が先**である。
- 診断上の罠: エッジの catch-all（`platform-frontend-edge` はホスト `*`）により、
  **`http://keycloak.localhost/...` は 200（SPA の HTML）を返す**。到達しているように見えるが Keycloak ではない。
  疎通確認では**本文か content-type まで見る**必要がある（実際にこの 200 で誤診しかけた）。
