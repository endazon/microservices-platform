---
title: SC-05 / SC-06 テスト仕様書のバックエンド節を復帰し、同種の欠落を機械検査で止める
type: spec
status: done
related_ids: [SC-05, SC-06, NFR, IADR-0130]
author: Claude
created: 2026-08-05
updated: 2026-08-05
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
related_specs:
  - "../tests/SC-05_document-management.md"
  - "../tests/SC-06_datasource-management.md"
  - "../tests/SC-07_conversion-jobs.md"
  - "../tests/TEST_STRATEGY.md"
  - "../adr/IADR-0130_test-spec-coverage-ratchet.md"
  - ./20260805_issue-503_sc05-08-admin-screens.md
  - ./20260805_issue-501_retry-admin-only.md
---

# 仕様書: テスト仕様書のバックエンド節の復帰と再発防止（#510）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 画面（SC）: **SC-05**（文書管理）・**SC-06**（データソース管理）
  （[05_screens/01_screens.md](../../planning/projects/microservices-platform/05_screens/01_screens.md)）
- ユースケース（UC）: UC-03（文書を管理する）・UC-04（データソースを登録・同期する）
- 機能要求（FR）: FR-01・FR-02・FR-06・FR-09
- 非機能（**NFR**）: 検査基盤（退行防止ゲート）。規約は
  [`docs/tests/TEST_STRATEGY.md`](../tests/TEST_STRATEGY.md) と
  [`.claude/rules/traceability.md`](../../.claude/rules/traceability.md)
- 本リポジトリの起点: #510（出所は #501 / PR #509 の衝突解消。原因は #503 / PR #508）

## 目的・背景

#503（PR #508）が `docs/tests/SC-05`〜`SC-08` を「全面改訂」した際、**フロントエンドの構造で
置き換えたためにバックエンド試験の節が失われた**。**テスト自体は消えていない**——記載だけが消えた。

同じ現象は SC-07 でも起きたが、そちらは #501（PR #509）が衝突解消の過程で気づいて復元した。
**衝突が起きなかった SC-05 / SC-06 は誰も読み直さなかったため残っていた。**

記載が消えると次の 2 つが起きる（#510 の記述）。

- 次に触る人が「バックエンド試験は無い」と読み、**重複して書く**か**消してよいと判断する**
- 受け入れ基準のうちバックエンドが担う分の対応が追えなくなる

`check-test-traceability.js` が見るのは**起点 ID の写像**（FR/UC/SC がテストから 1 件でも参照されて
いるか）であり、**節の欠落は検出しない**——SC-05 の ID はフロントのテストから参照され続けるため、
バックエンド節が丸ごと消えても順方向・逆方向のどちらも緑のままである。

## 対象範囲

| # | 作業 | 出力 |
| --- | --- | --- |
| 1 | SC-05 のバックエンド 2 節（BFF 書き込み／DocumentService 状態遷移ガード）の復帰 | `docs/tests/SC-05_document-management.md` |
| 2 | SC-06 のバックエンド 1 節（BFF）の復帰 | `docs/tests/SC-06_datasource-management.md` |
| 3 | 同種の欠落の全数確認と、見つかったものの是正 | `docs/tests/SC-03_document-detail.md` |
| 4 | 再発防止の機械検査 | `scripts/check-test-spec-coverage.js` ＋ baseline ＋ [[IADR-0130]] |
| 5 | ゲート一覧・スクリプト索引への追記 | `docs/tests/TEST_STRATEGY.md` / `scripts/README.md` |

**対象外**: バックエンドのテストコードそのもの（**1 行も変えない**。テストは消えていない）。
画面実装・フロントエンドのテスト。`.github/workflows/`（GitHub App 権限では編集できないため、
必要な変更は本書 §CI 結線 に記して親へ引き渡す）。

## 1〜2. 復帰の方針（**当時の記載をそのまま戻さない**）

`de55761`（#508 マージ前）の記載を復元の**出発点**にするが、そのまま戻さない。理由は 2 つある。

