---
title: 作業仕様書 — Wiki.js の初期セットアップと同期 API キーを冪等な runtime bootstrap で自動化する（#1108）
type: spec
status: done
related_ids:
  - FR-06
  - FR-13
  - FR-19
  - UC-07
  - SC-04
  - ADR-0011
  - ADR-0046
  - IADR-0020
  - IADR-0021
  - IADR-0095
  - IADR-0097
  - IADR-0248
  - IADR-0327
author: claude
created: 2026-08-31
updated: 2026-08-31
issue: "#1108"
---

# 作業仕様書 — Wiki.js の初期セットアップ自動化（#1108）

## 1. 目的と射程

稼働 dev クラスタの Wiki.js が **初期セットアップ未完了のまま `2/2 Running`** であり、
`/graphql` が 404 を返すため **Wiki 同期（FR-13 / UC-07 / SC-04）が配備上まったく成立していない**。
併せて `wikijs-sync` の `apiKey` が空で、セットアップが済んでも GraphQL は認証できない。

**射程**: 配備側（`deploy/` ＋ `scripts/k8s-local-up.sh` ＋ 検知）に閉じる。
**`WikiService` のコード（ゲートウェイ・ABAC・存在秘匿・削除伝播）は変更しない**（#1108 の制約）。
**Wiki.js の OIDC ストラテジ seed（#397 の残件）は射程外**とする —— 別の runtime 設定であり、
本 bootstrap が入れる `settings.host`（Site URL）だけが接点である（§7）。

## 2. 実測（着手前・2026-08-31・Rancher Desktop 内蔵 k3s v1.35.4+k3s1・develop c45533bc）

| 測ったこと | 出力 |
| --- | --- |
| `POST http://127.0.0.1:3000/graphql`（wiki-js pod 内 loopback） | `http=404` |
| `GET /healthz` | `http=200`（**setup モードの catch-all `app.get('*')` が返している**） |
| wiki-js ログ | `DB Configuration is empty or incomplete. Switching to Setup mode...` |
| `wikijs` DB の `settings` / `users` / `pages` / `authentication` | すべて **0 行**（スキーマだけが在る） |
| wiki-js の env | `DB_HOST=postgres` `DB_NAME=wikijs`（**DB は platform-infra の共有 Postgres**） |
| wiki-js の volume | `wiki-js-data` PVC → `/wiki/data` のみ（**DB はここに載っていない**） |
| postgres Deployment の volume | `{"emptyDir":{},"name":"data"}` ← **PVC ではない** |
| `platform-infra` の PVC | `postgres-data` は Bound だが **Deployment から参照されていない**（`PERSIST=1` で立っていない） |
| `/var/lib/postgresql/data/PG_VERSION` の mtime | `Aug 30 04:20`（= postgres pod の作成時刻。DB はその時に作り直された） |
| `wikijs-sync` Secret | `apiKey` の長さ **0**。`ExternalSecret` が owner・`SecretSyncedError` が 34 日継続 |
| Vault の `secret/msp/wikijs-sync` | `No value found`（dev Vault はインメモリ・17 回再起動している） |

**結論**: Wiki.js の DB は **共有 Postgres のコンテナ層（emptyDir）** に載っている。
postgres Pod を作り直すたびに **`wikijs` DB ごと消え、Wiki.js は setup モードへ戻る。**

## 3. #397 の先例をどう扱うか

`#397`（Wiki.js OIDC ストラテジの DB seed 自動化）は **`state_reason: duplicate` で 2026-08-02 に
close されており、実装は入っていない**（timeline に PR も commit も無い。#454 の全面再実装へ畳まれた）。
`deploy/local/wiki-oidc/README.md` には**手動 SQL 手順のまま**残っている。

したがって #397 は「採った方式」ではなく **「自動化すると決めたのに入らなかった前例」** である。
本 issue は同じ穴（runtime 設定が manifest に無く、DB 再作成で黙って消える）の再来にあたる。

## 4. #1088 と同じ PR で解くべきか —— 解かない

- 稼働クラスタが `PERSIST=1` で立っていないのは事実で、`postgres` の emptyDir が Wiki.js の DB を
  消している。**しかし `PERSIST=1` の永続化オーバーレイは既に実装済み**（`deploy/local/infra-persistence/`）
  であり、#1088 の射程は **Keycloak realm の import 戦略（`IGNORE_EXISTING`）と乖離検知**である。
- **永続化しても本件は解けない。** 新規クラスタの `wikijs` DB は常に空であり、**空の DB は必ず
  setup モードになる**（§2 の実測がその状態そのものである）。seed が要ることは永続化と独立している。
- 逆に **seed だけでも本件は解ける** —— 冪等な bootstrap を `k8s-local-up.sh` の経路に置けば、
  DB が消えても再実行で復旧する。
- よって **2 つは互いを無意味にしない。別 PR とする。** 依存関係は「#1088 が入ると本 bootstrap の
  再実行頻度が下がる」だけである。

