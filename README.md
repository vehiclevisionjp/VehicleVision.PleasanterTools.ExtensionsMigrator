# VehicleVision.PleasanterTools.ExtensionsMigrator

Pleasanter の拡張機能を、**ファイルで管理する形から Extensions テーブル（Extensions API）へ移す**ためのコマンドラインツールです。
`App_Data/Parameters/` 配下の `ExtendedScripts`・`ExtendedStyles`・`ExtendedHtmls`・`ExtendedSqls`・`ExtendedServerScripts`・`ExtendedNavigationMenus` を読み、
設定の意味を保ったまま、無効状態で登録し、読み戻して照合し、確認できたものだけをファイルから外して DB 側を有効にします。

> **このツールは設計の段階で、まだ動くものがありません。**
> 実装は Issue に沿って小さく進めています。現在の状態は [現在の状態](#現在の状態) にあります。
> 実機の Pleasanter に対する動作確認も、まだ行っていません（確認したことだけを、確認した版とともに書きます）。
> 仕様と事実確認の結果、未決定の事項は [アーキテクチャ方針](_documents/アーキテクチャ方針.md) にあります。

## 何をするか（計画）

| コマンド | 役割 | 書き込み先 |
| --- | --- | --- |
| `inventory` | 読み込まれるファイルを棚卸しし、移行・再生成置換・保持・要確認に分類する | 出力先のファイルだけ |
| `validate` | 変換結果・依存・ハッシュ・対象範囲・台帳を検査する | 出力先のファイルだけ |
| `plan` | ソースと対象 API の差分・競合・保持・実行単位を確定する | 出力先のファイルだけ |
| `register` | 無効状態で登録し、読み戻して照合する | Extensions API |
| `apply` | 対象ファイルを探索範囲外へ退避し、DB 側を有効にして再反映する | ソースと API |
| `verify` | 台帳・ファイル・API・反映の状態を確認する | 出力先のファイルだけ |
| `resume` | 途中の状態と結果不明の操作を照合して再開する | 明示した段階だけ |
| `rollback` | 記録した ID とバックアップで、機能単位に元へ戻す | ソースと API |

`plan` 以外の変更を伴うコマンドは、確認した plan の ID・ハッシュと journal を指定して実行します。
確認していない plan へ黙って適用しません。

## 大事な前提

- **元の設定の意味を変えません。** 名前・Description・無効状態・適用条件・未知の設定を維持します。名前を自動で変えません
- **ファイルと DB は共存します。** 同じ名前でも置き換えにならず、両方が読み込まれます。二重に読み込まないよう、退避と有効化の順序を plan に示します
- **移行後に元ファイルが無いのは正常な状態です。** 「ファイルが無いから DB のレコードを消す」動きはしません
- **「対象外」「保持」「削除」は別の状態です。** 出力でも区別します
- 対象の DB は API の背後にあります。通常の処理は SQL Server 固有の SQL や DB への直接接続に依存しません
- API キーは、引数の履歴・設定ファイル・plan・journal へ保存しません（環境変数などから読みます）
- Pleasanter のパラメーター再読込は、通常ログイン（Cookie 認証）と特権ユーザーが要り、API キーだけでは行えると仮定しません。
  手動で反映する場合の状態（`AwaitingReload`）を持ちます

## 使い方（`inventory`・`validate`）

どちらも何も書きません（`--out` で指定した出力先だけ）。`--source` には、Pleasanter の `App_Data/Parameters` に相当するフォルダを指定します。

```bash
extensions-migrator inventory --source <Parameters のフォルダ> --out inventory.json [--source-id <名前>] [--rules rules.json]
extensions-migrator validate  --source <Parameters のフォルダ> [--inventory inventory.json] [--rules rules.json]
```

- `Extended*` で始まるフォルダだけを読みます。分類は、移行（`Migrate`）・置換（`Replace`）・保持（`Keep`）・対象外（`Exclude`）・要確認（`NeedsReview`）です
- 出力の JSON には、本文は入りません（SHA-256 のハッシュだけ）。ソースのフルパスも入りません
- `--out` をソースのフォルダの中へ指定することはできません。保存は一時ファイルからの置き換えです
- `validate` は、要確認の設定があるとき、`--inventory` との差（変更・追加・削除）があるときに終了コード 1 を返します。同名・空名は警告です
- リンクと reparse point は辿らず、要確認にします

保持・対象外・置換は、利用者が JSON の規則で指定します（既定は規則なし。特定の環境の名前を前提にしません）。最初に一致した規則が使われます。

```json
{
  "formatVersion": 1,
  "rules": [
    { "match": "ExtendedSqls/Sample*.json", "classification": "keep", "reason": "サンプル" },
    { "match": "ExtendedStyles/generated/**", "classification": "replace", "reason": "再生成して置き換える" }
  ]
}
```

### `plan`（対象 API との照合。読み取りだけ）

```bash
export PLEASANTER_BASE_URL=https://pleasanter.example.com
export PLEASANTER_API_KEY=...   # 環境変数でだけ渡す（引数・設定・plan・ログには残りません）
extensions-migrator plan --source <Parameters のフォルダ> --tenant-id <テナント ID> [--ledger ledger.json] [--rules rules.json] --out plan.json
```

- 対象 API には `Get` だけを送ります（作成・更新・削除はしません）。接続先は HTTPS だけ（ローカルの検証環境の HTTP を除く）。リダイレクトは辿りません
- 設定ごとの予定: `CreateDisabled`（対象に無いので無効状態で作る予定）・`InSync`・`Retain`（保持・対象外・置換。対象には何もしない）・`Conflict`・`Blocked`（要確認）
- 台帳（`--ledger`）に無い既存レコードは、同じ内容・同じ名前でも自動では上書きせず `Conflict` にします。対象にしか無いレコードは数えるだけで、削除の対象にはしません
- 他のテナントのレコードは照合に使わず、件数だけ出します。`--tenant-id` は必須です
- 台帳が壊れている（チェックサム不一致・途切れ・別のソースのもの）ときは、空の台帳として扱わず、終了コード 1 で止まります
- plan の ID は内容から決まります（作成日時を含みません）。本文は埋め込まず、ハッシュだけです

## 現在の状態

| 項目 | 状態 |
| --- | --- |
| コマンドの入口・引数の解析・終了コード | あり（`check` と、未実装コマンドの案内だけ） |
| 接続確認（`check`） | 雛形のまま（Extensions API 用ではない。実機では未確認） |
| `inventory` / `validate` | 実装済み（ローカルのファイルだけ。合成データの単体テストで確認。実機の Pleasanter では未確認） |
| `plan`（対象 API の `Get` と台帳の照合） | 実装済み（スタブの API と合成データの単体テストで確認。実機の Pleasanter では未確認） |
| `register` / `resume` / `apply` / `verify` / `rollback` | 未実装 |
| GUI（Avalonia。試作） | 試作のみ（ウィンドウ・フォルダ選択・合成データの表示。`inventory` との結線はこれから）。実機の確認は Windows での起動だけ。macOS・Linux は未確認 |
| Azure（Kudu）のソース | 未実装（最初はローカルのファイルだけを扱う予定） |
| 実機の Pleasanter での確認 | なし |

進み具合と順序は GitHub の Issue（親の Issue にまとめています）を見てください。

## 対応する Pleasanter の版

設計は Pleasanter 1.5.9.1 の公式ソース（`664c833b144d9da9b6bb58e60f7e48eb01141e58`）を読んで確認した読み込み規則に基づきます。
実機での確認はまだです。対応する版を増やすときは、同じ規則が成り立つかを比べます。

## ライセンス

**AGPL-3.0-or-later と商用ライセンスのデュアルライセンス**です。詳細は [ライセンスについて](LICENSING.md) を参照してください。

| | ライセンス | 想定する利用 |
| --- | --- | --- |
| A | [GNU AGPL v3 以降](LICENSE) | オープンソースとして利用・改変・再配布する場合 |
| B | [株式会社ピー・エム・シー ITソリューション事業部の商用ライセンス](LICENSE-COMMERCIAL.md) | AGPL の義務を負わずに利用したい場合（個別契約） |

貢献には [CLA](CLA.md) への署名が要ります（[貢献のしかた](CONTRIBUTING.md)）。依存のライセンスは [NOTICE](NOTICE) にあります。

## 開発する人へ

ビルド・テスト・CI・規約は [開発ガイド](_documents/開発ガイド.md) と [開発運用](_documents/開発運用.md) を参照してください。
