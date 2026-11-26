use crate::constants::{BYTES_PER_GIB, KIB_PER_GIB, WARNING_NETWORK_LOCK, WARNING_NO_SENSORS};
use crate::monitoring::{HardwareType, SensorReading, SensorType};
use serde::{Deserialize, Serialize};
use std::sync::{Mutex, OnceLock};
use std::time::{Instant, SystemTime, UNIX_EPOCH};
use sysinfo::Networks;
use wmi::{COMLibrary, WMIConnection};

struct NetworkState {
    networks: Networks,
    last_refresh: Option<Instant>,
}

static NETWORK_STATE: OnceLock<Mutex<NetworkState>> = OnceLock::new();

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct InfoField {
    pub name: String,
    pub value: String,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SystemReport {
    pub generated_unix_ms: u128,
    pub computer: Option<ComputerSystem>,
    pub total_memory_gib: Option<f64>,
    pub operating_system: Option<OperatingSystem>,
    pub board: Option<BaseBoard>,
    pub bios: Option<Bios>,
    pub processors: Vec<Processor>,
    pub memory_modules: Vec<PhysicalMemory>,
    pub graphics: Vec<VideoController>,
    pub disks: Vec<DiskDrive>,
    pub volumes: Vec<LogicalDisk>,
    pub network_adapters: Vec<NetworkAdapter>,
    pub sensors: Vec<SensorReading>,
    pub warnings: Vec<String>,
}

#[derive(Debug, Serialize)]
pub struct InventorySnapshot {
    pub generated_unix_ms: u128,
    pub report: SystemReport,
}

macro_rules! wmi_record {
    ($name:ident { $($field:ident : $ty:ty),* $(,)? }) => {
        #[derive(Debug, Clone, Default, Deserialize, Serialize)]
        #[serde(rename_all(deserialize = "PascalCase", serialize = "camelCase"))]
        pub struct $name { $(pub $field: Option<$ty>),* }
    };
}

wmi_record!(ComputerSystem {
    manufacturer: String,
    model: String,
    name: String,
    total_physical_memory: u64,
});
wmi_record!(OperatingSystem {
    caption: String,
    version: String,
    build_number: String,
    os_architecture: String,
    cs_name: String,
    install_date: String,
    last_boot_up_time: String,
    total_visible_memory_size: u64,
    free_physical_memory: u64,
    total_virtual_memory_size: u64,
    free_virtual_memory: u64,
});
wmi_record!(BaseBoard {
    manufacturer: String,
    product: String,
    version: String,
    serial_number: String,
});
wmi_record!(Bios {
    manufacturer: String,
    smbios_bios_version: String,
    release_date: String,
    smbios_major_version: u16,
    smbios_minor_version: u16,
});
wmi_record!(Processor {
    name: String,
    manufacturer: String,
    socket_designation: String,
    number_of_cores: u32,
    number_of_logical_processors: u32,
    max_clock_speed: u32,
    l2_cache_size: u32,
    l3_cache_size: u32,
    address_width: u16,
    processor_id: String,
    load_percentage: u16,
});
wmi_record!(PhysicalMemory {
    bank_label: String,
    device_locator: String,
    manufacturer: String,
    part_number: String,
    capacity: u64,
    speed: u32,
    configured_clock_speed: u32,
    smbios_memory_type: u16,
    form_factor: u16,
    configured_voltage: u32,
});
wmi_record!(VideoController {
    name: String,
    driver_version: String,
    driver_date: String,
    adapter_ram: u64,
    current_horizontal_resolution: u32,
    current_vertical_resolution: u32,
    pnp_device_id: String,
});
wmi_record!(DiskDrive {
    index: u32,
    model: String,
    interface_type: String,
    size: u64,
    serial_number: String,
    status: String,
    media_type: String,
});
wmi_record!(LogicalDisk {
    device_id: String,
    volume_name: String,
    file_system: String,
    size: u64,
    free_space: u64,
});
wmi_record!(NetworkAdapter {
    description: String, mac_address: String, ip_address: Vec<String>,
    default_ip_gateway: Vec<String>, dhcp_enabled: bool,
});

#[derive(Debug, Deserialize)]
#[serde(rename_all(deserialize = "PascalCase"))]
struct DiskCounter {
    name: Option<String>,
    disk_read_bytes_per_sec: Option<f64>,
    disk_write_bytes_per_sec: Option<f64>,
}
fn connect() -> Result<WMIConnection, String> {
    let com = COMLibrary::new().map_err(|e| format!("COM initialization failed: {e}"))?;
    WMIConnection::new(com.into()).map_err(|e| format!("WMI connection to ROOT\\CIMV2 failed: {e}"))
}

fn query<T: for<'de> Deserialize<'de>>(
    connection: &WMIConnection,
    class: &str,
) -> Result<Vec<T>, String> {
    connection
        .raw_query::<T>(&format!("SELECT * FROM {class}"))
        .map_err(|e| format!("WMI query {class} failed: {e}"))
}

fn first<T: for<'de> Deserialize<'de>>(
    connection: &WMIConnection,
    class: &str,
) -> Result<Option<T>, String> {
    Ok(query(connection, class)?.into_iter().next())
}

fn timestamp_ms() -> Result<u128, String> {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|v| v.as_millis())
        .map_err(|e| format!("system clock error: {e}"))
}

