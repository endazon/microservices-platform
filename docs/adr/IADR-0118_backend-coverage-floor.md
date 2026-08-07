---
title: IADR-0118 バックエンドのカバレッジ床 — 単一情報源・実測からの切り下げ・ratchet
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0034, IADR-0115, IADR-0116, IADR-0123]
author: Claude
created: 2026-08-03
updated: 2026-08-04
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
related_specs:
  - ../specs/20260803_issue-453_regression-test-foundation.md
  - ../specs/20260803_issue-474_backend-floor-iadr-and-0116-followup.md
  - ../specs/20260804_issue-468_coverage-ast-exclusion.md
  - ../tests/TEST_STRATEGY.md
---

# IADR-0118: バックエンドのカバレッジ床 — 単一情報源・実測からの切り下げ・ratchet

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。
> 計画に影響する決定は計画側へ環流する（`/plan-feedback`）。

- 状態: Accepted
- 日付: 2026-08-03
- 決定者: Claude（実装）。#453（PR #464・マージ済み）で実施済みの決定を事後に記録する

## 起点・関連

- 関連する計画書 ID（FR/UC/SC/ADR）: NFR（品質・保守性。再実装期間中の退行検知）
- 関連する実装 ADR: [IADR-0034](IADR-0034_frontend-coverage-gate.md)（フロントの同等ゲート。本 IADR は
  そのバックエンド版にあたる）／[IADR-0116](IADR-0116_reimplementation-branching-and-pr-policy.md)
  （再実装の進行規約。規約 6 の受け入れゲートに本床が入る）／
  [IADR-0115](IADR-0115_impl-handoff-kit-as-single-source.md)（impl-handoff-kit を足場の単一情報源とする同期規約）
- 関連する実装仕様書:
  [作業仕様書 20260803_issue-453](../specs/20260803_issue-453_regression-test-foundation.md)（実測値と床の確定）／
  [テスト戦略](../tests/TEST_STRATEGY.md)（ゲート一覧・検査対象ユニットの切り分け・床の置き方）
- 関連 issue: #453（親 #454 フェーズ 0。PR #464 でマージ済み）／#468（合成点経由の混入の除去）／
  本 IADR の起票は #474

## コンテキストと課題

フロントには [IADR-0034](IADR-0034_frontend-coverage-gate.md) が定めるカバレッジ ratchet があり、
[`src/vitest.config.ts`](../../src/vitest.config.ts) の `thresholds` を
[`frontend-tests.yml`](../../.github/workflows/frontend-tests.yml) が強制している。一方**バックエンドには
同等のゲートが無かった**。[`ci.yml`](../../.github/workflows/ci.yml) の `build-and-test` は
`dotnet test --collect:"XPlat Code Coverage"` を渡していたが、閾値強制はコメントアウトされた例のまま
放置されていた。

全面再実装（#454）は 11 サービスを作り直す。**テストが薄いまま実装が置き換わっても CI が緑のままになる**
のがこの穴であり、#453 の受け入れ観点「カバレッジ floor が再実装前の水準を下回ったままマージできない」は
まさにここを塞ぐことを求めていた。

さらに、床を武装する直前の実測で**より深い穴**が判明した。MSP（platform + knowledge）の 14 テスト
プロジェクトは**どれも `coverlet.collector` を参照しておらず、Cobertura が 1 件も出ていなかった**
（AST の 38 プロジェクトは全件参照していた）。`dotnet test --collect:"XPlat Code Coverage"` は
collector 参照が無いと何も出力しない。その結果、CI が拾っていた 38 件のレポートは**すべて AST のもの**
であり、初回に観測した `line 47.22% / branch 41.45%` は AST の数字だった。**あのまま床を置いていれば、
別プロジェクトの数字を platform / knowledge の基準として固定していた。**

決めるべきは次の 4 点である。

