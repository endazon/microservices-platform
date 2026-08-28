import { useMemo, useState } from 'react';
import { Trans, useLingui } from '@lingui/react/macro';
import { Link, useNavigate, useSearch } from '@tanstack/react-router';
import {
  Alert,
  Button,
  Input,
  Label,
  StatusBadge,
  Table,
  TableBody,
  TableCaption,
  TableCell,
  TableHead,
  TableHeaderCell,
  TableRow,
  Tabs,
  TabsList,
  TabsTrigger,
} from '@platform/ui';
import { formatDateTime } from '@foundation/ui/formatDateTime';
import { toMessages } from '@foundation/ui/apiErrors';
import type { PrivateNoteDto } from '@foundation/api/generated/bff.schemas';
import { ConfirmDialog } from '../../../components/ConfirmDialog';
import { usePrivateNoteActions, usePrivateNotes } from '../api/usePrivateNotes';
import {
  daysUntilPurge,
  deletedBytes,
  freedBytesOf,
  isPurgeImminent,
  quotaLevel,
  formatBytes,
  usagePercent,
} from '../types/quota';
import type { PrivateNotesSearch, TabOption } from '../routes/sc19PrivateNotesRoute';
import { QuotaPanel } from './QuotaPanel';

// SC-19, UC-11, FR-19/FR-21: 個人資料管理（05_screens: ルート /my/notes）。
//
// ■ 🔴 描かないもの（05_screens §SC-19「描いてはいけないもの」）
//     - 他人の非公開資料の件数・存在を示唆する表示（「他 N 件は閲覧できません」等）
//     - 管理者による一括閲覧・一括公開範囲変更
//     - **本画面内での本文編集**（リッチエディタも編集導線も置かない。ADR-0046 D-02）
//   いずれも「置いていない」ことを単体テストが**陽性対照と対で**固定する。
//
// ■ タブは URL（`?tab=trash`）に持つ。**問い合わせは 1 本**であり、タブは同じ応答の絞りにすぎない。
// ■ 容量の内訳「うち削除済み」と削除済みの件数バッジは、**同じ応答から画面が数える**
//   （契約が「数え方を 2 つにしない」と決めている）。
// ■ 削除の確認は 2 種類ある。**論理削除は「容量は空かない」を事前に告げ**、
//   **完全削除は ①復元不可 ②90 日待てば自動 ③解放される容量 の 3 点**を出す。

/** 確認ダイアログの種別。開いていないときは `null`。 */
type Confirmation =
  { kind: 'softDelete'; note: PrivateNoteDto } | { kind: 'purge'; ids: string[] } | null;

