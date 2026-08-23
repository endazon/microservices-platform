---
title: issue #451 個人資料（private-note）と Obsidian 双方向同期 — バックエンド中核（台帳・容量・版保持・同期トークン・同期 API・発火検知）
type: spec
status: done
related_ids: [FR-19, FR-20, FR-22, UC-11, SC-19, SC-20, ADR-0034, ADR-0036, ADR-0037, ADR-0046, ADR-0054]
author: claude
created: 2026-08-23
updated: 2026-08-23
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0037_obsidian-sync-method.md
  - planning:projects/microservices-platform/07_adr/ADR-0046_private-note-not-synced-to-wikijs.md
  - planning:projects/microservices-platform/07_adr/ADR-0054_doc-scope-attribute-for-private-note.md
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
---

# 仕様書: issue #451 — 個人資料と Obsidian 双方向同期のバックエンド中核

> 実装 issue #451（FR-19 ＋ FR-20。計画 `ADR-0037` 決定 1〜20 起点）の作業仕様書。
> 本 PR は **DocumentService（knowledge ユニット）へのバックエンド中核**を実装する。
> SC-19 / SC-20 の画面・BFF 端点・Obsidian プラグイン本体・通知の platform 側受け口は
> §対象範囲「対象外」に列挙し、理由と送り先を明記する。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-19（個人資料）・FR-20（Obsidian 双方向同期）。発火源として FR-22（通知）
- ユースケース（UC）: UC-11
- 画面（SC）: SC-19（個人資料管理）・SC-20（Obsidian 連携設定）— **バックエンド API のみ**
- 関連 ADR: `ADR-0037`（同期方式。決定 1〜20）/ `ADR-0046`（Wiki.js 同期除外・D-06）/
  `ADR-0054`（`doc_scope` 語彙）/ `ADR-0036`（所有者ベース裁量制御）/ `ADR-0034` 決定 8（リンク作成時検証）
- 実装 IADR: `IADR-0270`（本作業の実装判断）/ 前提: `IADR-0253`（認可スコープ選言）・
  `IADR-0215`・`IADR-0267`（通知）・`IADR-0269`（MCP サービスアカウント除外）・`IADR-0264`（本文受け入れ経路）

## 着手条件の確認（実測 2026-08-23）

1. **`ADR-0037` の「着手可否の注記」を原文で確認した。** ★［2026-08-15］の追記が
   「**留保は外れ、FR-19 / FR-20 は SC-19 の本文編集導線を除外せずに着手してよい**（導線そのものが
   存在しないため）」と明記している。**決定 1〜20 は 1 つも覆っていない。着手可である。**
2. 過去のブロッカーの現況:
   - `private-note` 語彙の未裁定（2026-08-21 コメント）→ **`ADR-0054`（Accepted / 2026-08-22）で解消**
     （キー `doc_scope`・値 `private-note` / `organization`・API リソース名 `/private-notes`）。
   - 認可スコープ契約の選言（`ADR-0046` D-06 部品 3）→ **`IADR-0253` / #989 が 2026-08-23 に着地**。
     `AccessScopeResponse.Branches`（名前つき分岐の選言）が契約に載り、評価器は分岐を組み立てる。
     WikiService は分岐評価へ移行済み。GraphService / RetrievalService / AiAnalysisService は未移行
     （`AllowedFilters` を読む。段 3 継続中）。
   - dotnet 不在（2026-08-16 コメント）→ 偽陰性と自己訂正済み。本セッションでも `dotnet 10.0.400` を実測。
3. **本 issue 本文の陳腐化**（2026-08-22 コメントの確認を追認）: 「`docs/specs/` を作成する」は
   `.ai-context/specs/` の誤り（資料再編済み）。「編集手段の前提検証が未了」は解消済み（#602 → `ADR-0046`）。

## 目的・背景

FR-19（個人資料のライフサイクル: 作成→編集→論理削除→復元／完全削除、容量、版保持）と
FR-20（Obsidian 双方向同期: 同期トークン・端末・同期プロトコル・監査）のサーバ側中核を、
`ADR-0037` 決定 1〜20 に忠実に実装する。通知（FR-22）は**発火の検知までを本作業**が持ち、
通知の実体（NotificationService）は実装済み・**受け口（ingress）は未実装**のため、
配線は HTTP ポートとして用意し platform 側の受け口実装は統括へ渡す（`IADR-0215` 決定 5 /
`IADR-0267` フォローアップ 1 の「発火の結線は #451」の引き受け）。

## 母集合（着手前の実測。`.claude/rules/traceability.repo.md` 規則 1〜10）

新規実装だが、**既に散在する `private-note` / `doc_scope` / 通知 kind と食い違う実装を作らない**ため、
語彙側の文字列で全リポジトリを走査した（`grep -rl`、**拡張子で絞らず**・obj/bin/node_modules/.git/
TestResults と submodule `src/ai-stock-trading` を除外）。

