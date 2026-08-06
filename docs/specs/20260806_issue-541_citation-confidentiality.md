---
title: CitationDto に機密区分を追加する（出典の越境判断の手掛かり）
type: spec
status: done
related_ids: [FR-04, FR-05, FR-11, UC-01, UC-02, SC-01, ADR-0004, ADR-0010, IADR-0131, IADR-0132]
author: Claude
created: 2026-08-06
updated: 2026-08-06
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
  - "../../planning/projects/microservices-platform/06_technical/08_data-egress-policy.md"
  - "../../planning/docs/glossary.md"
related_specs:
  - ../functional/FR-04_ai-answer-citations.md
  - ../tests/FR-04_ai-answer-citations.md
  - ../screens/SC-01_search-chat.md
  - ../api/openapi.yaml
  - ../adr/IADR-0131_openapi-as-bff-contract-source.md
  - ../adr/IADR-0132_openapi-required-from-csharp-nullability.md
---

# 仕様書: 出典（`CitationDto`）への機密区分の追加（issue #541）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-04**（「**出典には機密区分を含める**（追加・2026-08-05）」）／FR-05（deny-by-default）／FR-11（越境制御）
- ユースケース（UC）: UC-01・UC-02
- 画面（SC）: **SC-01**（§出典への機密区分の表示。確定・2026-08-05）
- 関連 ADR（計画）: ADR-0004（ABAC）・ADR-0010（LLM ゲートウェイ）／技術検討 `06_technical/08_data-egress-policy.md`・`07_abac-attribute-model.md`
- 関連 ADR（実装）: [[IADR-0131]] 決定 5（状態・種別を `enum` にしない）／[[IADR-0132]] 決定 1・2 系（`required` は C# の非 null 性から・`required` と `default` を同居させない）
- 裁定: **質問票 第12回 Q7・Q10・Q30**（2026-08-05）／環流元 planning#197 ／計画反映 planning#200 ／実装 issue #541

## 目的・背景

計画（SC-01 §出典への機密区分の表示）は次のとおり確定している。

> 出典が表示されるのはその文書の閲覧権限を持つ利用者だけであり（ABAC は検索段で適用済み）、機微度の
> 露出リスクは小さい。一方で `08_data-egress-policy` は機密区分を軸に越境を統制しており、**利用者が
> AI 回答を社外資料へ引用してよいか判断する最も直接的な手掛かり**になる。

実装側は「契約に項目が無い」ことを理由に SC-01 のモック要素 #6-b（出典行の機密区分チップ）を
**実装しない**と宣言していた（`docs/screens/SC-01_search-chat.md` §実装しない要素の理由 (c)）。
本 issue はその**契約側の欠落**を埋める。

## 対象範囲

- 対象:
  - `Knowledge.Contracts` の `CitationDto.Confidentiality` と値集合 `ConfidentialityLevels`
  - `CitationMapper` の配線（`SearchResultDto.Attributes` の `confidentiality` から供給）
  - `RagOrchestrator.HighestConfidentiality` を新しい値集合へ載せ替える（4 値の梯子を 2 か所に持たない）
  - `docs/api/openapi.yaml` の `CitationDto`（契約の単一情報源。[[IADR-0131]] 決定 1）と orval 生成物の再生成
  - 単体テスト（`CitationMapperTests`）と、契約 baseline（`scripts/contract-schema-baseline.json`）の更新
  - `@platform/ui` の Storybook 例が持つ**誤った表示名**の是正（後述 §表示名の母集合）
