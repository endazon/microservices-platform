---
title: IADR-0514 deploy/ のインフラのイメージは `tag@sha256:<index digest>` で固定し、tag だけの参照の再混入は検査器（check-image-digests.js）で止める。自製イメージは対象外、Renovate は採らない
type: impl-adr
status: Accepted
related_ids: [NFR, ADR-0107, ADR-0112, ADR-0030, ADR-0007, ADR-0106, IADR-0461, IADR-0088, IADR-0315, IADR-0286]
author: claude
created: 2026-10-08
updated: 2026-10-09
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0107_infrastructure-product-selection-criteria.md 決定 3（digest 固定・Harbor ミラー）・決定 5・フォローアップ 2
  - planning:projects/microservices-platform/07_adr/ADR-0112_infrastructure-admin-endpoint-criterion.md 決定 3
  - planning:projects/microservices-platform/07_adr/ADR-0007_cicd-gitops-argocd.md（Harbor）
related_specs:
  - ../specs/20261008_1787_infra-audit-digest-pin.md
  - ../specs/20261009_1814_base-and-testcontainers-digest.md
---

# IADR-0514: deploy/ のインフラのイメージの digest 固定と、tag だけの参照の検知（#1787）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-08
- 決定者: claude（#1787。計画 ADR-0107 決定 3 が固定を定め、表記・境界・検知の仕組みを実装へ委ねた）

## 起点・関連

- 起点 issue: **#1787**（第 4 回全体監査 #1772 の切り出し）。
- 計画: **ADR-0107 決定 3**（イメージは digest で固定する。Harbor 配備後はミラーする）・**決定 5**（年 1 回と契機の点検）・**フォローアップ 2**。**ADR-0112 決定 3**（点検に基準 D）。
- 先例: [IADR-0461](./IADR-0461_object-storage-seaweedfs-deployment.md) 決定 1（seaweedfs を `tag@digest` で固定し、helm values に `digest:` を足した）。[IADR-0088](./IADR-0088_image-reference-redeploy-safety.md)（Wiki.js を浮動 major から `2.5` へ）。[IADR-0286](./IADR-0286_default-credentials-fail-fast.md)（例外を JSON に凍結する前方一方向の検査器）。[IADR-0315](./IADR-0315_qdrant-server-version-follows-client.md)（統合試験の Qdrant を配備と同じ参照で起こす）。
- 基点コミット: MSP `origin/develop` `1d71b2b3`。

## コンテキストと課題

`deploy/` のインフラのイメージの参照 42 件（21 製品）のうち、digest で固定していたのは seaweedfs（compose・helm values）と backup イメージの `FROM` の 3 件だけだった。運用仕様書は「具体版タグ（可能なら digest）」「digest は CD 層で段階導入」と書いており、ADR-0107 決定 3 と食い違っていた。

### 決めることは 4 つ

| # | 論点 | 選択肢 |
| --- | --- | --- |
| 1 | 表記 | (a) `repo@sha256:…`（tag を消す）／(b) `repo:tag@sha256:…`（tag を残す） |
| 2 | どの digest か | (a) 特定アーキテクチャの manifest／(b) multi-arch の image index（manifest list） |
| 3 | 再混入の検知 | (a) Renovate の設定（`pinDigests`）／(b) リポジトリ内の検査器／(c) 両方 |
| 4 | 境界 | 自製イメージ（`microservices-platform/*`・`k3d-local/*`）を含めるか |

## 決定

### 決定 1 — 表記は `repo:tag@sha256:<index digest>`。digest は multi-arch の image index のもの

- **1(b)・2(b) を採る。** tag は人が版を読むために残す（seaweedfs と同型）。digest があれば実行時は digest が優先され、tag は効かない。
- index の digest にするのは、開発者の手元（arm64）と CI（amd64）で同じ参照を使うためである。2026-10-08 に新たに固定した 20 種の参照（repo:tag）を匿名で 2 回解決し、**全件 index / manifest list 型で、2 回とも一致**した（seaweedfs は既存の digest と 1 回目の解決が一致）（作業仕様書 §初回の点検の方法）。
- helm values の 3 キー形式（`registry` / `image` / `tag`）は `digest:` を足し、テンプレートは `{{ if digest }}@{{ digest }}{{ end }}` で描く（embedding・wikijs を seaweedfs に揃えた）。
- 🔴 **浮動の tag（`7-alpine`・`2.5`・`22-alpine` 等）に digest を付けると、tag 系列の自動パッチは効かなくなる。** これは ADR-0107 決定 3 の意図（中身の差し替えを検知できるようにする）そのものであり、パッチは digest の解決し直しで取り込む（手順は運用仕様書 §インフラ製品の点検）。tag は変えない（変えると compose・k8s・試験の定数の追随が版の変更と混ざる）。

### 決定 2 — 自製イメージは対象外

