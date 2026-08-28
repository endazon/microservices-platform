---
title: 作業仕様書 — SC-12 MCP クライアント登録管理（BFF・画面・配備・仕様書）を通す（#452）
type: spec
status: done
related_ids:
  - FR-16
  - UC-09
  - SC-12
  - ADR-0021
  - ADR-0024
  - ADR-0034
  - ADR-0054
author: claude
created: 2026-08-28
updated: 2026-08-29
plan_refs:
  - "05_screens §SC-12: 登録クライアント一覧（有人／サービスアカウント種別・状態）、クライアント登録・無効化、無人アカウントの ABAC 属性割当（機密区分上限・アクセス可能タグ）、公開ツール一覧（実効構成の参照）、呼び出し監査ログへの導線"
  - "05_screens §SC-12 アクション: 公開ツールの変更は本画面から直接行わず、Git 経由の公開構成変更へ誘導する（許可リスト方式・GitOps）"
  - "05_screens §SC-12 入力/バリデーション: クライアント種別＝有人（Authorization Code+PKCE）／無人（Client Credentials）。無人時の ABAC 属性は必須で、定義済み機密区分・タグのみ・有人アカウントの上限を超えない"
  - "05_screens §共通シェル: アクセス制御の割当 —— SC-09・SC-12・SC-17 = システム管理者。ルートパスは /admin/mcp-clients。左ナビ「管理」グループの「MCP管理」"
  - "ADR-0024 §5: 実効ツール一覧と公開構成のドリフトを管理面へ出す。公開範囲の変更は許可リストの構成変更で行う"
related_adrs:
  - IADR-0297
issue: "#452"
---

# 作業仕様書: SC-12 MCP クライアント登録管理（#452）

## 起点

後段の管理 API は #445 で着地している（`McpClientEndpoints` の 6 端点・永続化・テスト 8 件）。
**欠けているのは「後段から先」である** —— BFF の口が無く、契約（`openapi.yaml`）に `mcp` の語が
1 件も無く、画面が無く、`McpServer` が配備 manifest のどこにも載っていない。
加えて `.ai-context/specs/20260828_issue-1020_internal-mcp-tools.md` が
「SC-12 のテスト仕様書は、クライアント登録管理を扱う issue の射程である」と明示的に申し送っている。

## 着手前の実測（親からの申し送りを鵜呑みにせず自分で引いた）

| 主張 | 実測コマンド | 結果 |
| --- | --- | --- |
| `openapi.yaml` に `mcp` が無い | `grep -ci mcp docs/api/openapi.yaml` | **0**（主張どおり） |
| BFF 合成点に MCP が無い | `BffEndpointComposition.Modules` を目視・件数 | **16 モジュール中 0 件**（主張どおり） |
| BFF に `McpServer` の named client が無い | `grep AddHttpClient Platform.Bff/Program.cs` | **12 件中 0 件**（主張どおり） |
| 配備に MCP が無い | `grep -ri mcp deploy/` | **0 件**（主張どおり）。**Dockerfile も無い**（親の申し送りに無い追加の欠落） |
| `docs/tests/SC-12_*.md` / `docs/screens/SC-12_*.md` が無い | `ls docs/tests docs/screens` | **どちらも無い**（主張どおり） |
| `router.test.ts` の `PLANNED_ROUTES` に SC-12 が無い | 目視 | **無い**（`check-route-manifest.js` が feature 作成と同時に落とす） |

**親の申し送りに無かった欠落を 1 件見つけた**: `McpServer` には Dockerfile が無い
（`find src -name Dockerfile` の 15 件に含まれない）。compose の `build:` も
`k8s-local-images.sh` の `MAPPING` も Dockerfile パスを要求するため、**先に Dockerfile を作らないと
配備の配線自体が書けない**。

## 母集合の引き方・結果・除外理由（規則 9・10）

### 引き方

**記憶で挙げず、誤りの側の文字列で全追跡ファイルを走査した。**

```
git grep -il "mcp" -- ':!src/ai-stock-trading'          # 139 件
grep -ri mcp deploy/                                     # 0 件
grep -rn "AddHttpClient" src/platform/backend/Bff/Platform.Bff/Program.cs
grep -n "^  [a-z0-9-]*:" deploy/docker-compose.yml deploy/helm/microservices-platform/values.yaml
```