- 対象外:
  - **SPA の表示**（SC-01 出典行の機密区分チップ）。契約が載ったことで着手可能になるが、
    表示名の i18n カタログ（Lingui ja/en）と「色だけで意味を持たせない」の作り込みを伴うため別 issue とする。
    #531（検索モード）が「契約と配線が射程・画面は別」と切ったのと同じ切り方である。
  - `DataSource.ConfidentialityKey` / `DocumentAttributes.ConfidentialityKey`（別サービスの
    ドメイン定数）を新しい契約定数へ寄せること。値は同一で、寄せ替えはコンパイルを跨ぐ整理であり
    本 issue の起点（出典に区分を載せる）と別種である。§申し送り に残す。
  - SC-03 / SC-05 の「表示名は計画に無いので生値を出す」という**古くなった根拠**の追随（§表示名の母集合）。

## 着手時の実測

**リポジトリ全体を数える値は書かない**（他 PR のマージで動く）。ここに書くのは本作業の母集合だけである。

| 測ったもの | コマンド | 実測 |
| --- | --- | --- |
| `CitationDto` を組み立てている箇所 | `git grep -n 'new CitationDto' -- src ':!src/ai-stock-trading'` | **4 箇所**（本番 1・テストスタブ 3） |
| 本番の組み立て | 同上 | `CitationMapper.ToCitations`（**名前付き引数**） |
| テストスタブの組み立て | 同上 | `AiAnalysisService.Api.Tests/TestWebApplicationFactory.cs` × 2、`Platform.Bff.Tests/BffTestFactory.cs` × 1（いずれも**位置引数 7 個**） |
| 機密区分の属性キーの実名 | `git grep -n 'confidentiality' -- src` | **`confidentiality`**（`DataSource.ConfidentialityKey` / `DocumentAttributes.ConfidentialityKey` の値。推測ではない） |
| 出典元となる値の所在 | `SearchResultDto.Attributes`（`Dictionary<string,string>`） | `RagOrchestrator.HighestConfidentiality` が**既に同じキーを読んでいる**（越境判定用） |
| 表示名の正 | `planning/docs/glossary.md` §機密区分 | `public`＝公開 / `internal`＝社内限 / `confidential`＝秘 / `restricted`＝**取扱制限** |

## 設計

| # | 決定 | 内容 |
| --- | --- | --- |
| D1 | **位置引数（既定値つき）** で追加する | `CitationDto(..., string Snippet, string Confidentiality = ConfidentialityLevels.SafeDefault)` |
| D2 | 値集合は **`enum` にしない** | `ConfidentialityLevels` の `const` 群 ＋ `Normalize`（[[IADR-0131]] 決定 5・#531 の作法） |
| D3 | 安全側の既定は **`restricted`** | 欠落・空・未知値はすべて `restricted` へ縮退 |
| D4 | `HighestConfidentiality` を新値集合へ載せ替える | 4 値の梯子を 2 か所に持たない |
| D5 | OpenAPI は `required` に入れ、`default` は書かない | [[IADR-0132]] 決定 1・決定 2 系 |
| D6 | **表示名を契約に載せない** | 正は planning `docs/glossary.md`。**`restricted` は「極秘」ではない** |

### D1: なぜ `init` プロパティではなく位置引数か（実測して決めた）

同じファイルの `AiAnswerDto.AnswerId` は `init` 既定値プロパティで非破壊に追加した先例だが、
**本件はその型と違う**。

- `AnswerId` は**呼び出し側が供給しない自動採番値**（`Guid.NewGuid()`）である。位置引数にすると
  すべての呼び出し側が「採番」を意識させられる。
- `Confidentiality` は**発行側（AiAnalysisService）が文書属性から供給する値**であり、出典 1 件の
  同一性を構成する。`Deconstruct` にも載るべきで、位置引数が自然である。

**「壊さないため」に `init` を選ぶ必要は無い**ことを実測で確かめた——上表のとおり組み立ては 4 箇所で、
うち 3 箇所は位置引数 7 個、1 箇所は名前付き引数である。**末尾に既定値つき位置引数を足すと 4 箇所すべてが
無改修でコンパイルできる**（追加した引数を渡していないテストスタブは安全側の既定値を受け取る）。

### D1 系: なぜ既定値を付けるか（2 つの独立した理由）

