---
title: 作業仕様書 — source generator 出力をカバレッジ集計から落とし、床を置き直す（#574）
type: spec
status: done
related_ids:
  - NFR
  - IADR-0118
  - IADR-0123
  - IADR-0138
  - IADR-0195
author: claude
created: 2026-08-15
updated: 2026-08-15
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
related_specs:
  - "./20260807_issue-571_coverage-exclude-generated.md"
  - "./20260804_issue-468_coverage-ast-exclusion.md"
  - "../../docs/tests/TEST_STRATEGY.md"
---

# 作業仕様書: source generator 出力をカバレッジ集計から落とし、床を置き直す（#574）

## 起点

- **NFR**（品質・保守性。再実装期間中の退行検知の精度。**当たる `NFR-xx` が無い＝場合 ②** なので無採番・環流しない）
- 起点 issue: **#574**（発端は #571 / PR #573 のマージ前監査が [IADR-0138](../adr/IADR-0138_coverage-exclude-generated-code.md) の記述の誤りを見つけたこと）
- 制約: [IADR-0118](../adr/IADR-0118_backend-coverage-floor.md)（床）／ [IADR-0123](../adr/IADR-0123_cobertura-class-attribution-and-line-dedup.md)（集計方式・「形を仮定しない」）／ [IADR-0138](../adr/IADR-0138_coverage-exclude-generated-code.md)（EF の生成コード除外。**本作業はその射程外を扱い、決定 1 と決定 4 を改定する**）

> **★ 値の基準時点は develop `1d7edce` / .NET SDK `10.0.400` / Release 構成 / レポート **14 件** / 統合テスト **43/43 成功**（2026-08-15 実測）である。**

## ★★ 測定環境について —— 前回できなかったことが今回できた

**[IADR-0138](../adr/IADR-0138_coverage-exclude-generated-code.md) 決定 5 は「CI の実測値を読む手段が無い」ため床 33 を*導出*した。**
**本作業では CI と同じ手順をローカルで完走できたため、床は導出ではなく実測から置く。**

| | #571 のとき | **本作業** |
| --- | --- | --- |
| .NET SDK | コンテナ内に手当て | **`dotnet-install.sh` を proxy 経由で取得し 10.0.400 を導入** |
| Postgres / RabbitMQ | **SDK コンテナへ直接導入**（fixture の接続先を一時パッチ） | **`dockerd` を起動し Testcontainers をそのまま使用**（パッチ不要） |
| MinIO | **用意できず 4 件失敗**（35/39） | **イメージ取得に成功。43/43 成功** |
| 実測の性格 | **導出値**（CI が通ることで検証される下限） | **実測値**（CI と同じ手順・同じレポート件数） |

> **★ ただしこの環境はセッション限りで消える。** よって**測定条件を必ず併記する**（`src/coverage-floor.json` の `$comment` が既に採っている作法）。
> **「環境が常に用意できる」ことを前提にした記述は書かない。**

## ★★ 母集合 —— 実測で引いた

**規則 1（誤りの側から引く）・規則 2（形を列挙）・規則 3（拡張子で絞らない）・規則 5（軸を 1 本で終わらせない）に従い、`git grep` で全数を引いた。**
除外は `planning/`（pin のみ）・`src/ai-stock-trading`（別リポ）・`CHANGELOG.md`（生成物）。

### 軸 a: **床の値（`line 33` / `branch 17`）を書いた live な文書**

検索語: `line 33` / `床 33` / `branch 17` / `"line": 33` / `"branch": 17`

| # | ファイル | 追随の中身 |
| --- | --- | --- |
| 1 | **`src/coverage-floor.json`** | **値の正本**。`backend.line` / `backend.branch` と `$comment` の導出 |
| 2 | `docs/adr/IADR-0116_reimplementation-branching-and-pr-policy.md` L110 | 規約 6 の表（必須チェックの一覧） |
| 3 | `docs/adr/IADR-0118_backend-coverage-floor.md` 決定 2 ・ §結果 | 追記で置き直しを記録 |
| 4 | `docs/tests/TEST_STRATEGY.md` L67 ・ L237 | ゲート一覧の床の値 ＋ 決定 5 の但し書き |
| 5 | `docs/adr/README.md` | 索引（IADR-0118 / IADR-0138 の行 ＋ **新 IADR の行**） |

### 軸 b: **「生成コード＝EF の `Migrations/` だけ」と書いた live な文書**

検索語: `ModelSnapshot` / `生成コード` / `source generator` / `分岐分母の 38%`

