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
    let result = attempt(server.clone()).await;
    crate::trace::emit(
        serde_json::json!({"stage":"dns","code":if result.is_ok(){"DNS_RESOLVER_COMPLETE"}else{"DNS_RESOLVER_FAILED"},"resolver":server,"durationMs":started.elapsed().as_millis() as u64}),
    );
    result
}

#[cfg(test)]
mod tests {
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
}
