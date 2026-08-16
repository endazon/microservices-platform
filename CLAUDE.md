# CLAUDE.md — 実装作業リポジトリ

このリポジトリは、上流工程リポジトリ（`project-planning`）で確定した計画書を**実装する**ための作業リポジトリである。Claude はこのファイルを毎セッション読み込む。指示は具体・簡潔に保つ。

> **技術スタックに依存する規約とフォルダ構成は、末尾の「技術スタック別ルール」へ追記する**（本ファイルは `impl-handoff-kit` のテンプレート由来）。
>
> **最初に `AI_SETUP.md` を読む**。利用可能な AI（Claude Code サブスク / Anthropic API / GitHub Copilot）の宣言と、有効化するファイル・シークレットがそこで決まる。

## 目的

- 計画書（要求・ユースケース・画面・技術検討・ADR）に忠実に実装する
- 計画と実装の**トレーサビリティ**（追跡可能性）を保つ
- 生成 AI を活用しつつ、人間がレビューできる変更単位を維持する
- **リポジトリの位置づけ（主たる成果物と付随成果物の主従）を README 冒頭で明示し、計画書（ビジョン・ADR）と一致させ続ける**（位置づけの漂流は実装・文書の齟齬の温床になる）

## 計画書の参照

- 計画リポジトリ `project-planning` を **git submodule** もしくは**隣接クローン**として参照する。既定パスは `../project-planning`（submodule の場合は `planning/`）。
- 計画書は `projects/<name>/00_vision 〜 07_adr` に格納されている。各 ID の意味は以下。
  - `FR-xx`: 機能要求（`02_requirements/`）
  - `NFR`: 非機能要件（`02_requirements/`）
  - `UC-xx`: ユースケース（`03_usecases/`）
  - `SC-xx`: 画面（`05_screens/`）
  - `ADR-xxxx`: 意思決定記録（`07_adr/`）
- 最新の計画書サマリは `/sync-plan` で `.ai-context/` に再生成する。実装着手前に該当 ID の計画書を必ず読む。

## 実装の進め方（AI 活用の基本フロー）

実装の起点となる ID（FR/UC）が与えられたら、**まず仕様書を作成してから**、以下の順で進める。

1. **計画書を読む**: 対象の要求・ユースケース・画面設計を読み、受け入れ基準を把握する。
2. **ADR 制約を確認する**: 関連する ADR を読み、確定済みの技術・設計上の制約に違反しないことを確認する。曖昧な場合は実装を止め、人間に確認する。
3. **仕様書を作成する（必須・着手前）**: `docs/specs/<YYYYMMDD>_<概要>.md` に作業仕様書を作成する（`/new-spec`）。以降の実装は必ずこの仕様書に沿って進める。仕様書なしで実装へ着手しない。該当する必須仕様書（機能/画面/通信/データ/技術/テスト/運用/セキュリティ）を作成・更新し、重要な実装判断は実装ADR（`IADR`）に残す（後述「仕様書」参照）。
4. **タスクに分解する**: 影響範囲・必要なテストを洗い出す（`/plan-to-tasks` を活用）。
5. **実装する**: 仕様書・計画書に忠実に実装する。計画外の機能追加・過剰な抽象化を行わない。
6. **テストを書く**: 受け入れ基準をテストケースへ写像する（`test-author` エージェントを活用）。
7. **検証する（完了前）**: `/verify` でビルド・テスト・lint を実行し、受け入れ基準と `docs/DEFINITION_OF_DONE.md` を満たすことを確認する。
8. **トレーサビリティを残す**: 後述の規約に従い、起点 ID をブランチ名・コミット・コード・PR に残す。
9. **計画へ環流する**: 実装中に計画書の誤り・不足・新たな制約を見つけたら、`/plan-feedback` で計画リポジトリへフィードバックする。

## 実装作業の進め方（計画リポの運用ガイド）

