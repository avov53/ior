//! Explicit opt-in ingress; the private loopback bridge remains unchanged.
use std::{io,net::Ipv4Addr,sync::Arc};
use serde::{Deserialize,Serialize};
use tokio::{net::TcpSocket,task::{JoinHandle,JoinSet}};
use crate::{proxy_inbound::OutboundConnector,ipc_contract::IngressAuth};

#[derive(Clone,Deserialize,Serialize)]
#[serde(rename_all="camelCase",deny_unknown_fields)]
pub struct LanProxySettings {pub enabled:bool,pub address:Ipv4Addr,pub prefix:u8,pub port:u16,pub auth:Option<IngressAuth>,#[serde(default)]pub allowed_client_ips:Option<Vec<Ipv4Addr>>}
impl LanProxySettings {
    pub fn validate(&self)->io::Result<()> {
        if !self.enabled{return Ok(())}
        if self.allowed_client_ips.is_some()&&self.auth.is_none(){return Err(io::Error::other("LAN_PASSWORD_REQUIRED"))}
        if !(self.address.is_private()||self.address.is_link_local())||!(1..=32).contains(&self.prefix)||self.port==0{return Err(io::Error::other("LAN_INVALID_ADDRESS"))}
        if let Some(auth)=&self.auth{if auth.username.is_empty()||auth.username.len()>255||auth.password.is_empty()||auth.password.len()>255||auth.username.contains(':'){return Err(io::Error::other("LAN_INVALID_AUTH"))}}
        Ok(())
    }
    pub fn allows(&self,peer:Ipv4Addr)->bool{let mask=u32::MAX.checked_shl((32-self.prefix)as u32).unwrap_or(0);u32::from(peer)&mask==u32::from(self.address)&mask&&self.allowed_client_ips.as_ref().is_none_or(|clients|clients.contains(&peer))}
}
pub struct LanListener {task:JoinHandle<()>}
impl Drop for LanListener{fn drop(&mut self){self.task.abort();}}
impl LanListener {
    pub async fn start(settings:LanProxySettings,connector:Arc<dyn OutboundConnector>)->io::Result<Self>{
        settings.validate()?;
        let socket=TcpSocket::new_v4()?;
        // The accepted sockets inherit this physical-network mark on Android.
        // Protect before bind/listen so LAN replies never enter our own TUN.
        #[cfg(target_os="android")]{use std::os::fd::AsRawFd;crate::android_network::protect_lan(socket.as_raw_fd())?;}
        socket.bind(std::net::SocketAddr::from((settings.address,settings.port)))?;
        let listener=socket.listen(64)?;
        let task=tokio::spawn(async move{
            let mut flows=JoinSet::new();
            loop {
                while flows.try_join_next().is_some(){}
                if flows.len()>=64{let _=flows.join_next().await;continue}
                let Ok((client,peer))=listener.accept().await else{break};
                let std::net::IpAddr::V4(ip)=peer.ip()else{continue};if !settings.allows(ip){continue}
                let connector=connector.clone();let auth=settings.auth.clone();
                flows.spawn(async move{let _=crate::proxy_inbound::handle_lan_client(client,connector,auth).await;});
            }
        });Ok(Self{task})
    }
}
pub async fn configure(slot:&mut Option<LanListener>,settings:LanProxySettings,connector:Arc<dyn OutboundConnector>)->io::Result<()> {
    settings.validate()?;slot.take();if settings.enabled{*slot=Some(LanListener::start(settings,connector).await?);}Ok(())
}

#[cfg(test)]
mod tests {
 use super::*;
 #[test] fn explicit_clients_require_password_and_intersect_the_local_subnet(){
 let mut config:LanProxySettings=serde_json::from_value(serde_json::json!({"enabled":true,"address":"192.168.7.2","prefix":24,"port":2080,"auth":{"username":"ior","password":"test-password"},"allowedClientIps":["192.168.7.9","192.168.8.9"]})).unwrap();
 assert!(config.validate().is_ok());assert!(config.allows("192.168.7.9".parse().unwrap()));assert!(!config.allows("192.168.7.10".parse().unwrap()));assert!(!config.allows("192.168.8.9".parse().unwrap()));config.allowed_client_ips=Some(vec![]);assert!(!config.allows("192.168.7.9".parse().unwrap()));config.auth=None;assert!(config.validate().is_err());
 }
 #[test] fn subnet_admission_and_bind_validation(){let settings=LanProxySettings{enabled:true,address:"192.168.7.2".parse().unwrap(),prefix:24,port:2080,auth:None,allowed_client_ips:None};assert!(settings.validate().is_ok());assert!(settings.allows("192.168.7.9".parse().unwrap()));assert!(!settings.allows("192.168.8.9".parse().unwrap()));let mut bad=settings.clone();bad.address="0.0.0.0".parse().unwrap();assert!(bad.validate().is_err());bad=settings;bad.prefix=0;assert!(bad.validate().is_err());}
}
