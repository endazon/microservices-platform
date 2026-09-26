---
title: IADR-0476 DocumentService の読み取りの全ての口に認証を求め、個人資料は所有者と共有先の利用者にだけ返す。主体は中継された利用者・gRPC 本文の利用者文脈・機械クライアント自身のいずれかで、グループの共有先だけを認可サービスへ問う
type: impl-adr
status: Accepted
related_ids: [FR-05, FR-06, FR-19, UC-03, SC-03, SC-05, NFR-09, ADR-0119, ADR-0034, ADR-0036, ADR-0054, ADR-0056, ADR-0058, ADR-0086, ADR-0098, ADR-0109, IADR-0012, IADR-0041, IADR-0045, IADR-0379, IADR-0402, IADR-0410, IADR-0416, IADR-0420, IADR-0447, IADR-0475]
author: claude
created: 2026-09-27
updated: 2026-09-27
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0119_document-machine-client-own-docs-and-read-abac.md 決定 3・決定 4
  - planning:projects/microservices-platform/06_technical/07_abac-attribute-model.md §所有者ベースの read 規則（2026-09-27 補完）
  - planning:projects/microservices-platform/10_feedback/20260927_document-machine-client-and-read-abac.md §残るもの
related_specs:
  - ../specs/20260927_issue-1614_document-read-authn-private-note.md
---

# IADR-0476: DocumentService の読み取りに認証を求め、個人資料を所有者と共有先の利用者にだけ返す

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-09-27
- 決定者: claude（#1614。planning#680 の裁定〔計画 ADR-0119 決定 3〕の実装側の残作業のうち「先に入れてよい」部分）

## 起点・関連

- 関連する計画書 ID: NFR-09（全 API で文書・データ単位の認可）、FR-06 / UC-03（文書の閲覧）、FR-19（個人資料）、SC-03 / SC-05
- 関連する計画 ADR: **ADR-0119** 決定 3（読み取りの全ての口で認証・個人資料は所有者と共有先にだけ・読めない文書は一覧から除き個別は 404・
  主体は中継された利用者／本文の利用者文脈／機械クライアント自身）・決定 4（認証の要求と個人資料の除外は内容の ABAC より先に入れてよい）、
  ADR-0034 決定 9（サービスアカウントは個人資料を一律に対象外）、ADR-0036 D-05・D-06・D-08・D-14、ADR-0054（`doc_scope`）、
  ADR-0056（存在秘匿は 404）、ADR-0058（`doc_scope` の不変）、ADR-0086 決定 1（east-west は利用者文脈を本文で運ぶ）、
  ADR-0098 決定 1（共有先は Keycloak のグループ）、ADR-0109 決定 3（後段は中継された利用者の資格情報を自ら検証する）
- 関連する実装 ADR: IADR-0012 / IADR-0041 / IADR-0045（組織文書の ABAC の実施点は BFF。**本 IADR は変えない**）、
  IADR-0379 / IADR-0402（文書読み取りの gRPC 面。`ServiceCaller`）、IADR-0410（利用者文脈を本文で運ぶ面）、
  IADR-0416（認可サービスは本文の属性を信じず引き直す）、IADR-0420（`MachinePrincipal`）、IADR-0447（共有先の唯一の解決点）、
  IADR-0475 決定 3（「裁定が出たら既存の一覧と新しい口の両方へ同じ門を置く新しい IADR で扱う」—— 本 IADR がそれである）
- 関連する実装仕様書: `.ai-context/specs/20260927_issue-1614_document-read-authn-private-note.md`

## コンテキストと課題

planning#680 の実測 10: DocumentService の `GET /documents`・`GET /documents/{id}`・版の取得は認証を求めない群に属し、
個人資料も除かない。個人資料の表題・属性・owner・共有先が、メッシュ内の任意の呼び出し元へ返っていた。
gRPC 面は `ServiceCaller` を要求するが、同じく個人資料を除かない。計画 ADR-0119 決定 3 は、DocumentService 自身が
読み取りに認証を求め、個人資料を所有者と共有先にだけ返すことを定めた（内容の ABAC は #1615 で別に入れる）。

決めることは 4 つあった。

