use eframe::egui::{self, Color32, RichText, Stroke, Vec2};
use pcmonitor_core::{
    collect_fast_sensors,
    monitoring::{SensorReading, SensorType},
};
use std::sync::mpsc::{self, Receiver};
use std::thread;
use std::time::Duration;

#[derive(Clone, Copy, PartialEq, Eq)]
enum Page {
    Dashboard,
    Sensors,
    System,
    Settings,
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum Lang {
    EnGb,
    EnUs,
    Ru,
    Zh,
}

impl Lang {
    fn name(self) -> &'static str {
        match self {
            Self::EnGb => "English (UK)",
            Self::EnUs => "English (US)",
            Self::Ru => "Русский",
            Self::Zh => "简体中文",
        }
    }
    fn code(self) -> &'static str {
        match self {
            Self::EnGb => "en-GB",
            Self::EnUs => "en-US",
            Self::Ru => "ru",
            Self::Zh => "zh-CN",
        }
    }
}

struct MonitorApp {
    page: Page,
    lang: Lang,
    sensors: Vec<SensorReading>,
    rx: Receiver<Vec<SensorReading>>,
    filter: String,
    paused: bool,
    last_update: String,
}

impl MonitorApp {
    fn new(cc: &eframe::CreationContext<'_>, rx: Receiver<Vec<SensorReading>>) -> Self {
        let mut visuals = egui::Visuals::dark();
        visuals.panel_fill = Color32::from_rgb(20, 24, 31);
        visuals.window_fill = Color32::from_rgb(24, 29, 37);
        visuals.extreme_bg_color = Color32::from_rgb(13, 16, 21);
        visuals.widgets.inactive.bg_fill = Color32::from_rgb(35, 42, 53);
        visuals.widgets.hovered.bg_fill = Color32::from_rgb(48, 62, 78);
        cc.egui_ctx.set_visuals(visuals);
        Self {
            page: Page::Dashboard,
            lang: Lang::EnUs,
            sensors: Vec::new(),
            rx,
            filter: String::new(),
            paused: false,
            last_update: "—".into(),
        }
    }

