---
title: 作業仕様書 — 緊急アクセス（Break-glass）の後段（DocumentService＋契約）。本人同意型の管理者の閲覧（#2046 の 1 本目）
type: spec
status: done
related_ids: [FR-19, UC-11, SC-19, SC-03, ADR-0036, ADR-0121, ADR-0142, ADR-0034, ADR-0088, ADR-0101, IADR-0610]
author: claude
created: 2026-10-23
updated: 2026-10-23
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md（D-08 管理者の可視性・緊急アクセス）
  - planning:projects/microservices-platform/06_technical/08_data-egress-policy.md（§経路 1 緊急アクセス）
  - planning:projects/microservices-platform/02_requirements/01_requirements.md（FR-19 の受け入れ基準「緊急アクセス」）
  - planning:projects/microservices-platform/03_usecases/01_usecases.md（UC-11 例外フロー）
  - planning:projects/microservices-platform/05_screens/01_screens.md（SC-19 緊急アクセスの導線・SC-03 の個人資料の表示）
  - planning#841（2026-10-23T14:21Z の裁定コメント。計画書への反映は計画側が行う）
---

# 仕様書: 緊急アクセス（Break-glass）の後段（#2046 の 1 本目）

## 起点となる計画書（トレーサビリティ）

- 要求: FR-19（個人資料。受け入れ基準「緊急アクセスは所有者本人のみが理由を入力して発動でき、発動時刻から 15 分で自動失効する。
  有効性はサーバ側の発動記録で判定。第三者の発動経路が無い。監査ログは 3 年保持で本文を含まない」）
- ユースケース: UC-11（例外フロー「緊急アクセスは所有者本人のみが理由を入力して発動でき、15 分で自動失効する」）
- 画面: SC-19（発動の導線。3 本目）、SC-03（管理者の閲覧の導線。裁定 2。3 本目）
- 計画 ADR: ADR-0036 D-08（統制の表）、ADR-0121 決定 1（本人は平時から自分の資料をすべて読める）、ADR-0142（論理削除の間は共有先も開けない）、
  ADR-0034 決定 9（機械の主体は個人資料を読まない）、ADR-0088（ロールは本文の主張ではなく名簿で確かめる）、ADR-0101（共有先の写しは所有者へ）
- 裁定: **planning#841（2026-10-23）**。計画書の現行の記述（D-08・SC-19・08_data-egress-policy）は受益者・対象の単位・終了・操作種別・
  監査ログの閲覧者を定めていない。**本仕様書と IADR-0610 は裁定を正とする**（計画書への反映は計画側が行う）。
- 実装 ADR: IADR-0610（本件の決定）
- issue: #2046

## 裁定（planning#841）の要点

1. 本人同意型の**管理者の閲覧**。権限は閲覧だけ（編集・削除・共有の変更・ダウンロードは増えない）。本人のための発動は置かない。
2. 閲覧できるのは**本人が発動時に指名した `platform-admin` の 1 人**。管理者の導線は SC-03（新画面なし）。
3. **資料 1 件ごと**に発動する。
4. 本人は **15 分より前に終えられる**。終了日時は本人の終了か発動＋15 分の早いほう。
5. 発動・終了に加え、**有効中の管理者の閲覧も 1 件ずつ記録**する（本文は記録しない・保持 3 年）。
6. 監査ログを読めるのは `platform-admin` と本人（本人は自分の資料の分だけ）。

変えない統制: 本人だけが発動・理由は必須・15 分で失効し延長なし・サーバ側の発動記録で判定。

## 分割計画（3 本）

| 本 | 範囲 | 依存 |
| --- | --- | --- |
| **1（本 PR）** | DocumentService: 発動記録（表・Migration・3 年の削除）・発動／本人の終了／状態の端点・閲覧の判定（要求ごとにサーバ側の記録）・指名された管理者に対象 1 件の閲覧だけを許す認可・監査ログ（発動・終了・閲覧 1 件ずつ）・監査ログの読み取り。Knowledge.Contracts の契約。IADR-0610 | なし |
| 2 | BFF: 発動・終了・状態の中継（**所有者本人のセッションのみ**）、監査ログの読み取りの中継、SC-03 の詳細・本文が後段の判定（`GetDocument` / `GetDocumentContent`）をそのまま通すことの確認（BFF 側で個人資料を先に弾いていないか） | 1 |
| 3 | 画面: SC-19 の導線（対象 1 件の選択・理由必須・指名先の選択・「この操作は監査ログに記録されます」の確認ダイアログ・残り時間・終了）、SC-03 の管理者の閲覧（新画面なし）、監査ログの表示 | 2 |

2・3 本目は本 PR に含めない。**本 PR だけでは利用者の操作の経路は無い**（後段の口だけ。BFF が中継しない限り画面からは届かない）。

## 受け入れ基準（本 PR）

