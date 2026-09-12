---
title: IADR-0445 公開範囲の指定先は共有台帳をそのまま BFF へ出し、画面は個人指定だけを描く — 利用者検索は「利用者名・表示名・有効状態」に閉じた認証必須の読み口を新設する
type: impl-adr
status: Accepted
related_ids: [FR-19, UC-11, SC-19, ADR-0036, ADR-0098, IADR-0131, IADR-0139, IADR-0253, IADR-0301, IADR-0401, IADR-0444, IADR-0445]
author: Claude
created: 2026-09-12
updated: 2026-09-12
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md
  - planning:projects/microservices-platform/07_adr/ADR-0098_share-target-is-keycloak-group-and-ui-waits-for-binding.md
  - planning:projects/microservices-platform/10_feedback/20260912_share-target-unit-and-sync-history.md
related_specs:
  - ../specs/20260912_1445-1446_share-targets-and-sync-history.md
---

# IADR-0445: 公開範囲の指定先は共有台帳をそのまま BFF へ出し、画面は個人指定だけを描く

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。
> 計画に影響する決定は計画側へ環流する（`/plan-feedback`）。

- 状態: Accepted
- 日付: 2026-09-12
- 決定者: Claude（実装）

## 起点・関連

- 関連する計画書 ID: FR-19・UC-11・SC-19（主要素 3 公開範囲の変更ダイアログ）／ADR-0098（決定 1 共有先の名前空間・表示名を出し
  識別子は出さない／決定 2 グループ指定の UI は `${current_groups}` の配備まで描かない・台帳は受け付けたまま／決定 3 グループ木は
  管理者が Keycloak 側で作る）／ADR-0036（D-06。§未確定事項 5 は ADR-0098 で解消）／planning#618 の裁定記録
- 関連する実装 ADR: [IADR-0444](IADR-0444_private-note-contract-visibility-sync-state-and-conflict-ledger.md)（決定 6 で指定先を
  裁定待ちとして切り離した。本決定はその残り）／[IADR-0253](IADR-0253_authz-scope-disjunction-contract.md)（段 4 共有台帳。
  **配線は本決定でも行わない**）／[IADR-0301](IADR-0301_user-admin-identity-provider-delegation.md)（`IIdentityAdminClient` の抽象。
  本決定はそこへ検索を足す）／[IADR-0401](IADR-0401_user-directory-grpc-narrow-surface.md)（s2s の名簿は列挙を持たない。
  本決定の読み口はそれと**別の面**であり、s2s には出さない）／[IADR-0131](IADR-0131_openapi-as-bff-contract-source.md)／
  [IADR-0139](IADR-0139_domain-bundled-contract-prs.md)
- 起点 issue: #1445（#1446 と 1 PR に束ねる）

## コンテキストと課題

ADR-0098 で指定先の名前空間が決まり、SC-19 は「3 状態と件数、および**個人指定**の一覧・検索・変更ダイアログを描く。
**グループ指定は描かない**」となった。実測（2026-09-12・`origin/develop` `285e30c`）:

| 段 | 現状 |
| --- | --- |
| 共有台帳の管理 API | DocumentService `/documents/{id}/shares`（GET / POST / DELETE。所有者限定・拒否は 404）。**BFF に出ていない** |
| 契約 DTO | `DocumentShareDto` / `CreateShareRequest` は DocumentService の内部型 |
| 利用者の表示名 | AdminOnly の `/authz/users`（全件）だけ。**一般利用者が他の利用者を表示名で検索する口は無い**。s2s の `UserDirectory` gRPC は列挙を意図的に持たない（IADR-0401 決定 2） |
| 画面 | 共有先を変える導線が無い。`@platform/ui` に Combobox は無い |

決めることは 3 つ —— ①共有台帳をどの形で BFF へ出すか、②「表示名を出し識別子は出さない」をどう満たすか（表示名の供給元）、
③グループを台帳・契約・画面のどこで止めるか。

## 検討した選択肢

### 論点 1: 共有台帳の契約の形

- (1a) **後段の内部型をそのまま契約へ移し、BFF は透過中継する**（`DocumentShareDto(subjectType, subjectId, grantedBy, createdAt)`）
- (1b) BFF で表示名を合成した `PrivateNoteShareDto(displayName, …)` を返す（BFF が AuthorizationService も呼ぶ）
- (1c) `PrivateNoteDto` に指定先の配列を埋め込む（一覧 1 本で全部返す）

(1a) を採る。BFF の私的資料の面は透過中継（`RelayAsync`）で統一されており（IADR-0444 決定 7）、後段の 404 / 409 / 400 を詰め替えない
ことが SC-19 の固定文言の根拠である。(1b) は BFF に 2 後段の合成を持ち込み、片方の不達を「共有先が無い」に見せる形を作りやすい。
(1c) は一覧が所有資料 × 共有先の積で重くなるうえ、指定先の表示名という**別サービスの情報**を一覧へ混ぜる。

### 論点 2: 表示名の供給元（一般利用者が引ける読み口）

- (2a) **AuthorizationService に認証必須・ロール不問の `lookup`（検索）と `resolve`（利用者名 → 表示名）を新設し、面を「利用者名・表示名・
  有効状態」の 3 項目に閉じる**
- (2b) AdminOnly の `/authz/users` を一般利用者へ開く
- (2c) 表示名を DocumentService 側の共有台帳へ写して持つ（付与時に写す）
- (2d) 自由入力（利用者名を手で打つ）にして検索を持たない

