---
title: 作業仕様書 — 計画 ADR-0140（Voyage・Claude の所在を受け入れたリスク、Claude の ZDR 未締結）へ実装側の文書・コメント・手順を追随させる（#1960）
type: spec
status: done
related_ids: [NFR-17, FR-02, FR-03, FR-11, ADR-0140, ADR-0016, ADR-0092, ADR-0127, IADR-0553, IADR-0007, IADR-0022, IADR-0025, IADR-0498, IADR-0504, IADR-0085]
author: claude
created: 2026-10-16
updated: 2026-10-16
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0140_voyage-tier-b-residency-accepted-risk.md
  - planning:projects/microservices-platform/06_technical/08_data-egress-policy.md
issue: "#1960"
---

# 作業仕様書 — 計画 ADR-0140 への追随（#1960）

> 本仕様書は編集の着手前に作成した（着手 2026-10-16）。基点は MSP `origin/develop` `3b49d911`。
> 計画は project-planning の隣接クローン `origin/main` `fc61ca3`（読み取り専用）を読んだ。
> **コードの挙動は変えない。** 変えるのは文書・コメント・配備物（compose）の口 1 つ（既定値は従前と同じ）。

## 起点

- 起点 issue: **#1960**（本件で起票。MSP に ADR-0140 を起点とする既存 issue は無かった —— 直近 300 件の issue / PR の件名を
  `voyage|ZDR|ADR-0140|ティアB|795|所在|residency` で引き、当たったのは閉じた #1740・#1921・#1764・#1766・#1754・#1819 だけ）。
- 計画: **ADR-0140**（planning#795 の裁定・planning#801 でマージ。2026-10-16 Accepted）。決定 1〜6 と §結果のフォローアップ 1〜5。
- 要求: **NFR-17**（データ越境統制）。

## ADR-0140 が実装側へ求めること（読み取り）

| ADR-0140 | 実装側の担当 | 本件での扱い |
| --- | --- | --- |
| 決定 1（Voyage の所在は受け入れたリスク。ティアB の定義への例外） | 文書に「受け入れたリスク」と「定義への例外」を残す | 運用・セキュリティ仕様書へ追記 |
| 決定 2（根拠は公開の規約と opt-out だけ。契約書は無い） | 「保護契約」「契約で」と書く箇所が契約を前提に読める | Voyage を名指しする箇所を追随。ティアB の**名称**（08 の呼び方「保護契約済み外部API」）は改めない（決定 1「08 の定義は改めない」） |
| 決定 3（認定は opt-out したアカウントの鍵に限る。オーナー確認待ちの安全側の推論。未確認の鍵では「未認定の環境へデプロイする場合」の手順を当てる） | 手順が実際に実行できること | 🔴 **手順をそのまま実行すると LLM ゲートウェイが起動しない**（下の実測 1）。手順を直し、compose の口を足す |
| 決定 4（「監査可能」は未照合） | 文書に未照合と残す | 運用・セキュリティ仕様書へ追記 |
| 決定 5（Claude の推論の所在は受け入れたリスク。ZDR は未締結と記録し、標準の保持〔最長 30 日・米国。違反検知分は最長 2 年〕を期限付きで受け入れた。受容者は利用者、期限は本番運用への移行判断の前） | 「Claude（ティア B・ZDR）」のように組織の ZDR が働いていると読める記述を直す。実装の「ZDR」はモデル単位の除外であることを明記 | 追随（運用仕様書に Claude の認定状況の節を新設） |
| 決定 6（統制の 3 点セット） | 判定根拠のエンドポイント定義への記録（`IADR-0007` のフォローアップ）は**未実装**。暫定手段は運用仕様書の記録 | **本件では実装しない**（フォローアップ 1。設定の型の追加でありコードの変更を伴う。ADR-0140 はフォローアップとして挙げ、本件の時点の実装を求めていない）。運用仕様書の記録を暫定手段として明記する |
| §結果 フォローアップ 2〜5 | 計画・組織・オーナーの担当 | 運用仕様書の未決事項に Claude の ZDR の期限を足す（追跡は計画 INDEX §未決事項） |

## 実測（2026-10-16・`3b49d911`）

