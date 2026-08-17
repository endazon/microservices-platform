---
title: IADR-0220 経路B の admin(50000) entrypoint を TLS 終端にし、web(80) は https へ恒久リダイレクトする（証明書は namespace ごとに置く）
type: impl-adr
status: Accepted
related_ids:
  - NFR-11
  - ADR-0047
  - ADR-0023
  - IADR-0091
  - IADR-0092
  - IADR-0093
  - IADR-0094
  - IADR-0095
  - IADR-0103
  - IADR-0206
author: claude
created: 2026-08-17
updated: 2026-08-17
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0047_edge-cert-scope-local-route.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0023_edge-cert-automation-cert-manager-letsencrypt.md"
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
---

# IADR-0220: 経路B の admin(50000) を TLS 終端にし、web(80) は https へ恒久リダイレクトする

- 状態: Accepted
- 日付: 2026-08-17
- 決定者: claude（実装）

## 起点・関連

- Issue: **#841**（#834 から切り出した**実体側**）。仕様書: [`../specs/20260817_issue-841_admin-entrypoint-https.md`](../specs/20260817_issue-841_admin-entrypoint-https.md)。
- 計画: **`NFR-11`**（全経路の HTTPS 化。**適用範囲は環境を問わない** —— 利用者裁定 2026-08-16・裁定依頼 planning#383）、
  **`ADR-0047`**（エッジ TLS 証明書の運用は経路B にも及ぶ。`*.localhost` では selfsigned CA を許容）、
  `ADR-0023`（自動化・配布層は cert-manager）。
- 実装 ADR: [[IADR-0206]]（経路B のエッジ TLS 終端＝ cert-manager の selfsigned→CA・`edge-tls`）、
  [[IADR-0091]]（経路B のエッジは Traefik・admin:50000 へホスト名ベース集約）、
  [[IADR-0103]]（経路B の SSO の恒久化。**`admin` ユーザーの ADR であり entrypoint の ADR ではない**。
  本 ADR は同 ADR を Supersede しない —— §関連 を参照）。

## コンテキストと課題

**証明書基盤は既に在る。残っていたのは適用範囲である。**

[[IADR-0206]] は 443（websecure）に載る `platform-frontend-edge` 1 件だけへ `spec.tls` を付け、
**admin(50000) に載る管理ツール 7 件（grafana / headlamp / vault / qdrant / minio / wiki / argocd）は
平文 http のまま**にした。その判断の根拠は同 ADR 決定 4 の

> 経路B は `LOCALEDGE=1` が **loopback（`127.0.0.1`）へ bind する閉域のローカル開発環境**であり、
> `NFR-11` が言う「外部から到達し得る」に当たらない、という整理で**適用外**とする

という読みである。**この読みは利用者裁定 2026-08-16 で明示的に否定された。**
計画の非機能要件表 `NFR-11` は「**★ 適用範囲は環境を問わない**……**ローカル検証環境（経路B）も適用内である。**
実装側は『loopback へ bind する閉域であり「外部から到達し得る」に当たらない』と読んで適用外として扱っていたが、
**その読みは採らない**」と書き換わり、同日 `ADR-0047` が証明書の発行方式（selfsigned CA の許容）を確定した。

したがって決めるべきは次の 3 点である。

1. admin(50000) をどう TLS 終端にするか（chart の values か、Traefik の引数か）
2. `spec.tls.secretName` は同 namespace の Secret しか参照できない。**管理ツールは 3 つの namespace に散っている**
3. `NFR-11` の「**平文 HTTP を残さない**」は 80 番をどう扱えと言っているか

**実測（走査基準 `5ed54b02`・`git grep -I -o`）**: `http://…localhost:50000` は **104 件 / 31 ファイル**。
うち **65 件 / 16 ファイル**が live な設定・コード・手順書である（残りは確定済みの `docs/specs/` 27 件と
過去の決定の記録 `docs/adr/` 12 件。母集合の引き方と除外理由は作業仕様書 §2）。

## 検討した選択肢

