---
title: 作業仕様書 — BFF のセッション Cookie を 2 レプリカで相互に復号できることを稼働クラスタで測る手順書と検査器（#1534）
type: spec
status: done
related_ids:
  - NFR-07
  - ADR-0032
  - IADR-0251
  - IADR-0273
  - IADR-0427
author: claude
created: 2026-10-04
updated: 2026-10-05
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0032_spa-auth-bff-session.md
related_specs:
  - 20260822_issue-439_bff-session-token-handler.md
  - 20260926_issue-1550_live-script-opt-in.md
  - 20261003_1245_login-checker-username-override.md
issue: "#1534"
---

# 作業仕様書 — BFF のセッション Cookie の複数レプリカ相互復号（稼働クラスタ側）

## 起点

- issue: **#1534**（#439 から分離）。受け入れ基準のうち **稼働環境が要る 2 件**（20 回以上 `/bff/auth/me` が全部 200・両レプリカに振られたこと／Pod を 1 つずつ作り直しても既存の Cookie で 200 が続く）を、
  利用者が稼働クラスタで実行できる形にする。**AI で先行できる 1 件（2 つの `WebApplicationFactory`）は本作業の射程外**（宣言ファイル領域 `Platform.Bff.Tests/**` に触れない）。
- 依頼者の指定: レプリカは **helm の values で**増やす（`kubectl scale` を使わない）。ログインは**試験専用の利用者**で行い、realm 宣言の共有利用者を使わない
  （拒否は `check-login-existence-disclosure.js` の `resolveLoginTarget` を借りる）。Pod ごとに port-forward して同じ Cookie を投げ、陰性対照（改ざんした Cookie）も両 Pod で測る。

## 射程

### やること

1. `scripts/check-bff-multi-replica-session.js`（新規）:
   - `--plan`（稼働クラスタに触れない）: `helm template` を values ＋ 上書き（`services.bff.replicas: 2`）で 2 回描き、**差分が `bff-service` の Deployment の `replicas` 1 行だけ**であることを判定する。
   - `--live`（#1550 の判定器の後ろ）: ①HPA が `bff-service` を所有していれば helm を触らず「既に 2 以上か」だけを見る ②所有していなければ
     `helm get values` で現在の利用者指定の values を退避し、**チャートの描画と稼働マニフェストが一致すること**（チェックアウトの版のずれで無関係な変更を押し込まない）と
     **上書きの差分が 1 行であること**を確かめてから `helm upgrade -f 退避 -f 上書き` する ③試験利用者でエッジ経由のログインを通す ④Pod ごとに port-forward し、
     同じ Cookie で `/bff/auth/me` を各 Pod N 回（既定 10。合計 20 以上）、改ざんした Cookie を各 Pod 1 回 ⑤`--restart` 指定時は `rollout restart` 後の新しい Pod 群で同じ Cookie を再度測る
     ⑥**必ず退避した values で `helm upgrade` し直して 1 レプリカへ戻す**（失敗・中断でも）。
   - 判定（純関数）: Pod 2 つ未満・200 以外・`/me` の利用者名の不一致・標本不足・改ざん Cookie が 401 以外 → 失敗。
2. `scripts/scripts.repo.test.js`: 純関数の試験（陽性・陰性）と、`--plan` / 引数検査の子プロセス試験（ツールを起動しない経路だけ）。
3. `scripts/live-scripts.json` / `scripts/README.md`: live の入口として登録（offline は `--plan`）。
4. `docs/operations/bff-multi-replica-session-runbook.md`（新規）: 前提（共有の鍵リング・共有のセッションストア）、壊れたときの見え方、手順、戻し方、停止条件、記録表。
5. `docs/authz/bff-session-design.md` §6: 手順書への可視リンクを足す（同じ `docs/` 内）。

### やらないこと

- 稼働クラスタでの実行（作業条件で触れない。手順書と検査器を用意し、実測は利用者が行う）。
- 統合テスト（2 つの `WebApplicationFactory`）。issue の AI 側の基準であり、宣言ファイル領域が別。
- IADR の新規採番（依頼者の指定。判断は既存の IADR-0251 決定 5 の検証手段 (a) の稼働側に当たり、新しい設計判断を含まない）。
- issue #1472 の T-40 への文書追記（調べた結果は報告に回す。下の「判断」）。

## 調べた事実（コードと構成から）

