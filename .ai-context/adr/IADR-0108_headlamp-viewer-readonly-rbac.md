---
title: IADR-0108 Headlamp の token ログイン用 SA（headlamp-viewer）を manifest 化し、権限は cluster-admin ではなく閲覧専用に絞る
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - IADR-0080
  - IADR-0084
  - IADR-0087
  - IADR-0105
author: claude
created: 2026-07-28
updated: 2026-07-28
plan_refs:
  - planning:projects/microservices-platform/02_requirements/ (NFR 運用性・再現性＝ローカル環境が既定手順だけで再現できること)
---

# IADR-0108: `headlamp-viewer` の manifest 化と閲覧専用 RBAC

- 状態: Accepted
- 日付: 2026-07-28
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: NFR（運用性・再現性＝ローカル k8s の管理 UI が手動ステップなしに再現できること／最小権限）
- 関連 ADR: [IADR-0080](./IADR-0080_headlamp-k8s-management-ui.md)（#271。Headlamp 導入・opt-in・「Pod の SA には広域権限を bind しない」fail-safe）／
  [IADR-0084](./IADR-0084_headlamp-oidc-apiserver-flags.md)（#328。apiserver OIDC 適用不能の実測＝token 方式が正式手順である根拠）／
  [IADR-0105](./IADR-0105_remove-apiserver-oidc-flag-wiring.md)（#399。apiserver OIDC 配線の除去。本 ADR はこれを**復活させない**）／
  [IADR-0087](./IADR-0087_k8s-local-up-optin-smoke-test.md)（#334。`k8s-local-up.sh` の opt-in ゲート smoke test＝回帰固定先）
- 関連仕様書: `docs/specs/20260728_issue-398_headlamp-viewer-manifest.md`
- Issue: #398（enhancement/infrastructure）。`Refs #328`・#393・#388・#271

## コンテキストと課題

ローカル（経路B）の Headlamp ログインは token 方式が正式手順である（[IADR-0084](./IADR-0084_headlamp-oidc-apiserver-flags.md)「⚠️ 2026-07-25 追記」・#393）。
しかしトークン発行元の ServiceAccount `headlamp-viewer` と、それに権限を与える ClusterRoleBinding は
**リポジトリに存在せず**、`deploy/local/README.md` が暫定の `kubectl create ...` を案内しているだけだった。

結果、`HEADLAMP=1` で立ち上げても**手動作成のステップが 1 つ残る**。これは「ローカル環境は既定手順だけで
再現できる」という NFR に反し、新規クラスタ・別マシン・他の実装者（および AI）で手順が失われる。

同時に、暫定手順が案内していた権限は **`cluster-admin`** であり、Issue #398 本文もそれを踏襲していた。
`headlamp-viewer` のトークンは `kubectl create token` で誰でも 24h 分を再発行できるため、
`cluster-admin` を恒久的に紐付けると「UI で閲覧する」用途に対して権限が過大になる。

## 決定

### 決定1: `headlamp-viewer` の SA と RBAC を `deploy/local/headlamp/` の manifest として収録する

新規ファイル `deploy/local/headlamp/headlamp-viewer-rbac.yaml` を追加し、`kustomization.yaml` の
`resources` に加える。既存の `HEADLAMP=1` → `kubectl apply -k deploy/local/headlamp` 経路にそのまま乗るため、
スクリプトの分岐は増やさない。

**Pod の SA（`headlamp`）とは別ファイルに分ける。** `headlamp.yaml` の SA `headlamp` は「権限を一切 bind しない」
ことが fail-safe の要（[IADR-0080](./IADR-0080_headlamp-k8s-management-ui.md)）であり、性質が正反対の「権限を bind する SA」を同じファイルに置くと
その不変条件が読み取りづらくなるため。

fail-safe は不変: headlamp overlay は `deploy/local/infra/kustomization.yaml` に**含めない**ので、
`HEADLAMP` 未設定なら SA も RBAC も作られない。

### 決定2: 付与する権限は `cluster-admin` ではなく**閲覧専用**とする

Issue 本文の `cluster-admin` 案を採らず、read-only に絞る。構成は ClusterRoleBinding 2 本:

| bind 先 ClusterRole | 由来 | 範囲 |
| --- | --- | --- |
| `view` | k8s 組み込み | 名前空間リソースの読み取り（ClusterRoleBinding で全 ns 横断）。**`secrets` を含まない**（組み込み `view` の設計） |
| `headlamp-viewer-cluster-read` | 本 ADR で新規定義 | `view` が持たないクラスタスコープ資源（nodes / PV / StorageClass / CRD / APIService / IngressClass / PriorityClass / RuntimeClass / RBAC / Webhook 設定 / metrics）の読み取り |

verbs は `get` / `list` / `watch` のみ。`create` / `update` / `patch` / `delete` および `pods/exec` は与えない。

### 決定3: 既存の inert な資産・apiserver 設定には触れない