- **パスが動いている。** BFF のテストプロジェクトは `KnowledgePlatform.Bff.Tests` →
  `Platform.Bff.Tests` へ、エンドポイントは `Bff/KnowledgePlatform.Bff/Foundation/Endpoints/` →
  `knowledge/backend/Bff/Knowledge.Bff.Endpoints/` へ移っている。当時の記載を戻すと
  **存在しないパスを指す文書**になる（`check-doc-links.js` はコードファイルへのリンクも検査するため、
  live link で書けば CI が落ちる）。
- **実在しないテスト名を載せたら、それは元の欠落より悪い。** 欠落は「無い」と誤読させるが、
  実在しない記載は「在る」と誤読させ、探しても見つからない時間を毎回課す。

したがって**現在のテストの実物を grep で確かめてから**書く（確認手順は §検証）。
構造は **#509 が SC-07 で採った形**（フロントの構造 ＋ バックエンド／BFF／deploy の表）に揃える。

## 3. 全数確認の方法

`docs/tests/*.md` の**節見出しの比較だけでは足りない**——見出しは改訂で正当に改名されるため、
「消えた」と「改名された」を区別できない（実測でも SC-08 / SC-10 / SC-11 が見出しの総入れ替えで
差分に現れる）。そこで**テストの実体の名前**で突合する。

1. 現存する `*Tests.cs`（`src/platform/backend` ＋ `src/knowledge/backend`）の**クラス名**を集める。
2. そのうち **`docs/tests/` のどこにも現れないもの**を列挙する。
3. 2 のそれぞれについて `git log -S<名前> -- docs/tests/` を引き、**かつて載っていたのに今は無い**
   ものだけを「欠落」として拾う（最初から載っていないものは欠落ではなく未記載であり、質が違う）。
4. フロントエンドのテストファイル（`*.{test,spec}.{ts,tsx}`）にも同じ 1〜3 を行う。

## 4. 再発防止 — **これが本題である**

「全面改訂」で節が落ちることは**レビューでも CI でも捕まらなかった**。規約や注意書きを足すだけでは
再発する（本リポジトリには「規約はあったのに混在していた」事例が #507 として起票されている）。
よって**機械検査へ載せる**。

### 方向の選択: (b) を採る

| 方向 | 内容 | 今回の欠陥を止められるか |
| --- | --- | --- |
| (a) | **仕様書が挙げるテスト名が実在するか** | **止められない**。落ちたのは記載であり、残った記載はすべて実在した |
| (b) | **実在するテストが仕様書に載っているか** | **止められる**。`BffDocumentWriteEndpointTests` は #508 以降 `docs/tests/` のどこからも参照されない状態だった |

(b) を採る。ただし素の (b) は「**すべてのテストを仕様書に書け**」という強すぎる要求になるため、
粒度と判定を次のように設計する。

### 粒度: **`docs/tests/` の仕様書ファイル × テストクラス（`*Tests.cs` のファイル名）の対**

- **テストアセンブリ（プロジェクト）単位では今回の欠陥を止められない。** `Platform.Bff.Tests` は
  SC-07 ほか複数の仕様書から参照され続けており、SC-05 の節が丸ごと消えても「1 つも無い」に
  ならないためである（実測: **本 PR 適用後の作業ツリーで 9 件**。数え方は
  `grep -rl 'Platform.Bff.Tests' docs/tests/ | wc -l` ＝ 当該文字列を含む `docs/tests/` 直下の
  Markdown ファイル数）。
- **テストメソッド単位では細かすぎる。** 表の 1 行を消しただけで赤くなり、仕様書の正当な要約
  （「主要ケースのみ挙げる」）を禁じてしまう。
- **クラス名だけを見る形でも足りない。** これは**変異試験で実測して判明した**（§検証 の M2）。
  `DocumentVersioningTests` / `DocumentEndpointVersioningTests` は SC-05 と **FR-06 の両方**が
  参照しているため、**SC-05 の §状態遷移ガード 節を丸ごと消しても「どこかには載っている」で緑**に
  なった。**「壊すと落ちる」を実測していなければ、入れたのに今回と同型の欠落を半分しか止めない
  検査になっていた。**