1. 🔴 **`Embedding__Routing__Endpoints__0__Enabled=false` だけでは LLM ゲートウェイが起動しない。**
   `EmbeddingRoutingOptionsValidator`（`ValidateOnStart`）は、public の許容ティア（A・B）に有効なエンドポイントが 1 つも無いと
   起動を失敗させる（試験 `Validate_AllDefaultPathEndpointsDisabled_Fails` が固定）。既定ではティア A（`selfhosted-ruri`・`deterministic-local`）は
   どちらも `Enabled: false` なので、運用仕様書の「未認定の環境へデプロイする場合」の手順をそのまま当てると、
   埋め込みだけでなく **Claude の呼び出しも含めてゲートウェイ全体が止まる**。手順が成り立つのは、セルフホスト（ティア A）を同時に有効にしたときだけである
   （試験 `Validate_VoyageDisabledButSelfHostedEnabled_Succeeds`）。
2. 🔴 **compose には Voyage の経路を止める口が無い。** 運用仕様書は「compose は `.env`」と書くが、`llm-gateway` の environment に
   `Embedding__Routing__Endpoints__0__Enabled` は無く、`.env` に何を書いても届かない（セルフホストの `SELFHOSTED_EMBEDDING_ENABLED` だけがある）。
   Helm は `services.llmgateway.extraEnvAppend` で足せる（テンプレートが既に持つ汎用の口）。
3. **鍵が無ければ Voyage へは何も出ない。** `VoyageEmbeddingProvider` は `Embedding:Voyage:ApiKey` が空なら HTTP を行わずに
   `EmbeddingProviderNotConfiguredException` を投げ、`public` / `internal` の文書は全文だけの点として入る（運用仕様書の既存の記述）。
   したがって「opt-out を確かめていないアカウントの鍵」に対する最も簡単な守りは、**その鍵を入れないこと**である。
4. エンドポイント定義（`LlmGateway/appsettings.json`）の `claude-managed`・`voyage-managed` は `Tier: "B"` だけを持ち、判定の根拠を持たない（ADR-0140 実測 7 と一致）。
   `inference_geo` は 0 件（`git grep inference_geo`）。`NonZdrModels` は空。
5. 計画 ADR の宣言レンジは `ADR-0001..0139`。計画リポジトリ `origin/main` `fc61ca3` で `node tools/doc-checks/gen-plan-ranges.js --check` は
   MSP の ADR を「宣言 [1, 141] / 実物 [1, 141]・欠番なし」と出す（FR 22・UC 11・SC 22・NFR 参考 29 は不動）。ADR-0140 を件名・trace ブロックで引くため、レンジを `0141` まで開ける。

## 決めたこと

1. **「ZDR」の語の読み分け**（IADR-0553 決定 1）: 実装の「ZDR 必須」「ZDR 対応モデル」「ZDR の外へは出ない」は**モデル単位の除外**（`NonZdrModels`）
   と**ティア C の除外**を指し、**組織の ZDR 契約を見ない**。Claude の組織の ZDR は**未締結**であり、Claude へ送った本文は標準の保持の下にある。
   live な文書・配備物のコメント・コードのコメントのうち、組織の ZDR が働いていると読める箇所（「ティア B・ZDR」「ティア B（ZDR）へ出る」「守りは ZDR」
   「ティア C（ZDR の契約が無い）」）を直す。モデル単位の意味で正しく書かれた箇所（`NonZdrModels` の説明・試験の名前）は残す。
2. **未認定の環境の手順を直す**（IADR-0553 決定 2）: 手順を 2 つに分ける。
   (a) **鍵を入れない**（最も簡単。外へは何も出ない。`public` / `internal` は全文だけの点）。
   (b) **セルフホスト（ティア A）を有効にしたうえで Voyage の経路を止める**（`Endpoints__0__Enabled=false`。ティア A を有効にしないとゲートウェイが起動しない）。
   決定的ローカル（検証スタック専用）を代わりに有効にして起動を通すことはしない（意味検索の品質を持たない）。
3. **compose に口を足す**（IADR-0553 決定 3）: `llm-gateway` に `Embedding__Routing__Endpoints__0__Enabled: ${VOYAGE_EMBEDDING_ENABLED:-true}`。
   既定は `true`（従前と同じ字面の挙動）。Helm は既存の `extraEnvAppend` を手順に書く（新しい values は足さない）。
4. **運用仕様書に Claude の送信先ティアの認定状況の節を新設する**（ADR-0140 決定 5 の表・受容者・期限・`inference_geo` 未指定・workspace 設定値は未確認・「監査可能」未照合）。
   Voyage の「ゼロ保持認定状況の記録」へ 2026-10-16 の追記（計画が例外として記録した・「監査可能」未照合・判定根拠はエンドポイント定義に無く本記録が暫定手段・射程は計画も安全側の推論として認めた）。
