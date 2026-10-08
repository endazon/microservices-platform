---
title: IADR-0513 本番の NetworkPolicy は、取引ユニット（AST）の名前空間から KB の読み手（取引判断 → 検索の REST）と書き手（情報収集・報告書 → 文書の REST）への ingress を、用途ごとの values で開ける。既定は閉じ、送り元は名前空間と Pod の AND、ポートは REST だけ
type: impl-adr
status: Accepted
related_ids: [NFR-09, FR-02, FR-03, ADR-0125, ADR-0119, ADR-0121, ADR-0085, ADR-0084, IADR-0026, IADR-0076, IADR-0078, IADR-0348, IADR-0461, IADR-0492, IADR-0500]
author: claude
created: 2026-10-08
updated: 2026-10-08
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0125_ast-kb-reader-static-project-policy-with-confidentiality-ceiling.md 決定 4（本番の NetworkPolicy は別件）・実測 10・フォローアップ 3
  - planning:projects/microservices-platform/07_adr/ADR-0119_document-machine-client-own-docs-and-read-abac.md（AST の書き手は DocumentService へ書く）
related_specs:
  - ../specs/20261008_1756_ast-kb-ingress-netpol.md
---

# IADR-0513: AST の名前空間から KB の読み手・書き手への ingress の NetworkPolicy（#1756）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-08
- 決定者: claude（#1756。計画 ADR-0125 決定 4 が「本 ADR では定めない」とし、フォローアップ 3 で実装へ渡した）

## 起点・関連

- 起点 issue: **#1756**。関連 #1696（読み手の主体）・#1755（機密区分の上限）・planning#712・AST#1078。
- 計画: ADR-0125 決定 4・実測 10（本番の既定拒否の NetworkPolicy が AST の名前空間からの ingress を塞ぐ）。ADR-0119（AST の機械クライアントの書き手の前提）。
- 先例: [IADR-0076](./IADR-0076_edge-bff-routing-and-oidc-hostname.md)・[IADR-0078](./IADR-0078_frontend-k8s-serving.md)・[IADR-0348](./IADR-0348_private-notes-sync-edge-route.md)（別名前空間 → 1 サービス・1 ポートの穴を、条件つきで 1 枚ずつ開ける形）。
  [IADR-0461](./IADR-0461_object-storage-seaweedfs-deployment.md) 決定 10（NetworkPolicy は許可の和なので、選ぶ範囲を広げるとポートまで開く）。
- 基点コミット: MSP `origin/develop` `bde4fdba`。AST は `origin/develop` `8c7205cc` を読んだ。

## コンテキストと課題

本番の chart は `default-deny-ingress`（全 Pod の ingress を拒否）と `allow-intra-namespace`（同じ名前空間の Pod だけ）を描く。
別名前空間からの許可はエッジ（istio-system）の 3 本だけで、AST の名前空間からは 1 本も無い。

AST は MSP へ次の 2 用途で REST を呼ぶ（AST の chart と appsettings の実測）。

| 用途 | AST の Pod（`app`） | 宛先 | 口 | 資格 |
| --- | --- | --- | --- | --- |
| KB の読み手 | `trade-decision-service` | `retrieval-service:8080` | `POST /search` | `ai-stock-trading-kb-reader`（ロールなし。[[IADR-0492]]・[[IADR-0500]]） |
| KB の書き手 | `information-collection-service`・`report-service` | `document-service:8080` | `POST /documents`・`GET /documents`・`PUT …/body` | `ai-stock-trading-kb-writer`（`platform-operator`） |

本番ではどちらも L3/L4 で塞がれる。読み手のポリシーの投入の保留は解かれたが（#1696）、届かない。

### 決めることは 4 つ

