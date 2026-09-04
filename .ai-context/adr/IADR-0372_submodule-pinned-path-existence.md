---
title: IADR-0372 submodule ツリーへのパス実在を既存の対応表検査へ相乗りさせ、CI では未 populate を赤にする
type: impl-adr
status: Accepted
related_ids:
  - ADR-0007
  - IADR-0067
  - IADR-0068
  - IADR-0070
author: claude
created: 2026-09-04
updated: 2026-09-04
plan_refs: []
---

# IADR-0372: submodule ツリーへのパス実在を既存の対応表検査へ相乗りさせ、CI では未 populate を赤にする

- 状態: Accepted
- 日付: 2026-09-04
- 決定者: claude（実装セッション）

## 起点・関連

- 関連する計画書 ID: `ADR-0007`（ローカル実行環境・デプロイ）
- 関連する実装 ADR: `IADR-0068`（image-mapping ドリフト検査の方式）／`IADR-0070` 決定 2（AST は
  単一 Dockerfile ＋ build args ＋ ユニットルート context）／`IADR-0067`（ビルド可否は `images.yml`）
- 関連する実装仕様書: `.ai-context/specs/20260904_issue-1182_service-project-existence.md`
- 起点 issue: #1182

## コンテキストと課題

`src/ai-stock-trading`（AST）の submodule pin を進めたとき、MSP 側が持つ **AST ツリー内の相対パス**
（`SERVICE_PROJECT` ＝ csproj、および `dockerfile`）が pin されたツリーに実在しなくなり、イメージ
ビルドの `dotnet restore` が `MSBUILD : error MSB1009` で落ちる事故が **2 回**起きている
（`36a8bc8a` / #577 と `ac3df666` / #1178。履歴で実測）。

既存の `check-image-mapping.js` は **`MAPPING` ⇔ compose の同値**しか見ない。この 2 つは同じ値を
持つので、AST が樹形を変えると**両方が同時に古くなり、ドリフトは 0 のまま**通り抜ける。**一致は
見ているが、実在は誰も見ていない。** 落ちるのは最もフィードバックの遅いイメージビルド段である。

決めることは 2 つある。

1. 実在検査を**どこに置くか**（既存検査器の拡張か、新設スクリプトか）。
2. **submodule が populate されていないとき**にどう振る舞うか。

## 検討した選択肢

### 1. 置き場所

| 案 | 内容 | 評価 |
| --- | --- | --- |
| (a) `check-image-mapping.js` を拡張 | 既存の compose / MAPPING パーサをそのまま使う。ワークフローも既存の `image-mapping.yml` 1 本 | **採用。** 入力（compose の build 定義と `MAPPING`）が完全に同じで、パーサを 2 つ持つ理由が無い。必須チェック名も増えない |
| (b) 新スクリプト `check-submodule-paths.js` を新設 | 責務が分かれて読みやすい | パーサを複製するか `check-image-mapping.js` を require するかになり、どちらも保守点が増える。新ワークフロー or 既存への相乗りの判断も別途要る |
| (c) `images.yml` の実ビルドに任せる（現状） | 追加コスト 0 | **2 回落ちた実績がそれを否定している。** 最も遅い場所で最初に落ちる |

### 2. 未 populate のときの振る舞い

| 案 | 内容 | 評価 |
| --- | --- | --- |
| (d) 常に fail-open（notice で skip） | 誤検知ゼロ | **CI で恒久的に「何も検査せず緑」になる。** 検査を足した意味が無い |
| (e) 常に fail-closed | 取りこぼしゼロ | ローカル（submodule 未取得が既定）で必ず赤になり、無関係な作業を妨げる |
| (f) 既定は fail-open、`--require-submodule` で fail-closed | ローカルは notice、CI は赤 | **採用。** `check-static-egress.js --require <dist>` と同じ形 |

## 決定

