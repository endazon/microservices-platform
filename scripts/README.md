# scripts — 補助スクリプト

補助成果物（CHANGELOG / OpenAPI）の生成・環境セットアップ・プロファイル適用を行う依存ゼロのスクリプト群。

| スクリプト | 役割 | 出力 |
| --- | --- | --- |
| `gen-changelog.js` | コミット履歴（`種別(起点ID): 要約`）から変更履歴を生成 | `CHANGELOG.md` |
| `gen-openapi-skeleton.js` | 通信仕様書（`docs/api/`）から OpenAPI 雛形を生成 | `docs/api/openapi.yaml` |
| `check-doc-links.js` | `docs/` 配下 Markdown の相対リンク（frontmatter の `plan_refs`/`related_specs`・本文リンク・インラインコードのパス）の実在を検査。破損があれば終了コード 1。**未 populate な submodule 配下は対象外にし、その件数を submodule 別に `notice` で報告する**（黙って飛ばすと「破損リンクはありません」が検査していない範囲まで含んだ断定になる） 。`--require-planning` で planning サブモジュール未 populate を fail 扱いにする（#232 / IADR-0058） | 標準出力（レポート） |
| `check-permission-denials.js` | claude-code-action の実行ログ（`outputs.execution_file`）を読み、**権限拒否で実行できなかったツール**を名前と件数で報告（Bash は `Bash(git show | diff)` のようにパイプ・置換の**全セグメント**を出す。引数は出さない）。**失敗判定は段階ポリシー**: 件数が許容値（既定 4、`PERMISSION_DENIALS_TOLERANCE` で変更可）を超えるか、拒否がターン数の半分以上なら終了コード 1。それ未満は警告（アノテーション + 実行サマリ）のみで終了コード 0——「成果物は正しいのに赤」の常態化は、拒否の赤を無視する学習を生み検査の目的を壊すため。`STRICT_PERMISSION_DENIALS=1` で「1 件でも失敗」の旧挙動に戻せる（実測: レビューが 17 件の拒否で潰れ、本文を書けないまま `success` で終了した事故が起点）。実行ログを読めない場合は `warn` を出して終了コード 0（fail-open）。**内訳は `$GITHUB_STEP_SUMMARY`（PR の Checks 画面から 1 クリック）にも書く**——ジョブログにしか無いと、AI 本文の「✅ 実測」との突き合わせができないため（planning#155）。`--self-test` で検証器自体も試験 | 標準出力＋実行サマリ |
| `check-action-versions.js` | ワークフローの `uses: <action>@vN` を集め、**メジャーバージョンの退行**を検出。`action-versions.json` の下限を下回る、または `--compare-with` で指定したディレクトリ（Dependabot 管理下のリポジトリ直下）より古ければ終了コード 1。Dependabot は github-actions エコシステムでは**リポジトリ直下しか走査しない**ため、配布テンプレートは自動追随しない（planning#148）。表に無いアクション・使われていない表エントリは `warn`。`--check-latest` で GitHub API から新しいメジャーを確認（warn のみ・fail-open）。`--self-test` で検証器自体も試験 | 標準出力（レポート） |
| `check-ai-workflow-config.js` | Claude 系ワークフローのツール許可設定を検査。`claude_args` の記法誤り（空白分割で無効化）・ブロック内コメント・「SDK を用意して実行ツールを許可していない」不一致・**実装用とレビュー用のスタック別実行ツールのドリフト**（片方にだけ `Bash(node:*)` が無い等）を検出。不備があれば終了コード 1。`--self-test` で検証器自体も試験 | 標準出力（レポート） |
| `check-unit-dependencies.js` | ユニット依存方向の機械検査（#231）。csproj の `ProjectReference`（ユニット外参照は `platform/backend/Shared/` の 3 プロジェクトのみ許可・platform→可変ユニット禁止・統合テスト例外。2 → 3 の改定は IADR-0117）と `Foundation/` 配下の `using *.Composable.*` を静的走査。違反があれば終了コード 1。フロントの合成点制約は ESLint（`src/eslint.config.js`）が担う。方式の根拠は IADR-0057 | 標準出力（レポート） |
| `check-cpm-versions.js` | CPM（Central Package Management）のバージョン直書き禁止の機械強制（#467）。`src/`（`ai-stock-trading` を除く）と `templates/` の `.csproj` を走査し、`PackageReference` の `Version` **属性**と `<Version>` **子要素**（MSBuild では属性形と等価）を違反として検出。違反があれば終了コード 1（着手時点の実測が違反 0 件のため ratchet / baseline を持たず最初から fail）。`VersionOverride` は CPM 公式の回避口のため**許可**し、使用箇所のみ `warn` ＋ 実行サマリの表で可視化する（終了コードは変えない）。走査対象は `.csproj`（雛形の `.csproj.sample` 含む）のみ——`.props` / `.targets` には正当な版記述（`PackageVersion` / `GlobalPackageReference`）があるため。XML コメントは除去してから走査する（説明コメント内の例示を赤にしない）。`check-backend-libraries.js` とは関心が異なる（**どの**ライブラリか / 版を**どこに**書くか）。`--self-test` で検証器自体も試験（負例を一時ツリーで実走査） | 標準出力＋実行サマリ |
| `check-contract-schema.js` | サービス間契約（`Shared.Contracts` のイベント/API スキーマ）の後方互換検査（#465 / IADR-0122）。`src/<unit>/backend/Shared/*.Contracts`（`ai-stock-trading` を除く）の `.cs` を**構文解析**し、public 型・メンバー・enum 値・`const` 値・属性を正規化 JSON スナップショット `contract-schema-baseline.json` へ落として比較する。削除・型変更・必須化・位置引数の並べ替え・enum/`const` 値の変更・属性の変更・**既定値の無いメンバーの追加**は**破壊的**として終了コード 1。非破壊の追加でも baseline と差分がある限り fail する（＝スナップショットテスト。`--update` で baseline を更新し、差分＝契約変更そのものを PR の diff に載せる）。破壊的変更は `contract-breaking-allowlist.json` の承認エントリ（`key`/`reason`/`approvedBy`/`issue`/`date` すべて必須）で通す（下記「契約の破壊的変更」）。抽出方式にリフレクション（.NET SDK 依存）・OpenAPI（イベント 0 件）・proto（`.proto` 0 件）を採らない理由は IADR-0122。`--self-test` で検査器自体も試験（負例を一時ツリーで実走査） | 標準出力＋実行サマリ |
| `check-i18n-catalogs.js` | Lingui カタログの**未翻訳キー**検査（#496 / ADR-0031 / IADR-0125 決定 4）。`src/lingui.config.ts` から locales とカタログのパスを読み（設定ファイルは**実行せず**正規表現で読む＝外部依存ゼロ）、`.po` を解析して **全ロケール・全エントリの `msgstr` が非空**であること、`fuzzy` フラグと `#~`（obsolete）が残っていないことを検査する。違反があれば終了コード 1。**`lingui extract` の再生成差分検査だけでは足りない**——`extract` は未訳を `msgstr ""` の空エントリとして生成するのが正常動作であり、未翻訳のまま差分検査を通過する。**`lingui compile --strict` でも足りない**——`sourceLocale`（本リポは ja）は検査対象外で、ja の訳文が空でも通る（いずれも実測。作業仕様書 §検証）。設定が読めない・カタログが欠けている場合は **fail-closed**（「見つからないから素通り」で検査が静かに失効するのを防ぐ）。`--self-test` で検査器自体も試験 | 標準出力 |
| `check-static-egress.js` | 静的ビルド成果物が**外部オリジンから何も取りに行かない**ことの検査（#496 / 08_data-egress-policy / IADR-0125 決定 5）。既定の走査対象は Storybook の静的ビルドと **SPA の `dist/`** の両方（統制対象は「SPA フロントエンド」そのものであり、カタログだけを見ても片手落ちである）。検出するのは**実際に取りに行く参照**——HTML のリソースタグ（`<link href>` / `<script src>` / `<img src|srcset>` / `<iframe src>` ほか。**`<a href>` は対象外**＝遷移であって取得ではない）、CSS の `@import` と `url()`、および既知の禁止ホスト（フォント CDN・汎用 CDN・analytics・エラー報告 SaaS）はどこに現れても違反。XML 名前空間（`http://www.w3.org/2000/svg`）や JSON Schema の `$schema` のような**取りに行かない URL 文字列**は検出しない（除外は用途ではなく**パターン**で書く。個別許可を積むと許可リストが検査の無効化装置になる）。**検出できないものを明記する（本検査は網羅ではない）**: 見るのは上記 3 種だけであり、**禁止ホスト表に無いホストへの `fetch()` / `XMLHttpRequest` / `WebSocket` / 動的 `import()` は検出しない**。その経路は ESLint の `no-restricted-globals`（`foundation/api` 以外での `fetch` 等の禁止。[[IADR-0121]] 決定 8）と orval の入力制限（同 決定 3）が担う。禁止ホスト表は網羅表ではなく代表例の表である。段階ポリシー: 引数なしは成果物が無ければ warn ＋ exit 0（fail-open）、`--require <dir>` は無ければ **fail**（CI はこちら）。`--self-test` で検査器自体も試験 | 標準出力 |
| `check-test-spec-coverage.js` | **実在するバックエンドテストが `docs/tests/` のテスト仕様書に載っているか**の検査（#510 / IADR-0130）。突合の単位は「**仕様書ファイル × テストクラス**」の対である。`src/**/*Tests.cs`（`.gitmodules` 由来の除外ユニットを除く）のクラス名を集め、各仕様書の本文から**識別子境界つきで**参照を探す（単純な部分一致だと `HealthEndpointTests` が `BffHealthEndpointTests` の一部として誤って被覆済みになる）。**クラス名だけを見る形では足りない**——`DocumentVersioningTests` は SC-05 と FR-06 の両方が参照しているため、SC-05 の節を丸ごと消しても緑になる（#510 の変異試験で実測）。落ちるのは**節**であり節は仕様書に属するので、対で固定する。判定は ratchet: 床 `test-spec-coverage-baseline.json` にある対が消えた（**節の消失**）／床にある対のクラスが実在しない／記載された対が床に無い、のいずれも終了コード 1。どの仕様書にも載らず床にも無いクラスは `warn`（基盤・回帰テストに記載義務は負わせない）。`--update` で床を再生成し差分を PR に載せる。**`check-test-traceability.js` とは対象が異なる**——あちらは起点 ID（FR/UC/SC）の写像、こちらはテストの実体と仕様書の記載の対応であり、ID の写像は「節が丸ごと消えても緑」（#510 の実測）。走査 0 件・`docs/tests/` 0 件・床が読めない／形式が違う場合は **fail-closed**。`--self-test` で検査器自体も試験（ratchet 4 判定と負例を一時ツリーで実走査） | 標準出力＋実行サマリ |
| `check-image-mapping.js` | `k8s-local-images.sh` の `MAPPING`（chart-image ↔ Dockerfile）と `deploy/docker-compose.yml` の `build` 定義の対応を機械検査（#275）。欠落・stale・Dockerfile 不一致・命名不整合・compose 専用除外（`frontend`）の腐り/二重掲載を検出し、ドリフトがあれば終了コード 1。ビルド可否は `images.yml`（#268 / IADR-0067）が担う。方式の根拠は IADR-0068 | 標準出力（レポート） |
| `verify-qdrant-attribute-payload.sh` | IADR-0014 / #71: 実機 Qdrant で ABAC 属性の格納表現・フィルタ通過を検証 | 標準出力（判定） |
| `measure-abac-combinations.js` | FR-17 / FR-18・#456: **実在する ABAC 属性の組み合わせ数を実測**する（計画 `14_knowledge-graph-graphrag` §6 手順 1）。計画が定める粒度の 3 段階——属性組み合わせ単位 / ロール単位 / 機密区分単位——をまとめて数え、利用者属性（Keycloak）× 文書属性（`document_svc`）の到達可能な組を `AbacEvaluator` と同じ意味論で評価する。**読み取り専用**（SELECT と Admin API の GET のみ）。既定は経路B を `kubectl exec` 経由で見るが、`ABAC_DOC_DSN` / `ABAC_KC_URL` で任意環境へ向けられる。`--dump` で収集した生データを保存し、`--input` で再集計できる（**データ破棄後も追試できる**＝#457 の切替前に測る意義）。集計は純関数で `scripts.repo.test.js` が単体試験する | 標準出力（要約 / `--json`） |
| `lib/excluded-units.js` | 検査器共通。`.gitmodules` の `src/<unit>` submodule から**検査対象外ユニット**を導出する単一情報源（#473）。`check-backend-libraries.js` / `check-test-traceability.js` / `check-coverage-floor.js` が使う。リポジトリ直下の `planning` は `src/` 配下でないためユニットにならない。`.gitmodules` が読めない場合は既定値へフォールバックせず**例外で停止**（除外 0 件で別プロジェクトを検査する fail-open を避ける）。`--self-test` でヘルパ自体も試験 | — |
| `lib/ci-annotate.js` | 検査器共通。警告を GitHub Actions のアノテーション（`::warning::` / `::notice::`）として出す。素の出力は緑ジョブのログに埋もれて読まれないため。ローカル実行時の見た目は従来どおり | — |
| `setup.sh` | 開発環境セットアップ（SessionStart hook / devcontainer から実行） | — |
| `apply-profile.sh` | `AI_SETUP.md` で宣言したプロファイルに応じてキットを構成（`.example` 有効化等） | `.ai-profile` |