1. 床の値と検査器をどこに置き、どう集計するか（単一情報源と方式）
2. 床の初期値をどう決めるか（実測との関係）
3. どのユニットを集計対象にするか
4. レポートが 1 件も出ないときにどう振る舞うか（fail-open か fail-closed か）

#453 の PR #464 でこれらを決め実装したが、記録は作業仕様書と
[`src/coverage-floor.json`](../../src/coverage-floor.json) の `$comment` にしか残っていなかった。フロントの
同等ゲートが IADR を持つのに対し非対称であり、後続の各ドメイン issue が「なぜこの値・この方式なのか」を
辿れない。**本 IADR はその決定を正式に記録するものである**（決定内容そのものは既に実装済みで、本 IADR で
新たに変更する事項は無い）。

## 検討した選択肢

### 集計方式

| | A. Cobertura を自前で直接集計（採用） | B. `reportgenerator` を CI に導入して集計 | C. coverlet の `/p:Threshold=` で閾値を強制 |
| --- | --- | --- | --- |
| 追加依存 | **ゼロ**（Node 標準のみ） | dotnet tool のインストールが要る | ビルド引数のみ |
| CI 速度 | 速い | tool restore のぶん遅い | 速い |
| オフライン実行 | 可 | 不可（tool 取得が要る） | 可 |
| 単一情報源 | JSON 1 ファイル | JSON ＋ tool 設定 | **各テストプロジェクト / ci.yml に分散** |
| 全プロジェクト横断の合算 | 可（全レポートを合算） | 可 | **不可**（プロジェクト単位の判定になる） |
| 既存検査器との作法の一致 | 一致（`--self-test` ＋ `ci-annotate`） | — | — |

C は「サービスごとに閾値」になり、薄いサービスと厚いサービスが打ち消し合う横断の床を表現できない。
B は目的（床の強制）に対して導入コストが釣り合わない。

### 集計の重み付け

| | 行数で加重（採用） | ファイル単位の被覆率を単純平均 |
| --- | --- | --- |
| 小さいファイルが多いとき | 実態を表す | **実態より高く出る**（1 行のファイルが 100% として 1 票を持つ） |

### 床の初期値の置き方

| | 実測を整数へ**切り下げ**（採用） | 実測そのまま | 実測を切り上げ | 推測値（例 40%） |
| --- | --- | --- | --- | --- |
| 初回の判定 | 通る | 通る（が余裕ゼロ） | **初回から fail** | 不明 |
| 計測ゆらぎ耐性 | あり | **無い**（統合テスト 1 件の skip・被覆済み死コードの削除で赤） | — | — |
| ゲートとしての実効 | あり | あり | あり | 低すぎれば無意味・高すぎれば恒常的に赤 |
| フロントとの作法 | 一致（実測 lines≈83% に対し床 78） | 不一致 | 不一致 | 不一致 |

### レポート 0 件のときの振る舞い

| | fail-open（採用） | fail-closed |
| --- | --- | --- |
| カバレッジと無関係な PR / ローカル実行 | 緑のまま | **全部赤**（`.md` だけの PR も止まる） |
| 検査が無音で失効する危険 | **ある**（本 IADR の決定 5 で塞ぐ） | 無い |

## 決定

1. **単一情報源は [`src/coverage-floor.json`](../../src/coverage-floor.json)**、検査器は
   [`scripts/check-coverage-floor.js`](../../scripts/check-coverage-floor.js) とする。フロントが
   `src/vitest.config.ts` の `thresholds` を単一情報源にしているのと対をなす配置である。
   - 検査器は**外部依存ゼロ**（Node 標準のみ）。`dotnet test --collect:"XPlat Code Coverage"` が出す
     **Cobertura XML を直接読んで集計**し、`reportgenerator` 等のツール導入を要さない。CI が速く、
     オフラインでも動く。
   - 集計は `src/` 配下の `coverage.cobertura.xml` を全件走査し、**全ファイルの行数で加重**して合算する。
     ファイル単位の被覆率を単純平均すると、小さいファイルが多いときに実態より高く出るためである。
   - 判定は `ci.yml` の `build-and-test` から実行し、床未満なら **fail**。
   - 既存検査器の作法（`--self-test` ＋ `lib/ci-annotate`）に揃える。

