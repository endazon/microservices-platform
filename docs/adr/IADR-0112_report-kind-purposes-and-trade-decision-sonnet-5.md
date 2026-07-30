---
title: IADR-0112 報告書を種別ごとの用途へ分離してモデルを割り当て、取引判断を claude-sonnet-5 へ改定する
type: impl-adr
status: Accepted
related_ids:
  - FR-11
  - ADR-0010
  - ADR-0025
  - IADR-0022
  - IADR-0101
  - IADR-0102
  - IADR-0106
author: claude
created: 2026-07-30
updated: 2026-07-30
plan_refs:
  - "../../planning/projects/ai-stock-trading/07_adr/ADR-0011_llm-model-pinning.md (取引判断の LLM モデル固定・Accepted)"
  - "../../planning/projects/ai-stock-trading/04_workflows/03_reporting-cycle.md (報告サイクル: 月報→週報→日報→取引の方針階層)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0010_llm-gateway.md (LLM ゲートウェイ設計・Accepted・本文凍結)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0025_llm-model-opus-5.md (グローバル既定を Opus 5 へ改定・Accepted)"
---

# IADR-0112: 報告書の種別別用途と取引判断モデルの改定

- 状態: Accepted
- 日付: 2026-07-30
- 決定者: claude（実装）／利用者（モデル割当の仕様指定・AST/ADR-0011 の改定意思）

## 起点・関連