- [x] AC1 発動は所有者本人だけ。管理者・他人・機械の主体は他人の資料に発動できない（404。存在ごと秘匿）
- [x] AC2 理由は必須（空・空白だけは 400）・500 文字以内
- [x] AC3 指名先は `platform-admin` の 1 人（利用者名簿で確かめる）。空・本人・サービスアカウント・管理者でない人は 400、確かめられなければ 503（発動しない）
- [x] AC4 有効時間は発動から 15 分固定（延長なし。閲覧を続けても延びない）。15 分ちょうどで失効
- [x] AC5 有効性は**要求ごとにサーバ側の発動記録**で判定（時刻は注入した時計）
- [x] AC6 本人は 15 分より前に終えられる。終了日時は早いほう。終えた後は閲覧できない。指名された管理者を含む第三者は終えられない
- [x] AC7 指名された管理者は**有効な間だけ・対象の 1 件だけ・閲覧だけ**できる（REST `GET /documents/{id}`・gRPC `GetDocument`・`GetDocumentContent`）
- [x] AC8 指名外の管理者・失効後・終了後・別の資料・論理削除中・いま管理者でない／確かめられない・機械の主体は閲覧できない（404 / found=false）
- [x] AC9 閲覧以外（編集・メタデータ・公開・アーカイブ・削除・本文の投入・共有の追加と取り消し・論理削除・露出・復元・完全削除）は有効中の指名された管理者にも許さない。資料は変わらない
- [x] AC10 一覧・版の一覧・特定版・MCP のツール実行の経路（`GetAsync`）では緊急アクセスで開かない
- [x] AC11 監査ログに発動・終了・閲覧（1 件ずつ・属性か本文か）を記録する。記録項目は発動日時・発動者・対象資料の識別子・理由・操作種別・終了日時。本文・題名を記録しない
- [x] AC12 監査ログを読めるのは `platform-admin`（全件）と本人（自分の資料の行だけ）。機械の主体は 403
- [x] AC13 監査ログと発動記録は 3 年で定期処理が削除する（3 年未満は残る）
- [x] AC14 Migration は表 2 つの追加だけ（既存の表を変えない）
- [x] AC15 指名先は正規形（前後の空白を落として小文字）で保存し、閲覧の照合も同じ関数で大小を無視して比べる。別人は拒否する（AI レビュー 🟡 対応）
- [x] AC16 所有者が変わった後（文書の `owner`・台帳の所有者のどちらか）は古い発動で閲覧できず、新しい所有者も古い発動を終えられず状態にも出ない（AI レビュー 🟡 対応）

## 設計

IADR-0610 を正とする。要点:

- 表: `BreakGlassGrants`（発動記録）・`BreakGlassAuditEntries`（監査ログ）。FK なし（資料が消えても保持期間残る）。
- 判定点: `BreakGlassViewGate`（`DocumentReadUseCase.GetForViewAsync` だけが呼ぶ）。通常の判定（`DocumentReadAccess`）で読めないときだけ問う。
  条件は「名前の分かる人・個人資料・その主体を指名した有効な発動記録・発動者が現在の所有者・論理削除されていない・いま platform-admin（名簿）」の AND。
  許すときは閲覧の監査ログを**保存してから**返す。
- 端点: `POST/GET /private-notes/{id}/break-glass`、`POST /private-notes/{id}/break-glass/end`、`GET /break-glass/audit`。
- 時計: `TimeProvider`（DI。試験は `FakeTimeProvider`）。

## 母集合の取り方（規則 9・10）

- 閲覧の口の母集合: `DocumentReadUseCase` の呼び出し元を `grep -rn "reads\.\|DocumentReadUseCase"` で引いた —— REST の 5 口（一覧・ページ・個別・版の一覧・特定版）、
  gRPC `DocumentRead` の 5 rpc（一覧・個別・版の一覧・特定版・本文）、MCP のツール実行（`get_document`・`list_documents`）。
  **緊急アクセスを通すのは REST 個別・gRPC 個別・gRPC 本文の 3 つだけ**。MCP は `GetAsync` のままで通さない（試験で固定）。
- 書き込みの口の母集合: `Map(Put|Post|Delete|Patch)` を `Features/Documents` と `Features/PrivateNotes` で引いた 12 口。すべて所有者または管理の口の判定を通り、
  読み取りの判定を見ない（試験で 12 口とも固定）。タグの反映（gRPC `DocumentTagWrite`）は GraphService の承認の経路で、管理者の枝は取り込み文書だけ
  （既存の `AdminWritePrivateNoteScopeTests` 系が固定）なので本件の対象外。
- 直接 `new DocumentReadUseCase(...)` する箇所: 2 つ（`DocumentListPagingTests`・`DocumentListKeysetPostgresTests`）。コンストラクタの追加に追随した。

## 検証

- `dotnet build` / `dotnet test`（DocumentService.Tests 全件）/ `dotnet format --verify-no-changes`
- 変異: 指名・失効・早期終了・名簿の確認・論理削除・`GetAsync` 経由・本人以外の発動・監査ログの絞り・保存前の返却の各変異で赤になること（PR 本文に表）
- Migration: `dotnet ef migrations add` の生成物（表 2・索引 4）を静的に確認。実 DB への適用は配備時（PR 本文の手順）

## 残余リスク・未決

- 計画書（D-08・SC-19・08_data-egress-policy・UC-11）への裁定の反映は計画側（planning#841）。
- BFF・画面が無い間は利用者の経路が無い（2・3 本目）。
- 同じ資料への並行の発動は検査と保存の間で 2 件になり得る（どちらも本人の操作。閲覧はどちらかの有効な記録で判定され、監査ログはそれぞれに残る）。
