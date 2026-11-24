# EventualShop fixture

A subset of [AntonioFalcaoJr/EventualShop](https://github.com/AntonioFalcaoJr/EventualShop) at commit `d63e424` (MIT licence,
see `LICENSE-EventualShop`): `Contracts`, the `Warehouse` and `ShoppingCart` services, and two compose files. It is the
second style the inventory is tested on (MassTransit-style consumers, events raised by aggregates, one event store context
per service, services built from compose), against `../EventualShop.truth.txt`. The compose files carry the sample's own
placeholder passwords on purpose: a test checks that no value reaches the inventory.