## プロファイルの適用

利用可能な AI（`claude-code` / `api` / `copilot`）を `AI_SETUP.md` で宣言し、対応する構成を適用する。

```bash
bash scripts/apply-profile.sh claude-code          # サブスクリプション
bash scripts/apply-profile.sh api                  # Anthropic API
bash scripts/apply-profile.sh --prune copilot      # Copilot のみ（Claude 系を削除）
```

## 使い方（ローカル）

```bash
node scripts/gen-changelog.js --out CHANGELOG.md
node scripts/gen-openapi-skeleton.js --src docs/api --out docs/api/openapi.yaml
node scripts/check-doc-links.js                    # 仕様書の相対リンク切れを検査（再発防止）
node scripts/check-ai-workflow-config.js           # AI ワークフローのツール許可設定を検査
node scripts/check-action-versions.js              # Actions のバージョン退行を検査
node scripts/check-action-versions.js --compare-with-ref origin/develop  # 同期による巻き戻りを検査
node scripts/check-action-versions.js --check-latest  # 新しいメジャーが出ていないか確認
node scripts/check-permission-denials.js <log>     # 実行ログの権限拒否を検査（CI では自動実行）
node scripts/scripts.test.js                       # 上記スクリプト群の単体テスト
node scripts/check-unit-dependencies.js --self-test # 検査器の自己試験
node scripts/check-unit-dependencies.js            # ユニット依存方向の検査（#231）
node scripts/check-cpm-versions.js --self-test     # 検査器の自己試験
node scripts/check-cpm-versions.js                 # CPM のバージョン直書き検査（#467）
node scripts/check-contract-schema.js --self-test  # 検査器の自己試験
node scripts/check-contract-schema.js              # Shared.Contracts の後方互換検査（#465）
node scripts/check-contract-schema.js --update     # baseline を現状で更新（承認済みの破壊的変更を消費）
node scripts/check-i18n-catalogs.js --self-test    # 検査器の自己試験
node scripts/check-i18n-catalogs.js                # Lingui カタログの未翻訳キー検査（#496）
node scripts/check-static-egress.js --self-test    # 検査器の自己試験
node scripts/check-static-egress.js --require src/packages/ui/storybook-static  # 外部 egress 検査（#496・要ビルド）
node scripts/check-test-spec-coverage.js --self-test  # 検査器の自己試験
node scripts/check-test-spec-coverage.js             # 実在するテスト → テスト仕様書の記載検査（#510）
node scripts/check-test-spec-coverage.js --update    # 床を現状で再生成（記載を増やしたとき）
node scripts/check-image-mapping.js --self-test    # 検査器の自己試験
node scripts/check-image-mapping.js                # MAPPING ↔ compose build のドリフト検査（#275）
node scripts/k8s-local-up.test.js                  # k8s-local-up.sh の opt-in ゲート横断 smoke test（#334・要 bash）
```

