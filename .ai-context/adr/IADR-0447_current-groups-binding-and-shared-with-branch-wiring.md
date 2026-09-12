---
title: IADR-0447 `${current_groups}` は IdP の所属照会で束縛し、共有先ベースの分岐は「ポリシー 1 本」と「DocumentService が応答へ載せる共有先の写し」で 4 面に効かせる — グループ指定の UI を解禁する
type: impl-adr
status: Accepted
related_ids: [FR-05, FR-19, UC-11, SC-19, ADR-0036, ADR-0088, ADR-0098, ADR-0100, IADR-0253, IADR-0301, IADR-0385, IADR-0396, IADR-0401, IADR-0445, IADR-0447, IADR-0448, IADR-0449]
author: Claude
created: 2026-09-12
updated: 2026-09-13
plan_refs:
  - planning:projects/microservices-platform/06_technical/07_abac-attribute-model.md
  - planning:projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md
  - planning:projects/microservices-platform/07_adr/ADR-0098_share-target-is-keycloak-group-and-ui-waits-for-binding.md
related_specs:
  - ../specs/20260912_1447-1448_current-groups-binding-and-set-valued-matching.md
---

# IADR-0447: `${current_groups}` は IdP の所属照会で束縛し、共有先ベースの分岐を 4 面に効かせる

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。
> 計画に影響する決定は計画側へ環流する（`/plan-feedback`）。

- 状態: Accepted
- 日付: 2026-09-12
- 決定者: Claude（実装）

## 起点・関連

- 関連する計画書 ID: FR-19・FR-05・UC-11・SC-19（主要素 3）／ADR-0036（D-03 束縛変数は `${current_user}` と `${current_groups}` の 2 つ／
  D-06 共有の単位は個人とグループ）／ADR-0088（決定 1 属性は IdP から引き直す）／ADR-0098（決定 1 共有先は Keycloak グループ ID・
  個人は利用者識別子／決定 2 グループ UI は配線まで描かない／決定 3 グループ木は管理者が作る／フォローアップ 1・3）／
  ADR-0100（決定 2 面を閉じる・件数を返さない）
- 関連 issue: #1447（本 IADR）、#1448（IADR-0448）、planning#618（裁定）、planning#621（環流）
- 関連 IADR: **IADR-0253 決定 3 を部分 supersede**（「束縛できる変数は `${current_user}` の 1 つだけ」を改める。決定 1・2・4 は据え置く）。
  IADR-0385（集合値の線上表現）・IADR-0396（`shared_with` を索引のリスト項目で運ぶ）・IADR-0401（名簿の列挙を s2s の面へ出さない）・
  IADR-0445（利用者検索の面。本 IADR のグループ検索は同型）・IADR-0448（集合値の交差突合）・IADR-0449（人限定）
- 作業仕様書: `../specs/20260912_1447-1448_current-groups-binding-and-set-valued-matching.md`

## コンテキストと課題

ADR-0098 は起案時に実測し、**グループ共有は台帳に入るが誰にも何も許可しない**と確定した（§実装の現状）。欠けていたのは 2 つである。

| 欠落 | 実測（2026-09-12・`origin/develop` `da485a1`） |
| --- | --- |
| `${current_groups}` の束縛 | `AbacEvaluator` のプレースホルダは `${current_user}` の 1 つ（IADR-0253 決定 3 が「増やさない」と定めていた。理由は「計画が語彙を定めていない」）。実装全体で `${current_groups}` は 0 件 |
| 共有先ベースの分岐の配線 | `DocumentShareEndpoints` が「消費側が共有記録へ到達する方式（DB per Service の越境）が未決のため別段」と注記。`DocumentUpdated.SharedWith` で索引（Qdrant）だけが間接的に到達できる。BFF の単体判定（`GET /bff/documents/{id}`）と GraphService は共有先に到達できない |

計画はその後、束縛変数の語彙（ADR-0036 D-03）と共有先の名前空間（ADR-0098 決定 1）を確定した。**IADR-0253 決定 3 の「語彙を先取りしない」という理由は消えた。**

さらに、`${current_groups}` の供給元には 3 つの候補があり、いずれを採るかで認可の信頼境界が変わる。

## 検討した選択肢

### 論点 1: `${current_groups}` の供給元

| 案 | 内容 | 判定 |
| --- | --- | --- |
| ① トークンの `groups` クレーム | realm が既に `oidc-group-membership-mapper`（`full.path=false`）で出している。BFF が本文へ載せて `/authz/scope` へ渡す | 🔴 **採らない。** `/authz/scope` は本文の主張を評価に用いない（ADR-0088 決定 1。`ScopeUserAttributeSource` が IdP から引き直す唯一の点）。クレームを信じる形はその決定と逆向きで、`platform-service` を持つサービスが 1 つ侵害されれば任意の所属を主張できる。加えてクレームが運ぶのは**名前**であり、ADR-0098 決定 1 の**識別子**ではない |
| **② IdP の所属照会** | `ScopeUserAttributeSource` が属性と同じ点で `GET /users/{id}/groups` を引く。値はグループ **ID** | **採る。** 信頼境界が変わらない（属性と同じ経路・同じ「居ない／引けなかった」の型）。往復が 1 判定あたり 1 つ増える |
| ③ 共有台帳側でグループを個人へ展開 | 付与時にグループの所属者を列挙して `user` 行に展開する | 採らない。所属の変化に追随できず（異動しても共有が残る）、名簿の列挙を DocumentService へ開くことになる（IADR-0401 の向きと逆） |

