---
title: IADR-0650 取り込みの Qdrant のコレクション・索引の保証は、成功するまで上限つきのバックオフで裏で再試行し、成功するまで readiness を NotReady にする。後続の器は保証の成功を待つ
type: impl-adr
status: Accepted
related_ids: [FR-02, FR-03, NFR-21, NFR-06, IADR-0002, IADR-0602, IADR-0536, IADR-0502]
author: claude
created: 2026-10-30
updated: 2026-10-30
related_specs:
  - ../specs/20261030_2283_qdrant-bootstrap-retry.md
---

# IADR-0650: 取り込みの Qdrant の保証の再試行と readiness（#2283）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-30
- 決定者: claude（#2283）

## 起点・関連

- 起点 issue: #2283（MSP#2281 の e2e-live が G13「Qdrant にコレクションが 1 件も無い」で落ちた。Deployment は 18/18 available だった）。
- 前提として変えない:
  - [IADR-0002](./IADR-0002_ingestion-pipeline-and-qdrant-bootstrap.md)（起動時の保証で作る）
  - [IADR-0602](./IADR-0602_readiness-own-dependencies-only.md)（readiness は自プロセスの必須依存だけ）
- 作業仕様書: [20261030_2283_qdrant-bootstrap-retry](../specs/20261030_2283_qdrant-bootstrap-retry.md)。

## 背景

- 保証は起動時に 1 回だけ走り、失敗は Error のログを残して終わっていた。upsert の経路は保証を呼ばないので、1 回の失敗で Pod の生存中ずっと書き込みが失敗した。
- readiness は Qdrant の疎通だけを見るので、疎通が戻れば Ready になった。**書き込めないスタックが Ready に見えた。**

## 決定

### 決定 1: 成功するまで裏で再試行する

- 保証の器を `BackgroundService` にし、起動は塞がない。
- 待ちは 1 秒から倍々で、上限 30 秒。**回数の上限は置かない**。諦めると NotReady のまま止まるだけで、得るものが無い。
- 失敗は計数 `ingestion.qdrant_bootstrap.failures` で数える。0 が正常である。
- ログは最初の失敗を Error、以後を Warning で残す。

### 決定 2: 保証が一度成功するまで readiness は NotReady

- チェック `qdrant-bootstrap` を `/health/ready` に載せる（`failureStatus: Unhealthy`）。
- 疎通（`qdrant`）とは別の問いである。届いていても、保証が済んでいなければ書き込めない。
- 一度成功したら戻さない。保証は冪等で、作ったコレクションを消す経路は無い。

### 決定 3: 後続の器は登録順ではなく保証の成功を待つ

- 次の 3 つの器は、singleton の `QdrantBootstrapState` を待ってから始める。
  - 2-gram の後付け
  - 属性キーの索引の発見
  - 埋め込み待ちの埋め直し
- 状態は `Common/Concurrency` に置く。Features から Infrastructure を引かないためである。

### 決定 4: 受け口（取り込み・削除）も保証の成功を上限つきで待つ（#2311 のレビュー）

- **事実（実測）**: hosted service の登録順は次のとおりで、開始は直列（`ServicesStartConcurrently=false`）である。
  1. 保証
  2. 2-gram の後付け
  3. 発見
  4. 埋め直し
  5. Wolverine（2 つ）
  6. Web
- 従前は保証が `StartAsync` の中で await していたので、Wolverine の受信は保証の 1 回目の後に始まった（その 1 回が失敗しても始まった）。**`BackgroundService` にしたことで、受信が保証より先に始まる窓が新しくできた。**
- 窓の中で書くと、コレクションが無く失敗する。共通の再試行は `RetryWithCooldown(2, 10, 30 秒)`、つまり 4 試行で待ちの合計は 42 秒である。使い切るとデッドレターへ移る（`UsePlatformMessagingDefaults`）。**Qdrant の停止が約 42 秒を超えると、取り込みが失われ得る**（デッドレターからの手での再投入が要る）。再試行だけでは吸収しきれない。
- 決定: 2 つの受け口（`DocumentUpdatedConsumer` と `DocumentDeletedConsumer`）の入口で、`QdrantBootstrapState.WaitForConsumerAsync` を待つ。
  - 待つ場所は排他の外である。
  - 上限は既定 60 秒（保証の待ちの上限 30 秒の 2 倍）。超えたら `QdrantBootstrapPendingException` を投げ、再試行の鎖へ返す。
  - 1 配信で待てる時間は、最大で 4 × 60 秒 ＋ 42 秒 ＝ 約 282 秒である。どの試行も受け口の実行期限（435 秒）の中に収まる。待ちは実行期限の中で数えるので、`EnsureRetryChainFits` の式（期限 × 試行 ＋ 待ち）も変わらない。
- 保証が成功した後は、待たずに通る（`IsEnsured` の即時判定）。
- 後続の器と受け口は、状態を**必須の引数**で受ける。登録を忘れたときに待たずに走ることを、型で防ぐ。

## 却下した案

- **upsert の経路で保証を呼び直す（遅延作成）**: 書き込みごとに存在確認が要る。また、索引の欠落は書き込みでは現れない（#1116）ので、索引の保証の漏れを塞げない。
- **起動を塞いで再試行する（`StartAsync` の中で待つ）**: 起動が終わらないと liveness も応答せず、Qdrant の停止が長いと Pod が再起動を繰り返す。readiness で表す方が、原因（Qdrant）の側に目が向く。
- **回数の上限で諦めてプロセスを落とす**: 再起動の繰り返しになるだけで、計器とログ以上の情報は増えない。

## 帰結・残余リスク

- Qdrant に届かない間、取り込みの Deployment は available にならない。これは意図した挙動で、`check-stack-ready` で捕まる。
- Qdrant の停止が 1 配信の待ちの上限（約 282 秒）を超えると、その間に届いた取り込み・削除はデッドレターへ移る。これは従前（保証の成功の後に Qdrant が止まった場合）と同じ扱いで、再投入の手順は既存のデッドレターの運用に従う。
- 一度成功した後に Qdrant 側でコレクションが消えた場合は、検出しない（従前と同じ）。検索側の readiness が Degraded を出す（[IADR-0318](./IADR-0318_qdrant-fulltext-payload-index.md) 系）。
