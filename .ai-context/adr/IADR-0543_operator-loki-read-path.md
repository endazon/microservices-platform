---
title: IADR-0543 運用者が経路 B の Loki を読む正規の経路は「svc/loki への port-forward ＋ 既存の読み取りの身元（Secret observability-gate の reader）」とし、新しい身元は作らない。保全は読み取り専用の scripts/export-loki-logs.js で行い、前段の内側（Pod の 3101）は break-glass のまま残す
type: impl-adr
status: Accepted
related_ids: [NFR-18, ADR-0133, ADR-0112, ADR-0006, IADR-0526, IADR-0210, IADR-0369]
author: claude
created: 2026-10-15
updated: 2026-10-15
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0133_admin-endpoint-front-authentication.md 決定 1（条件 1・2）
related_specs:
  - ../specs/20261015_1935_loki-log-read-runbook.md
---

# IADR-0543: 運用者が経路 B の Loki を読む正規の経路（#1935）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-15
- 決定者: claude（PoC からの依頼 #1935）

## 起点・関連

- 起点 issue: #1935。PoC で PC を再起動したところ Pod のログ（`kubectl logs`）が消えた。10-14 のセッションを監査するために Loki を読もうとしたが、#1842（PR #1865）が入れた前段の認証が壁になり、読む手順が無かった。
- 関連する計画書 ID: NFR-18。関連する計画 ADR は ADR-0133 決定 1（条件 1: 身元を検証する前段、条件 2: 別の段の到達の制限）・ADR-0112（基準 D）・ADR-0006（可観測性）。
- 関連する実装 ADR: [IADR-0526](./IADR-0526_loki-tempo-front-auth-and-networkpolicy.md)（前段の 2 つの身元・道の列挙・break-glass）・[IADR-0210](./IADR-0210_local-k8s-observability-persistence.md)（可観測性の PVC）。
- 関連する実装仕様書: `.ai-context/specs/20261015_1935_loki-log-read-runbook.md`。

## 文脈

- 経路 B の Loki は Pod の loopback（`127.0.0.1:3101`）だけで待っている。Service `loki` の口（3100）は同じ Pod の前段（container `gate`）が持つ。前段は 2 つの身元を受け付ける。書き込み（`writer`）は push だけ、読み取り（`reader`）は GET の読み取り API の列挙だけを通す（IADR-0526 決定 2）。NetworkPolicy `loki-ingress` は、3100 へ届く Pod を collector と Grafana に絞っている。
- 運用者向けの経路として IADR-0526 が残したのは、「運用の口を使うときの break-glass（`kubectl port-forward deploy/loki 3101`）」だけだった。日常の読み取りと、ログをファイルへ保全する経路は決まっていなかった。
- 実測（配備と同じ版の公式イメージで、描画した ConfigMap をそのまま使った。詳細は作業仕様書）:
  - Loki の 1 応答は `max_entries_limit_per_query`＝5000 件で切れる。
  - 保持は `retention_enabled: false`・`retention_period: 0s` である。Loki 自身はログを消さない。
  - WAL は `/tmp/loki/wal` にある。永続化の overlay の PVC（`/tmp/loki`）の内側である。

## 決定

### 決定 1: 日常の読み取りは「svc/loki 3100 への port-forward ＋ 既存の読み取りの身元」とし、新しい身元は作らない

- 運用者は `kubectl -n platform-infra port-forward svc/loki 3100:3100` で前段の口へ繋ぐ。トークンは Secret `observability-gate` の `reader` を読んで、Bearer として送る。
- **運用者専用の第 3 の身元（例 `operator`）は作らない。** 理由は 2 つある。
  - Secret `observability-gate` を読める者は、名前空間 `platform-infra` の Secret を get できる者である。これは break-glass の `pods/portforward` と同等以上の権限であり、運用者に新しい境界を跨がせない。
  - 身元を足すと、前段の Caddyfile・`gate.sh` の検査・起動器のトークン生成・#1842 の一致の試験と変異の 4 か所が動く。得るもの（Grafana と運用者の読み取りの区別）は、今の前段にアクセスログの身元の記録が無いため活かせない。