| 走査語（あり得る形を列挙） | 件数 | 主な所在と本作業への含意 |
| --- | --- | --- |
| `private-note` | 47 | WikiService 同期除外（#986・実装済み）／McpServer サービスアカウント除外（`IADR-0269`・実装済み）／NotificationService の通知 kind（#600・実装済み）／DashboardService KnowledgeHealth／frontend 通知文言／specs・IADR・docs |
| `private_note` / `privateNote` | 0 / 1 | 1 件は frontend 変数名。C# 側に別綴りは無い |
| `PrivateNote` | 22 | 上記の PascalCase 形。**DocumentService には 0 件＝ドメイン未実装**（issue コメント 2026-08-22 §5 の追認） |
| `doc_scope` / `DocScope` / `docScope` | 24 / 13 / 2 | WikiService（集合帰属判定・実装済み）／McpServer `DocumentScope`／DashboardService。**DocumentService の検証・付与は 0 件＝本作業が実装** |
| `private-notes`（API パス） | 0 | `/private-notes` は未実装。本作業が新設（`ADR-0054` 決定 4） |
| `Obsidian` / `obsidian` | 27 / 11 | GraphService `EdgeType`（wikilink 型付け・実装済み）／frontend 通知文言／specs・docs。**同期実装は 0 件** |
| `SyncToken` / `sync-token` / `sync_token` | 5 / 7 / 0 | すべて NotificationService の kind `sync-token-expiry` と文言・仕様書。**トークン実装は 0 件** |
| 陽性対照 `confidentiality` | 184 ファイル | 走査形が効いていることの対照 |

**除外とその理由**: `src/ai-stock-trading`（submodule・触らない領域）／`obj` `bin` `node_modules`
`TestResults`（生成物）／`CHANGELOG.md` は自動生成のため追随対象にしない。
`src/*/frontend/**`（`private-note` 3 件・通知文言）は**別エージェントが編集中の禁止領域**であり、
本作業は値の綴り（`private-note-purge-*` 等）を**既存の kind に一致させる**ことで整合を保つ
（frontend 側の変更は不要）。

**含意**: 発火検知が送る通知 kind は NotificationService の
`NotificationKinds`（`private-note-purge-weekly` / `-imminent` / `-done` / `storage-quota-warning` /
`sync-token-expiry`）と**文字列一致**させる（プロジェクト参照は platform → knowledge 禁止の逆向きでも
持ち込まず、定数を DocumentService 側に複製し、値の一致をテストで固定する）。

## 対象範囲

- **対象（本 PR）** — すべて DocumentService（`src/knowledge/backend/Services/DocumentService/`）:
  1. `doc_scope` の値域検証と、`/documents` 作成経路での `private-note` 拒否（`IADR-0270` 決定 2）
  2. 個人資料台帳（`PrivateNote`）・容量（`PrivateNoteQuota`）・端末／トークン（`SyncDevice`）の
     エンティティ＋EF マイグレーション
  3. `/private-notes` 端点（一覧＋容量表示・作成・論理削除・復元・完全削除〔単票／一括〕・露出 3 トグル）
  4. `/private-notes/devices` 端点（トークン発行・再発行・一覧・個別失効・一括失効）
  5. `/private-notes/sync/*` 端点（マニフェスト・push〔編集回数どおりの版〕・pull・削除。同期トークン認証）
  6. 容量規則（算入＝最新版＋論理削除済み／版履歴は非算入。80/95 警告・100% 新規のみ拒否）
  7. 版保持（1 資料あたり直近 50 版**かつ** 90 日。両方を満たさなくなった版から古い順に物理削除）
  8. 90 日経過の自動物理削除・通知発火検知（①-a 週次／①-b 7 日前／①-c 事後／② 容量閾値／③ トークン期限）
  9. 同期・完全削除の監査ログ（`IAuditLogger`。誰が・いつ・何件。タイトル不記載）
