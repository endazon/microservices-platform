// FR-20, SC-20, ADR-0037 決定 4・5・7・12・15, [[IADR-0338]] 決定 5・8, [[IADR-0352]]: 設定タブ。
//
// - 同期トークンは**貼り付けて保存するだけ**で、保存後は値を画面へ戻さない（SC-20 の
//   「発行直後に一度だけ表示・再表示不可」と同じ規律を受け側でも守る）。
// - 期限切れ（30 日）のトークンはサーバ側で無効になるだけで**ここに残る**。SC-20 の固定文言と同じ
//   注意を置き、再発行したら入れ直すことを伝える。
// - 文言は ja 固定（SPA の Lingui カタログの射程外。決定 8）。
import { type App, Notice, PluginSettingTab, Setting } from 'obsidian';
import type PrivateNotesSyncPlugin from '../main.ts';

export class SyncSettingTab extends PluginSettingTab {
  constructor(
    app: App,
    private readonly plugin: PrivateNotesSyncPlugin,
  ) {
    super(app, plugin);
  }

  display(): void {
    const { containerEl } = this;
    containerEl.empty();

    new Setting(containerEl).setName('接続').setHeading();

    new Setting(containerEl)
      .setName('接続先 URL')
      .setDesc(
        'ナレッジベースの同期プロトコルを受ける URL（https）。末尾に /private-notes/sync は付けません。',
      )
      .addText((text) =>
        text
          .setPlaceholder('https://kb.example.co.jp')
          .setValue(this.plugin.settings.endpoint)
          .onChange(async (value) => {
            this.plugin.settings.endpoint = value.trim();
            await this.plugin.persist();
          }),
      );

    new Setting(containerEl)
      .setName('同期フォルダ')
      .setDesc(
        'Vault 内のこのフォルダをナレッジベースと双方向に同期します。対象から外したファイルは削除されず、同期が止まるだけです。' +
          'このフォルダで削除したファイルはナレッジベース側で論理削除（90 日保管・復元可）になります。' +
          '同期した資料は業務関連資料として扱われます。',
      )
      .addText((text) =>
        text.setValue(this.plugin.settings.syncFolder).onChange(async (value) => {
          this.plugin.settings.syncFolder = value.trim();
          await this.plugin.persist();
        }),
      );

    new Setting(containerEl).setName('同期トークン').setHeading();

    const hasToken = this.plugin.tokenStore.load() !== null;
    new Setting(containerEl)
      .setName(hasToken ? '保存済み（この端末のみ）' : '未設定')
      .setDesc(
        'Obsidian 連携設定画面で発行したトークンを貼り付けて保存します。トークンはこの端末にだけ保存され、' +
          'Vault のファイルには入りません。有効期限は 30 日で自動更新はありません。' +
          '期限が切れたトークンはここに残ったままになるので、再発行したら入れ直してください。',
      )
      .addText((text) => {
        text.inputEl.type = 'password';
        text.inputEl.autocomplete = 'off';
        text.setPlaceholder('発行されたトークンを貼り付け');
        text.onChange((value) => {
          this.pendingToken = value;
        });
      })
      .addButton((button) =>
        button
          .setButtonText('保存')
          .setCta()
          .onClick(() => {
            const value = this.pendingToken.trim();
            if (value === '') {
              new Notice('トークンが空です。');
              return;
            }
            this.plugin.tokenStore.save(value);
            this.pendingToken = '';
            new Notice('同期トークンをこの端末に保存しました。');
            this.display();
          }),
      )
      .addButton((button) =>
        button
          .setButtonText('削除')
          .setDisabled(!hasToken)
          .onClick(() => {
            this.plugin.tokenStore.clear();
            new Notice('同期トークンをこの端末から削除しました。');
            this.display();
          }),
      );

    new Setting(containerEl).setName('同期').setHeading();
    new Setting(containerEl)
      .setName('いま同期する（取り込み → 送信）')
      .setDesc(
        'ナレッジベースの変更を取り込んでから、この端末の編集・新規作成・削除を送ります。' +
          '両方が変わっている資料は競合として 1 件ずつ確認し、選ぶまで上書きしません。' +
          '編集は保存の間隔が 30 秒以上空くごとに 1 版として刻まれます。',
      )
      .addButton((button) =>
        button
          .setButtonText('いま同期する')
          .setCta()
          .onClick(() => {
            void this.plugin.sync();
          }),
      )
      .addButton((button) =>
        button.setButtonText('取り込みのみ').onClick(() => {
          void this.plugin.pull();
        }),
      )
      .addButton((button) =>
        button.setButtonText('送信のみ').onClick(() => {
          void this.plugin.push();
        }),
      );
  }

  private pendingToken = '';
}