- したがって**「仕様書ファイル × クラス」の対**を単位にする。**落ちるのは節であり、節は仕様書
  ファイルに属する。** 表の行の増減では赤くならない点はクラス単位と同じである。

### 判定: ratchet（`documented`〔仕様書 → クラス名の配列〕を床として持つ）

本リポジトリの既存ゲート（写像検査・カバレッジ床・ライブラリ標準・契約互換）と同じ 3 判定にする。

| 事象 | 判定 |
| --- | --- |
| baseline にある**対**が消えた（**テストは実在する**） | **fail**（＝今回の欠陥。**他の仕様書に同じクラスの記載が残っていても落ちる**） |
| baseline にある対のクラスがテストごと消えた | **fail**（baseline から削除させる。仕様書側の記載も見直させる） |
| 記載された対が baseline に無い | **fail**（床を上げっぱなしにさせる。`--update` で更新） |
| 実在するが**どの仕様書にも**載っておらず baseline にも無い | **warn**（新規テストの多くは基盤・回帰であり、載せる義務は負わせない） |

**fail-closed**: 走査したテストクラスが 0 件、`docs/tests/` の Markdown が 0 件、baseline が
読めない／壊れている／形式が違う（旧形式のクラス名配列を含む）、のいずれも **fail** にする。
「見つからないから素通り」は本検査が塞ごうとしている穴と同型である。

**床を下げて黙らせる経路への手当て**: 床は `--update` で下げられるが、差分は必ず PR に現れる。
加えて**本 issue が復帰させた 4 対**（SC-05 の 3・SC-06 の 1）は `scripts/scripts.repo.test.js` の
専用テストが**床とは独立に**固定しており、床を下げるだけでは通らない。

### 既存 `check-test-traceability.js` に足すのではなく、新しい検査器にする（**理由**）

1. **突合の単位が違う。** `check-test-traceability.js` が扱うのは**起点 ID の集合**（FR/UC/SC/NFR）で
   あり、本検査が扱うのは**テストの実体（ファイル）と仕様書の参照**である。同じスクリプトに 3 本目の
   検査を足すと、1 つの終了コードが 3 つの無関係な理由で赤くなり、失敗の読み取りが難しくなる。
2. **baseline の名前空間が違う。** `test-traceability-allowlist.json` は ID をキーに `pending` /
   `specMissing` の意味を持つ。ここへファイル名の配列を混ぜると、同じファイルが 2 つの語彙を持つ。
3. **`scripts.test.js` を変更できない**（[[IADR-0115]] 分類 A・キットとバイト一致）。新規検査器は
   `--self-test` を内蔵し、`scripts.repo.test.js`（companion）から呼ぶ——`check-i18n-catalogs.js` /
   `check-static-egress.js` の先例と同じ作法である。
4. 既存検査器は 529 行あり、これ以上の多目的化は同期・レビューの単位として重い。

### 対象を**バックエンドの xUnit テストクラスに限る**（測ったうえでの判断）

フロントエンドのテストファイルも同じ形で検査できるが、**今回は対象に含めない**。

- 実測（**本 PR 適用後の作業ツリー**。母集合は `src/{platform,knowledge}/frontend` ＋ `src/packages`
  配下の `*.{test,spec}.{ts,tsx}` で `node_modules` を除いたもの＝ **59 件**）では、`docs/tests/` が
  **ファイル名で**参照していないものが **28 件**ある。うち大半は `foundation/` や `@platform/ui` の
  基盤テストで、画面の受け入れ基準に紐づかない。
- 残る画面テスト（**SC-01 の `citations.test.ts` と SC-04 の `WikiAccessPage.test.tsx` の 2 件**）は
  **節そのものは存在し**、仕様書がディレクトリ名で参照しているために「ファイル名では見つからない」
  だけである。ここを fail にすると、**仕様書の書き方の統一**という別件の作業を本検査が強制する
  ことになる。