> `check-ai-workflow-config.js` は、AI レビュー / 実装が「ジョブは成功するのに検証を実行できない」
> 状態に陥る設定不備を機械的に止める。失敗モードの一覧は `impl-handoff-kit/HOWTO.md` の
> 付録3（トラブルシューティング）を参照。
>
> **警告（`warn`）も読むこと。** 本検証器は「検査そのものが効いていない」状態を warn で報告する
> （既定名のファイルがあるのに `claude_args` を解析できない／既定名で 2 ファイルを引き当てられず
> ドリフト検査が動かない）。exit 0 のままなので CI は緑になるが、その間は記法検査もドリフト検査も
> 実行されていない。ERROR にしないのは、アクションの入力名変更で全リポジトリの CI が一斉に
> 落ちるのを避けるため（fail-open）である。
>
> GitHub Actions 上では警告は **アノテーション**（`::warning::`）として出るため、ジョブログを
> 開かなくても PR の Checks 画面と実行サマリで気付ける。ファイル名・構成が固まったリポジトリは
> `STRICT_AI_WORKFLOW_CONFIG=1` で警告を失敗として扱える（既定はオフ）。
>
> **`check-doc-links.js` の「対象外」表示に注意する。** PR CI は submodule を populate しないため、
> `planning/` 配下などへのリンクは**検査されない**。出力の `（未 populate の submodule 配下 N 件は
> 対象外 …）` はその範囲を示す。実際に ai-stock-trading では PR CI が planning 配下 753 件を毎回
> 飛ばし、その隙間に破損 20 件が蓄積した。PR 段階で検査したい場合は checkout に submodules と
> トークンを付けるか、定期ジョブ（`doc-links-planning`）の結果を確認すること。

