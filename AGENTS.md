# AI Agent Instructions for ShelfRow for Windows

## 1. プロジェクト概要と基本方針
* **背景と目的**:
  * macOS / iPadOS 版「ShelfRow」の Windows ネイティブ版。
  * かつて愛された macOS 用蔵書管理アプリ「Stackroom」の資産・軽快性・操作性を Windows 環境へ継承。
  * 電子書籍・コミック等の大量ファイル（数万冊規模）を扱うため、**「軽量・高速・低メモリ消費」** を最重要方針とする。
* **主要な要件**:
  1. **iCloud (CloudKit) 連携**: `iCloud.com.eureka.ShelfRow` コンテナ内の `com.apple.coredata.cloudkit.zone` と CloudKit Web Services (REST API) 経由で双方向同期。
  2. **NAS ボリューム管理**: 実体ファイルは NAS 上に配置。macOS の `/Volumes/Books/...` と Windows の `\\NAS\Books\...` を自動マッピング。
  3. **サムネイル分散キャッシュ**: サムネイル画像は iCloud に送らず、NAS 上の `ShelfRowThumbnails` フォルダを介して共有・同期。
  4. **Stackroom XML インポート**: `Stackroom Library.xml` のパース、不正な XML 1.0 制御文字の自動サニタイズ。

---

## 2. アーキテクチャとコーディング規約 (.NET 10 / C#)
* **ソリューション構造の厳守**:
  * `ShelfRow.Core`: ドメインモデル・インターフェース（外部依存ゼロ）。
  * `ShelfRow.Data`: SQLite リポジトリ（`Microsoft.Data.Sqlite` による高速・低オーバーヘッドアクセス）。
  * `ShelfRow.CloudKit`: Apple CloudKit Web Services クライアントおよび CoreData レコードマッパー。
  * `ShelfRow.Storage`: パス変換（POSIX <-> UNC/ドライブ）およびサムネイル共有キャッシュ管理。
  * `ShelfRow.Importer`: Stackroom XML (Apple plist) ストリーミングパーサー。
  * `ShelfRow.App`: WinUI 3 (Windows App SDK) デスクトップ UI。
* **非同期処理と UI 応答性**:
  * ファイル I/O、SQLite クエリ、ネットワーク通信はすべて `async/await`（`CancellationToken` 対応）で行う。
  * UI スレッド（DispatcherQueue）を長時間ブロックする重い処理は絶対に実行しない。
* **大量データの高速描画**:
  * 書影グリッドには UI 仮想化（`ItemsRepeater` や仮想化パネル）を適用し、画面外のセル描画負荷を抑制する。
  * 画像ロードは非同期で行い、メモリキャッシュとディスクキャッシュを併用する。
* **テストファースト**:
  * コアロジック、データ変換、マッパーの追加・改修時は `tests/` 配下の xUnit プロジェクトに対応する単体テストを必ず作成する。

---

## 3. 実装判断マトリクス

| 領域 | 推奨（Do） | 非推奨・禁止（Don't） |
|---|---|---|
| **データアクセス** | `Microsoft.Data.Sqlite`, WALモード, トランザクション一括処理 | 重厚な重量級ORM（大量件数でのメモリ浪費） |
| **CloudKit連携** | CloudKit Web Services REST API, 増分同期 (Change Token) | 全件常時再取得 |
| **パス処理** | `VolumePathResolver` による POSIX/UNC 抽象化 | Windows/macOS固有パスのハードコード |
| **UIフレームワーク** | WinUI 3 (Windows App SDK), Fluent Design, XAML | Electron / 重いWebビューラッパー |

---

## 4. 現在の実装状況と次のステップ
* **完了済み**:
  * 全 5 プロジェクトのコアロジック実装（Core, Data, CloudKit, Importer, Storage）
  * 全 24 件の単体テスト（100% 合格）
  * macOS 版 UI の完全移植（`ContentView.swift` に準拠した 3 ペイン構成：サイドバー、中央グリッド/リスト＋フィルターバー、詳細インスペクター）
  * macOS 版設定画面の完全移植（`PreferencesView.swift` に準拠した 8 タブ設定：一般、ビューア、ヘルパー、キーワード、カスタマイズ、iCloud、セキュリティ、保守）
  * `AppSettings` モデル、JSON 永続化サービス（`AppSettingsService`）、および ViewModel バインディングの実装
  * Windows 実機での WinUI 3 アプリ起動・ビルド検証（`ShelfRow.App` 起動、SQLite 初期化、ウィンドウ表示確認）
  * VS Code / Antigravity IDE での実行・デバッグ構成整備（`launch.json`, `tasks.json`, `run.ps1`）
* **次のタスク**:
  * [`ToDo.md`](file:///c:/Users/gsuga/src/ShelfRow-win/ToDo.md) を参照（mac 版と突き合わせた監査結果。優先度順）。
    最優先は「再インポートで蔵書が倍増する」「mac で更新した表紙が Windows に反映されない」の2件。

---

## 5. 重要な知見・トラブルシューティング
* **着手前に必ず読むこと**:
  * [`ANTIGRAVITY_CODE_ANALYSIS.md`](file:///c:/Users/gsuga/src/ShelfRow-win/ANTIGRAVITY_CODE_ANALYSIS.md)
  * 継承コードで実際に破綻していた6つの傾向（外部契約の未検証、難所の欠落、エラーの不可視、実規模未検証、
    WinUI 3 固有挙動の取り違え、部品の未配線）と、再発を防ぐチェックリスト、未検証領域の予測がある。
  * このプロジェクトには「テストが全件通る」「部品が揃っている」が動作を全く保証しなかった前例がある。
* **WinUI 3 Window と Converter**:
  * WinUI 3 の `Window` は `FrameworkElement` ではないため、Window レベルで `{x:Bind ..., Converter={StaticResource ...}}` を使用すると `CS1503` エラーになる。Converter ではなく ViewModel 側に直接プロパティ（`Brush`, `Visibility` 等）を設ける。
* **未定義 StaticResource による実行時例外**:
  * 未定義の XAML リソースはビルド時ではなく起動時の `InitializeComponent()` で `XamlParseException` を引き起こす。必ずインラインスタイルまたは `App.xaml` で定義されていることを確認する。
* **対話型セッションと仮想デスクトップ**:
  * エージェント環境（分離デスクトップ）からユーザーの画面（`Default` デスクトップ）へウィンドウを表示させるには、`schtasks /create ... /it` による対話型起動を用いる。
* **詳細な開発履歴・振り返り**:
  * 成功要因・失敗過程の詳細は [`PROJECT_REPORT.md`](file:///c:/Users/gsuga/src/ShelfRow-win/PROJECT_REPORT.md) を参照。