(2a) を採る。(2b) はロール・ABAC 属性・内部 ID まで全利用者へ出す。(2c) は表示名の正が IdP にあるのに写しを持ち、改名に追随しない
（IADR-0301 が「利用者の表を持たない」と決めた理由と同じ）。(2d) は SC-19 主要素 3「検索して追加」に反し、ADR-0043 決定 3 と同じ
理由（試行錯誤で実在を探れる・体験が悪い）で採らない。**新しい読み口は「到達する主体を増やす」**（全利用者が全利用者の表示名を
引ける）ため、面を 3 項目に閉じ、`enabled=false` の利用者は検索に出さない（退職者へは共有できない。resolve は既存の共有先の表示の
ために返す）。到達範囲の拡大は計画側へ環流する（§結果）。

### 論点 3: グループを止める場所

- (3a) **画面だけで止める**（`subjectType=user` 固定・導線と文言を置かない）。台帳・契約は `group` を受け付けたまま
- (3b) BFF で `group` を 400 にする
- (3c) 台帳で `group` を拒む

(3a) を採る。ADR-0098 決定 2 が「**台帳側は受け付けたままとする**（受け付けをやめると配線が入ったときに再び開く作業が生じる）」
と定めている。BFF で止める (3b) も同じ「再び開く作業」を作る。画面に導線が無い以上、通常の利用者は到達しない。

## 決定

1. **`DocumentShareDto` / `CreateShareRequest` を名前・項目を変えずに `Knowledge.Contracts` へ移し、後段はこの型を使う。** BFF は
   `GET/POST /bff/private-notes/{id}/shares`・`DELETE …/{subjectType}/{subjectId}` を後段の `/documents/{id}/shares*` へ透過中継する
   （POST / DELETE は write ゲート。`x-roles: []`）。
2. **`UserSummaryDto(username, displayName, enabled)` を `Platform.Shared.Contracts` に置き、AuthorizationService に
   `GET /authz/users/lookup?q=&limit=`（2 文字以上・有効な利用者のみ・上限 50）と `POST /authz/users/resolve`（1〜100 件・
   居ない名前は落ちる・無効化済みも返す）を、AdminOnly の `/authz/users` 群とは**別の群**（認証必須・ロール不問）で足す。**ロール・
   ABAC 属性・内部 ID を運ぶ項目を持たない**（型で面を閉じる）。`IIdentityAdminClient` に `SearchUsersAsync` を足す（Keycloak の
   `search=`・InMemory の部分一致）。Platform BFF は `/bff/users/lookup`・`/bff/users/resolve` を利用者の `Authorization` を転送して
   透過中継する。**s2s の gRPC 面には出さない**（IADR-0401 の「列挙を持たない」を崩さない）。
3. **画面は `subjectType=user` しか送らず、グループの導線・文言を置かない**（陽性対照つきのテストで固定）。台帳にグループ共有が
   ある資料は「グループへの共有 N 件は本画面で変更できません」と告知し、消さない。**識別子（利用者名）は画面に出さず**、表示名は
   `resolve` で引く。居ない名前は「（不明な利用者）」、`enabled=false` は「（無効化済み）」を併記する。
4. **#1445 と #1446 を 1 PR に束ねる**（IADR-0139 決定 1。同じ資源 `private-notes` への同型の契約追加。#1444 と同じ形）。
5. **`lookup` は無効化済みを返さず、`resolve` は返す**（意図的な非対称）。前者は「これから共有する相手」の候補であり退職者へは
   共有できない。後者は「既に共有している相手」の表示であり、無効化済みを落とすと一覧から行が消えて取り消しの導線を失う。
6. **Platform BFF の透過中継は `UserAdminBffEndpoints.Proxy` を `internal` にして再利用する**（作法を 2 本持たない）。認可は
   呼び出し側の群（`/bff/users` は認証のみ・`/bff/admin/users` は AdminOnly）が決め、中継は資格情報を転送するだけである。
   AuthorizationService 側も `/authz/users/lookup`・`/resolve` は AdminOnly 群と**別の `MapGroup`**（literal 2 セグメントで
   `{userId}` 経路と衝突しないことを経路表とテストで固定）。

## 結果

- 良い点: SC-19 主要素 3 が個人指定について計画どおり描ける。ADR-0098 決定 2 の暫定手段（グループを描かない）が、台帳・契約を
  触らずに画面 1 か所で守られる。配線（#1447）が入ればグループ指定の UI を足すだけで済む。
- 悪い点 / 残余リスク:
  - 🔴 **全利用者が全利用者の表示名を検索できるようになる**（従前は管理者だけ）。面は 3 項目に閉じたが、到達範囲の拡大は
    計画が明示的に定めていない。**planning へ環流する**（ADR-0098 決定 1「画面には表示名を出す」の含意として確認を求める）。
  - Keycloak の `search=` は username / email / 姓名の部分一致であり、InMemory は username / displayName の部分一致である。
    **照合の対象が実装で違う**（メールで引けるのは Keycloak だけ）。契約は「表示名・利用者名で検索」とだけ書き、メール一致は
    仕様に含めない。
  - `resolve` は `FindByUsernameAsync` を人数分（≤100）呼ぶ。共有先は資料あたり少数の前提であり、まとめて引く口は IdP 側に無い。
  - 画面のダイアログを開くたびに `shares` → `resolve` の 2 往復が走る。

## フォローアップ

1. #1447（`${current_groups}` の束縛と共有先ベースの分岐の配線）が入ったら、決定 3 の暫定手段を解除しグループ指定の UI を足す
   （表示名は Keycloak のグループ表示名。グループの検索口は本決定に無く、その時に足す）。
2. #1448（`BffScopeResolver.MatchesAll` の集合値突合）。
3. 表示名の検索の到達範囲について planning の応答を受け、必要なら `lookup` の絞り（同一部門のみ等）を足す。