- `microservices-platform/*`（chart の `services.*`・`frontend`）と `k3d-local/*`（経路 B の擬似レジストリ）は**対象外**。CD が一意タグ/digest を渡す（運用仕様書 §自製イメージ）。chart 既定の `tag: latest` は CD 上書き用のプレースホルダであり、digest を置く版がまだ無い。
- テンプレートの `{{ … }}` を含む行は values 側で検査する。
- ［2026-10-08 追記 / PR #1813 の独立監査］自製の判定は実際に使う接頭辞だけに錨を下ろす（レジストリ無しか `harbor.internal` / `k3d-local` 付きの `microservices-platform/`、および `k3d-local/`）。任意のレジストリ配下の `microservices-platform/` は免除しない。あわせて helm の `image: {repository, tag}` 形式と `*.Dockerfile` / `Containerfile` も拾うようにした。残る未対応の形は #1814。
- ［2026-10-09 追記 / #1814］**自製イメージの基底イメージは対象に入れる。** 自製イメージ（`microservices-platform/*`）そのものは対象外のままだが、
  それが `FROM` で載る上流のイメージ（`mcr.microsoft.com/dotnet/{sdk,aspnet}`・`node`・`caddy`）は外部イメージであり、
  同じ tag の中身の差し替えで自製イメージの中身が変わる点は infra と同じである。統合試験の Testcontainers のイメージも同様に対象とする。

### 決定 3 — 検知は検査器（`scripts/check-image-digests.js`）。Renovate は採らない

- **3(b) を採る。**
  - Renovate は本リポジトリに配備されていない（GitHub App の導入と権限付与は利用者の手が要る）。設定ファイルだけを置いても**何も検査されないまま緑に見える**（「統制を定めた」と「統制が働いている」の取り違え）。
  - Renovate の `pinDigests` は**更新の PR を出す**仕組みで、tag だけの参照の混入を**PR の段で赤にする**仕組みではない。受け入れ基準の「検知する」には CI の門が要る。
  - 検査器はネットワークを使わず `deploy/` だけを読むので、CI で決定的に走る。digest の解決は人が行う（決定 4）。
- 検査器が落とすもの: tag だけの参照／理由の無い例外／**実在しない例外**（腐り止め）／**同じ `repo:tag` が別の digest で書かれている**（compose と k8s の片側だけを更新した）／走査 0 件（fail-closed）。
- 例外は `scripts/image-digest-exceptions.json` に `file`・`ref`・`reason` で載せる。**2026-10-08 時点で 0 件。**
- `--list` は年次点検の母集合（製品ごとの参照箇所）を出す。点検は手で列挙しない（ADR-0107 決定 5 の点検の母集合）。
- CI は既存の `static-checks` ジョブに 2 ステップを足す（ジョブ名・起動条件・必須チェックは変えない）。
- ［2026-10-09 追記 / #1814］**走査の範囲を `src/` へ広げた。** `src/` は Containerfile（基底イメージ）と C#（Testcontainers の
  `new <X>Builder("<ref>")`・`.WithImage("<ref>")`・`const string …Image / Reference`、引数なしのモジュールのビルダ）だけを読み、
  YAML は読まない（配備物の `image:` は `deploy/` にしか無い）。submodule の `src/ai-stock-trading` は別リポジトリなので除く。
  あわせて PR #1813 の監査の残り（`COPY --from=<外部イメージ>`・`RUN --mount=…,from=`・テンプレートに直書きした `default "<ref>"`）
  を拾う。**独立した検査器は作らず本検査器を広げた**——表記・例外・`[digest-mismatch]`（配備と試験で同じ `repo:tag` を
  別の digest で書かない）を同じ規則で効かせるためである。CI の配線（ステップ名・ジョブ）は変えない。
  2026-10-09 時点で 24 製品・79 参照（自製 21 件は対象外）、例外 0 件。
