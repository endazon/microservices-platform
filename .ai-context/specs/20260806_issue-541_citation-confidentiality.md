---
title: CitationDto に機密区分を追加する（出典の越境判断の手掛かり）
type: spec
status: done
related_ids: [FR-04, FR-05, FR-11, UC-01, UC-02, SC-01, ADR-0004, ADR-0010, IADR-0131, IADR-0132]
author: Claude
created: 2026-08-06
updated: 2026-08-06
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
  - planning:projects/microservices-platform/06_technical/08_data-egress-policy.md
  - planning:docs/glossary.md
related_specs:
  - ../../docs/functional/FR-04_ai-answer-citations.md
  - ../../docs/tests/FR-04_ai-answer-citations.md
  - ../../docs/screens/SC-01_search-chat.md
  - ../../docs/api/openapi.yaml
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
- 関連 ADR（実装）: [IADR-0131](../adr/IADR-0131_openapi-as-bff-contract-source.md) 決定 5（状態・種別を `enum` にしない）／[IADR-0132](../adr/IADR-0132_openapi-required-from-csharp-nullability.md) 決定 1・2 系（`required` は C# の非 null 性から・`required` と `default` を同居させない）
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
  - `docs/api/openapi.yaml` の `CitationDto`（契約の単一情報源。[IADR-0131](../adr/IADR-0131_openapi-as-bff-contract-source.md) 決定 1）と orval 生成物の再生成
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
| D2 | 値集合は **`enum` にしない** | `ConfidentialityLevels` の `const` 群 ＋ `Normalize`（[IADR-0131](../adr/IADR-0131_openapi-as-bff-contract-source.md) 決定 5・#531 の作法） |
| D3 | 安全側の既定は **`restricted`** | 欠落・空・未知値はすべて `restricted` へ縮退 |
| D4 | `HighestConfidentiality` を新値集合へ載せ替える | 4 値の梯子を 2 か所に持たない |
| D5 | OpenAPI は `required` に入れ、`default` は書かない | [IADR-0132](../adr/IADR-0132_openapi-required-from-csharp-nullability.md) 決定 1・決定 2 系 |
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
   破壊的に分類し、「既定値を付ければ非破壊」と定める（[IADR-0122](../adr/IADR-0122_contract-schema-source-and-compat-gate.md) 決定 2）。承認 allowlist を
   使わずに済ませられるなら、そうする。
2. **ローリング更新中の実体**: `CitationDto` は AiAnalysisService → BFF → SPA を JSON で渡る。
   本項目を持たない旧サービスの応答が新 BFF に届き得る。`System.Text.Json` は
   コンストラクタ引数の既定値を尊重するため、**欠けたら安全側へ縮退**する。

### D3: なぜ安全側が `restricted` なのか（`DataSource` の `internal` と逆である理由）

`DataSource.DefaultConfidentiality` は `internal` である（[IADR-0019](../adr/IADR-0019_datasource-default-attributes.md)）。**逆だが矛盾していない**
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

- **`required` に入れる**: C# 側が `string`（非 null）だから（[IADR-0132](../adr/IADR-0132_openapi-required-from-csharp-nullability.md) 決定 1）。
- **`default` は書かない**: `required` と `default` の同居は契約の自己矛盾（[IADR-0132](../adr/IADR-0132_openapi-required-from-csharp-nullability.md) 決定 2 系）。
  C# 側の既定値は「発行側が供給しなかったときの縮退」であって「欠けてよい」ではない。
- **`enum` にしない**: [IADR-0131](../adr/IADR-0131_openapi-as-bff-contract-source.md) 決定 5。値集合は `description` に書く。

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

### 表示名の母集合（是正した 3 件と、しなかった 4 件）

> **最初に使った grep 式が母集合を取りこぼしていた。** 当初は
> `git grep -rn '極秘\|取扱制限\|社内限' -- src docs ':!src/ai-stock-trading'` で数えたが、
> **是正対象そのもの（`internal`＝「社内」）は「社内限」を含まないので、この式では構造的に拾えない。**
> 「誤った表示名を探すのに、正しい表示名だけを検索語にしていた」という取りこぼしである
> （マージ前監査で指摘され、残存 2 件が見つかった）。**正しくは値側から数える。**
>
> ```console
> $ git grep -nE '>(公開|社内|社内限|秘|取扱制限|極秘)<' -- src ':!src/ai-stock-trading'
> ```
>
> **是正は母集合から入る**という原則は、母集合の取り方そのものを間違えると効かない。

