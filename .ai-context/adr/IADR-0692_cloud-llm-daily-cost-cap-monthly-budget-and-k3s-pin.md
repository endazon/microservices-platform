---
title: IADR-0692 クラウドの LLM ゲートウェイの当日の費用の上限を合計 1 日 20 USD（取引ユニットが主）とし、そこから用途別の月次予算を導いて全経路に置く。段 2 は金額が無ければ起動しない。クラウドの k3s は版を固定し sha256 で照合して入れる
type: impl-adr
status: Accepted
related_ids: [NFR-18, NFR-21, FR-11, ADR-0044, IADR-0466, IADR-0674, IADR-0675, IADR-0678, IADR-0679, IADR-0689, IADR-0698]
author: claude
created: 2026-11-07
updated: 2026-11-07
related_specs:
  - ../specs/20261107_cloud-llm-cost-cap-and-k3s-pin.md
---

# IADR-0692: クラウドの LLM の当日の費用の上限・月次予算と、k3s の導入の固定

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-11-07
- 決定者: claude（所有者の裁定 2026-11-07「稼働の前に中程度の指摘もすべて直す」「LLM の当日の費用の上限は 1 日 20 USD」に基づく。コーディネーターの指示 2026-11-07）

## 起点・関連

- 前提: IADR-0678（送る前の上限。金額の既定を置かない）、IADR-0466（用途別の月次予算のゲージ。金額の既定を置かない・手動確認と併存させない）、
  IADR-0689 決定 12（クラウドは段 2）、IADR-0674（クラウドの最小構成）、IADR-0679（外部の成果物の sha256 の固定）。
- 作業仕様書: `.ai-context/specs/20261107_cloud-llm-cost-cap-and-k3s-pin.md`（現状の実測・sha256 の出どころ・是正の母集合）。

## コンテキスト

- クラウドは段 2（`Llm__CallLimits__Mode=Enforce`）だが、当日の費用の上限の金額が無かった。金額の無い呼び手では費用の判定そのものを飛ばすので、
  費用を止めるのは要求数と入力の上限だけだった。起動時の検証は 0 以下の金額だけを落とし、未設定は通していた。
- 月次予算の金額も未設定で、上限アラートは評価対象を持たなかった。クラウドの Prometheus には LLM の費用の規則が無かった。
- 設定の形は呼び手ごと（`Callers:<呼び手>:DailyCostLimit`）と呼び手 × 用途だけで、ゲートウェイ全体の上限は無い。呼び手の項目に無い値は `Default` を使う。
  通貨は単価表の通貨（USD）、日の区切りは UTC の 0 時（判定器に固定）。
- クラウドの k3s は、版を固定せず、取得したスクリプトを照合せずに実行して入れる手順だった。

## 決定

### 決定 1 — クラウドの当日の費用の上限は、全呼び手の合計で 1 日 20 USD。取引ユニットが主

| 呼び手 | 設定 | 金額（USD / UTC の日） |
| --- | --- | --- |
| `ai-stock-trading-llm-caller`（取引ユニット） | `Llm__CallLimits__Callers__ai-stock-trading-llm-caller__DailyCostLimit` | 18 |
| ナレッジの 4 つの呼び手（`aianalysis-service`・`graph-service`・`retrieval-service`・`conversion-service`） | `Llm__CallLimits__Default__DailyCostLimit`（それぞれに効く） | 0.5 × 4 = 2 |
| 合計 | — | **20** |

- 置き場は `deploy/cloud/values-cloud.yaml` の `extraEnvAppend`（クラウドだけ）。既定とローカルの描画は変えない（試験が固定する）。
- 配分の根拠: クラウドで動くのは取引ユニットだけで、ナレッジのサービスは描かない。取引ユニットの日々の取引判断と報告書が費用の大半を占める。
  ナレッジの枠は、将来クラウドへ持ち込むときに上限なしで動き出さないための残しであり、合計の 1 割に留めた。