## 検査（CI）

`ci.yml` が PR ごとに以下を実行する。**`scripts.test.js` は `scripts-tests` ジョブで走る**。

| ジョブ | 実行内容 |
| --- | --- |
| `scripts-tests` | `node scripts/scripts.test.js`（本 README のスクリプト群の横断テスト。`fetch-depth: 0` が必要） |
| `commit-messages` | `check-commit-messages.js`（コミット件名の規約と ADR/IADR 実在性） |
| `doc-links` | `check-doc-links.js`（相対リンクの実在） |
| `ai-workflow-config` | `check-ai-workflow-config.js --self-test` と本検査、および `check-action-versions.js`（Actions のバージョン退行。`fetch-depth: 0` が必要） |
| `pipeline-config` | `validate-pipeline-config.js --self-test`（任意コンポーネント。採否は HOWTO Part B-6） |
| `test-traceability` | `check-test-traceability.js --self-test` と本検査（受け入れ基準 → テストの写像）、および `check-test-spec-coverage.js --self-test` と本検査（#510 / IADR-0130。実在するテスト → テスト仕様書の記載） |
| `unit-dependencies` | `check-unit-dependencies.js --self-test` と本検査（#231 / IADR-0057） |
| `realm-constraints` | `check-realm-constraints.js --self-test` と本検査（#18 / #307 / #385） |
| `bff-downstreams` | `check-bff-downstreams.js --self-test` と本検査（#342 / IADR-0089） |
| `unit-service-ownership` | `check-unit-service-ownership.js --self-test` と本検査（#407 / IADR-0107） |
| `cpm-versions` | `check-cpm-versions.js --self-test` と本検査（#467。CPM のバージョン直書き禁止） |
| `contract-schema` | `check-contract-schema.js --self-test` と本検査（#465 / IADR-0122。`Shared.Contracts` の後方互換） |
| `frontend.yml` の `build-test` | `check-i18n-catalogs.js`（＋ `pnpm run i18n` の再生成差分）と `check-static-egress.js --require …`（#496 / IADR-0125）。`ci.yml` の `scripts-tests` は両者の `--self-test` と実データ検査を `scripts.repo.test.js` 経由で走らせる |
| `k8s-local-up-smoke` | `k8s-local-up.test.js`（#334 / IADR-0087・要 bash） |
| `scripts-tests`（再掲） | `check-test-spec-coverage.js` の `--self-test` と**実データの本走**（#510 / IADR-0130）。上の `test-traceability` の専用ステップと**二重に走る**——専用ステップは失敗をジョブ名で見せ、companion 側は `.github/workflows/` が編集できない環境（GitHub App 権限）でも検査が外れないことを担保する（`check-i18n-catalogs.js` の実データ検査と同じ結線） |

