---
title: IADR-0379 east-west gRPC の先行条件 — proto は所有ユニットの共有契約プロジェクトへ置き、番号不変の versioning を検査器で守り、h2c は専用ポートに分離し、s2s トークンは呼び出し側サービス自身の資格情報とする
type: impl-adr
status: Accepted
related_ids:
  - FR-05
  - NFR-09
  - NFR-16
  - ADR-0004
  - ADR-0029
  - ADR-0032
  - ADR-0075
  - IADR-0117
  - IADR-0122
  - IADR-0229
  - IADR-0251
author: claude
created: 2026-09-05
updated: 2026-09-05
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md (Accepted 2026-09-03) 決定 1・2・6
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md (Accepted 2026-07-25 / 2026-09-03 部分改定) 決定・フォローアップ・2026-08-04 追記
  - planning:projects/microservices-platform/07_adr/ADR-0032_spa-auth-bff-session.md (Accepted) 決定
  - planning:projects/microservices-platform/02_requirements/01_requirements.md NFR-09 / NFR-16
---

# IADR-0379: east-west gRPC の先行条件（置き場・versioning・h2c・s2s）（#1201）

- 状態: Accepted
- 日付: 2026-09-05
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: `ADR-0075` 決定 1（基盤が 4 点の現物を作り AST が追随する）・決定 2（`ADR-0029` の
  フォローアップの履行が移行着手の先行条件。期限 2026-11-30）・決定 6（基盤先行は MSP 自身の移行を含む）／
  `ADR-0029` §決定（east-west 同期は gRPC・proto は呼び出される側が所有）と 2026-08-04 追記（`*.Client` を作らない・
  キャッシュ等は呼び出し元の Infrastructure）／ `ADR-0032`（BFF セッション方式）／ `NFR-09`（全 API で OIDC/JWT 認証）／
  `NFR-16`（サービス間 mTLS）／ `FR-05`（参照実装の経路 = ABAC 権限スコープ解決）
- 関連する実装 ADR: `IADR-0117`（ユニット外参照は `Shared/` の 3 プロジェクトのみ）／ `IADR-0122`（C# 契約の
  スキーマ検査。本 IADR が「proto を母集合へ入れない」と決めた相手）／ `IADR-0229`（Kernel の公開面）／
  `IADR-0251`（BFF セッション。s2s と分ける相手）
- 関連する実装仕様書: `.ai-context/specs/20260905_issue-1201_east-west-grpc-preconditions.md`
- 実装ガイド（人が読む正）: `docs/api/east-west-grpc.md`
- issue: #1201

## コンテキストと課題

`ADR-0029` は 2026-07-25 に「east-west 同期は gRPC」と定めたが、フォローアップ（proto の配置と versioning 規約を
実装ガイドへ落とす）が未履行のまま 1 年近く経ち、本リポジトリの `.proto` は 0 件、`Grpc.*` を参照する `.csproj` も
0 件だった（CPM に版だけが 4 行在った）。`ADR-0075` はこのフォローアップの履行を移行着手の**先行条件**とし、
期限を置いた。**期限までに現物が無ければ、基盤先行という順序そのものが覆る**（AST の 22 本が待たされる理由が消える）。

決めるのは `ADR-0075` 決定 1 が挙げた 4 点である。

1. proto の**置き場**（どのプロジェクトが生成物を持つか。ユニット境界の規則とどう噛み合うか）
2. **versioning**（package の付け方・互換の判定・破壊的変更の扱い。検査器で守れる部分はどこか）
3. **h2c**（メッシュ内は平文であり、TLS 無しの HTTP/2 で Kestrel と Istio がどう噛み合うか。PERMISSIVE / STRICT の両方で壊れないこと）
4. **s2s トークン**（BFF セッション方式の利用者トークンとどう分けるか。利用者トークンをそのまま転送する形にしないこと）

制約: 既存の HTTP / Wolverine の呼び出しを置き換えない（本作業は先行条件の履行であって移行ではない）。参照実装は
1 経路。過剰な抽象化をしない。新しいライブラリを足さない（`Grpc.AspNetCore` / `Grpc.Net.Client` / `Grpc.Tools` /
`Google.Protobuf` は `ADR-0030` の採用ライブラリで CPM に版が既に在る。`scripts/backend-library-baseline.json` は触れない）。

## 検討した選択肢

### 1. 置き場

| | A. 所有ユニットの共有契約プロジェクト（採用） | B. 所有サービスのプロジェクト | C. 専用の `Platform.Shared.Grpc` を新設 |
| --- | --- | --- | --- |
| 他ユニットの呼び出し元が生成クライアントを参照できるか | できる（`Shared/` 3 プロジェクトの 1 つ） | **できない**（`IADR-0117`: サービスプロジェクトはユニット外から参照不可） | できるが `IADR-0117` の「3」を改定する IADR が要る |
| 所有の所在 | ディレクトリ `Protos/<unit>/<service>/` で所有サービスを示す | 明快 | 契約と分離されて所有が読みにくい |
| 既存 DTO 契約との関係 | 同じプロジェクトに REST の DTO と proto が並ぶ（同じ境界の契約） | 分かれる | 分かれる |

