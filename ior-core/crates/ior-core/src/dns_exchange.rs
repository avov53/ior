//! Bounded resolver hedging; dropping the losing future cancels its exchange.
use futures::{stream::FuturesUnordered, StreamExt};
use std::{future::Future, io, time::Duration};

pub async fn hedged<F, Fut>(servers: &[String], attempt: F) -> io::Result<Vec<u8>>
where
    F: Fn(String) -> Fut,
    Fut: Future<Output = io::Result<Vec<u8>>>,
{
    hedged_with_delay(servers, Duration::from_millis(500), attempt).await
}

pub async fn hedged_with_delay<F, Fut>(
    servers: &[String],
    hedge_delay: Duration,
    attempt: F,
) -> io::Result<Vec<u8>>
where
    F: Fn(String) -> Fut,
    Fut: Future<Output = io::Result<Vec<u8>>>,
{
    if servers.is_empty() || servers.len() > 4 {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "DNS_RESOLVER_LIST_INVALID",
        ));
    }
    let mut active = FuturesUnordered::new();
    active.push(observed_attempt(servers[0].clone(), &attempt));
    let mut next = 1;
    let mut delay = Box::pin(tokio::time::sleep(hedge_delay));
    let mut failure = io::Error::new(io::ErrorKind::TimedOut, "DNS_RESOLVERS_FAILED");
    loop {
        tokio::select! {
            result=active.next(),if !active.is_empty()=>match result {
                Some(Ok(answer))=>return Ok(answer),
                Some(Err(error))=>{
                    failure=error;
                    if next<servers.len(){active.push(observed_attempt(servers[next].clone(),&attempt));next+=1;delay.as_mut().reset(tokio::time::Instant::now()+hedge_delay);}
                    else if active.is_empty(){return Err(failure);}
                },
                None=>return Err(failure),
            },
            _=&mut delay,if next<servers.len()=>{
                crate::trace::code("dns","DNS_RESOLVER_HEDGE_STARTED");
                active.push(observed_attempt(servers[next].clone(),&attempt));next+=1;
                delay.as_mut().reset(tokio::time::Instant::now()+hedge_delay);
            }
        }
    }
}

async fn observed_attempt<F, Fut>(server: String, attempt: &F) -> io::Result<Vec<u8>>
where
    F: Fn(String) -> Fut,
    Fut: Future<Output = io::Result<Vec<u8>>>,
{
    let started = std::time::Instant::now();
    crate::trace::emit(serde_json::json!({"stage":"dns","code":"DNS_RESOLVER_STARTED","resolver":server}));
    let result = attempt(server.clone()).await;
    let negative = result.as_ref().err().is_some_and(|error|matches!(failure_code(error),"DNS_ADDRESS_NODATA"|"TUN_DNS_ADDRESS_NODATA"|"DNS_ADDRESS_NXDOMAIN"));
    crate::trace::emit(
        serde_json::json!({"stage":"dns","code":if result.is_ok(){"DNS_RESOLVER_COMPLETE"}else if negative{"DNS_RESOLVER_NEGATIVE_ANSWER"}else{"DNS_RESOLVER_FAILED"},"classification":if negative{"DNS_NEGATIVE_ANSWER"}else if result.is_ok(){"SUCCESS"}else{"RESOLVER_ATTEMPT_FAILURE"},"resolver":server,"durationMs":started.elapsed().as_millis() as u64,
            "reason":result.as_ref().err().map(failure_code)}),
    );
    result
}

pub fn failure_code(error: &io::Error) -> &'static str {
    match error.to_string().as_str() {
        "DNS_ADDRESS_NODATA" => "DNS_ADDRESS_NODATA",
        "DNS_ADDRESS_RESPONSE_INVALID" => "DNS_ADDRESS_RESPONSE_INVALID",
        "DNS_ADDRESS_RESPONSE_MISMATCH" => "DNS_ADDRESS_RESPONSE_MISMATCH",
        "DNS_ADDRESS_NXDOMAIN" => "DNS_ADDRESS_NXDOMAIN",
        "DNS_ADDRESS_SERVER_FAILURE" => "DNS_ADDRESS_SERVER_FAILURE",
        "DNS_ADDRESS_TRUNCATED" => "DNS_ADDRESS_TRUNCATED",
        "TUN_DNS_QUERY_INVALID" => "TUN_DNS_QUERY_INVALID",
        "TUN_DNS_CNAME_INCOMPLETE" => "TUN_DNS_CNAME_INCOMPLETE",
        "TUN_DNS_ADDRESS_NODATA" => "TUN_DNS_ADDRESS_NODATA",
        "TUN_DNS_RESPONSE_MISMATCH" => "TUN_DNS_RESPONSE_MISMATCH",
        "TUN_DNS_RESPONSE_INVALID" => "TUN_DNS_RESPONSE_INVALID",
        "TUN_DNS_SERVER_FAILURE" => "TUN_DNS_SERVER_FAILURE",
        "TUN_DNS_TCP_TRUNCATED" => "TUN_DNS_TCP_TRUNCATED",
        _ => match error.kind() {
            io::ErrorKind::TimedOut => "TUN_DNS_TIMEOUT",
            io::ErrorKind::UnexpectedEof => "TUN_DNS_RESPONSE_EOF",
            io::ErrorKind::InvalidData => "TUN_DNS_INVALID_DATA",
            _ => "TUN_DNS_TRANSPORT_FAILURE",
        },
    }
}