| # | 箇所 | 種別 | 本 PR |
| --- | --- | --- | --- |
| 1 | `src/packages/ui/src/stories/Primitives.stories.tsx`（`Select` の選択肢） | **正に反する写像**（`internal`＝「社内」・`restricted`＝「極秘」） | **是正した**（社内限 / 取扱制限） |
| 1' | 同ファイル（`StatusBadge` の例） | 同上（`社内`）。**#1 と同じファイルに残っていた** | **是正した**（社内限） |
| 1'' | `src/packages/ui/src/components/formControls.test.tsx`（`aria-label="機密区分"` の `Select`） | 同上（`社内`） | **是正した**（社内限） |
| 2 | `docs/screens/SC-03_document-detail.md` §属性の表示 | 「表示名が計画に無いので生値を出す」＝**古くなった根拠** | しない（§申し送り） |
| 3 | `docs/screens/SC-05_document-management.md` 表 #7 | 同上（「planning#197 の裁定待ち」） | しない（§申し送り） |
| 4 | `src/knowledge/frontend/src/features/abac/confidentiality.ts` の見出しコメント | 同上 | しない（§申し送り） |
| 5 | `src/knowledge/frontend/src/features/sc03-document/attributes.ts` の見出しコメント | 同上 | しない（§申し送り） |

> **偽陽性を 1 件除外した。** `sc05-documents/DocumentManagementPage.tsx` の `<Trans>公開</Trans>` は
> **文書を公開するボタンの label** であって機密区分の表示名ではない（`onCommand(doc.id, 'publish', …)`）。
> 値側から数える式は語だけを見るので、こうした同綴りを拾う。**機械検査を作るときは
> 「値と並んでいるか」を条件に含める必要がある**（§申し送り 5）。

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
- [x] 値集合は `enum` ではない（[IADR-0131](../adr/IADR-0131_openapi-as-bff-contract-source.md) 決定 5）
- [x] 既存の `CitationDto` の組み立て 4 箇所が**無改修でコンパイルできる**（非破壊）
- [x] OpenAPI の `CitationDto` に `confidentiality` が載り、`required` に入る（`default` は書かない）
- [x] 契約 baseline の差分が**非破壊のみ**である（破壊的変更 0 件）
- [x] 表示名を実装側で定義していない（正は planning `docs/glossary.md`。`restricted` は「取扱制限」）

## テスト方針

`CitationMapperTests` へ 5 ケースを足す（[IADR-0130](../adr/IADR-0130_test-spec-coverage-ratchet.md) のテスト仕様カバレッジと突合するため
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

**`dotnet` はホストの PATH には無いが、SDK コンテナで実走できる。** 当初「この環境では .NET SDK を
導入できない」と記録していたが、それは **`dotnet-install.sh` でホストへ入れる経路だけを試した**結果で
あった（`builds.dotnet.microsoft.com:443` はプロキシに 403 で拒否される）。**docker は使える**ので、
バックエンドの検証はコンテナで行える。素の `docker run` では NuGet が
`NU1301 … UntrustedRoot` で全滅するため、**プロキシの CA を信頼させ、`--network host` で
プロキシ（`127.0.0.1:39793`）へ到達させる**必要がある。

```console
$ docker run --rm --network host \
    -v "<worktree>:/w" \
    -v /root/.ccr/ca-bundle.crt:/usr/local/share/ca-certificates/ccr.crt:ro \
    -v "<scratchpad>/nuget:/root/.nuget/packages" \
    -w /w -e HTTPS_PROXY=http://127.0.0.1:39793 -e HTTP_PROXY=http://127.0.0.1:39793 \
    mcr.microsoft.com/dotnet/sdk:10.0 \
    bash -lc 'update-ca-certificates >/dev/null; dotnet build src/knowledge/backend/backend.slnx'
```

- **`--network host` が要る**。プロキシは**ホストの** `127.0.0.1:39793` にあり、コンテナ内の
  `127.0.0.1` は別物である。
- **CA の投入が要る**。`SSL_CERT_FILE` を渡すだけでは NuGet の SSL 検証を通らなかった。
  `/usr/local/share/ca-certificates/` へ置いて `update-ca-certificates` を走らせる。
- NuGet パッケージはボリュームで再利用する（毎回の復元を避ける）。

| コマンド | 結果 |
| --- | --- |
| `dotnet build src/knowledge/backend/backend.slnx` | **green**（0 Error / Warning 2 件は `MinioBuilder` の既存 obsolete 警告で本作業と無関係） |
| `dotnet format src/knowledge/backend/backend.slnx --verify-no-changes` | **差分なし** |
| `dotnet test …/AiAnalysisService.Api.Tests` | **59 件すべて合格** |
| `dotnet test …/Knowledge.Contracts.Tests` | **6 件すべて合格** |
| `dotnet build src/platform/backend/backend.slnx` | **green**（0 Error）。**4 箇所目の呼び出し元 `Platform.Bff.Tests/BffTestFactory.cs`（位置引数 7 個）が無改修でコンパイルできることを実測**した |
| `dotnet test …/Platform.Bff.Tests` | **148 合格 / 1 skip** |
| `dotnet format src/platform/backend/backend.slnx --verify-no-changes` | **差分なし** |

