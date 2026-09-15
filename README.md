## Eventbroker
The eventbroker aggregates events from different sources and creates a unified event stream. 
Depending on user settings, the eventbroker can send notifications to different channels.
Planned channels are:
- Email
- PushNotification - via firebase
- Webhook
- Discord webhook - available
- Minecraft chat - available

## Deploying
This project should be deployed within a container. 
### Configuration
See appsettings.json

### Bazaar order completions

`BazaarFillNotifications` consumes confirmed completions from Redis stream `bazaar:fills:v1`.
Subscribe with `SourceType: "bazaar"` and `SourceSubIdRegex: "filled"` and attach existing Firebase
or Discord webhook targets to receive alerts while out of game. Estimated fills do not alert.
The consumer group retains pending events for retry and recovers abandoned deliveries after a
minute. Delivery is at least once; duplicate enqueueing is suppressed by SkyBazaar per order.
The consumer reuses EventBroker's existing `REDIS_HOST` connection. SkyBazaar, SkyApi and
SkyModCommands use `EVENTS_REDIS_HOST` for this same Redis; their other caches stay separate.
The Talos chart adds a 90 MiB persistent volume, RDB saves approximately every five seconds after
writes, and a 32 MB `noeviction` data limit. Saved stream entries and consumer pending state survive
pod replacement; writes since the last completed save can be lost. SkyBazaar retries failed
publication every ten seconds, but its pending buffer is in memory, not a transactional outbox.
No user targets/subscriptions are created automatically. See [Bazaar order updates](../SkyBazaar/ORDER_UPDATES.md)
for recovery details, compatibility and the `2.0.0-pre1` test-client gate.

[Bazaar rollout diagnostics](../SkyBazaar/BAZAAR_DIAGNOSTICS.md) lists the consumer trace source,
reference/entry correlation, delivery metrics, and how to trace failures through target delivery.
