---
title: 作業仕様書 — 統合スタック上の主要導線スモークと、段の消失を検出する門（#466）
type: spec
status: in-progress
related_ids:
  - FR-03
  - FR-05
  - FR-09
  - NFR
  - SC-01
  - SC-02
  - SC-03
  - ADR-0043
  - IADR-0009
  - IADR-0151
  - IADR-0232
  - IADR-0243
  - IADR-0248
  - IADR-0252
author: claude
created: 2026-08-22
updated: 2026-08-22
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
related_specs:
  - "../adr/IADR-0248_integration-stack-ci-readiness-gate.md"
  - "../adr/IADR-0252_abac-positive-path-observation.md"
---

# 作業仕様書: 主要導線スモークと段の消失を検出する門（#466）

> **本書は #783 が段 2 として引き継いだ #466 を扱う。** 土台（統合スタックを CI で起こす経路）は
> #783 やること② / PR #963 / [IADR-0248] で着地済みであり、本作業は**その門の後ろに載せるスモーク**である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-03（横断検索）・FR-05（ABAC による可視性）・FR-09（属性/辞書）
- 画面（SC）: SC-01（検索）・SC-02（検索モード・並び順）・SC-03（文書詳細）
- 関連 ADR: ADR-0043（読み取り口を 3 種類作らない）・[IADR-0009]（deny-by-default で存在を秘匿する）
- issue: #466（#453 から分割）／土台: #783・PR #963

## 目的・背景

#783 の完了コメントが段 2 として次の 3 点を引き継いだ。

1. `scripts/verify-oidc-edge-flow.sh` を統合スタックのジョブに載せ、**PASS 件数を baseline 化**して退行を止める
2. 主要導線（ログイン → 検索 → 文書詳細 → ABAC 不可視）のスモーク本体
3. その前に `verify-oidc-edge-flow.sh` の期待値の陳腐化を直すこと

### 着手時の実測（前提の再確認。引き継ぎを鵜呑みにしない）

| 引き継ぎの主張 | 実測 | 判定 |
| --- | --- | --- |
| ③ 期待値の陳腐化（無トークン `GET /bff/documents` の期待が 200） | **既に修正済み**。`scripts/verify-oidc-edge-flow.sh` 段 8 は 401 を期待し、経緯（#659 が 12 日前に塞いだ）も本文に残っている | ✅ **不要** |
| ① スクリプトはジョブに載っているか | **載っている**。`.github/workflows/integration-stack.yml` の `🔴 Gate — ABAC の正常系が観測できる（#972）` が `ABAC_POSITIVE=1 bash scripts/verify-oidc-edge-flow.sh` を実行する | ✅ **不要** |
| ① PASS 件数の baseline 化 | **未実施**。後述のとおり `TOTAL` は表示専用である | ❌ **要対応** |
| ② 主要導線のスモーク | **検索（SC-01/02）と文書詳細（SC-03）が未カバー**。`integration-stack.yml` に `SC-01`/`SC-02`/`SC-03`/`playwright` の参照は 0 件（grep EXIT=1） | ❌ **要対応** |

## 対象範囲

- 対象: `scripts/verify-oidc-edge-flow.sh`（段の消失を検出する門、SC-03 の文書詳細、SC-01/02 の検索）
- 対象外: `.github/workflows/integration-stack.yml` の起動条件・必須チェックの変更（#783 が決めた形を動かさない）
- 対象外: Playwright によるブラウザスモーク（#466 本文の「スクリーンショット・トレース」は
  `integration-stack.yml` の `Dump cluster state` が満たす診断出力で代替する。ブラウザ導入は blast radius が別物）

## 設計

### ① 段の消失を検出する門（`PASS + FAIL == TOTAL`）

**現状は fail-open である。** 実測: `TOTAL` は `if [ "$ABAC_POSITIVE" = "1" ]; then TOTAL=13; else TOTAL=9; fi`
で決まるが、**用途は `step "N/$TOTAL"` の表示だけ**であり、`grep -n 'TOTAL'` の全 14 件が
宣言 1 件 ＋ 表示 13 件である。終了判定は `if [ "$FAIL" -gt 0 ]` のみを見る。

したがって **段が削除される／`if` ガードで静かに飛ぶ**と、`PASS` が減ったまま `FAIL=0` で
**EXIT=0（緑）**になる。「走らせた段が減ったこと」を誰も検出しない。

**#783 が「PASS 件数を baseline 化」と呼んだのはこの穴である。** ただし別ファイルの baseline は置かない
——`TOTAL` が既に「本来走るべき段数」の単一情報源であり、**同じ不変条件の情報源を 2 箇所に持たない**
（[IADR-0141]「参照点を 1 つに畳む」）。

### 🔴 素朴な `PASS + FAIL == TOTAL` は成立しない（実測して設計を差し替えた）