export function PrivateNotesPage() {
  const { t } = useLingui();
  const search: PrivateNotesSearch = useSearch({ from: '/_shell/my/notes' });
  const navigate = useNavigate({ from: '/my/notes' });

  const notes = usePrivateNotes();
  const actions = usePrivateNoteActions();
  const { create, softDelete, restore, purge, exposure } = actions;

  const [title, setTitle] = useState('');
  const [vaultPath, setVaultPath] = useState('');
  const [selected, setSelected] = useState<string[]>([]);
  const [confirming, setConfirming] = useState<Confirmation>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const all = useMemo(() => notes.data?.notes ?? [], [notes.data]);
  const usage = notes.data?.usage;
  const level = quotaLevel(usagePercent(usage));
  const isFull = level === 'full';

  const live = useMemo(() => all.filter((n) => !n.deleted), [all]);
  const trashed = useMemo(() => all.filter((n) => n.deleted), [all]);

  // 絞り込み（05_screens §SC-19 主要素 6）。**タイトルの部分一致だけ**を実装している ——
  // タグ・公開範囲・同期状態は台帳（契約）に項目が無い（作業仕様書 §計画との差異）。
  const query = search.q.trim().toLowerCase();
  const rows = useMemo(() => {
    const source = search.tab === 'trash' ? trashed : live;
    if (query === '') return source;
    return source.filter((n) => n.title.toLowerCase().includes(query));
  }, [search.tab, live, trashed, query]);

  // 「いま」は描画のたびに読み直さない —— 残り日数が描画のたびに揺れると、
  // 検査でも実運用でも同じ行が違う値を出しうる。
  const now = useMemo(() => new Date(), []);

  const mutations = Object.values(actions);
  const failed = mutations.find((m) => m.isError);
  const pending = mutations.some((m) => m.isPending);

  /** 新しい操作の前に、前回の結果（成功メッセージと各ミューテーションの失敗）を捨てる。 */
  function beginOperation() {
    setNotice(null);
    for (const mutation of mutations) mutation.reset();
  }

  const setParams = (patch: Partial<PrivateNotesSearch>) =>
    void navigate({ search: (prev: PrivateNotesSearch) => ({ ...prev, ...patch }) });

  function switchTab(tab: TabOption) {
    setSelected([]);
    setParams({ tab });
  }

  function submitCreate() {
    beginOperation();
    create.mutate(
      { data: { title: title.trim(), vaultPath: vaultPath.trim() || null } },
      {
        onSuccess: () => {
          setTitle('');
          setVaultPath('');
          setNotice(t`個人資料を作成しました。`);
        },
      },
    );
  }

  function toggleExposure(note: PrivateNoteDto, patch: Partial<PrivateNoteDto>) {
    beginOperation();
    exposure.mutate({
      id: note.id,
      // 露出の 3 つは**独立**である（05_screens §SC-20 主要素 8）。
      // 契約は 3 つ揃った要求を受けるので、変えない 2 つは現在値をそのまま送る。
      data: {
        includeInSearch: patch.includeInSearch ?? note.includeInSearch,
        includeInGraph: patch.includeInGraph ?? note.includeInGraph,
        includeInAi: patch.includeInAi ?? note.includeInAi,
      },
    });
  }

  function runRestore(ids: string[]) {
    beginOperation();
    for (const id of ids) restore.mutate({ id });
    setSelected([]);
    setNotice(t`選択した個人資料を復元しました。`);
  }

  function confirmPurge() {
    if (confirming?.kind !== 'purge') return;
    const ids = confirming.ids;
    beginOperation();
    purge.mutate(
      { data: { ids } },
      {
        onSuccess: () => {
          setSelected([]);
          setNotice(t`個人資料を完全に削除しました。`);
        },
      },
    );
    setConfirming(null);
  }

  function confirmSoftDelete() {
    if (confirming?.kind !== 'softDelete') return;
    const id = confirming.note.id;
    beginOperation();
    softDelete.mutate({ id }, { onSuccess: () => setNotice(t`個人資料を削除しました。`) });
    setConfirming(null);
  }

  const purgeTargets = confirming?.kind === 'purge' ? confirming.ids : [];
  // ADR-0037 決定 20: 解放容量は判断材料 —— KB 級の実サイズが 0.00 GB に潰れないよう単位を自動選択する。
  const freed = formatBytes(freedBytesOf(all, purgeTargets));

  return (
    <section className="flex flex-col gap-4">
      <h1 className="text-xl font-semibold">
        <Trans>個人資料</Trans>
      </h1>

      {/*
        05_screens §SC-19「業務関連資料としての扱い」の固定文言。
        🔴 **折りたたみやツールチップの中に隠さない。** 書き始める前に見える位置に置く。
      */}
      <Alert tone="info" label={t`取り扱い`}>
        <Trans>
          個人資料は業務関連資料として扱われます。退職時には、退職日から 30
          日間、管理者が閲覧することがあります。
        </Trans>
      </Alert>

      <QuotaPanel usage={usage} deletedBytes={deletedBytes(all)} />

      {/* 新規作成（05_screens §SC-19 主要素 4）。本文の入力欄も編集導線も置かない。 */}
      <section aria-label={t`個人資料の新規作成`} className="flex flex-col gap-2">
        <h2 className="text-sm font-semibold">
          <Trans>新しい個人資料を作成する</Trans>
        </h2>
        <div className="flex flex-wrap items-end gap-2">
          <div className="flex flex-col gap-1">
            <Label htmlFor="note-title" requiredHint={t`必須`}>
              <Trans>タイトル</Trans>
            </Label>
            <Input
              id="note-title"
              value={title}
              disabled={isFull}
              onChange={(e) => setTitle(e.target.value)}
            />
          </div>
          <div className="flex flex-col gap-1">
            <Label htmlFor="note-vault-path">
              <Trans>Obsidian 内のパス（任意）</Trans>
            </Label>
            <Input
              id="note-vault-path"
              value={vaultPath}
              disabled={isFull}
              onChange={(e) => setVaultPath(e.target.value)}
            />
          </div>
          <Button
            variant="primary"
            disabled={isFull || pending || title.trim() === ''}
            onClick={submitCreate}
          >
            <Trans>作成する</Trans>
          </Button>
        </div>
        <p className="text-xs text-[--color-fg-muted]">
          {/* ADR-0046 D-02 / D-04: 本文を書く経路は Obsidian 連携だけである。 */}
          <Trans>
            本文はこの画面では編集できません。本文を書くには Obsidian 連携を設定してください。
          </Trans>{' '}
          <Link to="/my/obsidian" className="underline">
            <Trans>Obsidian 連携設定へ</Trans>
          </Link>
        </p>
      </section>

      {notice && (
        <Alert tone="success" label={t`完了`} role="status">
          {notice}
        </Alert>
      )}
      {failed && (
        <Alert tone="danger" label={t`エラー`} role="alert">
          {toMessages(failed.error, t`操作に失敗しました。時間をおいて再度お試しください。`).join(
            ' ',
          )}
        </Alert>
      )}
      {notes.isError && (
        <Alert tone="danger" label={t`エラー`} role="alert">
          <Trans>個人資料の一覧を取得できませんでした。時間をおいて再度お試しください。</Trans>
        </Alert>
      )}

      <div className="flex flex-wrap items-end justify-between gap-2">
        <Tabs value={search.tab} onValueChange={(value) => switchTab(value as TabOption)}>
          <TabsList>
            <TabsTrigger value="active">
              <Trans>利用中（{live.length}）</Trans>
            </TabsTrigger>
            {/* 05_screens §SC-19 主要素 14: 削除済みの件数バッジ。 */}
            <TabsTrigger value="trash">
              <Trans>削除済み（{trashed.length}）</Trans>
            </TabsTrigger>
          </TabsList>
        </Tabs>
        <div className="flex flex-col gap-1">
          <Label htmlFor="note-filter">
            <Trans>タイトルで絞り込む</Trans>
          </Label>
          <Input
            id="note-filter"
            value={search.q}
            onChange={(e) => setParams({ q: e.target.value })}
          />
        </div>
      </div>

      {search.tab === 'trash' && trashed.length > 0 && (
        <div className="flex flex-wrap gap-2">
          <Button
            variant="secondary"
            disabled={selected.length === 0 || pending}
            onClick={() => runRestore(selected)}
          >
            <Trans>選択した資料を復元する</Trans>
          </Button>
          <Button
            variant="danger"
            disabled={selected.length === 0 || pending}
            onClick={() => setConfirming({ kind: 'purge', ids: selected })}
          >
            <Trans>選択した資料を完全に削除する</Trans>
          </Button>
        </div>
      )}

      {rows.length === 0 ? (
        <Alert tone="info" label={t`一覧`} role="status">
          {search.tab === 'trash' ? (
            <Trans>削除済みの個人資料はありません。</Trans>
          ) : (
            <Trans>個人資料がまだありません。上の入力欄から作成してください。</Trans>
          )}
        </Alert>
      ) : (
        <Table>
          <TableCaption>
            {search.tab === 'trash' ? t`削除済みの個人資料` : t`個人資料の一覧`}
          </TableCaption>
          <TableHead>
            <TableRow>
              {search.tab === 'trash' && (
                <TableHeaderCell scope="col">
                  <Trans>選択</Trans>
                </TableHeaderCell>
              )}
              <TableHeaderCell scope="col">
                <Trans>種別</Trans>
              </TableHeaderCell>
              <TableHeaderCell scope="col">
                <Trans>タイトル</Trans>
              </TableHeaderCell>
              <TableHeaderCell scope="col">
                {search.tab === 'trash' ? <Trans>削除日時</Trans> : <Trans>更新日時</Trans>}
              </TableHeaderCell>
              {search.tab === 'trash' && (
                <TableHeaderCell scope="col">
                  <Trans>完全削除まで</Trans>
                </TableHeaderCell>
              )}
              {search.tab === 'active' && (
                <TableHeaderCell scope="col">
                  <Trans>露出</Trans>
                </TableHeaderCell>
              )}
              <TableHeaderCell scope="col">
                <Trans>操作</Trans>
              </TableHeaderCell>
            </TableRow>
          </TableHead>
          <TableBody>
            {rows.map((note) => {
              const days = daysUntilPurge(note.purgeAt, now);
              const imminent = isPurgeImminent(days);
              return (
                <TableRow key={note.id}>
                  {search.tab === 'trash' && (
                    <TableCell>
                      <input
                        type="checkbox"
                        aria-label={t`${note.title} を選択`}
                        checked={selected.includes(note.id)}
                        onChange={(e) =>
                          setSelected((prev) =>
                            e.target.checked
                              ? [...prev, note.id]
                              : prev.filter((id) => id !== note.id),
                          )
                        }
                      />
                    </TableCell>
                  )}
                  <TableCell>
                    {/*
                      05_screens §SC-19「個人資料であることの表示」:
                      検索結果・グラフのノードと**同じアイコン 👤 と同じラベル**を使う。
                      **色だけで意味を持たせない**（記号と文言が意味を担う）。
                    */}
                    <span className="whitespace-nowrap text-xs">
                      <Trans>👤 個人資料（自分のみ）</Trans>
                    </span>
                  </TableCell>
                  <TableCell>
                    <span className="font-medium">{note.title}</span>
                    <span className="block text-xs text-[--color-fg-muted]">{note.vaultPath}</span>
                  </TableCell>
                  <TableCell>
                    {formatDateTime(search.tab === 'trash' ? note.deletedAt : note.updatedAt)}
                  </TableCell>
                  {search.tab === 'trash' && (
                    <TableCell>
                      {/* 残り 7 日以内は警告色（05_screens §SC-19 主要素 13）。色だけに頼らず文言も変える。 */}
                      <StatusBadge tone={imminent ? 'warning' : 'neutral'}>
                        {imminent ? t`まもなく完全削除（残り ${days} 日）` : t`残り ${days} 日`}
                      </StatusBadge>
                    </TableCell>
                  )}
                  {search.tab === 'active' && (
                    <TableCell>
                      <div className="flex flex-col gap-1 text-xs">
                        <label className="flex items-center gap-1">
                          <input
                            type="checkbox"
                            checked={note.includeInSearch}
                            disabled={pending}
                            onChange={(e) =>
                              toggleExposure(note, { includeInSearch: e.target.checked })
                            }
                          />
                          <Trans>横断検索に含める</Trans>
                        </label>
                        <label className="flex items-center gap-1">
                          <input
                            type="checkbox"
                            checked={note.includeInGraph}
                            disabled={pending}
                            onChange={(e) =>
                              toggleExposure(note, { includeInGraph: e.target.checked })
                            }
                          />
                          <Trans>ナレッジグラフに表示する</Trans>
                        </label>
                        <label className="flex items-center gap-1">
                          <input
                            type="checkbox"
                            checked={note.includeInAi}
                            disabled={pending}
                            onChange={(e) =>
                              toggleExposure(note, { includeInAi: e.target.checked })
                            }
                          />
                          <Trans>AI の入力に含める</Trans>
                        </label>
                      </div>
                    </TableCell>
                  )}
                  <TableCell>
                    {search.tab === 'trash' ? (
                      <div className="flex gap-2">
                        <Button
                          size="sm"
                          variant="secondary"
                          disabled={pending}
                          onClick={() => runRestore([note.id])}
                        >
                          <Trans>復元する</Trans>
                        </Button>
                        <Button
                          size="sm"
                          variant="danger"
                          disabled={pending}
                          onClick={() => setConfirming({ kind: 'purge', ids: [note.id] })}
                        >
                          <Trans>完全に削除する</Trans>
                        </Button>
                      </div>
                    ) : (
                      <Button
                        size="sm"
                        variant="secondary"
                        disabled={pending}
                        onClick={() => setConfirming({ kind: 'softDelete', note })}
                      >
                        <Trans>削除する</Trans>
                      </Button>
                    )}
                  </TableCell>
                </TableRow>
              );
            })}
          </TableBody>
        </Table>
      )}

      {/* ヘルプ（05_screens §SC-19 ヘルプ文言。ADR-0034 決定 2 の受け入れ済み副作用）。 */}
      <p className="text-xs text-[--color-fg-muted]">
        <Trans>
          リンク先が表示されない場合、リンク切れなのか、閲覧権限がないのかは区別できません。これは、閲覧権限のない文書の存在を知られないようにするための仕様です。
        </Trans>
      </p>

      {confirming?.kind === 'softDelete' && (
        <ConfirmDialog
          title={t`個人資料を削除しますか？`}
          confirmLabel={t`削除する`}
          cancelLabel={t`やめる`}
          pending={pending}
          onConfirm={confirmSoftDelete}
          onCancel={() => setConfirming(null)}
        >
          {/* 05_screens §SC-19「削除の確認ダイアログ（論理削除）」の固定文言。 */}
          <p>
            <Trans>
              「{confirming.note.title}
              」を削除します。削除しても容量は空きません（90
              日間保管されます）。すぐに容量を空けたい場合は、削除済み一覧から「完全に削除」を実行してください。
            </Trans>
          </p>
        </ConfirmDialog>
      )}

      {confirming?.kind === 'purge' && (
        <ConfirmDialog
          title={t`選択した個人資料を完全に削除しますか？`}
          confirmLabel={t`完全に削除する`}
          cancelLabel={t`やめる`}
          destructive
          pending={pending}
          onConfirm={confirmPurge}
          onCancel={() => setConfirming(null)}
        >
          {/* 05_screens §SC-19「完全削除（即時）の導線」の 3 点。**順序も含めて計画どおりに出す。** */}
          <p>
            <Trans>
              この操作は元に戻せません。削除後はいかなる方法でも復元できません。（削除の反映には時間がかかる場合があります。）
            </Trans>
          </p>
          <p>
            <Trans>
              90 日待てば自動的に完全削除されます。急がなければ、この操作は必要ありません。
            </Trans>
          </p>
          <p>
            <Trans>
              対象: {purgeTargets.length} 件 ／ 解放される容量: {freed}
            </Trans>
          </p>
        </ConfirmDialog>
      )}
    </section>
  );
}