1. **契約上の破壊性**: `scripts/check-contract-schema.js` は「**既定値の無いメンバーの追加**」を
   破壊的に分類し、「既定値を付ければ非破壊」と定める（[[IADR-0122]] 決定 2）。承認 allowlist を
   使わずに済ませられるなら、そうする。
2. **ローリング更新中の実体**: `CitationDto` は AiAnalysisService → BFF → SPA を JSON で渡る。
   本項目を持たない旧サービスの応答が新 BFF に届き得る。`System.Text.Json` は
   コンストラクタ引数の既定値を尊重するため、**欠けたら安全側へ縮退**する。

### D3: なぜ安全側が `restricted` なのか（`DataSource` の `internal` と逆である理由）

`DataSource.DefaultConfidentiality` は `internal` である（[[IADR-0019]]）。**逆だが矛盾していない**
——両者は別の問いに答えている。

| | 問い | 過剰な側の害 | 既定 |
| --- | --- | --- | --- |
| `DataSource` | 取り込み時に**何を付けるか** | 過剰制限すると社内文書が誰にも見えなくなる（fail-closed 除外の再発） | `internal` |
| 本件 | 表示時に**不明な値をどう見せるか** | 過剰公開すると「社外へ引用してよい」の判断を誤らせる | `restricted` |

決め手は**同一サービス内の既存規則**である。`RagOrchestrator.HighestConfidentiality` は
「属性の欠落・空文字は安全側（restricted）／未知の値も安全側」と定め、その値を LLM ゲートウェイの
越境判定へ渡している。**出典に表示する区分と越境判定に使う区分が食い違ってはならない**
——画面に「公開」と出ているのにゲートウェイは `restricted` として扱う、という状態を作らない。

### D4: 4 値の梯子を 1 か所へ寄せる

`HighestConfidentiality` は `public < internal < confidential < restricted` の順序表と
安全側の既定を**メソッド内のローカル変数**として持っていた。出典側でも同じ値集合・同じ縮退規則が
要るため、`Knowledge.Contracts` の `ConfidentialityLevels` へ寄せ、両者がそこを読むようにする。

**挙動差は大小文字だけである。** 従来は既知値を「元の綴りのまま」返していた（比較は
`OrdinalIgnoreCase`）。以後は正準の小文字を返す。実データに差は出ない——
`DocumentAttributes.AllowedConfidentiality` は `StringComparer.Ordinal` で小文字 4 値しか通さない。
下流にも影響しない——`SensitivityClasses.Parse` は `ToLowerInvariant()` してから照合する。

### D5: OpenAPI

```yaml
    CitationDto:
      required: [number, documentId, documentTitle, chunkId, score, snippet, confidentiality]
      properties:
        confidentiality: { type: string, description: "…public / internal / confidential / restricted…" }
```

- **`required` に入れる**: C# 側が `string`（非 null）だから（[[IADR-0132]] 決定 1）。
- **`default` は書かない**: `required` と `default` の同居は契約の自己矛盾（[[IADR-0132]] 決定 2 系）。
  C# 側の既定値は「発行側が供給しなかったときの縮退」であって「欠けてよい」ではない。
- **`enum` にしない**: [[IADR-0131]] 決定 5。値集合は `description` に書く。

### D6: 表示名（`restricted` は「極秘」ではない）

| 値 | 表示名 |
| --- | --- |
| `public` | 公開（**閲覧制限を設けない**の意。社外を含む一般公開ではない） |
| `internal` | 社内限 |
| `confidential` | 秘 |
| `restricted` | **取扱制限** |

**`restricted` を「極秘」としない**のは、個人資料（`private-note`）が既定でこの区分を持つためである
（裁定 Q30）。「極秘」にすると個人の作業メモがすべて極秘表示になり、本当に極秘の組織文書を
見分けられなくなる。正は planning `docs/glossary.md` であり、**本リポジトリは表示名を再定義しない**。

