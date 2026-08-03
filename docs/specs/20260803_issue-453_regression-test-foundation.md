---
title: 再実装の退行防止テスト基盤（写像規約・アーキテクチャ検査・カバレッジ床）
type: spec
status: in-progress
related_ids: [NFR, IADR-0034, IADR-0115, IADR-0116]
author: Claude
created: 2026-08-03
updated: 2026-08-03
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
---

# 仕様書: 再実装の退行防止テスト基盤

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（NFR: 保守性・性能。**再実装全体の受け入れゲート**）
- ユースケース（UC）/ 画面（SC）: なし（写像の**対象**は `FR-01..21` / `UC-01..11` / `SC-01..21` 全域）
- 関連 ADR: [IADR-0034](../adr/IADR-0034_frontend-coverage-gate.md)（フロントのカバレッジ ratchet）／
  [IADR-0115](../adr/IADR-0115_impl-handoff-kit-as-single-source.md)（「成果物は正しいのに赤」を作らない）／
  [IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md)（進行規約・規約 4 と規約 6）
- 本リポジトリの起点: #453（親 #454 フェーズ 0）

## 目的・背景

全面再実装（#454）では既存実装を破棄し得るため、**退行の検知手段をコードではなくテストへ移す**必要がある。
#453 は各ドメイン issue のテストが載る共通基盤と横断ルールを、他のすべてに先立って整備する。

### 現状の実測（`develop` = `3441861` 時点）

| 対象 | 実測 |
| --- | --- |
| フロントのカバレッジ | **ゲートあり**。[`src/vitest.config.ts`](../../src/vitest.config.ts) の `thresholds`（lines 78 / statements 78 / functions 68 / branches 74）を [`frontend-tests.yml`](../../.github/workflows/frontend-tests.yml) が強制（[IADR-0034](../adr/IADR-0034_frontend-coverage-gate.md)） |
| **バックエンドのカバレッジ** | **そもそも計測されていなかった**。[`ci.yml`](../../.github/workflows/ci.yml) の `build-and-test` は `--collect:"XPlat Code Coverage"` を渡すが、**MSP の 14 テストプロジェクトはどれも `coverlet.collector` を参照しておらず**、Cobertura が 1 件も出ていなかった（後述「実測で判明した事実」）。閾値強制もコメントアウトされた例のまま |
| ユニット依存規則 | 機械検査あり（[`check-unit-dependencies.js`](../../scripts/check-unit-dependencies.js)） |
| BFF 境界 | 機械検査あり（[`check-bff-downstreams.js`](../../scripts/check-bff-downstreams.js)） |
| CPM（バージョン直書き禁止） | **検査なし**。CLAUDE.md は「`.csproj` の `PackageReference` にはバージョンを書かない」と定めるが機械強制されていない |
| 受け入れ基準 → テストの写像 | **規約なし・検査なし**。`docs/tests/` に FR/SC 別のテスト仕様書はあるが、コード側のテストと機械的に突合できない |
| 契約テスト（`Shared.Contracts` の後方互換） | **なし**。[IADR-0049](../adr/IADR-0049_composability-standards-phased-adoption.md) で条件付き繰延 |

### 実測で判明した事実（本 PR の CI 実行で確定）

床を武装する直前に、**MSP のバックエンドはカバレッジを 1 行も計測していなかった**ことが判明した。

| | テストプロジェクト数 | `coverlet.collector` 参照 |
| --- | --- | --- |
| MSP（platform + knowledge） | 14 | **0** |
| AST（`src/ai-stock-trading`・別プロジェクト） | 38 | 38 |

`dotnet test --collect:"XPlat Code Coverage"` は **テストプロジェクトが `coverlet.collector` を参照して
いないと何も出力しない**。CPM には `coverlet.collector` 6.0.4 が定義されているが、どの MSP テスト
プロジェクトも参照していなかったため、中央定義が未使用のまま気付かれずにいた。

その結果、CI が拾っていた 38 件の Cobertura は**すべて AST のもの**であり、初回に観測した
`line 47.22% / branch 41.45%` は **AST のカバレッジ**だった。AST 除外（レビュー指摘の対応）を入れた
とたんに 0 件になったことで、この事実が表に出た。**あのまま床を設定していれば、別プロジェクトの
数字を platform / knowledge の基準として固定していた。**

