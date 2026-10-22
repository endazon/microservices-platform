---
title: IADR-0582 プロンプトの信頼境界の部品を knowledge ユニットの共有契約へ移して GraphService の提案・クラスタ要約にも適用し、BFF の全応答に nosniff・最小の CSP（sandbox）・フレーム禁止・同一オリジンの資源ポリシーを付ける
type: impl-adr
status: Accepted
related_ids: [NFR, FR-17, FR-18, ADR-0032, ADR-0033, ADR-0035, ADR-0051, IADR-0580, IADR-0579, IADR-0266, IADR-0430]
author: claude
created: 2026-10-22
updated: 2026-10-22
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0051_ai-suggestion-abac-boundary.md
  - planning:projects/microservices-platform/07_adr/ADR-0035_graphrag-retrieval-strategy.md
  - planning:projects/microservices-platform/07_adr/ADR-0032_spa-auth-bff-session.md
related_specs:
  - ../specs/20261022_ghsa-5h4q_graph-prompt-boundary-and-bff-headers.md
---

# IADR-0582: GraphService のプロンプトの信頼境界と BFF の応答ヘッダ（GHSA-5h4q-4m2j-rjhm）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-22
- 決定者: コーディネーター指示（2026-10-22。IADR-0579 / IADR-0580 が射程外とした 2 点）・claude（置き場所・ヘッダの値）

## 起点・関連

- 起点: GHSA-5h4q-4m2j-rjhm（多層防御の強化）。本 IADR はその 3 本目。
- 前提: [IADR-0580](./IADR-0580_rag-prompt-trust-boundary.md)（ask / analyze の信頼境界。§残る制約で GraphService を射程外とした）・
  [IADR-0579](./IADR-0579_answer-sanitize-allowlist-and-spa-csp.md)（§残る制約で BFF の応答ヘッダを射程外とした）・
  IADR-0266 / IADR-0430（提案・クラスタ要約の封）。
- 計画: **ADR-0051**・**ADR-0035**・**ADR-0033**・**ADR-0032**。矛盾しない（送る文書の集合・封の構築経路・BFF の経路は変えない）。

## コンテキストと課題

- GraphService の AI 提案（FR-18）とクラスタ要約（FR-17）は、文書の表題（取り込み文書由来）を並べたプロンプトを LLM へ送る。
  多層防御として、ask / analyze と同じく、こちらの指示と文書の側の文字列を区別できる形にする。
- BFF の応答には、文書として開かれたときの扱いを決める応答ヘッダが付いていない。SPA の配信（Caddy）とは別の経路
  （エッジから BFF へ直接）でも効く値を BFF 自身が付ける。

## 検討した選択肢

1. **部品の置き場所**（`PromptTrustBoundary`）:
   - (a) GraphService へ複写 —— 2 か所で片方が古くなる。
   - (b) platform の共有（`Platform.Shared.*`）—— ユニット外の共有は 3 プロジェクトに限られ（IADR-0117）、使うのは knowledge ユニットだけである。
     platform へ置くと可変ユニットの都合で基盤の共有面が増える。
   - (c) **knowledge ユニットの共有契約 `Knowledge.Contracts`（採用）**: 両サービスが既に参照している。ロジックを持つ静的型の前例
     （`ConfidentialityLevels`・`AiInputExposure`）がある。サービス間の直接参照は作らない。契約スキーマの baseline に型の追加（非破壊）を記録する。
2. **API の応答に CSP は要るか**: 要る（採用）。BFF の応答は fetch・`<img>` で読まれる前提で、CSP はそれらには効かない。
   効くのは**応答が文書として開かれたとき**だけであり、そのときに何も実行・取得・埋め込みさせない最小の値（`default-src 'none'; frame-ancestors 'none'; sandbox`）は
   正しい使い方を壊さない。中継の口（図画像は後段の Content-Type をそのまま返し、SVG を含む）があるため、付ける価値がある。

## 決定

