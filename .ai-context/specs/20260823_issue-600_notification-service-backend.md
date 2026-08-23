---
title: 作業仕様書 FR-22 NotificationService の backend 実装 — 本人限定配信・メール非従属・送出上限の観測（#600）
type: spec
status: in-progress
related_ids:
  - FR-22
  - FR-19
  - FR-20
  - UC-11
  - SC-10
  - ADR-0037
  - ADR-0045
  - ADR-0004
  - IADR-0009
  - IADR-0119
  - IADR-0130
  - IADR-0132
  - IADR-0141
  - IADR-0142
  - IADR-0197
  - IADR-0215
  - IADR-0267
author: Claude
created: 2026-08-23
updated: 2026-08-23
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
  - planning:projects/microservices-platform/06_technical/02_service-decomposition.md
  - planning:projects/microservices-platform/07_adr/ADR-0037_obsidian-sync-method.md
  - planning:projects/microservices-platform/07_adr/ADR-0045_mail-delivery-smtp-relay.md
related_specs:
  - ../adr/IADR-0215_notification-service-and-in-app-delivery.md
  - ../adr/IADR-0267_notification-service-backend-subject-scoping-and-send-rate.md
  - ./20260816_issue-600_fr22-in-app-notifications.md
  - ../../docs/functional/FR-22_user-notifications.md
  - ../../docs/tests/FR-22_user-notifications.md
  - ../../docs/api/BFF_notifications.md
  - ../../docs/data/notification.md
---

# 仕様書: FR-22 NotificationService の backend 実装（#600 第 2 段）

> **この作業でも issue #600 を閉じない。** 発火の結線（①②③）は #451 待ちであり、
> メール送出の実体（SMTP）は実環境待ちである。PR 本文は `Refs #600` とする。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-22**（利用者本人への通知。優先度 Should）。発火源は **FR-19** / **FR-20**
- ユースケース（UC）: **UC-11**（自分の資料を作成・管理し、公開範囲を自ら設定する）例外フロー
- 画面（SC）: **SC-10**（運用ダッシュボード。送出結果の観測面）。FR-22 固有の SC 番号は計画に無い
- 関連 ADR: **ADR-0037** 決定 6・17・18 ／ **ADR-0045** 決定 1・1-b・3・8 ／ **ADR-0004**（監査ログ・認証）
- 実装 ADR: **IADR-0215**（第 1 段で確定した 5 点）／ **IADR-0267**（本作業で起案。実装に閉じた 4 点）
- 計画書リンク:
  02_requirements/01_requirements.md（計画リポ） FR-22 ／
  07_adr/ADR-0045（計画リポ） 決定 3

## 母集合（是正・追随の対象をどう引いたか）

規則の正本は `.claude/rules/traceability.md` 規則 1〜8 と `.claude/rules/traceability.repo.md`
規則 9・10 である。**記憶で挙げず、誤りの側の文字列で追跡下の全ファイルを走査してから挙げた。**

- **走査範囲**: `git ls-files`（拡張子で絞らない）から `src/ai-stock-trading/`（別プロジェクトの
  submodule）と `src/node_modules/`（依存物）をパスで除外。行フィルタの二段掛けは使っていない。
- **走査時点**: ブランチ `claude/implementation-repo-all-issues-hilvbs` の作業ツリー（2026-08-23）。
  **リポジトリは shallow clone のため `git log` / `git blame` を出典に引いていない**（planning#410）。

| # | 検索語 | 意図 | ファイル数 | 追随が要るもの |
| ---: | --- | --- | ---: | --- |
| 1 | `NotificationService` | 「送出側は未実装」と述べている文書 | **7** | **4**（下表） |
| 2 | `FR-22` | 新設要求を引いている文書 | **50**（うち生成物 30・別紙 2） | 上と同じ 4 件 |
| 3 | `#600` | 本 issue を追跡している文書 | **14** | 上と同じ 4 件 |
| 4 | `IADR-0267` | 採番の重複（先着尊重） | **0** | —（空き番号であることの確認） |

> **採番の経緯**: 着手時の割り当ては `IADR-0264` だったが、並行作業の完了順により
> `0264` / `0265` / `0266` が先にコミットされた。**先着尊重**（先にマージした側が番号を確保する）に従い、
> 次の空き番号 `IADR-0267` へ改番した。ファイル名・本文の自称番号・本書の参照・コード内コメント・
> 追随した必須仕様書をすべて揃えてある（`check-adr-numbering.js` の重複違反は解消済み）。

**追随が要る 4 件と、その理由**

