---
title: IADR-0446 同期の監査ログに貯蔵を与える — `SyncAuditEntry` を監査ログの正本とし、失敗 7 理由と方向・内訳を同じ書き込みで記録し、本人へ新しい順 50 件を開き、3 年で消す
type: impl-adr
status: Accepted
related_ids: [FR-20, UC-11, SC-20, ADR-0037, ADR-0096, ADR-0099, IADR-0131, IADR-0139, IADR-0270, IADR-0352, IADR-0444, IADR-0446]
author: Claude
created: 2026-09-12
updated: 2026-09-12
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md
  - planning:projects/microservices-platform/07_adr/ADR-0099_sync-history-is-the-audit-log-opened-to-the-owner.md
  - planning:projects/microservices-platform/10_feedback/20260912_share-target-unit-and-sync-history.md
related_specs:
  - ../specs/20260912_1445-1446_share-targets-and-sync-history.md
---

# IADR-0446: 同期の監査ログに貯蔵を与える — `SyncAuditEntry` を監査ログの正本とする

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。
> 計画に影響する決定は計画側へ環流する（`/plan-feedback`）。

- 状態: Accepted
- 日付: 2026-09-12
- 決定者: Claude（実装）

## 起点・関連

- 関連する計画書 ID: FR-20・UC-11・SC-20（主要素 6 同期履歴）／ADR-0099（決定 1 供給元は既存の同期監査ログ・専用ストアを持たない／
  決定 2 本人のみ／決定 3 保持 3 年／決定 4 表示 50 件／決定 5 行の内容・タイトルとパスを含めない／決定 6 失敗も記録）／
  ADR-0037（決定 9「誰が・いつ・何件」）／ADR-0096（完全削除。題名を残さない理由）／planning#618 の裁定記録
- 関連する実装 ADR: [IADR-0444](IADR-0444_private-note-contract-visibility-sync-state-and-conflict-ledger.md)（決定 6 で同期履歴を
  裁定待ちとして切り離した。決定 4 の `SyncConflict` は**競合の台帳**であり、本決定の監査台帳とは別）／
  [IADR-0270](IADR-0270_private-note-obsidian-sync-backend-core.md)（同期の中核。`audit.Record` の呼び出しはここが置いた）／
  [IADR-0352](IADR-0352_obsidian-plugin-push-edit-granularity-and-conflict-resolution.md)（409 の応答は不変）／
  [IADR-0131](IADR-0131_openapi-as-bff-contract-source.md)／[IADR-0139](IADR-0139_domain-bundled-contract-prs.md)
- 起点 issue: #1446（#1445 と 1 PR に束ねる）

## コンテキストと課題

ADR-0099 は「同期履歴は**既存の同期監査ログ**を本人へ開く。専用の履歴ストアを持たない」と定めた。実測（2026-09-12・
`origin/develop` `285e30c`）:

| 段 | 現状 |
| --- | --- |
| 同期の監査ログ | `IAuditLogger.Record(action, subject, outcome, detail)` は **`ILogger` → OTel の構造化ログへ書くだけ**。テーブルも
  エンティティも無く、**本人へ読ませる口も保持期間の処理も存在しない** |
| 記録している経路 | Push 成功 2 か所・Pull・Delete・Move の成功のみ。`version_conflict` は `SyncConflictRecorder` が
  `private-note.sync.conflict`（outcome `recorded`）を 1 件出す |
| 失敗経路 | Push の 409 削除済み・409 パス衝突・507・413・400・404、Move の 409 系・404 は**記録なし** |
| 方向・内訳 | 無い（`device=` `count=` `versions=`） |

🔴 **ADR-0099 の「既存の監査ログ」には貯蔵が無い。** 決定 2〜4（本人へ開く・3 年保持・50 件）は、読める貯蔵があって初めて成立する。
決めることは —— ①貯蔵をどこに置くか（決定 1「専用ストアを持たない」との整合）、②何を記録し何を記録しないか、
③失敗経路の記録をどう原子化するか。

## 検討した選択肢

### 論点 1: 貯蔵

- (1a) **監査ログに貯蔵を与える**: DocumentService に `SyncAuditEntry` の表を持ち、それを**同期監査ログの正本**とする。既存の
  `ILogger` の行は同じ書き込みの**計器**（読み戻さない・保持を約束しない）として続ける
