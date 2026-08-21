# CLAUDE.md — 実装作業リポジトリ

このリポジトリは、上流工程リポジトリ（`project-planning`）で確定した計画書を**実装する**ための作業リポジトリである。Claude はこのファイルを毎セッション読み込む。指示は具体・簡潔に保つ。

> **技術スタックに依存する規約とフォルダ構成は、末尾の「技術スタック別ルール」へ追記する**（本ファイルは `impl-handoff-kit` のテンプレート由来。**キットは bootstrap 専用であり、既存リポジトリに追随義務は無い**。ADR-0048 決定 6）。
>
> **最初に `AI_SETUP.md` を読む**（利用可能な AI の宣言・有効化・シークレットの正本）。

## 目的

- 計画書（要求・ユースケース・画面・技術検討・ADR）に忠実に実装し、計画と実装の**トレーサビリティ**（追跡可能性）を保つ
- 生成 AI を活用しつつ、人間がレビューできる変更単位を維持する
- **資料の主従をディレクトリ構造で示す**（ADR-0048 決定 1）—— **`docs/` ＝人が読む生きた文書**、**`.ai-context/` ＝ AI 向け文脈資料・凍結記録**（実装ADR・作業仕様書・superpowers。本文プロズを後から書き換えない）。[`.ai-context/README.md`](.ai-context/README.md) 参照
- **リポジトリの位置づけ（主たる成果物と付随成果物の主従）を README 冒頭で明示し、計画書（ビジョン・ADR）と一致させ続ける**（位置づけの漂流は実装・文書の齟齬の温床になる）

## 計画リポジトリとの関係

- **計画書と裁定の記録は `project-planning` の `projects/<name>/`（`00_vision` 〜 `07_adr`）にある。** **各 ID（`FR` / `NFR` / `UC` / `SC` / `ADR` / `IADR`）の意味は `.claude/rules/traceability.md`「起点 ID の種別」が、レンジは `traceability.repo.md` が正本**であり、ここへ複写しない（同じ必読集合の中で二重に持たない）。実装着手前に該当 ID の計画書を必ず読む。
- **本リポジトリは planning に依存しない（submodule は張らない）**（ADR-0048 決定 2・[IADR-0228](.ai-context/adr/IADR-0228_planning-dependency-removal.md)）。参照は GitHub 上の URL を直接開くか、隣接クローン（既定パス `../project-planning`。**読み取り専用・pin 固定なし**）で行う。
- **計画への指摘（誤り・不足・新たな制約）は project-planning の GitHub issue で起票する**（`feedback.yml` テンプレート・`feedback` / `decision-needed` ラベル。ADR-0048 決定 5。手順は `/plan-feedback`）。**起票前に同件の既存 issue を必ず検索する。** **裁定の完了記録は planning 側 `projects/<name>/10_feedback/` に残り、本リポジトリには残さない**（`feedback/` は撤去済み）。
- **新規の実装ADR（IADR）・作業仕様書の起草は従来どおり本リポジトリ内 `.ai-context/` で行う**（ADR-0048 決定 7）—— 実装判断の記録は実装変更と同一 PR に置く。

## 実装の進め方（AI 活用の基本フロー）

実装の起点となる ID（FR/UC）が与えられたら、**まず仕様書を作成してから**、以下の順で進める。

1. **計画書を読む**: 対象の要求・ユースケース・画面設計を読み、受け入れ基準を把握する（参照手段は前掲「計画リポジトリとの関係」）。
2. **ADR 制約を確認する**: 関連する ADR を読み、確定済みの技術・設計上の制約に違反しないことを確認する。曖昧な場合は実装を止め、人間に確認する。
3. **仕様書を作成する（必須・着手前）**: `.ai-context/specs/<YYYYMMDD>_<概要>.md` に作業仕様書を作成する（`/new-spec`）。以降の実装は必ずこの仕様書に沿って進める。**仕様書なしで実装へ着手しない。** 該当する必須仕様書と実装ADR も併せて作成・更新する（後述「仕様書」）。
4. **タスクに分解する**: 影響範囲・必要なテストを洗い出す（`/plan-to-tasks` を活用）。
5. **実装する**: 仕様書・計画書に忠実に実装する。計画外の機能追加・過剰な抽象化を行わない。
6. **テストを書く**: 受け入れ基準をテストケースへ写像する（`test-author` エージェントを活用）。
7. **検証する（完了前）**: `/verify` でビルド・テスト・lint を実行し、受け入れ基準と `docs/DEFINITION_OF_DONE.md` を満たすことを確認する。
8. **トレーサビリティを残す**: 後述の規約に従い、起点 ID を残す。
9. **計画へ環流する**: 実装中に計画書の誤り・不足・新たな制約を見つけたら、**project-planning へ GitHub issue を起票する**（`/plan-feedback`。前掲「計画リポジトリとの関係」）。

