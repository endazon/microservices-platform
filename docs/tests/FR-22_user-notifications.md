---
title: FR-22 利用者本人への通知 テスト仕様書
type: test-spec
status: in-progress
related_ids:
  - FR-22
  - FR-19
  - FR-20
  - UC-11
  - ADR-0037
  - ADR-0045
  - IADR-0119
  - IADR-0125
  - IADR-0132
  - IADR-0135
  - IADR-0142
  - IADR-0215
author: Claude
created: 2026-08-16
updated: 2026-08-16
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0037_obsidian-sync-method.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0045_mail-delivery-smtp-relay.md"
related_specs:
  - ../adr/IADR-0215_notification-service-and-in-app-delivery.md
  - ../functional/FR-22_user-notifications.md
  - ../specs/20260816_issue-600_fr22-in-app-notifications.md
  - ../api/BFF_notifications.md
---

# テスト仕様書: FR-22 利用者本人への通知

> **`status: in-progress` の理由**: **受け入れ基準 5 つのうち 3 つ（本人以外へ届かない／メールが
> 送れなくてもアプリ内通知が届く／送信上限超過が静かに落ちない）は backend の振る舞いであり、
> 本 PR では実装もテストもしていない。** 線引きの正本は [[IADR-0215]] 決定 6。**追跡は #600。**

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-22**
- ユースケース（UC）: **UC-11** 例外フロー
- 受け入れ基準の所在（02_requirements）:
  [02_requirements/01_requirements.md](../../planning/projects/microservices-platform/02_requirements/01_requirements.md) FR-22 行
  および [ADR-0037](../../planning/projects/microservices-platform/07_adr/ADR-0037_obsidian-sync-method.md) 決定 6・17・18 ／
  [ADR-0045](../../planning/projects/microservices-platform/07_adr/ADR-0045_mail-delivery-smtp-relay.md) 決定 3・8
- 計画書リンク: 上記

## 受け入れ基準 5 つの写像（**この節が本書の主眼である**）

計画・issue #600 が挙げる受け入れ基準は 5 つある。**うち 4 つ（AC-2〜AC-5）は backend の振る舞いである。**

| AC | 受け入れ基準 | 本 PR での状態 | テスト |
| ---: | --- | --- | --- |
| **AC-1** | 実装 ADR に 5 点（送出主体・アプリ内通知の実体・メール経路・送信レート・発火の検知）が決定として書かれている | **満たした** | 文書レビュー（[[IADR-0215]] 決定 1〜5）。機械検査は無い |
| **AC-2** | **本文が件数と期限のみで構成される**（タイトル・本文・検索語・回答内容が混入しない） | **契約レベルで満たした／振る舞いは未実装** | **T-01（契約テスト）** ＋ **T-02（文言テスト）**。**後段が実際に何を詰めるかは backend の振る舞いであり未実装・#600 で追跡** |
| **AC-3** | **所有者本人以外へ届かない** | **未実装・#600 で追跡** | 宛先解決（JWT の `sub` で絞る）は BFF ＋ `NotificationService` の実装である。**`dotnet` 不在で `[Fact]` を実走できない** |
| **AC-4** | **メールが送れなくてもアプリ内通知が届く**（従属しない） | **未実装・#600 で追跡** | outbox が別トランザクションであることの検証は送出側の `[Fact]` である |
| **AC-5** | **送信上限を超える通知が静かに落ちない**（監査ログ・SC-10 で観測できる） | **未実装・#600 で追跡** | レート制御と `sent`/`deferred`/`dropped`/`failed` の記録は送出側の `[Theory]` である |

> **AC-3〜AC-5 に「代わりのフロントテスト」を置いていない。** 置くと**満たしていない基準を満たしたように
> 見せる**ことになる。**フロントで確かめられるのは「契約にその形が無い」ことまでである。**

## テスト対象・範囲

- **対象**: BFF 契約（`docs/api/openapi.yaml` の `/bff/notifications*` と `NotificationDto` ほか）、
  アプリ内通知の受け皿（`src/platform/frontend/src/foundation/notifications/`）、
  表示文言の組み立て（ja / en）。
- **対象外**: `NotificationService`・BFF 端点の実装・メール outbox・送信レート制御・発火の結線
  （[[IADR-0215]] 決定 6。**#600 / #451 で追跡**）。