> `scripts.test.js` を CI に載せないと「誰かが手で叩いたときだけ走るテスト」になる。
> 実際に、CHANGELOG 生成が全面的に壊れる回帰が PR の CI をすべて green のまま通り抜けたことがある
> （`changelog.yml` は push でしか起動しないため、壊れるのはマージ後）。

### リポジトリ固有の Actions を足す場所

`action-versions.json` は**キットが配布する下限表**であり、キットの更新のたびに差し替わる。
本リポジトリだけが使うアクション（デプロイ系・クラウド系など）を同ファイルへ直接追記すると、
`scripts.test.js` と同じく**バイト一致が崩れ、以後の同期で毎回手動マージが要る**。

固有の下限は **`scripts/action-versions.repo.json`** に置く。存在すれば `expected` / `$exempt`
をマージして読む（無ければ何もしない）。

```json
{
  "$comment": "本リポジトリ固有のアクション。キットの action-versions.json は編集しない。",
  "expected": { "azure/setup-helm": 5 },
  "$exempt": { "some/action": "タグ形式がメジャーを持たないため" }
}
```

追記しないと `… は action-versions.json に無いため下限を検査していない` の警告が
**アノテーションとして毎回出続ける**。常時出る警告は「読まなくてよいもの」として学習され、
`ci-annotate` を入れた目的（緑ジョブに埋もれる警告の可視化）そのものを損なう。

