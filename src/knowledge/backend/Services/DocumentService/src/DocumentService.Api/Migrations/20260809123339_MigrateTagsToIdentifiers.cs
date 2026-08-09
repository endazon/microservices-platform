using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocumentService.Api.Migrations
{
    /// <summary>
    /// FR-06, FR-09, SC-05, SC-09, #635: 文書のタグを**表示名から識別子へ**移す（IADR-0153 決定 1）。
    ///
    /// **本リポジトリで最初のデータ移行つきマイグレーションである**
    /// （着手時の実測: <c>grep "Sql(" Migrations/*.cs</c> は 0 件）。
    /// 従前のマイグレーションはすべてスキーマだけを動かしており、既存行を書き換える先例が無い。
    ///
    /// **列の型は変わらない** —— <c>Tags</c> は移行の前後とも <c>jsonb</c> の配列である。
    /// 変わるのは**中身**（<c>["経理","規程"]</c> → <c>["3fa8…","9c1b…"]</c>）だけなので、
    /// EF は差分を検出しない。**したがってここは手で書くしかない**
    /// （scaffold が生成したのは <c>Tags.UpdatedAt</c> 列の追加だけである）。
    ///
    /// **検証は実 PostgreSQL でしか行えない** —— EF InMemory は SQL を実行しないため、
    /// 単体テストではこのコードが 1 行も走らない。統合テスト（<c>Knowledge.IntegrationTests</c>、
    /// <c>[DockerFact]</c>）に置く。#636 の一意制約（InMemory が強制しない）と同じ理由である。
    /// </summary>
    public partial class MigrateTagsToIdentifiers : Migration
    {
        // C# の `string.Trim()`（= `Tag.Normalize`）が落とす空白の集合。
        //
        // **`btrim(x)` の既定（半角空白のみ）では足りない。** `Trim()` は `char.IsWhiteSpace` に従い
        // 全角空白 U+3000 やノーブレークスペース U+00A0 も落とすため、既定のままだと
        // **移行で登録した名前と、実行時に C# が正規化した名前が一致しなくなる**
        // （辞書に在るのに「辞書に無いタグです」と 400 になる）。ここで集合を揃えておく。
        //
        // **制御文字はすべて `\uXXXX` で書く。** PostgreSQL の `E''` が解釈する短縮形は
        // `\b \f \n \r \t` だけで、**`\v` は「バックスラッシュ以外の文字」として素の `v` になる**。
        // そのまま書くと**タグ名から文字 `v` を削る**という、気づきにくい壊れ方をする。
        private const string Whitespace =
            "E' \\u0009\\u000A\\u000B\\u000C\\u000D\\u0085\\u00A0\\u1680" +
            "\\u2000\\u2001\\u2002\\u2003\\u2004\\u2005\\u2006\\u2007\\u2008\\u2009\\u200A" +
            "\\u2028\\u2029\\u202F\\u205F\\u3000'";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // FR-09, SC-09, #635: 改名した時刻。**改名がまだ 1 度も起きていない既存行は
            // 作成時刻と同じ**にする（`Tag.Create` が両方へ同じ値を入れるのと揃える）。
            // scaffold の既定値 `0001-01-01` をそのまま使うと、**未改名のタグが
            // 「西暦 1 年に改名された」ように見える**。
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAt",
                table: "Tags",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.Sql(@"UPDATE ""Tags"" SET ""UpdatedAt"" = ""CreatedAt"";");

            // ── 手順 1: 既存文書が使っている表示名を辞書へ登録する ──────────────────
            //
            // **版履歴（`DocumentVersions`）の名前も登録する。** 履歴の過去版も識別子へ移すので、
            // 現行版に残っていない名前（付け外しされたタグ）も辞書に無いと参照先を失う。
            //
            // **登録しても使用件数は 0 件である** —— 使用件数は現行版だけを数えるためで
            // （IADR-0152 決定 2）、履歴にしか現れない名前は**登録直後に削除できる**。これは意図どおりである。
            migrationBuilder.Sql($@"
                INSERT INTO ""Tags"" (""Id"", ""Name"", ""CreatedAt"", ""UpdatedAt"")
                SELECT gen_random_uuid(), n.name, now(), now()
                FROM (
                    SELECT DISTINCT btrim(el.value #>> '{{}}', {Whitespace}) AS name
                    FROM ""Documents"" d
                    CROSS JOIN LATERAL jsonb_array_elements(d.""Tags"") AS el(value)
                    UNION
                    SELECT DISTINCT btrim(el.value #>> '{{}}', {Whitespace}) AS name
                    FROM ""DocumentVersions"" v
                    CROSS JOIN LATERAL jsonb_array_elements(v.""Tags"") AS el(value)
                ) n
                WHERE n.name <> ''
                  AND NOT EXISTS (SELECT 1 FROM ""Tags"" t WHERE t.""Name"" = n.name);
            ");

            // ── 手順 2: 配列の中身を表示名から識別子へ書き換える ────────────────────
            //
            // **`LEFT JOIN` ＋ `FILTER` にしてある。** 素の `JOIN` だと、解決できない要素しか
            // 持たない文書が副問い合わせに現れず、**表示名の配列のまま取り残される**
            // （その行は以後 `List<Guid>` へ復元できず、実行時に壊れる）。
            // 解決できないのは空文字だけ（手順 1 が空文字以外をすべて登録するため）であり、
            // **空文字はタグではない**ので落として `[]` にする。
            //
            // **順序を保つ**（`WITH ORDINALITY` ＋ `ORDER BY`）——タグの並びは画面の表示順である。
            migrationBuilder.Sql($@"
                UPDATE ""Documents"" d
                SET ""Tags"" = COALESCE(sub.ids, '[]'::jsonb)
                FROM (
                    SELECT d2.""Id"" AS doc_id,
                           jsonb_agg(to_jsonb(t.""Id""::text) ORDER BY el.ord)
                               FILTER (WHERE t.""Id"" IS NOT NULL) AS ids
                    FROM ""Documents"" d2
                    CROSS JOIN LATERAL jsonb_array_elements(d2.""Tags"") WITH ORDINALITY AS el(value, ord)
                    LEFT JOIN ""Tags"" t ON t.""Name"" = btrim(el.value #>> '{{}}', {Whitespace})
                    GROUP BY d2.""Id""
                ) sub
                WHERE d.""Id"" = sub.doc_id;
            ");

            migrationBuilder.Sql($@"
                UPDATE ""DocumentVersions"" v
                SET ""Tags"" = COALESCE(sub.ids, '[]'::jsonb)
                FROM (
                    SELECT v2.""Id"" AS version_id,
                           jsonb_agg(to_jsonb(t.""Id""::text) ORDER BY el.ord)
                               FILTER (WHERE t.""Id"" IS NOT NULL) AS ids
                    FROM ""DocumentVersions"" v2
                    CROSS JOIN LATERAL jsonb_array_elements(v2.""Tags"") WITH ORDINALITY AS el(value, ord)
                    LEFT JOIN ""Tags"" t ON t.""Name"" = btrim(el.value #>> '{{}}', {Whitespace})
                    GROUP BY v2.""Id""
                ) sub
                WHERE v.""Id"" = sub.version_id;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // **識別子を表示名へ戻す。** 戻せない識別子（辞書から消えたもの）は落とす——
            // 上りと同じ理由で、`LEFT JOIN` ＋ `FILTER` にして行ごと取り残さない。
            //
            // **改名の履歴は戻らない。** 改名後に下ると**新しい名前**が書き戻る。
            // 表示名しか持たない形へ戻す以上これは避けられず、**上りが情報を増やす向きの移行だからである**。
            migrationBuilder.Sql($@"
                UPDATE ""DocumentVersions"" v
                SET ""Tags"" = COALESCE(sub.names, '[]'::jsonb)
                FROM (
                    SELECT v2.""Id"" AS version_id,
                           jsonb_agg(to_jsonb(t.""Name"") ORDER BY el.ord)
                               FILTER (WHERE t.""Name"" IS NOT NULL) AS names
                    FROM ""DocumentVersions"" v2
                    CROSS JOIN LATERAL jsonb_array_elements(v2.""Tags"") WITH ORDINALITY AS el(value, ord)
                    LEFT JOIN ""Tags"" t ON t.""Id""::text = el.value #>> '{{}}'
                    GROUP BY v2.""Id""
                ) sub
                WHERE v.""Id"" = sub.version_id;
            ");

            migrationBuilder.Sql($@"
                UPDATE ""Documents"" d
                SET ""Tags"" = COALESCE(sub.names, '[]'::jsonb)
                FROM (
                    SELECT d2.""Id"" AS doc_id,
                           jsonb_agg(to_jsonb(t.""Name"") ORDER BY el.ord)
                               FILTER (WHERE t.""Name"" IS NOT NULL) AS names
                    FROM ""Documents"" d2
                    CROSS JOIN LATERAL jsonb_array_elements(d2.""Tags"") WITH ORDINALITY AS el(value, ord)
                    LEFT JOIN ""Tags"" t ON t.""Id""::text = el.value #>> '{{}}'
                    GROUP BY d2.""Id""
                ) sub
                WHERE d.""Id"" = sub.doc_id;
            ");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "Tags");
        }
    }
}
