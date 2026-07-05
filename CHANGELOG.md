# 変更履歴 (CHANGELOG)

## Unreleased

### 新機能

- **NFR**: OpenAPI と CHANGELOG の自動更新を PR 経由で反映する方式に変更 (2984229)
- **NFR**: 推移依存の脆弱性を CI で定期スキャンするジョブを追加し、セキュリティ自動化を強化 (7163cd1)
- **FR-05,NFR**: サービス間内部APIの認証方針をIADR-0017化しネットワーク分離を適用 (#79) (e8fe670)
- **FR-10**: 利用状況・検索傾向・回答品質ダッシュボード (#54) (7b237ad)
- **FR-08**: 回答へのフィードバック（👍/👎・コメント）収集 (#53) (5bff408)
- **FR-13**: Wiki 閲覧の ABAC 適用 (#50) (69e36ad)
- **FR-12**: 原本の正規化変換パイプライン（pandoc＋LLMコード化＋画像保持） (#49) (4254fd7)
- **FR-11**: LLM 呼び出し先を用途・機密度で切り替える (#47) (23ec9d7)
- **FR-09**: ABAC 属性辞書・ポリシー管理APIと検証 (#46) (4bad460)
- Claude Code ワークフローと設定ファイルの更新および新規作成 (#45) (a23fb8a)
- 破壊的コマンドと機密ファイルアクセスのガードを強化し、直接コミットをブロックする機能を追加 (#44) (2381b1a)
- **FR-07**: 指定データ範囲での分析・比較・抽出を実装 (#43) (9f99f7c)
- **FR-07**: 指定データ範囲での分析・比較・抽出（UC-02） (#41) (dce2484)
- **FR-06**: 文書のバージョン管理・メタデータ管理 (#39) (9104ad2)
- **FR-05**: ABAC を多値 allow-list ＋ deny-by-default へ是正 (#38) (a6f65aa)
- **FR-02**: 取り込みパイプラインを完成（parse→chunk→embed→index） (#30) (2a910fc)
- **FR-04**: AI回答に番号付き出典(元文書リンク)を付与しBFF集約を実装 (#32) (57513a2)
- **FR-03**: ベクトル＋全文のハイブリッド検索を実装 (#31) (df421ec)
- **FR-01**: 正規化文書をカタログへ登録するパイプラインを接続 (#29) (ed8b3c8)
- **P0**: P0 マイクロサービス基盤スケルトン実装（共有ライブラリ・各 REST/Worker サービス・deploy・CI 有効化。元コミット件名 feat(FR-10) は誤記で FR-10 Dashboard とは無関係） (b421761)
- Add various specification templates for observability, operations, screens, security, technical requirements, and testing (#1) (a40c411)

### 不具合修正

- **NFR**: 推移依存の脆弱性スキャンに関するADRをIADR-0018に更新 (784c0be)
- **NFR**: ビルド警告解消と軽微な構成不備の整理 (#63) (#78) (356b1dc)
- **NFR**: Microsoft.OpenApi の脆弱性（NU1903/GHSA-v5pm-xwqc-g5wc）を解消 (#77) (5a4860f)
- FR-11 用途別・機密度別 LLM ルーティングの実運用不具合を修正 (#70) (3f69944)
- **FR-05**: /search の Scope 未指定を deny 化し fail-closed で ABAC を強制 (#65) (5a13436)
- claude-code-review permission_denials_count:1 を解消 (#40) (d04ba5f)
- **FR-01**: 統合テストの MassTransit Bus 起動レースを解消 (#37) (f026ce9)
- **#34**: EFCore.Relational を 10.0.9 に直接ピンし MSB3277 を解消 (#36) (1dda3f9)

### ドキュメント

- 必須仕様書の欠落補完・リンク切れ/status 修正 (#59) (#72) (867d6c7)
- **IADR-0014**: フォローアップ節にIssue #71へのリンクを追記 (12b9b51)
- 計画書ステータスの環流フィードバック (#57) (#68) (3f89bb0)
- **ADR-0011**: 自前軽量閲覧APIを正式決定しADR-0011をSupersede提案(#56) (#67) (42e7544)

### ビルド

- **deps**: Bump github/codeql-action from 3 to 4 (#51) (a2e0587)
- **deps**: Bump actions/setup-dotnet from 4 to 5 (#52) (c9de40a)
- **deps**: Bump gitleaks/gitleaks-action from 2 to 3 (#6) (0cc8a27)
- **deps**: Bump actions/setup-node from 4 to 6 (#5) (2c84abc)
- **deps**: Bump actions/checkout from 4 to 7 (#4) (f71709a)
- **deps**: Bump softprops/action-gh-release from 2 to 3 (#3) (642421b)
- **deps**: Bump actions/dependency-review-action from 4 to 5 (#2) (34d0f00)

### CI

- **NFR**: CI・補助成果物ワークフローの develop 運用整合 (#76) (eeb21fd)

### その他

- claude-code-review.yml を更新 (eb444f8)
- Update claude-coding.yml (b349c36)
- Add comments for submodule access and token usage (85ee98e)
- Update and rename claude.yml to claude-codeing.yml (d71d237)
- Revise CI workflow for .NET version and steps (135835a)
- Change checkout action version and update allowed tools (62f3d83)
- Add git commit and push to allowed commands (572e278)
- Feature/official support (#28) (38f68ad)
- disable example workflows and fix dependency-review (68a26d4)
- Initial commit (68569e5)
