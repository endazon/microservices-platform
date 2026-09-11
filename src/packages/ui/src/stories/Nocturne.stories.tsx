import { useRef, useState } from 'react';
import { RefreshCw } from 'lucide-react';
import type { Meta, StoryObj } from '@storybook/react-vite';
import {
  Button,
  Dialog,
  DialogActions,
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogTitle,
  DialogTrigger,
  EmptyState,
  ErrorState,
  Kv,
  KvItem,
  LoadingState,
  Note,
  Panel,
  ProgressBar,
  Rule,
  Skeleton,
  Spinner,
  Stat,
  Tooltip,
  TooltipContent,
  TooltipProvider,
  TooltipTrigger,
} from '../index';

// ADR-0031（コンポーネントカタログ = Storybook）/ IADR-0125 決定 1・5。
//
// Nocturne（hi-fi モックのデザインシステム）で足した部品のカタログ。
// **公開面（src/index.ts）だけを通して参照する**（深い参照で書くと、公開面へ載せ忘れた部品が
// カタログには現れ「カタログにあるのにアプリから使えない」状態を作る）。
//
// ここに現れる日本語はカタログの見本であり、Lingui の抽出対象ではない
// （lingui.config.ts の include は platform / knowledge の src のみ）。

const meta = {
  title: '@platform/ui/Nocturne',
  parameters: {
    docs: {
      description: {
        component:
          'hi-fi モックアップ（Nocturne）の語彙から起こしたプリミティブ。' +
          '待ち・空・エラーの三部品と、本文の区画（.panel / .stat / .bar / .kv / .note / .hr）、' +
          '重なりの部品（Base UI の Dialog / Tooltip）。表示文言は持たない（IADR-0125 決定 1）。',
      },
    },
  },
} satisfies Meta;

export default meta;
type Story = StoryObj<typeof meta>;

// 🔴 三部品は**別の見た目**である。0 件（正常）と失敗（異常）を同じ扱いにしない。
export const States: Story = {
  name: '待ち・空・エラー',
  render: () => (
    <div className="grid max-w-4xl gap-4 sm:grid-cols-3">
      <LoadingState label="検索しています" />
      <EmptyState
        title="該当する文書がありません"
        description="対象範囲またはキーワードを変えてください"
        action={<Button variant="secondary">条件を変える</Button>}
      />
      <ErrorState
        title="読み込みに失敗しました"
        description="時間をおいて再度お試しください"
        action={<Button variant="primary">再試行</Button>}
      />
    </div>
  ),
};

export const SpinnerAndSkeleton: Story = {
  name: '輪と場所取り',
  render: () => (
    <div className="flex max-w-md flex-col gap-4">
      <div className="flex items-center gap-4">
        <Spinner label="読み込み中" size="sm" />
        <Spinner label="読み込み中" />
      </div>
      {/* 場所取りは**高さが分かっている器**にだけ置く（表の行など）。 */}
      <Skeleton lines={4} />
    </div>
  ),
};

export const Panels: Story = {
  name: '区画',
  render: () => (
    <div className="max-w-2xl">
      <Panel heading="接続設定">
        <Kv columns={2}>
          <KvItem label="エンドポイント">https://example.invalid/bff</KvItem>
          <KvItem label="認証方式">BFF セッション</KvItem>
        </Kv>
        <Note>認証情報は Vault で管理される。</Note>
      </Panel>
      <Rule />
      <Panel heading="まだ設定がありません" variant="ghost">
        接続先を追加すると、ここに一覧が出る。
      </Panel>
    </div>
  ),
};

export const Metrics: Story = {
  name: '指標と進捗',
  render: () => (
    <div className="max-w-2xl">
      <Panel heading="当日の状況">
        <div className="grid grid-cols-3 gap-4">
          <Stat label="Stage" value="2" meta="段階ゲート通過" />
          <Stat label="当日損益" value="+12,400" meta="前日比 +3.1%" tone="ok" />
          {/* 🔴 tone は色だけを変える。「上限に近づいています」を meta に書くことで意味が残る。 */}
          <Stat label="上限使用率" value="92%" meta="上限に近づいています" tone="warn" />
        </div>
        <div className="mt-4 flex flex-col gap-2">
          <ProgressBar label="上限使用率" value={92} tone="warn" />
          <ProgressBar label="取込み進捗" value={45} />
        </div>
      </Panel>
    </div>
  ),
};

function ConfirmDialogSample() {
  const cancelRef = useRef<HTMLButtonElement>(null);
  const [open, setOpen] = useState(false);
  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogTrigger render={<Button variant="danger">削除</Button>} />
      {/* 🔴 破壊的操作の確認は**取消側**へ初期フォーカスを当てる（既存の規律）。 */}
      <DialogContent initialFocus={cancelRef}>
        <DialogTitle>文書を削除しますか</DialogTitle>
        <DialogDescription>削除した文書は復元できません。</DialogDescription>
        <DialogActions>
          <DialogClose
            render={
              <Button ref={cancelRef} variant="secondary">
                取消
              </Button>
            }
          />
          <Button variant="danger">削除する</Button>
        </DialogActions>
      </DialogContent>
    </Dialog>
  );
}

export const Overlays: Story = {
  name: '重なり（ダイアログ・ツールチップ）',
  render: () => (
    <div className="flex items-center gap-4">
      <ConfirmDialogSample />
      <TooltipProvider delay={200}>
        <Tooltip>
          {/* ツールチップは補強である。名前は対象自身（aria-label）が持つ。 */}
          <TooltipTrigger
            render={
              <Button aria-label="更新" variant="ghost">
                <RefreshCw className="size-4" aria-hidden />
              </Button>
            }
          />
          <TooltipContent>一覧を取得し直す</TooltipContent>
        </Tooltip>
      </TooltipProvider>
    </div>
  ),
};
