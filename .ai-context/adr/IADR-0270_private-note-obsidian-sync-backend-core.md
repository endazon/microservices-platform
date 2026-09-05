---
title: IADR-0270 個人資料と Obsidian 同期の中核は DocumentService に置き、台帳・別系統トークン・deny 側既定で実装する
type: impl-adr
status: Proposed
related_ids: [FR-19, FR-20, FR-22, UC-11, SC-19, SC-20, ADR-0036, ADR-0037, ADR-0046, ADR-0054]
author: claude
created: 2026-08-23
updated: 2026-09-05
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0037_obsidian-sync-method.md
  - planning:projects/microservices-platform/07_adr/ADR-0046_private-note-not-synced-to-wikijs.md
  - planning:projects/microservices-platform/07_adr/ADR-0054_doc-scope-attribute-for-private-note.md
issue: "#451"
---

# IADR-0270: 個人資料と Obsidian 同期のバックエンド中核の実装方式

- 状態: Proposed
- 日付: 2026-08-23
- 決定者: claude（実装判断）／起点 issue #451

## 起点・関連

- 関連する計画書 ID: FR-19 / FR-20 / FR-22 / UC-11 / SC-19 / SC-20 /
  `ADR-0037`（決定 1〜20）/ `ADR-0046`（D-01〜D-06）/ `ADR-0054`（決定 1〜6）/ `ADR-0036`
- 関連する実装仕様書: `.ai-context/specs/20260823_issue-451_private-note-obsidian-sync-core.md`
- 前提 IADR（覆さない）: `IADR-0253`（認可スコープ選言）/ `IADR-0215`・`IADR-0267`（通知）/
  `IADR-0269`（MCP サービスアカウント除外）/ `IADR-0264`（本文受け入れ経路）/ `IADR-0021`（Wiki 同期）

## コンテキストと課題

計画は同期方式（`ADR-0037` 決定 1〜20）・語彙（`ADR-0054`）・編集経路（`ADR-0046`）を確定させたが、
**どのサービスが個人資料のライフサイクル（容量・版保持・削除・トークン）を持つか**と、
**プラグイン向け API の認証・競合・通知発火の実装形**は実装へ委ねられている。
決めるべきは次の 7 点である。

## 検討した選択肢と決定

### 決定 1: 実装先は **DocumentService**（新サービスを立てない）

| 案 | 評価 |
| --- | --- |
| **A. DocumentService へ実装（採用）** | 個人資料の実体は `Document`＋版（既存の append-only 版履歴をそのまま決定 8 に使える）。容量・削除・共有（`DocumentShare`）と同じ集約・同じ DB に閉じる |
| B. SyncService を新設 | 版・本文・共有へ届くために DocumentService の DB を読む＝DB per Service 違反。新規テストプロジェクト・デプロイ一式の台帳追随も要る |
| C. IngestionService へ相乗り | 取り込みは「システム投入経路」であり、`ADR-0054` 決定 5 が「取り込み経路が個人資料を作ることはない」と定める。責務が逆向き |

### 決定 2: 個人資料 = `Document` ＋ **台帳 `PrivateNote`**（Document のスキーマは変えない）

- 属性は `doc_scope=private-note`（`ADR-0054` 決定 1・2）・`owner=本人`・`confidentiality` 既定
  `restricted`。**判定は常に集合帰属**（`== "private-note"`。否定で書かない —— `doc_scope` を持たない
  既存組織文書が一斉に該当してしまう。WikiService `DocumentSyncConsumer` と同一の作法）。
- FR-19 固有の状態（最新版バイト数・論理削除／purge 期限・露出 3 トグル・Vault パス・本文ハッシュ）は
  **専用エンティティ `PrivateNote`（DocumentId を PK とする 1:1 台帳）**に持つ。
  `Document` へ列を足す案は、`Document` を読む全消費面（イベント・DTO・射影）の契約に波及するため採らない
  （`IADR-0253` 決定 4 が `DocumentShare` で採ったのと同じ分離）。
- **論理削除は台帳の `DeletedAt` / `PurgeAt` で表し、`Document.Status` は動かさない。**
  `archived` を流用すると「アーカイブ（非公開化）」と「削除（90 日後に消滅）」の 2 概念が
  1 状態に畳まれ、復元（restore）が `Archived → 再公開不可` のドメイン不変条件と衝突する。
