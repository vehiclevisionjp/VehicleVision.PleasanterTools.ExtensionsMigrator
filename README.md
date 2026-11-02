# VehicleVision.PleasanterTools.Template

Pleasanter 向けツールを作るときの、社内用テンプレートリポジトリです。標準 Web API を叩く別アプリとして、C#／.NET 10 で作ります。

## 含まれるもの

- 最小の Pleasanter API クライアント（`src/VehicleVision.PleasanterTools.Template.Core`）
- 接続確認の CLI（`src/VehicleVision.PleasanterTools.Template.Cli`）
- 単体テスト（`tests/`）
- AI の事実確認用に Pleasanter 本体ソースを必要時だけ取得する仕組み（本体は Git 管理外）
- CI、CodeQL、依存の脆弱性検査、リリース（`.github/workflows/`）
- AI エージェント向けの共通指示（`.github/copilot-instructions.md`、`AGENTS.md`、`CLAUDE.md`）

## 使い方

接続確認の CLI を動かすには、接続先を環境変数で渡します。

```bash
export PLEASANTER_BASE_URL=https://pleasanter.example.com
export PLEASANTER_API_KEY=...
dotnet run --project src/VehicleVision.PleasanterTools.Template.Cli -- check
```

新しいツールの作り方は [開発ガイド](_documents/開発ガイド.md) を参照してください。

## ライセンス

既定は **AGPL-3.0-or-later と商用ライセンスのデュアルライセンス**です。詳細は [ライセンスについて](LICENSING.md) を参照してください。

| | ライセンス | 想定する利用 |
| --- | --- | --- |
| A | [GNU AGPL v3 以降](LICENSE) | オープンソースとして利用・改変・再配布する場合 |
| B | [株式会社ピー・エム・シー ITソリューション事業部の商用ライセンス](LICENSE-COMMERCIAL.md) | AGPL の義務を負わずに利用したい場合（個別契約） |

この雛形から作るリポジトリも、この提供条件で始まります。作るときの確認事項は [ライセンス](_documents/ライセンス.md) を参照してください。依存のライセンスは [NOTICE](NOTICE) にあります。
