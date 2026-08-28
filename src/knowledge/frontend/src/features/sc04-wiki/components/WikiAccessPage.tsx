import { Trans } from '@lingui/react/macro';
import { appConfig } from '@foundation/config/runtimeConfig';

// SC-04, UC-07, FR-13: Wiki 閲覧導線。実体は Wiki.js（ABAC ゲートウェイ経由・Keycloak SSO 済み）。
// 本画面のスコープは「SPA から Wiki.js への遷移導線」。Wiki.js へは同一 Keycloak セッションで
// シームレスに遷移し、到達はゲートウェイ（ABAC）経由に限定される（IADR-0020）。閲覧権限は
// Wiki.js/ゲートウェイ側で判定するため、UI は導線のみを提供する（権限有無を UI で判定しない）。
//
// **［#449］表示文言を Lingui へ載せた。** 生の日本語文字列を持っており、`CLAUDE.md` §i18n
// （Lingui・ja / en）に反していた。**画面の構造は変えていない** —— 計画 05_screens §SC-04 の
// §主要素（ページツリー・本文・最終同期日時・SC-03 復帰リンク）を SPA 側で描くのか、
// Wiki.js 側が描くのかは**未決の設計判断**である（同節 §ルートは「Wiki.js 別ホスト・基盤 SPA とは
// 別配信」と書き、IADR-0020 の決定は「閲覧・編集 UI の実体は Wiki.js が担う」である）。
// **決まるまで導線のままにしておく** —— 先に SPA 側へ作ると Wiki.js の UI を二重に持つことになる。
export function WikiAccessPage() {
  const { wikiBaseUrl } = appConfig();

  return (
    <section>
      <h1>
        <Trans>Wiki 閲覧</Trans>
      </h1>
      <p>
        <Trans>
          社内 Wiki（Wiki.js）を開きます。ログイン中のアカウント（Keycloak SSO）でそのまま閲覧でき、
          アクセス範囲はゲートウェイ（ABAC）で制御されます。
        </Trans>
      </p>

      {wikiBaseUrl ? (
        <p>
          <a href={wikiBaseUrl} target="_blank" rel="noopener noreferrer">
            <Trans>Wiki を開く</Trans>
          </a>
        </p>
      ) : (
        <p role="note">
          <Trans>Wiki の接続先が未設定です。管理者に連絡してください。</Trans>
        </p>
      )}
    </section>
  );
}