| # | 案 | admin の TLS 化 | chart 版依存 | 採否 |
| --- | --- | --- | --- | --- |
| 1 | Helm values（`ports.admin.tls.enabled` / `ports.web.redirectTo`） | 可 | **あり** —— 同ファイルは既に `expose` のスキーマ差で注意書きを持つ | 却下 |
| 2 | **`additionalArguments` で Traefik の引数を直接渡す** | 可 | **無し**（引数はコマンドラインへそのまま流れる） | **採用** |
| 3 | admin 用に別の `secretName` を作る | 可 | — | 却下（`ADR-0023` / `ADR-0047` の「名前の安定」に反する） |
| 4 | 証明書を 1 namespace だけに置き、Secret を複製する | 不可に近い | — | 却下（複製の更新が cert-manager の外に出る＝失効に追随しない） |
| 5 | 80 番を閉じる（`web` entrypoint を落とす） | — | — | 却下（**リダイレクトを返す口が無くなる**。平文で来た利用者が沈黙する） |
| 6 | 80 番を平文のまま残す | — | — | 却下（`NFR-11`「平文 HTTP を残さない」に反する。**これが従来の姿である**） |

## 決定

### 1. `admin`(50000) を TLS 終端にする

`deploy/local/edge/traefik-entrypoint.yaml` の `HelmChartConfig` に `additionalArguments` を足し、
**`--entryPoints.admin.http.tls=true`** を渡す。証明書は SNI に応じて Ingress の `spec.tls` から選ばれる。

**values ではなく引数にするのは、values のスキーマが chart バージョンで変わるからである。**
同ファイルは既に `expose` について「新しめ(chart v25+/Traefik v3) はマップ形、古い chart では真偽値」と
注意している。同じ罠を 2 つ増やさない。

### 2. 管理系 Ingress 4 ファイル（7 ルータ）へ `spec.tls` を足す

`secretName` は **[[IADR-0206]] が安定させた `edge-tls` をそのまま使う**（`ADR-0047` 決定 2 の設計要件
「名前の安定」。**新しい名前を作らない**）。`hosts` は葉証明書の `dnsNames` と一致させるため
**`"*.localhost"`** と書く。

### 3. 葉証明書は **namespace ごと**に置き、Secret 名は変えない

`spec.tls.secretName` は**同じ namespace の Secret しか参照できない**。管理ツールは 3 つの
namespace に散っているため、葉証明書もそれぞれに要る。

| namespace | ルータ | 置き場 |
| --- | --- | --- |
| `microservices-platform` | minio / wiki（＋ 443 の frontend） | `tls/edge-certificate.yaml`（既存） |
| `platform-infra` | grafana / headlamp / vault / qdrant | `tls/edge-certificate.yaml`（本 ADR が追加） |
| `argocd` | argocd | `tls/argocd-certificate.yaml`（本 ADR が追加・**kustomization に含めない**） |

**3 件とも `issuerRef` は同じ `local-edge-ca`（`ClusterIssuer`）を指し、`secretName` は `edge-tls` である。**
CA 固有設定は `ClusterIssuer` に閉じたままであり、`ADR-0047` 決定 2 の設計要件 3 点は崩れない ——
Let's Encrypt / Vault PKI への差し替えは、いまも `issuerRef` の差し替えだけで済む。

**`argocd` だけ別ファイルにするのは、その namespace が `ARGOCD=1` の別 opt-in でのみ作られるからである。**
`tls/kustomization.yaml` へ含めると、ArgoCD を使わない環境で **tls overlay 全体が落ちる**
（[[IADR-0206]] 決定 5 が親 kustomization について述べたのと同じ形の fail-safe）。
`k8s-local-up.sh` が `argocd-ingress.yaml` と同じく「ns 存在時のみ」apply する。

### 4. `web`(80) は `websecure`(443) へ**恒久リダイレクト**する

`--entryPoints.web.http.redirections.entryPoint.{to=websecure,scheme=https,permanent=true}` を渡す。
**80 番を閉じない**のは、閉じるとリダイレクトを返す口が無くなり、平文で来た利用者に何も返せなくなるためである。