#[cfg(test)]
mod tests {
    #[test]
    fn resource_dns_negative_answers_are_not_transport_failures(){
        for code in ["DNS_ADDRESS_NODATA","DNS_ADDRESS_NXDOMAIN","DNS_ADDRESS_SERVER_FAILURE","DNS_ADDRESS_TRUNCATED","DNS_ADDRESS_RESPONSE_MISMATCH"]{
            let error=std::io::Error::new(std::io::ErrorKind::NotFound,code);assert_eq!(super::failure_code(&error),code);
        }
    }
    use super::*;
    #[tokio::test]
    async fn early_hedge_cancels_loser() {
        use std::sync::{
            atomic::{AtomicUsize, Ordering},
            Arc,
        };
        struct Guard(Arc<AtomicUsize>);
        impl Drop for Guard {
            fn drop(&mut self) {
                self.0.fetch_add(1, Ordering::SeqCst);
            }
        }
        let drops = Arc::new(AtomicUsize::new(0));
        let start = tokio::time::Instant::now();
        let result = hedged_with_delay(
            &["slow".into(), "fast".into()],
            Duration::from_millis(150),
            |name| {
                let drops = drops.clone();
                async move {
                    if name == "slow" {
                        let _guard = Guard(drops);
                        std::future::pending::<()>().await;
                    }
                    Ok(vec![9])
                }
            },
        )
        .await
        .unwrap();
        assert_eq!(result, vec![9]);
        assert!(start.elapsed() >= Duration::from_millis(150));
        assert!(start.elapsed() < Duration::from_millis(400));
        assert_eq!(drops.load(Ordering::SeqCst), 1);
    }
    #[tokio::test]
    async fn explicit_failure_does_not_wait_for_hedge() {
        let start = tokio::time::Instant::now();
        let result = hedged_with_delay(
            &["bad".into(), "fast".into()],
            Duration::from_secs(10),
            |name| async move {
                if name == "bad" {
                    Err(io::ErrorKind::ConnectionRefused.into())
                } else {
                    Ok(vec![3])
                }
            },
        )
        .await
        .unwrap();
        assert_eq!(result, vec![3]);
        assert!(start.elapsed() < Duration::from_millis(100));
    }
    #[tokio::test]
    async fn stalled_first_resolver_does_not_hide_second_answer() {
        let start = tokio::time::Instant::now();
        let result = hedged(&["slow".into(), "fast".into()], |name| async move {
            if name == "slow" {
                std::future::pending::<()>().await;
            }
            Ok(vec![7])
        })
        .await
        .unwrap();
        assert_eq!(result, vec![7]);
        assert!(start.elapsed() < Duration::from_secs(1));
    }
    #[tokio::test]
    async fn one_configured_resolver_does_not_invent_backups() {
        let calls=std::sync::atomic::AtomicUsize::new(0);
        let result=hedged(&["only".into()],|name|{assert_eq!(name,"only");calls.fetch_add(1,std::sync::atomic::Ordering::Relaxed);async{Err(std::io::Error::from(std::io::ErrorKind::TimedOut))}}).await;
        assert!(result.is_err());assert_eq!(calls.load(std::sync::atomic::Ordering::Relaxed),1);
    }
    #[tokio::test]
    async fn configured_resolver_total_failure_stays_failure() {
        let result=hedged(&["one".into(),"two".into()],|_|async{Err(std::io::Error::from(std::io::ErrorKind::ConnectionRefused))}).await;
        assert!(result.is_err());
    }

}
