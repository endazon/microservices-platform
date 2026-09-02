// FR-20, UC-11, SC-20, ADR-0037 決定 1・2・14, 08_data-egress-policy 例外規定, [[IADR-0331]]:
// 自作 Obsidian プラグイン（社内配布）の入口。第 1 段は **設定 → manifest → pull** のみ。
//
// Obsidian に触るのはこのファイルと `settings/` `obsidian/` `transport/obsidianTransport.ts` だけで、
// 同期の中身（`protocol/`）は Obsidian 実体なしで Vitest と Node ハーネスが固定する（決定 6）。
import { Notice, Plugin } from 'obsidian';
import { LocalStorageTokenStore, type TokenStore } from './obsidian/tokenStore.ts';
import { VaultFileStore } from './obsidian/vaultFileStore.ts';
import { EndpointError, normalizeEndpoint } from './protocol/endpoint.ts';
import { sha256Hex } from './protocol/hash.ts';
import { runPullSync, type PullReport } from './protocol/pullSync.ts';
import { SyncClient } from './protocol/syncClient.ts';
import { SyncAuthError } from './protocol/types.ts';
import {
  DEFAULT_SETTINGS,
  readPersistedData,
  type PersistedData,
  type PluginSettings,
} from './settings/settings.ts';
import { SyncSettingTab } from './settings/settingsTab.ts';
import { obsidianTransport } from './transport/obsidianTransport.ts';

export default class PrivateNotesSyncPlugin extends Plugin {
  settings: PluginSettings = { ...DEFAULT_SETTINGS };
  tokenStore!: TokenStore;
  private data: PersistedData = { settings: { ...DEFAULT_SETTINGS }, syncState: {} };
  private syncing = false;

  override async onload(): Promise<void> {
    this.data = readPersistedData(await this.loadData());
    this.settings = this.data.settings;
    this.tokenStore = new LocalStorageTokenStore(this.app);

    this.addSettingTab(new SyncSettingTab(this.app, this));
    this.addCommand({
      id: 'pull-private-notes',
      name: '個人資料をナレッジベースから取り込む（pull）',
      callback: () => {
        void this.pull();
      },
    });
  }

  async persist(): Promise<void> {
    this.data.settings = this.settings;
    await this.saveData(this.data);
  }

  /** pull の一巡。失敗は必ず通知にする（黙って古いままにしない）。 */
  async pull(): Promise<void> {
    if (this.syncing) {
      new Notice('同期を実行中です。');
      return;
    }
    const token = this.tokenStore.load();
    if (token === null) {
      new Notice(
        '同期トークンが未設定です。Obsidian 連携設定画面で発行し、プラグイン設定へ貼り付けてください。',
        8000,
      );
      return;
    }
    let endpoint: string;
    try {
      endpoint = normalizeEndpoint(this.settings.endpoint);
    } catch (e) {
      new Notice(e instanceof EndpointError ? e.message : '接続先 URL が不正です。', 8000);
      return;
    }

    this.syncing = true;
    try {
      const report = await runPullSync({
        client: new SyncClient(obsidianTransport, endpoint, token),
        files: new VaultFileStore(this.app.vault.adapter),
        state: {
          load: async () => this.data.syncState,
          save: async (state) => {
            this.data.syncState = state;
            await this.saveData(this.data);
          },
        },
        hasher: sha256Hex,
        syncFolder: this.settings.syncFolder,
        now: () => new Date(),
      });
      new Notice(summarize(report), 10000);
    } catch (e) {
      if (e instanceof SyncAuthError) {
        new Notice(
          '同期できませんでした: 同期トークンが無効です（期限切れ・失効・未登録）。' +
            'Obsidian 連携設定画面で再発行し、プラグイン設定へ入れ直してください。Vault のファイルは変更していません。',
          12000,
        );
      } else {
        new Notice(`同期に失敗しました: ${e instanceof Error ? e.message : String(e)}`, 12000);
      }
    } finally {
      this.syncing = false;
    }
  }
}

export function summarize(report: PullReport): string {
  const lines = [
    `個人資料の取り込み: 取得 ${report.written.length} 件 / 一致 ${report.adopted.length} 件 / 最新 ${report.upToDate} 件（サーバ側 ${report.manifestCount} 件）`,
  ];
  if (report.conflicts.length > 0) {
    lines.push(
      `⚠ ローカルで変更・削除されたため上書きしなかった: ${report.conflicts.length} 件（解決は次の段で対応）`,
    );
    for (const c of report.conflicts.slice(0, 5))
      lines.push(
        `  - ${c.localPath}（${c.cause === 'local-modified' ? '編集あり' : 'ローカルに無い'}）`,
      );
  }
  if (report.serverDeleted > 0)
    lines.push(`ℹ サーバ側で削除済み（ローカルは触っていません）: ${report.serverDeleted} 件`);
  if (report.skipped.length > 0)
    lines.push(`⚠ パスが不正または衝突しているため取り込めなかった: ${report.skipped.length} 件`);
  if (report.pullErrors.length > 0)
    lines.push(`⚠ 取得できなかった資料: ${report.pullErrors.length} 件`);
  return lines.join('\n');
}
