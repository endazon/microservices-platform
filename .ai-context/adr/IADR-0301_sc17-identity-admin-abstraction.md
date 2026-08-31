---
title: IADR-0301 SC-17 は AuthorizationService から IdP へ委譲し、利用者表も新規作成の口も持たない
type: impl-adr
status: Accepted
related_ids: [FR-05, FR-09, UC-05, SC-09, SC-17, ADR-0004, ADR-0026, ADR-0031, ADR-0032, ADR-0036]
author: implementation-agent
created: 2026-08-29
updated: 2026-08-31
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md
  - planning:projects/microservices-platform/07_adr/ADR-0026_authentication-ux-and-account-management.md
  - planning:projects/microservices-platform/06_technical/02_service-decomposition.md
  - planning:projects/microservices-platform/06_technical/07_abac-attribute-model.md
  - planning:projects/microservices-platform/06_technical/09_datasource-connectors.md
---

# IADR-0301: SC-17（ユーザーアカウント管理）の実装先・抽象・権限範囲の決定

- 状態: Accepted
- 日付: 2026-08-29
- 決定者: implementation-agent（実装担当）

## 起点・関連

- 関連する計画書 ID: FR-05 / FR-09 / UC-05 / SC-09 / SC-17 / ADR-0026（§決定）/ ADR-0032 / ADR-0004
- 関連する実装仕様書: `.ai-context/specs/20260829_issue-452_sc17-user-account-management.md`
- 先行: IADR-0040（管理 ABAC の BFF 透過と AdminOnly）/ IADR-0297（SC-12 の同型な決定）/
  IADR-0286（既定資格情報を持たず未注入は起動失敗）/ IADR-0251・IADR-0273（BFF セッションと
  バックチャネルログアウト）/ IADR-0129 決定 6（値域の判定を DOM から切り離す）

## コンテキストと課題

**SC-17 はバックエンドが皆無であった**（着手前に実測）。

- `/users` を宣言する端点は backend 全域で 0 件、`docs/api/openapi.yaml` に `users` の語が 0 件
- `AuthorizationService` が持つ `DbSet` は `AttributeDefinitions` と `Policies` の 2 つだけで、
  **「どの利用者にどの属性が付いているか」を持つ表は存在しない**
- Keycloak Admin API を呼ぶコードはリポジトリ全域で 0 件

一方、**利用者 ABAC 属性の実体は IdP のユーザー属性である**
（realm の `abac-attributes` クライアントスコープが `clearance` / `department` を
user attribute → claim で写し、判定側 `BffScopeResolver.ExtractUserAttributes` はその 2 クレームだけを読む）。
つまり計画が言う「属性ストア」の実装上の実体は IdP 側にあり、認可サービスに書き込み先は無い。

さらに **SC-17 の帰属が 3 者で食い違っていた**（詳細と実測箇所は作業仕様書 §2）。
計画 ADR-0026 §フォローアップは「Admin API 連携（SC-17）」と書くが担当サービスを名指ししておらず、
#438 は起票時に「管理画面バックエンド: SC-09・SC-17」を抱えたのち 2026-08-21 のコメントで
残作業から外し、planning#490 の引き継ぎ表は SC-17 を「未実装**画面**」として #452 へ送っている。
**どこにも「SC-17 のバックエンドを誰が作るか」が書かれていない。**

決めるべきことが 6 つある。

- (1) 実装先（既存サービスへ足すか、新サービスか）
- (2) IdP のサービスクライアントへ与える権限の範囲（計画 09_datasource-connectors が保留した点）
- (3) 実装の差し替え方（本番と開発・テスト）と資格情報の受け取り方
- (4) 抽象が持つ操作（とくに新規作成を持つか）
- (5) 割当の必須性と値域をどこから引くか
- (6) 「無効化」と「全セッション失効」を 1 つにするか分けるか

## 検討した選択肢

### (1) 実装先

| 案 | 内容 | 評価 |
| --- | --- | --- |
| 1-A | 新サービス（例: 身元管理サービス）を立てる | **却下。** 計画 06_technical/02_service-decomposition §サービス分割時の注意点 が「サービス数の基準は 11＋BFF とし、**これを超える分割は新 ADR で判断する**」と定める。**新サービスは計画 ADR が要るので、実装側の IADR では決められない。** 加えて配備 manifest・イメージマッピング・chart キー・BFF downstream の新設を伴い、1 画面の追加と同じ PR に混ぜる変更ではない |
| 1-B | **`AuthorizationService` へ `/authz/users*` を足す** | **採用。** 計画の同じ表が**認可サービスの保持データに「Keycloak連携」を明記**しており、SC-17 の反映先（Admin API ＋ 属性ストア）はそのまま同サービスの責務欄に載っている。入力規則「SC-09 の属性体系に定義済みの値のみ」の値域の正は同サービスの `AttributeDefinitions` であり、別サービスへ置くと値域と検証が 2 サービスに割れる。既に BFF の downstream として配備済みで `check-bff-downstreams.js` の :8080 突合も通っている |