**着手時の設計は `PASS + FAIL == TOTAL` だった。これは誤りである。**
`TOTAL` が数えているのは**段**であって**判定**ではない。1 つの段が複数の判定を刻む:

- 段 6（クレーム確認）は `iss` / `preferred_username` / `clearance` / `department` の **4 判定**
- 段 7（BFF 導線）は宛先ごとに刻むため **3 判定**

実測（probe run `32554867883`・`ABAC_POSITIVE` 無し）の出力は **PASS 12 / FAIL 2 = 14 判定**で、
同じ実行の `TOTAL` は **9** である。**素朴な等式を入れていたら、正常な実行が即座に赤になった。**

したがって**判定ではなく段を数える**。`step()` に「番号つきの段」だけを数える計数器を持たせ、
最後に `STEPS == TOTAL` を見る。これなら「段が消えた／静かに飛ばされた」ことだけを捕まえ、
1 段が何判定を刻むかには依存しない。

```
番号つきの step() が呼ばれた回数（STEPS）が TOTAL と一致しなければ FAIL とし、EXIT=1 にする。
```

### ② SC-03（文書詳細）のスモーク —— 決定的に観測できる

段 11 が作成した文書の **id を捕まえ**、`GET /bff/documents/{id}` を叩く。

- 正の対照: 200 ＋ `id` 一致 ＋ `title` 一致（**200 だけを PASS にしない**）
- 負の対照: 属性を持たない利用者（`$DENY_USER`）が同じ id を引けないこと

段 12 が一覧（`GET /bff/documents`）を見るのに対し、こちらは**単体取得**であり、
SC-03 の導線（一覧 → 詳細）が実際に繋がることを見る。

### ③ SC-01/02（検索）のスモーク —— 🔴 **「見つかること」は観測できない**

**素朴な設計は成立しない。** `POST /bff/search` は次の**すべて**で `200 ＋ 空` を返す
（`SearchBffEndpoints.cs` を読んで確認した）。

1. `req.Query` が空
2. `BffScopeResolver.ResolveAsync` が `null`（ABAC が deny へ縮退 / 認可サービス不調）
3. `RetrievalService` への `HttpRequestException` / `TaskCanceledException`

**つまり「検索が完全に壊れている」と「該当が無い」が、エッジからは区別できない。**
段 12 が文書一覧で潰したのと**同じ型の穴**である。よって状態コードだけを見るスモークは無価値である。

そして **「作成した文書が検索で見つかること」は、現在の CI スタックでは原理的に成立しない。**
実測した理由は 2 つあり、**1 つめだけで決定的**である。

1. 🔴 **BFF 経由で作成した文書は索引されない。** `CreateDocumentRequest` は `MarkdownUri` を
   受け取らず（`Title` / `OriginalUri` / `ContentType` / `Attributes` / `Tags` のみ）、
   `Document` の生成経路が `MarkdownUri` を設定しない。`DocumentEndpoints.ToEvent` は
   `d.MarkdownUri`（= null）を載せて `DocumentUpdated` を発行し、
   `IngestionService` の `DocumentUpdatedConsumer` は先頭で
   **`if (ev.MarkdownUri is null) { LogWarning("skipping ingestion"); return; }`** と早期 return する。
   **parse→chunk→embed→index に一度も入らない。**
2. 埋め込みの供給が無い。`deploy/local/values-local.yaml` は `Llm__ApiKey` を
   「未設定なら空文字 = 外部 LLM を呼ばない」fail-safe で配線しており、CI では鍵が入らない。
   `k8s-local-up.sh` が投入するのは ABAC ポリシーだけで（`ABACSEED=1`）、**文書の初期投入経路は無い**。

**したがって本作業では「見つかること」を assert しない。** 代わりに、観測できることだけを門にする。

- 認証の門: 無トークンの `POST /bff/search` が **401**（`RequireAuthorization()` が効いている）
- 応答の形: トークンありの `POST /bff/search` が **200 かつ `SearchResponse` の形**
  （`results` が配列であること。**壊れた JSON や別の形を 200 で通さない**）

そのうえで **「空であることを PASS の根拠にしていない」ことを本文に明記する** ——
ここを後から「検索は緑だから動いている」と読まれると、#972 が潰したのと同じ誤読が再発する。

**観測可能にするための欠落は別 issue として起票する**（本作業では閉じない）。

## 受け入れ基準

- [ ] 段を 1 つ削ると `verify-oidc-edge-flow.sh` が **FAIL** する（変異試験で実測）
- [ ] **正常な実行では門が誤発火しない**（`STEPS == TOTAL` が両モードで成立することを実測）
- [ ] SC-03 の文書詳細が正の対照・負の対照の**対**で判定される
- [ ] SC-01/02 の検索が、認証の門と応答の形で判定される
- [ ] **「見つかること」を観測できない理由**が、本仕様書とスクリプト本文の両方に根拠つきで残っている
- [ ] `TOTAL` の値が新しい段数と一致している（ズレると①自身が落ちる＝自己検査になっている）
- [ ] 変異試験をしている —— **壊すと実際に落ちる**ことを実測で示す

