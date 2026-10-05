use std::{fs, io, path::Path};

pub fn save_atomic(path: &Path, content: &[u8]) -> io::Result<()> {
    let temporary = path.with_extension("tmp");
    fs::write(&temporary, content)?;
    fs::rename(temporary, path)
}
