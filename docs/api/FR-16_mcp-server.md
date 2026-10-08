---
title: MCP サーバー 通信仕様書
type: api-spec
status: draft
author: claude
created: 2026-08-23
updated: 2026-10-09
---
<!-- trace:
ids: [FR-15, FR-16, UC-08, UC-09, SC-12]
adrs: [ADR-0004, ADR-0018, ADR-0021, ADR-0024, ADR-0034, ADR-0054, ADR-0062, ADR-0086, ADR-0088, ADR-0117, ADR-0121, ADR-0123]
iadrs: [IADR-0269, IADR-0292, IADR-0297, IADR-0373, IADR-0379, IADR-0462, IADR-0479, IADR-0483, IADR-0516]
specs: [20260823_issue-445_mcp-server-integration, 20260828_issue-1020_internal-mcp-tools, 20260828_issue-452_sc12-mcp-client-management, 20260904_issue-1190_mcp-project-attribute-ban, 20260926_1515_mcp-tool-declarations-grpc, 20260927_issue-1516_mcp-tool-execution-grpc, 20260927_issue-1611_mcp-tool-execution-ports, 20261008_1786_sc12-keycloak-provisioning, 20261009_1817_sc12-provisioning-wiring, 20261009_1818_sc12-idp-drift-detection]
issues: [#445, #452, #1020, #1190, #1514, #1515, #1516, #1517, #1611, #1786, #1817, #1818]
-->

# 通信仕様書: MCP サーバー

## 概要

外部 AI エージェント向けのエッジ集約サービスである。プロトコルは MCP（Model Context Protocol）、
トランスポートは Streamable HTTP。実装は公式 C# SDK を用い、**公開ツールはコードへ固定せず
動的ハンドラで解決する**。入口は Ingress Gateway の `/mcp` パスである。

インターフェースは 3 面ある。

| 面 | 相手 | 用途 |
| --- | --- | --- |
| MCP（`/mcp`） | 外部 AI エージェント | ツール一覧・ツール実行 |
| 管理 REST（`/mcp-clients`） | **境界層（`/bff/admin/mcp-clients`）経由の管理画面**・運用 | クライアント登録・無効化・属性割当・公開ツール一覧 |
| メッシュ内部（各サービスの `/internal/mcp-tools` と、その gRPC 面） | 各マイクロサービス | ツール定義の自己申告（本サービスは**呼ぶ側**）。［2026-09-26 追記］宛先ごとに REST か gRPC を選ぶ（下記「gRPC での収集」） |
| メッシュ内部（各サービスのツール実行の gRPC 面） | ツールを申告したサービス | ［2026-09-27 追記］ツールの実行（本サービスは**呼ぶ側**。下記「ツールの実行（gRPC）」）。［2026-09-28 改訂］受け口は文書・検索・グラフの 3 サービスとも持つ。🔴 文書の受け口は内容の属性による絞り込みの門が閉じている間（既定）は結果を返さず拒否する |

## エンドポイント一覧

| メソッド | パス | 概要 |
| --- | --- | --- |
| （MCP） | `/mcp` | `tools/list` / `tools/call`。認証必須 |
| GET | `/mcp-clients` | 登録クライアント一覧（管理者限定） |
| POST | `/mcp-clients` | クライアント登録（管理者限定） |
| POST | `/mcp-clients/{clientId}/disable` | 無効化（管理者限定） |
| POST | `/mcp-clients/{clientId}/enable` | 再有効化（管理者限定） |
| PUT | `/mcp-clients/{clientId}/attributes` | 属性割当の差し替え（管理者限定） |
| GET | `/mcp-clients/tools` | 実効ツール一覧と構成ドリフト（管理者限定） |

## 管理面への到達経路

**SPA からは境界層（`/bff/admin/mcp-clients*`）経由でのみ到達する。** 境界層は
`/bff/admin` の接頭辞を剥がして本サービスへ透過中継し、**状態コード・本文・Content-Type を
作り替えない**（400 の拒否理由・404 の不在・409 の重複がそのまま画面へ届く）。到達できないときだけ
502 へ縮退する。認可は境界層と本サービスの二重で強制する（利用者の資格情報を伝播する）。

| 境界層 | 本サービス |
| --- | --- |
| `GET /bff/admin/mcp-clients` | `GET /mcp-clients` |
| `POST /bff/admin/mcp-clients` | `POST /mcp-clients` |
| `GET /bff/admin/mcp-clients/tools` | `GET /mcp-clients/tools` |
| `POST /bff/admin/mcp-clients/{clientId}/disable` | `POST /mcp-clients/{clientId}/disable` |
| `POST /bff/admin/mcp-clients/{clientId}/enable` | `POST /mcp-clients/{clientId}/enable` |
| `PUT /bff/admin/mcp-clients/{clientId}/attributes` | `PUT /mcp-clients/{clientId}/attributes` |

### 無人の登録・属性の差し替えは認可サーバーへ書いてから登録簿へ書く（［2026-10-08 追加］）

`POST /mcp-clients`（種別が無人）と `PUT /mcp-clients/{clientId}/attributes`（無人の行）は、検証の後に認可サーバー
（Keycloak の管理 API）へ機密クライアントとサービスアカウントの属性を書き、成功したときだけ登録簿へ書く。
**有人は従来どおり登録簿だけへ書く。** 応答の状態コードは次のとおり。

| 状態 | 意味 | 認可サーバー | 登録簿 |
| --- | --- | --- | --- |
| 201 / 200 | 書けた | 書いた | 書いた（写し） |
| 400 | 検証の外れ。または、この入口が作っていないクライアントが認可サーバーにある（登録では同じクライアント ID、差し替えでは入口の印が無い） | 何も書かない | 書かない |
| 502 | 認可サーバーへの書き込み・照会が失敗した | 作りかけを消す／元の属性へ戻す | 書かない |
| 503 | 書き込み口が構成されていない（`McpClientProvisioning:Provider` 未設定。［2026-10-09］標準の配備〔helm・compose〕は `keycloak` を宣言済み） | 何も書かない | 書かない |
| 500 | 登録簿への書き込みが失敗した | 書いたものを取り消す（現在値が書いた値のままのときだけ戻す） | 書かない |

- 構成: `McpClientProvisioning:Provider`（`keycloak` / `in-memory`。後者は非配備ホスト限定）と
  `McpClientProvisioning:Keycloak:{BaseUrl,Realm,ClientId,ClientSecret}`（既定値なし）。管理用の機密クライアントは
  認可サービスの身元管理用とは別のクライアントであり、`realm-management` の `manage-clients`・`manage-users` だけを持つ。
  ［2026-10-09］配備は realm の `mcp-client-admin` とその secret（`mcp-client-admin-oidc` の `client-secret`。非 optional の参照）で配線した。
- 🔴 **client secret は応答に載せない。**
- 認可サーバーへの要求の期限は `McpClientProvisioning:Keycloak:TimeoutSeconds`（既定 10 秒）。時間切れは 502 であり、作りかけは消す（作成の要求そのものが時間切れになった場合も、引き直して入口の印があれば消す。同じクライアント ID の並行登録や引き直しの失敗では残り得るので、下の定期の照合が拾う）。要求を途中で取り消しても、認可サーバーへの書き込みと取り消しは最後まで走る。
- 登録簿で無効化された行の差し替えで認可サーバーにクライアントを作るときは、無効のまま作る。
- ［2026-10-09 / #1818］**登録簿と認可サーバーの定期の照合**（読むだけで書かない）: 起動時と `McpClientProvisioning:Reconciliation:Interval`（既定 `00:01:00`・下限 1 分）ごとに、
  認可サーバーのクライアントの一覧（`GET /admin/realms/{realm}/clients?first=&max=100`。入口の印の有無）と、無人の行ごとのサービスアカウントの属性
  （`GET /admin/realms/{realm}/users?username=service-account-<client>&exact=true`。認可サービスと同じ照会）を管理用の資格情報で読む。
  1 回の照合の期限は周期と同じ長さ、並行は 4 要求まで。計器はゲージ `mcp.idp_reconciliation.drifted`（失敗・未照合は系列なし）と
  カウンタ `mcp.idp_reconciliation.checks.total{mcp.idp_reconciliation.outcome=match|drift|failed}`（Meter `microservices-platform.mcp-server`）。
  警報と対応は運用仕様書の「MCP クライアント登録簿と認証基盤の照合」。
- 🔴 境界層は状態コードを作り替えないので 502 は画面へそのまま届くが、境界層自身の不達も 502 であり区別できない。

**メッシュ内の Service 名は `mcp-service` である**（配備の chart キーは `mcp`。テンプレートが
`-service` を付す）。境界層のコード既定もこの名前に揃えてあり、配備 manifest 側の上書きは持たない。
| GET | `/internal/introspection` | 自己申告（メッシュ内部限定） |
| gRPC | `platform.introspection.v1.ServiceIntrospection/Get` | ［2026-09-26 追記］自己申告の east-west gRPC 面（共通基盤が REST と対で張る。呼び出し側サービスの資格情報を要求する）。🔴 本サービスは構成情報 API の収集先に無いので h2c リスナを立てておらず、配備上は呼ばれない |
| GET | `/health/live`・`/health/ready` | ヘルスチェック |

## MCP 面

### `tools/list`

公開構成（許可リスト）と各サービスの自己申告を突合した**実効ツール一覧**を返す。
**既定は非公開**であり、構成に明記されたツールだけが現れる。
未登録・無効化されたクライアントには**空の一覧**を返す。

### `tools/call`

手順は次のとおりである。**ツール名で分岐しない**（統制の適用点を 1 本に保つ）。

1. トークンからクライアントを特定し、登録簿と突合する。未登録・無効化は拒否する
2. 実効ツール一覧に無ければ **「不明なツール」** として拒否する（「権限が無い」と区別させない）
3. 実行スコープを組む。**主体がサービスアカウントなら個人資料の除外制約を立てる**
4. **ツールを申告したサービス**へ、申告名で実行を送る（gRPC。下記「ツールの実行（gRPC）」）。🔴 **宛先は申告したサービスとツール名だけで決め、申告の中身の URL は使わない**。
   実行できない（受け口が無い・経路が無い・時間切れ・拒否・受け口の前提が満たされていない）ときは結果を返さず拒否する（fail-closed）
5. 応答から**個人資料と、MCP から外すプロジェクトの文書を除く**（サービスアカウントのとき。
   件数からも外す）。🔴 **除外はこの後段だけが行う。** 属性を付けても認可判定は同属性を見ないため、
   割当の禁止（下記）だけでは到達は止まらない
6. データ越境ポリシーを文書単位に適用し、送信不可の文書は本文を落として参照リンクのみにする
7. 監査ログへ記録する（主体・種別・クライアント・ツール・引数長・返却件数・全体件数）

## ツール定義の自己申告（各サービスが実装する側）

`GET /internal/mcp-tools` は次を返す。メッシュ内部限定であり Ingress へ公開しない。

```json
{
  "service": "retrieval-service",
  "tools": [
    {
      "name": "retrieval.search_documents",
      "description": "エージェント向けの説明文（いつ・何のために呼ぶか）",
      "input_schema": "{\"type\":\"object\"}",
      "required_scope": "retrieval:search",
      "egress_class": "internal"
    }
  ]
}
```

- ［2026-09-27 改訂］**ツール定義の規約は 5 項目である**（`name` / `description` / `input_schema` / `required_scope` / `egress_class`）。
  実行先の URL（旧 `endpoint`）は規約から外した —— 実行先は「申告したサービス（`service`）＋ツール名（`name`）」で決まり、
  URL を申告に持たせると、あるサービスが別のサービスの内部経路を自分のツールとして申告できてしまう。
  旧い申告元が `endpoint` を載せても本サービスは読み飛ばし、**その URL へ接続しない**。
- 🔴 **申告の `service` は、収集先の名前（`Mcp:Services` / `Mcp:GrpcServices` のキー）と一致しなければならない。** 一致しない申告は
  **拒否して公開しない**（書き換えない。エラーとして記録し、公開構成が要求していれば「申告なし」の構成ドリフトになる）。
  申告元は、自分が集められた名前でしかツールを公開できず、他のサービスの名でツールを公開したり実行先を向けたりできない。
- 同じサービスが同じツール名を 2 度申告した場合は、そのツールを公開しない（どちらかを推測しない）。構成ドリフトとして現れ、他のツールには影響しない。
- `egress_class` は必須である。欠けた申告は**公開しない**。
- `service` は自己申告（`/internal/introspection`）と同じサービス名を使う。公開構成の `service` は
  この値と突き合わせるため、綴りが割れると申告が見つからず構成ドリフトになる。
- 収集は起動時と定期（既定 5 分間隔）に行う。到達できないサービスは「申告なし」として扱い、
  公開構成が要求していれば構成ドリフトとして報告する。**推測で公開しない。**
- 端点は認可を要求しない。`/internal/introspection` と同じ防御（ネットワーク分離・相互 TLS）に置き、
  OpenAPI の記述からも外す。**画面向け集約の契約ではないため、その定義ファイルには現れない。**

### gRPC での収集（［2026-09-26 追記］）

申告の口は **REST と gRPC を対で**持つ（申告を張る唯一の口が両方を張る。張り忘れた宛先は本サービスからは
「申告なし」としか見えないため）。gRPC の面は `platform.mcp.v1.McpToolDeclarations/Declare` で、REST と**同じ 1 つの申告**を
返す（申告を組み立てる関数は 1 つ。個人資料の除外も同じ経路を通る）。

| 項目 | 値 |
| --- | --- |
| 契約 | 共有契約の proto（`platform/mcp/v1`）。**申告スキーマの共有契約への昇格はこの proto で行った**。REST の JSON と項目名・数が一致する（試験で固定）。［2026-09-27］旧 `endpoint`（番号 4）は番号と名前を予約に残した（再利用しない） |
| 切替 | **宛先ごと**の `Mcp:GrpcServices:<サービス名>`（h2c のアドレス）。在る宛先だけが gRPC、無い宛先は `Mcp:Services` の REST のまま（両方に在れば gRPC）。戻すのは 1 行を消すだけ |
| 認証・認可 | 呼び出し側サービスの資格情報（`platform-service`）を要求する。利用者のトークンは管理者でも通らない。REST の端点は認可を要求しないので**狭まる向き**である |
| 資格情報 | 本サービス自身のサービス間トークン（realm の `mcp-server` client。認可サービスの gRPC 経路と同じもの） |
| 期限 | REST の HTTP クライアントのタイムアウトと**同じ値**（書き写さない） |
| 失敗 | REST と同じく「申告なし」へ畳む（全 status・期限切れ・トークン取得失敗・空の `service`）。**推測で公開しない**。資格情報の拒否とトークン取得失敗は配線不備として Error、ほかは Warning |
| 配備 | helm・compose の本サービスに 3 宛先（文書・検索・グラフ）の gRPC アドレスを入れた。**並走中の正は REST** |

- ［2026-09-27 改訂］従前ここに書いた「`endpoint` は文字列のまま運ぶ。gRPC で実行するときの扱いは未決」は解消した。`endpoint` は規約から外した（上記）。
- REST の口の退役は、他の経路と同じ段でまとめて行う（それまで REST の口と `Mcp:Services` は残す）。

### 供給元と申告するツール

| サービス | 申告するツール |
| --- | --- |
| `document-service` | `document.get_document` / `document.list_documents` |
| `retrieval-service` | `retrieval.search_documents` |
| `graph-service` | `graph.get_backlinks` / `graph.get_links` / `graph.traverse` |

**個人資料を対象に含むツールは申告しない。** 候補としては持ち、申告を組み立てる 1 経路で落とす ——
「思い付かなかったから無い」と「規則で落としている」を読み分けられるようにするためである。
要約系（クラスタ要約）と AI 分析系も申告しない。

## ツールの実行（gRPC）（［2026-09-27 改訂］）

本サービスはツールの実行を **gRPC（h2c）** で、**ツールを申告したサービス**へ送る。従前の「申告の `endpoint` の URL へ REST で POST する」経路は廃した
（どのサービスも受け口を持たず、常に失敗していた）。

| 項目 | 値 |
| --- | --- |
| 面 | `platform.mcp.v1.McpToolExecution/Execute`（共有契約の proto `platform/mcp/v1`） |
| 宛先 | 🔴 **公開構成で申告を突き合わせたサービス**の h2c アドレス（申告の収集と同じ `Mcp:GrpcServices:<サービス名>`）。要求の `tool` は**申告名**（公開名ではない）。**申告の中身から宛先を作らない**（申告の `service` は収集先の名前と一致したものだけが残るので、宛先は常に申告元自身になる） |
| 資格情報 | 本サービス自身のサービス間トークン（申告の収集と同じ `mcp-server` client）。利用者のトークンは運ばない |
| 期限 | `Mcp:ToolExecutionTimeoutSeconds`（既定 30 秒、1 未満は 1 秒）。常に有限。申告の収集の期限とは別の値である |
| リトライ | 持たない（ツールの実行は冪等とは限らない） |
| 失敗 | 🔴 **fail-closed。** 経路が構成されていない・受け口が無い（`UNIMPLEMENTED`）・期限切れ・拒否・トークン取得失敗・到達不能は、結果を 1 件も返さず MCP クライアントへ拒否を返す。拒否の文言は内部の宛先（サービス名・アドレス）を含めない。ログは資格情報の拒否とトークン取得失敗を Error、ほかを Warning |
| 取り消し | 呼び出し側の取り消しは拒否へ畳まず、取り消しとして外へ出す |
| 配備 | 新しい構成は無い（`Mcp__GrpcServices__*` とサービス間トークンは申告の収集のために既に在る）。期限のキーは本サービスの構成ファイルに既定値で並べた |

> 🔴 **受け口（各サービスの実行口）は、文書・検索・グラフの 3 サービスとも持つ**（［2026-09-28 改訂］）。受け口の無い宛先
> （旧い版・将来の供給元）は `UNIMPLEMENTED` を返し、実行は拒否で終わる。
> 🔴 **文書の受け口は、内容の属性による絞り込み（門）が閉じている間（既定）は `FAILED_PRECONDITION` で結果を返さない**（［2026-09-28 裁定］）。
> 閉じている間の文書サービスの判定は組織文書を内容の属性で絞らず、その間の組織文書は BFF の判定だけが守る。MCP の経路は BFF を通らないので、
> 閉じたまま答えると画面では見えない機密・制限の組織文書が引けてしまう。MCP サーバーはこの status を「実行できない」として拒否する
> （既定の枝。結果 0 件・Warning）。門が開いた後は既存の読み取りの判定点をそのまま通し（認可サービスの読み取りの分岐）、
> 文書サービスの REST の同じ利用者の結果を超えない。これは判定の点を増やすのではなく、経路の開閉である。
> 応答は題名と許可リストの属性だけで、本文・参照リンクは持たない。

### 要求と応答（［2026-09-27 改訂］）

- 要求: `tool`（申告名）・`arguments_json`（MCP クライアントの引数の JSON。文字列のまま）・`user`（**利用者文脈**。利用者名と操作だけ）。
  - 🔴 **解決済みの実行スコープは運ばない。** 旧い `scope`（主体・種別・属性・除外制約・必要スコープ）は番号ごと予約へ移した。
    受けたサービスが利用者文脈で認可サービスへ判定を問い、**自分で**認可する。利用者の属性は認可サービスが引き直す。
  - 利用者名は、有人なら利用者の `preferred_username`（`sub` ではない）、サービスアカウントなら `service-account-<client>`（小文字）。
    受け手はこの接頭辞からサービスアカウント実行を見分け、個人資料を落とす（要求側の 1 層目。本サービスの応答側のフィルタは 2 層目として残る）。
  - 操作は公開ツールがすべて読み取りなので `read`。受け手は自分のツールが要する操作と突き合わせ、違えば拒否する。
  - 有人で利用者名が無い主体の実行は、下流へ送らずに拒否する。
- 受け口の要件（各サービス）: `ServiceCaller` を要求し、**本文の利用者文脈を信じるのは呼び出し元が MCP サーバーのときだけ**
  （許可集合。既定 `mcp-server`。構成キー `McpToolExecution:TrustedUserContextClients`、配列で書く）。
  それ以外は `PERMISSION_DENIED`、利用者文脈の欠落・引数の不正は `INVALID_ARGUMENT`、自分の申告に無いツールは `NOT_FOUND`。
- 応答: 次の共通エンベロープ（REST の JSON と同じ綴り。試験で一致を固定）。**文書単位の統制を成立させるための規約**である。

```json
{
  "documents": [
    {
      "document_id": "…",
      "title": "…",
      "attributes": { "doc_scope": "organization", "confidentiality": "internal" },
      "body": "本文（越境不可なら本サービスが落とす）",
      "reference_url": "https://wiki.internal/…"
    }
  ],
  "total_count": 1,
  "truncated": false
}
```

- `total_count` は**認可判定を通したあとの件数**である。権限外・除外対象を含めてはならない。
- `body` / `reference_url` は省略し得る（proto では有無を運ぶ）。

## 宣言的公開構成

Git 管理の JSON を `Mcp:PublicationConfigPath` で指す。**検証を通らない構成は適用しない**
（起動時に失敗させる）。

```json
{
  "version": "2026-08-23",
  "tools": [
    { "name": "retrieval.search_documents", "service": "retrieval", "published_name": "search_documents" }
  ],
  "service_account_attributes": {
    "batch-agent": { "doc_scope": "organization", "confidentiality": "internal" }
  }
}
```

検証項目: 公開名の一意性／サービス名の指定／初期公開範囲外（AI 分析系・要約系）の拒否／
**サービスアカウントへ個人資料を読ませる属性割当の拒否**／**サービスアカウントへ MCP から外す
プロジェクト（`projects` / `project` の値）を読ませる属性割当の拒否**。

**違反は 1 件目で打ち切らずすべて返す。** 打ち切ると 2 件目は 1 件目を直したあとの実行でしか
現れない。

## 管理 REST 面

いずれも管理者ロールを要求する。登録要求の `kind` は `interactive`（有人）または
`service-account`（無人）、`egressTier` は `self-hosted` / `protected-external` /
`standard-external`（未指定は最も低い保護水準へ倒す）。

**サービスアカウントに対して個人資料を読ませる属性割当と、MCP から外すプロジェクトを読ませる
属性割当は、登録時も差し替え時も拒否する。** 検証は宣言的公開構成と同じ 1 つの関数を通る
（2 か所へ書くと片方だけが緩む）。**拒否理由には外れた値を名指しで載せる。**

## 認証・認可

- 認証は OAuth 2.1（Keycloak）。有人は Authorization Code + PKCE、無人は Client Credentials。
- **主体種別はトークンではなく登録簿から採る。** クライアント側の申告で除外の適用対象から
  外れられないようにするためである。
- 本サービスは認可判定を持たず、各サービスへ委譲する。エージェント経由であることを理由に
  権限を拡張しない。

## 関連仕様

- [MCP サーバー 権限・認可仕様書](../authz/FR-16_mcp-server.md)
- [MCP サーバー統合 テスト仕様書](../tests/FR-16_mcp-server.md)
