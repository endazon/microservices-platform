---
title: gRPC の AddTag・ExpandNeighbors・ListValues で本文の利用者文脈を信じる呼び出し元を構成の許可集合に絞る —— AuthzScope/Resolve は受け入れの範囲を確かめて据え置く（#1636 段 1）
type: spec
status: done
related_ids: [FR-04, FR-05, FR-17, FR-18, NFR-09, SC-01, SC-05, ADR-0086, ADR-0088, ADR-0063, ADR-0034, ADR-0119, IADR-0410, IADR-0417, IADR-0413, IADR-0401, IADR-0420, IADR-0426, IADR-0476]
author: claude
created: 2026-09-27
updated: 2026-09-27
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0086_user-context-in-body-not-token-exchange.md 決定 1・決定 4・§結果（中継サービスが正直であることへの依存）
  - planning:projects/microservices-platform/07_adr/ADR-0088_authz-resolves-user-attributes-itself.md 決定 1・決定 2・決定 4（`user_id` の詐称は残る半分）
  - planning:projects/microservices-platform/07_adr/ADR-0063_tag-suggestion-reflection-and-approval-authz.md 決定 3（①所有者 または ②管理者）
issue: "#1636"
---

# 仕様書: gRPC の 3 面で本文の利用者文脈を信じる呼び出し元を絞る（#1636 段 1）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/microservices-platform/`）を一次情報とする。

## 起点となる計画書（トレーサビリティ）

- 要求: **NFR-09**（全 API で文書・データ単位の認可）、FR-18 / SC-05（AI タグ提案の承認）、FR-04 / FR-17（近傍展開・グラフ）、FR-05（ABAC）、SC-01（検索の属性の候補）
- 関連 ADR:
  - **ADR-0086 決定 1**（利用者の権限で動く east-west は利用者文脈を本文で運ぶ）と §結果「中継サービスが正直であることへの依存」、**決定 4**（`AuthzScope/Resolve` の主張の評価を受け入れたリスクとして記録）
  - **ADR-0088 決定 1・2・4**（認可サービスが属性を引き直す／両面に呼び出し元の資格を求める／`user_id` の詐称は残る半分で、閉じる手段は token exchange だけ）
  - ADR-0063 決定 3（タグ反映の認可は ①所有者 または ②管理者ロール）、ADR-0034 決定 1（ホップごとの判定）、ADR-0119 決定 3（判定の主体）
- 関連 IADR: **IADR-0410**（`AddTag`・`ExpandNeighbors` の gRPC 面。本件の追記 1 を置く）、**IADR-0417**（`ListValues` の gRPC 面。追記 1 を置く）、
  **IADR-0413**（`AuthzScope/Resolve` の引き直しと REST 面の認可。据え置きの記録を追記する）、IADR-0401 決定 2（残余リスクの自認）、IADR-0420（`MachinePrincipal`）
- 先行の同型: #1628（PR #1631、`DocumentRead`。IADR-0476 追記）・#1635（PR #1638、`DocumentSearch`。IADR-0426 追記 1）。両方とも develop に在る。
- 起点 issue: #1636（PR #1631 の監査で見つかった同型の 2〜5 位）

## 目的・背景

`ServiceCaller`（realm ロール `platform-service`）は 11 のサービスアカウント（別プロジェクト AST の `ai-stock-trading-llm-caller` を含む）が持つ。
次の 3 面は、その門だけで本文の利用者文脈を信じていた。

| 順位 | 面 | 正規の呼び出し元（クライアント） | 漏れるもの |
|---|---|---|---|
| 2 | `DocumentTagWrite/AddTag`（DocumentService） | GraphService の `GrpcDocumentTagWriter`（`graph-service`） | 本文の `user_roles` も信じるため、`["platform-admin"]` を名乗ると管理者の上書きで任意の組織文書にタグを書ける。存在の手掛かりにもなる |
| 3 | `GraphNeighbors/ExpandNeighbors`（GraphService） | RetrievalService の `GrpcGraphNeighborExpander`（`retrieval-service`） | 名乗った利用者のスコープの辺（文書 ID・辺の種類） |
| 4 | `AttributeValues/ListValues`（RetrievalService） | BFF の `AttributeValuesGrpcClient`（`bff`） | 名乗った利用者のスコープの属性の値 |
| 5 | `AuthzScope/Resolve`（AuthorizationService） | 資源サービス 5 と BFF | 任意の利用者の ABAC スコープ |

## 段の分け方（2 本の PR）