- 全体の上限を新設しない理由: 計画は基盤と利用側の費用を合算した 1 つの数値を禁じている（どちらの予算を超えたかが判別できなくなる）。
  合計は「呼び手の集合が閉じている」ことで保つ —— 許可表（CallerPolicy、Enforce）が表に無い呼び手を上限の判定より前に拒む。
- 用途ごとの当日の上限は置かない。報告書の生成のように 1 回が大きい用途を、用途の枠で途中から止めないためである。
- 上限はプロセスごとの状態（IADR-0678 §結果 1）なので、クラウドの 1 レプリカ・HPA なしが前提である（`deploy-cloud.test.js` が固定する）。

### 決定 2 — 月次予算（用途別）は当日の上限の割り当てから導き、全経路の `appsettings.json` に置く

- 式: **月次予算(用途) = 呼び手の日の上限のうち、その用途へ割り当てた額 × 30 日 × 0.5**。0.5 は「日の上限の半分を超える速さで 30 日使い続けた」で知らせる係数である
  （日の上限そのものに達してから知らせても、送る前の上限が先に止めている）。
- 割り当て（USD / 日）と月次予算（USD / 直近 30 日）:

| 呼び手 | 用途 | 割り当て | 月次予算 |
| --- | --- | --- | --- |
| 取引ユニット（18） | trade-decision | 9 | 135 |
| | trade-decision-screening | 2 | 30 |
| | report-daily | 2 | 30 |
| | report-weekly | 1.5 | 22.5 |
| | report-monthly | 0.5 | 7.5 |
| | other（stage0-recording・policy-revision・report-regeneration） | 3 | 置けない（下） |
| aianalysis（0.5） | rag-answer・analysis | 0.25・0.25 | 3.75・3.75 |
| graph（0.5） | graph-suggestion・graph-cluster-summary | 0.25・0.25 | 3.75・3.75 |
| retrieval（0.5） | rerank | 0.5 | 7.5 |
| conversion（0.5） | diagram-coding | 0.5 | 7.5 |

- 取引ユニットの割り当ては、本判断（中位のモデル・場の時間に繰り返す）を最大に、一次スクリーニング（下位のモデル）と日次の報告書を次に、
  週次・月次の報告書（上位のモデル・頻度が低い）を小さく置いた。`other` へ丸まる用途は月次予算に置けない（`other` はどの呼び手の予算かが決まらない値として検証器が拒む）。
  その分は当日の上限（決定 1）だけが守る。
- 置き場を `appsettings.json` の 1 か所にしたのは、月次確認の Runbook が定めた「置き場は 1 か所」（経路ごとに割らない）に従うためである。
  クラウドに限らず compose・経路B でもアラートが評価対象を持つ。
- 同じ変更で月次確認の Runbook を `superseded` にした（IADR-0466 決定 6。併存させない）。
- 割り当ての表と月次予算の一致、割り当ての合計と呼び手の日の上限の一致は `scripts/deploy-cloud.test.js` が見る。**どちらかを変えるときは両方を対で改める。**

### 決定 3 — 段 2 は `Default:DailyCostLimit` が無ければ起動しない（fail-closed）

- `LlmCallLimitOptionsValidator` が、`Mode=Enforce` かつ `Default.DailyCostLimit` が null のとき失敗を返す（ValidateOnStart なので起動で止まる）。
- `Default` を条件にしたのは、呼び手の項目は書かなかった値に `Default` を使うためである。`Default` に金額があれば、項目の有無によらずどの呼び手にも金額が効く。
  一部の呼び手にだけ金額を置いた構成は、書いていない呼び手を無制限に残すので、同じく落とす。
- 0 以下は従来どおり落とす。段 1（Report）は金額なしで起動する（既定とローカルを変えない）。

### 決定 4 — クラウドの月次予算の警報は、保持に合わせて直近 7 日で進みを見る

- `deploy/cloud/observability/prometheus.yaml` に `LlmMonthlyBudgetPaceExceeded` を足す:
  `sum by (llm_purpose, llm_currency) (increase(llm_cost_total[7d])) > on (llm_purpose, llm_currency) max by (llm_purpose, llm_currency) (llm_budget_monthly_limit) * 7 / 30`、`for: 5m`、warning（既定の受信先の Discord へ届く）。
