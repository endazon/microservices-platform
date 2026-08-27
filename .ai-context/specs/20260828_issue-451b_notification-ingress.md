---
title: 作業仕様書 FR-22 通知の受け口 POST /internal/notifications を実装する（#451-b）
type: spec
status: done
related_ids:
  - FR-22
  - FR-19
  - FR-20
  - UC-11
  - ADR-0037
  - ADR-0045
  - ADR-0004
  - IADR-0009
  - IADR-0017
  - IADR-0026
  - IADR-0215
  - IADR-0267
  - IADR-0270
  - IADR-0280
author: Claude
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (FR-22)
  - planning:projects/microservices-platform/07_adr/ADR-0037_obsidian-sync-method.md (決定 6・17・18)
  - planning:projects/microservices-platform/07_adr/ADR-0045_mail-delivery-smtp-relay.md (決定 1-b・3・8)
related_specs:
  - ./20260823_issue-600_notification-service-backend.md
  - ./20260823_issue-451_private-note-obsidian-sync-core.md
  - ../adr/IADR-0215_notification-service-and-in-app-delivery.md
  - ../adr/IADR-0267_notification-service-backend-subject-scoping-and-send-rate.md
  - ../adr/IADR-0270_private-note-obsidian-sync-backend-core.md
---

# 仕様書: FR-22 通知の受け口 `POST /internal/notifications`（#451-b）

> **この作業で #451 も #600 も閉じない。** 本作業が入れるのは**受け口ただ 1 つ**である。
> メール送出の実体（SMTP）・BFF 端点・デプロイ結線は残る（§対象範囲・§残件）。

## 起点となる計画書（トレーサビリティ）

- 機能要求: **FR-22**（利用者本人への通知。優先度 Should）。計画は「**アプリ内通知の実体と送出主体は
  実装設計に委ねる**」と明示しており、本作業はその委任の範囲内である。
- ユースケース: **UC-11**（自分の資料を作成・管理し、公開範囲を自ら設定する）例外フロー。
- 計画 ADR: **ADR-0037** 決定 6（削除通知の 3 段構え）・決定 17（容量 80/95%）・決定 18（トークン期限
  7 日前）／ **ADR-0045** 決定 1-b・3・8（メール上限と観測）／ **ADR-0004**（認証・監査ログ）。
- 実装 ADR: **IADR-0215**（通知サービスの新設と 5 決定）／ **IADR-0267**（本人限定・送出レート）／
  **IADR-0270** 決定 6（発火の検知はデータの在る DocumentService・送出のみ HTTP で NotificationService へ）／
  **IADR-0280** 決定 2（新規コードの配置写像）。
- 起点 issue: **#451**（残作業のうち受け口 = 451-b）。**発火の結線の残りは #600。**

## 母集合（是正・追随の対象をどう引いたか）

規則の正本は `.claude/rules/traceability.md` 規則 1〜8 と `.claude/rules/traceability.repo.md`
規則 9・10 である。**記憶で挙げず、誤りの側の文字列（「受け口は未実装」と言っている側）で
追跡下の全ファイルを走査してから挙げた。**

- **走査範囲**: `git ls-files`（拡張子で絞らない・行フィルタを継がない）。除外は `src/ai-stock-trading`
  （別プロジェクトの submodule）のみ。
- **走査時点**: 作業ツリー `/home/user/wt-3c`（ブランチ `wt-3c`・起点 `e43e0a9`）2026-08-28。
  **shallow clone のため `git log` / `git blame` を出典に引いていない**（`git rev-parse
  --is-shallow-repository` = `true` を確認済み。planning#410）。

