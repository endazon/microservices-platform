---
title: 作業仕様書 — Dependabot の docker の更新を切り分けられる形にし、互換の明らかな修正版（OpenBao 2.7.2）だけを上げる
type: spec
status: in-progress
related_ids: [NFR-18, IADR-0711, IADR-0708, IADR-0514, IADR-0525, IADR-0524]
author: claude
created: 2026-11-08
updated: 2026-11-08
related_specs:
  - 20261108_ci-review-perms-image-scan.md
---

# 作業仕様書 — Dependabot の docker の安全な更新

> 着手 2026-11-08（develop `59a542f53`＝MSP#2516 のマージ）。判断の記録は IADR-0711。
> 起点: Dependabot の最初の docker の PR（MSP#2523）をオーケストレーターが閉じたコメントの「直し方」。

## 閉じた理由（MSP#2523 のコメント）と本 PR の対応

| 理由 | 対応 |
| --- | --- |
| collector の `0.164.0-386`（i386 の tag）を選んだ | collector の更新を名指しで止める（決定 3）。手順書に接尾辞の無い multi-arch の tag の確かめ方を書く |
| 大版を跨ぐ更新が混ざった | 全製品の semver-major を ignore。0.x（Alertmanager）と chart と対（ESO）は小版も ignore |
| 7 製品を 1 本に束ねた | 1 イメージ 1 本（`group-by: dependency-name`）。修正版と小版を別の group |

## Dependabot の設定の根拠（公式文書。2026-11-08 に取得）

出典: GitHub Docs「Dependabot options reference」・「Controlling which dependencies are updated by Dependabot」・
「Optimizing the creation of pull requests for Dependabot version updates」（`docs.github.com` の記事本文）。

| 設定 | 文書の記述 | 本 PR の使い方 |
| --- | --- | --- |
| `ignore.update-types` | 「Supported values: `version-update:semver-patch`, `version-update:semver-minor`, and `version-update:semver-major`」。例 4 は `ignore` に `update-types: ["version-update:semver-major"]` | `dependency-name: "*"` で大版を除く。Alertmanager・ESO は minor も |
| `ignore.dependency-name` | 「optionally using `*` to match zero or more characters」。Docker は「The full name of the repository」で、ECR の例は host を除いた `base/foo/bar/ruby` | collector を名指し（versions も update-types も書かない＝全部の更新）。名前は host を除く（MSP#2523 の本文も `external-secrets/external-secrets`・`keycloak/keycloak`） |
| `ignore.versions` | 「Docker: use Bundler version syntax」 | **使わない**。範囲の書式で、接尾辞の型（`-386`）は書けない |
| `versioning-strategy` | 「Supported by: `bundler`, `cargo`, `composer`, `helm`, `mix`, `npm`, `pip`, `pub`, and `uv`」 | **使えない**（docker は対象の外） |
| Versioning tags（Docker） | 前リリースの型は `alpha`・`beta`・`rc`・`dev`・`preview`・`pre`・`nightly`・`snapshot`・`canary`・`unstable`（heuristic detection）。「The `dependabot.yml` file doesn't control the versioning tags」 | アーキテクチャ接尾辞は前リリースの型に入らない。設定で型を足せない |
| `groups.update-types` | 「Limit the group to one or more semantic versioning levels. Supported values: `minor`, `patch`, and `major`」 | `cloud-image-patch`（patch）・`cloud-image-minor`（minor） |
| `groups.group-by` | 「`dependency-name` … create a single pull request for each dependency update across all specified directories」。制限: 同じ生態系・version updates だけ | 両方の group に書く（1 イメージ 1 本・全ディレクトリで揃える） |
| `cooldown` | 既定 3 日。Docker は対応 | **書かない**。MSP#2523 の本文で Dependabot が「Cooldown could not be applied because no publication date was available from the registry」と警告した |

### 確かめられなかった点

1. **`-386` を選んだ仕組み**。Docker Hub の tag の一覧（`0.102.0`・`0.102.0-386`・`-amd64`・`-arm64`・`-armv7`・`-ppc64le`・`-s390x`。0.164.0 も同じ形）のうち、
   選ばれたのは数字だけの `-386` だった。数字だけの接尾辞を版の続きと読み、英字の接尾辞を別の型と扱っていると**推定**する。文書には無い（dependabot-core の
   実装は読んでいない）。
