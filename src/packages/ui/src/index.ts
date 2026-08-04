// IADR-0121 決定 4: @platform/ui の公開面はこのファイルのみ。各ユニットは深い相対参照
// （@platform/ui/src/...）を行わない（eslint の no-restricted-imports で機械的に禁止する）。
export { cn } from './lib/cn';
export { Button, buttonVariants, type ButtonProps } from './components/Button';
export { StatusBadge, type StatusBadgeProps } from './components/StatusBadge';
