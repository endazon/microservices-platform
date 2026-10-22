---
title: IADR-0583 建てたサービスイメージを Trivy で走査して実行サマリに載せ（当面は報告だけ）、CodeQL に JavaScript / TypeScript を足す
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0581, IADR-0578, IADR-0067, IADR-0232]
author: claude
created: 2026-10-22
updated: 2026-10-22
related_specs:
  - ../specs/20261022_image-scan-and-codeql-js.md
---

# IADR-0583: イメージの走査と CodeQL の JS/TS（GHSA-552h-47r7-6v6p）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-22
- 決定者: オーナー指示（2026-10-22）・claude（走査の置き場と落とし方）

## 起点・関連

- 起点: GHSA-552h-47r7-6v6p の 3 本目（多層防御の強化）。
- 前提: [IADR-0067](./IADR-0067_service-image-build-ci-gate.md)（イメージのビルド検証と集約 `image-build`）・[IADR-0232](./IADR-0232_ci-pr-latency-reduction.md)（CodeQL の PR は build-mode: none）・[IADR-0581](./IADR-0581_actions-sha-pin-and-dependabot.md)（SHA 固定）。

## コンテキストと課題

依存（nuget / npm）とコンテナの基底イメージの既知の脆弱性は、成果物の単位では見ていなかった。CodeQL は C# だけを解析しており、フロントエンドと検査器（JS/TS）は対象外だった。

## 検討した選択肢

1. **走査用の別ジョブで建て直して走査する** — 必須の集約から切り離せるが、全サービスのビルドが倍になる。
2. **build ジョブの中で建てた直後に走査し、当面は報告だけにする（採用）** — ビルドは 1 回。落とす形へ切り替えるときは exit-code を変えるだけで済む。
3. **build ジョブの中で落とす形にする** — 走査が実質の必須チェックになる。必須にするかはオーナーが決める（指示 2026-10-22）。

## 決定

1. **`build (<service>)` で建てたイメージを Trivy で走査する**（脆弱性・CRITICAL / HIGH・修正版あり）。`exit-code: "0"` ＋ `continue-on-error: true` で落とさず、表を実行サマリへ載せる。アクションは SHA 固定（IADR-0581）。
2. **CodeQL の行列に `javascript-typescript` を足す。** build-mode は常に none。dotnet の step は csharp の脚に限る。PR の `paths` に JS/TS の拡張子と `src/pnpm-lock.yaml` を足す（必須チェックではない）。
3. 形は `scripts.repo.test.js` が固定する。落とす形へ変えるときは、試験と本 IADR を同時に改定する。

## 固定する trivy-action の確かめ（2026-10-22）

同アクションには、2026-03-19〜20 にタグが改ざんされた公表済みの事案がある（aquasecurity/trivy の公開 advisory GHSA-69fq-xp46-6x23。CVE-2026-33634）。advisory は「0.35.0（immutable）と 2025-04-09 以降のコミットへの SHA 固定は安全」とし、古いタグは削除されて `v` 付きで復元されている。固定する `ed142fd0673e97e23eac54620cfb913e5ce36c25`（v0.36.0）は次のとおり確かめた。

| 観点 | 確かめた内容 | 手段 |
| --- | --- | --- |
| リリース | v0.36.0 は **Immutable release**。参照コミットは `ed142fd`。2026-04-22 公開（事案と是正の後） | GitHub のリリースのページ |
| タグ | 注釈付きタグ（`v0.36.0`）。メンテナの SSH 署名つきで、GitHub 上で Verified | `git cat-file -p refs/tags/v0.36.0`・リリースのページ |
| コミット | `master` に到達する。同アクションのリポジトリのプルリクエスト 563 のマージ（committer GitHub）。署名の鍵 ID は GitHub の web-flow 鍵 `B5690EEEBB952194` で、GitHub 上で Verified | `git branch --contains`・`git cat-file -p`・リリースのページ |
| 依存 | 内部で使う `aquasecurity/setup-trivy` は `3fb12ec12f41e471780db15c232d5dd185dcb514`（v0.2.6）に SHA 固定。advisory が「安全な内容で作り直した」とする v0.2.6 のタグと一致する。既定の trivy 本体は v0.70.0（事案の時点では未リリース。advisory の悪性版 v0.69.4〜0.69.6 ではない） | `git show v0.36.0:action.yaml`・`git ls-remote` |

- この環境では GitHub の公開鍵を取得できず、**署名の暗号学的な検証は手元で行っていない**（GitHub 上の Verified の表示に依る）。
- 代替案（アクションを使わず、trivy 本体を公式リリースからチェックサム（と cosign）で検証して取得する形）と比べた。代替案はアクションの層を除けるが、取得・検証・DB の取得を自前で書いて保守することになる。上の確かめが揃っているため、アクションの SHA 固定を採る。Dependabot が SHA を上げる PR では、同じ観点（Immutable・Verified・依存の固定）を確かめてからマージする。

## 結果

- 良い影響: サービスイメージの既知の脆弱性が PR ごとに見える。JS/TS のコードが CodeQL の解析に乗る。
- 悪い影響: build ジョブが走査の分だけ長くなる（脆弱性 DB の取得を含む）。CodeQL の PR の起動が JS/TS の変更でも走る。

## 残余

1. **走査を必須（落とす形）にするかは未決**（オーナーの裁定待ち）。決めるまでは報告だけで、見落としは人の確認に依存する。
2. `build-local (platform-backup)` のイメージは走査しない（compose の外。必要なら同じ形で足す）。
3. 脆弱性 DB の取得に失敗した回は表が空になる（サマリに「結果が無い」と出す。合格ではない）。
