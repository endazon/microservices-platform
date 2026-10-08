---
title: 作業仕様書 — インフラ製品の年次・契機の点検（基準 A〜D）を運用手順に置き、deploy/ のインフラのイメージを digest で固定する（#1787）
type: spec
status: done
related_ids: [NFR, ADR-0107, ADR-0112, ADR-0030, ADR-0007, ADR-0106, IADR-0514, IADR-0461, IADR-0088]
author: claude
created: 2026-10-08
updated: 2026-10-08
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0107_infrastructure-product-selection-criteria.md 決定 3・決定 5・フォローアップ 1〜3
  - planning:projects/microservices-platform/07_adr/ADR-0112_infrastructure-admin-endpoint-criterion.md 決定 3・フォローアップ 1
  - planning:projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md §結果（年 1 回の点検の時期）
  - planning:projects/microservices-platform/07_adr/ADR-0007_cicd-gitops-argocd.md（Harbor）
issue: "#1787"
---

# 作業仕様書 — インフラ製品の点検（基準 A〜D）と digest 固定（#1787）

> 本仕様書は実装着手前に作成した（着手 2026-10-08）。判断の記録は **IADR-0514**（固定の方針と検査器）に置く。
> 計画は project-planning `origin/main` の 4 ADR を GitHub API で読んだ（読み取り専用）。基点は MSP `origin/develop` `1d71b2b3`。
> 🔴 **稼働中のクラスタには何も実行しない。** レジストリへは匿名の HEAD / GET（manifest・config blob）だけを送った。

## 起点となる計画書（トレーサビリティ）

- 計画 ADR: **ADR-0107**（決定 3＝基準 B の digest 固定と Harbor ミラー、決定 5＝年 1 回と契機の点検、フォローアップ 1〜3）、**ADR-0112 決定 3**（点検に基準 D を含める）、**ADR-0030 §結果**（年 1 回の点検の時期）、**ADR-0007**（Harbor）。
- 非機能要件: 無採番（工程の点検。メタ作業の扱いは `.claude/rules/traceability.md`）。
- 起点 issue: **#1787**（第 4 回全体監査 #1772 の行「ADR-0107 決定 5 / ADR-0112 決定 3」の切り出し）。関連 IADR-0461（seaweedfs の digest とミラー）。

## 計画が決めていること・決めていないこと

| 計画が決めている | 計画が決めていない（本件で決める） |
| --- | --- |
| 基準 A〜D の中身（ADR-0107 決定 2〜4・ADR-0112 決定 1） | 点検の手順・記録先・母集合の引き方 |
| 年 1 回＋契機（取得の失敗・上流の archived・ライセンス変更の告知）で点検する。時期は ADR-0030 の年次点検と同じ | 「インフラのイメージ」の境界（自製イメージとの線引き） |
| イメージは digest で固定する。Harbor 配備後はミラーする | 固定の表記（`tag@digest`）、固定の更新手順、tag だけの参照の検知の仕組み |
| 基準を満たさない製品は製品ごとに差し替えを裁定する（ADR-0107 フォローアップ 1） | — （**差し替えは裁定事項であり、本件では行わない**。記録して環流する） |

## 現状（実測。`1d71b2b3`）

| 事実 | 確かめ方 |
| --- | --- |
| 運用仕様書 §定期点検（年次）は ADR-0030 の採用ライブラリの点検だけを持つ | `docs/operations/operations.md` |
| 同 §デプロイ の「Third-party イメージ」は「具体版タグ（可能なら digest）」で、digest は「CD 層で段階導入するのが望ましい」と書いている（ADR-0107 決定 3 と食い違う） | 同上 |
| `deploy/` のインフラのイメージの参照で digest 付きは seaweedfs（compose・helm values）と backup の Dockerfile の `FROM` だけ。postfix は digest をコメントに書いただけ | `grep -rn 'image:' deploy`・`FROM` |
| helm values のうち `registry`/`image`/`tag` の 3 キー形式のインフラは seaweedfs・embedding（TEI）・wikijs。digest を描くのは seaweedfs のテンプレートだけ | `templates/{seaweedfs,embedding,wikijs}.yaml` |
| 単一の文字列で持つ values は `drift.postSyncHook.image`（curl）と `syntheticMonitor.image`（node） | `values.yaml` |
| qdrant の参照は統合試験の定数 `QdrantTestImage.Reference` と**完全一致**を `QdrantTestImageDefinitionTests` が強制する | `src/knowledge/backend/Tests/Knowledge.IntegrationTests/Search/QdrantTestImageDefinitionTests.cs` |
| 匿名の取得: docker.io / ghcr.io / quay.io / registry.k8s.io の `/v2/` はいずれも 401（到達可） | `curl -w '%{http_code}'` |