| 文書 | いま誤りになる記述 | 対応 |
| --- | --- | --- |
| `docs/tests/FR-22_user-notifications.md` | AC-3〜AC-5 が「未実装・#600 で追跡」。T-09〜T-11 が空欄 | 本作業で実装したので表と T-09〜T-11 を書き換える |
| `docs/functional/FR-22_user-notifications.md` | 「送出側（`NotificationService`・メール outbox・レート制御・発火の結線）は入っていない」／受け入れ基準 3 件が未チェック／「データ仕様書: 未作成」 | 送出側が入ったので線引きを引き直す（**発火の結線だけが残る**） |
| `docs/api/BFF_notifications.md` | 「後段（`NotificationService`）と BFF 端点の実装は入っていない」／シーケンス図の `未実装・#600` | 後段は入り、**BFF 端点だけが残る**ので分けて書き直す |
| `docs/api/openapi.yaml`（通知 2 本のコメント） | 「実装（BFF 端点・NotificationService）は未着手である」 | 同上。**契約の形は 1 バイトも変えない**（コメントのみ） |

**追随させないと決めたもの（除外理由つき）**

- `.ai-context/adr/IADR-0215_*.md` … **確定済みの凍結記録**。決定 6 の表は当時の PR の射程であり、
  後から書き換えると「そのとき何を先送りしたか」の記録が壊れる（`traceability.repo.md` §凍結の射程）。
  **本作業の線引きは IADR-0267 が別途持つ。**
- `.ai-context/specs/20260816_issue-600_*.md` … 同じく当時の作業の記録。**本書が後段である。**
- `.ai-context/adr/README.md` … 統括側の管理対象（並行作業の衝突面）。**本作業では触らない。**
- `CHANGELOG.md` … 自動生成物（`scripts/gen-changelog.js`）。手で書き足さない。
- `src/platform/frontend/**`（生成物 30 件を含む） … 契約は変えないため追随不要。
  **#785 が並行して触っている領域でもある。**
- `scripts/**` … 統括側の指示により本作業では触らない。**影響は §検査器への影響 に開示する。**
- `docs/how-to/*-annex.md` / `docs/screens/SC-15_*.md` / `docs/operations/keycloak-smtp-relay-setup-runbook.md`
  … `FR-22` / `#600` を**別の文脈で**引いており（ID 修飾の例示・SMTP 基盤の手順）、本作業で偽になる記述は無い。
  **走査で当たったが、本文を読んで除外した。**

## 目的・背景

計画 FR-22 の受け入れ基準 5 件のうち、**AC-3 / AC-4 / AC-5 の 3 件は送出側（backend）の振る舞い**である。
第 1 段（PR #825）は契約とフロントを先行させ、この 3 件を **`dotnet` が実走できないという環境上の理由**で
先送りした（IADR-0215 決定 6）。**その理由は解消した**（本環境で `dotnet 10.0.400` が動く。§着手前の実測）。

## 対象範囲

### 対象

1. **`NotificationService` の新設**（`src/platform/backend/Services/NotificationService/`）。
   IADR-0215 決定 1 が定めた 12 番目のサービスである。
2. **本人限定配信**（AC-3）: 宛先の解決を **JWT の主体（`sub`）だけ**から行い、要求本文・クエリからは一切採らない。
3. **メールに従属しないアプリ内通知**（AC-4）: 永続化と outbox 投入を**別トランザクション**にする。
4. **送出レート制御と観測**（AC-5）: 日次上限（既定 500）・`sent` / `deferred` / `dropped` / `failed` の
   監査ログとメトリクス。
5. 上記に対応するテスト・データ仕様書・実装 ADR（IADR-0267）・既存仕様書の追随。

### 対象外（**送り先を明示する**）

