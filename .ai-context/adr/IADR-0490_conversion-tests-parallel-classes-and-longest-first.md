---
title: IADR-0490 ConversionService.Tests の実時間の期限を待つ試験は、検査を変えずにクラスを分けて並列にし、最長の 1 本を最初に起動する
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0232, IADR-0008]
author: claude
created: 2026-10-02
updated: 2026-10-02
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (NFR: 運用・保守)
related_specs:
  - ../specs/20261001_1686_conversion-tests-speed.md
---

# IADR-0490: ConversionService.Tests は試験を分けて並列にし、最長の 1 本を最初に起動する（#1686）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-02
- 決定者: claude（#1686 の 2026-10-01 利用者裁定「ConversionService.Tests の高速化」）

## 起点・関連

- 関連する計画書 ID: 無採番の NFR（CI の所要時間というメタ作業）
- 関連する実装 ADR: [[IADR-0232]]（PR の待ち時間＝ループ 1 周の長さ。決定 1 の判断基準「速くなるが精度が落ちない手段は無条件で採る」）、
  [[IADR-0008]]（本文変換の外部プロセスの期限。T-50 の試験の対象）

## コンテキストと課題

ci-latency の律速は knowledge の CI 脚で、その中の ConversionService.Tests が約 45 秒かかっていた（#1686）。
利用者裁定は「閾値は緩めず、ConversionService.Tests 自体を速くする。試験を消す・skip する・弱めることはしない」。

計測（作業仕様書）で、遅さの正体は **1 クラスの直列**だと分かった。`ExternalProcessTimeoutTests`（10 件）は
本文変換の期限・プロセスの kill・刈り取りの上限を**実時間で**待つことが検査そのもので、合計 27〜29 秒。
xUnit はクラスを 1 コレクションとし、その中を直列に走らせるため、試験プロジェクト全体の span（28〜33 秒）がこの 1 本で決まっていた。

## 検討した選択肢

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A | 待ちを `TimeProvider`／偽の時計へ置き換える | ✗ 待っているのは本番の実プロセス（`sh`＋`sleep`）の kill と刈り取りであり、時計を差し替えると**プロセスツリーを実際に止めること**を検査しなくなる。弱める |
| B | 期限（`HangTimeout` 2 秒）や刈り取りの上限（本番 10 秒）を試験で短くする（本番に注入点を足す） | ✗ `HangTimeout` は「止まる命令が期限の内に番号を書ける」ための下限（揺らぎの実測から置かれた）。刈り取りの上限を差し替えると本番の値を通らなくなる。検査が変わる |
| C | 試験プロジェクト全体の並列度（`maxParallelThreads`・`aggressive`）を上げる | △ コレクションの中は直列のままなので律速は縮まない。時間の検査を持つ他のクラスまで負荷を上げる |
| **D** | **`ExternalProcessTimeoutTests` を入れ子のクラス（＝別コレクション）へ分け、互いに並列に走らせる**（**採用**） | ○ 試験の本文は字下げ以外変わらない。直列の足し算が並列になる |
| **E** | **D に加え、最長の 1 本（`DetachedGrandchild` ≒ 12 秒）を最初に起動する順序づけを置く**（**採用**） | ○ xUnit v3 の既定はコレクションを無作為な順に起動するため、D だけだと最長の 1 本が後ろに回った回で「開始の遅れ ＋ 12 秒」に伸びる（実測 span 12.8〜16.7 秒）。起動の順だけを変え、検査には触れない |

## 決定

1. `ExternalProcessTimeoutTests` は helper だけを持つ `static class` とし、試験を T-50 の番号の組で入れ子のクラス 6 つ
   （`HungConverter` / `TimedOutJob` / `NormalExit` / `VersionProbeTimeout` / `ExitedBeforeTimeout` / `DetachedGrandchild`）へ分ける。
   各入れ子のクラスに元と同じ `[Trait("TestKind", "Integration")]` を付ける（外側の Trait は入れ子へ継がれない）。
2. 試験プロジェクトに `StartsFirstCollectionOrderer`（`[assembly: TestCollectionOrderer]`）を置く。既定の無作為な順を取ったうえで、
   `[StartsFirst]` の付いたコレクション定義のコレクションを先頭へ寄せる。`DetachedGrandchild` だけを定義つきのコレクションへ入れ、印を付ける。
3. 本番コードは変えない（案 B を採らない）。

## 理由

- IADR-0232 決定 1 の判断基準「速くなるが精度が落ちない手段は無条件で採る」に当たる。D・E は試験の集合・本文・期限・検査を変えない
  （前後の trx で試験名と結果の組 232 件が一致。`git diff -w` で試験メソッドの行に差分なし）。
- 並列にして共有される状態は無い（プロセス番号のファイル・原本・DB 名は試験ごとの GUID、helper は状態を持たない）。
  時間の検査は下限か余裕つきの上限で、負荷で遅くなる側には緩い。

## 結果

- 正: 単独実行の Duration 28〜33 秒 → 12〜13 秒。同じ脚の 4 試験プロジェクト同時実行の下でも 29〜35 秒 → 14〜17 秒（各 3 回・カバレッジ収集あり）。
- 負: 試験の完全名が `ExternalProcessTimeoutTests+<組>.<メソッド>` に変わる（`--filter` で完全名を引く運用は無い。試験仕様書・カバレッジ基準はファイル名で引く）。
- 負: ConversionService.Tests の下限として `DetachedGrandchild` の約 12 秒が残る（期限 2 秒 ＋ 本番の刈り取りの上限 10 秒。縮めると検査が変わる）。
- 試験の順序づけは ConversionService.Tests のアセンブリに閉じる。他の試験プロジェクトの順序は変わらない。
- 負（検知されない劣化）: `[Trait("TestKind","Integration")]` は入れ子クラスごとに付く。付け忘れても CI は落ちない（CI の選別は `Category`。`TestKind` は振り分けに使われない）。IADR-0368 がトップレベルのテストクラスの構文検査を入れるときは、入れ子クラスも対象に含める。順序づけの登録が外れても試験は緑のままで、所要だけが戻る（独立監査で実測）。
- 負: 短縮幅はコア数に依存する（1 コアに絞ると前後差なし）。CI の脚での実測は PR の run で確かめる。
- 新たに実時間を長く待つ試験を足したら、それが下限を超えるなら同じ印（`[StartsFirst]` のコレクション定義）を付ける。

## 関連

- 作業仕様書: `.ai-context/specs/20261001_1686_conversion-tests-speed.md`（計測の前後表・遅い試験の母集合）