| # | 検索語 | 意図 | ファイル数 | 追随が要るもの |
| ---: | --- | --- | ---: | ---: |
| 1 | `/internal/notifications` | 受け口のパスを名指ししている箇所 | **3** | **1**（送信側コード） |
| 2 | `IngressPath` | 送信側が持つパス定数の宣言と参照 | **1** | 0（値は変えない） |
| 3 | `PrivateNoteNotifier` | 発火側ポートと呼び出し元 | **12** | 0（送信側は変更しない） |
| 4 | `受け口` | 「受け口が無い」と述べている文書 | **33** | **4** |
| 5 | `ingress`（大文字小文字を問わない） | 語の別形 | **103**（`deploy/` を除くと 77） | 0 |
| 6 | `NotificationService` | サービス名を引いている文書・コード | **61** | 4（#4 と同じ集合） |

- **#5 の 103 件はほぼ全件が Kubernetes の Ingress リソース**（別語義）である。**本作業の受け口を
  指しているのは #1 の 3 件だけ**であり、#5 から新たに拾うものは無かった。**走査で当たったが本文を
  読んで除外した**（規則 6）。
- **#4 の 33 件・#1 の 3 件は、本作業が 1 行も書く前の数である**（規則 8。走査対象に自分の成果物が
  入るため、書く行為が母集合を動かす）。**本作業の完了後に同じ走査を引き直すと
  「受け口」は 39 件・`/internal/notifications` は 7 件を返す。**
  内訳は **33 ＋ 本作業が追加した 6 ファイル**（本書・受け口の DTO / 受理判断 / 端点 / テスト・
  `Program.cs` の登録コメント）＝ **39**、および **3 ＋ 本作業の 4 ファイル**（本書・DTO・端点・
  テスト）＝ **7** である。**数はコミットで固定した。**

### 追随が要る 4 件と、その扱い

| 文書 | いま誤りになる記述 | 本作業での扱い |
| --- | --- | --- |
| `src/knowledge/.../HttpPrivateNoteNotifier.cs` | 「🔴 **受け口（… `POST /internal/notifications`）は未実装である**」 | 🔴 **宣言ファイル領域が「読むだけ」であるため触らない。** 統括へ報告する（コメントのみの是正で、契約・挙動は変わらない） |
| `docs/functional/FR-19_private-notes.md`（2 箇所） | 「入っていないのは …②通知サービス側の受け口」「受け口は未実装・手渡し済み」 | **領域外。** 統括へ報告する |
| `docs/functional/FR-20_obsidian-sync.md` | 「入っていないのは …③通知サービス側の受け口」 | **領域外。** 統括へ報告する |
| `docs/functional/FR-22` / `docs/tests/FR-22` / `docs/data/notification.md` | 「**発火の結線**が入っていない」「通知が 1 件も発生しない」 | **領域外。** 受け口が入り、送信側（#451 中核）が既に発火しているため、**この 3 文書の「結線が無い」は本作業の後は成り立たない**。統括へ報告する |

### 追随させないと決めたもの（除外理由つき）

- `.ai-context/adr/IADR-0270_*.md` … **確定済みの凍結記録**。決定 6 の「受け口は未実装」は当時の
  射程の記録であり、後から書き換えると「そのとき何を先送りしたか」が壊れる
  （`traceability.repo.md` §凍結の射程）。**本作業の線引きは本書が持つ。**
- `.ai-context/specs/20260823_issue-451_*.md` / `20260823_issue-600_*.md` … 同じく当時の作業の記録。
  **本書が後段である。**
- `docs/api/openapi.yaml` … §設計 のとおり **1 バイトも変えない**（内部 API は載せない）。
  既存コメント「後段は実装済みだが BFF 端点はまだ」は**本作業の後も真**である。
- `CHANGELOG.md` … 自動生成物（`scripts/gen-changelog.js`）。手で書き足さない。
- `deploy/**` … `notification` の出現が **0 件**（実測）＝ NotificationService はまだ配備対象に
  入っていない。**領域外**であり、#600 の残件として報告する。

## 目的・背景

#451 の中核（`IADR-0270`）で **発火の検知側は実装済み**である —— DocumentService が
①削除通知（週次／7 日前／事後）②容量警告（80/95%）③トークン期限予告を検知し、
`IPrivateNoteNotifier` → `HttpPrivateNoteNotifier` が **`POST /internal/notifications`** へ送出する。
一方 **NotificationService 側に受け口が無い**ため、送出は常に失敗し、エラーログに残るだけであった
（本体操作の成否には影響させない設計）。**本作業はその受け口ただ 1 つを塞ぐ。**