fn value<T: Copy>(v: Option<T>) -> Option<f64>
where
    f64: From<T>,
{
    v.map(f64::from)
}

pub fn bytes_to_gib(bytes: Option<u64>) -> Option<f64> {
    bytes.map(|v| v as f64 / BYTES_PER_GIB)
}

/// Быстрый поток показаний для GUI без тяжёлых WMI-запросов инвентаризации.
pub fn collect_fast_sensors() -> (Vec<SensorReading>, Vec<String>) {
    let mut sensors = Vec::new();
    let mut warnings = Vec::new();
    collect_network_sensors(&mut sensors, &mut warnings);
    crate::counters::collect(&mut sensors, &mut warnings);
    (sensors, warnings)
}

/// Собирает фактические данные через WMI. Недоступная категория попадает в warnings;
/// никакие нули-заглушки не создаются.
pub fn collect_snapshot() -> Result<(SystemReport, Vec<SensorReading>), String> {
    let connection = connect()?;
    let mut warnings = Vec::new();
    let mut sensors = Vec::new();

    let computer =
        capture_one::<ComputerSystem>(&connection, "Win32_ComputerSystem", &mut warnings);
    let total_memory_gib = bytes_to_gib(computer.as_ref().and_then(|c| c.total_physical_memory));
    let operating_system =
        capture_one::<OperatingSystem>(&connection, "Win32_OperatingSystem", &mut warnings);
    let board = capture_one::<BaseBoard>(&connection, "Win32_BaseBoard", &mut warnings);
    let bios = capture_one::<Bios>(&connection, "Win32_BIOS", &mut warnings);
    let processors = capture_many::<Processor>(&connection, "Win32_Processor", &mut warnings);
    let memory_modules =
        capture_many::<PhysicalMemory>(&connection, "Win32_PhysicalMemory", &mut warnings);
    let graphics =
        capture_many::<VideoController>(&connection, "Win32_VideoController", &mut warnings);
    let disks = capture_many::<DiskDrive>(&connection, "Win32_DiskDrive", &mut warnings);
    let volumes = capture_many::<LogicalDisk>(
        &connection,
        "Win32_LogicalDisk WHERE DriveType=3",
        &mut warnings,
    );
    let network_adapters = capture_many::<NetworkAdapter>(
        &connection,
        "Win32_NetworkAdapterConfiguration WHERE IPEnabled=True",
        &mut warnings,
    );

    for (index, cpu) in processors.iter().enumerate() {
        if let Some(load) = cpu.load_percentage {
            let label = cpu
                .name
                .clone()
                .unwrap_or_else(|| format!("Процессор {index}"));
            sensors.push(SensorReading::new(
                format!("/windows/cpu/{index}/load"),
                &label,
                &label,
                HardwareType::Cpu,
                SensorType::Load,
                "LoadPercentage",
                Some(f64::from(load)),
            ));
        }
    }

    if let (Some(total_kib), Some(free_kib)) = (
        operating_system
            .as_ref()
            .and_then(|o| o.total_visible_memory_size),
        operating_system
            .as_ref()
            .and_then(|o| o.free_physical_memory),
    ) {
        if total_kib > 0 && free_kib <= total_kib {
            let used_gib = (total_kib - free_kib) as f64 / KIB_PER_GIB;
            let free_gib = free_kib as f64 / KIB_PER_GIB;
            let total_gib = total_kib as f64 / KIB_PER_GIB;
            sensors.push(SensorReading::new(
                "/windows/memory/used",
                "Физическая память",
                "Физическая память",
                HardwareType::Memory,
                SensorType::Data,
                "Memory Used",
                Some(used_gib),
            ));
            sensors.push(SensorReading::new(
                "/windows/memory/available",
                "Физическая память",
                "Физическая память",
                HardwareType::Memory,
                SensorType::Data,
                "Memory Available",
                Some(free_gib),
            ));
            sensors.push(SensorReading::new(
                "/windows/memory/load",
                "Физическая память",
                "Физическая память",
                HardwareType::Memory,
                SensorType::Load,
                "Memory Load",
                Some(used_gib / total_gib * 100.0),
            ));
        }
    }

    match query::<DiskCounter>(&connection, "Win32_PerfFormattedData_PerfDisk_PhysicalDisk") {
        Ok(counters) => {
            for counter in counters
                .into_iter()
                .filter(|c| c.name.as_deref() != Some("_Total"))
            {
                let label = counter.name.unwrap_or_else(|| "Накопитель".to_owned());
                if let Some(rate) = value(counter.disk_read_bytes_per_sec).filter(|v| *v >= 0.0) {
                    sensors.push(SensorReading::new(
                        format!("/windows/storage/{label}/read"),
                        &label,
                        &label,
                        HardwareType::Storage,
                        SensorType::Throughput,
                        "Disk Read",
                        Some(rate),
                    ));
                }
                if let Some(rate) = value(counter.disk_write_bytes_per_sec).filter(|v| *v >= 0.0) {
                    sensors.push(SensorReading::new(
                        format!("/windows/storage/{label}/write"),
                        &label,
                        &label,
                        HardwareType::Storage,
                        SensorType::Throughput,
                        "Disk Write",
                        Some(rate),
                    ));
                }
            }
        }
        Err(error) => warnings.push(error),
    }

    collect_network_sensors(&mut sensors, &mut warnings);
    crate::counters::collect(&mut sensors, &mut warnings);

    if sensors.is_empty() {
        warnings.push(WARNING_NO_SENSORS.to_owned());
    }

    let report = SystemReport {
        generated_unix_ms: timestamp_ms()?,
        computer,
        total_memory_gib,
        operating_system,
        board,
        bios,
        processors,
        memory_modules,
        graphics,
        disks,
        volumes,
        network_adapters,
        sensors: sensors.clone(),
        warnings,
    };
    Ok((report, sensors))
}

