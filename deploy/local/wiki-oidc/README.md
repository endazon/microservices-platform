# Wiki.js の Keycloak OIDC(SSO)（IADR-0095・#353）

> 起点: [IADR-0095](../../../docs/adr/IADR-0095_wikijs-keycloak-oidc.md) /
> 作業仕様書 [`docs/specs/20260721_issue-353_wikijs-keycloak-oidc.md`](../../../docs/specs/20260721_issue-353_wikijs-keycloak-oidc.md)

realm には `wiki-js` client が既存（[IADR-0020](../../../docs/adr/IADR-0020_wiki-js-deployment-abac-gateway.md)）。
本 PR で **集約後 URL `wiki.localhost:50000`（#357/edge）への redirect を realm に追加**し、edge に wiki route を足した。
**Wiki.js の OIDC 設定は DB/管理UI 保持**（"Generic OpenID Connect" ストラテジ）で manifest 自動化できないため、下記の
**管理UI 手順**（realm import と同型の runtime 設定）で入れる。ローカルログインは既定無効の OIDC 単一経路（IADR-0020）。

## 到達（集約後 URL・#357/edge）

```sh
# edge 集約を有効化（k3d はポート再作成が必要・破壊操作はユーザー実行）
k3d cluster delete msp-ast-dev
LOCALEDGE=1 bash scripts/k8s-local-up.sh
#   → http://wiki.localhost:50000  （admin-ingress-wiki.yaml が wiki-js:3000 へ）
# 従来の port-forward も併用可: kubectl -n microservices-platform port-forward svc/wiki-js 3300:3000 → http://localhost:3300
```

## OIDC 設定手順（管理UI・**client secret は非コミット**）

Wiki.js 管理コンソール（`/a`）→ **Authentication** → **+ Add Strategy** → **Generic OpenID Connect**:

| 項目 | 値 |
| --- | --- |
| Client ID | `wiki-js` |
| Client Secret | realm の `wiki-js` client secret（dev プレースホルダ `wiki-js-dev-secret-change-me`・**本番は変更**・**UI 入力＝リポジトリに平文コミットしない**） |
| Authorization Endpoint URL | `http://keycloak:8080/realms/microservices-platform/protocol/openid-connect/auth` |
| Token Endpoint URL | `http://keycloak:8080/realms/microservices-platform/protocol/openid-connect/token` |
| User Info Endpoint URL | `http://keycloak:8080/realms/microservices-platform/protocol/openid-connect/userinfo` |
| Issuer | `http://keycloak:8080/realms/microservices-platform` |
| Logout URL（任意） | `http://keycloak:8080/realms/microservices-platform/protocol/openid-connect/logout` |

- **Site URL（重要・Administration → General）**: `http://wiki.localhost:50000` に設定する。Wiki.js は
  **コールバックを `{Site URL}/login/{strategyKey}/callback`** で組み立てるため、これが集約 URL でないと redirect が
  一致しない（strategyKey はストラテジ作成時に生成。realm 側は `http://wiki.localhost:50000/*` の**ワイルドカード**で受ける）。
- **claim / group マッピング（fail-safe）**: strategy の **Map Groups** を有効化し、`groups`（realm の abac-attributes /
  roles スコープ由来）を Wiki.js グループへ対応づける。**未マッピングのユーザーは最小権限グループ（Guests 相当）に割当**
  （deny-by-default 寄り。管理権限は `platform-admin` 等のグループにのみ付与）。
- **issuer 整合（#284 手順A）**: Wiki.js server（microservices-platform ns）は ExternalName alias `keycloak` で in-cluster の
  endpoint に到達する。browser も `keycloak:8080` を解決できるよう hosts 追記＋`port-forward svc/keycloak 8080:8080`。

## 注意

- **port-forward 単独（`LOCALEDGE` 未使用）**: Site URL を集約 URL にしていると、コールバックが `wiki.localhost:50000` を
  指すため edge 未起動だと OIDC が完了しない（Grafana PR-2/IADR-0090・MinIO/IADR-0093 と同性質）。port-forward で OIDC を
  使う場合は Site URL を `http://localhost:3300` にする（realm には旧 redirect も登録済み）。
- **realm 反映**: `wiki-js` client の redirect 追加は realm 再インポートで反映（永続化時は管理コンソール追加 or 再作成）。
- **dev の Wiki.js DB**: OIDC ストラテジは DB 保持。DB を作り直すと再設定が必要（realm import と同様の runtime 手順）。
- CLI/一部 OS で `*.localhost` 未解決なら hosts 追記 or `*.nip.io`。
