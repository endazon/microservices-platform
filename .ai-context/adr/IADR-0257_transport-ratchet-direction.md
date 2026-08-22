---
title: IADR-0257 トランスポート ratchet の向きを非対称にする（前進は baseline 更新を促し、逆行は --update でも通さない）
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - ADR-0027
  - ADR-0030
  - ADR-0052
author: claude
created: 2026-08-23
updated: 2026-08-23
plan_refs:
  - "ADR-0027（非同期メッセージングを Wolverine へ移行する）"
  - "ADR-0052（Wolverine 移行の完了条件）"
issue: "#921"
---

# IADR-0257: トランスポート ratchet の向きを非対称にする

- 状態: Accepted
- 日付: 2026-08-23
- 決定者: claude（実装担当）／起点 issue `#921`

## 起点・関連

- 関連する計画書 ID: `ADR-0027`（Wolverine 採用・MassTransit 不採用）／`ADR-0030`／`ADR-0052`（対応表は
  発行側と購読側の両方を要する）
- 関連する実装記録: `IADR-0234`（移行境界）／`IADR-0245`（MT ⇄ Wolverine は相互運用しない。切替は辺単位）
- 関連する仕様書: `.ai-context/specs/20260823_issue-921_transport-ratchet.md`

## コンテキストと課題

`scripts/check-event-topology.js` は Wolverine 移行の「正解表」を凍結する検査器である。しかし
baseline 突合（`diffAgainstBaseline()`）は `ownersOf()`＝`Object.keys()` で **transport 配列を捨てて**
おり、owner 名が変わらない限り差分は 0 件だった。同ファイルの `transportMismatches()` は
**同一イベント内の発行側 ⇄ 購読側の食い違い**しか見ないため、**辺の両側を同時に移すと誰も何も言わない**。

実測（`#921`・2026-08-23）: `RawDocumentFetched` は実ソースが両側とも Wolverine 化していたのに
baseline は両側とも `["masstransit"]` のままで、検査は **exit 0（緑）** を返していた。理屈ではなく
現物で穴が開いていた。

ここで決めるのは「transport の変化を突合する」ことそのものではなく（それは `#921` のスコープで
自明である）、**変化の向きごとに何を要求するか**である。

## 検討した選択肢

1. **対称**（前進も逆行も「baseline を更新すれば通る」指摘にする）
2. **非対称**（前進は baseline 更新を促す違反。逆行は違反かつ **`--update` が書き込みを拒む**。
   明示フラグ `--allow-regression` でのみ解除できる）
3. **逆行だけを違反にし、前進は黙って許す**（`--update` を促さない）

| 軸 | 1 対称 | 2 非対称 | 3 逆行のみ |
| --- | --- | --- | --- |
| `ADR-0027`（MassTransit 不採用）の担保 | ✗ baseline 更新 1 回で溶ける | ✓ 明示と記録を要求する | ✓ |
| 正解表（baseline）の鮮度 | ✓ | ✓ | ✗ 前進が記録されず表が形骸化する |
| 移行作業の摩擦 | 低 | 低（`--update` 1 回） | 低 |
| 撤退の余地 | ある | ある（escape hatch を明示） | ある |

## 決定

**選択肢 2 を採る。**

- **前進**（`masstransit` → `wolverine`、二重購読への拡張、`masstransit` の脱落）は違反として
  **exit 1** で報告するが、文面は「baseline の更新を忘れている」とし、`--update` で通る。
- **逆行**（`wolverine` を失う、または `masstransit` が新たに混入する）は違反として **exit 1** で
  報告し、**`--update` も書き込みを拒否する**。解除には `--allow-regression` の明示が要る。
- **不明（transport が空）は判定しない。** 旧形式 baseline（owner の配列）や `using` を持たない
  ファイルで一斉に赤くしない。既存の `transportMismatches()` と同じ作法である。

## 理由

- **ratchet は「戻れないこと」で意味を持つ。** 逆行を前進と同じ扱い（更新すれば消える指摘）に
  すると、`ADR-0027` が確定した「MassTransit 不採用」は baseline 更新 1 回で溶ける。検査器が
  `ADR` の制約を代弁するなら、代弁の強さは制約の強さに合わせる。
- **前進を黙って許さないのは、正解表が移行の唯一の判定基準だからである。** 表が更新されない
  まま移行が進むと、次の退行を「表と違う」と言えなくなる（`ADR-0027` §決定 末尾が対応表を
  求めた理由そのもの）。
- **escape hatch を残すのは、検査器が撤退を物理的に不可能にすべきではないからである。**
  ただし逆行は**明示と記録**（本 IADR と同格の根拠）を要求する。

## 結果

- 良い影響: 辺単位の移行（`IADR-0245` の「停止 → 排出 → 切替」）が baseline の更新を**必ず伴う**。
  移行チェーンの後続段（C3 等）は「表が変わっていないこと」を根拠として使えるようになる。
- 悪い影響・トレードオフ: 辺を移す PR は `--update` の差分を必ず含む（レビュー対象が 1 ファイル増える）。
  これは意図した摩擦である。
- **限界（承知のうえで残す）**: transport が不明（空）なら判定を保留するため、`using` を
  global usings 側へ移すと transport が見えなくなり、ratchet も黙る。**保留は「安全」ではなく
  「見えていない」**である。加えて発行検出そのものの網羅性の穴（`Publish(変数)` 形が見えない。
  `ADR-0052` 決定 2 が担保を求めている）は本決定では埋まらない —— **見えている辺しか守れない。**
- フォローアップ: 発行検出の網羅性（`#921` がスコープ外とした 7 箇所）は別 issue。

## 関連

- Supersedes: なし
- Superseded by: なし