- **段 1（本仕様書・`Refs #1636`）**: 2〜4 位に #1628 / #1635 と同じ形の許可集合を当てる。5 位は受け入れの範囲を確かめ、据え置きを記録する。
  `AddTag` の本文の `user_roles` は、**段 1 の後は許可集合の中継者（graph-service）が運んだときだけ**効く（誰でも名乗れる状態は段 1 で閉じる）。
- **段 2（別 PR・`Closes #1636`）**: `AddTag` が本文の `user_roles` を信じること自体をやめ、管理者かどうかを認可サービスに引き直させる（下の「段 2 の方針」）。
  認可サービスの面（`UserDirectory`）への rpc の追加と DocumentService の新しい依存を伴い、段 1 と性質が違うので分ける。

## 設計（段 1）

### 1. 判定と拒否（3 面とも同じ）

- 本文の利用者文脈を信じるのは、呼び出し元が **機械の主体**（`MachinePrincipal.IsMachine`）で、**クライアント識別**（`MachinePrincipal.ClientIdOf`。
  `azp` を第一に、無ければ `service-account-<clientId>` から復元）が面ごとの許可集合に**序数一致**で含まれるときだけ。
- 許可集合に無い呼び出し元が利用者文脈を付けたら **`PERMISSION_DENIED`**（機械の主体として読み替えない。#1628 / #1635 と同じ判断）。
  拒否はクライアント識別だけを警告ログに残す。
- **判定の順**: ① 要求の形の誤り（GUID・`user_id` の空）→ `INVALID_ARGUMENT`（従来どおり・呼び出し元を問わない。何も漏れない）、
  ② 信頼しない呼び出し元 → `PERMISSION_DENIED`、③ 以降（入力検証・スコープ解決・文書の取得）。
  - 3 面とも **`user_id` の無い要求は元々 `INVALID_ARGUMENT`** で、機械の視野の口を持たない（`DocumentSearch` と同じ。#1628 の `DocumentRead` とは違う）。
    したがって「利用者文脈の無い要求は機械の主体」は、この 3 面では「元々受けない」を変えない形で満たす（**広げない**）。
  - `AddTag` はタグ名の検証（③）より前に②を置く。`ListValues` は空の `key` の早期 return より前に②を置く（信頼しない呼び出し元に「通る形」を残さない）。
- `AddTag` の拒否は `NOT_WRITABLE` へ畳まない（中継者の構成誤りが「その文書は書けない」に化けるため）。文書を引く前なので実在は漏れない。
  proto の「`PERMISSION_DENIED` は s2s の門だけが返す」は「＋許可集合に無い呼び出し元が利用者文脈を付けたとき」へ改める。

### 2. 構成

| 面 | 節（キー） | 既定 |
|---|---|---|
| `AddTag` | `DocumentTagWrite:TrustedUserContextClients` | `graph-service` |
| `ExpandNeighbors` | `GraphNeighbors:TrustedUserContextClients` | `retrieval-service` |
| `ListValues` | `AttributeValues:TrustedUserContextClients` | `bff` |

- **未構成なら既定。構成すると既定を置き換える**（.NET の配列の束縛は初期値に追記するので、既定は null にして読み出し側で解決する）。
- 空白だけの要素は捨て、1 つも残らなければ誰も信じない（fail-closed）。
- **1 つの値（配列でない）で構成されていたら起動時に例外**（#1631 の `ThrowIfScalar` と同じ。1 つの値は束縛されず既定へ静かに戻るため）。
- 節名は gRPC のサービス名（#1631 の `DocumentRead:`・#1635 の `DocumentSearch:` と同じ命名）。**キーも集合も面ごとに独立**
  （RetrievalService では `DocumentSearch:`（既定 `aianalysis-service`）と `AttributeValues:`（既定 `bff`）が並ぶ）。

### 3. 共有の小さな関数

- 判定・既定の解決・構成の形の検査の 3 関数を `Platform.Shared.Infrastructure` の `TrustedUserContextRelay`（`MachinePrincipal` の隣）に置く。
  面ごとの `*RelayOptions` は節名・既定・3 関数への委譲だけを持つ（今回 3 面ぶん写すと写しが 5 つになるため）。
- **#1631 / #1638 の `DocumentReadRelayOptions` / `DocumentSearchRelayOptions` は変えない**（同じ値を返すが、寄せるのは本件の射程外。残るものへ）。

### 4. AuthzScope/Resolve（5 位）は変えない

