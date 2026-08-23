---
title: 作業仕様書 — トランスポート ratchet を check-event-topology.js の baseline 突合へ追加する（#921）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0027
  - ADR-0030
  - ADR-0052
  - IADR-0257
author: claude
created: 2026-08-23
updated: 2026-08-23
plan_refs:
  - "ADR-0027（非同期メッセージングを Wolverine へ移行する）"
  - "ADR-0030（バックエンドアプリケーション層標準）"
  - "ADR-0052（Wolverine 移行の完了条件。対応表の定義を部分改定）"
issue: "#921"
---

# 作業仕様書: トランスポート ratchet（#921）

## 起点

- `#921`「トランスポート ratchet を Wolverine 移行チェーンへ追加する」。
- 計画 `ADR-0027`（Wolverine 採用・MassTransit 不採用）／`ADR-0030`／`ADR-0052`。
- 先行記録: `IADR-0234`（移行境界）・`IADR-0245`（MT ⇄ Wolverine は相互運用しない。切替は辺単位）。

## 穴の実態（着手前の実測）

`scripts/check-event-topology.js` の `diffAgainstBaseline()` は、突合を **owner 名の集合だけ**で行う。

```js
/** 片側の owner 名を並べる（増減の突合は従来どおり owner 集合で行う）。 */
function ownersOf(side) {
  return Object.keys(normalizeSide(side)).sort();
}
```

```js
      const a = new Set(ownersOf(old[kind]));
      const b = new Set(ownersOf(cur[kind]));
      const lost = [...a].filter((x) => !b.has(x));
      const added = [...b].filter((x) => !a.has(x));
```

`normalizeSide()` が持っている値（`{owner: [transport...]}` の transport 配列）は
`Object.keys()` で**捨てられる**。したがって owner が同じまま `[masstransit]` → `[wolverine]`
へ変わっても差分は 0 件になる。トランスポートを見ているのは `transportMismatches()` だけで、
そちらは **同一イベント内の発行側と購読側の食い違い**しか見ない —— 辺の両側を同時に移せば
交差は空にならないので**何も言わない**。

**実測（着手前・develop 状態）:**

```
$ node scripts/check-event-topology.js
[check-event-topology] OK: イベント 6 件 / 購読 5 件が baseline と一致。
  ...
  RawDocumentFetched: 発行 [knowledge/DataSourceService(wolverine)] → 購読 [knowledge/ConversionService(wolverine)]
```

`event-topology-baseline.json` の `RawDocumentFetched` は**両側とも `["masstransit"]`** である。
つまり **#441 の辺移行（MassTransit → Wolverine）が baseline を一切更新しないまま緑で通っている**。
穴は理屈ではなく**現物で発生している**。

## 対象範囲

- 対象: `scripts/check-event-topology.js`（`diffAgainstBaseline()` へのトランスポート突合追加・
  `--update` の逆行ガード・自己試験）／`scripts/event-topology-baseline.json`（前進 1 件の記録）／
  `scripts/README.md` の該当行／`IADR-0257`（＋索引 1 行）。
- 対象外:
  - **発行検出 regex の拡張**（`Publish(変数)` / `Publish(メソッド呼び出し())` の 7 件）。issue が
    明示的にスコープ外とした（型解析が要り regex では原理的に埋まらない）。
  - **辺の移行そのもの**（C1〜C3）。本 ratchet は後続段の安全網であり、移行作業ではない。
  - `docs/tech/tech-requirements.md` の防壁表・`PartialMigrationSafetyValveTests.cs` の冒頭注記。
    いずれも `transportMismatches()`（発行側と購読側の食い違い）を述べており、本変更で偽に
    ならない（下の母集合 軸 4 で確認）。

## 母集合（規則 9・10。走査してから挙げる）