| 事実 | 出所 |
| --- | --- |
| 鍵リングは Redis の `bff:dataprotection-keys`、アプリ名 `microservices-platform-bff` | `src/platform/backend/Bff/Platform.Bff/Foundation/Session/BffSessionExtensions.cs` |
| セッション本体も Redis（`RedisTicketStore`）。Cookie はセッションキーを保護しただけのもの | 同上・`docs/authz/bff-session-design.md` §0 |
| Redis 接続の既定は `redis:6379`（配備は既定のまま。ExternalName で `platform-infra` の `redis` へ） | `BffSessionOptions.cs` / `deploy/local/aliases/microservices-platform-externalnames.yaml` |
| ローカルの Redis は永続化なし（`redis:7-alpine` を素で起動） | `deploy/local/infra/redis.yaml` |
| レプリカの鍵は `services.bff.replicas`（既定 1）。ただし `scaling.enabled` かつ `scaling.services` に `bff` があれば Deployment は `replicas` を持たず HPA（minReplicas 2）が所有する | `deploy/helm/microservices-platform/templates/deployment.yaml` / `values.yaml` |
| 本番像の values は `scaling.enabled: true`・`bff` を含む → **本番像の BFF は既に 2 以上**。ローカルは `scaling.enabled: false` | `values.yaml` / `deploy/local/values-local.yaml` |
| `extraEnv` は Helm のリスト置換で丸ごと消える（#1389）。上書きは `extraEnvAppend` に書く | `templates/deployment.yaml` の注記 |
| `/bff/auth/me` はセッション Cookie だけで認証し、未認証は 401 | `Foundation/Endpoints/AuthBffEndpoints.cs` |
| Cookie 名 `__Host-msp-session`（既定のまま） | `BffSessionOptions.cs` |
| ログ水準は `Microsoft.AspNetCore: Warning`（認証失敗・復号失敗の情報ログは既定では出ない） | `Platform.Bff/appsettings.json` |

## helm template の差分（実測。helm v3.16.4）

| 描画 | 結果 |
| --- | --- |
| `-f deploy/local/values-local.yaml` 対 同 ＋ `services.bff.replicas: 2` のファイル | 差分 1 行（`bff-service` の Deployment の `replicas: 1` → `2`） |
| 上のファイル 対 `--set services.bff.replicas=2` | 同一（sha256 一致） |
| 上書きに `extraEnv` を 1 件書いた場合 | 既定の env 36 件（72 行。`Introspection__*` / `Services__*` 等）が消え、1 件だけ残る（Deployment が 233 → 163 行）→ **使ってはならない形** |
| 本番像の values（`scaling.enabled: true`）に `--set services.bff.replicas=2` | 差分なし（HPA が所有するため `replicas` を描かない） |

## 判断

- **検査器を `verify-oidc-edge-flow.sh` に足さず別に置く。** あちらは既定で realm 宣言の `developer` を使い、レプリカを変えない。
  本検査器は配備を変える（helm upgrade）ので、副作用の重さが違う入口を同じスクリプトに混ぜない。
- **ログインは Node で書き直す（bash の `acquire_session` を呼ばない）。** bash の関数はスクリプトの外から呼べない。
  画面の解析（`lib/keycloak-login-form.js`）・TOTP（`lib/totp.js`）・cookie と HTTP 器（`check-password-reset-mail.js`）・利用者名の拒否（`resolveLoginTarget`）は借りる。
  TOTP の状態ファイルの名前は `verify-oidc-edge-flow.sh` と同じ規則にする（同じ試験利用者で両方を走らせられる）。
- **上書きの既定は無い。** 利用者名とパスワードが無ければ止める（`resolveLoginTarget` の既定経路＝宣言の利用者へ倒さない）。パスワードは環境変数だけで受ける（引数だとプロセス一覧と履歴に残る）。
- **戻しは `kubectl scale` ではなく退避した values での `helm upgrade`。** 手で scale すると helm の持つ値と稼働が食い違う。
- **T-40 は手順を足さない。** テスト仕様書は観点と期待値の表であり、手順の置き場ではない（手順は `docs/operations/` の Runbook が持つ）。
  T-40 の手順を書くなら Runbook の新設か `secret-rotation-runbook.md` 等への追記で、どれが正か明らかでないため報告に留める。

## 受け入れ基準（本作業）

