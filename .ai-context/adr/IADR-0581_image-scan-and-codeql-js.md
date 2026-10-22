---
title: IADR-0581 建てたサービスイメージを Trivy で走査して実行サマリに載せ（当面は報告だけ）、CodeQL に JavaScript / TypeScript を足す
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0580, IADR-0578, IADR-0067, IADR-0232]
author: claude
created: 2026-10-22
updated: 2026-10-22
related_specs:
  - ../specs/20261022_image-scan-and-codeql-js.md
---

# IADR-0581: イメージの走査と CodeQL の JS/TS（GHSA-552h-47r7-6v6p）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-22
- 決定者: オーナー指示（2026-10-22）・claude（走査の置き場と落とし方）

## 起点・関連

- 起点: GHSA-552h-47r7-6v6p の 3 本目（多層防御の強化）。
- 前提: [IADR-0067](./IADR-0067_service-image-build-ci-gate.md)（イメージのビルド検証と集約 `image-build`）・[IADR-0232](./IADR-0232_ci-pr-latency-reduction.md)（CodeQL の PR は build-mode: none）・[IADR-0580](./IADR-0580_actions-sha-pin-and-dependabot.md)（SHA 固定）。

## コンテキストと課題

依存（nuget / npm）とコンテナの基底イメージの既知の脆弱性は、成果物の単位では見ていなかった。CodeQL は C# だけを解析しており、フロントエンドと検査器（JS/TS）は対象外だった。

## 検討した選択肢

1. **走査用の別ジョブで建て直して走査する** — 必須の集約から切り離せるが、全サービスのビルドが倍になる。
2. **build ジョブの中で建てた直後に走査し、当面は報告だけにする（採用）** — ビルドは 1 回。落とす形へ切り替えるときは exit-code を変えるだけで済む。
3. **build ジョブの中で落とす形にする** — 走査が実質の必須チェックになる。必須にするかはオーナーが決める（指示 2026-10-22）。

## 決定

1. **`build (<service>)` で建てたイメージを Trivy で走査する**（脆弱性・CRITICAL / HIGH・修正版あり）。`exit-code: "0"` ＋ `continue-on-error: true` で落とさず、表を実行サマリへ載せる。アクションは SHA 固定（IADR-0580）。
2. **CodeQL の行列に `javascript-typescript` を足す。** build-mode は常に none。dotnet の step は csharp の脚に限る。PR の `paths` に JS/TS の拡張子と `src/pnpm-lock.yaml` を足す（必須チェックではない）。
3. 形は `scripts.repo.test.js` が固定する。落とす形へ変えるときは、試験と本 IADR を同時に改定する。

## 結果

- 良い影響: サービスイメージの既知の脆弱性が PR ごとに見える。JS/TS のコードが CodeQL の解析に乗る。
- 悪い影響: build ジョブが走査の分だけ長くなる（脆弱性 DB の取得を含む）。CodeQL の PR の起動が JS/TS の変更でも走る。

## 残余

1. **走査を必須（落とす形）にするかは未決**（オーナーの裁定待ち）。決めるまでは報告だけで、見落としは人の確認に依存する。
2. `build-local (platform-backup)` のイメージは走査しない（compose の外。必要なら同じ形で足す）。
3. 脆弱性 DB の取得に失敗した回は表が空になる（サマリに「結果が無い」と出す。合格ではない）。