実装作業の運用標準（フェーズ分割・並列実装・監査・裁定・メタ作業の統制）は [`planning/docs/ai-implementation-workflow-guide.md`](planning/docs/ai-implementation-workflow-guide.md)（planning#294 で新設、planning#298 で 2026-08-08 に fixed へ確定。**更新日 2026-08-15**）を正本とする。拘束点の要約:

- **並列作業は宣言済みファイル領域の非重複で機械的に判定**し、交差する issue は直列化する。**マージは FIFO で 1 本ずつ**（develop へ rebase → CI 通過 → マージ → 次の PR が rebase）
- **原則は 1 issue = 1 PR**（[IADR-0116](docs/adr/IADR-0116_reimplementation-branching-and-pr-policy.md) 規約 1）。**束ねてよいのは「裁定済みの同型な契約追加」だけ**で、[IADR-0139](docs/adr/IADR-0139_domain-bundled-contract-prs.md) 決定 1 の **6 条件をすべて満たすとき**に限る。**判定の単位はドメインではなく資源**（同じ API 資源または同じ DTO 群に閉じること）、**束の上限は 4 件**（planning#370）。条件を満たさないものは束ねない（射程の正は上流ガイド §2。本リポで広げるには改定 IADR が要る）
- **フェーズ末監査は書いたエージェントと別の、フレッシュな文脈のエージェント**に diff と受け入れ基準だけを渡して行い、**証跡（実行コマンドと出力）必須**。宣言だけの監査は不合格
- **裁定依頼は小さく高頻度**に計画リポへ流す（`decision-needed` ラベル）。**blocked（AI だけでは完結しない）判定は棚卸しごとに再検証**する（恒久制約への誤分類が実測で 3 件）
- **検査器・規約の追加は「同型の事故が 2 回起きたら」**を条件とする（1 回目は記録に留める）。**毎セッション必読の規約は総量 50KB 予算**（#623 で到達。**予算内に保つ**）
  - **母集合はエージェントごとに分けて測り、合算しない**（裁定 planning#364。Claude は `CLAUDE.md` ＋ `.claude/rules/*.md` を読み `AGENTS.md` は読まない）。**予算値 51,200 バイト**（正本: 運用ガイド §8。`scripts/check-reading-budget.js` が出典つきの複製を持ち 100% 超 fail / 90% 以上 warn）。`.claude/rules/` は列挙せず走査、submodule 配下は入れない。`AGENTS.md` 系は同値を暫定流用し超過は fail にしない。測定: `cat CLAUDE.md .claude/rules/*.md | wc -c`／`wc -c < AGENTS.md`（別枠）
- **人間の関与はフェーズ計画の承認・フェーズ末監査結果のサンプリング確認・裁定の 3 点**（＋レビュー完了の required check 配備までは**マージ操作**を加えた 4 点）
- **複数実装リポのパリティ維持**（§11）: **運用装備の配布点は kit に一本化**（直した装備は kit へ環流し他リポへ追随 issue）。**同時起票 issue は 7 日後に相互のクローズ状態を突合**し片側のみ完了なら追随 issue。**突合観点は 6 種**（強制化装備 / ラベル体系 / 必読規約の総量 / pin の鮮度 / kit 追随の未消化数 / 定期監査・レビュー基盤の稼働）。**定期監査は「最後に結果を産出した日時」で稼働を確認**する

## トレーサビリティ規約

実装と計画書を相互に追跡できるよう、起点となる ID を以下の箇所に残す。

- **ブランチ名**: `feat/FR-012-<概要>` のように起点 ID を含める。
- **コミットメッセージ**: 先頭に種別と ID を付ける。例: `feat(FR-012): ログイン画面のバリデーションを実装`。
- **コード**: 計画書由来の実装には、該当箇所のコメントに ID を残す。例: `// FR-012, UC-03: 入力バリデーション`。
- **PR**: PR テンプレートの該当欄に実装した FR/UC・関連 ADR・受け入れ基準のチェックを記入する。
- 詳細な書式は `.claude/rules/traceability.md`（キット配布物）と `traceability.repo.md`（本リポ固有）を参照（自動適用）。