- **ADR-0086 決定 4** は「`AuthzScope/Resolve` が `user_id` / `user_attributes` を呼び出し元の本文から受け取り、Keycloak を引き直さずに評価する」ことを受け入れたリスクとして記録した。
- **ADR-0088** がその半分（偽の属性）を閉じ（IADR-0413 で着地）、**決定 4 が残る半分（他人の `user_id` を名乗る）を明示的に残した**。
  そして決定 2 は REST 面にも呼び出し元の資格（`ServiceCaller`）を求め、「詐称できる主体は `platform-service` を持つサービスに限られ、gRPC 面と同じ水準に揃う」とした。
  閉じる手段は token exchange だけで、ADR-0086 決定 2 が今は採らないと定めている。
- したがって「`platform-service` を持つ主体が任意の利用者のスコープを引ける」は**計画が受け入れた範囲そのもの**であり、計画の裁定なしに狭める根拠も、狭めねばならない根拠も無い。
  - 許可集合を当てることは技術的には可能だが（呼び出し元は BFF・AI 分析・グラフ・Wiki・MCP・検索・文書の 7 クライアント。資源サービスが自分のために解決する経路もある）、
    それは ADR-0088 決定 4 の「`platform-service` を持つサービスに限られる」という水準の選択を実装で動かすことになる。本件では行わない。
- 本件で面 2〜4 を閉じても、**攻撃者は Resolve で被害者のスコープ（属性の形）を知ることはできるが、それを使って資源（辺・属性値・タグの書き込み）を引く口は
  許可集合の中継者に限られる** —— Resolve の漏れは「スコープの記述」に留まる。これを IADR-0413 の追記と issue に記録する。

## 段 2 の方針（本 PR では実装しない。判断だけ先に置く）

- **管理者の上書きは外さない。** ADR-0063 決定 3 の②は取り込み文書（`owner=system`）を承認できる唯一の枝であり（①では誰も書けない）、
  helm・compose とも graph-service はタグの反映を gRPC で呼ぶ（`Services__DocumentServiceGrpc`）。外すと SC-05 の管理者の承認が配備で壊れる。
- **ロールは認可サービスが引き直す**（ADR-0088 決定 1 の原則を realm ロールへ延ばす）。本文の `user_roles` は `user_attributes` と同じ「呼び出し元の主張」であり、
  ADR-0088 が閉じた「偽の属性を主張する」の半分に当たる。IdP を引けるのは認可サービスだけ（IADR-0329 決定 1）なので、DocumentService は
  `UserDirectory` の狭い問い（「この利用者はこのロールを持つか」）で引く。所有者の枝で書けるときは引かない（往復を増やさない）。
- 段 2 の後も、`user_id` 自体は許可集合の中継者の主張のまま残る（ADR-0088 決定 4 と同じ残り方）。

## 受け入れ基準（段 1）

- AC-1: 各面で、許可集合に無い `platform-service` の主体（`ai-stock-trading-llm-caller`・`bff`/`graph-service`/`retrieval-service` のうち正規でないもの・`mcp-server`・`document-service` など）が利用者文脈を付けると `PERMISSION_DENIED`。
  `AddTag` では文書は変わらない。`ExpandNeighbors` / `ListValues` ではスコープ解決が呼ばれない。
- AC-2: クライアント識別の接頭辞・大小文字の変種は信じない（`PERMISSION_DENIED`）。
- AC-3: 利用者名が `service-account-<正規>` で `azp` が別のトークンは信じない。
- AC-4: 人のトークン（`azp=<正規>` と `platform-service` を持っていても）は信じない。
- AC-5（陽性対照）: 正規の呼び出し元（実トークンの形 ＝ graph・retrieval は利用者名なし・`azp` あり、BFF は `service-account-bff` と `azp=bff`）は従来どおり動く。
- AC-6: `user_id` の無い要求は呼び出し元を問わず `INVALID_ARGUMENT`（広げない）。
- AC-7: 構成は未構成なら既定・構成すると置き換え・空白だけは誰も信じない・1 つの値は起動時に例外。
- AC-8: 配備の固定: compose・helm の正規の呼び出し元の s2s の client が既定の集合に入り、realm にその機密クライアントのサービスアカウントが
  `platform-service` 付きで在り、呼び出し元がその面の宛先を配線しており、配備ファイルは集合を上書きしない。
- AC-9（`AddTag`）: 許可集合に無い呼び出し元が `user_roles=["platform-admin"]` を名乗っても管理者の上書きは起きない（`PERMISSION_DENIED`・文書は変わらない）。
- AC-10: 変異試験: (a) 呼び出し元の確認を落とす (b) 序数一致を接頭辞一致に変える (c)〔段 2〕本文のロールを再び信じる、で試験が落ちる。
- AC-11: 既存の試験（`GrpcDocumentTagWriteTests`・`GrpcGraphNeighborsTests`・`GrpcAttributeValuesTests`・呼び出し元側の `GrpcDocumentTagWriterTests`・`GrpcGraphNeighborExpanderTests`・`BffAttributeValuesGrpcTests`）が通る。

