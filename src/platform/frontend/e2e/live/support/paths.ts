// NFR / IADR-0624（#2025）: live 層のファイルの置き場（config と setup で同じ値を使う）。
import { fileURLToPath } from 'node:url';

/** storageState の既定の置き場（`src/platform/frontend/playwright/.auth/`。src/.gitignore が外す。レポートの外）。 */
export const DEFAULT_AUTH_DIR = fileURLToPath(
  new URL('../../../playwright/.auth', import.meta.url),
);

/** 手元の資格情報（任意）。リポジトリ直下の .gitignore の `.env.*` が外す。例示は `.env.e2e.example`。 */
export const LOCAL_ENV_FILE = fileURLToPath(new URL('../.env.e2e', import.meta.url));
