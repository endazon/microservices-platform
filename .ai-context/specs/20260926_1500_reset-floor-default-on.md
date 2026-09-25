---
title: 作業仕様書 — リセット申請の床を既定 ON にする（#1500・計画 ADR-0097 決定 2）
type: spec
status: done
related_ids: [SC-15, FR-05, NFR-13, ADR-0094, ADR-0097, IADR-0432]
author: claude
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0097_timing-floor-release-default-on-and-periodic-review.md (Accepted 2026-09-12)
  - planning:projects/microservices-platform/07_adr/ADR-0094_existence-hiding-timing-median-consistency-and-response-floor.md
related_specs: [20260911_issue-1410_reset-timing-floor.md, 20260911_issue-1410_ci-reset-floor.md, 20260925_1470_timing-self-control-step.md]
issue: "#1500"
---

# 作業仕様書 — リセット申請の床を既定 ON にする

## 起点

- issue: #1500（#1491 の監査で発見。ADR-0097 決定 2 が未実装で `RESET_FLOOR` の既定が 0 のまま）
- 計画 ADR-0097（Accepted 2026-09-12）決定 2・§フォローアップ 1
- 覆す実装判断: IADR-0432 決定 4（床を opt-in にし、既定の描画をバイト等価に保つ）

## 計画の決定（逐語の要点）

| 決定 | 内容 | 本作業での写像 |
| --- | --- | --- |
| 2 | **`RESET_FLOOR` の既定を 1 とし、`deploy/mail-relay/kustomization.yaml` が床を参照する形へ改める** | `istio-edge-up.sh` の既定を 1 へ。`deploy/mail-relay/kustomization.yaml` の `resources` へ `reset-floor` を足す |
| 2 | **IADR-0432 決定 4 を覆す** | IADR-0432 へ日付つき追記 `［2026-09-26 追記 / #1500］`。決定 4 の見出し直下にも追記への導線を置く |
| 2 | **値（150 ms）は構成が与え、コードは既定を持たない**（`RESET_FLOOR_MS` が無ければ器は起動しない） | `reset-floor.js` と `reset-floor.yaml` の値は触らない。`reset-floor.test.js` 試験 1 がこの形を引き続き固定する |
| 2 | 「既定 ON」とは overlay を既定で取り込むことであって、値をコードへ焼くことではない | 同上 |
| FU 1 | IADR-0432 決定 4 を部分改定する IADR を起こし、本 ADR を根拠として引く | 🔴 **新 IADR は起こさず、IADR-0432 への日付つき追記で記録する**（依頼元の指示。先例: 同 IADR の #1470 追記は計画 ADR-0103 による決定 5 の部分改定を追記で持っている）。**計画の字面との差異として報告する** |

## 設計（実装の判断）

### 床の器と経路の置き場

| 部品 | 旧 | 新 |
| --- | --- | --- |
| 器（`deploy/mail-relay/reset-floor/`） | opt-in overlay だけが取り込む | **`deploy/mail-relay/kustomization.yaml` が取り込む**（＝`deploy/local/infra` が描画し、`k8s-local-up.sh` の [4/7] で必ず立つ。go-live も同じ宣言を適用する） |
| 器の本体の ConfigMap（`reset-floor-script`） | `istio-edge-up.sh` が `RESET_FLOOR=1` のときだけ作る | **`k8s-local-up.sh` が [4/7] の apply より前に作る**（門・exporter と同型）。`istio-edge-up.sh` の作成は残す（冪等。単独実行でもリポジトリの版を当てる） |
| 経路（VirtualService の先頭 route） | `edge-istio-reset-floor` overlay（`RESET_FLOOR=1` のとき） | **同じ overlay を既定で当てる**（`RESET_FLOOR` の既定 1） |
| 退路 | — | **`RESET_FLOOR=0`** で素の `edge-istio` を当てる（経路が外れ、器は誰も通らないまま居る） |

- overlay は器も引き続き含む（**器と経路の 2 つで 1 組**を overlay 単独でも保つ。器は infra と同じ描画であり、両方から apply しても同一オブジェクトで冪等）。
- **`istio-edge-down.sh` は器を消さなくする。** 器は近接 MTA の配備単位（infra）の持ち物になったため、エッジの切り戻しが消すと infra の宣言から外れる。経路は VirtualService ごと `delete -k deploy/local/edge-istio` で消える。
- **`RESET_FLOOR` の値は 0 / 1 だけを受け付け、それ以外は入口に触る前に落とす。** 既定が 1 になったことで「`false` と書けば外れるつもり」の取り違えが起き得る。黙って床を入れても外しても誤りなので、[1/5] より前に止める（Traefik の Service を落とした後に気付くのが最悪である —— 同スクリプト冒頭の前提確認と同じ理由）。
- **integration-stack の job env `RESET_FLOOR: '1'` は外す。** 既定が 1 になった以上、CI は**既定の経路そのもの**を測るべきである（既定が再び 0 へ戻れば T-10 が赤になって気付ける）。

### ［2026-09-26 追記 / #1518 監査］器が落ちると申請は 503 になる

