# _reference（参考専用・AGPL）

Pleasanter 本体（**GNU AGPL v3**）は、AI（Claude・Copilot・Codex）が
API 仕様・スキーマ・挙動を事実確認するためだけに読む参照ソース。
本リポジトリでは追跡せず、必要なときに上流の `main` の先頭を取得する。
CI は参照ソースを使わず、checkout の `submodules: false` も維持する。

- 上流: <https://github.com/Implem/Implem.Pleasanter>
- 既定の取得先: リポジトリ直下の `_reference/Implem.Pleasanter/`（Git 管理外）
- 共有取得先: 環境変数 `PLEASANTER_REFERENCE_DIR`（その場所を直接読む）
- この `README.md` は引き続き Git で追跡する

## 厳守事項

- **コードをコピー・流用・リンク・プロジェクト参照しない。**
  本アプリは Pleasanter 本体を改造せず、**標準 API を叩く別アプリ**として実装する
- ビルド対象・配布物・ソリューションへ含めない
- 取得した本体の作業ツリーを編集しない。本リポジトリへのコミット対象でもない
- 実機未検証の結果を動作確認済みと書かない。実運用の設定・権限や API の実際の応答は実機で確認する

> 本アプリがネットワーク越しに API を利用するだけ、という前提を保つ。
> 本体のコードを取り込むとこの前提が崩れる。
> 「どうせ AGPL 側があるのだから取り込んでよい」ではない。
> 本リポジトリはデュアルライセンスで、取り込むと商用側を提供できなくなる（[LICENSING.md](../LICENSING.md)）。

## 取得と更新

事実確認を始める前に毎回実行する。Git と bash が必要で、Windows では Git Bash で同じスクリプトを使う。
PowerShell 版は不要。

```bash
bash scripts/sync-reference.sh

# worktree 間で共有する場合（絶対パスを推奨）
export PLEASANTER_REFERENCE_DIR='D:\references\Implem.Pleasanter'
bash scripts/sync-reference.sh
```

PowerShell からも環境変数を設定して bash を起動できる。

```powershell
$env:PLEASANTER_REFERENCE_DIR = 'D:\references\Implem.Pleasanter'
bash scripts/sync-reference.sh
```

未取得なら `main` を `git clone --depth 1` で浅く取得する。
既存なら `git fetch --depth 1 origin main` と `git checkout --detach FETCH_HEAD` で更新する。
終了時は各項目について Path・短い SHA・コミット日時・Status を 1 行ずつ出す。
既定の 1 件では完全な FullSHA・取得日時（UTC）・csproj の Version も同じ行に出す。
Version が読めない場合は警告と `Version=unknown` を出し、版を推測しない。

変更済み・ステージ済み・未追跡ファイルがある作業ツリー、上流 URL が異なる origin、
別リポジトリや非空の取得先では停止する。通信失敗は失敗コードで終了し、既存の HEAD とファイルを維持する。
新規 clone は一時取得先で成功してから配置する。並行取得はロックで止める。
異常終了で `.sync-lock` ディレクトリが残った場合は、実行中の同期が無いことを確認してから空ディレクトリを取り除く。

`PLEASANTER_REFERENCE_DIR` 指定時は取得先の案内だけを出し、
`_reference/Implem.Pleasanter` にジャンクションやシンボリックリンクを作らない。
相対パスはリポジトリ直下を基準にする。共有取得先の更新中に他の AI が読まないよう作業を調整する。

## 一覧ファイル（Manual・Overview）

リポジトリ直下に `references.json` があれば、その一覧だけを取得する。
`--manifest <path>` で別の一覧を指定できる。明示したファイルが無い場合はエラーになる。
一覧が無い各アプリでは、従来どおり本体の `main` 1 件を取得し、JSON パーサーも不要。
Template 自身には既定の一覧を置かず、実例だけを文書フォルダーに置く。

```json
[
  {
    "path": "resources/official/Implem.Pleasanter",
    "url": "https://github.com/Implem/Implem.Pleasanter.git",
    "branch": "main",
    "private": false,
    "depth": 1
  }
]
```

一覧は空でない JSON 配列。項目の仕様は次のとおり。

| 項目 | 仕様 |
| --- | --- |
| `path` | 必須の文字列。リポジトリ直下からの相対パス。区切りは `/` |
| `url` | 必須の HTTPS URL。資格情報・空白・クエリ・フラグメントは不可 |
| `branch` | 必須のブランチ名。clone・fetch ともに指定したブランチだけを取得 |
| `private` | 任意の真偽値。既定 false。true の取得失敗だけを警告扱いにする |
| `depth` | 任意の整数。既定 1、範囲 1～2147483647 |