> **現状、本リポジトリに companion は不要である。** 使用中の 10 アクションはすべてキットの
> 下限表に載っており（`github/codeql-action` は `$exempt`）、警告はゼロである。空の companion を
> 置くと「書き忘れ」として `warning:` が出るため、固有アクションを導入するまで作成しない。

| 状態 | 挙動 |
| --- | --- |
| companion なし | 何もしない（キット既定） |
| `expected` / `$exempt` が両方とも空 | `warning:`（書き忘れの検出） |
| JSON として壊れている | **失敗**（黙って無視すると「置いたのに効かない」状態になる） |
| キットの下限を**下げて**いる | `warning:`（退行を検出できなくなる方向の変更のため） |
| git 未追跡 | `warning:`（CI に存在せず、追記した下限が効かない） |

> **このファイルも必ずコミットする。** 理由は `scripts.repo.test.js` と同じである。

### リポジトリ固有のテストを足す場所

`scripts.test.js` は**キットが配布する共通テスト**であり、キットの更新のたびに差し替わる。
自前スクリプトの検査を同ファイルへ直接追記すると、同期のたびに手動マージが要り、
キットが同じテストを取り込んだ際に重複も生じる（重複はテストが落ちないため気付きにくい）。

固有テストは **`scripts/scripts.repo.test.js`** に置く。`scripts.test.js` が存在すれば自動で
読み込む（無ければ何もしない）。これにより `scripts.test.js` をキットとバイト一致に保て、
同期は上書きコピー 1 回で済む。