- **対象外（理由と送り先）**:
  - **SC-19 / SC-20 の画面**・BFF 端点（`/bff/private-notes*`）— `src/*/frontend/**` は並行作業の禁止領域。
    BFF は契約確定後の別 PR（残件として報告）
  - **Obsidian プラグイン本体・配布**（`ADR-0037` 決定 1）— 本リポジトリに TS ビルド器を持つ置き場が無く、
    frontend 系ツールチェーンは禁止領域。プラグインが呼ぶサーバ API 契約を本 PR で確定し、実装は残件
  - **NotificationService の受け口（ingress）** — `src/platform/backend/Services/**` は禁止領域。
    本 PR は knowledge 側の検知＋HTTP ポートまで（統括への台帳で依頼）
  - **退職時 30 日の管理者閲覧**（SC-19/SC-20 固定文言）・**アカウント無効化時のトークン失効**
    （`ADR-0037` フォローアップ 3）— AuthorizationService / 人事連携の領域。報告書「別担当へ渡す変更」
  - **権限外文書へのリンク作成時検証**（`ADR-0034` 決定 8）— リンク抽出は FR-21 の取り込み経路
    （IngestionService / GraphService）に依存。個人資料の本文はまだ取り込みへ流さない（下記）ため
    現時点で検証対象の経路が無い。残件として報告
  - **露出 3 トグルの消費側**（検索・グラフ・AI が ON を読む配線）— `IADR-0253` 段 3（Retrieval /
    AiAnalysis の分岐評価移行）が未了であり、ON にしても安全に絞れる基盤が無い。本 PR は
    **トグルを保存し、私的資料を取り込み・索引へ一切流さない**（deny 側に倒す。`IADR-0270` 決定 5）
  - **既存 2,368 件への `organization` 遡及付与** — 計画が遡及付与しないと裁定済み（`ADR-0054` §結果）
  - **`doc_scope` の「必須」強制（全文書）** — 必須属性の実効化は #516 の系譜（`IADR-0199`）。
    本 PR は値域検証＋ `/private-notes` 経路の強制付与まで

## 設計（要点。判断の記録は `IADR-0270`）

- 個人資料 = `Document`（`doc_scope=private-note` / `owner=本人` / `confidentiality` 既定 `restricted`）
  ＋ **台帳 `PrivateNote`**（バイト数・削除状態・purge 期限・露出トグル・Vault パス・本文ハッシュ）。
- 論理削除は台帳の `DeletedAt` / `PurgeAt`（＋90 日）で表し、`Document.Status` は変えない。
  完全削除は `Document` 行の物理削除（版・共有はカスケード）＋台帳行の削除。
- 容量: `usage = Σ LatestBytes（削除済み含む・purge 済み除く）`。版履歴は算入しない（構造上、
  台帳が最新版のバイト数しか持たないため算入しようがない＝規則を型で守る）。
- 同期トークン: 256bit 乱数。**平文は発行応答で 1 回だけ返し、保存は SHA-256 ハッシュのみ**。
  有効期限 30 日・手動再発行のみ（リフレッシュ端点を持たない）・個別／一括失効。
- `/private-notes/sync/*` は JWT ではなく同期トークン（Bearer）で認証する（`ADR-0037` 課題 2:
  ブラウザセッションと別系統）。失敗はすべて 401（存在・理由を漏らさない）。
- push は `edits[]`（時系列順）を受け、**1 編集 = 1 版**として版を刻む（決定 8。10 編集→10 版）。
  競合は `baseVersion` 不一致の 409 で返し、自動解決しない（決定 7。選択は利用者＝プラグイン側 UI）。
- 通知は `IPrivateNoteNotifier` ポート経由。実装は NotificationService への HTTP POST
  （`/internal/notifications`。**受け口は未実装＝統括への依頼**）。送出失敗は記録して握り潰さず、
  同期・削除の成否には影響させない。

## 受け入れ基準（計画の受け入れ基準からの写像。本 PR で満たす分。2026-08-23 全件テストで確認）

- [x] 容量: 同一資料を繰り返し編集しても使用量は最新版 1 件分から増えない（版履歴は非算入）
- [x] 容量: 論理削除で使用量は減らず、完全削除した時点で当該資料の分だけ減る
- [x] 容量: 80% / 95% の跨ぎで各 1 回警告が発火し、閾値を下回ると再武装する
- [x] 容量: 100% 到達で新規作成のみ拒否・既存資料の更新（同期 push の更新）は成功する
- [x] 容量: 100% で拒否された状態から完全削除で 100% を下回ると新規作成が再び成功する
- [x] 版保持: 直近 50 版以内は 90 日超でも残り、90 日以内は 50 版超でも残る。両方を外れた版だけが古い順に消える
- [x] 版: 1 回の同期に 10 編集を載せると 10 版が刻まれる
- [x] トークン: 有効期限 30 日・期限切れは 401・個別失効・一括失効が効く。自動リフレッシュ経路が存在しない
- [x] トークン: 期限 7 日前の検知が対象トークンを 1 回だけ拾う（当日の追加通知は無い）
- [x] 同期スコープ: 同期トークンで他者の資料・組織文書・自分に共有された他者の資料が一切取得できない（陽性対照: 自分の個人資料は取得できる）
- [x] 削除: Obsidian 側の削除はサーバ側で論理削除に留まり、90 日経過で自動物理削除される
- [x] 通知検知: ①-a 週次（件数＋最短期限）／①-b 7 日前／①-c 事後／② 80/95／③ 期限 7 日前が、
      件数・閾値・期限**のみ**を運ぶ（タイトル・本文のフィールドが型として存在しない）