### 論点 2: 共有先ベースの分岐を「誰が」出すか

| 案 | 内容 | 判定 |
| --- | --- | --- |
| **(a) ポリシー 1 本** | `documentConditions: { shared_with: ["${current_user}", "${current_groups}"] }` の read ポリシーを投入する。評価器は他のポリシーと同じく 1 ポリシー＝1 分岐で出す | **採る。** IADR-0253 決定 1 の形をそのまま使う。`owner` 分岐も同じ形（dev seed の write ポリシー）である。**評価器に固定の分岐を焼き込まない** —— 焼き込むと「ポリシーを消しても共有が効く」状態になり、管理者が統制を読めなくなる |
| (b) 評価器が構造的に出す | `${current_user}`・`${current_groups}` を持つ分岐を常に付ける | 採らない。ADR-0036 の選言は計画上の規則だが、実装は「分岐＝ポリシー」で統一しており、1 つだけ例外を作ると `PrivateNoteVisibility.BranchMayGrant` 等の読み手が「ポリシーに無い分岐」を扱うことになる |

### 論点 3: 消費側が共有記録へ到達する方式（DB per Service の越境）

| 案 | 内容 | 判定 |
| --- | --- | --- |
| **(A) 所有者（DocumentService）が写しを運ぶ** | `DocumentDto.SharedWith`（応答）と `DocumentUpdated.SharedWith`（イベント。既存）を**同じ解決点**で載せる。BFF は `DocumentAttributeEncoding.WithSharedWith` の像で判定し、GraphService はイベントから `shared_with` 属性へ写す | **採る。** 消費側は台帳に到達しない（越境しない）。索引が既に採っている形（IADR-0396 決定 3）を応答へ広げるだけ |
| (B) 共有台帳の読み口（gRPC）を DocumentService に開く | 消費側が判定のたびに照会する | 採らない。判定のたびに往復が増え、名簿の列挙に近い面が増える |
| (C) 共有台帳を各サービスへ複製する | イベントで台帳を写す | 採らない。DB per Service の越境を「複製」で行う形であり、(A) で足りる |

### 論点 4: 束縛が空になったとき

`${current_groups}` は 0..N 値へ展開される。所属が無い利用者では `shared_with: ["${current_groups}"]` が**空の許可集合**になる。

| 案 | 判定 |
| --- | --- |
| 空のまま分岐に残す | 採らない。消費側の `Contains`・`Match.Keywords` はどれも空集合を「一致しない」と読むが、**「空の許可集合」の意味を消費側 4 面に委ねる**ことになる |
| **フィルタが空になった分岐は分岐ごと落とす** | **採る。** 評価器が「この分岐は誰にも一致しない」と判断して出さない。`${current_user}` を併記した条件（本 IADR の推奨形）では利用者名が残るので分岐は落ちない |

## 決定

1. **`${current_groups}` は `ScopeUserAttributeSource` が IdP の所属照会（`IIdentityAdminClient.GetUserGroupsAsync`。Keycloak Admin API `GET /users/{id}/groups`）で束縛する。値は Keycloak のグループ ID の集合。** トークンのクレームは使わない。所属照会の失敗は属性と同じく「引けなかった」（503 / `UNAVAILABLE`）であり deny に畳まない。
2. **`AbacEvaluator` の束縛変数は `${current_user}`（1 値）と `${current_groups}`（0..N 値）の 2 つとする**（ADR-0036 D-03 のとおり）。**IADR-0253 決定 3 の「1 つだけ・増やさない」を改める**（同決定の「述語側は解釈しない・評価器でのみ解決する」は据え置く）。束縛後に許可集合が空になるフィルタを持つ分岐は落とす。`AllowedFilters`（キー単位 union）では従前どおり束縛しない。契約（`AccessScopeRequest`・proto）は変えない。
3. **共有先ベースの分岐（選言の第 3 節）はポリシー 1 本で表す**: `action: read`・`documentConditions: { shared_with: ["${current_user}", "${current_groups}"] }`。dev seed（`deploy/local/abac-seed/policies.json`）へ投入する。**配備環境では本ポリシーの投入が統制の実現手段である**（投入前は従前どおり共有が誰にも効かない＝fail-closed）。手順は `docs/authz/FR-19_share-target-authorization.md`。
4. **共有先は DocumentService が写しを運び、消費側は台帳へ到達しない。** `DocumentDto.SharedWith` を足し（`DocumentUpdated.SharedWith` と同じ解決点 `ResolveSharedWithAsync`・同じ値。`ToDto` の必須引数にして渡し忘れをコンパイルで止める）、BFF の単体判定は `AuthzView(doc)`＝`DocumentAttributeEncoding.WithSharedWith(doc.Attributes, doc.SharedWith)` の像で判定する。GraphService は `DocumentUpdated.SharedWith` を `GraphDocument.Attributes["shared_with"]`（カンマ連結）へ写す。RetrievalService は既に到達済み。WikiService へは個人資料が流れない（述語の統一のみ。IADR-0448）。
   - 🔴 **BFF の読み取り経路（`GET /bff/documents/{id}`・本文・版）は、個人資料の一律除外を「裁量の分岐が許可すること」の要求へ置き換える（`IsReadable`）。** 実測では `FetchAuthorizedAsync` が SC-05 用の `IsManageable`（`!IsPrivateNote` の一律除外）を呼んでおり、共有先が一致しても詳細は 404 だった。除外を外すのではなく、**個人資料は `owner` か `shared_with` を条件に持つ分岐（裁量の分岐）が一致したときだけ読める**とし、述語は新設せず既存の `PrivateNoteVisibility.BranchMayGrant`（索引 2 実装と Graph が既に使う関数）へ寄せた —— BFF が同じ関数の 4 つ目の消費面になる。書き込みプリフライト（`ForwardIfInScope`。action=write）と SC-05 一覧の一律除外は従前どおり。**副作用として `owner` 分岐が一致する所有者自身の個人資料も `GET /bff/documents/{id}` で読めるようになる**（従前は 404。ADR-0036 D-05 の範囲内）。