## 実装作業の進め方（計画リポの運用ガイド）

運用標準（フェーズ分割・並列実装・監査・裁定・メタ作業の統制）は project-planning の `docs/ai-implementation-workflow-guide.md` が正本である（参照手段は前掲）。拘束点の要約:

- **並列作業は宣言済みファイル領域の非重複で機械的に判定**し、交差する issue は直列化する。**マージは FIFO で 1 本ずつ**（develop へ rebase → CI 通過 → マージ → 次の PR が rebase）
- **原則は 1 issue = 1 PR**（[IADR-0116](.ai-context/adr/IADR-0116_reimplementation-branching-and-pr-policy.md) 規約 1）。**束ねてよいのは「裁定済みの同型な契約追加」だけ**で、条件・単位・上限は [IADR-0139](.ai-context/adr/IADR-0139_domain-bundled-contract-prs.md) 決定 1 が、**射程の正は上流ガイド §2** が持つ（ここへ複写しない。広げるには改定 IADR が要る）
- **フェーズ末監査は書いたエージェントと別の、フレッシュな文脈のエージェント**に diff と受け入れ基準だけを渡して行い、**証跡（実行コマンドと出力）必須**。宣言だけの監査は不合格
  - **★ `git log` / `git blame` を出典に引く前に `git rev-parse --is-shallow-repository` を確かめる**（planning#410）。**`true` なら出力は履歴の打ち切り位置を指し得るため出典に使えない**（境界コミットを「最後に触ったコミット」と記録した事故が実測されている）。**証跡の形式は満たすので、この作法が無いと止まらない。**
- **裁定依頼は小さく高頻度**に計画リポへ流す（`decision-needed` ラベル）。**blocked（AI だけでは完結しない）判定は棚卸しごとに再検証**する
- **検査器・規約の追加は「同型の事故が 2 回起きたら」**を条件とする（1 回目は記録に留める）。**毎セッション必読の規約は総量 50KB 予算**（#623 で到達。**予算内に保つ**）
  - **母集合はエージェントごとに分けて測り、合算しない**（Claude は `CLAUDE.md` ＋ `.claude/rules/*.md` を読み `AGENTS.md` は読まない）。**予算値・母集合・測り方の正は運用ガイド §8 と `scripts/check-reading-budget.js`**（CI が同じ母集合を測る）
- **kit との乖離は受容する**（ADR-0048 決定 6）。kit 同期のバイト一致検査は退役済みで、直した運用装備を kit へ環流する義務も追随 issue も無い。**乖離に気付いたら受容として記録する**

## トレーサビリティ規約

実装と計画書を相互に追跡できるよう、**起点となる ID をブランチ名・コミットメッセージ・コード内コメント・テスト・PR に残す**。

- **書式の正本は `.claude/rules/traceability.md`「残す箇所と書式」と `traceability.repo.md`（本リポ固有）である**（同ディレクトリの `*.md` は自動適用され、この CLAUDE.md と同じ必読集合に入る——例までここへ複写しない）。
- 🔴 **`docs/` 配下だけは書き方が違う。** 計画 ID（FR/UC/SC/ADR/NFR）・IADR・仕様書名・修飾付き issue 参照を**表示テキストへ書かず**、frontmatter 終端直後・H1 の直前の **trace ブロック**（HTML コメント。1 文書 1 個）へ非表示メタデータとして持つ（ADR-0048 決定 4）。**書式の正本は同決定、機械検査は `scripts/check-trace-blocks.js`**、書くときの実務は [`docs/traceability-appendix.md`](docs/traceability-appendix.md) §trace ブロック。**`.ai-context/` の凍結記録には適用しない**（本文にそのまま書く）。

