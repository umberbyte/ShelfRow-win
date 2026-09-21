# ShelfRow for Windows

**ShelfRow for Windows** は、macOS / iPadOS 版「**ShelfRow**」の設計思想（かつて愛された蔵書管理アプリ「Stackroom」のコンセプト・軽快性・資産を継承し、NAS運用を前提とした書庫管理）をそのまま Windows ネイティブ環境向けに提供するアプリケーションです。

動作が遅くなりやすい電子書籍・コミック管理において、徹底した軽量・高速性を追求し、数万冊規模のライブラリでも軽快に動作する設計を行っています。

---

## 🌟 主な特徴

- **iCloud (CloudKit) 双方向同期**:
  - macOS版およびiPad版（SwiftData / CoreData CloudKit Mirroring）と完全に同一の `iCloud.com.eureka.ShelfRow` コンテナ、`com.apple.coredata.cloudkit.zone` と同期。
  - Apple公式の **CloudKit Web Services REST API** を利用し、Windows環境からセキュアに蔵書メタデータを双方向同期します。
- **NAS運用に特化したボリューム管理**:
  - 書籍ファイルの実体はNASに配置。
  - macOSのPOSIXパス（`/Volumes/Books/...`）と WindowsのUNCパス（`\\NAS\Books\...`）またはドライブ文字（`Z:\...`）の自動相互変換・マッピング。
- **サムネイルのNAS分散キャッシュ共有**:
  - 著作権保護および容量（ギガバイト単位）の観点から、サムネイル画像はiCloudを経由せず、NAS上の共有フォルダ（`ShelfRowThumbnails`）を介してMac・iPad・Windows間で配布・同期します。
- **Stackroom からの資産移行**:
  - `Stackroom Library.xml` のストリーミングインポートに対応。不正な制御文字を自動除去して安全にインポート。
  - 書籍情報、フォルダ／本棚、スマート本棚条件、評価（レート）、未読状態を完全再現。
- **Windows ネイティブ & 高速仮想化UI**:
  - C# / .NET 10 + WinUI 3 (Windows App SDK) による Fluent Design。
  - UI仮想化技術により、大量の書影（サムネイル）を省メモリかつ滑らかにスクロール表示。

---

## 🏛 アーキテクチャ構成

クロスプラットフォーム検証が容易なように、コアロジックをUIから完全に分離した疎結合なマルチプロジェクト構成を採用しています。

```
ShelfRow for Windows/
├── src/
│   ├── ShelfRow.Core/       # ドメインモデル (Item, Shelf, Volume) および抽象インターフェース
│   ├── ShelfRow.Data/       # 高速SQLiteリポジトリ (ローカルキャッシュ・検索・インデックス)
│   ├── ShelfRow.CloudKit/   # Apple CloudKit Web Services クライアント & 差分同期エンジン
│   ├── ShelfRow.Importer/   # Stackroom XML (Apple plist) ストリーミングパーサー
│   ├── ShelfRow.Storage/    # NASボリュームパス変換 & サムネイル共有マネージャー
│   └── ShelfRow.App/        # WinUI 3 (Windows App SDK) デスクトップアプリケーション
└── tests/
    ├── ShelfRow.Core.Tests/
    ├── ShelfRow.Data.Tests/
    ├── ShelfRow.CloudKit.Tests/
    ├── ShelfRow.Importer.Tests/
    └── ShelfRow.Storage.Tests/
```

> [!NOTE]
> `ShelfRow.Core`、`ShelfRow.Data`、`ShelfRow.CloudKit`、`ShelfRow.Importer`、`ShelfRow.Storage` およびテスト群は、プラットフォーム非依存の純粋な .NET 10 ライブラリです。macOS環境上でも全単体テストを即座にビルド・実行（`dotnet test`）できます。

---

## 🚀 開発・テスト手順

### 1. 単体テストの実行（macOS / Windows 共通）
```bash
dotnet test
```
モデル変換、SQLite CRUD、Stackroom XMLパース、NASパス変換、CloudKitレコードマッピングの全テストが実行されます。

### 2. Windows 実機またはVMでの実行
Windows 10 (1809以降) または Windows 11 環境で：
```bash
dotnet run --project src/ShelfRow.App/ShelfRow.App.csproj
```

---

## 📁 データ互換性仕様

| エンティティ | SwiftData / CoreData 型 | Windows .NET 型 | CloudKit レコード型 |
| :--- | :--- | :--- | :--- |
| 書籍 | `Item` | `ShelfRow.Core.Models.Item` | `CD_Item` |
| 本棚 | `Shelf` | `ShelfRow.Core.Models.Shelf` | `CD_Shelf` |
| ボリューム | `Volume` | `ShelfRow.Core.Models.Volume` | `CD_Volume` |
| カバー抽出 | `CoverExtractionRecord` | `CoverExtractionRecord` | `CD_CoverExtractionRecord` |

---

## 📄 ライセンス

Copyright (c) 2026 Go Sugawara. All rights reserved.
