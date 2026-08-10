# docs — 実装リポジトリのドキュメント

この実装リポジトリの仕様書置き場である。計画リポジトリ（`project-planning`）の上流ドキュメント（要求・UC・画面設計・技術検討・ADR）を、実装向けに**詳細化**した仕様書を管理する。**作業着手前に仕様書を作成し、それに沿って実装する**運用とする。

## 構成

```text
docs/
├── templates/    # 各仕様書のひな形（spec / functional / screen / api / data / tech / test /
│                 #   operations / security / adr / observability / authz / integration /
│                 #   batch / migration / error / infra）
├── specs/        # 作業仕様書（作業/PR 単位の横断仕様）
├── functional/   # 機能仕様書        ├── operations/    # 運用仕様書
├── screens/      # 画面仕様書        ├── security/      # セキュリティ仕様書
├── api/          # 通信仕様書        ├── adr/           # 実装ADR（IADR-XXXX）
├── data/         # データ仕様書      ├── observability/ # ログ・可観測性（任意）
├── tech/         # 技術要件書        ├── authz/         # 権限・認可（任意）
├── tests/        # テスト仕様書      ├── integration/   # 外部連携（任意）
│                                     ├── batch/         # バッチ・ジョブ（任意）
│                                     ├── migration/     # 移行（任意）
│                                     ├── errors/        # エラー・メッセージ（任意）
│                                     ├── infra/         # インフラ・構成（任意）
│                                     └── how-to/        # 手順ガイド（任意）
```

## 必須の仕様書

対象が存在する限り作成・維持する。`/new-spec <種別> <ID|topic>` で作成。

| 種別 | 文書 | 出力先 | 粒度 | 計画書の一次情報 |
| --- | --- | --- | --- | --- |
| `work` | 作業仕様書 | `docs/specs/` | 作業/PR 単位 | 該当する FR/UC/SC |
| `functional` | 機能仕様書 | `docs/functional/` | 機能（FR）単位 | 02_requirements / 03_usecases / 04_workflows |
| `screen` | 画面仕様書 | `docs/screens/` | 画面（SC）単位 | 05_screens |
| `api` | 通信仕様書 | `docs/api/` | API/IF 単位 | 03_usecases / 04_workflows / 06_technical |
| `data` | データ仕様書 | `docs/data/` | エンティティ単位 | 02_requirements / 06_technical / 07_adr |
| `tech` | 技術要件書 | `docs/tech/` | リポ単位（1つ） | 06_technical / 07_adr / NFR |
| `test` | テスト仕様書 | `docs/tests/` | 機能（FR）単位 | 02_requirements（受け入れ基準）/ 03_usecases |
| `operations` | 運用仕様書 | `docs/operations/` | リポ単位（1つ） | NFR（運用・保守） |
| `security` | セキュリティ仕様書 | `docs/security/` | リポ単位（1つ） | NFR（セキュリティ）/ 07_adr |
| `adr` | 実装ADR（`IADR-XXXX`） | `docs/adr/` | 決定単位 | 06_technical / 07_adr（実装に閉じた判断） |

## 任意の仕様書

必要に応じて作成する。

| 種別 | 文書 | 出力先 |
| --- | --- | --- |
| `observability` | ログ・可観測性仕様書 | `docs/observability/` |
| `authz` | 権限・認可仕様書 | `docs/authz/` |
| `integration` | 外部連携仕様書 | `docs/integration/` |
| `batch` | バッチ・ジョブ仕様書 | `docs/batch/` |
| `migration` | 移行仕様書 | `docs/migration/` |
| `error` | エラー・メッセージ仕様書 | `docs/errors/` |
| `infra` | インフラ・構成仕様書 | `docs/infra/` |
| `runbook` | 運用 Runbook（運用仕様書の**下位**にあたる手順書） | `docs/operations/` |
| `how-to` | 手順ガイド（開発環境の起動・デプロイ・submodule 追加など） | `docs/how-to/`（[ローカル開発](how-to/local-development.md)・[デプロイ](how-to/deployment.md)・[ユニット submodule の追加](how-to/adding-a-unit-submodule.md)・[引継資料](how-to/session-handoff.md)） |

> `operations` はリポ単位で 1 つと定めているため、手順書が複数必要になると置き場が無くなる。
> **状態の単一情報源は `operations.md` に置き、Runbook は手順に特化して複数存在してよい**。
> `how-to` は仕様ではなく作業手順の案内であり、起点 ID を持たないことがある。
> その場合はフロントマターの起点 ID を空にしてよい（他の仕様書と異なり必須としない）。