| | 送り先 | 理由 |
| --- | --- | --- |
| **①②③ の発火の結線**（週次／日次バッチ・完全削除イベント・容量跨ぎイベントの購読） | **#451** | 発火源は FR-19 / FR-20 の機能であり **#451 は保留中**（IADR-0119 / IADR-0142）。**保留中の機能へ結線すると、動かないコードが「実装済み」として残る**（IADR-0215 決定 6 と同じ判断）。**決定 5 の表がそのまま実装の指示になる**ので、解除後に迷いは生じない |
| **週次通知の日次分割（平準化）** | **#451** | 「対象者を日次バッチへ分割する」（IADR-0215 決定 4）は**週次バッチそのもの**であり、発火の結線に属する。**上限の消費を数える器（本作業）が先に要る**という順序である |
| **SMTP リレーの実体** | **#600 継続 ＋ ADR-0045 のメール基盤** | 実環境が要るものは触らない（IADR-0197 決定 5 / 利用者裁定）。**トランスポートは差し替え可能な port にし、未設定は「成功」ではなく `failed` として記録する**（IADR-0215 決定 3） |
| **メール宛先アドレスの解決元**（Keycloak 属性か独自の連絡先か） | **#600 継続** | 機能仕様書 §未決事項 2 が明示的に未決としている。**port だけ置き、決定はしない** |
| **BFF 端点 `/bff/notifications*` の実装** | **#600 継続** | `src/platform/backend/Bff/` は本作業の担当領域外（並行作業の衝突面）。**契約は既に載っており、後段が入ったので次段で結べる** |
| **デプロイ結線**（compose / Helm chart / イメージ MAPPING） | **#600 継続** | `deploy/` と `scripts/` は本作業の担当領域外。**Dockerfile だけは service 配下に置く**（どちらの対応表にも載らないので既存検査は動かない） |
| **SSE への切り替え** | **#788** | 移行第 4 段の射程 |

## 設計

**IADR-0215 の決定 1〜5 をそのまま実装する。** 実装に閉じた判断（4 点）は IADR-0267 が持つ。

### 構成

```
src/platform/backend/Services/NotificationService/
  src/NotificationService.Api/
    Foundation/Domain/         Notification / EmailOutboxEntry / 値域定数
    Foundation/Persistence/    NotificationDbContext ＋ Migrations
    Foundation/Contracts/      NotificationDto / NotificationListDto / NotificationReadResultDto
    Foundation/Ports/          IEmailTransport / IEmailAddressResolver
    Foundation/Services/       NotificationSubject / NotificationStore / NotificationPublisher /
                               EmailOutboxDispatcher / NotificationRetention / NotificationMailBody
    Foundation/Observability/  NotificationDeliveryMetrics
    Foundation/Endpoints/      NotificationEndpoints
  tests/NotificationService.Api.Tests/
```

### 本人限定配信（AC-3）

- 主体は `NotificationSubject.Of(ClaimsPrincipal)` が **`sub` → `ClaimTypes.NameIdentifier` → `Identity.Name`**
  の順で解決する。**要求のクエリ・本文・ヘッダからは決して採らない**（採れる口を作らない）。
- `NotificationStore` の全問い合わせが `n.Subject == subject` で絞る。**絞りの無い読み出し口を公開しない。**
- 既読化は**本人の通知でなければ 404**（存在秘匿。IADR-0009 / ADR-0004 の 404 原則）。
  **403 を返さない**——他人の通知 ID の存在が漏れるためである。
- `unreadCount` も**本人の分だけ**を数える。

### メールに従属しない（AC-4）

`NotificationPublisher.PublishAsync` は 2 段である。

1. アプリ内通知を永続化して `SaveChangesAsync`（**ここまでが「通知が届いた」の定義**）。
2. **別の `SaveChangesAsync`** で outbox へ積む。**この段の例外は捕捉し、通知の成否に伝播させない。**

**明示トランザクションを開かないので、2 つの `SaveChangesAsync` は別トランザクションである。**
送信そのもの（SMTP）は `EmailOutboxDispatcher` が後から行うため、**永続化とは時間的にも分離**している。

### 送出レートと観測（AC-5）

- 上限は設定値 `Notification:DailyEmailLimit`、**既定 500**（ADR-0045 決定 1-b の個人アカウント上限。
  **最も厳しい値を既定に置く**）。
- `EmailOutboxDispatcher.DispatchPendingAsync(now)` は当日の `sent` 件数を数え、1 件ずつ次の順で判定する。
  1. **期限を過ぎたもの**（`Deadline <= now`）→ `dropped`（**繰り越しても意味が無い**。IADR-0215 決定 4 の例外）
  2. **上限に達している**→ `deferred`（繰り越し。次回の実行で再び拾う）
  3. それ以外 → 送信。例外・失敗は `failed`
- **4 つの結末すべて**を `IAuditLogger`（`action = notification.email.<outcome>`）と
  メトリクス `notification.email.total{notification.outcome, notification.kind}` に載せる。
  **上限到達そのもの**も `notification.email.limit_reached` として記録する（ADR-0045 決定 8）。
  **「設定が無いから静かに何もしない」を採らない**——受け入れ基準 5 が禁じている形そのものである。

### 保持（IADR-0215 決定 2）

`NotificationRetention.PurgeExpiredAsync` が **90 日**を経過した通知を物理削除する。
定期実行は `NotificationMaintenanceHostedService`（既定 5 分間隔）が担い、**テストでは無効化する**。

