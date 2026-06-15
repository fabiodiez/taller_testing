# Dependency Stabilization Plan

## Required local tools

- .NET 8 SDK (`8.0.100` or newer 8.0 feature band).
- Access to `https://api.nuget.org/v3/index.json`.

## NuGet packages

The API project uses the shared `Microsoft.AspNetCore.App` framework from the .NET 8 SDK and does not need extra package references.

The test project needs these packages from `nuget.org`:

- `Microsoft.NET.Test.Sdk` `17.11.1`
- `xunit` `2.9.0`
- `xunit.runner.visualstudio` `2.8.2`
- `Microsoft.AspNetCore.Mvc.Testing` `8.0.10`

## Stabilization steps

1. Use the repository `NuGet.config` so restores do not depend on private machine-level feeds.
2. Use `global.json` to keep builds on the .NET 8 SDK line.
3. Restore the test project with `dotnet restore .\Tests\ChargesApi.Tests.csproj`.
4. Build the API with `dotnet build .\ChargesApi.csproj --no-restore`.
5. Run tests with `dotnet test .\Tests\ChargesApi.Tests.csproj --no-restore`.

## Notes

The previous restore failure was caused by a global Azure DevOps feed returning `401 Unauthorized`, not by missing public packages. The API build also picked up test source files because the test project is nested under the API project directory.
