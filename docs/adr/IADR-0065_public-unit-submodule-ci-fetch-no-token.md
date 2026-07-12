---
title: IADR-0065 public な追加ユニットの CI submodule 取得はトークン不要（src/* のみ非再帰 init）で有効化する
type: impl-adr
status: Accepted
related_ids:
  - FR-14
  - IADR-0058
  - IADR-0060
author: claude
created: 2026-07-12
updated: 2026-07-12
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (FR-14: 構成変更で完結する疎結合ユニット)"
---

# IADR-0065: public な追加ユニットの CI submodule 取得はトークン不要で有効化する

- 状態: Accepted
- 日付: 2026-07-12
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: FR-14（構成変更のみで完結する疎結合ユニット）
- 関連 ADR: [[IADR-0058]]（private submodule の CI 取得はトークン付き）／[[IADR-0060]]（submodule 運用・
  CI 自動発見。「submodule 取得の有効化が組み込みの前提」）
- 関連仕様書: `docs/specs/20260712_issue-245_ai-stock-trading-unit-integration.md`、
  `docs/how-to/adding-a-unit-submodule.md`
- Issue: #245（#230 残作業。サンプルユニット = `endazon/ai-stock-trading`）

## コンテキストと課題

[[IADR-0060]] は、CI が `src/*/backend/backend.slnx` を自動発見してユニットをビルド／テストする一方、
**submodule は既定の `actions/checkout` では取得されず、未取得ユニットは glob に現れずビルド対象外になる**
ため「取得の有効化が組み込みの前提」と定めた。[[IADR-0058]]（planning submodule）は private を前提に、
`actions/checkout` へ `submodules: recursive` と read 権限を持つ PAT（`doc-links-planning.yml` 型）を与える
方式を採った。`how-to/adding-a-unit-submodule.md` も追加ユニットを private 想定で `secrets.UNIT_REPO_TOKEN`
を例示している。

しかし最初のサンプルユニット `endazon/ai-stock-trading` は **public** であり、read にトークンを要しない。
private を前提にトークン配線・secret 登録を課すと、public ユニットに不要な運用負荷と secret 露出面を増やす。

**重要な制約（実測で判明）**: 本体リポと各ユニットは計画リポ `endazon/project-planning`（**private**）を
`planning` submodule として持つ。`actions/checkout` の `submodules: recursive`（または `true`）は
**この private な `planning` まで取得しようとし、`GITHUB_TOKEN` の既定権限では `Repository not found` で
失敗する**（PR #258 初回 CI で再現。ゆえに既存 `ci.yml` は checkout で submodule を取得せず、planning の
検査は夜間トークン付き `doc-links-planning.yml` に分離していた = [[IADR-0058]]）。したがって「public ユニット
だから recursive で足りる」とはならず、**planning を巻き込まない取得方法**が要る。

## 検討した選択肢

1. **private 前提を踏襲し、public ユニットにも PAT（`UNIT_REPO_TOKEN`）を要求する**: [[IADR-0058]] と一様に
   なるが、public では不要なトークンを登録・維持することになり、secret 面と運用が無駄に増える。
2. **checkout の `submodules: recursive`/`true` を使う**: private な `planning` まで取得しようとして失敗する
   ため不可（上記制約）。
3. **checkout は submodule 取得せず、ユニット実体が要るジョブで `src/*` のユニット submodule のみ非再帰に
   init する（本決定）**: `.gitmodules` から `path` が `src/` で始まる submodule だけを選び
   `git submodule update --init`（非再帰）で取得する。private な `planning`（トップレベル）と、ユニットが
   内包する入れ子 `planning` の双方を巻き込まない。public ユニットは `GITHUB_TOKEN` 既定権限で read できる。

## 決定

1. **ユニット実体が要るジョブで取得**: `ci.yml` の **`lint`** / **`build-and-test`** / **`unit-dependencies`**
   に、checkout 直後のステップとして次を追加する。checkout の `submodules:` オプションは**使わない**
   （private `planning` を巻き込むため）。

   ```yaml
   - name: Fetch unit submodules (src/*, public, non-recursive)
     run: |
       git config --file .gitmodules --get-regexp '^submodule\..*\.path$' \
         | awk '$2 ~ /^src\// { print $2 }' \
         | xargs -r -n1 git submodule update --init
   ```

   - **非再帰**（`--recursive` を付けない）: ユニットが内包する `planning` 等の入れ子 submodule を取得しない。
   - **`src/*` 限定**: トップレベルの private `planning` を対象外にする。
   - public ユニットにつき**トークンは付与しない**（checkout が設定する github.com 用 auth header を継承し、
     public リポの read として成立する）。ユニット追加時の CI 変更は不要（`src/*` を自動列挙）。
   - `build-and-test` / `lint` はユニットのビルド・テスト・整形に、`unit-dependencies` はユニットの
     `.csproj` 依存方向の**継続的な機械検査**に submodule 実体が要る（未取得だと空の gitlink となり、pin 更新で
     platform 直参照が混入しても検出できないため。前回レビュー 🟡 指摘への対応）。
2. **取得しないジョブ**: `doc-links` は従来どおり submodule 未取得（planning のリンク検査は夜間トークン付き
   `doc-links-planning.yml` が担う）。その他 Node 系検査ジョブ（`commit-messages` / `pipeline-config` 等）は
   ユニット実体を要しないため付与しない。
3. **private ユニットへの拡張点**: 将来 private な追加ユニットを組み込む場合は、[[IADR-0058]] 型の
   read 権限 PAT（例 `secrets.UNIT_REPO_TOKEN`）を当該取得ステップ（`git -c http.extraheader=...` もしくは
   トークン付き clone）に与える。how-to はこの public/private 分岐を明記する。

## 理由

- **最小権限・最小運用**: public ユニットに不要なトークンを課さず、secret 露出面を増やさない。ユニット追加は
  checkout 1 行の変更で済み、[[IADR-0060]] の「ゼロ編集に近い組み込み」方針と整合する。
- **段階的拡張**: private が必要になった時点で [[IADR-0058]] の確立様式へ拡張でき、既存の private 経路
  （planning）とも矛盾しない。
- **fail-safe**: 取得は public リポの read のみで、外部送信・書き込み権限を伴わない。

## 結果

- `.github/workflows/ci.yml`: `lint` / `build-and-test` / `unit-dependencies` に「`src/*` のユニット
  submodule のみ非再帰 init」ステップを追加（checkout の `submodules:` は使わない）。3 ジョブで同一実装。
- `docs/how-to/adding-a-unit-submodule.md`: public ユニットはトークン不要（`src/*` を非再帰 init）、
  private ユニットは [[IADR-0058]] 型トークンを与える、の分岐と planning を巻き込まない理由を追記。
- 検証: サンプルユニット `ai-stock-trading` を `src/ai-stock-trading` に追加し、submodule 配置状態で
  ビルド 0 警告 / テスト 675 合格 / `dotnet format --verify-no-changes` クリーンをローカル実測
  （spec #245 参照）。

## 関連

- Supersedes: [[IADR-0060]] の**決定②（追加ユニットの CI submodule 取得方式）のみを改定**する
  （`actions/checkout` の `submodules: recursive` + トークン → checkout オプションは使わず `src/*` の
  ユニット submodule のみ非再帰 `git submodule update --init`。public はトークン不要）。IADR-0060 の
  他の決定（CI 自動発見・テンプレート・単独ビルド規約・バージョン固定）は有効のまま。
- Superseded by: なし