**これは [[IADR-0206]] 決定 4 の 3 命題のうち 2 つ（P2「admin:50000 の 7 件へは `spec.tls` を足さない」・
P3「`http` 経路は残す・恒久リダイレクトは足さない」）を Supersede する。**
**P1（443 の `platform-frontend-edge` へ `edge-tls` を追加する＝ TLS 終端の形）は引き続き有効**であり、
本 ADR もその形をそのまま踏襲している。
同決定がリダイレクトを避けた理由は「`http://*.localhost:50000` を前提にした既存 docs と realm の
redirectUris が全部一段回り道になり、7 クライアントの再設定を巻き込む」ことだった。
**本 ADR はその 7 クライアントの再設定を実際に行う**（realm・`values-local.yaml`・`grafana.yaml`・
`argocd-cm-patch.yaml`・`vault/oidc/bootstrap.sh`）ので、避ける理由が無くなっている。

**条文の側（[[IADR-0206]] の「`NFR-11` 適用外」という枠付けの撤回）は #834 が持ち、`848111cd` で
develop へマージされた。** それを受けて、**同 ADR 決定 4 へ本 ADR による部分 Supersede の注記**
（`［2026-08-17 追記 / #841］`）を入れてある —— **Supersede される側にも後継への導線を置く**ためである
（`traceability.repo.md`「旧 ID を残し、後継を併記する」。先例は [[IADR-0117]]）。

### 5. ArgoCD の `server.insecure=true` は据え置く

TLS を終端するのは Traefik であり、そこから `argocd-server` への in-cluster 転送は平文である。
`insecure` を外すと argocd-server 自身が http→https リダイレクトを返し、**エッジ経由が二重終端で壊れる**。
[[IADR-0092]] が置いた**設定は変えない。変えたのは前提の説明だけ**である ——
「edge が平文だから」から「エッジで終端し in-cluster は平文だから」へ改めた。
**改めた先は 2 箇所**: `deploy/local/argocd/oidc/argocd-cmdparams-patch.yaml` のコメントと、
[[IADR-0092]] 本体の `［2026-08-17 追記 / #841］`（**本文は書き換えず追記で読み替えを示す**形）。

> **★ 同型の前提崩れを持つ live な ADR は [[IADR-0092]] だけではなかった。** 誤りの側の語で
> 追跡下を全走査した結果（規則 9・7 軸）、**`Accepted` の live な ADR 4 件**が
> 「エッジ（`admin:50000`）は平文 http」を前提にしたままだった ——
> [[IADR-0092]]（ArgoCD）・[[IADR-0093]]（MinIO）・[[IADR-0094]]（Vault）・[[IADR-0095]]（Wiki.js）。
> **4 件すべてへ同じ形の追記を入れた**（本文は書き換えない・`status` は `Accepted` のまま）。
> **この「4 件」は同じ形の追記を新規に入れたものの数であり、同型の前提崩れを持つ live な ADR の全数ではない**
> —— [[IADR-0091]]（決定 3 の追記）と [[IADR-0206]]（決定 4 の追記）は**別扱いで是正済みであり、計 6 件**である。
> 走査と除外理由は作業仕様書 §2.7 が持つ（**同 §が 6 件すべてを列挙している**）。

## 理由

- **計画が絶対的な正である。** `NFR-11` は「平文 HTTP を残さない」「適用範囲は環境を問わない」と書き、
  `ADR-0047` は経路B での selfsigned CA を許容した。**実装側に選択の余地は無い。**
- **設計要件 3 点を守れば、本番へ寄せるときの差分は `issuerRef` の差し替えに収まる**（`ADR-0047` §理由）。
  namespace を増やしても `secretName` を `edge-tls` で揃えているため、消費側は CA を知らないままである。
- **`additionalArguments` は「TLS になったつもり」を作らない。** 引数はそのまま Traefik へ渡るため、
  chart 版の差で黙って無効化されることがない。**静的検査がこの文字列を直接見る。**

## 結果

- **良い影響**: 経路B の**エッジ**（`web`:80 / `websecure`:443 / `admin`:50000 の 3 entrypoint）が
  **すべて https になり**、そこに載るエンドポイントについて `NFR-11` の「平文 HTTP を残さない」を経路B でも満たす。
  **`NFR-11` の適用範囲について実装と計画が逆を向いた状態が解消する。**
  **ただし「経路B の全エンドポイントが https になった」とは書かない。** `NFR-11` は対象に
  **認証基盤（Keycloak）**を名指ししているが、**OIDC issuer は [[IADR-0091]] 決定 5
  （`Accepted`・live・本 ADR は改めない）のまま `http://keycloak:8080` である** ——
  **経路B に平文が 1 つ残っている。** 残件としての扱いは §検出しないこと（明示）に挙げる。