## 補助成果物の自動生成

補助成果物は生成可能なら必ず生成し、CI（`.github/workflows/`）で自動更新する。

- **CHANGELOG**（`CHANGELOG.md`）: コミット履歴から自動生成（`scripts/gen-changelog.js` / `changelog.yml`）。
- **OpenAPI**（`docs/api/openapi.yaml`）: コードからの生成コマンドがあればそれを、無ければ通信仕様書から雛形を生成（`scripts/gen-openapi-skeleton.js` / `openapi.yml`）。

## 運用ルール

1. **作業着手前に必ず作業仕様書を作成する**（`/new-spec`）。
2. 必須の仕様書は対象が存在する限り作成・維持する。任意の仕様書は必要に応じて作成する。
3. 重要な実装判断は**実装ADR（`docs/adr/`、`IADR-XXXX`）に残す**。計画ADR（計画リポ `ADR-XXXX`）とは別系統。
4. すべての仕様書に起点 ID（FR/UC/SC/ADR）と計画書リンクを記入し、関連仕様書を相互リンクする。
5. 計画書の誤り・不足・新たな制約は `/plan-feedback` で計画リポジトリへ環流する。
   **「環流した」と書いてよいのは `feedback/README.md` の手順 3（計画リポへのコピー、または Issue 起票）まで
   済んだときだけ**である。記録（`feedback/*.md`）を作った段階は「環流の記録を作成した・起票は未了」と書く。
   手順 2 で止まったものを「環流済み」と書くと、計画側が受け取っていない指摘を受け取ったことにしてしまう。
6. **`status` は「その仕様書が記述する実装の状態」を表す。計画側（`05_screens` 等）の `status` には追随しない。**
   - 値: `draft`（着手前・記述途中）／`in-progress`（実装中）／`completed`（実装・テストが揃った）／
     `done`（作業仕様書の完了。`docs/specs/`）／`superseded`（別の仕様書が置き換えた）。
   - **計画側と独立にする理由**: 計画の `status` は「計画としての確定度」であり、実装の進み方とは別の軸である。
     例えば計画 `05_screens/01_screens.md` は**再実装が終わるまで `draft` を維持する**と自ら宣言している
     （実装が動いている途中で計画を `fixed` に近づけると、計画が実装を追認する形になるため）。
     これに実装側が追随すると、**実装が完成している画面の仕様書がいつまでも `draft`** になり、
     `status` が何も伝えなくなる。
   - **画面の一部が未実装でも `completed` にしてよい。** ただし**何が残っているかを本文の冒頭に明記する**
     （例: 着手保留中の FR に属する要素）。`status` は粒度の粗い目印であり、残件の所在は本文が持つ。
   - **★ 値域は `node scripts/check-doc-status-vocabulary.js` が閉じる**（#667）。
     上の 5 値以外を書くと CI が落ちる。**ADR（`docs/adr/`）は別系統**で
     `Proposed / Accepted / Deprecated / Superseded`（正本は [`docs/templates/adr_template.md`](templates/adr_template.md) の
     状態欄。**本リポに `.claude/rules/adr.md` は無い** —— 同名の規約は計画リポ側にあり、
     実装 ADR（IADR）には適用されない）。
   - **★ 対象外の種別**: **上の種別表に無い `type`**（`tech-note` / `design` 等）と、
     **`how-to` / `how-to-guide` / `runbook`** は値域の検査から外す。
     手順ガイドと Runbook は**仕様ではなく作業手順の案内**であり（本書 §種別の注記）、
     「その仕様書が記述する実装の状態」という定義が当てはまらないためである。
     **検査器は除外した件数をログに出す**（黙って飛ばさない）。
   - **★ `review` は値域に含めない。** 2026-07 の作業仕様書 8 件が使っているが、
     **「レビュー中」だったのか「レビューを終えて完了した」のかが文書から読み取れない**ため、
     **推測で書き換えない**（#667 判断 2）。検査器は**この 8 件を据え置きとして許し、
     増えたら落ちる**（ラチェット）。**新規に `review` を書くことはできない。**
   - **★ 過去の仕様書の `status` を書き換えてよい範囲**（#667 判断 0）:
     **語彙の是正のみ**（`fixed` → `done` のように、同じ状態を別の語で書いていたものを揃える）。
     **状態の進行**（`draft` → `done` 等）と**本文への注記追加**は、
     `.claude/rules/traceability.md` の「記録の改竄」にあたるため**不可**である。

詳細な開発規約は `CLAUDE.md` を参照すること。