1. **主体を何から決めるか**（REST と gRPC の両方で）。
2. **個人資料の「共有先」のうち、グループをどう判定するか**。DocumentService は利用者の所属を知らない
   （トークンの `groups` は [[IADR-0447]] が採らないと決めた。台帳の共有先はグループ ID で、トークンの値はパスである）。
3. **既存の呼び出し元を壊さないこと**。BFF の読み取りは REST（利用者の資格情報を付けていない）と gRPC（BFF 自身の s2s トークン）の
   2 経路を持ち、compose・helm とも gRPC 経路で動いている。どちらもそのままだと、所有者が自分の個人資料を SC-03 で開けなくなる。
4. **#1615（内容の ABAC）が判定点を増やさずに入れる形**。

## 検討した選択肢

### 主体

1. **REST は資格情報そのもの、gRPC は本文の利用者文脈（無ければ呼び出し元サービス）**（**採用**）—— ADR-0119 決定 3 の 3 種に一致する。
   人か機械かは既存の `MachinePrincipal.IsMachine` ただ 1 つで決める。
2. gRPC にも利用者のトークンを載せる —— [[IADR-0379]] 決定 4（利用者のトークンは面を通らない。confused deputy）に反する。
3. 読み取りを `ServiceCaller` に限る —— AST の KB 用クライアント（`platform-operator` のみ）と ABAC 投入用の seed が一覧を読めなくなる。

### グループの共有先

1. **所有者・利用者の共有先は台帳だけで決め、グループの共有先のときだけ認可サービスへ問う**（**採用**）—— 問い合わせは `AuthzScope/Resolve`
   （action=read）で、許可の根拠は BFF・検索・グラフが使っている同じ述語（`AttributeFilterMatch.MatchesAll` ∧ `PrivateNoteVisibility.BranchMayGrant`）。
2. 個人資料の判定をすべて認可サービスへ問う —— 所有者が自分の資料を読むたびに認可サービスが要り、その不調で自分の資料まで読めなくなる。
   本件の範囲では得るものが無い（所有者の分岐は台帳だけで答えが出る）。
3. グループの共有先を判定しない（利用者の共有先だけ）—— 共有台帳にはグループが入り、BFF の単体判定はそれを許す（#1447）。
   DocumentService が 404 を返すと、グループへ共有された資料が SC-03 で開けなくなる（既存の消費者の退行）。
4. 利用者名簿の口（`UserDirectory`）に所属の照会を足す —— 新しい面を 1 つ増やし、判定の述語も DocumentService に 2 本目として書くことになる。

### BFF の中継

1. **REST は書き込みと同じ `Forwarding`（利用者の `Authorization` を中継）、gRPC は本文の `UserContext`**（**採用**）。
2. BFF の読み取りを s2s に揃える —— DocumentService から見て主体が BFF になり、個人資料が一律に返らなくなる。

## 決定

### 決定 1: 読み取りの 5 口を認証を要する 1 つの群に置く。ロールは積まない

`GET /documents`・`/documents/page`・`/documents/{id}`・`/documents/{id}/versions`・`/documents/{id}/versions/{version}` を
`RequireAuthorization()` の群（`read`）へ寄せる。旧 `pageRead` 群（#1575）もここへ合流した（同じ認可の既定を 2 本持たない）。
ロールを積まないのは、SC-03 の一般利用者の閲覧のためである（ロールで塞ぐと BFF の詳細が一般利用者に 404 を返す）。
gRPC 面は従前どおり `ServiceCaller`。健全性の口は匿名のまま。

### 決定 2: 主体（`DocumentReadPrincipal`）

| 経路 | 主体 |
| --- | --- |
| REST・`MachinePrincipal.IsMachine` が偽 | その利用者（`Identity.Name`＝`preferred_username`） |
| REST・`MachinePrincipal.IsMachine` が真 | 機械（そのサービスアカウント） |
| gRPC・要求に `user` がある | その利用者（`user.user_id`）。`service-account-` で始まれば機械 |
| gRPC・要求に `user` が無い | 機械（呼び出し元サービス自身） |

