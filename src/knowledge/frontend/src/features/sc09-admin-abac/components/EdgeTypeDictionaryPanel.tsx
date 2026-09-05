import { useState } from 'react';
import { Trans, useLingui } from '@lingui/react/macro';
import {
  Alert,
  Button,
  Input,
  Label,
  Select,
  Table,
  TableBody,
  TableCaption,
  TableCell,
  TableHead,
  TableHeaderCell,
  TableRow,
} from '@platform/ui';
import { toMessages } from '@foundation/utils/apiErrors';
import {
  edgeTypeInUseCount,
  useEdgeTypeActions,
  useEdgeTypeDictionary,
} from '../api/useEdgeTypeDictionary';
// SC-09, IADR-0135 決定 1: 表示に使う型は**契約（OpenAPI）から生成された DTO** である。
import type { EdgeTypeDto } from '@foundation/api/generated/bff.schemas';

// SC-09, UC-05, FR-17, ADR-0033 決定 3・9, INDEX 決定 18 (#1241): 辺の型辞書の管理
// （計画 §SC-09 §主要素の 4 区画のひとつ。**これで 4 区画が揃う**）。
//
// **この区画は #504 の時点では実装しなかった** —— [[IADR-0129]] 決定 1 が理由 **A: 要求の着手保留**
// として記録している（前提の ADR-0033 が当時 `Proposed` だった）。
// **その保留は 2026-08-07（#586）に解除された**が、判断先として名指しされていた #504 が
// 判断を残さずに閉じたため、**解除を実行する主体が居ないまま残っていた**（[[IADR-0388]] 決定 4）。
//
// 計画が確定した規則（ADR-0033 決定 9 / INDEX 決定 18）はすべてここで満たす:
//   - **参照が 1 件でもある型は削除拒否**（件数を添えて示す）
//   - **改名は許し、既存の辺は新しい名前へ追随する**（辺は型 ID を参照するので**自動**である）
//   - **同じ規則をタグ辞書にも適用する** —— タグ側は既に満たしており（[[IADR-0153]] 決定 1・6）、
//     本区画は**タグ辞書と同じ操作体系・同じ文言構造**にしてある。
//
// 🔴 **「逆向きの表示語」の列は作らない。** hi-fi モックはその列を描くが、
// ADR-0033 が逆向きの語を定めているのは「**バックリンク欄での表示**」であって辞書の管理項目ではなく、
// 辞書のドメイン（`EdgeType`）にも契約（`EdgeTypeDto`）にもその欄が無い。
// **そのバックリンク欄自体、SC-04 側で実現方式が未確定である**（#1240 で確認済み）——
// **消費者の無い管理項目を先に作ると、計画が決めたときに実装ではなく画面が先に決めたことになる。**

/**
 * 辞書の 1 行。
 *
 * 行を独立の部品にしてあるのは `lingui/no-expression-in-message` のためである——
 * 補間には**素の変数だけ**を置く（式を入れると抽出されたメッセージから元の値が読めない）。
 */
