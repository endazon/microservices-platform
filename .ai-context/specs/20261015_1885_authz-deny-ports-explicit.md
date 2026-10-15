---
title: バックチャネルログアウトの DENY（bff-service-plaintext-only-backchannel）に ports を明示し、Istio の警告を消しつつ拒否の範囲を変えない（#1885）
type: spec
status: done
related_ids: [NFR, ADR-0005, ADR-0026, ADR-0032, IADR-0273, IADR-0336]
author: claude
created: 2026-10-15
updated: 2026-10-15
plan_refs:
  - planning:projects/microservices-platform/07_adr（ADR-0005 サービスメッシュ / mTLS）
issue: "#1885"
---

# 仕様書: バックチャネルログアウトの DENY に ports を明示する（#1885）

## 起点となる計画書（トレーサビリティ）

- NFR（機密性）・ADR-0005（Istio / mTLS）・ADR-0026 / ADR-0032（BFF セッション・即時失効）。
- 方針の由来: IADR-0273（バックチャネルログアウト）・IADR-0336 決定 2（`fromOutsideMesh` の 2 枚組）。
- 起点: #1885（PoC 2026-10-10。`helm upgrade` のたびに `configured AuthorizationPolicy will deny all traffic to TCP ports under its scope due to the use of only HTTP attributes in a DENY rule` が出る）。

## 目的と射程

- **振る舞いを変えない明確化**である。DENY の `to.operation` に `ports: [mesh.backchannelLogout.port]` を足し、警告の条件（DENY の `to` に `ports` / `notPorts` が無い）から外す。
- 射程外: `templates/edge.yaml` の `<service>-edge-sync-only`（同じ警告の形だが、注記が「gRPC（8081）の面も当たる＝全ポートに掛ける」ことを意図と書いている。ローカルは edge 無効で描かれず、今回の警告の出所でもない）。Traefik 入口の 403 の実測（issue の「別件」）は PoC 側で扱う。

## 拒否の範囲が変わらない理由（実測と列挙）

DENY が効くのは、(1) mTLS の主体を持たない流入（`notPrincipals: ["*"]`）が (2) BFF のワークロードへ届く場合である。

| 流入 | 旧（ports 無し） | 新（ports: 8080） |
| --- | --- | --- |
| 平文 HTTP :8080 バックチャネルの URI | 通す | 通す |
| 平文 HTTP :8080 その他の経路 | 拒否 | 拒否 |
| 平文の非 HTTP（TCP）:8080 | 拒否（TCP では notPaths が落ちて広がる） | 拒否（ports は TCP でも残る） |
| mTLS（主体あり）の全流入 | 通す | 通す |
| 平文 :8080 以外 | 拒否 | DENY は当たらない |

最後の行だけが字面で異なるが、**BFF の受信ポートは 8080 だけ**である（実測: 描画した `Deployment/bff-service` の `containerPort` は `[8080]`。`services.bff` に `grpcPort` が無い。Dockerfile は `EXPOSE 8080` で、aspnet 10 の既定 `ASPNETCORE_HTTP_PORTS=8080` を上書きしていない。Kestrel の待受を足すコードも無い）。
8080 以外のポートには受け口が無いので、平文が届いても応答する者が居ない（名前空間が STRICT なら mTLS 層で先に落ちる。起動器の初期段の PERMISSIVE でも受け口が無い）。**したがって実効の拒否範囲は変わらない。** 前提（受信ポート＝ports の値）は試験が固定し、BFF に受け口が増えたら赤になる。

値は issue の提案どおり `mesh.backchannelLogout.port`（PeerAuthentication の `portLevelMtls` と同じ単一情報源）を使う。開けた口と DENY の範囲が字面で揃う。

## 母集合（規則 9・10）

- この方針を読む・書く箇所: `git grep -n "plaintext-only-backchannel\|backchannelLogout"` → テンプレート 1（`istio-mtls.yaml`）・`check-stack-ready.js` の G12（action が DENY であることだけを見る。ports は見ないので追随不要）・`k8s-local-up.sh` / `.test.js`（`--set` の組だけ）・values.yaml。
- 描画の形を固定している静的検査: `check-deploy-manifests.js` は AuthorizationPolicy の形を見ない。`MeshMtlsTests.cs` は PeerAuthentication / DestinationRule だけ。→ 追随不要。
- 文書: `docs/` に方針の rule を書いた箇所は無い（`bff-session-design.md` は受け口の URI だけ）。
- 新たに誤りになる自分の記述（規則 10）: テンプレートの注記は Go テンプレートの注記（`{{- /* */}}`）にして描画物へ出さない（描画の差分を `ports` の 2 行に保つ）。

## 受け入れ基準と試験の写像（`scripts/helm-backchannel-authz.test.js`）

| 受け入れ基準（issue） | 試験 |
| --- | --- |
| `to.operation.ports` があり、値は `mesh.backchannelLogout.port` と同じ単一情報源 | 「有効: DENY・…ports ＝ portLevelMtls のポート…」・「knob の追随」 |
| 警告が出ない | Istio の `ValidateAuthorizationPolicy` の条件を写した `tcpWarning` に新形が当たらず、旧形は当たる（陰性対照） |
| 拒否の範囲が広がりも狭まりもしない | 受信ポート × 主体の有無 × HTTP / TCP × 経路の全組で旧形と新形の判定が一致・前提（受信ポート＝{8080}） |
| 描画の差分は `ports` の 2 行だけ | `helm template`（既定・values-local・有効）の前後 diff（PR 本文） |
| 変異で赤になる | ports を外す／値を変える／BFF に grpcPort を足す |

CI は `static-checks-units`（helm 済み）に 1 ステップ足す。IADR は起こさない（振る舞いを変えない明確化であり、判断の根拠は本仕様書と issue で足りる）。