- ［2026-10-09 追記 / #1814 の独立監査］**C# の読み方の精度を上げた。**
  - ビルダは**名前の許可リスト**（Testcontainers のモジュール名＋`Container`）で判定する。拒否リストだった版は
    `AuthorizationPolicyBuilder("…")` のような無関係なビルダを読んだ。名前で判定するので、`using` の形（`global using`・
    csproj の `<Using Include="Testcontainers…">`・完全修飾の `new Testcontainers.PostgreSql.PostgreSqlBuilder("…")`）に依らず拾う。
  - 名前付き引数（`PostgreSqlBuilder(image: "…")`・`WithImage(image: "…")`）と `static readonly string …Image = "…"` を拾う。
  - 引数なしのモジュールのビルダは、同じ式（次の `;` まで）に `.WithImage(` があれば既定のイメージを使わないので落とさない。
  - コメントは行頭だけでなく行末の `//` とブロックコメントも読まない（文字列・文字リテラルの中の `//`・`"` は残す）。
  - Containerfile は行継続（末尾の `\`）をつないだ論理行で読む（`RUN \` の次の行の `--mount=…,from=` を拾う）。
  - **既知の限界（拾わない）**: target-typed の `new("<ref>")`（型が式に現れない）／変数・補間文字列・逐語的文字列・
    連結で組み立てた参照／Testcontainers に言及しないファイルの `…Image` / `Reference` 定数／許可リストに無いモジュールの
    ビルダ（新しいモジュールを使い始めたら `TC_MODULES` に足す）／許可リストと同名の無関係なビルダにイメージ形の
    文字列を渡す形（拾って検査してしまう。実在しない）。いずれも本リポの現物には無い（2026-10-09 の走査で 0 件）。

### 決定 4 — 固定の更新は人が匿名の registry API で解決する。Harbor へのミラーは配備まで対象外

- 手順は運用仕様書 §インフラ製品の点検（匿名トークン → `HEAD /v2/<repo>/manifests/<tag>`（Accept に index 型）→ `docker-content-digest`）。tag と digest を対で変え、`--list` が示す全参照を揃える。
- **Harbor へのミラーは Harbor の配備後に行う**（ADR-0107 決定 3「配備した後」。Harbor は未配備）。配備までは上流のレジストリから digest で取得する。
- ［2026-10-09 追記 / #1814］**基底イメージの digest は、年次点検に加えて上流のセキュリティ修正の告知を契機に解決し直す**
  （.NET の月例のサービシングリリース等。運用仕様書 §インフラ製品の点検 の契機⑤）。基底イメージはアプリの実行環境の修正を
  運ぶため、年 1 回では間隔が長すぎる。告知の検知は人が行う（自動の更新 PR の仕組み〔Dependabot の docker エコシステム等〕は
  採っていない。導入するかは別に判断する）。基底イメージと Testcontainers のイメージの更新は platform-infra の Pod を
  作り直さないので、運用仕様書の「インフラのイメージ参照を変える配備は引け後」には当たらない。

## 統制と現在の実現手段

| | |
| --- | --- |
| **統制** | ADR-0107 決定 3（digest 固定・Harbor 配備後のミラー） |
| **現在の実現手段** | `deploy/` のインフラの参照 42 件（21 製品）を digest で固定した。tag だけの参照の再混入は `check-image-digests.js`（CI の `static-checks`）が止める。統合試験の Qdrant（`QdrantTestImage.Reference`）も同じ digest にした |
| **配備までの暫定手段** | 🔴 **Harbor へのミラーは無い**（未配備）。上流が配布を止めると、digest で固定していても取得できない（ADR-0107 決定 5 の検知＝CI の取得失敗に頼る） |

## 結果

- 良い影響: 同じ tag の中身の差し替えで配備物が変わらなくなる。compose と k8s の片側だけの更新が CI で止まる。年次点検の母集合が機械的に引ける。
- 悪い影響 / トレードオフ: 浮動 tag の自動パッチ（`-alpine` のセキュリティ修正を含む）が止まる。**取り込みは年次点検と契機ごとの digest の解決し直しに依る**（ADR-0107 §結果 の「固定を更新する作業が要る」）。

## 残余

1. **`scripts/` が chart / マニフェストで入れる製品**（Istio・External Secrets・Reloader・cert-manager・Argo CD・k3s）は `deploy/` に参照が無く、本 IADR の対象外。Argo CD は `stable` ブランチのマニフェストを直接 apply しており版すら固定されていない。計画への確認を作業仕様書 §環流の記録 5 に置き、planning#750 で起票した。
   ［2026-10-09 追記 / #1843］**計画 ADR-0135 が母集合に含めると裁定し、版の固定と点検の追補を行った。** Argo CD は版のタグの URL（v3.5.4）、k3s は既定で `rancher/k3s:v1.35.4-k3s1` に固定した。6 製品の基準 A〜D は運用仕様書 §点検の記録 の 2026-10-08 回の追補に載せた。chart・上流マニフェストの内側のイメージの digest は固定していない（理由と残余は [IADR-0519](./IADR-0519_scripts-installed-products-version-pinning.md)）。
2. **統合試験の Testcontainers のイメージ**（`postgres:16-alpine`・`rabbitmq:3.13-alpine` 等。Qdrant と SeaweedFS を除く）は `deploy/` の外で、固定していない。
   ［2026-10-09 追記 / #1814］**解消した。** 基底イメージ（`src/` の Dockerfile）とあわせて固定し、検査器の走査に入れた（決定 3 の追記）。
3. digest は registry API から読んだ値であり、**本作業ではクラスタで pull して確かめていない**（実行機にクラスタが無い）。compose の構文（`docker compose config`）と chart の描画・スキーマ（`check-deploy-manifests.js`）は確かめた。

## 関連

- 作業仕様書: [20261008_1787_infra-audit-digest-pin](../specs/20261008_1787_infra-audit-digest-pin.md)
- 実装: `scripts/check-image-digests.js`・`scripts/image-digest-exceptions.json`・`deploy/`（compose・`deploy/local`・`deploy/mail-relay`・helm values / templates）・`docs/operations/operations.md` §インフラ製品の点検