対処として 14 テストプロジェクトすべてに `coverlet.collector` の `PackageReference` を追加した。追加後の
CI 実行（`8bfe639`）で **レポート 14 件**（＝ MSP のテストプロジェクト数と一致）が集計されるようになり、
MSP 単独の実測値が初めて得られた。

**バックエンドのカバレッジ床が無いことが最大の穴である。** 再実装で 11 サービスを作り直す間、テストが薄い
まま置き換わっても CI は緑のままになる。#453 の受け入れ観点「カバレッジ floor が再実装前の水準を下回った
ままマージできない」は、まさにここを塞ぐことを求めている。

## 対象範囲

本 issue のスコープは 6 項目あるが、**1 PR に収めるとレビュー可能な変更単位を超える**
（[IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md) 規約 4）。規約 4 は「PR ではなく
issue を分割する」と定めるため、**本 PR では基盤の 4 項目を実装し、残る 2 項目は後続 issue へ切り出す**。

### 本 PR の対象

1. **テスト戦略と受け入れ基準 → テストの写像規約**（`docs/tests/TEST_STRATEGY.md`）
2. **写像の機械検査**（`scripts/check-test-traceability.js`）— テストが起点 ID を持つことを検査可能にする
3. **バックエンドのカバレッジ床**（`scripts/check-coverage-floor.js` ＋ `src/coverage-floor.json` ＋ `ci.yml`）
4. **`docs/DEFINITION_OF_DONE.md` の更新**（再実装方針に合わせる）

### 後続 issue へ切り出す（本 PR の対象外）