### 2. versioning の検査

| | A. 専用検査器 `check-proto-contracts.js`（採用） | B. `check-contract-schema.js` の母集合へ proto を足す | C. 規約のみ（検査しない） |
| --- | --- | --- | --- |
| 互換規則 | フィールド番号の不変性・`reserved` を機械で守れる | C# の規則（位置引数・既定値）と噛み合わない | 約束だけ |
| パーサ | protobuf 用を 1 つ書く | 1 スクリプトに 2 構文 | — |
| 逃げ道 | allowlist（`IADR-0122` 決定 3 と同型） | 同左 | — |

### 3. h2c

| | A. 専用ポート（`Http2` のみ。採用） | B. 既存ポートで `Http1AndHttp2`（preface 検出に頼る） | C. TLS を Kestrel で終端 |
| --- | --- | --- | --- |
| 依存 | 無し（プロトコルはポートで決まる） | Kestrel の平文 HTTP/2 事前知識検出（`ADR-0075` §残るもの が懸念を挙げた） | 証明書配布がメッシュと二重になる |
| Istio | `appProtocol: grpc` で推定不要 | 1 ポートに 2 プロトコルが混ざり推定に頼る | サイドカーの mTLS と衝突 |
| 費用 | Listen の再宣言（ホスティング URL を捨てる Kestrel の挙動への対処） | 0 | 大 |

### 4. s2s トークン

| | A. 呼び出し側サービスの client credentials JWT（採用） | B. 利用者のトークンをそのまま転送 | C. Istio の peer principal（XFCC）で判定 | D. RFC 8693 token exchange |
| --- | --- | --- | --- | --- |
| confused deputy | 呼び出し先は「サービスが呼んだ」とだけ判定し、利用者ロールは面に入らない | **利用者が直接呼んだのと区別できず、AdminOnly 等がサービス間の面へ漏れる** | 同 A | 最も精密（`act` claim） |
| compose / 単体テストで再現できるか | できる（同じ JwtBearer） | できる | **できない**（サイドカー無し） | できるが Keycloak の設定が要る |
| 実装費用 | 小（既存の JwtBearer ＋ ロール 1 つ） | 0 | 中 | 大（realm・キャッシュ・aud） |

## 決定

1. **置き場（案 1-A）**: proto は**所有サービスが属するユニットの共有契約プロジェクト**（platform 所有 →
   `Platform.Shared.Contracts`、knowledge 所有 → `Knowledge.Contracts`）の `Protos/<unit>/<service>/v<N>/<name>.proto` に
   置き、`GrpcServices="Both"` でクライアント・サーバ基底の両方をそこから生成する。`*.Client` プロジェクトは作らない。
   **ユニット外参照の規則（`IADR-0117`）は変えない** —— platform のサービスが knowledge の gRPC を呼ぶことは今後も無く、
   現状の HTTP と同じ向きである。生成物（`obj/`）はコミットしない。
2. **versioning（案 2-A）**: `package <unit>.<service>.v<N>`、`csharp_namespace <ContractsRoot>.Grpc.<Service>.V<N>`。
   **フィールド番号は不変**。削除は番号と名前を `reserved` に残す。番号・型・ラベル・名前の変更、field / message / rpc /
   enum 値の削除、rpc の型変更、package・名前空間の変更は破壊的。**破壊的変更は `v<N+1>` を並走させて行い、in-place で
   壊さない。** 検査器 `scripts/check-proto-contracts.js`（規約 R1〜R4 ＋ baseline `proto-contract-baseline.json` との
   後方互換 ＋ allowlist `proto-breaking-allowlist.json`）が守る。**削除時の `reserved` 不在と `reserved` の再利用は
   allowlist でも通らない。** `check-contract-schema.js`（`IADR-0122`）の母集合には入れない（1 構文 1 パーサ。同 IADR へ
   日付つき追記）。
3. **h2c（案 3-A）**: `Grpc:Port`（既定 8081・未設定なら立てない）の**専用ポート**に `HttpProtocols.Http2` だけを bind し、
   HTTP/1.1 のポート（8080）はそのまま残す。共通ヘルパ `AddPlatformGrpcListener` は **HTTP 側のポートを再宣言する**
   （Kestrel は Listen を 1 つでも構成するとホスティング URL を捨てる。実測: 再宣言が無いと 8080 が消える）。
   helm は `services.<name>.grpcPort` を宣言したサービスにだけ `containerPort`（名前 `grpc`）・Service ポート
   （`name: grpc` / `appProtocol: grpc`）・env `Grpc__Port` を描画し、compose は `expose` と `Grpc__Port` で追随する。
   **readiness は HTTP の `/health/ready` のまま**（1 プロセスが両ポートを起動時に bind する）。サイドカーがある限り
   PERMISSIVE / STRICT のどちらでも mTLS は Envoy で終端されてアプリには平文が届くので、アプリ側の設定は両モードで同一。