5. **docs/ の表示テキストへ計画 ID・IADR・issue 番号を書かない**（trace ブロックへ）。凍結記録（`.ai-context/specs`・`superpowers`）は書き換えない。
   既存の IADR（0022・0025・0498・0504・0007）は**書き換えない**（下の母集合の除外理由）。
6. **計画 ADR のレンジを `0001..0141` へ開ける**（`traceability.repo.md` と別紙）。

## 母集合（規則 9・10。2026-10-16 `3b49d911` 時点）

### 規則 9（誤りの側の文字列で走査してから追随先を挙げる）

走査（`src/ai-stock-trading`・`.ai-context/specs`・`.ai-context/superpowers`・`CHANGELOG.md`・生成物 `docs/api/openapi.yaml` を除く）:

1. `git grep -n "ZDR"`（ファイル別件数で全体を見てから、`docs`・`deploy`・`src` の非試験を行で読んだ）
2. `git grep -n "保護契約"`
3. `git grep -n -iE "レジデンシー|residency"`
4. `git grep -n -E "inference_geo|監査可能"`
5. `git grep -n -E "ゼロ保持|zero.?retention|未認定|認定"`（`docs` `deploy` `src/platform` `src/knowledge` `scripts/*.js`）
6. `git grep -n -E "Endpoints__0__Enabled|VOYAGE_.*ENABLED"`（手順の実行可能性）

| 当たり | 追随 |
| --- | --- |
| `docs/operations/operations.md` §埋め込み の表「ティアB（Voyage・保護契約）」 | 追随（規約と opt-out・所在は受け入れたリスク） |
| 同「Voyage AI（ティアB）のゼロ保持設定」の「契約で確認」「`Endpoints__0__Enabled=false` で止められる」 | 追随（実測 1・2。手順の正しい形へ） |
| 同「ゼロ保持認定状況の記録」 | 追随（2026-10-16 の追記） |
| 同 ⚠️「未認定の環境へデプロイする場合」（compose は `.env`、k8s は values/`--set`） | **追随（主対象。実測 1・2）** |
| 同 §検索の再順位付け「ティア B（Claude・ZDR）へ出る」「守りは ZDR」 | 追随（組織の ZDR は未締結） |
| 同 §検索の再順位付け「ZDR の外へは出ないが」 | 追随（モデル単位の意味であると明記） |
| 同 §未決事項 | 追随（Claude の ZDR の期限付きの受容を足す） |
| 同 §いつ読むか | 追随（新設の節への行） |
| `docs/security/security.md` 脅威表「再順位付け」の行（区分によらず ZDR 必須） | 追随（モデル単位・組織は未締結） |
| 同「Voyage AI のデータ保持・学習利用」の行（契約でゼロ保持） | 追随（監査可能は未照合・定義への例外） |
| 同「検索クエリ文」の行（Voyage 側のゼロ保持契約が適用される） | 追随（契約ではなく規約と opt-out） |
| 同 脅威表に Claude の保持・所在の行が無い | 追随（行を足す） |
| `docs/functional/FR-11_llm-egress-routing.md` 越境マトリクス（ティアB 保護契約済み外部API） | 追随（表の下に注。名称は 08 の呼び方なので改めない） |
| 同 「ZDR によるモデル除外」「用途による ZDR の要件」 | 追随（モデル単位であり組織の契約を見ない旨の注） |
| 同 :399 Copilot のティア確定待ち（ZDR/学習不使用/レジデンシー） | 追随しない（Copilot の判定待ちの記述で今も正しい） |
| `docs/functional/FR-03_hybrid-search.md` 再順位付けの表「ZDR 必須」・受け入れ基準 | 追随（表の行にモデル単位の注。受け入れ基準のチェック項目は実装済みの挙動の記録なので文言を変えない） |
| `docs/tests/FR-11`・`docs/tests/FR-03`（T-11・T-13・T-29 ほか） | 追随しない（モデル単位の除外の試験の記述として正しい） |
| `docs/how-to/plan-id-range-history-annex.md:104`（0127 の題「Claude（ティア B・ZDR）」） | 追随しない（計画 ADR の題の引用であり、当時の記録） |
| `deploy/helm/microservices-platform/values.yaml:1209,1213`（ティア B・ZDR／ティア B（ZDR）へ出る） | 追随 |
| `deploy/docker-compose.yml:757-758`（契約時にゼロ保持） | 追随（規約と opt-out。あわせて口を足す） |
| `EgressMatrix.cs:4`（B=保護契約済み外部API（ZDR/学習不使用/レジデンシー）） | 追随（定義は 08 のとおり残し、例外の注を足す） |
| `LlmRouter.cs:14-15`（ティア C（ZDR の契約が無い）） | 追随（ティア B の Claude も組織の ZDR は未締結） |
| `LlmRoutingOptions.cs:85-87`（既定は空＝全モデル ZDR 対応扱い） | 追随（モデル単位の扱いであり組織の契約ではない） |
| `LlmRoutingOptions.cs:59-66`（ZeroDataRetentionPurposes） | 追随しない（用途の規則の説明で正しい。組織の注は 85-87 と LlmRouter に置く） |
| `VoyageEmbeddingProvider.cs:9-11`（ティアB=保護契約済み外部API・契約時にゼロ保持） | 追随 |
| `Program.cs:93`（ティアB=保護契約済み外部API（Claude）） | 追随 |
| `EmbeddingEgress.cs:14`・`EmbeddingRoutingOptions.cs:36`・`EmbeddingRoutingOptionsValidator.cs:26`・`McpServer/Domain/EgressPolicy.cs:11,14` | 追随しない（ティアの名称として 08 の呼び方を使っている。定義の注は `EgressMatrix.cs` の 1 か所に置く） |
| `ClaudeProvider.cs:16`（ZDR 有効化の優先） | 追随しない（fable 系を使わない理由＝モデル単位の ZDR の記述） |
| `CopilotProvider.cs:13`・`CompletionDto.cs:35`・`bff.schemas.ts`（生成物）・`IRerankCompletionClient.cs:10`・`SearchRerankOptions.cs:14` | 追随しない（ZDR の**要件**による拒否・判定の記述で正しい） |
| 試験（`LlmRouterTests.cs` ほか）のコメント「保護契約済み外部API」 | 追随しない（ティアの名称） |
| `.ai-context/adr/IADR-0022`・`IADR-0025`・`IADR-0498`・`IADR-0504`・`IADR-0007`・`IADR-0085` | **追随しない**: いずれも ADR-0140 が「前提として扱う」とした記録で、決定は覆らない。ZDR の読み分けと手順の是正は IADR-0553 が持つ（凍結記録の本文へ後付けしない） |
| `docs/operations/voyage-embedding-key-runbook.md:37-38`（確かめるまでは「未認定の環境へデプロイする場合」に従う） | 追随しない（参照先の節を直すので、参照は正しいまま） |
| `scripts/fixtures/review-floor/pr1921-*.txt` | 対象外（過去のレビューの固定値） |