導出値（件数・一覧）は走査ではなく**計算し直した**（合成点のモジュール数、compose のサービス数）。

### 触る母集合（結果）

| 区分 | ファイル | 触る理由 |
| --- | --- | --- |
| BFF | `Platform.Bff/Foundation/Endpoints/McpClientBffEndpoints.cs`（新規） | 画面から後段へ到達する唯一の経路 |
| BFF | `Platform.Bff/Program.cs` | `McpServer` の named client 登録 |
| BFF | `Platform.Bff/Composition/BffEndpointComposition.cs` | 合成点は 1 本しかない |
| BFF テスト | `Platform.Bff.Tests/BffTestFactory.cs` / `BffMcpClientEndpointTests.cs`（新規） / `BffEndpointCompositionTests.cs` | スタブ登録と、合成点の件数・群一覧の固定 |
| 契約 | `docs/api/openapi.yaml` / `docs/api/BFF_bff-surface.md` / `docs/api/FR-16_mcp-server.md` | `x-roles` は `check-bff-authz-docs.js` の一次情報 |
| 生成物 | `platform/frontend/src/lib/api/generated/**` | orval。CI が再生成差分ゼロを検査する |
| 画面 | `knowledge/frontend/src/features/sc12-mcp-clients/**`（新規）・`features/index.ts` | 合成点はタプルとナビ配列の 2 経路 |
| 画面 | `platform/frontend/src/app/routing/router.test.ts` | `PLANNED_ROUTES`（`check-route-manifest.js` の一次情報） |
| 画面 | `src/eslint.config.js` | lingui 規則の `files:`（画面を作り直すたびに伸ばす運用） |
| i18n | `platform/frontend/src/locales/{ja,en}/messages.{po,ts}` | 再生成差分ゼロと未翻訳ゼロ |
| 配備 | `src/platform/backend/Services/McpServer/Dockerfile`（新規）・`deploy/docker-compose.yml`・`deploy/helm/.../values.yaml`・`deploy/local/infra/postgres.yaml`・`deploy/create-multiple-dbs.sh`・`scripts/k8s-local-images.sh` | イメージ・Service・DB の 3 つが揃わないと起動しない |
| 文書 | `docs/screens/SC-12_*.md`・`docs/tests/SC-12_*.md`（新規）・`scripts/test-spec-coverage-baseline.json` | 必須仕様書 ＋ 床の更新 |
| 記録 | `.ai-context/adr/IADR-0297_*.md`・`.ai-context/adr/README.md` | 実装判断の記録と索引 |

### 除外したもの（理由つき）

| 除外 | 理由 |
| --- | --- |
| `src/ai-stock-trading/**` | 別プロジェクトの submodule。走査からも外した |
| `docs/security/security.md` / `docs/functional/FR-01_*.md` | **並行トラックの領域**（親の宣言）。触らない |
| `knowledge/backend/Services/{Document,DataSource,Conversion}Service/**` | 同上 |
| `Platform.Shared.Infrastructure/Foundation/Ports/Storage/**` | 同上 |
| Ingress（`deploy/helm/.../templates/edge.yaml`）への `/mcp` 公開 | **外部 AI エージェントの入口の話であり SC-12（管理画面）ではない。** ADR-0021 の射程で、公開すると認証・レート制御・越境の統制を同時に決める必要がある。**繰り延べであって放棄ではない**（§やり残しに記録した） |
| Keycloak のクライアント登録（realm 側） | 計画 §SC-12 の「登録→Keycloak クライアント作成」の後半。後段 API が扱うのは**プラットフォーム側の登録簿**だけで、realm への反映は別の決定（`docs/tests/UC-09_*.md` §未実施 が同じ線を引いている） |
| `.ai-context/` の凍結記録への追記 | 本文プローズを後から書き換えない |

## 決めたこと（詳細は IADR-0297）

1. **BFF の口は `/bff/admin/mcp-clients`（`AdminOnly` の passthrough proxy）** —— `/bff/admin/authz` と同型。
   🔴 **状態コードをそのまま透過する。** 後段の 404 を 403 や 200 へ変換しない。
