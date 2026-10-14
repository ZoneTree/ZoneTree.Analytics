$env:DOTNET_TieredCompilation = '0'
dotnet run -c Release --project apps/ZoneTree.Analytics.Dashboard/server -- --Dashboard:Watch=false

