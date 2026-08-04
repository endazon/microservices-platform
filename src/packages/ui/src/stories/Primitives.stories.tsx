import type { Meta, StoryObj } from '@storybook/react-vite';
import {
  Alert,
  Button,
  Card,
  CardContent,
  CardHeader,
  CardTitle,
  Input,
  Label,
  Select,
  StatusBadge,
  Table,
  TableBody,
  TableCaption,
  TableCell,
  TableHead,
  TableHeaderCell,
  TableRow,
  Tabs,
  TabsContent,
  TabsList,
  TabsTrigger,
  Tag,
  Textarea,
} from '../index';

// ADR-0031（コンポーネントカタログ = Storybook）/ IADR-0125 決定 1・5。
//
// カタログは **@platform/ui の公開面（src/index.ts）だけ**を通して部品を参照する。
// 深い参照（'../components/Button'）で書くと、公開面に載せ忘れた部品がカタログには
// 現れてしまい、「カタログにあるのにアプリから使えない」状態を作る。
//
// 文言は呼び出し側が渡す（プリミティブは文言を持たない。IADR-0125 決定 1）。
// export 名は PascalCase（storybook/prefer-pascal-case）とし、表示名は `name` で日本語にする。
// ここに現れる日本語はカタログの見本であり、Lingui の抽出対象ではない
// （lingui.config.ts の include は platform / knowledge の src のみ）。

const meta = {
  title: '@platform/ui/プリミティブ',
  parameters: {
    docs: {
      description: {
        component:
          '2 ユニット（platform / knowledge）で共用する shadcn/ui 派生プリミティブ。' +
          'ドメイン語彙・BFF 通信・ルーティング・認証・表示文言は持たない（IADR-0121 決定 4 / IADR-0125 決定 1）。',
      },
    },
  },
} satisfies Meta;

export default meta;
type Story = StoryObj<typeof meta>;

export const Buttons: Story = {
  name: 'ボタン',
  render: () => (
    <div className="flex flex-wrap items-center gap-3">
      <Button variant="primary">保存</Button>
      <Button variant="secondary">キャンセル</Button>
      <Button variant="ghost">詳細</Button>
      <Button variant="danger">削除</Button>
      <Button disabled>無効</Button>
    </div>
  ),
};

// INDEX 決定 21: 状態は「色 ＋ アイコン ＋ テキスト」の 3 点セットで表す。
// 色を外しても意味が成り立つことをカタログでも示す。
export const StatusBadges: Story = {
  name: '状態バッジ',
  render: () => (
    <div className="flex flex-wrap items-center gap-3">
      <StatusBadge tone="neutral">未実行</StatusBadge>
      <StatusBadge tone="success">同期済み</StatusBadge>
      <StatusBadge tone="warning">遅延</StatusBadge>
      <StatusBadge tone="danger">失敗</StatusBadge>
    </div>
  ),
};

export const FormControls: Story = {
  name: '入力',
  render: () => (
    <div className="max-w-md space-y-4">
      <div className="space-y-1">
        <Label htmlFor="sb-title" requiredHint="（必須）">
          タイトル
        </Label>
        <Input id="sb-title" placeholder="例: 経費精算マニュアル" />
      </div>
      <div className="space-y-1">
        <Label htmlFor="sb-class" requiredHint="（必須）">
          機密区分
        </Label>
        <Select id="sb-class" defaultValue="internal">
          <option value="public">公開</option>
          <option value="internal">社内</option>
          <option value="confidential">秘</option>
          <option value="restricted">極秘</option>
        </Select>
      </div>
      <div className="space-y-1">
        <Label htmlFor="sb-analysis">分析内容</Label>
        <Textarea id="sb-analysis" placeholder="この範囲の文書から要点を抽出してください" />
      </div>
      <div className="space-y-1">
        <Label htmlFor="sb-invalid">タイトル（エラー時）</Label>
        <Input id="sb-invalid" invalid defaultValue="" />
        <Alert tone="danger" label="エラー" role="alert">
          タイトルは 1 文字以上必要です。
        </Alert>
      </div>
    </div>
  ),
};

export const Alerts: Story = {
  name: '注記と警告',
  render: () => (
    <div className="max-w-2xl space-y-3">
      <Alert label="情報">接続情報（認証情報）は Vault で管理されます。</Alert>
      <Alert tone="success" label="成功">
        同期をトリガしました。
      </Alert>
      <Alert tone="warning" label="注意">
        前回の同期が失敗しています。接続設定を確認してください。
      </Alert>
      <Alert tone="danger" label="エラー" role="alert">
        必須属性が未設定のため保存できません。
      </Alert>
    </div>
  ),
};

export const Cards: Story = {
  name: '区画',
  render: () => (
    <div className="grid max-w-3xl gap-3 sm:grid-cols-2">
      <Card>
        <CardHeader>
          <CardTitle>属性・タグ</CardTitle>
          <StatusBadge tone="neutral">社内</StatusBadge>
        </CardHeader>
        <CardContent>部門: 経理 / タグ: 経費, 規程</CardContent>
      </Card>
      <Card>
        <CardHeader>
          <CardTitle>バージョン履歴</CardTitle>
        </CardHeader>
        <CardContent>v3（現行） / v2 / v1</CardContent>
      </Card>
    </div>
  ),
};

export const Tables: Story = {
  name: '表',
  render: () => (
    <div className="max-w-3xl">
      <Table>
        <TableCaption>データソース一覧</TableCaption>
        <TableHead>
          <TableRow>
            <TableHeaderCell>ソース</TableHeaderCell>
            <TableHeaderCell>種別</TableHeaderCell>
            <TableHeaderCell>同期状態</TableHeaderCell>
          </TableRow>
        </TableHead>
        <TableBody>
          <TableRow>
            <TableCell>共有フォルダ</TableCell>
            <TableCell>filesystem</TableCell>
            <TableCell>
              <StatusBadge tone="success">同期済み</StatusBadge>
            </TableCell>
          </TableRow>
          <TableRow>
            <TableCell>社内 Wiki</TableCell>
            <TableCell>wiki</TableCell>
            <TableCell>
              <StatusBadge tone="warning">遅延</StatusBadge>
            </TableCell>
          </TableRow>
        </TableBody>
      </Table>
    </div>
  ),
};

export const TabsStory: Story = {
  name: 'タブ',
  render: () => (
    <Tabs defaultValue="attrs" className="max-w-2xl">
      <TabsList aria-label="ABAC 設定">
        <TabsTrigger value="attrs">属性体系</TabsTrigger>
        <TabsTrigger value="tags">タグ辞書</TabsTrigger>
        <TabsTrigger value="edges">辺の型</TabsTrigger>
        <TabsTrigger value="policy">ポリシー定義</TabsTrigger>
      </TabsList>
      <TabsContent value="attrs">利用者属性・文書属性の定義</TabsContent>
      <TabsContent value="tags">タグ辞書の一覧</TabsContent>
      <TabsContent value="edges">辺の型（値集合）の一覧</TabsContent>
      <TabsContent value="policy">ポリシーの条件式</TabsContent>
    </Tabs>
  ),
};

export const TagStory: Story = {
  name: 'タグ（分類の名前）',
  render: () => (
    <div className="flex flex-wrap items-center gap-2">
      <Tag tone="accent">経理</Tag>
      <Tag tone="neutral">規程</Tag>
      <Tag tone="outline">組織文書</Tag>
      {/* 状態は Tag ではなく StatusBadge で表す（色 ＋ アイコン ＋ テキスト。INDEX 決定 21）。 */}
      <StatusBadge tone="success">同期済み</StatusBadge>
    </div>
  ),
};