## 5. 決定（IADR-0327 に残す）

1. **方式は #1108 の選択肢 1（セットアップ API を叩いて finalize する）** を採る。
   DB スナップショット（選択肢 2）は Wiki.js の版に固定され、`tag: "2.5"`（浮動 minor）と両立しない。
2. **実体は `deploy/local/wikijs-setup/bootstrap.sh`**（`deploy/local/vault/eso/bootstrap.sh` と同型の
   「runtime 設定の冪等な再適用」）。`scripts/k8s-local-up.sh` が **既定で（opt-in ではなく）** 呼ぶ。
   - opt-in にしない理由: **既定の経路が Wiki.js を使えない状態で残ることこそが #1108 である。**
   - 失敗しても `up` は止めない（best-effort・`|| echo WARN`）。**fail-closed の役割は検知側に置く。**
3. **HTTP は wiki-js コンテナ内の loopback（`kubectl exec ... curl http://127.0.0.1:3000`）で叩く。**
   STRICT mTLS（#1109）でも port-forward でもなく loopback なので、エッジ構成に依存しない。
4. **管理者パスワードはコミットしない。** 既存 Secret `wikijs-admin` があれば再利用し、
   無ければ `WIKIJS_ADMIN_PASSWORD` を使い、それも無ければ**乱数を生成**して Secret へ保存する。
   dev 既定文字列（`*-dev-secret-change-me`）を置かない —— これはエッジに露出する実ログイン口である。
5. **API キーは Wiki.js に発行させ、`wikijs-sync` Secret へ書き戻す。** Vault が居れば
   `secret/msp/wikijs-sync` にも書く（ESO が復旧したときに空で上書きされないため）。
   **キーはリポジトリに現れない。**
6. **検知は `scripts/check-stack-ready.js` に G7 として置く**（fail-closed）。§6 参照。

## 6. 「検査器を足すか」の判断（`.claude/rules` の 2 回ルール）

同型の事故＝**「manifest に無い runtime 状態が黙って欠落したまま、Pod は Ready を返す」**。

| # | issue | 事故 |
| --- | --- | --- |
| 1 | #397（2026-07・close: duplicate） | Wiki.js の OIDC ストラテジが DB 保持で、DB 再作成のたびに消える。**自動化すると決めたが入らなかった** |
| 2 | #1088（2026-08-30） | Keycloak realm が古い ConfigMap のまま焼き付き、**検知手段が無い** |
| 3 | #1108（本件） | Wiki.js が setup モードのまま `2/2 Running`。`/healthz` が setup ページに 200 を返す |

**2 回目を超えている（3 件目）。よって検査器を足す。**
G7 は「wiki-js の Deployment が在るのに `/graphql` が 404」を fail-closed で落とす。
Deployment 自体が無い場合（`wikijs.enabled=false`）は notice で飛ばす（G5 の「エッジは 2 通り」と同じ作法）。

## 7. 母集合（自分で引いた・除外理由つき）

**軸 1**（誤りの側の識別子で引く・追跡下の全ファイル、パス除外のみ）:
`git grep -ln -e wikijs-sync -e WIKIJS_SYNC_APIKEY -e WikiJs__ApiKey -- . ':!src/ai-stock-trading'` → 25 件
**軸 2**（対象そのもの）: `git grep -ln -i -e wiki-js -e 'wiki\.js' -e wikijs -- deploy scripts docs .github .claude *.md` → 60 件
**軸 3**（`wikijs-sync` の全該当行を目視）。

追随させる（本 PR で触る）:

| ファイル | 理由 |
| --- | --- |
| `scripts/k8s-local-up.sh` | bootstrap の呼び出し。`wikijs-sync` の再 apply が**発行済みキーを空で潰す**のを止める |
| `scripts/k8s-local-up.test.js` | 上の 2 点を回帰で固定する |
| `scripts/check-stack-ready.js` | G7（setup モード検知）＋ self-test |
| `deploy/local/wikijs-setup/{bootstrap.sh,README.md}` | 新規 |
| `deploy/local/README.md` | env 表の `WIKIJS_SYNC_APIKEY`「既定 空」が**もう真ではない**（bootstrap が発行する） |
| `deploy/local/wiki-oidc/README.md` | 「管理UI を開く」前提が setup 完了に依存する。bootstrap への導線を足す |
| `docs/operations/operations.md` | `wikijs-sync` を手で作る手順が残っている |
| `.ai-context/adr/IADR-0327_*.md` ＋ `.ai-context/adr/README.md` | 決定の記録 |

**除外した（理由）**:

| ファイル群 | 除外理由 |
| --- | --- |
| `.ai-context/specs/*`・確定済み `.ai-context/adr/*`（IADR-0021/0097/0103 等） | **凍結記録**（`traceability.repo.md`）。本文を後から書き換えない |
| `CHANGELOG.md` | 自動生成（`scripts/` ＋ `changelog.yml`）。手で書き足さない |
| `deploy/bootstrap/**`・`deploy/helm/**/values.yaml`・`deploy/docker-compose.yml` | **本番像**。本 bootstrap は経路B（ローカル k8s）専用であり、本番の Wiki.js セットアップは人間の運用行為である |
| `docs/security/security.md`（`wikijs-sync` の記述） | 「API キーは Secret 経由で注入しコミットしない」は**本 PR 後も正しい**（発行元が変わるだけで、供給経路は同一） |
| `docs/functional/*`・`docs/screens/*`・`docs/tests/*`・`docs/data/wiki-page-sync.md` | 機能仕様であり、配備の初期化手順を述べていない（誤りにならない） |
| `deploy/local/vault/eso/*` | `wikijs-sync` の ExternalSecret 定義は無改変で正しい。bootstrap は Vault にも書くので**新しい ExternalSecret を作らない**（同じパターンを 2 つ作らない・#458） |
| `src/knowledge/backend/**` | #1108 の制約により変更しない |

## 8. 受け入れ基準 → 検証の写像

| # | 基準 | 測り方 |
| --- | --- | --- |
| 1 | setup モードを抜けている | `POST /graphql` が 404 でない ＋ ログが setup を出さない |
| 2 | `apiKey` が空でない | `kubectl get secret wikijs-sync` の長さ > 0（**実値は貼らない**） |
| 3 | 陽性対照 | 文書を 1 件作る → wiki-service が dead-letter せず、Wiki.js に page が現れる |
| 4 | 陰性対照 | `doc_scope=private-note` の文書 → Wiki.js に**現れない**（ADR-0046 D-01） |
| 5 | 削除 | 文書を削除 → Wiki.js から page が消える |
| 6 | Pod 再作成に耐える | `rollout restart deploy/wiki-js` 後に 1 を再測 |
| 7 | 検知 | `check-stack-ready.js` が setup モードを落とす（陽性対照つき） |
| 8 | 既存 dead-letter | `wolverine-dead-letter-queue` の扱いを PR 本文で述べる |

## 9. 測れないもの

- `check-deploy-manifests.js` の `hasTool` は `command -v`（POSIX）を使うため **Windows では常に不在判定**。
  → 「無いので測れなかった」と報告する。
- Testcontainers 依存の統合テストは Docker daemon（containerd のため不在）で **skip**。件数を内訳に出す。

## 10. ［2026-08-31 追記 / #1108］着手後に判明した 2 段目の欠陥

**setup モードを直しただけでは同期は成立しなかった。**

`POST /finalize` を通して `/graphql` が 200 を返すようになった直後、陽性対照の文書は
**別の理由で落ちた**:

```
WikiJsSyncException: Wiki.js pages.create failed for 'doc/…' (code=1):
  insert or update on table "pages" violates foreign key constraint "pages_localecode_foreign"
```

`server/setup.js` は `locales` を `code != 'x'` で全削除してから **`en` を 1 行だけ**入れる。
一方 WikiService は `WikiJsGraphQlClient.Locale = "ja"` で push する。
🔴 **Wiki.js は GraphQL 200 を返すため、失敗は WikiService のエラーキューにしか出ない。**

対応: bootstrap の段 3 で `locales` へ行を冪等に入れる（値は実装から走査）。
検知: `check-stack-ready.js` の G7(c) が `isInstalled` を見る。IADR-0327 決定 6。

## 11. ［2026-08-31 追記 / #1108］実測の結果

| # | 基準 | 結果 |
| --- | --- | --- |
| 1 | setup モードを抜けた | `POST /graphql` → `http=200`（着手前は 404）。ログの `Switching to Setup mode` が消えた |
| 2 | `apiKey` が空でない | 長さ 502（実値は記録しない） |
| 3 | 陽性対照 | `doc/9654829c-…` が Wiki.js に現れ、ログは `Synced document …`。dead-letter 無し |
| 4 | 陰性対照 | `doc/1a1fbb76-…`（private-note）は現れず、ログは `Skipped Wiki.js sync for private-note document …(ADR-0046 D-01)` |
| 5 | 削除 | `DELETE /documents/<id>` → 30 秒後に Wiki.js から page が消えた |
| 6 | Pod 再作成 | `rollout restart deploy/wiki-js deploy/wiki-service` 後も 1〜4 が同じ結果 |
| 7 | 検知 | G7 self-test 9 件 ＋ 稼働クラスタでの陽性対照（apiKey を空にすると落ち、戻すと通る） |

**測るために一時的にクラスタへ入れ、戻したもの**: `abac-seeder` client の optional client scope `profile`
（`/private-notes` が `Identity.Name` を要求するため。realm ファイルは変更していない。実測後に削除して空を確認した）。

**観測したが本 issue の対象外**: `wiki-service.u0e-shared-document-updated` が `consumers=0` のまま滞留する。