### 規則 10（この変更で新たに誤りになる自分の記述）

- 節名「埋め込みプロバイダの設定・ゼロ保持・再索引」は変えない → 節名で引く参照は壊れない。
- 新設の節名（Claude の認定状況）を §いつ読むか から引く。節名を後で変えるときは表の行も直す。
- compose の口を足すので、運用仕様書の「compose は `.env`」は**正しくなる**。Helm は `extraEnvAppend`（上書きの `extraEnv` ではない。本番既定の env が消える）。
- レンジを 0141 まで開けたので、別紙の trace ブロックの `adrs` と `specs` に今回の分を足す。

## 受け入れ基準と検証

| 受け入れ基準 | 検証 |
| --- | --- |
| 運用仕様書から ADR-0140 の受け入れたリスク 4 件（Voyage の所在・opt-out 前の送信・Claude の推論の所在・Claude の ZDR 未締結〔期限付き〕）と受容者・期限が辿れる | 目視（新設の節・追記）。`node scripts/check-trace-blocks.js` |
| 未認定の環境の手順が、そのまま実行して起動する形になっている | 実測 1 の試験 2 件の存在で裏付け（`Validate_VoyageDisabledButSelfHostedEnabled_Succeeds` / `Validate_AllDefaultPathEndpointsDisabled_Fails`）。compose は `docker compose config` で、既定（`Endpoints__0__Enabled=true`）と `VOYAGE_EMBEDDING_ENABLED=false`（`false` が届く）の 2 通りを描いて確かめた |
| 計画 ADR のレンジが実物と一致する | `node scripts/check-trace-blocks.js`・`node scripts/check-commit-messages.js`（ADR-0140 を件名に持つコミット） |
| 文書系検査・scripts の試験が通る | `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`・`node scripts/gen-knowledge-graph.js --check`・`check-reading-budget.js` ほか |

## 環流（planning）

不要。ADR-0140 は手順の存在を前提に置く（決定 3・決定 6）が、手順の中身（ティア A を同時に有効にしないと起動しない）は実装の事情であり、
計画の決定を変えない。手順の誤りは実装側の記述の誤りとして本件で直した。
