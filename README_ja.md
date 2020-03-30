# App Service Acmebot

[![Build Status](https://dev.azure.com/shibayan/azure-acmebot/_apis/build/status/Build%20appservice-acmebot?branchName=master)](https://dev.azure.com/shibayan/azure-acmebot/_build/latest?definitionId=37&branchName=master)
[![Release](https://img.shields.io/github/release/shibayan/appservice-acmebot.svg)](https://github.com/shibayan/appservice-acmebot/releases/latest)
[![License](https://img.shields.io/github/license/shibayan/appservice-acmebot.svg)](https://github.com/shibayan/appservice-acmebot/blob/master/LICENSE)

これは Azure App Service 向けに Let's Encrypt 証明書の発行と更新を自動化するためのアプリケーションです。以下のような課題を解決するために開始しました。

- 複数の App Service への対応
- 簡単にデプロイと設定が完了する
- 信頼性の高い実装
- モニタリングを容易に (Application Insights, Webhook)

単一のアプリケーションで複数の App Service 証明書の管理が行えます。

## 注意

### Acmebot v3 へのアップグレード

https://github.com/shibayan/appservice-acmebot/issues/138

### Key Vault との統合

証明書を様々なサービスで利用する必要がある場合は、Key Vault 版の Acmebot v3 の利用を検討してください。

https://github.com/shibayan/keyvault-acmebot

Key Vault 版は App Service / Application Gateway / CDN / Front Door といった Key Vault 証明書に対応したサービスで利用することが出来ます。

## 目次

- [対応している機能](#対応している機能)
- [必要なもの](#必要なもの)
- [開始する](#開始する)
- [使い方](#使い方)
- [トラブルシューティング](#トラブルシューティング)
- [謝辞](#謝辞)
- [ライセンス](#ライセンス)

## 対応している機能

- Azure Web Apps と Azure Functions (Windows)
- Azure Web Apps (Linux) / Web App for Containers (Windows と Linux, Azure DNS が必要)
- Azure App Service Environment (Windows と Linux)
- デプロイスロットへの証明書の発行
- Zone Apex ドメイン向けの証明書の発行
- SANs (サブジェクト代替名) を持つ証明書の発行 (1 つの証明書で複数ドメインに対応)
- ワイルドカード証明書 (Azure DNS が必要)
- 単一アプリケーションで複数の App Service に対応

## 必要なもの

- Azure サブスクリプション
- カスタムドメインを登録済みの App Service
- E メールアドレス (Let's Encrypt の利用登録に必要)

## 開始する

### 1. Acmebot をデプロイする

<a href="https://portal.azure.com/#create/Microsoft.Template/uri/https%3A%2F%2Fraw.githubusercontent.com%2Fshibayan%2Fappservice-acmebot%2Fmaster%2Fazuredeploy.json" target="_blank">
  <img src="https://azuredeploy.net/deploybutton.png" />
</a>

<a href="http://armviz.io/#/?load=https%3A%2F%2Fraw.githubusercontent.com%2Fshibayan%2Fappservice-acmebot%2Fmaster%2Fazuredeploy.json" target="_blank">
  <img src="http://armviz.io/visualizebutton.png" />
</a>

### 2. App Service 認証を有効化する

Azure Portal にて `認証/承認` メニューを開き、App Service 認証を有効化します。「要求が認証されない場合に実行するアクション」として `Azure Active Directory でのログイン` を選択します。認証プロバイダーとして Azure Active Directory を利用することを推奨していますが、他のプロバイダーでもサポート外ですが動作します。

![Enable App Service Authentication with AAD](https://user-images.githubusercontent.com/1356444/49693401-ecc7c400-fbb4-11e8-9ae1-5d376a4d8a05.png)

認証プロバイダーとして Azure Active Directory を選択し、管理モードとして `簡易` を選択し「OK」を選択します。

![Create New Azure AD App](https://user-images.githubusercontent.com/1356444/49693412-6f508380-fbb5-11e8-81fb-6bbcbe47654e.png)

最後にこれまでの設定を保存して、App Service 認証の有効化が完了します。

### 3. 対象のリソースグループへアクセス制御 (IAM) を追加する

対象のリソースグループの `アクセス制御 (IAM)` を開き、デプロイしたアプリケーションに対して `Web サイト共同作成者` と `Web プラン共同作成者` のロールを割り当てます。

![Assign a role](https://user-images.githubusercontent.com/1356444/43694372-feaefda4-996d-11e8-9ee5-e58254ec05f5.png)

![IAM settings](https://user-images.githubusercontent.com/1356444/44624857-e169c900-a934-11e8-982c-5ad8c163beff.png)

**備考**

もし対象の App Service と紐づく App Service Plan が別々のリソースグループに存在する場合、App Service が存在するリソースグループには `Web サイト共同作成者` を、App Service Plan が存在するリソースグループには `Web プラン共同作成者` を割り当てる必要があります。

## 使い方

### 新しく証明書を発行する

ブラウザで `https://YOUR-FUNCTIONS.azurewebsites.net/add-certificate` へアクセスして、Azure Active Directory で認証すると Web UI が表示されます。その画面から対象の App Service とドメインを選択して実行すると、数十秒後に証明書の発行が完了します。

![Add certificate](https://user-images.githubusercontent.com/1356444/49693421-b3dc1f00-fbb5-11e8-8ac1-37092a2be711.png)

`アクセス制御 (IAM)` の設定が正しくない場合には、ドロップダウンリストには何も表示されません。

### ワイルドカード証明書もしくは Linux 向けの証明書を発行する

ワイルドカード証明書もしくは Linux 向けの証明書を発行するには Azure DNS が必要なため、対象の DNS ゾーンが含まれているリソースグループにて `DNS ゾーンの共同作成者` のロールを割り当てます。

![IAM settings](https://user-images.githubusercontent.com/1356444/44642883-3840d280-aa09-11e8-9346-faa26f9675af.png)

App Service on Linux や Web App for Containers 向けに証明書を発行するためには、常に Azure DNS が必要となります。

### 証明書の更新

アプリケーションは数日に 1 回、発行者が `Let's Encrypt Authority X3` もしくは `Let's Encrypt Authority X4` の証明書に対して有効期限のチェックを実行します。

デフォルトのチェックタイミングは UTC の 00:00 となります。タイムゾーンに合わせた変更が必要な場合には `WEBSITE_TIME_ZONE` を使ってタイムゾーンを設定してください。

### 新しいバージョンのデプロイ

このアプリケーションは自動的に更新されるため、常に最新バージョンを利用することが出来ます。明示的に最新バージョンをデプロイする必要がある場合は、Azure Function を再起動します。

## トラブルシューティング

**Causes Azure REST API error at GetSite or Dns01Precondition** エラーが発生する

対象のリソースグループへのロール割り当てが間違っているか、まだ反映されていない可能性があります。IAM 設定の反映には 30 分ほどかかる可能性があります。

## 謝辞

- [ACMESharp Core](https://github.com/PKISharp/ACMESharpCore) 作者 @ebekker
- [Durable Functions](https://github.com/Azure/azure-functions-durable-extension) 作者 @cgillum とコントリビューター
- [DnsClient.NET](https://github.com/MichaCo/DnsClient.NET) 作者 @MichaCo

## ライセンス

このプロジェクトは [Apache License 2.0](https://github.com/shibayan/appservice-acmebot/blob/master/LICENSE) の下でライセンスされています。