| 項目 | 理由 |
| --- | --- |
| **契約テスト基盤**（`Shared.Contracts` のイベント/API スキーマ後方互換検査） | スキーマの抽出方式（リフレクション / OpenAPI / proto）の選定から要り、[IADR-0049](../adr/IADR-0049_composability-standards-phased-adoption.md) の繰延判断の見直しも伴う。独立した設計判断であり IADR が要る |
| **E2E スモークセット**（Istio・Keycloak・BFF の統合スタック） | 実行環境（k3s / compose）の CI 上での起こし方が主題であり、#442（エッジ・実行基盤）と密結合する。#442 の成果に載せるべき |
| **NFR 性能試験の枠組み** | 既存 [#196](https://github.com/endazon/microservices-platform/issues/196) が担当。再実装後の受け入れゲートとして接続するのは各サービス完成後であり、フェーズ 0 で作っても実測対象が無い |
| **CPM バージョン直書き禁止の機械検査** | CLAUDE.md の「`.csproj` の `PackageReference` にバージョンを書かない」の機械化。単独で小さく関心も独立しており、本 PR の 3 点（写像・カバレッジ・DoD）に混ぜるとレビュー単位が散る |

いずれも #454 のチェックリストへ新 issue として追加し、#453 を親にする。

## 設計

### 1. 受け入れ基準 → テストの写像規約

計画書の受け入れ基準を、**テストの名前ではなくコメントの起点 ID で**突合可能にする。

```csharp
// FR-03, UC-01: ハイブリッド検索は語彙一致とベクトル類似の両方を返す
[Fact]
public async Task 検索は語彙一致とベクトル類似の両方を返す() { ... }
```

```ts
// SC-02: 検索結果一覧は 0 件のとき空状態を表示する
it('0 件のとき空状態を表示する', () => { ... })
```

**なぜ名前ではなくコメントか。** テスト名に ID を含める規約（`FR03_...`）は、日本語のテスト名（本リポジトリの
既存慣習）と両立せず、また ID が変わるたびにテスト名が変わって履歴の追跡が切れる。コメントなら
[`.claude/rules/traceability.md`](../../.claude/rules/traceability.md)「テスト: テスト名またはコメントに起点 ID を
残す」の既存規約にそのまま乗る。

**規約**:

- テストメソッド / `it` / `test` の**直前**のコメントに起点 ID を 1 つ以上書く（`FR-\d+` / `UC-\d+` / `SC-\d+` / `NFR`）。
- 複数 ID はカンマ区切り（`// FR-03, UC-01: ...`）。
- 他プロジェクトの ID は修飾する（`AST/FR-17`）。修飾付き ID は本リポジトリの突合対象から除外する。
- **起点 ID を持たないテストを禁止しない。** 基盤・回帰・検査器自身のテストは計画 ID に紐づかない。
  検査が見るのは「`docs/tests/` に仕様書がある FR/SC に、対応するテストが 1 つも無い」ことだけである。

### 2. 写像の機械検査（`scripts/check-test-traceability.js`）

外部依存ゼロの Node スクリプト。既存検査器の作法（`--self-test` ＋ `ci-annotate`）に揃える。

- `docs/tests/` から仕様書が存在する起点 ID を集める（ファイル名の `FR-xx` / `SC-xx` / `NFR-xx`）。
- `src/` のテストファイル（`*Tests.cs` / `*.test.ts(x)` / `*.spec.ts(x)`）から起点 ID コメントを集める。
- **仕様書があるのにテスト側に 1 件も ID が無い FR/SC を報告する。**

**判定方針は着手後の実測で変えた。** 当初は warn 開始（fail にすると全 PR が赤くなる懸念）を想定したが、
実際に走らせると **27/27 写像済み・未写像 0** であった。既存のテストが既に起点 ID コメントを持っていた
ためである。ゼロから始められるので、**warn ではなく最初から fail で強制する**。ただし「仕様書を先に書き、
テストは次の PR」という正当な段取りを塞がないよう、`scripts/test-traceability-allowlist.json` に
未写像を許す ID を明示できるようにする（#455 の baseline と同型の ratchet）。

| 状態 | 判定 |
| --- | --- |
| allowlist に無い未写像 | **fail**（写像の退行を止める） |
| allowlist にある未写像 | warn（残件として実行サマリに出す） |
| allowlist にあるのに写像済みになった | **fail**（減らし忘れを検出） |

### 3. バックエンドのカバレッジ床

**単一情報源を `src/coverage-floor.json` に置く**（フロントの `vitest.config.ts` に相当）。

- `ci.yml` の `build-and-test` が出力する Cobertura を集計し、床を下回れば **fail**。
- 集計は `scripts/check-coverage-floor.js`（外部依存ゼロ）。`reportgenerator` のインストールを要さない
  ため CI が速く、オフラインでも動く。各ファイルの被覆率を単純平均すると小さいファイルが多いときに
  実態より高く出るため、**全レポートの行数で加重**して集計する。
- **床の初期値は本 PR の CI 実行で得た実測値を入れる**（着手環境に .NET SDK が無く事前に測れない）。
  それまで `null` とし、集計と報告のみ行って判定しない。**推測値を置かない**——低すぎればゲートが
  無意味になり、高すぎれば初回から赤くなる。
- レポートが 1 件も無い場合は warn で素通りする（fail-open）。カバレッジと無関係な PR やローカル実行を
  赤くしないためである。

#### 実測値と床の確定（commit `8bfe639` の CI 実行）

`coverlet.collector` を MSP の 14 テストプロジェクトへ追加したのち、`build-and-test` が次を出力した。

```
[check-coverage-floor] レポート 14 件を集計: line 34.46%（18894/54826） / branch 17.62%（3154/17896）
```

レポート **14 件**は MSP のテストプロジェクト数と一致する（AST は `EXCLUDED_UNITS` で除外済み）。
これで「集計対象が MSP のみである」ことが件数として裏づけられた。

床は**実測を整数へ切り下げた `line 34` / `branch 17`** とする。実測そのもの（34.46 / 17.62）を置くと
計測ゆらぎ——統合テストが 1 件 skip される、被覆済みの死コードを消す等——で「成果物は正しいのに赤」に
なる（[IADR-0115](../adr/IADR-0115_impl-handoff-kit-as-single-source.md)）。フロントの
[`src/vitest.config.ts`](../../src/vitest.config.ts) も実測 lines≈83% に対して整数の床 78 を置いており、
**同じ作法に揃える**。切り上げ（34.46 → 35）は初回から fail するため行わない。

判定経路は実値で確認済み。実測（34.46 / 17.62）で違反 0、床を割る値（33.99 / 16.99）で違反 2 件。

**既知の限界（[#468](https://github.com/endazon/microservices-platform/issues/468) へ切り出し）**: 除外は
レポート**ファイルのパス**で判定するため、`Platform.Bff` が合成点として `ProjectReference` する
`AiStockTrading.Bff.Endpoints` の行が、`src/platform/` 配下のレポートの**中身**に混入する（実測 266 行・
すべて被覆済み）。除いた推定は `line 34.14%`（18628/54560）で、**床 34 は除去後も有効**（差 0.32pp）。
塞ぐには Cobertura の `<class filename>` で行を帰属させるパーサ改修が要るため分割した。

### 4. DoD の更新

`docs/DEFINITION_OF_DONE.md` に再実装期間の条件を追加する。

- 受け入れ基準に対応するテストを含むこと（起点 ID コメントで示す）
- カバレッジ床を下回らないこと（バックエンド・フロントとも）
- 不採用ライブラリの baseline を増やさないこと（#455 の ratchet）

## 受け入れ基準

- [ ] `docs/tests/TEST_STRATEGY.md` に写像規約・テスト種別・各ゲートの一覧がある
- [ ] `node scripts/check-test-traceability.js --self-test` が成功する
- [ ] `node scripts/check-test-traceability.js` が現状（27/27 写像済み）で成功し、未写像があれば fail する
- [ ] `node scripts/check-coverage-floor.js --self-test` が成功する
- [ ] `src/coverage-floor.json` があり、`ci.yml` が集計・報告し、床（`line 34` / `branch 17`）を下回れば fail させる
- [ ] `docs/DEFINITION_OF_DONE.md` が再実装方針を反映している
- [ ] `scripts.repo.test.js` に本 PR の検査器のテストが追加され `scripts.test.js` 全件が成功する
- [ ] `node scripts/check-doc-links.js` が破損リンク 0
- [ ] 契約テスト / E2E / 性能試験の後続 issue を起票し、#454 のチェックリストへ追加した

## テスト方針

| 受け入れ基準 | 検証手段 |
| --- | --- |
| 写像検査の判定（ID 抽出・未写像の検出・修飾 ID の除外） | `--self-test` ＋ `scripts.repo.test.js`（正例・負例） |
| カバレッジ集計（Cobertura の読み取り・床判定） | 同上（合成 XML フィクスチャで境界値: 床ちょうど / 床未満） |
| 実ファイルとの突合 | 実 `src/` を走査して現状違反 0 を確認するテスト（#455 と同型） |
| バックエンド床の実効性 | CI 実行（`8bfe639`）で実測値を得たうえで床を設定済み。実測（34.46 / 17.62）で違反 0、床を割る値（33.99 / 16.99）で違反 2 件を確認 |

## 計画書との差異

- 差異: なし。本作業は計画書の受け入れ基準を**テストへ写像する仕組み**を作るものであり、計画の解釈を変えない。

## 未決事項

1. ~~**バックエンドカバレッジ床の初期値**~~ → **解消**。`coverlet.collector` 追加後の CI 実行
   （`8bfe639`）で MSP のみの実測値 `line 34.46% / branch 17.62%`（レポート 14 件）を得て、床を
   `line 34` / `branch 17` に確定した（上記「実測値と床の確定」）。**初回に観測した 47.22% は AST の
   数字であり、床の根拠にしていない。**
2. **CPM バージョン直書き検査**（後続 issue #467）。本 PR では起票のみ行う。
3. **契約テストの方式**（後続 issue #465）。[IADR-0049](../adr/IADR-0049_composability-standards-phased-adoption.md) の
   繰延判断を見直すか否かを含め、独立した ADR が要る。
4. **合成点テスト経由で混入する AST 行の除外**（後続 issue #468）。実レポートを見てから設計する。
   レポートを見ずに Cobertura の属性の形を仮定して書くと、フィルタが何にもマッチせず「除外したつもりで
   素通り」になる——本 PR で一度踏んだ「AST の数字を MSP の実測値と取り違える」のと同型の失敗になる。
