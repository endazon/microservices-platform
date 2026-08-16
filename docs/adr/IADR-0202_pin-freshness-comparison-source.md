---
title: IADR-0202 計画 pin の鮮度検知は比較の向きを検査し、比較元を必ず出す（案 A のネットワーク fetch は採らない）
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - IADR-0119
  - IADR-0142
  - IADR-0170
author: claude（実装）
created: 2026-08-15
updated: 2026-08-16
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/README.md"
---

# IADR-0202: pin 鮮度検知の比較元（#749）

- 状態: Accepted
- 日付: 2026-08-15
- 決定者: claude（実装）

## 起点・関連

- **NFR**（着手ゲートの検知装備。メタ作業のため計画側の非機能要件表に当たる番号が無い。
  `.claude/rules/traceability.md` の無採番 `NFR` の 2 に当たる）。実装 issue: **#749**
- 作業仕様書: [20260815_issue-749](../specs/20260815_issue-749_pin-freshness-reverse-comparison.md)
- 改定対象: **[IADR-0170](./IADR-0170_planning-pin-freshness-detection.md)**（本検査器の設計）。
  fail-open ・issue 通知・分類の 3 決定は維持し、**比較元の扱いだけを足す**（Supersede しない）
- 制約: [IADR-0119](./IADR-0119_fr17-21-hold-until-adr-fixed.md) ／
  [IADR-0142](./IADR-0142_fr19-20-scoped-release-by-overturn-range.md)（着手条件）

## コンテキストと課題

`scripts/check-planning-pin-freshness.js` は submodule 内の `origin/HEAD` / `origin/main` /
`origin/master` を比較元として解決していた。**その `origin` は GitHub ではなく隣接クローン
`/home/user/project-planning` を指しており、誰も更新しないため pin より後ろにあった。**

結果、`git diff <新しい pin> <古い比較元>` という**逆方向の比較**となり、計画側に
`ADR-0046` が新設された状態で「着手可否に効く変更はありません」と報告した（#749）。

**分類器は正しく動いていた。壊れていたのは入力である。** そして**出力を読んでも比較相手が
分からなかったため、誤りに気づく手がかりが 1 つも無かった。** 本検査器は `scripts/setup.sh`
（SessionStart hook）から毎セッション呼ばれるため、この緑は「確認済み」と読まれる。

## 検討した選択肢

| | 案 A: 比較前にネットワーク fetch | **案 B: 祖先判定で逆方向を検出** | 案 C: キット版へ乗り換え |
| --- | --- | --- | --- |
| 効果 | 根治（正しい比較元を得る） | 対症（**誤った緑を止める**。正しい比較はできない） | 比較そのものをやめ pin の日付を見る |
| ネットワーク | **必要**（SessionStart hook が依存する） | 不要 | 不要（`--fetch` は opt-in） |
| upstream URL | `.gitmodules` もローカルパスを指し得るため**正準 URL の直書きが要る** | 不要 | 不要 |
| 失敗の見え方 | fetch 失敗時は既存の fail-open へ落ちる | 「比較できていない」と明示できる | 「乖離あり」の理由が分からない |
| 副作用 | 毎セッションの起動が遅くなる／認証が要る環境で毎回失敗する | なし | **IADR-0170 決定 3（着手可否に効くかの分類）を失う** |

## 決定

1. **案 B を採る。** `git merge-base --is-ancestor` を両向きに引き、位置関係を
   `same` / `forward` / `reverse` / `diverged` / `unknown` に分類する。
   **`reverse` / `diverged` では「着手可否に効く変更はありません」と報告しない。**
   `unknown`（浅いクローン等）は従来どおり続行するが、**向きを判定できなかった旨を出力へ添える。**
2. **fail-open は維持する。** `reverse` / `diverged` でも exit 0 とし、`warn()` の注釈で出す
   （IADR-0170 決定 1 を変えない。SessionStart と CI を止めない）。
   **代わりに `GITHUB_OUTPUT` へ `comparison=<relation>` を出し、夜間ワークフローが
   「pin が古い」とは別タイトルの issue を立てる。** 警告注釈だけでは緑のジョブに埋もれる。