- **悪い影響 / トレードオフ**:
  - **ブラウザ警告が管理ツール 7 件でも出る**（selfsigned CA。`ADR-0047` §結果 が「ローカル検証環境に限った受忍」と述べている）。
    消すにはルート CA を信頼ストアへ入れる（手順は `deploy/local/edge/README.md`）。
  - **`curl` に `--cacert ca.crt` が要る。** 手順書の疎通確認コマンドをすべて書き換えた。
  - **平文 `http://<tool>.localhost:50000` は TLS ハンドシェイクに失敗する。** 「404」ではなく
    「ハンドシェイク失敗」に変わったため、**古い URL を控えている利用者には別の見え方になる**。
    手順書の該当記述（「平文 http のみ・https は 404」と書いていた 2 箇所）を書き換えた。
    **その記述は前提を [[IADR-0103]] に帰していたが、同 ADR にその決定は無い**（上記 §関連）。
    書き換えにあたって**誤帰属も併せて解消した**。
  - **証明書が 3 本になる**（namespace ごと）。更新は cert-manager が担うため運用手数は増えない。
- **フォローアップ**:
  - **条文の追随（[[IADR-0206]] の「`NFR-11` 適用外」という枠付けの撤回）は #834 が持ち、`848111cd` で
    develop へマージされた。** それを受けて本 ADR による決定 4 の部分 Supersede の注記を同 ADR へ入れた。
  - 本番像（`deploy/helm/`）の HTTPS 化は #780 / #782 が持つ。**本 ADR は `deploy/helm/` の
    `browserRedirectUrl` の例示コメント 1 行以外を触らない。**

## 検出しないこと（明示）

- **実際に TLS ハンドシェイクが通るか**は検査しない（CI に `kustomize build` / クラスタが無い。#783 の領分）。
  本 ADR が静的検査で固定するのは「entrypoint に TLS 引数が在る」「7 ルータすべてが `spec.tls(edge-tls)` を持つ」
  「3 つの namespace に葉証明書が在る」「`argocd` 分は kustomization に含まれず ns 条件付きで apply される」である。
- **ブラウザが証明書を信頼するか**は検査しない（信頼ストアは環境の側にある。[[IADR-0206]] と同じ）。
- **issuer（`http://keycloak:8080`）の https 化は、`NFR-11` の残件として開いている。** 本 ADR は検出も是正もしない。
  `NFR-11` は対象に**認証基盤（Keycloak）**を名指ししているが、経路B の OIDC issuer は
  [[IADR-0091]] **決定 5**（最小案 = `keycloak:8080` 維持。`Accepted`・live・**本 ADR は改めない**）のままであり、
  **エッジを TLS 終端にしても平文のまま残る** —— issuer はエッジを経由しない in-cluster の正準名だからである。
  **その解消は #780**（Keycloak をエッジへ出し OIDC issuer を https のエッジ host へ移す）**が担う。**
  **したがって本 ADR は「`NFR-11` を経路B で満たし切った」とは主張しない。**
  **後続の監査はこの残件を根拠に `NFR-11` を未達のまま開けておくこと**（閉じない）。

## 関連