| # | 論点 | 選択肢 |
| --- | --- | --- |
| 1 | 書き手も開けるか | (a) 読み手だけ／(b) 読み手と書き手を別々の knob で |
| 2 | 送り元をどう選ぶか | (a) 名前空間だけ／(b) 名前空間 AND Pod のラベル |
| 3 | 名前空間をどのラベルで選ぶか | (a) AST の chart が付ける `app.kubernetes.io/part-of`／(b) API サーバが付ける `kubernetes.io/metadata.name` |
| 4 | 既定値と開け方 | (a) 既定で開ける／(b) 既定は閉じ、values で用途ごとに開ける |

## 決定

### 決定 1 — 読み手と書き手を別々の knob（`networkPolicy.fromAst.kbReader` / `kbWriter`）で開けられるようにする

- **(b) を採る。** 書き手が DocumentService へ書くことは計画 ADR-0119 が前提にしている（計画の未決ではない）。ネットワークの穴は統制の実現手段であり、
  書くことの可否はアプリ層（書き込みの群のロール・所有者の束縛）が判定する。読み手だけを開けても、本番で AST の収集記事・報告書が KB に入らなければ
  読み手が読むものが無い（ADR-0125 の良い影響が成り立たない）。
- **別々にするのは、片方だけを開ける配備を許すためである**（例: 書き手の主体の資格情報を未投入の環境）。1 用途 1 枚の NetworkPolicy にする
  （`allow-ast-kb-reader-ingress` / `allow-ast-kb-writer-ingress`）。

### 決定 2 — 送り元は「AST の名前空間 **かつ** clients の Pod」。行き先は 1 サービス、ポートは REST だけ

- `from` の 1 要素に `namespaceSelector` と `podSelector` を並べる（AND）。`podSelector` は `app In clients`。
  - 既定の clients: 読み手 `[trade-decision-service]`、書き手 `[information-collection-service, report-service]`。
  - AST の Pod のラベル `app: <名前>-service` は Deployment の selector と同じ値で、変えるとロールアウトが壊れる＝安定している。
  - `information-collection` は検索の BaseUrl の枠を持つが、本番の既定は空で、AST の chart が「検索を配線していない」と書いているので読み手の clients に入れない。
- 行き先は `podSelector: app: <target>-service`（`target` は `services` のキー。既定 `retrieval` / `document`）。
- ポートは **`services.<target>.port`（REST）だけ**。値は Service と同じ 1 か所から描く。east-west gRPC の `grpcPort`（8081）は開けない（AST は REST だけを使う）。
- **(a) を採らない。** 名前空間だけだと AST の全 Pod（発注・リスク管理ほか）が検索・文書の口に届く。

### 決定 3 — 名前空間は `kubernetes.io/metadata.name` で選ぶ

- API サーバが名前から自動で付け、書き換えられない。既存のエッジの許可（istio-system）と同じ形。
- `app.kubernetes.io/part-of: ai-stock-trading` は誰でも任意の名前空間に付けられ、付けた名前空間の同名ラベルの Pod が通ってしまう。

### 決定 4 — 既定は閉じる。有効なのに値が欠けるときは描画で止める

- `kbReader.enabled` / `kbWriter.enabled` の既定は `false`。既定（本番像）と `values-local` の描画はバイト等価のまま（`networkPolicy.enabled=false` の経路B では何も描かない）。
- 描画で止める（`fail`）: `namespace` が空・`clients` が空または空の要素を含む・`target` のサービスが無効または `port` を持たない・
  `target` が用途の行き先と違う（`kbReader` は `retrieval`、`kbWriter` は `document` だけを受ける。値の書き換えで BFF・認可サービス等へ穴を向けさせない）。
  空の `clients` を許すと `podSelector` の意味が「名前空間の全 Pod」に近づく書き方へ倒れやすく、空の `target` は行き先の無い穴になる。黙って倒さない。

## 統制と現在の実現手段