| # | ファイル | 追随の中身 |
| --- | --- | --- |
| 6 | **`scripts/check-coverage-floor.js`** | 判定規則・冒頭の方式説明・診断文・`--self-test` |
| 7 | `docs/adr/IADR-0138_coverage-exclude-generated-code.md` | **改定される当の決定**。日付つき追記で射程の拡張を明記 |
| 8 | `docs/adr/IADR-0123_cobertura-class-attribution-and-line-dedup.md` L224 追記 | 床の置き直しの記録（決定 7 の系列） |

### 軸 c: **新規に作るもの**

| # | 成果物 |
| --- | --- |
| 9 | **`docs/adr/IADR-0195_coverage-exclude-source-generator-output.md`**（新規。IADR-0138 決定 1・4 を改定） |
| 10 | **本作業仕様書** |
| 11 | `scripts/scripts.repo.test.js` の退行防止テスト（後述の変異試験を恒久化） |

### 除外したものと理由

| 対象 | 理由 |
| --- | --- |
| `docs/specs/20260807_issue-571_coverage-exclude-generated.md` | **確定済み（マージ済み PR の記録）。書き換えない**（`.claude/rules/traceability.md`「母集合」の但し書き） |
| フロントの「生成コード」（orval / Lingui。`src/*/frontend/**` ・ `docs/specs/20260805_issue-519_*` 等） | **別のゲートの管轄**（[IADR-0034](../adr/IADR-0034_frontend-coverage-gate.md) / `src/vitest.config.ts` の thresholds）。本決定はバックエンドの床にのみ効く |
| `src/**/Migrations/*ModelSnapshot.cs`（7 件） | **生成物の実体**そのもの。文書ではない |
| `scripts/check-contract-schema.js` の `obj/ 配下` | **契約スキーマの抽出元**を指す別文脈。カバレッジと無関係 |
| `docs/DEFINITION_OF_DONE.md` L49-51 | **床の値を書いていない**（「値の正は `coverage-floor.json`」と参照しているだけ）。追随不要 |

### 規則 8 で引き直すもの（是正の途中で新たに誤りになる自分の記述）

| 対象 | 判定 | 対応 |
| --- | --- | --- |
| **新しい床の値 `39` / `27`** | 走査して既存の記述と衝突しないか | 置き換え後に `line 39` / `branch 27` で全走査する |
| **「分岐分母の 38%」**（#574 起票時の値） | **本作業の実測は 40.1%** —— コミットが違うため両方正しい | **引用元を明示して両方残す**（起票時 = develop `3804511` 相当 / 本作業 = `1d7edce`） |
| **「生成コードの分岐は 0」**（IADR-0138 決定 4 の branch 据え置きの根拠） | **偽になる** —— source generator 出力は分岐 3970 を持つ | **IADR-0138 へ日付つき追記**し、新 IADR で branch も置き直す |

## ★★ 実測 1: 判定パターンの決定（#574 の第 1 チェック項目）

**issue は「`obj/` を基準にすると `Debug/net10.0/` 配下の中間生成物も巻き込む可能性がある。何が入っているかを全数で確認すること」と書いていた。全数で確認した。**

レポート内の `<class filename>` を全数（重複除去後 **1061 件**）に分類した結果:

| 分類 | 件数 | 中身 |
| --- | --- | --- |
| **`obj/` を区切り付きで含む** | **14** | `OpenApiXmlCommentSupport.generated.cs` **11** ／ `RegexGenerator.g.cs` **3**。**これ以外は 1 件も無い** |
| `obj/` の外の `*.g.cs` / `*.generated.cs` | **0** | —— |
| `Migrations/` 配下・`*ModelSnapshot.cs`（既存の除外） | 117 | EF の生成物 |
| その他（手書き） | 930 | —— |

**したがって「`obj/` 基準」と「`*.g.cs` ＋ `*.generated.cs` 基準」はこの母集合で完全に一致し、どちらも手書きコードを 1 行も巻き込まない。**
**中間生成物（`Debug/net10.0/` 等）の巻き込みは 0 件である** —— コンパイラが `<class filename>` へ書くのは
**ソースとして食わせたファイル**だけで、`.dll` や `.cache` はレポートに現れないためである。

### 採用: **`obj/` を区切り付きの一区画として含む**

**サフィックス基準ではなくディレクトリ基準を採る。**

| | **A. `obj/` 配下（採用）** | B. `*.g.cs` ＋ `*.generated.cs` | C. 両方の OR |
| --- | --- | --- | --- |
| 本実測での取りこぼし | **無し** | **無し**（A と完全一致） | 無し |
| 根拠の性質 | **構造的** —— `obj/` は MSBuild の中間出力ディレクトリで gitignore 済み。**人が書いたコードは定義上そこに無い** | **規約的** —— サフィックスは各 generator が選ぶ。新しい generator が別の名前で出せば**黙って素通りする** | 同左（B の弱点が残る） |
| 壊れ方 | 出力先が変われば除外 0 件 → **notice で出る**（IADR-0138 決定 3 の機構をそのまま使う） | **無音** | 片方でも当たれば notice が出ないため、B 側の劣化に気付けない |
| 規則の本数 | 1 本 | 2 本 | 3 本 |