## 対象範囲

### 対象

1. **`POST /internal/notifications`**（`NotificationService.Api`）。送信側のペイロードをそのまま受け、
   既存の `NotificationPublisher.PublishAsync` へ落とす。
2. **入力検証**（不正ペイロードは 400・副作用なし）と**同一事象の重複抑止**。
3. **テスト**（受理・400・重複・重複でない境界・未知種別・無認証到達）と**変異試験**。

### 対象外（送り先を明示する）

| | 送り先 | 理由 |
| --- | --- | --- |
| **メール送出の実体（SMTP）** | **#600 継続 ＋ ADR-0045 のメール基盤** | 実環境が要るものは触らない。トランスポートは port のままで、未設定は「成功」ではなく `failed`（IADR-0215 決定 3） |
| **メール宛先アドレスの解決元** | **#600 継続** | 機能仕様書 §未決事項 2 が明示的に未決。port だけが在る |
| **BFF 端点 `/bff/notifications*`** | **#600 継続** | `src/platform/backend/Bff/` は宣言領域外。契約は既に openapi.yaml に載っている |
| **デプロイ結線**（compose / Helm / NetworkPolicy） | **#600 継続** | `deploy/` は宣言領域外。実測 0 件（§母集合） |
| **送信側コメント・`docs/` の追随** | **#600 / #451** | 宣言領域外（§母集合の表） |
| **発火の検知そのもの** | **完了済み（#451 中核）** | DocumentService 側に実装済み。本作業は受け側だけを足す |

## 設計

### 配置（IADR-0280 決定 2 の写像）

| 追加物 | 置き場所 | 根拠 |
| --- | --- | --- |
| 端点 `NotificationIngressEndpoints` | `NotificationService.Api/Foundation/Endpoints/` | 決定 2「薄い端点 → `.Api`」 |
| 受け口の要求／応答 DTO | `NotificationService.Api/Foundation/Contracts/` | **端点の線上の契約**であり、既存 DTO（`NotificationDtos.cs`）と同じ位置に置く（既存位置のまま可） |
| 受理の判断（検証・重複抑止） | 同上 `Foundation/Services/NotificationIngress.cs` | ユースケース調整。**本サービスはまだ 8 要素へ分解されていない**（IADR-0280 段 2 の対象）ため、`.Application` へ 1 ファイルだけ切り出すと `.Api` 内の既存ユースケース（`NotificationStore` / `NotificationPublisher`）と割れる。**同一サービス内で置き場が 2 つに割れる方が読み手を迷わせる**ので既存位置へ揃えた |
| 端点の登録 | `Program.cs` | 決定 2「合成ルート → `.Api`」 |

**プロジェクト参照は 1 本も足さない**（`check-unit-dependencies.js` の規則 3 に触れない）。
**マイグレーションは不要**である —— スキーマを変えない（§重複の扱い）。

### 面（送信側と 1 バイトずれない形にする）

- **パス**: `/internal/notifications`。送信側 `HttpPrivateNoteNotifier.IngressPath` と同じ値。
  🔴 **platform → knowledge の参照は禁止**なので定数を共有できない。**文字列を複製し、一致は
  テストで固定する**（`IADR-0270` 決定 6 が通知種別で採ったのと同じ扱い）。
- **要求本文**（送信側の匿名オブジェクトと同形。camelCase）:
  `{ subject, kind, occurredAt, count?, thresholdPercent?, deadline? }`。**自由文の項目は 1 つも無い**
  （FR-22 の受け入れ基準を型の形で守る。IADR-0215 決定 2）。
- **応答**: **201** = 新規に受理した／**200** = 同一事象の再送を抑止した（既存の id を返す）／
  **400** = 不正なペイロード。本文は `{ id, duplicate }` の 2 項目だけで、**通知の中身を返さない**。
