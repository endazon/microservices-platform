// FR-20, UC-11, SC-20, ADR-0037 決定 1・2・4・5・7・8・14, 08_data-egress-policy 例外規定, [[IADR-0338]],
// [[IADR-0352]]: 自作 Obsidian プラグイン（社内配布）の入口。第 2 段で**双方向**（pull ＋ push / delete /
// 競合の 3 択）になった。
//
// Obsidian に触るのはこのファイルと `settings/` `obsidian/` `transport/obsidianTransport.ts` だけで、
// 同期の中身（`protocol/`）は Obsidian 実体なしで Vitest と Node ハーネスが固定する（IADR-0338 決定 6）。
//
// 「1 編集」の刻み（IADR-0352 決定 1）: Vault のイベント（modify / delete / rename）を journal に写す。
// 同期中の自分の書き込みが発火させるイベントは拾わない（`syncing`）。遅れて届いた分は push 側が
// 「最終同期時の内容と同じ編集」として落とす。
import { Notice, Plugin, TFile, type TAbstractFile } from 'obsidian';
import { askConflict } from './obsidian/conflictModal.ts';
import { LocalStorageTokenStore, type TokenStore } from './obsidian/tokenStore.ts';
import { VaultFileStore } from './obsidian/vaultFileStore.ts';
import {
  resolveServerDeleted,
  resolveVersionConflict,
  type ResolveDeps,
  type ResolveResult,
} from './protocol/conflictResolver.ts';
import { recordDelete, recordRename, recordSave } from './protocol/editJournal.ts';
import { EndpointError, normalizeEndpoint } from './protocol/endpoint.ts';
import { sha256Hex } from './protocol/hash.ts';
import { runPullSync, type PullReport } from './protocol/pullSync.ts';
import { runPushSync, type PushConflict, type PushReport } from './protocol/pushSync.ts';
import { SyncClient } from './protocol/syncClient.ts';
import { SyncAuthError } from './protocol/types.ts';
import { isInFolder } from './protocol/vaultPath.ts';
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
  private data: PersistedData = {
    settings: { ...DEFAULT_SETTINGS },
    syncState: {},
    journal: { edits: {}, deleted: {}, movedOut: {}, renamed: {} },
  };
  private syncing = false;
  private journalSave: number | null = null;

  override async onload(): Promise<void> {
    this.data = readPersistedData(await this.loadData());
    this.settings = this.data.settings;
    this.tokenStore = new LocalStorageTokenStore(this.app);

    this.addSettingTab(new SyncSettingTab(this.app, this));
    this.addCommand({
      id: 'sync-private-notes',
      name: '個人資料を同期する（取り込み → 送信）',
      callback: () => {
        void this.sync();
      },
    });
    this.addCommand({
      id: 'pull-private-notes',
      name: '個人資料をナレッジベースから取り込む（pull）',
      callback: () => {
        void this.pull();
      },
    });
    this.addCommand({
      id: 'push-private-notes',
      name: '個人資料をナレッジベースへ送る（push）',
      callback: () => {
        void this.push();
      },
    });

    this.app.workspace.onLayoutReady(() => {
      this.registerEvent(this.app.vault.on('modify', (f) => void this.onModify(f)));
      this.registerEvent(this.app.vault.on('delete', (f) => this.onDelete(f)));
      this.registerEvent(this.app.vault.on('rename', (f, old) => this.onRename(f, old)));
    });
  }

  async persist(): Promise<void> {
    this.data.settings = this.settings;
    await this.saveData(this.data);
  }

  // ── Vault イベント → journal ──────────────────────────────────────────────

  private isSyncTarget(file: TAbstractFile): file is TFile {
    return (
      file instanceof TFile &&
      file.extension === 'md' &&
      isInFolder(this.settings.syncFolder, file.path)
    );
  }

  private async onModify(file: TAbstractFile): Promise<void> {
    if (this.syncing || !this.isSyncTarget(file)) return;
    const content = await this.app.vault.read(file);
    recordSave(this.data.journal, file.path, content, new Date());
    this.scheduleJournalSave();
  }

  private onDelete(file: TAbstractFile): void {
    if (this.syncing) return;
    if (!(file instanceof TFile) || file.extension !== 'md') return;
    if (!isInFolder(this.settings.syncFolder, file.path)) return;
    recordDelete(this.data.journal, file.path);
    this.scheduleJournalSave();
  }

  private onRename(file: TAbstractFile, oldPath: string): void {
    if (this.syncing) return;
    if (!(file instanceof TFile) || file.extension !== 'md') return;
    recordRename(this.data.journal, oldPath, file.path, {
      fromInFolder: isInFolder(this.settings.syncFolder, oldPath),
      toInFolder: isInFolder(this.settings.syncFolder, file.path),
    });
    this.scheduleJournalSave();
  }

  private scheduleJournalSave(): void {
    if (this.journalSave !== null) window.clearTimeout(this.journalSave);
    this.journalSave = window.setTimeout(() => {
      this.journalSave = null;
      void this.saveData(this.data);
    }, 1000);
  }

  // ── 同期 ─────────────────────────────────────────────────────────────────

  private prepare(): { client: SyncClient } | null {
    const token = this.tokenStore.load();
    if (token === null) {
      new Notice(
        '同期トークンが未設定です。Obsidian 連携設定画面で発行し、プラグイン設定へ貼り付けてください。',
        8000,
      );
      return null;
    }
    let endpoint: string;
    try {
      endpoint = normalizeEndpoint(this.settings.endpoint);
    } catch (e) {
      new Notice(e instanceof EndpointError ? e.message : '接続先 URL が不正です。', 8000);
      return null;
    }
    return { client: new SyncClient(obsidianTransport, endpoint, token) };
  }

  private deps(client: SyncClient): ResolveDeps {
    return {
      client,
      files: new VaultFileStore(this.app.vault.adapter),
      state: {
        load: async () => this.data.syncState,
        save: async (state) => {
          this.data.syncState = state;
          await this.saveData(this.data);
        },
      },
      journal: {
        load: async () => this.data.journal,
        save: async (journal) => {
          this.data.journal = journal;
          await this.saveData(this.data);
        },
      },
      hasher: sha256Hex,
      syncFolder: this.settings.syncFolder,
      now: () => new Date(),
    };
  }

  /** pull → push の一巡。失敗は必ず通知にする（黙って古いままにしない）。 */
  async sync(): Promise<void> {
    await this.guarded(async (client) => {
      const pull = await runPullSync(this.deps(client));
      const push = await runPushSync(this.deps(client));
      new Notice(`${summarize(pull)}\n${summarizePush(push)}`, 12000);
      await this.presentConflicts(client, push.conflicts);
    });
  }

  async pull(): Promise<void> {
    await this.guarded(async (client) => {
      const report = await runPullSync(this.deps(client));
      new Notice(summarize(report), 10000);
    });
  }

  async push(): Promise<void> {
    await this.guarded(async (client) => {
      const report = await runPushSync(this.deps(client));
      new Notice(summarizePush(report), 10000);
      await this.presentConflicts(client, report.conflicts);
    });
  }

  private async guarded(run: (client: SyncClient) => Promise<void>): Promise<void> {
    if (this.syncing) {
      new Notice('同期を実行中です。');
      return;
    }
    const prepared = this.prepare();
    if (prepared === null) return;
    this.syncing = true;
    try {
      await run(prepared.client);
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

  /** 競合を 1 件ずつ Modal で提示し、選ばれたものだけ解決する（保留は何もしない）。 */
  private async presentConflicts(client: SyncClient, conflicts: PushConflict[]): Promise<void> {
    for (const conflict of conflicts) {
      const decision = await askConflict(this.app, conflict);
      if (decision === 'defer') continue;
      let result: ResolveResult;
      if (conflict.cause === 'version') {
        result = await resolveVersionConflict(this.deps(client), conflict, decision);
      } else if (conflict.cause === 'server-deleted') {
        if (decision === 'both') continue;
        result = await resolveServerDeleted(this.deps(client), conflict, decision);
      } else {
        continue;
      }
      new Notice(describeResolution(result), 8000);
    }
  }
}

export function summarize(report: PullReport): string {
  const lines = [
    `取り込み: 取得 ${report.written.length} 件 / 一致 ${report.adopted.length} 件 / 最新 ${report.upToDate} 件（サーバ側 ${report.manifestCount} 件）`,
  ];
  const localEdits = report.conflicts.filter((c) => c.cause === 'local-modified');
  const localDeleted = report.conflicts.filter((c) => c.cause === 'local-deleted');
  if (localEdits.length > 0)
    lines.push(
      `ℹ この端末で編集済みのため上書きしなかった（送信で送ります）: ${localEdits.length} 件`,
    );
  if (localDeleted.length > 0)
    lines.push(`ℹ この端末に無いため取り込まなかった: ${localDeleted.length} 件`);
  if (report.moved.length > 0)
    lines.push(`ℹ ナレッジベース側の名前変更に追随して移動: ${report.moved.length} 件`);
  if (report.staleOld.length > 0)
    lines.push(`⚠ 名前変更の旧ファイルが編集されていたため残した: ${report.staleOld.join(', ')}`);
  if (report.serverDeletedLocal.length > 0)
    lines.push(
      `⚠ ナレッジベース側で削除済み（この端末のファイルは残しています。送信時に確認します）: ${report.serverDeletedLocal.length} 件`,
    );
  else if (report.serverDeleted > 0)
    lines.push(`ℹ ナレッジベース側で削除済み: ${report.serverDeleted} 件`);
  if (report.skipped.length > 0)
    lines.push(`⚠ パスが不正または衝突しているため取り込めなかった: ${report.skipped.length} 件`);
  if (report.pullErrors.length > 0)
    lines.push(`⚠ 取得できなかった資料: ${report.pullErrors.length} 件`);
  return lines.join('\n');
}

export function summarizePush(report: PushReport): string {
  const lines = [
    `送信: 新規 ${report.created.length} 件 / 更新 ${report.updated.length} 件（${report.versionsPushed} 版）/ 削除 ${report.deleted.length} 件 / 変更なし ${report.unchanged} 件`,
  ];
  if (report.untracked.length > 0)
    lines.push(
      `ℹ 同期フォルダから外れたため同期を止めた（削除はしていません）: ${report.untracked.length} 件`,
    );
  if (report.renamedLocally.length > 0)
    lines.push(
      `ℹ この端末で名前を変えた資料: ${report.renamedLocally.length} 件（ナレッジベース側の名前は変わりません）`,
    );
  if (report.missingLocal.length > 0)
    lines.push(
      `⚠ 追跡中だがこの端末に見当たらない（削除は送っていません）: ${report.missingLocal.join(', ')}`,
    );
  if (report.conflicts.length > 0)
    lines.push(`⚠ 競合: ${report.conflicts.length} 件（順に確認します）`);
  if (report.errors.length > 0)
    for (const e of report.errors.slice(0, 5)) lines.push(`⚠ ${e.localPath}: ${e.message}`);
  return lines.join('\n');
}

export function describeResolution(result: ResolveResult): string {
  switch (result.kind) {
    case 'pushed':
      return `ローカルを採用: ${result.localPath} を版 ${result.version} として送りました（${result.versionsPushed} 版）。`;
    case 'overwritten':
      return `サーバを採用: ${result.localPath} をナレッジベースの版 ${result.version} で上書きしました。`;
    case 'both':
      return `両方残す: この端末の内容を ${result.copyPath} として送り、${result.localPath} はナレッジベースの内容にしました。`;
    case 'recreated':
      return `ローカルを採用: ${result.localPath} を新しい資料として送りました。`;
    case 'removed':
      return `サーバを採用: ${result.localPath} をゴミ箱へ移しました。`;
    case 'retry':
      return result.reason === 'version'
        ? `${result.localPath}: 解決の途中でナレッジベース側がまた進みました。次の同期でもう一度確認します。`
        : `${result.localPath}: ナレッジベース側で削除されていました。次の同期でもう一度確認します。`;
  }
}