- gRPC の `user.user_id` が空文字なら `INVALID_ARGUMENT`（「利用者が分からない」を機械の主体へ畳まない）。
- 本文の利用者文脈を信じてよいのは、それを運ぶのが `ServiceCaller`（realm ロール `platform-service`）を通ったサービスだからである
  （ADR-0086 決定 1。`DocumentTagWrite/AddTag` の `user_id` と同じ扱い）。
  ［2026-09-27 追記 / #1628］**この理由づけは改めた。** `platform-service` だけでは信じず、許可集合の中継者（既定 `bff`）に限る。
  それ以外が `user` を付けたら `PERMISSION_DENIED`。末尾の追記 1 を見よ。
- 名前の分からない人（`preferred_username` もクライアント識別も無い）は個人資料を読まない（組織文書は読む）。

### 決定 3: 可視性の唯一の判定点（`DocumentReadAccess`）

- **組織文書**（`DocumentScopes.IsPrivateNote` が偽。キー欠落を含む）は認証済みの全主体に返す。
- **個人資料**は、名前の分かる**利用者**のうち次のいずれかにだけ返す（OR）。機械には返さない。管理者ロールも特別扱いしない。
  1. 所有者 —— `DocumentBodyIntake.IsOwnedBy`（本文の書き込みの `CanWrite` と同じ比較。序数一致）
  2. 利用者の共有先 —— 主体 ∈ 共有台帳の `SubjectId`（`ResolveSharedWithAsync` と同じ集合。[[IADR-0447]] が種別を落とした理由のとおり、
     利用者名とグループ ID は名前空間が交わらない）
  3. グループの共有先 —— 1・2 で決まらず共有が 1 件以上あるときだけ、**認可サービスへ問う**（決定 4）
- 読めない文書は一覧から除き（件数にも含めない）、個別・版の一覧・特定版は REST 404 ／ gRPC `found=false`（「無い」と区別しない）。
  特定版は**現在の文書**の可視性で判定する（`doc_scope` は不変〔ADR-0058〕、所有者・共有先は現在の値が正）。
- `GET /documents/page` は従前どおり個人資料を**主体に依らず**返さない（組織文書の口。所有者でも空）。`DocumentReadAccess` を通さなくても
  「他人の個人資料を返さない」を満たす（より狭い）。

### 決定 4: グループの共有先は認可サービスへ問う（`IDocumentReadScopeSource`）

- 実装は `AuthzScope/Resolve`（gRPC・action=`read`・利用者属性は送らない）。所属は認可サービスが IdP から引く（[[IADR-0416]]）。
  評価は共有先を `shared_with` の集合値属性として重ねた像（`DocumentAttributeEncoding.WithSharedWith`。BFF の `AuthzView` と同じ像）に対して
  `AttributeFilterMatch.MatchesAll` ∧ `PrivateNoteVisibility.BranchMayGrant` を分岐ごとに行う（静的属性だけの分岐は個人資料を開かない ＝ D-08）。
- **要求ごとに高々 1 回**（`DocumentReadAccess` は要求の寿命で、利用者ごとに memo。ADR-0036 D-14 のキャッシュキーの要件は利用者名をキーにして満たす）。
  所有者・利用者の共有先・組織文書だけの読み取りでは 1 度も問わない。
- **fail-closed**: 許可なし・`RpcException`・s2s トークンの取得失敗・5 秒の上限・未構成（`Services:AuthorizationServiceGrpc` が無い配備の縮退）は
  すべて「読めない」。その資料だけが見えなくなり、所有者の資料と組織文書の読み取りは止まらない。要求そのものの取り消しだけは取り消しとして伝える。
- compose・helm の document-service は既に `Services__AuthorizationServiceGrpc` と s2s の資格情報（`platform-service`）を持つ（利用者名簿の口のため）。
  配備の変更は無い。

### 決定 5: gRPC 契約に `UserContext` を足す（非破壊）

`document_read.proto` の 4 つの要求へ `UserContext user`（`user_id` / `user_attributes` / `action`。検索・グラフの面と同じ 3 項目）を新しい番号で足した。
package をまたいで型を共有しない（[[IADR-0379]] 決定 1）ので写しになる。`user_attributes` / `action` は本件では読まない（#1615 のため先に契約へ置く）。

