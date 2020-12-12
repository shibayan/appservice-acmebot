# App Service Acmebot

![Build](https://github.com/shibayan/appservice-acmebot/workflows/Build/badge.svg)
[![Release](https://img.shields.io/github/release/shibayan/appservice-acmebot.svg)](https://github.com/shibayan/appservice-acmebot/releases/latest)
[![License](https://img.shields.io/github/license/shibayan/appservice-acmebot.svg)](https://github.com/shibayan/appservice-acmebot/blob/master/LICENSE)
[![Terraform Registry](https://img.shields.io/badge/terraform-registry-5c4ee5.svg)](https://registry.terraform.io/modules/shibayan/appservice-acmebot/azurerm/latest)

This is an application that automates the issuance and renewal of ACME SSL/TLS certificates for the Azure App Service. We have started to solve the following issues

- Support for multiple App Services
- Easy to deploy and configure
- Highly reliable implementation
- Ease of Monitoring (Application Insights, Webhook)

You can manage multiple App Service certificates in a single application.

## Announcements

### Upgrading to Acmebot v3

https://github.com/shibayan/appservice-acmebot/issues/138

### Integration with Key Vault

If you need to use the certificate for a variety of services, consider using the Key Vault version of Acmebot v3.

https://github.com/shibayan/keyvault-acmebot

The Key Vault version can be used with services that support Key Vault certificates such as App Service / Application Gateway / CDN / Front Door.

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
- Azure Web Apps (Linux) / Web App for Containers (Windows and Linux, requires Azure DNS)
- Azure App Service Environment (Windows and Linux)
- Issuing a certificate to the Deployment Slot
- Issuing Certificates for Zone Apex Domains
- Issuing certificates with SANs (subject alternative names) (one certificate for multiple domains)
- Wildcard certificate (requires Azure DNS)
- Support for multiple App Services in a single application
- ACME-compliant Certification Authorities
  - [Let's Encrypt](https://letsencrypt.org/)
  - [Buypass Go SSL](https://www.buypass.com/ssl/resources/acme-free-ssl)

[![architectural diagram](docs/acmebot-diagram.svg)](https://www.lucidchart.eu/documents/view/77879337-7889-41d9-bd2d-c3a184f9587b)

## Requirements

- Azure Subscription
- App Service with a registered custom domain
- Email address (required to register with Let's Encrypt)

## Getting Started

### 1. Deploy Acmebot

For Azure Cloud

<a href="https://portal.azure.com/#create/Microsoft.Template/uri/https%3A%2F%2Fraw.githubusercontent.com%2Fshibayan%2Fappservice-acmebot%2Fmaster%2Fazuredeploy.json" target="_blank">
  <img src="https://aka.ms/deploytoazurebutton" />
</a>

For Azure China

<a href="https://portal.azure.cn/#create/Microsoft.Template/uri/https%3A%2F%2Fraw.githubusercontent.com%2Fshibayan%2Fappservice-acmebot%2Fmaster%2Fazuredeploy.json" target="_blank">
  <img src="https://aka.ms/deploytoazurebutton" />
</a>

For Azure Government

<a href="https://portal.azure.us/#create/Microsoft.Template/uri/https%3A%2F%2Fraw.githubusercontent.com%2Fshibayan%2Fappservice-acmebot%2Fmaster%2Fazuredeploy.json" target="_blank">
  <img src="https://aka.ms/deploytoazurebutton" />
</a>

### 2. Enabling App Service Authentication

Open the `Authentication / Authorization` menu in Azure Portal and enable App Service authentication. Select the `Login with Azure Active Directory` as the action to perform if the request is not authenticated. We recommend using Azure Active Directory as your authentication provider, but it works with other providers as well, although it's not supported.

![Enable App Service Authentication with AAD](https://user-images.githubusercontent.com/1356444/49693401-ecc7c400-fbb4-11e8-9ae1-5d376a4d8a05.png)

Select Azure Active Directory as the authentication provider, select `Express` as the management mode, and select OK.

![Create New Azure AD App](https://user-images.githubusercontent.com/1356444/49693412-6f508380-fbb5-11e8-81fb-6bbcbe47654e.png)

Finally, you can save your previous settings to enable App Service authentication.

### 3. Add access control (IAM) to the target resource group

Open the `Access control (IAM)` of the target resource group and assign the roles `Website Contributor` and `Web Plan Contributor` to the deployed application.

![Assign a role](https://user-images.githubusercontent.com/1356444/43694372-feaefda4-996d-11e8-9ee5-e58254ec05f5.png)

![IAM settings](https://user-images.githubusercontent.com/1356444/44624857-e169c900-a934-11e8-982c-5ad8c163beff.png)

**Remarks**

If the App Service Plan associated with the App Service exists in a separate resource group, you should assign a `Website Contributor` to the resource group where the App Service exists, and a `Web Plan Contributor` to the resource group where the App Service Plan exists.

## Usage

### Issuing a new certificate

Access `https://YOUR-FUNCTIONS.azurewebsites.net/add-certificate` with a browser and authenticate with Azure Active Directory and the Web UI will be displayed. Select the target App Service and domain from that screen and run it, and after a few tens of seconds, the certificate will be issued.

![Add certificate](https://user-images.githubusercontent.com/1356444/49693421-b3dc1f00-fbb5-11e8-8ac1-37092a2be711.png)

If the `Access control (IAM)` setting is not correct, nothing will be shown in the drop-down list.

### Issuing a wildcard certificate or a certificate for Linux

Because Azure DNS is required to issue wildcard certificates or certificates for Linux, assign the role of `DNS Zone Contributor` in the resource group containing the target DNS zone.

![IAM settings](https://user-images.githubusercontent.com/1356444/44642883-3840d280-aa09-11e8-9346-faa26f9675af.png)

To issue certificates for "App Service on Linux" and "Web App for Container", Azure DNS is always required.

### Renewing certificates

All existing ACME certificates are automatically renewed 30 days before their expiration.

The default check timing is 00:00 UTC. If you need to change the time zone, use `WEBSITE_TIME_ZONE` to set the time zone.

### Deploying a new version

The application is automatically updated so that you are always up to date with the latest version. If you explicitly need to deploy the latest version, restart the Azure Function.


### The case where you want to use your own web.config

You can prevent Acmebot from creating `web.config` by creating your own `web.config` and `configured` files in the `site/.well-known` directory.

## Troubleshooting

**Causes Azure REST API error at GetSite or Dns01Precondition** error occurs

The role assignment to the target resource group may be incorrect or not yet reflected, and it may take up to 30 minutes for the IAM settings to take effect.

**CheckDnsChallenge failed: _acme-challenge.{domain}.com value is not correct** error occurs

In order for the certificate to be created, the Acmebot needs to create a TXT DNS record for `_acme-challenge` in Azure DNS. This error occurs when the TXT record isn't being served. One cause of this may be that the nameservers for your domain may be pointing to the domain registrar, rather than Azure DNS. Make sure that you have properly delegated the domain to Azure DNS: [Host your domain in Azure DNS](https://docs.microsoft.com/en-us/azure/dns/dns-delegate-domain-azure-dns#delegate-the-domain)

**CheckHttpChallenge failed: http://{domain}/.well-known/acme-challenge/{challenge} is InternalServerError status code** error occurs

It seems like URL rewrite error, so please try `inheritInChildApplications="false"` settings for web.config under wwwroot.

https://www.hanselman.com/blog/ChangingASPNETWebconfigInheritanceWhenMixingVersionsOfChildApplications.aspx

## Thanks

- [ACMESharp Core](https://github.com/PKISharp/ACMESharpCore) by @ebekker
- [Durable Functions](https://github.com/Azure/azure-functions-durable-extension) by @cgillum and contributors
- [DnsClient.NET](https://github.com/MichaCo/DnsClient.NET) by @MichaCo

## License

This project is licensed under the [Apache License 2.0](https://github.com/shibayan/appservice-acmebot/blob/master/LICENSE)
