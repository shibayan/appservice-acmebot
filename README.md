# azure-appservice-letsencrypt

[![Build status](https://ci.appveyor.com/api/projects/status/bhbdscxn7f33ne1p?svg=true)](https://ci.appveyor.com/project/shibayan/azure-appservice-letsencrypt)

Provide automation of Let's Encrypt for Azure App Service.

## Getting Started

### 1. Deploy to Azure Functions



### 2. Add application settings key

- LetsEncrypt:SubscriptionId
  - Azure SubscriptionId
- LetsEncrypt:ResourceGroupName
  - Target resource group name (temporary setting)
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

## License

[Apache License 2.0](https://github.com/shibayan/azure-appservice-letsencrypt/blob/master/LICENSE)