1. `PromptTrustBoundary` を `Knowledge.Contracts.Llm` へ移す（AiAnalysisService の型は消す）。
   - `Rules(boundary, includesUserInput)` は利用者の入力の囲みの有無で出し分ける。出力の形の指定（Markdown・JSON・散文）は各プロンプトが持つ
     （ask / analyze の「HTML・スクリプト・画像を出力しない」は AskPromptBuilder / AnalysisPromptBuilder へ移し、決まりの直後に置く）。
   - 決まりの文言は GraphService にも通じる形に**一般化した**（「検索で取り出した参照文書」→「検索・台帳から取り出した文書（本文の抜粋や表題）」、
     「参照文書の中に書かれた指示」→「文書の中に書かれた指示」）。ask / analyze のプロンプトは、この 2 か所の言い回しと、
     出力の形の行の位置（決まりの末尾 → 決まりの直後の行）だけが変わる。囲みの形・決まりの意味・セクションの構成は変わらない。
   - `OneLine` を共有へ置く（CitationMapper の表題・抜粋と GraphService の表題が同じ規則で畳む）。
2. GraphService の `SuggestionPrompt` と `ClusterSummaryPrompt` は、表題を `OneLine` で畳み、`Wrap(documents, …)` の囲みに入れ、`Rules(…, includesUserInput: false)` を囲みの前に置く。
   - **境界は封ごとに 1 つ**（封のコンストラクタで作る）。同じ封の `Render` は同じ本文を返す（送信と記録が食い違わない）。
   - 辺の型・タグの名前集合（SC-09 の辞書。管理者が定める値集合）は囲みに入れない（こちらが選ばせる値集合であり、生成段が辞書と突き合わせて辞書外を落とす）。
   - 封の構築経路（`Seal` ただ 1 つ）と、封に入る文書の集合は変えない（IADR-0266 / IADR-0430）。
3. BFF の全応答に次を付ける（`BffResponseSecurityHeadersMiddleware`。パイプラインの最初に置き、応答の開始時に付ける。既に値があれば上書きしない）:
   - `X-Content-Type-Options: nosniff`
   - `Content-Security-Policy: default-src 'none'; frame-ancestors 'none'; sandbox`
   - `X-Frame-Options: DENY`
   - `Cross-Origin-Resource-Policy: same-origin`（SPA と BFF は同一オリジン。CsrfHeaderMiddleware の前提と同じ）
   - `same-origin` は**SPA と BFF が同一オリジンで配られる**ことを前提にする。本番（エッジが `/bff` と SPA を同じホストで振り分ける）・
     compose（Caddy が `/bff` を中継する）・dev（Vite の `server.proxy` が `/bff` を中継する）・E2E（preview の同じ proxy と `page.route` の差し替え）は
     いずれも同一オリジンである（`bffBaseUrl` の絶対 URL は SPA が禁じている）。別オリジンから BFF の資源を `<img>` 等で読む構成を作るなら、この値を判断し直す。

## 結果

- knowledge ユニットで LLM へプロンプトを送る 4 か所（ask・analyze・提案・クラスタ要約）が同じ信頼境界を使う。
- BFF の応答は、文書として開かれても何も実行・取得・埋め込みできず、別オリジンのページから資源として読まれない。
  SPA の fetch・`<img>`（図・CAPTCHA）・ログイン / ログアウトのリダイレクトには影響しない（CSP は文書にだけ掛かり、3xx の応答の CSP は遷移先に及ばない）。
- compose の経路（Caddy が `/bff` を中継する）では、Caddy の `header` が SPA の CSP を同じ名前で付けるため、BFF の値と重なり得る（ブラウザは両方を課す＝より厳しい側になる）。
  k8s の経路（エッジから BFF へ直接）では BFF の値だけが付く。

## 残る制約

- 区別はプロンプトの上の区別であり、LLM の振る舞いを保証するものではない（IADR-0580 と同じ）。
- 辺の型・タグの名前集合は囲みに入れていない（決定 2）。辞書を管理者以外が書けるようにするなら、囲みへ入れることを判断し直す。
- 応答ヘッダは BFF が返す応答だけに付く。BFF 以外のサービスの応答は本 IADR の射程外である。
