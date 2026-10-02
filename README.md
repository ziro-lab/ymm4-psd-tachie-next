# ymm4-psd-tachie-next

**開発中・一般利用向けの配布は未開始です。** 未完成のソースと合成テストを公開する開発用Repoです。インストール用パッケージ、release、完成版としての動作保証はありません。

YMM4で元PSD/PSBを非同期に準備し、編集時の表示と共有キャッシュを扱う実装を開発しています。現在はRGB8の限定プロファイル、レイヤー/グループの描画、要求世代とdevice epoch、素材単位の準備共有、同内容キャッシュ再利用、取消・借用の寿命管理、最小の保存・素材列挙が対象です。選択素材の未使用先読みは暫定16 MiB・30秒です。

表情操作、目口、Extend等の完成機能は未実装です。通常の動画出力について、冷キャッシュで全frameを正しく完成させること、エラーでjobを停止すること、準備待ち中のnative Cancel、出力中の参照変更はまだ合格していません（H-A2）。ローカル試験は冷出力の全frame判定失敗と保存ダイアログ自動操作timeoutまでで、即時取消を保証しません。

初期CIは合成素材だけでpure、準備/共有runtime、Windows WARP renderer回帰を実行します。公式YMM4 4.56.1.0 Liteをjob内だけで取得し、versionとSHA256を確認してアダプターをbuildします。アダプターbuildやWARP回帰は実YMM4の通常タイムライン・動画writer成功・物理GPU性能の証明ではありません。最新版の変更時には版とhashを見直します。

開発環境は.NET SDK 10.0.401です。固定MIT parserの準備後に、例えば次を実行します。

```powershell
python eng/prepare_parser.py
dotnet run --project tests/PsdTachieNext.Tests -c Release -- test-results.json
dotnet run --project tests/PsdTachieNext.PreparationTests -c Release -- preparation-results.json
```

YMM4アダプターは `-p:YMM4DirPath=<独立した公式ホストのディレクトリ>` を指定してbuildします。検証用host proofには自動操作・制御gateがあります。通常利用のYMM4や個人プロジェクトへ配置せず、独立ホストと合成入力で使ってください。

自作部分は[MIT](LICENSE)。parser、Vortice、SharpGenの出所・固定版・全文ライセンスは[第三者notice](THIRD_PARTY_NOTICES.md)に記載しています。旧拡張PSDプラグインのコード/配布物、YMM4本体、ユーザー素材やその派生cacheは含みません。compiled cacheは元素材の画素やレイヤー情報を保持するため、匿名化したデータとして公開しないでください。
