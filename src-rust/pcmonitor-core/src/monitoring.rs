use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum SensorType {
    Voltage,
    Current,
    Power,
    Clock,
    Temperature,
    Load,
    Frequency,
    Fan,
    Flow,
    Control,
    Level,
    Factor,
    Data,
    SmallData,
    Throughput,
    TimeSpan,
    Energy,
    Humidity,
    Conductivity,
    Timing,
}

impl SensorType {
    pub fn unit(self) -> &'static str {
        match self {
            Self::Voltage => "В",
            Self::Current => "А",
            Self::Power => "Вт",
            Self::Clock => "МГц",
            Self::Temperature => "°C",
            Self::Load | Self::Control | Self::Level | Self::Humidity => "%",
            Self::Frequency => "Гц",
            Self::Fan => "об/мин",
            Self::Flow => "л/ч",
            Self::Factor | Self::TimeSpan | Self::Timing => "",
            Self::Data => "ГБ",
            Self::SmallData => "МБ",
            Self::Throughput => "Б/с",
            Self::Energy => "мВт·ч",
            Self::Conductivity => "мкСм/см",
        }
    }

    pub fn type_name(self) -> &'static str {
        match self {
            Self::Voltage => "Напряжение",
            Self::Current => "Ток",
            Self::Power => "Мощность",
            Self::Clock => "Частота",
            Self::Temperature => "Температура",
            Self::Load => "Загрузка",
            Self::Frequency => "Частота шины",
            Self::Fan => "Вентилятор",
            Self::Flow => "Поток",
            Self::Control => "Управление",
            Self::Level => "Уровень",
            Self::Factor => "Множитель",
            Self::Data | Self::SmallData => "Объём данных",
            Self::Throughput => "Скорость",
            Self::TimeSpan => "Время",
            Self::Energy => "Энергия",
            Self::Humidity => "Влажность",
            Self::Conductivity => "Проводимость",
            Self::Timing => "Тайминг",
        }
    }

    pub fn order(self) -> u8 {
        match self {
            Self::Temperature => 0,
            Self::Fan => 1,
            Self::Control => 2,
            Self::Load => 3,
            Self::Clock => 4,
            Self::Power => 5,
            Self::Voltage => 6,
            Self::Current => 7,
            Self::Data => 8,
            Self::SmallData => 9,
            Self::Throughput => 10,
            Self::Level => 11,
            Self::Factor => 12,
            Self::Frequency => 13,
            Self::Energy => 14,
            Self::TimeSpan => 15,
            Self::Humidity => 16,
            Self::Conductivity => 17,
            Self::Flow => 18,
            Self::Timing => 19,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum HardwareType {
    Cpu,
    Motherboard,
    SuperIo,
    Memory,
    GpuNvidia,
    GpuAmd,
    GpuIntel,
    Storage,
    Network,
    Cooler,
    EmbeddedController,
    Psu,
    Battery,
    PowerMonitor,
    OperatingSystem,
}

impl HardwareType {
    pub fn group_name(self) -> &'static str {
        match self {
            Self::Cpu => "Процессор",
            Self::Motherboard | Self::SuperIo => "Мат. плата",
            Self::Memory => "Оперативная память",
            Self::GpuNvidia | Self::GpuAmd | Self::GpuIntel => "Видеокарта",
            Self::Storage => "Накопители",
            Self::Network => "Сеть",
            Self::Cooler => "Охлаждение",
            Self::EmbeddedController => "Контроллер",
            Self::Psu => "Блок питания",
            Self::Battery => "Батарея",
            Self::PowerMonitor => "Питание",
            Self::OperatingSystem => "Система",
        }
    }

    pub fn order(self) -> u8 {
        match self {
            Self::Cpu => 0,
            Self::GpuNvidia | Self::GpuAmd | Self::GpuIntel => 1,
            Self::Memory => 2,
            Self::Motherboard | Self::SuperIo => 3,
            Self::Storage => 4,
            Self::Cooler | Self::EmbeddedController => 5,
            Self::Network => 6,
            Self::Psu | Self::PowerMonitor => 7,
            Self::Battery => 8,
            Self::OperatingSystem => 9,
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SensorReading {
    pub id: String,
    pub hardware: String,
    pub own_name: String,
    pub hardware_group: String,
    pub hardware_type: HardwareType,
    pub sensor_type: SensorType,
    pub sensor_name: String,
    pub value: Option<f64>,
    pub min: Option<f64>,
    pub max: Option<f64>,
    pub is_threshold: bool,
}

impl SensorReading {
    pub fn new(
        id: impl Into<String>,
        hardware: impl Into<String>,
        own_name: impl Into<String>,
        hardware_type: HardwareType,
        sensor_type: SensorType,
        sensor_name: impl Into<String>,
        value: Option<f64>,
    ) -> Self {
        let sensor_name = sensor_name.into();
        let value = value.filter(|v| v.is_finite());
        let is_threshold = is_threshold_sensor(sensor_type, &sensor_name);
        Self {
            id: id.into(),
            hardware: hardware.into(),
            own_name: own_name.into(),
            hardware_group: hardware_type.group_name().to_owned(),
            hardware_type,
            sensor_type,
            sensor_name,
            min: value,
            max: value,
            value,
            is_threshold,
        }
    }
}

const THRESHOLD_MARKERS: [&str; 6] = [
    "warning temperature",
    "critical temperature",
    "threshold",
    "порог",
    "alarm",
    "limit",
];

pub fn is_threshold_sensor(kind: SensorType, name: &str) -> bool {
    kind == SensorType::Temperature
        && THRESHOLD_MARKERS
            .iter()
            .any(|marker| name.to_lowercase().contains(marker))
}

pub fn is_valid_measurement(value: Option<f64>, kind: SensorType, name: &str) -> bool {
    value.is_some_and(f64::is_finite) && !is_threshold_sensor(kind, name)
}

pub fn format_value(kind: SensorType, value: f64) -> String {
    if !value.is_finite() {
        return "—".to_owned();
    }
    let (number, unit) = match kind {
        SensorType::Temperature => (format_fixed(value, 1), "°C"),
        SensorType::Voltage => (format_fixed(value, 3), "В"),
        SensorType::Current => (format_fixed(value, 3), "А"),
        SensorType::Power => (
            format_fixed(value, if value.abs() < 10.0 { 2 } else { 1 }),
            "Вт",
        ),
        SensorType::Clock | SensorType::Fan => (
            format_fixed(value, 0),
            if kind == SensorType::Clock {
                "МГц"
            } else {
                "об/мин"
            },
        ),
        SensorType::Load | SensorType::Control | SensorType::Level | SensorType::Humidity => {
            (format_fixed(value, 1), "%")
        }
        SensorType::Data => (format_fixed(value, 2), "ГБ"),
        SensorType::SmallData => (format_fixed(value, 1), "МБ"),
        SensorType::Energy => (format_fixed(value, 3), "мВт·ч"),
        SensorType::Conductivity => (format_fixed(value, 1), "мкСм/см"),
        SensorType::Flow => (format_fixed(value, 1), "л/ч"),
        SensorType::Throughput => return format_throughput(value),
        SensorType::Frequency => return format_frequency(value),
        SensorType::TimeSpan => return format_duration(value),
        SensorType::Factor | SensorType::Timing => (format_fixed(value, 3), ""),
    };
    if unit.is_empty() {
        number
    } else {
        format!("{number} {unit}")
    }
}

pub fn format_throughput(bytes_per_second: f64) -> String {
    let abs = bytes_per_second.abs();
    if abs < 1024.0 {
        return format!("{} Б/с", format_fixed(bytes_per_second, 0));
    }
    if abs < 1024.0 * 1024.0 {
        return format!("{} КБ/с", format_fixed(bytes_per_second / 1024.0, 1));
    }
    if abs < 1024.0 * 1024.0 * 1024.0 {
        return format!("{} МБ/с", format_fixed(bytes_per_second / 1_048_576.0, 2));
    }
    format!(
        "{} ГБ/с",
        format_fixed(bytes_per_second / 1_073_741_824.0, 2)
    )
}

pub fn format_frequency(hz: f64) -> String {
    let abs = hz.abs();
    if abs < 1_000.0 {
        return format!("{} Гц", format_fixed(hz, 0));
    }
    if abs < 1_000_000.0 {
        return format!("{} кГц", format_fixed(hz / 1_000.0, 1));
    }
    if abs < 1_000_000_000.0 {
        return format!("{} МГц", format_fixed(hz / 1_000_000.0, 1));
    }
    format!("{} ГГц", format_fixed(hz / 1_000_000_000.0, 2))
}

fn format_duration(seconds: f64) -> String {
    let s = seconds.max(0.0) as u64;
    let days = s / 86_400;
    let hours = (s / 3_600) % 24;
    let minutes = (s / 60) % 60;
    let rest = s % 60;
    if days > 0 {
        format!("{days} д {hours:02}:{minutes:02}:{rest:02}")
    } else if s >= 3_600 {
        format!("{}:{minutes:02}:{rest:02}", s / 3_600)
    } else if s >= 60 {
        format!("{}:{rest:02}", s / 60)
    } else {
        format!("{} с", format_fixed(seconds, 1))
    }
}

fn format_fixed(value: f64, decimals: usize) -> String {
    let scale = 10_f64.powi(decimals as i32);
    let rounded = (value * scale).round() / scale;
    format!("{rounded:.decimals$}").replace('.', ",")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn all_sensor_types_have_stable_labels_and_units() {
        assert_eq!(SensorType::Temperature.unit(), "°C");
        assert_eq!(SensorType::Temperature.type_name(), "Температура");
        assert_eq!(SensorType::Throughput.unit(), "Б/с");
        assert_eq!(SensorType::Timing.order(), 19);
    }

    #[test]
    fn non_finite_values_are_discarded_and_formatted_as_missing() {
        assert_eq!(format_value(SensorType::Temperature, f64::NAN), "—");
        assert_eq!(
            SensorReading::new(
                "id",
                "hw",
                "hw",
                HardwareType::Cpu,
                SensorType::Temperature,
                "Tctl",
                Some(f64::INFINITY)
            )
            .value,
            None
        );
    }

    #[test]
    fn threshold_values_are_not_measurements() {
        assert!(is_threshold_sensor(
            SensorType::Temperature,
            "Warning Temperature"
        ));
        assert!(!is_threshold_sensor(SensorType::Voltage, "Voltage limit"));
        assert!(!is_valid_measurement(
            Some(80.0),
            SensorType::Temperature,
            "Warning Temperature"
        ));
    }

    #[test]
    fn russian_unit_formatting_matches_monitor_catalog() {
        assert_eq!(format_value(SensorType::Temperature, 54.25), "54,3 °C");
        assert_eq!(format_throughput(1_572_864.0), "1,50 МБ/с");
        assert_eq!(format_frequency(2_500_000.0), "2,5 МГц");
    }

    #[test]
    fn metric_contract_keeps_canonical_groups_and_sensor_names() {
        let cases = [
            (HardwareType::Cpu, SensorType::Load, "CPU Total"),
            (HardwareType::GpuNvidia, SensorType::Load, "GPU Core"),
            (HardwareType::Memory, SensorType::Data, "Memory Used"),
            (HardwareType::Memory, SensorType::Data, "Memory Available"),
            (HardwareType::Memory, SensorType::Load, "Memory Load"),
            (HardwareType::Network, SensorType::Throughput, "Download"),
            (HardwareType::Network, SensorType::Throughput, "Upload"),
            (HardwareType::Network, SensorType::Data, "Data Downloaded"),
            (HardwareType::Network, SensorType::Data, "Data Uploaded"),
        ];

        for (hardware_type, sensor_type, sensor_name) in cases {
            let reading = SensorReading::new(
                "test-id",
                "test-hardware",
                "test-hardware",
                hardware_type,
                sensor_type,
                sensor_name,
                Some(1.0),
            );
            assert_eq!(reading.hardware_group, hardware_type.group_name());
            assert_eq!(reading.sensor_name, sensor_name);
        }
    }
}