> **`platform/backend` のビルドには `src/ai-stock-trading` の populate が要る。** 未 populate だと
> `BffEndpointComposition.cs(1,7): error CS0246: … 'AiStockTrading' could not be found` で落ちる。
> `git submodule update --init -- src/ai-stock-trading` で解消し、**pin は動かない**。
> これにより「**4 箇所すべてが無改修でコンパイルできる**」という受け入れ基準は 4/4 を機械検証できた
> （当初は platform 側が実コンパイル検証できず 3/4 に留まっていた）。
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

**素通りしたものも隠さず記す。** バックエンドのテストを壊す変異（M7 / M8）も、上記のコンテナ経路で
**実際に当てて落ちることを確かめた**（当初は「SDK が無いので未検証」としていた。§検証の冒頭を参照）。

| # | 変異 | 期待 | 実測 |
| --- | --- | --- | --- |
| M1 | OpenAPI の `CitationDto` から `confidentiality` プロパティごと落とす | 生成物の再生成差分検査が落ちる | **落ちた**（`pnpm run codegen` 後、コミット済み生成物と差分あり） |
| M2 | OpenAPI の `required` から `confidentiality` だけ外す（プロパティは残す） | 生成型が省略可になり、再生成差分検査が落ちる | **落ちた**（`confidentiality?: string` へ変化）。※型検査は素通りする——**この型を読むコードがまだ無い**（[IADR-0132](../adr/IADR-0132_openapi-required-from-csharp-nullability.md) 決定 5 の但し書きと同じ現象） |
| M3 | `scripts/contract-schema-baseline.json` から `CitationDto.Confidentiality` を消す | 契約検査が落ちる | **落ちた**（`exit=1`。非破壊 1 件の差分として検出） |
| M4 | C# の既定値を外す（`string Confidentiality`） | **破壊的**として契約検査が落ちる | **落ちた**（`[破壊的] メンバーの必須化: …CitationDto.Confidentiality（省略可能 → 必須）`。allowlist 承認を要求される） |
| M5 | `ConfidentialityLevels.SafeDefault` を `Restricted` → `Public` にする（安全側の反転） | 契約検査が落ちる | **落ちた**（`[破壊的] const 値の変更: …SafeDefault（Restricted → Public）`） |
| M6 | `restricted` の表示名を「**極秘**」へ戻す（Storybook の例） | 何かが落ちてほしい | **素通りした**（lint / typecheck / `packages/ui` の Vitest すべて exit=0）。**表示名を機械検査する仕組みは無い**——正は計画リポジトリの用語集であり、実装側に照合先が無いためである。§申し送り 5 に挙げる |
| M9 | テスト仕様書から `CitationMapperTests` の記載を落とす | カバレッジのラチェットが落ちる | **落ちた**（`[記載の消失] docs/tests/FR-04_ai-answer-citations.md が挙げていた CitationMapperTests`） |

**バックエンドのテストを壊す変異**（SDK コンテナで実走）:

| # | 変異 | 期待 | 実測 |
| --- | --- | --- | --- |
| M7 | `CitationMapper` から `Confidentiality:` の実引数を落とす | T-17 / T-20 が落ちる（既定値により**コンパイルは通る**ため、テストだけが検出できる） | **落ちた**。`Failed: 5 / Passed: 54`。`ToCitations_CarriesConfidentialityFromAttributes`（3 ケース）・`ToCitations_NormalizesConfidentialityCasing`・`ToCitations_AssignsConfidentialityPerCitation`。**予告どおりコンパイルは通った**（`Build FAILED` は出ていない） |
| M8 | `ConfidentialityLevels.SafeDefault` を `Restricted` → `Public` にする（縮退先の反転） | T-18 が落ちる | **落ちた**。`Failed: 6 / Passed: 53`。`ToCitations_FallsBackToRestricted_WhenConfidentialityMissingOrUnknown`（`null` / `""` / `"   "` / `"top-secret"` の 4 ケース）・`ConfidentialityLevels_HasFourValues_AndFailsSafeToRestricted`・`ToCitations_AssignsConfidentialityPerCitation` |

いずれも変異を戻したうえで **59 件すべて合格**に復帰することを確認した（変異が残っていないこと・
テストが変異そのものに反応したことの両方を示す）。

