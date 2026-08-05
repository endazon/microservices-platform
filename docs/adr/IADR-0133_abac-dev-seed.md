---
title: IADR-0133 経路B の ABAC 初期投入 — 宣言的シード＋管理 API 経由の冪等な opt-in 投入
type: impl-adr
status: Proposed
related_ids: [FR-05, FR-09, UC-05, SC-09, ADR-0004, ADR-0036, IADR-0091]
author: Claude
created: 2026-08-05
updated: 2026-08-05
plan_refs:
  - "../../planning/projects/microservices-platform/06_technical/07_abac-attribute-model.md"
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
---

# IADR-0133: 経路B の ABAC 初期投入 — 宣言的シード＋管理 API 経由の冪等な opt-in 投入

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

> **［2026-08-05 追記］`IADR-0132` から改番した。** 並行実装で採番が衝突し、
> `IADR-0132`（応答スキーマの `required` を C# の非 null 性から起こす・#520 / PR #528）が
> **先にマージされた**ため、先着尊重の原則（`.claude/rules/traceability.md` §採番衝突時の改番手順）に従い
> 後発の本 ADR を `IADR-0133` へ移した。**決定内容は一切変えていない。**
> 本 ADR を採番した時点では #528 が未マージで `docs/adr/` に 0132 が存在しなかった（着手時に確認済み）が、
> 実装中に #528 が先にマージされ、双方が develop に載って重複した。

- 状態: Proposed
- 日付: 2026-08-05
- 決定者: Claude（実装セッション）

## 起点・関連

- 関連する計画書 ID: FR-05（ABAC）／FR-09・UC-05（属性辞書・ポリシー管理）／SC-09（管理者設定）／
  ADR-0004（ABAC）／ADR-0036（所有者ベース裁量制御）