- [x] 監査: 同期・完全削除の実行記録（誰が・いつ・何件）が残り、タイトル・内容を含まない
- [x] 既定値: 同期経由で新規作成された資料は `doc_scope=private-note`・`confidentiality=restricted`・
      3 トグル OFF・公開範囲は所有者のみ（共有 0 件）で作られる
- [x] `doc_scope`: 未知値は 400。**判定は集合帰属**（`== private-note`）であり、属性を持たない既存
      組織文書の扱いが変わらない（陽性対照テスト）

## 変異試験の記録（2026-08-23。すべて元へ戻し、最終実行は 170/170 緑）

| # | 変異 | 検出 |
| --- | --- | --- |
| 1 | `IsPrivateNote` を否定（`!= organization`）へ | **50 件 fail**（陽性対照＋既存の作成系が一斉に落ちる） |
| 2 | 容量算入から論理削除済みを除外 | 1 件 fail（`論理削除では使用量が減らず完全削除で減る`） |
| 3 | 版保持の AND → OR | 1 件 fail（`版履歴は…両方を外れた版だけが消える`） |
| 4 | 新規作成の容量拒否を常に false | 2 件 fail（満杯時・跨ぎ拒否） |
| 5 | トークンの期限判定を除去 | 1 件 fail（`期限切れトークンは401になる`） |
| 6 | 同期経路の所有者スコープを除去 | 1 件 fail（`他者の資料は…到達できない`） |
| 7 | 95% 警告の重複抑止を除去 | 🔴 **初回は生き残った**（95% 以上に留まる連続更新が無かった）。テストへ 97% の追加更新を足して再実測 → 1 件 fail。強化後のテストで検出を確認 |

戻し忘れの確認: `git diff` の対象が本作業の意図した差分のみであること・全 170 件緑を最終確認した。

**本 PR では満たさない（理由つき）**: 画面表示系（SC-19/SC-20 の表示文言・確認ダイアログ）／
アプリ内通知の実受信（受け口未実装）／検索・グラフ・AI への露出 ON（消費側未移行）／
プラグイン実機の同期 E2E（実環境なし・そもそも実行していない）。

## テスト方針

- 既存の `DocumentService.Api.Tests`（InMemory ＋ TestAuthHandler ＋ RecordingObjectStorageClient）へ
  結合テストを追加する。新規テストプロジェクトは作らない。
- 🔴 否定形（拒否・不可視・非算入）は必ず陽性対照（許可・可視・算入）と対で置く。
- 通知はテスト用の記録 Notifier（`IPrivateNoteNotifier` 差し替え）で発火の有無・回数・ペイロードを固定する。
- 時刻依存（30 日期限・90 日 purge・7 日前・週次）はメンテナンスサービスへ `now` を引数で渡して検証する。
- 変異試験: 集合帰属→否定への書き換え、算入規則の反転、AND→OR（版保持）、期限比較の向き等を
  壊して検出テスト数を実測し、元へ戻して残渣 0 を確認する。

## 計画書との差異

- `IADR-0215` 決定 5 は ①-a/①-b/③ のバッチを「NotificationService のスケジューラ」と書いたが、
  判定に要るデータ（削除済み資料・トークン）は DocumentService の DB にあり、DB per Service の下で
  越境読みできない。**検知はデータの在る側（DocumentService）で行い、送出のみ NotificationService へ
  渡す**形へ改める（`IADR-0270` 決定 6。原則「時間が契機ならバッチ」は維持）。
- 100% 判定の境界（新規作成が上限を跨ぐ場合の扱い）は計画に明文が無く、`ADR-0037` §結果の
  「超過分は最新版の増分に限られる」から**新規作成は上限を跨げない**と導出した（`IADR-0270` 決定 4）。
- `doc_scope` の生涯不変性は計画が未言及（#986 の申し送りどおり実装で決めず、`/documents` 経路の
  現状挙動は変えない。`/private-notes` 経路は常に `private-note` を付与する）。

## 未決事項（残件として報告書へ）

1. NotificationService の受け口（`/internal/notifications`）— platform 側・統括へ依頼
2. `IObjectStorageClient` に削除が無く、完全削除後も MinIO 上に本文オブジェクトが残る
   （既存の `/documents` DELETE も同様）。**「完全削除（復元不可）」の実効性に関わる**ため要裁定
3. BFF 端点・SC-19/SC-20 画面・Obsidian プラグイン本体
4. 露出トグル ON の消費側配線（`IADR-0253` 段 3 完了後）
5. 退職時規則・アカウント無効化時のトークン失効（platform 側）
