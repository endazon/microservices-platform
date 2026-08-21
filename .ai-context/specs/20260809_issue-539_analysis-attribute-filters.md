---
title: 作業仕様書 — AnalysisRequest に対象範囲（属性フィルタ）を追加する（#539）
type: spec
status: done
related_ids:
  - FR-04
  - FR-05
  - FR-07
  - SC-01
  - SC-08
  - UC-01
  - UC-02
  - ADR-0043
  - IADR-0151
author: claude
created: 2026-08-09
updated: 2026-08-09
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md
  - planning:projects/microservices-platform/07_adr/ADR-0043_scoped-attribute-value-lookup.md
related_specs:
  - "./20260809_issue-540_scoped-attribute-values.md"
  - "../adr/IADR-0151_scoped-attribute-value-facets.md"
---

# 作業仕様書 — AnalysisRequest に対象範囲（属性フィルタ）を追加する（#539）

## 起点となる計画書（トレーサビリティ）

| 種別 | ID | 何を求めているか |
| --- | --- | --- |
| 画面 | **SC-01** | 主要素に**対象範囲フィルタ（タグ／部門／プロジェクト）**。入力規則は「**権限内の**タグ／`department`／`project` のみ選択可」（L181 / L186） |
| 画面 | **SC-08** | 分析対象の指定は**タグ・部門・プロジェクトのチップ**＋検索条件による追加。**候補は権限内に限り、同じ候補 API を用いる。SC-01 と一体で扱う**（L341〜342） |
| 要求 | **FR-04** | 「対象範囲の指定」を要求として明示（裁定 Q1） |
| 要求 | **FR-05** | ABAC。**範囲指定は narrowing-only で権限を一切広げない** |

**計画の言葉（L198・裁定 Q1）**:
> `SearchRequest` は既に `AttributeFilters` を持つのに `AnalysisRequest` だけが持たない非対称を解消する。

**Q9: `folder` は用いない。新設もしない。** ABAC 属性体系に `folder` が存在せず、
フォルダは取り込み時に属性へ写像されて消える。パスの階層・序数は本属性体系が意図的に排除している。

**Q2（権限内候補 API）は #540 で着地済み**（`/bff/attribute-values`）。**本 issue の射程外**である。

## 母集合（[IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md) 決定 1）

**着手時に実装側が引き直した。走査基準: develop `7d9b0e4`（#635 マージ直後）。**

**［#635 の教訓を適用］「コンパイルエラーで全部出る」経路だけを引かない。**
本 issue は**契約にメンバーを足す**変更なので、型検査は既存の呼び出し側を 1 つも壊さない
（既定値つきの追加は非破壊。[IADR-0122](../adr/IADR-0122_contract-schema-source-and-compat-gate.md) 決定 2）。**したがって型検査は母集合を教えてくれない。**
**HTTP / JSON で要求を組み立てている経路（フロントエンド・統合テスト）をパスから引くこと。**

### ★ 着手前の実測で分かった、issue 本文に書かれていない前提

**1. `AttributeFilters` は既に 2 系統あり、型が違う。**

| 用途 | 場所 | 型 |
| --- | --- | --- |
| 検索 | `Knowledge.Contracts/Dtos/SearchDto.cs` | `Dictionary<string, string>?`（**単値**。コメントに「FR-03: 単値完全一致フィルタ（**後方互換**）」） |
| 分析データ範囲 | `Knowledge.Contracts/Dtos/AnalysisDto.cs`（`AnalysisDataRange`） | `Dictionary<string, List<string>>?`（**多値**） |

**2. ★ 現在の検索フィルタは `tags` を絞れない。** —— **これが本 issue の中心的な発見である。**

`QdrantVectorStore.BuildAttributeConditions` は**キーを `attributes.{f.Key}` にハードコードしている**。
`InMemoryVectorStore.MatchesFilters` も `c.Attributes` しか見ない（`c.Tags` を見ていない）。実測:

```
QdrantVectorStore.cs:123   Key = $"attributes.{f.Key}",
InMemoryVectorStore.cs:79  c.Attributes.TryGetValue(f.Key, out var v)
```