## 設計（正は IADR-0514）

1. **固定の表記は `<repo>:<tag>@sha256:<index digest>`**。digest は multi-arch の image index（manifest list）のもので、tag は可読のために残す。helm values の 3 キー形式は `digest:` キーを足し、テンプレートが `{{ if digest }}@digest{{ end }}` で描く（seaweedfs と同型）。
2. **境界**: 自製イメージ（`microservices-platform/*`・`k3d-local/*`）は対象外（CD が一意タグ/digest を渡す。運用仕様書 §自製イメージ）。テンプレートの `{{ … }}` 行は values 側で検査する。
3. **検知の仕組みは検査器**（`scripts/check-image-digests.js`）。Renovate は採らない（理由は IADR-0514 決定 3）。
   - tag だけの参照 → 赤。例外は `scripts/image-digest-exceptions.json` に**理由つき**で列挙し、実在しない例外も赤（腐り止め）。
   - 同じ `repo:tag` が別の digest で書かれている → 赤（片側だけの更新を止める）。
   - 走査 0 件 → 赤（fail-closed）。
   - `--list` で点検の母集合（製品と参照箇所）を出す。年次点検はこれを母集合にする（手で列挙しない）。
4. **固定の更新手順**は運用仕様書に置く（匿名トークン → HEAD manifest → `docker-content-digest`。tag と digest を対で変え、全参照を揃える）。
5. **Harbor へのミラーは対象外**（Harbor 未配備。ADR-0107 決定 3 の「配備した後」）。運用仕様書に明記する。
6. **点検の結果は運用仕様書の点検節の「点検の記録」表**に置く。基準を満たさない製品は**差し替えずに記録し**、planning への環流の下書きを本仕様書 §環流の下書き に置く（起票は利用者）。

## 受け入れ基準

- [x] **Given** 運用仕様書 **When** 年次の点検の節を読む **Then** インフラ製品の基準 A〜D・契機・記録先・母集合の引き方が書かれている。
- [x] **Given** `deploy/` のインフラのイメージの参照 **When** 走査する **Then** すべて digest 付きである（例外は理由つきで列挙）。新たに tag だけの参照が入ったら検知する仕組みがある（検査器）。
- [ ] 初回の点検の記録があり、基準を満たさない製品があれば planning へ環流されている。 —— **記録はある。環流は下書きまで**（起票は利用者が行う。本 PR は `Refs #1787`）。

## 初回の点検（2026-10-08）の方法

- 母集合: `node scripts/check-image-digests.js --list`（`deploy/` の参照から機械的に引く）。
- **基準 A**: 固定した版のタグの `LICENSE` を `raw.githubusercontent.com` から読んだ（GitHub API は本セッションの権限外）。イメージの OCI ラベル `org.opencontainers.image.licenses` があれば併記した。archived の判定は API が使えず、**イメージの再ビルド日（config の `created`）で代替**した。
- **基準 B**: 匿名で index digest を 2 回解決し、2 回とも一致したことを確かめた（全 20 参照が index/manifest list 型）。
- **基準 C・D**: 製品の既定値は上流文書の知識に基づき、**配備側の設定（compose・`deploy/local`・helm）で無効化／閉塞しているかを実測**した。製品の既定の外部通信を通信の捕捉で実測したものではない（残余）。
- 結果の表は運用仕様書 §インフラ製品の点検 の「点検の記録」が正。

## 環流の下書き（planning へ。起票は利用者）

> 起票前に同件の既存 issue を検索すること（`feedback` / `decision-needed`）。

**件名**: `[feedback] microservices-platform: インフラ製品の初回点検（ADR-0107 決定 5 / ADR-0112 決定 3）で基準 A を満たさない製品が 2 件、基準 C の無効化が配備に入っていない製品が 4 件（MSP#1787）`

**本文の要点**:

1. **基準 A 不適合（裁定依頼・ADR-0107 フォローアップ 1）**
   - **Redis**: `redis:7-alpine` の実体は 7.4.11。**7.4 以降は RSALv2 / SSPLv1 のデュアルライセンス**（上流 `LICENSE.txt` 7.4.11）で OSI の OSS ではない。候補: ①7.2 系（BSD-3。保守期限の確認が要る）へ下げる ②Redis 8（AGPLv3 を選択肢に含む）へ上げる ③Valkey（BSD-3）へ差し替える。キャッシュ（ADR-0030 の HybridCache L2）と BFF の DataProtection 鍵の保存先が依存する。
   - **Vault**: `hashicorp/vault:1.16`（1.16.3）は **BUSL-1.1**（上流 `LICENSE` v1.16.3）で OSS ではない。配備は `deploy/local/vault`（dev opt-in）と ESO の ClusterSecretStore。候補: OpenBao（MPL-2.0）への差し替え。
