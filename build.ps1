#!/usr/bin/env pwsh

# A wrapper around `dotnet run build.cs`, and nothing more. The `build.ps1` in a Cake repository has
# historically been a bootstrapper — it fetched the tool before it could run the script — and this one
# fetches nothing: Cake.Sdk is a file-based build, so the .NET 10 SDK that builds the app runs it too.
# It exists so that `.\build.ps1 --target=Package-Zip` works from muscle memory and from a CI step
# that expects a script at the root, and so that neither has to care where the repository is checked
# out. Every argument is passed through untouched; `build.cs` owns them all.

$ErrorActionPreference = 'Stop'

Push-Location $PSScriptRoot
try {
    dotnet run build.cs -- @args

    # A failed target is a non-zero exit code from dotnet and not a PowerShell error, so
    # $ErrorActionPreference does not see it. Without this the wrapper reports success on every
    # failed build, which is the one thing a CI step needs it to get right.
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}
finally {
    Pop-Location
}