- バックエンドは表の `ケース` 列にメソッド名、見出しにクラス名を書く様式が定着しており、
  ファイル名キーが実務と一致する。
- フロントエンドへの拡張は、仕様書の参照様式を先に揃えてから別 issue で行う（[[IADR-0130]] §フォローアップ）。

### CI 結線

`.github/workflows/` は GitHub App 権限では編集できないため、本 PR ではワークフローを変更しない。
**それでも本検査は CI で強制される**——`scripts.repo.test.js`（companion）へ

1. `--self-test` が exit 0 であること
2. **本リポジトリの実データに対する本走が exit 0 であること**

の 2 件を足し、これを `ci.yml` の `scripts-tests` ジョブ（`REQUIRE_REPO_TESTS=1`）が実行するためである。
`check-i18n-catalogs.js` の実データ検査と同じ結線の形である。

**任意の追加（親のローカル権限で行う）**: `ci.yml` の `test-traceability` ジョブに独立ステップを
足すと、失敗が専用ジョブ名で見え、`::error` 注釈も PR の該当行に出る。要る差分は次のとおり。

```yaml
      - name: Self-test test-spec coverage checker
        run: node scripts/check-test-spec-coverage.js --self-test
      - name: Check tests are covered by test specs
        run: node scripts/check-test-spec-coverage.js
```

## 実装しないこと（と、その理由）

| 事項 | 理由 |
| --- | --- |
| 方向 (a)（仕様書が挙げるテスト名の実在検査） | **今回の欠陥を止めない**（上表）。散文中の識別子をテスト名と判別する必要があり、誤検出の設計が本題（b）より重い。別 issue の候補として [[IADR-0130]] へ残す |
| テストメソッド単位の突合 | 仕様書の正当な要約を禁じる（上述） |
| 「テスト仕様書を全面改訂するときは節を確認すること」という規約追記のみ | **#507 が「規約はあったのに守られなかった」ことを示している。** 規約は機械検査の**説明**として置き、強制は検査器が担う |

## 検証

- 復帰した節のテスト名がすべて実在すること（`grep` で全数確認。§報告に出力を貼る）
- `node scripts/check-doc-links.js` / `check-commit-messages.js --base origin/develop` /
  `check-test-traceability.js` / `check-unit-dependencies.js` が通ること
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が通ること
- 新規検査器の `--self-test` と本走が通ること
- **変異試験**（6 種。いずれも実測して報告に貼る。実行後は必ず復元し `git status` で汚れが
  残っていないことを確かめる）
  - **M1**: SC-06 の §BFF 節を丸ごと消す（#510 の欠陥の再現）→ **fail**
  - **M2**: SC-05 の §バックエンド（DocumentService・状態遷移ガード）節を丸ごと消す
    → **fail**。**同じクラスを FR-06 も参照しているため、クラス単位の床では緑になった**
    （この実測で粒度を対へ変更した）

  > **［2026-08-05 追記 / 訂正］M1・M2 の「丸ごと消す」は、節 ＋ 同一ファイル内の節外の言及
  > （改訂ノート・§対象（API）・§実行）を消すことを指す。節だけを消せば検査は緑である。**
  > 再実測の内訳と、本 issue が復帰させた 4 対がなぜその状態にあるかは
  > [[IADR-0130]] §変異試験が見つけた穴 の追記および §限界 2 の追記を正とする。
  > 強度を上げる案（`--strict`）は同 §フォローアップ 4（別 issue）へ置いた。
  - **M3**: テストクラスのファイルを 1 件消す（改名相当）→ **fail**（床の減らし忘れ）
  - **M4**: 未記載クラスを仕様書へ足すが床を上げない → **fail**（床の上げ忘れ）
  - **M5**: 床の JSON を壊す → **fail**（fail-closed）
  - **M6**: 床を旧形式（クラス名の配列）へ戻す → **fail**（形式の取り違えを黙って通さない）
- バックエンド／フロントエンドのコードには触れていないこと（`git diff --name-only` で示す）。
  したがって `dotnet build` / `pnpm run build` は実行しない
