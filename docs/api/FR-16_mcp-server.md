---
title: MCP サーバー 通信仕様書
type: api-spec
status: draft
author: claude
created: 2026-08-23
updated: 2026-08-29
---
<!-- trace:
ids: [FR-15, FR-16, UC-08, UC-09, SC-12]
adrs: [ADR-0004, ADR-0018, ADR-0021, ADR-0024, ADR-0034, ADR-0054]
iadrs: [IADR-0269, IADR-0292, IADR-0297]
specs: [20260823_issue-445_mcp-server-integration, 20260828_issue-1020_internal-mcp-tools, 20260828_issue-452_sc12-mcp-client-management]
issues: [#445, #452, #1020]
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
| メッシュ内部（各サービスの `/internal/mcp-tools`） | 各マイクロサービス | ツール定義の自己申告（本サービスは**呼ぶ側**） |

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

**メッシュ内の Service 名は `mcp-service` である**（配備の chart キーは `mcp`。テンプレートが
`-service` を付す）。境界層のコード既定もこの名前に揃えてあり、配備 manifest 側の上書きは持たない。
| GET | `/internal/introspection` | 自己申告（メッシュ内部限定） |
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
4. 申告された実行口（`endpoint`）へ委譲する
5. 応答から**個人資料を除く**（サービスアカウントのとき。件数からも外す）
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
      "endpoint": "http://retrieval-service:8080/internal/mcp/search_documents",
      "required_scope": "retrieval:search",
      "egress_class": "internal"
    }
  ]
}
```

- `egress_class` は必須である。欠けた申告は**公開しない**。
- `service` は自己申告（`/internal/introspection`）と同じサービス名を使う。公開構成の `service` は
  この値と突き合わせるため、綴りが割れると申告が見つからず構成ドリフトになる。
- 収集は起動時と定期（既定 5 分間隔）に行う。到達できないサービスは「申告なし」として扱い、
  公開構成が要求していれば構成ドリフトとして報告する。**推測で公開しない。**
- 端点は認可を要求しない。`/internal/introspection` と同じ防御（ネットワーク分離・相互 TLS）に置き、
  OpenAPI の記述からも外す。**画面向け集約の契約ではないため、その定義ファイルには現れない。**

### 供給元と申告するツール

| サービス | 申告するツール |
| --- | --- |
| `document-service` | `document.get_document` / `document.list_documents` |
| `retrieval-service` | `retrieval.search_documents` |
| `graph-service` | `graph.get_backlinks` / `graph.get_links` / `graph.traverse` |

**個人資料を対象に含むツールは申告しない。** 候補としては持ち、申告を組み立てる 1 経路で落とす ——
「思い付かなかったから無い」と「規則で落としている」を読み分けられるようにするためである。
要約系（クラスタ要約）と AI 分析系も申告しない。

## ツール実行口の応答エンベロープ（各サービスが実装する側）

申告した `endpoint` は次の形で応答する。**文書単位の統制を成立させるための規約**である。

> 🔴 **本規約の実体はまだどのサービスにも無い。** 実行時の権限伝播の方式（呼び出し元の資格情報を
> 運ぶか、内部専用の別経路にするか）が未決であり、本文で渡された主体情報を信じる形で実装すると
> そこへ到達できる誰もが任意の主体を名乗れる。方式が決まるまで実装しない。

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
- 要求のボディは `{ "scope": { … }, "arguments": { … } }` である。`scope` は主体・種別・属性・
  **個人資料の除外制約**・必要スコープを運ぶ。

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
**サービスアカウントへ個人資料を読ませる属性割当の拒否**。

## 管理 REST 面

いずれも管理者ロールを要求する。登録要求の `kind` は `interactive`（有人）または
`service-account`（無人）、`egressTier` は `self-hosted` / `protected-external` /
`standard-external`（未指定は最も低い保護水準へ倒す）。

**サービスアカウントに対して個人資料を読ませる属性割当は、登録時も差し替え時も拒否する。**

## 認証・認可

- 認証は OAuth 2.1（Keycloak）。有人は Authorization Code + PKCE、無人は Client Credentials。
- **主体種別はトークンではなく登録簿から採る。** クライアント側の申告で除外の適用対象から
  外れられないようにするためである。
- 本サービスは認可判定を持たず、各サービスへ委譲する。エージェント経由であることを理由に
  権限を拡張しない。

## 関連仕様

- [MCP サーバー 権限・認可仕様書](../authz/FR-16_mcp-server.md)
- [MCP サーバー統合 テスト仕様書](../tests/FR-16_mcp-server.md)
