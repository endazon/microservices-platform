---
title: IADR-0163 必須仕様書が指す `.cs` パスの実在を検査する
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - IADR-0027
  - IADR-0062
  - IADR-0130
  - IADR-0159
author: claude
created: 2026-08-10
updated: 2026-08-11
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
---

# IADR-0163: 必須仕様書のコードパス実在検査（#592）

- 状態: Accepted
- 日付: 2026-08-10
- 決定者: claude（実装）

## 起点・関連

- **NFR**（仕様書と実装の一致）。実装 issue: **#592**（出所は定期監査 2 回目・adr-guardian・`cf15568`）
- 作業仕様書: [20260810_issue-592](../specs/20260810_issue-592_doc-source-path-existence.md)
- 追随できていなかった決定: [IADR-0062](./IADR-0062_namespace-assembly-unit-rename.md)（ユニット改名）・[IADR-0027](./IADR-0027_composability-folder-structure.md)（Composable / Foundation 分割）
- 検査器の置き場所: [IADR-0130](./IADR-0130_test-spec-coverage-ratchet.md)（`check-test-spec-coverage.js`）

## コンテキストと課題

**確定済みの構造改定に必須仕様書 4 本が追随しておらず、存在しないコードパスを指したままだった。**
既存の検査器 2 本は**どちらも原理的に見ない**:

| 検査器 | 見ているもの | この型を見るか |
| --- | --- | --- |
| `check-doc-links.js` | **相対リンク**のみ | **見ない**（コードスパン内の裸のパスは対象外） |
| `check-test-spec-coverage.js` | **クラス名**で突合 | **見ない**（パスは照合しない） |

## 決定 1: **`.cs` で終わるパスだけを見る**（汎用のパス実在検査にしない）

#592 の案 a（`check-doc-links.js` へコードスパン内パスの汎用的な実在検査を足す）は**採らない**。
母集合を引き直したところ、**偽陽性が 6 クラス**あった:

| クラス | 実例 | なぜパスとして扱えないか |
| --- | --- | --- |
| (a) kubectl 資源参照 | `kubectl -n … logs deploy/wiki-js` | `deploy/<name>` は **Deployment 資源** |
| (b) ビルド生成物 | `--require src/platform/frontend/dist` | ビルドするまで存在しないのが正しい |
| (c) 省略形 | `docs/screens/SC-` | `SC-*.md` を地の文で切った形 |
| **(d) 不在を述べる文** | `（scripts/generate-openapi.sh は無い）` | **文の主旨が「存在しないこと」**。`openapi.yml` が `if [ -f … ]` で守る任意フックで、4 箇所が不在前提で書いている |
| (e) 相対表記 | `src/index.ts` | パッケージ内の相対パス |
| (f) 省略記号入り | `src/Tests/.../Deployment/MeshMtlsTests.cs` | `...` は省略であって階層名ではない |

**汎用にすると無関係な運用 Runbook（(a) ×3）と通信仕様書（(c)(d)）を落とす。**
[IADR-0159](./IADR-0159_openapi-dto-drift-checker.md) が実測したとおり**偽陽性は見逃しより重い** —— 無関係な PR の CI を誤って落とすからである。

`.cs` は資源名・生成物・省略形・相対表記のどれとも取り違えようがない。
**種類で絞るのではなく、曖昧さの無い形だけを見る。** 実測では、必須仕様書に残る `.cs` の不在は
**真の誤り 4 件ちょうど**で、6 クラスの偽陽性は 1 件も入らなかった。

**`.cs` 以外は見逃す側に倒す**（射程外。申し送り）。

## 決定 2: 検査は **`check-test-spec-coverage.js` へ足す**（新設しない）

同検査器は冒頭で**方向 (a)「仕様書が挙げるテスト名が実在するか」を検討して採らなかった**と書いている
——#510 の欠陥（節の消失）は方向 (b) でしか止まらないためである。
**本件は別の欠陥であり、方向 (a) がちょうど当たる。2 つの方向は競合せず補い合う。**

新設しない理由は `CLAUDE.md` の**必読規約 50KB 予算**（本リポは超過中）。

> **［2026-08-11 追記 / #697］★ 超過は解消した**（#623 の減量 段 1〜5 ＋ #697 で 50,000 を下回った。[IADR-0178](./IADR-0178_claude-md-defers-to-docs-readme.md) 決定 4）。
> **ただし本 ADR の決定は変えない** —— 余白はごく小さく、**予算内に保つのは各 PR の責任**である（同 決定 4）。
**同じ資源（仕様書と実体の対応）を見る検査器を 2 本に割らない。**

## 決定 3: **対象は「現在を記述する必須仕様書」に限る**（#592 より広く除外する）

| ディレクトリ | `.cs` の不在 | 扱い |
| --- | --- | --- |
| `docs/tests` / `functional` / `screens` / `api` / `data` / `tech` / `operations` / `security` | **4** | **検査する** |
| `docs/specs/`（作業仕様書） | 36 | **対象外**（#592 が明記） |
| `docs/adr/`（決定記録） | 4 | **対象外**（本 ADR で追加） |
| `docs/superpowers/plans/`（旧計画） | 82 | **対象外**（本 ADR で追加） |

**#592 は `docs/specs/` だけを対象外と書いているが、それでは足りない。**
`docs/adr/` の 4 件（`src/Bff/KnowledgePlatform.Bff/…` 等）は**改定前の構造を説明する文脈**であり、
追随させると「当時こう決めた」という記録として壊れる —— **`docs/specs/` と同じ理屈**がそのまま当たる。

**線引き: 「作業当時の事実を記録した文書」は追随させない。**