2. **`group-by` と `update-types` を同じ group に書いた組み合わせ**。文書に禁止は無いが、同じ形の例も無い。次の週次の Dependabot の実行で、PR の題（group 名）・
   1 イメージ 1 本に分かれたか・ログの警告を見て確かめる。
3. **host を除いた `dependency-name` が ignore で一致すること**。MSP#2523 の本文の表記と ECR の例からの推定。2 と同じく次の実行で確かめる（ESO・Alertmanager の小版、
   collector の更新が出ないこと）。
4. 数字だけのアーキテクチャ接尾辞の除外は**collector の名指しでしか書けない**。他の製品が同じ tag を出し始めたら同じ選び誤りが起きうる（今の対象では collector だけ）。

## 上げたもの・上げなかったもの

| 製品 | 判断 | 根拠 |
| --- | --- | --- |
| **OpenBao 2.7.1 → 2.7.2** | **上げる** | 修正版。上流の CHANGELOG（2.7.2・2026-10-26）の項目は修正（core・plugins・auth・secrets の各エンジン）と UI の改善だけで、設定の書式の変更は無い。既定の変わる項目は ssh の証明書の識別子テンプレートのワイルドカード（本リポは ssh を使わない）だけ。approle の role ID の path の正規化は、本リポの role 名 `cloud-operator`（正規の形）に当たらない。v2.7.1 と v2.7.2 の `Dockerfile` の差は基底イメージ（alpine・ubi・distroless）の digest だけ、`.release/docker/docker-entrypoint.sh` は同一（raw で取得して diff）。`vault` へのリンクも同じ |
| Keycloak 26.7.4 → 26.8.2 | **上げない** | 小版だが、上流の移行の手引き（`docs/documentation/upgrading/topics/changes/changes-26_8_{0,1,2}.adoc` を 26.8.2 のタグで取得）の Breaking changes に、本リポが使う面へ当たる項目が並ぶ: 管理の REST API の audience の検査、protocol mapper の管理を realm 管理者に限る、クライアント登録のポリシーの既定のロールと full scope の制限、SMTP の StartTLS の必須、view だけの権限で client secret を返さない、refresh token の期限が session idle に従う、ログイン画面の IdP のボタンの再設計。`check-realm-constraints.js` の同梱テーマの表（`KEYCLOAK_VERSION_FOR_THEMES = '26.7.4'`）も配布物から引き直しが要る。互換が明らかとは言えない |
| Prometheus v2 → v3・node 22 → 25・collector・Alertmanager・ESO | 上げない（依頼の範囲の外） | 大版（相当）。検証の項目を手順書 §大版の移行に並べた |

### OpenBao 2.7.2 の確かめ（証跡）

- `docker buildx imagetools inspect openbao/openbao:2.7.2` → `MediaType: application/vnd.oci.image.index.v1+json`・
  `Digest: sha256:5046911075e137e154ce49ab051502f4ec7cf367d484ae4a4dce665d9b90561f`。Manifests に `linux/amd64`・`linux/arm64`（ほか arm/v6・ppc64le・riscv64・s390x）。
  全 platform の image の label `org.opencontainers.image.version` は `v2.7.2`、created は `2026-10-26T19:32:27Z`。
- レジストリ API（`registry-1.docker.io/v2/openbao/openbao/manifests/2.7.2`。index の Accept）の `docker-content-digest` が同じ値。content-type は OCI の image index。
- MSP#2523 が提案した digest も同じ値（単一アーキテクチャの digest ではない）。
- CI が入れる実物の bao: `openbao_2.7.2_linux_amd64.tar.gz` の sha256 `582b1f3c…cbc96` は、公式の `checksums.txt`（v2.7.2）の行と、取得したファイルの `sha256sum` の両方で一致。
  手元では bao を実行していない（取得物の実行は環境の規則で止められた）。実物で通すのは CI の `Test cloud OpenBao procedures (real OpenBao, kubectl stub)`。

## 母集合（規則 9・10）

- 誤りの側の文字列で全文書を走査した: `git grep -n "2\.7\.1\b\|6d2b9385"`（specs・superpowers を除く）。

