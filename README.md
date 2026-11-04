# ONLINE RETURN

Windows のインターネット接続復帰を検知して、アラーム音・警告表示・Windows音量操作で知らせるデスクトップアプリです。

## 主な機能

- 約5秒間隔で外部HTTPS接続をチェック
- 連続2回の成功／失敗でオンライン・オフライン状態を確定し、瞬断による誤検知を抑制
- オフラインからオンラインに戻ったときだけアラームを実行
- 高めの周波数を使った短いスイープ音をループ再生
- アプリ画面を前面へ表示し、タスクバーも点滅
- 常に最前面にする設定
- 警告パネルのゆるやかな点滅
- Windowsマスター音量の変更（初期状態はOFF）
- アラーム停止時に変更前の音量とミュート状態を復元
- アプリの設定を %AppData%\OnlineReturnAlarm\settings.json に保存

## Windows用EXE

GitHub の **Actions** タブから最新の Build Windows app ワークフローを開き、Artifacts の online-return-alarm-win-x64 をダウンロードしてください。ZIPを展開すると OnlineReturnAlarm.exe が入っています。

このアプリは Windows 10 / 11 x64 向けです。初回起動時に SmartScreen が表示される場合があります。発行元署名のないEXEであることを確認して、自分でビルドした成果物だけを実行してください。

## 使い方

1. OnlineReturnAlarm.exe を起動します。
2. 必要な警告方法を選びます。
3. 「監視を開始」を押します。
4. オフラインが連続2回確認された後、オンラインが連続2回確認されると警告します。
5. 「アラーム停止」で音と警告表示を停止します。監視は継続します。

「監視を停止」を押すとネットワーク監視も停止します。

## Windows音量について

「Windowsのマスター音量を変更する」は明示的に有効にしない限り動作しません。有効にした場合、復帰時に選択した音量へ変更し、アラーム停止時に変更前の音量・ミュート状態へ戻します。

音量100%は聴覚に危険な場合があります。最初は低い値でテストし、ヘッドホンやイヤホンでは特に注意してください。物理ボリューム、外部スピーカーの電源、ハードウェア側のミュートなどはソフトウェアから解除できません。

## ビルド

Visual Studio 2022 または .NET 8 SDK を用意して、Windows上で実行します。

\`\`\`powershell
dotnet restore .\src\OnlineReturnAlarm\OnlineReturnAlarm.csproj
dotnet publish .\src\OnlineReturnAlarm\OnlineReturnAlarm.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish
\`\`\`

ビルド成果物は publish\OnlineReturnAlarm.exe です。

## 制限事項

- アプリを終了したり、PCがシャットダウン／スリープしたりすると監視できません。
- Windowsのロック画面、UACのセキュアデスクトップ、他アプリの保護画面を突破する機能はありません。
- Windowsのフォーカス制御により、状況によっては前面表示が制限されます。
- ネットワーク監視はMicrosoftまたはGoogleの疎通確認エンドポイントを利用します。組織のファイアウォールやプロキシによっては判定できないことがあります。
- ブラウザ版ではなく、Windows上で動作するネイティブアプリです。
