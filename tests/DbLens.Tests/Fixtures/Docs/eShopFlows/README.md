# eShopFlows fixture

The sources of [dotnet/eShop](https://github.com/dotnet/eShop) at commit `e5e24f0` (MIT licence, see `LICENSE-eShop`):
every `.cs`, `.csproj` and `appsettings*.json` under `src`, and the two `Directory.*.props` of the root. It is used by the
inventory tests of components, events, links and flows, against `../eShopFlows.truth.txt`, which was written by hand from
`grep` and from reading the code. The files are parsed as text and never compiled.
