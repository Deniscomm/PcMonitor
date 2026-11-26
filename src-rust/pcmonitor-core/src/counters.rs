use crate::constants::{BYTES_PER_GIB, WARNING_COUNTER_LOCK};
use crate::metrics::MetricSink;
use crate::monitoring::{HardwareType, SensorReading, SensorType};
use std::sync::{Mutex, OnceLock};
use std::time::Instant;
use sysinfo::{CpuRefreshKind, Disks, System};

struct CounterState {
    system: System,
    disks: Disks,
    refreshed_at: Option<Instant>,
}

static STATE: OnceLock<Mutex<CounterState>> = OnceLock::new();

pub fn collect(sensors: &mut Vec<SensorReading>, warnings: &mut Vec<String>) {
    let state = STATE.get_or_init(|| {
        let mut system = System::new();
        system.refresh_cpu_list(CpuRefreshKind::everything());
        Mutex::new(CounterState {
            system,
            disks: Disks::new_with_refreshed_list(),
            refreshed_at: None,
        })
    });

    let Ok(mut state) = state.lock() else {
        warnings.push(WARNING_COUNTER_LOCK.to_owned());
        return;
    };

    if state.refreshed_at.is_none() {
        std::thread::sleep(sysinfo::MINIMUM_CPU_UPDATE_INTERVAL);
    }
    state.system.refresh_cpu_all();
    state.system.refresh_memory();
    state.disks.refresh(true);
    state.refreshed_at = Some(Instant::now());

    let mut metrics = MetricSink::new(sensors);
    collect_memory_metrics(&state.system, &mut metrics);
    collect_cpu_metrics(&state.system, &mut metrics);
    collect_swap_metrics(&state.system, &mut metrics);
    collect_volume_metrics(&state.disks, &mut metrics);
}

fn collect_memory_metrics(system: &System, metrics: &mut MetricSink<'_>) {
    let total = system.total_memory();
    let used = system.used_memory();
    let available = system.available_memory();
    if total == 0 || used > total || available > total {
        return;
    }

    let label = "Physical Memory";
    metrics.push(
        "/windows/memory/used",
        label,
        HardwareType::Memory,
        SensorType::Data,
        "Memory Used",
        bytes_to_gib(used),
    );
    metrics.push(
        "/windows/memory/available",
        label,
        HardwareType::Memory,
        SensorType::Data,
        "Memory Available",
        bytes_to_gib(available),
    );
    metrics.push(
        "/windows/memory/load",
        label,
        HardwareType::Memory,
        SensorType::Load,
        "Memory Load",
        used as f64 / total as f64 * 100.0,
    );
}

fn collect_cpu_metrics(system: &System, metrics: &mut MetricSink<'_>) {
    metrics.push(
        "/windows/cpu/total/load",
        "CPU",
        HardwareType::Cpu,
        SensorType::Load,
        "CPU Total",
        f64::from(system.global_cpu_usage()),
    );
    for (index, cpu) in system.cpus().iter().enumerate() {
        let label = format!("Logical CPU #{}", index + 1);
        metrics.push(
            format!("/windows/cpu/logical/{index}/load"),
            &label,
            HardwareType::Cpu,
            SensorType::Load,
            "CPU Core Load",
            f64::from(cpu.cpu_usage()),
        );
        if cpu.frequency() > 0 {
            metrics.push(
                format!("/windows/cpu/logical/{index}/clock"),
                &label,
                HardwareType::Cpu,
                SensorType::Clock,
                "CPU Core Reported Clock",
                cpu.frequency() as f64,
            );
        }
    }
}

fn collect_swap_metrics(system: &System, metrics: &mut MetricSink<'_>) {
    let total = system.total_swap();
    let used = system.used_swap();
    if total == 0 || used > total {
        return;
    }

    let label = "Swap file";
    metrics.push(
        "/windows/memory/swap/used",
        label,
        HardwareType::Memory,
        SensorType::Data,
        "Swap Used",
        bytes_to_gib(used),
    );
    metrics.push(
        "/windows/memory/swap/free",
        label,
        HardwareType::Memory,
        SensorType::Data,
        "Swap Available",
        bytes_to_gib(total - used),
    );
    metrics.push(
        "/windows/memory/swap/load",
        label,
        HardwareType::Memory,
        SensorType::Load,
        "Swap Load",
        used as f64 / total as f64 * 100.0,
    );
}

fn collect_volume_metrics(disks: &Disks, metrics: &mut MetricSink<'_>) {
    for disk in disks.list() {
        let total = disk.total_space();
        let free = disk.available_space();
        if total == 0 || free > total {
            continue;
        }

        let label = disk.mount_point().to_string_lossy();
        if label.is_empty() {
            continue;
        }
        let id = label.replace(['/', '\\', ':'], "_");
        let used = total - free;
        metrics.push(
            format!("/windows/volume/{id}/used"),
            label.as_ref(),
            HardwareType::Storage,
            SensorType::Data,
            "Volume Used",
            bytes_to_gib(used),
        );
        metrics.push(
            format!("/windows/volume/{id}/free"),
            label.as_ref(),
            HardwareType::Storage,
            SensorType::Data,
            "Volume Free",
            bytes_to_gib(free),
        );
        metrics.push(
            format!("/windows/volume/{id}/load"),
            label.as_ref(),
            HardwareType::Storage,
            SensorType::Load,
            "Volume Used Space",
            used as f64 / total as f64 * 100.0,
        );
    }
}

fn bytes_to_gib(bytes: u64) -> f64 {
    bytes as f64 / BYTES_PER_GIB
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn bytes_are_converted_to_gib() {
        assert_eq!(bytes_to_gib(1_073_741_824), 1.0);
    }
}