- 送信側は `IsSuccessStatusCode` しか見ない。**200 と 201 を分けるのは受け側の観測のため**であり、
  送信側の挙動を変えない。

### 認証（既存の内部 API 慣行に従う）

**`RequireAuthorization()` を付けない。** 既存の内部 API（`/internal/introspection`・
`/internal/config/drift-run`・`/internal/mcp-tools`）と同じ扱いである。

- 呼び出し元は**ユーザー文脈を持たない定期処理**（DocumentService の常駐サービス）であり、
  **利用者の JWT を持ち得ない**。ここに認証を課すと、既存の全内部呼び出しと同じ理由で 401 になる
  —— これは `IADR-0017（Superseded by IADR-0026）` が正面から扱った制約そのものである。
- 第一防御は**現行値としては mesh の STRICT mTLS**（`IADR-0026`。`IADR-0017` のネットワーク分離は
  多層防御へ格下げして存続）。ホスト公開しない・NetworkPolicy 既定拒否も引き続き効く。
- 🔴 **残余リスクを明記する**: 同一ネットワーク内からは無認証で通知を作成できる。**作成できるのは
  「件数・閾値・期限だけを持つ通知」であり、読み出しは本人の JWT を要する**（`/notifications` は
  `RequireAuthorization()`＋主体で絞る）。**受け口は書き込み専用で、既存の通知を読み出さない**
  —— 受け口経由で他人の通知を読む経路を作らない。
- **OpenAPI には載せない**（`.ExcludeFromDescription()`）。`docs/api/openapi.yaml` の `/internal/*` は
  **実測 0 本**であり、内部 API を契約へ載せない慣行が既にある。**契約は 1 バイトも変えない。**

### 検証（400 の範囲）

| 項目 | 規則 | 理由 |
| --- | --- | --- |
| `subject` | 必須・空白不可・255 文字以内 | 宛先の無い通知は誰にも届かない。長さは DB 列（`HasMaxLength(255)`）に合わせ、**永続化時の 500 ではなく入口の 400 にする** |
| `kind` | 必須・空白不可・100 文字以内 | 同上（列は 100） |
| `kind` の**値** | **検証しない** | 🔴 **値集合は開いている**（IADR-0215 決定 2）。閉じると「種別を増やしたら未デプロイの受け側が既存の値ごと拒否する」を再現する |
| `occurredAt` | 必須（欠落は 400） | 既定値（`0001-01-01`）を黙って採ると、一覧の並び（`OccurredAt` 降順）と保持期間（90 日）の両方が壊れる |
| `count` | 省略可・0 以上 | 負の件数は表示できない |
| `thresholdPercent` | 省略可・0〜100 | 画面が % として表示する |
| `deadline` | 省略可・制約なし | **過去の期限も正当**である（期限切れの繰り越しは dispatcher が `dropped` にする） |

**400 のときは 1 件も永続化しない**（テストで固定する）。

### 重複の扱い

**同一事象の再送だけを畳む。** 判定は**ペイロード 6 項目の完全一致**
（`subject` / `kind` / `occurredAt` / `count` / `thresholdPercent` / `deadline`）で行い、
既存があれば**新規作成せず既存の id を 200 で返す**。

- 🔴 **`(subject, kind, occurredAt)` では畳まない。** 容量警告は 80% と 95% を**同一の検知時刻で
  同時に発火し得る**（送信側 `PrivateNoteUsage.RecordUsageAndWarnAsync` が跨いだ閾値を順に送る）。
  3 項目で畳むと、**95% の警告が 80% の重複として消える**。**この向きの誤りは「静かに落ちる」側**
  であり、FR-22 の受け入れ基準が最も禁じている形である。
- **畳めるのは同一ペイロードの再送だけである**（ネットワーク再送・送信側プロセスの再実行のうち
  検知時刻が同じもの）。**後続の実行で検知し直したもの（`occurredAt` が違う）は畳まない** ——
  そちらの抑止は送信側の発火記録（`PurgeImminentNotifiedAt` / `WeeklyDigestSentAt` / 容量警告の
  発火記録）が担う。**受け側が時間窓で丸めると、送信側の記録と二重に効いて通知が消える。**
