---
title: 作業仕様書 FR-22 通知の発火の結線（3 契機の送出を fail-open と冪等で固める。#600 トラック 3E）
type: spec
status: in-progress
related_ids:
  - FR-22
  - FR-19
  - FR-20
  - UC-11
  - NFR-19
  - ADR-0037
  - ADR-0045
  - IADR-0215
  - IADR-0267
  - IADR-0270
  - IADR-0280
author: Claude
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (FR-22 / FR-19)
  - planning:projects/microservices-platform/07_adr/ADR-0037_obsidian-sync-method.md (決定 6・17・18・§結果 フォローアップ 6)
  - planning:projects/microservices-platform/07_adr/ADR-0045_mail-delivery-smtp-relay.md (決定 8)
related_specs:
  - ./20260823_issue-451_private-note-obsidian-sync-core.md
  - ./20260823_issue-600_notification-service-backend.md
  - ./20260828_issue-451b_notification-ingress.md
  - ../adr/IADR-0215_notification-service-and-in-app-delivery.md
  - ../adr/IADR-0270_private-note-obsidian-sync-backend-core.md
---

# 仕様書: FR-22 発火の結線（#600 トラック 3E）

> **本作業で #600 は閉じない。** 配備（`deploy/`）・BFF 端点・SMTP の実体は残る（§残件）。

## 起点となる計画書（トレーサビリティ）

- 機能要求: **FR-22**（利用者本人への通知。優先度 Should）。3 契機 ①削除通知（週次／7 日前／事後）
  ②保存容量の警告（80% / 95% に**各 1 回**）③同期トークンの期限予告（7 日前）。
  **いずれも件数と期限のみを含み、資料のタイトル・本文を含めない。宛先は所有者本人のみ。**
- 関連要求: **FR-19**（容量の 80% / 95% 段階警告）／**FR-20**（同期トークン）／**UC-11** 例外フロー。
- 計画 ADR: **ADR-0037** 決定 6（3 段構えの削除通知）・決定 17（80/95%）・決定 18（期限 7 日前）／
  **ADR-0045** 決定 8（静かに落ちない）。
  🔴 **ADR-0037 §結果 フォローアップ 6 が「80% / 95% 警告の判定タイミングと再通知の抑制間隔を
  運用設計で定める」と実装側へ委ねている。** 本書の冪等性の設計はこの委任の範囲内であり、
  **計画への環流は要らない**（§計画書との差異）。
- 実装 ADR: **IADR-0215**（通知サービスの新設・決定 3「メールに従属させない」・決定 5「発火の検知」）／
  **IADR-0270** 決定 6（発火の検知はデータの在る DocumentService・送出のみ HTTP）／
  **IADR-0267**（本人限定・送出レート）／**IADR-0280** 決定 2（新規コードの配置写像）。
- 起点 issue: **#600**（FR-22 の残作業＝発火の結線。トラック 3E）。

## 母集合（是正・追随の対象をどう引いたか）

規則の正本は `.claude/rules/traceability.md` 規則 1〜8 と `.claude/rules/traceability.repo.md`
規則 9・10 である。**記憶で挙げず、「発火の結線が入っていない」と言っている側の文字列で
追跡下の全ファイルを走査してから挙げた。**

- **走査範囲**: `git ls-files`（拡張子で絞らない・行フィルタを継がない）。除外は `src/ai-stock-trading`
  （別プロジェクトの submodule）のみ。
- **走査時点**: 作業ツリー `/home/user/wt-3e-new`（ブランチ `wt-3e-449`・起点 `8253134`）2026-08-28。
  **shallow clone のため `git log` / `git blame` を出典に引いていない**
  （`git rev-parse --is-shallow-repository` = `true` を確認済み。planning#410）。

| # | 検索語 | 意図 | ファイル数 | 追随が要るもの |
| ---: | --- | --- | ---: | ---: |
| 1 | `発火の結線` | 「結線が無い」と述べている箇所 | **13** | **3**（下表） |
| 2 | `結線` | 語を広げる（#1 の上位集合） | **90** | 0（#1 以外は別文脈＝検査器・UI・pin の結線） |
| 3 | `通知が 1 件も発生しない` | 最も強い誤りの言明 | **2** | **1** |
| 4 | `受け口が入るまで` | 受け口不在を前提にしたコメント | **2** | **1**（`Program.cs`） |
| 5 | `受け口は未実装` | 同上（凍結記録の側） | **3** | 0（すべて凍結記録） |
| 6 | `notification-service` | 送出先の名前を引いている箇所 | **13** | 0（配備は §残件） |
| 7 | `Services:NotificationService` / `Services__NotificationService` | 接続設定の宣言 | **2** / **0** | 0（§配備 の判断） |

