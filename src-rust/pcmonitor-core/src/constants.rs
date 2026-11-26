//! Stable constants used by the collectors.

pub const BYTES_PER_GIB: f64 = 1_073_741_824.0;
pub const KIB_PER_GIB: f64 = 1_048_576.0;
pub const MIN_WATCH_INTERVAL_MS: u64 = 250;
pub const MAX_WATCH_INTERVAL_MS: u64 = 60_000;
pub const DEFAULT_WATCH_INTERVAL_MS: u64 = 1_000;

pub const WARNING_COUNTER_LOCK: &str = "System counter lock is unavailable.";
pub const WARNING_NETWORK_LOCK: &str = "Network statistics lock is unavailable.";
pub const WARNING_NO_SENSORS: &str =
    "Windows WMI returned no live monitoring counters; run in a supported Windows session and verify the WMI service.";