    fn tr(&self, key: &str) -> &'static str {
        match (self.lang, key) {
            (Lang::Ru, "dashboard") => "Дашборд",
            (Lang::Ru, "sensors") => "Датчики",
            (Lang::Ru, "system") => "Система",
            (Lang::Ru, "settings") => "Настройки",
            (Lang::Ru, "pause") => "Пауза",
            (Lang::Ru, "resume") => "Продолжить",
            (Lang::Ru, "search") => "Поиск датчиков",
            (Lang::Ru, "no_data") => "Нет данных",
            (Lang::Zh, "dashboard") => "仪表盘",
            (Lang::Zh, "sensors") => "传感器",
            (Lang::Zh, "system") => "系统",
            (Lang::Zh, "settings") => "设置",
            (Lang::Zh, "pause") => "暂停",
            (Lang::Zh, "resume") => "继续",
            (Lang::Zh, "search") => "搜索传感器",
            (Lang::Zh, "no_data") => "暂无数据",
            (_, "dashboard") => "Dashboard",
            (_, "sensors") => "Sensors",
            (_, "system") => "System",
            (_, "settings") => "Settings",
            (_, "pause") => "Pause",
            (_, "resume") => "Resume",
            (_, "search") => "Search sensors",
            (_, "no_data") => "No data",
            (_, _) => "PC Monitor",
        }
    }

    fn title(&self, sensor: &SensorReading) -> String {
        match (self.lang, sensor.sensor_name.as_str()) {
            (Lang::Ru, "CPU Total") => "Загрузка процессора".into(),
            (Lang::Ru, "Memory Load") => "Загрузка памяти".into(),
            (Lang::Ru, "Memory Used") => "Память занято".into(),
            (Lang::Ru, "Memory Available") => "Память доступно".into(),
            (Lang::Ru, "Download") => "Сеть: приём".into(),
            (Lang::Ru, "Upload") => "Сеть: отдача".into(),
            (Lang::Zh, "CPU Total") => "处理器负载".into(),
            (Lang::Zh, "Memory Load") => "内存负载".into(),
            (Lang::Zh, "Memory Used") => "已用内存".into(),
            (Lang::Zh, "Memory Available") => "可用内存".into(),
            (Lang::Zh, "Download") => "网络接收".into(),
            (Lang::Zh, "Upload") => "网络发送".into(),
            _ => sensor.sensor_name.clone(),
        }
    }

    fn value(&self, sensor: &SensorReading) -> String {
        sensor
            .value
            .map(|v| match sensor.sensor_type {
                SensorType::Load | SensorType::Control | SensorType::Level => format!("{v:.1} %"),
                SensorType::Data => format!("{v:.2} GB"),
                SensorType::Throughput => format_rate(v),
                SensorType::Clock => format!("{v:.0} MHz"),
                _ => format!("{v:.1} {}", sensor.sensor_type.unit()),
            })
            .unwrap_or_else(|| "—".into())
    }

    fn card(&self, ui: &mut egui::Ui, sensor: &SensorReading) {
        let value = sensor.value.unwrap_or(0.0);
        egui::Frame::group(ui.style())
            .stroke(Stroke::new(1.0, Color32::from_rgb(52, 65, 82)))
            .inner_margin(16.0)
            .show(ui, |ui| {
                ui.set_min_size(Vec2::new(240.0, 120.0));
                ui.label(RichText::new(self.title(sensor)).color(Color32::from_rgb(160, 174, 192)));
                ui.add_space(8.0);
                ui.label(
                    RichText::new(self.value(sensor))
                        .size(28.0)
                        .strong()
                        .color(Color32::from_rgb(235, 240, 247)),
                );
                if matches!(
                    sensor.sensor_type,
                    SensorType::Load | SensorType::Level | SensorType::Control
                ) {
                    ui.add_space(8.0);
                    ui.add(
                        egui::ProgressBar::new((value as f32 / 100.0).clamp(0.0, 1.0))
                            .desired_height(8.0)
                            .show_percentage(),
                    );
                }
                ui.small(format!("{} · {}", sensor.hardware_group, sensor.hardware));
            });
    }

    fn dashboard(&mut self, ui: &mut egui::Ui) {
        ui.heading(RichText::new(self.tr("dashboard")).size(26.0));
        ui.add_space(12.0);
        let wanted = [
            "CPU Total",
            "CPU Core Reported Clock",
            "Memory Load",
            "Memory Used",
            "Memory Available",
            "Download",
            "Upload",
        ];
        let mut cards: Vec<SensorReading> = wanted
            .iter()
            .filter_map(|name| {
                self.sensors
                    .iter()
                    .find(|s| s.sensor_name == *name)
                    .cloned()
            })
            .collect();
        cards.truncate(12);
        egui::Grid::new("dashboard_grid")
            .num_columns(3)
            .spacing([12.0, 12.0])
            .show(ui, |ui| {
                for (index, sensor) in cards.iter().enumerate() {
                    self.card(ui, sensor);
                    if index % 3 == 2 {
                        ui.end_row();
                    }
                }
            });
        if cards.is_empty() {
            ui.label(self.tr("no_data"));
        }
    }

    fn sensors_page(&mut self, ui: &mut egui::Ui) {
        let search_hint = self.tr("search");
        ui.horizontal(|ui| {
            ui.heading(self.tr("sensors"));
            ui.separator();
            ui.add(egui::TextEdit::singleline(&mut self.filter).hint_text(search_hint));
        });
        ui.add_space(8.0);
        egui::ScrollArea::vertical().show(ui, |ui| {
            egui::Grid::new("sensor_table")
                .striped(true)
                .num_columns(4)
                .spacing([18.0, 6.0])
                .show(ui, |ui| {
                    ui.strong("Device");
                    ui.strong("Sensor");
                    ui.strong("Value");
                    ui.strong("ID");
                    ui.end_row();
                    let query = self.filter.to_lowercase();
                    for sensor in self.sensors.iter().filter(|s| {
                        query.is_empty()
                            || s.sensor_name.to_lowercase().contains(&query)
                            || s.hardware.to_lowercase().contains(&query)
                    }) {
                        ui.label(&sensor.hardware);
                        ui.label(self.title(sensor));
                        ui.label(self.value(sensor));
                        ui.small(&sensor.id);
                        ui.end_row();
                    }
                });
        });
    }

    fn settings(&mut self, ui: &mut egui::Ui) {
        ui.heading(self.tr("settings"));
        ui.add_space(12.0);
        ui.horizontal(|ui| {
            ui.label("Language / Язык / 语言");
            egui::ComboBox::from_id_salt("language")
                .selected_text(self.lang.name())
                .show_ui(ui, |ui| {
                    for lang in [Lang::EnGb, Lang::EnUs, Lang::Ru, Lang::Zh] {
                        ui.selectable_value(&mut self.lang, lang, lang.name());
                    }
                });
        });
        let pause_label = if self.paused {
            self.tr("resume")
        } else {
            self.tr("pause")
        };
        ui.checkbox(&mut self.paused, pause_label);
        ui.label(format!("Locale: {}", self.lang.code()));
    }
}

