# Azure App Service Let's Encrypt

[![Build status](https://ci.appveyor.com/api/projects/status/bhbdscxn7f33ne1p?svg=true)](https://ci.appveyor.com/project/shibayan/azure-appservice-letsencrypt)

This function provide automation of Let's Encrypt for Azure App Service. This project started to solve some problems.

- Support multiple app services
- Simple deployment and configuration
- Robustness of implementation
- Easy monitoring (Application Insights)

They can manage multiple App Service certificates with simple one Functions.

## Table Of Contents

- [Feature Support](#feature-support)
- [Requirements](#requirements)
- [Getting Started](#getting-started)
- [Usage](#usage)
- [Known Issues](#known-issues)
- [Thanks](#thanks)
- [License](#license)

## Feature Support

- App Service (Windows) and Azure Functions
- Wildcard certificates (required Azure DNS)
- App Service (Linux) / Web App for Containers (required Azure DNS)
- Multiple App Service with one Functions

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
- LetsEncrypt:Contacts
  - Email address for Let's Encrypt account

### 3. Assign roles to target resource group

Using `Access control (IAM)`, assign a role to Function App. Require "Website Contributor" and "Web Plan Contributor" role.

![Assign role](https://user-images.githubusercontent.com/1356444/43694372-feaefda4-996d-11e8-9ee5-e58254ec05f5.png)

![IAM settings](https://user-images.githubusercontent.com/1356444/44624857-e169c900-a934-11e8-982c-5ad8c163beff.png)

**Remarks**

If the Web App refers to a Service Plan in a different resource group, Please assign "Website Contributor" role for Resource Group with Web App and "Web Plan Contributor" role for Resource Group with Service Plan.

## Usage

### Adding new certificate

Run `AddCertificate_HttpStart` function with parameters.

```sh
curl https://YOUR-FUNCTIONS.azurewebsites.net/api/AddCertificate_HttpStart?code=YOUR-FUNCTION-SECRET -X POST \
    -H 'Content-Type:application/json' \
    -d '{"ResourceGroupName":"My-WebApp-RG","SiteName":"my-webapp","Domains":["example.com"],"UseIpBasedSsl":false}'
```

- ResourceGroupName
  - Resource group containing App Service. (ex. My-WebApp-RG)
- SiteName
  - App Service name to issue certificate. (ex. my-webapp)
- Domains
  - Hostnames to issue certificate. It needs to be added to App Service. (ex. example.com)
- UseIpBasedSsl
  - Use IP Based SSL binding. (boolean, optional)

### Renew certificates

This function will check the expiration date once a day for the certificate issuer is "Let's Encrypt Authority X3" or "Let's Encrypt Authority X4".

The default time is UTC 00:00, so if necessary they can set any time zone with `WEBSITE_TIME_ZONE`.

### Deploy new version

This function use `Run From Package`. To deploy the latest version, just restart Azure Functions.

### Wildcard and Linux Container support

If they need a Wildcard certificate, additional assign "DNS Zone Contributor" role to Azure DNS or Resource group.

![IAM settings](https://user-images.githubusercontent.com/1356444/44642883-3840d280-aa09-11e8-9346-faa26f9675af.png)

Certificates for "App Service on Linux" and "Web App for Container" is required Azure DNS.

## Known Issues

**Causes Azure REST API error at GetSite or Dns01Precondition**

Make sure that the required role is assign for the resource group. Azure IAM may take up to 30 minutes to be reflected.

## Thanks

- [ACMESharp Core](https://github.com/PKISharp/ACMESharpCore) by @ebekker
- [Durable Functions](https://github.com/Azure/azure-functions-durable-extension) by @cgillum and contributors

## License

This project is licensed under the [Apache License 2.0](https://github.com/shibayan/azure-appservice-letsencrypt/blob/master/LICENSE)