### 表示名の母集合（是正した 1 件と、しなかった 4 件）

`git grep -rn '極秘\|取扱制限\|社内限' -- src docs ':!src/ai-stock-trading'` で数え切った。

| # | 箇所 | 種別 | 本 PR |
| --- | --- | --- | --- |
| 1 | `src/packages/ui/src/stories/Primitives.stories.tsx` | **正に反する写像**（`internal`＝「社内」・`restricted`＝「極秘」） | **是正した**（社内限 / 取扱制限） |
| 2 | `docs/screens/SC-03_document-detail.md` §属性の表示 | 「表示名が計画に無いので生値を出す」＝**古くなった根拠** | しない（§申し送り） |
| 3 | `docs/screens/SC-05_document-management.md` 表 #7 | 同上（「planning#197 の裁定待ち」） | しない（§申し送り） |
| 4 | `src/knowledge/frontend/src/features/abac/confidentiality.ts` の見出しコメント | 同上 | しない（§申し送り） |
| 5 | `src/knowledge/frontend/src/features/sc03-document/attributes.ts` の見出しコメント | 同上 | しない（§申し送り） |

**切り方**: #1 は**正（用語集）に反する記述**であり、放置すると誤った用語を教え続ける。#2〜#5 は
「まだ決まっていない」という**古くなった根拠**で、直すには「SPA が表示名を出すのか／どこで i18n するか」
という UI の判断が要る。両者を混ぜると本 PR が画面の作業になる。**素通りさせず、申し送りとして開示する。**

> なお `LlmGateway.Api.Tests` の `"極秘の本文"` は**送信本文のテスト文字列**であり、区分の表示名の
> 写像ではない。母集合に入れない。

## 受け入れ基準

- [x] `CitationDto` が機密区分を持ち、**出典 1 件ごとに**その文書の区分が載る
- [x] 値は**推測せず** `SearchResultDto.Attributes` の `confidentiality`（実在の属性キー）から供給される
- [x] 属性の**欠落・空文字・未知値**は安全側（`restricted`）へ縮退する
- [x] 出典に載る区分と、越境判定へ渡す区分が**同じ規則**から導かれる（食い違わない）
- [x] 値集合は `enum` ではない（[[IADR-0131]] 決定 5）
- [x] 既存の `CitationDto` の組み立て 4 箇所が**無改修でコンパイルできる**（非破壊）
- [x] OpenAPI の `CitationDto` に `confidentiality` が載り、`required` に入る（`default` は書かない）
- [x] 契約 baseline の差分が**非破壊のみ**である（破壊的変更 0 件）
- [x] 表示名を実装側で定義していない（正は planning `docs/glossary.md`。`restricted` は「取扱制限」）

## テスト方針

`CitationMapperTests` へ 5 ケースを足す（[[IADR-0130]] のテスト仕様カバレッジと突合するため
テスト仕様書 `docs/tests/FR-04_ai-answer-citations.md` に T-17〜T-21 として起こす）。

- 属性にある区分がそのまま出典へ載る（4 値すべて。`[Theory]`）
- 属性が**無い**／**空文字**／**未知値**は `restricted` へ縮退（`[Theory]`）
- 大小文字違いは正準の小文字へ正規化される
- 出典ごとに**別々の**区分が載る（1 件目 public・2 件目 confidential が混ざらない）
- 値集合そのものの回帰（4 値であること・安全側の既定が `restricted` であること）

## 計画書との差異

- 差異: なし。計画（FR-04 / SC-01 §出典への機密区分の表示・裁定 Q10）のとおり `CitationDto` へ含めた。
  表示（SC-01 のチップ）は本 issue の対象外として明示的に繰り延べた（§対象範囲）。

## 検証