### 決定 6: BFF は読み取りでも利用者を名指す

- REST 経路の読み取り（一覧・詳細・版の一覧・特定版・書き込みの事前確認）は書き込みと同じ `Forwarding` で呼ぶ。
- gRPC 経路は呼び出し元の利用者を `UserContext` で運ぶ（抽出は `AttributeValuesGrpcClient.ToUserContext` と同じ）。呼び出し元が機械なら運ばない。
- BFF の判定（`BffScopeResolver` ＋ `IsManageable` / `IsReadable`）は変えない。DocumentService の判定は BFF の判定より広いか等しい
  （所有者・共有先は通す）ので、BFF の応答は変わらない（AND 合成）。

### 決定 7: #1615 の差し込み口

判定は `DocumentReadAccess` ただ 1 か所で、REST 5 口と gRPC 4 rpc がすべて通る。#1615 は `IDocumentReadScopeSource` の同じ結果を
**組織文書にも**当てる（所有者・共有先の分岐も同じ結果に含まれる）ことで、判定点も問い合わせの口も増やさずに入る。そのときは
`/documents/page` も `DocumentReadAccess` を通す。

## 理由

- **主体を資格情報から決めるのは、ADR-0109 決定 3（後段は中継された資格情報を自ら検証する）と ADR-0086 決定 1（east-west は本文で運ぶ）を
  そのまま読んだ形だからである。** 新しい述語は作らず、人か機械かは `MachinePrincipal` ただ 1 つに委ねた。
- **グループだけを認可サービスへ問うのは、所属を知っているのが認可サービスだけだからである。** 所有者と利用者の共有先は台帳が答えを持つので、
  そこまで認可サービスに依存させると、その不調が自分の資料の読み取りを止める（得るものが無い）。
- **fail-closed を「その資料だけ」に閉じるのは、ADR-0119 §結果が挙げた「認可サービスが落ちたとき読み取りも止まる」を、本件の範囲では
  グループの共有先の資料だけに抑えられるからである。** 組織文書の fail-closed は #1615 の判断に残す。
- **BFF の判定を変えないのは、本件が門を 1 枚足す作業であって、既存の門を動かす作業ではないからである。**

## 結果

- 良い影響:
  - 個人資料の表題・owner・共有先がメッシュ内の任意の呼び出し元へ返らなくなる。読み取りの全ての口に主体が決まる。
  - 呼び出し元の棚卸し（仕様書）のとおり、既存の消費者はすべて資格情報を持つ（BFF は本件で中継を足した。AST の KB 用クライアント・
    ABAC 投入用の seed は client credentials を既に付けている）。
- 悪い影響 / トレードオフ:
  - グループの共有先の資料の読み取りは認可サービスに依存する（1 要求 1 往復・上限 5 秒）。
  - 機械クライアントは組織文書を内容の ABAC なしで読める（ADR-0119 決定 4 の暫定のまま。#1615 で閉じる）。
  - 資格情報を付けずに `GET /documents` を叩く道具は 401 を受ける（dev の seed の到達確認は「何か応答があれば到達」なので影響しない）。
- フォローアップ:
  1. #1615: 内容の ABAC を `DocumentReadAccess` に入れる（決定 7）。
  2. 本件の範囲外で残る ADR-0119 の実装（機械クライアントの自分の文書の更新・削除、`IADR-0075` の改訂、edge の AuthorizationPolicy）。
- 再検討の条件: 認可サービスが所属を返す狭い口を持ったとき（決定 4 の問い合わせを置き換えるか）。

## 追記 1: gRPC の本文の利用者文脈を信じる呼び出し元を許可集合（既定 `bff`）に絞る（2026-09-27 / #1628）

［2026-09-27 追記 / #1628］PR #1626 の監査の F1。決定 2 の「本文の利用者文脈を信じてよいのは `ServiceCaller` を通ったサービスだから」を改める。