**C は「起こり得ないケースへの防御的実装」に当たる**（`CLAUDE.md` 禁止事項）。**実測で一致している以上、規則を 2 本持つ理由が無い。**
**B を採らないのは、壊れたときに無音だからである** —— IADR-0123 と IADR-0138 が繰り返し名指しした失敗モードそのものである。

## ★★ 実測 2: 床への影響と置き直し

| | line | branch |
| --- | --- | --- |
| 現行（EF のみ除外） | `35.14%`（10016/28502） | `19.30%`（1912/9908） |
| **`obj/` も除外** | **`39.92%`（9486/23762）** | **`28.01%`（1663/5938）** |
| 差 | **+4.78pt** | **+8.71pt** |

source generator 出力だけを取り出すと **197 クラス / 4740 行（被覆 530 = 11.2%） / 分岐 3970（被覆 249 = 6.3%）**。
**分岐分母の 40.1% を占める**（#574 起票時の 38% は別コミットでの実測。どちらも正しい）。

### 置き直し: **`line 33` → `39`。`branch 17` → `27`**

**`branch` を切り下げの `28` にしない。** IADR-0118 決定 2 は「実測を整数へ切り下げる」と定めるが、
**`28.01%` の切り下げ `28` は余裕が `0.01pt` しかなく、床として機能しない。**

**これは主観ではなく計算できる。**

| 床 | 赤にするのに必要な被覆の喪失 | 判定 |
| --- | --- | --- |
| `branch 28` | `1663 → 1662`（**被覆分岐 1 本**。`1662/5938 = 27.99%`） | **不可** —— テスト 1 件の skip どころか分岐 1 本で割れる |
| **`branch 27`（採用）** | `1663 → 1603`（**被覆分岐 60 本**。余裕 `1.01pt`） | 可 |
| `line 39`（採用） | `9486 → 9267`（**被覆行 219 行**。余裕 `0.92pt`） | 可 |

**`coverage-floor.json` の `$comment` が既に「計測ゆらぎで赤にならない幅を確認してから上げること」と書いている。**
**その確認を実際に行った結果が上の表である。**

> **★★ この判断は仮定ではなく実証された（2026-08-15・CI 実測）。**
> **CI の branch は `27.99%（1662/5938）`** —— ローカル（`1663`）より**ちょうど被覆分岐 1 本少ない**。
> **切り下げの床 `28` を採っていたら、CI は初回から赤だった。**
> **表の「被覆分岐 1 本の喪失で割れる」が、まさにその 1 本の差として現実に出た。**

> **★ `line 33 → 39` / `branch 17 → 27` は ratchet の引き上げではなく、測定基準の変更に伴う置き直しである**
> （[IADR-0123](../adr/IADR-0123_cobertura-class-attribution-and-line-dedup.md) 決定 7 ／ IADR-0138 決定 4 と同じ性質）。
> **旧定義の床と新定義の床は比較できない**（分母・分子が違う）。

### 棄却した選択肢

| | 判断 |
| --- | --- |
| **除外しない**（現状維持） | **棄却。** エンドポイントへ XML doc コメントを 1 つ足すだけで `OpenApiXmlCommentSupport.generated.cs` が再生成され床が動く。**PR #568 と同じ失敗モードが分岐分母の 40% を巻き込んだまま残る** |
| **分岐だけ除外する** | **棄却。** line と branch が**別の母集合**を測ることになり、[IADR-0123](../adr/IADR-0123_cobertura-class-attribution-and-line-dedup.md) 決定 4 の coverlet 突合（`lines-valid` との一致検査）が意味を失う。歪みが大きいのは branch 側だが、**非対称にする代償のほうが大きい** |

## テスト（受け入れ基準の写像）

| # | 受け入れ基準（#574） | 確かめ方 |
| --- | --- | --- |
| 1 | 判定パターンを実レポートから決めた | **§実測 1 の全数分類表**（1061 件を 4 分類） |
| 2 | 人が書いたコードを巻き込まない | **同表**（`obj/` 配下 14 件は全て `*.generated.cs` / `*.g.cs`。手書き 0 件） |
| 3 | 床を再導出した（line / branch の両方） | **§実測 2** ＋ `src/coverage-floor.json` |
| 4 | 新規 IADR | **[IADR-0195](../adr/IADR-0195_coverage-exclude-source-generator-output.md)** |
| 5 | 変異試験（除外を外すと戻る／XML doc を足しても床が動かない／床を上げると落ちる） | **後述の変異試験の表** ＋ `scripts.repo.test.js` へ恒久化 |