4. **s2s トークン（案 4-A）**: gRPC の `authorization` メタデータには**呼び出し側サービス自身**の JWT（platform realm の
   confidential client の client credentials。`IServiceTokenProvider` が取得・再利用）を載せる。呼び出し先は同じ JwtBearer で
   検証し、gRPC サービス型に **`ServiceCaller` ポリシー（realm ロール `platform-service`）** を掛ける。
   **利用者のトークンはメタデータへ載せない**（利用者の文脈は本文で運ぶ。REST の要求本文と同じ形）。
   deny-by-default は変えない: 該当ポリシーが無ければ `granted=false` を応答で返し、呼び出し側は
   `UNAUTHENTICATED` / `PERMISSION_DENIED` / `UNAVAILABLE` / トークン取得失敗をすべて null（閲覧可能なし）へ縮退する。
   BFF セッション方式（`ADR-0032` / `IADR-0251`）との分け方は **north-south = 利用者トークン、east-west = s2s トークン**。
   BFF は自分の confidential client `bff` で client credentials を取る（realm に service account と `platform-service` を付けた）。
   token exchange（案 4-D）は、呼び出し先が利用者自身の権限で動く必要が出たときの次の段とする。
5. **参照実装と並走の正**: 参照実装は BFF → AuthorizationService の権限スコープ解決（`platform.authz.v1.AuthzScope/Resolve`）
   1 経路。`Services:AuthorizationServiceGrpc` が構成されたときだけ gRPC を使い、無ければ REST。**並走中の正は REST**
   （切替も戻しも構成だけで行う）。gRPC 面は REST と**同じ評価器** `AbacEvaluator.ResolveScope` を呼ぶ。

## 理由

- **決定 1** は `IADR-0117` の「ユニット外参照は 3 プロジェクトのみ」と `ADR-0029` の「所有者は呼び出される側」を
  同時に満たす唯一の置き場である。案 1-B は他ユニットから参照できず、案 1-C は改定 IADR を要するのに得るものが無い。
- **決定 2** で検査器を足すのは、`CLAUDE.md`「同型の事故が 2 回起きたら」の例外ではない —— `IADR-0122` が C# 契約で
  既に採った方式（スナップショット ＋ allowlist）を proto へ**同型で写す**ものであり、新しい統制ではない。番号の不変性は
  約束では守れない（誰も番号の履歴を覚えていない）。
- **決定 3** は「壊れないこと」を Kestrel の挙動への依存ではなく構造で得る。専用ポートの費用（Listen の再宣言）は
  試験で固定した。**readiness を gRPC ヘルスへ足さない**のは、1 プロセスの両ポートが同時に bind される以上、
  HTTP の readiness が h2c の bind も含意するからである。
- **決定 4** で案 4-B を退けるのは、利用者トークンの転送が呼び出し先から見て「利用者が直接呼んだ」のと区別できないためである。
  現状の REST `/authz/scope` は認可を掛けていない（メッシュの mTLS が第一防御）ので、s2s トークンは**現状より強くなる**
  向きの変更である。案 4-C は compose と単体テストで再現できず、案 4-D は費用に見合う要求がまだ無い。
- **決定 5** で REST を正にするのは、切替の事故を「構成を外す」だけで戻せる形にするためである。gRPC を正にすると
  戻し方がコード変更になる。

## 結果

- 良い影響: `ADR-0075` 決定 2 の先行条件が期限（2026-11-30）より前に履行された。AST と本リポジトリの残り 31 本は
  同じ 4 点を写せる。`git ls-files "*.proto"` = 1、`Grpc` を参照する `.csproj` = 3（Contracts / Infrastructure / AuthorizationService）。
- 悪い影響・トレードオフ:
  - 呼び出し元サービスごとに Keycloak の confidential client と secret の注入が要る（展開 issue の費用）。
  - h2c を有効にしたサービスは Service が複数ポートになり、ポートに名前が付く（`grpcPort` 無しのサービスは不変）。
  - `WebApplicationFactory.ConfigureAppConfiguration` は builder 時点の読み取りに間に合わない（既知の罠）ため、
    gRPC の器はポートを環境変数で与える。
  - 稼働クラスタでの h2c 往復は**未実測**（Pod の再起動を要するため）。実 Kestrel の往復（T-01 / T-02）で代替した。
- フォローアップ:
  1. 残り 31 本の展開（別 issue。呼び出し元ごとの client 登録・`grpcPort`・gRPC 計装・gRPC ヘルスの要否）。
  2. AST は `ADR-0075` 決定 4 のとおり、本リポジトリが公開した proto に追随する（AST#584。本リポジトリからは起票しない）。
  3. 計画側へ: `ADR-0029` §結果 のフォローアップは本 IADR で履行済み（`/sync-impl` で対応表へ載る）。

## 関連

- Supersedes: なし
- Superseded by: なし