| 軸 | 走査 | 結果 | 扱い |
| --- | --- | --- | --- |
| 1 | `grep -rn "ownersOf(" --include=*.js scripts/` | 8 箇所（244 定義 / 274・275 突合 / 433・434 自己試験 / 518 `countSubscribers` / 585・587 孤児 notice） | 突合は 274・275 のみ。他の 5 箇所は**数える／並べる**用途でトランスポートを要さない（`countSubscribers` は 0 件走査ガード、585・587 は notice の表示）。**是正対象は 274・275 だけ** |
| 2 | `git ls-files \| xargs grep -ln "event-topology-baseline"` | `scripts/check-event-topology.js` / `scripts/README.md` / `.ai-context/specs/20260821_issue-455_event-topology-baseline.md` | 前 2 件を追随。3 件目は**凍結記録**（`.ai-context/specs/`）のため本文を書き換えない |
| 3 | `grep -rln -iE "masstransit" --include=*.js scripts/` | `check-event-topology.js` / `check-backend-libraries.js` / `scripts.repo.test.js` | `check-backend-libraries.js` は**ライブラリ混入**の ratchet（`using MassTransit` の新規混入を fail）で軸が違う。本変更と重複せず、触らない（他担当の作業ファイルでもある） |
| 4 | `git ls-files \| xargs grep -ln "check-event-topology"` | 16 ファイル | live な権威文書は `scripts/README.md`・`docs/tech/tech-requirements.md`・`.github/workflows/ci.yml`。README のみ追随（能力と自己試験件数）。tech-requirements の防壁表と ci.yml の配線は**本変更で偽にならない**（検査の呼び出し方も exit コードの意味も変えない）。残りは `.ai-context/` の凍結記録と C# の注記 |
| 5 | `git ls-files \| xargs grep -n "owner 集合\|owner 名"` | 5 件。うち本検査器は 243 行のコメント 1 件 | 「増減の突合は従来どおり owner 集合で行う」は本変更で**誤りになる**（規則 10）。同コメントを是正 |
| 6 | `git ls-files \| xargs grep -n "19 件"`（自己試験件数） | `scripts/README.md:35` / `.ai-context/specs/` 2 件 | README のみ更新。`.ai-context/specs/` の 2 件は**その時点の実測を凍結した記録**であり遡及しない |

**除外したものと理由**: 軸 3 の `check-backend-libraries.js`（軸違い＋他担当）、軸 2・4・6 の
`.ai-context/specs/` と `.ai-context/adr/`（凍結記録。`traceability.repo.md`「凍結の射程」）、
軸 4 の C# ソース（他担当領域かつ記述は `transportMismatches()` について）。

## 設計

### 1. 突合軸の追加（`transportChanges()`）

baseline と現状の**両方に居る owner** について transport 配列を比較する。増えた／減った owner は
従来どおり owner 差分の側で報告し、**二重報告しない**。

分類（`classifyTransportChange(baseTs, curTs)`）:

| 条件 | 判定 |
| --- | --- |
| どちらかが空（不明） | **保留**（`null`）。旧形式 baseline・`using` を持たないファイルで一斉に赤くしない。既存の `transportMismatches()` と同じ作法 |
| 集合が等しい | 変化なし（順序は正規化して比較する） |
| `wolverine` を失った、または `masstransit` が新たに混入した | **逆行**（regression） |
| それ以外（＝ `wolverine` の追加／`masstransit` の脱落のみ） | **前進**（forward） |

### 2. ratchet の向き（非対称）

- **前進**（`[masstransit]` → `[wolverine]` / 二重購読への拡張）: **違反として報告し exit 1**。
  ただし文面は「baseline の更新を忘れている」。`--update` で表を更新すれば通る。
  —— 移行は歓迎するが、**表の更新を伴わない移行は許さない**（baseline の形骸化を防ぐ）。
- **逆行**（`[wolverine]` → `[masstransit]` / `masstransit` の再混入）: **違反として報告し exit 1**。
  さらに **`--update` が書き込みを拒否する**（`--allow-regression` を明示しない限り）。
  —— これが無いと ratchet は「更新すれば消える指摘」に過ぎず、`ADR-0027`（MassTransit 不採用）の
  制約が baseline 更新 1 回で溶ける。escape hatch は残すが、**明示・記録必須**とする。

決定の根拠は `IADR-0257` に残す。

### 3. 触らないもの

- `transportMismatches()`（発行側 ⇄ 購読側の食い違い）は現状のまま。軸が違う。
- 二重購読を違反にしない既存方針も変えない（`IADR-0234` / `IADR-0245`）。

## 受け入れ基準

- [ ] `diffAgainstBaseline()` が、owner 名が同一でトランスポートだけが変わったケースを違反にする
- [ ] 逆行（`wolverine` → `masstransit`）と前進（`masstransit` → `wolverine`）を**文面で区別**する
- [ ] `--update` が逆行を含む表の書き込みを拒否する（`--allow-regression` で明示解除できる）
- [ ] 不明（空配列・旧形式 baseline）は従来どおり判定を保留し、既存の緑を割らない
- [ ] owner の増減は従来どおり検出される（**退行が無い**）
- [ ] 変異試験で「トランスポートだけを変えると落ち、戻すと通る」ことを実測する
- [ ] `node scripts/check-event-topology.js` が exit 0（baseline を実態＝`RawDocumentFetched` の
      Wolverine 化へ追随させたうえで）
- [ ] `scripts/README.md` の記述と自己試験件数を追随させる

## テスト方針

`--self-test`（自己試験）へ追加する。CI の `event-topology` ジョブが本走査の前に呼ぶ。