fn collect_network_sensors(sensors: &mut Vec<SensorReading>, warnings: &mut Vec<String>) {
    let state = NETWORK_STATE.get_or_init(|| {
        Mutex::new(NetworkState {
            networks: Networks::new_with_refreshed_list(),
            last_refresh: None,
        })
    });
    let Ok(mut state) = state.lock() else {
        warnings.push(WARNING_NETWORK_LOCK.to_owned());
        return;
    };
    let now = Instant::now();
    state.networks.refresh(true);
    let elapsed = state
        .last_refresh
        .replace(now)
        .map(|previous| now.duration_since(previous).as_secs_f64())
        .filter(|seconds| *seconds > 0.0);

    for (name, data) in state.networks.list() {
        let label = name.trim();
        if label.is_empty() {
            continue;
        }
        let id_name = label.replace(['/', '\\'], "_");
        if let Some(seconds) = elapsed {
            sensors.push(SensorReading::new(
                format!("/windows/network/{id_name}/download"),
                label,
                label,
                HardwareType::Network,
                SensorType::Throughput,
                "Download",
                Some(data.received() as f64 / seconds),
            ));
            sensors.push(SensorReading::new(
                format!("/windows/network/{id_name}/upload"),
                label,
                label,
                HardwareType::Network,
                SensorType::Throughput,
                "Upload",
                Some(data.transmitted() as f64 / seconds),
            ));
        }
        sensors.push(SensorReading::new(
            format!("/windows/network/{id_name}/downloaded"),
            label,
            label,
            HardwareType::Network,
            SensorType::Data,
            "Data Downloaded",
            Some(data.total_received() as f64 / BYTES_PER_GIB),
        ));
        sensors.push(SensorReading::new(
            format!("/windows/network/{id_name}/uploaded"),
            label,
            label,
            HardwareType::Network,
            SensorType::Data,
            "Data Uploaded",
            Some(data.total_transmitted() as f64 / BYTES_PER_GIB),
        ));
        for (suffix, title, amount) in [
            (
                "rx-packets",
                "Packets Received",
                data.total_packets_received(),
            ),
            (
                "tx-packets",
                "Packets Transmitted",
                data.total_packets_transmitted(),
            ),
            (
                "rx-errors",
                "Receive Errors",
                data.total_errors_on_received(),
            ),
            (
                "tx-errors",
                "Transmit Errors",
                data.total_errors_on_transmitted(),
            ),
        ] {
            sensors.push(SensorReading::new(
                format!("/windows/network/{id_name}/{suffix}"),
                label,
                label,
                HardwareType::Network,
                SensorType::Factor,
                title,
                Some(amount as f64),
            ));
        }
    }
}

fn capture_one<T: for<'de> Deserialize<'de>>(
    connection: &WMIConnection,
    class: &str,
    warnings: &mut Vec<String>,
) -> Option<T> {
    match first(connection, class) {
        Ok(v) => v,
        Err(e) => {
            warnings.push(e);
            None
        }
    }
}

fn capture_many<T: for<'de> Deserialize<'de>>(
    connection: &WMIConnection,
    class: &str,
    warnings: &mut Vec<String>,
) -> Vec<T> {
    match query(connection, class) {
        Ok(v) => v,
        Err(e) => {
            warnings.push(e);
            Vec::new()
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn memory_unit_conversion_is_explicit() {
        assert_eq!(bytes_to_gib(Some(1_073_741_824)), Some(1.0));
        assert_eq!(bytes_to_gib(None), None);
    }

    #[test]
    fn a_snapshot_model_serializes_missing_sensor_data_as_null_not_zero() {
        let reading = SensorReading::new(
            "x",
            "hw",
            "hw",
            HardwareType::Cpu,
            SensorType::Temperature,
            "Tctl",
            None,
        );
        let json = serde_json::to_string(&reading).expect("reading serializes");
        assert!(json.contains("\"value\":null"));
    }
}
