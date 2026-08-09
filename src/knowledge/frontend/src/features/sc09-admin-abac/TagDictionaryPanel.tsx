import { useState } from 'react';
import { Trans, useLingui } from '@lingui/react/macro';
import {
  Alert,
  Button,
  Input,
  Label,
  Table,
  TableBody,
  TableCaption,
  TableCell,
  TableHead,
  TableHeaderCell,
  TableRow,
} from '@platform/ui';
import { toMessages } from '@foundation/ui/apiErrors';
import { tagInUseCount, useTagActions, useTagDictionary } from './useTagDictionary';
// SC-09, IADR-0135 決定 1: 表示に使う型は**契約（OpenAPI）から生成された DTO** である。
import type { TagDto } from '@foundation/api/generated/bff.schemas';

// SC-09, UC-05, FR-09, #640: タグ辞書の管理（計画 §SC-09 §主要素の 4 区画のひとつ）。
//
// **この区画は #504 の時点では実装しなかった** —— [[IADR-0129]] 決定 1 が理由 **B: 契約の不在**
// として記録している（値集合を返す口・使用件数・改名の追随のいずれも契約に無かった）。
// #634 / #635 が後段を作り、**#640 が BFF の書き込み口を足したことで理由 B は消えた**。
//
// 計画が確定した規則（2026-08-02）はすべてここで満たす:
//   - **参照が 1 件でもあるタグは削除拒否**（[[IADR-0153]] 決定 6）
//   - **改名は既存文書へ追随する**（同 決定 1。文書は識別子を参照しており表示だけが変わる）
//   - **削除前に使用件数を示す**（409 の `usageCount` を翻訳済みの文へ差し込む）

/**
 * 辞書の 1 行。
 *
 * 行を独立の部品にしてあるのは `lingui/no-expression-in-message` のためである——
 * 補間には**素の変数だけ**を置く（式を入れると抽出されたメッセージから元の値が読めない）。
 */
function TagRow({
  tag,
  onRename,
  onDelete,
}: {
  tag: TagDto;
  onRename: (next: string) => void;
  onDelete: () => void;
}) {
  const { t } = useLingui();
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState(tag.name);
  const tagName = tag.name;

  if (editing) {
    return (
      <TableRow>
        <TableCell colSpan={2}>
          <Input
            value={draft}
            onChange={(e) => setDraft(e.target.value)}
            aria-label={t`タグの新しい名前: ${tagName}`}
          />
        </TableCell>
        <TableCell>
          <Button
            type="button"
            size="sm"
            onClick={() => {
              onRename(draft);
              setEditing(false);
            }}
          >
            <Trans>保存</Trans>
          </Button>{' '}
          <Button
            type="button"
            size="sm"
            variant="ghost"
            onClick={() => {
              setDraft(tagName);
              setEditing(false);
            }}
          >
            <Trans>キャンセル</Trans>
          </Button>
        </TableCell>
      </TableRow>
    );
  }

  return (
    <TableRow>
      <TableCell>{tagName}</TableCell>
      {/* 使用件数は**現行版の文書の件数**である（IADR-0152 決定 2。版履歴は数えない）。 */}
      <TableCell>{tag.usageCount}</TableCell>
      <TableCell>
        <Button
          type="button"
          size="sm"
          variant="ghost"
          aria-label={t`タグを改名: ${tagName}`}
          onClick={() => setEditing(true)}
        >
          <Trans>改名</Trans>
        </Button>{' '}
        <Button
          type="button"
          size="sm"
          variant="ghost"
          aria-label={t`タグを削除: ${tagName}`}
          onClick={onDelete}
        >
          <Trans>削除</Trans>
        </Button>
      </TableCell>
    </TableRow>
  );
}