一方 **#540 が入れた「値集合の照会」側は `tags` を知っている**——
`AttributeValueKeys.ToPayloadKey` が `tags` だけを例外として扱い、他を `attributes.<key>` へ写す。

**つまり「候補は出せるが、その候補で絞れない」状態である。**
`tags` を選ばせておいて絞れないと、**画面が候補として出した値が結果に効かない**（利用者から見て壊れている）。

**3. SC-08 のチップは「planning#197 の裁定待ち」として明示的に未実装である。**
`sc08-analysis/analysisRange.ts` の冒頭コメントが、**本 issue が解く論点をそのまま書いている**:
> **タグ・フォルダのチップは実装しない**（画面仕様書 §実装しない要素 (a)）——`AnalysisDataRange` は
> 属性キー → 値集合しか取らず、**タグは属性とは別の軸**、フォルダは契約に存在しない。（中略）
> SC-01 の対象範囲フィルタと**同型の論点**であり、planning#197 の裁定を待つ。

**この注記は本 issue の完了とともに消す**（残すと「未実装」と読める）。

### 触るもの（**着手後に確定させる。現時点の想定**）

| # | 対象 | 何をするか |
| --- | --- | --- |
| 1 | `Knowledge.Bff.Endpoints/AnalysisBffEndpoints.cs` | `AnalysisRequest` へ対象範囲を足す |
| 2 | `AiAnalysisService/.../Endpoints/AnalysisEndpoints.cs` | `AskRequest` へ同じものを足す（**BFF だけでは後段へ届かない**） |
| 3 | `.../Services/IRagOrchestrator.cs` ＋ `RagOrchestrator.cs` | `AskAsync` / `AskStreamAsync` が範囲を受け、**`DataRangeScopeResolver` で ABAC と交差**させる（`AnalyzeAsync` が既に採っている形） |
| 4 | `RetrievalService/.../QdrantVectorStore.cs` ＋ `InMemoryVectorStore.cs` | **`tags` を絞れるようにする**（上記 2。写像は `AttributeValueKeys.ToPayloadKey` に寄せ、照会側と 1 つの真実にする） |
| 5 | `docs/api/openapi.yaml` ＋ orval 生成物 | 契約の追随（生成物はコミットし CI が再生成差分を検査する） |
| 6 | `knowledge/frontend/.../sc01-search/useAskStream.ts` ＋ `SearchChatPage.tsx` | SC-01 の対象範囲フィルタ |
| 7 | `knowledge/frontend/.../sc08-analysis/analysisRange.ts` ＋ `AnalysisDashboardPage.tsx` | SC-08 のチップ（上記 3 の注記を消す） |
| 8 | 上記それぞれのテスト ＋ `docs/functional/FR-04*` / `docs/tests/FR-04*` / `docs/tests/SC-01*` / `docs/tests/SC-08*` | 追随 |

### 触らないもの

| 対象 | 理由 |
| --- | --- |
| `SearchRequest.AttributeFilters`（単値） | **後方互換のために残っている口**であり、本 issue は分析側の欠落を埋めるもの。単値の口を壊さない |
| `/bff/attribute-values`（#540） | **候補 API は着地済み**（裁定 Q2）。本 issue は「候補で絞る」側だけを足す |
| `folder` に相当するキー | **裁定 Q9 で明確に否定されている**（新設もしない） |
| `src/ai-stock-trading` | 別プロジェクトの submodule（`KnowledgeModels.cs` に `AttributeFilters` が出るが対象外） |

## 決めたこと（着手時の判断。IADR に残す）

### 判断 1: **型は多値（`Dictionary<string, List<string>>`）にする**

**計画の言う「非対称の解消」は「能力を持たせること」であって、単値という形を写すことではない。**根拠:

1. **画面が多値を要求する。** SC-01 は「対象範囲フィルタ（タグ／部門／プロジェクト）」、
   SC-08 は「**チップ**」であり、**利用者は複数のタグを選ぶ**。単値だと「経理」か「規程」の一方しか選べない。
2. **単値の口は自ら「後方互換」と名乗っている**（`SearchDto.cs` のコメント）。
   **後方互換のために残っている形を、新しい口の手本にしない。**