- [x] `--plan` が values-local で「1 行だけ」を緑にし、`extraEnv` を含む上書きを赤にする（純関数の試験で固定）
- [x] 指定なしでは exit 3 でツールを起動しない（#1550 節が live-scripts.json 経由で試す）
- [x] 利用者名が無い・宣言の利用者・パスワードが無い → exit 2（子プロセス試験）
- [x] 判定関数の陽性・陰性（Pod 1 つ・401 混在・利用者名の不一致・改ざんが 200・標本不足）
- [x] 手順書に前提・壊れたときの見え方・戻し方・停止条件・記録表がある

## 検証

- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` ほか、依頼の検査器一式（PR 本文／報告に結果を貼る）。
- 自己変異（5 件以上）は報告に記録する。

## 残余リスク

- 稼働クラスタで 1 度も走らせていない。期待値はコードと構成から導いたもの。
- 本番像（HPA 所有）では本検査器は helm を触らないが、エッジ経由のログインと port-forward の前提（`kubectl` の権限・エッジ CA）は本番像で確かめていない。
- Helm 4 で入れたリリースに対する `helm get values` / `helm upgrade` の互換は手元の Helm 3 でしか確かめていない（描画の差分だけ）。

## ［2026-10-05 追記 / #1534］独立監査（条件付き GO）への対応

起点 ID を **NFR-07**（スケーラビリティ。HPA による水平スケール）へ寄せた（コードのコメント・手順書の trace ブロック・本仕様書の frontmatter。ブランチ名は据え置き）。

🔴 **本検査器と手順書は利用者が手で走らせる訓練（drill）であって、CI のゲートではない。** IADR-0251 決定 5 の検証手段
（(a) 2 レプリカ以上の統合テスト／(b) 永続化先の設定を構成の側から固定する検査）の代わりにはならない。合格は「その版・そのクラスタで 1 度測れた」ことだけを示す。

| 指摘 | 対応 |
| --- | --- |
| 🔴-1 `--live` の経路が未試験 | helm と kubectl のスタブを PATH の先頭に置き、呼び出しの記録を見る子プロセス試験を足した: 版のずれ → exit 2・upgrade 0 回／upgrade 後の rollout 失敗 → 退避した values だけ（上書きなし）で upgrade し直し、その rollout を待つ／既存の `services.bff.replicas` が 2・3・0 → exit 2・upgrade なし／HPA 所有 → helm を 1 度も呼ばない／リリースが `deployed` でない → exit 2／context が読めない → 何も呼ばずに exit 2／rollout 中の SIGTERM → シグナルで落ちず戻しは 1 回。シグナルの処理（SIGINT / SIGTERM / SIGHUP・port-forward の子を閉じる・戻せたら 130・戻せなければ 1）は `installSignalRestore` を偽の `process` で単体試験した |
| 🟡-1 手で戻す案内の `helm rollback`（直前の版）が誤り | upgrade の前に `helm status msp -o json` の `.version`（N）を読み、`.info.status` が `deployed` でなければ何も変えずに exit 2。戻しに失敗したら `helm rollback msp <N> -n …` を出す。戻し自体は従来どおり「退避した values で upgrade → rollout を待つ」（版のずれが無いことを upgrade 前に確かめてあり、`--wait` の意味が helm のメジャー版で違う rollback より単純）。手順書 §6 も同じ形にした |
| 🟡-2 どのクラスタか | 何かを変える前に `kubectl config current-context` を出す（読めなければ止める）。手順書 §4 と §8 の記録表に context 欄を足した |
| 🟡-3 HPA 所有の配備でも `--restart` は Pod を作り直す | 手順書 §5 に明記（承認なしに走らせない） |
| 🟡-4 利用者名の大小 | 小文字へ正規化して突き合わせる |
| 🟡-5 改ざんの 0 / 500 | 不合格になることを試験で固定した |
| 🟢-1 名前の無い 200 | 200 の件数と利用者名の件数が一致しなければ不合格 |
| 🟢-2 中断 | SIGHUP も受ける。port-forward の子を閉じる。戻しに失敗したら 1 |
| 🟢-3 `[前提]` の終了コード | `[前提]` の失敗（upgrade・rollout の未完了、Ready の Pod 不足、ログイン失敗、Cookie 未発行を含む）はすべて 2 に揃えた。戻しの失敗は優先して 1。鍵リングの件数は前提ではなく測定なので `[鍵リング]` へ改名した |
| 🟢-4 文書集合の変化・`--plan` と `--username` の併用 | 試験を足した |
| 🟢-6 / 🟢-7 手順書 | `--restart` は作り直しの後を測る（最中は測らない）・Pod へ直接つなぐのでエッジと Service の振り分けは通らない・コールバックで止まるのは症状そのものであり得る、を明記した |

