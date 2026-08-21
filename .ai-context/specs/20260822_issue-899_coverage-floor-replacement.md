---
title: 作業仕様書 — テストプロジェクト追加で割れたカバレッジ床を置き直す（#899）
type: spec
status: done
related_ids:
  - NFR
  - IADR-0118
  - IADR-0123
author: claude
created: 2026-08-22
updated: 2026-08-22
plan_refs:
  - "ADR-0030（バックエンドアプリケーション層標準）"
issue: "#899"
---

# 作業仕様書: カバレッジ床の置き直し（#899）

## 起点

- 実装 issue: `#899`（`ci-failure-issue.yml` が自動起票）
- 原因コミット: `071e356`（`#897` / U4 のマージ）

🔴 **これは自分が入れた退行である。** `IADR-0232` が「PR が緑でもここが赤ければ、その退行は
入っている」と予告した経路そのもので現れた —— 統合テストと床判定は PR から後段（develop への
push）へ移してあり、**PR の CI 41 件は全て緑だった**。

## 実測（推測を挟まずに測った）

### 二分：直前のコミットでは通っていた

| run | commit | 内容 | 結果 |
| --- | --- | --- | --- |
| #5 | `b93d469` | U4 の 1 つ前 | **success** |
| #6 | `071e356` | **U4 のマージ** | **failure** — line 38.5% < 床 39 |
| #7 | `1ce0196` | `#891` のマージ | failure — line 38.49% < 床 39 |

### 2 つの run のログを並べた差分

| 項目 | `b93d469`（OK） | `071e356`（NG） | 差 |
| --- | --- | --- | --- |
| レポート件数 | **15** | **16** | +1 |
| `platform` 行数 | 12275（被覆 2962） | **13224**（被覆 2971） | **+949 行 / 被覆 +9** |
| `lines-valid` | 34088 | 35037 | +949 |
| `<sources>` | 3 個 | **4 個**（`/src/platform/backend/Shared/` が増える） | +1 |

**+949 行が分母へ入り、そのうち被覆はわずか 9 行**である。

### 🔴 根本原因は「新しく可視化された未テストコード」ではなく **レポート間の二重計上** である

**最初はこう考えた** —— 「`Platform.Shared.Infrastructure` は従来どの試験からも計測されておらず、
`Platform.Shared.Infrastructure.Tests` の新設で初めて分母へ入った」。**これは誤りである。測って分かった。**

`ConversionService.Worker.Tests` の Cobertura を単独で出力して数えたところ:

```
Platform.Shared.Infrastructure クラス数: 51
例: platform/backend/Shared/Platform.Shared.Infrastructure/Foundation/Pipeline/PipelineExtensions.cs …
```

**共有ライブラリは既に、それを参照する各テストプロジェクトのレポートへ 1 部ずつ入っていた。**
`check-coverage-floor.js` はレポートを**跨いで重複排除しない**ので、`Platform.Shared.Infrastructure`
の行は**参照するテストプロジェクトの数だけ重複して分母に載る**。

新設した `Platform.Shared.Infrastructure.Tests` の単独レポートを実測すると:

```
<coverage line-rate="0.0116" lines-covered="9" lines-valid="772" …>
sources: ["/home/user/microservices-platform/src/platform/backend/Shared/"]
```

**被覆 1.16% の「もう 1 部」が加わった。** 加重平均が下がるのはこのためである。

🔴 **この二重計上は本作業が作ったものではなく、以前から在った。** `integration.yml` のコメントが
「**2 回実行すると行の分母が 2 倍になり床が割れる**」と警告しているのと同じ性質が、
**テストプロジェクトを 1 つ増やしただけでも**（規模は小さいが）起きる。

## 決定：床を置き直す（39 → 38）。**ただし「引き下げ」ではなく測定基盤の変化への追随である**

`src/coverage-floor.json` は同じ判断を**過去 2 回**下している。