- 読み取りの身元は前段の道の列挙の内側しか通らない。運用者が日常の読み取りで運用の口（`/flush`・`/config`）や削除 API に触れることは無い（最小権限）。
- **前段・到達の制限は弱めない。** Caddyfile も NetworkPolicy も変えない。port-forward は kubelet を経由して Pod のネットワーク名前空間の中から繋ぐ。クラスタの他の Pod から 3100 への到達は、これまでどおり NetworkPolicy が止める。

### 決定 2: 前段の内側（`deploy/loki` の 3101）は break-glass のまま残し、日常の読み取りには使わない

- 前段の内側は、保持の実効値（`/config`）を見るとき、前段が壊れていて読めないときに限って使う。保持の実効値は、`kubectl exec deploy/loki -c loki -- wget -qO- http://127.0.0.1:3101/config` でも読める。
- この経路は Loki の運用の口と削除 API がすべて開いた口である。runbook は GET の読み取りだけを書く。

### 決定 3: 保全は読み取り専用の `scripts/export-loki-logs.js` で行う（logcli は導入しない）

- 依存はゼロにする（Node の標準の fetch）。上限つきの要求には `lib/bounded-wait.js` を使う。
- 時刻は時差つきの RFC 3339 だけを受け付ける。JST の日付の境を 9 時間ずらす誤りを、引数の段で止める。
- ページ送りは forward で行う。次のページは前のページの最後の時刻から含めて取り直し、同じ時刻で書いた行（ラベル・時刻・本文の組）は落とす。ページ全体が同じ時刻で先へ進めないときは、その時刻（1 ns の幅）だけを 5000 件で取り直す。それでも切れるときは失敗にする（取りこぼしを成功に見せない）。
- トークンは環境変数 `LOKI_TOKEN` だけで受け取る（引数で受けるとプロセスの一覧と履歴に残る）。既に在る出力先は上書きしない。最後に sha256 を出す。
- 呼ぶ道は `query_range` と `query` だけである。前段の読み取りの列挙の内側にあることは、`scripts.repo.test.js` が gate の ConfigMap と突き合わせる。
- 稼働クラスタへ当たるので、`live-scripts.json` の `live` に入れる（`--live` / `LIVE=1`。#1550）。
- logcli を採らない理由: 外部バイナリの導入・版の固定・digest の管理が増える。前段の列挙との一致を試験で固定できない。

### 決定 4: 対話的な閲覧は Grafana の Explore（Keycloak の運用者ロール）を併記するが、保全には使わない

- Grafana の datasource は読み取りの身元を使う。edge（`LOCALEDGE=1`）経由の OIDC で `platform-operator`（Editor）以上なら Explore で読める。
- ただし Explore の表示は件数の上限で切れる。範囲を指定した完全な書き出しは決定 3 の道具で行う。

## 結果

- 良い影響: 前段と到達の制限を 1 行も変えずに、運用者がローカルからログを読めて、範囲を指定して保全できる。保持と永続化の確認手順が runbook にまとまる。
- 悪い影響: 読み取りのトークンを Grafana と運用者が共有する（前段からは区別できない）。トークンを回すと、両方へ同時に効く。

## 残余

1. **port-forward が NetworkPolicy の対象外であることは、稼働クラスタで実測していない**（この環境では k3d のクラスタを起こせない）。kubelet・containerd の port-forward が Pod のネットワーク名前空間の中から `localhost` へ繋ぐという設計上の性質に拠っている。runbook の確認手順（401 → 200 → 403）が、PoC で最初にこれを確かめる。
2. **保持の設定が無い（削除しない）ので、PVC `loki-data`（2Gi）がいずれ満杯になる。** 満杯になると新しいログの取り込みが失敗する（古いログは残る）。保持期間を決めるのは本件の射程外である。runbook には容量の確認（`df`）だけを置いた。
3. ログが消える経路は 4 つある。`OBSERVABILITY=1` で起動していない（Loki が居ない）、`PERSIST=0`（emptyDir）、`k8s-local-down.sh --apply`（名前空間と PV の削除）、PVC の満杯（新しいログだけ）。どれに当たるかは runbook の手順で切り分ける。消えたログを取り戻す手段は無い。