## 仕様書（docs/）

計画書（`project-planning` の上流ドキュメント）を実装向けに詳細化した仕様書を `docs/` に置く。`/new-spec <種別> <ID|topic>` で作成する。各仕様書には起点 ID（FR/UC/SC/ADR）と計画書リンク、関連仕様書への相互リンクを必ず記入する（**リンクの義務は仕様書側の一方向。ADR 側に逆リンクを張る義務は無い**。正本は `docs/README.md` 運用ルール 4 / [IADR-0171](docs/adr/IADR-0171_backlink-obligation-one-way.md)）。

**種別の一覧（必須 10 / 任意 9）と出力先・粒度は [`docs/README.md`](docs/README.md) が正本である。**
**ここへ複写しない** —— 2 箇所に置くと片方が古くなる（[IADR-0141](docs/adr/IADR-0141_audit-rounds-and-population-drawing.md)。
運用ルール 4 で既に採っている扱いと同じ。[IADR-0171](docs/adr/IADR-0171_backlink-obligation-one-way.md)）。
**`type` の値域はテンプレート（`docs/templates/*.md`）が持ち、`node scripts/check-doc-type-vocabulary.js` が閉じる。**

- 詳細・計画書との対応は `docs/README.md` を参照。実装着手前に少なくとも作業仕様書を作成する。
- 重要な実装判断（内部設計・ライブラリ選定等）は**実装ADR（`docs/adr/`、`IADR-XXXX`）に必ず残す**。計画に影響する決定は `/plan-feedback` で計画側へ環流する（計画ADR `ADR-XXXX` と区別する）。

## 補助成果物の自動生成

補助成果物は生成可能なら必ず生成し、CI で自動更新する（`scripts/` ＋ `.github/workflows/`）。

- **CHANGELOG**: コミット履歴（`種別(起点ID): 要約`）から `CHANGELOG.md` を生成（`changelog.yml`）。タグ push でリリースノートも生成。
- **OpenAPI**: コードからの生成コマンドがあればそれを、無ければ通信仕様書から雛形を生成し `docs/api/openapi.yaml` を更新（`openapi.yml`）。

## 生成 AI の活用

- 実装・レビュー・テスト生成にサブエージェントとスラッシュコマンドを活用する。一覧は `.claude/agents/` `.claude/commands/` を参照。
- GitHub 上では `@claude` メンションで Issue/PR に AI を呼び出せる（`.github/workflows/claude-coding.yml`。既定は `.example`。`AI_SETUP.md` のプロファイルで有効化する）。PR には自動 AI レビューが走る（`claude-code-review.yml`）。
- 他の AI（Cursor / Codex / GitHub Copilot）を使う場合も、本ファイルおよび `AGENTS.md` の方針（特にトレーサビリティ最優先）に従う。Copilot 固有の運用は `.github/copilot-instructions.md` と `AI_SETUP.md` を参照。
- **実装を AI に任せる前提の運用全体（起票→実装→検証→レビュー→マージ）と推奨ツールは `docs/ai-workflow.md` を参照する。**

## 自動化・検証・安全

実装の大半を AI に委ねるための仕組みを備える。

- **ガードレール（hooks）**: `.claude/hooks/` が破壊的コマンド（`guard-bash.js`）・秘密情報の混入（`guard-secrets.js`）をブロックし、仕様書なし実装やフロントマター欠如を警告（`check-impl.js`）する。
- **完了前検証**: `/verify` でビルド・テスト・lint を実行し、受け入れ基準と `docs/DEFINITION_OF_DONE.md` を満たすことを確認してから PR を出す。
- **再現可能な環境**: `.devcontainer/` と `scripts/setup.sh`（SessionStart hook が実行）で、AI がビルド・テストを実走できる環境を用意する。
- **CI ゲート**: `ci`（lint/build/test/coverage）・`security`（gitleaks/dependency-review）を必須チェックにし、ブランチ保護でマージを制御する（対象と check 名の正は `docs/ai-workflow.md` の表。`codeql` は #719 で `paths:` 付きになったため必須にしない）。

