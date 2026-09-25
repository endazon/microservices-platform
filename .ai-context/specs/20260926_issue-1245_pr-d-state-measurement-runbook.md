---
title: 作業仕様書 — #1245 PR-D の状態作りと実測を利用者が稼働クラスタで行うための手順書を置く
type: spec
status: done
related_ids:
  - SC-15
  - SC-13
  - SC-10
  - FR-05
  - NFR-13
  - ADR-0026
  - ADR-0045
  - ADR-0078
  - ADR-0094
  - ADR-0097
  - ADR-0103
  - ADR-0108
  - IADR-0347
  - IADR-0369
  - IADR-0404
  - IADR-0421
  - IADR-0427
  - IADR-0432
  - IADR-0463
author: claude
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0078_existence-hiding-response-indistinguishability-and-nearby-mta.md
  - planning:projects/microservices-platform/07_adr/ADR-0094_existence-hiding-timing-median-consistency-and-response-floor.md
  - planning:projects/microservices-platform/07_adr/ADR-0097_timing-floor-release-default-on-and-periodic-review.md
  - planning:projects/microservices-platform/07_adr/ADR-0103_degenerate-self-control-is-not-a-bound.md
  - planning:projects/microservices-platform/07_adr/ADR-0108_timing-samples-at-microsecond-resolution.md
related_specs:
  - 20260907_issue-1245_reset-gate
  - 20260909_issue-1245_mail-relay-observation
  - 20260911_issue-1245_login-existence-disclosure
  - 20260926_1525_timing-resolution-t25
issue: "#1245"
---

# 作業仕様書 — #1245 PR-D の状態作りと実測の手順書

## 起点

- issue: **#1245**（`fix(SC-15,FR-05,ADR-0026,ADR-0045)`。OPEN）。AI が先行できる段（PR-0 #1388 / PR-A #1305・#1310・#1314 /
  PR-B #1355 / PR-C #1319）はすべて着地済み（2026-09-11 の監査コメントで確認済み。本作業でも下の「読んだもの」で引き直した）。
- 残るのは **PR-D**: 稼働クラスタで relay を意図的に落として状態 A / C1 / C2 / C2' / C3 / D を作り、
  実在／非実在を対で申請し、窓の秒数・門の `close` PUT・キュー指標のしきい値・ログインのロックアウトを実測すること。
  **状態を作るのは利用者の手であり、AI は稼働クラスタに触れない。**
- 本作業の成果物は**利用者が実行する手順書**（`docs/operations/password-reset-relay-state-measurement-runbook.md`）である。
  実測値そのものは本作業では得られない（得ようとしない）。

## 射程

### やること

1. 新規の Runbook を 1 本置く（`type: runbook`、frontmatter と trace ブロックは
   `docs/operations/keycloak-smtp-relay-setup-runbook.md` に倣う）。内容は依頼の 1〜5:
   1. 安全の前置き（他のワークロードと同居するクラスタ・事前スナップショット・スナップショットへ戻す復元手順・中止条件）
   2. 状態 A / C1 / C2 / C2' / C3 / D の作り方・対の申請・期待値・窓の秒数の測り方・`close` PUT の証跡・再開の挙動
   3. キューのアラート 3 件の暫定しきい値を確定するために記録するもの
   4. ログインのロックアウトを専用の試験利用者で意図的に発火させる手順と、解除の手順
   5. 記録の雛形（issue へのコメントと計画への環流の置き場所、各行が満たす #1245 の受け入れ基準）
2. リポジトリから導けないことは**未決事項として明示**する（推測で埋めない）。

### やらないこと（編集禁止のファイル）

| ファイル | 理由 |
| --- | --- |
| `docs/screens/SC-15_password-reset.md` / `docs/tests/SC-15_password-reset.md` | 所要時間の判定（T-25）を別 PR が動かしている最中である（依頼の指定） |
| `scripts/check-password-reset-mail.js` / `scripts/check-login-existence-disclosure.js` | 同上。本作業は `--self-test` だけを走らせ、稼働モードは走らせない |
| `docs/operations/operations.md` | 別 PR が編集中（依頼の指定）。新しい Runbook から運用仕様書へのリンクは張るが、逆向きの索引は足さない（リンクの義務は仕様書側の一方向） |
| `scripts/` への新しい測定器の追加 | 下記「設計判断 1」 |

