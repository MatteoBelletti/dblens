# Integrations

External HTTP clients and services that run in the background.

<!-- dblens:item http:payments hash=93b01407 gen=0daaeb7e -->
### payments

| Project | Base address | Address read from | File |
| --- | --- | --- | --- |
| ShopApi | (none) | none | Program.cs |
<!-- dblens:slot http:payments purpose max-lines=2 evidence=inferred hash=93b01407 text=7318281f -->
Named HttpClient "payments", registered in the service collection in Program.cs. Its configure callback sets BaseAddress from the Payments:BaseUrl configuration key each time the client is created.
<!-- dblens:end -->

<!-- dblens:item hosted:OutboxPublisher hash=6ac350c8 gen=8a044ee5 -->
### OutboxPublisher

| Bases | Registered with AddHostedService | File |
| --- | --- | --- |
| BackgroundService | yes | OutboxPublisher.cs |
<!-- dblens:slot hosted:OutboxPublisher purpose max-lines=2 evidence=inferred hash=6ac350c8 text=25c85c5f -->
Background service that loops until stopped: it fetches pending messages from the outbox, publishes each to the message broker, then marks it as sent in the outbox. It then waits Outbox:IntervalSeconds before polling again.
<!-- dblens:end -->