## 受け入れ基準

計画・issue #600 の 5 件（AC 番号は既存のテスト仕様書に合わせる）。

- [x] **AC-3 通知が所有者本人にのみ届く**（他の利用者・管理者へは届かない）
- [x] **AC-2 本文が件数と期限のみ**（DTO に自由文の項目が 1 つも無いことを backend 側でも固定する）
- [x] **AC-4 メールが送れなくてもアプリ内通知は届く**
- [x] **AC-5 送信上限を超える通知が静かに落ちない**（監査ログ ＋ メトリクス）
- [ ] **①②③ の発火の結線** —— **対象外。#451 へ送る**（§対象範囲）

## テスト方針

**否定形を必ず置く**（「本人には届く」だけでは、全員に配るコードでも緑になる）。

| 写像先 | 内容 |
| --- | --- |
| **T-09**（AC-3） | 利用者 A / B の通知を作り、**B として読むと A の通知が 1 件も現れない**こと。**B が A の通知 ID を既読化すると 404 で、A の通知は未読のまま残る**こと。`unreadCount` に他人の分が混ざらないこと |
| **T-10**（AC-4） | 送信が必ず失敗するトランスポートで dispatcher を回し、**アプリ内通知が読める・未読のまま**であること。outbox は `failed` で、監査ログに残ること |
| **T-11**（AC-5） | 上限 1 で 2 件積み、1 件目 `sent` / 2 件目 `deferred`。**期限を過ぎたものは `dropped`**。`[Theory]` で 4 結末すべてが監査ログとメトリクスに載ること |
| 契約 | シリアライズした `NotificationDto` の項目集合が **7 項目ちょうど**で、`title` / `body` 等が無いこと |

**変異試験**（`.claude/rules` の要求どおり実施し、証跡を PR へ残す）:
`NotificationStore` の `n.Subject == subject` を外すと T-09 が落ち、戻すと通ることを実測する。

## 計画書との差異

- 差異: **なし**。計画は「アプリ内通知の実体と送出主体は実装設計に委ねる」と決めており、
  本作業はその委任の範囲内である（planning#284 / 計画側の実測コメント 2026-08-22）。
- **計画への環流は起票しない。** 本作業で計画書の誤り・不足は見つかっていない。

## 検査器への影響（開示）

- **`scripts/test-spec-coverage-baseline.json` の更新が要る。** 本作業はテスト仕様書へ新しいテストクラス名を
  載せるため、`check-test-spec-coverage.js` は「記載された対が baseline に無い」で fail する。
  **`scripts/` は本作業の担当領域外**なので、**統括側が `node scripts/check-test-spec-coverage.js --update` を
  実行する**必要がある。**黙って載せない選択（＝ warn 止まりにする）は採らなかった**——
  仕様書に載らないバックエンドテストを増やすのは、この検査器が塞いだ穴そのものだからである。
- `check-contract-schema.js` は `src/<unit>/backend/Shared/*.Contracts` だけを走査する（実測）。
  **本作業の DTO はサービス配下に置くので baseline は動かない。**
- `check-openapi-dto-drift.js` の走査対象も `*.Contracts/Dtos` だけである（実測）。同上。
- **`scripts/xunit1051-baseline.json` の更新が要る。** テストプロジェクトを 1 本増やしたため、
  `check-xunit1051-ratchet.js` が「baseline に無い」で fail する（**新規プロジェクトは
  `remaining:0` ＋ `migrated:true` で登録し、`src/Directory.Build.props` の `XUnit1051Migrated` へも
  同名を足す**のが規約）。**片方だけ直しても赤は消えない**ので、両方を統括側でまとめて行う必要がある。
  **並行作業の `McpServer.Api.Tests` も同じ違反に当たっており、同時に処理できる。**
- **`.ai-context/adr/README.md`（ADR 索引）への追記が要る。** `check-adr-numbering.js` が
  `[index-missing]` で fail する。**索引は統括側の管理対象**であり本作業では触らない。

## 未決事項

1. **メールの宛先アドレスの解決元**は決めていない（機能仕様書 §未決事項 2 のまま）。
   `IEmailAddressResolver` の port だけを置き、既定実装は**解決できない**を返す。
2. **日次上限の実値**は go-live のテナント確定後に合わせる（IADR-0215 フォローアップ 2）。
3. **`x-roles: []` の突合**は BFF 端点が入るまで効かない（`check-bff-authz-docs.js` は実装 → 契約の一方向）。