### 追随が要る 5 件と、その扱い

| 文書 | いま誤りになる記述 | 本作業での扱い |
| --- | --- | --- |
| `docs/functional/FR-22_user-notifications.md` | 「①②③ の発火の結線（が入っていない）」「**通知が 1 件も発生しない**」 | **本作業で是正する** |
| `docs/tests/FR-22_user-notifications.md` | 「残っているのは発火の結線だけ」「発火そのもののテストは無い。結線する issue が書く」 | **本作業で是正し、T-24〜T-27 を足す** |
| `docs/data/notification.md` | 「**発火の結線（通知を作る側）は入っていない。**」 | **本作業で是正する**（FR-22 のデータ仕様書であり、宣言領域外の指定に無い） |
| `src/knowledge/.../DocumentService.Api/Program.cs` | 「受け口が入るまで送出失敗はエラーログに記録される」 | **本作業で是正する**（同ファイルを送出結線のために触るため） |
| `docs/functional/FR-19_private-notes.md`（2 箇所）／`docs/functional/FR-20_obsidian-sync.md`（1 箇所） | 「受け口は実装済み・**発火の結線が残る**」 | 🔴 **宣言領域外（3F / 3G）。触らず統括へ報告する** |

### 追随させないと決めたもの（除外理由つき）

- `.ai-context/adr/IADR-0270_*.md`・`.ai-context/specs/20260823_*`・`20260828_issue-451b_*` …
  **確定済みの凍結記録**。当時の射程の記録であり、後から書き換えると「そのとき何を先送りしたか」が
  壊れる（`traceability.repo.md` §凍結の射程）。**本作業の線引きは本書が持つ。**
- `.ai-context/adr/IADR-0215_*.md` … **live な権威文書**であり、決定を延長するため
  **`［2026-08-28 追記 / #600］` の追記ブロックを足す**（書き換えではない。§IADR の扱い）。
- `.ai-context/adr/IADR-0267_*.md` … 送出側（NotificationService）の決定であり、発火側の変更で
  誤りにならない。
- `CHANGELOG.md` … 自動生成物（`scripts/gen-changelog.js`）。手で書き足さない。
- `deploy/**` … `notification` の出現が **0 件**（実測）。§配備 の判断のとおり本作業では触らない。
- `docs/api/openapi.yaml` … **1 バイトも変えない**（内部 API は載せない。宣言領域外でもある）。

### 走査が自分の成果物を含むことの開示（規則 8）

**上表の数はいずれも本作業が 1 行も書く前の数である。** 本作業の完了後に同じ走査を引き直すと
`発火の結線` は **13 ＋ 本作業が追加した 2 ファイル（本書・IADR-0215 の追記ブロックは既存
ファイルのため増えない）＝ 15**、`通知が 1 件も発生しない` は **2 − 1（FR-22 機能仕様書から削る）
＋ 1（本書が誤りとして引用する）＝ 2** を返す。**数はコミットで固定する。**

## 目的・背景（いま実際に何が欠けているか）

**「3 契機の発火側が丸ごと未結線」ではない。** 実測すると、#451 中核（IADR-0270）で
**検知・発火のポート呼び出しはすべて入っており**、3C（#451-b）で**受け口も入っている**。

| 契機 | 検知の実装場所 | 検知方式 | 状態 |
| --- | --- | --- | --- |
| ①-a 週次サマリ | `PrivateNoteMaintenanceService.NotifyWeeklyDigestAsync` | 定期（日次 HostedService）＋所有者ごとの前回送出時刻で 7 日間隔 | **実装済み** |
| ①-b 完全削除 7 日前 | 同 `NotifyPurgeImminentAsync` | 定期。`PurgeAt` の窓（now < PurgeAt ≦ now+7d）＋発火記録 | **実装済み** |
| ①-c 完全削除の事後 | 同 `PurgeExpiredAsync` | 定期。物理削除の実行と同じ経路（イベント時） | **実装済み** |
| ② 容量 80% / 95% | `PrivateNoteUsage.RecordUsageAndWarnAsync`（同期 push・完全削除・自動 purge の 3 呼び出し点） | **イベント時**（使用量が動いた直後の跨ぎ判定） | **実装済み** |
| ③ トークン期限 7 日前 | `PrivateNoteMaintenanceService.NotifyTokenExpiryAsync` | 定期。`ExpiresAt` の窓＋発火記録 | **実装済み** |