## 呼び出し元の母集合（着手前に自分で引いた）

### 引き方

- `grep -rln "DocumentTagWrite\|GraphNeighbors\|AttributeValues\.AttributeValuesClient\|AttributeValuesClient\|GraphNeighborsClient\|DocumentTagWriteClient" --include=*.cs --include=*.ts --include=*.py --include=*.go src`
  （`/obj/`・`/bin/` を除く）—— 両ユニットと submodule（`src/ai-stock-trading`。`git submodule update --init` 後）。
- 隣接クローン `../ai-stock-trading`（読み取りのみ）で `DocumentTagWrite|GraphNeighbors|AttributeValues|knowledge.graph.v1|knowledge.retrieval.v1|knowledge.document.v1`。
- `grep -n "DocumentServiceGrpc\|GraphServiceGrpc\|RetrievalServiceGrpc" deploy/docker-compose.yml deploy/helm/microservices-platform/values.yaml`
- realm（`deploy/keycloak/microservices-platform-realm.json`）の `clients[]`（`serviceAccountsEnabled`・`publicClient`・`defaultClientScopes`）と `users[].serviceAccountClientId`・`realmRoles`。
- compose の `ServiceToken__ClientId`、helm の `serviceToken.clientId`。

### 結果

- `AddTag` の gRPC クライアント: **GraphService の `GrpcDocumentTagWriter` だけ**（`AddDocumentTagWriteGrpcClient`。`Services:DocumentServiceGrpc` が在るときだけ）。
  helm `services.graph.extraEnv` と compose `graph-service` がともに `Services__DocumentServiceGrpc: http://document-service:8081` を持つ（配備で gRPC 経路）。
  graph の s2s の client: helm `services.graph.serviceToken.clientId: graph-service`・compose `ServiceToken__ClientId: graph-service`。
- `ExpandNeighbors` の gRPC クライアント: **RetrievalService の `GrpcGraphNeighborExpander` だけ**（`Services:GraphServiceGrpc`）。
  helm `services.retrieval.extraEnv`・compose `retrieval-service` がともに `Services__GraphServiceGrpc: http://graph-service:8081`。
  retrieval の s2s の client: helm `retrieval-service`・compose `retrieval-service`。
- `ListValues` の gRPC クライアント: **BFF の `AttributeValuesGrpcClient` だけ**（`Services:RetrievalServiceGrpc`）。helm `services.bff.extraEnv`・compose `bff` がともに
  `Services__RetrievalServiceGrpc: http://retrieval-service:8081`。BFF の s2s の client: helm `services.bff.serviceToken.clientId: bff`・compose `ServiceToken__ClientId: bff`。
- その他のヒットは RetrievalService / GraphService / DocumentService 自身・試験・REST 実装（`HttpDocumentTagWriter`）・同じ面の別 rpc（`ListEdgeTypeWeights`）・
  注記（`GrpcRagSearchTransport` のコメント・`GrpcPrivateNoteNotifier` のコメント）・REST の名前（`GraphNeighborsEndpoint`・`BffGraphNeighbors`）・フロントの生成物（REST の `/bff/graph/*`）。
- AST（submodule・隣接クローン）: 3 面のどれも呼ばない（ヒット 0）。
- realm: `graph-service`・`retrieval-service` は機密・サービスアカウント有効・既定スコープ `roles` のみ（**実トークンは `preferred_username` を持たず `azp` だけ**）。
  `bff` は機密・サービスアカウント有効・既定スコープ `profile,email,roles,abac-attributes`（`preferred_username = service-account-bff` と `azp=bff`）。
  3 つのサービスアカウントとも `platform-service` を持つ。
- `platform-service` を持つサービスアカウント 11: bff・ai-stock-trading-llm-caller・aianalysis-service・graph-service・conversion-service・retrieval-service・
  ingestion-service・wiki-service・datasource-service・mcp-server・document-service。

### 除外したもの

- REST の 3 口（`POST /documents/{id}/tags`・`GET /graph/neighbors/...`・`POST /search/attribute-values`）: 主体は呼び出し元の資格情報そのもの（本文の主張を権限の根拠にしない）。
- `GraphNeighbors/ListEdgeTypeWeights`: 利用者文脈を持たない描画用の語彙。
- `AuthzScope/Resolve`: 上の設計 4 のとおり据え置き（記録のみ）。

