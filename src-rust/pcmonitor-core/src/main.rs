use pcmonitor_core::constants::{
    DEFAULT_WATCH_INTERVAL_MS, MAX_WATCH_INTERVAL_MS, MIN_WATCH_INTERVAL_MS,
};
use pcmonitor_core::{collect_fast_sensors, collect_snapshot};
use std::collections::HashMap;
use std::io::{self, Write};
use std::time::{Duration, Instant};

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    if args.iter().any(|arg| arg == "--watch") {
        watch(interval_from_args(&args));
    } else {
        print_one_snapshot();
    }
}

fn interval_from_args(args: &[String]) -> Duration {
    let requested = args
        .iter()
        .find_map(|arg| arg.strip_prefix("--interval-ms="))
        .and_then(|value| value.parse::<u64>().ok())
        .unwrap_or(DEFAULT_WATCH_INTERVAL_MS);
    Duration::from_millis(requested.clamp(MIN_WATCH_INTERVAL_MS, MAX_WATCH_INTERVAL_MS))
}

fn print_one_snapshot() {
    match collect_snapshot() {
        Ok((report, _)) => match serde_json::to_string_pretty(&report) {
            Ok(json) => println!("{json}"),
            Err(error) => {
                eprintln!("Не удалось сериализовать системный снимок: {error}");
                std::process::exit(2);
            }
        },
        Err(error) => {
            eprintln!("Не удалось собрать системный снимок: {error}");
            std::process::exit(1);
        }
    }
}

fn watch(interval: Duration) {
    let mut history: HashMap<String, (f64, f64)> = HashMap::new();
    loop {
        let started = Instant::now();
        let line = {
            let (mut sensors, warnings) = collect_fast_sensors();
            for reading in &mut sensors {
                if let Some(value) = reading.value {
                    let range = history.entry(reading.id.clone()).or_insert((value, value));
                    range.0 = range.0.min(value);
                    range.1 = range.1.max(value);
                    reading.min = Some(range.0);
                    reading.max = Some(range.1);
                }
            }
            serde_json::json!({ "sensors": sensors, "warnings": warnings }).to_string()
        };

        println!("{line}");
        if io::stdout().flush().is_err() {
            break;
        }
        let remaining = interval.saturating_sub(started.elapsed());
        std::thread::sleep(remaining);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn interval_is_bounded_and_defaults_to_one_second() {
        assert_eq!(
            interval_from_args(&[]),
            Duration::from_millis(DEFAULT_WATCH_INTERVAL_MS)
        );
        assert_eq!(
            interval_from_args(&["--interval-ms=50".to_owned()]),
            Duration::from_millis(MIN_WATCH_INTERVAL_MS)
        );
        assert_eq!(
            interval_from_args(&["--interval-ms=999999".to_owned()]),
            Duration::from_millis(MAX_WATCH_INTERVAL_MS)
        );
        assert_eq!(
            interval_from_args(&["--interval-ms=750".to_owned()]),
            Duration::from_millis(750)
        );
    }
}