- **一意制約（unique index）は張らない。** ①スキーマ変更＝マイグレーションを増やさない
  ②Postgres の一意索引は NULL を互いに相異なるものとして扱うため、6 項目中 3 項目が NULL 許容の
  本ケースでは**強制にならない**。したがって判定は読み取り → 書き込みであり、**同時到着した
  完全同一の再送は二重に入り得る**。**受け入れる** —— 結末は「本人の一覧に同じ通知が 2 行出る」で
  あって、権限や秘匿が緩む向きではない。

### 配送

受理後は**既存の `NotificationPublisher.PublishAsync` をそのまま呼ぶ**。段 1 でアプリ内通知を
永続化し（**ここまでが「通知が届いた」の定義**）、段 2 で outbox へ積む。**送出の実配線はしない**
—— SMTP は `UnconfiguredSmtpEmailTransport` のままで、dispatcher が回れば `failed` として
監査ログ・メトリクスに残る（IADR-0215 決定 3。「未設定は成功ではない」）。

## 受け入れ基準

- [x] 送信側 `HttpPrivateNoteNotifier` が送るペイロードを、**そのパス・そのままの形**で受理する
- [x] 受理した通知が**宛先本人の `/notifications` に現れる**（既存の主体絞り込みに乗る）
- [x] **不正なペイロードは 400 で、1 件も永続化しない**
- [x] **同一事象の再送は 1 件に畳まれ**、**閾値違い（80%/95%）は畳まれない**
- [x] **未知の種別を拒否しない**（値集合は開いている）
- [ ] メールが実際に届く —— **対象外**（SMTP は環境待ち。#600）
- [ ] 画面に通知が出る —— **対象外**（BFF 端点・SC-19/SC-20 は未実装。#600）

## テスト方針

**否定形と境界を主役に置く。** 「正しいペイロードが 201 になる」だけのテストは、
**何でも受理して何でも作るコードでも緑になる**。

| テスト | 内容 |
| --- | --- |
| 受理の正例 | 送信側と同じ JSON で 201。通知が 1 件・outbox が 1 件。**宛先本人の `/notifications` に現れ、他人には現れない** |
| パス一致 | 送信側の宣言（`/internal/notifications`）と同じパスで到達する（**定数を複製したことの担保**） |
| 400（`[Theory]`） | `subject` 欠落／空白／256 文字・`kind` 欠落／空白／101 文字・`occurredAt` 欠落・`count` 負・`thresholdPercent` 101 の 9 形 |
| 400 の副作用 | 400 のとき通知が **0 件**であること |
| 重複 | 同一ペイロードを 2 回 → 1 回目 201・2 回目 200 で**同じ id**、通知は **1 件** |
| 重複でない | **同一時刻・同一種別で閾値だけ違う 2 件（80%/95%）は 2 件とも残る** |
| 開いた値集合 | 未知の `kind` を 201 で受理する |
| 無認証到達 | 認証ヘッダ無しで 201（内部 API 慣行）。**同じ器で `/notifications` は 401 のまま**であること |

**変異試験**（`.claude/rules` の要求どおり実施し、証跡を残す）:

1. 重複判定から `ThresholdPercent` の比較を外す → 「重複でない」テストが落ちる
2. 重複判定を丸ごと外す（常に新規作成）→ 「重複」テストが落ちる
3. `subject` の空白検査を外す → 400 のテストが落ちる

## 計画書との差異

- 差異: **なし**。計画は「アプリ内通知の実体と送出主体は実装設計に委ねる」と決めており
  （FR-22 の要求文）、受け口の面・状態コード・重複の扱いはその委任の範囲内である。
- **計画への環流は起票しない。** 本作業で計画書の誤り・不足は見つかっていない。

## 検査器への影響（開示）