1. トランスポートだけが変わる（owner 名は不変）＝ **本 issue の中核**
2. 逆行の文面／前進の文面の区別
3. 二重購読への拡張（`[masstransit]` → `[masstransit, wolverine]`）は前進
4. `masstransit` の再混入（`[wolverine]` → `[wolverine, masstransit]`）は逆行
5. 不明（空配列・旧形式 baseline）は保留
6. owner が減った／増えたケースでトランスポート差分を**二重報告しない**
7. 配列の順序違いを差分としない

加えて**変異試験**（`docs/DEFINITION_OF_DONE.md`「機械検査を新設・改修する」＝変異試験の妥当性を必ず見る）:
実 baseline の 1 辺の transport だけを書き換えて本走査が **exit 1** になること、戻すと **exit 0** に
なること、owner 名だけを変えた従来ケースが**引き続き** exit 1 になることを実測する。

## 計画書との差異

- 差異: なし。`ADR-0027` の決定（Wolverine 採用・MassTransit 不採用）と `ADR-0052`（対応表は
  発行側と購読側の両方を要する）に沿って、対応表の**保存**を機械で担保するだけである。

## 未決事項

- 発行検出の網羅性（`Publish(変数)` の 7 件が見えない）は本 issue のスコープ外。`ADR-0052` 決定 2 が
  求める「発行検出の網羅性の担保」は別作業として残る。**本 ratchet は「見えている辺」しか守れない。**

## 実施結果

| # | 変更 | 内容 |
| --- | --- | --- |
| 1 | `scripts/check-event-topology.js` | `normalizeTransports()` / `classifyTransportChange()` / `transportChanges()` / `fmtTransportChange()` を新設し、`diffAgainstBaseline()` から呼ぶ。`--update` に逆行ガード（`--allow-regression` で解除）。ヘッダに設計要点 7 とその根拠。自己試験 **19 → 26 件** |
| 2 | `scripts/event-topology-baseline.json` | **`RawDocumentFetched` の両側を `masstransit` → `wolverine` へ**（実ソースは既に Wolverine 化していたのに表が追随していなかった＝穴の現物）。`$comment` に ratchet の説明 |
| 3 | `scripts/README.md` | 当該行に ratchet の説明を追記。自己試験件数 19 → 26。`--allow-regression` を明記 |
| 4 | `.ai-context/adr/IADR-0257_transport-ratchet-direction.md`（＋索引 1 行） | ratchet の向きを非対称にする決定 |

## 変異試験の証跡（`docs/DEFINITION_OF_DONE.md`「機械検査を新設・改修する」）

| # | 変異 | 期待 | 実測 |
| --- | --- | --- | --- |
| M1 | baseline の `RawDocumentFetched` 発行側 transport **だけ**を `wolverine` → `masstransit`（owner 名は不変） | fail | **EXIT=1**・「トランスポートが変わった: [masstransit] → [wolverine]」1 件 |
| M1' | M1 を戻す | pass | **EXIT=0** |
| M2 | baseline の `DocumentDeleted` 発行側を `wolverine` にする（実ソースは `masstransit`＝逆行） | fail（文面が「逆行」） | **EXIT=1**・「トランスポートが**逆行**した: [wolverine] → [masstransit]」 |
| M2-b | M2 の状態で `--update` | 書き込まない | **EXIT=1**・md5 不変（`b44b307e…` のまま） |
| M2-c | M2 の状態で `--update --allow-regression` | 書き込む | **EXIT=0**（escape hatch が生きている） |
| M3 | baseline の owner 名**だけ**を変異（transport は不変） | fail（従来どおり） | **EXIT=1**・「購読先が減った / 増えた」2 件（**退行なし**） |
| M4 | 実データの `RawDocumentFetched` を両側同時に `masstransit` へ戻した表を作り、**従前の判定軸**に当てる | 従前は 0 件 / 本 PR 後は 2 件 | 従前の軸（owner 集合）**0 件** / `transportMismatches()`（既存判定）**0 件** / `diffAgainstBaseline()`（本 PR 後）**2 件（逆行）**。**新しい軸だけが捕まえていることの帰属証明** |

検証コマンドと結果:

```
$ node scripts/check-event-topology.js --self-test   → EXIT=0（self-test OK: 26 件）
$ node scripts/check-event-topology.js               → EXIT=0（イベント 6 件 / 購読 5 件が baseline と一致）
$ node scripts/check-backend-libraries.js            → EXIT=0（読むだけ・編集していない）
$ node scripts/check-doc-type-vocabulary.js          → EXIT=0
```

## 残件・注意

- 発行検出の網羅性（`Publish(変数)` 形 7 箇所）は本 issue のスコープ外（`ADR-0052` 決定 2 が別途担保を求める）。
- transport が不明（空）なら判定を保留する。`using` を global usings 側へ移すと ratchet も黙る。
  **保留は「安全」ではなく「見えていない」。**
