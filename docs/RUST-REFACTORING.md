# Rust core refactoring / Рефакторинг Rust-ядра / Rust 核心重构

## English (UK/US)

The Rust monitoring core is split into small, change-friendly modules:

- `src/constants.rs` — conversion factors, watch interval limits, and shared warning messages.
- `src/metrics.rs` — the single `MetricSink` responsible for validating values and constructing `SensorReading` objects.
- `src/counters.rs` — sysinfo collection, separated into memory, CPU, swap, and volume collectors.
- `src/inventory.rs` — WMI inventory, disk counters, and network counters; shared units come from `constants.rs`.
- `src/monitoring.rs` — stable public sensor contracts and formatting helpers.
- `src/main.rs` — thin one-shot/watch CLI; it keeps no collection logic.

Change a conversion or watch interval in `constants.rs`. Change measurement validation in `metrics.rs`. Add a new hardware source as a focused collector and send valid values through `MetricSink`.

## Русский

Rust-ядро разделено по ответственности: константы, единое построение и проверка метрик, сбор памяти/CPU/swap/дисков, WMI-инвентаризация, сетевые счётчики и тонкий CLI. Поэтому изменения вносятся в одном понятном месте, а не в монолитном файле. Недоступные данные не заменяются нулями-заглушками: причина передаётся в `warnings`.

## 中文

Rust 核心按职责拆分：常量、统一指标构造与校验、内存/CPU/swap/磁盘采集、WMI 清单、网络计数器和精简 CLI。修改单位换算或监控间隔时只需要改动对应模块。不可用数据不会伪造为零值，而是写入 `warnings`。

## Validation

```text
cargo fmt --manifest-path src-rust/pcmonitor-core/Cargo.toml --all
cargo test --manifest-path src-rust/pcmonitor-core/Cargo.toml
cargo build --release --manifest-path src-rust/pcmonitor-core/Cargo.toml
```
