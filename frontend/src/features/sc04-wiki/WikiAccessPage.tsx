import { appConfig } from '@foundation/config/runtimeConfig';

// SC-04, UC-07, FR-13: Wiki 閲覧導線。実体は Wiki.js（ABAC ゲートウェイ経由・Keycloak SSO 済み）。
// 本画面のスコープは「SPA から Wiki.js への遷移導線」。Wiki.js へは同一 Keycloak セッションで
// シームレスに遷移し、到達はゲートウェイ（ABAC）経由に限定される（IADR-0020）。閲覧権限は
// Wiki.js/ゲートウェイ側で判定するため、UI は導線のみを提供する（権限有無を UI で判定しない）。
export function WikiAccessPage() {
  const { wikiBaseUrl } = appConfig();

  return (
    <section>
      <h1>Wiki 閲覧</h1>
      <p>
        社内 Wiki（Wiki.js）を開きます。ログイン中のアカウント（Keycloak SSO）でそのまま閲覧でき、
        アクセス範囲はゲートウェイ（ABAC）で制御されます。
      </p>

      {wikiBaseUrl ? (
        <p>
          <a href={wikiBaseUrl} target="_blank" rel="noopener noreferrer">
            Wiki を開く
          </a>
        </p>
      ) : (
        <p role="note">Wiki の接続先が未設定です。管理者に連絡してください。</p>
      )}
    </section>
  );
}
