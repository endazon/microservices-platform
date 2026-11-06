---
title: IADR-0690 公開のファイルから非公開の指摘の識別子を除き（凍結記録も本文を置換する例外）、advisory の識別子は公開の第三者の allowlist だけを許す検査器で再混入を止める
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0307, IADR-0377, IADR-0572, IADR-0573, IADR-0574, IADR-0575, IADR-0576, IADR-0577, IADR-0578, IADR-0579, IADR-0580, IADR-0581, IADR-0582, IADR-0583, IADR-0584, IADR-0585, IADR-0586, IADR-0587, IADR-0588, IADR-0589, IADR-0590, IADR-0591, IADR-0606, IADR-0607, IADR-0247, AST:IADR-0624]
author: claude
created: 2026-11-07
updated: 2026-11-07
related_specs:
  - ../specs/20261107_2479_private-advisory-id-scrub.md
---

# IADR-0690: 非公開の指摘の識別子の除去と、advisory の識別子の allowlist 検査

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-11-07
- 決定者: 利用者の裁定（2026-11-06）・claude（起案・実装）

## 起点・関連

- issue #2479。非機能 `NFR`（メタ作業。計画の非機能要件表に当たる番号は無い）。
- 取引ユニットの側の同じ決定: AST の IADR-0624（AST#1567）。検査器の規則をそろえる。
- 検査器の作法の前例: IADR-0247（追跡下すべてを走査・0 件走査で緑にしない）。
- 作業仕様書: [`.ai-context/specs/20261107_2479_private-advisory-id-scrub.md`](../specs/20261107_2479_private-advisory-id-scrub.md)

## コンテキストと課題

- 本リポジトリは公開である。非公開の security advisory の識別子が、IADR・作業仕様書・chart・ローカルの配備物・起動器・CI・試験名・コードのコメント・作業仕様書のファイル名に残っていた。
- 非公開の advisory の識別子を公開の場に置くと、未公開の指摘の存在と対象を外部へ示すことになる。
- 依存パッケージの既知脆弱性を指す**公開の第三者 advisory** の識別子は、根拠の記録として残す価値がある。
- `.ai-context/` は凍結記録であり、本文を後から書き換えない規律がある。

## 検討した選択肢

1. **凍結記録も含めて本文を中立表記へ置換し、記録ごとに日付つきの追記を 1 行だけ足す（採用）** — 公開のツリーから識別子が消える。凍結を破った事実は追記と本 IADR に残る。
2. 凍結記録は残し、生きた文書とコードだけを置換する — 公開のツリーに識別子が残り、裁定の目的を果たさない。採らない。
3. 履歴まで書き換える（force-push） — 本リポジトリは破壊的な git 操作をしない。過去の版に残るものは利用者が残余として受容した。採らない。

再混入の検査器の形:

- **(a) allowlist だけ（採用）** — `GHSA-` で始まる識別子は公開の第三者の allowlist にあるものだけを許す。検査器は非公開の識別子を持たない。
- (b) 非公開の識別子のハッシュを照合する — 前置きなしの断片も捕まえられるが、検査器に識別子の派生物を置くことになる。利用者裁定で入れない。

## 決定

1. **凍結の例外の射程。** 利用者裁定により、`.ai-context/adr/` の IADR と `.ai-context/specs/` の作業仕様書も、**識別子の置換だけ**を目的に本文を書き換える。**決定の内容は変えない。** 触った記録ごとに日付つきの追記を 1 行だけ足し、`updated:` を進める（持たない記録には足さない）。追記・本 IADR・コミット・PR には、識別子・旧ファイル名・旧→新の対応表を書かない。`.ai-context/superpowers/` は該当が無く、触っていない。
2. **置換の規則。**
   - 単独の参照（frontmatter の `issue:` を含む）は、帰属に従って「非公開の指摘（MSP）」または「非公開の指摘（AST）」とする。
   - 試験名の接頭辞は中立な主題名「CI 堅牢化:」、試験の一時ディレクトリの接頭辞は `msp-ci-hardening-` とする。
   - 作業仕様書の名前は、`YYYYMMDD_` の直後の識別子の部分を除く（`git mv` と参照の追随を同じコミットに置く）。
   - IADR の見出しと索引の題名は、識別子の部分だけを中立表記にする。
   - 公開の第三者 advisory の識別子は残す。
