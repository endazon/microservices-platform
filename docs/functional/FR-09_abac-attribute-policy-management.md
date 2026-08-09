---
title: 文書属性・タグ／ABAC ポリシー管理 機能仕様書
type: functional-spec
status: draft
related_ids:
  - FR-09
  - SC-05
  - SC-09
  - UC-05
  - IADR-0152
  - IADR-0153
author: claude
created: 2026-07-02
updated: 2026-08-09
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (FR-09)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0004_authz-abac.md"
---

# 機能仕様書: 文書属性・タグ／ABAC ポリシー管理

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-09
- ユースケース（UC）: UC-05（ABAC 権限を管理する）
- 計画書リンク: `02_requirements/01_requirements.md`、`07_adr/ADR-0004`、`06_technical/07_abac-attribute-model.md`

## 概要

システム管理者が、文書に付与する**属性辞書（取りうる値の集合）**と **ABAC ポリシー**
（利用者属性×文書属性→許可アクション）を管理（登録・取得・更新・削除・有効/無効切替）する。
保存前に入力と辞書整合を検証し、矛盾するポリシー・不正な属性値を弾く（UC-05 例外フロー）。

## 機能詳細

| 項目 | 内容 |
| --- | --- |
| 入力 | 属性辞書（key/label/allowedValues/required/scope）, ポリシー（name/action/userConditions/documentConditions）, 文書属性（key→value） |
| 処理 | 属性辞書・ポリシーの CRUD＋有効/無効切替。保存前に `AbacValidation` で入力検証・辞書整合検証。文書属性は `POST /authz/attributes/validate` で辞書整合を検証（副作用なし） |
| 出力 | 管理対象エンティティ（JSON）／検証結果 `{ valid, errors }`／エラー時は RFC7807 `ValidationProblem`（400） |
| 業務ルール | ①属性辞書のキーは同一スコープ内で一意。②Key/Scope は不変。③許可値は非空・重複不可。④ポリシーの action は read/analyze/manage。⑤条件・文書属性は辞書に定義済みキーのみ許可値整合を検証し、未定義キーは許容（段階導入）。⑥必須属性（Required）は文書属性検証で充足を強制。 |

## 主要コンポーネント

- `AttributeDefinition` / `AbacPolicy`（Domain）: `Update` / `SetActive` と `UpdatedAt` を持つ。Key/Scope 不変。
- `AbacValidation`（Services）: 属性辞書・ポリシー・文書属性の検証。エラーを文字列リストで返す。
- `AuthzEndpoints`（Endpoints）: `/authz/policies` `/authz/attributes` の CRUD、`/authz/policies/{id}/active`、
  `/authz/attributes/validate`。検証失敗は `Results.ValidationProblem`。
- `AuthorizationDbContext`: `(Key, Scope)` 一意インデックス。

## 例外・代替フロー

- 不正入力（アクション/スコープ不正・許可値空/重複・キー重複・辞書外の条件値）→ 400 `ValidationProblem`＋errors。
- 存在しない ID の取得/更新/削除/切替 → 404 NotFound。
- 文書属性検証で必須欠落・許可値外 → 200 `{ valid:false, errors:[...] }`（保存側が結果を用いて判断）。

## 受け入れ基準との対応

- 管理者が属性・タグ・ポリシーを設定できる → CRUD＋有効/無効切替 API。
- 矛盾するポリシーは保存前に検証しエラー → `AbacValidation` ＋ `ValidationProblem`（UC-05 例外フロー）。
- 各サービスを個別デプロイ・ロールバック可能 → DocumentService と疎結合（検証 API 提供に留める。IADR-0006）。

## ポリシーの dry-run 検証（#535 / 裁定 Q23）

**［2026-08-09 追記 / #535］保存せず検証だけ行う口を新設した**（`POST /authz/policies/validate`。
BFF は `/bff/admin/authz/policies/validate`）。

- **判定ロジックは増やしていない。** 既存の `AbacValidation.ValidatePolicy` をそのまま使う。
- ★ **登録・更新・dry-run の 3 経路が同じ検証関数（`ValidatePolicyAsync`）を呼ぶ。**
  従前は同じ 3 行が `POST` と `PUT` に重複しており、dry-run を 3 つ目の複製にすると
  **将来どれか 1 つだけを直したときに黙ってズレる**。計画は「**信頼できない検証ボタンは無いより悪い**
  （押して安心してから壊す）」と名指しで禁じており、**その一致をコメントではなく構造で守る**。
- **矛盾があっても 200 である**（`{ valid: false, errors }`）。検証した結果として矛盾が見つかったことは
  要求の失敗ではない。**保存は従来どおり 400 ＋ RFC7807**（既存の契約は変えない）。
- **要求型は `CreatePolicyRequest` を再利用する。** 画面が保存用と検証用で 2 つの組み立てを持つと、
  そこがズレる余地になる（ズレると「検証は通ったのに保存で矛盾」に戻る）。
- **認可はシステム管理者限定**（`admin` グループ。[[IADR-0040]] 決定 2）。検証も管理操作である。

## タグ辞書（#634 / [[IADR-0152]]）

**［2026-08-09 追記 / #634］タグ辞書を新設した。** SC-09 が 2026-08-02 に確定した規則
（参照が 1 件でもあるタグは削除拒否・改名は既存文書へ追随・削除前に使用件数を示す）は
**すべて契約側の機能**であり、辞書が無いと 1 つも満たせなかった。
**`AttributeDefinition.AllowedValues` は ABAC 属性の許可値であってタグ辞書ではない**（計画も同じ切り分けをしている）。