## 配備の順番（段 1）

- 3 面とも既定の許可集合は配備の正規の呼び出し元の client と一致するので、**受け口のサービス（document-service・graph-service・retrieval-service）だけを先に配備してよい**。呼び出し元の変更は無い。
- 呼び出し元の client 名を変える配備は、**先に受け口の許可集合へ足すこと**。逆順の壊れ方:
  - graph-service の client を変えて document-service より先に出す → タグ提案の承認が `PERMISSION_DENIED` → `Unavailable` → **502**（承認は確定しない。成功へは縮退しない）。
  - retrieval-service の client を変えて graph-service より先に出す → 近傍展開が拒否され、二段検索の**グラフ再ランクが静かに効かなくなる**（検索は返る）。
  - BFF の client を変えて retrieval-service より先に出す → BFF は `PERMISSION_DENIED` を「後段が答えた上での失敗」として **502** にする
    （`SearchBffEndpoints`。IADR-0417 決定 9。検索画面の対象範囲フィルタに候補が出ない）。
- REST 輸送へ戻した配備には影響しない（REST の口は変えていない）。

## 残るもの

- 段 2（`AddTag` のロールを認可サービスから引く）。
- `DocumentReadRelayOptions`（#1631）・`DocumentSearchRelayOptions`（#1638）を `TrustedUserContextRelay` へ寄せること。
  特に `DocumentSearchRelayOptions` は起動時の `ThrowIfScalar` を持たない（1 つの値で構成すると既定の `aianalysis-service` へ静かに戻る）。
- `AuthzScope/Resolve` の `user_id` の詐称（ADR-0088 決定 4 の残る半分。token exchange 待ち）。

## 検証

- `dotnet test src/knowledge/backend/backend.slnx` / `dotnet test src/platform/backend/backend.slnx`
- `dotnet format <slnx> --verify-no-changes`（両ユニット）
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`
- 変異 (a)(b) を当てて落ちることを確かめ、`git show HEAD:<path> > <path>` で戻す。

### 結果（2026-09-27・ローカル）

- `dotnet test src/knowledge/backend/backend.slnx`: exit 0（DocumentService.Tests 763・GraphService.Tests 681・RetrievalService.Tests 379 合格ほか。全プロジェクト失敗 0）
- `dotnet test src/platform/backend/backend.slnx`: exit 0（Platform.Shared.Infrastructure.Tests 444・Platform.Bff.Tests 768 合格〔1 スキップ〕ほか。全プロジェクト失敗 0）
- `dotnet format <slnx> --verify-no-changes`: 両ユニット exit 0
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`: 841 tests passed（test-spec-coverage の床は `--update` で 13 対を足した。
  初回は `check-test-name-references` が共有の試験の注記の `*RelayOptionsTests` を実在しない試験名として止めたので、実在の名前へ直した）
- 変異（3 サービスの試験を `TrustedRelay|RelayOptions|RelayDeploymentWiring|GrpcDocumentTagWrite|GrpcGraphNeighbors|GrpcAttributeValues` で絞って実測。
  どれも `git show HEAD:<path> > <path>` で戻し、`grep "//MUT"` で残りが 0 件であることを確かめた）。件数は DocumentService / GraphService / RetrievalService の順:
  - M1 3 面の `EnsureTrustedRelay(context)` の呼び出しを落とす（呼び出し元の確認を落とす）: 14 / 13 / 14 件が落ちた（許可集合外 6 主体の Theory・変種 5・`azp` の食い違い・人のトークン・空のタグ名／空の key）。
  - M2 共有の判定の照合を落とす（`Contains` → `true`）: 21 / 20 / 21 件が落ちた（上記 ＋ 構成・判定の単体試験）。
  - M3 接頭辞一致（`clientId.StartsWith(c) || c.StartsWith(clientId)`）: 4 / 4 / 4 件が落ちた（`*-x`・末尾を欠いた名前の統合・単体）。
  - M4 大小文字を畳む（`OrdinalIgnoreCase`）: 3 / 3 / 3 件が落ちた。
  - M5 機械であることの確認を落とす: 2 / 2 / 2 件が落ちた（人のトークンの統合試験と単体試験）。
  - M6 1 つの値の検査を外す（`ThrowIfScalar` を素通しにする）: 1 / 1 / 1 件が落ちた。
  - 変異 (c)「本文のロールを再び信じる」は段 2 の PR で当てる（段 1 では本文のロールは許可集合の中継者から来たときにまだ効く）。
