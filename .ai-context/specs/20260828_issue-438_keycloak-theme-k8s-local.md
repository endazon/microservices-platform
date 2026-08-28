---
title: 作業仕様書 — k8s ローカル起動で Keycloak テーマ（loginTheme/accountTheme）を自動解決する（#438 残作業）
type: spec
status: done
related_ids:
  - SC-13
  - SC-14
  - SC-15
  - SC-16
  - ADR-0026
  - IADR-0261
author: claude
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0026_authentication-ux-and-account-management.md
related_specs:
  - "20260823_issue-438_keycloak-theme-and-smtp.md"
  - "../adr/IADR-0261_keycloak-theme-and-smtp-injection.md"
---

# 仕様書: k8s ローカル起動で Keycloak テーマ（loginTheme/accountTheme）を自動解決する（#438 残作業）

## 0. 起点と、この作業が閉じる残件

[20260823_issue-438_keycloak-theme-and-smtp.md](20260823_issue-438_keycloak-theme-and-smtp.md)（status: done）の
§7 残件 1「**k8s ローカル（`deploy/local/`）で loginTheme/accountTheme を自動解決するには
`scripts/k8s-local-up.sh` の変更が要る**」に対応する follow-up である。同仕様書は当時「`scripts/` 配下は
別担当の領域」として自身の射程外に置いた（[IADR-0261](../adr/IADR-0261_keycloak-theme-and-smtp-injection.md)
「フォローアップ」1 と同一の切り出し）。issue #438 本文・全 2 コメントを再読し、2026-08-21 コメント
（issue 作成者本人の棚卸し実測）が「未実装のまま残っている項目は `smtpServer` と `loginTheme`/`accountTheme`
の 2 点のみ」と明記していることを再確認した。**smtpServer は前作業で対応済み**（Vault → ExternalSecret →
kcadm の手順整備。IADR-0261 決定 2）であり、**本作業はテーマ側の k8s ローカル配線に閉じる**。

`deploy/local/README.md`「既知の制約」に明記されていた事象を裏取りする:

- `deploy/local/infra/keycloak.yaml` の `keycloak-theme-platform` ConfigMap 参照は `optional: true` の
  declarative な受け皿のみが存在し、生成する側が無かった（`scripts/k8s-local-up.sh` はこの ConfigMap を
  一度も `kubectl create configmap` しない）。
- 結果、`bash scripts/k8s-local-up.sh` を素で実行すると Pod は起動する（fail-safe）が、テーマ実体を解決
  できずログイン画面が「テーマが見つからない」500 になる。手動手順（同 README「手動でステップ実行する
  場合」）を都度実行しない限り解消しない。

## 1. 母集合（`.claude/rules/traceability.repo.md`「是正・追随の母集合の取り方」）

「誤りの側」＝ *k8s ローカル環境でテーマ ConfigMap が自動生成されないこと* を対象に、`loginTheme` /
`accountTheme` / `keycloak-theme-platform` / `smtpServer` の 4 語で全文書を走査した（規則 1・2・3・4：
誤りの側から・複数語で・拡張子を絞らず・パスから引く）。

```console
$ grep -rln "loginTheme\|accountTheme\|smtpServer\|deploy/keycloak/themes\|keycloak-theme-platform" \
    --include="*.sh" --include="*.js" --include="*.yaml" --include="*.yml" --include="*.md" .
```

ヒット（`src/ai-stock-trading` submodule を除く）と判定:

| ファイル | 対応 |
| --- | --- |
| `scripts/k8s-local-up.sh` | **対象**。[3/7] ブロックへ ConfigMap 生成を追加した |
| `scripts/k8s-local-up.test.js` | **対象**。生成コマンド・キー名・実行順序を固定するテストを追加した |
| `deploy/local/infra/keycloak.yaml` | **対象**（コメント更新のみ）。「未着手・当面は手動作成が必須」の記述を、自動生成される旨へ更新した。ConfigMap 参照・`optional: true`・items（マウント定義）自体は変更なし |
| `deploy/local/README.md` | **対象**（文書更新のみ）。「既知の制約」から本件の 1 項目を削除し、「手動でステップ実行する場合」のコメントを「自動化済み・手動実行の再現用」へ更新した |
| `deploy/keycloak/themes/platform/**` | 対象外（テーマ実体そのものは前作業で完成済み。`check-realm-constraints.js` のラチェットで固定されており、本作業は変更しない） |
| `deploy/docker-compose.yml` | 対象外。compose はホストマウントで既に有効（IADR-0261「結果」節）。本作業は k8s ローカルのみ |
| `docs/operations/keycloak-smtp-relay-setup-runbook.md` / `deploy/local/vault/eso/externalsecret-keycloak-smtp.yaml` / `deploy/local/vault/eso/bootstrap.sh` | 対象外。smtpServer 系であり本作業の射程外（§0） |
| `scripts/check-realm-constraints.js` | 対象外。realm.json とディスク上のテーマ実体（ファイルシステム）の整合を見るチェッカーで、k8s の ConfigMap 生成とは独立（読んで無関係と確認した） |
| `.ai-context/specs/20260815_issue-578_*.md` / `.ai-context/specs/20260823_issue-438_*.md` | 確定済み記録。凍結対象のため書き換えない（`.claude/rules/traceability.repo.md`「Superseded / Deprecated な ADR を引用するときの書式」の凍結節と同じ扱い。本仕様書冒頭の §0 から一方向でリンクする） |
| `.ai-context/adr/IADR-0261_keycloak-theme-and-smtp-injection.md` | 対象外（本作業の宣言ファイル領域に含まれない）。決定内容・フォローアップ節はこの follow-up の実施によって無効化されるものではない（「scripts/ 配線が入れば機能する」という記述どおりの結果になっただけで、決定自体の変更ではないため新 IADR は不要と判断した） |
| `docs/screens/SC-13〜16` / `docs/tests/SC-13` | smtpServer/テーマへの言及はあるが画面仕様であり、ConfigMap 配線とは無関係。対象外 |
| `.ai-context/specs/20260816_wave8-large-epic-reverification.md` | 過去の棚卸し記録（凍結）。#438 の当時状態を記した経過記録であり書き換え対象外 |

除外に理由が無いものは無い（規則 6）。

## 2. 対象範囲

- 対象: `scripts/k8s-local-up.sh` の `[3/7]` ブロックへの `keycloak-theme-platform` ConfigMap 生成の追加、
  `scripts/k8s-local-up.test.js` への固定テストの追加、`deploy/local/infra/keycloak.yaml` と
  `deploy/local/README.md` の関連コメント・記述の更新（自動化された旨への訂正）。
- 対象外: `smtpServer` の k8s ローカル配線（前作業で対応済み・本件の射程外）。テーマ実体自体の変更
  （CSS・theme.properties）。realm.json（`loginTheme`/`accountTheme`/国際化設定は前作業で投入済みで
  変更不要）。docker-compose 側（既に有効）。ABAC・SC-09・SC-17（issue #438 2026-08-21 コメントにより
  本 issue の残作業から除外済み・20260823 仕様書 §0 を踏襲）。

## 3. 設計

### 3.1 生成位置と冪等性

`scripts/k8s-local-up.sh` の `[3/7]` ブロックには、既に `keycloak-realms` ConfigMap を
`kubectl create configmap ... --dry-run=client -o yaml | kubectl apply -f -` という冪等パターン（サーバー
状態を読まず、意図する最終形を毎回宣言的に適用する）で生成している。テーマ ConfigMap もこの直後に
**同一パターン**で追加した（設計の単一情報源を増やさない。新しい適用方式を持ち込まない）。

```bash
kubectl create configmap keycloak-theme-platform -n "$INFRA_NS" \
  --from-file=login-theme-properties=deploy/keycloak/themes/platform/login/theme.properties \
  --from-file=login-css=deploy/keycloak/themes/platform/login/resources/css/platform.css \
  --from-file=account-theme-properties=deploy/keycloak/themes/platform/account/theme.properties \
  --from-file=account-css=deploy/keycloak/themes/platform/account/resources/css/platform.css \
  --dry-run=client -o yaml | kubectl apply -f -
```

キー名（`login-theme-properties` 等）は `deploy/local/infra/keycloak.yaml` の
`volumes[].configMap.items[].key` と 1:1 で対応させた（既存の宣言を変更せず、生成側をそれに合わせた）。

### 3.2 生成順序