function EdgeTypeRow({
  edgeType,
  onRename,
  onDelete,
}: {
  edgeType: EdgeTypeDto;
  onRename: (next: string) => void;
  onDelete: () => void;
}) {
  const { t } = useLingui();
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState(edgeType.name);
  const typeName = edgeType.name;

  if (editing) {
    return (
      <TableRow>
        <TableCell colSpan={4}>
          <Input
            value={draft}
            onChange={(e) => setDraft(e.target.value)}
            aria-label={t`辺の型の新しい名前: ${typeName}`}
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
              setDraft(typeName);
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
      <TableCell>{typeName}</TableCell>
      <TableCell>
        <LayerLabel layer={edgeType.layer} />
      </TableCell>
      {/* ADR-0033: 対称なら向きを持たない（探索では無向として扱う）。
       **語で書く** —— 記号や色だけで対称／非対称を示さない（INDEX 決定 21）。 */}
      <TableCell>{edgeType.isSymmetric ? <Trans>対称</Trans> : <Trans>非対称</Trans>}</TableCell>
      {/* 使用件数は**この型を使っている辺の本数**である。改名しても変わらない（辺は書き換わらない）。 */}
      <TableCell>{edgeType.usageCount}</TableCell>
      <TableCell>
        <Button
          type="button"
          size="sm"
          variant="ghost"
          aria-label={t`辺の型を改名: ${typeName}`}
          onClick={() => setEditing(true)}
        >
          <Trans>改名</Trans>
        </Button>{' '}
        <Button
          type="button"
          size="sm"
          variant="ghost"
          aria-label={t`辺の型を削除: ${typeName}`}
          onClick={onDelete}
        >
          <Trans>削除</Trans>
        </Button>
      </TableCell>
    </TableRow>
  );
}

/**
 * 層の表示名（ADR-0033 決定 3 の 3 層構成）。
 *
 * **値域外はそのまま出す** —— 辞書は実行時に変わるので、画面が知らない層が来ても
 * 「空欄」にはしない（何が入っているか分からなくなるほうが困る）。
 */
function LayerLabel({ layer }: { layer: string }) {
  if (layer === 'core') return <Trans>中核</Trans>;
  if (layer === 'recommended') return <Trans>推奨追加</Trans>;
  if (layer === 'future') return <Trans>将来検討</Trans>;
  return <>{layer}</>;
}

export function EdgeTypeDictionaryPanel() {
  const { t } = useLingui();
  const { data, isPending, isError } = useEdgeTypeDictionary();
  const actions = useEdgeTypeActions();
  const { create, rename, remove } = actions;
  const [name, setName] = useState('');
  const [layer, setLayer] = useState('recommended');
  const [isSymmetric, setIsSymmetric] = useState(false);

  // [[IADR-0127]] 決定 7 と同じ形（タグ辞書と対）: 画面が出す操作結果は**直近の 1 件だけ**。
  // **列挙は手書きの配列にしない** —— 4 本目のミューテーションを足したときに足し忘れて穴が開く。
  const mutations = Object.values(actions);
  const failed = mutations.find((m) => m.isError);
  // SC-09「削除前に使用件数を示す」。**数値が取れたときだけ専用の文言にする。**
  const inUseCount = failed ? edgeTypeInUseCount(failed.error) : null;
  const renamed = rename.isSuccess && !failed;

  const edgeTypes = data ?? [];

  function beginOperation() {
    for (const mutation of mutations) mutation.reset();
  }

  return (
    <section className="mt-3">
      <h2 className="mb-2 text-base font-semibold text-[--color-fg]">
        <Trans>辺の型</Trans>
      </h2>

      {isError && (
        <Alert tone="danger" role="alert" label={t`エラー`}>
          <Trans>辺の型辞書を読み込めませんでした。</Trans>
        </Alert>
      )}

      {/* ADR-0033 決定 9: 改名は**辺を 1 本も書き換えない**（辺は型 ID を参照している）。
          タグ辞書は再発行件数を出すが、**こちらに対応する数は無い** ——
          非同期の波及が無いからである。「何件へ反映しているか」を出すと、
          **起きていない処理を待っているように見える。** */}
      {renamed && (
        <Alert tone="success" role="status" className="mb-2" label={t`完了`}>
          <Trans>辺の型を改名しました。既存の辺はそのまま新しい名前で表示されます。</Trans>
        </Alert>
      )}

      {failed && (
        <Alert
          tone={inUseCount === null ? 'danger' : 'warning'}
          role="alert"
          label={inUseCount === null ? t`エラー` : t`注意`}
        >
          {inUseCount === null ? (
            toMessages(failed.error, t`辺の型辞書を更新できませんでした。`).join(' / ')
          ) : (
            // **件数を翻訳済みの文へ差し込む**（サーバの日本語をそのまま出すと en で混ざる）。
            <Trans>この辺の型は {inUseCount} 本の辺で使われているため削除できません。</Trans>
          )}
        </Alert>
      )}

      <Table>
        <TableCaption>
          <Trans>辺の型辞書の一覧</Trans>
        </TableCaption>
        <TableHead>
          <TableRow>
            <TableHeaderCell>
              <Trans>型名</Trans>
            </TableHeaderCell>
            <TableHeaderCell>
              <Trans>層</Trans>
            </TableHeaderCell>
            <TableHeaderCell>
              <Trans>方向</Trans>
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
              <TableCell colSpan={5}>
                <Trans>読み込み中…</Trans>
              </TableCell>
            </TableRow>
          ) : edgeTypes.length === 0 ? (
            <TableRow>
              <TableCell colSpan={5}>
                <Trans>辺の型は登録されていません。</Trans>
              </TableCell>
            </TableRow>
          ) : (
            edgeTypes.map((edgeType) => (
              <EdgeTypeRow
                key={edgeType.id}
                edgeType={edgeType}
                onRename={(next) => {
                  beginOperation();
                  rename.mutate({ id: edgeType.id, data: { name: next } });
                }}
                onDelete={() => {
                  beginOperation();
                  remove.mutate({ id: edgeType.id });
                }}
              />
            ))
          )}
        </TableBody>
      </Table>

      {/* ADR-0033 決定 3: **自動抽出の既定型は `related` であり、辞書に無い型は `related` へ丸めて
          警告として記録する**（拒否も破棄もしない —— 拒否すると取り込み全体が落ち、
          破棄すると辺そのものが失われる）。**丸めは後段が行う**ので画面は何もしないが、
          管理者は「型を消すと以後の抽出が `related` に寄る」ことを知って判断する必要がある。 */}
      <p className="mt-2 text-xs text-[--color-fg-muted]">
        <Trans>
          辞書に無い型は自動抽出のときに related
          へ丸められ、警告として記録されます。改名しても既存の辺はそのまま追随します。
        </Trans>
      </p>

      <form
        className="mt-3 flex items-end gap-2"
        onSubmit={(e) => {
          e.preventDefault();
          if (!name.trim()) return;
          beginOperation();
          create.mutate({ data: { name, layer, isSymmetric } });
          setName('');
        }}
      >
        <div>
          <Label htmlFor="new-edge-type-name">
            <Trans>型名（必須）</Trans>
          </Label>
          <Input id="new-edge-type-name" value={name} onChange={(e) => setName(e.target.value)} />
        </div>
        <div>
          <Label htmlFor="new-edge-type-layer">
            <Trans>層</Trans>
          </Label>
          <Select id="new-edge-type-layer" value={layer} onChange={(e) => setLayer(e.target.value)}>
            <option value="core">{t`中核`}</option>
            <option value="recommended">{t`推奨追加`}</option>
            <option value="future">{t`将来検討`}</option>
          </Select>
        </div>
        <div className="flex items-center gap-1 pb-2">
          <input
            id="new-edge-type-symmetric"
            type="checkbox"
            checked={isSymmetric}
            onChange={(e) => setIsSymmetric(e.target.checked)}
          />
          <Label htmlFor="new-edge-type-symmetric">
            <Trans>対称</Trans>
          </Label>
        </div>
        <Button type="submit">
          <Trans>追加</Trans>
        </Button>
      </form>
    </section>
  );
}