計画の「床は失敗しても安全側（効かなければ遅くならないだけ）」は**経路が入っていない**ときだけ成り立つ。経路は
器だけを向き予備の route が無いので、**器に ready な endpoint が無ければリセット申請の POST はすべて 503** になる。
当初の Runbook・画面仕様書・IADR-0432 追記・マニフェストと `istio-edge-up.sh` のコメントはこれを「遅くならないだけ」と書いていた（誤り。器の readiness のコメントは #1410 以来「床の無い経路へ戻る」と書いていた）。
すべて訂正し、起動器が器の rollout を待つ理由として書いた。併せて `k8s-local-up.sh` の冒頭でも `RESET_FLOOR` を
検査する（空・0・1 のみ。`ISTIO` 未設定で与えたら効かない旨を警告する）。

### 残るもの（本作業では塞がない）

- 🔴 **Traefik のエッジ（`ISTIO` 未設定の既定のローカル経路）には床への経路が無い。** 経路は Istio の VirtualService の patch でしか書いておらず、Traefik の Ingress には相当物が無い。器は立つが誰も通らない。計画 ADR-0097 は `RESET_FLOOR` と `deploy/mail-relay/kustomization.yaml` だけを名指しており、Traefik 側の経路は計画外なので足さない（報告に記す）。
- **go-live の経路**（production の Keycloak マニフェストは本リポジトリに無い）は、器は `deploy/mail-relay` の適用で入るが、**経路は go-live のエッジ側が与える必要がある**。運用 Runbook に書く。

## 母集合（着手時に引き直した）

| 軸 | 検索 | 対象として拾ったもの |
| --- | --- | --- |
| 1 識別子 | `RESET_FLOOR\|reset-floor\|edge-istio-reset-floor`（パス除外のみ・拡張子で絞らない） | `scripts/istio-edge-up.sh` / `scripts/istio-edge-down.sh` / `scripts/reset-floor.test.js` / `deploy/mail-relay/reset-floor/{kustomization,reset-floor}.yaml` / `deploy/local/edge-istio-reset-floor/kustomization.yaml` / `.github/workflows/{ci,integration-stack}.yml` / IADR-0432 |
| 2 誤りの側（opt-in・既定 0・バイト等価の語） | `床.{0,60}(既定\|opt-in\|選択式\|バイト等価)` とその逆順 | 軸 1 の各ファイルに加え `docs/screens/SC-15_password-reset.md`（§現況「床は既定では入っていない」） |
| 3 覆される決定の引用 | `IADR-0432 決定 4` | `.github/workflows/integration-stack.yml`（2 箇所） |
| 4 「床が入るまで赤」の語 | `床が入るまで\|床を配備するまで\|床の配備まで` | `docs/tests/SC-15_password-reset.md` / `docs/screens/SC-15_password-reset.md` / `integration-stack.yml` / `scripts/check-password-reset-mail.js`（コメント・失敗文） |
| 5 器を与える側 | `reset-gate-script`（器の ConfigMap を作る同型の箇所） | `scripts/k8s-local-up.sh`（[4/7] の前・rollout 待ち） |
| 6 説明文書 | 依頼元が名指した `docs/operations`・`scripts/README.md`、および `deploy/local/README.md`（dev 既定の部品一覧） | `docs/operations/keycloak-smtp-relay-setup-runbook.md` / `scripts/README.md` / `deploy/local/README.md` |

**除外したものと理由**

- `CHANGELOG.md`（14・767 行が opt-in に言及）: 生成物。手で書き足さない。
- `.ai-context/specs/20260911_issue-1410_*` ほか確定済み仕様書: 凍結記録。書き換えない。
- IADR-0432 の決定 4 本文・§理由の opt-in の段落: 本文を書き換えず、日付つき追記と見出し直下の導線で覆す（凍結の作法）。
- `deploy/mail-relay/reset-floor.js`: 「既定を持たない」は値の既定の話であり、本作業で変わらない。
- 軸 2 のヒットのうち `IADR-0136` / `IADR-0272` / `deploy/local/wiki-oidc/README.md` / `docs/how-to/session-handoff.md` / `docs/tests/UC-04_*`: 「床」が別の意味（カバレッジ床・権限の床・間隔の下限）。
- `scripts/check-stack-ready.js`（G11 が infra の描画と稼働を突き合わせる）: コードは変えない。描画に `reset-floor` が増え、`k8s-local-up.sh` が同じ描画を当てるので突き合わせは成り立つ。
- `.ai-context/adr/README.md` の IADR-0432 の行: タイトルセルの末尾「既定は opt-in」が誤りになるため**要約だけを直す**（日付つき追記は索引へ書かない。`scripts.repo.test.js` の `title-addendum` が止める）。

## 受け入れ基準

1. `kubectl kustomize deploy/mail-relay` と `deploy/local/infra` の描画に `reset-floor` の Deployment / Service が出る（値 150 ms はマニフェストが与える）。
2. `istio-edge-up.sh` を `RESET_FLOOR` 未設定で走らせると `edge-istio-reset-floor` を当てる。`RESET_FLOOR=0` で素の `edge-istio` を当てる。0 / 1 以外は [1/5] より前に落ちる。
3. `reset-floor.js` はコードに既定を持たないまま（`RESET_FLOOR_MS` が無ければ起動しない）。
4. `node scripts/reset-floor.test.js` が新しい既定を固定し、旧い既定（参照しない／既定 0）への変異で落ちる。
5. IADR-0432 に `［2026-09-26 追記 / #1500］` があり、決定 4 が ADR-0097 決定 2 に覆されたことを書く。
6. `docs/` の可視テキストに計画 ID・IADR・仕様書名を書かない（trace ブロックへ）。