## 変異試験（**実測**）

実クラスタ無しで変異を回すため、`verify-oidc-edge-flow.sh` が叩く 2 つの origin
（`EDGE_URL` / `KC_URL`）を受けるスタブ HTTP サーバを scratchpad に立てて計測した。
**スタブは足場でありリポジトリへはコミットしない**（検査対象ではない）。
スクリプトは JWT の署名を検証せず payload を base64 デコードするだけなので、
偽の JWT でクレーム段まで通せる。

### 基準（変異なし）—— 🔴 **門が誤発火しないこと**

| モード | EXIT | 出力 |
| --- | ---: | --- |
| `ABAC_POSITIVE=1` | **0** | `結果: PASS 22 / FAIL 0（段 17/17）` |
| 既定（ABAC 段なし） | **0** | `結果: PASS 16 / FAIL 0（段 11/11）` |

**`PASS 22` と `TOTAL 17` が一致しないことに注意。** 当初設計の `PASS + FAIL == TOTAL` を
入れていたら、この健全な実行が即座に赤になっていた。**段を数える設計にした理由がここに出ている。**

### 変異と結果

| # | 変異 | 着弾確認 | EXIT | 検出した判定 |
| --- | --- | --- | ---: | --- |
| M1 | 検索が契約と違う形（`{items:[]}`）を 200 で返す | スタブ側 | **1** | 段 11「200 だが SearchResponse の形ではない」 |
| M2 | 詳細が**別の文書の** title を 200 で返す | スタブ側 | **1** | 段 16「詳細の title が一致しない（期待 … / 実際 someone-elses-doc）」 |
| M3 | 属性を持たない利用者へ詳細を 200 で返す | スタブ側 | **1** | 段 17「200（属性が無いのに詳細が見えている）」 |
| M4 | **段 16 を丸ごと削除**（27 行削除 / 0 行追加） | 実装直後の版との `diff` で削除のみを確認 | **1** | 門「実行した段が 16 本で、宣言（TOTAL=17）と一致しません」 |
| M5 | `TOTAL` だけを旧値（13/9）へ戻す | `diff` で 1 行のみを確認 | **1** | 門「実行した段が 17 本で、宣言（TOTAL=13）と一致しません」 |

🔴 **M4 が門の存在理由そのものである。** M4 の出力は `PASS 21 / FAIL 1（段 16/17）` であり、
**FAIL 1 は門が出した 1 件だけ**である。門が無ければ `PASS 21 / FAIL 0` ＝ **EXIT=0（緑）** になり、
段が 1 本消えたことは誰にも見えなかった。

各変異のあと `diff` で当該箇所のみの変化を確認してから実行し、**復旧後に差分 0 へ戻ること**
（`diff EXIT=0`）を確認した。

### 🔴 最初の変異試験は**証明力が無かった**（記録として残す）

M1〜M3 の初回実行は 3 件とも `EXIT=0 / PASS 22 / FAIL 0` を返した。
**これを「判定が効いていない」と読んではならなかった。** 実際には
`pkill` がスタブを落とせておらず（`EADDRINUSE: address already in use`）、
**変異版が一度も起動せず、3 回とも変異なしの元プロセスを叩いていた**。
つまり**変異が着弾していなかった**のであって、検出漏れではない。

変異ごとに別ポートを割り当てて再実行したところ、3 件とも `EXIT=1` で期待どおり検出した。
**以後、変異は「着弾したこと」を先に確かめてから結果を読む**（本表の「着弾確認」列）。

## 採番

**`IADR-0253` を確保した。** 実測: develop の最大は `IADR-0252` で、**0253 は develop 上では空き**である。

**ただし PR #990（`feat/iadr-0253-authz-scope-disjunction`）が同じ 0253 を主張している。**
規約（`.claude/rules/traceability.md`「採番衝突時の改番手順」）は
**「番号は先にマージした側が確保する。後発は次の空き番号へ改番し、欠番を作らない」**であり、
**「欠番を作らない」が効く**ため、develop 上で空いている 0253 を取る。
0254 を仮置きすると `check-adr-numbering.js` が
`[missing-number] IADR-0253 が欠番` で**即座に落ちる**（実測 EXIT=1）。

🔴 **マージ直前に develop を引き直すこと。** #990 が先に着地していれば、
本 PR は後発なので **0254 へ改番する**（ファイル名・本文の自称番号・索引・本仕様書・PR タイトル）。
**#933 が仮置き番号のまま欠番を作って CI を止めた実例がある。**
改番するときは**間違っている側の番号で grep する**（正しい側で grep しても取りこぼしは見つからない）。