- **所有は DocumentService**（knowledge ユニット）である（[[IADR-0152]] 決定 1）。
  使用件数が文書の局所クエリになるため——サービスを跨ぐと、削除拒否の判定のたびに同期呼び出しが要り、
  数え落としが「消してはいけないタグを消せる」事故になる。
- **読み取りは管理者・運用者**（`GET /tags`。SC-05 の裁定 Q18）、**追加はシステム管理者**（`POST /tags`。SC-09）。
- **使用件数は現行版の文書の件数である**（[[IADR-0152]] 決定 2）。
  **版履歴は数えない**——append-only で付け替えられず、数えると一度でも使われたタグを永久に削除できなくなり、
  SC-09 の「使用件数 0 件のときに限り削除できる」が空文になる。
  **アーカイブ済みの文書は数える**——アーカイブ済みでもタグは付け替えられる（実測）ため、管理者は行動できる。
- **読み取り口は `/bff/attribute-values` の 1 系統である**（ADR-0043 決定 4）。
  管理者スコープは `dictionary` フィールドとして足しており、**一般利用者の応答形は #540 から変わらない**。
- **一般利用者へ辞書を返さない**（ADR-0043 決定 1）。一般利用者の候補は Qdrant の facet 経由のままである
  （[[IADR-0152]] 決定 4。**辞書は管理面、facet は利用者面**の 2 経路を意図的に保つ）。

### 改名・削除（#635 / [[IADR-0153]]）

**［2026-08-09 追記 / #635］改名・削除を実装し、#634 で暫定だった数え方を確定させた。**
#634 は使用件数を**表示名の一致**で数えていた（文書がまだ表示名を複写していたため）。**識別子の一致に置き換えたので、改名しても件数は変わらない。**

- **正本は識別子、派生は表示名である**（[[IADR-0153]] 決定 1・2。計画確定「辺は型の識別子を参照して保持し、表示名を複写しない」）。
  `Document.Tags` / `DocumentVersion.Tags` は `List<Guid>`、DTO・イベント・Qdrant ペイロード・Wiki.js は表示名のまま。
  **変換点は `TagResolver` の 1 箇所に閉じている**——散らすと「片方だけ識別子のまま漏れる」型の事故が起きる。
  **契約と下流サービスは 1 つも変わっていない。**
- **改名**（`PUT /tags/{id}`。システム管理者限定）: 表示名を差し替える。**文書は 1 件も書き換えない**——追随は解決だけで起こる。
  **版も増えない**（[[IADR-0153]] 決定 3。改名は文書の内容変更ではない）。
  **射影（Qdrant / Wiki.js）は表示名を焼き込んだ複写なので、`DocumentUpdated` を再発行して作り直す**。
  **再発行するのはそのタグを使っている文書だけ**である（辞書の 1 語の変更で索引全体を作り直さない）。
  応答の `republishedDocuments` に件数を添える——反映は非同期なので「0 件だった」と「まだ届いていない」を切り分けられる。
- **過去版も新しい名前で表示される**（同 決定 4）。版履歴も識別子を持つため、改名は履歴の表示にも一様に効く。
- **削除**（`DELETE /tags/{id}`。システム管理者限定）: **使用件数 0 件のときだけ許す**（同 決定 6）。
  1 件以上なら**件数を添えて 409** を返す（SC-09「削除前に使用件数を示す」。数だけでも管理者は行動できる）。
  **削除の判定と一覧の使用件数は同じ母集合で数える**——食い違うと管理者は辞書を信用できなくなる。
- **画面からの手入力は自動登録しない**（同 決定 5・planning#304）。辞書に無い名前は **400** で拒否する
  （SC-05「既定タグ辞書に整合」）。**黙って落とさない**——落とすと「保存できたのにタグが付いていない」という説明のつかない結果になる。
- **既存データは移行する**（`20260809123339_MigrateTagsToIdentifiers`）。表示名を辞書へ登録してから紐づけ直す。
  詳細と検証は `../data/document-and-version.md` を参照。

**［2026-08-09 / #640］この残件は解消した。** `/bff/tags` を新設し、追加（`POST`）・改名（`PUT`）・
削除（`DELETE`）を BFF 経由で操作できるようにした。**読み取りは管理者・運用者、書き込みは
システム管理者限定**で、後段 `/tags` にも同じ制限があり両層で効く（[[IADR-0044]] の多層防御）。
**SC-09 の画面にタグ辞書の区画を足した**（[[IADR-0129]] 決定 1 の理由 B は解除済み）。
**削除拒否の 409 は `usageCount` を添えて透過する** —— SC-09 の「削除前に使用件数を示す」を
画面が満たすためである。

## 関連仕様

- 作業仕様書: `../specs/20260702_FR-09_abac-attribute-policy-management.md`
- テスト仕様書: `../tests/FR-09_abac-attribute-policy-management.md`
- 実装 ADR: `../adr/IADR-0006_abac-management-validation.md`
- 関連: `./FR-05_abac-access-control.md`（スコープ評価は本辞書・ポリシーを消費する）

## 未決事項

- 利用者スコープ属性の取得経路（Keycloak クレームマッピング）の確定。
- 辞書整合を厳格化（未定義キー禁止）へ移行する時期。