## 読んだもの（一次情報）

基点: `origin/develop` = `33a21412`（#1526 のマージコミット）。`git rev-parse --is-shallow-repository` → `false`。

| 対象 | 読んだ要点 |
| --- | --- |
| #1245 本文とコメント全件 | 受け入れ基準 3 項目・状態表（A〜D）・PR-D の依頼 3 点・2026-09-10 の測り方の失敗（名前の長さの違い） |
| PR #1526（**MERGED** 2026-09-25T18:20Z・`33a2141`） | 札を `T-25` へ・時計を `process.hrtime.bigint()`（整数 ns）へ・段 1 の境界を半格子の整数比較へ・段の内訳を出力へ |
| `deploy/mail-relay/{kustomization.yaml, mail-relay.yaml, reset-gate.yaml, reset-gate.js, reset-floor/*, reset-floor.js}` | Deployment 名・コンテナ名・Service・NetworkPolicy 3 本・`POSTFIX_*` のキュー寿命と再送間隔・門の env（周期 10 s・タイムアウト 10 000 ms・再開 3 回）・門のログ文言・PUT の 4 キー |
| `deploy/local/infra/mailpit.yaml` / `deploy/local/edge-istio-reset-floor/kustomization.yaml` / `scripts/istio-edge-up.sh` / `scripts/k8s-local-up.sh` | 上流（捕捉用 MTA）の在り処・床の経路の名前 `reset-credentials-floor`・観測スタックは opt-in |
| `deploy/prometheus/alerts.yml` / `deploy/local/observability/prometheus.yaml` / `docs/observability/mail-relay-queue-metrics.md` / `deploy/mail-relay/mail-queue-exporter.js` | アラート 3 件の式・`for`・深刻度、計器名、`job="mail-relay"`、Prometheus の Service（`platform-infra/prometheus:9090`） |
| `scripts/check-password-reset-mail.js`（読むだけ） | 稼働モードの流れ（T-20 → T-17 / T-16 → T-10 → T-25）、`EXPECT_GATE_CLOSED`、閉じた状態では申請を打たずに早期終了すること、上流が落ちていると捕捉用 MTA の API が読めず前提で止まること、`module.exports` の面 |
| `scripts/check-login-existence-disclosure.js`（読むだけ） | 対象は realm 宣言の最初の対話利用者（= `admin`）で**上書きの手段が無い**こと、失敗回数を `failureFactor - 1` に抑えること、`attemptLogin` ほかの export |
| `deploy/keycloak/microservices-platform-realm.json` | `bruteForceProtected=true` / `failureFactor=5` / `waitIncrementSeconds=900` / `permanentLockout=false` / `adminEventsEnabled=true` / `smtpServer.host=mail-relay.platform-infra.svc.cluster.local` |
| `.ai-context/adr/IADR-0347` / `IADR-0404` / `IADR-0421` / `IADR-0427` | 状態表の由来、窓 W1 / W1' / W2 の定義、PR-D へ送られた未実測の項目 |
| 計画 ADR-0078 / ADR-0097 / ADR-0103 / ADR-0108（`gh api` で raw 取得） | 門は機械で閉じる（決定 4）・稼働クラスタでの再測定はフォローアップとして残っている（ADR-0097 フォローアップ 2） |
| 計画側の 2026-09-26 の裁定 2 件（**番号は本リポジトリの計画 ADR レンジ 0001..0110 の外なので、ここにも件名・trace にも書かない**） | ①床の器は複数レプリカ・落ちても床を外さない（本番で `RESET_FLOOR=0` を退路にしない）②所要時間の判定を順位和検定（片側 12・反復 3）へ改め、**整数 ns の時計を判定式の変更より先に入れない** |

## 母集合（規則 1〜10）

