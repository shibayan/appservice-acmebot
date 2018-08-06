# azure-appservice-letsencrypt

[![Build status](https://ci.appveyor.com/api/projects/status/bhbdscxn7f33ne1p?svg=true)](https://ci.appveyor.com/project/shibayan/azure-appservice-letsencrypt)

Provide automation of Let's Encrypt for Azure App Service.

## Getting Started

### 1. Deploy to Azure Functions

<a href="https://portal.azure.com/#create/Microsoft.Template/uri/https%3A%2F%2Fraw.githubusercontent.com%2Fshibayan%2Fazure-appservice-letsencrypt%2Fmaster%2Fazuredeploy.json" target="_blank">
  <img src="http://azuredeploy.net/deploybutton.png" />
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

### 3. Turn on Managed Service Identity


### 4. Attach "Website Contributor" role to MSI app


## Usage

### Adding new certificate

Run `AddCertificate_HttpStart` function with parameters.

```sh
curl https://***.azurewebsites.net/api/AddCertificate_HttpStart?code=*** -X POST -H 'Content-Type:application/json' -d "{"ResourceGroupName":"***","SiteName":"***","Domain":"***"}" 
```

### Renew certificates

This function will check the expiration date once a day for the certificate issuer is "Let's Encrypt Authority X3" or "Let's Encrypt Authority X4".

The default time is UTC 00: 00, so if necessary you can set any time zone with `WEBSITE_TIME_ZONE`.

## License

[Apache License 2.0](https://github.com/shibayan/azure-appservice-letsencrypt/blob/master/LICENSE)