```js
// scripts/scripts.repo.test.js
module.exports = ({ ok, assert }) => {
  ok('本リポ固有の検査', () => {
    assert.ok(true);
  });
};
```

`ok` をそのまま受け取るため、件数の集計は自動で正しくなる（カウンタが分かれない）。

> **このファイルは必ずコミットする。** 追跡されていないと CI（clean checkout）に存在せず、
> 固有テストが黙って走らなくなる。`scripts.test.js` は未追跡を検出して警告するが、
> `.gitignore` を確認しておくこと。
> `.local` を名前に使わないのは、多くのプロジェクトで「コミットしない」の目印だからである
> （キット自身も `CLAUDE.local.md` をその意味で使っている）。旧名 `scripts.local.test.js` は
> 移行のあいだ読み込むが、改名を促す警告を出す。

**消失を検出したい場合**（固有テストを持つリポジトリ向け）: `ci.yml` の `scripts-tests` ジョブで
`REQUIRE_REPO_TESTS=1` を設定すると、companion が見つからないときに失敗する。未設定だと
誤削除やマージ事故でテスト件数が静かに減るだけで CI は green のままになる。
**companion があるのに未設定の場合は `notice:` で促す**（失敗はさせない）。

`scripts.test.js` が検出して知らせる状態は以下のとおり。

| 状態 | 挙動 |
| --- | --- |
| companion なし | 何もしない（キット既定） |
| companion なし ＋ `REQUIRE_REPO_TESTS=1` | **失敗**（消失の検出） |
| companion あり・登録 0 件 | **失敗**（export 忘れ・空実装・全件 skip） |
| companion あり ＋ `REQUIRE_REPO_TESTS` 未設定 | `notice:` で設定を促す |
| companion が git 未追跡 | `warning:`（CI に存在せず固有テストが走らないため） |
| 旧名 `scripts.local.test.js` のみ | 読み込む ＋ `warning:` で改名を促す |
| 新旧が**両方**ある | 新名を優先して読み込み、`warning:` で旧名の残存を知らせる（移行漏れならテストを移し、不要なら削除する） |

## 契約の破壊的変更（`Shared.Contracts`）

サービス間契約の後方互換は `check-contract-schema.js` が CI（`contract-schema` ジョブ）で機械検査する。
方式と分類の決定は [IADR-0122](../docs/adr/IADR-0122_contract-schema-source-and-compat-gate.md)。

### 非破壊の変更（フィールド追加など）

1. 契約を変更する（**新しいフィールドには既定値を付ける**。既定値が無い追加は破壊的として扱われる）。
2. `node scripts/check-contract-schema.js --update` を実行する。
3. 更新された `contract-schema-baseline.json` を同じ PR にコミットする。

非破壊でも一度 CI が赤くなるのは意図的である。契約の変更を**必ず PR の diff に載せる**ことが本ゲートの
主眼であり（「両側同時更新で気付かれない」失敗を止める）、baseline の差分がレビューの対象になる。

### 破壊的な変更（削除・型変更・必須化・並べ替え・enum/const 値の変更・属性の変更）

**まず互換を保つ道を検討する**（新フィールドは既定値付きで足す／削除ではなく非推奨のまま残す／
新しいイベント型・API バージョンを足して移行期間を設ける。計画 `06_technical/10_composability-design` §3
「後方互換の追加のみ許可、削除・意味変更は新バージョン＋移行期間」）。

