---
title: IADR-0629 live の後始末は test が積んだ逆順に /bff/* へ直接送り、残骸掃き（sweep）は接頭辞 e2e- の一致だけを、既定 dry-run・使い捨てのスタックだけに向けて teardown の失効の前に流す
type: impl-adr
status: Accepted
related_ids: [NFR, ADR-0032, IADR-0624, IADR-0625]
author: claude
created: 2026-10-25
updated: 2026-10-25
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0032（SPA は BFF セッション）
related_specs:
  - ../specs/20261025_2030_e2e-live-cleanup-sweep.md
---

# IADR-0629: live の後始末の共通部品と、使い捨てのスタックだけに向ける sweep（#2030）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-25
- 決定者: claude（#2030 / E-50。条件はオーケストレーターの指示 2026-10-25: 既定 dry-run・`e2e-` 以外を消さない単体テスト・使い捨てのスタックの外へ向けない）

## 起点・関連

- 起点 issue: #2030（親 #2013 の基準書 §7・live-poc.md §3・§7）。関連: [IADR-0624](./IADR-0624_live-playwright-config-setup-and-readiness.md)（setup・teardown）・
  [IADR-0625](./IADR-0625_e2e-live-workflow-disposable-stack.md)（e2e-live の使い捨てのスタック）。
- 作業仕様書: [20261025_2030_e2e-live-cleanup-sweep](../specs/20261025_2030_e2e-live-cleanup-sweep.md)。

## コンテキストと課題

波 4（L3・live 書き込み）の test は `e2e-<runId>-` を付けて作り、afterEach で消す。消し損ねた残りは sweep が拾う（基準書 §7）。
設計（live-poc.md §3）は sweep を `--live [--older-than 24h] [--dry-run]` とし、常設の PoC で nightly に流す想定だった。
一方、PoC と使い捨てのスタック（e2e-live の k3d）は**エッジの URL がどちらも `https://localhost`** であり、URL では向け先を区別できない。
また BFF のセッションは live-setup が書く storageState にしか無く、teardown が実行の終わりに失効させる（IADR-0624）。

## 決定

1. **後始末の共通部品**（`e2e/live/support/cleanup.ts`）: test は `track(名前, 手順…)` で積み、afterEach で `run(testInfo)` を呼ぶ。
   積んだ**逆順**に、各手順を `/bff/*` へ**直接**送る（`X-MSP-CSRF` つき。画面を経由しない）。404 は消えたとみなす。失敗しても残りを続け、
   消せなかったものを「未削除一覧」として attach する（test は落とさない。残りは sweep が拾う）。名前が `e2e-` で始まらない・経路が `/bff/` で
   始まらないものは**積む時点で例外**。
2. **sweep**（`scripts/e2e-live-sweep.js`。Node 標準だけ）: 一致は **`name.startsWith('e2e-')` だけ**（大文字小文字を区別）。サーバ側の
   絞り込みは候補を減らすだけ。**既定は dry-run**（GET だけ。dry-run の経路で GET 以外を送ろうとしたら例外）、消すには `--delete`。
3. **向け先の門**（要求の前）: `--live`（#1550）が無ければ exit 3。`--edge-url`・`--auth-dir`・`--stack` は既定値を持たない（無ければ exit 2）。
   `--stack` は **`DISPOSABLE_STACKS = ['e2e-live']`** だけを受け、kubectl の current-context が **`k3d-<stack>`** でなければ止める（exit 2）。
4. **流す場所**: セッションは teardown が失効させるので、sweep は live の実行の中で、**teardown が失効の前に**子プロセスとして呼ぶ。
   `E2E_SWEEP`（`dry-run` / `delete`。未設定は流さない）と `E2E_SWEEP_STACK`（`E2E_SWEEP` を与えたら必須）で選ぶ。sweep が失敗しても
   失効は必ず済ませ、最後に exit 1。e2e-live は `E2E_SWEEP=dry-run`・`E2E_SWEEP_STACK=${CLUSTER}` で流す（消さない。スタックごと消える）。
5. **種類**: 文書・ABAC ポリシー・データソース・タグ（admin）、個人資料（e2e-user。論理削除 → 完全削除）、MCP クライアント（admin。
   **BFF に削除の口が無い**ので列挙だけ）。タグは使用中だと 409 になるので最後に消す。AST の監視銘柄は AST の live の持ち物なので入れない。
6. **`--older-than` は持たない。** 窓は常設の PoC で並行する実行の作りかけを消さないためのものだったが、向け先を使い捨てのスタックに限ったので
   並行する実行が無い。タグには作成日時も無い。

## 理由

- 決定 3: URL で区別できない以上、向け先の識別は「どのクラスタへ当たっているか」でしか取れない。kubectl の context は既存の検査器
  （`check-bff-multi-replica-session.js`）も最初に読む情報である。許可を名前の一覧（1 件）にしておけば、PoC の context を誤って足す
  変更は差分に現れる。
- 決定 4: sweep が自分でログインすると、ログイン・秘密の入力・TOTP の部品が 2 か所に分かれる（IADR-0628 と同じ理由）。失効の前に流せば
  live-setup のセッションをそのまま使え、cookie の値は子プロセスの要求のヘッダにしか載らない。
- 決定 2・5: 消す判定を手元の接頭辞 1 つに寄せると、試験が「接頭辞が一致しないものへ消す要求が飛ばない」を真理値表と混ぜた一覧の 2 つで
  固定できる。seed（ABAC ポリシー・検索用文書・タグ辞書）の名前が `e2e-` で始まらないことも同じ節で固定する。

## 影響

- 波 4 の rw spec は `createCleanup(page.request)` を使い、作ったものを `track` する。新しい種類（削除の口）を足すときは sweep の `KINDS` にも足す。
- 常設の PoC で sweep を流す経路は無い（向け先の門が止める）。PoC へ向ける日が来たら、`--older-than` の窓と向け先の識別を一緒に決め直す（本 IADR を改める）。
- 🔴 残余: kubectl の context と、エッジの URL が指すクラスタが一致することは確かめていない。同じ端末で k3d の e2e-live と PoC が同時に
  443 を待ち受けることはできないので、e2e-live の runner では一致する。手元で流すときは同じ前提に立つ。
- MCP クライアントの残骸は消えない（列挙して残ることを示す）。消す口が BFF に足されたら `remove` を足す。
