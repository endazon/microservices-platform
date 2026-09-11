---
title: パスワードリセットの存在秘匿へ所要時間の軸を足し、Keycloak 前段の床を opt-in で置く
issue: "#1410"
plan_refs:
  - SC-15
  - SC-13
  - FR-05
  - ADR-0094
  - ADR-0078
adr_refs:
  - IADR-0432
status: in-progress
created: 2026-09-11
---

# 作業仕様書: リセット申請の所要時間の判定と床（#1410）

## 起点

計画 ADR-0094（Accepted / 2026-09-11・環流 planning#596 の裁定）。ADR-0078 決定 1 の
**所要時間の行**と着手可否の注記を部分改定する。

- **決定 1**: 所要時間の判定は「**反復した中央値が分かれないこと**」。測定条件を揃えた反復 ≥3、
  名前の長さを揃える、**最初の反復は捨てる**（暖機）、標本数は反復間で揃える。
  許容比は**自己対照**（実在側を 2 群に分けた中央値の比＝環境の測定ノイズ）から導く。
  自己対照が広ければ **`評価不能`**（合格にしない）。
- **決定 2**: 差は **Keycloak 前段の床時間**で均す。床の値は**実在側の分布の上側を覆う値**として
  実装が実測から定め、導出を IADR に残す。Istio の固定 delay は比を変えないので不可。
- **決定 4**: `check-password-reset-mail.js` の T-10 へ所要時間の軸を足し、決定 1 を満たさなければ赤。
  **走査件数と各反復の中央値を併記**。`評価不能` は緑にしない。**床が入るまで赤が続くことを受け入れる**。
  **ログイン経路（IADR-0427）は対象外**。

## 走査した母集合（規則 2・9・10）

着手前に、誤りの側の文字列で追跡下の全ファイルを走査した。

| 走査語 | 当たり | 扱い |
| --- | --- | --- |
| `T-10`（`docs/` `scripts/` `deploy/` `.github/`） | `docs/tests/SC-15_password-reset.md`（T-10 行）・`docs/tests/SC-13_login.md`（別経路の T-10）・`.github/workflows/integration-stack.yml` | SC-15 側を改定。SC-13 側は決定 4 の**対象外**なので判定は足さない |
| `所要時間`（同上） | `docs/screens/SC-15_password-reset.md` 210 / 214 / 284 行・`docs/screens/SC-13_login.md` 114 / 123 / 131 / 152 行・`docs/tests/SC-13_login.md` 90 / 122 / 124 / 143 行・`deploy/mail-relay/reset-gate.{js,yaml}`・`deploy/local/synthetic-monitor/probe.js` | **規則 10**: 「閾値は計画が実測後に定める」「閾値が定まっていない」は本 ADR で**誤りになる**。SC-15 は「定まった」へ、SC-13 は「定まったが本経路は明示的に対象外」へ改める。probe.js / reset-gate は**別の数値**（プローブ周期・タイムアウト）であり対象外 |
| `EnvoyFilter`（`deploy/`） | **0 件** | 本リポジトリに EnvoyFilter の先例は無い（配置の選択で効く） |
| `evaluateConcealment` / `normalizeConcealmentBody` | `scripts/check-password-reset-mail.js` のみ | 比較器の利用者は 1 つ |
| `makeAbsentUsername` | `scripts/check-password-reset-mail.js`（長さを揃えない版）/ `check-login-existence-disclosure.js` の `makeAbsentUsernameOfLength`（揃える版） | 🔴 **リセット側は長さを揃えていない**（22 文字固定）。決定 1 が要求するので揃える版へ替える |
| `reset-gate-script`（`scripts/k8s-local-up.sh`） | 1 件（`--from-file` の ConfigMap） | 床のスクリプトも同型で置く |

**除外理由**: `.ai-context/adr/` と `.ai-context/specs/` の確定済み記録は**凍結**（本文へ後付けしない）。
`src/ai-stock-trading`（submodule）は射程外。

## 設計

### 段 1 —— 検査器（決定 1・4）

`scripts/check-password-reset-mail.js` に**純関数**を足し、`run()` から呼ぶ。

| 追加 | 役割 |
| --- | --- |
| `median(xs)` | 中央値（偶数個は中点） |
| `splitAlternating(xs)` | 取得順の**交互**で 2 群へ分ける。前半／後半で分けると反復内のドリフトを丸ごとノイズに数えてしまう。交互なら両群が反復の全体にまたがる |
| `makeAbsentUsernameOfLength(realm, length)` | **バイト長を揃えた**非実在名。realm 宣言と突き合わせて不在を確かめる |
| `evaluateTimingConsistency(input)` | 決定 1 の判定そのもの。反復の配列を受け、**最初の 1 反復を捨て**、残りで判定する |

`evaluateTimingConsistency` の判定（各反復ごと）:

- `cross = max(実在の中央値, 非実在の中央値) / min(...)`（向きを問わない比。1 以上）
- `self  = max(実在群 A の中央値, 実在群 B の中央値) / min(...)`（**自己対照**＝環境の測定ノイズ）
- **赤**: どれか 1 反復でも `cross > self`
- **`評価不能`**: 赤でなく、かつ自己対照が**広い**反復がある
- **緑**: 上記以外

