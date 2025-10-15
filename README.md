<h1 align="center">
  Acmebot for Azure App Service
</h1>
<p align="center">
  Automated ACME SSL/TLS certificates issuer for Azure App Service (Web Apps / Functions / Containers)
</p>
<p align="center">
  <a href="https://github.com/shibayan/appservice-acmebot/actions/workflows/build.yml" rel="nofollow"><img src="https://github.com/shibayan/appservice-acmebot/workflows/Build/badge.svg" alt="Build" style="max-width: 100%;"></a>
  <a href="https://github.com/shibayan/appservice-acmebot/releases/latest" rel="nofollow"><img src="https://badgen.net/github/release/shibayan/appservice-acmebot" alt="Release" style="max-width: 100%;"></a>
  <a href="https://github.com/shibayan/appservice-acmebot/stargazers" rel="nofollow"><img src="https://badgen.net/github/stars/shibayan/appservice-acmebot" alt="Stargazers" style="max-width: 100%;"></a>
  <a href="https://github.com/shibayan/appservice-acmebot/network/members" rel="nofollow"><img src="https://badgen.net/github/forks/shibayan/appservice-acmebot" alt="Forks" style="max-width: 100%;"></a>
  <a href="https://github.com/shibayan/appservice-acmebot/blob/master/LICENSE"><img src="https://badgen.net/github/license/shibayan/appservice-acmebot" alt="License" style="max-width: 100%;"></a>
  <a href="https://registry.terraform.io/modules/shibayan/appservice-acmebot/azurerm/latest" rel="nofollow"><img src="https://badgen.net/badge/terraform/registry/5c4ee5" alt="Terraform" style="max-width: 100%;"></a>
  <br>
  <a href="https://github.com/shibayan/appservice-acmebot/commits/master" rel="nofollow"><img src="https://badgen.net/github/last-commit/shibayan/appservice-acmebot" alt="Last commit" style="max-width: 100%;"></a>
  <a href="https://github.com/shibayan/appservice-acmebot/wiki" rel="nofollow"><img src="https://badgen.net/badge/documentation/available/ff7733" alt="Documentation" style="max-width: 100%;"></a>
  <a href="https://github.com/shibayan/appservice-acmebot/discussions" rel="nofollow"><img src="https://badgen.net/badge/discussions/welcome/ff7733" alt="Discussions" style="max-width: 100%;"></a>
</p>

## Motivation

We have started to address the following requirements:

- Support for multiple App Services
- Easy to deploy and configure
- Highly reliable implementation
- Ease of Monitoring (Application Insights, Webhook)

You can add multiple certificates to a single App Service.

## Feature Support

- Azure Web Apps and Azure Functions (Windows)
- Azure Web Apps (Linux) / Web App for Containers (Windows and Linux, requires Azure DNS)
- Azure App Service Environment (Windows and Linux)
- Issuing a certificate to the Deployment Slot
- Issuing certificates for Zone Apex Domains
- Issuing certificates with SANs (subject alternative names) (one certificate for multiple domains)
- Wildcard certificate (requires Azure DNS)
- Support for multiple App Services in a single application
- ACME-compliant Certification Authorities
  - [Let's Encrypt](https://letsencrypt.org/)
  - [Buypass Go SSL](https://www.buypass.com/ssl/resources/acme-free-ssl)
  - [ZeroSSL](https://zerossl.com/features/acme/) (Requires EAB Credentials)

[![architectural diagram](docs/images/acmebot-diagram.svg)](https://www.lucidchart.eu/documents/view/77879337-7889-41d9-bd2d-c3a184f9587b)

## Deployment

| Azure (Public) | Azure China | Azure Government |
| :---: | :---: | :---: |
| <a href="https://portal.azure.com/#create/Microsoft.Template/uri/https%3A%2F%2Fraw.githubusercontent.com%2Fshibayan%2Fappservice-acmebot%2Fmaster%2Fazuredeploy.json" target="_blank"><img src="https://aka.ms/deploytoazurebutton" /></a> | <a href="https://portal.azure.cn/#create/Microsoft.Template/uri/https%3A%2F%2Fraw.githubusercontent.com%2Fshibayan%2Fappservice-acmebot%2Fmaster%2Fazuredeploy.json" target="_blank"><img src="https://aka.ms/deploytoazurebutton" /></a> | <a href="https://portal.azure.us/#create/Microsoft.Template/uri/https%3A%2F%2Fraw.githubusercontent.com%2Fshibayan%2Fappservice-acmebot%2Fmaster%2Fazuredeploy.json" target="_blank"><img src="https://aka.ms/deploytoazurebutton" /></a> |

Learn more at https://github.com/shibayan/appservice-acmebot/wiki/Getting-Started

## Thanks

- [ACMESharp Core](https://github.com/PKISharp/ACMESharpCore) by @ebekker
- [Durable Functions](https://github.com/Azure/azure-functions-durable-extension) by @cgillum and contributors
- [DnsClient.NET](https://github.com/MichaCo/DnsClient.NET) by @MichaCo

## License

This project is licensed under the [Apache License 2.0](https://github.com/shibayan/appservice-acmebot/blob/master/LICENSE)