3. **比較元をどこから取ったかを、全経路の出力に必ず含める**（ref 名・commit・remote URL・
   fetch の成否）。**`origin` が URL ではなくローカルパスなら、その旨を添える。**
4. **案 A は採らない。** SessionStart hook をネットワーク・認証に依存させる代償が、得られる根治に
   見合わない。**そもそも本検査器は「pin を進める判断のきっかけ」であって、判断そのものではない**
   （IADR-0170）。案 B があれば、比較できていない状態は**黙って緑にならず必ず表に出る。**
5. **案 C（キット版への乗り換え）は本 issue では採らない。** 539 行の差分があり、
   IADR-0170 決定 3（着手可否に効く分類）を失うリスクの突合が先である。本ファイルは
   `scripts/kit-sync-classification.json` で分類 B（本リポが originate）であり、**是正をキットへ
   環流するのが順序として正しい。**

## 理由

- **#749 の実害は「比較できていないこと」ではなく「比較できていないのに緑を返したこと」である。**
  案 B はその 1 点を構造的に塞ぐ。案 A は塞がない —— fetch が成功しても、認証・プロキシの都合で
  古い ref を掴む経路は残り、**そのときまた黙って緑になる。**
- 案 A と案 B は排他ではないが、**先に入れるべきは B である**（issue 本文も同旨）。
  B が入っていれば、A の失敗も検出できる。逆は成り立たない。
- 出力に比較元を出すのは**コストゼロで、今回の誤りを人が見つけられた唯一の手段**である。

## 結果

- 良い影響: 逆方向・分岐の比較で緑を返さない。比較元が出力に出るため、`origin` の指す先が
  おかしいことがセッション開始時のログから読める。
- 悪い影響・トレードオフ: **正しい比較は依然できない。**「比較できていない」と分かるだけである。
  `unknown`（判定不能）は従来どおり続行するため、浅いクローンでは検出力が落ちる。
- フォローアップ:
  - **キット版との突合（案 C）を別 issue で起票する。** 本リポの是正をキットへ環流する（分類 B）。
  - 案 A が要ると判断される場合（例: 逆方向の検出が常態化した場合）は、**本 IADR を改定する**。
    実装側の判断だけで fetch を足さない。

［2026-08-16 追記 / #773］**決定 4 に反して、実装は既定でネットワーク fetch していた**（本 IADR を
起こした当の PR に入っていた）。`resolveComparisonSource` の既定が `{ fetch = true }` で、CLI は
`--no-fetch` を opt-in にしていたため、**フラグを渡さない本番の 2 経路**（`scripts/setup.sh` の
SessionStart hook ／ 夜間ワークフロー `planning-pin-freshness.yml`）が**そのまま案 A になっていた**。
フェーズ末のクロス監査が検出した（#773）。

**決定は変えていない。実装を決定へ合わせた**（新 IADR は立てない）。既定を `{ fetch = false }` とし、
fetch は **`--fetch` の opt-in**（キット版の正準名に合わせた。旧 `--no-fetch` は既定が反転すると
no-op になり、**逆の既定を読ませる**ため残さない）。再発は `scripts/scripts.repo.test.js` の
`pin 鮮度 #773: …` 3 件が止める —— **CLI のフラグ解析**と**関数の既定引数**の 2 つが変異点であり、
どちらを戻しても落ちる（本番経路が `--fetch` を渡さないことも固定した）。

**この型は通常の CI では捕まらない。** 検査器は fetch の成否に関わらず続行し、`--self-test` も
緑のままである。**ADR と実装の突き合わせでしか出ない。**

## 関連

- Supersedes: なし（IADR-0170 は有効。決定 1〜3 を維持したまま比較元の扱いを足す）
- Superseded by: なし
