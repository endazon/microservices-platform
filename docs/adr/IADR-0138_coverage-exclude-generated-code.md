---
title: IADR-0138 カバレッジ床は生成コード（EF の Migrations / ModelSnapshot）を集計から落とし、床を置き直す
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0034, IADR-0115, IADR-0118, IADR-0120, IADR-0123]
author: Claude
created: 2026-08-07
updated: 2026-08-15
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
related_specs:
  - ../specs/20260807_issue-571_coverage-exclude-generated.md
  - ../specs/20260804_issue-468_coverage-ast-exclusion.md
  - ../tests/TEST_STRATEGY.md
---

# IADR-0138: カバレッジ床は生成コード（EF の Migrations / ModelSnapshot）を集計から落とし、床を置き直す

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。
> 計画に影響する決定は計画側へ環流する（`/plan-feedback`）。

- 状態: Accepted
- 日付: 2026-08-07
- 決定者: Claude（実装）

## 起点・関連

- 関連する計画書 ID（FR/UC/SC/ADR）: NFR（品質・保守性。再実装期間中の退行検知の精度）
- 関連する実装 ADR:
  - [IADR-0118](IADR-0118_backend-coverage-floor.md)（バックエンドのカバレッジ床）。本決定は同 IADR
    **決定 1 の集計方式を詳細化**し、**決定 2 の床の値を置き直す**。決定そのものを覆さないため
    `Supersedes` ではなく**補完**である（IADR-0118 は Accepted のまま）。
  - [IADR-0123](IADR-0123_cobertura-class-attribution-and-line-dedup.md)（`<class filename>` による
    行のユニット帰属と二重記載の扱い）。本決定は**同じ層**（class 単位の走査と `filename` の解釈）で
    除外を 1 つ足す。`filename` の多段解釈（決定 2）と class 直下計数（決定 3）はそのまま用いる。
  - [IADR-0120](IADR-0120_excluded-units-from-gitmodules.md)（検査対象外ユニットの単一情報源）。
    本決定は除外**ユニット集合**を変えない。
  - [IADR-0034](IADR-0034_frontend-coverage-gate.md)（フロントのカバレッジ ratchet。対をなすゲート）
  - [IADR-0115](IADR-0115_impl-handoff-kit-as-single-source.md)（キット同期規約。対象スクリプトは
    **固有デルタ種 3**＝本リポにしか存在しないスクリプト）
- 関連する実装仕様書:
  [20260807_issue-571](../specs/20260807_issue-571_coverage-exclude-generated.md)