そのうえで破壊が必要なら、次の手順で**承認の記録を残して**通す。

1. `node scripts/check-contract-schema.js` の失敗出力から `key:` の行をコピーする。
2. `contract-breaking-allowlist.json` の `approvals` へ書く（**5 項目すべて必須**）。

   ```json
   {
     "key": "memberRemoved:Knowledge.Contracts.Events.IngestionCompleted.ChunkCount",
     "reason": "なぜ壊すか（互換を保てない理由・移行の段取り）",
     "approvedBy": "承認者",
     "issue": "#123",
     "date": "2026-08-04"
   }
   ```

3. `node scripts/check-contract-schema.js --update` を実行する。承認エントリは
   `contract-schema-baseline.json` の `$acceptedBreakingChanges` へ**移され**、allowlist は空へ戻る。
4. 更新された 2 ファイルを同じ PR にコミットする（承認の記録は baseline 側に残り、git 履歴で追える）。

`--update` は**未承認の破壊的変更があると baseline を更新しない**。承認を書かずに通す道は無い。
逆に、対応する変更が無い承認が allowlist に残っていれば検査は fail する（承認だけが残ると次の破壊的
変更を黙って通すため）。既存 ratchet 群（`backend-library-baseline.json` /
`test-traceability-allowlist.json` / `coverage-floor.json`）と同じ 3 判定である。

## 自動生成（CI）

- `.github/workflows/doc-links-planning.yml`: private な planning サブモジュール込みのリンク検査（#232 / IADR-0058）。夜間 + `workflow_dispatch` でトークン付き（Secret `PLANNING_REPO_TOKEN`。本リポジトリと planning 双方へ read 権限を持つ fine-grained PAT 推奨）に submodule を取得し、`check-doc-links.js --require-planning` を実行する。本体 `ci.yml` の `doc-links` は高速・トークン不要のまま非 planning リンクを毎 PR 検査する。
- `.github/workflows/image-mapping.yml`: `check-image-mapping.js`（`--self-test` ＋実チェック）を毎 PR/push で実行し、`MAPPING` と compose の `build` 定義のドリフトをマージ前に落とす（#275 / IADR-0068）。`ci.yml` には足さない（独立ワークフロー方針）。Node のみで docker 不要のため paths フィルタなしで常に結果を報告する。
- `.github/workflows/ci.yml` の `k8s-local-up-smoke` ジョブ: `k8s-local-up.test.js` を実行し、`k8s-local-up.sh` の opt-in ゲート（`PERSIST`/`OBSERVABILITY`/`VAULT`/`ARGOCD`/`HEADLAMP`/`LOCALEDGE`/`ESO`）を横断で固定する（#334 / IADR-0087）。あわせて **apiserver へ OIDC フラグを付与しない**ことを回帰として固定する（#399 / IADR-0105。旧 `HEADLAMP_OIDC_APISERVER` は除去済み・指定しても no-op）。外部バイナリを PATH 上の記録スタブへ差し替え、副作用ゼロでスクリプトを実行し発行コマンド列を検証する（実クラスタは作らない・Node + bash のみ）。
- `.github/workflows/changelog.yml`: `main` への push で CHANGELOG を再生成しコミットする。タグ push でリリースノートも生成する。
- `.github/workflows/openapi.yml`: OpenAPI を生成する。コードからの生成コマンド（`scripts/generate-openapi.sh` または変数 `OPENAPI_GENERATE_CMD`）が設定されていればそれを実行し、無ければ通信仕様書からの雛形生成にフォールバックする（「生成可能なら必ず生成」）。

> OpenAPI をコードから生成する場合は `scripts/generate-openapi.sh` を用意する（例: `dotnet swagger tofile ...` / `npx ...`）。未整備でも雛形は通信仕様書から生成される。