- **`POST /documents`（管理者の一般経路）は `doc_scope=private-note` を 400 で拒否する。**
  一般経路で作ると台帳行が無い個人資料ができ、容量算入（FR-19）から漏れる。個人資料の作成経路は
  `/private-notes`（SC-19）と `/private-notes/sync`（Obsidian）に限る。既存文書の更新経路は変えない
  （`doc_scope` の生涯不変性は計画未裁定 —— #986 と同じく実装で決めず、現状挙動を維持する）。

### 決定 3: 同期トークンは**ブラウザセッションと別系統の不透明トークン**とし、DocumentService が自前で検証する

- 256bit 乱数 → **平文は発行応答で 1 回だけ**返す。保存は SHA-256 ハッシュのみ（漏えい時に原文へ戻せない）。
- 有効期限 30 日（決定 12）・**再発行は明示操作のみ**（決定 15。リフレッシュ端点そのものを作らない）・
  端末ごとの個別失効＋全端末一括失効（決定 13）。
- `/private-notes/sync/*` は JWT を要求せず Bearer 同期トークンで認証する（`ADR-0037` 課題 2:
  「同期トークンは BFF のブラウザセッションとは別系統であり、独立に設計する」）。検証失敗は
  欠落・不正・期限切れ・失効のいずれも**同じ 401**（存在と理由を漏らさない）。
- Keycloak のトークン基盤へ載せる案は不採用 —— 実環境（Keycloak 稼働・カスタムグラント）が要り
  （`IADR-0197` 決定 5 の「実環境が要るものは触らない」）、スコープを「当該利用者の個人資料のみ」へ
  絞る語彙も Keycloak 側に無い。トークンの意味論（何が読めるか）はどのみち DocumentService が持つ。

### 決定 4: 容量の算入は**台帳の形で**守り、100% 判定は「新規作成は上限を跨げない」と読む

- `usage = Σ PrivateNote.LatestBytes`（`DeletedAt` の有無を問わず。purge 済みは行ごと消える）。
  **版履歴のバイト数は台帳がそもそも持たない** —— 「版履歴は算入しない」（決定 16）を
  実装の規律ではなく**データの形**で守る。
- 計上単位は**本文の UTF-8 バイト数**（タイトル・属性は算入しない。計画は計上単位を定めておらず、
  本文が支配項であるため。乖離が出たら planning へ環流する）。
- 100% の挙動（決定 17）: **新規作成は `usage ≥ limit` または `usage + 新規サイズ > limit` で拒否**。
  既存資料の更新は容量を見ずに通す。計画の明文は「100% に達した場合は新規作成のみ拒否」だが、
  `ADR-0037` §結果は「**超過分は最新版の増分に限られる**」と述べており、新規作成が上限を跨げると
  この記述が破れるため、跨ぎ拒否を含めて読む（作業仕様書 §計画書との差異に記録。環流対象）。
- 80% / 95% 警告は**跨ぎ判定**（`前回値 < 閾値 ≦ 今回値`）＋台帳（quota 行）に発火記録を持ち、
  閾値を下回ったら解除する（`IADR-0215` 決定 5 ② の指示どおり）。

### 決定 5: 個人資料は**取り込み・索引・Wiki 同期へ一切流さない**（`DocumentUpdated` を発行しない）

- 露出 3 トグル（横断検索／グラフ／AI）は**既定 OFF**（FR-19）であり、ON を安全に絞る消費側
  （Retrieval / AiAnalysis の分岐評価）は `IADR-0253` 段 3 が未了である。**この状態で
  `DocumentUpdated` を発行すると、本文が Qdrant へ索引され、`confidentiality` ベースの現行スコープでは
  restricted クリアランスの他者に露出し得る**（漏れる向き）。
- したがって本段では `/private-notes` / `/private-notes/sync` の書き込みはイベントを発行しない。
  OFF は「索引に存在しない」ことで構造的に守られる。トグルは保存されるが、ON の消費側配線は
  段 3 完了後の別 issue とする（フォローアップ 2）。**乖離は deny 側（見えなさすぎる側）に閉じる。**
- 完全削除（purge）だけは `DocumentDeleted` を発行する（下流に残骸があれば掃除する向き。冪等）。

### 決定 6: 通知の発火検知は**データの在る側（DocumentService）**で行い、送出はポート経由で NotificationService へ渡す