3. **交差の機構が既に多値で在る。** `DataRangeScopeResolver` は
   `AnalysisDataRange.AttributeFilters`（多値）と ABAC の多値 allow-list を交差させる。
   多値で足せば**この機構をそのまま使える**——単値にすると変換層が 1 枚増える。
4. **ABAC 側（`AccessScope` / `AttributeFilter`）が多値である。** 実効境界の表現に合わせるほうが素直である。

### 判断 2: **`tags` を絞れるようにするのは本 issue の射程に含める**

**含めないと受け入れ基準を満たせない**——SC-01 / SC-08 はどちらも第一に「タグ」を挙げており、
候補（#540）が既に `tags` を返している以上、**絞れないまま出すと画面が壊れる**。

**写像は `AttributeValueKeys.ToPayloadKey` へ寄せる**（照会側と同じ関数を使う）。
2 か所に同じ知識を持たせると、**片方だけ直したときに「候補には出るが絞れない」が再発する**。

## テスト（受け入れ基準の写像）

| # | 確かめること |
| --- | --- |
| 1 | `/bff/analysis/ask`・`/ask/stream` が対象範囲を受け取り、後段へ渡す |
| 2 | **範囲は ABAC と交差する（narrowing-only）**。権限外の値を指定しても広がらない |
| 3 | **権限の外だけを指す範囲は全体 deny** へ倒れる（`DataRangeScopeResolver` の既存規則） |
| 4 | **`tags` で絞れる**（Qdrant / InMemory の双方） |
| 5 | `department` / `project` で絞れる（属性経路の回帰） |
| 6 | 範囲を指定しないときの挙動が従来と同じ（既定値つき追加は非破壊） |
| 7 | 画面（SC-01 / SC-08）が候補 API の値でチップを組み立て、要求へ載せる |
| 8 | **候補に出る値と、絞れる値が一致する**（`ToPayloadKey` を 1 つの真実にしたこと） |

## 実装中に決めたこと（仕様書からの差分）

### 1. 母集合は 8 行の表より広かった。**画面テストが「通信していないこと」を assert していた**

**着手時の表に無かったもの**:

| 追加 | 何を | なぜ着手時に挙がらなかったか |
| --- | --- | --- |
| `features/scope-filter/`（**新規 4 ファイル**） | 軸の定義・候補の取得・チップの部品・テスト | 表は「SC-01 の画面」「SC-08 の画面」と書いており、**2 画面が 1 つの部品を共有する**ことを織り込んでいなかった。計画は「SC-01 と一体で扱う」と書いてある |
| `searchFlow.test.tsx` / `SearchChatPage.test.tsx` / `AnalysisDashboardPage.test.tsx` | 呼び出しを**添字ではなく URL で選ぶ**形へ | **候補の取得を起動時に足したことで `mock.calls[0]` が別の呼び出しになった。** 3 件が落ちた |
| `src/vitest.config.ts` | カバレッジ床（branches 85 → 86） | テストを増やしたら床を上げる規約（[IADR-0034](../adr/IADR-0034_frontend-coverage-gate.md)） |
| 英語カタログ 7 件 | 新規文言の翻訳 | 未翻訳は `check-i18n-catalogs.js` が止める |

**教訓（#635 の型の再来である）。** #635 では「コンパイルエラーで全部出た」を母集合の証拠にして
統合テストを取りこぼした。今回は**契約にメンバーを足すだけなので型検査は何も教えない**と
着手時に書いており、そこは正しく警戒できた。**それでも落ちたのは「テストが観測している範囲」**である——
`expect(apiRequest).not.toHaveBeenCalled()` は「送信していない」の代理表現であり、
**画面が別の通信を始めた瞬間に意味が変わる**。是正では、その代理表現を
`callsTo('/analysis/analyze')` という**意図どおりの表現**へ置き換えた。

### 2. カバレッジ床の導出規則を**取り違えた**（自己是正）

当初「数ポイントずつ上げる」という読みで 92/92/90/88 を書いたが、
**`vitest.config.ts` が定めている規則は「MSP 所有分の実測から 5pt 下・切り捨て」**である。
測り直した結果、**引き上がるのは branches だけ（85 → 86）**で、lines/statements と functions は据え置きだった。

