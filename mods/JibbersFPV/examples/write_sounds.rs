//! Writes the drone sounds as 16-bit mono WAVs for the Unity project that builds jibbers_fpv.bundle:
//! `cargo run --release --offline --example write_sounds` (from this folder).
//! The game's build strips AudioClip.Create/SetData, so the clips have to ship as assets.
use jibbers_fpv::synth;
use std::{fs, io::Write, path::Path};

fn wav(path: &Path, samples: &[f32]) -> std::io::Result<()> {
    let data: Vec<u8> = samples
        .iter()
        .flat_map(|s| ((s.clamp(-1., 1.) * 32767.) as i16).to_le_bytes())
        .collect();
    let rate = synth::RATE as u32;
    let mut f = fs::File::create(path)?;
    f.write_all(b"RIFF")?;
    f.write_all(&(36 + data.len() as u32).to_le_bytes())?;
    f.write_all(b"WAVEfmt ")?;
    f.write_all(&16u32.to_le_bytes())?;
    f.write_all(&1u16.to_le_bytes())?; // PCM
    f.write_all(&1u16.to_le_bytes())?; // mono
    f.write_all(&rate.to_le_bytes())?;
    f.write_all(&(rate * 2).to_le_bytes())?;
    f.write_all(&2u16.to_le_bytes())?;
    f.write_all(&16u16.to_le_bytes())?;
    f.write_all(b"data")?;
    f.write_all(&(data.len() as u32).to_le_bytes())?;
    f.write_all(&data)
}

fn main() -> std::io::Result<()> {
    let dir = Path::new(env!("CARGO_MANIFEST_DIR")).join("unity/Assets/Audio");
    fs::create_dir_all(&dir)?;
    wav(&dir.join("Motor.wav"), &synth::motor_loop())?;
    wav(&dir.join("Wind.wav"), &synth::wind_loop())?;
    wav(&dir.join("Crash.wav"), &synth::crash_clip())?;
    println!("wrote {}", dir.display());
    Ok(())
}