> **M7 が「コンパイルは通るがテストだけが落ちる」型である**ことは、既定値つき位置引数を選んだ帰結である
> （§D1 系）。**その予測が正しかったことを実測で確かめた** —— 変異後もビルドは成功し、
> 落ちたのはテストだけだった。既定値つき位置引数は「壊しても静かに通る」経路を作るので、
> **この形を選ぶときはテストが唯一の網になる**ことを意識する必要がある。

## 申し送り

1. **SC-01 出典行の機密区分チップ（表示）** — 契約が載ったので着手できる。i18n（Lingui ja/en）と
   「色だけで意味を持たせない」（INDEX 決定 21）を伴うため別 issue。
   `docs/screens/SC-01_search-chat.md` の表 #6-b は本 PR で理由を書き換えた（「契約に無い」→「契約に載った・表示は別 issue」）。
2. **表示名の古い根拠 4 件**（上表 #2〜#5）。planning#200 で表示名が確定したため、
   「裁定待ち」という根拠は成り立たない。SPA が表示名を出すかの判断とセットで是正する。
3. **属性キー定数の三重化**。`ConfidentialityLevels.AttributeKey`（本 PR で追加）・
   `DataSource.ConfidentialityKey`・`DocumentAttributes.ConfidentialityKey` が同じ値を持つ。
   後 2 者を契約定数へ寄せる整理は別 issue。
4. **バックエンドの検証手順を残した**（§検証の冒頭）。`dotnet` はホストに無いが SDK コンテナで実走できる。
   **ただし `--network host` とプロキシ CA の投入が要る**（素の `docker run` は NuGet が `NU1301
   UntrustedRoot` で全滅する）。この手順は本仕様書にしか書かれておらず、**次に C# を触る作業者が
   同じ壁で「SDK が無い」と結論しかねない**。`docs/how-to/` へ切り出す価値がある。**別 issue の候補。**
5. **表示名を機械検査する仕組みが無い**（変異 M6 が素通りした）。正は計画リポジトリの用語集だが、
   実装側にはそれを照合する検査が無く、誰かが「極秘」と書いても止まらない。
   用語集の表を読んで写像を突合する検査（`scripts/check-*.js`）を足す価値がある。**別 issue の候補。**
   **検査を作るときの要件は本作業で 2 つ判明した** —— (a) **誤った表示名を検索語にできない**ので、
   `internal` などの**値の側から**引く必要がある（§表示名の母集合の取りこぼし）、(b) 値と並んでいない
   同綴り（`<Trans>公開</Trans>` = 公開ボタンの label）を**偽陽性として除外**する必要がある。
6. **「フェイルセーフ」が 2 つの逆向きの既定に付いている。** 本作業は「属性の欠落 = `restricted`」を
   FR-05 deny-by-default の帰結として確立したが、`knowledge/frontend` の
   `features/abac/confidentiality.ts` はコメントで**「フェイルセーフ既定値」と名乗りながら `internal` を返す**。
   `sc05-documents/DocumentForm.tsx` はそれをフォーム初期値に使うため、**属性を持たない既存文書を編集すると
   `internal` が既定選択され、保存でその値が確定する**（書き込み経路）。
   両者は軸が違う（本作業＝**読み取り時の表示の縮退**／前者＝**付与時の初期値**）ので ADR 違反ではないが、
   同じ語が逆向きの既定に付いている状態は、**次の読み手にどちらかへ「揃える」誤修正を誘発する**。
   `confidentiality.ts` のコメントを「フォーム初期値であって縮退規則ではない」と書き分けるべきである。
7. **`ConfidentialityLevels.All` は契約ゲートの網に載らない。** `contract-schema-baseline.json` に
   記録されるのは `const` 群だけで、`public static readonly string[] All` は含まれない。したがって
   **梯子の順序を入れ替える変更（= 越境判定の `Rank` の意味を変える変更）は契約検査で止まらない**
   （変異 M5 が落ちたのは `SafeDefault` が `const` だったため）。現在の唯一の網は
   `ConfidentialityLevels_HasFourValues_AndFailsSafeToRestricted` が `All` を順序ごと固定していることであり、
   **このテストを緩めた瞬間に網が消える。**

## 未決事項

- `AskCitationsEvent`（SSE）は OpenAPI の生成対象外（[IADR-0131](../adr/IADR-0131_openapi-as-bff-contract-source.md) 決定 4）であり、SPA 側の
  手書き型 `AskCitation`（`features/sc01-search/citations.ts`）には本項目を足していない。
  表示を作る issue で型と表示を同時に足す（**先に型だけ足すと使われないフィールドが残る**）。
