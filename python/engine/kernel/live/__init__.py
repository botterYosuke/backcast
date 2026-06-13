"""engine.kernel.live — kernel の Live/Auto 統合（#25・方針: ADR-0004 案 C・記録: findings 0010）。

Rust core を一切ロードしない pure-Python の Live 実体:
- `broker.LiveBroker` — kernel `OrderEngine` ↔ 実 venue `OrderingVenueAdapter` の約定 bridge + order FSM。
- `driver` — `LiveRunner.bus` → kernel `Strategy`（market-data consumer + intent drain loop）。
- `controller.KernelLiveEngineController` — `LiveEngineController` Protocol 実体（`NautilusLiveEngineController` 置換）。

依存方向（固定・D5）: engine.kernel.live → engine.kernel → engine.live.{adapter,event_bus,strategy_host,order_types}。
**禁止**: engine.live.engine_controller / bar_supply / nautilus_*。便利 re-export は置かない（import 連鎖を広げない）。
"""
