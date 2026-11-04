//! Opt-in metadata only. Never changes routes, DNS replies or transport settings.
use std::sync::OnceLock;
fn domains(value:&str)->Vec<String>{value.split(',').take(8).filter_map(|item|{
    let name=item.trim().trim_end_matches('.').to_ascii_lowercase();
    (name.len()<=253 && name.contains('.') && name.parse::<std::net::IpAddr>().is_err() && name.split('.').all(|label|!label.is_empty()&&label.len()<=63&&label.bytes().all(|b|b.is_ascii_alphanumeric()||b==b'-'))).then_some(name)
}).collect()}
pub fn enabled(host:&str)->bool{
    matches(host,filter())
}
fn filter()->&'static Vec<String>{static FILTER:OnceLock<Vec<String>>=OnceLock::new();FILTER.get_or_init(||domains(&std::env::var("IOR_RESOURCE_DIAGNOSTICS").unwrap_or_else(|_|if cfg!(target_os="android"){"googlevideo.com,youtube.com,ytimg.com".into()}else{String::new()})))}
pub fn active()->bool{!filter().is_empty()}
pub fn bulk_udp()->bool{filter().iter().any(|name|name=="googlevideo.com")}
pub fn packet_candidates(names:&[String])->Vec<String>{packet_candidates_for(names,filter())}
fn packet_candidates_for(names:&[String],filters:&[String])->Vec<String>{names.iter().filter(|name|matches(name,filters)).take(4).cloned().collect()}
fn matches(host:&str,filter:&[String])->bool{if filter.is_empty(){return false;}let host=host.trim_end_matches('.');if host.len()>253 || !host.split('.').all(|label|!label.is_empty()&&label.len()<=63&&label.bytes().all(|b|b.is_ascii_alphanumeric()||b==b'-'||b==b'_')){return false;}filter.iter().any(|name|host.eq_ignore_ascii_case(name)||host.len()>name.len()&&host.as_bytes()[host.len()-name.len()-1]==b'.'&&host.get(host.len()-name.len()..).is_some_and(|tail|tail.eq_ignore_ascii_case(name)))}
pub fn emit(host:&str,code:&str,mut value:serde_json::Value){
    value["code"]=code.into();value["destinationName"]=host.into();crate::trace::emit(value);
}
pub fn emit_optional(host:Option<&str>,code:&str,mut value:serde_json::Value){if !active(){return;}if let Some(host)=host{emit(host,code,value);}else{value["code"]=code.into();crate::trace::emit(value);}}
#[cfg(test)]mod candidate_tests{use super::*;#[test]fn multiple_candidates_are_bounded_and_do_not_accept_urls_or_false_suffixes(){let names=["notgooglevideo.com","https://a.googlevideo.com/private","a.googlevideo.com","b.googlevideo.com","c.googlevideo.com","d.googlevideo.com","e.googlevideo.com"].map(str::to_owned);let result=packet_candidates_for(&names,&domains("googlevideo.com"));assert_eq!(result,vec!["a.googlevideo.com","b.googlevideo.com","c.googlevideo.com","d.googlevideo.com"]);assert!(packet_candidates_for(&names,&[]).is_empty());}}
#[cfg(test)]mod tests{use super::*;#[test]fn resource_filter_is_off_by_default_and_rejects_urls_and_false_suffixes(){assert!(!matches("cdn.roblox.com",&domains("")));let filter=domains("roblox.com,rbxcdn.com,https://secret.test/path,1.2.3.4");assert_eq!(filter.len(),2);assert!(matches("THUMBNAILS.ROBLOX.COM",&filter));assert!(matches("a.rbxcdn.com",&filter));assert!(!matches("notroblox.com",&filter));assert!(!matches("https://secret@a.roblox.com",&filter));assert!(!matches("roblox.com.other.test",&filter));}}