3. **触ったファイル**（新しい名前で書く）:
   - IADR（24 件）: IADR-0307・IADR-0377・IADR-0572〜IADR-0591・IADR-0606・IADR-0607、索引 `.ai-context/adr/README.md`。
   - 作業仕様書（25 件。うち改名 16 件）: `20261022_abac-seeder-disabled-by-default`・`20261022_actions-sha-pin-and-dependabot`・`20261022_admin-edge-default-closed`・`20261022_answer-sanitize-allowlist-and-csp`・`20261022_bff-externalsecret-admission-policy`・`20261022_bff-opend-auth-caller-secret`・`20261022_ci-workflow-trust-and-injection-hardening`・`20261022_compose-loopback-bind`・`20261022_dev-secret-guard-all-sa-clients`・`20261022_edge-block-keycloak-admin`・`20261022_egress-default-deny`・`20261022_graph-prompt-boundary-and-bff-headers`・`20261022_image-scan-and-codeql-js`・`20261022_infra-credentials-fail-closed`・`20261022_pod-security-context`・`20261022_rabbitmq-msp-per-service-users`・`20261022_rabbitmq-per-service-users`・`20261022_rag-prompt-trust-boundary`・`20261022_vault-k8s-auth-audience`・`20261023_department-sync-service-account-and-arm-b`・`20261023_infra-tls-cert-reload`・`20261023_infra-tls-stage1`・`20261023_machine-principal-issuing-client`・`20261023_mesh-mtls-strict-default`・`20261024_2193_k8s-api-token-separate-from-vault`。
   - 配備物: `deploy/local/`（31 件）・`deploy/helm/`（15 件）・`deploy/docker-compose.yml`・`deploy/rabbitmq/`。
   - 起動器・検査器・試験: `scripts/`（32 件。`scripts.repo.test.js` の試験名と一時ディレクトリの接頭辞、`action-versions.repo.json` を含む）。
   - CI: `.github/workflows/`（6 件）・`.github/dependabot.yml` のコメント。
   - コードのコメント: `src/platform/backend`（13 件）・`src/knowledge/backend`（12 件）・`src/platform/frontend`（7 件）・`src/knowledge/frontend`（3 件）・`src/eslint.config.js`。
   - 文書: `docs/`（7 件。trace ブロックと frontmatter の作業仕様書名、可視本文の参照）。
4. **再混入を止める検査器 `scripts/check-advisory-ids.js`。** 追跡下の全ファイル（submodule の中は出ない）の本文で `GHSA-` で始まる識別子を拾い（大文字小文字を区別しない）、公開の第三者 advisory 12 件の allowlist にあるものだけを許す。
   - 完全形は allowlist と完全一致するときだけ許す。
   - 2 区画の形（先頭の 2 区画だけ）は、allowlist の完全形の先頭 2 区画と一致するときだけ許す。
   - 短縮形（先頭の区画だけ）は、allowlist の先頭の区画と一致するときだけ許す。
   - 1 件でも外れれば exit 1。`git ls-files` の失敗と、走査件数が下限を下回ったときも exit 1（IADR-0247 と同じ作法）。
   - 規則は AST の `check-advisory-ids.js` とそろえる。
   - CI は `ci.yml` の自己試験と本検査の 2 ステップ、試験は `scripts/scripts.repo.test.js`（陰性対照と変異試験つき）。
5. **allowlist に足す規則。** 公開済みの第三者 advisory（依存パッケージの既知脆弱性）を新たに引くときだけ、理由を添えて足す。自リポジトリ・取引ユニットの非公開の指摘は足さず、中立表記で書く。
6. **submodule の前進。** 同じ PR で `src/ai-stock-trading` を、取引ユニットの側の除去を含む AST の develop へ進める。

## 理由

- 公開のツリーから識別子を除くことが裁定の目的であり、凍結記録だけを残すと目的を果たさない。
- allowlist の形は、検査器そのものが漏えいにならない。新しい非公開の指摘が増えても検査器を変えずに止められる。
- 両リポジトリで規則をそろえると、片方だけ緩い形が残らない。

## 結果

- 良い影響: 公開のツリー（submodule の中を含めて）に非公開の識別子が 0 件になる。前置きつきで新たに書かれれば CI が止める。
- 悪い影響・トレードオフ:
  - **前置きなしの断片（識別子の一部を含む名前・接頭辞など）は検査器で捕まらない**（残余。書き手とレビューで守る）。
  - 過去のコミット本文・過去の版・PR の Commits タブ・本文の編集履歴には残る（利用者が残余として受容済み）。
  - 凍結記録の本文が裁定の時点と字面で食い違う。日付つきの追記で読み分ける。
- フォローアップ: なし。

## 関連

- Supersedes: なし
- Superseded by: なし