**欠けているのは「検知」ではなく「結線」の 3 点である。**

1. 🔴 **送出アダプタ `HttpPrivateNoteNotifier` の fail-open が例外型の列挙で破れている。**
   握るのは `HttpRequestException` と `TaskCanceledException` の 2 型だけで、
   `InvalidOperationException`（BaseAddress 不整合・`HttpClient` 破棄後の利用）・`JsonException`・
   `NotSupportedException` などは**素通りして呼び出し元へ抜ける**。抜けた先は
   **利用者の要求（同期 push・完全削除）と定期処理**であり、**受け口が落ちていると業務処理が
   失敗する**。IADR-0215 決定 3 が構造で守った「通知はメールに従属しない」の**発火側の対**が、
   ここで型の列挙という形で破れている。
2. 🔴 **タイムアウトが既定の 100 秒である。** 受け口が TCP は受けるが応答しない状態
   （配備直後・過負荷）では、**利用者の要求が最大 100 秒止まる**。fail-open は「落ちない」だけでなく
   「**待たせない**」ことも要る。
3. 🔴 **計器が無い。** 送出の失敗はエラーログにしか残らず、**ダッシュボードには出ない**。
   ADR-0045 決定 8「静かに落ちない」は送出側（NotificationService）にはあるが、
   **発火側には無い**——受け口へ届かなかった通知は、いまはどこにも数えられていない。

加えて、**「各 1 回」の冪等性が送出と記録の順序で破れる**（§設計 B）。

## 対象範囲

### 対象

1. **送出アダプタの fail-open を全例外へ広げ**、**明示のタイムアウト**を置き、**計器**に載せる。
2. **発火記録を送出より先に確定させる**（4 つの発火記録すべて）。
3. **テスト**（本人のみ・件数と期限のみ・fail-open・順序＝冪等性・計器）と**変異試験**。
4. **文書の追随**（`docs/functional/FR-22` / `docs/tests/FR-22` / `docs/data/notification.md`）と
   **IADR-0215 への追記ブロック**。

### 対象外（送り先を明示する）

| | 送り先 | 理由 |
| --- | --- | --- |
| **配備（compose / Helm / NetworkPolicy）** | **#600 継続・独立 issue** | §配備 のとおり。**接続 env だけを足しても結線は成立しない** |
| **BFF 端点 `/bff/notifications*`** | **#600 継続** | `src/platform/backend/Bff/` は宣言領域外 |
| **SMTP の実体・宛先アドレスの解決元** | **実環境待ち** | 実環境が要るものは触らない（IADR-0215 §制約） |
| `PrivateNoteEndpoints.cs` / `DocumentEndpoints.cs` / `CreateDocumentRequest` | **3F / 3G** | 宣言領域外。**共有ヘルパ `PrivateNoteUsage` は触るが、端点ファイルは 1 行も触らない** |
| `docs/functional/FR-19` / `FR-20` の追随 | **3F / 3G** | 宣言領域外（§母集合の表） |
| `docs/api/openapi.yaml` / フロントエンド / 検査器本体 | — | 宣言領域外 |

## 設計

### A. 送出アダプタ —— fail-open を型の列挙から「呼び出し元のキャンセル以外すべて」へ

| 項目 | 変更前 | 変更後 | 理由 |
| --- | --- | --- | --- |
| 握る例外 | `HttpRequestException` / `TaskCanceledException` の 2 型 | **呼び出し元のキャンセル以外のすべて** | 型を列挙する形は**列挙から漏れた瞬間に業務処理が落ちる**。守りたい性質は「通知の送出は業務処理を失敗させない」であって「HTTP 例外だけを握る」ではない |
| 呼び出し元のキャンセル | 伝播（`!ct.IsCancellationRequested` の条件で除外） | **伝播（変えない）** | 握ると「キャンセルされたのに続行した」ように見える。**シャットダウンと利用者の切断は業務処理の側の事情**である |
| タイムアウト | 既定 100 秒 | **5 秒**（`HttpPrivateNoteNotifier.SendTimeout`） | 3 契機はいずれも日・週の粒度であり、**5 秒待って届かない通知のために利用者を待たせる理由が無い**。超過は `TaskCanceledException` として fail-open 側へ落ちる |
| 観測 | エラーログのみ | **ログ ＋ 計器** | ADR-0045 決定 8 の発火側の対 |

