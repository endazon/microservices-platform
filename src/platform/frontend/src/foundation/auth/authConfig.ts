// Issue #126: Keycloak OIDC（SPA public client + Authorization Code + PKCE）の UserManager 設定。
// トークンは localStorage へ永続化せず（既定はメモリ上の InMemoryWebStorage）保持する。
// silent 更新は **リフレッシュトークン**で行う（Authorization Code フローは Keycloak が refresh_token を
// 発行するため、oidc-client-ts は iframe を使わずトークンエンドポイントで更新する。silent_redirect_uri は不要）。
// 更新に失敗した場合は、API の 401 契機で再ログインへ誘導する（apiClient の setUnauthorizedHandler）。
// 接続先（authority/clientId）は実行時 config から取得する（環境非依存ビルド）。
import { UserManager, WebStorageStateStore, InMemoryWebStorage } from 'oidc-client-ts';
import type { UserManagerSettings } from 'oidc-client-ts';
import { appConfig } from '@foundation/config/runtimeConfig';

export function buildUserManagerSettings(
  origin: string = window.location.origin,
): UserManagerSettings {
  const { oidc } = appConfig();
  return {
    authority: oidc.authority,
    client_id: oidc.clientId,
    redirect_uri: `${origin}/callback`,
    post_logout_redirect_uri: origin,
    response_type: 'code',
    scope: 'openid profile email',
    // トークンはメモリ保持（永続化しない）。XSS 時の持ち出し面を狭める。
    userStore: new WebStorageStateStore({ store: new InMemoryWebStorage() }),
    // リフレッシュトークンで失効前に自動更新する（iframe を使わない。上記コメント参照）。
    automaticSilentRenew: true,
    // 認可コード交換後に URL のクエリを掃除する。
    loadUserInfo: true,
  };
}

let manager: UserManager | null = null;

/** アプリ共有の UserManager（初回のみ生成）。 */
export function userManager(): UserManager {
  manager ??= new UserManager(buildUserManagerSettings());
  return manager;
}

/** テスト用: UserManager を差し替え/破棄する。 */
export function setUserManagerForTest(m: UserManager | null): void {
  manager = m;
}