2. **後段の Service 名は `mcp-service`**（chart のキーは `mcp`）。helm のテンプレートが
   `{{ $name }}-service` を組むため、キーを `mcp-server` にすると `mcp-server-service` になる。
   **BFF のコード既定を `http://mcp-service:8080` にし、manifest 側の上書きを作らない**（後発サービスの規約）。
3. **画面に公開ツールの編集 UI を作らない。** 一覧＋「Git 経由で変更する」旨の固定文言だけを置く。
4. **属性割当の値域は後段の辞書（`/bff/admin/authz/attributes`）から引く。** 画面に値集合を焼き込まない。
5. **監査ログへの導線は実行時 config の Grafana URL を使い、未設定なら導線を出さず所在を文言で示す**
   （SC-10 の外部ツール導線と同じ作法）。

## 受け入れ基準

| # | 基準 | 固定する場所 |
| --- | --- | --- |
| 1 | 管理者は 6 端点すべてを BFF 経由で使える | `BffMcpClientEndpointTests`（陽性対照） |
| 2 | 運用者は 403・無認証は 401 | 同上 |
| 3 | 後段の 404 / 400 / 409 が**そのまま**返る（変換しない） | 同上 |
| 4 | 後段不達は 502 | 同上 |
| 5 | 資格情報が後段へ伝播する | 同上（スタブが `Authorization` を観測） |
| 6 | 画面は管理者以外に**存在しない**（`RequireRole` → NotFound） | `McpClientManagementPage.test.tsx` |
| 7 | 公開ツールの編集 UI が**無い**（陽性対照つき） | 同上 |
| 8 | 無人を選ぶと ABAC 属性が必須になり、有人では要求しない | 同上 |
| 9 | ルート `/admin/mcp-clients` が木に載り、ナビ項目が解決する | `router.test.ts` |
| 10 | 契約の `x-roles` が BFF の実効ロールと一致する | `check-bff-authz-docs.js` |

## 検証（実走して出力を報告に載せる）

`dotnet build` / `dotnet test` / `dotnet format --verify-no-changes` / `pnpm typecheck|lint|format:check|test` /
`pnpm codegen` の差分ゼロ / 各 `scripts/check-*.js` / `scripts.test.js`。

## 規則 10 の引き直し（この変更で新たに誤りになる自分の記述）

**是正のたびに引き直した結果、1 件見つかった。**

- `scripts/scripts.repo.test.js` の変異試験が、**「テスト仕様書を持たない ID」の代表として `SC-12` を
  名指ししていた**（`assert.match(out, /SC-12/)`）。本作業が `docs/tests/SC-12_*.md` を新設したことで
  SC-12 は母集合から外れ、**この検査自身が落ちる**。同じ場所には「従前は SC-20 を見ていたが
  新設で無主でなくなった。固定に使う ID はテスト仕様書を持たない ID へ追随させる」という
  申し送りが既にあったので、それに従い `UC-01` へ移した（SC-17 は fixture の issue が
  「SC-13〜17」で引き受けているため無主にならない）。
- **是正前の語（`mcp` / `SC-12`）で引いても捕まらない類ではなかった**が、
  「テスト仕様書を新設した」という**変更の側**から引き直して見つけた。

## 変異試験（検出力の実測。結果はテスト仕様書 §変異試験 が正本）

境界層 6 変異・画面 5 変異・配線 2 変異を投入し、**すべて戻して差分比較で残渣 0 を確認した**。
1 件だけ初版で検出できなかった（経路のエスケープを空白文字で測っていた）ため、
**測り方の側を直した**（経路の構造を変える文字へ）。

## やり残し（§報告にも書く）

1. **Ingress の `/mcp` 公開は行っていない**（上記の除外理由）。
2. **Keycloak クライアント作成は行っていない**（同上）。
3. **`check-adr-numbering.js` は本ワークツリー単体では欠番（0295 / 0296）で落ちる。**
   親の指示により 0297 を使っており、並行トラックの統合で解消する。
4. **Docker / k3s がこの環境に無いため、compose / helm の実起動は検証していない。**
   `helm template` も実行していない（`helm` バイナリが無い）。