| 出現 | 扱い |
| --- | --- |
| `deploy/cloud/vault/openbao.yaml`・`deploy/local/vault/vault-dev.yaml` の `image:`（`check-image-digests.js --list` の 2 か所） | 直す |
| `vault-dev.yaml` の版の注記・`deploy/local/vault/README.md` の製品の説明 | 直す |
| `scripts/lib/pinned-artifacts.sh`（表と注記）・`ci.yml`（版と注記）・`openbao-scripts.test.sh`（版の検査） | 直す（「宣言のイメージと同じ版」の対） |
| `docs/operations/secret-store-openbao-migration-runbook.md` の確認の表の `version` | 直す（宣言のイメージの版を見る） |
| `scripts/k8s-local-up.test.js` の `STUB_VAULT_LIVE_IMAGE`（2 か所） | 直さない。「稼働中が OpenBao」の模擬で、版は判定に使わない（旧 Vault との区別だけ）。古い版が稼働している形として正しい |
| `deploy/cloud/vault/lib.sh` の「OpenBao 2.7.1 の CLI で確かめた」・`openbao-scripts.test.sh` の「2.7.1 の実測」 | 直さない。その時点の実測の記録（2.7.2 で同じ形かは CI の実物の試験が見る） |
| `docs/operations/operations.md` の初回の点検の表・本文 | 直さない。点検の日の記録（「初回の表は書き換えない」の規則） |
| `.ai-context/adr/`（IADR-0525・0585・0689・0696・0697）・README の索引の題 | 直さない。凍結の記録 |
| `src/` の Testcontainers・Dockerfile | 出現なし（OpenBao は `src/` に無い） |

- Keycloak は上げないので写しは触らない（`git grep "26\.7\.4\|82a77884"` の出現は 60 件超。上げる PR で同じ走査が要る）。
- `anthropics/claude-code-action` の行末のコメント（v1.0.267）: 着手の時点では #2522 が open だったので直さなかった。#2522 のマージ（develop `472a45ceb`）を取り込んだ後に、2 つのワークフロー（`claude-code-review.yml`・`claude-coding.yml`）の注記を v1.0.268 へ直した。

## 方式

1. `.github/dependabot.yml` の docker（IADR-0711 決定 1〜5）。
2. OpenBao の写しを 2.7.2 へ（決定 6）。
3. 手順書 `docs/operations/third-party-image-scan-runbook.md` に §Dependabot が出さない更新・§大版の移行（Prometheus・Alertmanager・collector・ESO・node・Keycloak の確かめる項目）。
4. `scripts.repo.test.js` に IADR-0711 の言明と陰性対照。
5. #2522 のマージ後に develop を取り込み、`claude-code-action` の行末の注記を v1.0.268 へ直す（SHA は #2522 が上げた `c0949c50` のまま）。

## 受け入れ基準

| # | 基準 | 試験 |
| --- | --- | --- |
| AC1 | docker の項目が全製品の semver-major を ignore する | `scripts.repo.test.js` IADR-0711 |
| AC2 | collector を名指しで止め、Alertmanager・ESO の小版を止める | 同上 |
| AC3 | group が patch と minor に分かれ、どちらも `group-by: dependency-name` | 同上 |
| AC4 | cooldown を書かない（効かない統制を書かない） | 同上 |
| AC5 | 陰性対照: 大版の ignore・collector の ignore・group の分割・group-by を外すと赤 | 同上 |
| AC6 | OpenBao の写しが全部 2.7.2 の index の digest で揃う | `node scripts/check-image-digests.js`（`[digest-mismatch]` なし） |
| AC7 | CI の実物の bao が 2.7.2 で、sha256 が公式の値と一致 | CI の `Install OpenBao and age`・`Test cloud OpenBao procedures` |
| AC8 | 既存の試験・文書の検査が緑（0709・0710 の欠番による採番の検査の赤は既知） | `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` ほか |

## 残余

- 確かめられなかった点 2・3 は次の週次の Dependabot の実行で確かめる（出た PR の題と分け方、ESO・Alertmanager の小版と collector が出ないこと）。
- Dependabot は `deploy/cloud/**` だけを更新するので、写し（`deploy/local`・compose・helm values・`src/`）は手で揃える運用が続く。
- Keycloak 26.8 と大版の移行は、手順書 §大版の移行の項目を 1 製品ずつ別の PR で。