`[3/7]` は `[4/7] apply in-cluster infra`（`kubectl apply -k deploy/local/infra` で Keycloak Deployment を
含む in-cluster リソースを適用し、その後 `rollout status` で待ち合わせる）より**前**に実行される。
テーマ ConfigMap も `[3/7]` 内・`[4/7]` より前に生成されるため、**Keycloak Pod の初回起動時点で ConfigMap
が既に存在し**、（既存の手動手順で必要だった）`rollout restart` を追加で要求せずに済む。これは
`scripts/k8s-local-up.test.js` の新設テスト（§5）で順序を固定した。

### 3.3 変更しないもの

- `deploy/local/infra/keycloak.yaml` の `volumes[].configMap`（`optional: true`・`items` 一覧）は変更しない。
  「未作成でも Pod は起動する」という fail-safe な受け皿の設計はそのまま活きる（本作業は「作る側」を
  足しただけ）。
- realm.json・テーマ実体（`deploy/keycloak/themes/platform/**`）は前作業で完成済みであり変更しない。

## 4. 受け入れ基準

- [x] `scripts/k8s-local-up.sh` を実行すると、`keycloak-theme-platform` ConfigMap が
      `deploy/local/infra/keycloak.yaml` の `items` キーと一致する内容で自動生成される
- [x] ConfigMap 生成は Keycloak Deployment の適用（`[4/7]`）より前に行われる（初回起動でテーマが解決される）
- [x] `bash -n scripts/k8s-local-up.sh` が通る（構文）
- [x] `node scripts/k8s-local-up.test.js` が全件緑（既存 90 件 + 新設 4 件 = 94 件）
- [x] `deploy/local/README.md`「既知の制約」から本件（未着手）の記述が消え、実態と一致する
- [ ] **実クラスタでの見た目確認は環境待ち**（helm/kubectl/k3d が本作業環境に無い。bash stub テストで
      発行コマンド列を固定することを検証手段としており、実際に Keycloak がテーマを解決してログイン画面が
      描画されることの目視確認は、実クラスタが使える環境での確認事項として残す）

## 5. テスト方針

`scripts/k8s-local-up.test.js`（bash stub-on-PATH 方式・IADR-0087 と同型）に 4 件追加した。

1. `keycloak-theme-platform` の `kubectl create configmap` が既定実行（opt-in フラグ不要）で発行され、
   4 つの `--from-file` がすべて実ファイルパスで含まれることを固定する。
2. 生成コマンドが `keycloak-realms` と同型の `--dry-run=client -o yaml` → `kubectl apply -f -` パイプで
   適用されることを固定する（採取ログの隣接 2 行を検査）。
3. `keycloak-realms` の直後・`kubectl apply -k deploy/local/infra` より前という**順序**を固定する
   （§3.2 の設計意図そのものが崩れていないかを見る。行の存在だけでは順序退行を検出できないため独立の
   アサーションにした）。
4. `deploy/local/infra/keycloak.yaml` の `items` キー 4 つと ConfigMap 名が、スクリプト側の生成キーと
   一致することを固定する（マウント定義と生成側が将来ズレて silent に壊れることを防ぐ）。

これらはいずれも副作用ゼロ（k3d/kubectl/helm/docker は記録スタブ）。実クラスタ・helm・kubectl・Docker が
本作業環境に無いため、**bash stub テストが本作業の唯一の検証手段**である（前掲「検証」章と同じ理由）。

## 6. 計画書との差異

- 差異: なし。ADR-0026 の確定要件（認証画面のブランド適用）に対し、k8s ローカル環境という dev 用途の
  実行経路を実装どおりに機能させただけであり、計画・ADR-0026 / IADR-0261 の決定内容を変更していない。

## 7. 未決事項・残件

1. **実クラスタでの目視確認は環境待ち**（§4 最終項目）。helm/kubectl/k3d が使える環境で
   `bash scripts/k8s-local-up.sh` を実行し、ログイン画面に `platform` テーマの CSS が適用されていることを
   確認する作業が残る。
2. `smtpServer` の k8s ローカル配線（`externalsecret-keycloak-smtp.yaml` の apply の組み込み）は本作業の
   射程外のまま（IADR-0261 決定 2・§0 参照）。組織のメールテナントへの移行に伴う残件であり、本 issue の
   2026-08-21 コメントの対象外（テーマ側のみが本 issue の残作業）。