## 仕様書（`docs/` と `.ai-context/`）

計画書を実装向けに詳細化した資料は、上記の主従に従い 2 箇所に分かれる（ADR-0048 決定 1）。`/new-spec <種別> <ID|topic>` で作成する。

- **着手前に必須**なのは**作業仕様書**（`.ai-context/specs/<YYYYMMDD>_<概要>.md`）である。重要な実装判断（内部設計・ライブラリ選定等）は**実装ADR**（`.ai-context/adr/`、`IADR-XXXX`）に必ず残す（計画ADR `ADR-XXXX` と区別する）。
- **種別の一覧（必須 10 / 任意 9）と出力先・粒度は [`docs/README.md`](docs/README.md) が正本である。ここへ複写しない**（2 箇所に置くと片方が古くなる。[IADR-0141](.ai-context/adr/IADR-0141_audit-rounds-and-population-drawing.md)）。**`type` の値域はテンプレート（`docs/templates/*.md`）が持ち、`node scripts/check-doc-type-vocabulary.js` が閉じる。**
- **`docs/` 配下で可視のリンクとして張ってよいのは同一リポジトリの `docs/` 内だけ**である（他は前節の trace ブロックへ）。**リンクの義務は仕様書側の一方向**で、ADR 側に逆リンクを張る義務は無い（正本は `docs/README.md` 運用ルール 4 / [IADR-0171](.ai-context/adr/IADR-0171_backlink-obligation-one-way.md)）。`.ai-context/` 配下は従来どおり本文に書く。

## 補助成果物の自動生成

補助成果物（`CHANGELOG.md` / `docs/api/openapi.yaml` 等）は**生成可能なら必ず生成し、CI で自動更新する**。**手で書き足さない。** 生成の実体は `scripts/` ＋ `.github/workflows/`（`changelog.yml` / `openapi.yml`）にある。

## 生成 AI の活用

- 実装・レビュー・テスト生成にサブエージェントとスラッシュコマンドを活用する。一覧は `.claude/agents/` `.claude/commands/` を参照。
- 他の AI（Cursor / Codex / GitHub Copilot）を使う場合も、本ファイルおよび `AGENTS.md` の方針（**特にトレーサビリティ最優先**）に従う。
- 役割スロット（orchestrator / worker / reviewer）の配役とフォールバック連鎖は `ai-roster.json` で宣言する。差し替えの契約・切り戻しの正本は `docs/ai-orchestration.md`（都度読み）。
- **運用全体（起票→実装→検証→レビュー→マージ）と推奨ツールは `docs/ai-workflow.md`、AI の有効化・認証は `AI_SETUP.md` が正本である**（GitHub 上の `@claude` 呼び出しと自動 AI レビューの配線もそちら）。

## 自動化・検証・安全

実装の大半を AI に委ねるための仕組みを備える。

- **ガードレール（hooks）**: `.claude/hooks/` が破壊的コマンド（`guard-bash.js`）・秘密情報の混入（`guard-secrets.js`）をブロックし、仕様書なし実装やフロントマター欠如を警告（`check-impl.js`）する。
- **完了前検証**: 上の手順 7（`/verify`）を**PR を出す前に**通す。
- **再現可能な環境**: `.devcontainer/` と `scripts/setup.sh`（SessionStart hook が実行）で、AI がビルド・テストを実走できる環境を用意する。
- **CI ゲート**: `ci`・`security` を必須チェックにし、ブランチ保護でマージを制御する（**対象と check 名の正は `docs/ai-workflow.md` の表**。`codeql` は `paths:` 付きのため必須にしない）。
- **文書・トレーサビリティの機械検査**（一覧と挙動は [`scripts/README.md`](scripts/README.md)）: 資料再編で **`check-trace-blocks`**（trace ブロック規約）と **`gen-knowledge-graph --check`**（参照の in-repo 実在）を新設し、🔴 **planning 依存の検査器（pin 鮮度・kit 同期のバイト一致・環流の未送付／status 突合）は退役させた。復活させない**（ADR-0048 決定 2・5・6 / IADR-0228）。乖離の検知は issue 運用と定期棚卸しに委ねる。

## Git 運用

