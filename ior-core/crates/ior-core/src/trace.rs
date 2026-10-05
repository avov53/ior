use std::{
    sync::{
        atomic::{AtomicU64, Ordering},
        OnceLock,
    },
    time::Instant,
};
static RUN: OnceLock<String> = OnceLock::new();
static NEXT: AtomicU64 = AtomicU64::new(1);
#[derive(Clone)]
pub struct Flow {
    pub id: u64,
    pub started: Instant,
}
tokio::task_local! {pub static FLOW:Flow;}
pub fn new_flow() -> Flow {
    Flow {
        id: NEXT.fetch_add(1, Ordering::Relaxed),
        started: Instant::now(),
    }
}
pub fn set_run(id: &str) {
    let _ = RUN.set(id.to_owned());
}
pub fn current() -> Option<Flow> {
    FLOW.try_with(Clone::clone).ok()
}
pub fn emit_with_flow(mut event: serde_json::Value, flow: Option<&Flow>) {
    if let Some(run) = RUN.get() {
        event["runId"] = run.clone().into();
    }
    if let Some(flow) = flow {
        event["flowId"] = flow.id.into();
        event["elapsedMs"] =
            (flow.started.elapsed().as_millis().min(u64::MAX as u128) as u64).into();
    }
    eprintln!("{event}");
}
pub fn emit(mut event: serde_json::Value) {
    if let Some(run) = RUN.get() {
        event["runId"] = run.clone().into();
    }
    let _ = FLOW.try_with(|flow| {
        event["flowId"] = flow.id.into();
        event["elapsedMs"] =
            (flow.started.elapsed().as_millis().min(u64::MAX as u128) as u64).into();
    });
    eprintln!("{event}");
}
pub fn code(stage: &str, code: &str) {
    emit(serde_json::json!({"stage":stage,"code":code}));
}