- (1b) OTel 側のログ基盤（Loki 等）へ問い合わせる読み口を作る
- (1c) 「同期履歴」の専用ストアを別に作り、監査ログ（`ILogger`）とは独立に書く

(1a) を採る。(1b) は可観測性基盤を製品機能の依存にし、保持 3 年をログ基盤の設定に委ねる（本リポジトリの CI・テストで検証できない）。
(1c) は ADR-0099 決定 1 が退けた「同じ同期を 2 か所へ記録する」形であり、片方だけ失敗したときにどちらが正か決められない。
(1a) は**書き込みが 1 つ**（表への Add）であり、`ILogger` の行はその書き込みを観測する計器に格下げする —— 従前「監査ログ」と呼んでいた
ものが実は貯蔵を持たなかった、という事実に名前を付け直しただけである。**専用の履歴ストアではなく、監査ログの貯蔵である。**

### 論点 2: 記録する項目

- 行 = `OwnerId` / `DeviceId` / `DeviceName`（記録時点の写し）/ `OccurredAt` / `Direction`（`push` / `pull`）/
  `Added` / `Updated` / `Deleted` / `Conflicted` / `Outcome`（`success` / `failure`）/ `FailureReason`（コード・null 可）。
- 🔴 **`DocumentId`・タイトル・`VaultPath` の列を持たない**（ADR-0099 決定 5 を**型**で守る。反射のテストで列名を固定する）。
  SC-20 §未確定「監査ログに資料 ID を記録するか」は未決のままなので ID も持たない。
- 端末名を写しで持つのは、端末の失効・削除後も行が読めるためである（端末 ID からの JOIN にすると消えた端末の行が「不明」になる）。
  端末 ID は画面へ出さない（決定 5）。
- 失敗理由は**コード**（`version_conflict` / `deleted` / `path_conflict` / `quota_exceeded` / `body_too_large` / `invalid_request` /
  `not_found`）。決定 6 が名指しした 3 経路に、後段が既に拒む 4 経路を足す。**文言は画面が持つ**（「次に何をすればよいか分かる文言」は
  表示側の責務。記録に文言を持つと文言の改善が過去の行へ及ばない）。
- 方向と内訳の写像: Push 新規＝`added=1`、Push 更新＝`updated=1`、Move（改名）＝`updated=1`、Delete＝`deleted=1`、Pull＝`pull`・
  `updated=1`（端末が受け取った 1 件。追加か更新かはサーバが知らない）、Push の `version_conflict`＝`conflicted=1`・`failure`。
  🔴 **401（端末トークンが解決できない）は記録しない** —— 所有者が決まらず「本人の記録」の主体が無い。Manifest も記録しない
  （一覧の取得は同期ではない。従前どおり）。

### 論点 3: 失敗経路の原子性

- 成功経路: 行を Add し、**操作と同じ `SaveChangesAsync` に同乗**させる（操作が失敗すれば行も残らない。「成功」の行だけが真）。
- 失敗経路: 行を Add して**即時 `SaveChangesAsync`**。🔴 **呼ぶのは変更を加える前の早期 return だけ**という不変条件を置く
  （途中まで変更した状態で保存すると失敗した操作の一部が永続化する）。Push の `version_conflict` は `SyncConflictRecorder.RecordAsync`
  （競合台帳の保存）の**後**に記録する —— 競合の行（`SyncConflict`）と履歴の行（`SyncAuditEntry`）は別の表であり、前者は解決の対象、
  後者は事象の記録である。
- 🔴 **409 / 507 / 413 / 400 / 404 の応答の状態・本文は変えない**（IADR-0352。`ObsidianSyncProtocolTests` が固定している）。

## 決定

1. **`SyncAuditEntry` を DocumentService に持ち、これを同期監査ログの正本とする。** `ILogger` の `audit.Record` は同じ書き込みの計器として
   続け、detail に `direction= added= updated= deleted= conflicted= reason=` を足す。専用の履歴ストアは作らない（論点 1）。