**計器** `PrivateNoteNotificationMetrics`（`Foundation/Observability/`）:

- Meter 名は**サービス名**（`microservices-platform.document-service`。`IngestTagMetrics` と同じ器）。
- カウンタ `notification.dispatch.total`、属性は **`notification.kind`** と
  **`notification.outcome` ∈ {`sent`, `rejected`, `unreachable`}** の 2 つだけ。
- 🔴 **利用者識別子（subject）を属性にしない。** カーディナリティが非有界で、個人の利用行動の記録に
  踏み込む（`NotificationDeliveryMetrics` / `LlmUsageMetrics` と同じ規律。ADR-0044 決定 1）。
- **結末を 1 本のカウンタの属性で分ける**（送出側 `NotificationDeliveryMetrics` と同じ形）。
  結末ごとにカウンタを分けると「送れたぶん」だけを見て安心できてしまう。

### B. 冪等性 —— **発火記録を送出より先に確定させる（at-most-once）**

**現状はすべて「送出 → 記録 → `SaveChanges`」である。** プロセスが送出後・保存前に落ちる
（再起動・DB の一時障害・ホストの停止）と、**次周期に同じ通知をもう一度送る**。

🔴 **受け口の重複抑止では止まらない。** 3C の受け口は**ペイロード 6 項目の完全一致**でしか畳まず、
`occurredAt` が変わる再検知は畳まれない —— これは意図的な設計であり、時間窓で丸めると
**送信側の発火記録と二重に効いて通知が消える**（`20260828_issue-451b_notification-ingress.md`
§重複の扱い）。したがって**重複を止められるのは発火側だけ**である。

**決定: 発火記録を `SaveChanges` で確定させてから送出する。**

| # | 発火記録 | 置き場所 | 変更 |
| --- | --- | --- | --- |
| ①-a | `PrivateNoteQuota.WeeklyDigestSentAt` | `NotifyWeeklyDigestAsync` | 記録 → 保存 → 送出 |
| ①-b | `PrivateNote.PurgeImminentNotifiedAt` | `NotifyPurgeImminentAsync` | 同上 |
| ①-c | **記録を持たない** | `PurgeExpiredAsync` | **変更なし**。行そのものが消えるため構造的に 1 回である（対象が二度と現れない） |
| ② | `PrivateNoteQuota.Warned80` / `Warned95` | `PrivateNoteUsage.RecordUsageAndWarnAsync` | 同上（ヘルパ内で保存する） |
| ③ | `SyncDevice.ExpiryNotifiedAt` | `NotifyTokenExpiryAsync` | 同上 |

**受容するトレードオフ（明示する）**: **記録は残ったが送出に失敗した通知は、二度と送られない。**

- これは fail-open（送出の失敗で業務処理を止めない）と**同じ向きの受容**である。
- 逆順（送出先行）は「1 回以上」を保証するが、**重複を止める手段がどこにも無い**。
  FR-19 / FR-22 の受け入れ基準は「80% / 95% に達した時点で**各 1 回**」であり、
  **多いほうが要求違反**である。**少ないほうは計器で観測できる**（`outcome=unreachable`）。
- **②だけは自然に回復する** —— 使用量が閾値を下回れば発火記録が再武装され、再度跨いだときに
  改めて警告が出る（`PrivateNoteQuota.RecordUsage`）。

**`RecordUsageAndWarnAsync` がヘルパ内で `SaveChanges` するようになる。**
3 つの呼び出し点（同期 push の新規／更新・完全削除の端点・自動 purge）は**いずれも本体の業務処理を
先に確定させてから本ヘルパを呼んでいる**ため、ヘルパ内での確定は業務処理のトランザクションを
分断しない（実測で確認した）。呼び出し側に残る直後の `SaveChanges` は無害な no-op であり、
**宣言領域外のファイルを触らないため今回は除かない**（統括へ報告する）。

