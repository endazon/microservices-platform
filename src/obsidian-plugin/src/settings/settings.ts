// FR-20, SC-20, ADR-0037 決定 4, [[IADR-0331]] 決定 5: プラグイン設定と永続化の形。
//
// `data.json` に持つのは**接続先・同期フォルダ・同期状態**だけである。同期トークンは持たない
// （`obsidian/tokenStore.ts`）。
import type { SyncState } from '../protocol/pullPlanner.ts';

export interface PluginSettings {
  /** 同期プロトコルを受ける基底 URL（例: `https://kb.example.co.jp`）。末尾に `/private-notes/sync` は付けない。 */
  endpoint: string;
  /** 同期対象フォルダ（Vault 相対）。決定 4: 対象は特定フォルダのみ。 */
  syncFolder: string;
}

export const DEFAULT_SETTINGS: PluginSettings = {
  endpoint: '',
  syncFolder: '個人資料',
};

export interface PersistedData {
  settings: PluginSettings;
  syncState: SyncState;
}

export function readPersistedData(raw: unknown): PersistedData {
  const record = typeof raw === 'object' && raw !== null ? (raw as Record<string, unknown>) : {};
  const settings =
    typeof record.settings === 'object' && record.settings !== null
      ? (record.settings as Partial<PluginSettings>)
      : {};
  const syncState =
    typeof record.syncState === 'object' && record.syncState !== null
      ? (record.syncState as SyncState)
      : {};
  return {
    settings: {
      endpoint:
        typeof settings.endpoint === 'string' ? settings.endpoint : DEFAULT_SETTINGS.endpoint,
      syncFolder:
        typeof settings.syncFolder === 'string' ? settings.syncFolder : DEFAULT_SETTINGS.syncFolder,
    },
    syncState,
  };
}