新しい文書を足すだけの作業なので「是正の追随」は無いが、**PR-D の手順を既に書いている箇所と食い違わないこと**を確かめるため、次で引いた。

| 軸 | 走査 | 結果 |
| --- | --- | --- |
| 1 | `git grep -c "PR-D" -- . ':!CHANGELOG.md'` | 25 ファイル（**本仕様書と新しい手順書を書く前**の `33a21412` での数。両者も語を含むので、書いた後に同じ走査をすると 27 になる —— 25 ＋ 自分の 2 本）。うち #1245 系は `IADR-0347/0404/0421/0427`・1245 系の作業仕様書 4 本・`deploy/mail-relay/{mail-relay.yaml,reset-gate.js,reset-gate.yaml}`・`deploy/prometheus/alerts.yml`・`deploy/local/observability/{prometheus,grafana}.yaml`・`deploy/grafana/provisioning/alerting/slo-alerts.yaml`・`scripts/{README.md,check-password-reset-mail.js,check-login-existence-disclosure.js}` |
| 2 | `git grep -c -E "W1'\|窓 W1\|W2" -- docs deploy scripts` | `deploy/mail-relay/*`・`docs/screens/SC-15_password-reset.md`・`scripts/check-realm-constraints.js`・`docs/tests/SC-06_datasource-management.md`（別事象の「W2」） |
| 3 | `grep -n "^| T-" docs/tests/SC-15_password-reset.md` | 手動で残っている行: T-11 / T-12 / T-13 / T-14 / T-21 / T-24。本 Runbook が実測の手順を与えるのは **T-13（投函失敗の監査ログ）・T-21（閉じた状態の同値）・T-24（上流停止のキュー）** |

**除外と理由**:
- `IADR-0398` と `20260905/0906_issue-1278_*` の仕様書、`src/platform/backend/Services/NotificationService/Tests/**` の「PR-D」は **#1278 の別の段名**であり本件ではない（中身を開いて確認）。
- `docs/tests/SC-06_datasource-management.md` の「W2」は別機能の記号。
- 上の 1245 系の記録はすべて**凍結記録か編集禁止のファイル**であり、本作業は書き換えない。食い違いを見つけた場合は手順書の側に「どちらを正とするか」を書く（下記「設計判断 5」）。

## 設計判断

1. **測定器を `scripts/` へ足さず、手順書の中に使い捨てのコードとして置く。**
   - 既存の検査器は稼働モードで「閉じた状態では申請を打たずに終わる」「上流が落ちていると前提で止まる」「ログイン側は `admin` 固定」
     であり、状態 C1 / D の対の申請とロックアウトは既存の器では測れない。一方、検査器 2 本は編集禁止である。
   - 2026-09-10 の実測も「使い捨ての測定器をリポジトリへ入れない」形で行われた。恒久化するかは結果を見て決める（手順書に書く）。
   - 使い捨てのコードは**検査器の export をそのまま借りる**（`submitResetRequest` と同じ URL・同じ正規化・同じ時計）。
     申請の前半（認可要求 → 画面 → フォームの action）と後半（POST）を分けたのは、**閉じる前に取ったフォームへ閉じた後に POST する**
     ことでしか「閉じた状態の 400」を直接観測できないため（閉じるとログイン画面から導線が消える）。
2. **状態の作り方はリポジトリの宣言から導けるものだけを手順にする。**
   - C1 = 捕捉用 MTA（上流）を 0 レプリカ、C2 = relay を 0 レプリカ、C2' = relay への ingress を許す NetworkPolicy 2 本の
     `podSelector` を一致しない値へ差し替える（削除すると復元に resourceVersion の扱いが要るので差し替えにする）、
     C3 = relay の稼働コンテナで `postconf -e` によって投函を拒ませる（Pod を作り直さない＝C2 の窓を混ぜない）。
   - C2' と C3 は**導出であって実測ではない**（NetworkPolicy の強制の有無と DROP / REJECT の別、`postconf` の拒否コード）。
     手順書は「観測した結果で状態を名付け直す」判定を持たせ、未決事項に並べる。
