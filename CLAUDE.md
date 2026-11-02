# Claude Code 向け指示

@.github/copilot-instructions.md

現在の仕様と未決定事項は `_documents/アーキテクチャ方針.md`、運用は `_documents/開発運用.md` を読むこと。

Pleasanter 本体は Git で追跡しない参照専用ソースであり、実装へ取り込まない。
挙動を確かめる前に `bash scripts/sync-reference.sh` で `_reference/Implem.Pleasanter` を
上流の `main` の先頭へ更新する。`PLEASANTER_REFERENCE_DIR` があればその取得先を直接読む。
一覧ファイルがある場合は `references.json`（または `--manifest <path>`）の各 branch を取得する。
この場合 `PLEASANTER_REFERENCE_DIR` は使わず、共有先は `--root <dir>` で指定する。
Manual・Overview の参照も非追跡とし、private の取得は実行者本人の認証を使う。
未取得の private の内容は推測せず、取得できなかった旨を残す。
文書には確認したコミット SHA・ソースへのリンク・参照日を残す。
正式な編集元は `.github/copilot-instructions.md`。

外部記憶は、個別のリポジトリを作らず、Internal の `VehicleVision.PleasanterTools.ClaudeMemory` に `ExtensionsMigrator/` フォルダを作って管理する。現時点ではフォルダを作っていない。
