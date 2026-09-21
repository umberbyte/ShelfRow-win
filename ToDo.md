# ToDo — 残作業一覧

2026-09-22 時点。mac 版ソース (`C:\Users\gsuga\src\ShelfRow`) と突き合わせた監査の結果。
背景と、なぜこういう欠落の仕方をしているかは [`ANTIGRAVITY_CODE_ANALYSIS.md`](./ANTIGRAVITY_CODE_ANALYSIS.md) を参照。

各項目は単体で着手できるよう、**現象・根拠・mac 版の挙動・修正方針**を書いてある。
着手前に根拠のファイルを実際に開いて、記述がまだ有効か確認すること(この文書も古くなる)。

---

## 優先度A — データを壊す / 機能が成立しない

### [x] A-1. インポートにマージ戦略が無い（再インポートで蔵書が倍増する）

**最も危険。かつ修正は小さい。**

- **現象**: `Stackroom Library.xml` を2回インポートすると、全ての本と本棚が複製される。
- **根拠**: [`StackroomXmlImporter.cs`](src/ShelfRow.Importer/StackroomXmlImporter.cs) に既存項目の照合が一切無い。
- **mac 版**: `LibraryImporter.swift` が取り込み前に既存を fetch し、`legacyID` と `relativePath` の
  両方で照合してスキップする。本棚も同様。`Item.swift` のコメントにも
  「`legacyID` は `LibraryImporter` が挿入前に fetch することで一意に保たれる」と明記。
- **修正方針**: インポータに既存項目の辞書（`legacyID` → Item、`relativePath` → Item）を渡し、
  ヒットしたらスキップして本棚の関連付けにだけ使う。リポジトリ側に一括取得が要る。
- **実装済み (2026-09-22)**: `legacyID`、次に `relativePath` で既存書籍を照合し、タイトルと種別が
  同じ既存棚および既存ボリュームも再利用するよう修正。棚関連を含む一括取得 API と回帰テストを追加。

### [x] A-2. `CoverVersion` による差分計算が実装されていない（表紙が永久に更新されない）