- **事実**: `ServiceCaller` は realm ロール `platform-service` だけを見る。realm では 11 のサービスアカウントがそれを持つ
  （bff・aianalysis-service・graph-service・conversion-service・retrieval-service・ingestion-service・wiki-service・datasource-service・
  mcp-server・document-service・別プロジェクトの `ai-stock-trading-llm-caller`）。どれかが `user.user_id` を任意の利用者にして `DocumentRead` を呼べば、
  その利用者の個人資料の表題・owner・共有先が返っていた。実際に `DocumentRead` を呼ぶのは BFF だけである（`DocumentReadGrpcClient` のみ）。
- **決定**:
  1. **本文の `user` を信じるのは、呼び出し元が機械の主体（`MachinePrincipal.IsMachine`）で、かつクライアント識別
     （`MachinePrincipal.ClientIdOf`。`azp` を第一に、無ければ `service-account-<clientId>` から復元）が許可集合に序数一致で含まれるときだけ。**
     機械であることを併せて求めるのは、`azp` が人のトークンにも付く（BFF のセッションの利用者トークンは `azp=bff`）からである。
  2. **許可集合は `DocumentRead:TrustedUserContextClients`（配列）で構成する。未構成なら `bff` だけ。構成すると既定を置き換える**（足し合わせない。
     .NET の配列の束縛は初期値に追記するため、プロパティの既定を null にして読み出し側で既定を解決する）。空白だけの要素は捨て、
     1 つも残らなければ誰も信じない（fail-closed）。`MachinePrincipal` の「一覧を構成に持たない」とは向きが逆である（こちらは許可の集合で、
     外せば狭くなる。`SyntheticMonitoringOptions.Subjects` と同じ形）。
  3. **許可集合に無い呼び出し元が `user` を付けたら `PERMISSION_DENIED`**（利用者識別子が空かどうかより先に判定する）。拒否はクライアント識別だけを
     警告ログに残す（基数は realm の機密クライアント数で閉じる）。`user` を付けない呼び出し（呼び出し元サービス自身＝機械の主体）は従来どおり
     全ての `ServiceCaller` に開いている。
- **選ばなかった案: 機械の主体として扱う（`user` を捨てる）。** 個人資料は返らないが、ADR-0119 決定 3 の「本文で利用者が運ばれたなら、その利用者」を
  呼び出し先が黙って別の主体へ読み替えることになる。呼び出し元は利用者の視野のつもりで機械の視野を受け取り、誤りに気付けない
  （決定 2 の「利用者が分からないを機械の主体へ畳まない」と同じ理由）。拒否なら、受け付けた呼び出しでは決定 3 の主体の規則がそのまま成り立ち、
  新しい中継者を足すには構成を 1 行足す判断が要る。
- **ADR-0086 決定 1 との関係**: 決定 1 は運び方（本文で運ぶ）を定め、§結果は「中継サービスが正直であること」への依存を受け入れた。
  受け入れたのは利用者の権限で動く中継者への依存であって、`platform-service` を持つ全主体への依存ではない。依存を実在する中継者（BFF）へ
  狭めても、運び方は変わらない。
- **`ai-stock-trading-llm-caller` の `platform-service`**: 外せない。AST の TradeDecisionService・ReportService が LlmGateway の `/complete`（REST）と
  gRPC のテキスト生成をこの client で呼び、LlmGateway の両面の門が `ServiceCaller` である（AST/IADR-0323・AST/IADR-0332）。realm は変えない。
  `DocumentRead` の穴は本追記の許可集合で閉じる。
- **配備**: helm・compose とも BFF の s2s の client は `bff`（`services.bff.serviceToken.clientId` / `ServiceToken__ClientId`）で、既定の許可集合と一致する。
  document-service だけを先に配備してよい（BFF の変更は無い）。BFF の client 名を変える配備は、先に document-service の許可集合へ足すこと ——
  逆順だと BFF の gRPC の読み取りが利用者文脈つきで拒否され、BFF の縮退（一覧は空・詳細は 404）へ静かに落ちる。compose・helm の値の一致は
  `DocumentReadRelayDeploymentWiringTests` が固定する。
- **残るもの**: 本文の `user_id` を `platform-service` の全主体から信じる面は他にもある（`DocumentTagWrite/AddTag`・グラフの問い合わせ・検索の
  `HybridSearch` / `ListValues`・`AuthzScope/Resolve`）。面ごとに呼び出し元が違うので、別の issue で扱う。
