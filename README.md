# SysmonWidget

デスクトップに常駐するシステムリソースモニターです。CPU / メモリ / ディスク / GPU の使用率と Wi‑Fi の送受信速度を表示します。右側パネルでは時計・ポモドーロタイマー・Gmail の最新メールも出せます（メニューで ON/OFF）。

- **Windows 11** … .NET Framework 4 / WinForms の単一 exe
- **macOS 12+** … Intel / Apple Silicon 両対応のユニバーサルアプリ

ライセンスは [MIT](LICENSE) です。

## ダウンロード

[Releases](https://github.com/kheisansan/sysmonWidget/releases) から最新版を取得してください。

| ファイル | 対象 |
|----------|------|
| `SysmonWidget.exe` | Windows |
| `SysmonWidget-mac.zip` | Mac（展開して `SysmonWidget.app` を開く） |

## 主な機能

- CPU / MEM / DISK / GPU の使用率バー
- Wi‑Fi の Up / Down 速度と簡易グラフ
- 文字サイズ・透明度・最前面・自動起動
- 右側パネル（メニューで切り替え）
  - **時計** … デジタル / アナログ
  - **ポモドーロ** … 作業・休憩の間隔設定、開始 / 一時停止
  - **Gmail** … 最新メールの送信元と件名（件数 3〜10、更新間隔設定可）

## ビルド

### Windows

`build.bat` または `build.ps1` を実行します（`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe` が必要です）。

```bat
build.bat
```

### Mac

```bash
./mac/build.sh
open dist/SysmonWidget.app
```

## Gmail API の登録方法

Gmail パネルは **自分用の Google Cloud OAuth クライアント** が必要です。API 利用料は個人のポーリング用途では実質無料枠内で足ります。

### 1. プロジェクトを作成

1. [Google Cloud Console](https://console.cloud.google.com/) を開く
2. 上部のプロジェクト選択 → **新しいプロジェクト**
3. 名前を付けて作成（例: `SysmonWidget`）

### 2. Gmail API を有効化

1. [Gmail API](https://console.cloud.google.com/apis/library/gmail.googleapis.com) を開く
2. 対象プロジェクトを選んで **有効にする**

### 3. OAuth 同意画面（Google Auth platform）

1. **Google Auth platform** を開き、未設定ならセットアップを開始
2. アプリ名（例: `SysmonWidget`）とサポート用メールを入力
3. Audience（ユーザーの種類）は **External（外部）**
4. 連絡先メールを入力して作成

**テストユーザーを追加**（忘れるとログインできません）:

- Audience / テストユーザーに、使う Gmail アドレスを登録

必要なら Data Access に次のスコープを追加します（接続時にも要求されます）:

```
https://www.googleapis.com/auth/gmail.readonly
```

### 4. Desktop 用 OAuth クライアントを作成

1. **Google Auth platform → Clients → Create client**
2. Application type: **Desktop app**（Web アプリにしない）
3. 名前を付けて **Create**
4. 表示された **Client ID** をコピー
5. **Client Secret** も作成直後に控える（後から再表示できないことがあります）

JSON をダウンロードした場合は、中の `client_id` / `client_secret` を使います。

### 5. ウィジェットに登録して接続

1. ウィジェットを右クリック（Mac はメニューバーアイコンでも可）
2. **Gmail → 表示する** を ON
3. **Gmail → Client ID を設定…** に ID と Secret を入力して保存
4. **Gmail → 接続…** → ブラウザで Google アカウントを許可

「このアプリは確認されていません」と出た場合は、自分用テストアプリなので **詳細 → 続行** で進めて問題ありません。

件数は **Gmail → 表示件数**、ポーリング周期は **Gmail → 更新間隔**（15秒〜10分）から変更できます。

### 資格情報の保存場所

| 種類 | Windows | Mac |
|------|---------|-----|
| Client ID / Secret | Credential Manager（`SysmonWidget:GmailOAuthClient`） | Keychain（`com.sysmonwidget.mac.gmail` / `oauth-client`） |
| OAuth トークン | Credential Manager（`SysmonWidget:GmailOAuth`） | Keychain（`com.sysmonwidget.mac.gmail` / `oauth`） |
| その他の設定 | `%AppData%\SysmonWidget\settings.ini` | `~/Library/Application Support/SysmonWidget/settings.ini` |

Client ID / Secret とトークンは OS の資格情報ストアに保存します。`settings.ini` には書きません（旧バージョンで ini に平文保存していた場合は、起動時に自動で移行して ini から削除します）。

## 設定ファイル

位置・サイズ・透明度・パネルの ON/OFF などは上記の `settings.ini` に保存されます。

## ライセンス

[MIT License](LICENSE)