- 全ユニット横断: lines/statements 96.48%（5740/5949）／ branches 90.78%（1182/1302）／ functions 91.87%（407/443）
- **MSP 所有分のみ**: lines/statements 95.98%（4473/4660）／ branches 91.95%（915/995）／ functions 93.17%（314/337）

**規則を読まずに「それらしい値」を置くと、床が回帰防止ではなく気分になる。**

### 3. `AnalysisDataRange` の器は SC-08 だけが使う

SC-01（`ask` 系）は `AnalysisDataRange`（`Query` / `TopK` を持つ）を取らない。
そこで **`DataRangeScopeResolver` へ器に依存しない多重定義を足し、交差の規則だけを共有した**。
規則を 2 本持つと「検索では効くが分析では効かない」型の食い違いが生まれる
（**まさにその食い違いが `tags` で実際に起きていた**のが本 issue の出発点である）。

### 4. **候補の取得を TanStack Query へ載せ替えた**（PR #641 レビュー 🟡 の是正）

**当初 `useEffect` ＋ `useState` ＋ 手製の `cancelled` フラグで直接呼んでいた。**
`CLAUDE.md` は「**サーバー状態は TanStack Query に一元化する**（`queryClient.ts` が唯一の生成点）」と
定めており、**無記録の逸脱**だった。指摘は正当である。

**実害もあった**——`ScopeFilter` は SC-01 / SC-08 の共有部品なので、
**マウントのたびに 3 軸をキャッシュなしで取り直していた**。

`/bff/attribute-values` は POST なので orval は `useMutation` しか生成しない（照会に使えない）。
**`useSearchQuery`（SC-02）と同じ作法**で、生成された操作関数を `useQueries` の `queryFn` に据えた
（[IADR-0135](../adr/IADR-0135_generated-client-adoption-and-cache-keys.md) 決定 2）。軸ごとに独立したクエリなので、**1 軸の失敗が他を巻き込まない**性質は保たれる。

**指摘が挙げた傍証は当たっていた**——`ScopeFilter.test.tsx` は `QueryClientProvider` で包んでいたのに
ツリーに `useQuery` が無く、**wrapper が死んでいた**。載せ替えで生きた。

**是正で 2 つ副次的に直った／動いた:**

| | 変化 |
| --- | --- |
| **テストの `QueryClient`** | 素の `new QueryClient()` は**本番の再試行を持ち込む**ため、「1 軸だけ失敗したときの縮退」が再試行の待ちに隠れて観測できなかった（実測で失敗）。`renderUnitRoute` と同じ `retry: false` へ揃えた |
| **1 kB 未満の遅延チャンク** | **7 本 → 6 本**（`search-*.js` 400 B が解消）。載せ替えでチャンクグラフが変わった |

### 5. ★ **テストの記録を `static` にして CI で落ちた**（並列実行）

`AskStream_PassesAttributeFiltersDownstream` が CI で落ちた（`LastStreamFilters` が null）。
**手元では 1 度も落ちなかった。**

**原因は `StubRagOrchestrator` の記録を `static` にしたことである。**
xUnit は**テストクラスを並列に走らせる**ので、同じ端点（`/analysis/ask/stream`）を叩く
別のクラス（`AskStreamEndpointTests` 等）の要求が、**こちらの記録を上書きする**。

**是正**: 記録をインスタンスの状態にし、テストは `factory.Services` から解決する。
スタブは factory ごとの singleton であり、`IClassFixture` はクラスごとに factory を作るので、
**クラス間で共有されない**。

**教訓**: **「端点の外から観測できないものを観測する」ために状態を置くときは、その状態の寿命と共有範囲を決める。**
`static` は最も広い共有範囲であり、**並列実行と最も相性が悪い**。
`dotnet test` を 3 回連続で回して再現しないことも確認した（対症でなく原因を消したことの確認）。

### 6. バンドルの床が動いた（**2 度**）。**折り畳もうとしたらもっと悪化した**

CI の `check-chunk-budget` が落ちた（手元で `pnpm run build` を回していなかったので気づけなかった）。