- Supersedes: **2 本の ADR の、それぞれ一部**（**「`IADR-0206` だけ」ではない**）。
  - **[[IADR-0206]] 決定 4 の 2 命題** —— **P2**（admin:50000 の 7 件へは `spec.tls` を足さない）と
    **P3**（`http` 経路を残す・`--entryPoints.web.http.redirections.*` を足さない）。
    **P1**（443 の `platform-frontend-edge` へ `edge-tls` を追加する）と**決定 1・2・3・5・6 は改めない**ため、
    同 ADR は `Accepted` のまま残置する（先例: [[IADR-0117]] が [[IADR-0056]] 決定 3 を部分改定した形）。
    **対応する注記は同 ADR 決定 4 の `［2026-08-17 追記 / #841］` にある**（#843 マージ後に入れた）。
  - **[[IADR-0094]] 決定 2 の「http/https 両登録」** —— 同決定は「edge admin:50000 は現状 http だが、
    **将来の TLS 化に備え http/https 両方**を realm と Vault role の `allowed_redirect_uris` に登録する」と
    定めていたが、**本 ADR はその http 側を実際に削除し `https` のみにした**
    （realm の `vault` client と `deploy/local/vault/oidc/bootstrap.sh` の `REDIRECTS`）。
    **同 ADR の却下代替案「UI callback を https のみ登録」が、採用形へ反転している** ——
    却下理由「不一致で OIDC が失敗するのを防ぐ」は、**エッジが http を受けなくなったことで消えた**。
    **決定 1・3・4 は改めない**ため同 ADR も `Accepted` のまま残置する。
    **対応する注記は同 ADR §代替案 の直後の `［2026-08-17 追記 / #841］` にある。**
    （CLI の `http://localhost:8250/oidc/callback` は**エッジを経由しないローカル callback のため据え置き**であり、
    Supersede の射程外である。）
- Superseded by: なし
- **[[IADR-0103]] は Supersede しない。** 本 ADR の初稿は「同 ADR が前提にしていた『admin entrypoint は平文 http』」と
  書いていたが、**実測すると同 ADR に `50000` も `entrypoint` も `平文` も 1 件も無い**
  （`grep -n -e '50000' -e 'entrypoint' -e '平文' docs/adr/IADR-0103_*.md` → 0 件）。
  同 ADR が扱うのは **`admin` という「ユーザー」**（realm への恒久定義・ツール別 claim 設計・ESO 後の rollout・
  `argocd` DNS エイリアス・Vault の listing visibility）であって、**`admin` という「entrypoint」ではない** ——
  同じ語だが別物である。前提を同 ADR に帰していたのは
  [`../operations/local-sso-recovery-runbook.md`](../operations/local-sso-recovery-runbook.md) の括弧書きの側で、
  **本 ADR の初稿はその誤帰属を出典に当たらずに引き写していた**。#841 で当該記述を書き換えた際に**帰属ごと解消した**。
  **同 ADR の本文は触っていない**（同 ADR は何も間違っていない）。`related_ids` に残しているのは関連するためである。

> **［2026-08-17 追記 / 波 11 末クロス監査］本 ADR の「射程」の書き方を 3 点是正した。決定は 1 つも変えていない。**
>
> | 箇所 | 何が過大 / 過小だったか | 是正 |
> | --- | --- | --- |
> | §結果 §良い影響 | 「**経路B の全エンドポイントが https**」が**過大**。`NFR-11` は対象に**認証基盤（Keycloak）**を名指しするが、issuer は [[IADR-0091]] 決定 5 のまま `http://keycloak:8080` である | 射程を**エッジ 3 entrypoint**（80 / 443 / 50000）へ限定し、**残件を §検出しないこと（明示）へ挙げた**（解消は #780） |
> | §関連 `Supersedes` | 「[[IADR-0206]] 決定 4 の 2 命題**のみ**」が**過小**。本 ADR は [[IADR-0094]] **決定 2**（http/https 両登録）も覆しており、**同 ADR の却下代替案を採用形へ反転させている** | **2 本立て**に書き直した |
> | §決定 4 の ★ ブロック | 「live な ADR **4 件**」が母集合を狭く見せる（**同型の前提崩れを持つ live な ADR は計 6 件**） | 「4 件 = 新規に追記を入れた数」であることと、**[[IADR-0091]] / [[IADR-0206]] を含めて計 6 件**であることを明記 |
>
> **実作業に漏れは無い**（追記は 6 件すべてに入っており、`Supersede` した実体も正しい）。
> **誤っていたのは、それを説明する記述の射程だけ**である。`related_ids` へ
> [[IADR-0092]] / [[IADR-0093]] / [[IADR-0094]] / [[IADR-0095]] を追加して**相互リンクにした**
> （それまでは 4 件の側だけが本 ADR を持つ片方向だった）。
> 走査と除外理由は作業仕様書 [`../specs/20260817_wave11-audit-followup.md`](../specs/20260817_wave11-audit-followup.md) が持つ。