1. **`scripts/check-image-mapping.js` を拡張する**（選択肢 a）。純粋ロジックを 3 つ足し、
   `scripts.repo.test.js` と `--self-test` の両方から単体試験する。
   - `parseGitmodulesPaths(text)` —— `.gitmodules` から submodule の path を導出する
     （**列挙を書かない**）。
   - `collectSubmodulePaths({ mappingEntries, composeTargets, submodules })` —— compose / MAPPING の
     build 定義のうち **context が submodule 配下**のものから、`dockerfile` と `SERVICE_PROJECT` の
     リポルート相対パスを列挙する。compose の `../src/…` は既存の `normalizeComposeContext` で
     正規化してから判定する。
   - `computeMissingPaths(entries, exists, opts)` —— **存在判定 `exists` を注入**して実在を検査する。
     注入するので、submodule を populate せずに陽性・陰性の対を試験できる。
2. **既定は fail-open、`--require-submodule` で fail-closed**（選択肢 f）。未 populate のときは
   **skip した件数を notice で出す**（黙って飛ばさない）。
3. **導出が 0 件なら赤にする**（`empty-submodules` / `empty-submodule-paths`）。既存 `checkTree()` の
   `empty` 判定と同じ思想で、**0 件走査を緑にしない**。
4. **CI（`image-mapping.yml`）は submodule を取得してから `--require-submodule` 付きで呼ぶ。**
   取得イディオムは本リポジトリの既存 11 箇所と同一（`git config --file .gitmodules …
   | xargs -r -n1 git submodule update --init`）。**`on:` とジョブ名 `image-mapping` は変えない**
   （必須チェック名を動かすと恒久 pending になる）。
5. **`SERVICE_DLL` は対象にしない。** ビルド成果物の名前であってツリーに存在しないため、静的には
   検査できない（実ビルドの可否は `images.yml` が担う。`IADR-0067`）。

## 理由

- **同じ入力を 2 つのパーサで読まない。** 事故の形（`MAPPING` と compose が同時に腐る）は
  `IADR-0068` が見ている対応表そのものの上で起きるので、同じ場所で見るのが自然である。
- **`--require-submodule` は「締める方向の明示フラグ」**であり、検査を緩める抜け道の環境変数では
  ない。CI が必ず付けて呼ぶので、**取得ステップが将来壊れたら緑ではなく赤になる** —— 「陰性結論には
  陽性対照を対で置く」の実装形である。
- **存在判定を注入する**ので、検査器の陽性（パスが消えたら赤）を、実 submodule も
  ネットワークも無しで固定できる。`gh api` でツリーを引く案は、検査器がトークンとネットワークに
  依存するため採らなかった（本リポジトリの `check-*.js` は外部依存ゼロを保っている）。

## 結果

- 良い影響:
  - pin bump で `SERVICE_PROJECT` / `dockerfile` が失効すると、**イメージビルドではなく数秒の
    静的検査で**赤くなる。変異試験（#1178 の事故を再現）で `missing-path` 2 件を実測した。
  - 検査は AST に限定していない。`.gitmodules` に別のユニット submodule が増えれば自動的に対象になる。
- 悪い影響・トレードオフ:
  - `image-mapping.yml` が AST リポジトリの可用性に依存するようになる（既に 11 ワークフローが同じ
    依存を持つので新規のリスクではないが、この軽量ワークフローの実行時間は submodule 取得の分だけ
    増える）。
  - `check-image-mapping.js` の責務が「対応表の整合」から「整合＋実在」へ広がった。`IADR-0068` の
    「ビルド可否は `images.yml`」との境界は保つ（実在検査はビルドではない）。
- フォローアップ:
  - MSP が持つ AST ツリー内パスは他に 3 群ある（`Platform.Bff.csproj` の `ProjectReference`、
    `Platform.Bff/Dockerfile` と `platform/frontend/Dockerfile` の `COPY`）。いずれも**参照が消えたら
    必ず落ちる必須ジョブが既にある**ため本 IADR の射程に入れていない（母集合と根拠は仕様書 §対象範囲）。
    この前提が崩れたら射程を広げること。

## 関連

- Supersedes: なし
- Superseded by: なし