- `main` を安定版とし、直接コミットしない。作業ブランチ → プルリクエスト経由でマージする。
- 1 コミット = 1 論理変更。コミットメッセージ先頭に種別（`feat:` `fix:` `refactor:` `test:` `docs:` `chore:` 等）と起点 ID を付ける。
- 破壊的な git 操作（force push, `reset --hard`）は行わない。

## 禁止事項

- **仕様書（`.ai-context/specs/`）を作成せずに実装へ着手すること**（上の手順 3）。
- 計画書（特に fixed / Accepted）に反する実装、および ADR で確定した制約の無断逸脱。差異が必要な場合は、新 IADR または planning への issue 起票で根拠を残す。
- **planning への依存の再導入**（submodule 化・pin 固定・計画書をビルド／CI の前提にすること）。ADR-0048 決定 2 を覆すには新しい計画 ADR が要る。
- **`docs/` 配下の表示テキストへ計画 ID・IADR・仕様書名・修飾付き issue 参照を書くこと**（trace ブロックへ入れる）。
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
- **サービス境界**: サービス間は直接参照せず、`Shared.Contracts` の契約と HTTP（Refit）/ メッセージング（**Wolverine**。ADR-0027 / ADR-0030。**MassTransit は不採用**で、新規混入は `scripts/backend-library-baseline.json` の ratchet が CI で fail させる）で疎結合に保つ。ユニット外参照は `src/platform/backend/Shared/` の 3 プロジェクトのみ許可（IADR-0117 で 2 → 3 へ改定。`Platform.Shared.Kernel` は Result / Error を公開する（IADR-0229）。platform → 可変ユニットは禁止）。

### TypeScript / React（フロントエンド `src/<unit>/frontend/`）