## テスト観点

1. **契約の不変条件**（AC-2 の骨格）: **スキーマにタイトル／本文に相当する項目が無い**。
2. **文言の組み立て**: 件数・閾値・期限だけから文言ができ、**ja / en の両方が揃っている**。
3. **描画**: 未読件数・一覧・既読化・空状態・取得失敗の縮退。
4. **アクセシビリティ / 表示規約**: **色だけで意味を持たせない**（色 ＋ アイコン ＋ テキスト）。
5. **通信**: orval 生成フック経由であること（**手書き HTTP クライアントを使わない**。ESLint も止める）。

## テストケース一覧

| ID | 前提条件 | 手順 | 期待結果 | 対応受け入れ基準 | 区分（自動/手動） |
| --- | --- | --- | --- | --- | --- |
| T-01 | `docs/api/openapi.yaml` が読める | `NotificationDto` のプロパティ名集合を取る | **`title` / `body` / `message` / `subject` / `text` / `summary` / `detail` / `content` に相当する項目が 1 つも無い**。かつ許可した 7 項目以外が増えていない | **AC-2（契約）** | 自動（Vitest） |
| T-02 | — | 5 つの `kind` それぞれについて文言を組み立てる | **件数・閾値・期限だけが現れる**。ja / en の双方で文字列が非空 | **AC-2（文言）** | 自動（Vitest） |
| T-03 | 通知 3 件（うち未読 2 件）を返すスタブ | ベルを開く | 未読件数 **2** が出る。3 件が一覧に並ぶ | — | 自動（Vitest + Testing Library） |
| T-04 | 同上 | 各行を見る | **色だけで意味を持たせていない**（アイコンとテキストのラベルが常に付く） | — | 自動（Vitest + Testing Library） |
| T-05 | 未読 1 件 | 「既読にする」を押す | 既読化が呼ばれ、未読件数が **0** になる | — | 自動（Vitest + Testing Library） |
| T-06 | 通知 0 件 | ベルを開く | **「通知はありません」が出る**（無言で何も描かない形にしない） | — | 自動（Vitest + Testing Library） |
| T-07 | 取得が失敗する | ベルを開く | **共通シェルが壊れない**。取得失敗が色 ＋ アイコン ＋ テキストで出る | — | 自動（Vitest + Testing Library） |
| T-08 | 通知に `count` も `deadline` も無い（②の形） | 文言を組み立てる | **閾値だけで文言が成立する**（`undefined` が文字列へ漏れない） | **AC-2（文言）** | 自動（Vitest） |
| **T-09** | — | **本人以外の通知が返らないこと** | **未実装・#600 で追跡**（backend） | **AC-3** | — |
| **T-10** | — | **メール送信が失敗してもアプリ内通知が残ること** | **未実装・#600 で追跡**（backend） | **AC-4** | — |
| **T-11** | — | **日次上限を超えた送信が `deferred` / `dropped` として記録されること** | **未実装・#600 で追跡**（backend） | **AC-5** | — |

## テストデータ

- 通知 3 件（`private-note-purge-weekly` 未読 / `storage-quota-warning` 未読 / `sync-token-expiry` 既読）。
- **タイトル・本文に相当する値をテストデータに置かない**——置けないことを T-01 が固定しているためであり、
  置くとテストデータのほうが契約より広くなる。
- 通信は **orval 生成コードの経路**（`bffFetch` → `apiRequest`）へスタブを当てる（[[IADR-0135]] 決定 4）。
  MSW のハンドラは orval が生成する（`mock: true`）ので、**手書きのハンドラを増やさない**。

## 関連仕様

- 機能仕様書: [FR-22](../functional/FR-22_user-notifications.md)
- 画面仕様書: **なし**（共通シェル横断。計画に SC 番号が無い）
- 通信仕様書: [BFF 通知](../api/BFF_notifications.md)
- 実装 ADR: [[IADR-0215]]

## 未決事項

1. **AC-3〜AC-5 の `[Fact]` / `[Theory]` を書く時期**は、`dotnet` が実走できる環境が用意できたときである（#600）。
2. **E2E（Playwright）は置いていない。** 発火源が結線されるまで、通知が実際に出る筋道が無いためである（#451）。