impl eframe::App for MonitorApp {
    fn ui(&mut self, ui: &mut egui::Ui, _frame: &mut eframe::Frame) {
        if !self.paused {
            while let Ok(sensors) = self.rx.try_recv() {
                self.sensors = sensors;
                self.last_update = "now".into();
            }
        }
        ui.horizontal(|ui| {
            ui.label(RichText::new("PC Monitor").size(20.0).strong());
            ui.separator();
            ui.small(format!(
                "{} sensors · updated {}",
                self.sensors.len(),
                self.last_update
            ));
        });
        ui.separator();
        ui.horizontal(|ui| {
            for (page, label) in [
                (Page::Dashboard, self.tr("dashboard")),
                (Page::Sensors, self.tr("sensors")),
                (Page::System, self.tr("system")),
                (Page::Settings, self.tr("settings")),
            ] {
                if ui.selectable_label(self.page == page, label).clicked() {
                    self.page = page;
                }
            }
        });
        ui.separator();
        match self.page {
            Page::Dashboard => self.dashboard(ui),
            Page::Sensors => self.sensors_page(ui),
            Page::Settings => self.settings(ui),
            Page::System => {
                ui.heading(self.tr("system"));
                ui.label("Live system inventory is collected by the Rust monitoring core.");
            }
        }
        ui.separator();
        ui.small("Rust native GUI · no C#");
        ui.ctx().request_repaint_after(Duration::from_millis(250));
    }
}

fn format_rate(value: f64) -> String {
    let abs = value.abs();
    if abs < 1024.0 {
        format!("{value:.0} B/s")
    } else if abs < 1_048_576.0 {
        format!("{:.1} KB/s", value / 1024.0)
    } else {
        format!("{:.2} MB/s", value / 1_048_576.0)
    }
}

fn main() -> eframe::Result {
    let (tx, rx) = mpsc::channel::<Vec<SensorReading>>();
    thread::spawn(move || loop {
        let (sensors, _) = collect_fast_sensors();
        let _ = tx.send(sensors);
        thread::sleep(Duration::from_secs(1));
    });
    let options = eframe::NativeOptions {
        viewport: egui::ViewportBuilder::default()
            .with_inner_size([1280.0, 800.0])
            .with_min_inner_size([900.0, 600.0]),
        ..Default::default()
    };
    eframe::run_native(
        "PC Monitor",
        options,
        Box::new(move |cc| Ok(Box::new(MonitorApp::new(cc, rx)))),
    )
}
