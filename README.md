# Snap

WPF製 4ペイン ファイルエクスプローラ（Tablacus Explorer 代替）

## ダウンロード

**[最新版をダウンロード](https://github.com/RyoichiOkishima/Snap/releases/latest)**

`Snap.exe` をダウンロードして実行するだけで使えます。インストール・.NETランタイム不要です。

## 機能

| 機能 | ショートカット | 説明 |
|------|-------------|------|
| 4ペイン | — | 独立タブ付き4分割ペイン |
| サイドバー | ホバー | Pinned（ブックマーク）/ Today（最近のフォルダ） |
| コマンドパレット | Ctrl+Space / Ctrl+F | ファイル検索・コマンド実行 |
| ターミナル | Ctrl+T | フローティング PowerShell |
| コンテキストメニュー | 右クリック | Windows Shell ネイティブメニュー |

### コマンドパレットの使い方

| 入力 | 動作 |
|------|------|
| テキスト | サブフォルダ含むファイル名検索 |
| `*.cs` / `ext:cs` | 拡張子フィルタ付き検索 |
| `content:keyword` | ファイル内容検索（grep） |
| `/command` | アプリコマンド（new tab, close tab, refresh, settings, terminal） |
| `C:\path` / `~` | パスナビゲート |

## 技術スタック

- C# / .NET 8.0 / WPF / MVVM (CommunityToolkit.Mvvm)
- Shell COM API (IContextMenu) によるネイティブコンテキストメニュー

## ビルド

```
dotnet build
```

自己完結型バイナリ:
```
dotnet publish Snap/Snap.csproj -c Release --self-contained -r win-x64 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish
```