2. **床の値は実測からの整数切り下げとする。初期値は `line 34` / `branch 17`。**
   - 根拠は #453 の CI 実行（commit `8bfe639`）で得た実測
     **line 34.46%（18894/54826） / branch 17.62%（3154/17896）**、**レポート 14 件**。
     14 件は MSP のテストプロジェクト数と一致し、「集計対象が MSP のみである」ことが件数で裏づけられる。
   - **推測値は置かない。** 低すぎればゲートが無意味になり、高すぎれば初回から赤くなる。実測が得られる
     までは `null` とし、集計と報告のみ行って判定しなかった。
   - **切り上げ（34.46 → 35）は初回から fail するため行わない。** 実測そのままも採らない。計測ゆらぎ
     （統合テストが 1 件 skip される、被覆済みの死コードを消す等）だけで「成果物は正しいのに赤」に
     なるためである。フロントの `src/vitest.config.ts` が実測 lines≈83% に対し整数の床 78 を置いて
     いるのと**同じ作法**である。
   - 判定経路は実値で確認済み。実測（34.46 / 17.62）で違反 0、床を割る値（33.99 / 16.99）で違反 2 件。

   > **［2026-08-04 追記］床の値は据え置き（`line 34` / `branch 17`）。ただし根拠は差し替わった（#468）。**
   > 上記の初期値の根拠（`8bfe639` の `line 34.46%（18894/54826）` / `branch 17.62%（3154/17896）`）は、
   > **AST の混入込み・二重記載込み**の値である
   > （[IADR-0123](IADR-0123_cobertura-class-attribution-and-line-dedup.md) 決定 3 で計数方式が変わった。
   > **旧値は新方式の厳密に 2 倍**——`18894 = 9447 × 2` / `54826 = 27413 × 2` / `3154 = 1577 × 2` /
   > `17896 = 8948 × 2`。すべての項が 2 倍で揃うことは、二重記載が一律に効いていた＝決定 3 の
   > 強い裏づけである）。新方式での実測は
   > **line 34.14%（9314/27280） / branch 17.26%（1536/8898）**、除外前は
   > `line 34.46%（9447/27413）` / `branch 17.62%（1577/8948）`、混入は **6 クラス / 133 行（全て被覆済み） /
   > 分岐 50（被覆 41）**である。
   > 測定条件: CI run 30886437108（run_number **1144**）/ job `build-and-test` / commit `594117a` /
   > Release 構成 / レポート **14 件** / submodule populate 済み。
   > 整数切り下げは `line 34` / `branch 17` で**現在値と同値**のため
   > [`src/coverage-floor.json`](../../src/coverage-floor.json) は変更していない（値の正は同ファイル）。
   > **余裕は薄い——line +0.14pt / branch +0.26pt しかない。** ratchet で引き上げる際はこの薄さを踏まえること。

   > **［2026-08-07 追記］床の値を置き直した（`line 34` → `line 33`。`branch` は `17` 据え置き）。**
   > 上記の薄さが実害になった——[PR #568](https://github.com/endazon/microservices-platform/pull/568) は
   > **EF マイグレーションを 1 本追加しただけ**で床を割った。
   > [#571](https://github.com/endazon/microservices-platform/issues/571) /
   > [IADR-0138](IADR-0138_coverage-exclude-generated-code.md) が**生成コード**
   > （`Migrations/` 配下・`*ModelSnapshot.cs`）を集計から落とし、新しい定義での実測に合わせて床を置き直した。
   > **これは ratchet の引き下げ（退行）ではなく、測定基準の変更に伴う置き直しである**
   > （[IADR-0123](IADR-0123_cobertura-class-attribution-and-line-dedup.md) 決定 7 が #468 で行ったのと
   > 同じ性質の作業。あちらは切り下げ結果が同値だったため据え置きになった）。**旧定義の 34 と新定義の 33 は
   > 分母・分子が違うため直接比較できない。**
   > 値が下がったのは、**生成コードが平均より厚く被覆されている**ためである——統合テストが起動時
   > `MigrateAsync()` を通ると migration の `Up()` と Designer の `BuildTargetModel()`、`ModelSnapshot` の
   > `BuildModel()` が実行される（`Down()` は実行されない）。#571 のローカル実測（Postgres / RabbitMQ を
   > 実際に起動して統合テストを 35/39 通した測定）で **生成コード 2310 行のうち 933 行が被覆**＝ 40.4% となり、
   > 全体（約 34%）を上回った。分子・分母の双方から同じものを抜いた結果として比率は下がる。
   > **branch を据え置くのは生成コードの分岐が 0 だからである**（除外前後で分岐率は同値。分岐の定義は
   > 変えていないため、決定 4 の追記が課した「定義変更は床の置き直しとセット」には該当しない）。
   > **床 33 は CI ログを直接読んだ実測値ではなく、CI が通ることで検証される下限である**——導出は
   > `(9314 − 933) / (27280 − 2310) = 33.56%`（上限側 `(9314 − 969) / (27280 − 2310) = 33.42%`）の整数切り下げ。
   > 測定条件と導出は [IADR-0138](IADR-0138_coverage-exclude-generated-code.md) 決定 5 と
   > [`src/coverage-floor.json`](../../src/coverage-floor.json) の `$comment` を参照（値の正は同 JSON）。
   > なお**分岐の分母は `condition-coverage` の合算**であり coverlet の `branches-valid`（除外前 9356）とは
   > 定義が異なる。**被覆数を据え置いたまま分母だけ coverlet 基準に置き換える試算**では
   > 除外前 `1577 ÷ 9356 = 16.86%`、床が判定に使う除外後の対でも `1536 ÷ (9356 − 50) = 16.51%` となり、
   > いずれも床 17 を下回る（**定義を変えれば分子も変わるため、これは「coverlet 定義での実際の分岐率」では
   > なく分母差の影響を測る試算である**）。したがって**分岐の定義変更は床の置き直しとセットでしか
   > 行えない**（[IADR-0123](IADR-0123_cobertura-class-attribution-and-line-dedup.md) 決定 4 の追記）。

3. **運用は ratchet とする**（床は上げるが下げない）。テストを増やしたら床を引き上げ、床を割る変更を
   CI で止める。床の引き下げは**退行**であり、行う場合は正当な理由を作業仕様書に記す（検査器の失敗
   メッセージにもそう出す）。各ドメイン issue の遵守事項は
   [テスト戦略](../tests/TEST_STRATEGY.md)「各ドメイン issue が守ること」に置く。

4. **`ai-stock-trading`（AST）は集計対象外とする**（`EXCLUDED_UNITS`）。
   - `ci.yml` の `build-and-test` は**全ユニットの `backend.slnx` を自動発見して test する**ため、
     除外しないと AST のカバレッジが合算される。合算は双方向に濁る——AST 側のテストが厚ければ
     platform / knowledge の実際の退行を薄めて隠し、逆に **AST の pin 更新だけで無関係な PR の床判定が
     動く**。`check-test-traceability.js` / `check-backend-libraries.js` の `EXCLUDED_UNITS` と同じ
     切り分けに揃える。
   - **既知の限界を明示する。** 除外は**レポートファイルのパス**で判定するため、`Platform.Bff` が
     BFF の合成点として `ProjectReference` する `AiStockTrading.Bff.Endpoints` の行が、
     `src/platform/` 配下のレポートの**中身**に混入する。混入量は **AST 由来 230〜266 行**（いずれも
     全て被覆済み。Release 構成での 2 度の計測が 266 行 / 230 行と割れたため確定値を採らず範囲で記録
     する）。除去後の推定は 230 行なら `line 34.19%`、266 行なら `line 34.14%` で、**いずれも床 34 を
     上回る**。したがって**床の値は混入の確定を待たずに有効**である。塞ぐには Cobertura の
     `<class filename>` で行を帰属させるパーサ改修が要り、独立した設計判断を伴うため
     [#468](https://github.com/endazon/microservices-platform/issues/468) へ切り出した。

   > **［2026-08-04 追記］決定 4 の「既知の限界」（合成点経由の混入）は
   > [IADR-0123](IADR-0123_cobertura-class-attribution-and-line-dedup.md) で対処した（#468）。**
   > `check-coverage-floor.js` を class 単位走査へ作り替え、各 `<class filename>` を `src/<unit>/` へ
   > 帰属させて集計対象外ユニットの行を落とす（除外集合は [IADR-0120](IADR-0120_excluded-units-from-gitmodules.md)
   > の単一情報源から導出。レポートファイルのパスによる除外は併用する）。
   > **CI 初回実走で成立を確認した。** 診断出力（run 30886437108 / run_number **1144** /
   > job `build-and-test` / commit `594117a` / Release / レポート 14 件 / submodule populate 済み）は
   > **未帰属 0 件**（クラス 2036 件すべて帰属）・**混入 6 クラス / 133 行（すべて被覆済み） / 分岐 50（被覆 41）**・
   > 除外前後で **27413 → 27280 行**。除外された 6 クラスは
   > `AssumptionsBffEndpoints` / `MonitorBffEndpoints` / `RiskControlsBffEndpoints` と各 `<ProxyAsync>d__2` で、
   > いずれも `<sources>` 結合で `ai-stock-trading` へ帰属した。
   > **上記の「AST 由来 230〜266 行」は旧計数方式による値である**——文書全体の `<line>` を正規表現で
   > 数える方式であり、`<methods>` 配下と class 直下の二重記載を含む。**2 つの値の関係は次のとおり
   > 分解できる**（旧記述は「割れた原因は二重記載」と読めたが、それは正しくない）。
   > - **二重記載は 266 も 230 も一律に 2 倍にした要因**である。
   > - **266 と 230 の差そのものはスコープ差**——全プロジェクト実行と `Platform.Bff.Tests` 単体実行の
   >   違いである（出典: [`docs/specs/20260803_issue-453_regression-test-foundation.md`](../specs/20260803_issue-453_regression-test-foundation.md)
   >   の「既知の限界」節。「レビューの 2 度の計測（全プロジェクト実行 266 行 / `Platform.Bff.Tests`
   >   単体実行 230 行）」と記録されている）。
   > - 実測が分解を支持する: **266 = 133 × 2**（全プロジェクト実行の新方式値 133 行）、
   >   **230 = 115 × 2**（単体実行の新方式値 115 行）。
   >   115 行は本 PR のレビューによる独立実測である——測定条件は **.NET SDK 10.0.302 /
   >   `Platform.Bff.Tests` 単体実行 / 本 PR の commit `594117a` 時点**。**ビルド構成はレビュー
   >   コメントに記載が無いため断定しない**（本追記の他の数値は CI の Release 構成）。
   > - したがって **230 行と CI の全体集計（133 行）はスコープが異なり直接比較できない**が、
   >   旧値としては 115 × 2 で説明がつく（宙吊りではない）。
   > 床の値 `line 34` / `branch 17` は据え置き（根拠の差し替えは決定 2 の追記を参照）。

5. **レポートが 1 件も無い場合は warn で素通りする（fail-open）。ただしその代償はテストで塞ぐ。**
   - fail-open にするのは、カバレッジと無関係な PR やローカル実行を赤くしないためである。
   - 代償は「**床が静かに無効化される**」こと——#453 の実測で現に踏んだ失敗である（collector 参照が
     無く 0 件だった）。よって warn の本文で「探索が空振りした」のか「除外で全部落ちた」のかを必ず
     切り分けて出す（検出件数 / 除外件数 / 先頭 5 件のパス）。原因不明の warn は、この検査が無いのと
     同じである。
   - さらに [`scripts/scripts.repo.test.js`](../../scripts/scripts.repo.test.js) に**退行防止テスト 2 本**を置く。
     - `coverage-floor.json` の床が `null` へ戻ることを検知する（床の無武装化を止める）
     - **全テストプロジェクトが `coverlet.collector` を参照している**ことを検知する（参照が外れると
       Cobertura が出ず、fail-open で床が緑のまま失効する）
   - 床が両指標とも未設定（`null`）のときは `notice` を出して判定しない。未計測（分母 0）の指標は
     「測れていない」を 100% と誤解させないため `null` とし、判定対象から外す。

6. **「成果物は正しいのに赤」を常態化させない段階ポリシー**を本床の設計原則とする。典拠は
   [`scripts/README.md`](../../scripts/README.md) の `check-permission-denials.js` 節（段階ポリシーの設計）と、
   その前段の失敗モード **planning#146**（読み取り系 git の拒否で差分の内容と無関係に毎回赤くなる）・
   **planning#160**（拒否報告が原因を隠し、許可済みのコマンドを「拒否された」と報告する）、および
   段階ポリシーを導入した **planning#161**（ラベルが読めてもなお拒否が残る実測）・
   **planning#162**（「1 件でも失敗」の常態化が拒否の赤を無視する学習を生み、検査の目的を壊す。
   許容件数とターン数比による段階判定へ改めた）である。既知の残件を明示（allowlist / baseline / floor）したうえで
   **新規の悪化だけを止める**、という本リポジトリ共通の作法に揃える。なお impl-handoff-kit の
   **同期規約そのもの**は [IADR-0115](IADR-0115_impl-handoff-kit-as-single-source.md) であり、本節の
   段階ポリシーの典拠とは別である。

## 理由

- **フロントとの非対称を残さないため**である。[IADR-0034](IADR-0034_frontend-coverage-gate.md) は
  「初期しきい値は低くてよい。ラチェットで段階的に引き上げる」という設計判断を既に済ませており、
  バックエンドで同じ判断を別の理屈で立て直す必要はない。本 IADR は同じ設計をバックエンドの計測系
  （Cobertura / coverlet）へ写したものである。
- **切り下げを採る理由は運用の持続性**にある。実測そのまま（34.46）を床にすると、被覆済みの死コードを
  1 つ消しただけで赤になる。この赤は「成果物が正しいのに出る赤」であり、繰り返されると赤を無視する
  学習が生まれて検査の目的を逆から壊す（`check-permission-denials.js` 節の整理と同型）。整数への
  切り下げは、この失敗モードを避けるために**必要最小限の余裕だけ**を残す。余裕を常態化させないのが
  決定 3（ratchet）である。
- **AST を除外する理由は「床が何の退行を止めるためのものか」**にある。本床の目的は #454 で
  platform / knowledge を作り直す間の退行を止めることであって、別プロジェクトの品質を測ることでは
  ない。合算した床は、止めたい退行を隠し、無関係な変更で動く。
- **fail-open を採りつつテストで塞ぐ理由**は、fail-closed の代償が大きすぎるためである。ドキュメント
  だけの PR まで赤くなると、迂回（検査のスキップ）を誘発する。代わりに「床が無音で失効する」経路
  （床の null 化・collector 参照の脱落）を**個別に名指しして fail させる**。これは fail-open の穴を
  塞ぐ最小の手当てであり、実際に踏んだ失敗そのものを固定している。

## 結果

- 良い影響:
  - 再実装期間中、バックエンドのテストが薄いまま実装が置き換わる経路が CI で止まる。#453 の受け入れ
    観点が機械的に担保される。
  - フロント（[IADR-0034](IADR-0034_frontend-coverage-gate.md)）とバックエンドで**同じ設計原則の
    カバレッジゲート**が揃い、各ドメイン issue が守るべきことが 1 つの規則で説明できる。
  - MSP のバックエンドが**そもそも計測されていなかった**という事実が可視化され、14 テスト
    プロジェクトへの `coverlet.collector` 追加として恒久的に解消した。
  - 床の根拠・値・限界が本 IADR に集約され、`$comment` と作業仕様書にしか無い状態が解消した。
- 悪い影響・トレードオフ:
  - **床の絶対水準は低い**（line 34 / branch 17）。品質の水準を保証するものではなく、あくまで**回帰
    防止の床**である。各ドメイン issue でテストと床を段階的に引き上げる前提に立つ。
  - ~~合成点経由で AST の行が混入する（旧計数方式で 230〜266 行）。~~
    [#468](https://github.com/endazon/microservices-platform/issues/468) /
    [IADR-0123](IADR-0123_cobertura-class-attribution-and-line-dedup.md) で**解消**（CI run 1144 /
    commit `594117a` で成立を確認。混入は新方式で 6 クラス・133 行。上記の［2026-08-04 追記］を参照）。
  - **分岐（branch）の分母は `condition-coverage` の合算**であり、coverlet の `branches-valid` とは
    定義が異なる（CI 実測: 8948 対 9356）。床 17 はこの方式での実測に基づくため、**分岐の定義を変える
    場合は床の置き直しとセットでしか行えない**。他ツールの分岐率と本床の値は直接比較できない。
  - fail-open のため、想定外の経路でレポートが出なくなれば床は緑のまま失効しうる。既知の 2 経路は
    テストで固定したが、網羅ではない。warn の本文を読む運用が要る。
  - 床の値を書いた箇所が複数ある（`src/coverage-floor.json` / `docs/tests/TEST_STRATEGY.md` /
    `docs/DEFINITION_OF_DONE.md` / 本 IADR）。**値の正は `src/coverage-floor.json`**（機械が読む単一
    情報源）であり、文書側は引き上げのたびに追随が要る。
- フォローアップ:
  1. ~~[#468](https://github.com/endazon/microservices-platform/issues/468) で合成点経由の混入を除去し、
     実レポートで確定値を測り直す。床を置き直す。~~ **完了**（#468 /
     [IADR-0123](IADR-0123_cobertura-class-attribution-and-line-dedup.md)。CI run 1144 の実測
     `line 34.14%` / `branch 17.26%` の切り下げが現在値と同値のため、床は据え置きで根拠のみ差し替えた。
     決定 2 の［2026-08-04 追記］を参照）。**次に床を引き上げる際は余裕の薄さ（line +0.14pt /
     branch +0.26pt）を踏まえること。**
  2. 各ドメイン issue（#438〜#451）がテストを追加したら **床を引き上げる**（ratchet）。
     **［2026-08-07 追記］[#571](https://github.com/endazon/microservices-platform/issues/571) /
     [IADR-0138](IADR-0138_coverage-exclude-generated-code.md) 以降は EF の生成コードが集計から外れるため、
     マイグレーションの増減で床が動かなくなった。** ただし **source generator の出力（`obj/` 配下）は
     集計に残る**（175 クラス / 3866 行 / 分岐 3424 = 分岐分母の 38%）ので、**引き上げ幅がテストの増分
     だけを反映するわけではない**（XML doc コメントの増減でも動く）。扱いは **#574** で決める。
  3. [IADR-0116](IADR-0116_reimplementation-branching-and-pr-policy.md) 規約 6 の受け入れゲートに
     本床の具体値を記載した（#474 で追記済み）。床を引き上げた際は同規約の記載も追随させる。

## 関連

- Supersedes: なし
- Superseded by: なし
- 対をなす決定: [IADR-0034](IADR-0034_frontend-coverage-gate.md)（フロントのカバレッジゲート）