## Git 運用

- `main` を安定版とし、直接コミットしない。作業ブランチ → プルリクエスト経由でマージする。
- 1 コミット = 1 論理変更。コミットメッセージ先頭に種別（`feat:` `fix:` `refactor:` `test:` `docs:` `chore:` 等）と起点 ID を付ける。
- 破壊的な git 操作（force push, `reset --hard`）は行わない。

## 禁止事項

- **仕様書（`docs/specs/`）を作成せずに実装へ着手すること**。
- 計画書（特に fixed / Accepted）に反する実装。差異が必要な場合は、新 ADR または計画リポへの変更提案（`/plan-feedback`）で根拠を残す。
- ADR で確定した制約（技術スタック・アーキテクチャ等）の無断逸脱。
- 機密情報（個人情報・認証情報）のコミット。個人設定は `CLAUDE.local.md`（gitignore 推奨）へ。
- 計画外の大規模リファクタ・過剰な抽象化・起こり得ないケースへの防御的実装。

---

## 技術スタック別ルール

本リポジトリはマイクロサービスのモノレポである。**主たる成果物は platform ユニット（基盤）であり、
knowledge ユニット（ナレッジ機能）は付随する可変機能セット**である（issue #209 / IADR-0056）。
`src/<unit>/{backend,frontend}` のユニット構成・依存規則・submodule 追加手順は
[`src/README.md`](src/README.md) を参照。共通設定の単一情報源（`src/Directory.Build.props` /
`src/Directory.Packages.props` / `src/vitest.config.ts` 等）を尊重し、個別プロジェクトで上書きしない。

### C# / .NET（バックエンド `src/<unit>/backend/`）

- **ターゲット**: `.NET 10` / `C# 13`（`LangVersion 13`）。設定の単一情報源は [`src/Directory.Build.props`](src/Directory.Build.props)。個別 `.csproj` で `TargetFramework` を上書きしない。`global.json` は SDK `8.0.0` + `rollForward: latestMajor`（新しい SDK でビルド可）。
- **言語設定**: `Nullable` / `ImplicitUsings` は有効（props で既定 ON）。null 許容警告を握り潰さない。
- **パッケージ**: Central Package Management。バージョンは [`src/Directory.Packages.props`](src/Directory.Packages.props) に集約し、`.csproj` の `PackageReference` にはバージョンを書かない。
- **ソリューション**: 新形式 `.slnx` をユニット毎に持つ（[`src/platform/backend/backend.slnx`](src/platform/backend/backend.slnx) / [`src/knowledge/backend/backend.slnx`](src/knowledge/backend/backend.slnx)。ルート集約ソリューションは置かない）。プロジェクト追加時は所属ユニットの slnx に登録する。
- **命名規約**: 公開メンバは PascalCase、ローカル変数・引数は camelCase、private フィールドは `_camelCase`。
- **ビルド/テスト**: `dotnet build <unit>/backend/backend.slnx` / `dotnet test <unit>/backend/backend.slnx` が両ユニットで通ること。テストは **xUnit**。受け入れ基準は `[Fact]`/`[Theory]` に写像する。
- **フォーマット**: `dotnet format <slnx>` で整形（CI の `lint` ジョブが両ユニットに `--verify-no-changes` を強制）。
- **サービス境界**: サービス間は直接参照せず、`Shared.Contracts` の契約と HTTP（Refit）/ メッセージング（**Wolverine**。ADR-0027 / ADR-0030。MassTransit は不採用で、既存参照は `scripts/backend-library-baseline.json` の ratchet 管理下にあり新規混入は CI で fail する）で疎結合に保つ。ユニット外参照は `src/platform/backend/Shared/` の 3 プロジェクトのみ許可（IADR-0117 で 2 → 3 へ改定。`Platform.Shared.Kernel` は未作成。platform → 可変ユニットは禁止）。