- `IADR-0215` 決定 5 の表は ①-a/①-b/③ を「NotificationService のスケジューラ」としたが、
  判定に要るデータ（削除済み資料の期限・トークンの期限）は DocumentService の DB にあり、
  **DB per Service の下で NotificationService からは読めない**（同表の起草時点で NotificationService の
  backend は存在せず、置き場は未検証だった）。**原則（時間が契機ならバッチ・状態変化が契機なら
  イベント）は維持**し、バッチの居場所だけをデータ側へ移す。
- 送出は `IPrivateNoteNotifier` ポート。実装 `HttpPrivateNoteNotifier` は NotificationService の
  `POST /internal/notifications` へ JSON（subject / kind / occurredAt / count / thresholdPercent /
  deadline —— `NotificationPublisher.PublishAsync` と同形。**自由文フィールドは無い**）を送る。
  🔴 **受け口は未実装**であり platform 側（統括）に依頼する。受け口が入るまで送出は失敗し、
  **失敗はエラーログに記録して握り潰さない。ただし同期・削除・保存の成否には影響させない**
  （通知はアプリ内が主・本体操作の従属物ではない）。
- kind の文字列は NotificationService の `NotificationKinds` と一致させる（`private-note-purge-weekly` /
  `private-note-purge-imminent` / `private-note-purge-done` / `storage-quota-warning` /
  `sync-token-expiry`）。プロジェクト参照は張れない（platform → knowledge 禁止の裏返しで、
  knowledge → platform Services の参照も辺を増やす）ため**定数を複製し、値の一致をテストで固定**する。
- 週次通知（①-a）の平準化（`IADR-0215` 決定 4）は NotificationService 側の送信レート制御
  （`IADR-0267`）が担う。検知側は分割しない。

### 決定 7: push は `edits[]` を受けて **1 編集 = 1 版**、競合は 409 で返し自動解決しない

- プラグインはオフライン編集の各スナップショットを時系列順に送る（`ADR-0037` 決定 8:
  10 回編集 → 10 版。「1 編集」の刻みはプラグイン側のデバウンスに委ね、サーバは受けた edits の
  数だけ版を刻む —— フォローアップ 5 への本段の答え）。本文はいずれも既存の正準キー
  `documents/{id}/body.md` へ格納する（バケットのバージョニングが履歴を持つ。`ADR-0014` /
  既存 `/documents` 経路と同一の形）。
- 要求の `baseVersion`（クライアントが最後に見たサーバ版）が現在版と不一致なら **409** と
  サーバ側の現在版・更新時刻を返す。「ローカル採用／サーバ採用／両方残す」の選択は利用者に
  提示する（決定 7）—— ローカル採用＝pull 後に `baseVersion` を積み直して再 push、
  サーバ採用＝pull で上書き、両方残す＝プラグインが別ファイルとして新規 push。サーバに
  自動解決の分岐を作らない。
- 本文サイズ上限は FR-21 と同じ **1 MB / 413**（`DocumentBodyIntake.ExceedsLimit` を再利用。
  同期経路だけ上限が違うと「Obsidian では書けるが KB に入らない」資料ができる）。

## 理由

- **決定 1・2** は「認可・容量・版の真実源を 1 か所へ保つ」ため。個人資料の意味論が 2 サービスへ
  割れると、`ADR-0036` D-01（単一の評価モデル）と同じ失敗を容量・削除で再演する。
- **決定 3** は `ADR-0037` が「本機能の安全性は同期用資格情報のスコープに全面的に依存する」と
  名指しした点への直答。トークンの検証と資料スコープの適用を同一サービスに置くことで、
  「トークンは有効だがスコープは別サービス任せ」という分界の穴を作らない。
- **決定 5** は本 issue の直近裁定（`IADR-0253` の 2026-08-23 追記）が示した「複数キー混成で漏れる向きの
  乖離」を踏まえ、**露出の既定を構造（索引に入れない）で deny に倒す**もの。
- **決定 6** は `IADR-0215` の原則を保ちながら DB per Service と両立させる最小の再配置である。

## 結果

- 良い影響:
  - `ADR-0037` 決定 1〜20 のうちサーバ側で表現できる事項（3〜9・11〜20）が実装され、テストで固定される
  - 個人資料が検索・グラフ・AI・Wiki のどこにも露出しない状態が既定になる（deny 側）
  - `owner` 属性つきの実データが `/private-notes` 経路から生まれ、`IADR-0253` の分岐 ②（owner）が
    実データで検証可能になる