**同時に境界を引く。** 計画の同じ節が「**ID 管理を自作せず**、認可サービスは『ABAC ポリシー判定』に
責務を限定する」と書いており、無条件に足すとこの一文と衝突する。衝突しない形は次の 3 点で定義する。

- **利用者の表を本サービスへ作らない**（`DbSet` を増やさない）。一覧も割当もすべて IdP へ委譲する。
- **新規作成の口を持たない**（下の決定 4）。
- **認証・資格情報・パスワード・MFA を一切扱わない**（SC-13〜16 の領域）。

### (2) サービスクライアントの権限範囲

計画 06_technical/09_datasource-connectors は「取り込み経路が Keycloak 管理 API を呼ぶ主体は
SC-17 の管理者操作とは別である。…**本節はその権限設計を定めない**」と保留していた。
**その保留を SC-17 の側について埋める。**

| ロール（`realm-management`） | 与える | 使う操作 |
| --- | --- | --- |
| `view-users` | ○ | 利用者の列挙・ロールマッピングの読み取り |
| `manage-users` | ○ | 属性更新・ロール割当/解除・`enabled` 切替・セッション失効 |
| `view-realm` | ○ | 割当可能な realm ロールの列挙 |
| `manage-realm` / `manage-clients` / `create-client` / `impersonation` / `manage-authorization` | **×** | SC-17 のどの操作にも要らない。とくに `impersonation` は管理者が任意の利用者になり代われる（監査ログが「誰がやったか」を失う） |

**取り込み経路のクライアントとは別のクライアントにする。** 同じ機密クライアントを共用すると、
取り込みの資格情報が漏れた時点で利用者の権限まで書き換えられる。

### (3) 実装の差し替えと資格情報

| 案 | 内容 | 評価 |
| --- | --- | --- |
| 3-A | provider の既定を `in-memory` にする | **却下。** 構成の注入漏れが「起動失敗」ではなく「**反映したつもりで消える**」へ倒れる。管理者は保存の成功を見て、認可判定は一切変わらない。#1012（既定資格情報で誤った DB へ書けた欠陥）と同型の静かな壊れである |
| 3-B | provider の既定を `keycloak` にする | **却下。** `realm-management` ロールを持つ機密クライアントがまだ realm に無いため、既存の配備が一斉に起動できなくなる |
| 3-C | **`IdentityAdmin:Provider` に既定を置かず、宣言を必須にする** | **採用。** 未宣言は起動時例外。値域は `keycloak` / `in-memory` の 2 値。`keycloak` を選ぶと `IdentityAdmin:Keycloak:{BaseUrl,Realm,ClientId,ClientSecret}` が全部要り、欠けたら起動時例外（IADR-0286 と同型。**既定資格情報を持たない**）。`in-memory` は起動時に警告ログを 1 行出す —— 偽物が黙って動く形にしない |

**配備側は当面 `in-memory` を明示する**（compose / helm values）。**暫定であることと解消条件
（`realm-management` を持つ機密クライアントの配備）をコメントで併記する** ——
統制を定めた記述には現在の実現手段を併記し、未配備なら暫定手段を並べる（`CLAUDE.md`）。

### (4) 抽象が持つ操作

`IIdentityAdminClient` は 6 つの操作を持つ: 列挙 / 割当可能ロールの列挙 / ABAC 属性の差し替え /
realm ロールの差し替え / `enabled` の切替 / セッションの失効。

🔴 **利用者の新規作成に相当する操作を持たない。** 計画 05_screens §SC-17 アクションが
「アカウントは人事システム連携で自動プロビジョニングし…（**本画面から新規作成はしない**）」と定める。
**規約で禁じるのではなく型で持てなくする** —— 生やそうとした人がインターフェイスの改定にぶつかる。
不在は 3 層で固定する（`IdentityAdminContractTests` の反射・端点のルート表・BFF の 405・画面の否定形）。
いずれも**陽性対照と対**で置く（何も無い実装でも否定形だけなら緑になるため）。

### (5) 必須性と値域の出どころ

| 対象 | 出どころ | 理由 |
| --- | --- | --- |
| ロールの値域 | **IdP**（`ListAssignableRolesAsync`） | 計画は 4 種（利用者／管理者／運用者／システム管理者）を挙げるが、realm には `platform-admin` / `platform-operator` の 2 つしか無い（ADR-0026 §フォローアップが「管理ロールの権限分離（現行実装は単一 `platform-admin`）」を後続対応として既に挙げている）。**焼き込むと「計画には在るが選ぶと必ず失敗する選択肢」を描く**ことになる。引く形にすれば realm が増えた日に画面が自動追随する |
| 属性値の値域 | **SC-09 の属性辞書**（`scope=user`） | 計画の「SC-09 の属性体系・タグ辞書に定義済みの値のみ」そのもの |
| 属性の必須性 | **計画が名指しした 2 キー**（`department` / `clearance`） | 🔴 **辞書の `Required` 列から引かない。** 同列は取り込み時の必須性として運用されており（`deploy/local/abac-seed/attributes.json` の注記が「required は**すべて false**」「必須化は実データ側が属性を備えてから」と明記）、**割当の必須性とは別の軸**である。1 つの列を 2 つの意味で使うと、片方を直したときにもう片方が黙って緩む |