5. **`SubjectType` は運ばない。** 判定規則 `doc.shared_with ∩ ({${current_user}} ∪ ${current_groups}) ≠ ∅` は 1 つの集合として突き合わせる。利用者名と Keycloak グループ ID（UUID）は名前空間が交わらないことを前提に置く（§結果 残余リスク）。
6. **グループ検索の読み口を利用者検索と同型で新設する**: AuthorizationService `GET /authz/groups/lookup?q=&limit=`（2 文字以上・上限 50・既定 20・グループ木を平坦化・`path` 順）／`POST /authz/groups/resolve`（1〜100 件の ID → 像。無い ID は落ちる）。面は `GroupSummaryDto(Id, DisplayName, Path)` の 3 項目。BFF `/bff/groups/*` が透過中継。**人の主体だけが到達できる**（IADR-0449）。
7. **SC-19 のグループ指定を解禁する**（ADR-0098 決定 2 の暫定手段の解除）。画面は表示名（＋パス）を出し、識別子は出さない。「グループ共有がある資料は本画面で変更できません」の告知を撤去する。

## 結果

- **良い影響**: グループ共有が索引・BFF・グラフの全面で同じ答えを出す。`${current_groups}` の信頼境界が属性と同じ（IdP から引く）。共有先の到達方式が「所有者が写しを運ぶ」の 1 つに揃い、索引と応答で同じ値が並ぶ。
- **鮮度（ADR-0098 フォローアップ 3）**: `/authz/scope` → `ScopeUserAttributeSource` → Keycloak の経路にキャッシュは無い（実測 0 件）。**所属の変更は次の判定から反映される**（Keycloak Admin API の読み取り一貫性のみ）。代価は 1 判定あたり IdP 往復が 1 → 2（利用者照会＋所属照会）。planning へ環流する。
- **悪い影響 / 残余リスク**:
  - 🔴 **配備環境の統制はポリシー投入に依存する**（決定 3）。投入し忘れは fail-closed だが「共有したのに見えない」に戻る。投入の有無は `GET /authz/policies` で確認できる。
  - 判定ごとの IdP 往復が増える。件数の実測はキャッシュ導入の判断材料として残す（本 IADR ではキャッシュを置かない。置くと鮮度が変わり ADR-0032 と同じ論点になる）。
  - 利用者名がグループ ID（UUID）と同じ綴りになる可能性を排除していない（決定 5）。Keycloak の利用者名は任意文字列だが、UUID 形式の利用者名は人事連携（ADR-0026）が作らないことを前提に置く。
  - `DocumentDto.SharedWith` は共有先の識別子を所有者以外に見せ得る（BFF の `GET /bff/documents/{id}` は判定後に DTO を返す）。**共有された相手は「誰と共有されているか」を見られる**。ADR-0036 はこれを禁じていないが、planning へ記録として環流する。 **［2026-09-13 追記 / #1451］利用者裁定（planning#626・ADR-0098 フォローアップ 5）は「所有者だけに返す」。BFF が所有者以外への応答から `SharedWith` を落とす（IADR-0450）。本リスクは閉じた。**
  - Keycloak の realm export に共有用のグループ木は足していない（ADR-0098 決定 3。管理者の作業）。

## フォローアップ

1. 鮮度の実測（キャッシュ無し・往復 2）を planning へ環流（ADR-0098 フォローアップ 3）。
2. 共有された相手が共有先の識別子を見られる点の記録を planning へ環流。 **［2026-09-13 追記 / #1451］環流済み（ADR-0098 フォローアップ 5・planning#625）。裁定は planning#626、実装は IADR-0450。**
3. 配備環境へのポリシー投入手順（`docs/authz/FR-19_share-target-authorization.md`）。
