---
title: IADR-0708 AI のジョブは id-token を持たず、AI レビューは作成者か起動した者が bot の PR では走らせない（check は skipped）。配備する第三者のイメージは週次で走査して結果を Security タブだけに上げ（issue・ログ・ジョブの成否に出さない）、更新は Dependabot の docker で知らせる
type: impl-adr
status: Accepted
related_ids: [NFR-18, IADR-0232, IADR-0584, IADR-0514, IADR-0203, IADR-0581]
author: claude
created: 2026-11-08
updated: 2026-11-08
related_specs:
  - ../specs/20261108_ci-review-perms-image-scan.md
---

# IADR-0708: AI のジョブの権限と bot の PR、第三者のイメージの走査と更新

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-11-08
- 決定者: claude（依頼はオーケストレーター 2026-11-08。非公開の指摘（MSP）の medium 1 件・low 1 件）

## 起点・関連

- 前提: [IADR-0232](./IADR-0232_ci-pr-latency-reduction.md)（ジョブの権限の最小化・回収先の失敗の起票）、
  [IADR-0584](./IADR-0584_image-scan-and-codeql-js.md)（自製のイメージの走査）、[IADR-0514](./IADR-0514_infra-image-digest-pinning-and-checker.md)（digest の固定）、
  [IADR-0203](./IADR-0203_renovate-husky-hook-scope.md) / [IADR-0581](./IADR-0581_actions-sha-pin-and-dependabot.md)（依存の更新の棲み分け）。
- 作業仕様書: [20261108_ci-review-perms-image-scan](../specs/20261108_ci-review-perms-image-scan.md)。

## コンテキストと課題

- AI レビュー（`claude-code-review.yml`）と実装（`claude-coding.yml`）のジョブが `id-token: write` を持っていた。アクションには `github_token` を
  渡しており、固定の版のアクションはその場合に OIDC を求めない。workload identity も使っていない。要らない権限だった。
- AI レビューは PR のコードを実行する道具（`node`・`dotnet build/test` 等）と、PR・issue へ書けるトークンを持ち、PR の本文と差分を読む。
  bot の PR（依存の更新など）は本文・差分を上流の第三者が書く。ジョブが除いていたのは dependabot の起動と `automation/` のブランチだけで、
  `allowed_bots: '*'` だった。
- `deploy/` が digest で固定する上流のイメージ（RabbitMQ・Prometheus・Alertmanager・collector 等）は、既知の脆弱性の走査も更新の通知も無かった。
  `images.yml` の Trivy は自製のイメージだけ。

## 決定

1. 2 つの AI のジョブから `id-token: write` を外す（`scripts.repo.test.js` の JOB_SCOPES が固定する）。workload identity・Bedrock・Vertex を使うなら本 IADR を改定して戻す。
2. `claude-review` は、作成者または起動した者のログインが `[bot]` で終わる PR では**ジョブ単位の if で走らせない**。
   - 必須チェック `claude-review` はそのとき skipped になり、GitHub は合格として扱う（dependabot の PR で従前から同じ）。ステップの中で落とす形にしない
     （bot の PR が全部マージ不能になる）。ジョブ名・起動の types は変えない。bot の PR の確認は人のレビュー（CODEOWNERS）が持つ。
   - 判定はログインの接尾辞で行う（`user.type` は #524 の規則で全ワークフローに禁じている）。
   - `allowed_bots` は `''`（既定）。if を緩めても bot の起動はアクションが拒む。
   - 静的な読解だけに落として走らせる形は採らない（下の代替案）。