- CRB `headlamp-developer-cluster-admin`（`User: oidc:developer` → `cluster-admin`）は #388 の OIDC 化用資産として
  **現行 inert のまま残す**（[IADR-0105](./IADR-0105_remove-apiserver-oidc-flag-wiring.md) により `oidc:` 接頭辞の identity が生成されないため無効）。
- apiserver への OIDC フラグ／`config.yaml.d` ドロップインは**追加しない**（[IADR-0105](./IADR-0105_remove-apiserver-oidc-flag-wiring.md) の決定を維持）。

## 根拠

- **再現性（決定1）**: 手動ステップが 1 つでも残ると、環境再作成のたびに「README を読んで思い出す」作業が発生し、
  忘れると `NotFound` で詰まる。宣言的 manifest に落とせば `HEADLAMP=1` が唯一の入口になる。
- **最小権限（決定2）**: 用途は Pod / Deployment / Service / ログの**閲覧**であり、書き込み権限を必要としない。
  トークンは `kubectl create token` で誰でも再発行できるため、恒久 bind の権限は用途上の下限に置くのが妥当。
  組み込み `view` を土台にすることで、`secrets` 非許可という k8s 側で維持される設計をそのまま享受できる
  （自前で全資源 `*` の read を書くと `secrets` まで読めてしまう）。
- **可読性（決定1のファイル分離）**: 「`headlamp` SA には権限を付けない／`headlamp-viewer` には閲覧権限だけ付ける」
  という 2 つの規則を、ファイル境界で表現する。

## 影響・トレードオフ

- **UI の編集操作は 403 になる。** Headlamp からの scale / delete / exec / YAML 編集はできない。書き込みが必要な操作は
  各自の kubeconfig（クラスタ管理者権限）で `kubectl` を使う。dev 用途では読み取りが主であり許容する。
  将来 UI からの編集を常用したくなった場合は、本 ADR を改訂して `edit` ClusterRole の追加 bind を検討する。
- **既存クラスタに手作りされた CRB `headlamp-viewer`（cluster-admin）が残っていても衝突しない**（新規は
  `headlamp-viewer-view` / `headlamp-viewer-cluster-read` と別名）。ただし残存すると実効権限は cluster-admin のまま
  なので、README に削除手順を記載する。
- **クラスタスコープの読み取り列挙は k8s の資源追加に追随しない。** 新種のクラスタスコープ CRD を Headlamp で
  一覧したくなった場合は `headlamp-viewer-cluster-read` に追記が要る（CRD 実体は `view` の対象外のため）。
  ワイルドカード（`apiGroups: ["*"], resources: ["*"]`）で回避する案は、`secrets` が読めてしまうため採らない。
- RBAC 定義（ClusterRole/Binding）の読み取りを許可するため、誰が何を持つかは可視化される。読み取りのみ・
  ローカル閉域・dev 限定であり、権限昇格には繋がらない。

## 却下した代替案

- **`cluster-admin` を bind する（Issue 本文の案・従来の手作り運用と同じ）**: 手順は最短だが、24h トークンを
  誰でも再発行できる SA へ最大権限を恒久付与することになる。閲覧という用途に対し過大で、
  ローカル閉域であっても既定として妥当でない。
- **`ClusterRole` を 1 本にまとめ `apiGroups: ["*"] / resources: ["*"] / verbs: [get,list,watch]` とする**:
  記述は短いが `secrets` が全 namespace で読めてしまう（Keycloak admin パスワード・DB 認証情報等が平文で参照可能）。
  組み込み `view` が `secrets` を外している設計を捨てることになるため却下。
- **`headlamp.yaml` に追記する（ファイルを分けない）**: 差分は小さいが、「Pod の SA には権限を bind しない」という
  fail-safe の不変条件と、「ログイン用 SA には権限を bind する」という意図が同じファイルに同居し読み違えを招く。
- **Headlamp Pod の SA（`headlamp`）自体に閲覧権限を bind する**: ログイン不要で誰でも見られる状態になり、
  [IADR-0080](./IADR-0080_headlamp-k8s-management-ui.md) の fail-safe（トークンを貼らない限り可視化できない）を壊すため却下。

## 受け入れ条件

- `kubectl kustomize deploy/local/headlamp` に SA `headlamp-viewer`・ClusterRole `headlamp-viewer-cluster-read`・
  CRB 2 本が出力される（クラスタ非接続のレンダで検証可能）。
- `HEADLAMP` 未設定時は overlay 自体が適用されず、SA も RBAC も作られない（`k8s-local-up.test.js` で固定）。
- overlay の RBAC が `get`/`list`/`watch` 以外の verb を含まない（`k8s-local-up.test.js` で固定）。
- `HEADLAMP=1` で up した新規クラスタで、手動作成なしに `kubectl -n platform-infra create token headlamp-viewer`
  → Headlamp の Token ログイン → リソース閲覧ができる（実ブラウザ確認は稼働クラスタ依存＝live）。