- **スタック**: **React 19** + TypeScript 5.6 + **Vite 6**（ESM, `"type": "module"`）。テストは **Vitest 3**。Node は CI と揃え **22** を使う。ADR-0031 が確定したスタックへの移行は [IADR-0121](.ai-context/adr/IADR-0121_spa-stack-migration-staging.md) が段に分割して管理する（**段の進捗は同 IADR が正本。ここへ書かない**——進捗は最も速く腐る）。ルーティングは **TanStack Router**（[IADR-0124](.ai-context/adr/IADR-0124_tanstack-router-unit-composition.md)。`react-router-dom` は platform / knowledge から撤去済みで、再混入は ESLint が止める）。
- **構成**: pnpm workspace（ルート = `src/`、メンバは `pnpm-workspace.yaml` が正。IADR-0121 決定 2）。`platform/frontend`（foundation + アプリホスト）と `knowledge/frontend`（画面 features）を分離する（[IADR-0121](.ai-context/adr/IADR-0121_spa-stack-migration-staging.md) / [IADR-0056](.ai-context/adr/IADR-0056_repo-unit-structure-platform-knowledge.md)）。import はエイリアス `@foundation` / `@features`（合成点） / `@knowledge` を使う。
- **サーバー状態**: **TanStack Query** に一元化する（`foundation/api/queryClient.ts` が唯一の生成点）。**グローバルストア（Redux）は持たない**——`redux` / `@reduxjs/*` の import は ESLint が error にする（IADR-0121 決定 8）。
- **UI / CSS**: **Tailwind CSS v4** ＋ 共有 UI パッケージ **`@platform/ui`**（[`src/packages/ui`](src/packages/ui/README.md)。IADR-0121 決定 4 / IADR-0125 決定 1）。入れてよいのはデザイントークン・`cn()`・shadcn/ui 派生プリミティブのみで、ドメイン・通信・ルーティング・認証・**表示文言**は入れない。公開面は `src/index.ts` の 1 ファイルで、深い参照は ESLint が禁止する。**外部 CDN・Web フォント・analytics を使わない**（08_data-egress-policy。フォントはシステムフォント、アイコンは lucide-react）。この禁止は `node scripts/check-static-egress.js --require <dist>` が成果物を走査して強制する。状態表示は**色だけで意味を持たせない**（色 ＋ アイコン ＋ テキスト。`StatusBadge` / `Alert` / `notify` が API で強制する）。
- **i18n**: **Lingui（ja / en）**。マクロ（`@lingui/core/macro` の `msg`）を babel で展開する設定は **`src/vitest.config.ts` と `src/platform/frontend/vite.config.ts` の両方**に置く（片方だけだと静かに割れる）。カタログ（`foundation/i18n/locales/<locale>/messages.{po,ts}`）は **orval 生成物と同じくコミットし、`pnpm run i18n` の再生成差分を CI が検査する**。**未翻訳キーは `node scripts/check-i18n-catalogs.js` が止める**（[IADR-0125](.ai-context/adr/IADR-0125_ui-primitives-i18n-catalog-and-storybook.md) 決定 4）。表示言語はブラウザ設定から決め、**切替 UI は持たない**。テストは Vitest 側を `src/test/setup.ts`、Playwright 側を `playwright.config.ts` の `use.locale` で **ja に固定**する。
- **コンポーネントカタログ**: **Storybook**（`src/packages/ui/.storybook/`。`pnpm --filter @platform/ui run build-storybook`）。対象は `@platform/ui` のプリミティブのみで、画面（features）は入れない。テレメトリとクラッシュレポートは無効化する（08_data-egress-policy）。
- **BFF 境界**: バックエンドへは必ず `/bff/*` 経由。呼び出しは **orval 生成フック**（`pnpm run codegen`。入力は `docs/api/openapi.yaml` の `/bff/` 配下のみ・**生成物はコミット**し CI が再生成差分を検査）か `foundation/api` の `apiFetch` / `apiStream` を使う。**手書き HTTP クライアントは禁止**で、`foundation/api` 以外での `fetch` / `XMLHttpRequest` / `EventSource` と `axios` 等の import は ESLint が error にする。接続先はビルドに焼き込まず実行時 config（`platform/frontend/public/config.js`）で注入する。フロントから各サービスを直接叩かない。
- **認証**: `oidc-client-ts`（Authorization Code + PKCE）で Keycloak public client `platform-spa` を用いる。トークンやシークレットをコードに埋め込まない。**ADR-0032 の BFF セッション方式へ移行予定**（[IADR-0121](.ai-context/adr/IADR-0121_spa-stack-migration-staging.md) 決定 6 が正本。進捗はここへ書かない）。
- **Lint / 型**: ESLint flat config（[`src/eslint.config.js`](src/eslint.config.js)）+ typescript-eslint。`src/` で `pnpm run lint` / `pnpm run typecheck` が通ること。
- **フォーマット**: `pnpm run format` で整形（設定は [`src/.prettierrc.json`](src/.prettierrc.json)）。**CI の [`frontend.yml`](.github/workflows/frontend.yml) の `lint` 相当ジョブが `pnpm run format:check` を強制する**（C# 側の `dotnet format --verify-no-changes` と同じ役割。issue #562）。**対象範囲の単一情報源は [`src/.prettierignore`](src/.prettierignore)** であり、除外グロブを `package.json`・ワークフロー・**本ファイル**へ複写しない（`src/` の外は例外。[[IADR-0203]] 決定 3）。
- **テスト**: 単体は **Vitest**（jsdom）+ Testing Library、E2E は **Playwright**。テストは実装と同居し `*.{test,spec}.{ts,tsx}`。
- **カバレッジ**: `pnpm run test:coverage`（v8 provider）。[`src/vitest.config.ts`](src/vitest.config.ts) の `coverage.thresholds` は**回帰防止のラチェット**（全ユニット横断で計測）。テストを増やしたらしきい値を引き上げ、床を割る変更は CI（[`frontend-tests.yml`](.github/workflows/frontend-tests.yml)）で止める。

### CI（GitHub Actions）

- バックエンドは [`ci.yml`](.github/workflows/ci.yml)（ユニット毎に restore/build/test/format）、フロントは [`frontend.yml`](.github/workflows/frontend.yml)（typecheck/lint/build/e2e）と [`frontend-tests.yml`](.github/workflows/frontend-tests.yml)（単体テスト＋カバレッジ）に分離する。フロント用ジョブは `paths: ["src/*/frontend/**", ...]` で各ユニットの frontend 変更時のみ起動し、両スタックの CI を独立させる。
- `.github/workflows/` は**編集できる**（[IADR-0169](.ai-context/adr/IADR-0169_cross-repo-ref-scan-beyond-markdown.md) が実測。「編集不可」は誤り）。変更したら、**その変更で起動条件・必須チェックが変わらないか**を確かめること。
