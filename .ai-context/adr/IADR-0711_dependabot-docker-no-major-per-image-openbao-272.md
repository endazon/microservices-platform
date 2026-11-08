---
title: IADR-0711 Dependabot の docker は大版を出さず、0.x の製品と chart と対の製品は小版も出さず、collector は名指しで止め、1 イメージ 1 本の PR（修正版と小版は別の group）にする。OpenBao を 2.7.2 へ上げ、Keycloak 26.8 は上げない
type: impl-adr
status: Accepted
related_ids: [NFR-18, IADR-0708, IADR-0514, IADR-0525, IADR-0524, IADR-0689]
author: claude
created: 2026-11-08
updated: 2026-11-08
related_specs:
  - ../specs/20261108_dependabot-docker-safe-updates.md
---

# IADR-0711: Dependabot の docker を切り分けられる形にし、互換の明らかな修正版だけを上げる

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-11-08
- 決定者: claude（依頼はオーケストレーター 2026-11-08。MSP#2523 を閉じたコメントの直し方）

## 起点・関連

- 前提: [IADR-0708](./IADR-0708_ai-job-scopes-bot-pr-and-third-party-image-scan.md) 決定 4（Dependabot の docker を `/deploy/cloud/**` で足した）、
  [IADR-0514](./IADR-0514_infra-image-digest-pinning-and-checker.md)（digest の固定・`[digest-mismatch]`）、
  [IADR-0525](./IADR-0525_secret-store-openbao-raft-declarative-audit.md)（OpenBao 2.7.1）、
  [IADR-0524](./IADR-0524_keycloak-26-upgrade.md)（Keycloak 26.7.4）、[IADR-0689](./IADR-0689_cloud-openbao-eso-in-cluster-credentials.md)（CI の実物の OpenBao）。
- 作業仕様書: [20261108_dependabot-docker-safe-updates](../specs/20261108_dependabot-docker-safe-updates.md)。

## コンテキストと課題

IADR-0708 の設定で出た最初の PR（MSP#2523）は 7 製品を 1 本に束ね、大版を跨ぐ更新（Prometheus v2→v3・node 22→25・collector 0.102→0.164・
Alertmanager 0.27→0.34・ESO 2.8→2.14）と、collector の `0.164.0-386`（i386 の tag）を含んでいた。オーケストレーターが閉じた。

## 決定

1. **大版を出させない**: `ignore` に `dependency-name: "*"`・`update-types: ["version-update:semver-major"]`。
2. **0.x の製品と chart と対の製品は小版も出させない**: `prom/alertmanager`・`external-secrets/external-secrets` に `version-update:semver-minor`。
   - dependency-name は registry の host を除いた名前（MSP#2523 の本文の表記。文書の ECR の例も host を除く）。
3. **collector は名指しで全部の更新を止める**（`dependency-name: "otel/opentelemetry-collector-contrib"` だけの ignore）。
   - 上流は同じ版に `-386`・`-amd64`・`-arm64` 等の tag を出す。閉じた PR で選ばれたのは数字だけの `-386` だった。
   - 文書で確かめた限り、docker で tag の接尾辞の型を除く設定は無い。`versioning-strategy` は docker が対象の外、
     `ignore.versions` は「Docker: use Bundler version syntax」（範囲の書式で、接尾辞の型は書けない）。
   - collector は大版相当（0.x の小版）の移行が要るので、止めても失う更新は無い。移行のときに ignore を外すかを決める。
4. **1 イメージ 1 本の PR**: group を `cloud-image-patch`（`update-types: [patch]`）と `cloud-image-minor`（`[minor]`）に分け、
   どちらも `group-by: dependency-name`（同じイメージを全ディレクトリで 1 本の PR に揃える）。`open-pull-requests-limit` は 5。
5. **cooldown は書かない**: MSP#2523 で Dependabot は「registry から公開日を得られず cooldown を当てられない」と警告した。効かない統制を書かない。
6. **OpenBao 2.7.1 → 2.7.2 を上げる**（修正版。上流の CHANGELOG の項目は修正と UI の改善だけで、設定の書式の変更は無い。Dockerfile と
   entrypoint は基底イメージの修正版の digest 以外に差が無い）。index の digest `sha256:5046911075e137e154ce49ab051502f4ec7cf367d484ae4a4dce665d9b90561f`
   （`docker buildx imagetools inspect` とレジストリ API の `docker-content-digest` が一致。`linux/amd64`・`linux/arm64` を含む）。
   - 写し: `deploy/cloud/vault/openbao.yaml`・`deploy/local/vault/vault-dev.yaml`、CI が入れる実物の bao
     （`scripts/lib/pinned-artifacts.sh` の表・`ci.yml`・`openbao-scripts.test.sh` の版の検査。配布物の sha256 は公式の checksums.txt と取得物の両方で一致）。
7. **Keycloak 26.7.4 → 26.8.2 は上げない**。小版だが上流の移行の手引き（26.8.0・26.8.1・26.8.2 の Breaking changes）に、本リポが使う面へ
   当たる変更が並ぶ（管理の REST API の audience の検査、protocol mapper の管理の権限、クライアント登録のポリシー、SMTP の StartTLS、
   ログイン画面の IdP のボタン、refresh token の期限）。`check-realm-constraints.js` の同梱テーマの表（`KEYCLOAK_VERSION_FOR_THEMES`）も
   配布物から引き直しが要る。大版の移行と同じ扱いにする（手順書 §大版の移行）。

## 代替案

- **`ignore.versions` に `-386` の tag を列挙する**: 採らない。版ごとに tag が増え、列挙は必ず漏れる。
- **`ignore.versions` にワイルドカード（`*-386`）**: 採らない。docker の versions の書式は Bundler の範囲で、ワイルドカードが効くと文書に無い（確かめられない）。
- **group を外す（既定の 1 依存 1 PR）**: 採らない。`directories` の glob ではディレクトリごとに PR が割れうる。node のように同じイメージが 4 か所に在ると、
  1 か所だけの PR が `[digest-mismatch]` で赤になる。`group-by: dependency-name` は文書が「同じ依存を全ディレクトリで 1 本に」と書く。
- **`/deploy/local/**` 等も対象に足して写しを Dependabot に揃えさせる**: 本 PR では採らない。compose は別の生態系（`docker-compose`）で
  `group-by` が跨げず、`src/` の Dockerfile・helm values も残る。手順書の手での揃えと `[digest-mismatch]` を続ける。

## 結果

- 試験: `scripts.repo.test.js` の IADR-0711 の言明と陰性対照（大版の ignore・collector の ignore・0.x / chart と対の小版の ignore・group の分割と group-by・cooldown を書かない）。
  `check-image-digests.js`（写しの一致）、CI の `openbao-scripts.test.sh`（2.7.2 の実物）。
- 残余:
  - `group-by` と `update-types` を同じ group に書いた組み合わせ、`dependency-name` の host を除いた名前は、文書の例に同じ形が無い。次の週次の
    Dependabot の実行（PR の題・分け方・ログの警告）で確かめる。
  - 数字だけのアーキテクチャ接尾辞を出す製品が他に増えると、同じ選び誤りが起きうる（今の対象では collector だけ）。
  - OpenBao 2.7.2 の公開は 2026-10-26。Keycloak・SDK の前例（公開から 2 週間を超えた最新）にはマージの時点で届く（2026-11-09 以降）。