**前提の門**（満たさなければ赤）: 反復数 ≥ 3 / 反復間で標本数が揃っている / 片側 0 件でない /
自己対照の 2 群が各 2 標本以上。

🔴 **「自己対照が広い」の定義は計画が与えていない。実装が決め、IADR-0432 に導出を残す** ——
`self >= 2`（計画が記録した本経路の実測 1.9〜3.1 倍のうち**最小 1.9 倍**を上へ丸めた値）。
**ノイズ帯がそれ以上広い測定は、計画が既に見つけた最小の差すら再現できない**ので、
その緑は情報を持たない。**閾値ではなく可検出性の下限**である。

**出力**: 反復ごとに `n=… 実在 中央=… ms / 非実在 中央=… ms / 比=… / 自己対照=…` を併記し、
`評価不能` は `[T-10][評価不能]` の札で**緑と別に**出す。

**測定の形**: 反復 3・片側 6 標本。1 反復の中で実在／非実在を**交互に**打つ（ドリフトを両側へ等しく乗せる）。
最初の反復は暖機として捨てる（判定に使うのは 2 反復）。**T-16 / T-17 の後**に置く
（T-17 の「ちょうど 1 通」は先に測り終えている）。

### 段 2 —— 床（決定 2）

**置き場の選択は IADR-0432 が持つ。** 要旨:

- **Istio の EnvoyFilter / Lua は不可**。Envoy の Lua ストリームハンドルに **sleep が無い**。
  待てるのは `httpCall` の応答待ちだけで、**待たせる相手を別に用意しなければならない**。
- **`reset-gate`（#1245 の器）は要求経路に居ない**。SMTP を能動プローブして realm を PUT する
  制御ループであり、要求を握って待たせられない。
- ⇒ **同じ配備単位（`deploy/mail-relay/`）に、経路に居る部品を 1 つ足す**（`reset-floor`）。

`deploy/mail-relay/reset-floor.js`: Node 標準のみの逆プロキシ。上流応答を**全部受け切ってから**、
`起点 + FLOOR_MS` に達するまで待って書き出す。**本文・ステータス・ヘッダは 1 バイトも変えない**。

**既定はバイト等価**: `deploy/mail-relay/kustomization.yaml` は本部品を**参照しない**。
opt-in の overlay `deploy/local/edge-istio-reset-floor/` が
①部品 ②`msp-keycloak-edge` へ**リセット申請の経路だけ**を床へ向ける route の先頭挿入、を足す。
`scripts/k8s-local-up.sh` は `RESET_FLOOR=1` のときだけ ConfigMap と overlay を足す（既定 0）。

**床の値の導出**（IADR-0432 に記録）: 計画 ADR-0094 §コンテキストと課題 が記録した稼働 k3s の実測から、
**暖機（反復 1）を捨てた実在側の max の最大値 = 126 ms** を覆う値。50 ms 刻みで上へ丸めて **150 ms**。
🔴 **丸めの刻みだけが実装の選択であり、覆う対象は実測である。** 床を入れた構成での再実測は
`integration-stack.yml` が行う（決定 1 のフォローアップ 3）。

## 受け入れ基準

- [ ] `node scripts/check-password-reset-mail.js --self-test` が陽性・陰性対照込みで通る
- [ ] 合成した時系列で **赤 / 緑 / `評価不能`** の 3 値が撃ち分けられる（陰性対照＝床が効いた形）
- [ ] 非実在名が**実在名とバイト長一致**で、realm 宣言と突き合わせて不在である
- [ ] `node scripts/scripts.test.js` / `node scripts/reset-floor.test.js` が通る
- [ ] `helm lint` / `helm template` の既定描画が**バイト等価**（床は opt-in）
- [ ] `kubectl kustomize deploy/mail-relay` が**変わらない**／`deploy/local/edge-istio-reset-floor` が描画できる
- [ ] 文書検査器一式（trace ブロック・語彙・リンク・横断参照・ID 修飾・KG・テスト追跡・読書量・採番）が通る

## テスト方針

- 決定 1 の判定は**純関数**に閉じ、`--self-test` が合成時系列で撃つ（稼働クラスタ不要）。
- 床の保持は `holdDelayMs()` を純関数として切り出し、`scripts/reset-floor.test.js` が
  **マニフェストの env と実装の要求キーの突合**込みで固定する（`reset-gate.test.js` と同型）。

## 計画書との差異

- 差異: なし。**計画が委譲した 2 点**（`評価不能` の境界・床の値）は実装が決め、IADR-0432 に導出を残す。

## 未決事項

- 🔴 **床は稼働クラスタで打っていない**（本作業機にクラスタが無い）。「動くはず」を実測として書かない。
  床を入れた構成の再実測は `integration-stack.yml`（`RESET_FLOOR=1`）で行い、計画へ環流する。
- 床が実在側の分布を覆えなかった場合の裾の扱いは計画が未裁定（ADR-0094 §残るもの）。
