---
title: IADR-0253 エッジ導線スモークは段数で fail-closed にし、検索は「見つかること」を判定しない（観測できないため）
type: impl-adr
status: Accepted
related_ids:
  - FR-03
  - FR-05
  - NFR
  - SC-01
  - SC-02
  - SC-03
  - ADR-0043
  - IADR-0009
  - IADR-0141
  - IADR-0248
  - IADR-0252
author: claude
created: 2026-08-22
updated: 2026-08-22
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md
---

# IADR-0253 エッジ導線スモークの門と、観測できない検索

## 状況

#783 が統合スタックを CI で起こす経路（[IADR-0248]）を着地させ、段 2 として #466
（主要導線のスモーク）へ次を引き継いだ。

1. `scripts/verify-oidc-edge-flow.sh` の **PASS 件数を baseline 化**して退行を止める
2. 主要導線（ログイン → 検索 → 文書詳細 → ABAC 不可視）のスモーク本体

着手時に実測したところ、引き継ぎの前提のうち 2 件は既に解消済みで
（期待値の陳腐化は #972 が修正済み、スクリプトは既にジョブに載っている）、
**実際に空いていたのは上の 2 点だけ**であった。

## 決定 1: baseline ファイルは置かず、**段数**（`TOTAL`）で fail-closed にする

`TOTAL` は `step "N/$TOTAL"` の**表示にしか使われていなかった**。終了判定は `FAIL > 0` だけを見るため、
**段が削除されても・`if` ガードで静かに飛ばされても、`PASS` が減るだけで EXIT=0（緑）**になる。

`step()` に「番号つきの段」だけを数える計数器（`STEPS`）を持たせ、末尾で `STEPS == TOTAL` を見る。

- **別ファイルの baseline は置かない。** `TOTAL` が既に「本来走るべき段数」の単一情報源であり、
  同じ不変条件の情報源を 2 箇所に持つと片方が腐る（[IADR-0141]「参照点を 1 つに畳む」）。
- **判定数（`PASS + FAIL`）と突き合わせてはならない。** 1 つの段が複数の判定を刻む
  （段 6 はクレーム 4 件、段 7 は宛先 3 件）。実測では健全な実行が **PASS 22 / FAIL 0 に対し TOTAL 17** であり、
  素朴な等式を入れていれば**正常な実行が即座に赤**になっていた。
  この誤りは着手時の設計に実在し、実測して差し替えた。

## 決定 2: 🔴 検索（SC-01/02）は「**見つかること**」を判定しない

**判定できないからである。** 根拠を 2 段に分けて残す。

### (a) `200 ＋ 空` が複数の失敗と区別できない

`SearchBffEndpoints` の `POST /bff/search` は次の**すべて**で `Results.Ok(new SearchResponse([], 0, 0))` を返す。

1. `req.Query` が空
2. `BffScopeResolver.ResolveAsync` が `null`（ABAC が deny へ縮退／認可サービス不調）
3. `RetrievalService` への `HttpRequestException` / `TaskCanceledException`

**「検索が全く動いていない」と「該当が無い」がエッジからは同じ応答**になる。
[IADR-0252] が文書一覧で潰したのと同じ型の穴であり、状態コードだけを見るスモークは無価値である。

### (b) このスタックには索引が存在しない

- BFF 経由で作った文書は `MarkdownUri` を持たない（`CreateDocumentRequest` に項目が無く、
  生成経路も設定しない）。`DocumentEndpoints.ToEvent` が `d.MarkdownUri`（= null）を載せて
  `DocumentUpdated` を発行し、`IngestionService.DocumentUpdatedConsumer` は先頭で
  `if (ev.MarkdownUri is null) { …; return; }` と早期 return する。
  **parse→chunk→embed→index に一度も入らない。**
- `values-local.yaml` の `Llm__ApiKey` は未設定＝空（外部 LLM を呼ばない fail-safe）で、
  `k8s-local-up.sh` が投入するのは ABAC ポリシーだけ（文書の初期投入経路が無い）。

**(b) の 1 つめだけで決定的**である —— 索引に入らない以上、何を問い合わせても空しか返らない。

### したがって判定するのは 2 つだけ

- 無トークンの `POST /bff/search` が **401**（`RequireAuthorization()` が効いている）
- トークンありが **200 かつ `SearchResponse` の形**（`results` が配列・`totalHits` / `elapsedMs` が数値）

**「空であること」を PASS の根拠にしていない。** この断りをスクリプト本文にも置く ——
置かないと後から「検索は緑だから動いている」と読まれ、[IADR-0252] が潰した誤読が再発する。

## 決定 3: SC-03（文書詳細）は正負の対で見る

段 14（一覧）が通っても詳細が 404 になる形は捕まらないため、**単体取得**を別に見る。
**200 だけを PASS にしない** —— 別の文書が返っても 200 だからである。`id` と `title` の一致まで見る。
負の対照（属性を持たない利用者が同じ id を引けないこと）を対で置く。
deny-by-default は**存在を秘匿する**（[IADR-0009]）ので **403 でも 404 でも合格**とし、**200 だけを不合格**とする。

## 検出しないこと

- **検索が実際に文書を見つけられるか。** 決定 2 のとおり本スタックでは観測できない。
  索引可能な文書を用意する経路は別 issue とし、**本 ADR では閉じない**。
- **ブラウザ実行の導線。** Playwright は入れない（#466 の「スクリーンショット・トレース」は
  `integration-stack.yml` の `Dump cluster state` の診断出力で代替する）。blast radius が別物である。
- **`k8s-local-up.sh` の `HelmChartConfig` reconcile 失敗の伝播**（#953。[IADR-0248] が pin で回避した構造）。

## 根拠（変異試験・実測）

スタブ HTTP サーバでスクリプトを完走させ、5 件の変異がすべて `EXIT=1` で検出されることを実測した。
とくに **段 16 を丸ごと削除した変異は `PASS 21 / FAIL 1（段 16/17）` となり、FAIL 1 は門が出した 1 件だけ**である
——門が無ければ `PASS 21 / FAIL 0` ＝ 緑であった。

🔴 **初回の変異 3 件は `EXIT=0` を返したが、これは検出漏れではなく「変異が着弾していなかった」**
（`pkill` が効かず `EADDRINUSE` で変異版が起動していなかった）。**証明力の無い変異と検出漏れを混同しない。**
詳細は作業仕様書 `.ai-context/specs/20260822_issue-466_edge-smoke-and-step-loss-gate.md`。