- 関連 issue: [#571](https://github.com/endazon/microservices-platform/issues/571)（本決定の起点）。
  発端は [PR #568](https://github.com/endazon/microservices-platform/pull/568)（EF マイグレーションを
  1 本追加しただけで `Check backend coverage floor` が fail した）。

## コンテキストと課題

[`scripts/check-coverage-floor.js`](../../scripts/check-coverage-floor.js) は Cobertura を直接読み、
行/分岐で加重した被覆率を [`src/coverage-floor.json`](../../src/coverage-floor.json) の床と比べる
（IADR-0118 決定 1）。集計対象からは AST（`ai-stock-trading`）の行を落とす（IADR-0123 決定 1）。
しかし **EF Core が生成するコードは集計に入ったままだった**。

これは 2 つの意味で床の目的（「#454 で platform / knowledge を作り直す間の**手書きコードの**退行を
止める」）とずれる。

1. **人が書いていないコードの被覆率が床判定を動かす。** `Migrations/` 配下（migration 本体・
   `*.Designer.cs`）と `*ModelSnapshot.cs` は `dotnet ef` の出力であり、テストで被覆する対象ではない。
2. **被覆され方が「テストの厚さ」ではなく「統合テストが起動したかどうか」で決まる。** 後述の実測の
   とおり、統合テストが起動時 `MigrateAsync()` を通ると migration の `Up()` と Designer の
   `BuildTargetModel()`（および `ModelSnapshot` の `BuildModel()`）が実行され、**生成コードの被覆率が
   全体平均を上回る**。逆に統合テストの無いサービスのマイグレーションは 0% で入る。

実害は既に出ている。**PR #568 はマイグレーションを 1 本追加しただけで床を割った。** 床の余裕は
IADR-0123 の追記どおり **+0.14pt** しかなく、被覆しようのない生成コードが 150 行増えれば割れる。

決めるべきは次の 3 点である。(1) 何を生成コードとみなし、どう判定するか、(2) 除外を集計のどの層で
効かせるか、(3) 定義が変わるため床をどう置き直すか。

### 着手時の実測（判定パターンを決めるための一次情報）

**形を仮定して書くと、フィルタが何にもマッチせず「除外したつもりで素通り」になる**（IADR-0123 が
同じ失敗を名指ししている）。よって実レポートの `<class filename>` を先に見た。develop（`3804511`）
での実測（Release 構成 / レポート 14 件）は 2 形あった。

| `<sources>` | `<class filename>` の実例 |
| --- | --- |
| `/w/src/` | `knowledge/backend/Services/WikiService/src/WikiService.Api/Migrations/20260626150858_InitialCreate.cs` |
| `/w/src/platform/backend/` | `Services/AuthorizationService/src/AuthorizationService.Api/Migrations/AuthorizationDbContextModelSnapshot.cs` |

いずれも `Migrations/` を**区切り付きの一区画**として含む。`*.Designer.cs` も同ディレクトリに出るため、
この 1 規則で 3 種（本体 / Designer / ModelSnapshot）すべてに当たる。
**`Migrations/` の外にある `*ModelSnapshot.cs` は 0 件**だった（`git ls-files` と実レポートの双方で確認）。

生成コードの量（develop・AST 除外後）は **2310 行 / 45 クラス / 分岐 0** である。

## 検討した選択肢

### 何を「生成コード」とみなすか

| | A. `Migrations/` 配下 ＋ `*ModelSnapshot.cs`（採用） | B. `Migrations/` 配下のみ | C. `Down()` / `BuildModel()` など**実行され得ないメソッドだけ**除外 | D. `[GeneratedCode]` 属性で判定 |
| --- | --- | --- | --- | --- |
| 実測での取りこぼし | 無し | 現時点で無し（出力先を変えると割れる） | — | — |
| 説明可能性 | 「EF が生成するファイル」 | 同左 | **EF 内部の呼び出し経路に依存**（`Migrate()` が `TargetModel` を触るか等） | 「生成物一般」 |
| 実装コスト | パス規則 2 本 | 1 本 | メソッド名の対応表＋EF の版追随 | **Cobertura に属性情報が無く不可** |
| 壊れ方 | 出力先変更 → 除外 0 行（notice で出る） | 同左 | **EF の実装変更で静かにずれる** | — |

C は「被覆され得ない行だけ落とす」ため床の値を下げずに済むが、**EF の内部実装に判定を結びつける**。
`Migrate()` が `BuildTargetModel()` を呼ぶかは EF の版に依存し、変われば黙ってずれる。生成コードを
「人が書いていないから測らない」と定義するほうが、規則としても壊れ方としても素直である。
D は Cobertura に属性が出ないため実装できない。

### 除外を効かせる層

| | A. `<class filename>` の帰属と同じ層（採用） | B. coverlet の `ExcludeByFile` を各 csproj に足す | C. `reportgenerator` の filter |
| --- | --- | --- | --- |
| 設定の所在 | 検査器 1 箇所 | **テストプロジェクト 14 箇所に分散**（新規サービスで漏れる） | 追加ツール（IADR-0118 で棄却済み） |
| 既存除外との一貫性 | 同じ `filename` を使う（IADR-0123 決定 2） | 別の仕組みが 2 つ並ぶ | — |
| 除外量の可視化 | 診断に出せる | 出ない（消えた行は数えられない） | — |
| 外部依存 | ゼロ | ゼロ | 増える |

B は一見「正しい層」だが、**除外された行はレポートから消える**ため「何行落としたか」を CI ログで
確かめられない。IADR-0123 決定 5 が「無音の失敗を作らない」ために選んだ設計と逆行する。

### 床の置き直し

| | A. 実測からの整数切り下げ（採用） | B. 床を据え置く（34 のまま） | C. 生成コードの被覆分を足し戻して補正 |
| --- | --- | --- | --- |
| CI の初回判定 | 通る | **落ちる**（後述の実測: 新定義での実測は約 33.5%） | 通る |
| IADR-0118 決定 2 の作法 | 一致 | — | 不一致（床が実測から導けなくなる） |
| 意味の一貫性 | 新定義の実測から導いた床 | 旧定義の床を新定義へ流用 | 定義と床がねじれる |

## 決定

1. **生成コードは集計から落とす。** 対象は `<class filename>` が
   （a）`Migrations/` を区切り付きの一区画として含む、または（b）`*ModelSnapshot.cs` で終わる、
   クラスである。行・被覆行・分岐のすべてを落とす。
   - 判定に使うのは **[IADR-0123](IADR-0123_cobertura-class-attribution-and-line-dedup.md) 決定 2 が
     解決した経路**（`filename` そのもの、または `<sources>` と結合した値）である。帰属できなかった
     クラスは `filename` そのもので判定する。**同じ層で同じ値を使う**——ここを別扱いにすると、
     レポートによって当たったり当たらなかったりする。
   - 区切り付きで見るのは誤爆を避けるためである（`MigrationsHelper.cs` / `MyMigrations/` /
     `ModelSnapshotBuilder.cs` は生成物ではなく、落としてはならない）。
   - **集計対象外ユニット（IADR-0120）の除外を先に通す。** AST 由来の行を「生成コード」として
     二重計上させないためであり、IADR-0123 が記録した混入行数（AST 由来 133 行）の意味を保つ。

2. **除外量は毎回診断へ出す**（IADR-0123 決定 6 と同じ作法）。既定出力に
   「落としたクラス数・行数・被覆行数・分岐」「生成コードを戻したときの実測値」「ユニット別の内訳」
   「filename の例」を出し、`$GITHUB_STEP_SUMMARY` にも載せる。
   ユニット別の行数の行にも `うち生成 n 行（被覆 m）を除外` を添える。

3. **生成コードが 0 行だったときは notice を出す**（fail でも warn でもない）。
   EF の出力先を変えれば正常に 0 件になり得るため fail にはしない。しかし 0 件は
   「フィルタが素通りしている」状態と見分けがつかず、**放置すると床が静かに旧定義へ戻る**
   （＝マイグレーション 1 本で床判定が動く状態へ逆戻りする）。IADR-0118 決定 6 の段階ポリシーに従い
   notice で毎回可視化する。

4. **床を置き直す: `line 34` → `line 33`。`branch` は `17` のまま据え置く。**

   > **［2026-08-15 追記 / #574］本決定 4 の値は
   > [IADR-0195](IADR-0195_coverage-exclude-source-generator-output.md) 決定 3 が置き直した
   > （`line 33` → `39` / `branch 17` → `27`）。以下の値は当時の記録として残す。**
   > **とくに「branch を据え置くのは生成コードの分岐が 0 だから」は EF の生成コードについての実測であり、
   > source generator の出力には当たらない**——そちらは**分岐 3970**（被覆 249）を持つ。
   > **決定 1 の射程も IADR-0195 決定 1 が拡張した**（規則 2 本 → 3 本。`obj/` 配下を足した。
   > 既存の 2 規則は変えていない）。**決定 2・3・5 は有効なままである。**
   - **これは ratchet の引き下げ（退行）ではなく、測定基準の変更に伴う置き直しである**
     （IADR-0123 決定 7 が #468 で行ったのと同じ性質の作業。あちらは値が同値だったため据え置きに
     なったが、今回は下がる）。
   - **branch を据え置くのは、生成コードの分岐が 0 だからである。** 実測（develop）で生成コードの
     `condition-coverage` 分母は **0**、除外前後とも分岐率は同値だった。分岐の定義は変えていない
     （IADR-0123 決定 4 の追記が課した「定義変更は床の置き直しとセット」に該当しない）。
   - 値の正は [`src/coverage-floor.json`](../../src/coverage-floor.json) であり、導出は同ファイルの
     `$comment` に測定条件つきで記録した。

5. **床 33 は「CI ログを直接読んだ実測値」ではなく「CI が通ることで検証される下限」である。**
   本作業の環境から CI の実測値を読む手段が無い（`notice` はチェックのアノテーションに現れず、
   ジョブログの署名 URL はプロキシに拒否される）。したがって次のとおり導出した。**この但し書きを
   省かない**——測定条件のない数値は再現できず、後から根拠を検証できなくなる。

   | 項 | 値 | 出所 |
   | --- | --- | --- |
   | 基準（旧定義の CI 実測） | `line 34.14%（9314/27280）` | IADR-0123 の CI run 30886437108（run_number 1144）/ job `build-and-test` / commit `594117a` / Release / レポート 14 件 |
   | 生成コードの行数 | **2310 行**（45 クラス・分岐 0） | 本作業のローカル実測（develop `3804511` / Release / レポート 14 件）。`594117a..HEAD` に `*/Migrations/*` の変更が無いことを `git log` で確認済み |
   | 生成コードのうち CI で被覆される行 | **933〜969 行** | 933 は本作業のローカル実測（後述）。969 は同レポートの生成行数＝上限 |
   | 導出 | `(9314 − 933) / (27280 − 2310) = 33.56%` / 上限側 `(9314 − 969) / (27280 − 2310) = 33.42%` | — |
   | 床 | **33**（整数へ切り下げ。余裕 0.42pt 以上） | IADR-0118 決定 2 の作法 |

## 理由

- **「何を測っているか」を揃えるため**である。床の目的は手書きコードの試験の厚さを守ることであって、
  `dotnet ef` の出力量を測ることではない。生成コードを含めた比率は、**マイグレーションを足す/消す
  という設計上まったく無関係な操作で動く**。PR #568 はその実例である。
- **床の値が下がるのは、生成コードが平均より厚く被覆されているからである。** これは直感に反するが
  実測で確定した（下記）。**下がったこと自体は手書きコードの品質低下を意味しない**——分子・分母の
  両方から同じものを抜いた結果である。以後 **EF マイグレーションの増減では床判定が動かなくなる**。

  > **ただし「床がテストの厚さだけを表すようになる」とまでは言えない。** 当初そう書いていたが、
  > **source generator の出力（`obj/` 配下・175 クラス / 3866 行 / 分岐 3424）は集計に残る**ため、
  > XML doc コメントの増減でも床は動く。§結果 の「検出しないこと」を参照。**#574 で扱う。**
- **判定パターンを実レポートから決めた理由**は IADR-0123 と同じで、決め打ちが外れたときの壊れ方が
  「無音の素通り」だからである。あわせて決定 3 の notice でその状態を可視化する。

### 実測（決定 4・5 の根拠）

**生成コードが CI で被覆されることは推測ではなく実測である。** 作業環境では Docker Hub からの
イメージ取得が組織のエグレスポリシーで拒否される（`production.cloudfront.docker.com` が 403）ため
Testcontainers は使えない。代わりに **SDK コンテナ内へ PostgreSQL と RabbitMQ を導入して起動し、
統合テストを実走**させた（fixture の接続先だけを環境変数で差し替える一時パッチを当て、計測後に戻した）。

測定条件: develop `3804511` / .NET SDK 10.0（コンテナ）/ Release 構成 / レポート 14 件 /
統合テスト **35/39 成功**（MinIO を用意できず 4 件失敗。CI は 39/39）。

| 観測点 | 統合テストを走らせない（既定のローカル） | 統合テストを走らせた（CI 相当） |
| --- | --- | --- |
| 生成コード（行 / 被覆） | 2310 / **0** | 2310 / **933** |
| 生成コードを含む実測 | `line 26.38%（7223/27384）` | `line 33.65%（9214/27384）` |
| 生成コードを除いた実測 | `line 28.81%（7223/25074）` | **`line 33.03%（8281/25074）`** |
| 分岐（除外前 → 除外後） | `15.77%` → `15.77%` | `17.04%` → `17.04%` |

被覆されるのは `Up()` と `BuildTargetModel()` と `BuildModel()` であり、`Down()` は被覆されない
（`AuthorizationService` の内訳実測: `Up 56/56` / `BuildTargetModel 154/154` / `BuildModel 83/83` /
`Down 0/18`）。経路も実測で裏づけた——`WebApplicationFactory` は `builder.Build()` の**後**の
起動処理まで実行する（`FeedbackService.Api` の `Program.cs` で
`if (db.Database.IsRelational())` が `hits=3`、その内側の `MigrateAsync()` が `hits=0`〔InMemory の
ため〕として観測できる）。**したがってリレーショナル接続を持つ統合テストでは `MigrateAsync()` が
実際に走る。**

## 結果

- 良い影響:
  - **マイグレーションの追加・削除が床判定を動かさなくなる。** 変異試験で確認済み——生成コードを
    154 行（0 被覆）追加しても除外後の実測値は `33.03%（8281/25074）` から**まったく動かない**。
    除外を無効化すると `33.65% → 33.46%` と下がる（PR #568 の失敗モードそのもの）。
  - 床が「手書きコードの試験の厚さ」だけを表すようになり、ratchet の引き上げが意味を持つ。
  - 除外量と除外前後の値が CI ログ・実行サマリで毎回読める。
- 悪い影響・トレードオフ:
  - **床の数値が 34 → 33 へ下がる。** 表面上は緩和に見える。**旧定義の床と新定義の床は比較できない**
    （分母・分子が違う）ことを、値を引用するすべての箇所に明記する必要がある。
  - **新定義での実測 CI 値は本 PR の CI 実走まで確定しない。** 床 33 は導出値であり、CI が通ることで
    初めて検証される（決定 5）。**乖離した場合は実測を読んで置き直す**（それも本 IADR の作法である）。
  - 生成コードのフィルタが壊れても **fail にはならない**（notice のみ）。決定 3 のとおり意図した設計だが、
    notice を読む運用が要る。
  - **検出しないこと**（明示する）:
    - **マイグレーションそのものの正しさ**——スキーマが実際に適用できるかは統合テストの責務であり、
      床は関与しない。除外後は migration の被覆率が「良く」も「悪く」も見えなくなる。
    - **`Migrations/` に手書きコードを置いた場合**——黙って集計から落ちる。EF の慣例上そこには
      生成物しか置かないという前提に依存する。
    - **EF 以外の生成物**（source generator の出力）——**集計に残る。本決定の対象外である。**
      当初この項に「develop の実レポートに 0 件のため規則を作らない」と書いていたが、**それは誤りだった**。
      `git ls-files` で数えた結果であり、**source generator の出力は `obj/` 配下に出るため gitignore で
      見えなかった**（IADR-0123 が名指しした「形を仮定して書くと素通りする」型を、本 ADR 自身が踏んだ）。
      **実レポートで数え直すと 0 件ではない。**

      ```
      EF 除外後の集計に残る source generator 出力:
        175 クラス / 3866 行（被覆 176 = 4.6%） / 分岐 3424（被覆 65 = 1.9%）
        → 行の 15.4%、分岐分母の 38% を占める
        内訳: OpenApiXmlCommentSupport.generated.cs 160 / RegexGenerator.g.cs 15
      ```

      **したがって「生成コードの増減で床が動かない」と言えるのは EF の `Migrations/` についてだけである。**
      エンドポイントへ XML doc コメントを 1 つ足せば `OpenApiXmlCommentSupport.generated.cs` が再生成され、
      **PR #568 と同じ失敗モードが起こり得る。** 扱いを決めるには床の再導出が要るため本決定には含めず、
      **フォローアップ issue へ切り出した**（#574）。

      > **［2026-08-15 追記 / #574］解消済み。**
      > [IADR-0195](IADR-0195_coverage-exclude-source-generator-output.md) が `obj/` 配下を集計から
      > 落とし、床を置き直した。**上記の懸念（XML doc コメント 1 つで床が動く）は変異試験で実証された**
      > ——生成コードを 154 行 / 308 分岐だけ足すと、旧定義の分岐率は `19.3% → 18.72%` と **0.58pt** 動いた。
      > 新定義では実測値がまったく動かない。なお上記の実測（175 クラス / 3866 行 / 分岐 3424）は
      > 起票時（develop `3804511` 相当）の値であり、`1d7edce` での再実測は
      > **197 クラス / 4740 行 / 分岐 3970（分岐分母の 40.1%）**である。**コミットが違うため両方正しい。**
    - **フロントのカバレッジ**（[IADR-0034](IADR-0034_frontend-coverage-gate.md) / `src/vitest.config.ts`）
      ——本決定はバックエンドの床にのみ効く。orval 生成物の扱いは別の決定である。
- フォローアップ:
  1. **本 PR の CI 実走で新定義の実測値を読み、必要なら床を置き直す**（決定 5）。実測が導出値
     （33.42〜33.56%）から大きく外れた場合は、生成コードの被覆量の見積り（933〜969 行）を疑う。
  2. 各ドメイン issue がテストを追加したら床を引き上げる（ratchet。IADR-0118 決定 3）。
     **以後は生成コードの増減で床が動かないため、引き上げ幅は素直にテストの増分を反映する。**
  3. 床の値を書いた文書（[`docs/tests/TEST_STRATEGY.md`](../tests/TEST_STRATEGY.md) /
     [IADR-0116](IADR-0116_reimplementation-branching-and-pr-policy.md) 規約 6 /
     [IADR-0118](IADR-0118_backend-coverage-floor.md) 決定 2）は本 PR で追随済み。値の正は
     [`src/coverage-floor.json`](../../src/coverage-floor.json)。

## 関連

- Supersedes: なし（[IADR-0118](IADR-0118_backend-coverage-floor.md) 決定 1・2 と
  [IADR-0123](IADR-0123_cobertura-class-attribution-and-line-dedup.md) 決定 1 を**補完**する。
  いずれも Accepted のまま）
- Superseded by: **[IADR-0195](IADR-0195_coverage-exclude-source-generator-output.md)（決定 4 のみ。
  床の値を `line 33` / `branch 17` → `line 39` / `branch 27` へ置き直した）**。
  決定 1 は同 IADR が**射程を拡張**（`obj/` 配下を足す。既存の 2 規則は不変）、
  **決定 2・3・5 は有効なまま**であり、本 IADR は Accepted のまま残る。
- 実装: [`scripts/check-coverage-floor.js`](../../scripts/check-coverage-floor.js)（`--self-test` 付き）／
  [`src/coverage-floor.json`](../../src/coverage-floor.json)（床の値の単一情報源）
