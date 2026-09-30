# Snap

WPF製 4ペイン ファイルエクスプローラ（Tablacus Explorer 代替）

## ダウンロード

**[最新版をダウンロード](https://github.com/vellivore/Snap/releases/latest)**

`Snap.exe` をダウンロードして実行するだけで使えます。インストール・.NETランタイム不要です。

## 機能

| 機能 | ショートカット | 説明 |
|------|-------------|------|
| 4ペイン | — | 独立タブ付き4分割ペイン。操作対象のペインは青い枠で表示 |
| サイドバー | ホバー | Pinned（ブックマーク）/ Today（最近のフォルダ） |
| コマンドパレット | Ctrl+Space / Ctrl+F | ファイル検索・コマンド実行（テキスト入力中の Ctrl+Space は入力欄に渡す） |
| ターミナル | Ctrl+T | フローティング PowerShell（Esc で閉じる） |
| コンテキストメニュー | 右クリック / Shift+右クリック | Windows Shell ネイティブメニュー。Shift で拡張メニュー（「PowerShell ウィンドウをここで開く」など） |

### キーボード操作

| 操作 | キー |
|------|------|
| ペイン 1〜4 へ移動（左上・右上・左下・右下） | Ctrl+1 〜 Ctrl+4 |
| 上のフォルダへ | Backspace / Alt+↑（一覧の空白をダブルクリックでも可） |
| 戻る / 進む | Alt+← / Alt+→（マウスの戻る・進むボタンでも可） |
| アドレスバー | Ctrl+L / Alt+D / F4 |
| 新しいタブ（今のフォルダで開く） | Ctrl+N（＋ボタン・パレットの new tab も同じ） |
| タブを閉じる | Ctrl+W / タブを中クリック |
| 次のタブ / 前のタブ | Ctrl+Tab / Ctrl+Shift+Tab |
| フォルダを新しいタブで開く | フォルダを中クリック（背面で開く） / Ctrl+ダブルクリック（前面で開く） |
| タブの複製・他のタブを閉じる | タブを右クリック →「複製」「他を閉じる」 |
| 名前の先頭で移動 | 一覧で文字を入力（日本語は IME で確定した文字で移動） |
| その場で絞り込み | Ctrl+Shift+F（名前の部分一致・Esc で解除・別のフォルダへ移ると解除） |
| 開く | Enter（ファイルだけを選んでいれば全部開く。フォルダを含むときは先頭のフォルダへ移動） |
| プロパティ | Alt+Enter |
| フルパスをコピー | Ctrl+Shift+C（複数は改行区切り。未選択なら今のフォルダ） |
| 名前の変更 / ごみ箱へ / 更新 | F2 / Delete / F5 |
| コピー / 切り取り / 貼り付け | Ctrl+C / Ctrl+X / Ctrl+V |

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
dotnet publish Snap -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/
```