- `#571`: line 34 → 33 —— 「**これは引き下げではなく定義変更に伴う置き直しである**」
- `#574`: line 33 → 39 / branch 17 → 27 —— 同上

本件も同型である。**被覆の実態は 1 行も悪化していない** —— 既存テストは 1 件も減らず
（`#891` の -2 行は撤去した `FindRepoFile` / `FindRepoFileForTests` が被覆済みだったため）、
分母だけが二重計上でふくらんだ。

### 却下した案

| 案 | 却下の理由 |
| --- | --- |
| **床を満たすまでテストを足す**（+132 行の被覆が要る） | `Platform.Shared.Infrastructure` の被覆を上げること自体は正しいが、**数字を戻すために書くテストは動機が逆**である。別 issue で、テスト自身の価値のために書く |
| **集計をレポート跨ぎで重複排除する** | **本来の正しい直し方だが、測定定義の変更であり床の置き直しと IADR を伴う**（`IADR-0123` 決定 7 の作法）。develop が赤い状態で急いでやる変更ではない。**別 issue へ出す** |
| **新テストプロジェクトの計測範囲を絞る** | coverlet のフィルタはアセンブリ / クラス単位で、1 アセンブリの中の 2 ファイルだけを残す形は脆い。かつ**未テストであること自体は隠すべきでない** |

### 新しい床の値

**実測 `line 38.49%`（9967/25896・run 32521971071 / commit `1ce0196`）を切り下げて 38。**

- 余裕は **0.49pt ≒ 被覆 127 行**。過去の置き直し（`#574`）が採った余裕 0.42pt と同等以上である。
- **branch は 27 のまま据え置く** —— 実測 27.32%（1838/6727）で床を上回っており、動かす理由が無い。

## やること

1. `src/coverage-floor.json` の `backend.line` を **39 → 38**、`$comment` へ本件の測定条件と根拠を追記
2. 追随 issue を 2 本起票する
   - 集計のレポート跨ぎ重複排除（測定定義の変更 ＋ 床の置き直し ＋ IADR）
   - `Platform.Shared.Infrastructure` の被覆を上げる
3. `#899` を閉じる（床の置き直しで develop が緑に戻ったことを確認してから）

## 受け入れ基準

1. `src/coverage-floor.json` の `backend.line` が **38**、`branch` は **27** のまま
2. `$comment` に**測定条件（run / commit / 実測値 / 余裕）**と、**二重計上という機構**が書かれている
3. `node scripts/check-coverage-floor.js --self-test` が EXIT=0
4. develop の `Integration` が緑に戻る（**マージ後に実測して確認する。宣言で終わらせない**）
5. 追随 issue 2 本が起票済み

## 🔴 検証の限界（正直に書く）

**この環境では床判定を実走できない。** `check-coverage-floor.js` は Cobertura レポートを
`src/**/TestResults/` から集める設計で、**統合テストを含む全量**の計測が要る。本環境は
**Docker デーモンが動いておらず**、統合テスト 26 件が skip されるため、**CI と同じ母集合を
作れない**。

したがって **床 38 が正しいことの根拠は CI の実測値そのもの**であり、
**確証はマージ後の `Integration` が緑になることで得る**（受け入れ基準 4）。
`coverage-floor.json` の既存コメントも同じ限界を記録している
（「この 33 は CI のログを直接読んだ実測値ではなく、CI が通ることで検証される下限である」）。

## 実装後に確定した結果

| 項目 | 値 |
| --- | --- |
| `src/coverage-floor.json` | `backend.line` **39 → 38** / `branch` **27 のまま** |
| 起票した追随 issue | **#900**（集計のレポート跨ぎ重複排除＝正しい直し方）／ **#901**（`Platform.Shared.Infrastructure` の被覆を上げる） |
| `check-coverage-floor.js --self-test` | **80 件 OK / EXIT=0** |

**受け入れ基準 4（develop の Integration が緑に戻る）はマージ後に実測して確認する。**
本環境では床判定を実走できないため（上記「検証の限界」）、**宣言で終わらせない。**
