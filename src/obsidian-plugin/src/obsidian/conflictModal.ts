// FR-20, UC-11, SC-20, ADR-0037 決定 7, [[IADR-0352]] 決定 3・4: 競合の 3 択を利用者へ提示する Modal。
//
// **自動解決を既定にしない**（決定 7）。利用者がボタンを押すまで何も実行せず、「保留」で閉じれば
// 次の同期でまた提示される（journal と状態は push 側が触っていない）。
// Obsidian に依存するのはこのファイルだけで、解決の実体は `protocol/conflictResolver.ts`。
// 文言は ja 固定（[[IADR-0338]] 決定 8）。
import { type App, Modal, Setting } from 'obsidian';
import type { ConflictChoice } from '../protocol/conflictResolver.ts';
import type { PushConflict } from '../protocol/pushSync.ts';

export type ConflictDecision = ConflictChoice | 'defer';

/** 1 件の競合を提示し、利用者の選択（保留を含む）を返す。 */
export function askConflict(app: App, conflict: PushConflict): Promise<ConflictDecision> {
  return new Promise((resolve) => {
    const modal = new ConflictModal(app, conflict, resolve);
    modal.open();
  });
}

class ConflictModal extends Modal {
  private decided = false;

  constructor(
    app: App,
    private readonly conflict: PushConflict,
    private readonly resolveWith: (decision: ConflictDecision) => void,
  ) {
    super(app);
  }

  override onOpen(): void {
    const { contentEl, conflict } = this;
    this.setTitle('個人資料の競合');
    contentEl.createEl('p', { text: conflict.localPath });

    if (conflict.cause === 'version') {
      contentEl.createEl('p', {
        text:
          `ナレッジベース側が版 ${conflict.baseVersion} → ${conflict.serverVersion} に進んでいます` +
          `（${conflict.serverUpdatedAt}）。この端末には未送信の編集が ${conflict.pendingEdits} 件あります。`,
      });
      this.button(
        'ローカルを採用',
        'この端末の編集をナレッジベースの最新版の上に積んで送る',
        'local',
        true,
      );
      this.button(
        'サーバを採用',
        'ナレッジベースの内容でこのファイルを上書きし、未送信の編集を捨てる',
        'server',
      );
      this.button(
        '両方残す',
        'この端末の内容を別名のファイルとして新規に送り、このファイルはナレッジベースの内容にする',
        'both',
      );
    } else if (conflict.cause === 'server-deleted') {
      contentEl.createEl('p', {
        text:
          'ナレッジベース側で削除された資料がこの端末に残っています' +
          (conflict.purgeAt ? `（完全削除予定: ${conflict.purgeAt}）` : '') +
          '。復元は画面「個人資料管理」から行えます。',
      });
      this.button('ローカルを採用', 'この端末の内容を新しい資料として送り直す', 'local', true);
      this.button('サーバを採用', 'このファイルをゴミ箱（.trash）へ移す', 'server');
    } else {
      contentEl.createEl('p', {
        text: `同じパス（${conflict.vaultPath}）の資料がナレッジベースに既にあります。先に「取り込む（pull）」を実行してください。`,
      });
    }
    new Setting(contentEl).addButton((b) =>
      b.setButtonText('保留（次回の同期でまた確認）').onClick(() => this.decide('defer')),
    );
  }

  override onClose(): void {
    this.contentEl.empty();
    if (!this.decided) this.decide('defer');
  }

  private button(label: string, desc: string, choice: ConflictChoice, cta = false): void {
    new Setting(this.contentEl)
      .setName(label)
      .setDesc(desc)
      .addButton((b) => {
        b.setButtonText(label).onClick(() => this.decide(choice));
        if (cta) b.setCta();
      });
  }

  private decide(decision: ConflictDecision): void {
    if (this.decided) return;
    this.decided = true;
    this.resolveWith(decision);
    this.close();
  }
}
