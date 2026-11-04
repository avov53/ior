use std::{
    sync::{
        atomic::{AtomicU64, Ordering},
        OnceLock,
    },
    time::Instant,
};
static RUN: OnceLock<String> = OnceLock::new();
static NEXT: AtomicU64 = AtomicU64::new(1);
static CONTEXT: std::sync::RwLock<Option<std::sync::Arc<serde_json::Value>>> = std::sync::RwLock::new(None);
/// Metadata only: no credentials, links, SNI or profile names.
pub fn set_context(session: &str, protocol: &str, transport: &str) {
    if let Ok(mut context) = CONTEXT.write() {
        *context = Some(std::sync::Arc::new(serde_json::json!({"sessionId":session,"protocol":protocol,"profileTransport":transport})));
    }
}
fn context() -> Option<std::sync::Arc<serde_json::Value>> {
    FLOW.try_with(|flow|flow.context.clone()).unwrap_or_else(|_|CONTEXT.read().ok().and_then(|context|context.clone()))
}
fn attach_context(event: &mut serde_json::Value, context: Option<&serde_json::Value>) {
    if let Some(serde_json::Value::Object(context)) = context {
        for (key, value) in context { event[key] = value.clone(); }
    }
}
#[cfg(target_os="android")]
type Sink=std::sync::Arc<dyn Fn(serde_json::Value)+Send+Sync>;
#[cfg(target_os="android")]
static SINK:std::sync::RwLock<Option<Sink>>=std::sync::RwLock::new(None);
#[cfg(target_os="android")]
pub fn set_sink(sink:Option<Sink>){*SINK.write().unwrap()=sink;}
fn output(event:serde_json::Value){
    #[cfg(target_os="android")]
    if let Some(sink)=SINK.read().ok().and_then(|value|value.clone()){sink(event);return;}
    eprintln!("{event}");
}
#[derive(Clone)]
pub struct Flow {
    pub id: u64,
    pub started: Instant,
    context: Option<std::sync::Arc<serde_json::Value>>,
}
tokio::task_local! {pub static FLOW:Flow;}
pub fn new_flow() -> Flow {
    Flow {
        id: NEXT.fetch_add(1, Ordering::Relaxed),
        started: Instant::now(),
        context: context(),
    }
}
pub fn set_run(id: &str) {
    let _ = RUN.set(id.to_owned());
}
pub fn current() -> Option<Flow> {
    FLOW.try_with(Clone::clone).ok()
}
pub fn emit_with_flow(mut event: serde_json::Value, flow: Option<&Flow>) {
    attach_context(&mut event, flow.and_then(|flow|flow.context.as_deref()));
    if let Some(run) = RUN.get() {
        event["runId"] = run.clone().into();
    }
    if let Some(flow) = flow {
        event["flowId"] = flow.id.into();
        event["elapsedMs"] =
            (flow.started.elapsed().as_millis().min(u64::MAX as u128) as u64).into();
    }
    output(event);
}
pub fn emit(mut event: serde_json::Value) {
    attach_context(&mut event, context().as_deref());
    if let Some(run) = RUN.get() {
        event["runId"] = run.clone().into();
    }
    let _ = FLOW.try_with(|flow| {
        event["flowId"] = flow.id.into();
        event["elapsedMs"] =
            (flow.started.elapsed().as_millis().min(u64::MAX as u128) as u64).into();
    });
    output(event);
}
pub fn code(stage: &str, code: &str) {
    emit(serde_json::json!({"stage":stage,"code":code}));
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn child_flow_keeps_parent_session_context() {
        let original=std::sync::Arc::new(serde_json::json!({"sessionId":"old-session","protocol":"hysteria2","profileTransport":"quic"}));
        let parent=Flow{id:900,started:Instant::now(),context:Some(original.clone())};
        let child=FLOW.sync_scope(parent, new_flow);
        assert!(std::sync::Arc::ptr_eq(child.context.as_ref().unwrap(),&original));
        let mut event=serde_json::json!({"code":"TEST"});
        attach_context(&mut event,child.context.as_deref());
        assert_eq!(event["sessionId"],"old-session");assert_eq!(event["protocol"],"hysteria2");
    }
}