- クラウドの Prometheus は保持 7 日・emptyDir であり、30 日の窓は常に 7 日分しか持たない（compose・経路B の `LlmMonthlyBudgetExceeded` をそのまま置くと、
  月次予算の 30/7 倍を使うまで鳴らない）。窓の違う規則に同じ名前を付けないため、名前を分けた。
- 保持を 30 日へ延ばす案は採らなかった（8 GB のノードに絞った IADR-0675 の資源の判断を変える。再起動で消える emptyDir では延ばしても埋まらない）。

### 決定 5 — クラウドの k3s は、版を固定したリリースのバイナリと同じ版のタグのインストーラを、sha256 で照合してから入れる

- 版は **v1.35.10+k3s1**。試験の k3d の k3s（v1.35.4）と同じ minor の、最新の patch である（試験で通した minor から離れず、patch の修正は取り込む）。
- 版・取得元・sha256 は `scripts/lib/pinned-artifacts.sh` の `pinned_tool_entry`（`k3s`・`k3s-install`）が 1 つに持つ。`pinned_tool` は `.tar.gz` でない配布物も扱うように、
  出力の名前の拡張子を URL から決めるようにした（openbao の出力は従来どおり `.tar.gz`）。
- 手順（`deploy/cloud/README.md` §1-4）: 手元で `pinned_tool` で取得して照合 → 照合に使った値と一緒にノードへ運ぶ → ノードで `sha256sum -c` → `/usr/local/bin/k3s` に置く →
  `INSTALL_K3S_SKIP_DOWNLOAD=true` でインストーラに取得をさせずに実行する。k3s の起動の旗は変えない（試験が既存の値と突き合わせる）。
- sha256 の出どころ: バイナリは同じリリースの `sha256sum-amd64.txt` と一致を確かめた。インストーラには公式の checksum が無いので、2 回取って一致した値を固定した。
- 取得物を照合せずにシェルへ流す形が `deploy/cloud/` の全ファイルと `docs/operations/cloud-*` に無いことを、`scripts/deploy-cloud.test.js` が固定する。

## 結果・残余リスク

1. **再起動で当日の費用の枠は 0 に戻る**（IADR-0678 §結果 2）。再起動を繰り返すと、1 日に上限の数倍を使い得る。事後の観測（決定 4）が残る。
   ［2026-11-07 追記 / IADR-0698］所有者の裁定（再起動しても残るように直す）により、クラウドでは当日の費用の枠を PVC の上のファイルに残し、
   再起動の後は前回の値を読み込んでから判定する（読めない間は送らない）。この残余は解消した。
2. **`other` へ丸まる取引ユニットの用途は月次予算の外**である。当日の上限だけが守る。単価表の用途に足せば月次予算に置ける（別の判断）。
3. 月次予算の進みの警報は、クラウドの Prometheus が再起動でデータを失うと、7 日に満たない分だけ鳴りにくくなる。
4. 合計 20 USD は、許可表が表に無い呼び手を拒むことに依る。許可表を段 1 へ戻すと、表に無い呼び手にも既定の 0.5 USD が効き、合計は 20 USD を超え得る。
5. 提供元の側の支出の上限（コンソールの設定）はリポジトリの外である。所有者の運用で併用する。
   ［2026-11-07 追記 / IADR-0698］所有者の裁定により、クラウドの稼働の前提条件として `deploy/cloud/README.md` §3 に明記した（値は所有者が決め、リポジトリに書かない）。
6. 見込み額は第 1 候補のモデルの単価で換算する（IADR-0678 §結果 7）。
7. k3s の版を上げるときは、表の 2 行と README の版を対で改める。インストーラの sha256 は公式の値が無いので、取り直して一致を確かめる手間が残る。

## フォローアップ

- 実測（費用の実績）がそろったら、割り当てを見直す（決定 2 の表と試験を対で改める）。