| 段階 | 初期ロード合計 | 1 kB 未満の遅延チャンク |
| --- | --- | --- |
| develop | 578.15 kB | 6 本 |
| 対象範囲フィルタを足した直後 | 578.72 kB（+0.57） | **7 本**（orval 生成の `search` 400 B が共有され切り出された） |
| **TanStack Query へ載せ替えた後（最終）** | **582.78 kB**（develop から +4.63） | **6 本**（元に戻った） |

**最後の +4.06 kB は規約に従った代償である**——`useQueries` を使うと `vendor-query`
（**名前付きチャンク＝エントリの静的依存**）に載る量が増える。
**`CLAUDE.md` が定める「サーバー状態は TanStack Query に一元化する」は確定事項**であり、
0.7% の初期ロードと引き換えに破る理由は無い。

**先例に倣った折り畳みは逆効果だった。** `vite.config.ts` の `@platform/ui` / `vendor-query` に倣って
「生成 API を 1 本へ束ねる」manualChunks 規則を試したが、実測で**初期ロードが +13.49 kB**
（578.15 → 591.64 kB）に増えた。名前付きチャンクへ寄せると**遅延ロードだったものが初期ロードへ移る**ためである。
**0.57 kB の節約に 13.49 kB は払えないので採らない。**

### 7. ［観測・射程外］Qdrant の検索結果は `Tags` を復元していない
`QdrantVectorStore.MapPayload` は `Tags: []` を固定で入れている（実測）。
**絞り込みには影響しない**（フィルタは Qdrant 側で効き、候補は `/bff/attribute-values` が返す）ため
本 issue では触らない。**ただし SC-02 の結果一覧は既にタグを描こうとしており**
（`SearchResultsPage.tsx` が `result.tags?.map(...)` を持つ）、**本番ではタグが 1 つも出ない**。
`InMemoryVectorStore` は `Tags` を正しく運ぶので**テストは緑のまま**である——
[IADR-0014](../adr/IADR-0014_qdrant-attribute-payload-key.md) が ABAC 属性で踏んだ「テストは緑・本番は空」と同じ型である。

**#642 として起票した**（「記録に留める」だけだと、起票されたかどうかを文書から確認できない）。

## 検証記録（実測）

**走査基準: develop `7d9b0e4`（#635 マージ直後）＋ 本ブランチ。**

| コマンド | 結果 |
| --- | --- |
| `dotnet build knowledge/backend/backend.slnx` | `Build succeeded.` |
| `dotnet test knowledge/backend/backend.slnx` | 全 11 アセンブリ Passed（`RetrievalService` 64 件・`AiAnalysisService` 68 件。着手時は 51 / 59） |
| `dotnet format knowledge/backend/backend.slnx --verify-no-changes` | 差分なし |
| `pnpm run typecheck` / `lint` / `format:check` | いずれも通過（lint の warning 9 件は既存の `react-refresh` のみ） |
| `pnpm run test` | **606 件 Passed**（着手時 588 件） |
| `pnpm run test:coverage` | 床（90/90/88/86）を満たす |
| `pnpm run codegen` / `pnpm run i18n` | 再生成差分をコミット済み |
| `pnpm run build` ＋ `node scripts/check-chunk-budget.js` | 床を 578.15 → **582.78 kB** へ更新・1 kB 未満の遅延チャンクは 6 本のまま（上記「実装中に決めたこと 6」） |
| `node scripts/{check-doc-links,check-cross-repo-refs,check-plan-id-qualification,check-i18n-catalogs,check-test-traceability,check-test-spec-coverage}.js` | すべて OK |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | 293 件 Passed |

### 実走できなかったもの（**理由つき**）

- **実 Qdrant での絞り込みは実走していない。** `QdrantVectorStore` の検索そのものは実機が要る。
  **実機なしに固定できるのはキーの写像**（`BuildAttributeConditions`）であり、そこが
  「候補に出る値」と「絞れる値」を一致させる要なので、`internal` へ上げて直接テストした。
  **意味論（`Match.Keywords` が配列ペイロードの要素いずれかに一致すること）は Qdrant の仕様に依存しており、
  ここでは固定できていない**——`InMemoryVectorStore` 側で同じ意味論をテストして揃えてある。