3. 配備する第三者のイメージを週次（と手動）で走査する `third-party-image-scan.yml` を置く。
   - 対象は `check-image-digests.js --scan-targets`（`deploy/` の第三者・digest 固定・補間なし・重複なし）。固定の検査器と同じ抽出を使い、母集合を 2 つに割らない。
   - Trivy（`images.yml` と同じ固定の版。v0.70.0 をワークフローに明示する）で CRITICAL / HIGH・修正版ありを SARIF にし、
     週次・手動の実行だけが code scanning へ上げる。
   - 🔴 **本リポジトリは public なので、結果を公開の面へ出さない。** 通知は Security タブの alert（書き込みの権限を持つ人だけが見る）に一本化する。
     - issue は起票しない（`ci-failure-issue.yml` を呼ばない）。
     - 見つかった脆弱性でジョブを落とさない（イメージごとのジョブの成否も公開の面である）。落とすのは走査そのものが失敗したときだけ。
     - ログ・注記・実行の要約に、イメージごとの件数もイメージ名も出さない。ジョブ名は番号にする。PR の run は SARIF を上げず、完走したかだけを示す。
     - 受け入れはリポジトリの ignore ファイル（`.trivyignore` 等）で行わない（公開のファイルに残す CVE を書くことになる）。Security タブの dismiss に
       理由と見直しの日を書き、オーナーが月に 1 回見直す。
     - いずれも `scripts.repo.test.js` が陰性対照つきで固定する（起票の呼び出し・件数を数える step・名前や件数の echo・結果での exit・PR での上げ・ignore ファイル）。
   - 必須チェックにしない（paths を持つ）。
   - code scanning の category は、参照から digest を落とし英数字と `. _ / -` だけにした名前（`third-party-image/<repo>-<tag>`）。
     `:` `@` と 64 桁の digest を入れない。対象の間で重ならないこと・文字種と 100 字以内を試験が固定する。
4. 更新の通知は Dependabot の docker（`/deploy/cloud/**`・1 本の PR へまとめる）。棲み分け（IADR-0203 / IADR-0581）の「Dependabot が持つ生態系」に docker を足す。
   同じイメージの他の参照（`deploy/local/`・compose・helm values）は手順書で揃え、片側だけの更新は `[digest-mismatch]` が止める。
5. 手順書は `docs/operations/third-party-image-scan-runbook.md`。

## 代替案

- **bot の PR では道具を外した静的なレビューに落とす**: 採らない。`--allowedTools` は検査器（`check-ai-workflow-config.js`・試験）が文字列で読んでおり、
  式で切り替えると両方が読めなくなる。プロンプトの下限（`scripts.test.js`・`helm lint` の実走）とも衝突し、`check-review-verdict.js` が落とす。
- **ステップで判定して `exit 1`**: 採らない。必須チェックが赤になり、bot の PR がマージ不能になる。
- **Renovate の kubernetes manager**: 採らない。本リポで Renovate の PR は直近 200 本で 0 本（稼働を確かめられない）。Dependabot は動いている（25 本）。
- **PR でも走査で落とす**: 採らない。既存の古い版が全部の `deploy/` の PR を赤くし、赤を読み流す癖が付く。上げる PR の注記で足りる。
- **走査の結果を表・件数でログ・実行の要約・注記へ出す／失敗を issue に起票する／脆弱性でジョブを落とす**: 採らない。public リポジトリでは
  どれも誰でも読める面で、どのイメージに既知の脆弱性が残るかを公開することになる。
- **受け入れを Trivy の ignore ファイル（期限つきの YAML）で行う**: 採らない。期限で自動的に戻る利点はあるが、残す CVE を公開のファイルに書くことになる。
  dismiss には期限が無いので、オーナーの月次の見直しで補う。

## 結果

- 試験: `scripts.repo.test.js`（JOB_SCOPES、IADR-0708 の言明と陰性対照: id-token・bot の除外・allowed_bots・ジョブ名・結果を公開の面へ出さないこと・ignore ファイルを置かないこと・category・dependabot）、
  `check-image-digests.js --self-test`（`scanTargets`）。
- 残余: bot の PR は AI レビューを受けない。alert の扱い（上げる・dismiss・移行を計画へ）は手順書に従い、Security タブの中で行う。
  dismiss の見直しは期限で自動的に戻らない（オーナーの月次の作業）。走査そのものの失敗は issue にならず、ワークフローの失敗の通知だけになる。アクションが `origin` をトークン入りの URL へ書き換える件は IADR-0232 の記録のとおり（権限の範囲とジョブ終了での失効が縛る）。
