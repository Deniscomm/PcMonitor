//! Shared metric construction helpers.
//!
//! Keeping validation here prevents individual hardware collectors from
//! drifting apart when a new metric is added or a value policy changes.

use crate::monitoring::{HardwareType, SensorReading, SensorType};

pub struct MetricSink<'a> {
    sensors: &'a mut Vec<SensorReading>,
}

impl<'a> MetricSink<'a> {
    pub fn new(sensors: &'a mut Vec<SensorReading>) -> Self {
        Self { sensors }
    }

    pub fn push(
        &mut self,
        id: impl Into<String>,
        hardware: impl Into<String>,
        hardware_type: HardwareType,
        sensor_type: SensorType,
        sensor_name: impl Into<String>,
        value: f64,
    ) {
        if value.is_finite() && value >= 0.0 {
            let hardware = hardware.into();
            self.sensors.push(SensorReading::new(
                id,
                hardware.clone(),
                hardware,
                hardware_type,
                sensor_type,
                sensor_name,
                Some(value),
            ));
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rejects_non_finite_and_negative_values() {
        let mut sensors = Vec::new();
        let mut sink = MetricSink::new(&mut sensors);
        for value in [f64::NAN, f64::INFINITY, -1.0] {
            sink.push(
                "test",
                "CPU",
                HardwareType::Cpu,
                SensorType::Load,
                "load",
                value,
            );
        }
        assert!(sensors.is_empty());
    }
}
