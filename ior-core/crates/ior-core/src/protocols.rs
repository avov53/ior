use crate::core_api::Protocol;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Capability {
    pub parse: bool,
    pub native_transport: bool,
    pub fallback_backend: bool,
}

pub fn capability(_protocol: Protocol) -> Capability {
    Capability {
        parse: true,
        native_transport: true,
        fallback_backend: false,
    }
}
