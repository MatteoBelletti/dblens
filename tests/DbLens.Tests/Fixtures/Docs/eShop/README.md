# eShop fixture

A subset of [dotnet/eShop](https://github.com/dotnet/eShop) at commit `669682e` (MIT licence, see
`LICENSE-eShop`): every project file, the settings files, and the source files the DbLens.Docs inventory
tests read. The files are parsed as text and never compiled. Their settings hold the sample's own
placeholder passwords on purpose: a test checks that no settings *value* reaches the inventory.