## 変異試験（受け入れ基準 5）

| # | 変異 | 期待 | 実測 | 判定 |
| --- | --- | --- | --- | --- |
| M1 | `obj/` の判定を無効化（`objDISABLED/` へ差し替え） | 旧定義へ戻り落ちる | `line 35.14%（10016/28502）` / `branch 19.3%` へ戻り **`exit=1`**。あわせて **source generator 側の notice が出た**（種別ごとに数えているため） | **落ちた（検出）** |
| M2 | **XML doc コメントを足した状態を再現** —— 実レポートへ `obj/…/OpenApiXmlCommentSupport.generated.cs` として **154 行 / 308 分岐 / 0 被覆**を注入・**除外あり** | 実測値が動かない | `line 39.92%（9486/23762）` / `branch 28.01%（1663/5938）`。**注入前と完全に同値**。一方「生成コードを戻すと」は `line 35.58% → 35.41%` / **`branch 19.3% → 18.72%`（0.58pt）**と動いた | **意図どおり（不動）** |
| M3 | 床を `line 40` へ引き上げ | 落ちる | `実測 39.92% < 床 40%` で **`exit=1`** | **落ちた（検出）** |

**M2 が本 issue の核心である** —— **旧定義なら XML doc コメント 1 箇所で床が動いていた**（`branch` の 0.58pt は旧床の余裕を優に超える）。

**恒久化**: M1 / M2 の判定規則そのものは `check-coverage-floor.js --self-test` の新規 15 ケース（`generatedKindOf` の全数形・種別ごとの計数・**片方だけ 0 行でも notice が出ること**）が固定する。M3 の型（床の追随漏れ）は `scripts.repo.test.js` の新規テストが固定する。

## 着地の実測

| | 値 |
| --- | --- |
| `check-coverage-floor`（ローカル・実レポート 14 件） | **exit=0** / `line 39.92%（9486/23762）` / `branch 28.01%（1663/5938）` |
| **`check-coverage-floor`（CI 実測）**（PR #741 / run `31866326272`） | **exit=0** / **`line 39.92%（9485/23762）`** / **`branch 27.99%（1662/5938）`** |
| `check-coverage-floor --self-test` | **80 件 OK**（改修前 65 件） |
| `node scripts/scripts.test.js` | **516 件 OK** |
| 文書検査 8 種（links / cross-repo-refs / plan-id / doc-type / kit-sync / feedback-dispatched / feedback-status-sync / action-versions） | **すべて exit=0** |
| 追随した文書 | **5 件**（`coverage-floor.json` / IADR-0116 / IADR-0118 / IADR-0138 / TEST_STRATEGY） ＋ 索引 1 件 |

### 規則 8 で引き直したもの

| 対象 | 判定 | 対応 |
| --- | --- | --- |
| 新しい床 `39` / `27` | **走査した** —— ゲートの言明は 2 箇所（IADR-0116 規約 6 の表・TEST_STRATEGY ゲート一覧）で、両方 `39` / `27` へ追随済み | **機械検査を置いた**（`scripts.repo.test.js`。JSON と突き合わせる） |
| 「生成コードの分岐は 0」（IADR-0138 決定 4 の据え置きの根拠） | **偽になった** | **IADR-0138 と TEST_STRATEGY へ日付つき追記**し、射程が EF に限られることを明記 |
| 「EF 以外の生成物は集計に残る」（IADR-0138 §検出しないこと・索引） | **偽になった** | **両方へ「解消済み」を追記**（索引側は追記ブロック形を使わない —— 索引タイトルセルの検査が `title-addendum` として弾く） |
| 「分岐分母の 38%」 | **偽にならない**（起票時のコミットでの実測） | **両方残し、コミットを併記** |
| 導出値（差 `+4.78pt` / `+8.71pt`・余裕 `0.92pt` / `1.01pt`） | 走査では捕まらない | **計算し直した**（規則 8 の但し書き） |

## 射程外

- **フロントのカバレッジ** —— [IADR-0034](../adr/IADR-0034_frontend-coverage-gate.md) / `src/vitest.config.ts` の管轄。orval / Lingui 生成物の扱いは別の決定である
- **EF の `Migrations/` の扱い** —— IADR-0138 決定 1 のまま。**規則を 1 本足すだけで、既存の 2 本は変えない**
- **測定環境の恒久化** —— 本 PR は「この環境で測れた」ことを記録するに留める。**CI 以外で常に測れるようにする作業は別 issue**