絶対パス、ドライブ名、`\`、空の要素、`.`・`..` 要素、`.git` で始まる要素（大文字小文字を問わない）、
末尾のドット・空白、制御文字を拒否する。パスの重複・親子関係も後続の項目を失敗にする。
取得先とその途中のシンボリックリンクや、基点の外へ解決されるパスも拒否する。
不正な項目は取得せず、残りの項目は続行する。JSON 自体が壊れている場合は取得開始前に失敗する。
JSON の解析は jq、node、python3 または python の順に選び、どれも使えなければ明確に失敗する。
追加のパッケージ依存・自動インストールは無い。

```bash
# Template 内の実例を試す場合。各対象リポジトリへの配布は別作業
bash scripts/sync-reference.sh --manifest _documents/references.manual.json --root 'D:\references\Manual'
bash scripts/sync-reference.sh --manifest _documents/references.overview.json --root 'D:\references\Overview'
```

`--root` は一覧がある場合だけ使え、取得先を `<root>/<path>` に変える。
`--manifest`・`--root` の相対引数もリポジトリ直下を基準にする。
一覧があると `PLEASANTER_REFERENCE_DIR` は無視する（1 件だけの一覧でも同じ）。
各取得先は Git 管理外にし、リンクを作らず直接読む。参照先を CI・ビルド・配布物へ含めない。

既存先は origin の完全一致とクリーンな作業ツリーを確認し、fetch 後に detach checkout する。
各項目の失敗はほかの取得を止めない。最終行群の状態は `updated`・`failed`・`unavailable`。
通常の失敗が 1 件でもあれば終了コード 1、成功と private の `unavailable` だけなら 0。
未取得時の SHA・日付は `-`。既存先で失敗した場合は、確認できた HEAD の SHA・日付を表示する。
private でも不正入力・ローカル変更・origin 不一致は失敗であり、警告で隠さない。
Git の認証エラー本文は表示せず、取得失敗と認証・接続・branch の確認を案内する。

### private の認証と実例の参照元

`pmc-ko/Qiita.Article` は private。Manual 用実例の `private: true` は取得権限を付与する指定ではない。
取得する人が `gh auth login` でログインするか、Git の資格情報を設定する。
設定済みの Git credential helper を優先し、GitHub の private では `gh auth token --hostname github.com`
を実行時だけ credential helper 内で使える。トークンを URL・コマンド引数・ログ・Git 設定へ保存しない。
bot の App は使わず、取得不能の内容や記事本文は文書へ補わない。

2026-10-26 に各ローカル作業ツリーの `.gitmodules` を読み、HEAD の定義と一致することを確認した。
配布時は対象の最新定義を再確認する。

- [Manual 用実例](../_documents/references.manual.json): 10 件。ツールは `master`、ほかは `main`
- [Overview 用実例](../_documents/references.overview.json): 12 件。すべて `develop`
- [Manual の .gitmodules][manual-modules]（SHA `acb51c4775aa4442abc15b2f5544248ae78bcef6`）
- [Overview の .gitmodules][overview-modules]（SHA `f88b8613b6313c83dceedf7ef5e0dd36944e0b2f`）
- Overview 用実例の新規 5 件（BinaryMigrator・DatabaseConverter・DotNetClient・FeedIngest・MailIngest）は、旧 `.gitmodules` にない。
  2026-11-02 に `gh repo view` で、公開（PUBLIC）・既定ブランチ `develop` を確認して足した

実例は参照定義だけで、Manual・Overview のサブモジュール削除や実際の配布は行っていない。
各リポジトリの移行は [配布手順](../_documents/参照ソースのオンデマンド取得への配布手順.md) に従う。

## 事実の記録と再現性

2026-10-26 の決定により、サブモジュール・日次更新 workflow・追従 Issue / PR は廃止した。
AI 用の参照専用で CI は使わないため、ポインタ更新の PR や bot の迂回権限は不要。
再現性は gitlink ではなく、**文書に書く確認済み SHA** が担う。

文書へ事実を書くときは、完全なコミット SHA・該当ソースへの固定リンク・参照日を併記する。
現在の取得先が同じ SHA とは限らないので、過去の記述を最新ソースの根拠として扱わない。
`main` はリリース前の変更も含む。Version だけでリリース済みとは判断せず、
必要なら上流の `Pleasanter_*` タグとの対応も確認する。
再確認していない過去の事実の SHA や参照日を、自動で最新の値へ置き換えない。

確認例（2026-10-26 参照、SHA `1c693a1ee379a87e588232e43216400431376653`）:

- [Context.cs][context] は API リクエストの `ApiKey` を読み、ユーザー解決へ渡す
- [Implem.Pleasanter.csproj][project] の Version は `1.5.9.1`、TargetFramework は `net10.0`

実機の応答内容（日時書式・未設定値など）と実運用の権限は、このソース確認だけでは保証しない。

一次情報（2026-10-26 参照）:

- [git clone](https://git-scm.com/docs/git-clone)（浅い clone とブランチ指定）
- [git fetch](https://git-scm.com/docs/git-fetch)（`--depth` と `FETCH_HEAD`）
- [Git credentials](https://git-scm.com/docs/gitcredentials)（credential helper の標準入出力）
- [gh auth token](https://cli.github.com/manual/gh_auth_token)（ホストの指定と実行者の認証）

[manual-modules]: https://github.com/vehiclevisionjp/VehicleVision.Pleasanter.Manual/blob/acb51c4775aa4442abc15b2f5544248ae78bcef6/.gitmodules
[overview-modules]: https://github.com/vehiclevisionjp/VehicleVision.PleasanterTools.Overview/blob/f88b8613b6313c83dceedf7ef5e0dd36944e0b2f/.gitmodules
[context]: https://github.com/Implem/Implem.Pleasanter/blob/1c693a1ee379a87e588232e43216400431376653/Implem.Pleasanter/Libraries/Requests/Context.cs#L497-L504
[project]: https://github.com/Implem/Implem.Pleasanter/blob/1c693a1ee379a87e588232e43216400431376653/Implem.Pleasanter/Implem.Pleasanter.csproj#L3-L10