### C. 発火の検知方式は変えない

**既存の実装様式（メンテナンスの HostedService 周期＝日次）へ寄せる**という拘束は既に満たされている
（§目的・背景の表）。**新しい検知経路も新しい周期も作らない。**

### 配備（compose / Helm）の判断 —— **本作業では配線しない**

- **実測: `deploy/` に `notification` の出現は 0 件**である。NotificationService は**まだ配備対象に
  入っていない**。
- DocumentService のコード既定は `http://notification-service:8080` であり、
  **compose のサービス名としても in-cluster DNS としても正しい値**である。
  したがって `Services__NotificationService` を足しても**既定と同値の重複**にしかならない。
- 🔴 **指す先が配備されていない以上、env を足しても結線は成立しない。** 必要なのは配備そのもの
  （compose のサービス定義・`create-multiple-dbs.sh` の `notification_svc`・`k8s-local-images.sh` の
  `MAPPING`（`check-image-mapping.js` が突合）・helm values・NetworkPolicy）であり、
  **本トラック（発火の結線）の射程を超える。独立の issue として統括へ申し送る。**

### IADR の扱い —— **新 IADR を起こさず IADR-0215 へ追記ブロックを足す**

指示は「新 IADR が要る規模の決定が出たら IADR-0285 を使う。**可能なら IADR-0215 への日付つき
追記ブロックで足りるか先に検討する**」であった。**検討の結果、追記で足りると判断した。**

- 本作業の 2 つの決定（fail-open の射程・発火記録の順序）は、**いずれも既存決定の延長**である ——
  決定 3「メールに従属させない」の**発火側の対**と、決定 5「発火の検知」の**実行順序の詳細**。
  **新しい機構も新しい構成要素も作らない。**
- `.ai-context/adr/` は **live な権威文書**であり、決定を変える追記は
  `［YYYY-MM-DD 追記 / #NNN］` ブロックで残す（`traceability.repo.md`）。IADR-0215 には
  既に `［2026-08-16 追記 / #600］` の先例がある。
- **IADR-0285 は使わない**（消費しない）。統括が「独立の IADR が要る」と判断した場合は、
  本書の §設計 A・B をそのまま移せる形で書いてある。

## 受け入れ基準

- [x] **3 契機が所有者本人にのみ発火する**（他人・管理者の subject が現れない）
- [x] **送出するペイロードが件数・閾値・期限だけで構成される**（タイトル・本文・検索語が混入しない。
      **ポートの型に自由文の引数が無い**ことに加え、**アダプタが実際に送る JSON** でも固定する）
- [x] **受け口が落ちていても業務処理（同期 push・完全削除・定期処理）が失敗しない**
      （**任意の例外**・非 2xx・タイムアウトのいずれでも）
- [x] **呼び出し元のキャンセルは握り潰さない**
- [x] **「各 1 回」が再起動・再計算で破れない**（**発火記録が送出より先に確定している**）
- [x] **送出の結末が計器に載る**（利用者識別子を属性にしない）
- [ ] メールが実際に届く —— **対象外**（SMTP は環境待ち）
- [ ] 画面に通知が出る —— **対象外**（BFF 端点・配備が残る）

## テスト方針

**否定形と順序を主役に置く。** 「通知が発火する」だけのテストは既にあり（3 契機とも）、
**それらは記録用スタブを相手にしているため、送出の失敗も順序も見ていない。**

新設: `PrivateNoteNotificationDispatchTests`（`DocumentService.Api.Tests`）。