**この環境では .NET SDK を導入できない。** `dotnet` は PATH にもファイルシステムにも存在せず
（`find / -maxdepth 4 -name dotnet -type f` = 0 件・`~/.nuget` 無し）、`dotnet-install.sh` の取得は
プロキシに拒否される（`curl: (56) CONNECT tunnel failed, response 403`。
`$HTTPS_PROXY/__agentproxy/status` の `recentRelayFailures` に `builds.dotnet.microsoft.com:443` の
403 が記録されている）。**したがって `dotnet build` / `dotnet test` /
`dotnet format --verify-no-changes` は未検証である。** 実行できたものだけを下に記す。

| コマンド | 結果 |
| --- | --- |
| `node scripts/check-contract-schema.js`（変更前） | OK（2 プロジェクト / 20 ファイル / **57 型**） |
| `node scripts/check-contract-schema.js`（変更後・`--update` 前） | 差分 **2 件**（破壊的 **0** / 非破壊 2）。`CitationDto.Confidentiality` のメンバー追加・`ConfidentialityLevels` の型追加 |
| `node scripts/check-contract-schema.js`（`--update` 後） | OK（**58 型**） |
| `node scripts/check-doc-links.js` | OK（未 populate の submodule 配下 2 件は対象外） |
| `node scripts/check-test-spec-coverage.js`（`--update` 前） | 違反 1 件（床の上げ忘れ: `CitationMapperTests`） |
| `node scripts/check-test-spec-coverage.js`（`--update` 後） | OK |
| `node scripts/check-test-traceability.js` | OK |
| `node scripts/check-bff-downstreams.js` | OK |
| `pnpm run codegen`（`src/`） | 生成物差分は `CitationDto.confidentiality`（**必須**）の追加と faker 2 行のみ |
| `pnpm run lint`（`src/`） | **0 errors** / 8 warnings（既存の `react-refresh` 警告のみ。本作業由来 0） |
| `pnpm run typecheck`（`src/`） | `packages/ui` OK・`knowledge/frontend` OK。**`platform/frontend` は 1 件失敗** |
| `pnpm test`（`src/`） | 48 ファイル / 448 件が合格、**3 ファイル / 6 件が失敗** |

**typecheck と test の失敗は本作業と無関係の環境要因である。** いずれも
`Failed to resolve import "@ai-stock-trading/features"`（`TS2307` / vite 解決失敗）で、
**`src/ai-stock-trading` submodule が未 populate**（`git submodule status` が `-655e2ed…`）
であることによる。本作業では触れてはならない領域である。
**同一であることを実測で確かめた**——`git stash push -u` で本作業の変更をすべて退避したうえで
同じ 3 ファイルを実行し、**まったく同じ 6 件が失敗**した（`Test Files 3 failed (3) / Tests 6 failed (6)`）。

### 変異試験（壊すと落ちることの実測）

**バックエンドのテスト（T-17〜T-21）は走らせられないため、C# の写像を壊す変異は実測できていない。**
実測できたのは契約（C# ソース → baseline）・OpenAPI・生成物・テスト仕様カバレッジにかかる変異である。
**素通りしたものも隠さず記す。**

