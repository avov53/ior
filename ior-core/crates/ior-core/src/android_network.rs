//! Android owns VPN permission and physical network selection. No unprotected fallback.
use std::{io, sync::{Arc, RwLock}};
type Protector = Arc<dyn Fn(i32) -> io::Result<()> + Send + Sync>;
static PROTECTOR: RwLock<Option<Protector>> = RwLock::new(None);
static LAN_PROTECTOR: RwLock<Option<Protector>> = RwLock::new(None);
pub fn set_lan_protector(protector:Option<Protector>){*LAN_PROTECTOR.write().expect("LAN socket protector lock")=protector;}
pub fn protect_lan(fd:i32)->io::Result<()>{LAN_PROTECTOR.read().map_err(|_|io::ErrorKind::Other)?.clone().ok_or_else(||io::Error::other("ANDROID_LAN_PROTECTOR_MISSING"))?(fd)}
pub fn set_protector(protector: Option<Protector>) {
    *PROTECTOR.write().expect("socket protector lock") = protector;
}
pub fn protect(fd: i32) -> io::Result<()> {
    let callback = PROTECTOR.read().map_err(|_| io::ErrorKind::Other)?.clone()
        .ok_or_else(||io::Error::other("ANDROID_SOCKET_PROTECTOR_MISSING"))?;
    callback(fd)
}