### TypeScript / React（フロントエンド `src/<unit>/frontend/`）

- **スタック**: **React 19** + TypeScript 5.6 + **Vite 6**（ESM, `"type": "module"`）。テストは **Vitest 3**。Node は CI と揃え **22** を使う。ADR-0031 が確定したスタックへの移行は [IADR-0121](docs/adr/IADR-0121_spa-stack-migration-staging.md) が段に分割して管理する（**段の進捗は同 IADR が正本。ここへ書かない**——進捗は最も速く腐る）。ルーティングは **TanStack Router**（[IADR-0124](docs/adr/IADR-0124_tanstack-router-unit-composition.md)。`react-router-dom` は platform / knowledge から撤去済みで、再混入は ESLint が止める）。
- **構成**: pnpm workspace（ルート = `src/`、`pnpm-workspace.yaml` = `'*/frontend'` + `'packages/*'`。IADR-0121）。`platform/frontend`（foundation + アプリホスト）と `knowledge/frontend`（画面 features）を分離する（[IADR-0121](docs/adr/IADR-0121_spa-stack-migration-staging.md) が [IADR-0033](docs/adr/IADR-0033_frontend-spa-foundation.md) を Superseded / [IADR-0056](docs/adr/IADR-0056_repo-unit-structure-platform-knowledge.md)）。import はエイリアス `@foundation` / `@features`（合成点） / `@knowledge` を使う。
- **サーバー状態**: **TanStack Query** に一元化する（`foundation/api/queryClient.ts` が唯一の生成点）。**グローバルストア（Redux）は持たない**——`redux` / `@reduxjs/*` の import は ESLint が error にする（IADR-0121 決定 8）。
- **UI / CSS**: **Tailwind CSS v4** ＋ 共有 UI パッケージ **`@platform/ui`**（[`src/packages/ui`](src/packages/ui/README.md)。IADR-0121 決定 4 / IADR-0125 決定 1）。入れてよいのはデザイントークン・`cn()`・shadcn/ui 派生プリミティブのみで、ドメイン・通信・ルーティング・認証・**表示文言**は入れない（文言を持つと i18n の入口が 2 つに割れる）。公開面は `src/index.ts` の 1 ファイルで、深い参照は ESLint が禁止する。**外部 CDN・Web フォント・analytics を使わない**（08_data-egress-policy。フォントはシステムフォント、アイコンは npm 同梱の lucide-react）。この禁止は**ビルド成果物を走査して機械検査する**（`node scripts/check-static-egress.js --require <dist>`）。状態表示は**色だけで意味を持たせない**（色 ＋ アイコン ＋ テキスト。INDEX 決定 21。`StatusBadge` / `Alert` / `notify` が API で強制する）。
- **i18n**: **Lingui（ja / en）**。マクロ（`@lingui/core/macro` の `msg`）を babel で展開する設定は **`src/vitest.config.ts` と `src/platform/frontend/vite.config.ts` の両方**に置く（片方だけだと静かに割れる）。カタログ（`platform/frontend/src/foundation/i18n/locales/<locale>/messages.{po,ts}`）は **orval 生成物と同じくコミットし、`pnpm run i18n` の再生成差分を CI が検査する**。未翻訳キーは `node scripts/check-i18n-catalogs.js`（全ロケールの `msgstr` 非空）が止める——**再生成差分検査だけでは未翻訳を検出できない**（IADR-0125 決定 4 の実測）。表示言語はブラウザ設定から決め、**切替 UI は持たない**（計画に要素が無い）。テストは Vitest 側を `src/test/setup.ts`、Playwright 側を `playwright.config.ts` の `use.locale` で **ja に固定**する。
- **コンポーネントカタログ**: **Storybook**（`src/packages/ui/.storybook/`。`pnpm --filter @platform/ui run build-storybook`）。対象は `@platform/ui` のプリミティブのみで、画面（features）は入れない。テレメトリとクラッシュレポートは無効化する（08_data-egress-policy）。
- **BFF 境界**: バックエンドへは必ず `/bff/*` 経由。呼び出しは **orval 生成フック**（`pnpm run codegen`。入力は `docs/api/openapi.yaml` の `/bff/` 配下のみ・**生成物はコミット**し CI が再生成差分を検査）か `foundation/api` の `apiFetch` / `apiStream` を使う。**手書き HTTP クライアントは禁止**で、`foundation/api` 以外での `fetch` / `XMLHttpRequest` / `EventSource` と `axios` 等の import は ESLint が error にする。接続先はビルドに焼き込まず実行時 config（`platform/frontend/public/config.js`）で注入する。フロントから各サービスを直接叩かない。
- **認証**: `oidc-client-ts`（Authorization Code + PKCE）で Keycloak public client `platform-spa` を用いる。トークンやシークレットをコードに埋め込まない。**ADR-0032 の BFF セッション方式へ移行予定**（移行第 3 段 / #439。BFF 側が未実装のため、それまでは現行方式を維持する。IADR-0121 決定 6）。
- **Lint / 型**: ESLint flat config（[`src/eslint.config.js`](src/eslint.config.js)）+ typescript-eslint。`src/` で `pnpm run lint` / `pnpm run typecheck` が通ること。
- **フォーマット**: `pnpm run format` で整形（設定は [`src/.prettierrc.json`](src/.prettierrc.json)）。**CI の [`frontend.yml`](.github/workflows/frontend.yml) の `lint` 相当ジョブが `pnpm run format:check` を強制する**（C# 側の `dotnet format --verify-no-changes` と同じ役割。issue #562）。**対象範囲の単一情報源は [`src/.prettierignore`](src/.prettierignore)** であり、グロブを `package.json` やワークフローへ複写しない。バックエンド・`src/ai-stock-trading`（別リポの submodule）・生成物（orval / Lingui カタログ）・`*.md` は対象外。
- **テスト**: 単体は **Vitest**（jsdom）+ Testing Library、E2E は **Playwright**。テストは実装と同居し `*.{test,spec}.{ts,tsx}`。受け入れ基準をテストケースへ写像する。
- **カバレッジ**: `pnpm run test:coverage`（v8 provider）。[`src/vitest.config.ts`](src/vitest.config.ts) の `coverage.thresholds` は**回帰防止のラチェット**（全ユニット横断で計測）。テストを増やしたらしきい値を引き上げ、床を割る変更は CI（[`frontend-tests.yml`](.github/workflows/frontend-tests.yml)）で止める。

### CI（GitHub Actions）

- バックエンドは [`ci.yml`](.github/workflows/ci.yml)（ユニット毎に restore/build/test/format）、フロントは [`frontend.yml`](.github/workflows/frontend.yml)（typecheck/lint/build/e2e）と [`frontend-tests.yml`](.github/workflows/frontend-tests.yml)（単体テスト＋カバレッジ）に分離する。フロント用ジョブは `paths: ["src/*/frontend/**", ...]` で各ユニットの frontend 変更時のみ起動し、両スタックの CI を独立させる。
- `.github/workflows/` は**編集できる**（[IADR-0169](docs/adr/IADR-0169_cross-repo-ref-scan-beyond-markdown.md) が `git log` で実測。従前ここには「GitHub App 権限では編集不可」と書いてあったが**誤りである**）。ワークフローを変更したら、**その変更で起動条件・必須チェックが変わらないか**を確かめること。
