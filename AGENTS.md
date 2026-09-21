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
  * 全 18 件の単体テスト（100% 合格）
  * 初期 Git コミット完了
* **次のタスク**:
  * Windows 実機での WinUI 3 アプリ起動・ビルド検証
  * サムネイル画像非同期ロード用の ImageSource 変換コンバーターの実装
  * Windows 資格情報マネージャー（Credential Locker / DPAPI）による Apple ID WebAuth トークンのセキュア保管
