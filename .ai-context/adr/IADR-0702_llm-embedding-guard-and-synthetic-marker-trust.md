---
title: IADR-0702 LLM ゲートウェイの埋め込みの面にも呼び手ごとの許可表と送る前の上限を掛け（費用は補完と同じ呼び手の枠に合算）、合成監視の標識を引き継ぐ呼び手を許可集合に限る。どちらも段（Off／Report／Enforce）を持ち、クラウドだけを段 2 にする
type: impl-adr
status: Accepted
related_ids: [NFR-18, NFR-21, NFR-02, FR-02, FR-11, ADR-0044, ADR-0076, ADR-0138, IADR-0678, IADR-0684, IADR-0692, IADR-0698, IADR-0378, IADR-0689]
author: claude
created: 2026-11-07
updated: 2026-11-07
related_specs:
  - ../specs/20261107_llm-gateway-embed-limits-synthetic-marker.md
---

# IADR-0702: LLM ゲートウェイの埋め込みの面の統制と、合成監視の標識の引き継ぎ手の限定

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-11-07
- 決定者: 所有者の裁定（当日の費用の上限は全呼び手の合計で 1 日 20 USD。2026-11-07）・claude（方式）

## 起点・関連

- 要求 **NFR-18**（統制）・**NFR-21**（費用と運用の観測）・**NFR-02**（合成監視）・**FR-02**（埋め込み）・**FR-11**（LLM 費用の計測）。計画 ADR-0044・ADR-0076 決定 4・ADR-0138 決定 7。
- 前提: IADR-0678（送る前の上限。補完だけが対象）、IADR-0684（補完の呼び手ごとの許可表。結果 3 で「埋め込みの面は射程外」）、
  IADR-0692（クラウドの当日の上限 合計 20 USD）、IADR-0698（当日の費用の枠の置き場）、IADR-0378（合成の標識。内周はヘッダを引き継ぐ）。
- 作業仕様書: `.ai-context/specs/20261107_llm-gateway-embed-limits-synthetic-marker.md`。

## コンテキストと課題

- 埋め込み（gRPC `LlmEmbedding/Embed`）は門（`ServiceCaller`）だけを通り、補完に掛けた呼び手ごとの許可表と送る前の上限を通らなかった。
  クラウドは埋め込みを使わない（`embedding.enabled: false`）が、ゲートウェイの受け口は HTTP/2 も受けるので gRPC の面は在り、
  手順書は「埋め込みを使うなら」埋め込みの鍵を置く案内をしていた。鍵を置けば、上限の外に費用の口が生まれる。
- 補完の両面は、合成監視の標識（ヘッダ）が在れば呼び手が誰でも合成として扱い、費用の計上（月次予算の警報の元）から外していた。
  IADR-0378 の受容の前提は「メッシュの STRICT mTLS の内側で、外周が付けたヘッダだけが届く」ことで、メッシュも外周も無いクラウドでは成り立たない。
  標識をゲートウェイへ運ぶ正規の呼び手は AI 分析・検索の 2 つだけで、取引ユニットは標識を使わない。
- どちらも、稼働中の呼び手（ナレッジの取り込み・検索）を止め得る、または費用の計上を変える。段を付けて入れる。

## 決定

### 決定 1 — 埋め込みも補完と同じ 1 つの許可表を通す（`AllowEmbedding`・段 `Llm:CallerPolicy:EmbeddingMode`）

- 呼び手の項目（`Llm:CallerPolicy:Callers:<呼び手>`）に `AllowEmbedding`（既定 false）を足す。gRPC 面は判定器（`EmbedUseCase`）を呼ぶ前に
  `LlmCallerPolicy.EvaluateEmbedding` を呼ぶ。表に無い呼び手は `unknown_caller`、表に在るが許可の無い呼び手は `embedding_not_allowed`
  （`llm.policy.kind` の値域は 4 値）。計器の用途は固定の `embedding`。
- 段は補完の段（`Mode`）と別にする（`Off`＝判定しない／`Report`＝既定／`Enforce`＝`PERMISSION_DENIED`）。補完を段 2 にした構成でも、
  埋め込みの呼び手を確かめないまま止めないためである。
- 既定の設定（`appsettings.json`）で許すのは取り込み・検索の 2 呼び手だけ。取引ユニットの呼び手には許さない。

### 決定 2 — 埋め込みも補完と同じ 1 つの上限の判定器を通す（`AdmitEmbedding`・段 `Llm:CallLimits:EmbeddingMode`）

- `EmbedUseCase` は送る直前に `LlmCallLimiter.AdmitEmbedding` を呼ぶ（判定器を 2 つにしない。IADR-0678 と同型）。
  - 🔴 **呼び手の要求数・当日の費用の枠は補完と同じ鍵に数える。** 呼び手の日の上限（クラウドは合計 20 USD の割り当て）は補完と埋め込みの合計である。
    枠を分けると、埋め込みの分だけ合計の上限を超えられる（試験で固定）。
  - 用途の枠は固定の `embedding`（`PurposeDailyCostLimits` のキーにも書ける。起動の検証が受け付ける）。入力の長さは `MaxPromptChars` で見る。`max_tokens` は見ない。
  - 見込み額は入力の文字数を入力トークンとみなす。送った後は同じ文字数で置き換える（提供元の応答のトークン数は使わない。多めの側）。
    送れなかった（鍵なし・上流の失敗・取り消し）ときは戻す。
  - 単価は `Llm:CallLimits:EmbeddingPrices`（モデル → 入力の百万トークンあたり）。既定は `voyage-3.5` の 0.06（計画の比較時点の目安。正は契約の価格表）。
    表に無いモデルは 0 にせず `UnknownModelPrice` の入力単価で数える。0 以下の単価は起動で落とす。
  - 外へ出ない経路（ティア A。自前・決定的）は費用を数えない（要求数と長さは見る）。
