# Third-party components

The SDK-selected, untrimmed .NET 10 self-contained WinForms runtime includes the original license/notice files from the pinned Microsoft.NETCore.App and Microsoft.WindowsDesktop.App runtime packages.

Account transport additionally distributes:
- Microsoft.Identity.Client 4.92.0 (MIT), Microsoft Corporation. Source: https://github.com/AzureAD/microsoft-authentication-library-for-dotnet . License: Microsoft.Identity.Client-LICENSE.txt.
- Microsoft.IdentityModel.Abstractions 8.14.0 (MIT), Microsoft Corporation. Source: https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet . License: Microsoft.IdentityModel-LICENSE.txt.
- System.Security.Cryptography.ProtectedData 10.0.0 (MIT), .NET contributors. Source: https://github.com/dotnet/runtime . The runtime MIT license and ProtectedData-THIRD-PARTY-NOTICES.txt accompany the distribution.

Exact package versions and content hashes are locked in packages.lock.json. No embedded browser or full Microsoft Graph SDK is bundled.