| # | 変異 | 期待 | 実測 |
| --- | --- | --- | --- |
| M1 | OpenAPI の `CitationDto` から `confidentiality` プロパティごと落とす | 生成物の再生成差分検査が落ちる | **落ちた**（`pnpm run codegen` 後、コミット済み生成物と差分あり） |
| M2 | OpenAPI の `required` から `confidentiality` だけ外す（プロパティは残す） | 生成型が省略可になり、再生成差分検査が落ちる | **落ちた**（`confidentiality?: string` へ変化）。※型検査は素通りする——**この型を読むコードがまだ無い**（[[IADR-0132]] 決定 5 の但し書きと同じ現象） |
| M3 | `scripts/contract-schema-baseline.json` から `CitationDto.Confidentiality` を消す | 契約検査が落ちる | **落ちた**（`exit=1`。非破壊 1 件の差分として検出） |
| M4 | C# の既定値を外す（`string Confidentiality`） | **破壊的**として契約検査が落ちる | **落ちた**（`[破壊的] メンバーの必須化: …CitationDto.Confidentiality（省略可能 → 必須）`。allowlist 承認を要求される） |
| M5 | `ConfidentialityLevels.SafeDefault` を `Restricted` → `Public` にする（安全側の反転） | 契約検査が落ちる | **落ちた**（`[破壊的] const 値の変更: …SafeDefault（Restricted → Public）`） |
| M6 | `restricted` の表示名を「**極秘**」へ戻す（Storybook の例） | 何かが落ちてほしい | **素通りした**（lint / typecheck / `packages/ui` の Vitest すべて exit=0）。**表示名を機械検査する仕組みは無い**——正は計画リポジトリの用語集であり、実装側に照合先が無いためである。§申し送り 5 に挙げる |
| M9 | テスト仕様書から `CitationMapperTests` の記載を落とす | カバレッジのラチェットが落ちる | **落ちた**（`[記載の消失] docs/tests/FR-04_ai-answer-citations.md が挙げていた CitationMapperTests`） |

**未実測（.NET SDK が無いため）**:

| # | 変異 | 期待（机上） | 実測 |
| --- | --- | --- | --- |
| M7 | `CitationMapper` から `Confidentiality:` の実引数を落とす | T-17 / T-20 が落ちる（既定値により**コンパイルは通る**ため、テストだけが検出できる） | **未検証** |
| M8 | `ConfidentialityLevels.FromAttributes` の縮退先を `Public` にする | T-18 が落ちる | **未検証**（M5 と違い `SafeDefault` を触らないため契約検査では捕まらない） |

> **M7 が「コンパイルは通るがテストだけが落ちる」型である**ことは、既定値つき位置引数を選んだ帰結である
> （§D1 系）。**そのテストを走らせられない環境で実装した**以上、この一点は CI に依存する。
> §申し送り 4 のとおり、マージ前に CI の緑を確認すること。

## 申し送り

1. **SC-01 出典行の機密区分チップ（表示）** — 契約が載ったので着手できる。i18n（Lingui ja/en）と
   「色だけで意味を持たせない」（INDEX 決定 21）を伴うため別 issue。
   `docs/screens/SC-01_search-chat.md` の表 #6-b は本 PR で理由を書き換えた（「契約に無い」→「契約に載った・表示は別 issue」）。
2. **表示名の古い根拠 4 件**（上表 #2〜#5）。planning#200 で表示名が確定したため、
   「裁定待ち」という根拠は成り立たない。SPA が表示名を出すかの判断とセットで是正する。
3. **属性キー定数の三重化**。`ConfidentialityLevels.AttributeKey`（本 PR で追加）・
   `DataSource.ConfidentialityKey`・`DocumentAttributes.ConfidentialityKey` が同じ値を持つ。
   後 2 者を契約定数へ寄せる整理は別 issue。
4. **バックエンドの未検証**（上記）。`dotnet build` / `dotnet test` / `dotnet format --verify-no-changes`
   は CI（`ci.yml`）で初めて走る。**マージ前に CI の緑を確認すること。** とくに変異 M7 / M8
   （§変異試験）は本 PR のテストだけが検出でき、その実行が CI 頼みである。
5. **表示名を機械検査する仕組みが無い**（変異 M6 が素通りした）。正は計画リポジトリの用語集だが、
   実装側にはそれを照合する検査が無く、誰かが「極秘」と書いても止まらない。
   用語集の表を読んで写像を突合する検査（`scripts/check-*.js`）を足す価値がある。**別 issue の候補。**

## 未決事項

- `AskCitationsEvent`（SSE）は OpenAPI の生成対象外（[[IADR-0131]] 決定 4）であり、SPA 側の
  手書き型 `AskCitation`（`features/sc01-search/citations.ts`）には本項目を足していない。
  表示を作る issue で型と表示を同時に足す（**先に型だけ足すと使われないフィールドが残る**）。