3. **門を止める（陰性対照の生の 500 / 200 を取る）手順は任意にする。** 陰性対照は PR #1169 に既にあり、門を止めている間は
   利用者名を列挙できる窓が開きっぱなしになる。上限時間と前提（外部から到達できないこと）を付ける。
4. **ログインの稼働モードの検査器は利用者のクラスタで走らせない。** 対象が `admin` 固定で、失敗を 4 回積む。
   ロックアウトは専用の試験利用者（宣言に無い名前）でだけ測る。宣言済みの利用者名を渡すと使い捨てのコードが拒む。
5. **所要時間の判定は #1526 の検査器の出力を正本にしない。** #1526 は着地済み（札・時計）だが、その後の計画の裁定で判定式が
   順位和検定へ改められ、**本リポジトリにはまだ実装されていない**（時計だけが先に入っている）。手順書は**生の標本**
   （片側 12・反復 3）を記録させ、判定は後から当てられる形にする。依存関係を手順書に明記する。
6. **復元は「スナップショットと同じ」を目標にし、戻らないものを列挙する。** realm の `reset-gate.*` 属性は門が開け直すと
   `state=open` で残る（門が平常運転で作る姿）。これを消すには手で `manage-realm` の PUT を打つことになるので手順にしない。
   Keycloak の管理イベント・利用者イベント・Postfix のログは消さない（記録として残る）。

## 受け入れ基準（本作業）

- [ ] `docs/operations/password-reset-relay-state-measurement-runbook.md` が在り、依頼の 1〜5 をすべて含む
- [ ] 各状態の作り方・戻し方が**名前空間とオブジェクト名を明示した**コマンドで書かれ、触る対象の一覧が手順書にある
- [ ] #1526 に依存する部分（札・時計・段の内訳）と、未実装の判定式に依存する部分が区別して書かれている
- [ ] 導けないことが未決事項として列挙されている
- [ ] 可視本文に計画 ID・IADR・仕様書名・修飾付き issue 参照が無い（trace ブロックへ）。本リポジトリの計画 ADR レンジ外の番号は trace ブロックにも無い
- [ ] 使い捨てのコードが構文として正しい（`node --check`）。検査器は `--self-test` のみ実行
- [ ] 下記の検証コマンドがすべて通る

## 検証

`node scripts/check-trace-blocks.js` / `node scripts/check-doc-type-vocabulary.js` / `node scripts/gen-knowledge-graph.js --check` /
`node scripts/check-cross-repo-refs.js` / `node scripts/check-plan-id-qualification.js` / `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` /
`node scripts/check-password-reset-mail.js --self-test` / `node scripts/check-login-existence-disclosure.js --self-test` /
手順書から抜き出した使い捨てコード 3 本の `node --check`。

## 未決事項（手順書にも同じものを置く）

1. 利用者のクラスタが NetworkPolicy を強制するか・強制するなら DROP か REJECT か（C2' が作れるか）。リポジトリは「dev の k3d 既定は強制しない」と書いている。
2. 閉じた状態の 400 を過去の実測がどう観測したか（記録に手段が無い）。
3. `postconf` による拒否（`queue_minfree` → MAIL FROM の 452 / `smtpd_client_restrictions=reject` → RCPT の 554）と、このイメージで `postfix reload` が効くか。
4. 上流が捕捉用 MTA ではない（実テナント）場合の C1 の作り方。本手順は中止条件にしている。
5. `reset-gate.*` 属性を実験前の「無し」へ戻すか。
6. ログインの検査器に対象利用者の上書きを足すか（後続の課題）。
7. 所要時間の判定式（順位和検定）の実装と、先に入った時計の扱い（本リポジトリに追跡 issue が無い）。
8. Keycloak が一時ロック中の利用者にどの文言・ステータス・所要時間を返すか（実測で決める）。
9. 観測スタック・Istio エッジ（床の経路）が利用者のクラスタに在るか。無ければ該当の測定は「未測」と記録する。
10. C2' の 10 秒待ちの経路上に、10 秒未満のプロキシのタイムアウトが無いか（リポジトリのマニフェストには明示のタイムアウトが無い）。
