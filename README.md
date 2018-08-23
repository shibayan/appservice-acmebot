# Azure App Service Let's Encrypt

[![Build status](https://ci.appveyor.com/api/projects/status/bhbdscxn7f33ne1p?svg=true)](https://ci.appveyor.com/project/shibayan/azure-appservice-letsencrypt)

This function provide automation of Let's Encrypt for Azure App Service. This project started to solve some problems.

- Support multiple app services
- Simple deployment and configuration
- Robustness of implementation
- Easy monitoring (App Insights)

They can manage multiple App Service certificates with simple one Functions.

## Table Of Contents

- [Requirements](#requirements)
- [Getting Started](#getting-started)
- [Usage](#usage)
- [Thanks](#thanks)
- [License](#license)

## Requirements

- Azure Subscription
- App Service with added hostname
- Email address (for Let's Encrypt account)

## Getting Started

### 1. Deploy to Azure Functions

<a href="https://portal.azure.com/#create/Microsoft.Template/uri/https%3A%2F%2Fraw.githubusercontent.com%2Fshibayan%2Fazure-appservice-letsencrypt%2Fmaster%2Fazuredeploy.json" target="_blank">
  <img src="https://azuredeploy.net/deploybutton.png" />
</a>

<a href="http://armviz.io/#/?load=https%3A%2F%2Fraw.githubusercontent.com%2Fshibayan%2Fazure-appservice-letsencrypt%2Fmaster%2Fazuredeploy.json" target="_blank">
  <img src="http://armviz.io/visualizebutton.png" />
</a>

### 2. Add application settings key

- LetsEncrypt:SubscriptionId
  - Azure Subscription Id
- LetsEncrypt:ResourceGroupName
  - Target resource group name (temporary setting, will remove)
- LetsEncrypt:Contacts
  - Email address for Let's Encrypt account

### 3. Assign "Website Contributor" role to target resource group

Using `Access control (IAM)`, assign a role to Function App. Require "Website Contributor" role.

![Attach role](https://user-images.githubusercontent.com/1356444/43694372-feaefda4-996d-11e8-9ee5-e58254ec05f5.png)

If they need a Wildcard certificate, assign "DNS Zone Contributor" role.

## Usage

### Adding new certificate

Run `AddCertificate_HttpStart` function with parameters.

```sh
curl https://***.azurewebsites.net/api/AddCertificate_HttpStart?code=*** -X POST \
    -H 'Content-Type:application/json' \
    -d "{"ResourceGroupName":"***","SiteName":"***","Domain":"***","UseIpBasedSsl":(true/false)}"
```

- ResourceGroupName
  - Resource group containing App Service
- SiteName
  - App Servicee name to issue certificate
- Domain
  - Host name to issue certificate (It needs to be added to App Service)
- UseIpBasedSsl
  - Use IP Based SSL binding

### Renew certificates

This function will check the expiration date once a day for the certificate issuer is "Let's Encrypt Authority X3" or "Let's Encrypt Authority X4".

The default time is UTC 00:00, so if necessary they can set any time zone with `WEBSITE_TIME_ZONE`.

### Deploy new version

This function use `Run From Package`. To deploy the latest version, just restart Azure Functions.

## Thanks

- [ACMESharp Core](https://github.com/PKISharp/ACMESharpCore) by @ebekker
- [Durable Functions](https://github.com/Azure/azure-functions-durable-extension) by @cgillum and contributors

## License

This project is licensed under the [Apache License 2.0](https://github.com/shibayan/azure-appservice-letsencrypt/blob/master/LICENSE)
