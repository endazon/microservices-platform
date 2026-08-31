---
title: 作業仕様書 — UC-01〜UC-07 のテスト仕様書を起こし、specMissing の allowlist を空にする（#1106）
type: spec
status: done
related_ids:
  - UC-01
  - UC-02
  - UC-03
  - UC-04
  - UC-05
  - UC-06
  - UC-07
  - NFR
  - IADR-0130
author: claude
created: 2026-08-31
updated: 2026-08-31
plan_refs:
  - planning:projects/microservices-platform/03_usecases/01_usecases.md
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
---

# 作業仕様書: UC のテスト仕様書（#1106）

## 起点となる計画書（トレーサビリティ）

- ユースケース: `projects/microservices-platform/03_usecases/01_usecases.md` の UC-01〜UC-07
  （基本フロー・代替フロー・例外フロー・関連要求・関連画面を逐語で読んだ）。
- 本作業は **文書化のみ**である。テストの実体は 1 行も足さない（#1106 の補足・制約）。

## 母集合（規則 9・10 に従い、自分で引いた）

**issue 本文の数えを転記していない。** 以下はすべて本作業で実測した。

### 1. 対象 ID の母集合

`.claude/rules/traceability.repo.md`「起点 ID の種別（固有）」が正本で、**UC のレンジは `UC-01..11`**
（`UC-01..07` ではない）。よって母集合は 11 件である。

### 2. テスト仕様書が「ある UC」「ない UC」

`docs/tests/` を機械的に列挙した（`ls docs/tests`。全 53 件）。

| 状態 | UC | 根拠 |
| --- | --- | --- |
| 仕様書あり | UC-08 / UC-09 / UC-10 / UC-11 | `docs/tests/UC-08_*.md` 〜 `UC-11_*.md` が実在 |
| 仕様書なし | **UC-01 / UC-02 / UC-03 / UC-04 / UC-05 / UC-06 / UC-07** | 同ディレクトリに `UC-0[1-7]_*.md` が無い |

除外理由: UC-08〜UC-11 は既に仕様書があるので本作業の対象外。触らない。

### 3. issue 本文の数えとの差（**2 件の誤りを実測した**）

| issue 本文の記述 | 自分の実測 | 差の説明 |
| --- | --- | --- |
| `ls docs/tests \| grep -c '^UC-'` → **0** （`develop` `a2c7e5b1`） | 同コミットで **4**（`git ls-tree --name-only a2c7e5b1 docs/tests/` に UC-08〜UC-11 が並ぶ） | UC-08 は `736f5996`（2026-08-23）、UC-10 は `3b3136ef`（2026-08-22）で追加済み。`a2c7e5b1` は 2026-08-30 でそれより後。**issue の実測は誤り**。ただし**作るべき本数（7 件）は変わらない** |
| `check-test-spec-coverage.js` の未記載 **110 件** | `9a4d1a9a` で **114 件** | 計測時点の差（issue は `a2c7e5b1`）。本作業の効果は 114 からの減少で測る |

`specMissing` の 7 件と `check-test-traceability.js` の warn（UC-01〜UC-07）は実測で一致した。

### 4. 各 UC を参照している既存テストの母集合

`check-test-traceability.js` と同じ走査規則（テストファイル＝`(Tests?\.cs|\.(test|spec)\.(ts|tsx|js|jsx))$`、
`src/` 配下、AST を除外、修飾付き ID は除外）で 388 件のテストファイルを走査した。

| UC | 参照しているテストファイル数 |
| --- | --- |
| UC-01 | 25 |
| UC-02 | 12 |
| UC-03 | 20 |
| UC-04 | 33 |
| UC-05 | 23 |
| UC-06 | 11 |
| UC-07 | 8 |

**仕様書の「実装マッピング」に書くクラスは、この走査結果に実在するものだけである。**
架空のクラス名は書かない（`check-test-spec-coverage.js` が実在しないクラスを fail させる）。

## やること

1. `docs/tests/UC-01_*.md` 〜 `UC-07_*.md` の 7 件を新設する。各件は計画の
   **基本フロー / 代替フロー / 例外フロー**をテストケースへ写像し、**実在するテストクラス**を指す。
2. 既存テストで満たせない受け入れ基準は「しない／一部する」として**理由つきで**記録する。
   穴が大きいものは**別 issue へ切る**（本 PR ではテストを書かない）。
3. `scripts/test-traceability-allowlist.json` の `specMissing` を空にする（`pending` の
   SC-13〜15 は射程外なので触らない）。
4. `scripts/test-spec-coverage-baseline.json` を `--update` で上げる（記載の対が増えるため）。

## やらないこと

- **テストの実体を書き足さない。** 穴は別 issue。
- UC-08〜UC-11 の既存仕様書の改訂。
- `pending`（SC-13/14/15）の解消。

## 検証

`check-trace-blocks` / `check-doc-links` / `check-doc-updated` / `check-doc-type-vocabulary` /
`check-doc-status-vocabulary` / `gen-knowledge-graph --check` / `check-commit-messages` /
`check-test-traceability` / `check-test-spec-coverage` / `check-plan-id-qualification` /
`check-reading-budget` / `scripts.test.js`。

**テストの実体に触れないため `dotnet test` / `pnpm run test` は本作業の合否を左右しない**が、
「実装マッピングに書いたクラスが実在する」ことは `check-test-spec-coverage.js` が機械検査する。

## 結果（実測）

| 観測点 | 着手前（`9a4d1a9a`） | 完了後 |
| --- | --- | --- |
| `docs/tests/` の件数 | 53 | 60 |
| `specMissing` | 7 件（UC-01〜UC-07） | **0 件（空）** |
| 逆方向の warn | 「計画レンジ 54 件のうち 7 件に仕様書がありません」 | **54 件中 54 件に仕様書あり** |
| 順方向の写像 | 仕様書のある 48 件中 45 件 | 55 件中 52 件（未写像 3 件は `pending` の SC-13〜15。射程外） |
| 記載の被覆: 未記載クラス | **114 件** | **88 件（26 件減）** |
| 記載の被覆: 仕様書 × クラスの対 | 175 件 | **272 件** |
| 記載の被覆: 参照済みクラス | 148 / 262 | **174 / 262** |

**issue 本文が言う「未記載 110 件」は `a2c7e5b1` 時点の値であり、着手時点の実測は 114 件だった。**
減少幅は 114 → 88 で数える。

## 切り出した issue

- **#1126** — UC-07 基本フロー 1 の「Wiki で**検索する**」に実装もテストも無く、
  未認証時の応答も固定されていない（実測のコマンドと出力を issue 本文に貼った）。
  重複検索は `Wiki 検索` / `WikiService 未認証` / `in:title Wiki` の 3 本で行い、
  該当 0 件を確認した（#449 は経路の再実装、#1108 は稼働環境の障害で、いずれも本件を含まない）。

UC-05 の「Keycloak への実反映が稼働環境で届いていない」件は **#1101 が既に追っている**ため
起票しなかった（仕様書の「未実施」節から参照している）。

## 測れなかったもの

- **Keycloak・Wiki.js・Qdrant・実 LLM を通した挙動**。CI もローカルもこれらを起動しない
  （Testcontainers は Docker daemon を要し、この環境には無い）。各仕様書の「未実施」節に、
  **skip のまま緑になる**ことと合わせて明記した。
- **`check-test-spec-coverage.js` の warn 88 件の内訳が「基盤・回帰テスト」か「記載漏れ」か**。
  本作業の射程は UC の 7 件であり、残りは各ドメイン issue が引き受ける。