**利用者側の属性は辞書に無いキーも拒否する**（文書側の「自由タグ許容」と意図的に違える）。
利用者属性は**認可判定の主体側の入力**であり、辞書外のキーを受けても判定には一切効かない。
受け付けて無視すると「割り当てたのに効かない」が黙って作れる。

### (6) 無効化とセッション失効

**1 つの操作にする**（`POST /authz/users/{id}/disable` が両方を行う）。分けると、無効化だけ実行して
失効を忘れた状態が作れる —— そのとき既存セッションはアクセストークンの寿命だけ生き続け、
計画の「無効化→**全セッション即時失効**」が満たされない。順序は**無効化してから失効**である
（逆順だと、失効と無効化の間に張り直されたセッションが残る）。

失効は IdP のバックチャネルログアウトを起こし、BFF の `BackchannelLogoutProcessor` が
subject 単位でチケットを削除する（ADR-0032 / IADR-0251 / IADR-0273）。realm の client `bff` には
`backchannel.logout.url` と `backchannel.logout.session.required: true` が登録済みであることを実測した。

## 決定

1. **SC-17 のバックエンドは `AuthorizationService` へ置く。** ただし**利用者の表を持たず**、
   一覧も割当も IdP へ委譲する。認証・資格情報・MFA は扱わない。新サービスは作らない
   （計画がサービス数の増加に新 ADR を要求しているため、実装側では決められない）。
2. **IdP のサービスクライアントへ与えるのは `view-users` / `manage-users` / `view-realm` の 3 つだけ**とし、
   `manage-realm` / `manage-clients` / `create-client` / `impersonation` は与えない。
   **取り込み経路のクライアントとは別のクライアントにする。**
3. **`IdentityAdmin:Provider` に既定を置かない**（未宣言は起動時例外）。`keycloak` の資格情報も
   既定を持たず、欠落は起動時例外。`in-memory` は起動時に警告を出す。配備側は当面 `in-memory` を
   **暫定として明示**し、解消条件をコメントで併記する。
4. **抽象は新規作成を持たない。** 不在を型・ルート表・BFF・画面の 4 箇所で、**陽性対照と対にして**固定する。
5. **ロールの値域は IdP から、属性値の値域は SC-09 の辞書から引く。** 画面にも後段にも焼き込まない。
   **属性の必須性だけは計画が名指しした 2 キー（`department` / `clearance`）で持ち、辞書の
   `Required` 列を流用しない。** 利用者側の属性は辞書に無いキーも拒否する。
6. **無効化と全セッション失効は 1 つの操作**とし、無効化 → 失効の順で行う。

## 結果

- 良い: 「属性ストア」の実体が 1 つ（IdP）に閉じ、認可サービスに二重の真実源を作らない。
  計画が禁じた新規作成が型で塞がれる。値域が増えても画面と後段が追随する。
- 良い: fake により、実 IdP 無しで画面・BFF・後段・検証まで通しで動かせる。
- **代償 1**: 配備側が当面 `in-memory` を宣言するため、**実配備でも SC-17 の変更はプロセス内に
  しか残らない**。起動時の警告ログと本 IADR・作業仕様書の残件記録が唯一の歯止めである。
  解消は `realm-management` クライアントの配備をもって行う。
- **代償 2**: Admin REST 実装は**スタブした `HttpMessageHandler` に対してのみ**検証した。
  実 Keycloak との疎通は未検証である（本環境に Docker / k3s / 実 Keycloak が無い）。
  **「緑である」ことは「実 IdP へ反映できる」ことを意味しない。**
- **代償 3**: 保存はロールと属性の 2 要求に分かれ、**原子的ではない**（片方だけ通る余地がある）。
  画面は送る前に検証し、失敗した側の理由を出す。原子性が要るなら後段に 1 つの口を足す改定が要る。
- 追随: 計画側へ 2 件の環流を起票する（SC-17 の帰属の食い違い／`07_abac-attribute-model` の
  「即時失効は満たされていない」が BFF セッション移行後の実態と食い違う）。

［2026-08-31 追記 / #1101］**代償 1 と代償 2 は解消した。後継は IADR-0321 である**
（本文は当時の記録として残す。付け替えない）。realm へ機密クライアント `identity-admin` を登録して
配備を `keycloak` へ移し、稼働クラスタで疎通を実測した。**決定 3 の「配備側は当面 `in-memory` を
宣言する」はもう成り立たない** —— `in-memory` は非配備ホストでしか選べない（IADR-0321 決定 5）。
なお決定 2 の 3 ロールは実測でも過不足が無かったが、**それだけでは Admin API が 403 になる**
（トークンにクライアントロールを載せるスコープが要る。IADR-0321 決定 2）。代償 3 は未解消のまま。