- 関連する実装仕様書:
  [作業仕様書](../specs/20260805_issue-517_abac-dev-seed.md)／
  [#466 の導線検証](../specs/20260805_issue-466_oidc-edge-flow-verification.md)（本件の発見元）／
  [#456 の実測](../specs/20260805_issue-456_abac-attribute-combination-measurement.md)
- issue: #517（発見）／#466（E2E の前提）／#516（必須属性の欠落）

## コンテキストと課題

`AuthorizationService` はポリシーが 1 件も無いと `AbacEvaluator` が **deny-by-default** で縮退する。
これは仕様どおりだが、**投入経路が一度も実行されていない環境では「認証を通しても画面が常に空」**になり、
利用者からは故障と区別が付かない。実測（#466）でも、実トークンで `GET /bff/documents` が `200 []` を返し、
同時刻に `document-service` を直接叩くと文書は返る、という状態だった。

E2E（#466）で「認証後に**結果が出る**」ことを検証したくても、この状態では「空であること」しか固定できない。
何らかの初期投入が要る。**何を単一情報源にし、どう投入し、既定でどう振る舞わせるか**を決める。

## 決定

### 決定 1: 単一情報源は**リポジトリ内の宣言的 JSON**（`deploy/local/abac-seed/`）

`realm.json`（Keycloak）・`deploy/local/minio-oidc/policies/`（MinIO）と**同型**にする。
これらは既に「宣言的ファイルを正とし、適用は別途行う」という作法をこのリポジトリに確立している。
ABAC だけ別の作法（コードに埋め込む・スクリプト内に直書き）を持ち込む理由が無い。

### 決定 2: 投入は**管理 API 経由**。**直 DB 書き込みはしない**

`POST /authz/attributes` / `POST /authz/policies` を使う。DB へ直接 INSERT すれば port-forward も
トークンも要らず簡単だが、**`AbacValidation` の検証を素通りする**——値集合の整合・キーの一意性・
矛盾するポリシーの検出（UC-05 の例外フロー）はすべて API 側にある。
検証を通らないデータが dev に入ると、「dev では動くが SC-09 の画面からは作れない」ものが生まれる。

### 決定 3: **冪等**にする（同一性の鍵は属性 `key`+`scope`・ポリシー `name`）

`k8s-local-up.sh` は冪等（再実行可）であることを前提に運用されており、投入だけ再実行で重複するのは
その前提を壊す。既存を取得して差分だけを作成する。**更新はしない**（既に運用者が SC-09 から編集した
値を、スクリプトの再実行で黙って上書きしないため）。

### 決定 4: **opt-in（`ABACSEED=1`）・既定オフ**

`LOCALEDGE` / `PERSIST` / `OBSERVABILITY` / `VAULT` / `ARGOCD` / `HEADLAMP` / `ESO` と同じ作法に揃える。
既定（未設定）は**何も実行せず挙動不変**。認可の初期状態を黙って変えないことが理由である——
**deny-by-default はセキュリティ上の既定値**であり、それを既定で緩めるのは「dev だから」で正当化しない。

> 検討した代替: 既定オンにして dev の体験を良くする。**採らない。** 経路B の構成は本番構成の
> 予行でもあり、「起動したら全員に読める初期ポリシーが入る」という体験を既定にすると、
> 本番で同じ手順を踏んだときの事故（初期ポリシーの残置）を誘発する。

### 決定 5: 投入先は**稼働中のサービス**であり、chart / manifest ではない

`kubectl apply` するリソースを増やさない。ABAC のポリシーは**アプリケーションのデータ**であって
k8s のリソースではない。CRD 化・ConfigMap 化は、SC-09 の画面から編集される同じデータに
**二つの正**を作る。

### 決定 6: ポリシーは **`clearance` の階段を明示列挙**する（序数比較を持ち込まない）

計画 `07_abac-attribute-model` は「評価は**集合帰属**のまま。序数比較は導入しない」と定めている。
したがって「`restricted` は `confidential` より上」という順序をスクリプトやポリシー評価に持ち込まず、
各段の許可集合を `policies.json` に列挙する。`AbacEvaluator` はマッチした全ポリシーの文書条件を
**union** するため、上位の利用者は下位のポリシーにもマッチして自然に広い集合を得る。

### 決定 7: 属性辞書の `required` は**すべて false** で入れる

計画は `confidentiality` / `department` / `owner` / `lifecycle` を必須と定めるが、
**実データにはそれらが無い**（#516。実測では `confidentiality` のみ）。
`/authz/attributes/validate` を呼ぶ取り込み経路は現時点で存在しないため直ちに壊れはしないが、
`required: true` を先に入れると、**#516 を解消する前に取り込みを塞ぐ**時限式の罠になる。
必須化は実データが属性を備えてから行う。

## 結果

- 経路B で `ABACSEED=1`（または `node scripts/seed-abac-policies.js`）を実行すると、
  **認証済み利用者に文書が見えるようになる**。実測（2026-08-05・文書 2,467 件）:

  | 利用者 | 投入前 | 投入後 |
  | --- | --- | --- |
  | `developer`（`clearance=restricted`） | 0 件 | 2,467 件 |
  | `poc-user`（`clearance=internal`） | 0 件 | 2,467 件 |
  | `poc-operator`（`clearance` 無し） | 0 件 | **0 件** |
  | 無トークン | 0 件 | **0 件** |

  **投入後も「権限が無ければ 0 件」が保たれる**（全開放ではない）ことを実測で確認した。
- #466（E2E の CI 実行）は、これで「認証後に結果が出る」ことを検証できる状態になった。
  残る障害は issuer / 手順A の扱いのみである。
- `measure-abac-combinations.js`（#456）の ABAC 対象キーの決定が、投入後は
  **計画既定への縮退ではなく属性辞書**を使うようになる（同スクリプトの設計どおり）。

## トレードオフ（この決定が**しないこと**）

- **本番の初期投入は解決しない。** 本 ADR の射程は経路B（dev）である。本番のポリシー投入は
  SC-09 の運用手順であり、必要なら別途決める。
- **既定オフのままでは「空の画面」は起きうる。** 既定を変えていないため、`ABACSEED` を知らない
  利用者は従来どおり空を見る。緩和は文書（`deploy/local/abac-seed/README.md`・
  `scripts/README.md`）と #517 の記録に留める。
- **`owner` / `shared_with`（ADR-0036 の動的束縛）は入れていない。** 実データに `owner` が無く
  （#516）、`${current_user}` の束縛は評価エンジン側の対応状況と合わせて確認が要るため、
  本 ADR では扱わない。
- **投入したポリシーは dev の便宜的な値**であって、計画が定める本来のポリシー設計ではない。
  名前に `dev:` を接頭させて取り違えを防ぐ。
