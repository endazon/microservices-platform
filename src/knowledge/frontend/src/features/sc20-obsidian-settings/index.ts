// ADR-0031 / IADR-0262 決定 4: feature の公開面。
// feature の外から触ってよいのはこのファイルが再輸出したものだけである。
export {
  createSc20ObsidianSettingsRoute,
  sc20ObsidianSettingsNav,
  sc20ObsidianSettingsBreadcrumb,
} from './routes/sc20ObsidianSettingsRoute';