2. **記録する項目は論点 2 のとおり。`DocumentId`・タイトル・`VaultPath` の列を持たない。** 失敗理由はコード 7 値（`SyncFailureReasons`）。
3. **成功は操作と同じ SaveChanges、失敗は変更前の早期 return で即時保存**（論点 3）。プロトコルの応答は不変。
4. **`GET /private-notes/sync-history?limit=`（本人・`OccurredAt desc`・既定 50・上限 200）を後段に、`GET /bff/private-notes/sync-history`
   （透過中継・`x-roles: []`・50 件固定）を BFF に足す。** 「さらに読み込む」は置かない（ADR-0099 決定 4 は実装設計に委ねた。要望が出てから）。
5. **定期処理 ⑦ `PurgeSyncAuditAsync`: `OccurredAt < now − 3 年` の行を削除する**（`SyncAuditEntry.RetentionYears = 3`。ADR-0099 決定 3）。
   既存 ⑥ の後ろに置く。監査に「いつ・何件」を 1 行。
6. **記録の入口は操作（`SyncOps.Push / Pull / Delete / Move`）であり、方向はそこから導出する**（`DirectionOf`。delete / move も
   `push`）。監査 action は従前どおり経路ごと（`private-note.sync.<op>`）で、`audit.Record` の呼び出しは recorder の側へ移した
   （各経路の直呼びを消し、`extra` で既存の `count= versions=` を引き継ぐ。「push 1 回＝記録 1 件」を固定する既存テストを
   崩さない）。push の成功 2 経路も、行の Add を資料の Add／版の確定と**同じ** `SaveChangesAsync` の前に置く（監査の指摘で是正。当初は資料の保存後に別の保存で確定させており、2 回の保存の間で落ちると「成功したのに履歴が無い」が起こり得た）。同名への move
   （冪等の no-op）は記録しない。🔴 **`FailureAsync` の不変条件「資料へ変更を加える前の早期 return でのみ呼ぶ」は
   コメントとレビューだけが守る**（機械検査は無い。同型の事故が 2 回起きたら検査器を足す）。
7. **画面（SC-20）は失敗理由のコードを「次に何をすればよいか」の文言へ写す**（`syncHistory.ts`）。未知のコードは「同期に失敗しました
   （理由: `<code>`）」で落とさない。タイトル・パスの列を持たない（DTO に無いことをテストで固定）。

## 結果

- 良い点: SC-20 主要素 6 が計画の 6 項目どおりに描ける。失敗が初めて記録に残る（従前は成功しか無かった）。保持 3 年が本リポジトリの
  テストで検証できる（時計を進める）。
- 悪い点 / 残余リスク:
  - 🔴 **ADR-0099 決定 1 の「既存の同期監査ログ」に貯蔵が無かった** —— 本決定は「監査ログに貯蔵を与えた」のであり、計画が
    前提にした「既に在る記録を開いた」のとは実態が違う。**planning へ環流する**（ADR-0099 §実装の現状 の追記候補）。
  - **`ILogger` の行と表の行は同じ書き込みから出るが、`ILogger` は保存に失敗しても出る**（Add と Log の間に SaveChanges の失敗が
    入る余地は成功経路にある）。計器と正本のずれは「表に無い行がログにある」向きのみで、逆は無い。
  - 3 年分の行数の観測手段は無い（ADR-0099 §結果 と同じ残余）。同期は頻繁で、1 操作 1 行である。
  - 端末名は写しなので、端末を改名しても過去の行は旧名のままである（監査ログとして正しい振る舞いだが、画面では「今の名前」と
    食い違う）。
  - push の 507 / パス衝突の失敗経路では、直前の `GetOrCreateQuotaAsync` が既定の quota 行を追加していることがあり、失敗の
    保存がそれも確定させる（後続の成功でも作られる行であり無害）。
  - Migration に backfill は無く、**配備前の同期は履歴に無い**（構造化ログにしか無かったものは表へ写せない）。
  - 未解決競合のローカル本文の保持期限は本決定でも定まらない（IADR-0444 の残余。競合の本文は監査ログではなく 3 年に揃える根拠が無い）。

## フォローアップ

1. planning への環流（貯蔵が無かった事実・表示名検索の到達範囲）。応答によって決定 1 の名付け（監査ログの貯蔵）を改める可能性がある。
2. 同期の監査ログの第三者閲覧（ADR-0099 決定 2 の残り）が裁定されたら、読み口の認可を足す（本決定の口は本人のみ）。
3. 3 年分の行数の観測（05_observability-ops の指標への追加を検討）。