- **現象**: mac 側で表紙を作り直しても Windows 側は古いまま。ローカルにファイルがあるかしか見ていない。
- **根拠**: `CoverVersion` は [`ItemViewModel.cs:246`](src/ShelfRow.App/ViewModels/ItemViewModel.cs#L246)
  で公開されるのみで、**どこからも使われていない**。`LocalCoverState` 相当も無い。
  マニフェストは `Version` を持つのに、同期候補の判定は `Bytes` のみ
  ([`ThumbnailStorageManager.cs`](src/ShelfRow.Storage/ThumbnailStorageManager.cs) の `SyncAllThumbnailsFromNasAsync`)。
- **mac 版**: `LocalStore.swift` の `LocalCoverState` がこの端末が持つ版を記録し、
  iCloud 経由で来る `Item.coverVersion` と比較するだけで差分計算が完結する。
  「だから2万件のファイルをネットワーク越しに列挙せずに済む」と明記されている。
  失敗回数も記録し、3回で諦める。
- **付随する不具合**:
  - マニフェストが無いと 256 シャードを全列挙する（mac が明示的に避けている動作）
  - 失敗の記録が無いため、NAS に存在しない本のサムネイルをスクロールのたびに取りに行く
- **修正方針**: ローカルに `LocalCoverState` 相当のテーブル（ItemId / Version / Bytes / 失敗回数）を持たせ、
  取得判定とスクロール時のフェッチをこれで行う。**この表は端末固有なので iCloud に送らないこと。**
- **実装済み (2026-09-22)**: 現行mac版に合わせ、NASマニフェストの版と端末固有
  `LocalCoverStates` を比較する方式を実装。表示時取得にも適用し、同一版の失敗は3回で停止する。
  マニフェスト不在時の256シャード全走査は廃止した。

### [ ] A-3. 書影の生成経路が存在しない

- **現象**: Windows 版は書影を1枚も作れない。NAS 配布フォルダに mac が置いたものを受け取るだけ。
  mac 側が未処理の本は永久に表紙なし。
- **根拠**: `ZipExtractor.swift` (251行) 相当が無い。`CoverExtractionRecord` は Core に型だけ存在し、
  抽出処理はどこにもない。
- **mac 版**: `ZipExtractor.swift` + `ItemFileAccess.swift` が ZIP / フォルダからページを列挙し、
  `bestCoverPage` / `orderedCoverCandidates` で表紙候補を選ぶ（連番判定・モノクロ判定・
  画像の完全性チェックまで行う）。
- **修正方針**: 大きい。まず ZIP からの1枚抽出だけを通し、候補選択ロジックは後で移植する。
  `System.IO.Compression` で足りる。A-2 と繋がる（生成したら `CoverVersion` を上げる）。

### [ ] A-4. サムネイル配布の「提供側」が全て未配線

- **現象**: Windows は NAS 配布フォルダから受け取るだけで、一度も貢献しない。
- **根拠**: `CopyToNasDistributionAsync` / `WriteManifestAsync` / `SetEntry` / `HasLocalThumbnail` /
  `FindNasThumbnailFile` の**5つとも、自ファイル以外から呼ばれていない**。
- **修正方針**: A-3 の後。生成した書影を配布フォルダへ書き、マニフェストを更新する経路を繋ぐ。

---

## 優先度B — 静かに間違う

### [ ] B-1. 旧 Stackroom のサムネイルを移行していない

- **根拠**: mac 版はインポート時に `Stackroom Library/[ID]/thumbnail.jpg` をキャッシュへコピーする
  （「ユーザーが旧アプリのフォルダをすぐ削除できるように」と明記）。Windows 版に該当処理が無い。
- **影響**: A-3 と合わせ、移行してきたユーザーは書影を失う。
- **修正方針**: インポート時に旧アセットフォルダのパスを受け取り、`[legacyID]/thumbnail.jpg` を
  `GetLocalThumbnailPath(item.Id)` へコピーする。

### [ ] B-2. 外部キーが強制されておらず、インポートが順序事故に依存している

- **根拠**: スキーマに `FOREIGN KEY` 宣言はあるが `PRAGMA foreign_keys = ON` が無い。
  インポートは本を先に保存し（このとき `ItemShelves` 行が作られる）、本棚を後から保存する。
  FK が効いていればここで失敗する。**効いていないから動いている**状態。
- **修正方針**: 保存順を本棚 → 本に変える。そのうえで `PRAGMA foreign_keys = ON` を有効にするか、
  意図的に無効なら理由をコメントで残す。有効化は既存DBへの影響を確認してから。

### [ ] B-3. 本棚メンバーシップを iCloud へ送信していない

- **現象**: Windows で本を棚に入れても mac に反映されない。受信側は実装済みなので逆方向は動く。
- **根拠**: `CloudKitSyncEngine.SyncUpAsync` は Item / Shelf / Volume のみ送る。
- **mac 版のデータ形式**: `CDMR` 結合レコード。`CD_entityNames` = `"Item:Shelf"`、
  `CD_recordNames` = `"<item>:<shelf>"`、`CD_relationships` = `"shelves:items"`。
  読み取りは `CloudKitMapper.ToManyToManyLink` に実装済み。
- **修正方針**: 追加・削除を追跡する必要がある（`ItemShelves` に PendingUpload 相当の列）。
  新規リンクは CDMR レコードを create、削除は保存済みの `CloudKitRecordName` で delete。
  **実データで往復を確認するまで完了としないこと。**

---

## 優先度C — UI はあるが効いていない設定

いずれも**値がどこからも読まれていない**。使えると誤解させる分、無いより悪い。
実装するか、UI を消すかを決めること。

- [ ] **C-1. セキュリティ > 簡易ロック** — `PasswordLockEnabled` / `PasswordValue` 参照ゼロ。**ロックされない**
- [ ] **C-2. カスタマイズ > 独自フィールド名** — `EffectiveGenreLabel` ほか参照ゼロ（作者名のみ1箇所で使用）
- [ ] **C-3. カスタマイズ > 独自タイプ名** — `GetEffectiveTypeName` 参照ゼロ
- [ ] **C-4. キーワード等価ルール** — `KeywordRules` 参照ゼロ。mac の `KeywordEquivalenceCodec`
      （検索語を等価語に展開する仕組み）に相当するものが無い
- [ ] **C-5. カスタマイズ > リネーム書式** — `CustomRenameFormat` 参照ゼロ

---

## 優先度D — 未移植の画面

- [ ] **D-1. スマートシェルフの編集** — mac `SmartShelfEditorView.swift` (370行)。
      **現状は作成も編集もできない**。読み取りと条件評価は実装済みなので表示はされる
- [ ] **D-2. 表紙の差し替え** — mac `CoverEditorView.swift` (220行)。A-3 の後
- [ ] **D-3. スタンプ** — mac `StampBarView.swift` (143行)

---

## 優先度E — 個別の軽微なバグ

- [x] **E-1.** [`ThumbnailImageLoader.cs:100`](src/ShelfRow.App/Services/ThumbnailImageLoader.cs#L100)
      `TryEnqueue` の戻り値を見ていない。失敗すると `TaskCompletionSource` が永久に完了せず、
      その `await` がハングし `_inFlightTasks` にも残り続ける
- [x] **E-2.** 同ファイル `:59` `ConcurrentDictionary.GetOrAdd` のファクトリは同一キーで
      複数回実行され得るため、重複ロードが走る可能性がある
- [ ] **E-3.** [`ApplePlistParser.cs:61`](src/ShelfRow.Importer/ApplePlistParser.cs#L61)
      日付の解析失敗時に `DateTime.UtcNow` を代入している。壊れたデータが
      「今日追加された本」として静かに紛れ込む。失敗は null にすべき
- [ ] **E-4.** 同ファイル `:36` `List<byte>` に1バイトずつ追加している。数十MBの XML でメモリを倍使う

---

## 完了済み（再調査不要）

- CloudKit Web Services の認証（API トークン埋め込み、WebView2 サインイン、資格情報マネージャー保管、自動再試行）
- 受信同期（Item / Shelf / Volume / CDMR、ボリューム参照の解決、削除の伝搬、増分同期）
- 送信同期（Item / Shelf / Volume。本棚メンバーシップは B-3 で残）
- スマートシェルフの条件評価（SQL へ変換。編集 UI は D-1 で残）
- ヘルパーによる本の起動（ダブルクリック / Enter、mac と同じ解決順序、既読化）
- NAS ボリュームのパス割り当て
- 一覧の仮想化・バックグラウンド読み込み・同期のスレッド分離
- Stackroom XML の制御文字サニタイズ（実装済みだった）

## 調査に使える道具

`tools/ShelfRow.CloudKitProbe` — 実データの確認用。推測で判断しないこと。

```
dotnet run --project tools/ShelfRow.CloudKitProbe -- <apiToken> development          # ゾーンをダンプして構造を出力
dotnet run --project tools/ShelfRow.CloudKitProbe -- --analyze <zone-dump.json>      # 保存済みダンプを再解析（通信不要）
dotnet run --project tools/ShelfRow.CloudKitProbe -- --sync <apiToken> development   # 使い捨てDBへ実同期
```