- 段は補完の段（`Mode`）と別（`Off`＝判定も計上もしない／`Report`＝既定／`Enforce`）。`Enforce` で超えた呼び出しは送らず `Embedded=false` で返す
  （`prompt_length` は再試行で解消しないので `Retryable=false`、ほかは `true`）。`Enforce` は IADR-0692 と同じく既定の当日の費用の上限が無いと起動しない。
- 退けた案:
  - **埋め込みの単価を補完の単価表（`Llm:Pricing:Models`）へ足す**: 計画 ADR-0138 決定 7 は単価表を補完の 3 モデルに限る（試験が固定する）。
    単価表は費用の計器（`llm.cost.total`）の換算にも使うので、埋め込みのモデルを混ぜない。
  - **クラウドの手順書から鍵の案内を外すだけ**: 鍵を置けば口が開く状態が残る。案内は外し（決定 4）、判定器も通す。

### 決定 3 — 合成監視の標識を引き継ぐ呼び手を許可集合に限る（`Llm:SyntheticMarker`）

- 補完の両面（REST・gRPC）は、標識の有無（`SyntheticTraffic.IsSyntheticInternalRequest`。定義は変えない）と検証済みのトークンの主体を
  `LlmSyntheticMarkerPolicy.IsSynthetic` へ渡す。主体が `TrustedCallers`（既定は AI 分析・検索）に在れば標識を引き継ぐ。
- 段 `Mode`: `Report`（既定）は従前どおり引き継ぎ、許可集合の外の呼び手が付けた標識を `llm.synthetic_marker.untrusted.total`
  （`llm.marker.mode`・`llm.caller`。呼び手は許可表の名前か `other`）に数えて報告する。`Enforce` は許可集合の外の標識を無視し、実利用として費用に計上する。
  送信は段に依らず止めない。許可集合が空なら、`Enforce` ではどの標識も引き継がない。
- 送る前の上限は従前から標識に依らず数えている（IADR-0678）。変えるのは費用の計上だけである。

### 決定 4 — クラウドは 3 つとも段 2。手順書から埋め込みの鍵の案内を外す

- `deploy/cloud/values-cloud.yaml` に `Llm__CallerPolicy__EmbeddingMode`・`Llm__CallLimits__EmbeddingMode`・`Llm__SyntheticMarker__Mode` を `Enforce` で足す
  （`scripts/deploy-cloud.test.js` の段 2 の試験が、クラウドの描画に在り・既定とローカルの描画に無いことを固定する）。
- クラウドには埋め込みの許可のある呼び手（取り込み・検索）も、標識の許可集合の呼び手（AI 分析・検索）も描かれない。取引ユニットの呼び手は
  埋め込みを呼べず、標識を付けても費用に計上される。
- `docs/operations/cloud-openbao-init-unseal-runbook.md` §4 の表から埋め込みの鍵の案内を外し、試験で固定する。ESO は在るキーを全部写す（`dataFrom.extract`）形のまま変えない
  （稼働中の Secret の形を変えない。鍵を置いても決定 1・2 が止める）。

## 結果・残余リスク

1. 既定とローカルは段 1 のまま（埋め込みの違反・上限の超過、許可集合の外の標識を数えるだけ）。段 2 へ切り替える前に、
   `llm_caller_policy_violation_total{llm_purpose="embedding"}`・`llm_call_limit_exceeded_total{llm_purpose="embedding"}`・`llm_synthetic_marker_untrusted_total` が
   想定外の呼び手で増えていないことを確かめる。
2. 段 1 でも埋め込みは呼び手の要求数・当日の費用の枠を消費する（呼び手の合計を正しく数えるため）。補完を段 2 にして埋め込みを段 1 にした構成では、
   埋め込みの量で同じ呼び手の補完が止まり得る。止めたくなければ埋め込みの段を `Off` にする。
3. 一括の再索引は埋め込みの要求数の既定（呼び手×用途 120 / 分）を超え得る。段 2 にする前に、取り込みの呼び手の上限を `Callers` で上書きする。
4. 埋め込みの実額は見込みと同じ文字数で数える（多めの側）。提供元の実際のトークン数とはずれる。
5. 要求数の窓はプロセス内（IADR-0678 決定 5）。

## 適用の順序（稼働中の構成）

1. ゲートウェイのイメージをこの変更を含む版でビルドしてノードへ取り込む。
2. `helm upgrade` で chart を当てる（env の追加だけ。PVC・Recreate は IADR-0698 のまま）。取引ユニットの補完の許可・上限・費用の計上は変わらない
   （取引ユニットは標識を使わず、埋め込みを呼ばない）。
3. 起動の後、`llm_caller_policy_violation_total`・`llm_synthetic_marker_untrusted_total` が出ていないことを確かめる。