- 起点 issue: [#420](https://github.com/endazon/microservices-platform/issues/420)（報告書の種別別 purpose）、
  [#421](https://github.com/endazon/microservices-platform/issues/421)（`trade-decision` の改定）。
- 計画への環流: [project-planning#50](https://github.com/endazon/project-planning/issues/50)。
  AST/ADR-0011 の改定を**新 ADR で**起案する依頼（本 IADR に先行して起票済み）。
- 仕様書: `docs/specs/20260730_issue-420-421_report-and-trade-model-routing.md`。
- 本 IADR は [[IADR-0022]] の**用途別割当の内容のみ**を更新する。ルーティング設計・ティア判定・
  ZDR 除外ロジックは変更しない。
- AST 側の対応（種別ごとの purpose 送出・上位方針の feed-forward）は
  [ai-stock-trading#291](https://github.com/endazon/ai-stock-trading/issues/291) /
  [#293](https://github.com/endazon/ai-stock-trading/issues/293)。別リポ・別 PR。

## コンテキストと課題

利用者は本システムを「生成 AI を活用した金融商品の完全自動取引システム」と定義し、**月報/週報/日報を
「次の取引に活かす方針書」**と位置づけたうえで、用途ごとの割当モデルを仕様として指定した。

| 用途 | 指定モデル | 現行の実効モデル |
| --- | --- | --- |
| 月報 | `claude-fable-5` | `claude-opus-5`（`default` 追随） |
| 週報 | `claude-opus-5` | `claude-opus-5`（`default` 追随＝**偶然の一致**） |
| 日報 | `claude-sonnet-5` | `claude-opus-5`（`default` 追随） |
| 取引判断 | `claude-sonnet-5` | `claude-opus-4-8`（[[IADR-0102]] のピン） |

論点は 4 つある。

### 1. 報告書の用途エントリが存在しない

AST の report-service は単一 purpose `report-narrative` で `/complete` を呼ぶ。これは `PurposeModels` に
無いため `DefaultModel` へ着地し、**3 種別すべてが `claude-opus-5`** で生成されている。方針階層
（`AST/04_workflows/03_reporting-cycle` の 月報→週報→日報→取引）の「上位ほど難度が高い」という
性質が、モデル選択に一切反映されていない。

### 2. 週報の一致は偶然であり、`default` 改定で無音に失効する

週報の指定値 `claude-opus-5` は現行の実効モデルと一致する。しかしそれは `default` 追随の結果であって、
割当として固定されていない。`default` を改定した瞬間（[[IADR-0101]] が実際に行った操作）に週報の
モデルは何の通知もなく変わる。[[IADR-0102]] が取引判断で踏んだ失効と同じ構造であり、
「現在値が一致しているから設定不要」は誤りである。**一致していても明示エントリを置く。**

### 3. `Models`（利用許可集合）未登録は無音で失敗する

`LlmRouter.ResolveModel` の用途別解決は `eligible.Contains(purposeModel)` を条件とし、`eligible` は
エンドポイントの `Models` から導出される。`PurposeModels` へ用途を追加しても対象モデルが `Models` に
無ければ、**例外もログも出さずに `DefaultModel` へ落ちる**（#376 / [[IADR-0102]] で実際に踏んだ罠）。

本作業で用いる 4 モデル（`claude-fable-5` / `claude-opus-5` / `claude-sonnet-5`）はいずれも
`claude-managed` の `Models` に登録済みであり、追加は不要である。ただし「今回は不要だった」で
済ませず、[[IADR-0106]] の集合ガード（T-19）が新用途も自動的に検査することを確認する。

### 4. `NonZdrModels` により月報だけが機密区分で失効し得る

`claude-managed` の `NonZdrModels` は `["claude-fable-5"]` である。`EligibleModels` は
`EgressMatrix.RequiresZeroDataRetention(sensitivity)` が真のとき（`confidential` / `restricted`）に
`NonZdrModels` を除外するため、**機密区分を上げると月報だけが `claude-fable-5` を失い
`DefaultModel` へ落ちる**。

report-service の既定 `LlmGateway:Confidentiality` は `internal`（ZDR 要件なし）であり現状は成立する。
`analysis` = `claude-fable-5` が既に同じ性質を持っている（T-13）ため、本作業は新しい脆さを持ち込むので
はなく、既存の性質を月報にも適用する。ただし「月報のモデルは機密区分の設定次第で黙って変わる」ことは
運用上の重要事実であり、テストで固定して明示する。

### 5. `trade-decision` の改定は設定書き換えだけでは行えない

AST/ADR-0011（Accepted）§決定:

> モデルを更新する場合は、新モデルで Stage 0（コスト2倍・ウォークフォワード・DSR/PBO 補正）を再実行し、
> エッジが維持されることを確認してから採用する。更新は月報レビュー時のみとし、更新前後のモデル ID を
> 報告書へ記録する。

[[IADR-0102]] §結果も「**設定値の書き換えだけで更新してはならない**」と明記している。

## 検討した選択肢

### A. 報告書のモデル割当

1. **種別ごとに独立した purpose（`report-monthly` / `report-weekly` / `report-daily`）を置く（採用）**
   — 方針階層をそのままルーティングへ写す。週報が現行値と一致していても明示エントリを置くことで、
   `default` 改定による無音失効（論点 2）を断つ。`PurposeModels` は既に用途別の辞書であり、
   新しい機構を要さない。
2. 単一 `report-narrative` のまま、モデルを 1 つ選ぶ — 現行構造の維持。利用者の仕様指定
   （種別ごとに別モデル）を満たせない。
3. AST 側が `Model` を明示指定する — `ResolveModel` の優先順位①（`RequestedModel`）で固定できるが、
   固定モデル ID が AST の設定へ散らばり、基盤の `Models` 許可一覧との整合を運用で担保しにくい。
   [[IADR-0102]] が選択肢 3 として同じ理由で退けた案であり、一貫性のためにも採らない。

### B. 取引判断のモデル改定

1. **設定を改定し、Stage 0 再検証を実弾解禁の必須ゲートとして課す（採用）** — 後述 §決定 3。
2. Stage 0 再検証の完了を先行条件にする — AST/ADR-0011 の字面には最も忠実。しかし Stage 0 は
   バックテストの実過去データ源が未接続で**実行不能**（AST#208）であり、利用者の仕様指定が無期限に
   凍結される。「守れない手続きを掲げて実質何もしない」状態は、ADR が守ろうとした検証妥当性を
   1 ミリも増やさない。
3. 設定だけ書き換えて手続きを踏まない — [[IADR-0102]] §結果が明示的に禁じている。採らない。
4. AST/ADR-0011 を Deprecated にしてピン留めをやめる — 取引判断が `default` に追随するようになり、
   ADR-0011 が守ろうとした再現性・監査可能性そのものを失う。利用者が指定したのは**ピンの値**で
   あって、ピンする仕組みの廃止ではない。採らない。

## 決定

### 決定 1: 報告書を種別ごとの用途へ分離する

`Llm:Routing:PurposeModels` へ次を追加する。

| purpose | モデル |
| --- | --- |
| `report-monthly` | `claude-fable-5` |
| `report-weekly` | `claude-opus-5` |
| `report-daily` | `claude-sonnet-5` |

- **週報は現行の実効モデルと同値だが、明示エントリを置く。** 一致は `default` 追随による偶然であり、
  `default` の改定で無音に失効する（論点 2）。
- **`report-narrative` は削除しない。** AST が種別ごとの purpose へ移行するまでの間、および
  `LlmGateway:Purpose` を明示設定した既存デプロイのために、未知 purpose として `default` へ落ちる
  従来挙動を維持する（非破壊）。移行完了後の掃除は §フォローアップ 1。

### 決定 2: `Models` / `NonZdrModels` は変更しない

- 4 モデルはすべて `claude-managed` の `Models` に登録済みである。追加は不要。
- `NonZdrModels` の `claude-fable-5` は事実であり変更しない。**`confidential` / `restricted` の
  月報は `claude-fable-5` を失い `DefaultModel`（`claude-opus-5`）へ落ちる。** これは安全側の
  正しい挙動だが、無音であるためテスト（T-22）で固定して明示する。
- report-service の既定機密区分は `internal`（ZDR 要件なし）であり、指定どおり `claude-fable-5` が
  選択される。**`LlmGateway:Confidentiality` を上げると月報のモデルが黙って変わる**ことを運用の
  既知事項として記録する。

### 決定 3: `trade-decision` を `claude-sonnet-5` へ改定し、Stage 0 再検証を実弾解禁の必須ゲートとして課す

`PurposeModels.trade-decision` を `claude-opus-4-8` → **`claude-sonnet-5`** へ改定する。
AST/ADR-0011 の手続きは次のとおり踏む。

**(1) 計画への環流を先行させる。** ADR は Accepted 後に本文を実質変更しない規約
（`planning/.claude/rules/adr.md`）に従い、**ADR-0011 を書き換えず新 ADR を起案**する依頼を
[project-planning#50](https://github.com/endazon/project-planning/issues/50) へ起票済み。
併せて ADR-0011 §決定の「報告書生成の LLM は別扱い。基盤の既定モデルを用いてよい」も、報告書が
方針書である以上整合しないため改定を提案している（決定 1 の計画側根拠）。

**(2) Stage 0 再検証は「要る」。ただし実弾解禁の必須ゲートとして課す。**

- ADR-0011 が Stage 0 一致を求める理由は、§コンテキストが述べるとおり「バックテスト（Stage 0）で
  検証したモデルと**本番**モデルが異なれば、検証結果の妥当性が失われる」ことにある。
  現状は実弾 OFF（`TrdEnv=real` は起動時停止・閂②③がコード固定。AST#217）であり、実資金の取引が
  存在しない。**したがって現時点の設定変更で毀損する検証妥当性は存在しない。**
- Stage 0 再検証は**現時点で実行不能**である。バックテストの実過去データ源が未接続であり
  （AST#208「実データ未接続では昇格ゲートが実効化しない」）、再検証を先行条件とすると利用者の
  仕様指定が無期限に凍結される。
- よって設定は改定し、**`claude-sonnet-5` での Stage 0 再検証を実弾解禁の必須ゲート**として
  AST#208 / #217 と束ねて追跡する（§フォローアップ 2）。実弾解禁時に「Stage 0 で検証したモデル」と
  「本番モデル」が一致しない状態は ADR-0008 の段階ゲートを空洞化させるため、ここは譲らない。

**(3) バージョン固定の原則は維持する。** 改定するのはピンの値であって、ピンする仕組みではない。
`trade-decision` は引き続き `PurposeModels` の明示エントリを持ち、`default`（`claude-opus-5`）の
改定に自動追随しない。AST/ADR-0011 §決定の「基盤の定型 RAG 層のモデル改定には自動追随しない」は
本改定後も成立する。

**(4) `claude-opus-4-8` は `Models` に残す。** `Models` は「割当」ではなく「利用を許可するモデル集合」
であり、削除すると明示 `Model: "claude-opus-4-8"` を送る呼び出し側が黙って別モデルへ落ちる
（[[IADR-0106]] の判断を踏襲）。ロールバックの余地も残る。

## 理由

- **方針階層をルーティングへ写すのが最も素直である。** `PurposeModels` は既に用途別の辞書であり、
  種別ごとに purpose を分けるだけで実現できる。新しい機構・抽象化を持ち込まない。
- **週報に明示エントリを置くのは冗長ではなく防御である。** [[IADR-0101]] が `default` を改定した
  結果 [[IADR-0102]] のピンが必要になったという実例が既にある。「今の値が一致している」は
  設定を省く理由にならない。
- **取引判断の改定は、手続きを空文化させずに仕様を通す唯一の道である。** 選択肢 B-2（再検証を
  先行条件にする）は字面には忠実だが、実行不能な条件を掲げるだけで検証妥当性を増やさない。
  実弾 OFF という事実に基づいて「いま失うものは無い／実弾解禁時には失う」を切り分け、後者にゲートを
  置くほうが、ADR-0011 の**目的**（検証と本番の一致による監査可能性）を実際に守る。
- **費用面は概ね有利である。** 最頻の日報が `claude-opus-5` → `claude-sonnet-5`、取引判断が
  `claude-opus-4-8` → `claude-sonnet-5` へ下がる。増えるのは月 1 回の月報（`claude-fable-5`）のみ。
  月次 LLM 費用上限 15,000 円（`AST/06_technical/05_trading-assumptions` §6）に対しては改善方向だが、
  Sonnet 5 の新トークナイザ（同一テキストで約 +30% トークン）が相殺し得るため実測で確認する
  （§フォローアップ 3）。

## 結果

- 良い影響:
  - 報告書の方針階層（月報→週報→日報）がモデル選択に反映され、最上位の月報に最難関モデルが充たる。
  - 週報・日報・取引判断が `default` 追随から外れ、基盤の既定モデル改定による**無音の挙動変化**が
    3 用途ぶん減る。
  - 取引判断の割当が利用者の仕様と一致し、`claude-opus-4-8` という単一版数への依存が解ける
    （#382 の提供終了リスクが下がる）。
  - 最頻経路（日報・取引判断）の単価が下がる。
- 悪い影響 / トレードオフ:
  - **`claude-sonnet-5` での Stage 0 再検証が未実施のまま設定が先行する。** 実弾 OFF の現在は
    害が無いが、この負債を実弾解禁前に必ず解消する必要がある（§フォローアップ 2）。忘れると
    ADR-0008 の段階ゲートが空洞化する。
  - **月報の `claude-fable-5` は ZDR 非対応**であり、`LlmGateway:Confidentiality` を `confidential`
    以上へ変更すると黙って `claude-opus-5` へ落ちる。設定変更時に気付ける仕組みは無い
    （#384 の起動時検証は `Models` 未登録は検知できても ZDR 除外は検知しない）。
  - **取引判断が Sonnet 5 系になり、新トークナイザで入出力トークン数が約 +30% 増える**。単価低下と
    相殺するが、レート制限枠・プロンプトキャッシュ最小長のベースラインはずれる。
  - `PurposeModels` のエントリが 5 → 8 に増え、`Models` 許可一覧との整合を維持する運用負荷が増える。
    #384（起動時検証）の優先度が上がる。
  - **本 PR 単体では報告書のモデル割当は実効化しない。** AST が種別ごとの purpose を送るまで
    （AST#291）、report-service は `report-narrative` を送り続け `default` へ落ちる。これは
    非破壊（現行と同じ挙動）だが、「設定したのに変わらない」状態が一時的に生じる。
- フォローアップ:
  1. **`report-narrative` エントリの掃除**。AST#291 の移行完了後、`LlmGateway:Purpose` の明示設定が
     どのデプロイにも残っていないことを確認したうえで、未知 purpose のフォールバック経路を
     整理するか判断する。
  2. **`claude-sonnet-5` での Stage 0 再検証を実弾解禁の必須ゲートとして追跡する**（AST#208 / #217）。
     本 IADR の最重要フォローアップ。実データ源が接続され次第、Stage 0 を再実行してエッジ維持を
     確認し、結果を月報へ記録する（ADR-0011 §決定「更新前後のモデル ID を報告書へ記録する」）。
  3. **費用の実測と月次上限の再評価**（#380 / AST#243）。日報・取引判断の単価低下と Sonnet 5 の
     +30% トークンの差引、および月報 `claude-fable-5` の月 1 回ぶんを実測する。
  4. **#382 の見直し**。`claude-opus-4-8` の提供終了監視は本改定でピン対象から外れたため、
     追跡内容を `claude-sonnet-5` へ改める必要がある。
  5. **AST#285 の解消**。取引判断の実効モデルに関する AST 側ドキュメントの記述が本改定で再び
     陳腐化するため、AST 側で追随する。

## 関連

- Supersedes: なし（[[IADR-0102]] の**ピンの値のみ**を改定する。ピンする仕組み・`Models` への
  登録という決定は維持される）
- Superseded by: なし
- 関連要求 / UC: FR-11（LLM 送信可否の統制）、`AST/ADR-0011`（取引判断のモデル固定）、
  `AST/FR-04`（AI 判断のガードレール）、`AST/FR-06`（報告書）