- 悪い影響・トレードオフ:
  - 🔴 **通知の受け口（platform 側）が入るまで、発火検知は「送れなかった」をログに残すだけ**である
    （アプリ内通知は届かない）。go-live 前に受け口が必須（統括への台帳）
  - 🔴 **完全削除後も MinIO 上の本文オブジェクトは残る**（`IObjectStorageClient` に削除が無い。
    既存 `/documents` DELETE も同様）。「復元できなくなる」は DB・API 面では真だがストレージ実体は
    残存する。**計画の「完全削除（即時）」の実効性に関わるため planning へ環流する**
  - 露出トグル ON はまだ何も起こさない（段 3 完了までの意図的な deny。SC-20 の表示は残件）
  - `edits[]` の丸ごと再送で版が重複し得る（プラグインは push 成功後に送信済み印を付ける前提。
    version の一意制約が同版の二重挿入は防ぐ）
- フォローアップ:
  1. NotificationService の受け口実装（platform・統括）と結線テスト
  2. 露出トグル消費側の配線（`IADR-0253` 段 3 完了後・別 issue）
  3. ストレージ実体の削除手段（`IObjectStorageClient` への削除追加は Platform.Shared.Infrastructure =
     担当領域外。planning 環流と併走）
  4. BFF 端点・SC-19/SC-20 画面・Obsidian プラグイン本体（別 issue）
  5. 100% 判定の跨ぎ拒否（決定 4）を planning へ確認する（要求文の字義と §結果 の含意の突き合わせ）

［2026-08-28 追記 / #451］**上のトレードオフ「完全削除後も MinIO 上の本文オブジェクトは残る」と
フォローアップ 3（ストレージ実体の削除手段）は解消した。** 計画 `ADR-0057` 決定 1 の裁定を受け、
`IObjectStorageClient` へ全バージョン削除（`DeleteAsync`）を足し、削除の入口 3 経路
（`/documents` DELETE・`/private-notes/purge`・90 日自動物理削除）を台帳からの逆引きで結線した。
**本文とその過去版が指していた本文は実体ごと消える。** 現行の正は
[IADR-0296](./IADR-0296_deletion-propagation-to-object-storage.md) であり、**本 ADR の本文は
当時の記録として書き換えない**（[IADR-0117](./IADR-0117_platform-shared-kernel-placement.md)
フォローアップ 3 と同じ扱い）。個人資料は変換経路を通らないため図表資産を持たず、
IADR-0296 決定 4 の限界（既存文書の資産を遡及付与しない）は本 ADR の射程には掛からない。

［2026-09-05 追記 / #1184］🔴 **決定 5（個人資料は `DocumentUpdated` を発行しない）は解除された。**
計画 `ADR-0061`（planning#492）が「露出 3 トグルのうち **1 つでも ON なら索引へ載せる**／
**3 つとも OFF なら載せない**／**ON → OFF は索引からの削除まで及ぶ**」と裁定したためで、
現行の正は後継の [IADR-0396](./IADR-0396_private-note-exposure-index-production.md) である
（`IADR-0270` 決定 5（後継 `IADR-0396`）と引くこと。**ID を付け替えない**）。
**本 ADR の本文は当時の記録として書き換えない**（2026-08-28 追記と同じ扱い）。

決定 5 が守っていた性質（**既定 OFF を「索引に存在しない」ことで構造的に守る**）は失われていない ——
発行の門と索引の門が同じ純関数 `DocumentExposure.IsIndexable` を評価し、3 つとも OFF の資料は
イベントそのものが出ない。**フォローアップ 2（露出トグル消費側の配線）も同 issue で閉じた。**
露出トグルの画面は SC-19 の一覧に既に在るため、**本解除は着地と同時に利用者の手に届く。**

## 関連

- Supersedes: なし
- Superseded by: なし（決定 5 のみ [IADR-0396](./IADR-0396_private-note-exposure-index-production.md) が後継。ADR 全体は現行である）
- 実装 issue: #451（本体）/ #989（`IADR-0253`）/ #986（Wiki 同期除外）/ #600（FR-22 通知）/
  #516（必須属性）/ #602（前提検証・CLOSED）