| 統制 | 現在の実現手段 | 配備までの暫定手段 |
| --- | --- | --- |
| 既定では AST の名前空間から 1 本も開けない | **ある**: values の既定 `false`。試験 `scripts/helm-ast-kb-ingress.test.js` の陰性対照（既定・values-local は描かない・評価で読み手・書き手が落ちる） | — |
| 開けるときは読み手 → 検索 REST・書き手 → 文書 REST だけ | **ある**: 同試験が描画を k8s の評価規則で呼び出し元の一覧（gRPC・他の Pod・他の名前空間・他のサービス・名前空間の中）へ当てる。変異 3 本（AND を OR に割る・ポートを外す・名前空間の選択を空にする）で赤になる | — |
| 本番で実際に届く | 🔴 **無い**（本番へ values を当てていない。稼働中のクラスタには触らない） | 運用仕様書 §AST の KB の読み手のポリシーの投入「本番の前提」の手順（values を有効にし、AST の Pod をメッシュへ入れる） |

## 結果

- 良い影響: 本番で AST の KB の読み手・書き手を使う手段が chart に入る。穴は 2 サービス × REST の 1 ポート × AST の 3 Pod に限られ、既定は閉じたまま。
- 悪い影響 / トレードオフ: NetworkPolicy は L4 なので、開けた REST の口のどのパスにも届く（検索の `/search` 以外・文書の書き込み以外の口も）。
  何ができるかはアプリ層の認証・ロール・ABAC が決める（読み手はロールなし・書き手は `platform-operator`）。
- 🔴 **書き手の穴は、DocumentService の読み取りの統制に依存する。** 計画 ADR-0119 決定 3 の暫定手段は「NetworkPolicy・STRICT mTLS・BFF の判定」であり、
  組織文書の内容の ABAC は門（`IContentAbacGate`）が開いたときだけ `DocumentReadAccess` が行う（`DocumentReadUseCase.cs` の冒頭の注記。計画 ADR-0121 決定 4・5）。
  **門が閉じている間に `kbWriter` を開けると、AST の書き手の Pod（`platform-operator` のトークン）が `GET /documents` で組織文書をすべて読める**
  （個人資料は除かれる）。NetworkPolicy という暫定手段の一枚を、AST の書き手の Pod の分だけ外すことになる。運用仕様書のチェックに載せた。

## 残余

1. 🔴 **メッシュ**: MSP は `mesh.mtlsMode: STRICT`。AST の Pod がサイドカーを持たない（AST の chart の `mesh.sidecarInjection.enabled` の既定は無効）と、
   穴を開けても平文は拒否されて届かない。本番で開けるときは AST 側のメッシュ参入と組で行う（運用仕様書に記した）。
2. **LLM ゲートウェイ**: AST の報告書・取引判断は `llmgateway-service:8080` も呼ぶ。同じ既定拒否で塞がれるが、#1756 の射程（KB）の外であり本 IADR では開けない
   （試験の呼び出し元の一覧に「閉じたまま」として載せた）。#1811 で追う。
3. **認証基盤への到達**: 読み手・書き手のトークンは認証基盤（`platform-infra`）から取る。MSP の chart の外であり、本 IADR は扱わない。
4. **L7 の絞り込みは無い**: パス単位で絞る AuthorizationPolicy は置かない（決定の結果の欄）。必要になれば DocumentService のエッジの DENY（[[IADR-0348]] 追記）と
   同じ型で、AST の主体（`cluster.local/ns/ai-stock-trading/sa/...`）を対象に足す。ALLOW にすると他の呼び出し元を切るので DENY の形にする。
5. **clients の追随は人手**: AST の chart で KB を呼ぶ Pod が増えたら、`clients` へ足さないと届かない（安全側に倒れる）。機械の突合は置いていない。
6. **書き手の穴と内容の ABAC の門**: 上の §結果のとおり、門が閉じている間の `kbWriter` は組織文書の読み取りを AST の書き手の Pod へ広げる。門の状態を values から機械で突き合わせる手段は無い（運用の確かめに頼る）。

## 関連

- Supersedes: なし
- Superseded by: なし
- 作業仕様書: [`../specs/20261008_1756_ast-kb-ingress-netpol.md`](../specs/20261008_1756_ast-kb-ingress-netpol.md)