| テスト | 内容 | 対応する受け入れ基準 |
| --- | --- | --- |
| 送出の面 | 実アダプタが叩くパスが `/internal/notifications` である。送る JSON の項目集合が **6 項目ちょうど**で、`title` / `body` / `message` / `text` / `summary` / `detail` / `content` に相当する項目が 1 つも無い | 件数と期限のみ |
| fail-open（`[Theory]`） | ハンドラが **任意の例外**（`InvalidOperationException` を含む）・`HttpRequestException`・`TaskCanceledException` を投げる／**500**・**404** を返す —— **いずれも `NotifyAsync` は例外を投げない** | 受け口が落ちても止めない |
| キャンセル | 既にキャンセル済みの `CancellationToken` を渡すと **`OperationCanceledException` が伝播する** | 握り潰さない |
| タイムアウト配線 | **実ホストに登録された名前付きクライアント**のタイムアウトが既定 100 秒ではなく `SendTimeout` である | 待たせない |
| 計器 | 結末ごとに `notification.dispatch.total` が 1 件ずつ載る。**属性は kind と outcome の 2 つだけで、subject が現れない** | 静かに落ちない |
| **順序（①-b / ①-a / ③）** | 送出の瞬間に**別スコープの `DbContext`** で読むと、**発火記録が既に永続化されている** | 各 1 回 |
| **順序（②）** | 同上（`Warned80` が送出前に永続化されている） | 各 1 回 |
| **業務経路の fail-open** | **実アダプタ＋常に例外を投げるハンドラ**を差した器で同期 push を行うと、**201 が返る**（通知は届かないが資料は保存される） | 受け口が落ちても止めない |
| 宛先 | 上記の順序テストで発火した通知の subject が**所有者本人のみ**である | 本人のみ |

**変異試験**（実際に壊して落ちることを実測する。§検証の実測 に結果を書く）:

1. `catch` の条件を元の 2 型（`HttpRequestException or TaskCanceledException`）へ戻す
   → 任意例外の fail-open テストが落ちる
2. 名前付きクライアントのタイムアウト設定を外す → タイムアウト配線のテストが落ちる
3. `NotifyPurgeImminentAsync` の順序を元（送出 → 記録 → 保存）へ戻す → ①-b の順序テストが落ちる
4. `RecordUsageAndWarnAsync` のヘルパ内 `SaveChanges` を外す → ②の順序テストが落ちる
5. 計器の記録を外す → 計器テストが落ちる

## 計画書との差異

- 差異: **なし**。冪等性の記録方式と再通知の抑制は **ADR-0037 §結果 フォローアップ 6 が明示的に
  実装側へ委ねている**。fail-open の射程は FR-22 の「アプリ内通知が主・メールが補助」を
  発火側へ写したものであり、計画の決定を動かさない。
- **計画への環流は起票しない。** 本作業で計画書の誤り・不足は見つかっていない。

## 検査器への影響（開示）

- **`check-test-spec-coverage.js`**: 新クラス `PrivateNoteNotificationDispatchTests` を
  `docs/tests/FR-22_user-notifications.md` へ載せ、**`--update` で baseline を前進させる**
  （床を上げる向き。差分理由は本節）。
- **`check-xunit1051-ratchet.js`**: `DocumentService.Api.Tests` は `migrated:false`
  （`remaining:94`）。判定は関係のみで件数を見ないため **baseline は動かさない**。
- `check-contract-schema.js` / `check-openapi-dto-drift.js`: 走査対象は `*.Contracts` プロジェクトのみ。
  **本作業は `Foundation/` 配下だけなので動かない。**
- `check-unit-dependencies.js`: ProjectReference を足さないため影響なし。
- `check-backend-libraries.js`: 新規パッケージを足さないため影響なし。
- `check-trace-blocks.js` / `check-doc-updated.js` / `gen-knowledge-graph --check`:
  `docs/` の 3 文書は trace ブロックの `specs:` へ本書を足し、`updated:` を前進させる。
- **`check-image-mapping.js` / `check-deploy-manifests.js`**: `deploy/` を触らないため影響なし。

## 検証の実測（コミット前・すべて実走）

> **本節は実装後に追記する**（`docs(FR-22)` の最終コミット）。

## 残件（本作業の後に残るもの）

1. **配備**（compose / Helm / `create-multiple-dbs.sh` / `k8s-local-images.sh` の MAPPING /
   NetworkPolicy）—— **これが入るまで通知は 1 件も受け口へ届かない**（送出は `unreachable` として
   計器とログに残る）。**独立 issue として統括へ申し送る。**
2. **BFF 端点 `/bff/notifications*`** —— これが入るまで**画面には出ない**。
3. **SMTP の実体と宛先解決** —— outbox は積まれるが `failed` で終わる。
4. **`docs/functional/FR-19` / `FR-20` の追随 3 箇所** —— 宣言領域外（3F / 3G）。統括へ報告する。
5. **`ObsidianSyncEndpoints` / `PrivateNoteEndpoints` に残る冗長な `SaveChanges`** ——
   no-op であり挙動を変えないが、宣言領域外のため今回は除かない。