export function TagDictionaryPanel() {
  const { t } = useLingui();
  const { data, isPending, isError } = useTagDictionary();
  const { create, rename, remove } = useTagActions();
  const [name, setName] = useState('');

  // [[IADR-0127]] 決定 7 と同じ形: 画面が出す操作結果は**直近の 1 件だけ**。新しい操作の開始時に
  // 全ミューテーションの状態を捨てる。**TanStack Query は「別のミューテーションが成功した」ことでは
  // 他方の `isError` を戻さない**ため、放置すると「削除が 409 で拒否された直後に別のタグを改名して
  // 成功しても、古い削除拒否の警告が残り、改名成功の通知が出ない」状態になる
  // （管理者は「今の改名が失敗した」と誤解する）。`AttributeDictionaryPanel` と同じ作法である。
  const mutations = [create, rename, remove];
  const failed = mutations.find((m) => m.isError);
  // SC-09「削除前に使用件数を示す」。**数値が取れたときだけ専用の文言にする。**
  const inUseCount = failed ? tagInUseCount(failed.error) : null;

  // FR-09, SC-09, #640: 改名が下流へ**何件波及したか**を出す（[[IADR-0153]] 決定 3）。
  //
  // **射影（Qdrant / Wiki.js）の反映は非同期である。** 件数を出さないと、管理者は
  // **「対象が 0 件だった」と「まだ届いていない」を区別できない**——どちらも
  // 「一覧の名前は変わったのに検索結果が古いまま」に見えるためである。
  //
  // **削除拒否では件数を見せて行動させているのに、改名では見せない**のは非対称で、
  // 同じ「非同期の波及」を扱う面として揃わない。
  const republished =
    rename.isSuccess && !failed ? (rename.data?.data?.republishedDocuments ?? null) : null;

  const tags = data?.tags ?? [];

  function beginOperation() {
    for (const mutation of mutations) mutation.reset();
  }

  return (
    <section className="mt-3">
      <h2 className="mb-2 text-base font-semibold text-[--color-fg]">
        <Trans>タグ辞書</Trans>
      </h2>

      {isError && (
        <Alert tone="danger" role="alert" label={t`エラー`}>
          <Trans>タグ辞書を読み込めませんでした。</Trans>
        </Alert>
      )}

      {republished !== null && (
        <Alert tone="success" role="status" className="mb-2" label={t`完了`}>
          <Trans>タグを改名しました。{republished} 件の文書へ反映しています。</Trans>
        </Alert>
      )}

      {failed && (
        <Alert
          tone={inUseCount === null ? 'danger' : 'warning'}
          role="alert"
          label={inUseCount === null ? t`エラー` : t`注意`}
        >
          {inUseCount === null ? (
            toMessages(failed.error, t`タグ辞書を更新できませんでした。`).join(' / ')
          ) : (
            // **件数を翻訳済みの文へ差し込む**（サーバの日本語をそのまま出すと en で混ざる）。
            // 件数が無いと「なぜ消えないか」しか分からず、外す作業に着手できない。
            <Trans>このタグは {inUseCount} 件の文書で使われているため削除できません。</Trans>
          )}
        </Alert>
      )}

      <Table>
        <TableCaption>
          <Trans>タグ辞書の一覧</Trans>
        </TableCaption>
        <TableHead>
          <TableRow>
            <TableHeaderCell>
              <Trans>タグ</Trans>
            </TableHeaderCell>
            <TableHeaderCell>
              <Trans>使用件数</Trans>
            </TableHeaderCell>
            <TableHeaderCell>
              <Trans>操作</Trans>
            </TableHeaderCell>
          </TableRow>
        </TableHead>
        <TableBody>
          {isPending ? (
            <TableRow>
              <TableCell colSpan={3}>
                <Trans>読み込み中…</Trans>
              </TableCell>
            </TableRow>
          ) : tags.length === 0 ? (
            <TableRow>
              <TableCell colSpan={3}>
                <Trans>タグは登録されていません。</Trans>
              </TableCell>
            </TableRow>
          ) : (
            tags.map((tag) => (
              <TagRow
                key={tag.id}
                tag={tag}
                onRename={(next) => {
                  beginOperation();
                  rename.mutate({ id: tag.id, data: { name: next } });
                }}
                onDelete={() => {
                  beginOperation();
                  remove.mutate({ id: tag.id });
                }}
              />
            ))
          )}
        </TableBody>
      </Table>

      <form
        className="mt-3 flex items-end gap-2"
        onSubmit={(e) => {
          e.preventDefault();
          if (!name.trim()) return;
          beginOperation();
          create.mutate({ data: { name } });
          setName('');
        }}
      >
        <div>
          <Label htmlFor="new-tag-name">
            <Trans>タグ名（必須）</Trans>
          </Label>
          <Input id="new-tag-name" value={name} onChange={(e) => setName(e.target.value)} />
        </div>
        <Button type="submit">
          <Trans>追加</Trans>
        </Button>
      </form>
    </section>
  );
}