## 決定 4: **別プロジェクトの submodule は判定しない**

`docs/adr/IADR-0072` が指す `src/ai-stock-trading/…/MonitorSettingsEndpoints.cs` は
実体が submodule の中にあり、populate していない場面では**実在しても不在に見える**。
既存ヘルパ `scripts/lib/excluded-units.js`（`.gitmodules` を単一情報源に導出）で除外する
——**名指しでハードコードしない**（issue #473 の劣化を持ち込まない）。

## 決定 5: **ラチェットを置かない**

是正後の不在は **0 件**であり、据え置く債務が無い。
**空の床を置くと「また据え置いてよい」と読める**（[IADR-0162](./IADR-0162_openapi-required-request-vs-response.md) 決定 4 と同じ理由）。

## 結果

### 是正した 4 件

| 仕様書 | 誤り | 正 |
| --- | --- | --- |
| `docs/tests/FR-01` / `FR-06` / `FR-07` | `Tests/KnowledgePlatform.IntegrationTests/…` | `Tests/Knowledge.IntegrationTests/…`（[IADR-0062](./IADR-0062_namespace-assembly-unit-rename.md)） |
| `docs/functional/FR-13` | `WikiService.Api/Endpoints/…` | `WikiService.Api/Foundation/Endpoints/…`（[IADR-0027](./IADR-0027_composability-folder-structure.md)） |

### 変異試験（**落とす側と落とさない側の両方**）

| 変異 | 期待 | 実測 |
| --- | --- | --- |
| **M0（陽性対照）: 幽霊 `.cs` を `docs/tests/` へ置く** | 落ちる | **落ちた** |
| **M1: 是正した 1 行を改名前へ戻す** | 落ちる | **落ちた**（#592 受け入れ基準 4） |
| M2: **同じ幽霊パス**を `docs/specs/` へ置く | 落ちない | **落ちなかった** |
| M3: `...` 入りの不在パスを `docs/tests/` へ置く | 落ちない | **落ちなかった** |
| M4: submodule 配下の不在パスを `docs/tests/` へ置く | 落ちない | **落ちなかった** |
| M5: `deploy/wiki-js` を `docs/tests/` へ置く | 落ちない | **落ちなかった** |
| M6: **同じ幽霊パス**を `docs/adr/` へ置く | 落ちない | **落ちなかった** |

**M0 が要である。** M2〜M6 は「落ちない」ことを主張する変異なので、
**そもそも検査が動いていない場合と区別が付かない。** M2/M6 と**まったく同じ幽霊パス**を
`docs/tests/` へ置いて落ちることを先に示し、M3〜M5 は**同一ファイルへ**置いて形だけを変えている。

### ★ 初版は 0 件検査だった（正直に書く）

**初版は `walk()` へ絶対パスを渡しており、`walk` の catch が黙って空配列を返していた。**
必須仕様書を 1 件も読まないまま「違反 0 件」と報告し、**変異試験 M1 が通ってしまった。**

気づけたのは **M1 が落ちるべきなのに落ちなかった**からである
——**「テストを書いた」と「変更が守られている」は別**（[IADR-0159](./IADR-0159_openapi-dto-drift-checker.md)）を、また実地で踏んだ。

再発を止めるため 2 つ置いた:

- `main()` に**fail-closed の門**（必須仕様書 0 件なら fail）。本検査器が他の走査に既に持っている作法へ揃えた。
- 自己試験に**実データでの下限**（必須仕様書 1 件以上・`.cs` 参照 1 件以上）。

### 検出しないこと（正直に書く）

- **`.cs` 以外のパス**（`.ts` / `.yaml` / `.sh` / ディレクトリ）。決定 1 のとおり曖昧さが残るため
  **見逃す側に倒した**。`docs/tech/tech-requirements.md` の `src/index.ts` のような相対表記は今後も素通りする。
- **`docs/specs/` / `docs/adr/` / `docs/superpowers/` の不在パス 122 件**（`.cs` に限った数。決定 3）。
- **パスが実在しても中身が対応しているか**は見ない。ファイルが在れば通る。

## 申し送り

- **`.cs` 以外への拡張**は、偽陽性 6 クラスを 1 つずつ潰せる形が見つかってから行うこと。
  **「いま合っていないから」は理由にならない。**
- **★ 同じ穴が他の検査器にもある（実測した）。** 上記「初版は 0 件検査だった」を受けて
  `scripts/check-*.js` を走査したところ、**0 件走査の門を持たない検査器が 15 本**ある。
  そのうち **`check-openapi-dto-drift.js`（#525・自分が書いた）で実際に再現した** ——
  C# の `records` を空にすると `findDrift` が `[]` を返し、**「違反 0 件」として OK になる**:

  ```console
  $ node -e "…findDrift({Foo:{props:['a'],required:[]}}, {}, {entries:[]}, new Set())"
  [] → 違反 0 件として OK になる
  ```

  **grep の本数（15）を欠陥数として扱わない** —— 単一ファイルを読む検査器は「0」が正常な状態であり、
  1 本ずつ確かめないと分からない。**確かめたのは 1 本で、それは再現した。**
  本 PR の資源（#592 の仕様書パス）とは別なので**束ねず、別 issue へ送る**
  （[IADR-0139](./IADR-0139_domain-bundled-contract-prs.md) 決定 1「判定の単位は資源」）。
- **`docs/how-to/` と `docs/ai-workflow.md`**（不在 5 件）は必須仕様書ではないため対象外にしてある。
  必須へ格上げするなら `SOURCE_PATH_SPEC_DIRS` へ足すこと。

## 関連

- Supersedes: なし
- Superseded by: なし
