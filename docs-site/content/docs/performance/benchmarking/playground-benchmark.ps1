$env:DOTNET_TieredCompilation = '0'
dotnet run -c Release --project src/ZoneTree.Analytics.Playground -- 1000000 30 1