- **`scripts/xunit1051-baseline.json`**: `NotificationService.Api.Tests` は
  `remaining:0` / `migrated:true` で登録済み。**新規テストは `TestContext.Current.CancellationToken`
  を渡す**（baseline は動かさない）。
- **`check-test-spec-coverage.js`**: `docs/tests/FR-22_user-notifications.md` は領域外のため触らない。
  **テスト仕様書へ新クラス名を載せないので baseline は動かない。** 反面、**受け口のテストは
  テスト仕様書に載らない**——統括へ報告する（#600 の追随に含める）。
- `check-contract-schema.js` / `check-openapi-dto-drift.js`: 走査対象は `*.Contracts` プロジェクトのみ。
  **本作業の DTO はサービス配下なので動かない。**
- `check-unit-dependencies.js`: ProjectReference を足さないため影響なし。
- `gen-knowledge-graph --check`: 本書が参照する `.ai-context/` の文書はすべて実在を確認済み。

## 検証の実測（コミット前・すべて実走）

作業ツリー `/home/user/wt-3c`・`dotnet 10`（`export PATH="/root/.dotnet:$PATH"`）。Docker 無し。

| 検証 | コマンド | 結果 |
| --- | --- | --- |
| ビルド | `dotnet build src/platform/backend/backend.slnx` | **成功・警告 0・エラー 0** |
| 整形 | `dotnet format src/platform/backend/backend.slnx --verify-no-changes` | **差分なし** |
| テスト | `dotnet test .../NotificationService.Api.Tests.csproj` | **53 件全緑**（本作業前 35 件 ＋ 本作業 18 件＝ Fact 9・Theory 9 ケース） |
| ユニット依存 | `node scripts/check-unit-dependencies.js` | OK（csproj 196 / .cs 1843 を走査・違反 0） |
| ライブラリ ratchet | `node scripts/check-backend-libraries.js` | OK（新規混入 0・既知残件 9 は baseline 済み） |
| コミット件名 | `node scripts/check-commit-messages.js --range e43e0a9..HEAD` | OK |

### 変異試験（3 種・いずれも実際に落として戻した）

| # | 変異 | 落ちたテスト | 検出 |
| ---: | --- | --- | :---: |
| 1 | 重複判定から `ThresholdPercent` の比較を外す | `同一時刻同一種別でも閾値が違えば別の通知として残る` | **1 件** ✔ |
| 2 | 重複判定を丸ごと外す（常に新規作成） | `同一ペイロードの再送は畳まれて通知は1件のままである` | **1 件** ✔ |
| 3 | `subject` の必須・空白検査を外す | `不正なペイロードは400を返す`（subject 欠落・空白）／`不正なペイロードは通知を1件も作らない` | **3 件** ✔ |

**変異 1 が本作業で最も重要な検出である。** 3 項目（`subject` / `kind` / `occurredAt`）で畳む素朴な
実装は**変異 2・3 のテストをすべて通過してしまい**、容量警告 95% だけが静かに消える。
**この向きの誤りを捕まえるテストを 1 本だけ持っている**（同一時刻・同一種別・閾値違い）。

## 残件（本作業の後に残るもの）

**本書は `status: done` だが、issue は閉じない。** 受け口は入り、**送信側（#451 中核）から
受け側（本作業）までの経路は繋がった**が、**#600 の結線（BFF 端点・デプロイ）と SMTP の実体は
残っている**。**利用者の目に通知が届く状態にはまだ無い。**

1. **BFF 端点 `/bff/notifications*`**（#600）—— これが入るまで**画面には出ない**。
2. **SMTP の実体と宛先解決**（#600 ＋ 実環境）—— outbox は積まれるが `failed` で終わる。
3. **デプロイ結線**（#600）—— `deploy/` に notification-service が無い（実測 0 件）。
   **DocumentService の `Services:NotificationService` は既定 `http://notification-service:8080` を
   持つため、配備さえされれば結線は動く。**
4. **文書の追随 4 件**（§母集合）—— 宣言領域外のため統括へ報告する。
5. **テスト仕様書 `docs/tests/FR-22` への写像**（#600）。