2. **基準 A の注意（不適合ではない）**: RabbitMQ 3.13 系はコミュニティ保守の対象外になった系列で、`3.13-management-alpine` は 2025-12-02 以降再ビルドが無い。4.x への移行を次回の点検の対象に入れる。Keycloak 24.0（24.0.5。2025-02 再ビルド）も保守の続く系列ではない。
3. **基準 C（ADR-0107 フォローアップ 3: 08_data-egress-policy の統制表への追加）** —— 製品は無効化できる（基準 C は満たす）が、**配備に無効化が入っていない**:
   - Grafana（`[analytics] reporting_enabled`・`check_for_updates`・`check_for_plugin_updates` が既定で有効）
   - Loki（`analytics.reporting_enabled` が既定で有効）
   - Tempo（`usage_report.reporting_enabled` が既定で有効）
   - Qdrant（テレメトリが既定で有効。`QDRANT__TELEMETRY_DISABLED`）
   - 補足: Mailpit の版確認（`MP_DISABLE_VERSION_CHECK`）、TEI のモデル取得（HF Hub。初回起動時の取得そのものが外部通信）。
   - 統制表への追加と、実装側の無効化（別 issue）を依頼する。
4. **基準 D の扱いの確認（裁定依頼）**: Loki・Tempo・OTel collector・Alertmanager（silence の作成）は製品単体で認証を必須にできず、前段（メッシュの AuthorizationPolicy・リバースプロキシ）で掛ける形になる。ADR-0112 決定 1 の「認証を必須にできる」に前段の認証を含めてよいかを確認したい。
5. **母集合の外**: `scripts/` が chart / マニフェストで入れる製品（Istio・External Secrets・Reloader・cert-manager・Argo CD・k3s）は `deploy/` の参照に現れず、本件の母集合に入っていない。**Argo CD は `stable` ブランチのマニフェストを直接 apply しており版すら固定されていない**。点検と固定の対象に入れるかの確認を依頼する。

## 母集合（規則 9・10。`1d71b2b3` 時点）

### 規則 9 — 誤りの側の文字列で全文書を走査

- `grep -rnE '^\s*-?\s*image:|^FROM ' deploy` と、helm values の `registry/image/tag` ブロック（`grep -n 'tag:' values.yaml`）。`check-image-digests.js --list` で **21 製品・42 参照**（試験・プローブ用の node・busybox・curl を含む。自製 21 参照は対象外）。固定前に digest 付きだったのは 3 参照（seaweedfs の compose・helm values と backup の `FROM`）で、残る 39 参照を本件で固定した（変更ファイルは `deploy/` の 22 本＋helm テンプレート 2 本）。
- 文書側: 「具体版タグ（可能なら digest）」「digest ピン」「CD 層で digest」を `grep -rn 'digest' docs/operations docs/security deploy/*.md` で引いた。運用仕様書 §Third-party イメージ が追随先（ADR-0107 決定 3 と食い違う記述）。`docs/security/security.md` には該当記述が無い。
- 試験側: `QdrantTestImage.Reference`（完全一致の突合）・`SeaweedFsContainer.Image`（既に digest）。他の統合試験の Testcontainers の参照（`postgres:16-alpine`・`rabbitmq:3.13-alpine`）は `deploy/` の外であり本件の母集合外（残余）。

### 規則 10 — この変更で新たに誤りになる自分の記述

- `deploy/mail-relay/mail-relay.yaml` の「タグで pin する」「digest はコメント」→ digest を参照へ入れたので注記を直す。
- 運用仕様書 §Third-party イメージ の「可能なら digest」→ 直す。
- `scripts/scripts.repo.test.js` の検査器の母集合の件数 60 → 61（導出値は数え直した）。

## 検証

- `node scripts/check-image-digests.js --self-test` / `node scripts/check-image-digests.js` / `--list`
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`
- `node scripts/check-deploy-manifests.js`（helm / kubectl / kubeconform）と helm の試験 4 本
- `dotnet test` で `QdrantTestImageDefinitionTests`（コンテナを起こさない）
- `check-trace-blocks` / `check-adr-numbering` / `check-doc-updated --base origin/develop` / `check-commit-messages` / `check-cross-repo-refs` / `check-plan-id-qualification` / `gen-knowledge-graph --check`

## 範囲外

- Harbor へのミラー（Harbor 未配備。ADR-0107 決定 3）。
- 基準を満たさない製品の差し替え（裁定事項）と、基準 C の無効化の配備（別 issue。環流の下書き 3）。
- `scripts/` が入れる chart 由来の製品と、統合試験の Testcontainers のイメージの固定。
