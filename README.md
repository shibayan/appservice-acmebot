# App Service Acmebot

[![Build Status](https://dev.azure.com/shibayan/azure-acmebot/_apis/build/status/Build%20appservice-acmebot?branchName=master)](https://dev.azure.com/shibayan/azure-acmebot/_build/latest?definitionId=37&branchName=master)
[![Release](https://img.shields.io/github/release/shibayan/appservice-acmebot.svg)](https://github.com/shibayan/appservice-acmebot/releases/latest)
[![License](https://img.shields.io/github/license/shibayan/appservice-acmebot.svg)](https://github.com/shibayan/appservice-acmebot/blob/master/LICENSE)

This function provide easy automation of Let's Encrypt for Azure App Service. This project started to solve some problems.

- Support multiple app services
- Simple deployment and configuration
- Robustness of implementation
- Easy monitoring (Application Insights, Webhook)

They can manage multiple App Service certificates with single Function App.

### Attention

If you need fine-grained certificate management, I strongly recommend using Key Vault version.

https://github.com/shibayan/keyvault-acmebot

The Key Vault version is available for services that support Key Vault certificates such as App Service / App Gateway / CDN / Front Door.

## Table Of Contents

- [Feature Support](#feature-support)
- [Requirements](#requirements)
- [Getting Started](#getting-started)
- [Usage](#usage)
- [Troubleshooting](#troubleshooting)
- [Thanks](#thanks)
- [License](#license)

## Feature Support

- Azure Web Apps and Azure Functions (Windows)
- Azure Web Apps (Linux) / Web App for Containers (required Azure DNS)
- Azure App Service Environment (Windows / Linux)
- Certificate issued to any deployment slot
- Subject Alternative Names certificates (multi-domains support)
- Wildcard certificates (required Azure DNS)
- Multiple App Services support with single Function App

## Requirements

- Azure Subscription
- App Service with added hostnames
- Email address (for Let's Encrypt account)

## Getting Started

### 1. Deploy to Azure Functions

<a href="https://portal.azure.com/#create/Microsoft.Template/uri/https%3A%2F%2Fraw.githubusercontent.com%2Fshibayan%2Fappservice-acmebot%2Fmaster%2Fazuredeploy.json" target="_blank">
  <img src="https://azuredeploy.net/deploybutton.png" />
</a>

<a href="http://armviz.io/#/?load=https%3A%2F%2Fraw.githubusercontent.com%2Fshibayan%2Fappservice-acmebot%2Fmaster%2Fazuredeploy.json" target="_blank">
  <img src="http://armviz.io/visualizebutton.png" />
</a>

### 2. Add application settings key

- LetsEncrypt:SubscriptionId
  - Azure Subscription Id
- LetsEncrypt:Contacts
  - Email address for Let's Encrypt account
- LetsEncrypt:Webhook
  - Webhook destination URL (optional, Slack recommend)

### 3. Enable App Service Authentication (EasyAuth) with AAD

Open `Authentication / Authorization` from Azure Portal and turn on App Service Authentication. Then select `Log in with Azure Active Directory` as an action when not logging in.

![Enable App Service Authentication with AAD](https://user-images.githubusercontent.com/1356444/49693401-ecc7c400-fbb4-11e8-9ae1-5d376a4d8a05.png)

Set up Azure Active Directory provider by selecting `Express`.

![Create New Azure AD App](https://user-images.githubusercontent.com/1356444/49693412-6f508380-fbb5-11e8-81fb-6bbcbe47654e.png)

### 4. Assign roles to target resource group

Using `Access control (IAM)`, assign a role to Function App. Require `Website Contributor` and `Web Plan Contributor` roles.

![Assign a role](https://user-images.githubusercontent.com/1356444/43694372-feaefda4-996d-11e8-9ee5-e58254ec05f5.png)

![IAM settings](https://user-images.githubusercontent.com/1356444/44624857-e169c900-a934-11e8-982c-5ad8c163beff.png)

**Remarks**

If the Web App refers to a Service Plan in a different resource group, Please assign `Website Contributor` role for Resource Group with Web App and `Web Plan Contributor` role for Resource Group with Service Plan.

## Usage

### Adding new certificate

Go to `https://YOUR-FUNCTIONS.azurewebsites.net/add-certificate`. Since the Web UI is displayed, if you select the target App Service and domain and execute it, a certificate will be issued.

![Add certificate](https://user-images.githubusercontent.com/1356444/49693421-b3dc1f00-fbb5-11e8-8ac1-37092a2be711.png)

If nothing is displayed in the dropdown, the IAM setting is incorrect.

### Adding wildcard certificate or Linux Container support

If they need a Wildcard certificate, additional assign `DNS Zone Contributor` role to Azure DNS or Resource group.

![IAM settings](https://user-images.githubusercontent.com/1356444/44642883-3840d280-aa09-11e8-9346-faa26f9675af.png)

Certificates for "App Service on Linux" and "Web App for Container" is required Azure DNS.

### Renew certificates

This function will check the expiration date once a day for the certificate issuer is `Let's Encrypt Authority X3` or `Let's Encrypt Authority X4`.

The default time is UTC 00:00, so if necessary they can set any time zone with `WEBSITE_TIME_ZONE`.

### Deploy new version

This function use `Run From Package`. To deploy the latest version, just restart Azure Functions.

## Troubleshooting

**Causes Azure REST API error at GetSite or Dns01Precondition**

Make sure that the required role is assign for the resource group. Azure IAM may take up to 30 minutes to be reflected.

## Thanks

- [ACMESharp Core](https://github.com/PKISharp/ACMESharpCore) by @ebekker
- [Durable Functions](https://github.com/Azure/azure-functions-durable-extension) by @cgillum and contributors
- [DnsClient.NET](https://github.com/MichaCo/DnsClient.NET) by @MichaCo

## License

This project is licensed under the [Apache License 2.0](https://github.com/shibayan/appservice-acmebot/blob/master/LICENSE)
